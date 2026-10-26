using System;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;
using Color = System.Windows.Media.Color;

namespace WKI_Clipper.Tests;

/// <summary>
/// The colour themes: which colours a choice of accent and glass produces. Pure maths on System.Windows.Media.Color -
/// nothing here needs a window or a UI thread (the one test that reads the shipped Theme.xaml brings its own).
/// </summary>
[Collection("WpfApplication")]   // the one test that loads Theme.xaml needs the process' single Application
public sealed class ThemePaletteTests
{
    private static Color C(string hex) => (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);

    private static ThemePalette Build(string accent = "wki-orange", string glass = "violet", string? accentCustom = null, string? glassCustom = null)
        => ThemePaletteBuilder.Build(new ThemeSettings { AccentPreset = accent, GlassPreset = glass, AccentCustom = accentCustom, GlassCustom = glassCustom });

    // ---- the default theme is today's theme, value for value ----

    /// <summary>The literal values of Views/Theme.xaml (lines 8-57) at the time the themes were added - the reference the default must hit.</summary>
    [Fact]
    public void Default_palette_reproduces_current_theme_exactly()
    {
        var p = ThemePaletteBuilder.Build(new ThemeSettings());

        Assert.Equal(C("#140E1F"), p.Colors["BgColor"]);
        Assert.Equal(C("#221A30"), p.Colors["PanelColor"]);
        Assert.Equal(C("#2D2340"), p.Colors["PanelHoverColor"]);
        Assert.Equal(C("#3A2F4D"), p.Colors["BorderColor"]);
        Assert.Equal(C("#8E86A3"), p.Colors["MutedColor"]);
        Assert.Equal(C("#5E5772"), p.Colors["FaintColor"]);
        Assert.Equal(C("#FF7A2C"), p.Colors["AccentColor"]);
        Assert.Equal(C("#FF9F4A"), p.Colors["AccentLightColor"]);
        Assert.Equal(C("#8F4A2E"), p.Colors["AccentDim"]);

        Assert.Equal(new[] { C("#4DFF7A2C"), C("#42963E2C") }, p.GradientStops["ActiveBrush"]);
        Assert.Equal(new[] { C("#FF9F4A"), C("#FF7A2C") }, p.GradientStops["AccentGradientBrush"]);
        Assert.Equal(new[] { C("#C8461A"), C("#FF7A2C"), C("#FF9F4A") }, p.GradientStops["BarBrush"]);
        Assert.Equal(new[] { C("#F2261D36"), C("#EB1A1426"), C("#EB1A1426") }, p.GradientStops["SurfaceBrush"]);

        // the hard-coded accent and glass spots of the theme (slider thumb halo, tooltip) as values, too
        Assert.Equal(C("#33FF7A2C"), p.Colors["AccentGlowColor"]);
        Assert.Equal(C("#66FF7A2C"), p.Colors["AccentGlowStrongColor"]);
        Assert.Equal(C("#40FF7A2C"), p.Colors["AccentEdgeColor"]);
        Assert.Equal(C("#F2311F33"), p.Colors["PopupColor"]);
    }

    [Fact]
    public void A_missing_or_unknown_choice_is_the_default_theme()
    {
        var expected = Build();
        Assert.Equal(expected.Colors, Build(accent: "no-such-accent", glass: "no-such-glass").Colors);
        Assert.Equal(expected.Colors, ThemePaletteBuilder.Build(new ThemeSettings { AccentPreset = null!, GlassPreset = null! }).Colors);
    }

    [Fact]
    public void Missing_theme_section_gives_defaults()
    {
        // an old settings.json has no "Theme" at all, a hand-edited one may say null
        var old = JsonSerializer.Deserialize<AppSettings>("{ \"SchemaVersion\": 10 }")!;
        Assert.Equal("wki-orange", old.Theme.AccentPreset);
        Assert.Equal("violet", old.Theme.GlassPreset);
        Assert.Null(old.Theme.AccentCustom);
        Assert.Null(old.Theme.GlassCustom);

        var nulled = JsonSerializer.Deserialize<AppSettings>("{ \"Theme\": null }")!;
        SettingsService.EnsureSections(nulled);
        Assert.NotNull(nulled.Theme);
        Assert.Equal(Build().Colors, ThemePaletteBuilder.Build(nulled.Theme).Colors);

        // and a section survives a round trip
        var s = new AppSettings();
        s.Theme.AccentPreset = "gold";
        s.Theme.GlassCustom = "#204060";
        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s))!;
        Assert.Equal("gold", back.Theme.AccentPreset);
        Assert.Equal("#204060", back.Theme.GlassCustom);
    }

    [Fact]
    public void Default_palette_matches_the_shipped_theme_dictionary()
    {
        // The literal table above and Theme.xaml must never drift apart.
        var p = ThemePaletteBuilder.Build(new ThemeSettings());
        var r = WpfTestSupport.OnSta(() =>
        {
            var dict = (ResourceDictionary)System.Windows.Application.LoadComponent(
                new Uri("/WKI_Clipper;component/Views/Theme.xaml", UriKind.Relative));
            Color[] Stops(string key) => ((LinearGradientBrush)dict[key]).GradientStops.Select(s => s.Color).ToArray();
            return (colors: new[] { "BgColor", "PanelColor", "PanelHoverColor", "BorderColor", "MutedColor", "FaintColor", "AccentColor", "AccentLightColor", "AccentDim" }
                        .ToDictionary(k => k, k => (Color)dict[k]),
                    active: Stops("ActiveBrush"), accent: Stops("AccentGradientBrush"), bar: Stops("BarBrush"), surface: Stops("SurfaceBrush"));
        });

        foreach (var (key, color) in r.colors) Assert.Equal(color, p.Colors[key]);
        Assert.Equal(r.active, p.GradientStops["ActiveBrush"]);
        Assert.Equal(r.accent, p.GradientStops["AccentGradientBrush"]);
        Assert.Equal(r.bar, p.GradientStops["BarBrush"]);
        Assert.Equal(r.surface, p.GradientStops["SurfaceBrush"]);
    }

    // ---- presets ----

    [Fact]
    public void The_presets_are_the_ones_of_the_design_in_both_languages()
    {
        Assert.Equal(new[] { "WKI-Orange", "Episches Blau", "Karmesinrot", "Smaragd", "Königsviolett", "Gold", "Eisblau", "Magenta" },
                     ThemePresets.Accent.Select(p => p.NameDe));
        Assert.Equal(new[] { "#FF7A2C", "#4C7DFF", "#DC143C", "#14C98B", "#9B5CF6", "#F5B700", "#22D3EE", "#FF4FA3" },
                     ThemePresets.Accent.Select(p => p.Hex));
        Assert.Equal(new[] { "Violett", "Nachtblau", "Graphit", "Bordeaux", "Waldgrün", "Schwarz (OLED)" },
                     ThemePresets.Glass.Select(p => p.NameDe));
        Assert.All(ThemePresets.Accent.Concat(ThemePresets.Glass), p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.NameEn));
            Assert.Equal(p.Hex, ThemePaletteBuilder.NormalizeHex(p.Hex));
        });
        Assert.Equal("wki-orange", ThemePresets.Accent[0].Id);
        Assert.Equal("violet", ThemePresets.Glass[0].Id);
        Assert.Equal(ThemePresets.Accent.Count, ThemePresets.Accent.Select(p => p.Id).Distinct().Count());
        Assert.Equal(ThemePresets.Glass.Count, ThemePresets.Glass.Select(p => p.Id).Distinct().Count());
    }

    private static void AssertAllKeys(ThemePalette p)
    {
        foreach (var key in ThemePaletteKeys.ColorKeys) Assert.True(p.Colors.ContainsKey(key), "colour " + key);
        foreach (var gradient in ThemePaletteKeys.Gradients)
        {
            Assert.True(p.GradientStops.ContainsKey(gradient.Key), "gradient " + gradient.Key);
            Assert.Equal(gradient.Offsets.Length, p.GradientStops[gradient.Key].Length);
        }
        Assert.Equal(ThemePaletteKeys.ColorKeys.Count, p.Colors.Count);
        Assert.Equal(ThemePaletteKeys.Gradients.Count, p.GradientStops.Count);
    }

    [Fact]
    public void Every_accent_preset_builds_all_keys()
    {
        foreach (var preset in ThemePresets.Accent)
        {
            var p = Build(accent: preset.Id);
            AssertAllKeys(p);
            Assert.Equal(C(preset.Hex), p.Colors["AccentColor"]);                       // the glass is dark enough: no lift
            Assert.NotEqual(p.Colors["AccentColor"], p.Colors["AccentLightColor"]);
            Assert.True(Lightness(p.Colors["AccentLightColor"]) > Lightness(p.Colors["AccentColor"]), preset.Id + ": light is lighter");
            Assert.True(Lightness(p.Colors["AccentDim"]) < Lightness(p.Colors["AccentColor"]), preset.Id + ": dim is darker");
            // the accent follows the whole gradient family
            Assert.Equal(p.Colors["AccentColor"], p.GradientStops["AccentGradientBrush"][1]);
            Assert.Equal(p.Colors["AccentLightColor"], p.GradientStops["AccentGradientBrush"][0]);
            Assert.Equal(p.Colors["AccentColor"], p.GradientStops["BarBrush"][1]);
            Assert.Equal((byte)0x4D, p.GradientStops["ActiveBrush"][0].A);
            Assert.Equal((byte)0x42, p.GradientStops["ActiveBrush"][1].A);
        }
    }

    [Fact]
    public void Every_glass_preset_builds_all_keys()
    {
        foreach (var preset in ThemePresets.Glass)
        {
            var p = Build(glass: preset.Id);
            AssertAllKeys(p);
            // depth order of the glass survives every tint: background < panel < hover < border, text greys on top
            Assert.True(Lightness(p.Colors["BgColor"]) < Lightness(p.Colors["PanelColor"]), preset.Id);
            Assert.True(Lightness(p.Colors["PanelColor"]) < Lightness(p.Colors["PanelHoverColor"]), preset.Id);
            Assert.True(Lightness(p.Colors["PanelHoverColor"]) < Lightness(p.Colors["BorderColor"]), preset.Id);
            Assert.True(Lightness(p.Colors["FaintColor"]) < Lightness(p.Colors["MutedColor"]), preset.Id);
            // the surface keeps its alphas
            Assert.Equal(new byte[] { 0xF2, 0xEB, 0xEB }, p.GradientStops["SurfaceBrush"].Select(c => c.A).ToArray());
        }
        // a different glass is a different glass
        Assert.NotEqual(Build(glass: "violet").Colors["PanelColor"], Build(glass: "night-blue").Colors["PanelColor"]);
        Assert.NotEqual(Build(glass: "violet").Colors["BgColor"], Build(glass: "bordeaux").Colors["BgColor"]);
    }

    [Fact]
    public void The_black_glass_is_darker_than_any_other_and_the_graphite_is_nearly_grey()
    {
        var black = Build(glass: "black").Colors;
        foreach (var preset in ThemePresets.Glass.Where(g => g.Id != "black"))
            Assert.True(Lightness(black["BgColor"]) < Lightness(Build(glass: preset.Id).Colors["BgColor"]), preset.Id);
        Assert.True(Lightness(black["BgColor"]) < 0.05);

        var graphite = Build(glass: "graphite").Colors["PanelColor"];
        Assert.True(Math.Max(graphite.R, Math.Max(graphite.G, graphite.B)) - Math.Min(graphite.R, Math.Min(graphite.G, graphite.B)) <= 8);
    }

    // ---- custom colours ----

    [Fact]
    public void Custom_hex_overrides_preset()
    {
        var p = Build(accent: "gold", accentCustom: "#33CC66");
        Assert.Equal(C("#33CC66"), p.Colors["AccentColor"]);

        var glass = Build(glass: "violet", glassCustom: "#204060");
        Assert.NotEqual(Build().Colors["PanelColor"], glass.Colors["PanelColor"]);
        // a bluish tint gives a bluish glass
        var panel = glass.Colors["PanelColor"];
        Assert.True(panel.B > panel.R, "the custom blue tint reaches the panel");

        // the typed hex is as good as the picked one: no '#', lower case, short form
        Assert.Equal(C("#33CC66"), Build(accentCustom: "33cc66").Colors["AccentColor"]);
        Assert.Equal(C("#33CC66"), Build(accentCustom: " #3C6 ").Colors["AccentColor"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nope")]
    [InlineData("#12")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("#1234567")]
    public void Invalid_custom_hex_falls_back_to_preset(string? bad)
    {
        Assert.Null(ThemePaletteBuilder.NormalizeHex(bad));
        Assert.Equal(Build(accent: "emerald").Colors, Build(accent: "emerald", accentCustom: bad).Colors);
        Assert.Equal(Build(glass: "forest").Colors, Build(glass: "forest", glassCustom: bad).Colors);
    }

    [Theory]
    [InlineData("#33cc66", "#33CC66")]
    [InlineData("33CC66", "#33CC66")]
    [InlineData("  #3c6  ", "#33CC66")]
    [InlineData("#ff7a2c", "#FF7A2C")]
    public void Hex_is_normalised_to_six_upper_case_digits(string input, string expected)
        => Assert.Equal(expected, ThemePaletteBuilder.NormalizeHex(input));

    // ---- readability ----

    [Fact]
    public void Dark_custom_accent_is_lifted_to_contrast_3()
    {
        foreach (var dark in new[] { "#2A0A1A", "#000000", "#101040", "#331100" })
        {
            var p = Build(accentCustom: dark);
            double contrast = ThemePaletteBuilder.Contrast(p.Colors["AccentColor"], p.Colors["PanelColor"]);
            Assert.True(contrast >= 3.0, $"{dark}: contrast {contrast:0.00}");
            // lifted, not replaced: a lifted dark red is still red
            if (dark == "#2A0A1A")
            {
                var a = p.Colors["AccentColor"];
                Assert.True(a.R > a.G && a.R > a.B, "hue kept");
            }
            // the derived colours follow the lifted accent, not the typed one
            Assert.Equal(p.Colors["AccentColor"], p.GradientStops["AccentGradientBrush"][1]);
        }
    }

    [Fact]
    public void A_readable_custom_accent_is_left_alone()
        => Assert.Equal(C("#FFD23F"), Build(accentCustom: "#FFD23F").Colors["AccentColor"]);

    [Fact]
    public void Every_accent_preset_is_readable_on_every_glass_preset_and_barely_changed()
    {
        // Most pairs are fine as they are; on the lighter or greener glasses a preset may need a hair more lightness -
        // that hair is all it gets: the accent stays recognisably itself.
        foreach (var accent in ThemePresets.Accent)
            foreach (var glass in ThemePresets.Glass)
            {
                var p = Build(accent.Id, glass.Id);
                var got = p.Colors["AccentColor"];
                Assert.True(ThemePaletteBuilder.Contrast(got, p.Colors["PanelColor"]) >= 3.0, $"{accent.Id} on {glass.Id}");

                var (h0, s0, l0) = ThemeColor.ToHsl(C(accent.Hex));
                var (h1, s1, l1) = ThemeColor.ToHsl(got);
                Assert.True(l1 >= l0 - 1e-9 && l1 - l0 <= 0.08, $"{accent.Id} on {glass.Id}: lightness {l0:0.000} -> {l1:0.000}");
                Assert.True(Math.Abs(h1 - h0) <= 2 && Math.Abs(s1 - s0) <= 0.03, $"{accent.Id} on {glass.Id}: hue/saturation moved");
            }
    }

    [Fact]
    public void Contrast_follows_wcag()
    {
        Assert.Equal(21.0, ThemePaletteBuilder.Contrast(Colors.White, Colors.Black), 3);
        Assert.Equal(1.0, ThemePaletteBuilder.Contrast(Colors.Gray, Colors.Gray), 6);
        Assert.Equal(ThemePaletteBuilder.Contrast(C("#FF7A2C"), C("#221A30")), ThemePaletteBuilder.Contrast(C("#221A30"), C("#FF7A2C")), 9);
    }

    // ---- what a theme never touches ----

    [Fact]
    public void Signal_and_text_colours_are_not_in_the_palette()
    {
        var p = Build(accent: "crimson", glass: "bordeaux");
        string[] never = { "TextColor", "DangerColor", "SuccessColor", "TextBrush", "DangerBrush", "SuccessBrush", "LineBrush", "SoftFillBrush", "SoftFillHoverBrush" };
        foreach (var key in never)
        {
            Assert.DoesNotContain(key, p.Colors.Keys);
            Assert.DoesNotContain(key, p.GradientStops.Keys);
            Assert.DoesNotContain(key, ThemePaletteKeys.All);
        }
        Assert.Equal(29, ThemePaletteKeys.All.Count);
        Assert.Equal(ThemePaletteKeys.All.Count, ThemePaletteKeys.All.Distinct().Count());
    }

    [Fact]
    public void The_derivation_formulas_are_calibrated_on_the_default_without_the_exact_pin()
    {
        // The default accent is pinned to the literal values; the formulas every OTHER accent goes through
        // must land within a hair of them, or the presets would not look like siblings of the default.
        var (light, dim, bar, active) = ThemePaletteBuilder.DeriveAccentUnpinned(C("#FF7A2C"));
        AssertNear(C("#FF9F4A"), light);
        AssertNear(C("#8F4A2E"), dim);
        AssertNear(C("#C8461A"), bar);
        AssertNear(C("#963E2C"), active);
    }

    [Fact]
    public void The_glass_formulas_are_calibrated_on_the_violet_without_the_exact_pin()
    {
        var glass = ThemePaletteBuilder.DeriveGlassUnpinned(0, 1.0, 1.0);
        AssertNear(C("#140E1F"), glass.Bg);
        AssertNear(C("#221A30"), glass.Panel);
        AssertNear(C("#2D2340"), glass.PanelHover);
        AssertNear(C("#3A2F4D"), glass.Border);
        AssertNear(C("#8E86A3"), glass.Muted);
        AssertNear(C("#5E5772"), glass.Faint);
    }

    private static void AssertNear(Color expected, Color actual, int tolerance = 2)
    {
        Assert.True(Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance && Math.Abs(expected.B - actual.B) <= tolerance,
            $"expected {expected} but was {actual}");
    }

    private static double Lightness(Color c) => (Math.Max(c.R, Math.Max(c.G, c.B)) + Math.Min(c.R, Math.Min(c.G, c.B))) / 510.0;
}
