using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Controller settings section, button names and the validity rules for stored combos.</summary>
public sealed class GamepadSettingsAndButtonsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_gamepad_set_" + Guid.NewGuid().ToString("N"));

    public GamepadSettingsAndButtonsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- button bits ----

    [Fact]
    public void Button_bits_match_the_xinput_header()
    {
        Assert.Equal(0x0001, GamepadButtons.Up);
        Assert.Equal(0x0002, GamepadButtons.Down);
        Assert.Equal(0x0004, GamepadButtons.Left);
        Assert.Equal(0x0008, GamepadButtons.Right);
        Assert.Equal(0x0010, GamepadButtons.Menu);     // XINPUT_GAMEPAD_START
        Assert.Equal(0x0020, GamepadButtons.View);     // XINPUT_GAMEPAD_BACK
        Assert.Equal(0x0040, GamepadButtons.LS);
        Assert.Equal(0x0080, GamepadButtons.RS);
        Assert.Equal(0x0100, GamepadButtons.LB);
        Assert.Equal(0x0200, GamepadButtons.RB);
        Assert.Equal(0x1000, GamepadButtons.A);
        Assert.Equal(0x2000, GamepadButtons.B);
        Assert.Equal(0x4000, GamepadButtons.X);
        Assert.Equal(0x8000, GamepadButtons.Y);
    }

    [Fact]
    public void The_valid_mask_covers_exactly_the_fourteen_buttons()
    {
        Assert.Equal(14, System.Numerics.BitOperations.PopCount(GamepadButtons.ValidMask));
        Assert.Equal(0, GamepadButtons.ValidMask & 0x0400);   // reserved / guide
        Assert.Equal(0, GamepadButtons.ValidMask & 0x0800);
    }

    // ---- text ----

    [Fact]
    public void The_default_combo_reads_view_plus_menu()
    {
        Assert.Equal("View + Menu", GamepadButtons.Describe(GamepadButtons.View | GamepadButtons.Menu));
    }

    [Fact]
    public void Describe_uses_a_fixed_order_independent_of_how_the_mask_was_built()
    {
        Assert.Equal("LB + RB", GamepadButtons.Describe(GamepadButtons.RB | GamepadButtons.LB));
        Assert.Equal("LB + View + A", GamepadButtons.Describe(GamepadButtons.A | GamepadButtons.View | GamepadButtons.LB));
        Assert.Equal("LS + RS", GamepadButtons.Describe(GamepadButtons.RS | GamepadButtons.LS));
        Assert.Equal("D-Pad ↑ + Y", GamepadButtons.Describe(GamepadButtons.Y | GamepadButtons.Up));
    }

    [Theory]
    [InlineData(GamepadButtons.Up, "D-Pad ↑")]
    [InlineData(GamepadButtons.Down, "D-Pad ↓")]
    [InlineData(GamepadButtons.Left, "D-Pad ←")]
    [InlineData(GamepadButtons.Right, "D-Pad →")]
    [InlineData(GamepadButtons.A, "A")]
    [InlineData(GamepadButtons.B, "B")]
    [InlineData(GamepadButtons.X, "X")]
    [InlineData(GamepadButtons.Y, "Y")]
    public void Single_buttons_have_their_name(ushort mask, string expected)
    {
        Assert.Equal(expected, GamepadButtons.Describe(mask));
    }

    [Fact]
    public void An_empty_or_unknown_mask_reads_as_a_dash()
    {
        Assert.Equal("—", GamepadButtons.Describe(0));
        Assert.Equal("—", GamepadButtons.Describe(0x0400));   // only a bit that is not a known button
    }

    // ---- validity ----

    [Theory]
    [InlineData(0, 0)]
    [InlineData((int)GamepadButtons.A, 1)]
    [InlineData(GamepadButtons.View | GamepadButtons.Menu, 2)]
    [InlineData(0xF3FF, 14)]
    public void Buttons_are_counted(int mask, int expected)
    {
        Assert.Equal(expected, GamepadButtons.Count((ushort)mask));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData((int)GamepadButtons.A, false)]                                      // one button is too easy to hit in a game
    [InlineData(GamepadButtons.View | GamepadButtons.Menu, true)]
    [InlineData(GamepadButtons.LB | GamepadButtons.RB | GamepadButtons.A, true)]
    [InlineData(GamepadButtons.A | 0x0400, false)]                             // contains a bit that is not a button
    public void A_stored_combo_needs_two_known_buttons(int mask, bool valid)
    {
        Assert.Equal(valid, GamepadButtons.IsValidCombo((ushort)mask));
    }

    [Fact]
    public void Sanitize_drops_unusable_entries_and_duplicate_masks()
    {
        var raw = new Dictionary<string, ushort>
        {
            [HotkeyActions.SaveReplay] = GamepadButtons.View | GamepadButtons.Menu,
            [HotkeyActions.Screenshot] = GamepadButtons.A,                          // one button
            [HotkeyActions.ToggleRecording] = 0,                                    // empty
            ["Zeta"] = GamepadButtons.View | GamepadButtons.Menu,                   // same mask as SaveReplay
            ["Alpha"] = GamepadButtons.LB | GamepadButtons.RB,
        };

        var clean = GamepadButtons.Sanitize(raw);

        Assert.Equal(2, clean.Count);
        Assert.Equal(GamepadButtons.View | GamepadButtons.Menu, clean[HotkeyActions.SaveReplay]);
        Assert.Equal(GamepadButtons.LB | GamepadButtons.RB, clean["Alpha"]);
    }

    [Fact]
    public void Sanitize_keeps_the_ordinally_first_action_of_a_duplicate_mask()
    {
        var raw = new Dictionary<string, ushort>
        {
            ["Zeta"] = GamepadButtons.LB | GamepadButtons.RB,
            ["Alpha"] = GamepadButtons.LB | GamepadButtons.RB,
        };
        var clean = GamepadButtons.Sanitize(raw);
        var only = Assert.Single(clean);
        Assert.Equal("Alpha", only.Key);
    }

    [Fact]
    public void Sanitize_tolerates_a_missing_map()
    {
        Assert.Empty(GamepadButtons.Sanitize(null));
    }

    // ---- settings defaults ----

    [Fact]
    public void Controller_defaults()
    {
        var g = new AppSettings().Gamepad;
        Assert.True(g.Enabled);
        Assert.Equal(500, g.HoldMs);
        var one = Assert.Single(g.Bindings);
        Assert.Equal(HotkeyActions.SaveReplay, one.Key);
        Assert.Equal(GamepadButtons.View | GamepadButtons.Menu, one.Value);
    }

    // Output folders point into the temp dir so Load()'s EnsureOutputDirs never touches the real Videos folder.
    private void WriteSettingsFile(string fragment)
    {
        string Esc(string p) => JsonSerializer.Serialize(p);
        var json = "{ \"SchemaVersion\": " + SettingsService.CurrentSchemaVersion + ", " +
                   "\"Output\": { \"ClipsFolder\": " + Esc(Path.Combine(_dir, "c")) +
                   ", \"ScreenshotsFolder\": " + Esc(Path.Combine(_dir, "s")) +
                   ", \"BufferFolder\": " + Esc(Path.Combine(_dir, "b")) + " }" +
                   fragment + " }";
        File.WriteAllText(Path.Combine(_dir, "settings.json"), json);
    }

    [Fact]
    public void A_settings_file_without_the_section_loads_the_defaults_and_keeps_its_schema_version()
    {
        WriteSettingsFile("");
        var loaded = new SettingsService(_dir).Load();

        Assert.Equal(SettingsService.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.NotNull(loaded.Gamepad);
        Assert.True(loaded.Gamepad.Enabled);
        Assert.Equal(500, loaded.Gamepad.HoldMs);
        Assert.Equal(GamepadButtons.View | GamepadButtons.Menu, loaded.Gamepad.Bindings[HotkeyActions.SaveReplay]);
    }

    [Fact]
    public void An_explicit_null_section_loads_the_defaults()
    {
        WriteSettingsFile(", \"Gamepad\": null");
        var loaded = new SettingsService(_dir).Load();
        Assert.NotNull(loaded.Gamepad);
        Assert.True(loaded.Gamepad.Enabled);
        Assert.Single(loaded.Gamepad.Bindings);
    }

    [Fact]
    public void An_explicit_null_binding_map_loads_as_empty_not_as_the_default()
    {
        WriteSettingsFile(", \"Gamepad\": { \"HoldMs\": 800, \"Bindings\": null }");
        var loaded = new SettingsService(_dir).Load();
        Assert.Equal(800, loaded.Gamepad.HoldMs);
        Assert.NotNull(loaded.Gamepad.Bindings);
        Assert.Empty(loaded.Gamepad.Bindings);
    }

    [Fact]
    public void A_partial_section_keeps_the_other_defaults()
    {
        WriteSettingsFile(", \"Gamepad\": { \"Enabled\": false }");
        var loaded = new SettingsService(_dir).Load();
        Assert.False(loaded.Gamepad.Enabled);
        Assert.Equal(500, loaded.Gamepad.HoldMs);
        Assert.Equal(GamepadButtons.View | GamepadButtons.Menu, loaded.Gamepad.Bindings[HotkeyActions.SaveReplay]);
    }

    [Fact]
    public void A_removed_binding_stays_removed_after_a_save_and_load()
    {
        WriteSettingsFile("");
        var svc = new SettingsService(_dir);
        svc.Load();
        svc.Current.Gamepad.Bindings.Remove(HotkeyActions.SaveReplay);
        svc.Save();

        var again = new SettingsService(_dir).Load();
        Assert.Empty(again.Gamepad.Bindings);
    }

    [Fact]
    public void The_section_survives_a_save_and_load()
    {
        WriteSettingsFile("");
        var svc = new SettingsService(_dir);
        svc.Load();
        svc.Current.Gamepad.Enabled = false;
        svc.Current.Gamepad.HoldMs = 900;
        svc.Current.Gamepad.Bindings[HotkeyActions.Screenshot] = GamepadButtons.LB | GamepadButtons.RB;
        svc.Save();

        var again = new SettingsService(_dir).Load();
        Assert.False(again.Gamepad.Enabled);
        Assert.Equal(900, again.Gamepad.HoldMs);
        Assert.Equal(GamepadButtons.LB | GamepadButtons.RB, again.Gamepad.Bindings[HotkeyActions.Screenshot]);
        Assert.Equal(GamepadButtons.View | GamepadButtons.Menu, again.Gamepad.Bindings[HotkeyActions.SaveReplay]);
        Assert.Equal(SettingsService.CurrentSchemaVersion, again.SchemaVersion);
    }
}
