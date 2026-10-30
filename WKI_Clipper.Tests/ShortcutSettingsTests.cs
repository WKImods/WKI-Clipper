using System;
using System.IO;
using System.Text.Json;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// The shortcut bar's saved state: schema v10 adds the "Shortcuts" section. Every field has an
/// initializer, so older files load the defaults; EnsureSections guards against explicit nulls.
/// </summary>
public sealed class ShortcutSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_shortcuts_" + Guid.NewGuid().ToString("N"));

    public ShortcutSettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void V9_settings_migrate_to_v10_with_expanded_empty_shortcuts()
    {
        // A real v9 file has no "Shortcuts" property at all.
        var s = JsonSerializer.Deserialize<AppSettings>("{\"SchemaVersion\":9}")!;
        Assert.Equal(9, s.SchemaVersion);

        bool changed = SettingsService.MigrateIfNeeded(s);

        Assert.True(changed);
        Assert.Equal(SettingsService.CurrentSchemaVersion, s.SchemaVersion);   // migrates all the way up (v11 added the AutoClicker section)
        Assert.NotNull(s.Shortcuts);
        Assert.True(s.Shortcuts.Expanded);
        Assert.Empty(s.Shortcuts.Items);
        Assert.Empty(s.Shortcuts.RecentGames);
        Assert.Null(s.Shortcuts.DefaultBrowserId);
    }

    [Fact]
    public void Null_shortcuts_section_and_lists_are_guarded()
    {
        var noSection = JsonSerializer.Deserialize<AppSettings>("{\"SchemaVersion\":10,\"Shortcuts\":null}")!;
        Assert.Null(noSection.Shortcuts);
        SettingsService.EnsureSections(noSection);
        Assert.NotNull(noSection.Shortcuts);
        Assert.NotNull(noSection.Shortcuts.Items);
        Assert.NotNull(noSection.Shortcuts.RecentGames);

        var nullLists = JsonSerializer.Deserialize<AppSettings>(
            "{\"SchemaVersion\":10,\"Shortcuts\":{\"Items\":null,\"RecentGames\":null}}")!;
        Assert.Null(nullLists.Shortcuts.Items);
        Assert.Null(nullLists.Shortcuts.RecentGames);
        SettingsService.EnsureSections(nullLists);
        Assert.NotNull(nullLists.Shortcuts.Items);
        Assert.NotNull(nullLists.Shortcuts.RecentGames);
        Assert.True(nullLists.Shortcuts.Expanded);   // untouched default
    }

    [Fact]
    public void Shortcuts_round_trip()
    {
        var seen = new DateTime(2026, 10, 20, 12, 30, 0, DateTimeKind.Utc);

        var first = new SettingsService(_dir);
        first.Current.SchemaVersion = SettingsService.CurrentSchemaVersion;
        // Output folders point into the temp dir so Load()'s EnsureOutputDirs never touches the real Videos folder.
        first.Current.Output.ClipsFolder = Path.Combine(_dir, "clips");
        first.Current.Output.ScreenshotsFolder = Path.Combine(_dir, "shots");
        first.Current.Output.BufferFolder = Path.Combine(_dir, "buffer");
        first.Current.Shortcuts.Expanded = false;
        first.Current.Shortcuts.DefaultBrowserId = "chrome";
        first.Current.Shortcuts.Items.Add(new ShortcutEntry
        {
            Id = "game:steam:1874880",
            Group = ShortcutGroup.Game,
            Kind = ShortcutKind.SteamApp,
            Target = "1874880",
            Arguments = null,
            DisplayName = "Arma Reforger",
            IconSource = @"D:\SteamLibrary\steamapps\common\Arma Reforger\ArmaReforgerSteam.exe",
            Hidden = true,
            Missing = true,
            Hotkey = new HotkeyBinding { Modifiers = HotkeyModifier.Control | HotkeyModifier.Alt, Key = 0x41 },
        });
        first.Current.Shortcuts.Items.Add(new ShortcutEntry
        {
            Id = "launcher:xbox",
            Group = ShortcutGroup.Launcher,
            Kind = ShortcutKind.StoreApp,
            Target = "Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App",
            DisplayName = "Xbox",
        });
        first.Current.Shortcuts.RecentGames.Add(new SeenGame
        {
            ExePath = @"C:\Games\World_of_Tanks_EU\WorldOfTanks.exe",
            DisplayName = "World of Tanks",
            LastSeenUtc = seen,
        });
        first.Save();

        // The enums must stay readable (and hand-editable) in the file.
        var json = File.ReadAllText(first.SettingsFilePath);
        Assert.Contains("\"SteamApp\"", json);
        Assert.Contains("\"StoreApp\"", json);

        var loaded = new SettingsService(_dir).Load();

        Assert.False(loaded.Shortcuts.Expanded);
        Assert.Equal("chrome", loaded.Shortcuts.DefaultBrowserId);
        Assert.Equal(2, loaded.Shortcuts.Items.Count);

        var arma = loaded.Shortcuts.Items[0];
        Assert.Equal("game:steam:1874880", arma.Id);
        Assert.Equal(ShortcutGroup.Game, arma.Group);
        Assert.Equal(ShortcutKind.SteamApp, arma.Kind);
        Assert.Equal("1874880", arma.Target);
        Assert.Null(arma.Arguments);
        Assert.Equal("Arma Reforger", arma.DisplayName);
        Assert.Equal(@"D:\SteamLibrary\steamapps\common\Arma Reforger\ArmaReforgerSteam.exe", arma.IconSource);
        Assert.True(arma.Hidden);
        Assert.True(arma.Missing);
        Assert.NotNull(arma.Hotkey);
        Assert.Equal(HotkeyModifier.Control | HotkeyModifier.Alt, arma.Hotkey!.Modifiers);
        Assert.Equal(0x41u, arma.Hotkey.Key);

        var xbox = loaded.Shortcuts.Items[1];
        Assert.Equal(ShortcutKind.StoreApp, xbox.Kind);
        Assert.Null(xbox.Hotkey);
        Assert.False(xbox.Hidden);

        var game = Assert.Single(loaded.Shortcuts.RecentGames);
        Assert.Equal(@"C:\Games\World_of_Tanks_EU\WorldOfTanks.exe", game.ExePath);
        Assert.Equal("World of Tanks", game.DisplayName);
        Assert.Equal(seen, game.LastSeenUtc);
    }
}
