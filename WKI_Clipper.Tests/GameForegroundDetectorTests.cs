using System;
using System.Collections.Generic;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// "A game is in front" had two definitions (the live <see cref="GameDetector"/> and the Discord option's own
/// foreground check). <see cref="GameForegroundDetector"/> is now a thin adapter over the detector; these
/// tests keep the behaviour the compress-wait relied on (own window over the game, tray-click transients,
/// a window that becomes fullscreen later) and pin the detector rules that make it work.
/// </summary>
public sealed class GameForegroundDetectorTests
{
    private const int Self = 100;
    private static readonly LayoutRect Mon = new(0, 0, 2560, 1440);
    private static readonly LayoutRect Mon2 = new(2560, 0, 1920, 1080);
    private static readonly LayoutRect Small = new(100, 100, 1600, 900);

    private sealed class Rig
    {
        public long Now = 1_000;
        public readonly Dictionary<IntPtr, ForegroundFacts?> Windows = new();
        public readonly HashSet<int> Alive = new();
        public IntPtr Foreground;
        public readonly GameDetector Detector;

        public Rig()
        {
            Detector = new GameDetector(
                windowModeTarget: () => null,
                probe: h => Windows.TryGetValue(h, out var f) ? f : null,
                foreground: () => Foreground,
                displayName: f => "Name of " + f.ExeName,
                isAlive: g => Alive.Contains(g.Pid),
                clock: () => Now,
                selfPid: Self,
                autoTimer: false);
        }

        public static ForegroundFacts Facts(string exe, int pid, LayoutRect? window = null, LayoutRect? monitor = null)
        {
            var mon = monitor ?? Mon;
            return new ForegroundFacts(pid, @"C:\Games\" + exe, exe, window ?? mon, mon, false, false, false, false);
        }

        public IntPtr Window(int id, string exe, int pid, LayoutRect? window = null, LayoutRect? monitor = null)
        {
            var h = new IntPtr(id);
            Windows[h] = Facts(exe, pid, window, monitor);
            return h;
        }

        public IntPtr Own(int id = 90) => Window(id, "WKI_Clipper.exe", Self);

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

        public bool Wait() => GameForegroundDetector.IsGameInForeground(Detector);
    }

    [Fact]
    public void No_detector_yet_means_no_game()
        => Assert.False(GameForegroundDetector.IsGameInForeground(null));

    [Fact]
    public void A_fullscreen_game_in_front_counts_at_once_while_the_settled_state_follows_after_the_debounce()
    {
        var rig = new Rig();
        rig.Alive.Add(777);
        rig.Focus(rig.Window(1, "Arma.exe", 777));

        Assert.True(rig.Wait());               // live re-probe: no need to wait for the 1.5 s debounce
        Assert.Null(rig.Detector.Current);     // the settled state (overlay, FPS, profiles) still waits
        rig.Advance(1500);
        Assert.NotNull(rig.Detector.Current);
        Assert.True(rig.Wait());
    }

    [Fact]
    public void A_small_window_or_a_browser_is_not_a_game()
    {
        var rig = new Rig();
        rig.Focus(rig.Window(1, "notes.exe", 5, window: new LayoutRect(100, 100, 900, 700)));
        rig.Advance(2000);
        Assert.False(rig.Wait());

        rig.Focus(rig.Window(2, "chrome.exe", 6));
        rig.Advance(2000);
        Assert.False(rig.Wait());
    }

    [Fact]
    public void Opening_the_clippers_own_board_over_the_game_still_counts_as_a_game()
    {
        var rig = new Rig();
        rig.Alive.Add(777);
        rig.Focus(rig.Window(1, "Arma.exe", 777));
        rig.Advance(1500);

        rig.Focus(rig.Own());
        rig.Advance(10_000);

        Assert.True(rig.Wait());
    }

    [Fact]
    public void A_game_that_exits_while_the_board_is_open_stops_holding_the_compress_back()
    {
        var rig = new Rig();
        rig.Alive.Add(777);
        var game = rig.Window(1, "Arma.exe", 777);
        rig.Focus(game);
        rig.Advance(1500);
        rig.Focus(rig.Own());
        Assert.True(rig.Wait());

        rig.Alive.Remove(777);                  // the game quit behind the board ...
        rig.Windows[game] = null;               // ... and its window is gone
        Assert.True(rig.Wait());                // the check starts the drop (still inside the debounce)
        rig.Advance(1500);
        Assert.False(rig.Wait());
    }

    [Fact]
    public void Leaving_the_game_for_the_desktop_stops_holding_the_compress_back()
    {
        var rig = new Rig();
        rig.Alive.Add(777);
        rig.Focus(rig.Window(1, "Arma.exe", 777));
        rig.Advance(1500);

        rig.Focus(rig.Window(3, "explorer.exe", 5));
        rig.Advance(1500);

        Assert.False(rig.Wait());
        Assert.Null(rig.Detector.Current);
    }

    // ---- tray click / start menu: a shell window in front for a moment, then our own ----

    [Fact]
    public void A_tray_click_that_puts_the_taskbar_in_front_before_our_window_does_not_drop_the_game()
    {
        var rig = new Rig();
        rig.Alive.Add(777);
        rig.Focus(rig.Window(1, "Arma.exe", 777));
        rig.Advance(1500);

        rig.Focus(rig.Window(3, "explorer.exe", 5));   // Shell_TrayWnd
        rig.Focus(rig.Own());                          // the board opens
        rig.Advance(1500);
        rig.Advance(10_000);

        Assert.True(rig.Wait());
        Assert.Equal(777, rig.Detector.Current!.Pid);
    }

    [Fact]
    public void The_start_menu_before_our_window_is_a_transient_too()
    {
        var rig = new Rig();
        rig.Alive.Add(777);
        rig.Focus(rig.Window(1, "Arma.exe", 777));
        rig.Advance(1500);

        rig.Focus(rig.Window(4, "StartMenuExperienceHost.exe", 6));
        rig.Focus(rig.Own());
        rig.Advance(10_000);

        Assert.Equal(777, rig.Detector.Current!.Pid);
    }

    [Fact]
    public void Game_then_desktop_still_ends_in_no_game_when_our_window_never_comes()
    {
        var rig = new Rig();
        rig.Alive.Add(777);
        rig.Focus(rig.Window(1, "Arma.exe", 777));
        rig.Advance(1500);

        rig.Focus(rig.Window(3, "explorer.exe", 5, window: Mon));   // the desktop itself
        rig.Advance(1500);

        Assert.Null(rig.Detector.Current);
    }

    [Fact]
    public void Game_then_a_browser_then_the_board_is_no_game()
    {
        var rig = new Rig();
        rig.Alive.Add(777);
        rig.Focus(rig.Window(1, "Arma.exe", 777));
        rig.Advance(1500);

        rig.Focus(rig.Window(2, "chrome.exe", 6));
        rig.Focus(rig.Own());
        rig.Advance(1500);

        Assert.Null(rig.Detector.Current);
        Assert.False(rig.Wait());   // the window before ours was the browser, not the game
    }

    [Fact]
    public void A_browser_followed_by_the_taskbar_followed_by_the_board_is_still_no_game()
    {
        var rig = new Rig();
        rig.Alive.Add(777);
        rig.Focus(rig.Window(1, "Arma.exe", 777));
        rig.Advance(1500);

        rig.Focus(rig.Window(2, "chrome.exe", 6));      // really left the game ...
        rig.Focus(rig.Window(3, "explorer.exe", 5));    // ... then the taskbar ...
        rig.Focus(rig.Own());                           // ... then our window
        rig.Advance(1500);

        Assert.Null(rig.Detector.Current);
    }

    [Fact]
    public void A_shell_only_no_game_state_is_flagged_so_the_detector_can_tell_a_transient_from_leaving()
    {
        var d = new GameDebounce();
        var game = new GameInfo(1, "a.exe", "A");
        d.Observe(game, 0);
        d.Poll(2000);

        d.Observe(null, 3000, shellWindow: true);
        Assert.True(d.PendingIsShellTransient);

        d.Observe(null, 3100, shellWindow: false);   // same pending candidate (null), but a real window this time
        Assert.False(d.PendingIsShellTransient);
        Assert.Equal(3000 + GameDebounce.DelayMs, d.DueAtMs);   // the wait was not restarted

        d.CancelPending();
        Assert.False(d.HasPending);
        Assert.Equal(game, d.Current);
    }

    [Fact]
    public void A_pending_game_is_never_a_shell_transient()
    {
        var d = new GameDebounce();
        d.Observe(new GameInfo(1, "a.exe", "A"), 0, shellWindow: true);
        Assert.False(d.PendingIsShellTransient);
    }

    // ---- live re-probe ----

    [Fact]
    public void A_window_that_becomes_borderless_after_it_got_the_focus_is_noticed_while_the_board_is_open()
    {
        var rig = new Rig();
        rig.Alive.Add(777);
        var game = rig.Window(1, "Arma.exe", 777, window: Small);   // windowed when it got the focus
        rig.Focus(game);
        rig.Advance(2000);
        Assert.Null(rig.Detector.Current);

        rig.Focus(rig.Own());
        Assert.False(rig.Wait());

        rig.Windows[game] = Rig.Facts("Arma.exe", 777);             // the user switched to borderless; no focus event
        Assert.True(rig.Wait());
        rig.Advance(1500);
        Assert.Equal(777, rig.Detector.Current!.Pid);               // the settled state caught up
    }

    [Fact]
    public void A_game_that_went_back_to_a_window_stops_holding_the_compress_back()
    {
        var rig = new Rig();
        rig.Alive.Add(777);
        var game = rig.Window(1, "Arma.exe", 777);
        rig.Focus(game);
        rig.Advance(1500);
        rig.Focus(rig.Own());
        Assert.True(rig.Wait());

        rig.Windows[game] = Rig.Facts("Arma.exe", 777, window: Small);
        Assert.False(rig.Wait());
    }

    [Fact]
    public void A_window_that_cannot_be_probed_keeps_the_settled_state()
    {
        var rig = new Rig();
        rig.Alive.Add(777);
        rig.Focus(rig.Window(1, "Arma.exe", 777));
        rig.Advance(1500);

        rig.Foreground = new IntPtr(555);   // some window the probe cannot read
        Assert.True(rig.Wait());
    }

    [Fact]
    public void A_disposed_detector_does_not_blow_up_the_compress_poll()
    {
        var rig = new Rig();
        rig.Alive.Add(777);
        rig.Focus(rig.Window(1, "Arma.exe", 777));
        rig.Advance(1500);
        rig.Detector.Dispose();
        // A disposed detector keeps its last state; the poll must still answer instead of throwing.
        _ = rig.Wait();
    }

    // ---- which game is in front RIGHT NOW (the F9 length follows the footage, not the debounced state) ----

    [Fact]
    public void The_game_in_front_is_named_at_once_without_touching_the_settled_state()
    {
        var rig = new Rig();
        rig.Alive.Add(777);
        rig.Focus(rig.Window(1, "Arma.exe", 777));

        Assert.Null(rig.Detector.Current);                          // inside the 1.5 s debounce
        Assert.Equal("Arma.exe", rig.Detector.GameExeInFrontNow());
        Assert.Null(rig.Detector.Current);                          // a plain question: nothing was fed to the debounce
    }

    [Fact]
    public void With_our_window_in_front_the_last_external_game_is_named_even_before_it_was_settled()
    {
        var rig = new Rig();
        rig.Alive.Add(777);
        rig.Focus(rig.Window(1, "Arma.exe", 777));
        rig.Focus(rig.Own());                                       // Alt-Tab into the clipper within the debounce

        Assert.Null(rig.Detector.Current);
        Assert.Equal("Arma.exe", rig.Detector.GameExeInFrontNow());
    }

    [Fact]
    public void A_tray_click_in_between_does_not_hide_the_game()
    {
        var rig = new Rig();
        rig.Alive.Add(777);
        rig.Focus(rig.Window(1, "Arma.exe", 777));
        rig.Focus(rig.Window(3, "explorer.exe", 5));
        rig.Focus(rig.Own());

        Assert.Equal("Arma.exe", rig.Detector.GameExeInFrontNow());
    }

    [Fact]
    public void A_browser_chat_or_small_window_in_front_names_no_game_even_while_the_settled_game_is_another()
    {
        var rig = new Rig();
        rig.Alive.Add(777);
        rig.Focus(rig.Window(1, "Arma.exe", 777));
        rig.Advance(1500);
        Assert.NotNull(rig.Detector.Current);

        rig.Focus(rig.Window(2, "Discord.exe", 6));
        Assert.Null(rig.Detector.GameExeInFrontNow());
        Assert.NotNull(rig.Detector.Current);                       // still inside the debounce

        rig.Focus(rig.Window(4, "notes.exe", 8, window: Small));
        Assert.Null(rig.Detector.GameExeInFrontNow());

        rig.Focus(rig.Window(5, "chrome.exe", 9));
        rig.Focus(rig.Own());
        Assert.Null(rig.Detector.GameExeInFrontNow());              // the window before ours was the browser
    }

    [Fact]
    public void An_unreadable_window_or_a_disposed_detector_names_no_game()
    {
        var rig = new Rig();
        rig.Alive.Add(777);
        rig.Focus(rig.Window(1, "Arma.exe", 777));
        rig.Foreground = new IntPtr(555);                           // a window the probe cannot read
        Assert.Null(rig.Detector.GameExeInFrontNow());

        rig.Foreground = new IntPtr(1);
        rig.Detector.Dispose();
        Assert.Null(rig.Detector.GameExeInFrontNow());
    }

    // ---- Verify must not take down a game that was committed while it was checking ----

    [Fact]
    public void A_game_committed_while_the_liveness_check_runs_is_not_dropped()
    {
        long now = 1_000;
        var windows = new Dictionary<IntPtr, ForegroundFacts?>();
        GameDetector? det = null;
        bool armed = true;
        det = new GameDetector(
            windowModeTarget: () => null,
            probe: h => windows.TryGetValue(h, out var f) ? f : null,
            foreground: () => IntPtr.Zero,
            displayName: f => f.ExeName,
            // While game A is being checked (dead), the pending game B matures and is committed on another thread.
            isAlive: g =>
            {
                if (g.Pid != 777 || !armed) return true;
                armed = false;
                now += 1500;
                det!.Pump();
                return false;
            },
            clock: () => now, selfPid: Self, autoTimer: false);

        windows[new IntPtr(1)] = Rig.Facts("A.exe", 777);
        windows[new IntPtr(2)] = Rig.Facts("B.exe", 888);
        det.OnForegroundChanged(new IntPtr(1));
        now += 1500; det.Pump();
        Assert.Equal(777, det.Current!.Pid);
        det.OnForegroundChanged(new IntPtr(2));          // B is pending

        det.Verify();

        Assert.Equal(888, det.Current!.Pid);
        now += 5000; det.Pump();
        Assert.Equal(888, det.Current!.Pid);             // and nothing is pending to drop it later
    }

    // ---- the monitor the game is on travels with GameInfo (crosshair profiles are placed relative to it) ----

    [Fact]
    public void The_detected_game_carries_the_monitor_and_the_window_it_was_seen_with()
    {
        var rig = new Rig();
        rig.Alive.Add(777);
        rig.Focus(rig.Window(1, "Arma.exe", 777, monitor: Mon2));
        rig.Advance(1500);

        Assert.Equal(Mon2, rig.Detector.Current!.Monitor);
        Assert.Equal(new IntPtr(1), rig.Detector.Current!.Window);
    }

    [Fact]
    public void Coming_back_to_the_same_game_refreshes_its_window_and_monitor_without_an_announcement()
    {
        var rig = new Rig();
        rig.Alive.Add(777);
        var announced = 0;
        rig.Detector.GameChanged += _ => announced++;
        rig.Focus(rig.Window(1, "Arma.exe", 777, monitor: Mon));
        rig.Advance(1500);
        Assert.Equal(1, announced);

        rig.Focus(rig.Own());
        rig.Focus(rig.Window(7, "Arma.exe", 777, monitor: Mon2));   // recreated window, now on the other monitor
        rig.Advance(5000);

        Assert.Equal(1, announced);
        Assert.Equal(new IntPtr(7), rig.Detector.Current!.Window);
        Assert.Equal(Mon2, rig.Detector.Current!.Monitor);
    }

    [Fact]
    public void Neither_monitor_nor_window_make_two_sightings_of_the_same_game_look_different()
    {
        var a = new GameInfo(5, "Arma.exe", "Arma") { Monitor = Mon, Window = new IntPtr(1) };
        var b = new GameInfo(5, "ARMA.exe", "Arma Reforger") { Monitor = Mon2, Window = new IntPtr(2) };
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        var plain = new GameInfo(5, "Arma.exe", "Arma");
        Assert.Null(plain.Monitor);
        Assert.Equal(IntPtr.Zero, plain.Window);
    }

    [Fact]
    public void The_old_pixel_rule_now_lives_in_one_place_with_the_same_answers()
    {
        var m = new LayoutRect(0, 0, 3440, 1440);
        Assert.True(GameDetector.CoversMonitor(new LayoutRect(0, 0, 3440, 1440), m));          // borderless fullscreen
        Assert.True(GameDetector.CoversMonitor(new LayoutRect(-1, -1, 3442, 1442), m));        // a driver overshooting by a pixel
        Assert.False(GameDetector.CoversMonitor(new LayoutRect(0, 0, 3440, 1400), m));         // maximized above a taskbar
        Assert.False(GameDetector.CoversMonitor(new LayoutRect(100, 100, 1400, 800), m));      // ordinary window
        Assert.False(GameDetector.CoversMonitor(new LayoutRect(-32000, -32000, 160, 28), m));  // minimized
    }
}
