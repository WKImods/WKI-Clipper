using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>The logic behind the edit dialog (rows, game sources, browser picks) on the facts of Phillip's PC; no window involved.</summary>
public sealed class ShortcutEditorTests : IDisposable
{
    private const string ArmaExe = @"D:\SteamLibrary\steamapps\common\Arma Reforger\ArmaReforgerSteam.exe";
    private const string WotExe = @"C:\Games\World_of_Tanks_EU\WorldOfTanks.exe";
    private const string FortniteExe = @"D:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe";
    private const string ToolExe = @"E:\Portable\Tool\tool.exe";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_editor_" + Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;
    private readonly FakeShell _shell = new();
    private readonly FakeFileSystem _fs;
    private readonly FakeRegistry _reg = PhillipsPc.BrowserRegistry();
    private readonly ShortcutService _service;
    private readonly ShortcutEditorModel _model;

    public ShortcutEditorTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new SettingsService(_dir);
        _settings.Current.SchemaVersion = SettingsService.CurrentSchemaVersion;
        _settings.Current.Output.ClipsFolder = Path.Combine(_dir, "clips");
        _settings.Current.Output.ScreenshotsFolder = Path.Combine(_dir, "shots");
        _settings.Current.Output.BufferFolder = Path.Combine(_dir, "buffer");

        _fs = PhillipsPc.Steam().Merge(PhillipsPc.Epic()).Merge(PhillipsPc.Programs())
            // the games' own files: the exes that were seen in front, and the real neighbours the icon guess has to choose between
            .Add(ArmaExe).Add(@"D:\SteamLibrary\steamapps\common\Arma Reforger\ArmaReforgerSteamDiag.exe")
            .Add(@"D:\SteamLibrary\steamapps\common\Arma Reforger\ArmaReforger_BE.exe").Add(@"D:\SteamLibrary\steamapps\common\Arma Reforger\CrashReporter.exe")
            .Add(@"D:\SteamLibrary\steamapps\common\DayZ\DayZ_x64.exe").Add(@"D:\SteamLibrary\steamapps\common\DayZ\DayZ_BE.exe")
            .Add(WotExe).Add(PhillipsPc.WotStart.Path).Add(PhillipsPc.WotUninstall.Path)
            .Add(FortniteExe).Add(@"D:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteLauncher.exe")
            .Add(@"D:\Program Files\Epic Games\MarvelsSpiderManRemaster\Spider-Man.exe")
            .Add(ToolExe);

        _service = new ShortcutService(_settings, _shell, _fs, _reg, () => { }, _ => { });
        _model = new ShortcutEditorModel(_service, _shell, _fs, _reg, PhillipsPc.SteamRoot, PhillipsPc.EpicManifests, _ => null);
    }

    public void Dispose()
    {
        _service.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void Seen(string exe, string name)
        => _settings.Current.Shortcuts.RecentGames.Add(new SeenGame { ExePath = exe, DisplayName = name, LastSeenUtc = DateTime.UtcNow });

    private ShortcutEntry Entry(string id) => _settings.Current.Shortcuts.Items.Single(e => e.Id == id);

    // ---- suggestions ----

    [Fact]
    public void Suggestions_resolve_arma_to_steam_and_wot_to_shortcut()
    {
        Seen(ArmaExe, "Arma Reforger");
        Seen(WotExe, "World of Tanks");
        Seen(FortniteExe, "Fortnite");
        Seen(ToolExe, "Some Tool");

        var s = _model.Suggestions();

        Assert.Equal(new[] { "Arma Reforger", "World of Tanks", "Fortnite", "Some Tool" }, s.Select(c => c.Name).ToArray());   // as seen, newest first
        Assert.Equal(new LaunchTarget(ShortcutKind.SteamApp, "1874880", null), s[0].Target);
        Assert.Equal(new LaunchTarget(ShortcutKind.ShellLink, PhillipsPc.WotStart.Path, null), s[1].Target);   // the vendor shortcut, NOT the uninstall one
        Assert.Equal(new LaunchTarget(ShortcutKind.EpicApp, "Fortnite", null), s[2].Target);
        Assert.Equal(new LaunchTarget(ShortcutKind.Exe, ToolExe, null), s[3].Target);                          // nothing better known: the exe
        Assert.All(s, c => Assert.False(c.AlreadyAdded));
        Assert.Equal(ArmaExe, s[0].IconSource);                                                                // the logo comes from the exe that was seen
        Assert.Equal(WotExe, s[1].IconSource);
    }

    [Fact]
    public void Suggestions_skip_launchers_browsers_and_exes_that_are_gone_and_mark_what_is_on_the_bar()
    {
        Seen(PhillipsPc.SteamExePath, "Steam");                  // old data from before the exclusion rules
        Seen(PhillipsPc.ChromePath, "Google Chrome");
        Seen(@"C:\Games\Uninstalled\game.exe", "Gone");          // not on disk any more
        Seen(ArmaExe, "Arma Reforger");
        _service.AddGame(new LaunchTarget(ShortcutKind.SteamApp, "1874880", null), "Arma Reforger", ArmaExe);

        var s = _model.Suggestions();

        var arma = Assert.Single(s);
        Assert.Equal("Arma Reforger", arma.Name);
        Assert.True(arma.AlreadyAdded);                          // listed, but there is nothing left to add
    }

    [Fact]
    public void Suggestions_are_empty_without_history()
        => Assert.Empty(_model.Suggestions());

    // ---- libraries ----

    [Fact]
    public void Steam_library_list_excludes_redistributables()
    {
        var games = _model.SteamGames();

        Assert.Equal(new[] { "Arma Reforger", "DayZ", "WARDOGS" }, games.Select(g => g.Name).ToArray());   // by name; 228980 is not there
        Assert.DoesNotContain(games, g => g.Target.Target == "228980");
        Assert.All(games, g => Assert.Equal(ShortcutKind.SteamApp, g.Target.Kind));
        Assert.Equal("1874880", games.Single(g => g.Name == "Arma Reforger").Target.Target);
    }

    [Fact]
    public void Steam_games_get_the_logo_of_their_main_exe_not_of_a_helper()
    {
        var games = _model.SteamGames();

        // Arma's folder holds ArmaReforgerSteam.exe, its Diag build, the BattlEye launcher and a crash reporter.
        Assert.Equal(ArmaExe, games.Single(g => g.Name == "Arma Reforger").IconSource);
        Assert.Equal(@"D:\SteamLibrary\steamapps\common\DayZ\DayZ_x64.exe", games.Single(g => g.Name == "DayZ").IconSource);
        Assert.Null(games.Single(g => g.Name == "WARDOGS").IconSource);   // no exe found: the bar shows a letter tile instead
    }

    [Fact]
    public void Epic_list_reads_the_manifests_with_the_launch_exe_as_logo()
    {
        var games = _model.EpicGames();

        Assert.Equal(new[] { "Fortnite", "Marvel's Spider-Man Remastered" }, games.Select(g => g.Name).ToArray());
        var fortnite = games.First();
        Assert.Equal(new LaunchTarget(ShortcutKind.EpicApp, "Fortnite", null), fortnite.Target);
        Assert.Equal(@"D:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteLauncher.exe", fortnite.IconSource);
        Assert.Equal(@"D:\Program Files\Epic Games\MarvelsSpiderManRemaster\Spider-Man.exe", games[1].IconSource);
    }

    [Fact]
    public void Listing_a_game_that_is_on_the_bar_marks_it()
    {
        _service.AddGame(new LaunchTarget(ShortcutKind.SteamApp, "221100", null), "DayZ", null);

        var games = _model.SteamGames();

        Assert.True(games.Single(g => g.Name == "DayZ").AlreadyAdded);
        Assert.False(games.Single(g => g.Name == "WARDOGS").AlreadyAdded);
    }

    [Fact]
    public void Without_steam_or_epic_the_lists_are_empty()
    {
        var bare = new ShortcutEditorModel(_service, _shell, new FakeFileSystem(), new FakeRegistry(), @"X:\NoSteam", @"X:\NoEpic", _ => null);

        Assert.Empty(bare.SteamGames());
        Assert.Empty(bare.EpicGames());
        Assert.Empty(bare.Suggestions());
    }

    // ---- adding ----

    [Fact]
    public void Adding_a_choice_puts_the_game_on_the_bar()
    {
        var choice = _model.SteamGames().Single(g => g.Name == "DayZ");

        _model.AddGame(choice);
        _model.AddGame(choice);   // twice is still one

        var game = Assert.Single(_settings.Current.Shortcuts.Items, e => e.Group == ShortcutGroup.Game);
        Assert.Equal("DayZ", game.DisplayName);
        Assert.Equal(ShortcutKind.SteamApp, game.Kind);
        Assert.Equal("221100", game.Target);
        Assert.Equal(@"D:\SteamLibrary\steamapps\common\DayZ\DayZ_x64.exe", game.IconSource);
    }

    [Fact]
    public void A_picked_file_goes_through_the_same_resolver_as_a_suggestion()
    {
        Assert.Equal(AddFileResult.Added, _model.AddGameFromFile(ArmaExe));                       // an exe in a Steam library: Steam starts it
        Assert.Equal(AddFileResult.Added, _model.AddGameFromFile(WotExe));                        // WoT: the vendor's start shortcut
        Assert.Equal(AddFileResult.Added, _model.AddGameFromFile(ToolExe));                       // unknown: the exe itself
        Assert.Equal(AddFileResult.Added, _model.AddGameFromFile(PhillipsPc.WotStart.Path));      // a shortcut: itself

        var games = _settings.Current.Shortcuts.Items.Where(e => e.Group == ShortcutGroup.Game).ToArray();
        Assert.Equal(new[] { ShortcutKind.SteamApp, ShortcutKind.ShellLink, ShortcutKind.Exe }, games.Select(g => g.Kind).ToArray());   // WoT exe and WoT shortcut are the same entry
        Assert.Equal(PhillipsPc.WotStart.Path, games[1].Target);
        Assert.Equal(ToolExe, games[2].Target);
        Assert.Equal("tool", games[2].DisplayName);                                               // no product name known: the file name
    }

    [Fact]
    public void A_picked_exe_takes_its_product_name_when_it_has_one()
    {
        var named = new ShortcutEditorModel(_service, _shell, _fs, _reg, PhillipsPc.SteamRoot, PhillipsPc.EpicManifests,
            path => path == ToolExe ? "Super Tool" : null);

        named.AddGameFromFile(ToolExe);

        Assert.Equal("Super Tool", _settings.Current.Shortcuts.Items.Single(e => e.Group == ShortcutGroup.Game).DisplayName);
    }

    [Fact]
    public void An_uninstall_shortcut_is_never_added_as_a_game()
    {
        Assert.Equal(AddFileResult.Uninstaller, _model.AddGameFromFile(PhillipsPc.WotUninstall.Path));
        Assert.DoesNotContain(_settings.Current.Shortcuts.Items, e => e.Group == ShortcutGroup.Game);
    }

    [Fact]
    public void Missing_and_unsupported_files_are_refused()
    {
        Assert.Equal(AddFileResult.NotFound, _model.AddGameFromFile(@"C:\Gone\game.exe"));
        Assert.Equal(AddFileResult.Unsupported, _model.AddGameFromFile(@"C:\Games\Foo\readme.txt"));
        Assert.Equal(AddFileResult.NotFound, _model.AddGameFromFile(""));
        Assert.DoesNotContain(_settings.Current.Shortcuts.Items, e => e.Group == ShortcutGroup.Game);
    }

    // ---- rows ----

    [Fact]
    public async Task Rows_report_order_hidden_missing_hotkey_and_what_may_be_removed()
    {
        await _service.RescanAsync();
        _service.AddGame(new LaunchTarget(ShortcutKind.SteamApp, "1874880", null), "Arma Reforger", ArmaExe);
        _service.AddGame(new LaunchTarget(ShortcutKind.SteamApp, "221100", null), "DayZ", null);
        _service.SetHidden("launcher:ea", true);
        Entry("launcher:rockstar").Missing = true;
        var gameId = _settings.Current.Shortcuts.Items.First(e => e.Group == ShortcutGroup.Game).Id;
        _service.SetHotkey(gameId, new HotkeyBinding { Modifiers = HotkeyModifier.Control | HotkeyModifier.Alt, Key = 0x31 });

        var games = _model.Rows(ShortcutGroup.Game);
        var launchers = _model.Rows(ShortcutGroup.Launcher);
        var browsers = _model.Rows(ShortcutGroup.Browser);

        Assert.Equal(new[] { "Arma Reforger", "DayZ" }, games.Select(r => r.Name).ToArray());
        Assert.True(games[0].CanRemove);                      // games can be removed ...
        Assert.False(games[0].CanMoveUp);
        Assert.True(games[0].CanMoveDown);
        Assert.False(games[1].CanMoveDown);
        Assert.Equal(0x31u, games[0].Hotkey!.Key);
        Assert.Null(games[1].Hotkey);                         // unbound by default

        Assert.Equal(8, launchers.Count);
        Assert.All(launchers, r => Assert.False(r.CanRemove));   // ... launchers only be hidden
        Assert.True(launchers.Single(r => r.Id == "launcher:ea").Hidden);
        Assert.True(launchers.Single(r => r.Id == "launcher:rockstar").Missing);
        Assert.Equal(@"C:\icons\launcher_steam.png", launchers.Single(r => r.Id == "launcher:steam").IconPng);

        var only = Assert.Single(browsers);
        Assert.True(only.IsDefaultBrowser);
        Assert.False(only.CanRemove);
        Assert.False(only.CanMoveUp);
        Assert.False(only.CanMoveDown);
    }

    [Fact]
    public void Nothing_moves_above_the_default_browser_and_picked_browsers_can_be_removed()
    {
        _service.AddBrowser(_service.DetectBrowsers().Single(b => b.Id == "chrome"));
        _service.AddBrowser(_service.DetectBrowsers().Single(b => b.Id == "comet"));

        var rows = _model.Rows(ShortcutGroup.Browser);

        Assert.Equal(new[] { "browser:default", "browser:chrome", "browser:comet" }, rows.Select(r => r.Id).ToArray());
        Assert.False(rows[1].CanMoveUp);     // would pass the default browser
        Assert.True(rows[1].CanMoveDown);
        Assert.True(rows[2].CanMoveUp);
        Assert.False(rows[2].CanMoveDown);
        Assert.True(rows[1].CanRemove);
        Assert.True(rows[2].CanRemove);
    }

    [Fact]
    public void The_model_acts_on_rows_through_the_service()
    {
        _service.AddGame(new LaunchTarget(ShortcutKind.SteamApp, "1", null), "A", null);
        _service.AddGame(new LaunchTarget(ShortcutKind.SteamApp, "2", null), "B", null);
        var a = _model.Rows(ShortcutGroup.Game)[0].Id;

        _model.Move(a, +1);
        Assert.Equal(new[] { "B", "A" }, _model.Rows(ShortcutGroup.Game).Select(r => r.Name).ToArray());

        _model.SetHidden(a, true);
        Assert.True(_model.Rows(ShortcutGroup.Game).Single(r => r.Id == a).Hidden);

        _model.Remove(a);
        Assert.Equal(new[] { "B" }, _model.Rows(ShortcutGroup.Game).Select(r => r.Name).ToArray());
    }

    [Fact]
    public void A_hotkey_collision_is_reported_with_its_owner()
    {
        _service.AddGame(new LaunchTarget(ShortcutKind.SteamApp, "1", null), "Arma", null);
        _service.AddGame(new LaunchTarget(ShortcutKind.SteamApp, "2", null), "DayZ", null);
        var ids = _model.Rows(ShortcutGroup.Game).Select(r => r.Id).ToArray();
        var combo = new HotkeyBinding { Modifiers = HotkeyModifier.Control | HotkeyModifier.Alt, Key = 0x31 };

        Assert.Null(_model.HotkeyCollision(ids[0], combo));            // free
        _model.SetHotkey(ids[0], combo);

        Assert.Contains("Arma", _model.HotkeyCollision(ids[1], combo));   // taken by the other entry
        Assert.Null(_model.HotkeyCollision(ids[0], combo));            // an entry may keep its own
        Assert.Contains("SaveReplay", _model.HotkeyCollision(ids[1], new HotkeyBinding { Modifiers = HotkeyModifier.None, Key = 0x78 }));   // F9 is the clipper's
    }

    // ---- browsers ----

    [Fact]
    public void The_default_browser_selector_lists_the_windows_default_first_then_the_installed_browsers()
    {
        var options = _model.DefaultBrowserOptions();

        Assert.Null(options[0].Id);                          // null = follow Windows
        Assert.Contains("Comet", options[0].Label);          // and it says who that is right now
        Assert.Equal(new[] { "chrome", "comet", "brave" }, options.Skip(1).Select(o => o.Id).ToArray());   // installed only: Opera and Firefox are not
        Assert.Null(_model.SelectedDefaultBrowserId);
    }

    [Fact]
    public void The_default_browser_choice_is_kept()
    {
        _model.SetDefaultBrowser("chrome");

        Assert.Equal("chrome", _model.SelectedDefaultBrowserId);
        Assert.Equal("chrome", _settings.Current.Shortcuts.DefaultBrowserId);
        _model.SetDefaultBrowser(null);
        Assert.Null(_settings.Current.Shortcuts.DefaultBrowserId);
    }

    [Fact]
    public void Browsers_to_add_are_the_four_candidates_and_the_detected_ones_minus_those_already_on_the_bar()
    {
        Assert.Equal(new[] { "opera", "chrome", "comet", "firefox", "brave" }, _model.AddableBrowsers().Select(b => b.Id).ToArray());

        _model.PickBrowser(_model.AddableBrowsers().Single(b => b.Id == "chrome"));

        Assert.DoesNotContain(_model.AddableBrowsers(), b => b.Id == "chrome");
        Assert.Contains(_model.AddableBrowsers(), b => b.Id == "opera" && !b.Installed);   // not installed ones are offered, too: picking them explains
    }

    [Fact]
    public void Missing_browser_pick_requests_dialog_not_add()
    {
        var opera = _model.AddableBrowsers().Single(b => b.Id == "opera");

        var pick = _model.PickBrowser(opera);

        Assert.False(pick.Added);
        Assert.Equal("opera", pick.Missing!.Id);
        Assert.Equal("https://www.opera.com/download", pick.Missing.DownloadUrl);
        Assert.DoesNotContain(_settings.Current.Shortcuts.Items, e => e.Id == "browser:opera");   // nothing was added
    }

    [Fact]
    public void An_installed_browser_pick_adds_it_once()
    {
        var chrome = _model.AddableBrowsers().Single(b => b.Id == "chrome");

        var first = _model.PickBrowser(chrome);
        var second = _model.PickBrowser(chrome);

        Assert.True(first.Added);
        Assert.Null(first.Missing);
        Assert.True(second.Added);
        Assert.Single(_settings.Current.Shortcuts.Items, e => e.Id == "browser:chrome");
    }

    [Fact]
    public void Dialog_offers_installed_alternatives_only()
    {
        // Brave is picked already: it is not an "alternative" to add; Firefox is not installed; Internet Explorer is never offered.
        _model.PickBrowser(_model.AddableBrowsers().Single(b => b.Id == "brave"));
        var opera = _model.AddableBrowsers().Single(b => b.Id == "opera");

        var pick = _model.PickBrowser(opera);

        Assert.Equal(new[] { "chrome", "comet" }, pick.Alternatives.Select(b => b.Id).ToArray());
        Assert.All(pick.Alternatives, b => Assert.True(b.Installed));
        Assert.DoesNotContain(pick.Alternatives, b => b.Id == "firefox");
        Assert.DoesNotContain(pick.Alternatives, b => (b.ExePath ?? "").EndsWith("iexplore.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Using_another_browser_adds_that_one()
    {
        var pick = _model.PickBrowser(_model.AddableBrowsers().Single(b => b.Id == "opera"));
        var other = pick.Alternatives.First();

        Assert.True(_model.AddBrowser(other));

        Assert.Contains(_settings.Current.Shortcuts.Items, e => e.Id == ShortcutReconciler.BrowserEntryId(other.Id));
        Assert.DoesNotContain(_settings.Current.Shortcuts.Items, e => e.Id == "browser:opera");
    }
}
