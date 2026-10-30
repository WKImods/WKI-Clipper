using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class ClickerConflictsTests
{
    private static ClickerHotkey Hk(ClickerModifiers mods, params int[] keys)
        => new() { Modifiers = mods, Keys = new List<int>(keys) };

    private static AppSettings Settings(System.Action<ClickerSettings>? edit = null)
    {
        var s = new AppSettings();
        edit?.Invoke(s.AutoClicker.Clicker);
        return s;
    }

    private static ClickerConflict? Find(AppSettings s, ClickerConflictKind kind)
        => ClickerConflicts.Evaluate(s).FirstOrDefault(c => c.Kind == kind);

    [Fact]
    public void The_defaults_have_no_conflicts()
    {
        var all = ClickerConflicts.Evaluate(new AppSettings());
        Assert.Empty(all);
        Assert.True(ClickerConflicts.CanStart(all));
    }

    // ---- output key vs hotkey ----

    private static AppSettings Keyboard(int vk, KeyCase kase, ClickerHotkey hotkey)
        => Settings(c => { c.InputType = ClickerInput.Keyboard; c.KeyboardVk = vk; c.KeyCase = kase; c.Hotkey = hotkey; });

    [Fact]
    public void A_bare_hotkey_that_is_the_output_key_only_warns()
    {
        var s = Keyboard(0x41, KeyCase.Lower, Hk(ClickerModifiers.None, 0x41));
        var c = Find(s, ClickerConflictKind.OutputKeyIsHotkey);
        Assert.NotNull(c);
        Assert.False(c!.BlocksStart);
        Assert.True(ClickerConflicts.CanStart(ClickerConflicts.Evaluate(s)));
    }

    [Theory]
    [InlineData(ClickerModifiers.Shift, KeyCase.Upper)]
    [InlineData(ClickerModifiers.LShift, KeyCase.Lower)]
    [InlineData(ClickerModifiers.Ctrl, KeyCase.Lower)]
    [InlineData(ClickerModifiers.Shift | ClickerModifiers.Ctrl, KeyCase.Upper)]
    public void An_output_key_that_is_part_of_the_hotkey_warns_whatever_the_modifiers(ClickerModifiers mods, KeyCase kase)
    {
        var c = Find(Keyboard(0x41, kase, Hk(mods, 0x41)), ClickerConflictKind.OutputKeyIsHotkey);
        Assert.NotNull(c);
        Assert.False(c!.BlocksStart);
    }

    [Fact]
    public void An_output_key_among_several_hotkey_keys_warns_and_another_key_does_not()
    {
        Assert.NotNull(Find(Keyboard(0x41, KeyCase.Lower, Hk(ClickerModifiers.None, 0x42, 0x41)), ClickerConflictKind.OutputKeyIsHotkey));
        Assert.Null(Find(Keyboard(0x41, KeyCase.Lower, Hk(ClickerModifiers.None, 0x42)), ClickerConflictKind.OutputKeyIsHotkey));
        Assert.Null(Find(Keyboard(0x41, KeyCase.Lower, Hk(ClickerModifiers.Ctrl)), ClickerConflictKind.OutputKeyIsHotkey));
    }

    [Fact]
    public void Mouse_output_never_conflicts_with_a_key_hotkey()
    {
        var s = Settings(c => { c.InputType = ClickerInput.Mouse; c.KeyboardVk = 0x41; c.Hotkey = Hk(ClickerModifiers.None, 0x41); });
        Assert.Null(Find(s, ClickerConflictKind.OutputKeyIsHotkey));
    }

    [Theory]
    [InlineData(ClickerMouseButton.Left, ClickerVk.LButton)]
    [InlineData(ClickerMouseButton.Right, ClickerVk.RButton)]
    [InlineData(ClickerMouseButton.Middle, ClickerVk.MButton)]
    public void The_clicked_mouse_button_inside_the_hotkey_warns(ClickerMouseButton button, int vk)
    {
        var s = Settings(c => { c.InputType = ClickerInput.Mouse; c.MouseButton = button; c.Hotkey = Hk(ClickerModifiers.Shift, vk); });
        var c = Find(s, ClickerConflictKind.OutputKeyIsHotkey);
        Assert.NotNull(c);
        Assert.False(c!.BlocksStart);
        Assert.Contains("Maustaste", c.Message);
        Assert.Null(Find(Settings(x => { x.MouseButton = button; x.Hotkey = Hk(ClickerModifiers.Shift, ClickerVk.XButton1); }),
            ClickerConflictKind.OutputKeyIsHotkey));
    }

    [Fact]
    public void The_master_hotkey_is_checked_like_the_hotkey()
    {
        var s = Settings(c =>
        {
            c.InputType = ClickerInput.Keyboard; c.KeyboardVk = 0x7B;   // F12
            c.MasterHotkey = Hk(ClickerModifiers.None, 0x7B);
        });
        var c = Find(s, ClickerConflictKind.OutputKeyIsMasterHotkey);
        Assert.NotNull(c);
        Assert.False(c!.BlocksStart);
        Assert.Contains("Master-Hotkey", c.Message);
    }

    // ---- risky hotkeys are warnings ----

    [Theory]
    [InlineData(ClickerVk.LButton, "linke Maustaste")]
    [InlineData(ClickerVk.RButton, "rechte Maustaste")]
    public void A_bare_left_or_right_button_warns(int vk, string text)
    {
        var c = Find(Settings(x => x.Hotkey = Hk(ClickerModifiers.None, vk)), ClickerConflictKind.HotkeyIsBareMouseButton);
        Assert.NotNull(c);
        Assert.False(c!.BlocksStart);
        Assert.Contains(text, c.Message);
        Assert.NotNull(Find(Settings(x => x.MasterHotkey = Hk(ClickerModifiers.None, vk)), ClickerConflictKind.MasterIsBareMouseButton));
    }

    [Fact]
    public void A_mouse_button_with_a_modifier_or_another_button_is_not_bare()
    {
        Assert.Null(Find(Settings(x => x.Hotkey = Hk(ClickerModifiers.Shift, ClickerVk.LButton)), ClickerConflictKind.HotkeyIsBareMouseButton));
        Assert.Null(Find(Settings(x => x.Hotkey = Hk(ClickerModifiers.None, ClickerVk.LButton, ClickerVk.RButton)), ClickerConflictKind.HotkeyIsBareMouseButton));
        Assert.Null(Find(Settings(x => x.Hotkey = Hk(ClickerModifiers.None, ClickerVk.XButton1)), ClickerConflictKind.HotkeyIsBareMouseButton));
    }

    [Fact]
    public void A_modifier_only_hotkey_warns_and_names_its_combo()
    {
        var c = Find(Settings(x => x.Hotkey = Hk(ClickerModifiers.Ctrl | ClickerModifiers.Shift)), ClickerConflictKind.HotkeyIsModifierOnly);
        Assert.NotNull(c);
        Assert.False(c!.BlocksStart);
        Assert.Contains("Strg+Umschalt", c.Message);
        Assert.NotNull(Find(Settings(x => x.MasterHotkey = Hk(ClickerModifiers.LCtrl | ClickerModifiers.RAlt)), ClickerConflictKind.MasterIsModifierOnly));
        Assert.Null(Find(Settings(), ClickerConflictKind.HotkeyIsModifierOnly));
    }

    [Fact]
    public void Esc_in_a_hotkey_warns()
    {
        Assert.NotNull(Find(Settings(x => x.Hotkey = Hk(ClickerModifiers.None, ClickerVk.Escape)), ClickerConflictKind.HotkeyHasEscape));
        Assert.NotNull(Find(Settings(x => x.Hotkey = Hk(ClickerModifiers.Shift, ClickerVk.Escape)), ClickerConflictKind.HotkeyHasEscape));
        Assert.NotNull(Find(Settings(x => x.MasterHotkey = Hk(ClickerModifiers.None, ClickerVk.Escape)), ClickerConflictKind.MasterHasEscape));
    }

    [Theory]
    [InlineData(ClickerVk.WheelUp)]
    [InlineData(ClickerVk.WheelDown)]
    [InlineData(ClickerVk.WheelLeft)]
    [InlineData(ClickerVk.WheelRight)]
    public void A_wheel_hotkey_warns(int wheel)
    {
        var c = Find(Settings(x => x.Hotkey = Hk(ClickerModifiers.None, wheel)), ClickerConflictKind.HotkeyHasWheel);
        Assert.NotNull(c);
        Assert.False(c!.BlocksStart);
        Assert.Contains("Mausrad", c.Message);
        Assert.NotNull(Find(Settings(x => x.MasterHotkey = Hk(ClickerModifiers.Ctrl, wheel)), ClickerConflictKind.MasterHasWheel));
    }

    [Fact]
    public void No_risky_hotkey_ever_blocks_the_start()
    {
        foreach (var hk in new[]
                 {
                     Hk(ClickerModifiers.None, ClickerVk.LButton), Hk(ClickerModifiers.None, ClickerVk.RButton), Hk(ClickerModifiers.Alt),
                     Hk(ClickerModifiers.None, ClickerVk.Escape), Hk(ClickerModifiers.None, ClickerVk.WheelDown)
                 })
        {
            var all = ClickerConflicts.Evaluate(Settings(x => { x.Hotkey = hk; x.MasterHotkey = hk.Clone(); }));
            Assert.NotEmpty(all);
            Assert.All(all, c => Assert.False(c.BlocksStart));
            Assert.True(ClickerConflicts.CanStart(all));
            Assert.Contains(all, c => c.Kind == ClickerConflictKind.HotkeyIsMasterHotkey);   // same combo for both: a warning, too
        }
    }

    [Fact]
    public void Keyboard_output_without_a_key_blocks_the_start()
    {
        var s = Settings(c => { c.InputType = ClickerInput.Keyboard; c.KeyboardVk = 0; });
        var c = Find(s, ClickerConflictKind.NoOutputKey);
        Assert.NotNull(c);
        Assert.True(c!.BlocksStart);
        Assert.Null(Find(Settings(), ClickerConflictKind.NoOutputKey));   // mouse output needs no key
    }

    // ---- whitelist ----

    [Fact]
    public void An_enabled_empty_whitelist_blocks_the_start()
    {
        var s = Settings(c => c.WhitelistEnabled = true);
        var c = Find(s, ClickerConflictKind.EmptyWhitelist);
        Assert.NotNull(c);
        Assert.True(c!.BlocksStart);
    }

    [Fact]
    public void A_filled_or_disabled_whitelist_is_fine()
    {
        Assert.Null(Find(Settings(c => { c.WhitelistEnabled = true; c.Whitelist.Add(new BlacklistEntry { ExeName = "a.exe" }); }), ClickerConflictKind.EmptyWhitelist));
        Assert.Null(Find(Settings(c => c.WhitelistEnabled = false), ClickerConflictKind.EmptyWhitelist));
    }

    // ---- clash with the clipper's RegisterHotKey bindings ----

    [Fact]
    public void A_hotkey_equal_to_a_clipper_action_is_a_warning_naming_the_action()
    {
        // ToggleOverlay is Ctrl+Alt+G by default.
        var s = Settings(c => c.Hotkey = Hk(ClickerModifiers.Ctrl | ClickerModifiers.Alt, 0x47));
        var c = Find(s, ClickerConflictKind.HotkeyIsClipperHotkey);

        Assert.NotNull(c);
        Assert.False(c!.BlocksStart);
        Assert.Contains("ToggleOverlay", c.Message);
        Assert.True(ClickerConflicts.CanStart(ClickerConflicts.Evaluate(s)));
    }

    [Fact]
    public void Sided_modifiers_clash_like_their_generic_form()
    {
        // F9 alone, Ctrl+F9 and Ctrl+Alt+C are bound by default.
        Assert.NotNull(Find(Settings(c => c.Hotkey = Hk(ClickerModifiers.LCtrl, 0x78)), ClickerConflictKind.HotkeyIsClipperHotkey));
        Assert.NotNull(Find(Settings(c => c.Hotkey = Hk(ClickerModifiers.RCtrl | ClickerModifiers.LAlt, 0x43)), ClickerConflictKind.HotkeyIsClipperHotkey));
        Assert.NotNull(Find(Settings(c => c.Hotkey = Hk(ClickerModifiers.None, 0x78)), ClickerConflictKind.HotkeyIsClipperHotkey));
    }

    [Fact]
    public void A_different_modifier_set_is_no_clash()
    {
        Assert.Null(Find(Settings(c => c.Hotkey = Hk(ClickerModifiers.Ctrl | ClickerModifiers.Alt, 0x78)), ClickerConflictKind.HotkeyIsClipperHotkey));
        Assert.Null(Find(Settings(c => c.Hotkey = Hk(ClickerModifiers.Shift, 0x78)), ClickerConflictKind.HotkeyIsClipperHotkey));
        Assert.Null(Find(Settings(c => c.Hotkey = Hk(ClickerModifiers.Ctrl, 0x59)), ClickerConflictKind.HotkeyIsClipperHotkey));
    }

    [Fact]
    public void Any_key_of_a_multi_key_hotkey_can_clash_but_mouse_buttons_cannot()
    {
        // F9 = SaveReplay, Ctrl+F9 = ToggleRecording by default
        Assert.NotNull(Find(Settings(c => c.Hotkey = Hk(ClickerModifiers.None, 0x78, 0x79)), ClickerConflictKind.HotkeyIsClipperHotkey));
        Assert.NotNull(Find(Settings(c => c.Hotkey = Hk(ClickerModifiers.Ctrl, 0x78, ClickerVk.XButton1)), ClickerConflictKind.HotkeyIsClipperHotkey));
        Assert.Null(Find(Settings(c => c.Hotkey = Hk(ClickerModifiers.Ctrl, ClickerVk.XButton1)), ClickerConflictKind.HotkeyIsClipperHotkey));
    }

    [Fact]
    public void A_clipper_binding_with_extra_modifiers_clashes_unless_strict()
    {
        // ToggleOverlay is Ctrl+Alt+G by default; a non-strict Ctrl+G also matches when Ctrl+Alt+G is pressed.
        var loose = Settings(c => c.Hotkey = Hk(ClickerModifiers.Ctrl, 0x47));
        var found = Find(loose, ClickerConflictKind.HotkeyIsClipperHotkey);
        Assert.NotNull(found);
        Assert.Contains("ToggleOverlay", found!.Message);

        Assert.Null(Find(Settings(c => { c.Hotkey = Hk(ClickerModifiers.Ctrl, 0x47); c.StrictModifiers = true; }), ClickerConflictKind.HotkeyIsClipperHotkey));
    }

    [Fact]
    public void The_master_hotkey_follows_the_same_modifier_rule()
    {
        Assert.NotNull(Find(Settings(c => c.MasterHotkey = Hk(ClickerModifiers.Ctrl, 0x47)), ClickerConflictKind.MasterIsClipperHotkey));
        Assert.Null(Find(Settings(c => { c.MasterHotkey = Hk(ClickerModifiers.Ctrl, 0x47); c.StrictModifiers = true; }), ClickerConflictKind.MasterIsClipperHotkey));
    }

    [Fact]
    public void An_unbound_clipper_action_is_no_clash()
    {
        var s = Settings(c => c.Hotkey = Hk(ClickerModifiers.None, 0x78));
        // bare F9 also fires with Ctrl+F9 (ToggleRecording) while the modifiers are not strict
        s.Hotkeys[HotkeyActions.ToggleRecording] = new HotkeyBinding { Key = 0 };
        s.Hotkeys[HotkeyActions.SaveReplay] = new HotkeyBinding { Key = 0 };
        s.Hotkeys[HotkeyActions.SpotifyNext] = new HotkeyBinding { Modifiers = 0, Key = 0 };
        Assert.Null(Find(s, ClickerConflictKind.HotkeyIsClipperHotkey));
    }

    [Fact]
    public void The_win_family_maps_to_the_win_modifier()
    {
        var s = Settings(c => c.Hotkey = Hk(ClickerModifiers.RWin, 0x4B));
        s.Hotkeys[HotkeyActions.TogglePerfOverlay] = new HotkeyBinding { Modifiers = HotkeyModifier.Win, Key = 0x4B };
        Assert.NotNull(Find(s, ClickerConflictKind.HotkeyIsClipperHotkey));

        // Win+Shift+K also satisfies a non-strict Win+K ...
        s.Hotkeys[HotkeyActions.TogglePerfOverlay] = new HotkeyBinding { Modifiers = HotkeyModifier.Win | HotkeyModifier.Shift, Key = 0x4B };
        Assert.NotNull(Find(s, ClickerConflictKind.HotkeyIsClipperHotkey));

        // ... but not a strict one
        s.AutoClicker.Clicker.StrictModifiers = true;
        Assert.Null(Find(s, ClickerConflictKind.HotkeyIsClipperHotkey));
    }

    [Fact]
    public void Shortcut_bar_hotkeys_are_included()
    {
        var s = Settings(c => c.Hotkey = Hk(ClickerModifiers.Ctrl | ClickerModifiers.Alt, 0x59));   // the default
        s.Shortcuts.Items.Add(new ShortcutEntry
        {
            Id = "game:arma", Group = ShortcutGroup.Game, Kind = ShortcutKind.Exe, Target = @"C:\Games\arma.exe", DisplayName = "Arma",
            Hotkey = new HotkeyBinding { Modifiers = HotkeyModifier.Control | HotkeyModifier.Alt, Key = 0x59 }
        });

        var c = Find(s, ClickerConflictKind.HotkeyIsClipperHotkey);

        Assert.NotNull(c);
        Assert.Contains("Arma", c!.Message);
    }

    [Fact]
    public void Stream_deck_tile_hotkeys_are_included()
    {
        var s = Settings();
        s.Streaming.Buttons.Add(new StreamButtonConfig
        {
            Slot = 2, Label = "Szene 1", Hotkey = new HotkeyBinding { Modifiers = HotkeyModifier.Control | HotkeyModifier.Alt, Key = 0x59 }
        });

        var c = Find(s, ClickerConflictKind.HotkeyIsClipperHotkey);

        Assert.NotNull(c);
        Assert.Contains("Szene 1", c!.Message);
    }

    [Fact]
    public void The_master_hotkey_is_checked_against_the_clipper_too()
    {
        var s = Settings(c => c.MasterHotkey = Hk(ClickerModifiers.None, 0x79));   // F10 = Screenshot
        var c = Find(s, ClickerConflictKind.MasterIsClipperHotkey);
        Assert.NotNull(c);
        Assert.False(c!.BlocksStart);
        Assert.Contains("Screenshot", c.Message);
    }

    [Fact]
    public void Hotkey_and_master_with_the_same_combo_warn()
    {
        var s = Settings(c => c.MasterHotkey = Hk(ClickerModifiers.Ctrl | ClickerModifiers.Alt, 0x59));
        var c = Find(s, ClickerConflictKind.HotkeyIsMasterHotkey);
        Assert.NotNull(c);
        Assert.False(c!.BlocksStart);
    }

    [Fact]
    public void Generic_and_sided_modifiers_of_the_same_key_overlap_even_when_strict()
    {
        var s = Settings(c =>
        {
            c.StrictModifiers = true;
            c.Hotkey = Hk(ClickerModifiers.Ctrl, 0x59);
            c.MasterHotkey = Hk(ClickerModifiers.LCtrl, 0x59);
        });
        Assert.NotNull(Find(s, ClickerConflictKind.HotkeyIsMasterHotkey));
    }

    [Fact]
    public void A_master_hotkey_with_extra_modifiers_overlaps_unless_strict()
    {
        System.Action<ClickerSettings, bool> set = (c, strict) =>
        {
            c.StrictModifiers = strict;
            c.Hotkey = Hk(ClickerModifiers.Ctrl, 0x59);
            c.MasterHotkey = Hk(ClickerModifiers.Ctrl | ClickerModifiers.Alt, 0x59);
        };
        Assert.NotNull(Find(Settings(c => set(c, false)), ClickerConflictKind.HotkeyIsMasterHotkey));
        Assert.Null(Find(Settings(c => set(c, true)), ClickerConflictKind.HotkeyIsMasterHotkey));
    }

    [Fact]
    public void No_clicker_section_means_no_conflicts()
    {
        var s = new AppSettings();
        s.AutoClicker.Clicker = null!;
        Assert.Empty(ClickerConflicts.Evaluate(s));
    }

    [Fact]
    public void Several_conflicts_are_reported_together()
    {
        var s = Settings(c =>
        {
            c.InputType = ClickerInput.Keyboard; c.KeyboardVk = 0x59;
            c.Hotkey = Hk(ClickerModifiers.None, 0x59);
            c.WhitelistEnabled = true;
        });
        var all = ClickerConflicts.Evaluate(s);
        var kinds = all.Select(x => x.Kind).ToList();
        Assert.Contains(ClickerConflictKind.OutputKeyIsHotkey, kinds);
        Assert.Contains(ClickerConflictKind.EmptyWhitelist, kinds);
        Assert.Equal(new[] { ClickerConflictKind.EmptyWhitelist }, all.Where(x => x.BlocksStart).Select(x => x.Kind));
    }

    [Fact]
    public void Each_hotkey_field_shows_its_own_warnings_and_the_overlap()
    {
        var s = Settings(c =>
        {
            c.InputType = ClickerInput.Mouse; c.MouseButton = ClickerMouseButton.Left;
            c.Hotkey = Hk(ClickerModifiers.None, ClickerVk.LButton);
            c.MasterHotkey = Hk(ClickerModifiers.None, ClickerVk.LButton);
            c.WhitelistEnabled = true;
        });
        var all = ClickerConflicts.Evaluate(s);

        var start = ClickerConflicts.ForHotkey(all, master: false).Select(x => x.Kind).ToList();
        var master = ClickerConflicts.ForHotkey(all, master: true).Select(x => x.Kind).ToList();

        Assert.Equal(new[] { ClickerConflictKind.OutputKeyIsHotkey, ClickerConflictKind.HotkeyIsBareMouseButton, ClickerConflictKind.HotkeyIsMasterHotkey }, start);
        Assert.Equal(new[] { ClickerConflictKind.OutputKeyIsMasterHotkey, ClickerConflictKind.MasterIsBareMouseButton, ClickerConflictKind.HotkeyIsMasterHotkey }, master);
    }

    [Fact]
    public void Each_hotkey_field_gets_its_modifier_escape_wheel_and_clipper_warnings()
    {
        var s = Settings(c =>
        {
            c.Hotkey = Hk(ClickerModifiers.Ctrl | ClickerModifiers.Alt, 0x47, ClickerVk.Escape, ClickerVk.WheelDown);   // Ctrl+Alt+G is a clipper binding
            c.MasterHotkey = Hk(ClickerModifiers.Shift);
        });
        var all = ClickerConflicts.Evaluate(s);

        var start = ClickerConflicts.ForHotkey(all, master: false).Select(x => x.Kind).ToHashSet();
        var master = ClickerConflicts.ForHotkey(all, master: true).Select(x => x.Kind).ToHashSet();

        Assert.Superset(new HashSet<ClickerConflictKind> { ClickerConflictKind.HotkeyHasEscape, ClickerConflictKind.HotkeyHasWheel, ClickerConflictKind.HotkeyIsClipperHotkey }, start);
        Assert.DoesNotContain(ClickerConflictKind.MasterIsModifierOnly, start);
        Assert.Contains(ClickerConflictKind.MasterIsModifierOnly, master);
        Assert.DoesNotContain(ClickerConflictKind.HotkeyHasEscape, master);
    }
}

[Collection("Language")]
public sealed class ClickerConflictsEnglishTests
{
    [Fact]
    public void The_messages_follow_the_language()
    {
        using var _ = TestLanguage.English();
        var s = new AppSettings();
        s.AutoClicker.Clicker.WhitelistEnabled = true;
        s.AutoClicker.Clicker.Hotkey = new ClickerHotkey { Modifiers = ClickerModifiers.Ctrl | ClickerModifiers.Alt, Keys = { 0x47 } };

        var all = ClickerConflicts.Evaluate(s);

        Assert.Contains(all, c => c.Kind == ClickerConflictKind.EmptyWhitelist && c.Message.StartsWith("The whitelist is on but empty"));
        Assert.Contains(all, c => c.Kind == ClickerConflictKind.HotkeyIsClipperHotkey && c.Message.Contains("clipper action \"ToggleOverlay\""));
    }

    [Fact]
    public void The_new_warnings_follow_the_language()
    {
        using var _ = TestLanguage.English();
        var s = new AppSettings();
        s.AutoClicker.Clicker.Hotkey = new ClickerHotkey { Modifiers = ClickerModifiers.Ctrl | ClickerModifiers.Shift };
        s.AutoClicker.Clicker.MasterHotkey = new ClickerHotkey { Keys = { ClickerVk.WheelUp } };

        var all = ClickerConflicts.Evaluate(s);

        Assert.Contains(all, c => c.Kind == ClickerConflictKind.HotkeyIsModifierOnly && c.Message.StartsWith("The hotkey is modifiers only (Ctrl+Shift)"));
        Assert.Contains(all, c => c.Kind == ClickerConflictKind.MasterHasWheel && c.Message.StartsWith("The master hotkey uses the mouse wheel"));
    }
}
