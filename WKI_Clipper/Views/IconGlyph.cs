using System.Windows;
using System.Windows.Controls;

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
}
