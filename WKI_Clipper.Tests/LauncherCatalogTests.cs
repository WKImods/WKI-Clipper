using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class LauncherCatalogTests
{
    private static IReadOnlyList<CatalogHit> Detect(
        IReadOnlyList<ShellLinkInfo>? links = null, FakeRegistry? reg = null, FakeFileSystem? fs = null, bool xbox = false)
        => LauncherCatalog.Detect(links ?? PhillipsPc.StartMenuLinks(), reg ?? new FakeRegistry(), fs ?? PhillipsPc.Programs(), _ => xbox);

    private static CatalogHit Hit(IReadOnlyList<CatalogHit> hits, string id) => Assert.Single(hits, h => h.Id == id);

    [Fact]
    public void Rockstar_matches_launcherpatcher_shortcut()
    {
        var hit = Hit(Detect(), "rockstar");

        Assert.Equal("Rockstar Games", hit.Name);
        Assert.Equal(ShortcutKind.CatalogLauncher, hit.Kind);
        // The Start-menu shortcut (not the desktop copy, not the bare exe) is the start path.
        Assert.Equal(PhillipsPc.RockstarLink.Path, hit.Target);
        Assert.Equal(PhillipsPc.RockstarLink.Path, hit.IconSource);
    }

    [Fact]
    public void Ea_matches_ealauncher_not_updater()
    {
        // The scan lists "App Recovery" (EALauncher.exe -recovery), both updaters and the error reporter BEFORE "EA.lnk".
        var hit = Hit(Detect(), "ea");

        Assert.Equal("EA app", hit.Name);
        Assert.Equal(PhillipsPc.EaLink.Path, hit.Target);
    }

    [Theory]
    [InlineData("EA Updater")]
    [InlineData("EA app-Updater")]
    [InlineData("EA Error Reporter")]
    [InlineData("EA deinstallieren")]
    [InlineData("Uninstall EA")]
    public void Shortcuts_named_updater_reporter_or_deinstall_never_match_a_row(string name)
    {
        // Even when such a shortcut points at the launcher exe itself.
        var bad = PhillipsPc.Link(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\EA\" + name + ".lnk", PhillipsPc.EaLauncherPath);

        var hits = Detect(new[] { bad });

        Assert.DoesNotContain(hits, h => h.Id == "ea");
    }

    [Fact]
    public void Ea_game_shortcuts_with_arguments_do_not_match()
    {
        var hits = Detect(new[] { PhillipsPc.EaAppRecovery });

        Assert.DoesNotContain(hits, h => h.Id == "ea");
    }

    [Fact]
    public void Gog_game_shortcut_is_not_the_gog_launcher()
    {
        // "Cyberpunk 2077.lnk" targets GalaxyClient.exe with /command=runGame ...: it starts a game, not GOG GALAXY.
        var onlyGame = Detect(new[] { PhillipsPc.GogCyberpunk });
        Assert.DoesNotContain(onlyGame, h => h.Id == "gog");

        var both = Hit(Detect(new[] { PhillipsPc.GogCyberpunk, PhillipsPc.GogLink }), "gog");
        Assert.Equal(PhillipsPc.GogLink.Path, both.Target);
        Assert.Equal("GOG GALAXY", both.Name);
    }

    [Fact]
    public void Epic_matches_win32_shortcut()
    {
        var hit = Hit(Detect(), "epic");

        Assert.Equal("Epic Games", hit.Name);
        Assert.Equal(PhillipsPc.EpicLink.Path, hit.Target);
    }

    [Fact]
    public void Start_menu_shortcut_is_preferred_over_a_desktop_copy()
    {
        // Steam: the desktop copy is listed first, the Start-menu one later - and the target spelling differs (Steam.exe/steam.exe).
        var steam = Hit(Detect(), "steam");
        Assert.Equal(PhillipsPc.SteamStartMenuLink.Path, steam.Target);

        var ubi = Hit(Detect(), "ubisoft");
        Assert.Equal(PhillipsPc.UbisoftLink.Path, ubi.Target);
    }

    [Fact]
    public void Real_pc_yields_all_catalog_launchers_but_xbox_without_the_package()
    {
        var hits = Detect();

        Assert.Equal(
            new[] { "steam", "ea", "epic", "gog", "ubisoft", "battlenet", "rockstar", "wgc" },
            hits.Select(h => h.Id).ToArray());
        Assert.Contains(hits, h => h.Id == "wgc" && h.Name == "Wargaming.net Game Center" && h.Target == PhillipsPc.WargamingGameCenter.Path);
        Assert.Contains(hits, h => h.Id == "battlenet" && h.Name == "Battle.net" && h.Target == PhillipsPc.BattleNetLink.Path);
        Assert.Contains(hits, h => h.Id == "ubisoft" && h.Name == "Ubisoft Connect");
        Assert.All(hits, h => Assert.Equal(ShortcutKind.CatalogLauncher, h.Kind));
    }

    [Fact]
    public void World_of_tanks_start_shortcut_is_not_a_launcher_row()
    {
        // wgc_api.exe --open belongs to the game; only wgc.exe (the Game Center) is the launcher.
        var wgc = Hit(Detect(), "wgc");

        Assert.Equal(PhillipsPc.WargamingGameCenter.Path, wgc.Target);
    }

    [Fact]
    public void Xbox_store_app_detected_when_package_installed()
    {
        string? asked = null;
        var hits = LauncherCatalog.Detect(PhillipsPc.StartMenuLinks(), new FakeRegistry(), PhillipsPc.Programs(),
            aumid => { asked = aumid; return true; });

        var xbox = Hit(hits, "xbox");
        Assert.Equal("Xbox", xbox.Name);
        Assert.Equal(ShortcutKind.StoreApp, xbox.Kind);
        Assert.Equal(PhillipsPc.XboxAumid, xbox.Target);
        Assert.Equal(PhillipsPc.XboxAumid, asked);
        Assert.Equal(@"shell:AppsFolder\" + PhillipsPc.XboxAumid, xbox.IconSource);
        Assert.Equal("xbox", hits[^1].Id);   // catalog order: Xbox is last
    }

    [Fact]
    public void Xbox_is_absent_without_the_package()
        => Assert.DoesNotContain(Detect(xbox: false), h => h.Id == "xbox");

    [Fact]
    public void Shortcut_whose_target_is_gone_is_not_an_installed_launcher()
    {
        // A leftover Start-menu entry of an uninstalled program must not show up as a working launcher.
        var fs = new FakeFileSystem();   // no exe on disk at all

        Assert.Empty(Detect(fs: fs));
    }

    [Fact]
    public void Steam_falls_back_to_the_registry_exe_without_a_shortcut()
    {
        var reg = new FakeRegistry().Set("HKCU", @"Software\Valve\Steam", "SteamExe", "c:/program files (x86)/steam/steam.exe");

        var hit = Hit(Detect(links: new ShellLinkInfo[0], reg: reg), "steam");

        Assert.Equal(ShortcutKind.CatalogLauncher, hit.Kind);
        Assert.Equal(@"c:\program files (x86)\steam\steam.exe", hit.Target);   // slashes fixed, exists case-insensitively
        Assert.Equal(hit.Target, hit.IconSource);
    }

    [Fact]
    public void Ea_falls_back_to_the_registry_launcher_path_without_a_shortcut()
    {
        var reg = new FakeRegistry().Set("HKLM", @"SOFTWARE\Electronic Arts\EA Desktop", "LauncherAppPath", PhillipsPc.EaLauncherPath);

        var hit = Hit(Detect(links: new ShellLinkInfo[0], reg: reg), "ea");

        Assert.Equal(PhillipsPc.EaLauncherPath, hit.Target);
    }

    [Fact]
    public void Registry_fallback_is_ignored_when_the_exe_is_gone()
    {
        var reg = new FakeRegistry().Set("HKCU", @"Software\Valve\Steam", "SteamExe", @"E:\Gone\steam.exe");

        Assert.DoesNotContain(Detect(links: new ShellLinkInfo[0], reg: reg), h => h.Id == "steam");
    }

    [Fact]
    public void Launcher_names_contain_every_catalog_target_and_the_extra_helper_processes()
    {
        var names = LauncherCatalog.LauncherExeNames;

        foreach (var n in new[] { "steam.exe", "EALauncher.exe", "EpicGamesLauncher.exe", "GalaxyClient.exe", "UbisoftConnect.exe",
                                  "Battle.net Launcher.exe", "LauncherPatcher.exe", "wgc.exe",
                                  "EADesktop.exe", "Battle.net.exe", "Launcher.exe", "wgc_api.exe" })
            Assert.Contains(n, names);

        Assert.Contains("STEAM.EXE", names);   // case-insensitive
        Assert.DoesNotContain("ArmaReforgerSteam.exe", names);
    }
}
