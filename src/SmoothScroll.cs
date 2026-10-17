// SmoothScroll - opt-in inertia wheel scrolling ("平滑滚动").
//
// Why this can be live while colors cannot: the tint hook acts at BAML parse
// time, so brushes are frozen in the process cache and a color change needs a
// restart (see 开发文档 §10). Wheel scrolling is live behaviour, not cached
// state, so flipping the switch takes effect immediately.
//
// Mechanism: ONE class handler for UIElement.PreviewMouseWheel, registered
// from Bootstrap.Run() (before the host builds any UI, on the thread that will
// become WPF's Dispatcher). ThemeMenu's UI-thread install pass calls Install()
// again as an idempotent safety net. A tunneling handler runs on the way DOWN,
// ahead of ScrollViewer's own wheel handling, so marking the event handled
// suppresses WPF's default single-jump per click; we then drive the offset
// ourselves with a frame-synced exponential pass on CompositionTarget.Rendering.
//
// Because class handlers fire once per element on the route (outermost first),
// the same wheel event reaches us many times. e.Handled is the dedupe: after the
// first pass claims it, later passes on the same args object return early.
//
// Scope guards - every one must hold, otherwise we touch nothing:
//  * the switch is on (Bootstrap.Smooth);
//  * no Ctrl/Alt in the modifiers (hosts bind Ctrl+wheel to zoom - never steal it);
//  * the nearest scrollable ancestor IS a real ScrollViewer;
//  * that ScrollViewer can actually scroll on the axis we would move;
//  * our own surfaces are excluded (settings window, flyout cards) so the
//    injector's UI keeps stock behaviour.
//
// Everything is wrapped defensively. Worst case for any failure is "the
// original scrolling happens", never a crash and never a frozen view.

using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace V6RamTheme
{
    internal static class SmoothScroll
    {
        // Tag put on the settings window so we can recognise our own surface.
        internal const string SelfMark = "v6-ram-theme-window";

        // Pixels scrolled per wheel click, as a fraction of the viewport.
        // Deliberately close to the stock "a few rows per click" feel; the only
        // difference is that the movement is eased instead of instantaneous.
        private const double WheelFraction = 0.2;
        private const double MinStep = 24.0;
        private const double MaxStep = 160.0;

        // Time constant of the exponential approach, in seconds. 0.11s gives a
        // short, responsive glide that stops cleanly when the wheel pauses.
        private const double Tau = 0.11;

        // Snap tolerance in pixels - below this we land exactly and stop.
        private const double Epsilon = 0.5;

        // Minimum pixels a glide advances per frame near the target. Offsets
        // rasterize to whole pixels; sub-pixel steps read as a stutter.
        private const double TailFloorPx = 1.5;

        private static bool _classHooked;
        private static readonly object _lock = new object();

        private static ScrollViewer _sv;
        private static bool _horiz;
        private static double _cur;
        private static double _target;
        private static bool _running;
        private static long _lastTick;   // Stopwatch timestamp at previous frame
        private static bool _loggedFirst;
        private static bool _loggedError;

        /// <summary>Register the wheel class handler. Idempotent; safe to call early.</summary>
        public static void Install()
        {
            lock (_lock)
            {
                if (_classHooked) return;
            }
            try
            {
                EventManager.RegisterClassHandler(
                    typeof(UIElement),
                    UIElement.PreviewMouseWheelEvent,
                    new MouseWheelEventHandler(OnPreviewWheel),
                    handledEventsToo: false);   // we claim the event ourselves
                // Flag ONLY on success: if this call ever throws, the retry from
                // ThemeMenu's UI-thread pass must still be able to register.
                // RegisterClassHandler routes through the current thread's
                // Dispatcher, so a failure here is possible in principle - the
                // retry is the safety net, not the primary path.
                lock (_lock) { _classHooked = true; }
                Log.Write("smooth scroll handler registered (state=" + (Bootstrap.Smooth ? "on" : "off") + ")");
            }
            catch (Exception ex)
            {
                Log.Write("smooth install fail: " + ex.Message);
            }
        }

        // Read-only test seam (used by wheel-lab): is a glide in progress?
        // Nothing in production reads this; it exists so a harness can wait for
        // the easing to finish instead of guessing frame counts.
        internal static bool IsRunning => _running;

        // ---------------- wheel interception ----------------

        private static void OnPreviewWheel(object sender, MouseWheelEventArgs e)
        {
            if (!Bootstrap.Smooth) return;
            // First pass on the outermost element claims the event; every later
            // element on the same route sees Handled and must not act twice.
            if (e.Handled) return;
            if (e.Delta == 0) return;

            try
            {
                var mods = Keyboard.Modifiers;
                if (BlockedByModifiers(mods)) return;

                bool horiz = (mods & ModifierKeys.Shift) != 0;
                var sv = FindScroll(e.OriginalSource as DependencyObject, horiz);
                if (sv == null) return;   // nothing stock-scrollable here: not our business

                double units = e.Delta / 120.0;
                if (units == 0.0) return;

                e.Handled = true;
                PlaybackSmooth.Abort();   // wheel glide now owns the view
                // Sign: a wheel-down click carries Delta < 0, and the stock
                // behaviour it replaces (ScrollLineDown) INCREASES the offset.
                // So the eased target moves by -units.
                Begin(sv, horiz, -StepFor(sv, horiz) * units);

                if (!_loggedFirst)
                {
                    _loggedFirst = true;
                    Log.Write("smooth wheel intercepted on " + sv.GetType().Name
                        + " axis=" + (horiz ? "x" : "y")
                        + " scrollable=" + (horiz ? sv.ScrollableWidth : sv.ScrollableHeight).ToString("F0")
                        + " viewport=" + (horiz ? sv.ViewportWidth : sv.ViewportHeight).ToString("F0"));
                }
            }
            catch (Exception ex)
            {
                // Never swallow silently twice, never log-spam: one line is enough.
                if (!_loggedError) { _loggedError = true; Log.Write("smooth wheel fail: " + ex.Message); }
                Stop();
            }
        }

        // Nearest ScrollViewer up the tree that can scroll on the wanted axis,
        // unless a self-marked Window owns it. The candidate is remembered while
        // walking because our own settings window CONTAINS a ScrollViewer (the
        // preview host), so testing the window first would never see it. Only
        // once the walk resolves the owner can we decide.
        private static ScrollViewer FindScroll(DependencyObject d, bool horiz)
        {
            ScrollViewer candidate = null;
            int guard = 0;
            while (d != null && guard++ < 400)
            {
                if (d is ScrollViewer sv && candidate == null)
                {
                    // Flyout cards from ThemeMenu are named CardScroll. A popup
                    // is its own visual root, so the MenuItem above it may be
                    // unreachable - the Name check is the reliable guard there.
                    if (sv.Name != "CardScroll")
                    {
                        double room = horiz ? sv.ScrollableWidth : sv.ScrollableHeight;
                        if (room > 0) candidate = sv;
                    }
                }
                if (d is Window w)
                    return (w.Tag as string) == SmoothScroll.SelfMark ? null : candidate;
                if (d is MenuItem mi && (mi.Tag as string) == ThemeMenu.Marker) return null;

                d = ParentOf(d);
            }
            return candidate;
        }

        // Visual parent where possible; falls back to the logical parent for the
        // handful of nodes that are DependencyObjects but not visuals.
        private static DependencyObject ParentOf(DependencyObject d)
        {
            try
            {
                if (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
                    return VisualTreeHelper.GetParent(d);
            }
            catch { }
            try { return LogicalTreeHelper.GetParent(d); } catch { }
            return null;
        }

        private static double StepFor(ScrollViewer sv, bool horiz)
        {
            double vp = horiz ? sv.ViewportWidth : sv.ViewportHeight;
            if (vp <= 0 || double.IsNaN(vp)) vp = 200;
            double px = vp * WheelFraction;
            return px < MinStep ? MinStep : (px > MaxStep ? MaxStep : px);
        }

        // Pure modifier policy, kept separate so it is unit-testable without
        // synthesising physical Ctrl/Alt/Shift states. Ctrl+wheel (zoom) and
        // Alt+wheel belong to the host; Shift stays ours (horizontal roll).
        internal static bool BlockedByModifiers(ModifierKeys mods) =>
            (mods & (ModifierKeys.Control | ModifierKeys.Alt)) != 0;

        // ---------------- easing pass ----------------

        private static void Begin(ScrollViewer sv, bool horiz, double delta)
        {
            // Adopt reality as the starting point whenever we are idle (the app
            // may have moved the view since the last glide - playback follow,
            // scrollbar drag), and always when the target view/axis changes.
            // While a glide is still running, consecutive clicks just extend it.
            if (!ReferenceEquals(sv, _sv) || horiz != _horiz || !_running)
            {
                _sv = sv;
                _horiz = horiz;
                _cur = Offset(sv, horiz);
                _target = _cur;
            }

            double max = MaxOffset(sv, horiz);
            _target += delta;
            if (_target < 0) _target = 0;
            if (_target > max) _target = max;

            if (!_running)
            {
                _running = true;
                _lastTick = Stopwatch.GetTimestamp();
                CompositionTarget.Rendering += OnRendering;
            }
        }

        private static void OnRendering(object sender, EventArgs e)
        {
            // Frame dt from the high-resolution counter. DateTime.UtcNow has
            // ~15.6 ms granularity, which aliased a 165 Hz panel's ~6 ms frames
            // into alternating 0 / 15.6 ms steps - the glide then advanced only
            // ~30 effective steps per second and felt choppy.
            long now = Stopwatch.GetTimestamp();
            // Optional frame cap (shared with playback follow via
            // Bootstrap.MaxFps): at 165 Hz every frame is another host layout
            // pass over the virtualized canvas; capping reduces that pressure.
            if (Bootstrap.MaxFps > 0)
            {
                double minPeriod = (double)Stopwatch.Frequency / Bootstrap.MaxFps;
                if (_lastTick != 0 && now - _lastTick < minPeriod) return;
            }
            double dt = _lastTick == 0
                ? 1.0 / 60.0
                : (now - _lastTick) / (double)Stopwatch.Frequency;
            _lastTick = now;
            if (dt <= 0 || dt > 0.5) dt = 1.0 / 60.0;   // first frame / wake-up guard
            Step(dt);
        }

        // One easing frame, extracted so wheel-lab can drive the WHOLE curve
        // without a real render loop (CompositionTarget.Rendering never fires
        // under synthetic frame pumping - see 开发文档 §5.7). Production reaches
        // this only through OnRendering above.
        internal static void Step(double dt)
        {
            if (!_running) return;
            try
            {
                var sv = _sv;
                if (sv == null) { Stop(); return; }

                // Switched off mid-glide: land exactly on the target at once, so
                // "off" never leaves the view sliding on its own.
                if (!Bootstrap.Smooth) { Apply(_sv, _target); Stop(); return; }

                // Content can shrink or grow under us (project edits, resize).
                double max = MaxOffset(sv, _horiz);
                if (_target > max) _target = max;
                if (_target < 0) _target = 0;

                // If the app moved the view itself (scrollbar drag, playback
                // following the cursor, jump to selection), follow reality instead
                // of fighting it. That keeps us out of every host-driven scroll.
                double actual = Offset(sv, _horiz);
                if (Math.Abs(actual - _cur) > 1.5) { _cur = actual; _target = actual; }

                // Exponential approach with a per-frame floor: pure exponential
                // decay asymptotes to the target, so near the end each frame
                // advances less than a pixel. Offsets rasterize to whole
                // pixels, and those sub-pixel steps read as a stutter/crawl
                // right before the target - the "choppy near the end" complaint.
                // The floor keeps the tail moving at a steady small pace until
                // the offset lands.
                double gap = _target - _cur;
                double step = gap * (1.0 - Math.Exp(-dt / Tau));
                if (Math.Abs(step) < TailFloorPx) step = Math.Sign(gap) * TailFloorPx;
                if (Math.Abs(step) > Math.Abs(gap)) step = gap;
                _cur += step;

                if (Math.Abs(_target - _cur) < Epsilon)
                {
                    Apply(sv, _target);
                    Stop();
                    return;
                }
                Apply(sv, _cur);
            }
            catch (Exception ex)
            {
                if (!_loggedError) { _loggedError = true; Log.Write("smooth render fail: " + ex.Message); }
                Stop();
            }
        }

        private static void Stop()
        {
            if (!_running) return;
            _running = false;
            try { CompositionTarget.Rendering -= OnRendering; } catch { }
        }

        private static double Offset(ScrollViewer sv, bool horiz) =>
            horiz ? sv.HorizontalOffset : sv.VerticalOffset;

        private static double MaxOffset(ScrollViewer sv, bool horiz) =>
            horiz ? sv.ScrollableWidth : sv.ScrollableHeight;

        private static void Apply(ScrollViewer sv, double v)
        {
            if (_horiz) sv.ScrollToHorizontalOffset(v);
            else sv.ScrollToVerticalOffset(v);
        }
    }
}
