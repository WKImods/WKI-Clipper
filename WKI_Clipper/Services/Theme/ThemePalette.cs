using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>One choice in the colour picker: an id for settings.json, names in both languages and the colour of its swatch.</summary>
public sealed record ThemePreset(string Id, string NameDe, string NameEn, string Hex);

/// <summary>The ready-made accents and glass tints of the picker.</summary>
public static class ThemePresets
{
    public const string DefaultAccentId = "wki-orange";
    public const string DefaultGlassId = "violet";

    /// <summary>Accent colours. The hex is the accent itself (the main colour of buttons, bars, the selection).</summary>
    public static IReadOnlyList<ThemePreset> Accent { get; } = new[]
    {
        new ThemePreset("wki-orange", "WKI-Orange", "WKI orange", "#FF7A2C"),
        new ThemePreset("epic-blue", "Episches Blau", "Epic blue", "#4C7DFF"),
        new ThemePreset("crimson", "Karmesinrot", "Crimson", "#DC143C"),
        new ThemePreset("emerald", "Smaragd", "Emerald", "#14C98B"),
        new ThemePreset("royal-violet", "Königsviolett", "Royal violet", "#9B5CF6"),
        new ThemePreset("gold", "Gold", "Gold", "#F5B700"),
        new ThemePreset("ice-blue", "Eisblau", "Ice blue", "#22D3EE"),
        new ThemePreset("magenta", "Magenta", "Magenta", "#FF4FA3"),
    };

    /// <summary>
    /// Glass tints. The hex is a swatch colour that carries the tint's hue and saturation (the glass keeps today's lightness);
    /// the violet one is exactly the current theme.
    /// </summary>
    public static IReadOnlyList<ThemePreset> Glass { get; } = new[]
    {
        new ThemePreset("violet", "Violett", "Violet", "#2D2340"),
        new ThemePreset("night-blue", "Nachtblau", "Night blue", "#1E2A45"),
        new ThemePreset("graphite", "Graphit", "Graphite", "#2F3134"),
        new ThemePreset("bordeaux", "Bordeaux", "Bordeaux", "#461D26"),
        new ThemePreset("forest", "Waldgrün", "Forest green", "#214230"),
        new ThemePreset("black", "Schwarz (OLED)", "Black (OLED)", "#0A0A0A"),
    };

    public static ThemePreset? FindAccent(string? id) => Accent.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    public static ThemePreset? FindGlass(string? id) => Glass.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Every colour a theme decides, as plain values: <see cref="Colors"/> by resource key, and the stops of the four gradient
/// brushes by brush key (their offsets and directions are fixed, see <see cref="ThemePaletteKeys"/>).
/// </summary>
public sealed record ThemePalette(IReadOnlyDictionary<string, Color> Colors, IReadOnlyDictionary<string, Color[]> GradientStops);

/// <summary>A gradient brush of the theme: its key, the offsets of its stops and the direction it runs in.</summary>
public sealed record GradientSpec(string Key, double[] Offsets, Point Start, Point End);

/// <summary>
/// The resource keys of Views/Theme.xaml a theme owns - and, by leaving them out, the ones it never touches: the text colour,
/// the signal colours (danger, success), the warning amber and the white hairlines.
/// </summary>
public static class ThemePaletteKeys
{
    /// <summary>The colour resources a theme sets. The last four carry an alpha: the halo of the slider thumb, the tooltip's edge and fill.</summary>
    public static IReadOnlyList<string> ColorKeys { get; } = new[]
    {
        "AccentColor", "AccentLightColor", "AccentDim",
        "BgColor", "PanelColor", "PanelHoverColor", "BorderColor", "MutedColor", "FaintColor",
        "AccentGlowColor", "AccentGlowStrongColor", "AccentEdgeColor", "PopupColor",
    };

    /// <summary>The solid brushes of a theme and the colour each one is made of.</summary>
    public static IReadOnlyList<(string Brush, string Color)> SolidBrushes { get; } = new[]
    {
        ("AccentBrush", "AccentColor"), ("AccentDimBrush", "AccentDim"),
        ("BgBrush", "BgColor"), ("PanelBrush", "PanelColor"), ("PanelHoverBrush", "PanelHoverColor"),
        ("BorderBrush", "BorderColor"), ("MutedBrush", "MutedColor"), ("FaintBrush", "FaintColor"),
        ("AccentGlowBrush", "AccentGlowColor"), ("AccentGlowStrongBrush", "AccentGlowStrongColor"),
        ("AccentEdgeBrush", "AccentEdgeColor"), ("PopupBrush", "PopupColor"),
    };

    /// <summary>The gradient brushes, with the offsets and direction they have in Theme.xaml.</summary>
    public static IReadOnlyList<GradientSpec> Gradients { get; } = new[]
    {
        new GradientSpec("ActiveBrush", new[] { 0.0, 1.0 }, new Point(0, 0), new Point(1, 0)),
        new GradientSpec("AccentGradientBrush", new[] { 0.0, 1.0 }, new Point(0, 0), new Point(0, 1)),
        new GradientSpec("BarBrush", new[] { 0.0, 0.6, 1.0 }, new Point(0, 0), new Point(1, 0)),
        new GradientSpec("SurfaceBrush", new[] { 0.0, 0.18, 1.0 }, new Point(0, 0), new Point(0, 1)),
    };

    /// <summary>Every resource key a theme owns: the XAML must reference these dynamically, or a switch would not reach them.</summary>
    public static IReadOnlyList<string> All { get; } = ColorKeys
        .Concat(SolidBrushes.Select(b => b.Brush))
        .Concat(Gradients.Select(g => g.Key))
        .ToArray();
}

/// <summary>HSL maths and hex text for theme colours (the picker's sliders use it, too).</summary>
public static class ThemeColor
{
    /// <summary>Hue 0-360, saturation and lightness 0-1.</summary>
    public static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, d = max - min;
        if (d == 0) return (0, 0, l);

        double s = d / (1 - Math.Abs(2 * l - 1));
        double h = max == r ? 60 * (((g - b) / d) % 6)
                 : max == g ? 60 * ((b - r) / d + 2)
                 : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, Math.Clamp(s, 0, 1), l);
    }

    public static Color FromHsl(double h, double s, double l, byte alpha = 255)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double hp = h / 60;
        double x = c * (1 - Math.Abs(hp % 2 - 1));
        double m = l - c / 2;
        (double r, double g, double b) = hp switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromArgb(alpha, ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    /// <summary>"#RRGGBB" in upper case (the alpha is not part of a theme's hex).</summary>
    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    internal static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    private static byte ToByte(double v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
}

/// <summary>
/// Builds the palette of a theme choice. The default choice (WKI orange on violet glass) IS today's theme, value for value.
/// Everything else derives from it: an accent brings its lighter, darker and bar shades with the same relation the orange has
/// to its own; a glass tint moves the hue and saturation of today's violet surfaces and keeps their lightness.
/// </summary>
public static class ThemePaletteBuilder
{
    /// <summary>Text on the glass is never recoloured, so an accent has to stand out from the panels on its own: WCAG 3:1.</summary>
    public const double MinAccentContrast = 3.0;

    // ---- today's theme (Views/Theme.xaml): the reference everything else is derived from ----

    private static readonly Color RefAccent = Rgb(0xFF, 0x7A, 0x2C);
    private static readonly (Color Light, Color Dim, Color BarDeep, Color ActiveEnd) RefAccentShades =
        (Rgb(0xFF, 0x9F, 0x4A), Rgb(0x8F, 0x4A, 0x2E), Rgb(0xC8, 0x46, 0x1A), Rgb(0x96, 0x3E, 0x2C));

    /// <summary>The tint of the violet glass (its hover panel) - hue and saturation every other tint is measured against.</summary>
    private static readonly Color RefTint = Rgb(0x2D, 0x23, 0x40);
    private static readonly (double H, double S, double L) RefTintHsl = ThemeColor.ToHsl(RefTint);

    private static readonly GlassColors RefGlass = new(
        Bg: Rgb(0x14, 0x0E, 0x1F), Panel: Rgb(0x22, 0x1A, 0x30), PanelHover: Rgb(0x2D, 0x23, 0x40), Border: Rgb(0x3A, 0x2F, 0x4D),
        Muted: Rgb(0x8E, 0x86, 0xA3), Faint: Rgb(0x5E, 0x57, 0x72),
        Surface0: Rgb(0x26, 0x1D, 0x36), Surface1: Rgb(0x1A, 0x14, 0x26), Popup: Rgb(0x31, 0x1F, 0x33));

    /// <summary>The surface colours of a glass tint (opaque; the alphas of the gradients and the tooltip are added when the palette is built).</summary>
    internal sealed record GlassColors(Color Bg, Color Panel, Color PanelHover, Color Border, Color Muted, Color Faint,
                                       Color Surface0, Color Surface1, Color Popup);

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    // ---- building ----

    public static ThemePalette Build(ThemeSettings s)
    {
        s ??= new ThemeSettings();

        var glass = ResolveGlass(s);
        var accent = LiftForContrast(ResolveAccent(s), glass.Panel);
        var (light, dim, barDeep, activeEnd) = DeriveAccent(accent);

        var colors = new Dictionary<string, Color>
        {
            ["AccentColor"] = accent,
            ["AccentLightColor"] = light,
            ["AccentDim"] = dim,
            ["BgColor"] = glass.Bg,
            ["PanelColor"] = glass.Panel,
            ["PanelHoverColor"] = glass.PanelHover,
            ["BorderColor"] = glass.Border,
            ["MutedColor"] = glass.Muted,
            ["FaintColor"] = glass.Faint,
            ["AccentGlowColor"] = ThemeColor.WithAlpha(accent, 0x33),
            ["AccentGlowStrongColor"] = ThemeColor.WithAlpha(accent, 0x66),
            ["AccentEdgeColor"] = ThemeColor.WithAlpha(accent, 0x40),
            ["PopupColor"] = ThemeColor.WithAlpha(glass.Popup, 0xF2),
        };

        var stops = new Dictionary<string, Color[]>
        {
            ["ActiveBrush"] = new[] { ThemeColor.WithAlpha(accent, 0x4D), ThemeColor.WithAlpha(activeEnd, 0x42) },
            ["AccentGradientBrush"] = new[] { light, accent },
            ["BarBrush"] = new[] { barDeep, accent, light },
            ["SurfaceBrush"] = new[]
            {
                ThemeColor.WithAlpha(glass.Surface0, 0xF2), ThemeColor.WithAlpha(glass.Surface1, 0xEB), ThemeColor.WithAlpha(glass.Surface1, 0xEB),
            },
        };
        return new ThemePalette(colors, stops);
    }

    /// <summary>True when the settings ask for exactly today's theme (nothing to apply at startup).</summary>
    public static bool IsDefault(ThemeSettings s)
    {
        if (s is null) return true;
        return NormalizeHex(s.AccentCustom) is null && NormalizeHex(s.GlassCustom) is null
            && (ThemePresets.FindAccent(s.AccentPreset)?.Id ?? ThemePresets.DefaultAccentId) == ThemePresets.DefaultAccentId
            && (ThemePresets.FindGlass(s.GlassPreset)?.Id ?? ThemePresets.DefaultGlassId) == ThemePresets.DefaultGlassId;
    }

    // ---- hex text ----

    /// <summary>"#RRGGBB" in upper case from "#RRGGBB", "RRGGBB", "#RGB" or "RGB" (surrounding blanks allowed); null for anything else.</summary>
    public static string? NormalizeHex(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        string t = input.Trim();
        if (t.StartsWith('#')) t = t[1..];
        if (t.Length == 3) t = string.Concat(t.Select(ch => new string(ch, 2)));
        if (t.Length != 6 || !t.All(Uri.IsHexDigit)) return null;
        return "#" + t.ToUpperInvariant();
    }

    private static Color? ParseHex(string? input)
    {
        if (NormalizeHex(input) is not { } hex) return null;
        return Rgb(Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16));
    }

    // ---- readability ----

    /// <summary>WCAG contrast ratio of two colours, 1 (equal) to 21 (black on white).</summary>
    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Lin(byte v)
        {
            double x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    /// <summary>An accent too dark to stand out from the panel gets exactly as much lightness as the contrast needs - hue and saturation stay.</summary>
    private static Color LiftForContrast(Color accent, Color panel)
    {
        if (Contrast(accent, panel) >= MinAccentContrast) return accent;

        var (h, s, l) = ThemeColor.ToHsl(accent);
        double lo = l, hi = 1;
        for (int i = 0; i < 24; i++)
        {
            double mid = (lo + hi) / 2;
            if (Contrast(ThemeColor.FromHsl(h, s, mid), panel) >= MinAccentContrast) hi = mid; else lo = mid;
        }
        return ThemeColor.FromHsl(h, s, hi);
    }

    // ---- accent ----

    private static Color ResolveAccent(ThemeSettings s)
        => ParseHex(s.AccentCustom)
           ?? ParseHex(ThemePresets.FindAccent(s.AccentPreset)?.Hex)
           ?? RefAccent;

    /// <summary>The lighter, darker, bar and wash shades of an accent. The default orange gets its exact, current ones.</summary>
    private static (Color Light, Color Dim, Color BarDeep, Color ActiveEnd) DeriveAccent(Color accent)
        => accent == RefAccent ? RefAccentShades : DeriveAccentUnpinned(accent);

    /// <summary>
    /// The relation the orange has to its shades, applied to any accent: hue nudged, saturation and lightness scaled (the light
    /// shade lifts lightness 14 % of the way to white). Calibrated on the orange - a test keeps it within a hair of the real values.
    /// </summary>
    internal static (Color Light, Color Dim, Color BarDeep, Color ActiveEnd) DeriveAccentUnpinned(Color accent)
    {
        var (h, s, l) = ThemeColor.ToHsl(accent);
        return (
            ThemeColor.FromHsl(h + 6.0, s, l + (1 - l) * 0.142),
            ThemeColor.FromHsl(h - 4.86, s * 0.5132, l * 0.6321),
            ThemeColor.FromHsl(h - 7.01, s * 0.7699, l * 0.7558),
            ThemeColor.FromHsl(h - 12.0, s * 0.5464, l * 0.6489));
    }

    // ---- glass ----

    private static GlassColors ResolveGlass(ThemeSettings s)
    {
        if (ParseHex(s.GlassCustom) is { } custom) return DeriveGlass(custom, 1.0);

        var preset = ThemePresets.FindGlass(s.GlassPreset) ?? ThemePresets.Glass[0];
        // OLED black is the one glass that is darker than today's lightness: the surfaces drop to a third, the text greys stay.
        double lightScale = preset.Id == "black" ? 0.3 : 1.0;
        return DeriveGlass(ParseHex(preset.Hex) ?? RefTint, lightScale);
    }

    /// <summary>The surfaces for a tint: hue and saturation of the tint, the lightness of today's glass. The violet tint gives exactly today's colours.</summary>
    private static GlassColors DeriveGlass(Color tint, double lightScale)
    {
        var (h, s, _) = ThemeColor.ToHsl(tint);
        double hueShift = h - RefTintHsl.H;
        double satScale = s / RefTintHsl.S;
        if (hueShift == 0 && satScale == 1 && lightScale == 1) return RefGlass;
        return DeriveGlassUnpinned(hueShift, satScale, lightScale);
    }

    /// <summary>
    /// Today's glass with every colour's hue shifted, its saturation scaled and (for the surfaces, not the grey text colours)
    /// its lightness scaled. (0, 1, 1) is the violet, up to rounding.
    /// </summary>
    internal static GlassColors DeriveGlassUnpinned(double hueShift, double satScale, double lightScale)
    {
        static Color Move(Color c, double hueShift, double satScale, double lightScale)
        {
            var (h, s, l) = ThemeColor.ToHsl(c);
            return ThemeColor.FromHsl(h + hueShift, Math.Min(1, s * satScale), l * lightScale);
        }
        Color Surface(Color c) => Move(c, hueShift, satScale, lightScale);
        Color Text(Color c) => Move(c, hueShift, satScale, 1.0);

        return new GlassColors(
            Surface(RefGlass.Bg), Surface(RefGlass.Panel), Surface(RefGlass.PanelHover), Surface(RefGlass.Border),
            Text(RefGlass.Muted), Text(RefGlass.Faint),
            Surface(RefGlass.Surface0), Surface(RefGlass.Surface1), Surface(RefGlass.Popup));
    }
}
