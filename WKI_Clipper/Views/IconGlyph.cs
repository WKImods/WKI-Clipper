using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WKI_Clipper.Views;

/// <summary>
/// Glyphs from Windows' own icon font (Win11: Segoe Fluent Icons, Win10: Segoe MDL2 Assets).
///
/// The font has to be set on the TextBlock itself: the theme's implicit TextBlock style
/// forces "Segoe UI" onto every text — including the one a Button generates for string
/// content — and these private-use code points exist only in the icon font, so a Button
/// with string content shows empty boxes.
/// </summary>
internal static class IconGlyph
{
    public static readonly FontFamily Font = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public const string Previous = "", Next = "", Play = "", Pause = "",
                        Shuffle = "", RepeatAll = "", RepeatOne = "",
                        Volume = "", Refresh = "", Grid = "",
                        Stack = "";   // two overlapping pages ("Copy")

    public static TextBlock Make(string glyph, double size = 14) => new()
    {
        Text = glyph,
        FontFamily = Font,
        FontSize = size,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>A widget's icon — identical in the sidebar and in the widget's title bar.</summary>
    public static FrameworkElement ForWidget(Models.WidgetId id, double size = 15, Brush? brush = null)
    {
        brush ??= (Brush)Application.Current.FindResource("TextBrush");
        if (Services.WidgetCatalog.Glyph(id) is { } glyph)
        {
            var t = Make(glyph, size);
            t.Foreground = brush;
            return t;
        }
        return Crosshair(size + 3, brush);
    }

    /// <summary>Crosshair drawn by hand (ring + four ticks + center dot) — the font has none that reads as one.</summary>
    public static FrameworkElement Crosshair(double size, Brush brush)
    {
        var root = new Grid { Width = 18, Height = 18 };
        root.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 12, Height = 12, Stroke = brush, StrokeThickness = 1.4,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        root.Children.Add(new System.Windows.Shapes.Path
        {
            Stroke = brush, StrokeThickness = 1.4,
            Data = Geometry.Parse("M9,0 L9,4 M9,14 L9,18 M0,9 L4,9 M14,9 L18,9")
        });
        root.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 2.6, Height = 2.6, Fill = brush,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        return new Viewbox { Width = size, Height = size, Child = root };
    }
}
