using System;
using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.ClickerHost;
using WKI_Clipper.Models;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>The click loop on virtual time (design section 4): rates, batching, duty holds, limits, failsafes, pause zones, hold modes, double click, click points.</summary>
public sealed class ClickEngineTests
{
    private static double[] Times(IEnumerable<InputEvent> events) => events.Select(e => e.T).ToArray();

    // ---- rates and batching ----

    [Fact]
    public void Twenty_five_per_second_sends_one_click_every_40_ms_and_stops_at_the_time_limit()
    {
        var rig = new EngineRig();

        var summary = rig.RunFor(1);

        var bursts = rig.Input.Of("burst").ToList();
        Assert.Equal(25, bursts.Count);
        Assert.All(bursts, b => Assert.Equal(1, b.A));
        Assert.Equal(Enumerable.Range(0, 25).Select(i => i * 40.0), Times(bursts));
        Assert.Equal(StopReason.TimeLimit, summary.Reason);
        Assert.Equal(25, summary.Clicks);
        Assert.Equal("1 s", summary.Detail);
        Assert.Equal(1.0, summary.Seconds, 3);
    }

    [Fact]
    public void From_50_per_second_two_cycles_go_into_one_batch()
    {
        var rig = new EngineRig(s => s.ClickSpeed = 100);

        var summary = rig.RunFor(1);

        var bursts = rig.Input.Of("burst").ToList();
        Assert.Equal(50, bursts.Count);
        Assert.All(bursts, b => Assert.Equal(2, b.A));
        Assert.Equal(Enumerable.Range(0, 50).Select(i => i * 20.0), Times(bursts));
        Assert.Equal(100, summary.Clicks);
    }

    [Fact]
    public void Above_500_per_second_three_cycles_go_into_one_batch()
    {
        var rig = new EngineRig(s =>
        {
            s.ExtendedSpeedLimit = true;
            s.ClickSpeed = 600;
        });

        var summary = rig.RunFor(1);

        Assert.All(rig.Input.Of("burst"), b => Assert.Equal(3, b.A));
        Assert.InRange(summary.Clicks, 600, 603);
    }

    [Fact]
    public void The_set_rate_is_kept_over_a_long_run_with_randomized_batches()
    {
        var rig = new EngineRig(s =>
        {
            s.SpeedRandomizationEnabled = true;
            s.SpeedRandomizationPercent = 35;
            s.ClickSpeed = 50;
        }, seed: 7);

        var summary = rig.RunFor(20);

        Assert.InRange(summary.Clicks, 950, 1050);   // 50 per second for 20 s = 1000
        var gaps = rig.Input.Of("burst").Zip(rig.Input.Of("burst").Skip(1), (a, b) => b.T - a.T).ToList();
        Assert.True(gaps.Max() - gaps.Min() > 5, "the batch durations should vary");
    }

    [Fact]
    public void Rate_in_minutes_and_duration_mode_set_the_interval()
    {
        var perMinute = new EngineRig(s =>
        {
            s.ClickUnit = RateUnit.Minute;
            s.ClickSpeed = 30;   // every 2 s
        });
        perMinute.RunFor(5);
        Assert.Equal(new[] { 0.0, 2000, 4000 }, Times(perMinute.Input.Of("burst")));

        var duration = new EngineRig(s =>
        {
            s.RateInputMode = RateInputMode.Duration;
            s.DurationMs = 250;
        });
        duration.RunFor(1);
        Assert.Equal(new[] { 0.0, 250, 500, 750 }, Times(duration.Input.Of("burst")));
    }

    // ---- duty hold ----

    [Fact]
    public void A_duty_cycle_holds_the_button_for_its_share_of_the_cycle()
    {
        var rig = new EngineRig(s =>
        {
            s.ClickSpeed = 10;
            s.DutyCycleEnabled = true;
            s.DutyCyclePercent = 45;
        });

        rig.RunFor(0.25);

        var presses = rig.Input.Events.Where(e => e.Kind is "down" or "up").ToList();
        Assert.Equal(new[] { ("down", 0.0), ("up", 45.0), ("down", 100.0), ("up", 145.0), ("down", 200.0), ("up", 245.0) },
            presses.Select(e => (e.Kind, e.T)).ToArray());
    }

    [Fact]
    public void A_hold_below_a_tenth_of_a_millisecond_goes_out_as_one_burst()
    {
        var rig = new EngineRig(s =>
        {
            s.ExtendedSpeedLimit = true;
            s.ClickSpeed = 600;
            s.DutyCycleEnabled = true;   // capped to 1 % above 500 per second: 0.017 ms
            s.DutyCyclePercent = 45;
        });

        rig.RunFor(0.1);

        Assert.Empty(rig.Input.Of("down"));
        Assert.NotEmpty(rig.Input.Of("burst"));
    }

    [Fact]
    public void The_chosen_mouse_button_is_used()
    {
        var rig = new EngineRig(s => s.MouseButton = ClickerMouseButton.Right);
        rig.RunFor(0.1);
        Assert.All(rig.Input.Of("burst"), b => Assert.Equal((int)ClickerMouseButton.Right, b.B));
    }

    // ---- limits and failsafes ----

    [Fact]
    public void The_click_limit_ends_the_run_right_after_the_last_click()
    {
        var rig = new EngineRig(s =>
        {
            s.ClickSpeed = 100;
            s.ClickLimitEnabled = true;
            s.ClickLimit = 5;
        });

        var summary = rig.Run();

        Assert.Equal(new[] { 2, 2, 1 }, rig.Input.Of("burst").Select(b => b.A).ToArray());   // the last batch is cut to fit
        Assert.Equal(5, summary.Clicks);
        Assert.Equal(StopReason.ClickLimit, summary.Reason);
        Assert.Equal("5", summary.Detail);
        Assert.True(summary.Seconds < 0.05, "no waiting for the next batch after the limit");
    }

    [Fact]
    public void A_cursor_in_a_corner_stops_before_the_first_click()
    {
        var rig = new EngineRig();
        rig.Env.Cursor = new PixelPoint(0, 0);

        var summary = rig.Run();

        Assert.Equal(StopReason.CornerTopLeft, summary.Reason);
        Assert.Equal(0, summary.Clicks);
        Assert.Empty(rig.Input.Of("burst"));
    }

    [Fact]
    public void An_edge_stops_the_run_and_a_negative_monitor_has_its_own_edges()
    {
        var rig = new EngineRig();
        rig.Env.MonitorList = new() { new PixelRect(-1920, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1080) };
        rig.Env.Cursor = new PixelPoint(-1000, 5);
        Assert.Equal(StopReason.EdgeTop, rig.Run().Reason);
    }

    [Fact]
    public void Moving_into_a_corner_during_a_slow_run_stops_it_within_a_slice()
    {
        var rig = new EngineRig(s =>
        {
            s.ClickUnit = RateUnit.Minute;
            s.ClickSpeed = 1;   // one click a minute
        });
        rig.Waiter.At(1234, () => rig.Env.Cursor = new PixelPoint(1919, 1079));

        var summary = rig.Run();

        Assert.Equal(StopReason.CornerBottomRight, summary.Reason);
        Assert.InRange(rig.Clock.Now, 1234, 1245);
    }

    [Fact]
    public void The_task_switcher_in_front_stops_the_run()
    {
        var rig = new EngineRig();
        rig.Env.Fg = new ForegroundInfo(new IntPtr(2), 5, "TaskSwitcherWnd", @"C:\Windows\explorer.exe", false);
        Assert.Equal(StopReason.TaskSwitcher, rig.Run().Reason);
    }

    [Fact]
    public void The_whitelist_lets_only_listed_programs_click()
    {
        var blocked = new EngineRig(s =>
        {
            s.WhitelistEnabled = true;
            s.Whitelist = new() { new BlacklistEntry { ExeName = "other.exe" } };
        });
        var summary = blocked.Run();
        Assert.Equal(StopReason.NotOnWhitelist, summary.Reason);
        Assert.Equal("game.exe", summary.Detail);
        Assert.Empty(blocked.Input.Of("burst"));

        var allowed = new EngineRig(s =>
        {
            s.WhitelistEnabled = true;
            s.Whitelist = new() { new BlacklistEntry { ExeName = "GAME.exe" } };
        });
        Assert.Equal(StopReason.TimeLimit, allowed.RunFor(0.1).Reason);
    }

    [Fact]
    public void A_lock_during_a_run_stops_it_and_a_locked_engine_refuses_to_start()
    {
        var rig = new EngineRig();
        rig.Waiter.At(100, () => rig.Engine.SetLocked(true));
        var summary = rig.Run();
        Assert.Equal(StopReason.Locked, summary.Reason);
        Assert.InRange(rig.Clock.Now, 100, 110);

        rig.Engine.RunSync(new EngineStart(false), out var status);
        Assert.Equal(EngineStartStatus.Locked, status);
    }

    [Fact]
    public void A_start_is_refused_without_an_output_key_or_with_an_empty_whitelist_or_while_running()
    {
        var keyless = new EngineRig(s => s.InputType = ClickerInput.Keyboard);   // KeyboardVk 0
        keyless.Engine.RunSync(new EngineStart(false), out var status);
        Assert.Equal(EngineStartStatus.NoOutputKey, status);

        var empty = new EngineRig(s => s.WhitelistEnabled = true);
        empty.Engine.RunSync(new EngineStart(false), out status);
        Assert.Equal(EngineStartStatus.EmptyWhitelist, status);

        var rig = new EngineRig();
        EngineStartStatus? inner = null;
        rig.Waiter.At(50, () => inner = rig.Engine.Start(new EngineStart(false)));
        rig.RunFor(0.1);
        Assert.Equal(EngineStartStatus.Busy, inner);
    }

    private const int KeyY = 0x59, KeyA = 0x41;

    private static EngineRig KeyboardRig(int vk, ClickerHotkey? hotkey = null, ClickerHotkey? master = null, KeyCase keyCase = KeyCase.Lower)
        => new(s =>
        {
            s.InputType = ClickerInput.Keyboard;
            s.KeyboardVk = vk;
            s.KeyCase = keyCase;
            if (hotkey is not null) s.Hotkey = hotkey;
            s.MasterHotkey = master;
            s.TimeLimitEnabled = true;   // a start that is allowed must end by itself
            s.TimeLimit = 0.1;
            s.TimeLimitUnit = TimeLimitUnit.Seconds;
        });

    // Free-hotkeys design, section 3: an output key that is part of a hotkey is a warning in the UI, never a refused start.

    [Fact]
    public void An_output_key_that_is_the_hotkey_still_starts()
    {
        var rig = KeyboardRig(KeyY, hotkey: new ClickerHotkey { Keys = { KeyY } });

        var summary = rig.Engine.RunSync(new EngineStart(false), out var status);

        Assert.Equal(EngineStartStatus.Started, status);
        Assert.NotNull(summary);
        Assert.NotEmpty(rig.Input.Events);
    }

    [Fact]
    public void An_output_key_that_is_the_master_hotkey_still_starts()
    {
        var rig = KeyboardRig(KeyY, master: new ClickerHotkey { Keys = { KeyY } });

        rig.Engine.RunSync(new EngineStart(false), out var status);

        Assert.Equal(EngineStartStatus.Started, status);
    }

    [Fact]
    public void Shift_plus_the_upper_case_output_letter_still_starts()
    {
        var shiftA = new ClickerHotkey { Modifiers = ClickerModifiers.Shift, Keys = { KeyA } };

        var upper = KeyboardRig(KeyA, hotkey: shiftA, keyCase: KeyCase.Upper);
        upper.Engine.RunSync(new EngineStart(false), out var status);
        Assert.Equal(EngineStartStatus.Started, status);
    }

    [Fact]
    public void A_hotkey_with_a_real_modifier_is_no_conflict_for_the_output_key()
    {
        var rig = KeyboardRig(KeyY, hotkey: new ClickerHotkey { Modifiers = ClickerModifiers.Ctrl, Keys = { KeyY } });

        rig.Engine.RunSync(new EngineStart(false), out var status);

        Assert.Equal(EngineStartStatus.Started, status);
    }

    [Fact]
    public void A_mouse_output_never_conflicts_with_a_single_key_hotkey()
    {
        var rig = new EngineRig(s =>
        {
            s.Hotkey = new ClickerHotkey { Keys = { KeyY } };
            s.TimeLimitEnabled = true;
            s.TimeLimit = 0.1;
        });

        rig.Engine.RunSync(new EngineStart(false), out var status);

        Assert.Equal(EngineStartStatus.Started, status);
    }

    [Fact]
    public void A_stop_request_wakes_the_wait_and_everything_pressed_is_released()
    {
        var rig = new EngineRig(s => s.ClickSpeed = 10);
        rig.Waiter.At(105, () => rig.Engine.Stop(StopReason.Hotkey));

        var summary = rig.Run();

        Assert.Equal(StopReason.Hotkey, summary.Reason);
        Assert.Equal(105, rig.Clock.Now);
        Assert.Equal(1, rig.Input.ReleaseAllCalls);
        Assert.False(rig.Engine.IsRunning);
    }

    [Fact]
    public void The_first_stop_reason_wins()
    {
        var rig = new EngineRig();
        rig.Waiter.At(50, () =>
        {
            rig.Engine.Stop(StopReason.MasterSwitch);
            rig.Engine.Stop(StopReason.Hotkey);
        });
        Assert.Equal(StopReason.MasterSwitch, rig.Run().Reason);
    }

    [Fact]
    public void A_stop_while_idle_does_nothing_to_the_next_run()
    {
        var rig = new EngineRig();
        rig.Engine.Stop(StopReason.Hotkey);
        Assert.Equal(StopReason.TimeLimit, rig.RunFor(0.1).Reason);
    }

    [Fact]
    public void Every_run_brackets_the_waiter_session_even_when_it_fails()
    {
        var ok = new EngineRig();
        ok.RunFor(0.1);
        Assert.Equal((1, 1), (ok.Waiter.SessionsBegun, ok.Waiter.SessionsEnded));

        var failing = new EngineRig();
        failing.Input.ThrowOnClick = new InvalidOperationException("boom");
        failing.Run();
        Assert.Equal((1, 1), (failing.Waiter.SessionsBegun, failing.Waiter.SessionsEnded));
    }

    [Fact]
    public void A_failing_sink_ends_the_run_cleanly_and_still_releases()
    {
        var rig = new EngineRig();
        rig.Input.ThrowOnClick = new InvalidOperationException("boom");

        var summary = rig.Run();

        Assert.Equal(StopReason.Manual, summary.Reason);
        Assert.Equal(1, rig.Input.ReleaseAllCalls);
        Assert.False(rig.Engine.IsRunning);
    }

    [Fact]
    public void After_a_long_stall_the_missed_clicks_are_not_fired_in_a_burst()
    {
        var rig = new EngineRig(s => s.ClickSpeed = 10);
        rig.Waiter.At(150, () => rig.Clock.Now += 5000);   // the PC slept

        rig.RunFor(10);

        Assert.Equal(1, rig.Input.Of("burst").Count(b => b.T >= 5150 && b.T < 5250));
    }

    [Fact]
    public void A_stall_below_a_second_is_caught_up_so_the_long_run_mean_holds()
    {
        var rig = new EngineRig();   // 25 per second
        rig.Waiter.At(100, () => rig.Clock.Now += 300);   // a GC or scheduler stall

        var summary = rig.RunFor(2);

        Assert.Equal(50, summary.Clicks);   // 2 s x 25 per second: nothing was lost
    }

    [Fact]
    public void After_a_suspend_sized_gap_the_schedule_restarts_from_now_without_a_burst()
    {
        var rig = new EngineRig();
        rig.Waiter.At(100, () => rig.Clock.Now += 5000);

        rig.RunFor(10);

        var after = Times(rig.Input.Of("burst")).Where(t => t >= 5100).ToArray();
        Assert.NotEmpty(after);
        for (int i = 1; i < after.Length; i++)
            Assert.True(after[i] - after[i - 1] >= 39.9, "no catch-up burst after the gap: " + after[i - 1] + " -> " + after[i]);
    }

    // ---- pause zones ----

    [Fact]
    public void A_pause_zone_holds_the_clicker_and_the_schedule_goes_on_afterwards()
    {
        var rig = new EngineRig(s =>
        {
            s.ClickSpeed = 10;
            s.ZonesEnabled = true;
            s.Zones = new() { new ClickerZone { Id = "p", X = 1000, Y = 500, W = 200, H = 100, Action = ZoneAction.Pause } };
        });
        rig.Waiter.At(150, () => rig.Env.Cursor = new PixelPoint(1050, 550));
        rig.Waiter.At(450, () => rig.Env.Cursor = new PixelPoint(960, 540));

        rig.RunFor(0.65);

        // 0 and 100 before the pause; the 300 ms spent inside do not count against the schedule: the click due at 200 comes at 500.
        Assert.Equal(new[] { 0.0, 100, 500, 600 }, Times(rig.Input.Of("burst")));
        Assert.True(rig.Input.ReleaseAllCalls >= 2, "the pause lets go of everything pressed");
        Assert.Contains(rig.Snapshots, s => s.Paused);
    }

    [Fact]
    public void A_button_held_by_a_hold_mode_is_released_by_a_pause_zone_and_pressed_again_after_it()
    {
        var rig = new EngineRig(s =>
        {
            s.DutyMode = DutyMode.Hold;
            s.ZonesEnabled = true;
            s.Zones = new() { new ClickerZone { Id = "p", X = 1000, Y = 500, W = 200, H = 100, Action = ZoneAction.Pause } };
        });
        rig.Waiter.At(100, () => rig.Env.Cursor = new PixelPoint(1050, 550));
        rig.Waiter.At(300, () => rig.Env.Cursor = new PixelPoint(960, 540));

        rig.RunFor(0.5);

        Assert.Equal(2, rig.Input.Of("down").Count());
        Assert.Contains(rig.Input.Of("up"), u => u.T is >= 100 and <= 110);
        Assert.Equal(0, rig.Input.HeldCount);
    }

    // ---- hold modes ----

    [Fact]
    public void Duty_mode_hold_presses_once_and_releases_at_the_end()
    {
        var rig = new EngineRig(s => s.DutyMode = DutyMode.Hold);

        var summary = rig.RunFor(0.5);

        Assert.Equal(new[] { 0.0 }, Times(rig.Input.Of("down")));
        Assert.Equal(new[] { 500.0 }, Times(rig.Input.Of("up")));
        Assert.Equal(1, summary.Clicks);
        Assert.Equal(StopReason.TimeLimit, summary.Reason);
    }

    [Fact]
    public void A_held_key_repeats_at_the_windows_delay_and_rate()
    {
        var rig = new EngineRig(s =>
        {
            s.InputType = ClickerInput.Keyboard;
            s.KeyboardVk = 0x41;
            s.DutyMode = DutyMode.Hold;
        });   // delay index 0 = 250 ms, speed 31 = 30 per second

        var summary = rig.RunFor(1);

        Assert.Equal(new[] { 0.0 }, Times(rig.Input.Of("kdown")));
        var repeats = rig.Input.Of("krepeat").ToList();
        Assert.Equal(23, repeats.Count);   // 250, 283.3 ... 983.3
        Assert.Equal(250, repeats[0].T, 3);
        Assert.Equal(1000.0 / 30, repeats[1].T - repeats[0].T, 3);
        Assert.Equal(24, summary.Clicks);   // the press and every repeat count
        Assert.InRange(Assert.Single(rig.Input.Of("kup")).T, 1000, 1010);   // the time limit is noticed at the next 10 ms slice
    }

    [Fact]
    public void A_keyboard_at_100_percent_duty_holds_the_key_too()
    {
        var rig = new EngineRig(s =>
        {
            s.InputType = ClickerInput.Keyboard;
            s.KeyboardVk = 0x41;
            s.DutyCycleEnabled = true;
            s.DutyCyclePercent = 100;
        });
        rig.RunFor(0.5);
        Assert.Single(rig.Input.Of("kdown"));
        Assert.NotEmpty(rig.Input.Of("krepeat"));
        Assert.Empty(rig.Input.Of("kburst"));
    }

    [Fact]
    public void A_keyboard_tap_honours_the_duty_hold()
    {
        var rig = new EngineRig(s =>
        {
            s.InputType = ClickerInput.Keyboard;
            s.KeyboardVk = 0x42;
            s.ClickSpeed = 10;
            s.DutyCycleEnabled = true;
            s.DutyCyclePercent = 45;
        });
        rig.RunFor(0.15);
        Assert.Equal(new[] { 0.0, 100 }, Times(rig.Input.Of("kdown")));
        Assert.Equal(new[] { 45.0, 145 }, Times(rig.Input.Of("kup")));
    }

    [Theory]
    [InlineData(KeyCase.Upper, false, true)]
    [InlineData(KeyCase.Upper, true, false)]
    [InlineData(KeyCase.Lower, true, true)]
    [InlineData(KeyCase.Lower, false, false)]
    public void Shift_goes_around_a_letter_when_capslock_disagrees_with_the_wanted_case(KeyCase wanted, bool caps, bool shift)
    {
        var rig = new EngineRig(s =>
        {
            s.InputType = ClickerInput.Keyboard;
            s.KeyboardVk = 0x41;
            s.KeyCase = wanted;
        });
        rig.Env.Caps = caps;

        rig.RunFor(0.05);

        Assert.Equal(shift ? 1 : 0, rig.Input.Of("kburst").First().B);
    }

    [Fact]
    public void Other_keys_never_get_a_shift()
    {
        var rig = new EngineRig(s =>
        {
            s.InputType = ClickerInput.Keyboard;
            s.KeyboardVk = 0x31;
            s.KeyCase = KeyCase.Upper;
        });
        rig.RunFor(0.05);
        Assert.Equal(0, rig.Input.Of("kburst").First().B);
    }

    // ---- double click ----

    [Fact]
    public void A_double_click_is_two_presses_inside_the_double_click_window_and_counts_two()
    {
        var rig = new EngineRig(s =>
        {
            s.DoubleClick = true;
            s.ClickSpeed = 5;   // a cycle every 200 ms
        });

        var summary = rig.RunFor(1);

        Assert.Equal(new[] { 0.0, 100, 200, 300, 400, 500, 600, 700, 800, 900 }, Times(rig.Input.Of("down")));
        Assert.Equal(10, summary.Clicks);
    }

    [Fact]
    public void Both_presses_of_a_double_click_honour_the_duty_hold()
    {
        var rig = new EngineRig(s =>
        {
            s.DoubleClick = true;
            s.ClickSpeed = 5;
            s.DutyCycleEnabled = true;
            s.DutyCyclePercent = 45;
        });

        rig.RunFor(0.25);

        var e = rig.Input.Events.Where(x => x.Kind is "down" or "up").Take(4).Select(x => (x.Kind, x.T)).ToArray();
        // hold 90 ms; the second press 100 ms after the first, the whole pair inside the 200 ms cycle
        Assert.Equal(new[] { ("down", 0.0), ("up", 90.0), ("down", 100.0), ("up", 190.0) }, e);
    }

    [Fact]
    public void The_click_limit_cuts_the_last_double_click_to_a_single_click()
    {
        var rig = new EngineRig(s =>
        {
            s.DoubleClick = true;
            s.ClickSpeed = 5;
            s.ClickLimitEnabled = true;
            s.ClickLimit = 3;
        });

        var summary = rig.Run();

        Assert.Equal(3, rig.Input.Of("down").Count());
        Assert.Equal(3, summary.Clicks);
        Assert.Equal(StopReason.ClickLimit, summary.Reason);
    }

    // ---- click points ----

    private static ClickPoint P(int x, int y, int clicks = 1, int radius = 0) => new() { X = x, Y = y, Clicks = clicks, Radius = radius };

    [Fact]
    public void Click_points_are_visited_in_order_and_the_run_ends_when_complete()
    {
        var rig = new EngineRig(s =>
        {
            s.ClickSpeed = 100;   // from 50 per second the cursor jumps
            s.ClickPointsEnabled = true;
            s.StopWhenComplete = true;
            s.ClickPoints = new() { P(200, 200, clicks: 2), P(400, 300) };
        });

        var summary = rig.Run();

        Assert.Equal(StopReason.AllPointsDone, summary.Reason);
        Assert.Equal(3, summary.Clicks);
        var seq = rig.Input.Events.Where(e => e.Kind is "move" or "down").Select(e => e.Kind).ToArray();
        Assert.Equal(new[] { "move", "down", "move", "down", "move", "down" }, seq);
        var moves = rig.Input.Of("move").ToList();
        Assert.InRange(moves[0].A, 198, 202);
        Assert.InRange(moves[1].A, 198, 202);
        Assert.InRange(moves[2].A, 398, 402);
        Assert.InRange(moves[2].B, 298, 302);
    }

    [Fact]
    public void Below_50_per_second_the_cursor_glides_to_the_first_click_of_a_point_before_clicking()
    {
        var rig = new EngineRig(s =>
        {
            s.ClickSpeed = 10;
            s.ClickPointsEnabled = true;
            s.ClickPoints = new() { P(600, 700) };
        });

        rig.RunFor(0.05);

        var firstDown = rig.Input.Of("down").First().T;
        var moves = rig.Input.Of("move").TakeWhile(m => m.T <= firstDown).ToList();
        Assert.True(moves.Count >= 5, "a 20-60 ms glide has several steps, got " + moves.Count);
        Assert.InRange(firstDown, 20, 61);
        Assert.InRange(moves[^1].A, 598, 602);
        Assert.InRange(moves[^1].B, 698, 702);
        Assert.Equal(moves.OrderBy(m => m.T).Select(m => m.T), moves.Select(m => m.T));
    }

    [Fact]
    public void The_cursor_entering_a_corner_during_a_glide_ends_the_run_before_the_click()
    {
        var rig = new EngineRig(s =>
        {
            s.ClickSpeed = 1;   // a one second cycle: the glide takes 200 ms
            s.ClickPointsEnabled = true;
            s.ClickPoints = new() { P(600, 700) };
        });
        // The glide's own injected moves put the cursor back on the path, so the user "holds" it in the corner for a while.
        for (int t = 100; t <= 140; t++) rig.Waiter.At(t, () => rig.Env.Cursor = new PixelPoint(1919, 1079));

        var summary = rig.Run();

        Assert.Equal(StopReason.CornerBottomRight, summary.Reason);
        Assert.Empty(rig.Input.Of("down"));
        Assert.InRange(rig.Clock.Now, 100, 125);   // within a slice or two, not after the 200 ms glide
    }

    [Fact]
    public void A_spurious_wake_in_a_glide_finishes_the_move_instead_of_clicking_mid_path()
    {
        var rig = new EngineRig(s =>
        {
            s.ClickSpeed = 1;
            s.ClickPointsEnabled = true;
            s.ClickPoints = new() { P(600, 700) };
        });
        rig.Waiter.At(100, () => rig.Waiter.Wake());   // a wake with no stop request behind it

        rig.RunFor(0.3);

        var firstDown = rig.Input.Of("down").First();
        var lastMove = rig.Input.Of("move").Last(m => m.T <= firstDown.T);
        Assert.Equal((600, 700), (lastMove.A, lastMove.B));
        Assert.InRange(firstDown.T, 100, 125);
    }

    [Fact]
    public void Without_click_points_the_cursor_is_never_moved()
    {
        var rig = new EngineRig();
        rig.RunFor(0.3);
        Assert.Empty(rig.Input.Of("move"));
    }

    // ---- start: no Ctrl+click ----

    [Fact]
    public void A_toggle_start_waits_until_the_hotkey_modifiers_are_released()
    {
        var rig = new EngineRig();
        rig.Env.Down.UnionWith(new[] { ClickerVk.LControl, ClickerVk.LMenu, 0x59 });
        rig.Waiter.At(300, () => rig.Env.Down.Clear());

        rig.S.TimeLimitEnabled = true;
        rig.S.TimeLimit = 0.6;
        rig.Apply();
        rig.Run(waitForRelease: true);

        Assert.InRange(rig.Input.Of("burst").First().T, 300, 305);
    }

    [Fact]
    public void A_start_without_the_wait_flag_or_in_hold_mode_clicks_at_once()
    {
        var plain = new EngineRig();
        plain.Env.Down.Add(ClickerVk.LControl);
        plain.RunFor(0.1);
        Assert.Equal(0, plain.Input.Of("burst").First().T);

        var hold = new EngineRig(s => s.ActivationMode = ActivationMode.Hold);
        hold.Env.Down.Add(ClickerVk.LControl);
        hold.S.TimeLimitEnabled = true;
        hold.S.TimeLimit = 0.1;
        hold.Apply();
        hold.Run(waitForRelease: true);
        Assert.Equal(0, hold.Input.Of("burst").First().T);
    }

    [Fact]
    public void A_toggle_start_waits_until_an_output_button_that_is_the_hotkey_is_released()
    {
        // Toggle, hotkey = LMB, output = LMB held: the user's own LMB-up would otherwise end the clicker's hold at once.
        var rig = new EngineRig(s =>
        {
            s.Hotkey = new ClickerHotkey { Keys = { ClickerVk.LButton } };
            s.DutyMode = DutyMode.Hold;
        });
        rig.Env.Down.Add(ClickerVk.LButton);
        rig.Waiter.At(200, () => rig.Env.Down.Clear());
        rig.S.TimeLimitEnabled = true;
        rig.S.TimeLimit = 0.4;
        rig.Apply();
        rig.Run(waitForRelease: true);

        Assert.InRange(rig.Input.Of("down").First().T, 200, 205);
    }

    [Fact]
    public void The_start_waits_for_the_output_key_only_when_the_hotkey_contains_it()
    {
        var s = new ClickerSettings { Hotkey = new ClickerHotkey { Modifiers = ClickerModifiers.Ctrl, Keys = { 0x4B } }, InputType = ClickerInput.Keyboard, KeyboardVk = 0x4B };
        Assert.Equal(new[] { ClickerVk.LControl, ClickerVk.RControl, 0x4B }, ClickEngine.StartReleaseKeys(s));

        s.KeyboardVk = 0x4C;
        Assert.Equal(new[] { ClickerVk.LControl, ClickerVk.RControl }, ClickEngine.StartReleaseKeys(s));
    }

    [Fact]
    public void The_output_key_is_known_only_while_a_run_sends_it()
    {
        var rig = new EngineRig(s => { s.InputType = ClickerInput.Mouse; s.MouseButton = ClickerMouseButton.Right; });
        int during = -1;
        rig.Waiter.At(50, () => during = rig.Engine.OutputVk);
        rig.RunFor(0.1);

        Assert.Equal(ClickerVk.RButton, during);
        Assert.Equal(0, rig.Engine.OutputVk);
    }

    [Fact]
    public void Both_hands_of_every_demanded_modifier_family_are_watched()
    {
        var keys = ClickEngine.ModifierKeys(ClickerHotkey.Default());
        Assert.Equal(new[] { ClickerVk.LControl, ClickerVk.RControl, ClickerVk.LMenu, ClickerVk.RMenu }, keys);
        Assert.Empty(ClickEngine.ModifierKeys(new ClickerHotkey { Keys = { 0x70 } }));
    }

    // ---- state ----

    [Fact]
    public void State_is_reported_ten_times_a_second_at_most()
    {
        var rig = new EngineRig();

        rig.RunFor(1);

        Assert.InRange(rig.Snapshots.Count, 8, 14);
        Assert.True(rig.Snapshots.First().Running);
        Assert.False(rig.Snapshots.Last().Running);
        Assert.Equal(25, rig.Snapshots.Last().Cps);
        Assert.Equal(25, rig.Snapshots.Last().Clicks);
    }

    [Fact]
    public void A_flapping_blocked_state_does_not_push_the_report_rate_above_ten_per_second()
    {
        var rig = new EngineRig();   // 25 per second
        // Windows takes every second click only: the blocked flag flips with every batch.
        for (int k = 0; k < 50; k++)
        {
            bool fail = k % 2 == 1;
            rig.Waiter.At(k * 40 - 1, () => rig.Input.FailEverything = fail);
        }

        rig.RunFor(1);

        Assert.InRange(rig.Snapshots.Count, 8, 14);
        Assert.Contains(rig.Snapshots, s => s.BlockedByElevated == "game.exe");   // the change still gets through
    }

    [Fact]
    public void An_elevated_window_in_front_is_reported_as_blocking()
    {
        var rig = new EngineRig();
        rig.Env.Fg = new ForegroundInfo(new IntPtr(3), 9, "Window", @"C:\Tools\admin.exe", elevated: true);
        rig.RunFor(0.2);
        Assert.Contains(rig.Snapshots, s => s.BlockedByElevated == "admin.exe");

        var elevatedHost = new EngineRig();
        elevatedHost.Env.Elevated = true;
        elevatedHost.Env.Fg = rig.Env.Fg;
        elevatedHost.RunFor(0.2);
        Assert.All(elevatedHost.Snapshots, s => Assert.Null(s.BlockedByElevated));
    }

    [Fact]
    public void A_foreground_with_unreadable_process_info_is_not_reported_as_blocking()
    {
        // Anti-cheat protected games strip the query rights from handles: no image name and no token, and that must not read as "elevated".
        var rig = new EngineRig();
        rig.Env.Fg = new ForegroundInfo(new IntPtr(3), 9, "Window", exe: null, elevated: false);

        rig.RunFor(0.3);

        Assert.NotEmpty(rig.Snapshots);
        Assert.All(rig.Snapshots, s => Assert.Null(s.BlockedByElevated));
    }

    [Fact]
    public void Input_that_windows_rejects_is_not_counted_and_flags_the_foreground_program()
    {
        var rig = new EngineRig();
        rig.Input.FailEverything = true;

        var summary = rig.RunFor(0.3);

        Assert.Equal(0, summary.Clicks);
        Assert.Contains(rig.Snapshots, s => s.BlockedByElevated == "game.exe");
    }

    [Fact]
    public void A_listener_that_throws_does_not_stop_the_run_or_the_end_of_it()
    {
        var rig = new EngineRig();
        rig.Engine.StateChanged += _ => throw new InvalidOperationException("listener");
        rig.Engine.RunEnded += _ => throw new InvalidOperationException("listener");

        var summary = rig.RunFor(0.2);

        Assert.Equal(StopReason.TimeLimit, summary.Reason);
        Assert.False(rig.Engine.IsRunning);
    }

    [Fact]
    public void The_run_is_over_and_the_engine_idle_when_the_run_ended_event_fires()
    {
        var rig = new EngineRig();
        bool? running = null;
        RunSummary? seen = null;
        rig.Engine.RunEnded += s =>
        {
            running = rig.Engine.IsRunning;
            seen = s;
        };

        var summary = rig.RunFor(0.1);

        Assert.False(running);
        Assert.Same(summary, seen);
    }

    // ---- live config ----

    [Fact]
    public void A_config_push_changes_the_rate_of_a_running_click_loop()
    {
        var rig = new EngineRig(s => s.ClickSpeed = 10);
        rig.Waiter.At(500, () =>
        {
            rig.S.ClickSpeed = 100;
            rig.Apply();
        });

        rig.RunFor(1);

        var bursts = rig.Input.Of("burst").ToList();
        Assert.Equal(5, bursts.Count(b => b.A == 1 && b.T < 500));
        Assert.Equal(25, bursts.Count(b => b.A == 2 && b.T >= 500));
    }
}
