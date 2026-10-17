// Offscreen geometry lab for the dropdown card's rounded corners.
//
// Purpose: find a structure where ALL FOUR corners are rounded and the item
// content can never square one off. Rendered offscreen (no live app, no popup
// timing races) and measured pixel-by-pixel, so the answer is data, not eyes.
//
// Variants are rendered side by side; the measurement pass reports, for each
// card, the inset of the painted area on every row near each corner. A real
// rounded corner shows a monotonically decreasing inset over ~radius rows.

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Path = System.Windows.Shapes.Path;

public static class Lab
{
    private const int CW = 200;      // card width
    private const int CH = 150;      // card height
    private const int Radius = 12;   // requested corner radius
    private const int Pad = 3;       // inset between card edge and content
    private const int Gap = 24;      // spacing between variant cards

    private static readonly Color CardFill = Color.FromRgb(0x27, 0x27, 0x27);
    private static readonly Color CardEdge = Color.FromRgb(0x88, 0x88, 0x88);
    private static readonly Color ContentFill = Color.FromRgb(0x00, 0xC0, 0x00);
    private static readonly Color Backdrop = Color.FromRgb(0xFF, 0x00, 0xFF);

    [STAThread]
    public static void Main()
    {
        // Reproduce the REAL structure exactly: an ItemsPresenter inside the
        // ScrollViewer (which is what the actual ControlTemplate hosts), driven
        // by real MenuItems. Earlier lab versions faked the rows directly inside
        // a StackPanel and could not reproduce the shipped defect.
        var variants = new List<(string name, Func<FrameworkElement> build)>
        {
            ("real-items",   () => RealItemsPresenter(3)),   // current shipped inset
            ("clip-card",    () => ClipCardGrid()),          // NEW: clip outer Grid
            ("inset-6",      () => RealItemsPresenter(6)),
            ("inset-9",      () => RealItemsPresenter(9)),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Background = new SolidColorBrush(Backdrop) };
        foreach (var v in variants) row.Children.Add(v.build());

        var root = new Border { Child = row, Padding = new Thickness(12), Background = new SolidColorBrush(Backdrop) };
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        root.Arrange(new Rect(0, 0, root.DesiredSize.Width, root.DesiredSize.Height));
        root.UpdateLayout();

        int pw = (int)Math.Ceiling(root.ActualWidth);
        int ph = (int)Math.Ceiling(root.ActualHeight);
        var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        var outPath = System.IO.Path.Combine(AppContext.BaseDirectory, "corners.png");
        using (var fs = File.Create(outPath)) enc.Save(fs);

        Console.WriteLine("image " + pw + "x" + ph + " -> " + outPath);

        // ---- measurement ----
        int stride = pw * 4;
        var px = new byte[ph * stride];
        rtb.CopyPixels(px, stride, 0);

        // Measure the CARD SILHOUETTE (anything that is not the magenta
        // backdrop). This answers "where does the card end", which is what the
        // corner shape is about. Note the Path fill itself also counts - so a
        // profile that stays at 0 means the Path paints to the very corner,
        // which is exactly the shipped defect.
        bool IsContent(int x, int y)
        {
            if (x < 0 || y < 0 || x >= pw || y >= ph) return false;
            int i = y * stride + x * 4;
            byte b = px[i], g = px[i + 1], r = px[i + 2];
            bool isBackdrop = r > 200 && g < 90 && b > 200;
            return !isBackdrop;
        }

        // Locate each card by SCANNING for the card fill colour, instead of
        // computing an expected origin. The previous version assumed a fixed
        // pitch (CW+Gap), which does not hold once the cards are wrapped in a
        // StackPanel with margins - every corner probe was then taken at the
        // wrong coordinates and reported nonsense.
        var cards = FindCards(px, stride, pw, ph, variants.Count);
        for (int k = 0; k < variants.Count; k++)
        {
            Console.WriteLine();
            Console.WriteLine("=== " + variants[k].name + " ===");
            if (k >= cards.Count) { Console.WriteLine("  card not located"); continue; }
            var c = cards[k];
            Console.WriteLine("  located card body: x " + c.L + ".." + c.R + "   y " + c.T + ".." + c.B);
            Report(IsContent, c, pw, ph);
        }
    }

    // Row-background colour test, kept for reference: item rows use a lighter
    // fill than the card's own Path.
    private static bool IsRowBg(byte[] px, int stride, int x, int y, int pw, int ph)
    {
        if (x < 0 || y < 0 || x >= pw || y >= ph) return false;
        int i = y * stride + x * 4;
        byte b = px[i], g = px[i + 1], r = px[i + 2];
        return r > 0x38 && r < 0x60 && g > 0x3A && g < 0x64 && b > 0x40 && b < 0x6E;
    }

    private struct Box { public int L, T, R, B; }

    // Finds each card's outer bounds by scanning for non-backdrop pixels.
    private static List<Box> FindCards(byte[] px, int stride, int pw, int ph, int expect)
    {
        var boxes = new List<Box>();
        int midY = ph / 2;

        bool NotBackdrop(int x, int y)
        {
            int i = y * stride + x * 4;
            byte b = px[i], g = px[i + 1], r = px[i + 2];
            return !(r > 200 && g < 90 && b > 200);
        }

        // Horizontal runs of painted pixels on the middle row = the cards.
        int x0 = -1;
        for (int x = 0; x <= pw; x++)
        {
            bool inNow = x < pw && NotBackdrop(x, midY);
            if (inNow && x0 < 0) x0 = x;
            if (!inNow && x0 >= 0)
            {
                if (x - x0 > CW / 3) boxes.Add(new Box { L = x0, R = x - 1 });
                x0 = -1;
            }
        }

        // Vertical extent at each card's horizontal centre. Rows are only
        // counted while they stay contiguous: the caption label sits BELOW the
        // card with a gap, so stop at the first blank row after the card starts.
        for (int i = 0; i < boxes.Count; i++)
        {
            var bx = boxes[i];
            int cx = (bx.L + bx.R) / 2, t = -1, b2 = -1;
            for (int y = 0; y < ph; y++)
            {
                if (NotBackdrop(cx, y))
                {
                    if (t < 0) t = y;
                    b2 = y;
                }
                else if (t >= 0)
                {
                    break;   // gap found -> card ended (caption is further down)
                }
            }
            bx.T = t; bx.B = b2;
            boxes[i] = bx;
        }
        return boxes;
    }

    private static void Report(Func<int, int, bool> isContent, Box card, int pw, int ph)
    {
        int cardL = card.L, cardR = card.R, cardT = card.T, cardB = card.B;

        Console.WriteLine("  size " + (cardR - cardL + 1) + "x" + (cardB - cardT + 1));
        Console.WriteLine("  TL: " + Corner(isContent, cardL, cardT, +1, +1));
        Console.WriteLine("  TR: " + Corner(isContent, cardR, cardT, -1, +1));
        Console.WriteLine("  BL: " + Corner(isContent, cardL, cardB, +1, -1));
        Console.WriteLine("  BR: " + Corner(isContent, cardR, cardB, -1, -1));

        Console.WriteLine("  --- raw maps (C=content), 16x10 per corner ---");
        Dump(isContent, "TL", cardL, cardT, +1, +1);
        Dump(isContent, "TR", cardR, cardT, -1, +1);
        Dump(isContent, "BL", cardL, cardB, +1, -1);
        Dump(isContent, "BR", cardR, cardB, -1, -1);
    }

    private static void Dump(Func<int, int, bool> isContent, string tag, int cx, int cy, int sx, int sy)
    {
        Console.WriteLine("  " + tag + ":");
        for (int dy = 0; dy < 10; dy++)
        {
            var row = "    ";
            for (int dx = 0; dx < 16; dx++)
                row += isContent(cx + sx * dx, cy + sy * dy) ? 'C' : '.';
            Console.WriteLine(row);
        }
    }

    // From the card's corner, walk inward; on each row report how far in the
    // content starts. Rounded corner => descending sequence ending at the pad.
    private static string Corner(Func<int, int, bool> isContent, int cardEdgeX, int cardEdgeY, int sx, int sy)
    {
        var parts = new List<string>();
        for (int d = 0; d <= 10; d++)
        {
            int y = cardEdgeY + sy * d;
            int found = -1;
            for (int t = 0; t <= CW; t++)
            {
                int x = cardEdgeX + sx * t;
                if (isContent(x, y)) { found = t; break; }
            }
            parts.Add(found < 0 ? "x" : found.ToString());
        }
        return string.Join(",", parts);
    }

    // ---------- variant builders ----------

    private static FrameworkElement Frame(string label, UIElement inner)
    {
        // Card and caption must NOT share a Grid: a stretched caption sibling
        // overlaps the card's bounds and gets picked up by the pixel scan.
        var card = new Grid { Width = CW, Height = CH, Margin = new Thickness(Gap / 2, 0, Gap / 2, 0) };
        card.Children.Add(inner);

        var col = new StackPanel { Orientation = Orientation.Vertical };
        col.Children.Add(card);
        col.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 10,
            Foreground = Brushes.White,
            Margin = new Thickness(Gap / 2 + 4, 6, 0, 0),
        });
        return col;
    }

    private static UIElement ContentBlock()
    {
        return new Border
        {
            Background = new SolidColorBrush(ContentFill),
        };
    }

    // 1. Baseline: Border with CornerRadius only (the known-broken case).
    private static FrameworkElement BorderOnly()
    {
        var card = new Border
        {
            Background = new SolidColorBrush(CardFill),
            BorderBrush = new SolidColorBrush(CardEdge),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radius),
            Padding = new Thickness(Pad),
            Child = ContentBlock(),
        };
        return Frame("1 border only", card);
    }

    // 2. Path draws the shape; content gets a concentric OpacityMask.
    private static FrameworkElement PathPlusMask()
    {
        var g = new Grid();
        var shape = new Path { Fill = new SolidColorBrush(CardFill), Stroke = new SolidColorBrush(CardEdge), StrokeThickness = 1 };
        var content = ContentBlock();
        var host = new Border { Margin = new Thickness(Pad), Child = content };

        g.SizeChanged += (s, e) =>
        {
            double w = g.ActualWidth, h = g.ActualHeight;
            if (w < 2 || h < 2) return;
            shape.Data = new RectangleGeometry(new Rect(0.5, 0.5, w - 1, h - 1), Radius, Radius);

            double iw = host.ActualWidth, ih = host.ActualHeight;
            if (iw < 2 || ih < 2) return;
            double r = Math.Max(0, Radius - Pad);
            // Relative viewbox/viewport so the drawing is mapped onto the
            // element's own box rather than anchored at its natural size.
            var b = new DrawingBrush
            {
                Stretch = Stretch.Fill,
                TileMode = TileMode.None,
                Viewbox = new Rect(0, 0, 1, 1),
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                Viewport = new Rect(0, 0, 1, 1),
                ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                Drawing = new GeometryDrawing(Brushes.White, null,
                    new RectangleGeometry(new Rect(0, 0, iw, ih), r, r)),
            };
            host.OpacityMask = b;
        };

        g.Children.Add(shape);
        g.Children.Add(host);

        // Force layout so the SizeChanged pass runs before rendering.
        g.Measure(new Size(CW, CH));
        g.Arrange(new Rect(0, 0, CW, CH));
        g.UpdateLayout();
        return Frame("2 path+mask", g);
    }

    // 3. Path draws the shape; content is clipped by the same rounded geometry
    //    (Clip on the CONTENT, not the card - so it cannot fight the shape).
    private static FrameworkElement PathPlusClip()
    {
        var g = new Grid();
        var shape = new Path { Fill = new SolidColorBrush(CardFill), Stroke = new SolidColorBrush(CardEdge), StrokeThickness = 1 };
        var content = ContentBlock();
        var host = new Border { Margin = new Thickness(Pad), Child = content };

        g.SizeChanged += (s, e) =>
        {
            double w = g.ActualWidth, h = g.ActualHeight;
            if (w < 2 || h < 2) return;
            shape.Data = new RectangleGeometry(new Rect(0.5, 0.5, w - 1, h - 1), Radius, Radius);

            double iw = host.ActualWidth, ih = host.ActualHeight;
            if (iw < 2 || ih < 2) return;
            double r = Math.Max(0, Radius - Pad);
            host.Clip = new RectangleGeometry(new Rect(0, 0, iw, ih), r, r);
        };

        g.Children.Add(shape);
        g.Children.Add(host);

        g.Measure(new Size(CW, CH));
        g.Arrange(new Rect(0, 0, CW, CH));
        g.UpdateLayout();
        return Frame("3 path+clip", g);
    }

    // Content that behaves like real menu items: a StackPanel of rows, each with
    // its OWN opaque background. This is what overpaints a card corner in the
    // real menu, so the lab must reproduce it.
    private static StackPanel MenuItems()
    {
        var sp = new StackPanel();
        for (int i = 0; i < 6; i++)
        {
            sp.Children.Add(new Border
            {
                Height = 22,
                Background = new SolidColorBrush(ContentFill),
                Margin = new Thickness(3, 1, 3, 1),
                Child = new TextBlock { Text = "item " + i, FontSize = 10, Foreground = Brushes.Black },
            });
        }
        return sp;
    }

    // Items with NO background of their own - only the hover highlight paints,
    // and it is itself rounded. This is how the real template behaves for the
    // resting state: Bd has Background='Transparent' and only turns opaque on
    // hover.
    private static StackPanel MenuItemsTransparent()
    {
        var sp = new StackPanel();
        for (int i = 0; i < 6; i++)
        {
            sp.Children.Add(new Border
            {
                Height = 22,
                Background = Brushes.Transparent,
                Margin = new Thickness(3, 1, 3, 1),
                Child = new TextBlock { Text = "item " + i, FontSize = 10, Foreground = Brushes.Black },
            });
        }
        return sp;
    }

    // 4. EXACTLY the structure the real ThemeMenu uses today: a ScrollViewer
    //    inside the Grid, with the OpacityMask applied TO THE SCROLLVIEWER.
    private static FrameworkElement ScrollMask()
    {
        var g = new Grid();
        var shape = new Path { Fill = new SolidColorBrush(CardFill), Stroke = new SolidColorBrush(CardEdge), StrokeThickness = 1 };
        var scroll = new ScrollViewer
        {
            Margin = new Thickness(Pad),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = MenuItems(),
        };

        g.SizeChanged += (s, e) =>
        {
            double w = g.ActualWidth, h = g.ActualHeight;
            if (w < 2 || h < 2) return;
            shape.Data = new RectangleGeometry(new Rect(0.5, 0.5, w - 1, h - 1), Radius, Radius);

            double iw = scroll.ActualWidth, ih = scroll.ActualHeight;
            if (iw < 2 || ih < 2) return;
            double r = Math.Max(0, Radius - Pad);
            scroll.OpacityMask = new DrawingBrush
            {
                Stretch = Stretch.None,
                TileMode = TileMode.None,
                Drawing = new GeometryDrawing(Brushes.White, null,
                    new RectangleGeometry(new Rect(0, 0, iw, ih), r, r)),
            };
        };

        g.Children.Add(shape);
        g.Children.Add(scroll);
        g.Measure(new Size(CW, CH));
        g.Arrange(new Rect(0, 0, CW, CH));
        g.UpdateLayout();
        return Frame("4 scroll+mask", g);
    }

    // 5. Same as 4, but the mask sits on a WRAPPER around the ScrollViewer, and
    //    uses a relative viewbox so the drawing is mapped onto the wrapper's own
    //    box regardless of its measured size.
    private static FrameworkElement ScrollWrapMask()
    {
        var g = new Grid();
        var shape = new Path { Fill = new SolidColorBrush(CardFill), Stroke = new SolidColorBrush(CardEdge), StrokeThickness = 1 };
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = MenuItems(),
        };
        var wrap = new Border { Margin = new Thickness(Pad), Child = scroll };

        g.SizeChanged += (s, e) =>
        {
            double w = g.ActualWidth, h = g.ActualHeight;
            if (w < 2 || h < 2) return;
            shape.Data = new RectangleGeometry(new Rect(0.5, 0.5, w - 1, h - 1), Radius, Radius);

            double iw = wrap.ActualWidth, ih = wrap.ActualHeight;
            if (iw < 2 || ih < 2) return;
            double r = Math.Max(0, Radius - Pad);
            wrap.OpacityMask = new DrawingBrush
            {
                Stretch = Stretch.Fill,
                TileMode = TileMode.None,
                Viewbox = new Rect(0, 0, 1, 1),
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                Viewport = new Rect(0, 0, 1, 1),
                ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                Drawing = new GeometryDrawing(Brushes.White, null,
                    new RectangleGeometry(new Rect(0, 0, iw, ih), r, r)),
            };
        };

        g.Children.Add(shape);
        g.Children.Add(wrap);
        g.Measure(new Size(CW, CH));
        g.Arrange(new Rect(0, 0, CW, CH));
        g.UpdateLayout();
        return Frame("5 wrap+mask", g);
    }

    // 6. No mask at all: the host that CONTAINS the ScrollViewer gets a rounded
    //    Clip using the FULL card radius (not radius-minus-pad). Because the
    //    clip is on the wrapper, everything inside - including item rows with
    //    their own opaque backgrounds - is cut by the same arc.
    private static FrameworkElement ClipHost()
    {
        var g = new Grid();
        var shape = new Path { Fill = new SolidColorBrush(CardFill), Stroke = new SolidColorBrush(CardEdge), StrokeThickness = 1 };
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = MenuItems(),
        };
        var wrap = new Border { Margin = new Thickness(Pad), Child = scroll };

        g.SizeChanged += (s, e) =>
        {
            double w = g.ActualWidth, h = g.ActualHeight;
            if (w < 2 || h < 2) return;
            shape.Data = new RectangleGeometry(new Rect(0.5, 0.5, w - 1, h - 1), Radius, Radius);

            double iw = wrap.ActualWidth, ih = wrap.ActualHeight;
            if (iw < 2 || ih < 2) return;
            // Inner radius chosen so the arc stays concentric with the outer one.
            wrap.Clip = new RectangleGeometry(new Rect(0, 0, iw, ih),
                Math.Max(0, Radius - Pad), Math.Max(0, Radius - Pad));
        };

        g.Children.Add(shape);
        g.Children.Add(wrap);
        g.Measure(new Size(CW, CH));
        g.Arrange(new Rect(0, 0, CW, CH));
        g.UpdateLayout();
        return Frame("6 clip host", g);
    }

    // 7. Like 6, but the clip is applied to the whole Grid (card) so the shape
    //    itself is never overdrawn either.
    private static FrameworkElement ClipFull()
    {
        var g = new Grid();
        var shape = new Path { Fill = new SolidColorBrush(CardFill), Stroke = new SolidColorBrush(CardEdge), StrokeThickness = 1 };
        var scroll = new ScrollViewer
        {
            Margin = new Thickness(Pad),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = MenuItems(),
        };

        g.SizeChanged += (s, e) =>
        {
            double w = g.ActualWidth, h = g.ActualHeight;
            if (w < 2 || h < 2) return;
            shape.Data = new RectangleGeometry(new Rect(0.5, 0.5, w - 1, h - 1), Radius, Radius);
            // Clip the ScrollViewer with the concentric inner radius.
            double iw = scroll.ActualWidth, ih = scroll.ActualHeight;
            if (iw < 2 || ih < 2) return;
            scroll.Clip = new RectangleGeometry(new Rect(0, 0, iw, ih),
                Math.Max(0, Radius - Pad), Math.Max(0, Radius - Pad));
        };

        g.Children.Add(shape);
        g.Children.Add(scroll);
        g.Measure(new Size(CW, CH));
        g.Arrange(new Rect(0, 0, CW, CH));
        g.UpdateLayout();
        return Frame("7 clip scroll", g);
    }

    // 8. Control: the card shape and NOTHING else. If this shows four rounded
    //    corners, the Path geometry is correct and any remaining squareness in
    //    the other variants comes from the content layer, not the shape.
    private static FrameworkElement ShapeOnly()
    {
        var g = new Grid();
        var shape = new Path { Fill = new SolidColorBrush(CardFill), Stroke = new SolidColorBrush(CardEdge), StrokeThickness = 1 };
        g.SizeChanged += (s, e) =>
        {
            double w = g.ActualWidth, h = g.ActualHeight;
            if (w < 2 || h < 2) return;
            shape.Data = new RectangleGeometry(new Rect(0.5, 0.5, w - 1, h - 1), Radius, Radius);
        };
        g.Children.Add(shape);
        g.Measure(new Size(CW, CH));
        g.Arrange(new Rect(0, 0, CW, CH));
        g.UpdateLayout();
        return Frame("8 shape only", g);
    }

    // 9. The real-situation test: card Path + item rows that are TRANSPARENT at
    //    rest (matching SubTemplate's Bd Background='Transparent'), so the card's
    //    rounded shape is what shows at the corners.
    private static FrameworkElement TransparentRows()
    {
        var g = new Grid();
        var shape = new Path { Fill = new SolidColorBrush(CardFill), Stroke = new SolidColorBrush(CardEdge), StrokeThickness = 1 };
        var scroll = new ScrollViewer
        {
            Margin = new Thickness(Pad),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = MenuItemsTransparent(),
        };

        g.SizeChanged += (s, e) =>
        {
            double w = g.ActualWidth, h = g.ActualHeight;
            if (w < 2 || h < 2) return;
            shape.Data = new RectangleGeometry(new Rect(0.5, 0.5, w - 1, h - 1), Radius, Radius);
            double iw = scroll.ActualWidth, ih = scroll.ActualHeight;
            if (iw < 2 || ih < 2) return;
            scroll.Clip = new RectangleGeometry(new Rect(0, 0, iw, ih),
                Math.Max(0, Radius - Pad), Math.Max(0, Radius - Pad));
        };

        g.Children.Add(shape);
        g.Children.Add(scroll);
        g.Measure(new Size(CW, CH));
        g.Arrange(new Rect(0, 0, CW, CH));
        g.UpdateLayout();
        return Frame("9 transparent rows", g);
    }

    // One shared builder for the rounded opacity mask, so the ScrollViewer and
    // its content get the identical treatment.
    private static DrawingBrush RoundedMask(double w, double h, double r)
    {
        return new DrawingBrush
        {
            Stretch = Stretch.Fill,
            TileMode = TileMode.None,
            Viewbox = new Rect(0, 0, 1, 1),
            ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewport = new Rect(0, 0, 1, 1),
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
            Drawing = new GeometryDrawing(Brushes.White, null,
                new RectangleGeometry(new Rect(0, 0, w, h), r, r)),
        };
    }

    private enum RealMode { TransparentRows, HoverHighlight, AlwaysScrollbar, NoItemMargin }

    // Exactly the shipped structure:
    //   Grid (Card)
    //     ├ Path (CardShape)      no size; geometry written on SizeChanged
    //     └ ScrollViewer (CardScroll, Margin=inset)
    //         └ ItemsPresenter     <- the real host; NOT a StackPanel
    //   items are real MenuItems using a Sub-like template with an OPAQUE row
    //   background, margin 3,1 - matching VOCALOID6's own items.
    private static FrameworkElement RealItemsPresenter(double inset)
    {
        var g = new Grid { MinWidth = 210 };
        var shape = new Path
        {
            Fill = new SolidColorBrush(CardFill),
            Stroke = new SolidColorBrush(CardEdge),
            StrokeThickness = 1,
        };

        // OPAQUE row backgrounds - the layer that can square off a corner, and
        // the reason earlier "transparent row" labs could never reproduce the
        // shipped defect. Items go directly into a StackPanel inside the
        // ScrollViewer: ItemContainerGenerator-based hosting renders
        // asynchronously and produced empty cards in the previous attempt.
        var items = new StackPanel();
        for (int i = 0; i < 6; i++)
        {
            items.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x43, 0x47, 0x4F)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(9, 7, 11, 7),
                Margin = new Thickness(3, 1, 3, 1),
                MinHeight = 34,
                Child = new TextBlock
                {
                    Text = "菜单项 " + i,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brushes.White,
                    FontSize = 13,
                },
            });
        }

        var scroll = new ScrollViewer
        {
            Margin = new Thickness(inset),
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = items,
        };

        g.SizeChanged += (s, e) =>
        {
            double w = g.ActualWidth, h = g.ActualHeight;
            if (w < 2 || h < 2) return;
            shape.Data = new RectangleGeometry(new Rect(0.5, 0.5, w - 1, h - 1), Radius, Radius);

            double iw = scroll.ActualWidth, ih = scroll.ActualHeight;
            if (iw < 2 || ih < 2) return;
            double r = Math.Max(0, Radius - inset);
            // Put the rounded mask on BOTH the ScrollViewer and its content.
            // A ScrollViewer with HorizontalScrollBarVisibility=Disabled still
            // measures its child at the child's full desired width, so a row can
            // paint past the ScrollViewer's own right edge -- which is exactly the
            // "rows reach the card border on the right" defect. Masking the inner
            // panel too pins the rows inside the rounded area.
            scroll.OpacityMask = RoundedMask(iw, ih, r);
            if (items.ActualWidth >= 2 && items.ActualHeight >= 2)
                items.OpacityMask = RoundedMask(items.ActualWidth, items.ActualHeight, r);
        };

        g.Children.Add(shape);
        g.Children.Add(scroll);

        g.Measure(new Size(CW, CH));
        g.Arrange(new Rect(0, 0, CW, CH));
        g.UpdateLayout();
        return Frame("inset " + inset, g);
    }

    // The real situation the earlier lab versions MISSED: menu item rows carry an
    // OPAQUE background (VOCALOID6's own items are not transparent), so each row
    // paints a hard rectangle starting at (CardInset + rowMargin) from the card
    // edge. If that start lands inside the card's corner arc, the row's square
    // corner shows through and the card's rounding is hidden.
    //
    // This variant sweeps rowMargin so the crossover point is measurable.
    private static FrameworkElement OpaqueRows(double rowMargin)
    {
        var g = new Grid();
        var shape = new Path
        {
            Fill = new SolidColorBrush(CardFill),
            Stroke = new SolidColorBrush(CardEdge),
            StrokeThickness = 1,
        };

        var items = new StackPanel();
        for (int i = 0; i < 5; i++)
        {
            var bd = new Border
            {
                // OPAQUE row background - this is the layer that can square a corner.
                Background = new SolidColorBrush(ContentFill),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(9, 7, 11, 7),
                Margin = new Thickness(rowMargin, 1, rowMargin, 1),
                MinHeight = 34,
            };
            items.Children.Add(bd);
        }

        var scroll = new ScrollViewer
        {
            Margin = new Thickness(Pad),
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = items,
        };

        g.SizeChanged += (s, e) =>
        {
            double w = g.ActualWidth, h = g.ActualHeight;
            if (w < 2 || h < 2) return;
            shape.Data = new RectangleGeometry(new Rect(0.5, 0.5, w - 1, h - 1), Radius, Radius);
            double iw = scroll.ActualWidth, ih = scroll.ActualHeight;
            if (iw < 2 || ih < 2) return;
            double r = Math.Max(0, Radius - Pad);
            scroll.OpacityMask = new DrawingBrush
            {
                Stretch = Stretch.None,
                TileMode = TileMode.None,
                Drawing = new GeometryDrawing(Brushes.White, null,
                    new RectangleGeometry(new Rect(0, 0, iw, ih), r, r)),
            };
        };

        g.Children.Add(shape);
        g.Children.Add(scroll);
        g.Measure(new Size(CW, CH));
        g.Arrange(new Rect(0, 0, CW, CH));
        g.UpdateLayout();
        return Frame("margin " + rowMargin, g);
    }
    // Faithful reproduction of the REAL SubTemplate structure:
    //
    //   Grid (card)
    //     ├ Path   rounded fill + stroke  (geometry synced to card size)
    //     └ ScrollViewer  Margin=3
    //         └ StackPanel (ItemsPresenter)
    //             └ MenuItem rows: Border Bd, Padding=9,7,11,7
    //                             Margin=3,1  MinHeight=34
    //
    // The previous lab variants used simplified content. This one keeps every
    // margin and padding from the real template, because those insets are the
    // whole question: the row margin (3) plus the card pad (3) puts the row's
    // own paint exactly where the corner arc lives.
    private static FrameworkElement RealTemplate(RealMode mode)
    {
        var g = new Grid();
        var shape = new Path
        {
            Fill = new SolidColorBrush(CardFill),
            Stroke = new SolidColorBrush(CardEdge),
            StrokeThickness = 1,
        };

        var items = new StackPanel();
        for (int i = 0; i < 5; i++)
        {
            bool hover = mode == RealMode.HoverHighlight && i == 4;
            var bd = new Border
            {
                // Real template: Bd Background='Transparent' at rest, and the
                // hover trigger sets #12FFFFFF. Both cases must be tested.
                Background = hover
                    ? new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF))
                    : Brushes.Transparent,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(9, 7, 11, 7),
                Margin = mode == RealMode.NoItemMargin
                    ? new Thickness(0, 1, 0, 1)
                    : new Thickness(3, 1, 3, 1),
                MinHeight = 34,
            };
            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var check = new TextBlock
            {
                Text = "\uE73E",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 13,
                Width = 24,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0,
            };
            Grid.SetColumn(check, 0);
            rowGrid.Children.Add(check);

            var header = new TextBlock
            {
                Text = "菜单项 " + i,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.White,
                FontSize = 13,
            };
            Grid.SetColumn(header, 1);
            rowGrid.Children.Add(header);

            bd.Child = rowGrid;
            items.Children.Add(bd);
        }

        var scroll = new ScrollViewer
        {
            Margin = new Thickness(Pad),
            VerticalScrollBarVisibility = mode == RealMode.AlwaysScrollbar
                ? ScrollBarVisibility.Visible
                : ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = items,
        };

        g.SizeChanged += (s, e) =>
        {
            double w = g.ActualWidth, h = g.ActualHeight;
            if (w < 2 || h < 2) return;
            shape.Data = new RectangleGeometry(new Rect(0.5, 0.5, w - 1, h - 1), Radius, Radius);
            double iw = scroll.ActualWidth, ih = scroll.ActualHeight;
            if (iw < 2 || ih < 2) return;
            scroll.Clip = new RectangleGeometry(new Rect(0, 0, iw, ih),
                Math.Max(0, Radius - Pad), Math.Max(0, Radius - Pad));
        };

        g.Children.Add(shape);
        g.Children.Add(scroll);
        g.Measure(new Size(CW, CH));
        g.Arrange(new Rect(0, 0, CW, CH));
        g.UpdateLayout();
        return Frame("real " + mode, g);
    }

    // E: the real card lives inside a Popup, which measures its child in a
    // SEPARATE visual tree with no fixed size. Everything inside is measured
    // before it is ever shown, and SizeChanged may only fire once the popup is
    // open. This variant reproduces that: build the card, then open a Popup
    // containing it, and render the popup's own visual tree.
    private static FrameworkElement RealTemplateInPopup()
    {
        var g = new Grid { Width = CW, Height = CH };
        var shape = new Path
        {
            Fill = new SolidColorBrush(CardFill),
            Stroke = new SolidColorBrush(CardEdge),
            StrokeThickness = 1,
        };

        var items = new StackPanel();
        for (int i = 0; i < 5; i++)
        {
            var bd = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(9, 7, 11, 7),
                Margin = new Thickness(3, 1, 3, 1),
                MinHeight = 34,
                Child = new TextBlock
                {
                    Text = "菜单项 " + i,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brushes.White,
                    FontSize = 13,
                },
            };
            items.Children.Add(bd);
        }

        var scroll = new ScrollViewer
        {
            Margin = new Thickness(Pad),
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = items,
        };

        g.SizeChanged += (s, e) =>
        {
            double w = g.ActualWidth, h = g.ActualHeight;
            if (w < 2 || h < 2) return;
            shape.Data = new RectangleGeometry(new Rect(0.5, 0.5, w - 1, h - 1), Radius, Radius);
            double iw = scroll.ActualWidth, ih = scroll.ActualHeight;
            if (iw < 2 || ih < 2) return;
            scroll.Clip = new RectangleGeometry(new Rect(0, 0, iw, ih),
                Math.Max(0, Radius - Pad), Math.Max(0, Radius - Pad));
        };

        g.Children.Add(shape);
        g.Children.Add(scroll);

        // Lay the card out first (like the template being applied), THEN show it
        // in a popup - matching the real open sequence. The popup is opened and
        // immediately closed again: it forces the same measure/arrange path the
        // real card goes through, and the card can then be re-parented into the
        // frame for rendering.
        var popup = new Popup
        {
            Child = g,
            Placement = PlacementMode.Absolute,
            HorizontalOffset = 0,
            VerticalOffset = 0,
            StaysOpen = true,
            IsOpen = true,
        };
        popup.UpdateLayout();
        double pw2 = g.ActualWidth, ph2 = g.ActualHeight;
        popup.Child = null;
        popup.IsOpen = false;

        Console.WriteLine("    [popup] card measured " + pw2.ToString("F0") + "x" + ph2.ToString("F0"));
        return Frame("E in-popup-layout", g);
    }

    // 10. Clip the OUTER CARD GRID with the full rounded geometry. This catches
    //     ALL child overflow — Path stroke, ScrollViewer, rows that paint past
    //     the ScrollViewer's right edge — because Clip is a hard geometric cut
    //     at the parent level. OpacityMask (the current shipped approach) only
    //     modulates alpha inside the element's own bounds and cannot touch
    //     content that overflows those bounds.
    private static FrameworkElement ClipCardGrid()
    {
        var g = new Grid();
        var shape = new Path
        {
            Fill = new SolidColorBrush(CardFill),
            Stroke = new SolidColorBrush(CardEdge),
            StrokeThickness = 1,
        };

        // Opaque rows — the layer that squares off a corner in the real menu.
        var items = new StackPanel();
        for (int i = 0; i < 6; i++)
        {
            items.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x43, 0x47, 0x4F)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(9, 7, 11, 7),
                Margin = new Thickness(3, 1, 3, 1),
                MinHeight = 34,
                Child = new TextBlock
                {
                    Text = "item " + i,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brushes.White,
                    FontSize = 13,
                },
            });
        }

        var scroll = new ScrollViewer
        {
            Margin = new Thickness(Pad),
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = items,
        };

        g.SizeChanged += (s, e) =>
        {
            double w = g.ActualWidth, h = g.ActualHeight;
            if (w < 2 || h < 2) return;
            shape.Data = new RectangleGeometry(new Rect(0.5, 0.5, w - 1, h - 1), Radius, Radius);
            // Hard-clip the entire card to the rounded outer shape. This is the
            // fix: nothing inside can paint past the rounded boundary.
            g.Clip = new RectangleGeometry(new Rect(0, 0, w, h), Radius, Radius);
        };

        g.Children.Add(shape);
        g.Children.Add(scroll);
        g.Measure(new Size(CW, CH));
        g.Arrange(new Rect(0, 0, CW, CH));
        g.UpdateLayout();
        return Frame("10 clip-card-grid", g);
    }
}
