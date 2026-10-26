using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// One name per entry everywhere: the bar, the edit dialog and the hotkey messages (collision notice, failed-registration toast) all read
/// <see cref="ShortcutReconciler.DisplayNameOf"/>. The standard browser is stored with an English fallback name; the user must never see it
/// in a German message.
/// </summary>
public sealed class ShortcutDisplayNameTests
{
    private static readonly HotkeyBinding Combo = new() { Modifiers = HotkeyModifier.Control | HotkeyModifier.Alt, Key = 0x42 };   // Ctrl+Alt+B

    private static AppSettings WithDefaultBrowserBound()
    {
        var s = new AppSettings();
        var browser = ShortcutReconciler.NewDefaultBrowserEntry();
        browser.Hotkey = new HotkeyBinding { Modifiers = Combo.Modifiers, Key = Combo.Key };
        s.Shortcuts.Items.Add(browser);
        return s;
    }

    [Fact]
    public void The_stored_name_of_the_standard_browser_is_english_but_every_label_is_localized()
    {
        Assert.Equal("Default browser", ShortcutReconciler.NewDefaultBrowserEntry().DisplayName);   // the stored fallback
        Assert.Equal("Standard-Browser", ShortcutReconciler.DisplayNameOf(ShortcutReconciler.NewDefaultBrowserEntry()));
    }

    [Fact]
    public void The_collision_notice_names_the_standard_browser_in_the_ui_language()
    {
        var clash = HotkeyService.FindCollision(WithDefaultBrowserBound(), new HotkeyBinding { Modifiers = Combo.Modifiers, Key = Combo.Key },
            excludeShortcutId: "game.other");

        Assert.NotNull(clash);
        Assert.Contains("Standard-Browser", clash);
        Assert.DoesNotContain("Default browser", clash);
    }

    [Fact]
    public void The_failed_registration_toast_names_the_standard_browser_in_the_ui_language()
    {
        var text = HotkeyService.DescribeAction(WithDefaultBrowserBound(), ShortcutHotkeys.ActionFor(ShortcutReconciler.DefaultBrowserEntryId));

        Assert.Contains("Standard-Browser", text);
        Assert.DoesNotContain("Default browser", text);
    }

    [Fact]
    public void An_entry_without_a_display_name_is_named_by_its_file_then_by_its_id()
    {
        var withFile = new ShortcutEntry { Id = "game.a", Group = ShortcutGroup.Game, Kind = ShortcutKind.Exe, Target = @"C:\Games\Foo\foo.exe", DisplayName = " " };
        var bare = new ShortcutEntry { Id = "game.b", Group = ShortcutGroup.Game, Kind = ShortcutKind.SteamApp, Target = "", DisplayName = "" };
        var named = new ShortcutEntry { Id = "game.c", Group = ShortcutGroup.Game, Kind = ShortcutKind.Exe, Target = @"C:\x.exe", DisplayName = "Foo" };

        Assert.Equal("foo.exe", ShortcutReconciler.DisplayNameOf(withFile));
        Assert.Equal("game.b", ShortcutReconciler.DisplayNameOf(bare));
        Assert.Equal("Foo", ShortcutReconciler.DisplayNameOf(named));
    }

    [Fact]
    public void A_collision_with_an_entry_without_a_display_name_still_names_it()
    {
        var s = new AppSettings();
        s.Shortcuts.Items.Add(new ShortcutEntry
        {
            Id = "game.a", Group = ShortcutGroup.Game, Kind = ShortcutKind.Exe, Target = @"C:\Games\Foo\foo.exe", DisplayName = "",
            Hotkey = new HotkeyBinding { Modifiers = Combo.Modifiers, Key = Combo.Key },
        });

        var clash = HotkeyService.FindCollision(s, new HotkeyBinding { Modifiers = Combo.Modifiers, Key = Combo.Key }, excludeShortcutId: "game.z");

        Assert.Contains("foo.exe", clash);   // not an empty pair of quotes
    }
}

/// <summary>The English wording of the same messages (the app language is process-wide, so these run alone).</summary>
[Collection("Language")]
public sealed class ShortcutDisplayNameEnglishTests
{
    [Fact]
    public void The_english_messages_say_default_browser()
    {
        using var _ = TestLanguage.English();
        var s = new AppSettings();
        var browser = ShortcutReconciler.NewDefaultBrowserEntry();
        browser.Hotkey = new HotkeyBinding { Modifiers = HotkeyModifier.Control | HotkeyModifier.Alt, Key = 0x42 };
        s.Shortcuts.Items.Add(browser);

        var clash = HotkeyService.FindCollision(s, new HotkeyBinding { Modifiers = HotkeyModifier.Control | HotkeyModifier.Alt, Key = 0x42 }, excludeShortcutId: "game.z");
        var toast = HotkeyService.DescribeAction(s, ShortcutHotkeys.ActionFor(ShortcutReconciler.DefaultBrowserEntryId));

        Assert.Equal("shortcut \"Default browser\"", clash);
        Assert.Equal("shortcut \"Default browser\"", toast);
    }
}
