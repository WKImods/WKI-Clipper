using System.Collections.Generic;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class ClickerHotkeyTests
{
    private const ClickerModifiers Ctrl = ClickerModifiers.Ctrl;
    private const ClickerModifiers Alt = ClickerModifiers.Alt;

    private static ClickerHotkey Key(ClickerModifiers mods, params int[] keys)
        => new() { Modifiers = mods, Keys = new List<int>(keys) };

    [Fact]
    public void The_default_is_Ctrl_Alt_Y()
    {
        var h = ClickerHotkey.Default();
        Assert.Equal(Ctrl | Alt, h.Modifiers);
        Assert.Equal(new[] { 0x59 }, h.Keys);
        Assert.True(h.IsValid());
        Assert.Equal("Strg+Alt+Y", h.ToDisplayString());
    }

    [Fact]
    public void Default_hands_out_a_new_instance_every_time()
    {
        var a = ClickerHotkey.Default();
        a.Keys.Add(0x41);
        Assert.Single(ClickerHotkey.Default().Keys);
    }

    [Fact]
    public void Clone_is_independent()
    {
        var a = Key(Ctrl, 0x41);
        var b = a.Clone();
        b.Keys.Add(0x42);
        b.Modifiers = Alt;
        Assert.Single(a.Keys);
        Assert.Equal(Ctrl, a.Modifiers);
    }

    [Theory]
    [InlineData(ClickerModifiers.Ctrl | ClickerModifiers.Alt, 0x59, "Strg+Alt+Y")]
    [InlineData(ClickerModifiers.LCtrl, 0x41, "LStrg+A")]
    [InlineData(ClickerModifiers.RCtrl | ClickerModifiers.RAlt, 0x7B, "RStrg+RAlt+F12")]
    [InlineData(ClickerModifiers.Shift, 0x31, "Umschalt+1")]
    [InlineData(ClickerModifiers.LShift | ClickerModifiers.RShift, 0x41, "LUmschalt+RUmschalt+A")]
    [InlineData(ClickerModifiers.Win, 0x44, "Win+D")]
    [InlineData(ClickerModifiers.None, ClickerVk.XButton1, "Maus 4")]
    [InlineData(ClickerModifiers.LCtrl, ClickerVk.XButton2, "LStrg+Maus 5")]
    [InlineData(ClickerModifiers.Ctrl, ClickerVk.MButton, "Strg+Maus Mitte")]
    [InlineData(ClickerModifiers.None, 0x20, "Leertaste")]
    [InlineData(ClickerModifiers.None, 0x60, "Num 0")]
    [InlineData(ClickerModifiers.None, 0x70, "F1")]
    [InlineData(ClickerModifiers.None, 0x87, "F24")]
    [InlineData(ClickerModifiers.None, 0xE8, "Taste 0xE8")]
    public void The_german_display_string(ClickerModifiers mods, int vk, string expected)
        => Assert.Equal(expected, Key(mods, vk).ToDisplayString());

    [Fact]
    public void Keys_show_in_the_order_they_were_pressed()
        => Assert.Equal("Strg+Q+W+E+R+T", Key(Ctrl, 0x51, 0x57, 0x45, 0x52, 0x54).ToDisplayString());

    [Fact]
    public void Describe_names_an_empty_hotkey()
    {
        Assert.Equal("(keine)", ClickerHotkey.Describe(null));
        Assert.Equal("(keine)", ClickerHotkey.Describe(new ClickerHotkey()));
        Assert.Equal("Strg+Alt+Y", ClickerHotkey.Describe(ClickerHotkey.Default()));
    }

    [Fact]
    public void Validity_rules()
    {
        Assert.True(Key(ClickerModifiers.None, 0x41).IsValid());
        Assert.True(Key(Ctrl, 1, 2, 3, 4, 5).IsValid());                       // five keys, mouse VKs included
        Assert.False(Key(Ctrl, 1, 2, 3, 4, 5, 6).IsValid());                   // six
        Assert.False(Key(Ctrl, 0x41, 0x41).IsValid());                         // duplicate
        Assert.False(Key(Ctrl, 0).IsValid());
        Assert.False(Key(Ctrl, 256).IsValid());
        Assert.False(Key(Ctrl, 0x105).IsValid());                              // just past the wheel codes
        Assert.False(Key(ClickerModifiers.None, ClickerVk.LControl).IsValid()); // a modifier is no main key
        Assert.True(Key(Ctrl, ClickerVk.LButton).IsValid());
        Assert.True(Key(ClickerModifiers.None, 0x41, ClickerVk.LButton).IsValid());
        Assert.False(new ClickerHotkey { Keys = null! }.IsValid());
        Assert.False(Key((ClickerModifiers)(1 << 20), 0x41).IsValid());         // undefined modifier bit
        Assert.False(Key(ClickerModifiers.None).IsValid());                    // nothing at all
    }

    [Fact]
    public void Every_input_is_a_valid_hotkey()
    {
        Assert.True(Key(Ctrl).IsValid());                                                   // modifier only
        Assert.True(Key(ClickerModifiers.LCtrl | ClickerModifiers.RAlt).IsValid());         // AltGr, sided
        Assert.True(Key(ClickerModifiers.LShift).IsValid());
        Assert.True(Key(ClickerModifiers.None, ClickerVk.LButton).IsValid());               // bare left button
        Assert.True(Key(ClickerModifiers.None, ClickerVk.RButton).IsValid());
        Assert.True(Key(ClickerModifiers.None, ClickerVk.Escape).IsValid());
        Assert.True(Key(ClickerModifiers.None, 0x08).IsValid());                            // Backspace
        Assert.True(Key(ClickerModifiers.None, 0x2C).IsValid());                            // Print Screen
        Assert.True(Key(ClickerModifiers.None, 0xAF).IsValid());                            // Volume up
        Assert.True(Key(ClickerModifiers.None, 0xFE).IsValid());
        foreach (var wheel in new[] { ClickerVk.WheelUp, ClickerVk.WheelDown, ClickerVk.WheelLeft, ClickerVk.WheelRight })
            Assert.True(Key(ClickerModifiers.None, wheel).IsValid());
        Assert.True(Key(Ctrl, ClickerVk.WheelUp, 0x41).IsValid());
    }

    [Fact]
    public void Wheel_codes_lie_outside_the_vk_range_and_count_as_mouse_input()
    {
        foreach (var wheel in new[] { ClickerVk.WheelUp, ClickerVk.WheelDown, ClickerVk.WheelLeft, ClickerVk.WheelRight })
        {
            Assert.True(wheel > 0xFF);
            Assert.True(ClickerVk.IsWheel(wheel));
            Assert.True(ClickerVk.IsMouseInput(wheel));
            Assert.False(ClickerVk.IsMouse(wheel));
        }
        Assert.True(ClickerVk.IsMouseInput(ClickerVk.LButton));
        Assert.False(ClickerVk.IsMouseInput(0x41));
        Assert.True(Key(ClickerModifiers.None, ClickerVk.WheelDown).HasMouseInput);
        Assert.True(Key(ClickerModifiers.None, ClickerVk.LButton).HasMouseInput);
        Assert.False(Key(Ctrl).HasMouseInput);
        Assert.False(Key(Ctrl, 0x41).HasMouseInput);
    }

    [Fact]
    public void Empty_and_modifier_only()
    {
        Assert.True(new ClickerHotkey().IsEmpty);
        Assert.True(new ClickerHotkey { Keys = null! }.IsEmpty);
        Assert.False(Key(Ctrl).IsEmpty);
        Assert.True(Key(Ctrl).IsModifierOnly);
        Assert.False(Key(Ctrl, 0x41).IsModifierOnly);
        Assert.False(new ClickerHotkey().IsModifierOnly);
    }

    [Theory]
    [InlineData(ClickerModifiers.Ctrl | ClickerModifiers.Shift, "Strg+Umschalt")]
    [InlineData(ClickerModifiers.LCtrl | ClickerModifiers.RAlt, "LStrg+RAlt")]
    [InlineData(ClickerModifiers.RShift, "RUmschalt")]
    [InlineData(ClickerModifiers.Win, "Win")]
    public void A_modifier_only_hotkey_shows_its_modifiers(ClickerModifiers mods, string expected)
    {
        Assert.Equal(expected, Key(mods).ToDisplayString());
        Assert.Equal(expected, ClickerHotkey.Describe(Key(mods)));
    }

    [Theory]
    [InlineData(ClickerVk.WheelUp, "Mausrad hoch")]
    [InlineData(ClickerVk.WheelDown, "Mausrad runter")]
    [InlineData(ClickerVk.WheelLeft, "Mausrad links")]
    [InlineData(ClickerVk.WheelRight, "Mausrad rechts")]
    [InlineData(ClickerVk.LButton, "Maus links")]
    [InlineData(ClickerVk.Escape, "Esc")]
    [InlineData(0x2C, "Druck")]
    [InlineData(0x13, "Pause")]
    [InlineData(0x5D, "Menütaste")]
    [InlineData(0xAF, "Lauter")]
    [InlineData(0xB3, "Wiedergabe/Pause")]
    [InlineData(0x7C, "F13")]
    [InlineData(0xE2, "<")]
    public void The_new_inputs_have_german_names(int vk, string expected)
        => Assert.Equal(expected, ClickerHotkey.KeyName(vk));

    [Fact]
    public void SameCombo_and_Overlaps_handle_modifier_only_and_wheel_hotkeys()
    {
        Assert.True(ClickerHotkey.SameCombo(Key(Ctrl | Alt), Key(Ctrl | Alt)));
        Assert.False(ClickerHotkey.SameCombo(Key(Ctrl | Alt), Key(Ctrl | Alt, 0x59)));
        Assert.True(ClickerHotkey.SameCombo(Key(ClickerModifiers.None, ClickerVk.WheelUp), Key(ClickerModifiers.None, ClickerVk.WheelUp)));
        Assert.False(ClickerHotkey.SameCombo(Key(ClickerModifiers.None, ClickerVk.WheelUp), Key(ClickerModifiers.None, ClickerVk.WheelDown)));

        // Ctrl+Alt+Y presses Ctrl+Alt on the way, so a Ctrl+Alt hotkey fires with it - strict or not.
        Assert.True(ClickerHotkey.Overlaps(Key(Ctrl | Alt), Key(Ctrl | Alt, 0x59), strict: false));
        Assert.True(ClickerHotkey.Overlaps(Key(Ctrl | Alt, 0x59), Key(Ctrl | Alt), strict: true));
        Assert.False(ClickerHotkey.Overlaps(Key(ClickerModifiers.Shift), Key(Ctrl, 0x59), strict: true));
        Assert.False(ClickerHotkey.Overlaps(new ClickerHotkey(), Key(Ctrl), strict: false));
    }

    [Fact]
    public void SameCombo_ignores_key_order_and_needs_equal_modifiers()
    {
        Assert.True(ClickerHotkey.SameCombo(Key(Ctrl, 0x41, 0x42), Key(Ctrl, 0x42, 0x41)));
        Assert.False(ClickerHotkey.SameCombo(Key(Ctrl, 0x41), Key(ClickerModifiers.LCtrl, 0x41)));
        Assert.False(ClickerHotkey.SameCombo(Key(Ctrl, 0x41), Key(Ctrl, 0x41, 0x42)));
        Assert.False(ClickerHotkey.SameCombo(null, null));
        Assert.False(ClickerHotkey.SameCombo(Key(Ctrl, 0x41), null));
    }

    [Fact]
    public void Overlaps_catches_generic_vs_sided_even_when_strict()
    {
        Assert.True(ClickerHotkey.Overlaps(Key(Ctrl, 0x59), Key(ClickerModifiers.LCtrl, 0x59), strict: true));
        Assert.True(ClickerHotkey.Overlaps(Key(ClickerModifiers.RCtrl, 0x59), Key(Ctrl, 0x59), strict: false));
        // opposite sides cannot be pressed as one chord of the same key
        Assert.False(ClickerHotkey.Overlaps(Key(ClickerModifiers.LCtrl, 0x59), Key(ClickerModifiers.RCtrl, 0x59), strict: true));
    }

    [Fact]
    public void Overlaps_needs_non_strict_for_extra_modifiers()
    {
        Assert.True(ClickerHotkey.Overlaps(Key(Ctrl, 0x59), Key(Ctrl | Alt, 0x59), strict: false));
        Assert.False(ClickerHotkey.Overlaps(Key(Ctrl, 0x59), Key(Ctrl | Alt, 0x59), strict: true));
    }

    [Fact]
    public void Overlaps_keeps_flagging_the_identical_combo_and_ignores_unrelated_ones()
    {
        Assert.True(ClickerHotkey.Overlaps(Key(Ctrl | Alt, 0x59), Key(Ctrl | Alt, 0x59), strict: true));
        Assert.True(ClickerHotkey.Overlaps(Key(Ctrl, 0x41, 0x42), Key(Ctrl, 0x42, 0x41), strict: true));
        Assert.False(ClickerHotkey.Overlaps(Key(Ctrl, 0x59), Key(Ctrl, 0x5A), strict: false));
        Assert.False(ClickerHotkey.Overlaps(null, Key(Ctrl, 0x59), strict: false));
        Assert.False(ClickerHotkey.Overlaps(Key(Ctrl, 0x59), null, strict: false));
    }

    [Fact]
    public void Repair_drops_modifier_keys_duplicates_and_a_generic_flag_next_to_a_sided_one()
    {
        var repaired = ClickerSettingsRulesAccess.Repair(
            new ClickerHotkey { Modifiers = Ctrl | ClickerModifiers.LCtrl | Alt, Keys = { 0x41, 0xA2, 0x41, 0x42, 0x11 } });

        Assert.NotNull(repaired);
        Assert.Equal(ClickerModifiers.LCtrl | Alt, repaired!.Modifiers);
        Assert.Equal(new[] { 0x41, 0x42 }, repaired.Keys);
    }

    [Fact]
    public void Repair_caps_the_keys_at_five()
    {
        var repaired = ClickerSettingsRulesAccess.Repair(Key(Ctrl, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47));
        Assert.Equal(5, repaired!.Keys.Count);
    }

    [Fact]
    public void An_unrepairable_hotkey_becomes_the_default_or_none()
    {
        Assert.True(ClickerHotkey.SameCombo(ClickerHotkey.Default(), ClickerSettingsRulesAccess.Repair(new ClickerHotkey())));
        Assert.True(ClickerHotkey.SameCombo(ClickerHotkey.Default(), ClickerSettingsRulesAccess.Repair(null)));
        Assert.True(ClickerHotkey.SameCombo(ClickerHotkey.Default(), ClickerSettingsRulesAccess.Repair(Key(ClickerModifiers.None, 0x300))));
        Assert.Null(ClickerSettingsRules.NormalizeHotkey(new ClickerHotkey(), required: false));
        Assert.Null(ClickerSettingsRules.NormalizeHotkey(Key(ClickerModifiers.None, ClickerVk.LControl), required: false));
        Assert.Null(ClickerSettingsRules.NormalizeHotkey(null, required: false));
    }

    [Fact]
    public void The_repair_keeps_the_new_kinds_of_hotkeys()
    {
        foreach (var h in new[]
                 {
                     Key(Ctrl), Key(ClickerModifiers.LCtrl | ClickerModifiers.RAlt), Key(ClickerModifiers.None, ClickerVk.LButton),
                     Key(ClickerModifiers.None, ClickerVk.WheelDown), Key(ClickerModifiers.None, ClickerVk.Escape), Key(Ctrl, ClickerVk.WheelLeft, 0x41)
                 })
        {
            Assert.True(ClickerHotkey.SameCombo(h, ClickerSettingsRulesAccess.Repair(h)));
            Assert.True(ClickerHotkey.SameCombo(h, ClickerSettingsRules.NormalizeHotkey(h, required: false)));
        }
    }

    [Fact]
    public void The_repair_drops_codes_outside_the_vk_and_wheel_range()
    {
        var repaired = ClickerSettingsRulesAccess.Repair(Key(Ctrl, 0x41, 0x105, 0x101, -3, 1000));
        Assert.Equal(new[] { 0x41, ClickerVk.WheelUp }, repaired!.Keys);
    }

    [Fact]
    public void Undefined_modifier_bits_are_dropped_by_the_repair()
    {
        var repaired = ClickerSettingsRulesAccess.Repair(Key((ClickerModifiers)((1 << 20) | (int)Ctrl), 0x41));
        Assert.Equal(Ctrl, repaired!.Modifiers);
    }
}

/// <summary>Tiny wrapper so the repair tests read as "repair a required hotkey".</summary>
internal static class ClickerSettingsRulesAccess
{
    public static ClickerHotkey? Repair(ClickerHotkey? h) => ClickerSettingsRules.NormalizeHotkey(h, required: true);
}

/// <summary>The English wording (the app language is process-wide, so these run alone).</summary>
[Collection("Language")]
public sealed class ClickerHotkeyEnglishTests
{
    [Fact]
    public void The_english_display_strings()
    {
        using var _ = TestLanguage.English();

        Assert.Equal("Ctrl+Alt+Y", ClickerHotkey.Default().ToDisplayString());
        Assert.Equal("LCtrl+Mouse 4", new ClickerHotkey { Modifiers = ClickerModifiers.LCtrl, Keys = { ClickerVk.XButton1 } }.ToDisplayString());
        Assert.Equal("RShift+Space", new ClickerHotkey { Modifiers = ClickerModifiers.RShift, Keys = { 0x20 } }.ToDisplayString());
        Assert.Equal("(none)", ClickerHotkey.Describe(null));
        Assert.Equal("Key 0xE8", ClickerHotkey.KeyName(0xE8));
        Assert.Equal("Wheel up", ClickerHotkey.KeyName(ClickerVk.WheelUp));
        Assert.Equal("Wheel down", ClickerHotkey.KeyName(ClickerVk.WheelDown));
        Assert.Equal("Wheel left", ClickerHotkey.KeyName(ClickerVk.WheelLeft));
        Assert.Equal("Wheel right", ClickerHotkey.KeyName(ClickerVk.WheelRight));
        Assert.Equal("Ctrl+Shift", new ClickerHotkey { Modifiers = ClickerModifiers.Ctrl | ClickerModifiers.Shift }.ToDisplayString());
        Assert.Equal("Print Screen", ClickerHotkey.KeyName(0x2C));
        Assert.Equal("Volume up", ClickerHotkey.KeyName(0xAF));
    }
}
