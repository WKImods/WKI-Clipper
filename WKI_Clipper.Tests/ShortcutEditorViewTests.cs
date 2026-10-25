using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.Views;
using Xunit;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Size = System.Windows.Size;

namespace WKI_Clipper.Tests;

/// <summary>
/// The edit dialog's content, the missing-browser dialog and the hotkey button on a real STA thread with the app theme. Nothing is ever
/// shown (measure, arrange, render to a picture) and no input is simulated: clicks go through the same handlers via the test seams.
/// </summary>
[Collection("WpfApplication")]   // only one System.Windows.Application per process
public sealed class ShortcutEditorViewTests
{
    private const string WotLnk = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Wargaming.net\World_of_Tanks_EU\World of Tanks EU.lnk";

    private static T OnSta<T>(Func<T> body)
        => WpfTestSupport.OnSta(() => { WpfTestSupport.EnsureTheme(); return body(); });

    /// <summary>A real service and model on fakes (a scanned PC with launchers and the standard browser, two games), disposed with the scope.</summary>
    private sealed class Env : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_editor_view_" + Guid.NewGuid().ToString("N"));
        public FakeShell Shell { get; } = new();
        public FakeFileSystem Fs { get; } = PhillipsPc.Steam().Merge(PhillipsPc.Epic()).Merge(PhillipsPc.Programs()).Add(WotLnk);
        public SettingsService Settings { get; }
        public ShortcutService Service { get; }
        public ShortcutEditorModel Model { get; }

        public Env(bool scan = true)
        {
            Directory.CreateDirectory(_dir);
            Settings = new SettingsService(_dir);
            Settings.Current.SchemaVersion = SettingsService.CurrentSchemaVersion;
            Service = new ShortcutService(Settings, Shell, Fs, PhillipsPc.BrowserRegistry(), () => { }, _ => { });
            Model = new ShortcutEditorModel(Service, Shell, Fs, PhillipsPc.BrowserRegistry(), PhillipsPc.SteamRoot, PhillipsPc.EpicManifests, _ => null);
            if (!scan) return;
            Service.RescanAsync().Wait();
            Service.AddGame(new LaunchTarget(ShortcutKind.SteamApp, "1874880", null), "Arma Reforger", null);
            Service.AddGame(new LaunchTarget(ShortcutKind.ShellLink, WotLnk, null), "World of Tanks", WotLnk);
        }

        public string FirstGameId => Service.Entries().First(e => e.Group == ShortcutGroup.Game).Id;

        public void Dispose()
        {
            Service.Dispose();
            try { Directory.Delete(_dir, true); } catch { }
        }
    }

    // ---- the content ----

    [Fact]
    public void Editor_renders_three_groups()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wki_clipper_ui");
        Directory.CreateDirectory(dir);

        var r = OnSta(() =>
        {
            using var env = new Env();
            var view = new ShortcutEditorView(env.Model);
            var host = new System.Windows.Controls.Border
            {
                Width = 760, Padding = new Thickness(0),
                Background = (Brush)Application.Current.FindResource("BgBrush"),
                Child = view,
            };
            host.Measure(new Size(760, double.PositiveInfinity));
            host.Arrange(new Rect(0, 0, 760, host.DesiredSize.Height));
            host.UpdateLayout();

            var bmp = new RenderTargetBitmap(760, (int)Math.Ceiling(host.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
            bmp.Render(host);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var f = File.Create(Path.Combine(dir, "shortcut_editor.png"))) enc.Save(f);

            return (headers: view.GroupHeaderCount, games: view.RowIds(ShortcutGroup.Game), launchers: view.RowIds(ShortcutGroup.Launcher),
                    browsers: view.RowIds(ShortcutGroup.Browser), height: host.DesiredSize.Height, width: host.ActualWidth);
        });

        Assert.Equal(3, r.headers);                   // Spiele, Launcher, Browser
        Assert.Equal(2, r.games.Count);
        Assert.Equal(8, r.launchers.Count);
        Assert.Equal(new[] { "browser:default" }, r.browsers.ToArray());
        Assert.Equal(760, r.width);
        Assert.InRange(r.height, 300, 2000);
    }

    [Fact]
    public void Hidden_and_missing_rows_are_listed_too()
    {
        var r = OnSta(() =>
        {
            using var env = new Env();
            env.Service.SetHidden("launcher:ea", true);
            var view = new ShortcutEditorView(env.Model);
            view.Rebuild();
            return (ids: view.RowIds(ShortcutGroup.Launcher), ticked: view.IsShown("launcher:ea"), tickedOther: view.IsShown("launcher:epic"));
        });

        Assert.Contains("launcher:ea", r.ids);        // hidden: still a row, to be ticked again
        Assert.False(r.ticked);
        Assert.True(r.tickedOther);
    }

    [Fact]
    public void An_empty_games_list_says_how_to_fill_it()
    {
        var r = OnSta(() =>
        {
            using var env = new Env(scan: false);
            var view = new ShortcutEditorView(env.Model);
            return (games: view.RowIds(ShortcutGroup.Game), hint: view.EmptyHint(ShortcutGroup.Game));
        });

        Assert.Empty(r.games);
        Assert.False(string.IsNullOrWhiteSpace(r.hint));
    }

    // ---- what the controls do ----

    [Fact]
    public void Ticking_moving_and_removing_go_through_the_model_and_refresh_the_rows()
    {
        var r = OnSta(() =>
        {
            using var env = new Env();
            var view = new ShortcutEditorView(env.Model);
            var first = env.FirstGameId;

            view.ClickShown(first);                                   // un-tick
            bool hidden = env.Service.Entries().Single(e => e.Id == first).Hidden;
            bool tickAfter = view.IsShown(first);

            view.ClickDown(first);
            var afterDown = view.RowIds(ShortcutGroup.Game).ToArray();
            view.ClickUp(first);
            var afterUp = view.RowIds(ShortcutGroup.Game).ToArray();

            view.ClickRemove(first);
            return (hidden, tickAfter, afterDown, afterUp, first, left: view.RowIds(ShortcutGroup.Game).ToArray(),
                    canRemoveLauncher: view.CanRemove("launcher:steam"), canRemoveGame: view.CanRemove(view.RowIds(ShortcutGroup.Game)[0]));
        });

        Assert.True(r.hidden);
        Assert.False(r.tickAfter);
        Assert.Equal(r.first, r.afterDown[1]);        // moved behind the other game ...
        Assert.Equal(r.first, r.afterUp[0]);          // ... and back
        Assert.DoesNotContain(r.first, r.left);       // removed
        Assert.Single(r.left);
        Assert.False(r.canRemoveLauncher);            // only games and picked browsers have a remove button
        Assert.True(r.canRemoveGame);
    }

    [Fact]
    public void The_hotkey_field_is_unbound_by_default_and_checks_collisions_before_it_binds()
    {
        var r = OnSta(() =>
        {
            using var env = new Env();
            var view = new ShortcutEditorView(env.Model);
            var id = env.FirstGameId;
            int changed = 0;
            view.HotkeysChanged += () => changed++;

            var before = view.HotkeyOf(id);
            var f9 = view.HotkeyButton(id).Accept(new HotkeyBinding { Modifiers = HotkeyModifier.None, Key = 0x78 });   // F9: the clipper's
            var noticeAfterF9 = view.StatusText;
            var changedAfterF9 = changed;

            var free = view.HotkeyButton(id).Accept(new HotkeyBinding { Modifiers = HotkeyModifier.Control | HotkeyModifier.Alt, Key = 0x31 });
            var saved = env.Service.Entries().Single(e => e.Id == id).Hotkey;
            var shown = view.HotkeyOf(id);

            view.ClearHotkey(id);
            return (before, f9, noticeAfterF9, changedAfterF9, free, saved, shown, afterClear: env.Service.Entries().Single(e => e.Id == id).Hotkey, changed);
        });

        Assert.Null(r.before);                              // nothing bound
        Assert.False(r.f9);                                 // F9 belongs to "save replay"
        Assert.Contains("SaveReplay", r.noticeAfterF9);     // and the user is told who has it
        Assert.Equal(0, r.changedAfterF9);                  // a refused combination changes nothing
        Assert.True(r.free);
        Assert.Equal(0x31u, r.saved!.Key);
        Assert.Equal(HotkeyModifier.Control | HotkeyModifier.Alt, r.saved.Modifiers);
        Assert.Equal(0x31u, r.shown!.Key);
        Assert.Null(r.afterClear);                          // cleared again
        Assert.Equal(2, r.changed);                         // bound, cleared: the host re-registers twice
    }

    [Fact]
    public void The_default_browser_selector_lists_the_options_and_applies_the_choice()
    {
        var r = OnSta(() =>
        {
            using var env = new Env();
            var view = new ShortcutEditorView(env.Model);

            var options = view.DefaultBrowserLabels();
            var selectedFirst = view.SelectedDefaultBrowser;
            view.ChooseDefaultBrowser("chrome");
            var afterChoice = (view.SelectedDefaultBrowser, model: env.Model.SelectedDefaultBrowserId, saved: env.Settings.Current.Shortcuts.DefaultBrowserId);
            view.ChooseDefaultBrowser(null);
            return (options, selectedFirst, afterChoice, back: env.Model.SelectedDefaultBrowserId);
        });

        Assert.Equal(4, r.options.Count);                   // Windows default, Chrome, Comet, Brave (Opera and Firefox are not installed)
        Assert.Contains("Comet", r.options[0]);             // the Windows default says who it is right now
        Assert.Null(r.selectedFirst);
        Assert.Equal("chrome", r.afterChoice.Item1);
        Assert.Equal("chrome", r.afterChoice.model);
        Assert.Equal("chrome", r.afterChoice.saved);
        Assert.Null(r.back);
    }

    [Fact]
    public void The_buttons_raise_their_requests()
    {
        var r = OnSta(() =>
        {
            using var env = new Env();
            var view = new ShortcutEditorView(env.Model);
            var log = new List<string>();
            view.RescanRequested += () => log.Add("rescan");
            view.AddGameRequested += _ => log.Add("game");
            view.AddBrowserRequested += _ => log.Add("browser");
            view.DoneRequested += () => log.Add("done");

            view.ClickRescan();
            view.ClickAddGame();
            view.ClickAddBrowser();
            view.ClickDone();
            return log;
        });

        Assert.Equal(new[] { "rescan", "game", "browser", "done" }, r.ToArray());
    }

    [Fact]
    public void While_a_rescan_runs_the_button_waits_and_the_status_says_so()
    {
        var r = OnSta(() =>
        {
            using var env = new Env();
            var view = new ShortcutEditorView(env.Model);

            view.SetBusy(true);
            var during = (enabled: view.RescanEnabled, status: view.StatusText);
            view.SetBusy(false);
            return (during, after: view.RescanEnabled);
        });

        Assert.False(r.during.enabled);
        Assert.False(string.IsNullOrWhiteSpace(r.during.status));
        Assert.True(r.after);
    }

    [Fact]
    public void Rebuild_follows_changes_made_elsewhere()
    {
        var ids = OnSta(() =>
        {
            using var env = new Env();
            var view = new ShortcutEditorView(env.Model);
            env.Service.AddGame(new LaunchTarget(ShortcutKind.SteamApp, "221100", null), "DayZ", null);   // e.g. a game added by a scan or the bar
            view.Rebuild();
            return view.RowIds(ShortcutGroup.Game);
        });

        Assert.Equal(3, ids.Count);
    }

    // ---- the missing-browser dialog ----

    [Fact]
    public void The_missing_browser_dialog_offers_the_download_and_installed_alternatives_only()
    {
        var r = OnSta(() =>
        {
            using var env = new Env();
            var pick = env.Model.PickBrowser(env.Model.AddableBrowsers().Single(b => b.Id == "opera"));
            var dialog = new BrowserMissingDialog(pick);
            try
            {
                return (name: dialog.MissingName, hasDownload: dialog.CanOpenDownload, alternatives: dialog.AlternativeIds.ToArray(), outcome: dialog.Outcome);
            }
            finally { dialog.Close(); }
        });

        Assert.Equal("Opera", r.name);
        Assert.True(r.hasDownload);
        Assert.Equal(new[] { "chrome", "comet", "brave" }, r.alternatives);   // installed ones: no Opera, no Firefox, no Internet Explorer
        Assert.Equal(BrowserMissingOutcome.Cancel, r.outcome);                // closing it any other way is "cancel"
    }

    [Fact]
    public void The_dialog_answers_with_the_choice_made()
    {
        var r = OnSta(() =>
        {
            using var env = new Env();
            var pick = env.Model.PickBrowser(env.Model.AddableBrowsers().Single(b => b.Id == "opera"));

            BrowserMissingDialog Make() => new(pick);
            var d1 = Make(); d1.ClickDownload();
            var d2 = Make(); d2.SelectAlternative("comet"); d2.ClickUseOther();
            var d3 = Make(); d3.ClickCancel();
            var d4 = Make(); d4.ClickUseOther();   // nothing selected yet: takes the first one offered
            return (d1.Outcome, d1.DownloadUrl, d2.Outcome, other2: d2.Other?.Id, d3.Outcome, d4.Outcome, other4: d4.Other?.Id);
        });

        Assert.Equal(BrowserMissingOutcome.OpenDownload, r.Item1);
        Assert.Equal("https://www.opera.com/download", r.DownloadUrl);
        Assert.Equal(BrowserMissingOutcome.UseOther, r.Item3);
        Assert.Equal("comet", r.other2);
        Assert.Equal(BrowserMissingOutcome.Cancel, r.Item5);
        Assert.Equal(BrowserMissingOutcome.UseOther, r.Item6);
        Assert.Equal("chrome", r.other4);
    }

    [Fact]
    public void Without_an_installed_alternative_that_choice_is_not_offered()
    {
        var r = OnSta(() =>
        {
            var firefox = new BrowserInfo("firefox", "Mozilla Firefox", null, false, "https://www.firefox.com/");
            var dialog = new BrowserMissingDialog(new BrowserPick(false, firefox, Array.Empty<BrowserInfo>()));
            try { dialog.ClickUseOther(); return (canUseOther: dialog.CanUseOther, outcome: dialog.Outcome); }
            finally { dialog.Close(); }
        });

        Assert.False(r.canUseOther);
        Assert.Equal(BrowserMissingOutcome.Cancel, r.outcome);   // the button does nothing
    }

    [Fact]
    public void A_browser_without_a_download_page_offers_none()
    {
        var dialog = OnSta(() =>
        {
            var edge = new BrowserInfo("edge", "Microsoft Edge", null, false, null);
            var d = new BrowserMissingDialog(new BrowserPick(false, edge, Array.Empty<BrowserInfo>()));
            try { return (d.CanOpenDownload, d.DownloadUrl); }
            finally { d.Close(); }
        });

        Assert.False(dialog.CanOpenDownload);
        Assert.Null(dialog.DownloadUrl);
    }

    // ---- the window ----

    [Fact]
    public void The_editor_window_is_a_topmost_dialog_that_is_never_shown_here()
    {
        var r = OnSta(() =>
        {
            using var env = new Env();
            var w = new ShortcutEditorWindow(env.Service, env.Model, () => { });
            try { return (w.Topmost, w.ShowInTaskbar, w.IsVisible, w.ResizeMode, w.Title, rows: w.View.RowIds(ShortcutGroup.Launcher).Count); }
            finally { w.Close(); }
        });

        Assert.True(r.Topmost);            // above the board's topmost windows
        Assert.False(r.ShowInTaskbar);
        Assert.False(r.IsVisible);
        Assert.False(string.IsNullOrWhiteSpace(r.Title));
        Assert.Equal(8, r.rows);
    }

    [Fact]
    public void The_window_refreshes_when_the_service_changes_from_another_thread()
    {
        var r = OnSta(() =>
        {
            using var env = new Env();
            var w = new ShortcutEditorWindow(env.Service, env.Model, () => { });
            try
            {
                int before = w.View.RowIds(ShortcutGroup.Game).Count;
                System.Threading.Tasks.Task.Run(() => env.Service.AddGame(new LaunchTarget(ShortcutKind.SteamApp, "221100", null), "DayZ", null)).Wait();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { }));
                return (before, after: w.View.RowIds(ShortcutGroup.Game).Count);
            }
            finally { w.Close(); }
        });

        Assert.Equal(2, r.before);
        Assert.Equal(3, r.after);
    }

    [Fact]
    public void A_changed_hotkey_tells_the_host_so_it_can_register_it()
    {
        var calls = OnSta(() =>
        {
            using var env = new Env();
            int n = 0;
            var w = new ShortcutEditorWindow(env.Service, env.Model, () => n++);
            try
            {
                w.View.HotkeyButton(env.FirstGameId).Accept(new HotkeyBinding { Modifiers = HotkeyModifier.Control | HotkeyModifier.Alt, Key = 0x32 });
                return n;
            }
            finally { w.Close(); }
        });

        Assert.Equal(1, calls);
    }

    [Fact]
    public void The_rescan_button_prunes_what_is_still_missing()
    {
        var r = OnSta(() =>
        {
            using var env = new Env();
            env.Shell.Links = env.Shell.Links.Where(l => !l.Path.Contains("Rockstar")).ToList();   // Rockstar was uninstalled
            var w = new ShortcutEditorWindow(env.Service, env.Model, () => { });
            try
            {
                var before = w.View.RowIds(ShortcutGroup.Launcher).ToArray();
                w.View.ClickRescan();   // "Neu suchen": scan on a pool thread, prune what is still missing, refresh on this thread
                PumpUntil(() => w.LastRescan is { IsCompleted: true } && w.View.RescanEnabled);
                return (before, after: w.View.RowIds(ShortcutGroup.Launcher).ToArray(), busy: !w.View.RescanEnabled);
            }
            finally { w.Close(); }
        });

        Assert.Contains("launcher:rockstar", r.before);        // still there before the click ...
        Assert.DoesNotContain("launcher:rockstar", r.after);   // ... gone after it
        Assert.Contains("launcher:steam", r.after);
        Assert.False(r.busy);
    }

    /// <summary>Runs the dispatcher of this STA thread until the condition holds (the editor's awaits continue on it).</summary>
    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < deadline)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { }));
            System.Threading.Thread.Sleep(10);
        }
    }
}