// PlaybackSmooth - "平滑播放" (Plan B).
//
// Root cause found by decompiling the host:
//   * The piano-roll follows the playhead through
//     ZoomScrollViewer.SongPositionPropertyChanged -> ScrollToHorizontalOffset.
//     That is the ONE real playback-jump source. Watching every ScrollChanged
//     (the old Plan A) mis-fired on ZOOM slider writes and on whole-page jumps,
//     which is exactly what made zoom and page-turns stutter.
//   * FastCanvas virtualizes notes by their CANVAS coordinates, so shifting the
//     content with a RenderTransform (the old transform glide) fought its
//     viewport culling -> parts were displaced / never rendered near max zoom.
//
// Plan B therefore:
//   1. A Harmony PREFIX on ZoomScrollViewer.SongPositionPropertyChanged decides,
//      per jump, whether to glide or to let the host teleport.
//   2. Small/medium jumps (a playhead sliding just past the edge) are turned into
//      a per-frame ScrollToHorizontalOffset glide - the same mechanism the host
//      itself uses, so FastCanvas updates stay consistent (no transform).
//   3. Large jumps (>= MaxGlideShare * viewport, e.g. a whole page) are NOT
//      glided: we let the original method run and it teleports, as before. A
//      60 fps glide across a 1.4M-pixel canvas is what caused the stutter, so
//      big jumps stay instant on purpose.
//   4. Zoom is untouched: it flows through the zoom-slider path, never through
//      SongPosition, so gliding playback no longer hijacks zoom.
//
// The ScrollChanged class handler is kept for hosts where a followable ScrollViewer
// is not a ZoomScrollViewer, but it now SKIPS the piano-roll body (owned by the
// prefix above) and the ruler, so it can never double-chase or steal zoom events.
//
// Everything is defensive: any failure means "original host behaviour, unchanged",
// never a crash and never a frozen view.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace V6RamTheme
{
    internal static class PlaybackSmooth
    {
        private const double BaseSpeed = 3000.0;
        private const double MinDuration = 0.15;
        private const double MaxDuration = 0.65;
        private const double Epsilon = 0.5;
        private const double MinJump = 40;
        private const double ViewportShare = 0.4;

        // A jump wider than this fraction of the viewport is a page jump: the
        // host's own teleport is cheaper and less stuttery than chasing it.
        private const double MaxGlideShare = 1.5;

        // Continuous playback follow uses exponential catch-up instead of the
        // fixed-speed glide: at high zoom the playhead outruns 3000 px/s, and a
        // glide that never catches up degenerates into glide-teleport churn.
        // Time constant in seconds (63% of the gap closed per Tau).
        private const double FollowTau = 0.08;

        // Stop following (and shut the timer down) once playback has been quiet
        // this long, so the glide finishes and hands control back cleanly.
        private const double IdleStop = 0.25;

        // Minimum pixels the glide must advance per frame near the target.
        // Offsets rasterize to whole pixels; sub-pixel steps read as a stutter.
        private const double TailFloorPx = 1.5;

        // Piano-roll body / ruler ScrollViewer names (confirmed from the host
        // BAML): the body is glided by the SongPosition prefix, the ruler is
        // linked and must never be chased.
        private const string PianorollViewerName = "xPianorollViewer";
        private const string RulerViewerName = "xRulerViewer";

        private static bool _classHooked;
        private static bool _prefixHooked;
        private static readonly object _lock = new object();

        private static bool _selfWrite;
        private static double _pendingEcho;

        // Chase state.
        private static ScrollViewer _sv;
        private static bool _horiz;
        private static double _cur;        // current visual position (A→B)
        private static double _target;     // B (host's jump target)
        private static double _speed;
        private static double _remaining;
        private static bool _running;
        // High-resolution frame clock: DateTime.UtcNow has ~15.6 ms granularity
        // on Windows and DispatcherTimer only ticks at ~60 Hz (both were making
        // the glide feel ~30 fps on a 165 Hz display). Stopwatch is sub-microsecond
        // and CompositionTarget.Rendering fires once per composed frame.
        private static long _lastTick;   // Stopwatch timestamp at previous frame

        // Exponential follow mode: once a glide is engaged by playback, each
        // new SongPosition event retargets instead of restarting a fixed-speed
        // glide. At high zoom the playhead outruns 3000 px/s, and a fixed glide
        // that never catches up degenerates into glide-lag-teleport churn (the
        // "not smooth" symptom). Exponential catch-up always converges.
        private static bool _follow;
        private static double _idle;       // seconds since last retarget

        // Transform path is disabled under Plan B (kept only so the fallback
        // surface and lab wiring still resolve); it is never taken at runtime.
        private static bool _useTransform;
        private static TranslateTransform _transform;
        private static UIElement _transformTarget;
        private static TransformGroup _transformGroup;
        private static bool _addedToGroup;

        private static bool _loggedFirst;
        private static bool _loggedError;

        internal static bool IsChasing => _running;

        internal static string DebugState =>
            "cur=" + _cur.ToString("F1") + " target=" + _target.ToString("F1")
            + " running=" + _running + " horiz=" + _horiz
            + " mode=" + (_useTransform ? "transform" : "offset")
            + " sv=" + (_sv == null ? "null" : _sv.GetType().Name);

        internal static double DebugCur => _cur;

        internal static readonly List<string> Trace = new List<string>();

        // ---------------- install ----------------

        public static void Install()
        {
            lock (_lock)
            {
                if (_classHooked) return;
                try
                {
                    EventManager.RegisterClassHandler(
                        typeof(ScrollViewer),
                        ScrollViewer.ScrollChangedEvent,
                        new ScrollChangedEventHandler(OnScrollChanged));
                    _classHooked = true;
                    Log.Write("playback chase handler registered (state=" + (Bootstrap.SmoothPlay ? "on" : "off") + ")");
                }
                catch (Exception ex)
                {
                    Log.Write("playback chase install fail: " + ex.Message);
                }
            }
            TryHookSongPosition();
        }

        // Harmony prefix on each editor's ZoomScrollViewer.SongPositionPropertyChanged.
        private static void TryHookSongPosition()
        {
            lock (_lock) { if (_prefixHooked) return; }
            try
            {
                Assembly host = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    if (a.GetName().Name == "VOCALOID6") { host = a; break; }
                if (host == null) return;   // resolved again on the next call

                var prefix = new HarmonyLib.HarmonyMethod(
                    typeof(PlaybackSmooth).GetMethod(nameof(SongPosPrefix),
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));

                int ok = 0;
                foreach (var ns in new[] { "MusicalEditor", "TrackEditor", "WaveEditor" })
                {
                    var t = host.GetType("Yamaha.VOCALOID." + ns + ".ZoomScrollViewer", false);
                    if (t == null) continue;
                    var m = t.GetMethod("SongPositionPropertyChanged",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (m == null) continue;
                    try
                    {
                        if (_harmony.Patch(m, prefix: prefix) != null) ok++;
                    }
                    catch (Exception ex) { Log.Write("songpos patch fail (" + ns + "): " + ex.Message); }
                }

                if (ok > 0)
                {
                    _prefixHooked = true;
                    Log.Write("playback follow prefix active (" + ok + " viewers)");
                }
            }
            catch (Exception ex)
            {
                Log.Write("playback follow prefix install fail: " + ex.Message);
            }
        }

        private static readonly HarmonyLib.Harmony _harmony = new HarmonyLib.Harmony("v6-ram-theme-playback");

        // Prefix for ZoomScrollViewer.SongPositionPropertyChanged.
        // Returns true = run the original (host teleport, our glide skipped);
        // returns false = original suppressed, we glide to the same target.
        private static bool SongPosPrefix(object __instance, object[] __args)
        {
            if (!Bootstrap.SmoothPlay) return true;
            try
            {
                var sv = __instance as ScrollViewer;
                if (sv == null) return true;
                // SongPosition is a SHARED property: every editor's
                // ZoomScrollViewer (piano roll, parameter view, track editor,
                // wave editor) gets this callback during playback. The chase
                // state is global single-slot, so letting them all glide made
                // them steal the chase from each other - the ruler/track area
                // jittered and the piano roll never finished a glide. Only the
                // piano-roll body is ours to glide; everything else stays with
                // the host's native behavior.
                if (sv.Name != PianorollViewerName) return true;

                if (__args == null || __args.Length < 1) return true;
                var e = (DependencyPropertyChangedEventArgs)__args[0];

                // VM (protected property) and its follow settings.
                var vm = GetProp(__instance, "VM");
                if (vm == null) return true;
                var mode = GetProp(vm, "AutoScrollMode");
                if (mode == null || mode.ToString() != "On") return true;
                var wptObj = GetProp(vm, "WidthPerTick");
                if (!(wptObj is double wpt) || wpt <= 0) return true;

                double num = TickToDouble(e.NewValue) * wpt;
                double off = sv.HorizontalOffset;

                // Host's own "should follow?" test (from the decompiled body).
                bool outside = num < off || off + sv.ViewportWidth < num;
                if (!outside) return true;

                double desired = num - GetScrollMargin();
                double max = sv.ScrollableWidth;
                double delta = Clamp(desired, 0, max) - off;

                double big = sv.ViewportWidth * MaxGlideShare;

                // Already following this view: just retarget. Do NOT restart a
                // fixed-speed glide - at high zoom the playhead outruns it and
                // the restart churn is what looked "not smooth".
                if (_running && _follow && ReferenceEquals(sv, _sv) && _horiz)
                {
                    _target = Clamp(desired, 0, max);
                    _idle = 0;
                    return false;
                }

                // Medium jump -> start a glide (suppress the host's teleport).
                // Small continuous follow (playhead drifting past the edge) also
                // engages the glide so the view creeps along instead of the host
                // teleporting on every tick. Only a jump wider than a page
                // (delta > big) is left to the host, because chasing it at 60fps
                // across a huge canvas is what stuttered.
                if (Math.Abs(delta) >= 1.0 && Math.Abs(delta) <= big)
                {
                    StartGlide(sv, true, off, Clamp(desired, 0, max), max);
                    _follow = true;
                    _idle = 0;
                    if (!_loggedFirst)
                    {
                        _loggedFirst = true;
                        Log.Write("playback chase engaged (songpos) on " + sv.GetType().Name
                            + " axis=x jump=" + delta.ToString("F0") + " glide=yes");
                    }
                    return false; // suppress the host's instant jump
                }

                return true; // huge page jumps: host default
            }
            catch (Exception ex)
            {
                if (!_loggedError) { _loggedError = true; Log.Write("songpos prefix fail: " + ex.Message); }
                return true;
            }
        }

        // ---------------- ScrollChanged safety net (disabled by default) ----------------

        // The safety net used to chase large jumps on every OTHER scrollable
        // view. But the piano roll's glide propagates through the host's linked
        // scrolling into the ruler / track editor / wave editor, and those
        // linked events were then mistaken for new playback jumps and chased
        // back - a ping-pong that made the upper track area jitter. With the
        // prefix now owning only the piano-roll body, the net has nothing safe
        // left to do, so it stays off. Flip to true only for experiments.
        // (static readonly, not const, so the body below is not flagged dead.)
        private static readonly bool SafetyNetEnabled = false;

        private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (!SafetyNetEnabled) return;
            if (!Bootstrap.SmoothPlay) return;
            if (_selfWrite) return;
            try
            {
                var sv = sender as ScrollViewer;
                if (sv == null) return;
                if (sv.Name == "CardScroll") return;
                if (sv.Name == RulerViewerName) return;
                if (sv.Name == PianorollViewerName) return;   // owned by the prefix
                var win = Window.GetWindow(sv);
                if (win != null && (win.Tag as string) == SmoothScroll.SelfMark) return;
                if (SmoothScroll.IsRunning) return;
                if (Mouse.LeftButton == MouseButtonState.Pressed) return;

                double thV = ThresholdFor(sv, false);
                double thH = ThresholdFor(sv, true);
                bool bigV = Math.Abs(e.VerticalChange) >= thV;
                bool bigH = Math.Abs(e.HorizontalChange) >= thH;
                if (!bigV && !bigH) return;

                bool horiz = bigH && (!bigV ||
                    Math.Abs(e.HorizontalChange) > Math.Abs(e.VerticalChange));
                double change = horiz ? e.HorizontalChange : e.VerticalChange;
                double max = horiz ? sv.ScrollableWidth : sv.ScrollableHeight;

                // A jump larger than a page: teleport, do not chase (page-turn stutter).
                double big = (horiz ? sv.ViewportWidth : sv.ViewportHeight) * MaxGlideShare;
                if (Math.Abs(change) > big) return;

                if (Math.Abs(_pendingEcho) > 0.001 && Math.Abs(change - _pendingEcho) <= 1.0)
                {
                    _pendingEcho = 0;
                    return;
                }

                double actualNow = Clamp(horiz ? e.HorizontalOffset : e.VerticalOffset, 0, max);
                double preJump = Clamp(actualNow - change, 0, max);

                Trace.Add("evt change=" + change.ToString("F1")
                    + " preJump=" + preJump.ToString("F1") + " actualNow=" + actualNow.ToString("F1")
                    + " running=" + _running + " same=" + ReferenceEquals(sv, _sv));

                if (ReferenceEquals(sv, _sv) && horiz == _horiz && _running)
                {
                    StartGlide(sv, horiz, _cur, actualNow, max);
                    return;
                }

                StartGlide(sv, horiz, preJump, actualNow, max);

                if (!_loggedFirst)
                {
                    _loggedFirst = true;
                    Log.Write("playback chase engaged (scroll-changed) on " + sv.GetType().Name
                        + " axis=" + (horiz ? "x" : "y") + " jump=" + change.ToString("F0"));
                }
            }
            catch (Exception ex)
            {
                if (!_loggedError) { _loggedError = true; Log.Write("playback chase fail: " + ex.Message); }
                Abort();
            }
        }

        // ---------------- glide (offset-driven) ----------------

        private static void StartGlide(ScrollViewer sv, bool horiz, double from, double to, double max)
        {
            CleanupTransform();

            _sv = sv;
            _horiz = horiz;
            _cur = Clamp(from, 0, max);
            _target = Clamp(to, 0, max);

            double dist = Math.Abs(_target - _cur);
            double dur = Math.Clamp(dist / BaseSpeed, MinDuration, MaxDuration);
            _remaining = dur;
            _speed = dist > 0.001 ? (_target - _cur) / dur : 0.0;

            // Plan B: always the offset path. The transform glide is disabled
            // because it fights FastCanvas virtualization near max zoom.
            _useTransform = false;
            Apply(sv, _cur, to);

            if (!_running)
            {
                _running = true;
                // Frame-driven, not DispatcherTimer: a render-priority timer
                // still only ticks on the system ~16 ms granularity, which
                // quantized the glide to ~30-60 uneven steps on a 165 Hz panel.
                // CompositionTarget.Rendering fires once per actual frame.
                try { _lastTick = Stopwatch.GetTimestamp(); } catch { _lastTick = 0; }
                CompositionTarget.Rendering += OnRendering;
            }
        }

        private static void OnRendering(object sender, EventArgs e)
        {
            long now = Stopwatch.GetTimestamp();
            // Optional frame cap (Bootstrap.MaxFps): at 165 Hz every frame is a
            // host layout pass on a huge virtualized canvas; capping reduces
            // that work. _lastTick is only advanced when we actually step, so
            // dt keeps measuring real elapsed time between applied steps.
            if (Bootstrap.MaxFps > 0)
            {
                double minPeriod = (double)Stopwatch.Frequency / Bootstrap.MaxFps;
                if (_lastTick != 0 && now - _lastTick < minPeriod) return;
            }
            double dt = _lastTick == 0 ? 1.0 / 60.0 : (now - _lastTick) / (double)Stopwatch.Frequency;
            _lastTick = now;
            if (dt <= 0 || dt > 0.5) dt = 1.0 / 60.0;
            Step(dt);
        }

        internal static void Abort()
        {
            if (!_running) return;
            _running = false;
            _sv = null;
            _follow = false;
            _idle = 0;
            _lastTick = 0;
            try { CompositionTarget.Rendering -= OnRendering; } catch { }
            CleanupTransform();
        }

        private static void CleanupTransform()
        {
            if (_transform != null) { _transform.X = 0; _transform.Y = 0; }
            if (_addedToGroup && _transformGroup != null && _transform != null)
            {
                try { _transformGroup.Children.Remove(_transform); } catch { }
            }
            else if (_transformTarget != null && !_addedToGroup)
            {
                try
                {
                    if (ReferenceEquals(_transformTarget.RenderTransform, _transform))
                        _transformTarget.RenderTransform = null;
                }
                catch { }
            }
            _transform = null;
            _transformTarget = null;
            _transformGroup = null;
            _addedToGroup = false;
        }

        // ---------------- easing pass ----------------

        internal static void Step(double dt)
        {
            if (!_running) return;
            try
            {
                var sv = _sv;
                if (sv == null) { Abort(); return; }

                if (!Bootstrap.SmoothPlay)
                {
                    if (_useTransform) { _transform.X = 0; _transform.Y = 0; }
                    else { Apply(sv, _target, _cur); }
                    Abort();
                    return;
                }

                double max = _horiz ? sv.ScrollableWidth : sv.ScrollableHeight;
                _target = Clamp(_target, 0, max);

                // Exponential follow: while playback keeps retargeting _target,
                // close the remaining gap by a fixed fraction each frame instead
                // of a fixed speed. Always converges even when the playhead moves
                // faster than BaseSpeed (high zoom), so the view glides instead of
                // lagging then teleporting.
                if (_follow)
                {
                    _idle += dt;
                    double gap = _target - _cur;
                    if (Math.Abs(gap) < Epsilon)
                    {
                        _cur = _target;
                        Apply(sv, _cur, _cur);
                        // Stop following once playback has gone quiet.
                        if (_idle > IdleStop) Abort();
                        return;
                    }
                    // Exponential step, with a per-frame floor: pure exponential
                    // decay approaches the target asymptotically, so the last few
                    // pixels move less than 1 px per frame. Scroll offsets are
                    // rasterized to whole pixels, and that reads as crawling /
                    // stepping right before the target - the "choppy near the
                    // playhead" complaint. The floor keeps the tail moving at a
                    // steady small speed until it lands.
                    double step = gap * (1.0 - Math.Exp(-dt / FollowTau));
                    if (Math.Abs(step) < TailFloorPx) step = Math.Sign(gap) * TailFloorPx;
                    if (Math.Abs(step) > Math.Abs(gap)) step = gap;
                    _cur = Clamp(_cur + step, 0, max);
                    Apply(sv, _cur, _cur - step);
                    return;
                }

                _remaining -= dt;
                _cur += _speed * dt;

                if (_remaining <= 0 || Math.Abs(_target - _cur) < Epsilon)
                {
                    _cur = _target;
                    if (_useTransform) { _transform.X = 0; _transform.Y = 0; }
                    else { Apply(sv, _target, _cur); }
                    Abort();
                    return;
                }

                _cur = Clamp(_cur, 0, max);

                if (_useTransform)
                {
                    double shift = _cur - _target;
                    if (_horiz) _transform.X = shift;
                    else _transform.Y = shift;
                }
                else
                {
                    Apply(sv, _cur, _cur - _speed * dt);
                }
            }
            catch (Exception ex)
            {
                if (!_loggedError) { _loggedError = true; Log.Write("playback chase tick fail: " + ex.Message); }
                Abort();
            }
        }

        private static double Clamp(double v, double lo, double hi) =>
            v < lo ? lo : (v > hi ? hi : v);

        private static void Apply(ScrollViewer sv, double v, double expectedFrom)
        {
            _selfWrite = true;
            try
            {
                _pendingEcho = v - expectedFrom;
                if (_horiz) sv.ScrollToHorizontalOffset(v);
                else sv.ScrollToVerticalOffset(v);
            }
            finally { _selfWrite = false; }
        }

        // ---------------- reflection helpers ----------------

        private static readonly Dictionary<string, PropertyInfo> _propCache = new Dictionary<string, PropertyInfo>();
        private static double _margin = double.NaN;

        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            var key = obj.GetType().FullName + "." + name;
            PropertyInfo pi;
            if (!_propCache.TryGetValue(key, out pi))
            {
                pi = obj.GetType().GetProperty(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _propCache[key] = pi;   // may be null; cached so we do not re-reflect
            }
            return pi == null ? null : pi.GetValue(obj, null);
        }

        private static double GetScrollMargin()
        {
            if (!double.IsNaN(_margin)) return _margin;
            _margin = 0.0;
            try
            {
                Assembly host = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    if (a.GetName().Name == "VOCALOID6") { host = a; break; }
                if (host == null) return 0.0;
                var musical = host.GetType("Yamaha.VOCALOID.Musical", false);
                if (musical == null) return 0.0;
                var f = musical.GetField("ScrollPartLeftMargin",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (f == null) return 0.0;
                var val = f.IsLiteral ? f.GetRawConstantValue() : f.GetValue(null);
                _margin = Convert.ToDouble(val);
            }
            catch { _margin = 0.0; }
            return _margin;
        }

        // VSMAbsTick is a host struct; extract its numeric value generically.
        private static double TickToDouble(object tick)
        {
            if (tick == null) return 0.0;
            try
            {
                if (tick is long || tick is int) return Convert.ToDouble(tick);
                var t = tick.GetType();
                var conv = t.GetMethod("ToInt64", Type.EmptyTypes);
                if (conv != null) return Convert.ToDouble(conv.Invoke(tick, null));
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "op_Explicit" || m.ReturnType != typeof(long)) continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == t)
                        return Convert.ToDouble(m.Invoke(null, new[] { tick }));
                }
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    if (f.FieldType == typeof(long)) return Convert.ToDouble(f.GetValue(tick));
            }
            catch { }
            return 0.0;
        }

        // ---------------- lab test seam ----------------

        internal static void ResetStateForTest()
        {
            Abort();
            _cur = 0;
            _target = 0;
            _speed = 0;
            _remaining = 0;
            _pendingEcho = 0;
            _useTransform = false;
            _loggedFirst = false;
            _loggedError = false;
        }

        private static double ThresholdFor(ScrollViewer sv, bool horiz)
        {
            double vp = horiz ? sv.ViewportWidth : sv.ViewportHeight;
            if (vp <= 0 || double.IsNaN(vp)) vp = 200;
            return Math.Max(MinJump, vp * ViewportShare);
        }

        // Transform path is intentionally left unused under Plan B; kept so the
        // lab's DebugState ("mode=...") and any future re-enable still resolve.
        private static bool TrySetupTransform(ScrollViewer sv, bool horiz, double from, double to)
        {
            return false;
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T result) return result;
                var found = FindVisualChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }
    }
}
