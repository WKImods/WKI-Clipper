using System.Collections.Generic;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// The text that tells which keys are refused while a long clip is written: the user's own bindings (keyboard and
/// controller), not the defaults F8/F9, and nothing for a key that is not set.
/// </summary>
public sealed class SaveKeyHintsTests
{
    private static AppSettings Settings(HotkeyBinding? replay = null, HotkeyBinding? gif = null, bool gamepad = true,
        Dictionary<string, ushort>? combos = null)
    {
        var s = new AppSettings();
        if (replay is not null) s.Hotkeys[HotkeyActions.SaveReplay] = replay;
        if (gif is not null) s.Hotkeys[HotkeyActions.SaveGif] = gif;
        s.Gamepad.Enabled = gamepad;
        if (combos is not null) s.Gamepad.Bindings = combos;
        return s;
    }

    private static HotkeyBinding Key(uint vk, HotkeyModifier mods = 0) => new() { Key = vk, Modifiers = mods };
    private static readonly HotkeyBinding Unbound = new() { Key = 0 };
    private static readonly Dictionary<string, ushort> NoCombos = new();

    [Fact]
    public void The_defaults_name_both_keys_and_the_controller_combo()
    {
        var s = new AppSettings();

        Assert.Equal(new[] { "F9", "View + Menu", "F8" }, SaveKeyHints.Bindings(s));
    }

    [Fact]
    public void Rebound_keys_are_named_instead_of_F8_and_F9()
    {
        var s = Settings(replay: Key(0x7A), gif: Key(0x77, HotkeyModifier.Control), combos: NoCombos);   // F11, Ctrl+F8

        Assert.Equal(new[] { "F11", "Strg + F8" }, SaveKeyHints.Bindings(s));
        Assert.Equal("F11 / Strg + F8 sind bis zum Ende gesperrt.", SaveKeyHints.BlockedUntilFinished(s));
        Assert.Equal("F11 / Strg + F8 sind solange gesperrt.", SaveKeyHints.BlockedMeanwhile(s));
        Assert.DoesNotContain("F9", SaveKeyHints.BlockedUntilFinished(s));
    }

    [Fact]
    public void An_unbound_key_is_left_out()
    {
        var s = Settings(replay: Key(0x7A), gif: Unbound, combos: NoCombos);

        Assert.Equal(new[] { "F11" }, SaveKeyHints.Bindings(s));
        Assert.Equal("F11 ist bis zum Ende gesperrt.", SaveKeyHints.BlockedUntilFinished(s));   // one key: "ist", not "sind"
        Assert.Equal("F11 ist solange gesperrt.", SaveKeyHints.BlockedMeanwhile(s));
    }

    [Fact]
    public void A_missing_binding_counts_as_unbound()
    {
        var s = Settings(combos: NoCombos);
        s.Hotkeys.Remove(HotkeyActions.SaveGif);

        Assert.Equal(new[] { "F9" }, SaveKeyHints.Bindings(s));
    }

    [Fact]
    public void With_nothing_bound_the_text_is_neutral()
    {
        var s = Settings(replay: Unbound, gif: Unbound, combos: NoCombos);

        Assert.Empty(SaveKeyHints.Bindings(s));
        Assert.Equal("Clip-/GIF-Speichern ist bis zum Ende gesperrt.", SaveKeyHints.BlockedUntilFinished(s));
        Assert.Equal("Clip-/GIF-Speichern ist solange gesperrt.", SaveKeyHints.BlockedMeanwhile(s));
    }

    [Fact]
    public void A_controller_combo_is_named_while_the_controller_is_on_and_valid()
    {
        var combo = (ushort)(GamepadButtons.LB | GamepadButtons.RB);
        var on = Settings(replay: Unbound, gif: Unbound, combos: new() { [HotkeyActions.SaveGif] = combo });
        var off = Settings(replay: Unbound, gif: Unbound, gamepad: false, combos: new() { [HotkeyActions.SaveGif] = combo });
        var single = Settings(replay: Unbound, gif: Unbound, combos: new() { [HotkeyActions.SaveGif] = GamepadButtons.A });   // not a valid combo

        Assert.Equal(new[] { "LB + RB" }, SaveKeyHints.Bindings(on));
        Assert.Empty(SaveKeyHints.Bindings(off));       // polling is off: the combo does nothing
        Assert.Empty(SaveKeyHints.Bindings(single));
    }

    [Fact]
    public void The_same_text_is_not_listed_twice()
    {
        var s = Settings(replay: Key(0x78), gif: Key(0x78), combos: NoCombos);   // both on F9 (a hand-edited file)

        Assert.Equal(new[] { "F9" }, SaveKeyHints.Bindings(s));
    }
}

/// <summary>The English wording (the app language is process-wide, so these run alone).</summary>
[Collection("Language")]
public sealed class SaveKeyHintsEnglishTests
{
    [Fact]
    public void The_english_sentences_agree_with_the_number_of_keys()
    {
        using var _ = TestLanguage.English();
        var two = new AppSettings();
        two.Gamepad.Enabled = false;
        var one = new AppSettings();
        one.Gamepad.Enabled = false;
        one.Hotkeys[HotkeyActions.SaveGif] = new HotkeyBinding { Key = 0 };
        var none = new AppSettings();
        none.Gamepad.Enabled = false;
        none.Hotkeys[HotkeyActions.SaveGif] = new HotkeyBinding { Key = 0 };
        none.Hotkeys[HotkeyActions.SaveReplay] = new HotkeyBinding { Key = 0 };

        Assert.Equal("F9 / F8 are blocked until it finishes.", SaveKeyHints.BlockedUntilFinished(two));
        Assert.Equal("F9 / F8 are blocked meanwhile.", SaveKeyHints.BlockedMeanwhile(two));
        Assert.Equal("F9 is blocked until it finishes.", SaveKeyHints.BlockedUntilFinished(one));
        Assert.Equal("F9 is blocked meanwhile.", SaveKeyHints.BlockedMeanwhile(one));
        Assert.Equal("Clip/GIF saving is blocked until it finishes.", SaveKeyHints.BlockedUntilFinished(none));
        Assert.Equal("Clip/GIF saving is blocked meanwhile.", SaveKeyHints.BlockedMeanwhile(none));
    }
}
