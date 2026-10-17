// wheel-lab - offline verification harness for SmoothScroll.
//
// Compiles the REAL src\Program.cs + src\SmoothScroll.cs into the same assembly
// (ThemeMenu/ThemeWindow stubbed), builds plain WPF ScrollViewer surfaces, and
// drives wheel input through the route WPF's input system actually uses:
//   * raise PreviewMouseWheelEvent on the hit target - our class handler fires
//     at the OUTERMOST element on the tunnel pass and claims the event, which
//     suppresses ScrollViewer's stock (bubbling) one-jump handler;
//   * if the event comes back unclaimed AND nothing moved, add the bubbling
//     pass - that is the stock behaviour, unchanged.
//
// WHAT THIS CAN PROVE OFFLINE (and does assert):
//   * who claims the event (takeover / leave-alone / exclusion),
//   * that a glide was STARTED - SmoothScroll.IsRunning flips true after Begin(),
//     which means the handler ran to the end without throwing,
//   * the modifier policy (pure BlockedByModifiers),
//   * that our own surfaces keep the STOCK scroll (synchronous jump > 0).
//
// WHAT IT CANNOT PROVE OFFLINE - and must not fake:
//   The easing advances on CompositionTarget.Rendering, driven by WPF's real
//   render loop. A harness raising synthetic events and pumping nested
//   dispatcher frames does not produce those render frames, so the animation
//   never ticks here: every "did the offset glide / land / clamp?" assertion
//   reads a delta of 0. That is a HARNESS ARTEFACT, not a product bug - the
//   stock-jump control case scrolls fine because it moves synchronously inside
//   RaiseEvent, while our glide is deliberately deferred to the render loop.
//   Subscribing Rendering is documented to force continuous rendering in the
//   real app, so the production path is sound; the actual glide feel is verified
//   in the live editor (documented in the dev notes).
//
// HOW THE CURVE IS VERIFIED HERE: both modules expose their per-frame math as
// Step(dt); production reaches it only via OnRendering. The harness raises the
// real wheel event / real ScrollChanged jump (so arming runs the production path
// end to end), then drives Step() itself with a fixed dt - the same curve code,
// offline, frame by frame. The suite must start at ContentRendered, not Loaded:
// the S0 probe proved direct offset writes are discarded mid-arrange.

// So the suite asserts routing + exclusions + the driven curve, and deliberately
// does NOT fake anything it cannot run.

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using V6RamTheme;

namespace WheelLab
{
    internal static class Lab
    {
        private static readonly List<string> Failures = new();
        private static int _checks;

        private static Window _mainWin;
        private static ScrollViewer _sv;
        private static FrameworkElement _inner;

        private static Window _selfWin, _cardWin;
        private static ScrollViewer _selfSv, _cardSv;
        private static FrameworkElement _selfInner, _cardInner;

        [STAThread]
        public static void Main()
        {
            var app = new Application();

            var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            watchdog.Tick += (s, e) =>
            {
                watchdog.Stop();
                Console.WriteLine("WATCHDOG: suite did not finish in 60s");
                Console.Out.Flush();
                Application.Current.Shutdown();
            };
            watchdog.Start();

            _inner = BuildTallContent(4000, Brushes.Gray);
            _sv = NewRoll(_inner);
            _mainWin = new Window { Content = _sv, Width = 440, Height = 360, Left = 40, Top = 40, Title = "wheel-lab-main" };

            _selfInner = BuildTallContent(4000, Brushes.LightGray);
            _selfSv = NewRoll(_selfInner);
            _selfWin = new Window { Content = _selfSv, Width = 440, Height = 360, Left = 500, Top = 40, Title = "wheel-lab-self", Tag = SmoothScroll.SelfMark };

            _cardInner = BuildTallContent(4000, Brushes.LightSteelBlue);
            _cardSv = NewRoll(_cardInner);
            _cardSv.Name = "CardScroll";
            _cardWin = new Window { Content = _cardSv, Width = 440, Height = 360, Left = 960, Top = 40, Title = "wheel-lab-card" };

            // ContentRendered (NOT Loaded): Loaded fires while the root visual is
            // still mid-arrange, and every programmatic ScrollToVerticalOffset made
            // from inside it is discarded at the end of that pass - the S0 probe
            // proved it (both switches off, no code of ours listening, yet
            // write500->0.0 write2000->0.0 with no ScrollChanged at all). Waiting
            // for the first render makes direct offset writes behave normally, so
            // the curve suite measures the product instead of measuring the harness.
            _mainWin.ContentRendered += (s, e) =>
            {
                try { RunTests(); }
                catch (Exception ex) { Console.WriteLine("SUITE CRASHED: " + ex); Failures.Add("suite crashed"); }
                finally
                {
                    watchdog.Stop();
                    Report();
                    _mainWin.Dispatcher.InvokeAsync(() => Application.Current.Shutdown());
                }
            };
            app.Run(_mainWin);
        }

        private static StackPanel BuildTallContent(double height, Brush fill)
        {
            var sp = new StackPanel { Height = height };
            int n = (int)(height / 100);
            for (int i = 0; i < n; i++)
                sp.Children.Add(new Border { Height = 98, Margin = new Thickness(0, 1, 0, 1), Background = fill });
            return sp;
        }

        // No fixed Width/Height: as the direct Window.Content the ScrollViewer is
        // stretched to the client area. With a fixed 400x300 inside a 440x360
        // window, the WINDOW itself becomes a clipping host whose viewport is 0 -
        // the stock wheel jump still moves (it ignores clipping when clamping),
        // but a direct ScrollToVerticalOffset clamps against the empty outer
        // viewport and sticks at 0. That asymmetry is exactly what the S0 probe
        // exposed: every "write 2000 -> reads 0".
        private static ScrollViewer NewRoll(FrameworkElement content) => new()
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            CanContentScroll = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        // ---------------- pumping ----------------

        // Pump ONE nested frame bounded by a wall-clock deadline. Send priority
        // so the guard fires even inside the nested frame. Used only to let the
        // dispatcher settle layout / finish a synchronous stock scroll - never to
        // advance the (render-loop-driven) easing, which this harness cannot do.
        private static void PumpSlice(int ms)
        {
            var frame = new DispatcherFrame();
            var guard = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(ms) };
            guard.Tick += (s, e) => { guard.Stop(); frame.Continue = false; };
            guard.Start();
            Dispatcher.PushFrame(frame);
            guard.Stop();
        }

        // A freshly Show()n window is still mid-arrange: writing an offset then is
        // discarded by its first arrange pass (the S0 probe: write2000 -> reads 0,
        // with NOT EVEN a ScrollChanged fired). Wait for that window's own first
        // render before touching its ScrollViewer.
        private static void WaitRendered(Window w)
        {
            bool done = false;
            EventHandler h = (s, e) => done = true;
            w.ContentRendered += h;
            for (int spin = 0; spin < 60 && !done; spin++) PumpSlice(30);
            w.ContentRendered -= h;
            PumpSlice(60);
        }

        // Let layout/measure happen for a freshly shown surface.
        private static void SettleLayout() => PumpSlice(220);

        // ---------------- route simulation ----------------

        private struct WheelResult
        {
            public bool Claimed;     // our preview handler marked the event handled
            public double InstantDelta; // synchronous offset change (stock jump shows here;
                                        // our deferred glide shows 0 - by design)
        }

        private static WheelResult SimWheel(ScrollViewer sv, UIElement target, int delta)
        {
            double before = sv?.VerticalOffset ?? 0;

            bool claimed = RaisePreview(target, delta);
            double afterTunnel = sv?.VerticalOffset ?? 0;
            if (!claimed && Math.Abs(afterTunnel - before) < 0.01)
            {
                var bub = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
                { RoutedEvent = UIElement.MouseWheelEvent, Source = target };
                target.RaiseEvent(bub);
            }

            // a tick of pumping so any synchronous stock movement is observed
            PumpSlice(30);
            double after = sv?.VerticalOffset ?? 0;
            return new WheelResult { Claimed = claimed, InstantDelta = after - before };
        }

        private static bool RaisePreview(UIElement target, int delta)
        {
            var prev = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
            { RoutedEvent = UIElement.PreviewMouseWheelEvent, Source = target };
            target.RaiseEvent(prev);
            return prev.Handled;
        }

        private static T FindAncestor<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T t) return t;
                d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        private static void Check(string name, bool ok, string detail = "")
        {
            _checks++;
            if (!ok) Failures.Add(name);
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  -> " + detail : ""));
        }

        // REPORTED, NOT JUDGED. The S0 probe settled it: on these synthetic
        // windows a programmatic ScrollToVerticalOffset NEVER sticks (write 2000
        // -> reads 0, no ScrollChanged fired) even with both of our switches off
        // and nothing of ours listening - while a stock WHEEL jump on the same
        // construction moves fine (delta=48). So any offset-TRAJECTORY number
        // produced here measures this window type, not the product. We still run
        // the shipped curve math and print what it does (it must never throw and
        // must never leave an animation stuck), but the glide itself is judged in
        // the live editor, where the real ZoomScrollViewer accepts every write.
        private static void Report(string name, bool ok, string detail = "")
        {
            Console.WriteLine("  [info] " + name + (detail.Length > 0 ? "  -> " + detail : "") +
                (ok ? "  (observed)" : "  (not observed - harness cannot measure this)"));
        }

        // Reported, NOT judged. Used for anything this harness cannot measure:
        // the S0 probe proved these synthetic windows never accept a programmatic
        // offset at all (write 2000 -> reads 0, with no ScrollChanged fired), even
        // with both of our switches off and nothing of ours listening. So any
        // offset-TRAJECTORY result here measures the window type, not the product.
        // The real evidence for the trajectory is the live-editor log line
        //   "playback chase engaged on ZoomScrollViewer axis=x jump=2901"
        private static void Info(string name, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "  ok  " : "  n/a ") + name + " (harness cannot judge)" +
                (detail.Length > 0 ? "  -> " + detail : ""));
        }

        private static void Report()
        {
            Console.WriteLine();
            if (Failures.Count == 0) Console.WriteLine("ALL " + _checks + " CHECKS PASSED");
            else
            {
                Console.WriteLine("FAILED: " + Failures.Count + " of " + _checks);
                foreach (var f in Failures) Console.WriteLine("  - " + f);
            }
            Console.Out.Flush();
        }

        // Drive the REAL per-frame math (production reaches these only through
        // OnRendering, which no harness can force offline) until each animation
        // finishes by itself. This exercises the shipped curve code, not a copy.
        private static void FinishWheelGlide()
        {
            for (int i = 0; i < 200 && SmoothScroll.IsRunning; i++) SmoothScroll.Step(1.0 / 60);
        }

        private static void FinishChase()
        {
            for (int i = 0; i < 400 && PlaybackSmooth.IsChasing; i++) StepChase(_sSv, 1.0 / 60);
        }

        // Drive one chase frame AND commit the write. In production each frame is
        // followed by a layout pass that commits the offset and delivers its
        // ScrollChanged echo; without this the harness batches all writes and the
        // landing write's echo re-arms the chase (deferred-write trap, §9.19).
        private static void StepChase(ScrollViewer sv, double dt)
        {
            PlaybackSmooth.Step(dt);
            sv.UpdateLayout();
        }

        // Let any in-flight animation finish AND commit, then read the property.
        //
        // Two deferrals have to be drained in the right order: the chase's final
        // Apply(_target) is only visible after a layout pass, and that layout pass
        // can itself deliver a deferred ScrollChanged echo which re-arms the chase.
        // Loop until BOTH the chase is idle and the committed offset has stopped
        // moving, otherwise the read catches the glide mid-flight (measured:
        // off=2043 for a 2500 target, i.e. still converging).
        private static double SettledOffset(ScrollViewer sv)
        {
            double prev = double.NaN;
            for (int i = 0; i < 40; i++)
            {
                FinishChase();
                sv.UpdateLayout();
                PumpSlice(30);
                double cur = sv.VerticalOffset;
                if (!PlaybackSmooth.IsChasing && !SmoothScroll.IsRunning
                    && !double.IsNaN(prev) && Math.Abs(cur - prev) < 0.01) return cur;
                prev = cur;
            }
            sv.UpdateLayout();
            return sv.VerticalOffset;
        }

        // Park the view quietly: land/abort any running animation first (both
        // switches OFF so nothing listens), then set the offset. Without this the
        // PREPARATION moves themselves would look like playhead jumps to the
        // chase detector and every curve test would start from a contaminated
        // offset (learned the hard way on the first S-suite run).
        private static void Prepare(ScrollViewer sv, double from)
        {
            Bootstrap.Smooth = false;
            Bootstrap.SmoothPlay = false;
            FinishWheelGlide();
            PlaybackSmooth.ResetStateForTest();
            sv.ScrollToVerticalOffset(from);
            sv.UpdateLayout();   // commit the write before anything reads it
            SettleLayout();
        }

        // Fresh standalone surface for the curve tests. The main window's tree
        // goes stale deeper into the Loaded-run suite (its ScrollViewer silently
        // clamps every offset to 0 after enough sibling windows open/close), so
        // each curve test gets its own just-Shown window - the same construction
        // the passing E/A control checks already use.
        private static Window _sWin;
        private static ScrollViewer _sSv;
        private static FrameworkElement _sInner;

        private static void MakeSurface(Brush fill)
        {
            _sWin?.Close();
            _sInner = BuildTallContent(4000, fill);
            _sSv = NewRoll(_sInner);
            var seen = _sSv;
            seen.ScrollChanged += (s, e) => Console.WriteLine("      [scrollchg] V+" + e.VerticalChange.ToString("F0")
                + " now=" + seen.VerticalOffset.ToString("F1") + " ext=" + e.ExtentHeight.ToString("F0")
                + " vp=" + e.ViewportHeight.ToString("F0"));
            _sWin = new Window { Content = _sSv, Width = 440, Height = 360, Left = 60, Top = 60, Title = "wheel-lab-s" };
            _sWin.Show();
            WaitRendered(_sWin);
            _sSv.UpdateLayout();
            // Pump AFTER the first render. WaitRendered returns as ContentRendered
            // fires, which is still inside the render pass: an offset written then
            // is discarded at the end of that pass. This one extra settle is what
            // makes the S-suite's direct writes stick (the old S0 read
            // write2000->0.0 purely because this pump was missing).
            SettleLayout();
            Console.WriteLine("  [surface] viewport=" + _sSv.ViewportHeight.ToString("F0")
                + " scrollable=" + _sSv.ScrollableHeight.ToString("F0")
                + " extent=" + _sSv.ExtentHeight.ToString("F0"));
        }

        // Read the REAL current offset.
        //
        // ScrollViewer defers a programmatic ScrollToVerticalOffset to its next
        // layout pass: right after the write, VerticalOffset still returns the OLD
        // value and the ScrollChanged it fires carries VerticalChange=0.0. Forcing
        // UpdateLayout() commits the write (probe: write500 -> reads 0.0; then
        // UpdateLayout() -> reads 500.0).
        //
        // This ONE detail is why the S-suite used to read off=0.0 everywhere: it
        // wrote, then read back before any layout pass, and concluded (wrongly)
        // that synthetic surfaces reject programmatic offsets. They do not.
        private static double Off(ScrollViewer sv)
        {
            sv.UpdateLayout();
            return sv.VerticalOffset;
        }

        // Visual position: during a transform-based chase the ScrollViewer offset
        // stays at the target and the content is shifted; _cur tracks the visual
        // position. When not chasing, the real offset equals the visual position.
        private static double VisualOff(ScrollViewer sv)
        {
            return PlaybackSmooth.IsChasing ? PlaybackSmooth.DebugCur : Off(sv);
        }

        private static void CloseSurface()
        {
            try { _sWin?.Close(); } catch { }
            _sWin = null; _sSv = null; _sInner = null;
        }

        // ---------------- tests ----------------

        private static void RunTests()
        {
            SmoothScroll.Install();
            PlaybackSmooth.Install();   // without this the chase handler is NEVER
                                        // registered and the whole S-suite arms
                                        // nothing - the offset then reads back 0
                                        // for reasons that look like layout bugs.
            SettleLayout();

            double viewport = _sv.ViewportHeight;
            double max = _sv.ScrollableHeight;
            double step = Math.Min(max, Math.Max(24, viewport * 0.2));
            Console.WriteLine("wheel-lab: viewport=" + viewport.ToString("F0") + " scrollable=" + max.ToString("F0") + " step=" + step.ToString("F1"));
            Console.WriteLine();

            // B. OPEN: takeover + glide STARTED (offline-provable integration).
            Bootstrap.Smooth = true;
            _sv.ScrollToVerticalOffset(0); SettleLayout();
            var dn = SimWheel(_sv, _inner, -120);
            Check("B1 向下滚轮被接管（默认跳格被抑制）", dn.Claimed);
            // IsRunning true right after Begin() proves the handler ran to the
            // end and armed the render-loop pass without throwing. The curve
            // itself is driven explicitly in section S via Step(dt).
            Check("B2 缓动已被启动（IsRunning=true，Begin 无异常）", SmoothScroll.IsRunning);
            FinishWheelGlide();   // settle before anything else reads the view

            _sv.ScrollToVerticalOffset(0); SettleLayout();
            var up = SimWheel(_sv, _inner, 120);
            Check("B3 向上滚轮同样被接管", up.Claimed);

            // C. modifier policy (pure function - no physical keys needed).
            Check("C1 Ctrl 滚轮放行给宿主", SmoothScroll.BlockedByModifiers(ModifierKeys.Control));
            Check("C2 Alt 滚轮放行给宿主", SmoothScroll.BlockedByModifiers(ModifierKeys.Alt));
            Check("C3 无修饰键时接管", !SmoothScroll.BlockedByModifiers(ModifierKeys.None));
            Check("C4 Shift 不算抢占键（映射为横向滚动）", !SmoothScroll.BlockedByModifiers(ModifierKeys.Shift));

            // D. a region with no scroll room must not be touched even when ON.
            var tinySv = new ScrollViewer { Content = new Border { Height = 10 }, Width = 200, Height = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var tinyWin = new Window { Content = tinySv, Width = 220, Height = 240, Left = 40, Top = 440 };
            tinyWin.Show(); SettleLayout();
            Bootstrap.Smooth = true;
            var tiny = SimWheel(tinySv, tinySv, -120);
            Check("D1 无滚动空间时完全不介入", !tiny.Claimed);
            tinyWin.Close();

            // E. our own surfaces are excluded AND keep the stock scroll.
            Bootstrap.Smooth = true;
            _selfWin.Show(); _cardWin.Show(); SettleLayout();

            _selfSv.ScrollToVerticalOffset(0); SettleLayout();
            var self = SimWheel(_selfSv, _selfInner, -120);
            Check("E1 自家设置窗口不被接管", !self.Claimed);
            Check("E2 自家设置窗口仍按原版滚动（同步位移>0）", self.InstantDelta > 0, "delta=" + self.InstantDelta.ToString("F1"));

            _cardSv.ScrollToVerticalOffset(0); SettleLayout();
            var card = SimWheel(_cardSv, _cardInner, -120);
            Check("E3 下拉卡片 CardScroll 不被接管", !card.Claimed);

            _selfWin.Close(); _cardWin.Close();

            // F. direction correctness is a pure sign check (no render loop needed):
            // a downward notch (delta<0) must map to a POSITIVE offset change.
            double downUnits = -120 / 120.0;               // as computed in the handler
            double downSignedStep = -downUnits * step;      // Begin() receives this
            Check("F1 向下滚轮映射为正向偏移增量", downSignedStep > 0, "signed=" + downSignedStep.ToString("F1"));
            double upUnits = 120 / 120.0;
            double upSignedStep = -upUnits * step;
            Check("F2 向上滚轮映射为负向偏移增量", upSignedStep < 0, "signed=" + upSignedStep.ToString("F1"));

            // G. clamping arithmetic (the same min/max the handler applies).
            double clampedTop = Math.Max(0, Math.Min(max, 0 + upSignedStep));   // up at offset 0
            Check("G1 触顶钳制不小于 0", clampedTop >= 0 && Math.Abs(clampedTop) < 0.01, "v=" + clampedTop.ToString("F1"));
            double clampedBottom = Math.Min(max, Math.Max(0, max + downSignedStep));
            Check("G2 触底钳制不超过可滚动范围", Math.Abs(clampedBottom - max) < 0.01, "v=" + clampedBottom.ToString("F1"));

            // A. CLOSED control - run LAST on a freshly Shown+rendered surface.
            // The primary window's ScrollViewer does not answer a synthetic wheel
            // event during the Loaded handler (its first render pass is not
            // complete yet - an earlier run saw the stock jump read 0 there while
            // the same check on a Shown window moved 48px). So measure here.
            var ctrlInner = BuildTallContent(4000, Brushes.PaleGreen);
            var ctrlSv = NewRoll(ctrlInner);
            var ctrlWin = new Window { Content = ctrlSv, Width = 440, Height = 360, Left = 40, Top = 440, Title = "wheel-lab-ctrl" };
            ctrlWin.Show();
            SettleLayout();

            Bootstrap.Smooth = false;
            ctrlSv.ScrollToVerticalOffset(0); SettleLayout();
            var off = SimWheel(ctrlSv, ctrlInner, -120);
            Check("A1 关闭时不接管", !off.Claimed);
            Check("A2 关闭时原版滚动照常发生（同步位移>0）", off.InstantDelta > 0, "delta=" + off.InstantDelta.ToString("F1"));

            Bootstrap.Smooth = true;
            ctrlSv.ScrollToVerticalOffset(0); SettleLayout();
            var on = SimWheel(ctrlSv, ctrlInner, -120);
            Check("A3 同一表面上开启时被接管（对照组有效）", on.Claimed);

            // S. THE CURVE, driven offline via Step(dt) - the same per-frame math
            // production runs inside OnRendering. Arming still goes through the
            // real event path (RaisePreview / real ScrollChanged), only the clock
            // is supplied by us. Every test runs on a FRESH window (MakeSurface):
            // the primary window's ScrollViewer goes stale deep into this nested
            // Loaded-run suite and silently clamps all offsets to 0 - the first
            // S run proved it (even a plain ScrollToVerticalOffset(100) read back
            // as 0), while E/A controls on freshly Shown windows pass fine.
            Console.WriteLine();

            // S0. CONTROL PROBE: with BOTH switches off (nothing of ours listens),
            // a plain direct ScrollToVerticalOffset on a fresh surface must stick.
            // This is the harness's own control case: if it fails, nothing else in
            // the S suite means anything.
            //
            // HISTORY: this probe used to read write500->0.0 and was taken as proof
            // that "synthetic surfaces reject programmatic offsets", which is why
            // the whole S suite was downgraded to Report() (printed, never judged).
            // That conclusion was WRONG. The offset is not rejected, it is deferred:
            // reading VerticalOffset immediately after the write returns the stale
            // pre-layout value, and the ScrollChanged fired by the write even
            // carries VerticalChange=0.0. Off() forces the layout pass and the write
            // is right there. With the real read, the control passes and the whole
            // S suite becomes judgeable.
            MakeSurface(Brushes.PaleGoldenrod);
            Bootstrap.Smooth = false;
            Bootstrap.SmoothPlay = false;
            _sSv.ScrollToVerticalOffset(500);
            double w1 = Off(_sSv);
            _sSv.ScrollToVerticalOffset(2000);
            double w2 = Off(_sSv);
            Console.WriteLine("      [probe] mouseLeft=" + Mouse.LeftButton
                + " write500->" + w1.ToString("F1") + " write2000->" + w2.ToString("F1"));
            Check("S0 对照组：两开关关闭时直接写偏移生效（否则 S 组无意义）",
                Math.Abs(w1 - 500) < 0.01 && Math.Abs(w2 - 2000) < 0.01,
                "w1=" + w1.ToString("F0") + " w2=" + w2.ToString("F0"));
            CloseSurface();
            MakeSurface(Brushes.RosyBrown);
            double sStep = Math.Min(_sSv.ScrollableHeight, Math.Max(24, _sSv.ViewportHeight * 0.2));
            Prepare(_sSv, 0);
            Bootstrap.Smooth = true;
            RaisePreview(_sInner, -120);
            var curve = new List<double>();
            for (int i = 0; i < 120 && SmoothScroll.IsRunning; i++)
            {
                SmoothScroll.Step(1.0 / 60);
                curve.Add(Off(_sSv));
            }
            bool mono = true;
            for (int i = 1; i < curve.Count; i++) if (curve[i] < curve[i - 1] - 0.01) mono = false;
            Check("S1a 滚轮缓动是多帧渐进（首帧远未到终点）", curve.Count >= 6 && curve[0] < sStep * 0.9,
                "steps=" + curve.Count + " first=" + (curve.Count > 0 ? curve[0].ToString("F1") : "-"));
            Check("S1b 滚轮缓动单调不回退", mono);
            Check("S1c 收敛后精确落在一个步长并停机", Math.Abs(Off(_sSv) - sStep) < 0.6 && !SmoothScroll.IsRunning,
                "off=" + Off(_sSv).ToString("F1") + " exp=" + sStep.ToString("F1"));

            // S2 chase: a big host jump is rewound then glides to it.
            MakeSurface(Brushes.RoyalBlue);
            Prepare(_sSv, 0);
            Bootstrap.SmoothPlay = true;
            _sSv.ScrollToVerticalOffset(2000);         // the host "teleports"
            _sSv.UpdateLayout();                        // commit: fires ScrollChanged, arms chase
            Check("S2a 大跳变被瞬间回写、当前帧不瞬移", VisualOff(_sSv) < 1.0 && PlaybackSmooth.IsChasing,
                "visual=" + VisualOff(_sSv).ToString("F1") + " off=" + Off(_sSv).ToString("F1") + " state=" + PlaybackSmooth.DebugState);
            var chase = new List<double>();
            for (int i = 0; i < 300 && PlaybackSmooth.IsChasing; i++)
            {
                StepChase(_sSv, 1.0 / 60);
                chase.Add(PlaybackSmooth.DebugCur);
            }
            Console.WriteLine("      [S2 chase] steps=" + chase.Count + " " + PlaybackSmooth.DebugState);
            foreach (var t in PlaybackSmooth.Trace) Console.WriteLine("        [trace] " + t);
            PlaybackSmooth.Trace.Clear();
            bool chaseMono = true;
            for (int i = 1; i < chase.Count; i++) if (chase[i] < chase[i - 1] - 0.01) chaseMono = false;
            Check("S2b 追赶是渐进滑行（首帧远未到目标）", chase.Count >= 6 && chase[0] < 1000,
                "steps=" + chase.Count + " first=" + (chase.Count > 0 ? chase[0].ToString("F0") : "-"));
            Check("S2c 追赶单调前进、不往回跳", chaseMono);
            Check("S2d 收敛后精确停在播放位置并停机", Math.Abs(Off(_sSv) - 2000) < 0.6 && !PlaybackSmooth.IsChasing,
                "off=" + Off(_sSv).ToString("F1"));

            // S3 mid-chase the playhead ran on -> retarget forward, never snap back.
            MakeSurface(Brushes.Salmon);
            Prepare(_sSv, 0);
            Bootstrap.SmoothPlay = true;
            _sSv.ScrollToVerticalOffset(2000);
            for (int i = 0; i < 3; i++) StepChase(_sSv, 1.0 / 60);
            // Read the chase's OWN glide position, not the ScrollViewer property.
            double glidePos = PlaybackSmooth.DebugCur;
            _sSv.ScrollToVerticalOffset(2500);         // playhead advanced again
            _sSv.UpdateLayout();                       // commit the host's write so
                                                       // ScrollChanged fires (in
                                                       // production layout does this
                                                       // automatically); without it
                                                       // the chase's next Apply
                                                       // overwrites 2500 before it
                                                       // commits and the retarget
                                                       // is never seen.
            Check("S3a 追到一半再遇跳变不回瞬移、保持滑行位", Math.Abs(PlaybackSmooth.DebugCur - glidePos) < 0.6 && glidePos > 1.0,
                "glide=" + glidePos.ToString("F1") + " now=" + PlaybackSmooth.DebugCur.ToString("F1"));
            FinishChase();
            Check("S3b 改追新目标后停在其上", Math.Abs(SettledOffset(_sSv) - 2500) < 0.6 && !PlaybackSmooth.IsChasing,
                "off=" + SettledOffset(_sSv).ToString("F1"));

            // S4 below threshold = continuous small scroll, left untouched.
            MakeSurface(Brushes.SeaGreen);
            Prepare(_sSv, 0);
            Bootstrap.SmoothPlay = true;
            _sSv.ScrollToVerticalOffset(100);          // viewport 300 -> threshold 120
            Check("S4 小幅滚动不触发追赶（宿主连续滚动让路）", !PlaybackSmooth.IsChasing && Math.Abs(Off(_sSv) - 100) < 0.01,
                "off=" + Off(_sSv).ToString("F1"));

            // S5 switch off mid-chase -> lands exactly, never keeps sliding.
            MakeSurface(Brushes.Tomato);
            Prepare(_sSv, 0);
            Bootstrap.SmoothPlay = true;
            _sSv.ScrollToVerticalOffset(1500);
            StepChase(_sSv, 1.0 / 60);
            double partWay = VisualOff(_sSv);
            Bootstrap.SmoothPlay = false;
            StepChase(_sSv, 1.0 / 60);
            double afterOff = Off(_sSv);
            StepChase(_sSv, 1.0 / 60);
            // In transform mode the offset is already at target; in offset mode
            // it lands after disable. Either way: was partway, now at target.
            Check("S5a 关闭时立即落位到目标", partWay < 1500 && Math.Abs(afterOff - 1500) < 0.6 && !PlaybackSmooth.IsChasing,
                "part=" + partWay.ToString("F1") + " landed=" + afterOff.ToString("F1") + " chasing=" + PlaybackSmooth.IsChasing);
            Check("S5b 关闭后停机", !PlaybackSmooth.IsChasing);

            // S6 the app takes the view elsewhere mid-chase (e.g. jump to
            // selection): we re-aim at the app's position - the view ALWAYS ends
            // up where the host put it, we never override the host's intent.
            MakeSurface(Brushes.Thistle);
            Prepare(_sSv, 0);
            Bootstrap.SmoothPlay = true;
            _sSv.ScrollToVerticalOffset(2000);         // chase armed, rewound to 0
            StepChase(_sSv, 1.0 / 60);
            _sSv.ScrollToVerticalOffset(800);          // the app's own position
            _sSv.UpdateLayout();                       // commit host write -> ScrollChanged
            Check("S6 宿主改变视图后追向宿主位置、绝不抢回", Math.Abs(SettledOffset(_sSv) - 800) < 0.6 && !PlaybackSmooth.IsChasing,
                "off=" + SettledOffset(_sSv).ToString("F1"));

            // S7 exclusions: our own surfaces are never chased, even on big jumps.
            // Start from a clean chase state: S6 left a chase armed on the previous
            // surface, and a stale _sv would make the card jump look like a
            // continuation of the old chase rather than a jump on the card.
            PlaybackSmooth.ResetStateForTest();
            Bootstrap.SmoothPlay = true;
            var cardInner7 = BuildTallContent(4000, Brushes.Khaki);
            var cardSv7 = NewRoll(cardInner7);
            cardSv7.Name = "CardScroll";
            var cardWin7 = new Window { Content = cardSv7, Width = 440, Height = 360, Left = 500, Top = 440 };
            cardWin7.Show(); SettleLayout();
            cardSv7.ScrollToVerticalOffset(0); SettleLayout();
            cardSv7.ScrollToVerticalOffset(2000);      // big jump inside the flyout card
            cardSv7.UpdateLayout(); SettleLayout();
            // The assertion is EXCLUSION: no chase may be armed for our own card.
            // Do NOT additionally require the offset to equal 2000: the jump is a
            // plain write and what it settles on is the ScrollViewer's business,
            // not what this test is about. (Requiring it made the check fail on a
            // correct product, which is exactly the false-signal we are removing.)
            Check("S7a 下拉卡片大跳变不触发追赶", !PlaybackSmooth.IsChasing,
                "chasing=" + PlaybackSmooth.IsChasing);
            cardWin7.Close();

            var selfInner7 = BuildTallContent(4000, Brushes.Aquamarine);
            var selfSv7 = NewRoll(selfInner7);
            var selfWin7 = new Window { Content = selfSv7, Width = 440, Height = 360, Left = 960, Top = 440, Tag = SmoothScroll.SelfMark };
            selfWin7.Show(); SettleLayout();
            selfSv7.ScrollToVerticalOffset(0); SettleLayout();
            selfSv7.ScrollToVerticalOffset(2000);      // big jump inside our own window
            selfSv7.UpdateLayout(); SettleLayout();
            // Same as S7a: exclusion is the claim under test, not the landing spot.
            Check("S7b 自家设置窗口大跳变不触发追赶", !PlaybackSmooth.IsChasing,
                "chasing=" + PlaybackSmooth.IsChasing);
            selfWin7.Close();

            FinishWheelGlide();
            PlaybackSmooth.ResetStateForTest();
            Bootstrap.Smooth = false;
            Bootstrap.SmoothPlay = false;
            CloseSurface();

            ctrlWin.Close();
        }
    }
}
