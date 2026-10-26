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

    [Theory]
    [InlineData("https://www.foo-game.example/")]
    [InlineData("http://foo.example/download")]
    [InlineData("file:///C:/Games/Foo/foo.exe")]
    [InlineData("")]
    public void Url_shortcuts_that_open_a_page_or_a_file_are_not_candidates(string address)
    {
        // A .url has no folder to compare, and a web page or file link starts no game: only a launcher scheme counts.
        var url = new ShellLinkInfo(@"C:\Users\Public\Desktop\Foo.url", "Foo", null, null, null, address);

        var t = Resolve(@"C:\Games\Foo\foo.exe", url);

        Assert.Equal(ShortcutKind.Exe, t.Kind);
    }

    // ---- vendor shortcuts: sibling tools (server, editor, settings) are not the game ----

    [Fact]
    public void A_dedicated_server_shortcut_in_the_exe_folder_does_not_beat_the_games_own_shortcut()
    {
        // Foo.exe runs from bin\; the official start sits one level up, the server shortcut next to the exe.
        var game = PhillipsPc.Link(@"C:\Users\Public\Desktop\Foo.lnk", @"C:\Games\Foo\FooLauncher.exe");
        var server = PhillipsPc.Link(@"C:\Users\Public\Desktop\Foo Dedicated Server.lnk", @"C:\Games\Foo\bin\FooServer.exe");

        var t = Resolve(@"C:\Games\Foo\bin\Foo.exe", server, game);

        Assert.Equal(new LaunchTarget(ShortcutKind.ShellLink, game.Path, null), t);
    }

    [Theory]
    [InlineData("Foo Server", @"C:\Games\Foo\bin\FooServer.exe")]
    [InlineData("Foo Dedicated Server", @"C:\Games\Foo\bin\FooDS.exe")]
    [InlineData("Foo Editor", @"C:\Games\Foo\bin\FooEditor.exe")]
    [InlineData("Foo Settings", @"C:\Games\Foo\bin\FooSettings.exe")]
    [InlineData("Foo Config", @"C:\Games\Foo\bin\FooConfig.exe")]
    [InlineData("Foo Setup", @"C:\Games\Foo\bin\FooSetup.exe")]
    [InlineData("Foo Benchmark", @"C:\Games\Foo\bin\FooBench.exe")]
    [InlineData("Foo SDK", @"C:\Games\Foo\bin\FooSdk.exe")]
    [InlineData("Foo Tools", @"C:\Games\Foo\bin\FooTools.exe")]
    [InlineData("Foo Modkit", @"C:\Games\Foo\bin\ModKit.exe")]
    [InlineData("Foo Safe Mode", @"C:\Games\Foo\bin\FooSafeMode.exe")]
    [InlineData("Foo", @"C:\Games\Foo\bin\FooServer.exe")]   // a plain name, but the target is the server
    public void A_tool_shortcut_alone_is_never_the_start_path(string name, string target)
    {
        var tool = PhillipsPc.Link(@"C:\Users\Public\Desktop\" + name + ".lnk", target);

        var t = Resolve(@"C:\Games\Foo\bin\Foo.exe", tool);

        Assert.Equal(new LaunchTarget(ShortcutKind.Exe, @"C:\Games\Foo\bin\Foo.exe", null), t);
    }

    [Fact]
    public void The_tool_shortcut_loses_to_the_game_shortcut_at_the_same_folder_level()
    {
        // "Foo Settings" sorts before "Foo" in the same folder (a space is before a dot) - the path must not decide.
        var settings = PhillipsPc.Link(@"C:\Users\Public\Desktop\Foo Settings.lnk", @"C:\Games\Foo\FooSettings.exe");
        var game = PhillipsPc.Link(@"C:\Users\Public\Desktop\Foo.lnk", @"C:\Games\Foo\FooLauncher.exe");

        var t = Resolve(@"C:\Games\Foo\Foo.exe", settings, game);

        Assert.Equal(game.Path, t.Target);
    }

    [Fact]
    public void A_shortcut_named_exactly_like_the_game_beats_a_looser_one_that_sits_closer()
    {
        // "Foo Extras" (not a tool) is level 0, "Foo" is one level up: the name that fits better wins, then the distance.
        var extras = PhillipsPc.Link(@"C:\Users\Public\Desktop\Foo Extras.lnk", @"C:\Games\Foo\bin\FooExtras.exe");
        var game = PhillipsPc.Link(@"C:\Users\Public\Desktop\Foo.lnk", @"C:\Games\Foo\FooLauncher.exe");

        var t = Resolve(@"C:\Games\Foo\bin\Foo.exe", extras, game);

        Assert.Equal(game.Path, t.Target);
    }

    [Fact]
    public void A_tool_marker_in_the_games_own_exe_name_is_not_a_tool()
    {
        // The game IS the server (or the editor): its own shortcut must still be found.
        var shortcut = PhillipsPc.Link(@"C:\Users\Public\Desktop\Foo Server.lnk", @"C:\Games\Foo\FooServerLauncher.exe");

        var t = Resolve(@"C:\Games\Foo\FooServer.exe", shortcut);

        Assert.Equal(new LaunchTarget(ShortcutKind.ShellLink, shortcut.Path, null), t);
    }

    [Fact]
    public void A_shortcut_to_the_exe_itself_wins_even_when_its_name_has_a_tool_word()
    {
        var exact = PhillipsPc.Link(@"C:\Users\Public\Desktop\Foo Settings Free.lnk", @"C:\Games\Foo\foo.exe");

        var t = Resolve(@"C:\Games\Foo\foo.exe", exact);

        Assert.Equal(exact.Path, t.Target);
    }

    // ---- launcher-hosted games: the third start path ----

    private const string ValorantExe = @"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe";
    private const string RiotClient = @"C:\Riot Games\Riot Client\RiotClientServices.exe";
    private const string RainbowExe = @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games\Tom Clancy's Rainbow Six Siege\RainbowSix.exe";

    private static readonly ShellLinkInfo ValorantShortcut = PhillipsPc.Link(
        @"C:\Users\Public\Desktop\VALORANT.lnk", RiotClient, "--launch-product=valorant --launch-patchline=live", @"C:\Riot Games\Riot Client");
    private static readonly ShellLinkInfo LeagueShortcut = PhillipsPc.Link(
        @"C:\Users\Public\Desktop\League of Legends.lnk", RiotClient, "--launch-product=league_of_legends --launch-patchline=live", @"C:\Riot Games\Riot Client");
    private static readonly ShellLinkInfo RiotClientShortcut = PhillipsPc.Link(
        @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Riot Games\Riot Client.lnk", RiotClient, null, @"C:\Riot Games\Riot Client");
    private static readonly ShellLinkInfo RainbowUrl = new(
        @"C:\Users\Public\Desktop\Tom Clancy's Rainbow Six Siege.url", "Tom Clancy's Rainbow Six Siege", null, null, null, "uplay://launch/635/0");

    [Fact]
    public void Valorant_exe_resolves_to_the_riot_client_shortcut_not_the_raw_exe()
    {
        // The game exe refuses to run without the Riot Client (and Vanguard); the desktop shortcut starts the client with the product.
        var t = Resolve(ValorantExe, PhillipsPc.SteamLink, RiotClientShortcut, LeagueShortcut, ValorantShortcut);

        Assert.Equal(new LaunchTarget(ShortcutKind.ShellLink, ValorantShortcut.Path, null), t);
    }

    [Fact]
    public void Rainbow_six_exe_resolves_to_its_uplay_url_shortcut()
    {
        var t = Resolve(RainbowExe, PhillipsPc.UbisoftDesktopLink, PhillipsPc.UbisoftLink, RainbowUrl);

        Assert.Equal(new LaunchTarget(ShortcutKind.ShellLink, RainbowUrl.Path, null), t);
    }

    [Theory]
    [InlineData("link2ea://launchgame/1234?platform=origin")]
    [InlineData("com.epicgames.launcher://apps/abc?action=launch")]
    [InlineData("steam://rungameid/1")]
    [InlineData("UPLAY://launch/635/0")]
    public void Any_launcher_scheme_in_a_url_shortcut_named_after_the_game_counts(string address)
    {
        var url = new ShellLinkInfo(@"C:\Users\Public\Desktop\Foo.url", "Foo", null, null, null, address);

        var t = Resolve(@"C:\Games\Foo\foo.exe", url);

        Assert.Equal(new LaunchTarget(ShortcutKind.ShellLink, url.Path, null), t);
    }

    [Fact]
    public void A_launcher_shortcut_that_starts_another_game_is_not_this_games_start()
    {
        // Only the League shortcut exists: Valorant must not start League.
        var t = Resolve(ValorantExe, LeagueShortcut, RiotClientShortcut);

        Assert.Equal(new LaunchTarget(ShortcutKind.Exe, ValorantExe, null), t);
    }

    [Fact]
    public void A_launcher_shortcut_without_arguments_starts_the_launcher_not_the_game()
    {
        // Neither shortcut passes an argument: they open the Riot Client, whatever they are called.
        var named = PhillipsPc.Link(@"C:\Users\Public\Desktop\VALORANT.lnk", RiotClient, null);

        var t = Resolve(ValorantExe, named, RiotClientShortcut);

        Assert.Equal(ShortcutKind.Exe, t.Kind);
    }

    [Fact]
    public void A_shared_parent_folder_name_is_too_shallow_to_name_a_game()
    {
        // "Riot Games" sits two segments deep (C:\Riot Games): a launcher shortcut with that name and an argument says nothing about Valorant.
        var generic = PhillipsPc.Link(@"C:\Users\Public\Desktop\Riot Games.lnk", RiotClient, "--launch-product=league_of_legends");

        var t = Resolve(ValorantExe, generic);

        Assert.Equal(ShortcutKind.Exe, t.Kind);
    }

    [Fact]
    public void A_start_shortcut_in_the_games_own_tree_beats_a_launcher_shortcut()
    {
        // World of Tanks stays on wgc_api.exe --open even when a launcher-style shortcut with the game's name is around.
        var launcher = new ShellLinkInfo(@"C:\Users\Public\Desktop\World of Tanks.url", "World of Tanks", null, null, null, "wgc://launch/wot");
        var vendor = PhillipsPc.Link(@"C:\Users\Public\Desktop\World of Tanks EU.lnk", RiotClient, "--launch-product=world_of_tanks");

        var t = Resolve(@"C:\Games\World_of_Tanks_EU\WorldOfTanks.exe", launcher, vendor, PhillipsPc.WotStart);

        Assert.Equal(new LaunchTarget(ShortcutKind.ShellLink, PhillipsPc.WotStart.Path, null), t);
    }

    [Fact]
    public void An_exact_shortcut_beats_an_in_tree_one_which_beats_a_launcher_shortcut()
    {
        var exe = @"C:\Games\Foo\bin\Foo.exe";
        var launcher = new ShellLinkInfo(@"C:\Users\Public\Desktop\Foo.url", "Foo", null, null, null, "uplay://launch/1/0");
        var inTree = PhillipsPc.Link(@"C:\Users\Public\Desktop\Foo Game.lnk", @"C:\Games\Foo\FooLauncher.exe");
        var exact = PhillipsPc.Link(@"C:\Users\Public\Desktop\Anything.lnk", exe);

        Assert.Equal(inTree.Path, Resolve(exe, launcher, inTree).Target);
        Assert.Equal(exact.Path, Resolve(exe, launcher, inTree, exact).Target);
        Assert.Equal(launcher.Path, Resolve(exe, launcher).Target);
    }

    [Fact]
    public void Uninstall_and_helper_filters_still_apply_to_launcher_shortcuts()
    {
        var uninstall = PhillipsPc.Link(@"C:\Users\Public\Desktop\VALORANT deinstallieren.lnk", RiotClient, "--uninstall-product=valorant");
        var updater = PhillipsPc.Link(@"C:\Users\Public\Desktop\VALORANT Updater.lnk", RiotClient, "--update=valorant");
        var settings = PhillipsPc.Link(@"C:\Users\Public\Desktop\VALORANT Settings.lnk", RiotClient, "--settings=valorant");
        var urlUninstall = new ShellLinkInfo(@"C:\Users\Public\Desktop\VALORANT.url", "VALORANT", null, null, null, "riot://uninstall/valorant");

        var t = Resolve(ValorantExe, uninstall, updater, settings, urlUninstall);

        Assert.Equal(ShortcutKind.Exe, t.Kind);
    }

    [Fact]
    public void Steam_and_epic_still_win_over_a_launcher_shortcut()
    {
        var url = new ShellLinkInfo(@"C:\Users\Public\Desktop\Arma Reforger.url", "Arma Reforger", null, null, null, "steam://rungameid/1874880");

        var arma = Resolve(@"D:\SteamLibrary\steamapps\common\Arma Reforger\ArmaReforgerSteam.exe", url);
        var spider = Resolve(@"D:\Program Files\Epic Games\MarvelsSpiderManRemaster\Spider-Man.exe",
            new ShellLinkInfo(@"C:\Users\Public\Desktop\Spider-Man.url", "Spider-Man", null, null, null, "com.epicgames.launcher://apps/x?action=launch"));

        Assert.Equal(new LaunchTarget(ShortcutKind.SteamApp, "1874880", null), arma);
        Assert.Equal(new LaunchTarget(ShortcutKind.EpicApp, "be23672deb69402781cd47cc2919caf4", null), spider);
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
