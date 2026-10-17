// ThemeMenu - restyles VOCALOID6's whole menu bar with a WinUI 3 look and
// adds a "主题" entry. The entry is a LEAF item that opens ThemeWindow (a
// native-framed settings window) instead of a flyout card - this also avoids
// the old race where clicking a flyout item made WPF close the submenu
// instantly and the collapse animation never played.
// Every other (native) menu keeps the animated flyout template; disabled items
// are dimmed like stock WPF/WinUI, and hover only highlights enabled items.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace V6RamTheme
{
    internal static class ThemeMenu
    {
        internal const string Marker = "v6-ram-theme-menu";

        private static string _dir;
        private static Timer _retry;
        private static int _tries;
        private static bool _installed;

        private static MenuItem _themeRoot;

        // ---------------- usage counter display ----------------
        //
        // A top-level item that is pure information: it shows the cumulative
        // software usage time and cannot be "activated" into doing anything
        // surprising. Clicking it drops a small panel with the breakdown
        // (total / this session / since).
        //
        // Why a MenuItem with children rather than a plain TextBlock: the menu
        // bar is the host's own Menu, and the only thing that lines up with the
        // other entries for free - same height, same baseline, same hover, same
        // theme - is another MenuItem. A bare TextBlock gets a different vertical
        // centre and drifts off the baseline (this project already paid for that
        // lesson when restyling the bar).
        //
        // The dropdown items carry IsEnabled=false so clicking a row does nothing;
        // the SubTemplate dims disabled rows to #5AF0F0F0, which is exactly the
        // "informational, not actionable" look we want (see the disabled-state
        // trigger in SubXaml).

        internal const string UsageMarker = "v6-ram-theme-usage";

        private static MenuItem _usageRoot;

        private static void InstallUsageItem(Menu menu)
        {
            try
            {
                if (menu.Items.Cast<object>().Any(
                        i => i is MenuItem mi && (mi.Tag as string) == UsageMarker))
                    return;

                var root = new MenuItem { Tag = UsageMarker };
                _usageRoot = root;

                // Clicking opens the activity window, same visual language as
                // the theme window. Deferred for the same reason as the theme
                // entry: opening a Window synchronously inside Click runs while
                // the menu is still closing and holding the mouse.
                root.Click += (s, e) =>
                {
                    var d = ((MenuItem)s).Dispatcher;
                    d.BeginInvoke(new Action(() =>
                    {
                        try { UsageWindow.ShowOrFocus(); }
                        catch (Exception ex) { Log.Write("open usage window fail: " + ex.Message); }
                    }), DispatcherPriority.ApplicationIdle);
                };

                menu.Items.Add(root);
                RefreshUsageText();

                // One subscription for the whole process. The value is derived
                // from the project folders, so it does not tick: SetHeaderIfChanged
                // makes the 1s stream of notifications a no-op after the first.
                UsageStats.Changed += RefreshUsageText;
                // The project stopwatch has its own 1s beat; hook it too so the
                // bar keeps moving even if the usage ticker is throttled.
                ProjectTimer.Changed += RefreshUsageText;

                Log.Write("usage counter installed in menu bar");
            }
            catch (Exception ex)
            {
                Log.Write("usage item install fail: " + ex.Message);
            }
        }

        // Whether the bar also shows the current project's stopwatch.
        // Persisted in v6.cfg ("showproject"); the cumulative figure is always
        // shown because it is the original purpose of the item.
        private static bool _showProject = true;

        internal static bool ShowProjectTimer
        {
            get { return _showProject; }
            set
            {
                if (_showProject == value) return;
                _showProject = value;
                PersistShowProject();
                RefreshUsageText();
            }
        }

        private static void PersistShowProject()
        {
            try
            {
                var cfg = Path.Combine(_dir, "v6.cfg");
                var lines = File.Exists(cfg)
                    ? File.ReadAllLines(cfg).ToList()
                    : new List<string>();
                bool done = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    var t = lines[i].Trim();
                    var eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    if (t.Substring(0, eq).Trim() != "showproject") continue;
                    lines[i] = "showproject=" + (_showProject ? "1" : "0");
                    done = true;
                    break;
                }
                if (!done) lines.Add("showproject=" + (_showProject ? "1" : "0"));
                File.WriteAllText(cfg, string.Join(Environment.NewLine, lines) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Log.Write("persist showproject fail: " + ex.Message);
            }
        }

        private static void RefreshUsageText()
        {
            try
            {
                if (_usageRoot == null) return;

                // Two figures, clearly labelled:
                //   累计使用  - this session + the whole project library (estimate)
                //   当前工程  - the STOPWATCH for the open .vpr
                // They are different kinds of number, so they are not summed.
                var total = UsageStats.Total + TimeSpan.FromHours(ProjectStats.TotalEstHours);
                var text = "累计使用 " + UsageStats.Fmt(total);

                var sel = ProjectTimer.Selected;
                if (_showProject && !string.IsNullOrEmpty(sel))
                    text += "   ·   " + ShortName(sel) + " " + UsageStats.Fmt(ProjectTimer.SelectedElapsed);

                SetHeaderIfChanged(_usageRoot, text);
            }
            catch (Exception ex)
            {
                Log.Write("usage refresh fail: " + ex.Message);
            }
        }

        // Project names can be long; the menu bar has a fixed budget. Keep the
        // tail (the part that distinguishes one project from another).
        private static string ShortName(string n)
        {
            if (string.IsNullOrEmpty(n)) return "";
            const int Max = 10;
            return n.Length <= Max ? n : n.Substring(0, Max - 1) + "…";
        }

        // Writing Header unconditionally every second would make WPF re-measure the
        // menu bar once a second for no visible change. Compare first; assignment
        // only happens when the text really differs (i.e. once per minute).
        private static void SetHeaderIfChanged(MenuItem mi, string text)
        {
            if (!Equals(mi.Header as string, text)) mi.Header = text;
        }

        private static readonly object _lock = new object();

        public static string Dir => _dir;
        // ---------------- WinUI 3 templates (XAML strings) ----------------

        private const string XamlNs =
            "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

        // Popup open/close ownership stays with the framework (9.7). Notes:
        //  * PopupAnimation is NONE on purpose. 'Fade' makes WPF rebuild the
        //    layered popup window every frame, and combined with
        //    AllowsTransparency + DropShadowEffect the card could blink or not
        //    paint at all on the first frame. We animate the card's own Opacity
        //    instead: a composited property on a window that is already up.
        //  * Geometry: no Margin here - a margin is INSIDE the popup and used to
        //    shift the card up/left so it covered the menu bar row it belonged
        //    to. Inset is done with internal Padding, which does not move the
        //    popup origin.
        //  * VerticalOffset=0 for top-level items: the card hangs BELOW the bar.
        //
        // BOTTOM-CORNER FIX (replaces the 9.13 known limitation): the card is
        // NO LONGER a Border. A Border rounds only its own background/stroke;
        // its child is laid out into the full rectangle and painted over the
        // bottom corners, and every "patch" tried on top of the Border (inner
        // Border, ClipToBounds, OpacityMask on the Border, Clip on the Border)
        // either missed the stroke or fought the Border's own rounded drawing.
        // New structure draws the shape exactly once and clips content once:
        //   Grid(Card)
        //     ├─ Path(CardShape)   rounded-rect fill + stroke, geometry synced
        //     │                    to ActualWidth/Height on SizeChanged
        //     └─ ScrollViewer(CardScroll, Margin=3) with an OpacityMask built
        //        from the SAME rounded geometry (inset by the margin), so the
        //        item content can never square off a corner.
        // The Path has no desired size (null Data until first sync), so layout
        // is driven entirely by the ScrollViewer, same as the old Padding=3.
        // Popup open/close ownership still stays 100% with the framework.
        private const string CardBlock =
            "<Popup x:Name='PART_Popup' AllowsTransparency='True' Focusable='False' " +
            "       Placement='Bottom' SnapsToDevicePixels='True' " +
            "       PopupAnimation='None' " +
            "       IsOpen='{TemplateBinding IsSubmenuOpen}'>" +
            "  <Border x:Name='Card' MinWidth='210' Background='#F2272727' " +
            "          BorderBrush='#33FFFFFF' BorderThickness='1' CornerRadius='8' " +
            "          Padding='2' RenderTransformOrigin='0.5,0' Opacity='1' " +
            "          TextElement.Foreground='#F0F0F0F0'>" +
            "    <ScrollViewer x:Name='CardScroll' Margin='0' VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Disabled'>" +
            "      <ItemsPresenter SnapsToDevicePixels='True'/>" +
            "    </ScrollViewer>" +
            "  </Border>" +
            "</Popup>";

        private static readonly string TopXaml =
            "<ControlTemplate " + XamlNs + " TargetType='{x:Type MenuItem}'>" +
            "  <Grid TextElement.Foreground='#F0F0F0F0' TextElement.FontSize='13'>" +
            "    " + CardBlock +
            "    <Border x:Name='Bd' Background='Transparent' CornerRadius='4' Padding='10,2,10,2'>" +
            "      <ContentPresenter ContentSource='Header' RecognizesAccessKey='True' VerticalAlignment='Center'/>" +
            "    </Border>" +
            "  </Grid>" +
            "  <ControlTemplate.Triggers>" +
            "    <Trigger Property='Role' Value='TopLevelHeader'>" +
            // Align the card's left edge with the item, and sit it just under
            // the menu bar. A negative Y here is what used to cover the bar.
            "      <Setter TargetName='PART_Popup' Property='HorizontalOffset' Value='-9'/>" +
            "      <Setter TargetName='PART_Popup' Property='VerticalOffset' Value='0'/>" +
            "    </Trigger>" +
            "    <MultiTrigger>" +
            "      <MultiTrigger.Conditions>" +
            "        <Condition Property='IsHighlighted' Value='True'/>" +
            "        <Condition Property='IsEnabled' Value='True'/>" +
            "      </MultiTrigger.Conditions>" +
            "      <Setter TargetName='Bd' Property='Background' Value='#12FFFFFF'/>" +
            "    </MultiTrigger>" +
            "    <Trigger Property='IsSubmenuOpen' Value='True'>" +
            "      <Setter TargetName='Bd' Property='Background' Value='#0CFFFFFF'/>" +
            "    </Trigger>" +
            "    <Trigger Property='IsEnabled' Value='False'>" +
            "      <Setter TargetName='Bd' Property='TextElement.Foreground' Value='#5AF0F0F0'/>" +
            "    </Trigger>" +
            "  </ControlTemplate.Triggers>" +
            "</ControlTemplate>";

        private static readonly string SubXaml =
            "<ControlTemplate " + XamlNs + " TargetType='{x:Type MenuItem}'>" +
            "  <Grid TextElement.Foreground='#F0F0F0F0' TextElement.FontSize='13'>" +
            "    " + CardBlock +
            "    <Border x:Name='Bd' Background='Transparent' CornerRadius='4' Padding='9,7,11,7' Margin='3,1' MinHeight='34' TextElement.Foreground='#F0F0F0F0'>" +
            "      <Grid>" +
            "        <Grid.ColumnDefinitions>" +
            "          <ColumnDefinition Width='Auto'/>" +
            "          <ColumnDefinition Width='*'/>" +
            "          <ColumnDefinition Width='Auto'/>" +
            "          <ColumnDefinition Width='Auto'/>" +
            "        </Grid.ColumnDefinitions>" +
            "        <TextBlock x:Name='CheckIcon' Grid.Column='0' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' " +
            "                   Text='&#xE73E;' FontSize='13' Width='24' TextAlignment='Center' " +
            "                   VerticalAlignment='Center' Opacity='0'/>" +
            "        <ContentPresenter Grid.Column='1' ContentSource='Header' " +
            "                          RecognizesAccessKey='True' VerticalAlignment='Center'/>" +
            "        <ContentPresenter Grid.Column='2' ContentSource='InputGestureText' Margin='26,0,2,0' " +
            "                          TextElement.Foreground='#8CF0F0F0' VerticalAlignment='Center'/>" +
            "        <TextBlock x:Name='Chevron' Grid.Column='3' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' " +
            "                   Text='&#xE76C;' FontSize='11' Margin='8,0,2,0' VerticalAlignment='Center' " +
            "                   Visibility='Collapsed'/>" +
            "      </Grid>" +
            "    </Border>" +
            "  </Grid>" +
            "  <ControlTemplate.Triggers>" +
            "    <Trigger Property='Role' Value='SubmenuHeader'>" +
            "      <Setter TargetName='PART_Popup' Property='Placement' Value='Right'/>" +
            "      <Setter TargetName='PART_Popup' Property='HorizontalOffset' Value='-2'/>" +
            "      <Setter TargetName='PART_Popup' Property='VerticalOffset' Value='-9'/>" +
            "    </Trigger>" +
            "    <MultiTrigger>" +
            "      <MultiTrigger.Conditions>" +
            "        <Condition Property='IsHighlighted' Value='True'/>" +
            "        <Condition Property='IsEnabled' Value='True'/>" +
            "      </MultiTrigger.Conditions>" +
            "      <Setter TargetName='Bd' Property='Background' Value='#12FFFFFF'/>" +
            "    </MultiTrigger>" +
            "    <Trigger Property='IsChecked' Value='True'>" +
            "      <Setter TargetName='CheckIcon' Property='Opacity' Value='1'/>" +
            "    </Trigger>" +
            "    <Trigger Property='IsEnabled' Value='False'>" +
            "      <Setter TargetName='Bd' Property='TextElement.Foreground' Value='#5AF0F0F0'/>" +
            "      <Setter TargetName='CheckIcon' Property='Opacity' Value='0'/>" +
            "    </Trigger>" +
            "  </ControlTemplate.Triggers>" +
            "</ControlTemplate>";

        // Leaf top-level items ("主题") get a template WITHOUT any PART_Popup.
        //
        // Why this matters: WPF's MenuItem treats a named PART_Popup as *the*
        // submenu host. A leaf item that carries one still participates in
        // submenu open/close bookkeeping, so clicking it makes WPF collapse the
        // parent flyout at the same moment our Click handler runs, and the
        // settings window only flashes. A leaf owns no submenu, so its template
        // must own no PART_Popup either.
        private static readonly string LeafXaml =
            "<ControlTemplate " + XamlNs + " TargetType='{x:Type MenuItem}'>" +
            "  <Grid TextElement.Foreground='#F0F0F0F0' TextElement.FontSize='13' Background='Transparent'>" +
            "    <Border x:Name='Bd' Background='Transparent' CornerRadius='4' Padding='10,2,10,2'>" +
            "      <StackPanel Orientation='Horizontal'>" +
            "        <Ellipse x:Name='Dot' Width='8' Height='8' Fill='#FF8B8B95' Visibility='Collapsed' VerticalAlignment='Center' Margin='0,0,7,0'/>" +
            "        <ContentPresenter ContentSource='Header' RecognizesAccessKey='True' VerticalAlignment='Center'/>" +
            "      </StackPanel>" +
            "    </Border>" +
            "  </Grid>" +
            "  <ControlTemplate.Triggers>" +
            "    <MultiTrigger>" +
            "      <MultiTrigger.Conditions>" +
            "        <Condition Property='IsHighlighted' Value='True'/>" +
            "        <Condition Property='IsEnabled' Value='True'/>" +
            "      </MultiTrigger.Conditions>" +
            "      <Setter TargetName='Bd' Property='Background' Value='#12FFFFFF'/>" +
            "    </MultiTrigger>" +
            "    <Trigger Property='IsEnabled' Value='False'>" +
            "      <Setter TargetName='Bd' Property='TextElement.Foreground' Value='#5AF0F0F0'/>" +
            "    </Trigger>" +
            "  </ControlTemplate.Triggers>" +
            "</ControlTemplate>";

        private static ControlTemplate _topTemplate;
        private static ControlTemplate _subTemplate;
        private static ControlTemplate _leafTemplate;

        private static ControlTemplate TopTemplate =>
            _topTemplate ?? (_topTemplate = (ControlTemplate)XamlReader.Parse(TopXaml));

        private static ControlTemplate SubTemplate =>
            _subTemplate ?? (_subTemplate = (ControlTemplate)XamlReader.Parse(SubXaml));

        private static ControlTemplate LeafTemplate =>
            _leafTemplate ?? (_leafTemplate = (ControlTemplate)XamlReader.Parse(LeafXaml));

        // ---------------- flyout refs + animation ----------------

        private sealed class Fly
        {
            public FrameworkElement Card;
            public System.Windows.Controls.Primitives.Popup Popup;   // owned popup (template PART_Popup)
            public TranslateTransform Slide;
        }

        private static readonly ConditionalWeakTable<MenuItem, Fly> Flies = new();
        private static readonly ConditionalWeakTable<MenuItem, object> Hooked = new();

        private static Fly Resolve(MenuItem mi)
        {
            Fly fly;
            if (Flies.TryGetValue(mi, out fly)) return fly;

            try
            {
                if (!mi.HasItems) return null;
                if (!mi.IsInitialized) mi.ApplyTemplate();
                var c = mi.Template.FindName("Card", mi) as FrameworkElement;
                if (c == null) return null;

                var tt = new TranslateTransform(0, 0);
                c.RenderTransform = new TransformGroup { Children = { tt } };
                c.RenderTransformOrigin = new Point(0.5, 0);

                fly = new Fly { Card = c, Slide = tt };

                // Rounded-card wiring (replaces the 9.13 known limitation).
                // The old failure mode: a Border rounds only its OWN
                // background/stroke, and the content layer (ScrollViewer ->
                // items) paints as a full rectangle OVER the bottom corners —
                // the corners were drawn, then covered. Fix: split the card
                // into a Path (the ONLY thing that draws the rounded shape:
                // fill + stroke) and a ScrollViewer whose content is clipped
                // by an OpacityMask built from the SAME concentric rounded
                // geometry. Nothing square covers a corner any more, and the
                // shape is drawn exactly once (no Border-vs-Clip fighting
                // that corrupted the card in attempt 5 of 9.13).

                Flies.Add(mi, fly);
                // Popup.Opened fires only after the popup's layered window has
                // actually been created and shown. Animation started from
                // SubmenuOpened (sync or via dispatcher defer) finishes before
                // that first visible frame, which reads as "no animation".
                var popup = mi.Template.FindName("PART_Popup", mi) as System.Windows.Controls.Primitives.Popup;
                if (popup != null)
                {
                    fly.Popup = popup;
                    popup.Opened += (s, e) =>
                    {
                        try
                        {
                            AnimateIn(fly);
                            Log.Write("popup opened: animated card");
                        }
                        catch (Exception ex) { Log.Write("popup opened fail: " + ex.Message); }
                    };
                    if (popup.IsOpen)
                    {
                        try { AnimateIn(fly); }
                        catch (Exception ex) { Log.Write("popup already-open fail: " + ex.Message); }
                    }
                }
                else
                {
                    Log.Write("resolve: PART_Popup not found for " + Convert.ToString(mi.Header));
                }

                return fly;
            }
            catch (Exception ex)
            {
                Log.Write("resolve fly fail: " + ex.Message);
                return null;
            }
        }

        // (rounded corners are drawn natively by the Border template;
        //  no runtime geometry sync is needed)

                // Entrance: slide + opacity fade. Deliberately NO scale animation - a
        // ScaleTransform makes the shadowed card re-rasterise every frame.
        private static void AnimateIn(Fly fly)
        {
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var sb = new Storyboard();

            fly.Slide.BeginAnimation(TranslateTransform.YProperty, null);
            fly.Card.BeginAnimation(UIElement.OpacityProperty, null);
            fly.Slide.Y = 8;

            var ty = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease };
            Storyboard.SetTarget(ty, fly.Slide);
            Storyboard.SetTargetProperty(ty, new PropertyPath(TranslateTransform.YProperty));
            sb.Children.Add(ty);

            var op = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease };
            Storyboard.SetTarget(op, fly.Card);
            Storyboard.SetTargetProperty(op, new PropertyPath(UIElement.OpacityProperty));
            sb.Children.Add(op);

            sb.Begin();
        }

        // ---------------- styling + wiring ----------------

        private static void StyleItem(MenuItem mi, bool topLevel)
        {
            // A top-level item with no children (our "主题" entry) must NOT get
            // the popup-bearing template - see LeafXaml for why.
            mi.Template = topLevel
                ? (mi.HasItems ? TopTemplate : LeafTemplate)
                : SubTemplate;

            if (Hooked.TryGetValue(mi, out _)) return;
            Hooked.Add(mi, true);

            if (mi.HasItems)
            {
                mi.SubmenuOpened += (s, e) =>
                {
                    if (!Equals(e.OriginalSource, s)) return;
                    // Resolve() registers the Popup.Opened handler that plays
                    // the entrance animation once the popup is on screen.
                    Resolve((MenuItem)s);
                };
            }

            foreach (var o in mi.Items.OfType<MenuItem>())
                StyleItem(o, false);
        }

        private static void StyleMenuBar(Menu menu)
        {
            foreach (var top in menu.Items.OfType<MenuItem>())
                StyleItem(top, true);
        }

        // ---------------- install flow ----------------

        public static void Install(string dir)
        {
            _dir = dir;
            LoadShowProject();
            _retry = new Timer(_ => TryOnThreadPool(), null, 1500, 1500);
        }

        private static void LoadShowProject()
        {
            try
            {
                var cfg = Path.Combine(_dir, "v6.cfg");
                if (!File.Exists(cfg)) return;
                foreach (var raw in File.ReadAllLines(cfg))
                {
                    var line = raw.Trim();
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (line.Substring(0, eq).Trim() != "showproject") continue;
                    _showProject = Bootstrap.ParseBool(line.Substring(eq + 1).Trim());
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Write("load showproject fail: " + ex.Message);
            }
        }

        private static void TryOnThreadPool()
        {
            try
            {
                lock (_lock) { if (_installed) return; }
                var app = Application.Current;
                if (app == null) return;

                app.Dispatcher.Invoke(() =>
                {
                    try { TryOnUi(); }
                    catch (Exception ex) { Log.Write("menu install fail: " + ex.Message); }
                });

                lock (_lock)
                {
                    if (_installed || ++_tries > 240)
                        _retry?.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log.Write("menu retry fail: " + ex.Message);
            }
        }

        private static void TryOnUi()
        {
            lock (_lock) { if (_installed) return; }

            var app = Application.Current;
            if (app == null) return;

            Menu menu = null;
            foreach (Window w in app.Windows)
            {
                menu = FindMenu(w);
                if (menu != null) break;
            }
            if (menu == null) return;

            if (!menu.Items.Cast<object>().Any(
                    i => i is MenuItem mi && (mi.Tag as string) == Marker))
            {
                var root = new MenuItem { Header = "主题", Tag = Marker };
                _themeRoot = root;
                ThemeWindow.OnThemeChanged += _ => UpdateThemeDot();
                // Defer the window creation until the menu has fully settled.
                // Opening a Window synchronously inside Click runs while WPF is
                // still closing the menu and capturing the mouse; the new window
                // can lose activation and appear to flash. Post to the dispatcher
                // so the click finishes first.
                root.Click += (s, e) =>
                {
                    var d = ((MenuItem)s).Dispatcher;
                    d.BeginInvoke(new Action(() =>
                    {
                        try { ThemeWindow.ShowOrFocus(_dir); }
                        catch (Exception ex) { Log.Write("open theme window fail: " + ex.Message); }
                    }), DispatcherPriority.ApplicationIdle);
                };
                menu.Items.Add(root);
            }

            InstallUsageItem(menu);

            StyleMenuBar(menu);

            // Wheel interception must be registered on the UI thread once the
            // host app exists; it is independent of the colour mode and of the
            // current switch state (the handler checks the flag per event, so a
            // toggle in the settings window applies immediately).
            SmoothScroll.Install();
            PlaybackSmooth.Install();

            UpdateThemeDot();

            lock (_lock) { _installed = true; }
            Log.Write("winui menu bar styled; theme entry opens settings window");

            // debug aid: auto-open the settings window right after injection
            if (Environment.GetEnvironmentVariable("V6_THEME_WINDOW") == "1")
            {
                _ = menu.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { ThemeWindow.ShowOrFocus(_dir); }
                    catch (Exception ex) { Log.Write("auto-open theme window fail: " + ex.Message); }
                }), DispatcherPriority.ApplicationIdle);
            }
        }


        // Refresh the accent-dot on the "主题" menu item. Uses the live token;
        // called at install time and whenever the settings window saves.
        private static void UpdateThemeDot()
        {
            try
            {
                if (_themeRoot == null) return;
                _themeRoot.ApplyTemplate();
                var dot = _themeRoot.Template?.FindName("Dot", _themeRoot) as System.Windows.Shapes.Ellipse;
                if (dot == null) return;
                var col = Bootstrap.ResolveAccent(Bootstrap.ActiveToken);
                dot.Fill = col.HasValue
                    ? new SolidColorBrush(col.Value)
                    : Brushes.Gray;
                dot.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Log.Write("update theme dot fail: " + ex.Message);
            }
        }

        // ---- visual/logical tree search for the WPF Menu ----

        private static Menu FindMenu(DependencyObject root)
        {
            if (root == null) return null;
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(root);
            int guard = 0;
            while (queue.Count > 0 && guard++ < 20000)
            {
                var node = queue.Dequeue();
                if (node is Menu m && m.IsVisible) return m;

                foreach (var c in LogicalChildrenOf(node)) queue.Enqueue(c);
                if (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
                {
                    int vc = VisualTreeHelper.GetChildrenCount(node);
                    for (int i = 0; i < vc; i++) queue.Enqueue(VisualTreeHelper.GetChild(node, i));
                }
            }
            return null;
        }

        private static IEnumerable<DependencyObject> LogicalChildrenOf(DependencyObject d)
        {
            foreach (var o in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
                yield return o;
        }
    }
}
