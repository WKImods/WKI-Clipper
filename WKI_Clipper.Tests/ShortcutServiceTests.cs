using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class ShortcutServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_shortcut_service_" + Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;
    private readonly FakeShell _shell = new();
    private readonly FakeFileSystem _fs = PhillipsPc.Programs();
    private readonly FakeRegistry _reg = PhillipsPc.BrowserRegistry();
    private readonly List<string> _toasts = new();
    private readonly List<ShortcutService> _services = new();

    private const string WotLnk = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Wargaming.net\World_of_Tanks_EU\World of Tanks EU.lnk";
    private const string FooExe = @"C:\Games\Foo\foo.exe";

    public ShortcutServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = NewSettings(_dir);
        _fs.Add(WotLnk).Add(FooExe);
    }

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Output folders point into the temp dir so a Load() never touches the real Videos folder.</summary>
    private static SettingsService NewSettings(string dir)
    {
        var s = new SettingsService(dir);
        s.Current.SchemaVersion = SettingsService.CurrentSchemaVersion;
        s.Current.Output.ClipsFolder = Path.Combine(dir, "clips");
        s.Current.Output.ScreenshotsFolder = Path.Combine(dir, "shots");
        s.Current.Output.BufferFolder = Path.Combine(dir, "buffer");
        return s;
    }

    private ShortcutService Create()
    {
        var svc = new ShortcutService(_settings, _shell, _fs, _reg,
            closeBoard: () => { lock (_shell.Log) _shell.Log.Add("close"); },
            toast: msg => { lock (_toasts) _toasts.Add(msg); });
        _services.Add(svc);
        return svc;
    }

    private static LaunchTarget Link(string path) => new(ShortcutKind.ShellLink, path, null);

    private ShortcutEntry Entry(string id) => _settings.Current.Shortcuts.Items.Single(e => e.Id == id);

    private SettingsService Reload() => new SettingsService(_dir);   // a fresh service reading what was saved

    private string[] Ids(IEnumerable<BarItem> items) => items.Select(i => i.Id).ToArray();

    // ---- launching ----

    [Fact]
    public void Launch_closes_board_before_starting()
    {
        var svc = Create();
        svc.AddGame(Link(WotLnk), "World of Tanks", WotLnk);
        var id = svc.VisibleItems().Single(i => i.Group == ShortcutGroup.Game).Id;

        bool ok = svc.Launch(id);

        Assert.True(ok);
        Assert.Equal(new[] { "close", $"start:ShellLink:{WotLnk}" }, _shell.Log.ToArray());   // the board goes first
        Assert.Empty(_toasts);
    }

    [Fact]
    public void Steam_and_epic_games_start_through_their_protocols_without_a_file_check()
    {
        var svc = Create();
        svc.AddGame(new LaunchTarget(ShortcutKind.SteamApp, "1874880", null), "Arma Reforger", null);
        svc.AddGame(new LaunchTarget(ShortcutKind.EpicApp, "Fortnite", null), "Fortnite", null);

        foreach (var item in svc.VisibleItems().Where(i => i.Group == ShortcutGroup.Game))
            Assert.True(svc.Launch(item.Id));

        Assert.Equal(new[] { "close", "start:SteamApp:1874880", "close", "start:EpicApp:Fortnite" }, _shell.Log.ToArray());
    }

    [Fact]
    public void Missing_target_launch_reports_and_greys()
    {
        var svc = Create();
        svc.AddGame(new LaunchTarget(ShortcutKind.Exe, @"C:\Games\Gone\gone.exe", null), "Gone Game", null);   // never on disk
        var id = svc.VisibleItems().First(i => i.Group == ShortcutGroup.Game).Id;
        int changed = 0;
        svc.ItemsChanged += (_, _) => changed++;

        bool ok = svc.Launch(id);

        Assert.False(ok);                                               // no crash, no start
        Assert.DoesNotContain(_shell.Log, l => l.StartsWith("start:"));
        Assert.DoesNotContain("close", _shell.Log);                     // the board stays open so the grey entry is visible
        var toast = Assert.Single(_toasts);
        Assert.Contains("Gone Game", toast);
        Assert.True(Entry(id).Missing);                                 // greyed ...
        Assert.True(svc.VisibleItems().First(i => i.Id == id).Missing);
        Assert.True(changed >= 1);                                      // ... and the bar is told
        Assert.True(Reload().Load().Shortcuts.Items.Single(e => e.Id == id).Missing);   // ... and it stays grey after a restart
    }

    [Fact]
    public void Missing_launcher_shortcut_and_browser_exe_are_checked_too()
    {
        var svc = Create();
        _fs.Add(WotLnk);
        var items = _settings.Current.Shortcuts.Items;
        items.Add(new ShortcutEntry { Id = "launcher:ghost", Group = ShortcutGroup.Launcher, Kind = ShortcutKind.CatalogLauncher, Target = @"C:\Gone\Ghost.lnk", DisplayName = "Ghost" });
        items.Add(new ShortcutEntry { Id = "browser:vivaldi", Group = ShortcutGroup.Browser, Kind = ShortcutKind.Browser, Target = @"C:\Gone\vivaldi.exe", DisplayName = "Vivaldi" });

        Assert.False(svc.Launch("launcher:ghost"));
        Assert.False(svc.Launch("browser:vivaldi"));

        Assert.Equal(2, _toasts.Count);
        Assert.True(Entry("launcher:ghost").Missing);
        Assert.True(Entry("browser:vivaldi").Missing);
        Assert.Empty(_shell.Started);
    }

    [Fact]
    public void Entry_that_is_back_is_started_and_loses_its_grey()
    {
        var svc = Create();
        svc.AddGame(new LaunchTarget(ShortcutKind.Exe, FooExe, null), "Foo", null);
        var id = svc.VisibleItems().First(i => i.Group == ShortcutGroup.Game).Id;
        Entry(id).Missing = true;   // an earlier scan could not find it

        Assert.True(svc.Launch(id));

        Assert.False(Entry(id).Missing);
        Assert.Contains(_shell.Log, l => l == $"start:Exe:{FooExe}");
    }

    [Fact]
    public void A_failed_start_is_reported_with_its_reason_and_does_not_grey_an_entry_that_exists()
    {
        var svc = Create();
        svc.AddGame(new LaunchTarget(ShortcutKind.Exe, FooExe, null), "Foo", null);
        var id = svc.VisibleItems().First(i => i.Group == ShortcutGroup.Game).Id;
        _shell.StartSucceeds = false;

        bool ok = svc.Launch(id);

        Assert.False(ok);
        var toast = Assert.Single(_toasts);
        Assert.Contains("Foo", toast);
        Assert.Contains(_shell.StartError!, toast);
        Assert.False(Entry(id).Missing);   // the file is there: this was no "uninstalled" case
    }

    [Fact]
    public void Launch_of_an_unknown_id_does_nothing()
    {
        var svc = Create();

        Assert.False(svc.Launch("game.doesnotexist"));

        Assert.Empty(_shell.Log);
        Assert.Empty(_toasts);
    }

    [Fact]
    public void Launcher_with_a_shortcut_target_starts_the_shortcut_and_one_with_an_exe_starts_the_exe()
    {
        var svc = Create();
        _settings.Current.Shortcuts.Items.Add(new ShortcutEntry { Id = "launcher:wgc", Group = ShortcutGroup.Launcher, Kind = ShortcutKind.CatalogLauncher, Target = WotLnk, DisplayName = "WGC" });
        _settings.Current.Shortcuts.Items.Add(new ShortcutEntry { Id = "launcher:rockstar", Group = ShortcutGroup.Launcher, Kind = ShortcutKind.CatalogLauncher, Target = PhillipsPc.RockstarPath, DisplayName = "Rockstar" });

        Assert.True(svc.Launch("launcher:wgc"));
        Assert.True(svc.Launch("launcher:rockstar"));

        Assert.Equal(Link(WotLnk), _shell.Started[0]);
        Assert.Equal(new LaunchTarget(ShortcutKind.Exe, PhillipsPc.RockstarPath, null), _shell.Started[1]);
    }

    [Fact]
    public void Xbox_store_app_must_still_be_installed()
    {
        var svc = Create();
        _settings.Current.Shortcuts.Items.Add(new ShortcutEntry { Id = "launcher:xbox", Group = ShortcutGroup.Launcher, Kind = ShortcutKind.StoreApp, Target = PhillipsPc.XboxAumid, DisplayName = "Xbox" });

        Assert.False(svc.Launch("launcher:xbox"));
        Assert.True(Entry("launcher:xbox").Missing);

        _shell.StoreApps.Add(PhillipsPc.XboxAumid);
        Assert.True(svc.Launch("launcher:xbox"));
        Assert.False(Entry("launcher:xbox").Missing);
        Assert.Equal(new LaunchTarget(ShortcutKind.StoreApp, PhillipsPc.XboxAumid, null), _shell.Started.Single());
    }

    // ---- browsers ----

    [Fact]
    public void Default_browser_starts_the_windows_default_exe()
    {
        var svc = Create();

        Assert.True(svc.Launch(ShortcutReconciler.DefaultBrowserEntryId));

        Assert.Equal(new LaunchTarget(ShortcutKind.DefaultBrowser, PhillipsPc.CometPath, null), _shell.Started.Single());
        Assert.Empty(_toasts);
    }

    [Fact]
    public void Default_browser_is_never_internet_explorer_and_asks_the_user_to_choose()
    {
        var svc = Create();
        _shell.DefaultBrowserExe = PhillipsPc.IePath;

        bool ok = svc.Launch(ShortcutReconciler.DefaultBrowserEntryId);

        Assert.False(ok);
        Assert.Empty(_shell.Started);
        Assert.DoesNotContain("close", _shell.Log);   // nothing to start: the board stays so the user can fix it
        Assert.Single(_toasts);
    }

    [Fact]
    public void No_windows_default_browser_at_all_gives_the_same_notice()
    {
        var svc = Create();
        _shell.DefaultBrowserExe = null;

        Assert.False(svc.Launch(ShortcutReconciler.DefaultBrowserEntryId));

        Assert.Empty(_shell.Started);
        Assert.Single(_toasts);
    }

    [Fact]
    public void Chosen_default_browser_wins_and_an_uninstalled_choice_falls_back_with_a_notice()
    {
        var svc = Create();
        _settings.Current.Shortcuts.DefaultBrowserId = "chrome";
        Assert.True(svc.Launch(ShortcutReconciler.DefaultBrowserEntryId));
        Assert.Equal(PhillipsPc.ChromePath, _shell.Started.Last().Target);
        Assert.Empty(_toasts);

        _settings.Current.Shortcuts.DefaultBrowserId = "opera";   // not installed
        Assert.True(svc.Launch(ShortcutReconciler.DefaultBrowserEntryId));
        Assert.Equal(PhillipsPc.CometPath, _shell.Started.Last().Target);   // back on the Windows default
        Assert.Single(_toasts);
    }

    // ---- scanning ----

    [Fact]
    public async Task Rescan_adds_detected_launchers_once()
    {
        var svc = Create();

        await svc.RescanAsync();
        await svc.RescanAsync();

        var ids = _settings.Current.Shortcuts.Items.Select(e => e.Id).ToArray();
        Assert.Equal(
            new[] { "launcher:steam", "launcher:ea", "launcher:epic", "launcher:gog", "launcher:ubisoft", "launcher:battlenet", "launcher:rockstar", "launcher:wgc", "browser:default" },
            ids);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Equal(2, _shell.ScanCount);
        Assert.NotNull(svc.LastScanUtc);
    }

    [Fact]
    public async Task A_rescan_without_news_does_not_write_the_settings_again()
    {
        var svc = Create();
        await svc.RescanAsync();
        int saves = 0;
        _settings.SettingsChanged += (_, _) => saves++;

        await svc.RescanAsync();

        Assert.Equal(0, saves);
    }

    [Fact]
    public async Task Rescan_persists_and_gives_every_launcher_an_icon()
    {
        var svc = Create();

        await svc.RescanAsync();

        Assert.Equal(9, Reload().Load().Shortcuts.Items.Count);
        var steam = svc.VisibleItems().Single(i => i.Id == "launcher:steam");
        Assert.Equal(@"C:\icons\launcher_steam.png", steam.IconPng);
        Assert.Equal("Steam", steam.Name);
    }

    [Fact]
    public async Task Rescan_raises_items_changed()
    {
        var svc = Create();
        int raised = 0;
        svc.ItemsChanged += (_, _) => Interlocked.Increment(ref raised);

        await svc.RescanAsync();

        Assert.True(raised >= 1);
    }

    [Fact]
    public async Task Rescan_with_prune_removes_missing_launchers_but_keeps_games()
    {
        var svc = Create();
        await svc.RescanAsync();
        svc.AddGame(new LaunchTarget(ShortcutKind.Exe, @"C:\Games\Gone\gone.exe", null), "Gone Game", null);   // a game whose exe is not there
        Entry(svc.VisibleItems().First(i => i.Group == ShortcutGroup.Game).Id).Missing = true;                 // ... and a click already greyed it
        var pickedBrowser = new ShortcutEntry
        {
            Id = ShortcutReconciler.BrowserEntryId("opera"), Group = ShortcutGroup.Browser, Kind = ShortcutKind.Browser,
            Target = @"C:\old\opera.exe", DisplayName = "Opera",
        };
        _settings.Current.Shortcuts.Items = _settings.Current.Shortcuts.Items.Append(pickedBrowser).ToList();

        // Rockstar and Epic are uninstalled; Opera was never installed.
        _shell.Links = _shell.Links.Where(l => !l.Path.Contains("Rockstar") && !l.Path.Contains("Epic Games Launcher")).ToList();

        // A normal (automatic) rescan only greys them out.
        await svc.RescanAsync();
        var afterPlain = _settings.Current.Shortcuts.Items.ToDictionary(e => e.Id);
        Assert.True(afterPlain["launcher:rockstar"].Missing);
        Assert.True(afterPlain["launcher:epic"].Missing);
        Assert.True(afterPlain["browser:opera"].Missing);
        Assert.Contains(afterPlain.Values, e => e.Group == ShortcutGroup.Game);

        // "Neu suchen" removes what is still missing - launchers and browsers, never games.
        await svc.RescanAsync(pruneMissing: true);

        var ids = _settings.Current.Shortcuts.Items.Select(e => e.Id).ToArray();
        Assert.DoesNotContain("launcher:rockstar", ids);
        Assert.DoesNotContain("launcher:epic", ids);
        Assert.DoesNotContain("browser:opera", ids);
        Assert.Contains("launcher:steam", ids);
        Assert.Contains("browser:default", ids);
        var game = Assert.Single(_settings.Current.Shortcuts.Items, e => e.Group == ShortcutGroup.Game);
        Assert.Equal("Gone Game", game.DisplayName);   // kept - only a click on it reports it missing
        Assert.Equal(ids, Reload().Load().Shortcuts.Items.Select(e => e.Id).ToArray());   // and the result is saved
    }

    [Fact]
    public async Task A_missing_launcher_comes_back_when_it_is_installed_again()
    {
        var svc = Create();
        await svc.RescanAsync();
        var all = _shell.Links;
        _shell.Links = all.Where(l => !l.Path.Contains("Rockstar")).ToList();
        await svc.RescanAsync();
        Assert.True(Entry("launcher:rockstar").Missing);

        _shell.Links = all;
        await svc.RescanAsync();

        Assert.False(Entry("launcher:rockstar").Missing);
    }

    [Fact]
    public async Task Overlapping_rescans_run_one_scan()
    {
        var svc = Create();
        using var gate = new ManualResetEventSlim(false);
        _shell.ScanGate = gate;

        var first = svc.RescanAsync();
        Assert.True(_shell.ScanEntered.Wait(TimeSpan.FromSeconds(5)));
        var second = svc.RescanAsync();   // the board was opened twice quickly
        gate.Set();
        await Task.WhenAll(first, second);

        Assert.Equal(1, _shell.ScanCount);
    }

    [Fact]
    public async Task A_prune_request_during_a_running_scan_is_not_lost()
    {
        var svc = Create();
        await svc.RescanAsync();
        _shell.Links = _shell.Links.Where(l => !l.Path.Contains("Rockstar")).ToList();
        using var gate = new ManualResetEventSlim(false);
        _shell.ScanGate = gate;
        _shell.ScanEntered.Reset();

        var plain = svc.RescanAsync();
        Assert.True(_shell.ScanEntered.Wait(TimeSpan.FromSeconds(5)));
        var prune = svc.RescanAsync(pruneMissing: true);
        gate.Set();
        await Task.WhenAll(plain, prune);

        Assert.DoesNotContain(_settings.Current.Shortcuts.Items, e => e.Id == "launcher:rockstar");
    }

    [Fact]
    public async Task Edits_made_while_the_shell_is_scanned_survive_it()
    {
        var svc = Create();
        await svc.RescanAsync();
        using var gate = new ManualResetEventSlim(false);
        _shell.ScanGate = gate;
        _shell.ScanEntered.Reset();

        var scan = svc.RescanAsync();
        Assert.True(_shell.ScanEntered.Wait(TimeSpan.FromSeconds(5)));
        svc.SetHidden("launcher:steam", true);                                          // the user hides Steam ...
        svc.AddGame(Link(WotLnk), "World of Tanks", WotLnk);                            // ... and adds a game while the scan is out
        gate.Set();
        await scan;

        Assert.True(Entry("launcher:steam").Hidden);
        Assert.Contains(_settings.Current.Shortcuts.Items, e => e.Group == ShortcutGroup.Game && e.DisplayName == "World of Tanks");
    }

    [Fact]
    public async Task Edits_made_while_icons_are_read_survive_the_scan()
    {
        // The icon step runs AFTER the scan took its snapshot of the list: the longest window for a lost edit.
        var svc = Create();
        await svc.RescanAsync();
        using var gate = new ManualResetEventSlim(false);
        _shell.IconGate = gate;
        _shell.IconEntered.Reset();

        var scan = svc.RescanAsync();
        Assert.True(_shell.IconEntered.Wait(TimeSpan.FromSeconds(5)));
        svc.AddGame(Link(WotLnk), "World of Tanks", WotLnk);   // only the scan's first icon read waits at the gate
        svc.SetHidden("launcher:ea", true);
        gate.Set();
        await scan;

        Assert.True(Entry("launcher:ea").Hidden);

        Assert.Contains(_settings.Current.Shortcuts.Items, e => e.Group == ShortcutGroup.Game && e.DisplayName == "World of Tanks");
        Assert.Equal(ShortcutGroup.Game, _settings.Current.Shortcuts.Items.First().Group);
    }

    [Fact]
    public async Task A_scan_that_throws_leaves_the_items_alone_and_never_crashes()
    {
        var svc = Create();
        await svc.RescanAsync();
        var before = _settings.Current.Shortcuts.Items.Select(e => e.Id).ToArray();
        _shell.Links = null!;   // the fake now throws inside the scan

        await svc.RescanAsync();

        Assert.Equal(before, _settings.Current.Shortcuts.Items.Select(e => e.Id).ToArray());
    }

    // ---- list ----

    [Fact]
    public void A_fresh_service_already_offers_the_default_browser()
    {
        var svc = Create();

        var only = Assert.Single(svc.VisibleItems());
        Assert.Equal("browser:default", only.Id);
        Assert.Equal(ShortcutGroup.Browser, only.Group);
    }

    [Fact]
    public async Task Visible_items_are_grouped_games_launchers_browsers()
    {
        var svc = Create();
        await svc.RescanAsync();
        svc.AddGame(new LaunchTarget(ShortcutKind.SteamApp, "1874880", null), "Arma Reforger", null);
        svc.AddGame(Link(WotLnk), "World of Tanks", WotLnk);
        svc.SetHidden("launcher:ea", true);

        var visible = svc.VisibleItems();

        var groups = visible.Select(i => i.Group).ToArray();
        Assert.Equal(groups.OrderBy(g => (int)g), groups);                        // games, then launchers, then browsers
        Assert.Equal(2, groups.Count(g => g == ShortcutGroup.Game));
        Assert.Equal(new[] { "Arma Reforger", "World of Tanks" }, visible.Where(i => i.Group == ShortcutGroup.Game).Select(i => i.Name).ToArray());
        Assert.DoesNotContain(visible, i => i.Id == "launcher:ea");               // hidden entries are not on the bar
        Assert.Equal(7, visible.Count(i => i.Group == ShortcutGroup.Launcher));
        Assert.Equal("browser:default", visible.Last().Id);
    }

    [Fact]
    public async Task Missing_entries_are_visible_but_flagged()
    {
        var svc = Create();
        await svc.RescanAsync();
        _shell.Links = _shell.Links.Where(l => !l.Path.Contains("Rockstar")).ToList();

        await svc.RescanAsync();

        Assert.True(svc.VisibleItems().Single(i => i.Id == "launcher:rockstar").Missing);
        Assert.False(svc.VisibleItems().Single(i => i.Id == "launcher:steam").Missing);
    }

    [Fact]
    public async Task Move_and_hide_persist()
    {
        var svc = Create();
        await svc.RescanAsync();

        svc.Move("launcher:epic", -1);          // Epic before EA
        svc.Move("launcher:steam", +2);         // Steam two places down
        svc.SetHidden("launcher:gog", true);

        var reloaded = Reload().Load().Shortcuts.Items;
        var launchers = reloaded.Where(e => e.Group == ShortcutGroup.Launcher).Select(e => e.Id).ToArray();
        Assert.Equal(new[] { "launcher:epic", "launcher:ea", "launcher:steam", "launcher:gog", "launcher:ubisoft", "launcher:battlenet", "launcher:rockstar", "launcher:wgc" }, launchers);
        Assert.True(reloaded.Single(e => e.Id == "launcher:gog").Hidden);
        Assert.False(reloaded.Single(e => e.Id == "launcher:steam").Hidden);
        // The bar agrees with the file: same order, the hidden one left out.
        Assert.Equal(launchers.Where(i => i != "launcher:gog").ToArray(),
            svc.VisibleItems().Where(i => i.Group == ShortcutGroup.Launcher).Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task Move_stays_inside_its_group_and_clamps_at_the_ends()
    {
        var svc = Create();
        await svc.RescanAsync();
        svc.AddGame(new LaunchTarget(ShortcutKind.SteamApp, "1", null), "A", null);
        svc.AddGame(new LaunchTarget(ShortcutKind.SteamApp, "2", null), "B", null);

        svc.Move("launcher:steam", -5);   // already first
        svc.Move("launcher:wgc", +5);     // already last
        var gameA = svc.VisibleItems().First(i => i.Name == "A").Id;
        svc.Move(gameA, +9);              // A behind B, but still a game

        var ids = _settings.Current.Shortcuts.Items.Select(e => e.Id).ToArray();
        Assert.Equal("launcher:steam", ids.First(i => i.StartsWith("launcher:")));
        Assert.Equal("launcher:wgc", ids.Last(i => i.StartsWith("launcher:")));
        Assert.Equal(new[] { "B", "A" }, svc.VisibleItems().Where(i => i.Group == ShortcutGroup.Game).Select(i => i.Name).ToArray());
        Assert.Equal(ShortcutGroup.Game, _settings.Current.Shortcuts.Items.First().Group);
    }

    [Fact]
    public async Task The_default_browser_stays_first_in_its_group()
    {
        var svc = Create();
        await svc.RescanAsync();
        _settings.Current.Shortcuts.Items = _settings.Current.Shortcuts.Items.Concat(new[]
        {
            new ShortcutEntry { Id = "browser:chrome", Group = ShortcutGroup.Browser, Kind = ShortcutKind.Browser, Target = PhillipsPc.ChromePath, DisplayName = "Google Chrome" },
            new ShortcutEntry { Id = "browser:brave", Group = ShortcutGroup.Browser, Kind = ShortcutKind.Browser, Target = PhillipsPc.BravePath, DisplayName = "Brave" },
        }).ToList();

        svc.Move("browser:brave", -5);    // tries to jump over the default browser
        svc.Move("browser:default", +1);  // the default browser itself does not move

        var browsers = _settings.Current.Shortcuts.Items.Where(e => e.Group == ShortcutGroup.Browser).Select(e => e.Id).ToArray();
        Assert.Equal(new[] { "browser:default", "browser:brave", "browser:chrome" }, browsers);
    }

    [Fact]
    public void Move_and_hide_of_an_unknown_id_change_nothing_and_raise_nothing()
    {
        var svc = Create();
        int raised = 0;
        svc.ItemsChanged += (_, _) => raised++;

        svc.Move("game.nope", 1);
        svc.SetHidden("game.nope", true);
        svc.Remove("game.nope");

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Setting_hidden_to_the_value_it_already_has_raises_nothing()
    {
        var svc = Create();
        int raised = 0;
        svc.ItemsChanged += (_, _) => raised++;

        svc.SetHidden("browser:default", false);

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Add_game_twice_keeps_one()
    {
        var svc = Create();

        svc.AddGame(Link(WotLnk), "World of Tanks", WotLnk);
        svc.AddGame(Link(WotLnk.ToUpperInvariant().Replace('\\', '/')), "WoT again", null);   // another spelling of the same shortcut

        var game = Assert.Single(_settings.Current.Shortcuts.Items, e => e.Group == ShortcutGroup.Game);
        Assert.Equal("World of Tanks", game.DisplayName);
        Assert.StartsWith("game.", game.Id);
        Assert.Equal(game.Id, ShortcutService.GameEntryId(Link(WotLnk)));
    }

    [Fact]
    public void Game_ids_are_stable_and_differ_per_target()
    {
        var a = ShortcutService.GameEntryId(new LaunchTarget(ShortcutKind.SteamApp, "1874880", null));
        var b = ShortcutService.GameEntryId(new LaunchTarget(ShortcutKind.SteamApp, "1874880", null));
        var c = ShortcutService.GameEntryId(new LaunchTarget(ShortcutKind.SteamApp, "221100", null));
        var d = ShortcutService.GameEntryId(new LaunchTarget(ShortcutKind.Exe, @"C:\Games\Foo\foo.exe", "-a"));
        var e = ShortcutService.GameEntryId(new LaunchTarget(ShortcutKind.Exe, @"C:\Games\Foo\foo.exe", "-b"));

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(d, e);   // the same exe with other arguments is another entry
        Assert.Matches("^game\\.[0-9a-f]{16}$", a);
    }

    [Fact]
    public void Added_games_land_at_the_end_of_the_games_before_the_launchers()
    {
        var svc = Create();

        svc.AddGame(new LaunchTarget(ShortcutKind.SteamApp, "1", null), "First", null);
        svc.AddGame(new LaunchTarget(ShortcutKind.SteamApp, "2", null), "Second", null);

        var groups = _settings.Current.Shortcuts.Items.Select(e => e.Group).ToArray();
        Assert.Equal(new[] { ShortcutGroup.Game, ShortcutGroup.Game, ShortcutGroup.Browser }, groups);
        Assert.Equal(new[] { "First", "Second" }, _settings.Current.Shortcuts.Items.Where(e => e.Group == ShortcutGroup.Game).Select(e => e.DisplayName).ToArray());
    }

    [Fact]
    public void Added_game_has_its_icon_and_is_saved()
    {
        var svc = Create();

        svc.AddGame(Link(WotLnk), "World of Tanks", WotLnk);

        var item = svc.VisibleItems().Single(i => i.Group == ShortcutGroup.Game);
        Assert.NotNull(item.IconPng);
        Assert.Contains(Reload().Load().Shortcuts.Items, e => e.DisplayName == "World of Tanks");
    }

    [Fact]
    public void Remove_takes_out_games_and_picked_browsers_but_never_launchers_or_the_default_browser()
    {
        var svc = Create();
        svc.AddGame(Link(WotLnk), "World of Tanks", WotLnk);
        _settings.Current.Shortcuts.Items.Add(new ShortcutEntry { Id = "launcher:steam", Group = ShortcutGroup.Launcher, Kind = ShortcutKind.CatalogLauncher, Target = "x", DisplayName = "Steam" });
        _settings.Current.Shortcuts.Items.Add(new ShortcutEntry { Id = "browser:chrome", Group = ShortcutGroup.Browser, Kind = ShortcutKind.Browser, Target = PhillipsPc.ChromePath, DisplayName = "Chrome" });
        var gameId = _settings.Current.Shortcuts.Items.Single(e => e.Group == ShortcutGroup.Game).Id;

        svc.Remove(gameId);
        svc.Remove("browser:chrome");
        svc.Remove("launcher:steam");
        svc.Remove("browser:default");

        var ids = _settings.Current.Shortcuts.Items.Select(e => e.Id).ToArray();
        Assert.DoesNotContain(gameId, ids);
        Assert.DoesNotContain("browser:chrome", ids);
        Assert.Contains("launcher:steam", ids);
        Assert.Contains("browser:default", ids);
        Assert.DoesNotContain(Reload().Load().Shortcuts.Items, e => e.Id == gameId);
    }

    // ---- what the edit dialog needs ----

    [Fact]
    public async Task Entries_snapshot_includes_hidden_ones_and_cannot_change_the_live_list()
    {
        var svc = Create();
        await svc.RescanAsync();
        svc.SetHidden("launcher:gog", true);

        var snapshot = svc.Entries();

        Assert.Equal(_settings.Current.Shortcuts.Items.Select(e => e.Id).ToArray(), snapshot.Select(e => e.Id).ToArray());
        Assert.True(snapshot.Single(e => e.Id == "launcher:gog").Hidden);      // hidden ones are listed (the dialog shows them unticked)
        snapshot.Single(e => e.Id == "launcher:gog").Hidden = false;           // editing the copy ...
        snapshot.Single(e => e.Id == "launcher:steam").DisplayName = "changed";
        Assert.True(Entry("launcher:gog").Hidden);                             // ... changes nothing of the real thing
        Assert.Equal("Steam", Entry("launcher:steam").DisplayName);
    }

    [Fact]
    public async Task IconOf_gives_the_cached_icon_of_any_entry_hidden_or_not()
    {
        var svc = Create();
        await svc.RescanAsync();
        svc.SetHidden("launcher:steam", true);

        Assert.Equal(@"C:\icons\launcher_steam.png", svc.IconOf("launcher:steam"));
        Assert.Null(svc.IconOf("game.nothing"));
    }

    [Fact]
    public void SetHotkey_sets_clears_and_persists()
    {
        var svc = Create();
        svc.AddGame(Link(WotLnk), "World of Tanks", WotLnk);
        var id = _settings.Current.Shortcuts.Items.First(e => e.Group == ShortcutGroup.Game).Id;
        int raised = 0;
        svc.ItemsChanged += (_, _) => raised++;

        svc.SetHotkey(id, new HotkeyBinding { Modifiers = HotkeyModifier.Control | HotkeyModifier.Alt, Key = 0x31 });

        Assert.Equal(1, raised);
        var saved = Reload().Load().Shortcuts.Items.Single(e => e.Id == id);
        Assert.Equal(0x31u, saved.Hotkey!.Key);
        Assert.Equal(HotkeyModifier.Control | HotkeyModifier.Alt, saved.Hotkey.Modifiers);

        svc.SetHotkey(id, null);
        Assert.Null(Entry(id).Hotkey);
        Assert.Null(Reload().Load().Shortcuts.Items.Single(e => e.Id == id).Hotkey);

        svc.SetHotkey(id, new HotkeyBinding { Key = 0 });   // "not bound" is the same as none
        Assert.Null(Entry(id).Hotkey);

        svc.SetHotkey("game.unknown", new HotkeyBinding { Key = 0x32 });   // an unknown id changes nothing
    }

    [Fact]
    public void SetDefaultBrowser_persists_and_null_means_the_windows_default()
    {
        var svc = Create();
        Assert.Null(svc.DefaultBrowserId);

        svc.SetDefaultBrowser("chrome");

        Assert.Equal("chrome", svc.DefaultBrowserId);
        Assert.Equal("chrome", Reload().Load().Shortcuts.DefaultBrowserId);
        svc.SetDefaultBrowser(null);
        Assert.Null(Reload().Load().Shortcuts.DefaultBrowserId);
    }

    [Fact]
    public void AddBrowser_adds_an_installed_browser_once_and_refuses_one_that_is_missing()
    {
        var svc = Create();
        var browsers = svc.DetectBrowsers();
        var chrome = browsers.Single(b => b.Id == "chrome");
        var opera = browsers.Single(b => b.Id == "opera");

        Assert.True(svc.AddBrowser(chrome));
        Assert.True(svc.AddBrowser(chrome));      // again: still "there", but not twice
        Assert.False(svc.AddBrowser(opera));      // not installed: that is the missing-browser dialog's business

        var entries = _settings.Current.Shortcuts.Items.Where(e => e.Group == ShortcutGroup.Browser).ToArray();
        Assert.Equal(new[] { "browser:default", "browser:chrome" }, entries.Select(e => e.Id).ToArray());
        var added = entries[1];
        Assert.Equal(ShortcutKind.Browser, added.Kind);
        Assert.Equal(PhillipsPc.ChromePath, added.Target);
        Assert.Equal("Google Chrome", added.DisplayName);
        Assert.False(added.Missing);
        Assert.True(svc.Launch("browser:chrome"));
        Assert.Equal(new LaunchTarget(ShortcutKind.Browser, PhillipsPc.ChromePath, null), _shell.Started.Last());
    }

    [Fact]
    public void Recent_games_are_handed_out_as_a_copy()
    {
        var svc = Create();
        svc.RememberGame(@"C:\Games\World_of_Tanks_EU\WorldOfTanks.exe", "World of Tanks");

        var recent = svc.RecentGames();

        Assert.Equal("World of Tanks", Assert.Single(recent).DisplayName);
        Assert.Equal(PhillipsPc.CometPath, svc.WindowsDefaultBrowserExe());
    }

    // ---- bar state (collapsed or open) ----

    [Fact]
    public void The_bar_starts_expanded_and_remembers_collapsing()
    {
        var svc = Create();
        Assert.True(svc.Expanded);
        int raised = 0;
        svc.ItemsChanged += (_, _) => raised++;

        svc.SetExpanded(false);

        Assert.False(svc.Expanded);
        Assert.Equal(1, raised);
        Assert.False(Reload().Load().Shortcuts.Expanded);   // survives a restart
        svc.SetExpanded(false);
        Assert.Equal(1, raised);                            // the same value again changes nothing
    }

    // ---- when the board asks for a rescan ----

    [Fact]
    public async Task Rescan_if_stale_scans_on_the_first_open_and_after_a_minute_but_not_in_between()
    {
        var svc = Create();
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        svc.Clock = () => now;

        Assert.Null(svc.LastScanUtc);
        await svc.RescanIfStale();                      // never scanned: first open
        Assert.Equal(1, _shell.ScanCount);
        Assert.Equal(now, svc.LastScanUtc);

        now = now.AddSeconds(59);
        await svc.RescanIfStale();                      // opened again right away: nothing to do
        Assert.Equal(1, _shell.ScanCount);

        now = now.AddSeconds(2);                        // 61 s after the scan
        await svc.RescanIfStale();
        Assert.Equal(2, _shell.ScanCount);
        Assert.Equal(now, svc.LastScanUtc);
    }

    [Fact]
    public async Task A_failed_scan_does_not_count_as_fresh()
    {
        var svc = Create();
        svc.Clock = () => new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        _shell.Links = null!;

        await svc.RescanIfStale();

        Assert.Null(svc.LastScanUtc);                   // the next open tries again
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0, false)]
    [InlineData(60, false)]
    [InlineData(61, true)]
    public void The_staleness_rule_is_a_minute(int? ageSeconds, bool stale)
    {
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        DateTime? last = ageSeconds is { } a ? now.AddSeconds(-a) : null;

        Assert.Equal(stale, ShortcutService.IsStale(last, now));
    }

    // ---- icon of something that is not an entry (the game chip) ----

    [Fact]
    public void IconFor_reads_through_the_shell_and_gives_null_for_nothing()
    {
        var svc = Create();

        Assert.Equal(@"C:\icons\game_current.png", svc.IconFor(@"D:\SteamLibrary\steamapps\common\Arma Reforger\ArmaReforgerSteam.exe", "game_current"));
        Assert.Null(svc.IconFor("", "game_current"));
        Assert.Null(svc.IconFor(null, "game_current"));
    }

    // ---- recent games ----

    [Fact]
    public void RememberGame_from_game_changed_uses_resolver_exclusions()
    {
        var svc = Create();

        foreach (var (exe, name) in new[]
                 {
                     (PhillipsPc.SteamExePath, "Steam"),                                                            // launcher
                     (@"C:\Games\World_of_Tanks_EU\wgc_api.exe", "Wargaming"),                                      // launcher process
                     (PhillipsPc.ChromePath, "Google Chrome"),                                                      // browser
                     (@"C:\Users\cr4zy\AppData\Local\Programs\WKI_Clipper\WKI_Clipper.exe", "WKI Clipper"),         // ourselves
                     ("", "nothing"),
                 })
            svc.RememberGame(exe, name);
        Assert.Empty(_settings.Current.Shortcuts.RecentGames);

        svc.RememberGame(@"D:\SteamLibrary\steamapps\common\Arma Reforger\ArmaReforgerSteam.exe", "Arma Reforger");
        svc.RememberGame(@"C:\Games\World_of_Tanks_EU\WorldOfTanks.exe", "World of Tanks");

        Assert.Equal(new[] { "World of Tanks", "Arma Reforger" }, _settings.Current.Shortcuts.RecentGames.Select(g => g.DisplayName).ToArray());
        Assert.Equal(2, Reload().Load().Shortcuts.RecentGames.Count);   // saved
    }

    [Fact]
    public void RememberGame_of_an_excluded_exe_writes_nothing()
    {
        var svc = Create();
        int saves = 0;
        _settings.SettingsChanged += (_, _) => saves++;

        svc.RememberGame(PhillipsPc.SteamExePath, "Steam");

        Assert.Equal(0, saves);
    }

    [Fact]
    public async Task Remember_game_is_safe_from_many_threads()
    {
        var svc = Create();   // GameChanged arrives on pool threads while the UI reads the list

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() =>
        {
            svc.RememberGame($@"C:\Games\G{i % 12}\g{i % 12}.exe", "G" + (i % 12));
            _ = svc.VisibleItems();
        })));

        Assert.Equal(12, _settings.Current.Shortcuts.RecentGames.Count);
    }

    [Fact]
    public async Task After_dispose_nothing_is_raised()
    {
        var svc = Create();
        int raised = 0;
        svc.ItemsChanged += (_, _) => raised++;
        svc.Dispose();

        await svc.RescanAsync();
        svc.SetHidden("browser:default", true);

        Assert.Equal(0, raised);
        Assert.Equal(0, _shell.ScanCount);
    }
}

public sealed class ShortcutHotkeyTests
{
    private static AppSettings WithShortcuts(params ShortcutEntry[] entries)
    {
        var s = new AppSettings();
        s.Shortcuts.Items.AddRange(entries);
        return s;
    }

    private static ShortcutEntry Entry(string id, string name, HotkeyBinding? hotkey)
        => new() { Id = id, DisplayName = name, Group = ShortcutGroup.Game, Kind = ShortcutKind.SteamApp, Target = "1", Hotkey = hotkey };

    private static HotkeyBinding Key(HotkeyModifier mods, uint key) => new() { Modifiers = mods, Key = key };

    [Fact]
    public void App_prefix_round_trips()
    {
        foreach (var id in new[] { "game.3fa9c1d2e4b58a77", "launcher:steam", "browser:default" })
        {
            var action = ShortcutHotkeys.ActionFor(id);

            Assert.StartsWith("App:", action);
            Assert.True(ShortcutHotkeys.TryParse(action, out var back));
            Assert.Equal(id, back);
        }
        Assert.Equal("App:", ShortcutHotkeys.Prefix);
    }

    [Theory]
    [InlineData("")]
    [InlineData("App:")]
    [InlineData("app:game.x")]
    [InlineData("SaveReplay")]
    [InlineData("StreamButton:3")]
    public void Other_actions_are_not_shortcut_actions(string action)
    {
        Assert.False(ShortcutHotkeys.TryParse(action, out var id));
        Assert.Equal("", id);
    }

    [Fact]
    public void Entry_without_key_is_not_registered()
    {
        var settings = WithShortcuts(
            Entry("game.a", "A", null),
            Entry("game.b", "B", new HotkeyBinding()),                                              // Key = 0: "not bound"
            Entry("game.c", "C", Key(HotkeyModifier.Control | HotkeyModifier.Alt, 0x31)));          // Ctrl+Alt+1

        var bindings = ShortcutHotkeys.Bindings(settings).ToList();

        var only = Assert.Single(bindings);
        Assert.Equal("App:game.c", only.Action);
        Assert.Equal(0x31u, only.Binding.Key);
        Assert.Equal(HotkeyModifier.Control | HotkeyModifier.Alt, only.Binding.Modifiers);
    }

    [Fact]
    public void A_hidden_entry_keeps_its_hotkey()
    {
        var hidden = Entry("game.h", "H", Key(HotkeyModifier.None, 0x75));
        hidden.Hidden = true;

        Assert.Single(ShortcutHotkeys.Bindings(WithShortcuts(hidden)));   // the user bound it; hiding it from the bar does not unbind it
    }

    [Fact]
    public void Shortcut_hotkey_collides_with_F9()
    {
        var settings = WithShortcuts(Entry("game.a", "Arma", null));
        var f9 = Key(HotkeyModifier.None, 0x78);

        var clash = HotkeyService.FindCollision(settings, f9, excludeShortcutId: "game.a");

        Assert.NotNull(clash);
        Assert.Contains("SaveReplay", clash);   // F9 belongs to the clipper
    }

    [Fact]
    public void A_free_combo_does_not_collide_and_an_unbound_candidate_never_does()
    {
        var settings = WithShortcuts(Entry("game.a", "Arma", null));

        Assert.Null(HotkeyService.FindCollision(settings, Key(HotkeyModifier.Control | HotkeyModifier.Alt, 0x31), excludeShortcutId: "game.a"));
        Assert.Null(HotkeyService.FindCollision(settings, new HotkeyBinding(), excludeShortcutId: "game.a"));
    }

    [Fact]
    public void Two_shortcuts_cannot_share_a_combo_but_an_entry_may_keep_its_own()
    {
        var combo = Key(HotkeyModifier.Control | HotkeyModifier.Alt, 0x32);
        var settings = WithShortcuts(Entry("game.a", "Arma", combo), Entry("game.b", "DayZ", null));

        var clash = HotkeyService.FindCollision(settings, Key(combo.Modifiers, combo.Key), excludeShortcutId: "game.b");
        Assert.NotNull(clash);
        Assert.Contains("Arma", clash);

        Assert.Null(HotkeyService.FindCollision(settings, Key(combo.Modifiers, combo.Key), excludeShortcutId: "game.a"));
    }

    [Fact]
    public void A_stream_deck_tile_and_a_shortcut_are_checked_against_each_other()
    {
        var combo = Key(HotkeyModifier.Control | HotkeyModifier.Alt, 0x33);
        var settings = WithShortcuts(Entry("game.a", "Arma", combo));
        settings.Streaming.Buttons.Add(new StreamButtonConfig { Slot = 4, Label = "Scene", Hotkey = Key(HotkeyModifier.Control | HotkeyModifier.Alt, 0x34) });

        Assert.Contains("Arma", HotkeyService.FindCollision(settings, Key(combo.Modifiers, combo.Key), excludeSlot: 4));
        Assert.Contains("Scene", HotkeyService.FindCollision(settings, Key(HotkeyModifier.Control | HotkeyModifier.Alt, 0x34), excludeShortcutId: "game.a"));
    }

    [Fact]
    public void Describe_action_names_the_shortcut()
    {
        var settings = WithShortcuts(Entry("game.a", "Arma Reforger", Key(HotkeyModifier.None, 0x75)));

        Assert.Contains("Arma Reforger", HotkeyService.DescribeAction(settings, "App:game.a"));
        Assert.Equal("App:game.gone", HotkeyService.DescribeAction(settings, "App:game.gone"));   // unknown: the raw action, like any other
        Assert.Equal("SaveReplay", HotkeyService.DescribeAction(settings, "SaveReplay"));
    }

    [Fact]
    public void Signature_changes_only_when_a_hotkey_does()
    {
        var a = Entry("game.a", "A", Key(HotkeyModifier.None, 0x75));
        var plain = ShortcutHotkeys.Signature(new[] { a, Entry("game.b", "B", null) });

        Assert.Equal(plain, ShortcutHotkeys.Signature(new[] { Entry("game.b", "renamed", null), a }));   // order and names do not matter
        Assert.NotEqual(plain, ShortcutHotkeys.Signature(new[] { Entry("game.a", "A", Key(HotkeyModifier.None, 0x76)), Entry("game.b", "B", null) }));
        Assert.NotEqual(plain, ShortcutHotkeys.Signature(new[] { Entry("game.b", "B", null) }));
        Assert.Equal("", ShortcutHotkeys.Signature(new[] { Entry("game.b", "B", null) }));
    }
}
