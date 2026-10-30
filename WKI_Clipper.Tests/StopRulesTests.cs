using System;
using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>The per-iteration stop checks and their order (design section 4), plus the pixel geometry they use.</summary>
public sealed class StopRulesTests
{
    private static readonly PixelRect Main = new(0, 0, 1920, 1080);
    private static readonly PixelRect Left = new(-1920, 0, 1920, 1080);     // left of the primary: negative X
    private static readonly PixelRect Above = new(0, -1080, 1920, 1080);    // above the primary: negative Y

    /// <summary>A context in the middle of one monitor where nothing stops: every check on, none triggered.</summary>
    private static StopContext Quiet() => new()
    {
        Cursor = new PixelPoint(960, 540),
        Monitors = new[] { Main },
        CornerStopEnabled = true,
        CornerSizePx = 50,
        EdgeStopEnabled = true,
        EdgeSizePx = 40,
        TaskSwitcherStopEnabled = true,
        ForegroundClass = "Notepad",
        ForegroundExe = "notepad.exe"
    };

    private static StopReason Eval(StopContext c) => StopRules.Evaluate(c);

    // ---- PixelRect ----

    [Fact]
    public void A_rect_contains_its_top_left_pixel_but_not_the_one_past_the_bottom_right()
    {
        var r = new PixelRect(10, 20, 5, 3);
        Assert.True(r.Contains(10, 20));
        Assert.True(r.Contains(14, 22));
        Assert.False(r.Contains(15, 22));
        Assert.False(r.Contains(14, 23));
        Assert.False(r.Contains(9, 20));
        Assert.False(r.Contains(10, 19));
        Assert.Equal(15, r.Right);
        Assert.Equal(23, r.Bottom);
    }

    [Fact]
    public void A_rect_with_negative_origin_contains_negative_points()
    {
        var r = new PixelRect(-1920, -50, 1920, 100);
        Assert.True(r.Contains(-1920, -50));
        Assert.True(r.Contains(-1, 49));
        Assert.False(r.Contains(0, 0));
        Assert.True(r.Contains(new PixelPoint(-960, 0)));
    }

    // ---- no stop ----

    [Fact]
    public void Nothing_triggered_means_no_stop()
        => Assert.Equal(StopReason.None, Eval(Quiet()));

    // ---- click limit ----

    [Fact]
    public void The_click_limit_stops_at_the_limit_not_before()
    {
        var c = Quiet();
        c.ClickLimitEnabled = true;
        c.ClickLimit = 100;
        c.Clicks = 99;
        Assert.Equal(StopReason.None, Eval(c));
        c.Clicks = 100;
        Assert.Equal(StopReason.ClickLimit, Eval(c));
        c.Clicks = 150;
        Assert.Equal(StopReason.ClickLimit, Eval(c));
    }

    [Fact]
    public void A_disabled_click_limit_never_stops()
    {
        var c = Quiet();
        c.ClickLimit = 1;
        c.Clicks = 1_000_000;
        Assert.Equal(StopReason.None, Eval(c));
    }

    // ---- time limit ----

    [Theory]
    [InlineData(TimeLimitUnit.Seconds, 30.0, 30.0)]
    [InlineData(TimeLimitUnit.Minutes, 2.0, 120.0)]
    [InlineData(TimeLimitUnit.Hours, 0.5, 1800.0)]
    public void The_time_limit_is_converted_to_seconds(TimeLimitUnit unit, double value, double seconds)
        => Assert.Equal(seconds, StopRules.TimeLimitSeconds(value, unit), 9);

    [Fact]
    public void The_time_limit_stops_when_the_elapsed_time_reaches_it()
    {
        var c = Quiet();
        c.TimeLimitEnabled = true;
        c.TimeLimitSeconds = 60;
        c.ElapsedSeconds = 59.999;
        Assert.Equal(StopReason.None, Eval(c));
        c.ElapsedSeconds = 60;
        Assert.Equal(StopReason.TimeLimit, Eval(c));
    }

    [Fact]
    public void A_disabled_time_limit_never_stops()
    {
        var c = Quiet();
        c.TimeLimitSeconds = 1;
        c.ElapsedSeconds = 9999;
        Assert.Equal(StopReason.None, Eval(c));
    }

    // ---- corners ----

    [Theory]
    [InlineData(0, 0, StopReason.CornerTopLeft)]
    [InlineData(49, 49, StopReason.CornerTopLeft)]
    [InlineData(1919, 0, StopReason.CornerTopRight)]
    [InlineData(1870, 49, StopReason.CornerTopRight)]
    [InlineData(0, 1079, StopReason.CornerBottomLeft)]
    [InlineData(49, 1030, StopReason.CornerBottomLeft)]
    [InlineData(1919, 1079, StopReason.CornerBottomRight)]
    [InlineData(1870, 1030, StopReason.CornerBottomRight)]
    public void Each_corner_square_of_the_configured_size_stops(int x, int y, StopReason expected)
    {
        var c = Quiet();
        c.Cursor = new PixelPoint(x, y);
        Assert.Equal(expected, Eval(c));
    }

    [Fact]
    public void Just_outside_the_corner_square_is_an_edge_not_a_corner()
    {
        var c = Quiet();
        c.CornerSizePx = 20;
        c.Cursor = new PixelPoint(30, 10);   // 30 px from the left (outside the 20 px corner), 10 px from the top (inside the 40 px edge)
        Assert.Equal(StopReason.EdgeTop, Eval(c));
    }

    [Fact]
    public void A_disabled_corner_check_leaves_the_corner_to_the_edge_check()
    {
        var c = Quiet();
        c.CornerStopEnabled = false;
        c.Cursor = new PixelPoint(0, 0);
        Assert.Equal(StopReason.EdgeTop, Eval(c));
        c.EdgeStopEnabled = false;
        Assert.Equal(StopReason.None, Eval(c));
    }

    // ---- edges ----

    [Theory]
    [InlineData(960, 0, StopReason.EdgeTop)]
    [InlineData(960, 39, StopReason.EdgeTop)]
    [InlineData(960, 1079, StopReason.EdgeBottom)]
    [InlineData(960, 1040, StopReason.EdgeBottom)]
    [InlineData(0, 540, StopReason.EdgeLeft)]
    [InlineData(39, 540, StopReason.EdgeLeft)]
    [InlineData(1919, 540, StopReason.EdgeRight)]
    [InlineData(1880, 540, StopReason.EdgeRight)]
    public void Each_edge_band_of_the_configured_size_stops(int x, int y, StopReason expected)
    {
        var c = Quiet();
        c.Cursor = new PixelPoint(x, y);
        Assert.Equal(expected, Eval(c));
    }

    [Theory]
    [InlineData(960, 40)]
    [InlineData(960, 1039)]
    [InlineData(40, 540)]
    [InlineData(1879, 540)]
    public void One_pixel_inside_the_band_is_safe(int x, int y)
    {
        var c = Quiet();
        c.Cursor = new PixelPoint(x, y);
        Assert.Equal(StopReason.None, Eval(c));
    }

    [Fact]
    public void The_sizes_are_per_check_settings()
    {
        var c = Quiet();
        c.EdgeSizePx = 200;
        c.Cursor = new PixelPoint(960, 150);
        Assert.Equal(StopReason.EdgeTop, Eval(c));
        c.EdgeSizePx = 100;
        Assert.Equal(StopReason.None, Eval(c));
    }

    // ---- multiple monitors, negative coordinates ----

    [Fact]
    public void The_corner_of_a_monitor_left_of_the_primary_stops_with_negative_coordinates()
    {
        var c = Quiet();
        c.Monitors = new[] { Main, Left };
        c.Cursor = new PixelPoint(-1920, 0);
        Assert.Equal(StopReason.CornerTopLeft, Eval(c));
        c.Cursor = new PixelPoint(-1920 + 49, 1079 - 49);
        Assert.Equal(StopReason.CornerBottomLeft, Eval(c));
        c.Cursor = new PixelPoint(-1, 0);
        Assert.Equal(StopReason.CornerTopRight, Eval(c));
    }

    [Fact]
    public void The_edges_of_a_monitor_above_the_primary_are_measured_from_its_own_rect()
    {
        var c = Quiet();
        c.Monitors = new[] { Main, Above };
        c.Cursor = new PixelPoint(960, -1080);
        Assert.Equal(StopReason.EdgeTop, Eval(c));
        c.Cursor = new PixelPoint(960, -1);
        Assert.Equal(StopReason.EdgeBottom, Eval(c));
        c.Cursor = new PixelPoint(0, -540);
        Assert.Equal(StopReason.EdgeLeft, Eval(c));
    }

    [Fact]
    public void The_middle_of_a_negative_monitor_is_safe()
    {
        var c = Quiet();
        c.Monitors = new[] { Main, Left, Above };
        c.Cursor = new PixelPoint(-960, 540);
        Assert.Equal(StopReason.None, Eval(c));
        c.Cursor = new PixelPoint(960, -540);
        Assert.Equal(StopReason.None, Eval(c));
    }

    [Fact]
    public void Each_monitor_has_its_own_edges_including_where_it_touches_a_neighbour()
    {
        var c = Quiet();
        c.Monitors = new[] { Main, Left };
        // x = 0 is the primary's left edge, x = -1 the left monitor's right edge: both count (checked per monitor).
        c.Cursor = new PixelPoint(0, 540);
        Assert.Equal(StopReason.EdgeLeft, Eval(c));
        c.Cursor = new PixelPoint(-1, 540);
        Assert.Equal(StopReason.EdgeRight, Eval(c));
    }

    [Fact]
    public void A_monitor_with_a_non_standard_size_uses_its_own_corners()
    {
        var c = Quiet();
        c.Monitors = new[] { new PixelRect(1920, 200, 1280, 720) };
        c.Cursor = new PixelPoint(1920 + 1279, 200 + 719);
        Assert.Equal(StopReason.CornerBottomRight, Eval(c));
        c.Cursor = new PixelPoint(1920 + 640, 200 + 360);
        Assert.Equal(StopReason.None, Eval(c));
    }

    [Fact]
    public void A_cursor_on_no_monitor_triggers_no_corner_or_edge()
    {
        var c = Quiet();
        c.Cursor = new PixelPoint(5000, 5000);
        Assert.Equal(StopReason.None, Eval(c));
        c.Monitors = Array.Empty<PixelRect>();
        c.Cursor = new PixelPoint(0, 0);
        Assert.Equal(StopReason.None, Eval(c));
    }

    // ---- zones ----

    private static ClickerZone Zone(int x, int y, int w, int h, ZoneAction a = ZoneAction.Stop, bool enabled = true)
        => new() { Id = Guid.NewGuid().ToString("N"), X = x, Y = y, W = w, H = h, Action = a, Enabled = enabled };

    [Fact]
    public void A_stop_zone_under_the_cursor_stops()
    {
        var c = Quiet();
        c.ZonesEnabled = true;
        c.Zones = new[] { Zone(900, 500, 100, 100) };
        Assert.Equal(StopReason.StopZone, Eval(c));
        c.Cursor = new PixelPoint(1000, 540);   // the pixel past the right edge
        Assert.Equal(StopReason.None, Eval(c));
    }

    [Fact]
    public void Only_enabled_stop_zones_count_and_only_while_zones_are_on()
    {
        var c = Quiet();
        c.Zones = new[]
        {
            Zone(900, 500, 100, 100, ZoneAction.Pause),
            Zone(900, 500, 100, 100, ZoneAction.Start),
            Zone(900, 500, 100, 100, ZoneAction.Stop, enabled: false)
        };
        c.ZonesEnabled = true;
        Assert.Equal(StopReason.None, Eval(c));   // pause/start never stop, the disabled stop zone is dim
        c.Zones = new[] { Zone(900, 500, 100, 100) };
        c.ZonesEnabled = false;
        Assert.Equal(StopReason.None, Eval(c));
    }

    [Fact]
    public void A_stop_zone_on_a_negative_monitor_works()
    {
        var c = Quiet();
        c.Monitors = new[] { Main, Left };
        c.ZonesEnabled = true;
        c.Zones = new[] { Zone(-1500, 300, 200, 200) };
        c.Cursor = new PixelPoint(-1400, 400);
        Assert.Equal(StopReason.StopZone, Eval(c));
    }

    [Fact]
    public void A_stop_zone_beats_a_pause_zone_it_overlaps()
    {
        var c = Quiet();
        c.ZonesEnabled = true;
        c.Zones = new[] { Zone(900, 500, 100, 100, ZoneAction.Pause), Zone(940, 520, 40, 40) };
        Assert.Equal(StopReason.StopZone, Eval(c));
    }

    // ---- task switcher ----

    [Theory]
    [InlineData("TaskSwitcherWnd")]
    [InlineData("TaskViewWindow")]
    [InlineData("WindowsSwitchWindow")]
    [InlineData("MultitaskingViewFrame")]
    public void The_task_switcher_window_classes_stop(string className)
    {
        var c = Quiet();
        c.ForegroundClass = className;
        c.ForegroundExe = "explorer.exe";
        Assert.Equal(StopReason.TaskSwitcher, Eval(c));
    }

    [Fact]
    public void Windows_11_task_switching_is_an_explorer_xaml_island()
    {
        var c = Quiet();
        c.ForegroundClass = "XamlExplorerHostIslandWindow";
        c.ForegroundExe = @"C:\Windows\explorer.exe";
        Assert.Equal(StopReason.TaskSwitcher, Eval(c));
        c.ForegroundExe = "SomeOtherApp.exe";
        Assert.Equal(StopReason.None, Eval(c));
        c.ForegroundExe = null;
        Assert.Equal(StopReason.None, Eval(c));
    }

    [Fact]
    public void The_task_switcher_class_check_can_be_turned_off_and_ignores_other_windows()
    {
        var c = Quiet();
        c.ForegroundClass = "TaskSwitcherWnd";
        c.TaskSwitcherStopEnabled = false;
        Assert.Equal(StopReason.None, Eval(c));

        c.TaskSwitcherStopEnabled = true;
        c.ForegroundClass = "Chrome_WidgetWin_1";
        Assert.Equal(StopReason.None, Eval(c));
        c.ForegroundClass = null;
        Assert.Equal(StopReason.None, Eval(c));
    }

    [Fact]
    public void IsTaskSwitcher_ignores_case()
    {
        Assert.True(StopRules.IsTaskSwitcher("taskswitcherwnd", null));
        Assert.True(StopRules.IsTaskSwitcher("XAMLEXPLORERHOSTISLANDWINDOW", "Explorer.EXE"));
        Assert.False(StopRules.IsTaskSwitcher("", "explorer.exe"));
    }

    // ---- whitelist ----

    private static List<BlacklistEntry> List(params string[] exes)
        => exes.Select(e => new BlacklistEntry { ExeName = e, DisplayName = e }).ToList();

    [Fact]
    public void A_foreground_program_off_the_whitelist_stops()
    {
        var c = Quiet();
        c.WhitelistEnabled = true;
        c.Whitelist = List("game.exe");
        c.ForegroundExe = "notepad.exe";
        Assert.Equal(StopReason.NotOnWhitelist, Eval(c));
    }

    [Theory]
    [InlineData("game.exe")]
    [InlineData("GAME.EXE")]
    [InlineData(@"D:\Games\Game.exe")]
    public void A_whitelisted_program_may_click_whatever_its_spelling(string foreground)
    {
        var c = Quiet();
        c.WhitelistEnabled = true;
        c.Whitelist = List("Game.exe");
        c.ForegroundExe = foreground;
        Assert.Equal(StopReason.None, Eval(c));
    }

    [Fact]
    public void An_enabled_empty_whitelist_or_an_unknown_foreground_program_stops()
    {
        var c = Quiet();
        c.WhitelistEnabled = true;
        c.Whitelist = new List<BlacklistEntry>();
        Assert.Equal(StopReason.NotOnWhitelist, Eval(c));

        c.Whitelist = List("game.exe");
        c.ForegroundExe = null;
        Assert.Equal(StopReason.NotOnWhitelist, Eval(c));
    }

    [Fact]
    public void A_disabled_whitelist_allows_every_program()
    {
        var c = Quiet();
        c.Whitelist = List("game.exe");
        c.ForegroundExe = "notepad.exe";
        Assert.Equal(StopReason.None, Eval(c));
    }

    [Fact]
    public void The_whitelist_answer_follows_a_change_of_foreground_program_and_of_list()
    {
        var c = Quiet();
        c.WhitelistEnabled = true;
        c.Whitelist = List("game.exe");
        c.ForegroundExe = "game.exe";
        Assert.Equal(StopReason.None, Eval(c));
        c.ForegroundExe = "notepad.exe";
        Assert.Equal(StopReason.NotOnWhitelist, Eval(c));
        c.Whitelist = List("notepad.exe");
        Assert.Equal(StopReason.None, Eval(c));
    }

    // ---- lock ----

    [Fact]
    public void A_lock_from_the_clipper_stops()
    {
        var c = Quiet();
        c.Locked = true;
        Assert.Equal(StopReason.Locked, Eval(c));
    }

    // ---- order ----

    [Fact]
    public void The_checks_run_in_the_documented_order()
    {
        // Everything triggered at once; peel one reason off at a time and the next one in line shows.
        var c = Quiet();
        c.Monitors = new[] { Main };
        c.Cursor = new PixelPoint(0, 0);                // corner (and edge) and inside the stop zone
        c.ZonesEnabled = true;
        c.Zones = new[] { Zone(0, 0, 100, 100) };
        c.ClickLimitEnabled = true; c.ClickLimit = 10; c.Clicks = 10;
        c.TimeLimitEnabled = true; c.TimeLimitSeconds = 1; c.ElapsedSeconds = 5;
        c.ForegroundClass = "TaskSwitcherWnd";
        c.WhitelistEnabled = true; c.Whitelist = List("game.exe"); c.ForegroundExe = "notepad.exe";
        c.Locked = true;

        var seen = new List<StopReason>();
        seen.Add(Eval(c)); c.ClickLimitEnabled = false;
        seen.Add(Eval(c)); c.ZonesEnabled = false;
        seen.Add(Eval(c)); c.CornerStopEnabled = false;
        seen.Add(Eval(c)); c.EdgeStopEnabled = false;
        seen.Add(Eval(c)); c.TaskSwitcherStopEnabled = false;
        seen.Add(Eval(c)); c.WhitelistEnabled = false;
        seen.Add(Eval(c)); c.Locked = false;
        seen.Add(Eval(c)); c.TimeLimitEnabled = false;
        seen.Add(Eval(c));

        Assert.Equal(new[]
        {
            StopReason.ClickLimit, StopReason.StopZone, StopReason.CornerTopLeft, StopReason.EdgeTop, StopReason.TaskSwitcher,
            StopReason.NotOnWhitelist, StopReason.Locked, StopReason.TimeLimit, StopReason.None
        }, seen);
    }

    // ---- from settings ----

    [Fact]
    public void The_context_takes_its_configuration_from_the_settings()
    {
        var s = new ClickerSettings
        {
            ClickLimitEnabled = true, ClickLimit = 250,
            TimeLimitEnabled = true, TimeLimit = 2, TimeLimitUnit = TimeLimitUnit.Minutes,
            CornerStopEnabled = false, CornerSizePx = 33,
            EdgeStopEnabled = true, EdgeSizePx = 22,
            TaskSwitcherStopEnabled = false,
            ZonesEnabled = true, Zones = new List<ClickerZone> { Zone(1, 2, 3, 4) },
            WhitelistEnabled = true, Whitelist = List("game.exe")
        };
        var c = StopContext.From(s);

        Assert.True(c.ClickLimitEnabled);
        Assert.Equal(250, c.ClickLimit);
        Assert.True(c.TimeLimitEnabled);
        Assert.Equal(120, c.TimeLimitSeconds, 9);
        Assert.False(c.CornerStopEnabled);
        Assert.Equal(33, c.CornerSizePx);
        Assert.True(c.EdgeStopEnabled);
        Assert.Equal(22, c.EdgeSizePx);
        Assert.False(c.TaskSwitcherStopEnabled);
        Assert.True(c.ZonesEnabled);
        Assert.Single(c.Zones);
        Assert.True(c.WhitelistEnabled);
        Assert.Single(c.Whitelist);
    }

    // ---- reason texts ----

    [Fact]
    public void Every_stop_reason_has_a_text()
    {
        foreach (var r in Enum.GetValues<StopReason>().Where(r => r != StopReason.None))
            Assert.False(string.IsNullOrWhiteSpace(StopReasonText.Describe(r)), r.ToString());
    }

    [Fact]
    public void A_detail_is_appended_in_parentheses()
        => Assert.EndsWith("(1000)", StopReasonText.Describe(StopReason.ClickLimit, "1000"));

    [Fact]
    public void The_activation_reasons_map_to_stop_reasons()
    {
        Assert.Equal(StopReason.Hotkey, StopReasonText.From(ActivationReason.Hotkey));
        Assert.Equal(StopReason.HoldReleased, StopReasonText.From(ActivationReason.HoldReleased));
        Assert.Equal(StopReason.MasterSwitch, StopReasonText.From(ActivationReason.MasterSwitch));
        Assert.Equal(StopReason.Locked, StopReasonText.From(ActivationReason.Locked));
        Assert.Equal(StopReason.PickerOpened, StopReasonText.From(ActivationReason.PickerOpened));
        Assert.Equal(StopReason.None, StopReasonText.From(ActivationReason.None));
    }
}
