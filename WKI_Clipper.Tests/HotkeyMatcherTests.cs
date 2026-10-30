using System.Collections.Generic;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class HotkeyMatcherTests
{
    private const int Y = 0x59, A = 0x41, B = 0x42, C = 0x43, D = 0x44, E = 0x45;

    private static ClickerHotkey Hk(ClickerModifiers mods, params int[] keys)
        => new() { Modifiers = mods, Keys = new List<int>(keys) };

    private static HashSet<int> Down(params int[] vks) => new(vks);

    private static bool Match(ClickerHotkey hk, bool strict, params int[] down) => HotkeyMatcher.IsMatch(hk, Down(down), strict);

    // ---- generic modifiers: either side ----

    [Theory]
    [InlineData(ClickerVk.LControl, ClickerVk.LMenu)]
    [InlineData(ClickerVk.RControl, ClickerVk.LMenu)]
    [InlineData(ClickerVk.LControl, ClickerVk.RMenu)]
    [InlineData(ClickerVk.RControl, ClickerVk.RMenu)]
    public void A_generic_modifier_is_satisfied_by_either_side(int ctrl, int alt)
        => Assert.True(Match(ClickerHotkey.Default(), false, ctrl, alt, Y));

    [Fact]
    public void Both_sides_of_a_generic_modifier_are_fine()
        => Assert.True(Match(ClickerHotkey.Default(), false, ClickerVk.LControl, ClickerVk.RControl, ClickerVk.LMenu, Y));

    [Fact]
    public void The_generic_vk_in_a_polled_set_counts_as_either_side()
        => Assert.True(Match(ClickerHotkey.Default(), false, ClickerVk.Control, ClickerVk.Menu, Y));

    [Fact]
    public void A_missing_modifier_or_key_does_not_match()
    {
        Assert.False(Match(ClickerHotkey.Default(), false, ClickerVk.LControl, Y));                   // Alt missing
        Assert.False(Match(ClickerHotkey.Default(), false, ClickerVk.LMenu, Y));                      // Ctrl missing
        Assert.False(Match(ClickerHotkey.Default(), false, ClickerVk.LControl, ClickerVk.LMenu));     // key missing
        Assert.False(Match(ClickerHotkey.Default(), false));
    }

    // ---- sided modifiers ----

    [Fact]
    public void A_sided_modifier_needs_that_very_key()
    {
        var left = Hk(ClickerModifiers.LCtrl, A);
        Assert.True(Match(left, false, ClickerVk.LControl, A));
        Assert.False(Match(left, false, ClickerVk.RControl, A));
        Assert.False(Match(left, false, ClickerVk.Control, A));        // the generic VK alone does not say which side

        var right = Hk(ClickerModifiers.RAlt, A);
        Assert.True(Match(right, false, ClickerVk.RMenu, A));
        Assert.False(Match(right, false, ClickerVk.LMenu, A));
    }

    [Fact]
    public void Two_sided_flags_of_one_family_need_both_keys()
    {
        var both = Hk(ClickerModifiers.LShift | ClickerModifiers.RShift, A);
        Assert.True(Match(both, false, ClickerVk.LShift, ClickerVk.RShift, A));
        Assert.False(Match(both, false, ClickerVk.LShift, A));
        Assert.False(Match(both, false, ClickerVk.RShift, A));
    }

    [Fact]
    public void Win_keys_work_like_the_other_families()
    {
        Assert.True(Match(Hk(ClickerModifiers.Win, A), false, ClickerVk.RWin, A));
        Assert.True(Match(Hk(ClickerModifiers.LWin, A), false, ClickerVk.LWin, A));
        Assert.False(Match(Hk(ClickerModifiers.LWin, A), false, ClickerVk.RWin, A));
    }

    // ---- strict vs loose ----

    [Fact]
    public void Loose_matching_ignores_extra_modifiers_and_keys()
    {
        var hk = Hk(ClickerModifiers.Ctrl, A);
        Assert.True(Match(hk, false, ClickerVk.LControl, ClickerVk.LShift, ClickerVk.LMenu, A, B));
    }

    [Fact]
    public void Strict_matching_rejects_an_extra_modifier()
    {
        var hk = Hk(ClickerModifiers.Ctrl, A);
        Assert.True(Match(hk, true, ClickerVk.LControl, A));
        Assert.False(Match(hk, true, ClickerVk.LControl, ClickerVk.LShift, A));
        Assert.False(Match(hk, true, ClickerVk.LControl, ClickerVk.LMenu, A));
        Assert.False(Match(hk, true, ClickerVk.LControl, ClickerVk.RWin, A));
    }

    [Fact]
    public void Strict_matching_rejects_modifiers_for_a_hotkey_without_any()
    {
        var hk = Hk(ClickerModifiers.None, A);
        Assert.True(Match(hk, true, A));
        Assert.False(Match(hk, true, ClickerVk.RControl, A));
        Assert.False(Match(hk, true, ClickerVk.Shift, A));   // generic VK counts as a modifier down
    }

    [Fact]
    public void Strict_matching_ignores_extra_non_modifier_keys()
        => Assert.True(Match(Hk(ClickerModifiers.Ctrl, A), true, ClickerVk.LControl, A, B));

    [Fact]
    public void Strict_matching_with_one_demanded_side_rejects_the_other_side()
    {
        var hk = Hk(ClickerModifiers.LCtrl, A);
        Assert.True(Match(hk, true, ClickerVk.LControl, A));
        Assert.False(Match(hk, true, ClickerVk.LControl, ClickerVk.RControl, A));
    }

    [Fact]
    public void Strict_matching_with_a_generic_modifier_accepts_both_sides()
        => Assert.True(Match(Hk(ClickerModifiers.Ctrl, A), true, ClickerVk.LControl, ClickerVk.RControl, A));

    // ---- mouse VKs and multiple keys ----

    [Fact]
    public void Mouse_buttons_are_keys_like_any_other()
    {
        var hk = Hk(ClickerModifiers.None, ClickerVk.XButton1);
        Assert.True(Match(hk, false, ClickerVk.XButton1));
        Assert.False(Match(hk, false, ClickerVk.XButton2));
        Assert.True(Match(Hk(ClickerModifiers.LCtrl, ClickerVk.MButton), false, ClickerVk.LControl, ClickerVk.MButton));
        Assert.False(Match(Hk(ClickerModifiers.LCtrl, ClickerVk.MButton), false, ClickerVk.MButton));
    }

    [Fact]
    public void Five_keys_must_all_be_down()
    {
        var hk = Hk(ClickerModifiers.Alt, A, B, C, D, E);
        Assert.True(Match(hk, false, ClickerVk.LMenu, A, B, C, D, E));
        Assert.False(Match(hk, false, ClickerVk.LMenu, A, B, C, D));
        Assert.True(Match(hk, false, ClickerVk.LMenu, A, B, C, D, E, Y));
    }

    [Fact]
    public void Mouse_and_keyboard_keys_can_be_mixed()
        => Assert.True(Match(Hk(ClickerModifiers.None, A, ClickerVk.RButton), false, A, ClickerVk.RButton));

    [Fact]
    public void An_empty_or_null_hotkey_never_matches()
    {
        Assert.False(HotkeyMatcher.IsMatch(null, Down(A), false));
        Assert.False(HotkeyMatcher.IsMatch(new ClickerHotkey(), Down(A), false));
        Assert.False(HotkeyMatcher.IsMatch(new ClickerHotkey { Keys = null! }, Down(A), false));
        Assert.False(HotkeyMatcher.IsMatch(new ClickerHotkey(), Down(ClickerVk.LControl, ClickerVk.LShift, ClickerVk.LMenu), true));
    }

    [Fact]
    public void An_empty_down_set_never_matches()
    {
        Assert.False(Match(Hk(ClickerModifiers.None, A), false));
        Assert.False(Match(Hk(ClickerModifiers.Ctrl), false));
    }

    // ---- modifier-only hotkeys ----

    [Fact]
    public void A_modifier_only_hotkey_matches_on_its_modifiers()
    {
        var hk = Hk(ClickerModifiers.Ctrl | ClickerModifiers.Shift);
        Assert.True(Match(hk, false, ClickerVk.LControl, ClickerVk.RShift));
        Assert.False(Match(hk, false, ClickerVk.LControl));
        Assert.False(Match(hk, false, ClickerVk.LShift));
    }

    [Fact]
    public void A_modifier_only_hotkey_ignores_extra_non_modifier_keys_even_when_strict()
    {
        var hk = Hk(ClickerModifiers.Shift);
        Assert.True(Match(hk, false, ClickerVk.LShift, A));
        Assert.True(Match(hk, true, ClickerVk.LShift, A, ClickerVk.LButton));
    }

    [Fact]
    public void Strict_rejects_an_undemanded_modifier_family_for_a_modifier_only_hotkey()
    {
        var hk = Hk(ClickerModifiers.Shift);
        Assert.True(Match(hk, true, ClickerVk.LShift));
        Assert.False(Match(hk, true, ClickerVk.LShift, ClickerVk.LControl));
        Assert.True(Match(hk, false, ClickerVk.LShift, ClickerVk.LControl));
    }

    [Fact]
    public void Sided_modifier_only_hotkeys_follow_the_sided_rules()
    {
        var hk = Hk(ClickerModifiers.RShift);
        Assert.True(Match(hk, false, ClickerVk.RShift));
        Assert.False(Match(hk, false, ClickerVk.LShift));
        Assert.True(Match(hk, false, ClickerVk.RShift, ClickerVk.LShift));
        Assert.False(Match(hk, true, ClickerVk.RShift, ClickerVk.LShift));   // strict: the other side counts as extra
    }

    [Fact]
    public void AltGr_arrives_as_left_ctrl_plus_right_alt()
    {
        var sided = Hk(ClickerModifiers.LCtrl | ClickerModifiers.RAlt);
        var generic = Hk(ClickerModifiers.Ctrl | ClickerModifiers.Alt);
        int[] altGr = { ClickerVk.LControl, ClickerVk.RMenu };

        Assert.True(Match(sided, false, altGr));
        Assert.True(Match(sided, true, altGr));
        Assert.True(Match(generic, false, altGr));
        Assert.True(Match(generic, true, altGr));
        Assert.False(Match(sided, false, ClickerVk.RControl, ClickerVk.LMenu));   // Ctrl+Alt with the other hands is not AltGr
        Assert.True(Match(Hk(ClickerModifiers.LCtrl | ClickerModifiers.RAlt, 0x51), true, ClickerVk.LControl, ClickerVk.RMenu, 0x51));   // AltGr+Q
    }

    // ---- new kinds of keys ----

    [Fact]
    public void A_bare_left_button_and_wheel_keys_match_like_keys()
    {
        Assert.True(Match(Hk(ClickerModifiers.None, ClickerVk.LButton), false, ClickerVk.LButton));
        Assert.True(Match(Hk(ClickerModifiers.None, ClickerVk.WheelUp), true, ClickerVk.WheelUp));
        Assert.False(Match(Hk(ClickerModifiers.None, ClickerVk.WheelUp), false, ClickerVk.WheelDown));
        Assert.True(Match(Hk(ClickerModifiers.Ctrl, ClickerVk.WheelDown), false, ClickerVk.LControl, ClickerVk.WheelDown));
        Assert.True(Match(Hk(ClickerModifiers.None, ClickerVk.Escape), false, ClickerVk.Escape));
    }
}
