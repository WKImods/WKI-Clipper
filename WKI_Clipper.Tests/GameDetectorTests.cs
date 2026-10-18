using System;
using System.Collections.Generic;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// "Is a game in front, which one": the pure rules (blocklist, window-covers-monitor, Window-mode
/// target, own process), the 1.5 s debounce, and the detector's event flow against a fake window probe
/// and a manual clock. The Win32 probe itself needs a real desktop and is checked by hand.
/// </summary>
public sealed class GameDetectorTests
{
    private const int Self = 100;
    private static readonly LayoutRect Mon = new(0, 0, 1920, 1080);

    private static ForegroundFacts Facts(string exe = "Game.exe", int pid = 4242, LayoutRect? window = null,
        bool minimized = false, bool maximized = false, bool caption = false, bool windowsImage = false)
        => new(pid, @"C:\Games\" + exe, exe, window ?? Mon, Mon, minimized, maximized, caption, windowsImage);

    private static GameInfo Game(int pid = 4242, string exe = "Game.exe", string name = "Game") => new(pid, exe, name);

    // ---- blocklist ----

    [Theory]
    [InlineData("chrome")] [InlineData("msedge")] [InlineData("firefox")] [InlineData("opera")]
    [InlineData("brave")] [InlineData("comet")]
    [InlineData("discord")] [InlineData("slack")] [InlineData("teams")] [InlineData("ms-teams")]
    [InlineData("explorer")] [InlineData("steam")] [InlineData("steamwebhelper")] [InlineData("epicgameslauncher")]
    [InlineData("obs64")] [InlineData("WKI_Clipper")] [InlineData("Code")] [InlineData("devenv")]
    [InlineData("notepad")] [InlineData("notepad++")] [InlineData("Notepad2")]
    [InlineData("CHROME")] [InlineData("chrome.exe")] [InlineData("applicationframehost")] [InlineData("msedgewebview2")]
    public void Browsers_chat_launchers_editors_and_the_shell_are_never_games(string name)
        => Assert.True(GameDetector.IsBlocked(name), name);

    [Theory]
    [InlineData("ArmaReforgerSteam")] [InlineData("ArmaReforger")] [InlineData("cs2")]
    [InlineData("VALORANT-Win64-Shipping")] [InlineData("CodeVein-Win64-Shipping")]   // "Code" is exact, not a prefix
    [InlineData("SteamWorldDig")] [InlineData("TeamFortress2")] [InlineData("RocketLeague")]
    public void Games_are_not_blocked(string name)
        => Assert.False(GameDetector.IsBlocked(name), name);

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("  ")]
    public void Nothing_to_look_at_counts_as_blocked(string? name)
        => Assert.True(GameDetector.IsBlocked(name));

    // ---- covers the monitor ----

    [Fact]
    public void A_window_exactly_on_the_monitor_covers_it()
        => Assert.True(GameDetector.CoversMonitor(Mon, Mon));

    [Fact]
    public void A_borderless_window_that_overhangs_the_monitor_covers_it()
        => Assert.True(GameDetector.CoversMonitor(new LayoutRect(-8, -8, 1936, 1096), Mon));

    [Fact]
    public void One_or_two_pixels_short_still_counts()
        => Assert.True(GameDetector.CoversMonitor(new LayoutRect(1, 1, 1918, 1078), Mon));

    [Fact]
    public void A_window_clearly_smaller_than_the_monitor_does_not_cover_it()
    {
        Assert.False(GameDetector.CoversMonitor(new LayoutRect(100, 100, 800, 600), Mon));
        Assert.False(GameDetector.CoversMonitor(new LayoutRect(0, 0, 1920, 1040), Mon));   // maximized above a taskbar
        Assert.False(GameDetector.CoversMonitor(new LayoutRect(0, 0, 1900, 1080), Mon));
    }

    [Fact]
    public void A_window_on_another_monitor_does_not_cover_this_one()
        => Assert.False(GameDetector.CoversMonitor(new LayoutRect(1920, 0, 1920, 1080), Mon));

    [Fact]
    public void An_empty_monitor_is_never_covered()
        => Assert.False(GameDetector.CoversMonitor(Mon, new LayoutRect(0, 0, 0, 0)));

    // ---- classification ----

    [Fact]
    public void A_fullscreen_app_is_a_game()
        => Assert.True(GameDetector.IsGame(Facts(), windowModeTarget: null, Self));

    [Fact]
    public void The_clipper_itself_is_never_a_game()
        => Assert.False(GameDetector.IsGame(Facts("WKI_Clipper.exe", pid: Self), null, Self));

    [Fact]
    public void Even_another_clipper_process_name_is_never_a_game()
        => Assert.False(GameDetector.IsGame(Facts("WKI_Clipper.exe", pid: 555), null, Self));

    [Fact]
    public void A_fullscreen_browser_is_not_a_game()
        => Assert.False(GameDetector.IsGame(Facts("chrome.exe"), null, Self));

    [Fact]
    public void A_small_window_is_not_a_game()
        => Assert.False(GameDetector.IsGame(Facts(window: new LayoutRect(200, 200, 900, 700)), null, Self));

    [Fact]
    public void A_minimized_window_is_not_a_game()
        => Assert.False(GameDetector.IsGame(Facts(minimized: true), null, Self));

    [Fact]
    public void Windows_own_ui_is_not_a_game()
        => Assert.False(GameDetector.IsGame(Facts("SystemSettings.exe", windowsImage: true), null, Self));

    [Fact]
    public void A_maximized_normal_app_with_a_hidden_taskbar_is_not_a_game()
        => Assert.False(GameDetector.IsGame(
            Facts("blender.exe", window: new LayoutRect(-8, -8, 1936, 1096), maximized: true, caption: true), null, Self));

    [Fact]
    public void A_windowed_game_at_native_resolution_still_is_one()
    {
        // Windowed at the monitor's resolution: it has a title bar and is bigger than the monitor, but it is
        // not "maximized" — a game, not a maximized document window.
        var f = Facts(window: new LayoutRect(-8, -31, 1936, 1119), caption: true);
        Assert.True(GameDetector.IsGame(f, null, Self));
    }

    [Fact]
    public void The_window_mode_target_is_a_game_even_when_its_window_is_small()
    {
        var f = Facts("ArmaReforgerSteam.exe", window: new LayoutRect(300, 200, 1000, 700), caption: true);
        Assert.False(GameDetector.IsGame(f, null, Self));
        Assert.True(GameDetector.IsGame(f, "ArmaReforgerSteam", Self));
    }

    [Theory]
    [InlineData("armareforgersteam")]
    [InlineData("ArmaReforgerSteam.exe")]
    public void The_window_mode_target_matches_without_case_or_extension(string target)
    {
        var f = Facts("ArmaReforgerSteam.exe", window: new LayoutRect(300, 200, 1000, 700));
        Assert.True(GameDetector.IsGame(f, target, Self));
    }

    [Fact]
    public void A_different_window_while_a_target_is_set_follows_the_normal_rules()
    {
        var small = Facts("notmytarget.exe", window: new LayoutRect(300, 200, 1000, 700));
        Assert.False(GameDetector.IsGame(small, "ArmaReforgerSteam", Self));
        Assert.True(GameDetector.IsGame(Facts("other.exe"), "ArmaReforgerSteam", Self));   // fullscreen: still a game
    }

    [Fact]
    public void A_minimized_target_is_not_in_front()
        => Assert.False(GameDetector.IsGame(Facts("Target.exe", minimized: true), "Target", Self));

    [Fact]
    public void A_target_in_the_clipper_process_is_still_refused()
        => Assert.False(GameDetector.IsGame(Facts("WKI_Clipper.exe", pid: Self), "WKI_Clipper", Self));

    // ---- identity ----

    [Fact]
    public void A_game_is_its_pid_plus_exe_name_not_its_display_name()
    {
        Assert.Equal(Game(1, "A.exe", "Name One"), Game(1, "a.EXE", "Another label"));
        Assert.NotEqual(Game(1, "A.exe"), Game(2, "A.exe"));      // restarted: new process
        Assert.NotEqual(Game(1, "A.exe"), Game(1, "B.exe"));      // pid reused by another program
        Assert.Equal(Game(1, "A.exe").GetHashCode(), Game(1, "a.exe", "x").GetHashCode());
    }

    // ---- debounce ----

    [Fact]
    public void A_new_game_counts_only_after_it_stayed_in_front_for_1_5_seconds()
    {
        var d = new GameDebounce();
        d.Observe(Game(), 0);
        Assert.False(d.Poll(1499));
        Assert.Null(d.Current);
        Assert.True(d.Poll(1500));
        Assert.Equal(Game(), d.Current);
        Assert.False(d.Poll(5000));   // nothing pending any more
    }

    [Fact]
    public void Seeing_the_same_candidate_again_does_not_restart_the_wait()
    {
        var d = new GameDebounce();
        d.Observe(Game(), 0);
        d.Observe(Game(), 1000);
        d.Observe(Game(), 1400);
        Assert.True(d.Poll(1500));
    }

    [Fact]
    public void A_quick_alt_tab_away_and_back_changes_nothing()
    {
        var d = new GameDebounce();
        d.Observe(Game(), 0);
        d.Poll(1500);

        d.Observe(null, 10_000);          // browser in front
        Assert.False(d.Poll(11_000));
        d.Observe(Game(), 11_000);        // back within 1.5 s
        Assert.False(d.Poll(13_000));
        Assert.Equal(Game(), d.Current);
        Assert.False(d.HasPending);
    }

    [Fact]
    public void Leaving_the_game_for_good_is_reported_after_the_debounce()
    {
        var d = new GameDebounce();
        d.Observe(Game(), 0);
        d.Poll(1500);

        d.Observe(null, 20_000);
        Assert.False(d.Poll(21_499));
        Assert.True(d.Poll(21_500));
        Assert.Null(d.Current);
    }

    [Fact]
    public void A_different_candidate_restarts_the_wait()
    {
        var d = new GameDebounce();
        d.Observe(Game(1, "A.exe"), 0);
        d.Observe(Game(2, "B.exe"), 1000);
        Assert.False(d.Poll(1500));
        Assert.True(d.Poll(2500));
        Assert.Equal(2, d.Current!.Pid);
    }

    [Fact]
    public void Switching_straight_from_one_game_to_another_goes_through_the_debounce_once()
    {
        var d = new GameDebounce();
        d.Observe(Game(1, "A.exe"), 0);
        d.Poll(1500);
        d.Observe(Game(2, "B.exe"), 5000);
        Assert.Equal(1, d.Current!.Pid);
        Assert.True(d.Poll(6500));
        Assert.Equal(2, d.Current!.Pid);
    }

    [Fact]
    public void The_due_time_is_when_the_pending_candidate_matures()
    {
        var d = new GameDebounce();
        Assert.Null(d.DueAtMs);
        d.Observe(Game(), 300);
        Assert.Equal(300 + GameDebounce.DelayMs, d.DueAtMs);
        d.Poll(1800);
        Assert.Null(d.DueAtMs);
    }

    [Fact]
    public void Nothing_in_front_and_nothing_current_stays_quiet()
    {
        var d = new GameDebounce();
        d.Observe(null, 0);
        Assert.False(d.HasPending);
        Assert.False(d.Poll(10_000));
    }

    // ---- detector flow ----

    private sealed class Rig
    {
        public long Now = 1_000;
        public string? Target;
        public readonly Dictionary<IntPtr, ForegroundFacts?> Windows = new();
        public readonly HashSet<int> Alive = new();
        public int NameLookups;
        public readonly List<GameInfo?> Events = new();
        public IntPtr Foreground;
        public readonly GameDetector Detector;

        public Rig()
        {
            Detector = new GameDetector(
                windowModeTarget: () => Target,
                probe: h => Windows.TryGetValue(h, out var f) ? f : null,
                foreground: () => Foreground,
                displayName: f => { NameLookups++; return "Name of " + f.ExeName; },
                isAlive: g => Alive.Contains(g.Pid),
                clock: () => Now,
                selfPid: Self,
                autoTimer: false);
            Detector.GameChanged += g => Events.Add(g);
        }

        public IntPtr Window(int id, ForegroundFacts? facts)
        {
            var h = new IntPtr(id);
            Windows[h] = facts;
            return h;
        }

        /// <summary>Moves the foreground to <paramref name="h"/> at the current time (the hook's event).</summary>
        public void Focus(IntPtr h)
        {
            Foreground = h;
            Detector.OnForegroundChanged(h);
        }

        public void Advance(long ms)
        {
            Now += ms;
            Detector.Pump();
        }
    }

    [Fact]
    public void A_game_coming_to_the_front_is_announced_after_the_debounce_with_its_identity()
    {
        var rig = new Rig();
        var game = rig.Window(1, Facts("Arma.exe", pid: 777));
        rig.Alive.Add(777);

        rig.Focus(game);
        rig.Advance(1000);
        Assert.Empty(rig.Events);
        Assert.Null(rig.Detector.Current);

        rig.Advance(500);
        var info = Assert.Single(rig.Events)!;
        Assert.Equal(777, info.Pid);
        Assert.Equal("Arma.exe", info.ExeName);
        Assert.Equal("Name of Arma.exe", info.DisplayName);
        Assert.Equal(info, rig.Detector.Current);
    }

    [Fact]
    public void Alt_tabbing_to_the_browser_and_back_within_the_debounce_announces_nothing_new()
    {
        var rig = new Rig();
        var game = rig.Window(1, Facts("Arma.exe", pid: 777));
        var browser = rig.Window(2, Facts("chrome.exe", pid: 888));
        rig.Alive.Add(777);
        rig.Focus(game);
        rig.Advance(1500);
        Assert.Single(rig.Events);

        rig.Focus(browser);
        rig.Advance(1000);
        rig.Focus(game);
        rig.Advance(5000);

        Assert.Single(rig.Events);
        Assert.Equal(777, rig.Detector.Current!.Pid);
    }

    [Fact]
    public void Leaving_the_game_announces_no_game_after_the_debounce()
    {
        var rig = new Rig();
        var game = rig.Window(1, Facts("Arma.exe", pid: 777));
        var desktop = rig.Window(2, Facts("explorer.exe", pid: 5, window: new LayoutRect(0, 0, 1920, 1080)));
        rig.Focus(game);
        rig.Advance(1500);

        rig.Focus(desktop);
        rig.Advance(1500);

        Assert.Equal(2, rig.Events.Count);
        Assert.Null(rig.Events[1]);
        Assert.Null(rig.Detector.Current);
    }

    [Fact]
    public void The_clippers_own_windows_never_change_the_game()
    {
        var rig = new Rig();
        var game = rig.Window(1, Facts("Arma.exe", pid: 777));
        var own = rig.Window(2, Facts("WKI_Clipper.exe", pid: Self));
        rig.Focus(game);
        rig.Advance(1500);

        rig.Focus(own);                  // clicking in the widget board
        rig.Advance(10_000);

        Assert.Single(rig.Events);
        Assert.Equal(777, rig.Detector.Current!.Pid);
    }

    [Fact]
    public void A_window_that_cannot_be_probed_is_ignored()
    {
        var rig = new Rig();
        var game = rig.Window(1, Facts("Arma.exe", pid: 777));
        var gone = rig.Window(2, null);
        rig.Focus(game);
        rig.Advance(1500);

        rig.Focus(gone);
        rig.Advance(10_000);

        Assert.Single(rig.Events);
    }

    [Fact]
    public void The_display_name_is_only_looked_up_for_games()
    {
        var rig = new Rig();
        rig.Focus(rig.Window(1, Facts("chrome.exe", pid: 1)));
        rig.Focus(rig.Window(2, Facts("small.exe", pid: 2, window: new LayoutRect(10, 10, 500, 400))));
        rig.Focus(rig.Window(3, Facts("explorer.exe", pid: 3)));
        Assert.Equal(0, rig.NameLookups);

        rig.Focus(rig.Window(4, Facts("Arma.exe", pid: 4)));
        Assert.Equal(1, rig.NameLookups);
    }

    [Fact]
    public void Changing_the_window_mode_target_takes_effect_on_reevaluation()
    {
        var rig = new Rig();
        var windowed = rig.Window(1, Facts("Arma.exe", pid: 777, window: new LayoutRect(300, 200, 1000, 700)));
        rig.Focus(windowed);
        rig.Advance(1500);
        Assert.Empty(rig.Events);            // small window, no target: not a game

        rig.Target = "Arma";
        rig.Detector.Reevaluate();
        rig.Advance(1500);

        Assert.Equal(777, Assert.Single(rig.Events)!.Pid);
    }

    [Fact]
    public void Reevaluating_while_nothing_changed_does_not_postpone_the_announcement()
    {
        var rig = new Rig();
        rig.Focus(rig.Window(1, Facts("Arma.exe", pid: 777)));
        rig.Advance(1000);
        rig.Detector.Reevaluate();           // e.g. a settings save
        rig.Advance(500);

        Assert.Single(rig.Events);
    }

    [Fact]
    public void A_game_that_exited_while_the_clipper_was_in_front_is_dropped_by_verification()
    {
        var rig = new Rig();
        var game = rig.Window(1, Facts("Arma.exe", pid: 777));
        rig.Alive.Add(777);
        rig.Focus(game);
        rig.Advance(1500);

        rig.Detector.Verify();               // still alive
        rig.Advance(5000);
        Assert.Single(rig.Events);

        rig.Alive.Clear();                   // the game exited
        rig.Detector.Verify();
        rig.Advance(1500);

        Assert.Equal(2, rig.Events.Count);
        Assert.Null(rig.Events[1]);
    }

    [Fact]
    public void Verification_does_not_cancel_a_newer_game_that_is_about_to_take_over()
    {
        var rig = new Rig();
        var a = rig.Window(1, Facts("A.exe", pid: 1));
        var b = rig.Window(2, Facts("B.exe", pid: 2));
        rig.Alive.Add(1);
        rig.Focus(a);
        rig.Advance(1500);

        rig.Alive.Clear();                   // A exited ...
        rig.Alive.Add(2);
        rig.Focus(b);                        // ... and B came to the front
        rig.Detector.Verify();               // must not replace B's pending announcement with "no game"
        rig.Advance(1500);

        Assert.Equal(2, rig.Detector.Current!.Pid);
    }

    [Fact]
    public void A_throwing_listener_does_not_stop_the_others()
    {
        var rig = new Rig();
        int second = 0;
        rig.Detector.GameChanged += _ => throw new InvalidOperationException("boom");
        rig.Detector.GameChanged += _ => second++;

        rig.Focus(rig.Window(1, Facts("Arma.exe", pid: 777)));
        rig.Advance(1500);

        Assert.Equal(1, second);
    }
}
