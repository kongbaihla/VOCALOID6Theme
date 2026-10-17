// ThemeWindow - a WinUI-3-styled settings window opened from the "主题" menu.
// Contents: accent presets + custom hex input, tint-saturation and panel-tint
// sliders, and a live preview that re-renders a miniature editor mock using
// the same tint algorithm the hook applies at startup.
// Save writes v6.theme (token) and v6.cfg (sat/bg). Full-page recoloring
// itself needs an editor restart because WPF parses colors once.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shell;

namespace V6RamTheme
{
    internal static class ThemeWindow
    {
        private static Window _win;
        // Raised after a successful save so the menu indicator (and any
        // other live surface) can refresh without a restart.
        internal static event Action<string> OnThemeChanged;
        private static string _dir;

        // live editing state (preview only; persisted on save)
        private static string _token = "off";
        private static double _sat = 0.85;
        private static double _bg = 0.35;
        // unlike the colour state above, toggling this ALSO writes through to
        // Bootstrap.Smooth immediately - wheel easing is live behaviour, not
        // cached colours, so the editor responds without a restart.
        private static bool _smooth = false;
        // 播放跟随平滑：视图跳到播放头/选区时改为滑行追赶。同样即时生效。
        private static bool _play = false;
        private static readonly (string label, string token)[] Presets =
        {
            ("原版", "off"),
            ("紫", "#8B5CF6"),
            ("蓝", "#3B82F6"),
            ("橙", "#F59E0B"),
            ("粉", "#EC4899"),
            ("亮青", "#22D3EE"),
            ("红", "#EF4444"),
            ("绿", "#22C55E"),
            ("黄", "#EAB308"),
            ("玫红", "#F43F5E"),
        };

        // mock colors of a VOCALOID-like editor; every one goes through the
        // same tint rule as ColorPatch so the preview is faithful.
        private static readonly (string key, string hex)[] Mock =
        {
            ("titlebar",  "#FF1B1B1C"),
            ("menubar",   "#FF252526"),
            ("toolbar",   "#FF2D2D30"),
            ("headercol", "#FF2D2D30"),
            ("canvas",    "#FF1E1E1E"),
            ("gridline",  "#FF3F3F46"),
            ("accent",    "#FF007ACC"),
            ("accent2",   "#FF29ABE2"),
            ("notered",   "#FFC80000"),
        };

        private static readonly Dictionary<string, string> MockMap =
            new() { ["titlebar"] = "#FF1B1B1C", ["menubar"] = "#FF252526", ["toolbar"] = "#FF2D2D30",
                    ["headercol"] = "#FF2D2D30", ["canvas"] = "#FF1E1E1E", ["gridline"] = "#FF3F3F46",
                    ["accent"] = "#FF007ACC", ["accent2"] = "#FF29ABE2", ["notered"] = "#FFC80000" };

        // preview elements tagged with their mock key (walked recursively)
        private static FrameworkElement _previewRoot;
        private static readonly Dictionary<string, Border> _swatchRings = new();
        private static TextBlock _satVal, _bgVal;
        private static TextBox _hexBox;
        private static ToggleView _toggleScroll;
        private static ToggleView _togglePlay;
        private static TextBlock _smoothHint;

        // frame-rate cap control (auto / 60 / 120)
        private static double _fps;
        private static string _fpsAutoText = "自动";
        private static readonly List<Button> _fpsButtons = new();

        // WinUI-style toggle switch (hand-built: rounded track + knob), shared
        // by the two live switches. Colors are literal and the window is built
        // under ColorPatch.Suppress, so they never get tinted (9.10).
        private sealed class ToggleView
        {
            public readonly Border Track;
            public readonly Border Knob;
            public readonly TextBlock Label;
            public Func<bool> State;
            public Action OnToggle;
            public string OnText = "已开启（即时生效）";
            public string OffText = "已关闭";

            public ToggleView()
            {
                Track = new Border
                {
                    Width = 44,
                    Height = 22,
                    CornerRadius = new CornerRadius(11),
                    BorderThickness = new Thickness(1),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Knob = new Border
                {
                    Width = 14,
                    Height = 14,
                    CornerRadius = new CornerRadius(7),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Track.Child = Knob;
                Label = new TextBlock { FontSize = 12, Foreground = Fg, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                row.Children.Add(Track);
                row.Children.Add(Label);
                var hit = new Border
                {
                    Child = row,
                    Background = Brushes.Transparent,
                    Cursor = System.Windows.Input.Cursors.Hand,
                };
                hit.MouseLeftButtonUp += (s, e) => { OnToggle?.Invoke(); Render(); };
                Host = hit;
            }

            public readonly Border Host;

            public void Render()
            {
                var on = State != null && State();
                Track.Background = on ? AccentBrush() : B("#33FFFFFF");
                Track.BorderBrush = on ? Brushes.Transparent : B("#55FFFFFF");
                Knob.Background = on ? B("#FF101010") : B("#FFD0D0D6");
                // knob slides 4..26 inside the 44-wide track (14-wide knob, 4 padding)
                Knob.Margin = new Thickness(on ? 26 : 4, 0, 0, 0);
                Label.Text = on ? OnText : OffText;
            }
        }

        public static void ShowOrFocus(string dir)
        {
            _dir = dir;
            if (_win != null)
            {
                try { _win.Activate(); return; } catch { _win = null; }
            }
            try
            {
                LoadCurrent();
                // Build the window with the color hook suppressed: every literal
                // in here is our own UI palette and must survive untouched.
                using (ColorPatch.Suppress())
                {
                    _win = Build();
                }
                _win.Show();
                Log.Write("theme window opened (token=" + _token + ")");
            }
            catch (Exception ex)
            {
                Log.Write("theme window fail: " + ex.Message);
                _win = null;
            }
        }

        private static void LoadCurrent()
        {
            _swatchRings.Clear();
            try
            {
                var f = Path.Combine(_dir, "v6.theme");
                if (File.Exists(f)) _token = (File.ReadAllLines(f).FirstOrDefault() ?? "off").Trim();
                var cfg = Path.Combine(_dir, "v6.cfg");
                if (File.Exists(cfg))
                {
                    foreach (var ln in File.ReadAllLines(cfg))
                    {
                        var kv = ln.Split('=');
                        if (kv.Length != 2) continue;
                        if (kv[0].Trim() == "sat" && double.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
                            _sat = Math.Min(1.0, Math.Max(0.3, s));
                        if (kv[0].Trim() == "bg" && double.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
                            _bg = Math.Min(1.0, Math.Max(0.0, b));
                        // smooth is intentionally NOT taken from the file here -
                        // see below: the toggle mirrors the LIVE state instead.
                    }
                }
            }
            catch { }
            if (string.IsNullOrEmpty(_token)) _token = "off";
            // Both live toggles mirror the RUNNING flags, not the last saved
            // value: colour needs a restart so its saved==live, but scrolling
            // and playback follow are live, so they show what is happening now.
            _smooth = Bootstrap.Smooth;
            _play = Bootstrap.SmoothPlay;
            _fps = Bootstrap.MaxFps;
        }

        // ---------------- brushes ----------------

        // NOTE: do NOT use BrushConverter here. WPF routes color strings through
        // MS.Internal.Parsers - exactly what this hook patches - so every fixed
        // color in the settings window (swatches, card background, text) would
        // itself get tinted to the CURRENT accent. That is why the six preset
        // swatches used to render as six shades of the active accent instead of
        // their own purple/blue/orange/pink/cyan. Parse the hex arithmetically
        // so the hook never sees it.
        private static SolidColorBrush B(string hex)
        {
            var s = hex.TrimStart('#');
            uint v = Convert.ToUInt32(s, 16);
            byte a, r, g, b;
            if (s.Length == 8) { a = (byte)(v >> 24); r = (byte)(v >> 16); g = (byte)(v >> 8); b = (byte)v; }
            else { a = 255; r = (byte)(v >> 16); g = (byte)(v >> 8); b = (byte)v; }
            var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            br.Freeze();
            return br;
        }

        private static SolidColorBrush BrushFromArgb(uint argb) =>
            new(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

        private static readonly Brush Fg = B("#F0F0F0F0");
        private static readonly Brush FgDim = B("#9AF0F0F0");
        private static readonly Brush CardBg = B("#F2202020");
        private static readonly Brush CardEdge = B("#1FFFFFFF");

        // ---------------- generic builders ----------------

        private static TextBlock T(string text, double size, bool dim = false) => new()
        {
            Text = text,
            FontSize = size,
            Foreground = dim ? FgDim : Fg,
            Margin = new Thickness(0, 0, 0, 6),
        };

        private static Border Keyed(string key, Border inner)
        {
            inner.Tag = key;
            return inner;
        }

        private static ControlTemplate RoundButtonTemplate(double radius) => (ControlTemplate)XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
            "  <Border x:Name='Bd' CornerRadius='" + radius + "' Background='#14FFFFFF' BorderBrush='#22FFFFFF' BorderThickness='1'>" +
            "    <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>" +
            "  </Border>" +
            "  <ControlTemplate.Triggers>" +
            "    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Background' Value='#24FFFFFF'/></Trigger>" +
            "    <Trigger Property='IsPressed' Value='True'><Setter TargetName='Bd' Property='Background' Value='#0AFFFFFF'/></Trigger>" +
            "  </ControlTemplate.Triggers>" +
            "</ControlTemplate>");

        // Chip template for the frame-cap buttons: background and border follow
        // the button's own properties so the selected chip can be highlighted.
        private static ControlTemplate ChipTemplate() => (ControlTemplate)XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
            "  <Border x:Name='Bd' CornerRadius='4' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1'>" +
            "    <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>" +
            "  </Border>" +
            "</ControlTemplate>");

        private static Button AccentBtn(string content)
        {
            var tpl = (ControlTemplate)XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
                "  <Border x:Name='Bd' CornerRadius='4' Background='{TemplateBinding Background}' BorderBrush='#33FFFFFF' BorderThickness='1'>" +
                "    <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>" +
                "  </Border>" +
                "  <ControlTemplate.Triggers>" +
                "    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Opacity' Value='0.9'/></Trigger>" +
                "  </ControlTemplate.Triggers>" +
                "</ControlTemplate>");
            return new Button
            {
                Content = content,
                Template = tpl,
                Background = AccentBrush(),
                Foreground = B("#FF101010"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(24, 10, 24, 10),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
        }

        // The accent as DISPLAYED (raw #RRGGBB), shared by the save button,
        // preset swatches and the preview's active-color chips. Going through
        // Bootstrap.ResolveAccent keeps all three identical; "off" falls back to
        // the neutral gray so the button still reads as a button.
        private static Brush AccentBrush()
        {
            var c = Bootstrap.ResolveAccent(_token);
            return c.HasValue ? new SolidColorBrush(c.Value) : B("#FF8B8B95");
        }

        // ---------------- main window ----------------

        private static Window Build()
        {
            // ===== left: settings =====
            var left = new StackPanel { Width = 360, Margin = new Thickness(20, 14, 6, 12) };
            left.Children.Add(T("主题设置", 20));
            left.Children.Add(T("选定配色后重启编辑器，整个界面按其重新渲染", 12, true));

            left.Children.Add(T("强调色", 14));
            var swatchRow = new WrapPanel();
            foreach (var (label, token) in Presets)
            {
                var inner = new Border
                {
                    Width = 34,
                    Height = 34,
                    CornerRadius = new CornerRadius(6),
                    Background = token == "off" ? B("#14FFFFFF") : B(token), // swatch shows the TRUE accent hex
                    Child = token == "off"
                        ? new TextBlock { Text = "原", FontSize = 12, Foreground = Fg, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                        : null,
                };
                var ring = new Border
                {
                    CornerRadius = new CornerRadius(9),
                    BorderThickness = new Thickness(2),
                    BorderBrush = Brushes.Transparent,
                    Padding = new Thickness(3),
                    Child = inner,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                ring.MouseLeftButtonUp += (s, e) => { _token = token; _hexBox.Text = token == "off" ? "#RRGGBB" : token; RefreshAll(); };
                _swatchRings[token] = ring;

                // Keep swatch and its caption in ONE vertical cell so WrapPanel
                // wraps whole cells and the caption stays centered under its
                // swatch - as loose siblings they drifted apart (the old code
                // nudged the caption with negative margins and it broke as soon
                // as the row wrapped).
                var cap = T(label, 11, true);
                cap.HorizontalAlignment = HorizontalAlignment.Center;
                cap.Margin = new Thickness(0, 4, 0, 0);
                var cell = new StackPanel { Margin = new Thickness(0, 0, 10, 10) };
                cell.Children.Add(ring);
                cell.Children.Add(cap);
                swatchRow.Children.Add(cell);
            }
            left.Children.Add(swatchRow);

            _hexBox = new TextBox
            {
                Width = 150,
                FontSize = 13,
                Padding = new Thickness(6, 5, 6, 5),
                Background = B("#18FFFFFF"),
                Foreground = Fg,
                CaretBrush = Fg,
                BorderBrush = B("#30FFFFFF"),
                Text = _token == "off" ? "#RRGGBB" : _token,
            };
            _hexBox.LostFocus += (s, e) => TryApplyHex();
            _hexBox.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) TryApplyHex(); };
            var hexPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 12) };
            hexPanel.Children.Add(_hexBox);
            var hint = T("自定义 #RRGGBB，回车生效", 11, true);
            hint.Margin = new Thickness(10, 3, 0, 0);
            hexPanel.Children.Add(hint);
            left.Children.Add(hexPanel);

            left.Children.Add(T("染色强度 · 彩色元素跟随主色的饱和度", 12, true));
            var satPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            var satSl = new Slider { Minimum = 0.3, Maximum = 1.0, Value = _sat, Width = 250, VerticalAlignment = VerticalAlignment.Center };
            satSl.ValueChanged += (s, e) => { _sat = e.NewValue; _satVal.Text = _sat.ToString("F2"); RefreshPreview(); };
            _satVal = T(_sat.ToString("F2"), 12);
            _satVal.Margin = new Thickness(10, 0, 0, 4);
            satPanel.Children.Add(satSl);
            satPanel.Children.Add(_satVal);
            left.Children.Add(satPanel);

            left.Children.Add(T("面板染色 · 菜单栏/顶栏等深色底的主色倾向", 12, true));
            var bgPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            var bgSl = new Slider { Minimum = 0.0, Maximum = 1.0, Value = _bg, Width = 250, VerticalAlignment = VerticalAlignment.Center };
            bgSl.ValueChanged += (s, e) => { _bg = e.NewValue; _bgVal.Text = _bg.ToString("F2"); RefreshPreview(); };
            _bgVal = T(_bg.ToString("F2"), 12);
            _bgVal.Margin = new Thickness(10, 0, 0, 4);
            bgPanel.Children.Add(bgSl);
            bgPanel.Children.Add(_bgVal);
            left.Children.Add(bgPanel);

            // ---- two live toggles (scroll + playback follow) ----
            left.Children.Add(T("钢琴卷帘平滑滚动 · 滚轮改为缓动", 12, true));
            _smoothHint = T("开启后滚动带惯性缓动，即时生效、无需重启；Ctrl+滚轮缩放不受影响。", 11, true);
            _smoothHint.TextWrapping = TextWrapping.Wrap;
            _smoothHint.Margin = new Thickness(0, 0, 0, 6);
            left.Children.Add(_smoothHint);

            _toggleScroll = new ToggleView
            {
                State = () => _smooth,
                OnText = "已开启（即时生效）",
                OffText = "已关闭（原版逐格滚动）",
                OnToggle = () =>
                {
                    _smooth = !_smooth;
                    Bootstrap.Smooth = _smooth;   // live: no restart needed
                    PersistToggles();
                    Log.Write("smooth scroll " + (_smooth ? "enabled" : "disabled") + " (live, persisted)");
                },
            };
            left.Children.Add(_toggleScroll.Host);

            left.Children.Add(T("播放跟随平滑 · 视图跳向播放头/选区时改为滑行追赶", 12, true));
            var playHint = T("播放时画面不再整屏瞬移，而是平滑追赶到播放位置；拖动滚动条时自动让路，即时生效。", 11, true);
            playHint.TextWrapping = TextWrapping.Wrap;
            playHint.Margin = new Thickness(0, 0, 0, 6);
            left.Children.Add(playHint);

            _togglePlay = new ToggleView
            {
                State = () => _play,
                OnText = "已开启（即时生效）",
                OffText = "已关闭（原版瞬移）",
                OnToggle = () =>
                {
                    _play = !_play;
                    Bootstrap.SmoothPlay = _play;  // live: no restart needed
                    PersistToggles();
                    Log.Write("playback follow " + (_play ? "enabled" : "disabled") + " (live, persisted)");
                },
            };
            left.Children.Add(_togglePlay.Host);

            // ---- frame-rate cap for both glides (auto / 60 / 120) ----
            left.Children.Add(T("滑行帧率上限 · 自动按屏幕刷新率运行", 12, true));
            var fpsHint = T("高刷屏上每帧都会重排大画布；限到 60/120 可减轻卡顿。即时生效。", 11, true);
            fpsHint.TextWrapping = TextWrapping.Wrap;
            fpsHint.Margin = new Thickness(0, 0, 0, 6);
            left.Children.Add(fpsHint);

            var fpsRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            var autoHz = DetectRefreshHz();
            _fpsAutoText = "自动 " + (autoHz > 0 ? ((int)Math.Round(autoHz) + "Hz") : "");
            _fpsButtons.Clear();
            foreach (var (label, hz) in new[]
                     {
                         (_fpsAutoText, 0.0),
                         ("60", 60.0),
                         ("120", 120.0),
                     })
            {
                var b = new Button
                {
                    Content = label,
                    Template = ChipTemplate(),
                    Foreground = Fg,
                    FontSize = 12,
                    Padding = new Thickness(12, 6, 12, 6),
                    Margin = new Thickness(0, 0, 8, 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Tag = hz,
                };
                b.Click += (s, e) =>
                {
                    _fps = (double)((Button)s).Tag;
                    Bootstrap.MaxFps = _fps;   // live: no restart needed
                    Log.Write("fps cap " + (_fps <= 0 ? "auto" : _fps.ToString("F0")) + " (live, save to persist)");
                    RefreshFpsButtons();
                };
                _fpsButtons.Add(b);
                fpsRow.Children.Add(b);
            }
            left.Children.Add(fpsRow);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
            var saveBtn = AccentBtn("保存并应用（即时生效）");
            var offBtn = new Button { Content = "回到原版", Template = RoundButtonTemplate(4), Foreground = Fg, FontSize = 13, Padding = new Thickness(22, 10, 22, 10), Margin = new Thickness(10, 0, 0, 0), Cursor = System.Windows.Input.Cursors.Hand };
            saveBtn.Click += (s, e) => Save();
            offBtn.Click += (s, e) => { _token = "off"; _hexBox.Text = "#RRGGBB"; RefreshAll(); };
            btnRow.Children.Add(saveBtn);
            btnRow.Children.Add(offBtn);

            var note = T("提示：颜色保存后即时生效；个别非常规界面元素若未更新，重启即完全一致。", 11, true);
            note.Margin = new Thickness(0, 14, 12, 0);
            note.TextWrapping = TextWrapping.Wrap;

            // ===== right: live preview =====
            // ===== right: live preview + actions =====
            _previewRoot = BuildPreview();
            var previewHost = new ScrollViewer
            {
                Content = _previewRoot,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(4, 10, 18, 8),
            };

            // Right column: actions on top, preview fills the middle, note at
            // the bottom - so the buttons are visible at the default window
            // size even when the left settings column is tall.
            var right = new DockPanel { Margin = new Thickness(0, 0, 0, 0) };
            DockPanel.SetDock(btnRow, Dock.Top);
            right.Children.Add(btnRow);
            DockPanel.SetDock(note, Dock.Bottom);
            right.Children.Add(note);
            right.Children.Add(previewHost);

            var content = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(left, Dock.Left);
            content.Children.Add(left);
            content.Children.Add(right);
            // ===== fake WinUI title bar =====
            var titleBar = new DockPanel { Height = 34, Background = KeyBrush("titlebar") };
            var titleTxt = new TextBlock { Text = "  主题设置", FontSize = 12, Foreground = FgDim, VerticalAlignment = VerticalAlignment.Center };
            titleTxt.Tag = "titlebartext";
            var closeBtn = new Button
            {
                Content = new TextBlock { Text = "\uE8BB", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, Foreground = Fg },
                Width = 46,
                Height = 34,
                Background = Brushes.Transparent,
                Foreground = Fg,
                Cursor = System.Windows.Input.Cursors.Arrow,
            };
            closeBtn.Template = (ControlTemplate)XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
                "  <Border x:Name='Bd' Background='{TemplateBinding Background}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
                "  <ControlTemplate.Triggers>" +
                "    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Background' Value='#33C94040'/></Trigger>" +
                "  </ControlTemplate.Triggers>" +
                "</ControlTemplate>");
            closeBtn.Click += (s, e) => { _win?.Close(); _win = null; };
            // Inside a WindowChrome caption band, controls are draggable chrome
            // by default and would swallow clicks - opt this one out.
            WindowChrome.SetIsHitTestVisibleInChrome(closeBtn, true);
            DockPanel.SetDock(closeBtn, Dock.Right);
            titleBar.Children.Add(closeBtn);
            titleBar.Children.Add(titleTxt);

            var root = new DockPanel();
            DockPanel.SetDock(titleBar, Dock.Top);
            root.Children.Add(titleBar);
            root.Children.Add(content);

            var outer = new Border
            {
                // No CornerRadius here: the window background is opaque and the
                // Border sits flush inside it, so rounding this Border would just
                // expose the window fill at the corners. DWM rounds the actual
                // frame (DwmSetWindowAttribute, see below) and clips the content.
                Background = CardBg,
                BorderBrush = CardEdge,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = root,
                ClipToBounds = true,
            };

            var win = new Window
            {
                Width = 880,
                Height = 580,
                MinWidth = 700,
                MinHeight = 460,
                // System-native frame instead of a hand-drawn one: DWM keeps
                // owning the rounded corners, drop shadow, snap layouts,
                // double-click maximize and the Alt+Space system menu, which a
                // WindowStyle=None + AllowsTransparency window gives up.
                WindowStyle = WindowStyle.SingleBorderWindow,
                AllowsTransparency = false,
                // Must stay OPAQUE: with AllowsTransparency=false WPF cannot
                // render a transparent window background (the window fails to
                // show at all). DWM rounds the frame and then clips whatever we
                // paint, so the opaque fill below is safe. See 9.14.
                Background = CardBg,
                ShowInTaskbar = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = outer,
                Foreground = Fg,
                Title = "V6 主题设置",
                // Self-mark: SmoothScroll walks up to the owning Window and
                // refuses to take over its ScrollViewers (the preview host).
                // Our own UI keeps stock scrolling.
                Tag = SmoothScroll.SelfMark,
            };

            WindowChrome.SetWindowChrome(win, new WindowChrome
            {
                CaptionHeight = 34,
                ResizeBorderThickness = new Thickness(6),
                // 0 = do not draw any WPF-side corner rounding, leave the frame
                // shape to DWM (which we request explicitly below).
                CornerRadius = new CornerRadius(0),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false,
            });

            // WindowChrome takes over the non-client area, which also switches
            // OFF Win11's automatic DWM rounding - so we ask DWM for rounded
            // corners explicitly once the HWND exists. See 9.14.
            win.SourceInitialized += (s, e) =>
            {
                try { DwmRoundCorners(new System.Windows.Interop.WindowInteropHelper(win).Handle); }
                catch (Exception ex) { Log.Write("dwm round corners fail: " + ex.Message); }
            };

            win.Closed += (s, e) => _win = null;

            RefreshAll();
            return win;
        }

        // ---- Win11 rounded corners via DWM ----
        // DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2.
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private static void DwmRoundCorners(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            int pref = 2; // DWMWCP_ROUND
            // Best effort: on Windows 10 this returns an error and the window
            // simply keeps square corners.
            DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
        }

        private static void TryApplyHex()
        {
            var t = _hexBox.Text.Trim().ToUpperInvariant();
            if (t.Length == 7 && t[0] == '#' &&
                Uri.IsHexDigit(t[1]) && Uri.IsHexDigit(t[2]) && Uri.IsHexDigit(t[3]) && Uri.IsHexDigit(t[4]) &&
                Uri.IsHexDigit(t[5]) && Uri.IsHexDigit(t[6]))
            {
                _token = t;
                RefreshAll();
            }
        }

        // Write v6.cfg right away when a toggle flips.
        //
        // Why this exists: the two switches are advertised as taking effect
        // immediately, so a user who flips one and closes the window reasonably
        // expects it to stick. It used to require also pressing 保存, which was
        // only mentioned in the log line ("save to persist") and nowhere in the
        // UI - the setting silently reverted on restart.
        //
        // Deliberately writes ONLY v6.cfg. The accent token lives in v6.theme and
        // still needs an explicit 保存, because switching colour is a bigger,
        // visible change that the user should confirm.
        // Merge-write v6.cfg: update the keys this window owns, leave every other
        // key exactly as it was.
        //
        // This file is shared. ProjectStats owns "projects", ThemeMenu owns
        // "showproject", and this window owns sat/bg/smooth/smoothplay/fps.
        // Previously each writer emitted the whole file from its own five
        // values, so saving a colour silently erased "projects" and
        // "showproject" - observed as the calendar going blank and the project
        // stopwatch disappearing right after changing the accent colour.
        private static void WriteCfgKeys(Dictionary<string, string> owned)
        {
            var path = Path.Combine(_dir, "v6.cfg");
            var lines = new List<string>();
            try
            {
                if (File.Exists(path)) lines.AddRange(File.ReadAllLines(path));
            }
            catch (Exception ex)
            {
                Log.Write("read cfg fail: " + ex.Message);
            }

            foreach (var kv in owned)
            {
                bool replaced = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    var t = lines[i].Trim();
                    var eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    if (t.Substring(0, eq).Trim() != kv.Key) continue;
                    lines[i] = kv.Key + "=" + kv.Value;
                    replaced = true;
                    break;
                }
                if (!replaced) lines.Add(kv.Key + "=" + kv.Value);
            }

            File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        }

        private static Dictionary<string, string> OwnedCfgKeys() => new Dictionary<string, string>
        {
            ["sat"] = _sat.ToString("F2", CultureInfo.InvariantCulture),
            ["bg"] = _bg.ToString("F2", CultureInfo.InvariantCulture),
            ["smooth"] = _smooth ? "1" : "0",
            ["smoothplay"] = _play ? "1" : "0",
            ["fps"] = _fps.ToString("F0", CultureInfo.InvariantCulture),
        };

        private static void PersistToggles()
        {
            try
            {
                WriteCfgKeys(OwnedCfgKeys());
            }
            catch (Exception ex)
            {
                Log.Write("persist toggles fail: " + ex.Message);
            }
        }

        private static void Save()
        {
            try
            {
                File.WriteAllText(Path.Combine(_dir, "v6.theme"), _token);
                Bootstrap.ActiveToken = _token;   // keep menu dot in sync now
                OnThemeChanged?.Invoke(_token);
                WriteCfgKeys(OwnedCfgKeys());
                Bootstrap.Smooth = _smooth;
                Bootstrap.SmoothPlay = _play;
                Bootstrap.MaxFps = _fps;

                // Hot-switch: apply the new token/strength to the live process
                // (re-tints every already-theme-colored surface in memory).
                Bootstrap.ApplyToken(_token, _sat, _bg);
                ColorPatch.ReapplyTint();
                Log.Write("theme saved: token=" + _token + " sat=" + _sat.ToString("F2") + " bg=" + _bg.ToString("F2")
                    + " smooth=" + (_smooth ? "1" : "0") + " smoothplay=" + (_play ? "1" : "0"));
                MessageBox.Show("已保存并即时生效。\n（个别非常规界面元素若未随新主题更新，重启后即完全一致。）",
                    "V6 主题", MessageBoxButton.OK, MessageBoxImage.Information);
                var w = _win; _win = null;
                try { w?.Close(); } catch { }
            }
            catch (Exception ex)
            {
                Log.Write("save fail: " + ex.Message);
                MessageBox.Show("保存失败：" + ex.Message, "V6 主题");
            }
        }

        private static SolidColorBrush KeyBrush(string key) => B(MockMap[key]);

        // ---------------- preview mock ----------------

        private static FrameworkElement BuildPreview()
        {
            var col = new StackPanel();
            col.Children.Add(T("实时预览（示意）", 14));

            var editor = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = CardEdge,
                ClipToBounds = true,
            };
            var ed = new StackPanel();
            editor.Child = ed;

            // title strip
            ed.Children.Add(Keyed("titlebar", new Border
            {
                Height = 20,
                Child = new TextBlock { Text = "  VOCALOID6", FontSize = 10, Foreground = B("#FFCCCCCC"), VerticalAlignment = VerticalAlignment.Center, Tag = "titlebar-text" },
            }));

            // menu strip
            var menuStrip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 3, 8, 3) };
            foreach (var w in new[] { "文件", "编辑", "任务", "播放控制", "视图", "设置", "帮助", "主题" })
            {
                menuStrip.Children.Add(new TextBlock { Text = w + "  ", FontSize = 11, Foreground = B("#FFE4E4E4") });
            }
            ed.Children.Add(Keyed("menubar", new Border { Child = menuStrip }));

            // toolbar
            var toolRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 5, 8, 5) };
            toolRow.Children.Add(Keyed("accent", new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(3), Background = KeyBrush("accent"), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center }));
            toolRow.Children.Add(Keyed("accent", new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(3), Background = KeyBrush("accent"), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center }));
            toolRow.Children.Add(Keyed("accent2", new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(3), Background = KeyBrush("accent2"), VerticalAlignment = VerticalAlignment.Center }));
            toolRow.Children.Add(new TextBlock { Text = "   120.00 BPM   4/4", FontSize = 11, Foreground = B("#FF007ACC"), VerticalAlignment = VerticalAlignment.Center, Tag = "accent" });
            ed.Children.Add(Keyed("toolbar", new Border { Child = toolRow }));

            // tracks
            var body = new Grid { Height = 230 };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var headers = new StackPanel();
            for (int i = 0; i < 3; i++)
            {
                var hrow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 8, 6, 0) };
                string swKey = i == 2 ? "notered" : "accent";
                hrow.Children.Add(Keyed(swKey, new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = KeyBrush(swKey), VerticalAlignment = VerticalAlignment.Center }));
                hrow.Children.Add(new TextBlock { Text = "  轨道" + (i + 1), FontSize = 10, Foreground = B("#FFCCCCCC"), VerticalAlignment = VerticalAlignment.Center });
                headers.Children.Add(new Border { Height = 44, Child = hrow });
            }
            var hdrBorder = Keyed("headercol", new Border { Child = headers });
            Grid.SetColumn(hdrBorder, 0);
            body.Children.Add(hdrBorder);

            var canvas = new Grid();
            // gridlines
            for (int i = 1; i < 9; i++)
                canvas.Children.Add(Keyed("gridline", new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(i * 34, 0, 0, 0), Background = KeyBrush("gridline") }));
            for (int i = 1; i < 6; i++)
                canvas.Children.Add(Keyed("gridline", new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, i * 38, 0, 0), Background = KeyBrush("gridline") }));
            // notes
            canvas.Children.Add(Keyed("accent", new Border { Width = 60, Height = 18, CornerRadius = new CornerRadius(3), Background = KeyBrush("accent"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(34, 38, 0, 0) }));
            canvas.Children.Add(Keyed("accent2", new Border { Width = 44, Height = 18, CornerRadius = new CornerRadius(3), Background = KeyBrush("accent2"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(120, 114, 0, 0) }));
            canvas.Children.Add(Keyed("notered", new Border { Width = 52, Height = 18, CornerRadius = new CornerRadius(3), Background = KeyBrush("notered"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(190, 76, 0, 0) }));
            // playhead
            canvas.Children.Add(Keyed("accent", new Border { Width = 2, Background = KeyBrush("accent"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(96, 0, 0, 0) }));

            var canvasBg = Keyed("canvas", new Border { Background = KeyBrush("canvas"), Child = canvas });
            Grid.SetColumn(canvasBg, 1);
            body.Children.Add(canvasBg);

            ed.Children.Add(body);
            col.Children.Add(editor);

            var cap = T("色块/按钮/播放头/选中框 = 强调色；深色面板 = 面板染色；红色为告警语义色，始终保护不染色。", 11, true);
            cap.TextWrapping = TextWrapping.Wrap;
            cap.Margin = new Thickness(0, 8, 0, 0);
            col.Children.Add(cap);

            return col;
        }

        // ---------------- tint math (mirrors ColorPatch) ----------------

        private static uint Tint(uint argb)
        {
            if (_token == "off") return argb;
            if (!Bootstrap.TryAccentHue(_token, out var hue)) return argb;

            byte a = (byte)(argb >> 24), r = (byte)(argb >> 16), g = (byte)(argb >> 8), b = (byte)argb;
            ColorPatch.RgbToHsl(r, g, b, out var h, out var s, out var l);

            if (s > 0.12)
            {
                bool srcRed = h < 0.055 || h > 0.945;
                bool accentRed = hue < 0.055 || hue > 0.945;
                if (srcRed && !accentRed) return argb;
                var ns = Math.Min(1.0, Math.Max(0.30, s * _sat));
                var c = ColorPatch.HslToRgb(hue, ns, l);
                return ((uint)a << 24) | ((uint)c.Item1 << 16) | ((uint)c.Item2 << 8) | c.Item3;
            }
            if (_bg > 0 && l < 0.34)
            {
                var ns = Math.Min(0.5, _bg * 0.5);
                var c = ColorPatch.HslToRgb(hue, ns, l);
                return ((uint)a << 24) | ((uint)c.Item1 << 16) | ((uint)c.Item2 << 8) | c.Item3;
            }
            return argb;
        }

        // ---------------- refresh ----------------

        private static void RefreshAll()
        {
            foreach (var kv in _swatchRings)
                kv.Value.BorderBrush = string.Equals(kv.Key, _token, StringComparison.OrdinalIgnoreCase)
                    ? B("#FFF0F0F0") : Brushes.Transparent;
            _toggleScroll?.Render();
            _togglePlay?.Render();
            RefreshFpsButtons();
            RefreshPreview();
        }

        private static void RefreshFpsButtons()
        {
            foreach (var b in _fpsButtons)
            {
                var hz = (double)b.Tag;
                bool on = Math.Abs(hz - _fps) < 0.001;
                b.Background = on ? B("#26FFFFFF") : B("#0FFFFFFF");
                b.BorderBrush = on ? B("#AAFFFFFF") : B("#33FFFFFF");
            }
        }

        // Current monitor refresh rate in Hz (0 when unavailable). Used only to
        // label the "auto" button - the glide is frame-driven either way.
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        private struct DEVMODE
        {
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight;
            public int dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType;
            public int dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
        private const int ENUM_CURRENT_SETTINGS = -1;

        private static double DetectRefreshHz()
        {
            try
            {
                var dm = new DEVMODE();
                dm.dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf(typeof(DEVMODE));
                if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm) && dm.dmDisplayFrequency > 24)
                    return dm.dmDisplayFrequency;
            }
            catch { }
            return 0;
        }

        private static void RefreshPreview()
        {
            if (_previewRoot == null) return;
            Walk(_previewRoot);
        }

        private static void Walk(DependencyObject node)
        {
            if (node is Border bd && bd.Tag is string key && MockMap.ContainsKey(key))
                bd.Background = BrushFromArgb(Tint(Convert.ToUInt32(MockMap[key].TrimStart('#'), 16)));
            if (node is TextBlock tb && tb.Tag is string tkey && MockMap.ContainsKey(tkey))
                tb.Foreground = BrushFromArgb(Tint(Convert.ToUInt32(MockMap[tkey].TrimStart('#'), 16)));

            int n = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < n; i++)
                Walk(VisualTreeHelper.GetChild(node, i));
        }
    }
}
