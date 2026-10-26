using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.Views;
using Xunit;
using Color = System.Windows.Media.Color;
using Size = System.Windows.Size;

namespace WKI_Clipper.Tests;

/// <summary>
/// The colour picker: what a click on a swatch, a typed hex, a slider and "Standard" do to the settings and to the theme - pure
/// logic in <see cref="ThemePickerState"/> - and the window that shows it, built and rendered offscreen (never shown).
/// </summary>
[Collection("WpfApplication")]   // only one System.Windows.Application per process
public sealed class ThemePickerTests
{
    private static Color C(string hex) => (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);

    /// <summary>The picker's world: settings, and a log of what was applied and how often the file was saved.</summary>
    private sealed class World
    {
        public ThemeSettings Settings { get; } = new();
        public List<ThemePalette> Applied { get; } = new();
        public int Saves { get; private set; }
        public ThemePickerState State { get; }
        public World() => State = new ThemePickerState(Settings, p => Applied.Add(p), () => Saves++);
    }

    // ---- the lists ----

    [Fact]
    public void Picker_lists_all_presets_per_tab()
    {
        var w = new World();
        Assert.Equal(ThemePresets.Accent, w.State.Presets(ThemeTab.Accent));
        Assert.Equal(ThemePresets.Glass, w.State.Presets(ThemeTab.Glass));
        Assert.Equal(8, w.State.Presets(ThemeTab.Accent).Count);
        Assert.Equal(6, w.State.Presets(ThemeTab.Glass).Count);

        // the window shows them all, with the names of the design in the current language
        var r = WpfTestSupport.OnSta(() =>
        {
            WpfTestSupport.EnsureTheme();
            var window = new ThemePickerWindow(new World().State);
            try { return (accent: window.SwatchNames(ThemeTab.Accent), glass: window.SwatchNames(ThemeTab.Glass), tabs: window.TabNames()); }
            finally { window.Close(); }
        });
        Assert.Equal(new[] { "WKI-Orange", "Episches Blau", "Karmesinrot", "Smaragd", "Königsviolett", "Gold", "Eisblau", "Magenta" }, r.accent);
        Assert.Equal(new[] { "Violett", "Nachtblau", "Graphit", "Bordeaux", "Waldgrün", "Schwarz (OLED)" }, r.glass);
        Assert.Equal(new[] { "Akzent", "Glas" }, r.tabs);
    }

    [Fact]
    public void The_current_choice_is_what_the_settings_say()
    {
        var w = new World();
        Assert.Equal("wki-orange", w.State.SelectedPresetId(ThemeTab.Accent));
        Assert.Equal("violet", w.State.SelectedPresetId(ThemeTab.Glass));
        Assert.Equal("#FF7A2C", w.State.CurrentHex(ThemeTab.Accent));

        w.Settings.AccentPreset = "gold";
        w.Settings.GlassCustom = "#204060";
        Assert.Equal("gold", w.State.SelectedPresetId(ThemeTab.Accent));
        Assert.Null(w.State.SelectedPresetId(ThemeTab.Glass));          // a custom colour is active: no preset is the choice
        Assert.Equal("#204060", w.State.CurrentHex(ThemeTab.Glass));
        Assert.Equal("#204060", w.State.CustomHex(ThemeTab.Glass));
        Assert.Null(w.State.CustomHex(ThemeTab.Accent));
    }

    // ---- choosing ----

    [Fact]
    public void Choosing_preset_saves_and_applies()
    {
        var w = new World();
        w.Settings.AccentCustom = "#33CC66";                  // an earlier custom colour must not outlive the choice

        w.State.ChoosePreset(ThemeTab.Accent, "crimson");

        Assert.Equal("crimson", w.Settings.AccentPreset);
        Assert.Null(w.Settings.AccentCustom);
        Assert.Equal(1, w.Saves);
        var palette = Assert.Single(w.Applied);
        Assert.Equal(ThemePaletteBuilder.Build(w.Settings).Colors, palette.Colors);
        Assert.Equal(C("#DC143C"), palette.Colors["AccentColor"]);

        w.State.ChoosePreset(ThemeTab.Glass, "bordeaux");
        Assert.Equal("bordeaux", w.Settings.GlassPreset);
        Assert.Equal("crimson", w.Settings.AccentPreset);     // the other tab is untouched
        Assert.Equal((2, 2), (w.Saves, w.Applied.Count));
    }

    [Fact]
    public void An_unknown_preset_changes_nothing()
    {
        var w = new World();
        w.State.ChoosePreset(ThemeTab.Accent, "no-such-preset");
        w.State.ChoosePreset(ThemeTab.Glass, "");
        Assert.Equal("wki-orange", w.Settings.AccentPreset);
        Assert.Equal("violet", w.Settings.GlassPreset);
        Assert.Equal((0, 0), (w.Saves, w.Applied.Count));
    }

    // ---- the hex field ----

    [Theory]
    [InlineData("#12")]
    [InlineData("zzzzzz")]
    [InlineData("")]
    [InlineData("#1234567")]
    [InlineData(null)]
    public void Hex_field_rejects_invalid_and_keeps_previous(string? typed)
    {
        var w = new World();
        w.Settings.AccentCustom = "#33CC66";

        bool ok = w.State.ApplyHex(ThemeTab.Accent, typed, out string shown);

        Assert.False(ok);
        Assert.Equal("#33CC66", shown);                        // the field goes back to what is in force
        Assert.Equal("#33CC66", w.Settings.AccentCustom);
        Assert.Equal((0, 0), (w.Saves, w.Applied.Count));      // nothing was applied or saved

        // with no custom colour yet, the field goes back to the colour of the chosen preset
        var fresh = new World();
        Assert.False(fresh.State.ApplyHex(ThemeTab.Glass, "nope", out string back));
        Assert.Equal("#2D2340", back);
    }

    [Fact]
    public void A_valid_hex_becomes_the_custom_colour_applied_and_saved()
    {
        var w = new World();

        Assert.True(w.State.ApplyHex(ThemeTab.Accent, "  ff8800 ", out string shown));

        Assert.Equal("#FF8800", shown);
        Assert.Equal("#FF8800", w.Settings.AccentCustom);
        Assert.Equal(1, w.Saves);
        Assert.Equal(C("#FF8800"), Assert.Single(w.Applied).Colors["AccentColor"]);
        Assert.Null(w.State.SelectedPresetId(ThemeTab.Accent));

        // typing the colour that is already in force is not a new theme
        Assert.True(w.State.ApplyHex(ThemeTab.Accent, "#FF8800", out _));
        Assert.Equal((1, 1), (w.Saves, w.Applied.Count));
    }

    [Fact]
    public void Sliders_make_a_hex_and_a_hex_gives_its_sliders_back()
    {
        var w = new World();
        string red = w.State.HexFromHueSaturation(ThemeTab.Accent, 0, 1.0);
        string green = w.State.HexFromHueSaturation(ThemeTab.Accent, 120, 1.0);
        Assert.NotEqual(red, green);
        Assert.Equal(red, ThemePaletteBuilder.NormalizeHex(red));
        var (hue, sat) = w.State.HueSaturationOf(green);
        Assert.Equal(120, hue, 0);
        Assert.Equal(1.0, sat, 2);

        // a glass tint is dark: the same sliders give a much darker colour there than on the accent tab
        var accentTint = ThemeColor.ToHsl(C(w.State.HexFromHueSaturation(ThemeTab.Accent, 220, 0.4)));
        var glassTint = ThemeColor.ToHsl(C(w.State.HexFromHueSaturation(ThemeTab.Glass, 220, 0.4)));
        Assert.True(glassTint.L < accentTint.L / 2);
        Assert.InRange(glassTint.H, 218, 222);

        // and a grey has a hue of 0 and no saturation
        Assert.Equal((0.0, 0.0), w.State.HueSaturationOf("#808080"));
        Assert.Equal((0.0, 0.0), w.State.HueSaturationOf("garbage"));
    }

    // ---- "Standard" ----

    [Fact]
    public void Standard_resets_only_its_tab()
    {
        var w = new World();
        w.Settings.AccentPreset = "gold";
        w.Settings.AccentCustom = "#33CC66";
        w.Settings.GlassPreset = "forest";
        w.Settings.GlassCustom = "#204060";

        w.State.Reset(ThemeTab.Accent);

        Assert.Equal("wki-orange", w.Settings.AccentPreset);
        Assert.Null(w.Settings.AccentCustom);
        Assert.Equal("forest", w.Settings.GlassPreset);          // the glass tab keeps its choice, custom colour included
        Assert.Equal("#204060", w.Settings.GlassCustom);
        Assert.Equal(1, w.Saves);
        Assert.Equal(C("#FF7A2C"), Assert.Single(w.Applied).Colors["AccentColor"]);

        w.State.Reset(ThemeTab.Glass);
        Assert.Equal("violet", w.Settings.GlassPreset);
        Assert.Null(w.Settings.GlassCustom);
        Assert.Equal(2, w.Saves);
        // both tabs default: exactly today's theme
        Assert.Equal(ThemePaletteBuilder.Build(new ThemeSettings()).Colors, w.Applied[^1].Colors);
    }

    // ---- the window ----

    private static T OnSta<T>(Func<T> body) => WpfTestSupport.OnSta(() => { WpfTestSupport.EnsureTheme(); return body(); });

    [Fact]
    public void Picker_renders_both_tabs()
    {
        var r = OnSta(() =>
        {
            var window = new ThemePickerWindow(new World().State);
            try
            {
                var renders = new List<(int W, int H, int Drawn, byte[] Pixels)>();
                foreach (var tab in new[] { ThemeTab.Accent, ThemeTab.Glass })
                {
                    window.SelectTab(tab);
                    var root = (FrameworkElement)window.Content;
                    root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    root.Arrange(new Rect(root.DesiredSize));
                    root.UpdateLayout();

                    int w = (int)Math.Ceiling(root.ActualWidth), h = (int)Math.Ceiling(root.ActualHeight);
                    var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(root);
                    var pixels = new byte[w * h * 4];
                    bmp.CopyPixels(pixels, w * 4, 0);
                    int drawn = 0;
                    for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] > 0) drawn++;
                    renders.Add((w, h, drawn, pixels));
                }
                return (renders, active: window.ActiveTab);
            }
            finally { window.Close(); }
        });

        Assert.Equal(2, r.renders.Count);
        foreach (var render in r.renders)
        {
            Assert.InRange(render.W, 260, 520);
            Assert.InRange(render.H, 200, 700);
            Assert.True(render.Drawn > render.W * render.H / 2, "the glass surface is drawn");
        }
        Assert.False(r.renders[0].Pixels.SequenceEqual(r.renders[1].Pixels), "the two tabs look different");
        Assert.Equal(ThemeTab.Glass, r.active);
    }

    [Fact]
    public void Clicking_a_swatch_in_the_window_goes_through_the_state()
    {
        var w = new World();
        var shown = OnSta(() =>
        {
            var window = new ThemePickerWindow(w.State);
            try
            {
                window.ClickSwatch(ThemeTab.Accent, "emerald");
                var accentSelected = window.SelectedSwatch(ThemeTab.Accent);
                window.ClickSwatch(ThemeTab.Glass, "night-blue");
                return (accentSelected, glass: window.SelectedSwatch(ThemeTab.Glass), hex: window.HexText(ThemeTab.Accent));
            }
            finally { window.Close(); }
        });

        Assert.Equal("emerald", w.Settings.AccentPreset);
        Assert.Equal("night-blue", w.Settings.GlassPreset);
        Assert.Equal(2, w.Saves);
        Assert.Equal("emerald", shown.accentSelected);       // the ring moved to the swatch
        Assert.Equal("night-blue", shown.glass);
        Assert.Equal("#14C98B", shown.hex);                  // and the hex field shows the colour of the choice
    }

    [Fact]
    public void Typing_a_bad_hex_in_the_window_puts_the_old_one_back()
    {
        var w = new World();
        var r = OnSta(() =>
        {
            var window = new ThemePickerWindow(w.State);
            try
            {
                string before = window.HexText(ThemeTab.Accent);
                window.TypeHex(ThemeTab.Accent, "#XYZ");
                var afterBad = (window.HexText(ThemeTab.Accent), window.HexNoteShown(ThemeTab.Accent));
                window.TypeHex(ThemeTab.Accent, "#4C7DFF");
                return (before, afterBad, afterGood: (window.HexText(ThemeTab.Accent), window.HexNoteShown(ThemeTab.Accent)));
            }
            finally { window.Close(); }
        });

        Assert.Equal("#FF7A2C", r.before);
        Assert.Equal(("#FF7A2C", true), r.afterBad);          // reverted, and the field says why
        Assert.Equal(("#4C7DFF", false), r.afterGood);
        Assert.Equal("#4C7DFF", w.Settings.AccentCustom);
        Assert.Equal(1, w.Saves);
    }

    [Fact]
    public void The_standard_button_of_a_tab_resets_that_tab_in_the_window()
    {
        var w = new World();
        w.Settings.AccentPreset = "gold";
        w.Settings.GlassPreset = "forest";
        OnSta(() =>
        {
            var window = new ThemePickerWindow(w.State);
            try { window.ClickStandard(ThemeTab.Glass); return 0; }
            finally { window.Close(); }
        });
        Assert.Equal("violet", w.Settings.GlassPreset);
        Assert.Equal("gold", w.Settings.AccentPreset);
    }

    /// <summary>Lets the dispatcher run for a while - a timer that was left behind would fire in here.</summary>
    private static void Pump(int milliseconds)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void Closing_the_picker_settles_a_slider_move_that_is_still_waiting_and_leaves_no_timer_behind()
    {
        var w = new World();
        var r = OnSta(() =>
        {
            var window = new ThemePickerWindow(w.State);
            window.MoveSliders(ThemeTab.Accent, 200, 0.9);          // the user lets go of the slider ...
            bool waiting = window.RestPending(ThemeTab.Accent);
            window.Close();                                         // ... and the board is closed within the 350 ms
            var afterClose = (applied: w.Applied.Count, pending: window.RestPending(ThemeTab.Accent));
            Pump(600);                                              // nothing may fire any more, long after the picker is gone
            return (waiting, afterClose, applied: w.Applied.Count, saves: w.Saves);
        });

        Assert.True(r.waiting);
        Assert.Equal((1, false), r.afterClose);          // taken over while closing - the colour the user saw is not lost - and no timer left
        Assert.Equal(1, r.applied);                      // and not a second time afterwards
        Assert.Equal(1, r.saves);
        Assert.NotNull(w.Settings.AccentCustom);
    }

    [Fact]
    public void Closing_a_picker_with_nothing_pending_applies_nothing()
    {
        var w = new World();
        OnSta(() =>
        {
            var window = new ThemePickerWindow(w.State);
            window.Close();
            Pump(500);
            return 0;
        });
        Assert.Equal((0, 0), (w.Applied.Count, w.Saves));
    }

    [Fact]
    public void A_typed_colour_is_applied_once_when_the_picker_closes_not_again_when_the_field_loses_the_focus_in_the_closing_window()
    {
        var w = new World();
        var r = OnSta(() =>
        {
            var window = new ThemePickerWindow(w.State);
            window.SetHexWithoutCommit(ThemeTab.Accent, "#4C7DFF");     // typed, Enter not pressed
            window.Close();
            int afterClose = w.Applied.Count;
            window.LoseHexFocus(ThemeTab.Accent);                       // what the closing window does to the field's focus
            Pump(500);
            return (afterClose, applied: w.Applied.Count);
        });

        Assert.Equal((1, 1), r);
        Assert.Equal("#4C7DFF", w.Settings.AccentCustom);
    }

    [Fact]
    public void A_picker_that_is_closed_and_opened_again_is_not_disturbed_by_the_old_ones_timer()
    {
        var w = new World();
        var r = OnSta(() =>
        {
            var old = new ThemePickerWindow(w.State);
            old.MoveSliders(ThemeTab.Glass, 40, 0.5);
            old.Close();
            var fresh = new ThemePickerWindow(w.State);
            string before = fresh.HexText(ThemeTab.Glass);
            Pump(600);
            string after = fresh.HexText(ThemeTab.Glass);
            fresh.Close();
            return (before, after);
        });
        Assert.Equal(r.before, r.after);
    }

    [Fact]
    public void Escape_asks_the_host_to_close_the_picker()
    {
        int closes = 0;
        OnSta(() =>
        {
            var window = new ThemePickerWindow(new World().State);
            try
            {
                window.CloseRequested += () => closes++;
                window.PressEscape();
                return 0;
            }
            finally { window.Close(); }
        });
        Assert.Equal(1, closes);
    }
}
