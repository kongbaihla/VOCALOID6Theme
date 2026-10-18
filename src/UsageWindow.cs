// UsageWindow - the activity calendar behind the menu bar's "累计使用" item.
//
// Visual language follows ThemeWindow (native WindowChrome frame, DWM rounded
// corners, same dark palette), and the calendar itself is a port of the
// heatmap from kongbaihla/FileHub: 26 week columns, 7 day rows, five intensity
// levels derived from the accent, month ticks positioned from measured column
// geometry, and a hover tooltip.
//
// WHAT IT COUNTS: launches per day, not minutes. These are different questions
// and only one of them has data behind it - see UsageStats.Launches for why.
//
// Everything here is defensive: a failure to build the window logs and does
// nothing else, exactly like the theme window.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace V6RamTheme
{
    internal static class UsageWindow
    {
        private static Window _win;

        // 30 weeks: the project files span 2026-03-23 .. today, which is just
        // over half a year. 26 weeks would clip the earliest projects off the
        // left edge and silently hide them.
        private const int Weeks = 30;

        private static readonly Brush Bg = B("#F2202020");
        private static readonly Brush Fg = B("#F0F0F0F0");
        private static readonly Brush FgDim = B("#9AF0F0F0");
        private static readonly Brush CardEdge = B("#1FFFFFFF");
        private static readonly Brush EmptyCell = B("#F02A2A2A");
        private static readonly Brush EmptyEdge = B("#22FFFFFF");
        private static readonly Brush GapBrush = B("#F0303030"); // "nothing on this day"

        // Arithmetic hex parse, NOT BrushConverter: this hook patches
        // MS.Internal.Parsers, which BrushConverter routes through, so a parsed
        // literal would come back tinted to the live accent. See 9.10.
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

        // ---- accent ramp (FileHub's level colours, ported) ----------------
        //
        // FileHub derives lv1..lv4 by mixing the accent into the page ground at
        // 30/52/76/100%. Same idea here, in WPF terms: LinearInterpolate between
        // the panel colour and the accent. Deriving it instead of hard-coding
        // four greens means the calendar follows the user's theme colour.
        private static Color Mix(Color from, Color to, double t)
        {
            if (t < 0) t = 0; else if (t > 1) t = 1;
            return Color.FromRgb(
                (byte)(from.R + (to.R - from.R) * t),
                (byte)(from.G + (to.G - from.G) * t),
                (byte)(from.B + (to.B - from.B) * t));
        }

        private static Color LevelColor(int level, Color accent)
        {
            var ground = Color.FromRgb(0x20, 0x20, 0x20);
            switch (level)
            {
                case 1: return Mix(ground, accent, 0.30);
                case 2: return Mix(ground, accent, 0.52);
                case 3: return Mix(ground, accent, 0.76);
                case 4: return accent;
                default: return Color.FromRgb(0x2A, 0x2A, 0x2A);
            }
        }

        private static Color Accent()
        {
            var c = Bootstrap.ResolveAccent(Bootstrap.ActiveToken);
            return c ?? Color.FromRgb(0x3B, 0x82, 0xF6);
        }

        // ---- window -------------------------------------------------------

        public static void ShowOrFocus()
        {
            // Rescan before building so the numbers reflect the folder as it is
            // now. Measured at 51 ms for 49 folders / 45 .vpr files, so paying
            // it on every open is cheaper than showing a stale count.
            //
            // ProjectStats caches its scan for the whole process (it used to be
            // scanned exactly once at startup), which meant a project folder
            // created later never appeared until VOCALOID6 was restarted.
            try { ProjectStats.Refresh(); }
            catch (Exception ex) { Log.Write("usage rescan fail: " + ex.Message); }

            if (_win != null)
            {
                // Already open: the window's cells were built from the previous
                // scan, so rebuild it rather than showing numbers that disagree
                // with the folder.
                var old = _win;
                _win = null;
                try { old.Close(); } catch { }
            }
            try
            {
                _win = Build();
                _win.Show();
                Log.Write("usage window opened");
            }
            catch (Exception ex)
            {
                _win = null;
                Log.Write("usage window build fail: " + ex.Message);
            }
        }

        private static Window Build()
        {
            var accent = Accent();

            var root = new DockPanel { LastChildFill = true };

            // title bar (same 34px caption band as ThemeWindow)
            var titleBar = new DockPanel { Height = 34, Background = B("#F21B1B1B") };
            var title = new TextBlock
            {
                Text = "  使用记录",
                FontSize = 12,
                Foreground = FgDim,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var closeBtn = new Button
            {
                Content = "\uE711",                 // Segoe Fluent Icons: Cancel
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 10,
                Width = 46,
                Background = Brushes.Transparent,
                Foreground = FgDim,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Template = RoundButtonTemplate(),
            };
            closeBtn.Click += (s, e) => _win?.Close();
            // Without this the caption band swallows the click as a drag.
            WindowChrome.SetIsHitTestVisibleInChrome(closeBtn, true);
            DockPanel.SetDock(closeBtn, Dock.Right);
            titleBar.Children.Add(closeBtn);
            titleBar.Children.Add(title);
            DockPanel.SetDock(titleBar, Dock.Top);
            root.Children.Add(titleBar);

            // ---- body ----
            var body = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };

            // totals row
            var stats = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
            // Same combined figure the menu bar shows: this session plus the
            // project library's total, so the two surfaces never disagree.
            var combined = UsageStats.Total + TimeSpan.FromHours(ProjectStats.TotalEstHours);
            stats.Children.Add(StatCard("累计使用", UsageStats.Fmt(combined), accent));
            stats.Children.Add(StatCard("本次会话", UsageStats.Fmt(UsageStats.ThisSession), accent));
            stats.Children.Add(StatCard("工程文件",
                ProjectStats.FileCount.ToString(CultureInfo.InvariantCulture) + " 个", accent));
            // Real count, not an estimate: how many distinct days have at least
            // one project opened. The old card showed EstLaunches, which was
            // derived from folder span and size - a number that looked measured
            // but was invented.
            int activeDays;
            try { activeDays = ProjectStats.Launches.Count(); }
            catch { activeDays = 0; }
            stats.Children.Add(StatCard("活跃天数",
                activeDays.ToString(CultureInfo.InvariantCulture) + " 天", accent));

            // Total opens recorded by this program. Lives next to 活跃天数
            // because the two are read together ("N 天里开了 M 次"), and unlike
            // the old 累计启动 card this one is counted, not inferred.
            int totalOpens;
            try { totalOpens = ProjectTimer.TotalOpens(); }
            catch { totalOpens = 0; }
            stats.Children.Add(StatCard("总启动",
                totalOpens.ToString(CultureInfo.InvariantCulture) + " 次", accent));

            body.Children.Add(stats);

            // ---- current project: user-picked, REALLY timed ----
            //
            // Separate card from the estimate row on purpose: this number is a
            // stopwatch and the others are inferences. Mixing them in one row
            // would invite reading them as the same kind of quantity.
            body.Children.Add(BuildCurrentProjectCard(accent));

            // ---- heatmap card ----
            var card = new Border
            {
                Background = Bg,
                BorderBrush = CardEdge,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 14, 16, 14),
            };
            var cardStack = new StackPanel();

            // Title row: label on the left, folder picker on the right.
            //
            // A Grid, not a DockPanel: the card lives in a vertical StackPanel,
            // which measures children with infinite width, so a DockPanel's
            // Dock.Right child gets pushed past the visible edge and disappears.
            // Star + Auto columns make the split explicit.
            var titleRow = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var pickBtn = new Button
            {
                Content = "选择工程目录…",
                FontSize = 11,
                Padding = new Thickness(10, 4, 10, 4),
                Background = B("#22FFFFFF"),
                Foreground = Fg,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "选择存放 .vpr 工程的文件夹；热力图按其中的工程统计。",
            };
            pickBtn.Click += (s, e) => PickFolder();
            Grid.SetColumn(pickBtn, 1);
            titleRow.Children.Add(pickBtn);

            var titleText = new TextBlock
            {
                Text = "工程活动热力图",
                FontSize = 13,
                Foreground = Fg,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(titleText, 0);
            titleRow.Children.Add(titleText);
            cardStack.Children.Add(titleRow);
            cardStack.Children.Add(new TextBlock
            {
                Text = "每一格是一天，颜色越亮表示当天打开的工程越多。只统计 .vpr 的访问时间，与上方勾选无关。",
                FontSize = 11,
                Foreground = FgDim,
                Margin = new Thickness(0, 0, 0, 4),
            });
            // Current folder, ellipsised so a deep path cannot push the button
            // off the card.
            cardStack.Children.Add(new TextBlock
            {
                Text = "目录：" + ShortPath(ProjectStats.CurrentFolder),
                FontSize = 10,
                Foreground = FgDim,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 6),
            });

            // Which file types count as a project. .vpr is always on (it is the
            // editor's own format); the other two are shared formats that other
            // software also writes, so they are opt-in and rescan on toggle.
            var extRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            extRow.Children.Add(new TextBlock
            {
                Text = "文件计数：vpr 固定",
                FontSize = 10,
                Foreground = FgDim,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                ToolTip = "这一行只影响上方「工程文件」的数字，不影响下方热力图。",
            });
            Action<bool, bool> applyExt = (vsqx, midi) =>
            {
                ProjectStats.IncludeVsqx = vsqx;
                ProjectStats.IncludeMidi = midi;
                ProjectStats.SaveFileTypes();
                ProjectStats.Refresh();
                var old = _win;
                _win = null;
                try { old?.Close(); } catch { }
                ShowOrFocus();
            };
            var cbVsqx = new CheckBox
            {
                Content = "vsqx",
                IsChecked = ProjectStats.IncludeVsqx,
                FontSize = 10,
                Foreground = FgDim,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                Cursor = Cursors.Hand,
            };
            cbVsqx.Click += (s, e) =>
                applyExt(cbVsqx.IsChecked == true, ProjectStats.IncludeMidi);
            extRow.Children.Add(cbVsqx);

            var cbMidi = new CheckBox
            {
                Content = "midi（含 .mid / .midi）",
                IsChecked = ProjectStats.IncludeMidi,
                FontSize = 10,
                Foreground = FgDim,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = "MIDI 是通用格式，其他软件也会读写。素材包里的批量 MIDI 可能让某个工程显得异常活跃。",
            };
            cbMidi.Click += (s, e) =>
                applyExt(ProjectStats.IncludeVsqx, cbMidi.IsChecked == true);
            extRow.Children.Add(cbMidi);
            cardStack.Children.Add(extRow);

            var data = BuildDayStats();
            var today = DateTime.Today;

            // (the month strip is built inside the scaled block below, so it
            // cannot drift out of alignment with the grid)

            // the grid itself: one column per week, 7 cells per column, Monday top
            var grid = new StackPanel { Orientation = Orientation.Horizontal };

            var firstCol = FirstColumnStart(today);
            // Computed once: the ramp normalises against the busiest day, and
            // recomputing it inside the 182-cell loop would rescan the whole
            // dictionary per cell.
            int maxCount = Math.Max(1, data.Values.Select(v => v.Projects).DefaultIfEmpty(1).Max());
            for (int w = 0; w < Weeks; w++)
            {
                var col = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Margin = new Thickness(0, 0, w == Weeks - 1 ? 0 : ColumnGap, 0),
                };
                for (int d = 0; d < 7; d++)
                {
                    var day = firstCol.AddDays(w * 7 + d);
                    data.TryGetValue(day, out var stat);
                    col.Children.Add(MakeCell(day, stat, today, accent, maxCount));
                }
                grid.Children.Add(col);
            }

            // month ticks live in the SAME scaled block as the grid, so they
            // cannot drift out of alignment when the Viewbox shrinks everything.
            var tickCanvas = new Canvas
            {
                Height = 16,
                Width = Weeks * (Cell + ColumnGap) - ColumnGap,
            };
            int seenMonth0 = -1;
            for (int w = 0; w < Weeks; w++)
            {
                var colStart = firstCol.AddDays(w * 7);
                if (colStart.Month == seenMonth0) continue;
                seenMonth0 = colStart.Month;
                var tb = new TextBlock
                {
                    Text = colStart.ToString("M月", CultureInfo.InvariantCulture),
                    FontSize = 10,
                    Foreground = FgDim,
                };
                Canvas.SetLeft(tb, w * (Cell + ColumnGap));
                tickCanvas.Children.Add(tb);
            }

            var scaled = new StackPanel();
            scaled.Children.Add(tickCanvas);
            scaled.Children.Add(grid);

            // WHY A VIEWBOX: the grid is 26 fixed-size columns, and "how many
            // logical pixels the card really has" turned out to vary with DPI
            // scaling in ways hand-computed constants kept getting wrong - twice
            // the newest week (the only one with data) was clipped off the right
            // edge. Stretch=Uniform with StretchDirection=DownOnly means the grid
            // is drawn at its natural size when it fits and is scaled DOWN only
            // when it would overflow, so it can never be cut off on any display.
            cardStack.Children.Add(new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = scaled,
            });

            // legend (same ramp, small chips)
            var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            legend.Children.Add(new TextBlock { Text = "少", FontSize = 11, Foreground = FgDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            for (int lv = 0; lv <= 4; lv++)
                legend.Children.Add(new Border
                {
                    Width = 12, Height = 12,
                    CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(LevelColor(lv, accent)),
                    BorderBrush = lv == 0 ? EmptyEdge : Brushes.Transparent,
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 4, 0),
                });
            legend.Children.Add(new TextBlock { Text = "多", FontSize = 11, Foreground = FgDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) });
            cardStack.Children.Add(legend);

            card.Child = cardStack;
            body.Children.Add(card);

            var scroll = new ScrollViewer
            {
                Content = body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            root.Children.Add(scroll);

            var win = new Window
            {
                Width = 720,
                // Tall enough for every section; sized from the measured content
                // in the Loaded handler below rather than by guessing. The guess
                // was 480 and the content outgrew it as cards were added, which
                // is what produced the scrollbar.
                Height = 480,
                MinWidth = 560,
                MinHeight = 400,
                // Native frame, like ThemeWindow: DWM keeps the rounded corners,
                // shadow, snap layouts and Alt+Space menu.
                WindowStyle = WindowStyle.SingleBorderWindow,
                AllowsTransparency = false,
                // Must stay opaque (AllowsTransparency=false cannot render a
                // transparent background - the window would not appear).
                Background = Bg,
                Foreground = Fg,
                ShowInTaskbar = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Title = "V6 使用记录",
                Content = root,
                // Self-mark so SmoothScroll leaves our own scrolling alone.
                Tag = SmoothScroll.SelfMark,
            };
            WindowChrome.SetWindowChrome(win, new WindowChrome
            {
                CaptionHeight = 34,
                ResizeBorderThickness = new Thickness(6),
                CornerRadius = new CornerRadius(0),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false,
            });
            win.SourceInitialized += (s, e) =>
            {
                try { DwmRoundCorners(new System.Windows.Interop.WindowInteropHelper(win).Handle); }
                catch { }
            };

            // Size the window to its content once, then scroll only if the user
            // shrinks it.
            //
            // Why not just pick a bigger number: the content grew every time a
            // card was added (stats row, current-project card, folder picker),
            // and each guess eventually became the next scrollbar. Measuring the
            // real desired height ends that game.
            win.Loaded += (s, e) =>
            {
                try
                {
                    // Measure the body unconstrained: inside the ScrollViewer its
                    // DesiredSize is clamped to the viewport, so ask for infinity.
                    body.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    double content = body.DesiredSize.Height;

                    // Caption band + a little breathing room below the body.
                    double chrome = win is Window ? 34 : 0;
                    double target = content + chrome + 6;

                    // Never exceed the work area of the current monitor.
                    var wa = SystemParameters.WorkArea;
                    if (target > wa.Height - 40) target = wa.Height - 40;

                    win.Height = Math.Max(360, target);
                }
                catch (Exception ex)
                {
                    Log.Write("usage window autosize fail: " + ex.Message);
                }
            };

            win.Closed += (s, e) => _win = null;
            return win;
        }

        // ---- current-project card -----------------------------------------

        private static TextBlock _curTimeText;
        private static TextBlock _curStateText;
        private static ComboBox _curCombo;

        private static Border BuildCurrentProjectCard(Color accent)
        {
            var outer = new Border
            {
                Background = B("#F21B1B1B"),
                BorderBrush = CardEdge,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10, 14, 12),
                Margin = new Thickness(0, 0, 0, 14),
            };

            var rows = new StackPanel();

            // header: label + live clock
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var lbl = new TextBlock
            {
                Text = "当前工程",
                FontSize = 11,
                Foreground = FgDim,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(lbl, 0);
            head.Children.Add(lbl);

            _curTimeText = new TextBlock
            {
                Text = "0:00:00",
                FontSize = 18,
                Foreground = Fg,
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(_curTimeText, 1);
            head.Children.Add(_curTimeText);
            rows.Children.Add(head);

            // No picker: the current project is detected automatically from the
            // editor's title bar (see ProjectTimer.AutoDetect). This label just
            // reports what is being timed.
            _curStateText = new TextBlock
            {
                Text = "",
                FontSize = 10,
                Foreground = FgDim,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
            };
            rows.Children.Add(_curStateText);

            // Toggle: show the project stopwatch in the MENU BAR.
            // The cumulative figure is not affected - it is the item's original
            // purpose and stays visible either way.
            var barToggle = new CheckBox
            {
                Content = "在菜单栏显示工程时长",
                IsChecked = ThemeMenu.ShowProjectTimer,
                FontSize = 11,
                Foreground = FgDim,
                Margin = new Thickness(0, 10, 0, 0),
                Cursor = Cursors.Hand,
            };
            barToggle.Checked += (s, e) => ThemeMenu.ShowProjectTimer = true;
            barToggle.Unchecked += (s, e) => ThemeMenu.ShowProjectTimer = false;
            rows.Children.Add(barToggle);

            outer.Child = rows;

            // Tick the display ~1/s while the window is open; unhook on close so
            // a closed window does not keep waking the dispatcher.
            Action onChanged = () => RefreshCurrentProject();
            ProjectTimer.Changed += onChanged;
            outer.Unloaded += (s, e) => ProjectTimer.Changed -= onChanged;

            RefreshCurrentProject();
            return outer;
        }

        private static void RefreshCurrentProject()
        {
            try
            {
                if (_curTimeText == null) return;
                var sel = ProjectTimer.Selected;
                _curTimeText.Text = UsageStats.Fmt(ProjectTimer.SelectedElapsed);
                if (_curStateText == null) return;

                if (string.IsNullOrEmpty(sel))
                    _curStateText.Text = "未检测到打开的工程。在编辑器里打开一个 .vpr 后会自动开始计时。";
                else
                    _curStateText.Text = "正在计时「" + sel + "」 · 累计 " +
                        UsageStats.Fmt(ProjectTimer.Get(sel));
            }
            catch { }
        }

        // ---- folder picker -------------------------------------------------

        // Let the user point the calendar at wherever their .vpr files live.
        //
        // Before this the folder could only be changed by hand-editing v6.cfg,
        // which is unreasonable: the installer has no idea where someone keeps
        // their projects, and a wrong/absent path silently yields an empty
        // calendar with no hint about how to fix it.
        //
        // Uses OpenFolderDialog (net8.0-windows, no WinForms dependency). After a
        // successful pick the window is closed and reopened rather than patched
        // in place - rebuilding is one call and cannot leave half-updated state
        // behind, and the scan it triggers is what re-reads the new folder.
        private static void PickFolder()
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "选择 VOCALOID6 工程目录（.vpr 所在文件夹）",
                    Multiselect = false,
                };
                var cur = ProjectStats.CurrentFolder;
                if (!string.IsNullOrEmpty(cur) && System.IO.Directory.Exists(cur))
                    dlg.InitialDirectory = cur;

                if (dlg.ShowDialog() != true) return;

                var picked = dlg.FolderName;
                if (!ProjectStats.SetFolder(picked))
                {
                    MessageBox.Show("无法保存工程目录，请检查程序目录是否可写。",
                        "V6 使用记录", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                ProjectStats.Refresh();

                // Rescan done; reopen so every count on screen reflects it.
                var old = _win;
                _win = null;
                try { old?.Close(); } catch { }
                ShowOrFocus();
            }
            catch (Exception ex)
            {
                Log.Write("pick folder fail: " + ex.Message);
                MessageBox.Show("选择目录失败：" + ex.Message,
                    "V6 使用记录", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Tail of a path, so a deep folder does not eat the whole line.
        private static string ShortPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return "（未设置，点右侧按钮选择）";
            try
            {
                var name = System.IO.Path.GetFileName(p.TrimEnd('\\', '/'));
                var parent = System.IO.Path.GetFileName(
                    System.IO.Path.GetDirectoryName(p.TrimEnd('\\', '/')));
                if (string.IsNullOrEmpty(name)) return p;
                return string.IsNullOrEmpty(parent) ? name : parent + "\\" + name;
            }
            catch { return p; }
        }

        // ---- pieces -------------------------------------------------------

        // Cell geometry - the NATURAL size the grid is drawn at.
        //
        // The grid no longer has to be hand-fitted to the window: it sits in a
        // Viewbox (Stretch=Uniform, DownOnly), so if these sizes overflow the
        // card on some DPI the whole block scales down instead of losing its
        // rightmost column. That frees the numbers here to be chosen purely for
        // readability.
        //
        //   natural: 26*18 + 25*6 = 618 logical units
        //   the card is ~648 wide, so it fits on this display at full size and
        //   scales down gracefully on a narrower one.
        private const double Cell = 18;
        private const double CellGap = 4;     // vertical gap between the 7 rows
        private const double ColumnGap = 6;   // horizontal gap between the 26 weeks

        private static Border MakeCell(DateTime day, DayStat stat, DateTime today, Color accent, int maxCount)
        {
            int count = stat.Projects;
            int level = 0;
            if (count > 0)
            {
                // A single launch still has to read as a filled cell, so lv1 is
                // the floor for any activity; the rest scale off the busiest day.
                level = Math.Max(1, Math.Min(4, (int)Math.Ceiling(count * 4.0 / maxCount)));
            }

            // Tooltip states each number's coverage explicitly: Projects covers
            // the whole library, Opens only since this build started counting.
            string tip;
            if (day > today) tip = null;
            else if (count == 0 && stat.Opens == 0)
                tip = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "  没有打开工程";
            else
            {
                tip = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                      "  打开过 " + count + " 个工程";
                if (stat.Opens > 0)
                    tip += "\n本工具记录到 " + stat.Opens + " 次打开";
            }

            bool future = day > today;
            var cell = new Border
            {
                Width = Cell,
                Height = Cell,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 0, 0, CellGap),
                Background = future ? Brushes.Transparent : new SolidColorBrush(LevelColor(level, accent)),
                BorderBrush = (level == 0 && !future) ? EmptyEdge : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                ToolTip = tip,
            };
            return cell;
        }

        private static Border StatCard(string label, string value, Color accent)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = FgDim });
            sp.Children.Add(new TextBlock
            {
                Text = value,
                FontSize = 20,
                Foreground = Fg,
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                Margin = new Thickness(0, 2, 0, 0),
            });
            return new Border
            {
                Background = B("#F21B1B1B"),
                BorderBrush = CardEdge,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10, 22, 10),
                Margin = new Thickness(0, 0, 10, 0),
                Child = sp,
            };
        }

        private static ControlTemplate RoundButtonTemplate()
        {
            const string xaml =
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
                "<Border x:Name='bd' Background='{TemplateBinding Background}' CornerRadius='4'>" +
                "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>" +
                "</Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsMouseOver' Value='True'>" +
                "<Setter TargetName='bd' Property='Background' Value='#E8111111'/>" +
                "</Trigger>" +
                "</ControlTemplate.Triggers>" +
                "</ControlTemplate>";
            return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
        }

        // Monday-aligned start of the FIRST of the Weeks columns, chosen so that
        // today lands inside the LAST column.
        //
        // Getting this wrong is easy and silent: anchoring on
        // `today - (Weeks*7 - 1)` scrolls the window back one whole week too far
        // once today is not a Sunday, so today's cell - the only one with data -
        // falls into a 27th column that is never drawn, and the grid looks
        // completely empty.
        private static DateTime FirstColumnStart(DateTime today)
        {
            // Monday-based index of today: 0 = Monday .. 6 = Sunday.
            int dow = ((int)today.DayOfWeek + 6) % 7;
            var thisMonday = today.AddDays(-dow);
            return thisMonday.AddDays(-(Weeks - 1) * 7);
        }

        // Per-day activity from the user's .vpr project files (see ProjectStats
        // for why launches/hours are estimates but the DATES are real).
        private static Dictionary<DateTime, int> BuildDayData()
        {
            var d = new Dictionary<DateTime, int>();
            try
            {
                foreach (var kv in ProjectStats.Launches)
                {
                    if (d.TryGetValue(kv.Key.Date, out int n)) d[kv.Key.Date] = n + kv.Value;
                    else d[kv.Key.Date] = kv.Value;
                }
            }
            catch (Exception ex)
            {
                Log.Write("usage window data fail: " + ex.Message);
            }
            return d;
        }

        /// <summary>
        /// One heatmap day: two numbers with different provenance.
        ///
        /// Projects - REAL for the whole library: count of distinct .vpr files
        ///            whose access time falls on that day.
        /// Opens    - REAL but only since this program started counting; .vpr
        ///            keeps a single access stamp, so earlier opens are simply
        ///            not recoverable. Callers must label it as such.
        ///
        /// The cell colour follows Projects, because that is the only figure
        /// that covers the whole history; Opens is shown in the tooltip.
        /// </summary>
        private struct DayStat
        {
            public int Projects;
            public int Opens;
        }

        private static Dictionary<DateTime, DayStat> BuildDayStats()
        {
            var d = new Dictionary<DateTime, DayStat>();
            try
            {
                foreach (var kv in ProjectStats.Launches)
                {
                    var day = kv.Key.Date;
                    d.TryGetValue(day, out var s);
                    s.Projects += kv.Value;
                    d[day] = s;
                }
                foreach (var kv in ProjectTimer.OpensByDay())
                {
                    var day = kv.Key.Date;
                    d.TryGetValue(day, out var s);
                    s.Opens += kv.Value;
                    d[day] = s;
                }
            }
            catch (Exception ex)
            {
                Log.Write("usage window daystats fail: " + ex.Message);
            }
            return d;
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private static void DwmRoundCorners(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            int pref = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
        }
    }
}
