// ProjectTimer - REAL per-project elapsed time, chosen by the user.
//
// WHY THIS IS MANUAL: the injected hook cannot see which .vpr the editor has
// open. Verified on a live editor session:
//   * the .vpr is not kept locked (opening it exclusively for read succeeds)
//   * the main window title stays "VOCALOID6 Editor", no project name
//   * no child process, no extra window
// So the user picks the project from a dropdown and the timer accrues against
// whatever is selected, from the moment it is selected.
//
// This is genuine measured time, unlike ProjectStats.EstHours which is a
// heuristic. The two must never be added together or shown as if equivalent:
//   EstimatedHours  - all projects, inferred from span+size
//   TimerHours      - the selected project only, stopwatch, this machine only
//
// Storage: v6.ptime, one line per project:  <name>\t<ticks>
// Names are enough of a key here: within one project folder they are the
// folder name with the date tail stripped, which is what the user sees.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Threading;

namespace V6RamTheme
{
    internal static class ProjectTimer
    {
        private const string FileName = "v6.ptime";
        private const string OpensFileName = "v6.opens";
        private const int FlushSeconds = 30;

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, TimeSpan> _totals =
            new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);

        private static string _dir;
        private static string _selected;        // currently timing this project
        private static DateTime _lastTickUtc;
        private static DateTime _lastFlushUtc;
        private static TimeSpan _pending;       // accrued since the last flush
        private static DispatcherTimer _beat;
        private static bool _installed;

        /// <summary>Raised ~1/s so the UI can refresh the displayed value.</summary>
        public static event Action Changed;

        /// <summary>Name of the project being timed, or null.</summary>
        public static string Selected
        {
            get { lock (_lock) { return _selected; } }
        }

        public static void Install(string ownDir)
        {
            lock (_lock)
            {
                if (_installed) return;
                _installed = true;
                _dir = ownDir;
            }
            try
            {
                Load();
                LoadOpens();
                _lastTickUtc = DateTime.UtcNow;
                _lastFlushUtc = _lastTickUtc;

                _beat = new DispatcherTimer(DispatcherPriority.Background)
                {
                    // 1s: the display must move every second (same lesson as the
                    // menu-bar clock - a 5s beat made the seconds visibly stutter).
                    Interval = TimeSpan.FromSeconds(1),
                };
                _beat.Tick += (s, e) => { Tick(); Changed?.Invoke(); };
                _beat.Start();
            }
            catch (Exception ex)
            {
                Log.Write("project timer install fail: " + ex.Message);
            }
        }

        /// <summary>Measured time for one project (excludes the un-flushed second).</summary>
        public static TimeSpan Get(string project)
        {
            if (string.IsNullOrEmpty(project)) return TimeSpan.Zero;
            lock (_lock)
            {
                _totals.TryGetValue(project, out var t);
                if (string.Equals(project, _selected, StringComparison.OrdinalIgnoreCase))
                    t += DateTime.UtcNow - _lastTickUtc + _pending;
                return t;
            }
        }

        /// <summary>Live view of the selected project, including the partial second.</summary>
        public static TimeSpan SelectedElapsed
        {
            get
            {
                lock (_lock)
                {
                    if (_selected == null) return TimeSpan.Zero;
                    _totals.TryGetValue(_selected, out var t);
                    return t + _pending + (DateTime.UtcNow - _lastTickUtc);
                }
            }
        }

        /// <summary>
        /// Start timing <paramref name="project"/> (null stops the clock).
        ///
        /// The previously selected project keeps everything it already earned;
        /// switching only changes where the NEXT second goes.
        /// </summary>
        public static void Select(string project)
        {
            lock (_lock)
            {
                if (string.Equals(_selected, project, StringComparison.OrdinalIgnoreCase))
                    return;

                // Fold the elapsed tail into the outgoing project before moving.
                if (_selected != null)
                {
                    var tail = _pending + (DateTime.UtcNow - _lastTickUtc);
                    if (tail > TimeSpan.Zero && tail < TimeSpan.FromMinutes(10))
                    {
                        _totals.TryGetValue(_selected, out var cur);
                        _totals[_selected] = cur + tail;
                    }
                }
                _pending = TimeSpan.Zero;
                _lastTickUtc = DateTime.UtcNow;
                _selected = project;
            }
            Log.Write("project timer select: " + (project ?? "(none)"));

            // Count a real open. Select() only fires when the title settled on a
            // DIFFERENT project, so A->A (dirty marker, re-title) is already
            // filtered out upstream. Opening A again after leaving it counts
            // again, which is what "打开次数" means.
            if (!string.IsNullOrEmpty(project)) CountOpen(project);

            Flush();
        }

        // ---- per-day open counts -------------------------------------------
        //
        // The ONLY source of true open counts is this program observing them.
        // .vpr keeps just one access timestamp, so a file opened on Monday and
        // again on Tuesday records Tuesday only - history cannot be recovered.
        // Counting therefore starts the first time this build runs, and the UI
        // must not present it as if it covered the whole library.

        private static readonly Dictionary<string, int> _opens =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private static string OpenKey(DateTime day, string project) =>
            day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" + project;

        private static void CountOpen(string project)
        {
            try
            {
                var key = OpenKey(DateTime.Today, project);
                lock (_lock)
                {
                    _opens.TryGetValue(key, out int n);
                    _opens[key] = n + 1;
                }
                SaveOpens();
            }
            catch (Exception ex)
            {
                Log.Write("count open fail: " + ex.Message);
            }
        }

        /// <summary>Opens recorded for a given day (0 when nobody counted).</summary>
        public static int OpensOn(DateTime day)
        {
            lock (_lock)
            {
                int total = 0;
                var prefix = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|";
                foreach (var kv in _opens)
                    if (kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                        total += kv.Value;
                return total;
            }
        }

        /// <summary>Days that have at least one recorded open.</summary>
        public static IEnumerable<KeyValuePair<DateTime, int>> OpensByDay()
        {
            var acc = new Dictionary<DateTime, int>();
            lock (_lock)
            {
                foreach (var kv in _opens)
                {
                    var bar = kv.Key.IndexOf('|');
                    if (bar <= 0) continue;
                    if (!DateTime.TryParseExact(kv.Key.Substring(0, bar), "yyyy-MM-dd",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                        continue;
                    acc.TryGetValue(d, out int n);
                    acc[d] = n + kv.Value;
                }
            }
            return acc.OrderBy(k => k.Key).ToList();
        }

        /// <summary>
        /// Every open this program has ever recorded, across all days.
        /// Counted, never inferred - and therefore only covers the period since
        /// the open-count feature first ran on this machine.
        /// </summary>
        public static int TotalOpens()
        {
            lock (_lock)
            {
                int total = 0;
                foreach (var kv in _opens) total += kv.Value;
                return total;
            }
        }

        private static void LoadOpens()
        {
            try
            {
                var path = Path.Combine(_dir, OpensFileName);
                if (!File.Exists(path)) return;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    var tab = line.LastIndexOf('\t');
                    if (tab <= 0) continue;
                    var key = line.Substring(0, tab);
                    if (!int.TryParse(line.Substring(tab + 1), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out int n) || n <= 0) continue;
                    lock (_lock) { _opens[key] = n; }
                }
                Log.Write("open-count loaded: " + _opens.Count + " entries");
            }
            catch (Exception ex)
            {
                Log.Write("open-count load fail: " + ex.Message);
            }
        }

        private static void SaveOpens()
        {
            try
            {
                var path = Path.Combine(_dir, OpensFileName);
                string text;
                lock (_lock)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append("# yyyy-MM-dd|project\topens\n");
                    foreach (var kv in _opens.OrderBy(k => k.Key, StringComparer.Ordinal))
                        sb.Append(kv.Key).Append('\t')
                          .Append(kv.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    text = sb.ToString();
                }
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, text);
                try
                {
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                }
                catch (IOException)
                {
                    File.WriteAllText(path, text);
                    try { File.Delete(tmp); } catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Write("open-count save fail: " + ex.Message);
            }
        }

        private static void Tick()
        {
            try
            {
                // Detect the open project BEFORE accruing, so a switch is billed
                // to the right project rather than to the previous second.
                // Runs ~1/s; the title is read from the visual tree, which is
                // cheap enough at that rate but not on every render.
                AutoDetect();

                lock (_lock)
                {
                    var now = DateTime.UtcNow;
                    var gap = now - _lastTickUtc;
                    _lastTickUtc = now;

                    if (_selected == null) return;

                    // Same fairness rule as the usage clock: a long gap means the
                    // machine slept or the process was suspended, so do not bill it.
                    if (gap < TimeSpan.Zero || gap > TimeSpan.FromMinutes(10)) return;

                    _pending += gap;

                    if ((now - _lastFlushUtc).TotalSeconds >= FlushSeconds)
                    {
                        // Fold pending into the total and persist.
                        _totals.TryGetValue(_selected, out var cur);
                        _totals[_selected] = cur + _pending;
                        _pending = TimeSpan.Zero;
                        _lastFlushUtc = now;
                        Save();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("project timer tick fail: " + ex.Message);
            }
        }

        public static void Flush()
        {
            try
            {
                lock (_lock)
                {
                    if (_selected != null && _pending > TimeSpan.Zero)
                    {
                        _totals.TryGetValue(_selected, out var cur);
                        _totals[_selected] = cur + _pending;
                        _pending = TimeSpan.Zero;
                    }
                    _lastFlushUtc = DateTime.UtcNow;
                }
                Save();
            }
            catch (Exception ex)
            {
                Log.Write("project timer flush fail: " + ex.Message);
            }
        }

        // ---- automatic detection -------------------------------------------
        //
        // Reads the project name out of the editor's own title bar. Confirmed by
        // probing the live process:
        //   * Window.Title is just "VOCALOID6 Editor" - the project name is NOT
        //     a window property
        //   * the real title is a TextBlock inside the self-drawn caption bar,
        //     reading "VOCALOID6 Editor - <project>"
        //   * no file lock, no child process
        // So detection walks the visual tree for that TextBlock. The walk is
        // bounded and exception-swallowing; a failure just means "no project".

        private static string _lastSeenTitle;
        private static string _pendingProject;
        private static int _pendingStable;      // consecutive ticks agreeing
        private const int StableTicks = 3;      // ~3s before switching

        private static void AutoDetect()
        {
            try
            {
                var title = ReadTitleBarText();
                if (string.IsNullOrEmpty(title)) return;

                if (title == _lastSeenTitle)
                {
                    // Same title as last tick: count toward stability.
                    if (_pendingProject != null && ++_pendingStable >= StableTicks)
                    {
                        var target = _pendingProject;
                        _pendingProject = null;
                        _pendingStable = 0;
                        Select(target);
                    }
                    return;
                }

                // Title changed: the new one must hold for StableTicks before we
                // bill to it. Editors change titles transiently (loading, saving,
                // the "*" dirty marker), and those must not split a session.
                _lastSeenTitle = title;
                var name = ToProjectName(title);
                if (string.IsNullOrEmpty(name)) return;

                // Prefer the canonical .vpr stem when the project is inside the
                // scanned library - that is what makes "(B)雨爱司冯版/雨爱.vpr"
                // and the title "雨爱" agree on one key.
                //
                // But a match is NOT required. Timing and the heatmap answer
                // different questions: the heatmap needs a folder to walk, while
                // the stopwatch only needs the name the editor already puts in
                // its own title bar. Requiring a match meant that with no
                // `projects=` configured the timer silently refused to run even
                // though the name was right there on screen.
                //
                // Phantom entries are prevented by ToProjectName (which only
                // accepts text after " - ") and by the stability window, not by
                // this lookup.
                var canonical = ResolveProject(name) ?? name;
                _pendingProject = canonical;
                _pendingStable = 1;
            }
            catch (Exception ex)
            {
                Log.Write("project autodetect fail: " + ex.Message);
            }
        }

        // Does <paramref name="name"/> refer to a real project?
        //
        // Map a title-bar name to the .vpr stem, which is what gets stored.
        //
        // The editor titles its window with the .vpr FILE stem ("雨爱"), while
        // folders often differ ("(B)雨爱司冯版"). Keying storage by folder name
        // made the UI show a name the user never sees in the editor, so the
        // .vpr stem is the key now - it matches the title bar exactly, and a
        // folder holding several .vpr files gets one entry per file instead of
        // merging them.
        //
        // Returns null when the name matches nothing, so a stray title (a splash
        // window, a tool dialog) cannot create phantom entries.
        private static string ResolveProject(string name)
        {
            try
            {
                var all = ProjectStats.Projects;
                // Exact .vpr stem match wins outright.
                foreach (var p in all)
                {
                    if (p.VprNames == null) continue;
                    foreach (var stem in p.VprNames)
                        if (string.Equals(stem, name, StringComparison.OrdinalIgnoreCase))
                            return stem;
                }
                // Fall back to a folder-name match; then the canonical stem is
                // that folder's most recently written .vpr.
                foreach (var p in all)
                {
                    if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (p.VprNames != null && p.VprNames.Length > 0)
                    {
                        if (!string.IsNullOrEmpty(p.VprPath))
                        {
                            var stem = System.IO.Path.GetFileNameWithoutExtension(p.VprPath);
                            if (!string.IsNullOrEmpty(stem)) return stem;
                        }
                        return p.VprNames[0];
                    }
                }
            }
            catch { }
            return null;
        }

        // "VOCALOID6 Editor - 宇宙分解" -> "宇宙分解".
        // Kept here rather than in a probe class: the only caller is AutoDetect.
        private static string ToProjectName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var s = raw.Trim();

            // A .vpr path (if a title ever carries one) collapses to its stem.
            if (s.IndexOf(".vpr", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try
                {
                    var n = System.IO.Path.GetFileNameWithoutExtension(s);
                    if (!string.IsNullOrEmpty(n)) return n.Trim();
                }
                catch { }
            }

            int dash = s.LastIndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0) return s.Substring(dash + 3).Trim();
            return null;
        }

        // Walk the visual tree for the caption TextBlock. Bounded depth/count so
        // this stays cheap when called once per second.
        private static string ReadTitleBarText()
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app == null) return null;
                int budget = 600;
                foreach (var w in app.Windows)
                {
                    var win = w as System.Windows.Window;
                    if (win == null) continue;
                    var t = FindCaption(win, 0, ref budget);
                    if (!string.IsNullOrEmpty(t)) return t;
                }
            }
            catch { }
            return null;
        }

        private static string FindCaption(System.Windows.DependencyObject node, int depth, ref int budget)
        {
            if (node == null || depth > 12 || budget <= 0) return null;
            budget--;
            try
            {
                var tb = node as System.Windows.Controls.TextBlock;
                if (tb != null && !string.IsNullOrEmpty(tb.Text))
                {
                    var s = tb.Text;
                    // The caption is "<app> - <project>"; ignore anything else.
                    if (s.IndexOf(" - ", StringComparison.Ordinal) > 0) return s;
                }
                int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < n; i++)
                {
                    var r = FindCaption(System.Windows.Media.VisualTreeHelper.GetChild(node, i), depth + 1, ref budget);
                    if (!string.IsNullOrEmpty(r)) return r;
                }
            }
            catch { }
            return null;
        }

        // ---- persistence ---------------------------------------------------

        private static void Load()
        {
            try
            {
                var path = Path.Combine(_dir, FileName);
                if (!File.Exists(path)) return;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    var tab = line.LastIndexOf('\t');
                    if (tab <= 0) continue;
                    var name = line.Substring(0, tab);
                    if (!long.TryParse(line.Substring(tab + 1), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var ticks)) continue;
                    if (ticks < 0) continue;
                    lock (_lock) { _totals[name] = TimeSpan.FromTicks(ticks); }
                }
                Log.Write("project timer loaded: " + _totals.Count + " entries");
            }
            catch (Exception ex)
            {
                Log.Write("project timer load fail: " + ex.Message);
            }
        }

        private static void Save()
        {
            try
            {
                var path = Path.Combine(_dir, FileName);
                string text;
                lock (_lock)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append("# project\tseconds\n");
                    foreach (var kv in _totals.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                        sb.Append(kv.Key.Replace('\t', ' ')).Append('\t')
                          .Append(kv.Value.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    text = sb.ToString();
                }
                // temp + replace: a truncate-then-write would lose the history on
                // a crash mid-write (same reason as UsageStats).
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, text);
                try
                {
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                }
                catch (IOException)
                {
                    File.WriteAllText(path, text);
                    try { File.Delete(tmp); } catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Write("project timer save fail: " + ex.Message);
            }
        }
    }
}
