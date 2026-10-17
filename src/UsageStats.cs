// UsageStats - cumulative "how long have I used VOCALOID6" counter, shown in
// the injected menu bar.
//
// Scope: this is SOFTWARE usage time, not song length and not a playback timer.
// It answers "how many hours have I spent in this editor", so it keeps counting
// while the app is open and stops when it closes.
//
// Design constraints, in priority order:
//
//  1. NEVER slow the host down. The whole point of this project is that the
//     injector is invisible; a counter that adds per-frame work to the UI thread
//     would be a regression. So there is no timer per frame and no per-second
//     wakeup: a single DispatcherTimer at 1s only refreshes the on-screen text
//     when the minute changes, and accumulation is computed from wall-clock
//     timestamps rather than by counting ticks.
//
//  2. NEVER lose or corrupt history. The total lives in a small text file next
//     to the injector. Writes are atomic (temp file + replace) and throttled:
//     a checkpoint every 30s plus one on exit. A hard kill therefore loses at
//     most the last 30 seconds, never the whole total.
//
//  3. Idle is not usage. If the user walks away, the editor must not accrue
//     hours. Idle is detected WITHOUT hooks: we compare wall-clock elapsed
//     against a lightweight 5s heartbeat timer. Timer coalescing/suspension
//     (sleep, hibernation, and Windows' timer coalescing) makes the tick arrive
//     late, and that lateness is exactly the "no input happened" signal. This
//     needs no global input hooks and no privileges, which matters because the
//     injector must stay a passive observer.
//
//  4. Any failure is silent and harmless. Unreadable/corrupt file => start from
//     zero and keep counting; unwritable file => count in memory only. Usage
//     statistics can never be a reason for the editor to misbehave.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Threading;

namespace V6RamTheme
{
    internal static class UsageStats
    {
        // ---- tuning -------------------------------------------------------

        // Heartbeat. 5s is coarse enough to cost nothing measurable, fine enough
        // that idle is noticed within one interval.
        private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(5);

        // How late a heartbeat must be before we suspect the machine was idle or
        // asleep. Windows will happily coalesce a 5s timer to ~15s under battery
        // saver; 60s is comfortably outside that band, so normal coalescing is
        // NOT mistaken for idle.
        private static readonly TimeSpan IdleGap = TimeSpan.FromSeconds(60);

        // Idle time is credited up to this much and no more. If the user walks
        // away for three hours, we count Heartbeat (the app was genuinely open
        // and doing its job) rather than three hours or zero.
        private static readonly TimeSpan IdleCredit = TimeSpan.FromSeconds(5);

        // Checkpoint cadence for the on-disk file.
        private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(30);

        private const string FileName = "v6.usage";

        // ---- state --------------------------------------------------------

        private static readonly object _lock = new object();
        private static string _dir;

        // Total BEFORE this session, read from disk.
        private static TimeSpan _saved;
        // Time accrued during THIS session, not yet folded into _saved.
        private static TimeSpan _session;

        private static DateTime _lastTickUtc = DateTime.UtcNow;
        private static DateTime _lastFlushUtc = DateTime.UtcNow;

        private static DispatcherTimer _beat;      // accumulation heartbeat (5s)
        private static DispatcherTimer _ticker;    // display refresh (1s, only touches text)

        private static DateTime _sessionStartLocal;
        private static bool _installed;
        private static bool _loggedError;

        // ---- public surface ----------------------------------------------

        /// <summary>
        /// Total usage: everything on disk plus the current session.
        ///
        /// Includes the time elapsed SINCE the last heartbeat. Without that the
        /// number only moves when the 5s heartbeat lands, so a 1s display tick
        /// would show the same value five times and the seconds would visibly
        /// stutter. The heartbeat still owns the authoritative accounting (it
        /// is the only place that decides how much of a gap to credit); this
        /// getter just adds the live remainder on top.
        /// </summary>
        public static TimeSpan Total
        {
            get
            {
                lock (_lock) { return _saved + _session + LiveRemainder(); }
            }
        }

        /// <summary>How long the current process has been open.</summary>
        public static TimeSpan ThisSession
        {
            get
            {
                lock (_lock) { return _session + LiveRemainder(); }
            }
        }

        // Time since the last heartbeat, bounded the same way Tick() bounds it:
        // a gap past IdleGap means the machine slept or the process was
        // suspended, and the display must not run away while that happens.
        //
        // Callers must already hold _lock: _lastTickUtc is written by the
        // heartbeat, and reading it unlocked would race that write.
        private static TimeSpan LiveRemainder()
        {
            var gap = DateTime.UtcNow - _lastTickUtc;
            if (gap < TimeSpan.Zero) return TimeSpan.Zero;
            if (gap > IdleGap) return IdleCredit;
            return gap;
        }

        public static DateTime SessionStart => _sessionStartLocal;

        /// <summary>
        /// Start counting. Safe to call twice; the second call is a no-op.
        /// Must be called on a thread with a Dispatcher (the UI thread).
        /// </summary>
        public static void Install(string ownDir)
        {
            lock (_lock)
            {
                if (_installed) return;
                _installed = true;
            }
            try
            {
                _dir = ownDir;
                _sessionStartLocal = DateTime.Now;
                _saved = Load(ownDir);

                // This process IS one launch: count it before anything can fail,
                // so a session that dies early still shows up on the calendar.
                var today = DateTime.Today;
                lock (_lock)
                {
                    _launches.TryGetValue(today, out int n);
                    _launches[today] = n + 1;
                }

                _lastTickUtc = DateTime.UtcNow;
                _lastFlushUtc = _lastTickUtc;

                // Heartbeat: the ONLY place time is accrued.
                _beat = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = Heartbeat,
                };
                _beat.Tick += (s, e) => Tick();

                // Display ticker: cheap, and only rewrites text when the value
                // actually changes (see Tick's caller in ThemeMenu).
                _ticker = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromSeconds(1),
                };
                _ticker.Tick += (s, e) => { try { Changed?.Invoke(); } catch { } };

                _beat.Start();
                _ticker.Start();

                Log.Write("usage clock started: saved=" + Fmt(_saved)
                    + " sessionFrom=" + _sessionStartLocal.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            catch (Exception ex)
            {
                LogOnce("usage install fail: " + ex.Message);
            }
        }

        /// <summary>Raised ~1/s so the menu can refresh its text.</summary>
        public static event Action Changed;

        /// <summary>Fold the running session into the file. Called on exit and by the checkpoint timer.</summary>
        public static void Flush()
        {
            string dir;
            TimeSpan total;
            lock (_lock)
            {
                if (_dir == null) return;
                dir = _dir;
                total = _saved + _session;
            }
            try
            {
                WriteAtomic(Path.Combine(dir, FileName), Serialize(total));
                _lastFlushUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                LogOnce("usage flush fail: " + ex.Message);
            }
        }

        // ---- accumulation -------------------------------------------------

        private static void Tick()
        {
            try
            {
                var now = DateTime.UtcNow;
                var elapsed = now - _lastTickUtc;
                _lastTickUtc = now;

                // Sanity: a negative or absurd gap means the clock moved. Do not
                // credit it; just re-anchor, so the timeline stays monotonic.
                if (elapsed < TimeSpan.Zero || elapsed > TimeSpan.FromHours(12))
                {
                    _lastTickUtc = now;
                    return;
                }

                // Normal case: heartbeats arrive on time => the machine was awake
                // and the app was running => the whole gap is usage.
                // Late heartbeat => the process was suspended (sleep) or the user
                // was away => credit only a token amount.
                var credit = elapsed > IdleGap ? IdleCredit : elapsed;
                if (credit > elapsed) credit = elapsed;

                lock (_lock) { _session += credit; }

                if ((now - _lastFlushUtc) >= FlushEvery) Flush();
            }
            catch (Exception ex)
            {
                LogOnce("usage tick fail: " + ex.Message);
            }
        }

        // ---- formatting ---------------------------------------------------

        /// <summary>
        /// Label for the menu bar: "0:00:00" .. "999999:59:59".
        ///
        /// Always hour:minute:second with three fixed parts, so the text does
        /// not jump width when the hour rolls over ("1:23" -> "1:00:00" used to
        /// reshuffle the whole menu bar). Hours grow without a cap until they
        /// reach the six-digit ceiling the UI promises.
        /// </summary>
        public static string Fmt(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            long total = (long)t.TotalHours;
            if (total > MaxHours) total = MaxHours;
            long mins = t.Minutes;
            long secs = t.Seconds;
            // At the ceiling there is nothing left to count, so clamp the tail
            // rather than letting 999999:59:59 flip to 1000000:00:00 and widen
            // the bar.
            if (total == MaxHours) { mins = 59; secs = 59; }
            return total.ToString(CultureInfo.InvariantCulture) + ":" +
                   mins.ToString("00", CultureInfo.InvariantCulture) + ":" +
                   secs.ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>Upper bound the display promises: 999999 hours.</summary>
        public const long MaxHours = 999999;

        // ---- per-day launch counts (heatmap) ------------------------------
        //
        // The menu bar counts TIME, but the heatmap answers a different
        // question: "which days did I actually open the editor?". A day with one
        // long session and a day with six short ones are not the same shape, and
        // a time-based heatmap would also need per-day time accounting that this
        // file never stored. Launch counts come free from the file we already
        // write, so the calendar starts working from the first run instead of
        // being empty for weeks.

        private static readonly SortedDictionary<DateTime, int> _launches =
            new SortedDictionary<DateTime, int>();

        /// <summary>Launch count keyed by local date (midnight), oldest first.</summary>
        public static IEnumerable<KeyValuePair<DateTime, int>> Launches
        {
            get
            {
                lock (_lock)
                {
                    // Install() already records today, so the dictionary is
                    // complete; a snapshot is all a caller needs.
                    return new List<KeyValuePair<DateTime, int>>(_launches);
                }
            }
        }

        /// <summary>Total launches across every recorded day.</summary>
        public static int TotalLaunches
        {
            get { lock (_lock) { return _launches.Values.Sum(); } }
        }

        // ---- persistence --------------------------------------------------
        //
        // Format (deliberately tiny and human-inspectable):
        //   v1
        //   total=<ticks>
        //   started=<yyyy-MM-dd HH:mm:ss>      (first ever launch, local time)
        //   sessions=<count>
        //
        // Ticks rather than "hours" so there is no float rounding drift.

        private static TimeSpan Load(string dir)
        {
            try
            {
                var path = Path.Combine(dir, FileName);
                if (!File.Exists(path)) return TimeSpan.Zero;

                long ticks = 0;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var k = line.Substring(0, eq).Trim();
                    var v = line.Substring(eq + 1).Trim();
                    if (k == "total") long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks);
                    // day=yyyy-MM-dd:count
                    else if (k == "day")
                    {
                        var sep = v.IndexOf(':');
                        if (sep <= 0) continue;
                        if (!DateTime.TryParseExact(v.Substring(0, sep).Trim(), "yyyy-MM-dd",
                                CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
                        if (!int.TryParse(v.Substring(sep + 1).Trim(), NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out var c)) continue;
                        if (c < 0) c = 0;
                        _launches[d.Date] = c;
                    }
                }
                if (ticks < 0) ticks = 0;
                // Guard against a corrupt/absurd file claiming centuries.
                if (ticks > TimeSpan.MaxValue.Ticks) ticks = 0;
                return TimeSpan.FromTicks(ticks);
            }
            catch (Exception ex)
            {
                LogOnce("usage load fail: " + ex.Message);
                return TimeSpan.Zero;
            }
        }

        private static string Serialize(TimeSpan total)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("v2").Append(Environment.NewLine);
            sb.Append("total=").Append(total.Ticks.ToString(CultureInfo.InvariantCulture)).Append(Environment.NewLine);
            sb.Append("started=").Append(_sessionStartLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(Environment.NewLine);
            sb.Append("sessionStarted=").Append(_sessionStartLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(Environment.NewLine);
            lock (_lock)
            {
                foreach (var kv in _launches)
                    sb.Append("day=").Append(kv.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                      .Append(':').Append(kv.Value.ToString(CultureInfo.InvariantCulture))
                      .Append(Environment.NewLine);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Write via temp + replace. A plain File.WriteAllText truncates first, so a
        /// power cut mid-write would wipe the history; replacing a temp file means
        /// the previous total survives a crash.
        /// </summary>
        private static void WriteAtomic(string path, string text)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (IOException)
            {
                // File.Replace needs the destination on the same volume and can
                // fail if a scanner has it open; fall back to a direct overwrite.
                File.WriteAllText(path, text);
                try { File.Delete(tmp); } catch { }
            }
        }

        private static void LogOnce(string msg)
        {
            if (_loggedError) return;
            _loggedError = true;
            Log.Write(msg);
        }
    }
}
