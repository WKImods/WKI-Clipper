using System;
using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class ShortcutReconcilerTests
{
    private static CatalogHit Hit(string id, string name, string target)
        => new(id, name, ShortcutKind.CatalogLauncher, target, target);

    private static ShortcutEntry Launcher(string id, string name, bool hidden = false, bool missing = false, HotkeyBinding? hotkey = null)
        => new()
        {
            Id = ShortcutReconciler.LauncherEntryId(id), Group = ShortcutGroup.Launcher, Kind = ShortcutKind.CatalogLauncher,
            Target = @"C:\old\" + id + ".lnk", IconSource = @"C:\old\" + id + ".lnk", DisplayName = name,
            Hidden = hidden, Missing = missing, Hotkey = hotkey,
        };

    private static ShortcutEntry Game(string id, string name)
        => new() { Id = id, Group = ShortcutGroup.Game, Kind = ShortcutKind.SteamApp, Target = "1", DisplayName = name };

    private static ShortcutEntry DefaultBrowser()
        => new() { Id = ShortcutReconciler.DefaultBrowserEntryId, Group = ShortcutGroup.Browser, Kind = ShortcutKind.DefaultBrowser, Target = "default", DisplayName = "Default browser" };

    private static string[] Ids(IEnumerable<ShortcutEntry> items) => items.Select(i => i.Id).ToArray();

    // ---- Reconcile ----

    [Fact]
    public void Reconcile_appends_new_launcher_and_keeps_order()
    {
        // The user hid Epic, put Epic before Steam and has a game; GOG is new.
        var saved = new[]
        {
            Game("game:arma", "Arma"),
            Launcher("epic", "Epic Games", hidden: true),
            Launcher("steam", "Steam"),
            DefaultBrowser(),
        };
        var hits = new[]
        {
            Hit("steam", "Steam", @"C:\Start\Steam.lnk"),
            Hit("epic", "Epic Games", @"C:\Start\Epic.lnk"),
            Hit("gog", "GOG GALAXY", @"C:\Start\GOG.lnk"),
        };

        var result = ShortcutReconciler.Reconcile(saved, hits, new BrowserInfo[0]);

        Assert.Equal(new[] { "game:arma", "launcher:epic", "launcher:steam", "launcher:gog", "browser:default" }, Ids(result));
        Assert.True(result[1].Hidden);          // Hidden kept
        Assert.False(result[3].Hidden);         // new one is visible
        Assert.Equal(@"C:\Start\GOG.lnk", result[3].Target);
        Assert.Equal(ShortcutGroup.Launcher, result[3].Group);
        Assert.Equal("GOG GALAXY", result[3].DisplayName);
        Assert.False(result[3].Missing);
    }

    [Fact]
    public void Reconcile_updates_the_start_path_of_a_known_launcher_and_keeps_user_choices()
    {
        var hotkey = new HotkeyBinding { Modifiers = HotkeyModifier.Control, Key = 0x53 };
        var saved = new[] { Launcher("steam", "Steam", hidden: true, missing: true, hotkey: hotkey), DefaultBrowser() };

        var result = ShortcutReconciler.Reconcile(saved, new[] { Hit("steam", "Steam", @"C:\New\Steam.lnk") }, new BrowserInfo[0]);

        var steam = result.Single(e => e.Id == "launcher:steam");
        Assert.Equal(@"C:\New\Steam.lnk", steam.Target);
        Assert.Equal(@"C:\New\Steam.lnk", steam.IconSource);
        Assert.False(steam.Missing);       // found again
        Assert.True(steam.Hidden);         // the user's choice
        Assert.Equal(0x53u, steam.Hotkey!.Key);
        Assert.Equal(HotkeyModifier.Control, steam.Hotkey.Modifiers);
    }

    [Fact]
    public void Reconcile_marks_uninstalled_launcher_missing()
    {
        var saved = new[] { Launcher("steam", "Steam"), Launcher("epic", "Epic Games"), DefaultBrowser() };

        var result = ShortcutReconciler.Reconcile(saved, new[] { Hit("steam", "Steam", @"C:\Start\Steam.lnk") }, new BrowserInfo[0]);

        Assert.Equal(new[] { "launcher:steam", "launcher:epic", "browser:default" }, Ids(result));   // stays in the list
        Assert.False(result[0].Missing);
        Assert.True(result[1].Missing);
        Assert.Equal(@"C:\old\epic.lnk", result[1].Target);   // untouched: the entry just turns grey
    }

    [Fact]
    public void Reconcile_always_has_default_browser_first()
    {
        // From scratch: a fresh install has no items at all.
        var fresh = ShortcutReconciler.Reconcile(new ShortcutEntry[0], new[] { Hit("steam", "Steam", @"C:\Start\Steam.lnk") }, new BrowserInfo[0]);
        Assert.Equal(new[] { "launcher:steam", "browser:default" }, Ids(fresh));
        var created = fresh.Last();
        Assert.Equal(ShortcutGroup.Browser, created.Group);
        Assert.Equal(ShortcutKind.DefaultBrowser, created.Kind);
        Assert.False(created.Hidden);

        // A saved default browser that drifted behind another browser goes back to the front of its group.
        var chrome = new ShortcutEntry
        {
            Id = ShortcutReconciler.BrowserEntryId("chrome"), Group = ShortcutGroup.Browser, Kind = ShortcutKind.Browser,
            Target = PhillipsPc.ChromePath, DisplayName = "Google Chrome",
        };
        var drifted = new[] { chrome, DefaultBrowser() };
        var infos = new[] { new BrowserInfo("chrome", "Google Chrome", PhillipsPc.ChromePath, true, "https://www.google.com/chrome/") };

        var result = ShortcutReconciler.Reconcile(drifted, new CatalogHit[0], infos);

        Assert.Equal(new[] { "browser:default", "browser:chrome" }, Ids(result));
    }

    [Fact]
    public void Reconcile_keeps_a_single_default_browser_even_if_the_file_has_two()
    {
        var saved = new[] { DefaultBrowser(), DefaultBrowser() };

        var result = ShortcutReconciler.Reconcile(saved, new CatalogHit[0], new BrowserInfo[0]);

        Assert.Single(result);
    }

    [Fact]
    public void Reconcile_never_adds_browsers_on_its_own()
    {
        var infos = new[]
        {
            new BrowserInfo("chrome", "Google Chrome", PhillipsPc.ChromePath, true, null),
            new BrowserInfo("comet", "Comet", PhillipsPc.CometPath, true, null),
        };

        var result = ShortcutReconciler.Reconcile(new ShortcutEntry[0], new CatalogHit[0], infos);

        Assert.Equal(new[] { "browser:default" }, Ids(result));
    }

    [Fact]
    public void Reconcile_tracks_picked_browsers_installed_state()
    {
        string Browser(string id) => ShortcutReconciler.BrowserEntryId(id);
        ShortcutEntry Picked(string id, string path) => new()
        {
            Id = Browser(id), Group = ShortcutGroup.Browser, Kind = ShortcutKind.Browser, Target = path, IconSource = path, DisplayName = id,
        };
        var saved = new[] { DefaultBrowser(), Picked("chrome", @"C:\old\chrome.exe"), Picked("opera", @"C:\old\opera.exe"), Picked("vivaldi", @"C:\old\vivaldi.exe") };
        var infos = new[]
        {
            new BrowserInfo("chrome", "Google Chrome", PhillipsPc.ChromePath, true, null),    // installed, moved
            new BrowserInfo("opera", "Opera", null, false, "https://www.opera.com/download"),  // uninstalled
            // vivaldi: not known at all
        };

        var result = ShortcutReconciler.Reconcile(saved, new CatalogHit[0], infos);

        var chrome = result.Single(e => e.Id == "browser:chrome");
        Assert.False(chrome.Missing);
        Assert.Equal(PhillipsPc.ChromePath, chrome.Target);
        Assert.Equal(PhillipsPc.ChromePath, chrome.IconSource);
        Assert.Equal("Google Chrome", chrome.DisplayName);

        var opera = result.Single(e => e.Id == "browser:opera");
        Assert.True(opera.Missing);
        Assert.Equal(@"C:\old\opera.exe", opera.Target);   // old path stays, the entry turns grey
        Assert.True(result.Single(e => e.Id == "browser:vivaldi").Missing);
        Assert.False(result.Single(e => e.Id == "browser:default").Missing);
    }

    [Fact]
    public void Reconcile_leaves_games_untouched_and_groups_the_result()
    {
        var steam = Launcher("steam", "Steam");
        var game = Game("game:arma", "Arma");
        game.Hidden = true;
        game.Missing = true;
        var saved = new[] { steam, game, DefaultBrowser() };   // a hand-edited file with the game behind the launcher

        var result = ShortcutReconciler.Reconcile(saved, new[] { Hit("steam", "Steam", @"C:\Start\Steam.lnk") }, new BrowserInfo[0]);

        Assert.Equal(new[] { "game:arma", "launcher:steam", "browser:default" }, Ids(result));   // Game, Launcher, Browser
        var kept = result[0];
        Assert.True(kept.Hidden);
        Assert.True(kept.Missing);   // only the launch flow changes a game's Missing flag
    }

    [Fact]
    public void Reconcile_does_not_touch_its_input()
    {
        var saved = new[] { Launcher("epic", "Epic Games"), DefaultBrowser() };

        var result = ShortcutReconciler.Reconcile(saved, new CatalogHit[0], new BrowserInfo[0]);

        Assert.True(result.Single(e => e.Id == "launcher:epic").Missing);
        Assert.False(saved[0].Missing);                  // the caller's list is unchanged
        Assert.NotSame(saved[0], result[0]);
    }

    [Fact]
    public void Reconcile_is_stable_when_nothing_changed()
    {
        var hits = new[] { Hit("steam", "Steam", @"C:\Start\Steam.lnk"), Hit("epic", "Epic Games", @"C:\Start\Epic.lnk") };
        var first = ShortcutReconciler.Reconcile(new ShortcutEntry[0], hits, new BrowserInfo[0]);

        var second = ShortcutReconciler.Reconcile(first, hits, new BrowserInfo[0]);

        Assert.Equal(Ids(first), Ids(second));
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(first),
            System.Text.Json.JsonSerializer.Serialize(second));
    }

    [Fact]
    public void Reconcile_keeps_a_launcher_group_entry_it_does_not_manage()
    {
        // Something a user put into settings.json by hand: not a catalog id, so no Missing flag from the scan.
        var custom = new ShortcutEntry { Id = "custom:tool", Group = ShortcutGroup.Launcher, Kind = ShortcutKind.Exe, Target = @"C:\Tools\tool.exe", DisplayName = "Tool" };

        var result = ShortcutReconciler.Reconcile(new[] { custom }, new CatalogHit[0], new BrowserInfo[0]);

        var kept = result.Single(e => e.Id == "custom:tool");
        Assert.False(kept.Missing);
        Assert.Equal(@"C:\Tools\tool.exe", kept.Target);
    }

    // ---- recent games ----

    private static Func<string, bool> NoExclusions => _ => false;

    [Fact]
    public void RememberGame_adds_newest_first()
    {
        var s = new ShortcutSettings();
        var t0 = new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);

        ShortcutReconciler.RememberGame(s, @"D:\SteamLibrary\steamapps\common\Arma Reforger\ArmaReforgerSteam.exe", "Arma Reforger", t0, NoExclusions);
        ShortcutReconciler.RememberGame(s, @"C:\Games\World_of_Tanks_EU\WorldOfTanks.exe", "World of Tanks", t0.AddHours(1), NoExclusions);

        Assert.Equal(new[] { "World of Tanks", "Arma Reforger" }, s.RecentGames.Select(g => g.DisplayName).ToArray());
        Assert.Equal(t0.AddHours(1), s.RecentGames[0].LastSeenUtc);
    }

    [Fact]
    public void RememberGame_dedupes_and_caps_30()
    {
        var s = new ShortcutSettings();
        var t0 = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < 35; i++)
            ShortcutReconciler.RememberGame(s, $@"C:\Games\G{i}\g{i}.exe", "G" + i, t0.AddMinutes(i), NoExclusions);

        Assert.Equal(30, s.RecentGames.Count);
        Assert.Equal("G34", s.RecentGames[0].DisplayName);   // newest first
        Assert.Equal("G5", s.RecentGames[^1].DisplayName);   // the five oldest fell off

        // The same game again (other case, forward slashes) moves to the front instead of appearing twice.
        ShortcutReconciler.RememberGame(s, "c:/games/g10/G10.EXE", "G10 renamed", t0.AddHours(5), NoExclusions);

        Assert.Equal(30, s.RecentGames.Count);
        Assert.Equal("G10 renamed", s.RecentGames[0].DisplayName);
        Assert.Equal(t0.AddHours(5), s.RecentGames[0].LastSeenUtc);
        Assert.Single(s.RecentGames, g => ShortcutPaths.SamePath(g.ExePath, @"C:\Games\G10\g10.exe"));
    }

    [Fact]
    public void RememberGame_ignores_blank_paths_and_names_it_after_the_exe_when_unnamed()
    {
        var s = new ShortcutSettings();
        var t = DateTime.UtcNow;

        ShortcutReconciler.RememberGame(s, "", "Nothing", t, NoExclusions);
        ShortcutReconciler.RememberGame(s, @"C:\Games\Foo\foo.exe", "  ", t, NoExclusions);

        var only = Assert.Single(s.RecentGames);
        Assert.Equal("foo", only.DisplayName);
    }

    [Fact]
    public void RecentGames_skips_launchers_browsers_and_self()
    {
        var s = new ShortcutSettings();
        var browsers = BrowserCatalog.Detect(PhillipsPc.BrowserRegistry(), PhillipsPc.Programs());
        const string self = @"C:\Users\cr4zy\AppData\Local\Programs\WKI_Clipper\WKI_Clipper.exe";
        var excluded = ShortcutReconciler.ExclusionFilter(LauncherCatalog.LauncherExeNames, browsers, self);
        var now = DateTime.UtcNow;

        foreach (var exe in new[]
                 {
                     PhillipsPc.SteamExePath,                                   // launcher
                     @"C:\Program Files (x86)\Steam\Steam.exe",                 // launcher, other spelling
                     PhillipsPc.EpicLauncherPath,                               // launcher
                     @"C:\Program Files\Electronic Arts\EA Desktop\EA Desktop\EADesktop.exe",   // launcher helper process
                     @"C:\Games\World_of_Tanks_EU\wgc_api.exe",                 // launcher helper process
                     PhillipsPc.ChromePath,                                     // browser (detected)
                     PhillipsPc.CometPath,                                      // browser (detected)
                     @"C:\Program Files\Mozilla Firefox\firefox.exe",           // browser (not installed, still a browser)
                     @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                     self,                                                      // the clipper itself
                     @"D:\Dev\WKI_Clipper\WKI_Clipper\bin\Debug\net8.0-windows10.0.22621.0\WKI_Clipper.exe",   // a dev build of it
                 })
            ShortcutReconciler.RememberGame(s, exe, Name(exe), now, excluded);

        Assert.Empty(s.RecentGames);

        // Real games still get in.
        ShortcutReconciler.RememberGame(s, @"D:\SteamLibrary\steamapps\common\Arma Reforger\ArmaReforgerSteam.exe", "Arma Reforger", now, excluded);
        ShortcutReconciler.RememberGame(s, @"C:\Games\World_of_Tanks_EU\WorldOfTanks.exe", "World of Tanks", now, excluded);
        Assert.Equal(2, s.RecentGames.Count);
    }

    private static string Name(string exe) => System.IO.Path.GetFileNameWithoutExtension(exe);

    [Fact]
    public void Exclusion_filter_survives_null_own_path_and_blank_input()
    {
        var excluded = ShortcutReconciler.ExclusionFilter(LauncherCatalog.LauncherExeNames, new BrowserInfo[0], null);

        Assert.True(excluded(""));                       // nothing to remember
        Assert.False(excluded(@"C:\Games\Foo\foo.exe"));
        Assert.True(excluded(@"C:\Games\Foo\WKI_Clipper.exe"));   // the clipper's own exe name, wherever it runs from
    }
}
