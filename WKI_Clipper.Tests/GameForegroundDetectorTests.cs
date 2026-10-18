using System;
using System.Collections.Generic;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// "A game is in front" had two definitions (the live <see cref="GameDetector"/> and the Discord option's own
/// foreground check). <see cref="GameForegroundDetector"/> is now a thin adapter over the detector; these
/// tests keep the behaviour the compress-wait relied on.
/// </summary>
public sealed class GameForegroundDetectorTests
{
    private const int Self = 100;
    private static readonly LayoutRect Mon = new(0, 0, 2560, 1440);
    private static readonly LayoutRect Mon2 = new(2560, 0, 1920, 1080);

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

        public IntPtr Window(int id, string exe, int pid, LayoutRect? window = null, LayoutRect? monitor = null)
        {
            var mon = monitor ?? Mon;
            var h = new IntPtr(id);
            Windows[h] = new ForegroundFacts(pid, @"C:\Games\" + exe, exe, window ?? mon, mon, false, false, false, false);
            return h;
        }

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
    public void No_detector_yet_means_no_game()
        => Assert.False(GameForegroundDetector.IsGameInForeground(null));

    [Fact]
    public void A_fullscreen_game_in_front_counts_after_the_detectors_debounce()
    {
        var rig = new Rig();
        rig.Alive.Add(777);
        rig.Focus(rig.Window(1, "Arma.exe", 777));

        Assert.False(GameForegroundDetector.IsGameInForeground(rig.Detector));   // still inside the 1.5 s debounce
        rig.Advance(1500);
        Assert.True(GameForegroundDetector.IsGameInForeground(rig.Detector));
    }

    [Fact]
    public void A_small_window_or_a_browser_is_not_a_game()
    {
        var rig = new Rig();
        rig.Focus(rig.Window(1, "notes.exe", 5, window: new LayoutRect(100, 100, 900, 700)));
        rig.Advance(2000);
        Assert.False(GameForegroundDetector.IsGameInForeground(rig.Detector));

        rig.Focus(rig.Window(2, "chrome.exe", 6));
        rig.Advance(2000);
        Assert.False(GameForegroundDetector.IsGameInForeground(rig.Detector));
    }

    [Fact]
    public void Opening_the_clippers_own_board_over_the_game_still_counts_as_a_game()
    {
        var rig = new Rig();
        rig.Alive.Add(777);
        rig.Focus(rig.Window(1, "Arma.exe", 777));
        rig.Advance(1500);

        rig.Focus(rig.Window(2, "WKI_Clipper.exe", Self));
        rig.Advance(10_000);

        Assert.True(GameForegroundDetector.IsGameInForeground(rig.Detector));
    }

    [Fact]
    public void A_game_that_exits_while_the_board_is_open_stops_holding_the_compress_back()
    {
        var rig = new Rig();
        rig.Alive.Add(777);
        rig.Focus(rig.Window(1, "Arma.exe", 777));
        rig.Advance(1500);
        rig.Focus(rig.Window(2, "WKI_Clipper.exe", Self));
        Assert.True(GameForegroundDetector.IsGameInForeground(rig.Detector));

        rig.Alive.Remove(777);                                                  // the game quit behind the board
        Assert.True(GameForegroundDetector.IsGameInForeground(rig.Detector));  // the adapter's check starts the drop ...
        rig.Advance(1500);                                                      // ... which settles after the debounce
        Assert.False(GameForegroundDetector.IsGameInForeground(rig.Detector));
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

        Assert.False(GameForegroundDetector.IsGameInForeground(rig.Detector));
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
        _ = GameForegroundDetector.IsGameInForeground(rig.Detector);
    }

    // ---- the monitor the game is on travels with GameInfo (crosshair profiles are placed relative to it) ----

    [Fact]
    public void The_detected_game_carries_the_monitor_it_is_on()
    {
        var rig = new Rig();
        rig.Alive.Add(777);
        rig.Focus(rig.Window(1, "Arma.exe", 777, monitor: Mon2));
        rig.Advance(1500);

        Assert.Equal(Mon2, rig.Detector.Current!.Monitor);
    }

    [Fact]
    public void The_monitor_does_not_make_two_sightings_of_the_same_game_look_different()
    {
        var a = new GameInfo(5, "Arma.exe", "Arma") { Monitor = Mon };
        var b = new GameInfo(5, "ARMA.exe", "Arma Reforger") { Monitor = Mon2 };
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Null(new GameInfo(5, "Arma.exe", "Arma").Monitor);
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
