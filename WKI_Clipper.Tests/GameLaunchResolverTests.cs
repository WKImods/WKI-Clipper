using System.Collections.Generic;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class GameLaunchResolverTests
{
    private static readonly IReadOnlyList<SteamApp> Steam = SteamFromPhillipsPc();
    private static readonly IReadOnlyList<EpicApp> Epic = GameLibraries.EpicApps(PhillipsPc.EpicManifests, PhillipsPc.Epic());

    private static IReadOnlyList<SteamApp> SteamFromPhillipsPc()
    {
        var fs = PhillipsPc.Steam();
        return GameLibraries.SteamApps(GameLibraries.SteamLibraries(PhillipsPc.SteamRoot, fs), fs);
    }

    private static LaunchTarget Resolve(string exe, params ShellLinkInfo[] links)
        => GameLaunchResolver.Resolve(exe, Steam, Epic, links, PhillipsPc.LauncherExeNames);

    // ---- Steam ----

    [Fact]
    public void Arma_exe_resolves_to_steam_1874880()
    {
        var t = Resolve(@"D:\SteamLibrary\steamapps\common\Arma Reforger\ArmaReforgerSteam.exe");

        Assert.Equal(new LaunchTarget(ShortcutKind.SteamApp, "1874880", null), t);
    }

    [Theory]
    [InlineData(@"d:\steamlibrary\steamapps\common\arma reforger\ArmaReforgerSteam.exe")]
    [InlineData(@"D:/SteamLibrary/steamapps/common/Arma Reforger/ArmaReforgerSteam.exe")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Arma Reforger\Bin\Win64\ArmaReforgerSteam.exe")]
    [InlineData(@"D:\\SteamLibrary\\steamapps\\common\\Arma Reforger\\ArmaReforgerSteam.exe")]
    public void Steam_game_on_second_library_with_different_case_resolves_appid(string exe)
    {
        // The library as Steam wrote it down, with another case and a trailing slash than the process path.
        var apps = new[] { new SteamApp(1874880, "Arma Reforger", "Arma Reforger", @"d:\steamlibrary\") };

        var t = GameLaunchResolver.Resolve(exe, apps, Epic, new ShellLinkInfo[0], PhillipsPc.LauncherExeNames);

        Assert.Equal(new LaunchTarget(ShortcutKind.SteamApp, "1874880", null), t);
    }

    [Fact]
    public void Steam_game_resolves_end_to_end_when_libraryfolders_spells_the_path_differently()
    {
        var fs = new FakeFileSystem()
            .Add(PhillipsPc.SteamRoot + @"\steamapps\libraryfolders.vdf",
                 "\"libraryfolders\" { \"0\" { \"path\" \"C:\\\\Program Files (x86)\\\\Steam\" } \"1\" { \"path\" \"d:\\\\steamlibrary\\\\\" } }")
            .Add(@"D:\SteamLibrary\steamapps\appmanifest_1874880.acf", PhillipsPc.Acf(1874880, "Arma Reforger", "Arma Reforger"));
        var apps = GameLibraries.SteamApps(GameLibraries.SteamLibraries(PhillipsPc.SteamRoot, fs), fs);

        var t = GameLaunchResolver.Resolve(@"D:\SteamLibrary\steamapps\common\Arma Reforger\ArmaReforgerSteam.exe",
            apps, Epic, new ShellLinkInfo[0], PhillipsPc.LauncherExeNames);

        Assert.Equal(new LaunchTarget(ShortcutKind.SteamApp, "1874880", null), t);
    }

    [Fact]
    public void Sibling_folder_with_a_longer_name_is_not_the_steam_game()
    {
        // "Arma Reforger Tools" starts with "Arma Reforger" but is another install folder.
        var t = Resolve(@"D:\SteamLibrary\steamapps\common\Arma Reforger Tools\Workbench\ArmaReforgerWorkbenchSteam.exe");

        Assert.Equal(ShortcutKind.Exe, t.Kind);
    }

    [Fact]
    public void Steam_app_with_empty_installdir_never_matches_everything_under_common()
    {
        var apps = new[] { new SteamApp(5, "Broken", "", PhillipsPc.SteamLibraryD) };

        var t = GameLaunchResolver.Resolve(@"D:\SteamLibrary\steamapps\common\Other\other.exe",
            apps, Epic, new ShellLinkInfo[0], PhillipsPc.LauncherExeNames);

        Assert.Equal(ShortcutKind.Exe, t.Kind);
    }

    [Fact]
    public void Steam_wins_over_a_vendor_shortcut()
    {
        var desktop = PhillipsPc.Link(@"C:\Users\Public\Desktop\Arma Reforger.lnk",
            @"D:\SteamLibrary\steamapps\common\Arma Reforger\ArmaReforgerSteam.exe");

        var t = Resolve(@"D:\SteamLibrary\steamapps\common\Arma Reforger\ArmaReforgerSteam.exe", desktop);

        Assert.Equal(new LaunchTarget(ShortcutKind.SteamApp, "1874880", null), t);
    }

    // ---- Epic ----

    [Fact]
    public void Spiderman_exe_resolves_to_epic_appname()
    {
        var t = Resolve(@"D:\Program Files\Epic Games\MarvelsSpiderManRemaster\Spider-Man.exe");

        Assert.Equal(new LaunchTarget(ShortcutKind.EpicApp, "be23672deb69402781cd47cc2919caf4", null), t);
    }

    [Fact]
    public void Fortnite_shipping_exe_deep_in_the_install_resolves_to_epic()
    {
        var t = Resolve(@"D:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe");

        Assert.Equal(new LaunchTarget(ShortcutKind.EpicApp, "Fortnite", null), t);
    }

    // ---- vendor shortcuts ----

    [Fact]
    public void Wot_exe_resolves_to_wgc_api_open_shortcut()
    {
        // Real order of the scan: the uninstall shortcut and a helper from the same folder come BEFORE the start shortcut.
        var t = Resolve(@"C:\Games\World_of_Tanks_EU\WorldOfTanks.exe",
            PhillipsPc.WotUninstall, PhillipsPc.AslainLogsArchiver, PhillipsPc.WargamingGameCenter,
            PhillipsPc.SteamLink, PhillipsPc.WotStart);

        Assert.Equal(new LaunchTarget(ShortcutKind.ShellLink, PhillipsPc.WotStart.Path, null), t);
    }

    [Fact]
    public void Wot_exe_in_the_win64_subfolder_still_finds_the_shortcut_one_level_up()
    {
        var t = Resolve(@"C:\Games\World_of_Tanks_EU\win64\WorldOfTanks.exe", PhillipsPc.WotUninstall, PhillipsPc.WotStart);

        Assert.Equal(new LaunchTarget(ShortcutKind.ShellLink, PhillipsPc.WotStart.Path, null), t);
    }

    [Fact]
    public void Uninstall_shortcut_is_never_a_launch_target()
    {
        // Only the uninstall shortcut exists: the exe itself is the answer, not "wgc_api.exe --uninstall".
        var t = Resolve(@"C:\Games\World_of_Tanks_EU\WorldOfTanks.exe", PhillipsPc.WotUninstall);

        Assert.Equal(new LaunchTarget(ShortcutKind.Exe, @"C:\Games\World_of_Tanks_EU\WorldOfTanks.exe", null), t);
    }

    [Theory]
    [InlineData(@"C:\X\Uninstall Foo.lnk", @"C:\Games\Foo\foo.exe", null)]
    [InlineData(@"C:\X\Foo deinstallieren.lnk", @"C:\Games\Foo\foo.exe", null)]
    [InlineData(@"C:\X\Foo.lnk", @"C:\Games\Foo\foo.exe", "--uninstall")]
    [InlineData(@"C:\X\Foo.lnk", @"C:\Games\Foo\foo.exe", "/UNINSTALL /silent")]
    [InlineData(@"C:\X\Foo.lnk", @"C:\Games\Foo\unins000.exe", null)]
    [InlineData(@"C:\X\Foo.lnk", @"C:\Games\Foo\Uninstaller.exe", null)]
    public void Uninstall_markers_in_name_arguments_or_target_are_recognised(string lnk, string target, string? args)
        => Assert.True(GameLaunchResolver.IsUninstallShortcut(PhillipsPc.Link(lnk, target, args)));

    [Theory]
    [InlineData(@"C:\X\World of Tanks EU.lnk", @"C:\Games\World_of_Tanks_EU\wgc_api.exe", "--open")]
    [InlineData(@"C:\X\Steam.lnk", @"C:\Program Files (x86)\Steam\Steam.exe", null)]
    [InlineData(@"C:\X\Foo.lnk", @"C:\Games\Foo\foo.exe", null)]
    public void Ordinary_shortcuts_are_not_uninstall_shortcuts(string lnk, string target, string? args)
        => Assert.False(GameLaunchResolver.IsUninstallShortcut(PhillipsPc.Link(lnk, target, args)));

    [Fact]
    public void Uninstall_markers_in_the_target_folder_do_not_count()
    {
        // Only the file name of the target is looked at, not its folders: a game in "C:\Games\Unins Quest\" is fine.
        var plain = PhillipsPc.Link(@"C:\X\Quest.lnk", @"C:\Games\Unins Quest\quest.exe");
        Assert.False(GameLaunchResolver.IsUninstallShortcut(plain));
    }

    [Theory]
    [InlineData(@"C:\X\Foo Updater.lnk", @"C:\Games\Foo\FooUpdater.exe")]
    [InlineData(@"C:\X\Foo Error Reporter.lnk", @"C:\Games\Foo\ErrorReporter.exe")]
    [InlineData(@"C:\X\Foo Repair.lnk", @"C:\Games\Foo\FooRepair.exe")]
    [InlineData(@"C:\X\Foo Readme.lnk", @"C:\Games\Foo\readme.html")]
    public void Updater_reporter_and_repair_shortcuts_are_not_launch_targets(string lnk, string target)
    {
        var t = Resolve(@"C:\Games\Foo\foo.exe", PhillipsPc.Link(lnk, target));

        Assert.Equal(ShortcutKind.Exe, t.Kind);
    }

    [Fact]
    public void Launcher_shortcut_in_a_shared_folder_is_not_a_launch_target()
    {
        // "Racer" sits one folder below a shared folder that also holds a launcher; the shortcut is named after the game.
        var shortcut = PhillipsPc.Link(@"C:\Users\Public\Desktop\Racer.lnk", @"C:\Apps\Shared\Launcher.exe");

        var t = Resolve(@"C:\Apps\Shared\Racer\Racer.exe", shortcut);

        Assert.Equal(ShortcutKind.Exe, t.Kind);
    }

    [Fact]
    public void Launcher_stub_in_the_games_own_folder_is_allowed()
    {
        // wgc_api.exe is on the launcher list (it must not become a "recent game"), yet in World_of_Tanks_EU it IS the game's start.
        var shortcut = PhillipsPc.Link(@"C:\Users\Public\Desktop\Racer.lnk", @"C:\Apps\Shared\Racer\Launcher.exe");

        var t = Resolve(@"C:\Apps\Shared\Racer\Racer.exe", shortcut);

        Assert.Equal(new LaunchTarget(ShortcutKind.ShellLink, shortcut.Path, null), t);
    }

    [Fact]
    public void Shortcut_to_the_exe_itself_is_a_vendor_shortcut()
    {
        var desktop = PhillipsPc.Link(@"C:\Users\Public\Desktop\Whatever Name.lnk", @"C:\Games\Foo\foo.exe");

        var t = Resolve(@"C:\Games\Foo\foo.exe", desktop);

        Assert.Equal(new LaunchTarget(ShortcutKind.ShellLink, desktop.Path, null), t);
    }

    [Fact]
    public void Exact_shortcut_beats_a_related_stub()
    {
        var stub = PhillipsPc.Link(@"C:\Users\Public\Desktop\Foo Tools.lnk", @"C:\Games\Foo\FooTools.exe");
        var exact = PhillipsPc.Link(@"C:\Users\Public\Desktop\Foo.lnk", @"C:\Games\Foo\foo.exe");

        var t = Resolve(@"C:\Games\Foo\foo.exe", stub, exact);

        Assert.Equal(exact.Path, t.Target);
    }

    [Fact]
    public void Unrelated_helper_shortcut_in_the_game_folder_is_ignored()
    {
        // "Aslains Logs Archiver" lives in the WoT folder but is not the game.
        var t = Resolve(@"C:\Games\World_of_Tanks_EU\WorldOfTanks.exe", PhillipsPc.AslainLogsArchiver);

        Assert.Equal(ShortcutKind.Exe, t.Kind);
    }

    [Fact]
    public void Shortcut_target_in_a_shared_root_folder_is_ignored()
    {
        var shortcut = PhillipsPc.Link(@"C:\Users\Public\Desktop\Foo.lnk", @"C:\Games\Foo.exe");

        var t = Resolve(@"C:\Games\Foo\Foo.exe", shortcut);

        Assert.Equal(ShortcutKind.Exe, t.Kind);
    }

    [Fact]
    public void Shortcut_far_above_the_exe_is_ignored()
    {
        var shortcut = PhillipsPc.Link(@"C:\Users\Public\Desktop\Foo.lnk", @"C:\Games\Publisher\Foo\foo_launcher.exe");

        var t = Resolve(@"C:\Games\Publisher\Foo\bin\x64\release\Foo.exe", shortcut);

        Assert.Equal(ShortcutKind.Exe, t.Kind);
    }

    [Fact]
    public void Url_shortcuts_have_no_folder_and_are_not_candidates()
    {
        var url = new ShellLinkInfo(@"C:\Users\Public\Desktop\Foo.url", "Foo", null, null, null, "steam://rungameid/1");

        var t = Resolve(@"C:\Games\Foo\foo.exe", url);

        Assert.Equal(ShortcutKind.Exe, t.Kind);
    }

    // ---- fallback ----

    [Fact]
    public void Unknown_exe_falls_back_to_exe()
    {
        var t = Resolve(@"E:\Portable\Tool\tool.exe", PhillipsPc.WotStart, PhillipsPc.SteamLink);

        Assert.Equal(new LaunchTarget(ShortcutKind.Exe, @"E:\Portable\Tool\tool.exe", null), t);
    }

    [Fact]
    public void Empty_exe_path_falls_back_without_throwing()
    {
        var t = Resolve("", PhillipsPc.WotStart);

        Assert.Equal(new LaunchTarget(ShortcutKind.Exe, "", null), t);
    }
}
