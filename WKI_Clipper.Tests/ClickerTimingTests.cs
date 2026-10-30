using System;
using WKI_Clipper.Models;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Double click press pairs and the keyboard repeat schedule (design section 4).</summary>
public sealed class ClickerTimingTests
{
    // ---- DoubleClickTiming ----

    private static void AssertInvariants(DoubleClickPlan p, double cycleMs, double dctMs)
    {
        Assert.True(p.Hold1Ms >= 0 && p.Hold2Ms >= 0 && p.GapMs >= 0);
        Assert.True(p.TotalMs <= cycleMs + 1e-9, $"pair {p.TotalMs} ms does not fit the {cycleMs} ms cycle");
        Assert.True(p.SecondPressOffsetMs <= 0.9 * dctMs + 1e-9, $"second press at {p.SecondPressOffsetMs} ms is outside 90 % of {dctMs} ms");
        Assert.True(cycleMs - p.TotalMs >= Math.Min(DoubleClickTiming.MinGapMs, cycleMs / 2) - 1e-9,
            $"only {cycleMs - p.TotalMs} ms of rest after the pair in a {cycleMs} ms cycle");
    }

    [Fact]
    public void A_double_click_at_ten_per_second_uses_the_whole_window()
    {
        // 100 ms cycle, 45 % duty = 45 ms hold, system double-click time 500 ms.
        var p = DoubleClickTiming.Plan(cycleMs: 100, holdMs: 45, doubleClickTimeMs: 500);

        Assert.Equal(45, p.Hold1Ms, 9);
        Assert.Equal(45, p.Hold2Ms, 9);
        Assert.Equal(5, p.GapMs, 9);            // the 10 ms of up time are split between the gap and the rest after the pair
        AssertInvariants(p, 100, 500);
    }

    [Fact]
    public void Both_presses_honour_the_duty_hold()
    {
        var p = DoubleClickTiming.Plan(cycleMs: 1000, holdMs: 30, doubleClickTimeMs: 500);

        Assert.Equal(30, p.Hold1Ms, 9);
        Assert.Equal(30, p.Hold2Ms, 9);
    }

    [Fact]
    public void The_second_press_starts_at_ninety_percent_of_the_double_click_time_when_the_cycle_is_long()
    {
        var p = DoubleClickTiming.Plan(cycleMs: 5000, holdMs: 20, doubleClickTimeMs: 500);

        Assert.Equal(450, p.SecondPressOffsetMs, 9);
        AssertInvariants(p, 5000, 500);
    }

    [Fact]
    public void A_zero_hold_gives_two_instant_presses_with_the_gap_between()
    {
        var p = DoubleClickTiming.Plan(cycleMs: 100, holdMs: 0, doubleClickTimeMs: 500);

        Assert.Equal(0, p.Hold1Ms);
        Assert.Equal(0, p.Hold2Ms);
        Assert.Equal(50, p.GapMs, 9);
        AssertInvariants(p, 100, 500);
    }

    [Theory]
    [InlineData(0.0, 50.0)]
    [InlineData(45.0, 5.0)]
    [InlineData(100.0, 1.0)]
    public void The_button_is_up_after_the_pair_before_the_next_cycle_starts(double holdMs, double restMs)
    {
        // 10 clicks per second: press 2 must not land on the next cycle's press 1.
        var p = DoubleClickTiming.Plan(cycleMs: 100, holdMs: holdMs, doubleClickTimeMs: 500);

        Assert.True(100 - p.TotalMs > 0);
        Assert.Equal(restMs, 100 - p.TotalMs, 9);
    }

    [Fact]
    public void A_hold_longer_than_the_cycle_allows_is_shortened_and_a_gap_remains()
    {
        var p = DoubleClickTiming.Plan(cycleMs: 100, holdMs: 100, doubleClickTimeMs: 500);

        Assert.True(p.GapMs >= DoubleClickTiming.MinGapMs);
        Assert.True(p.Hold1Ms < 100);
        Assert.Equal(p.Hold1Ms, p.Hold2Ms, 9);
        AssertInvariants(p, 100, 500);
    }

    [Fact]
    public void A_hold_longer_than_the_double_click_window_is_shortened()
    {
        // cycle 10 s, hold 5 s, window 0.9 * 500 = 450 ms: the pair must still fit the window.
        var p = DoubleClickTiming.Plan(cycleMs: 10_000, holdMs: 5000, doubleClickTimeMs: 500);

        Assert.True(p.GapMs >= DoubleClickTiming.MinGapMs);
        AssertInvariants(p, 10_000, 500);
    }

    [Theory]
    [InlineData(100.0, 0.0, 200.0)]
    [InlineData(100.0, 45.0, 200.0)]
    [InlineData(100.0, 45.0, 500.0)]
    [InlineData(100.0, 99.0, 900.0)]
    [InlineData(250.0, 100.0, 300.0)]
    [InlineData(1000.0, 450.0, 500.0)]
    [InlineData(60_000.0, 30_000.0, 500.0)]
    [InlineData(2.0, 1.0, 500.0)]
    [InlineData(1.0, 1.0, 500.0)]
    [InlineData(100.0, 45.0, 5000.0)]
    public void The_pair_always_fits_the_cycle_and_the_double_click_window(double cycleMs, double holdMs, double dctMs)
        => AssertInvariants(DoubleClickTiming.Plan(cycleMs, holdMs, dctMs), cycleMs, dctMs);

    [Fact]
    public void Invalid_input_yields_a_finite_non_negative_plan()
    {
        foreach (var (cycle, hold, dct) in new[]
                 {
                     (double.NaN, 10.0, 500.0), (100.0, double.NaN, 500.0), (100.0, 10.0, double.NaN),
                     (-5.0, 10.0, 500.0), (100.0, -3.0, 500.0), (100.0, 10.0, 0.0), (100.0, 10.0, -1.0)
                 })
        {
            var p = DoubleClickTiming.Plan(cycle, hold, dct);
            Assert.True(double.IsFinite(p.Hold1Ms) && double.IsFinite(p.GapMs) && double.IsFinite(p.Hold2Ms));
            Assert.True(p.Hold1Ms >= 0 && p.GapMs >= 0 && p.Hold2Ms >= 0);
        }
    }

    // ---- KeyboardRepeat ----

    [Theory]
    [InlineData(0, 250.0)]
    [InlineData(1, 500.0)]
    [InlineData(2, 750.0)]
    [InlineData(3, 1000.0)]
    [InlineData(-4, 250.0)]
    [InlineData(9, 1000.0)]
    public void The_repeat_delay_index_maps_to_250_to_1000_ms(int index, double delayMs)
        => Assert.Equal(delayMs, KeyboardRepeat.DelayMs(index));

    [Theory]
    [InlineData(0, 2.5)]
    [InlineData(31, 30.0)]
    [InlineData(-1, 2.5)]
    [InlineData(99, 30.0)]
    public void The_repeat_speed_ends_are_2_5_and_30_per_second(int speed, double hz)
        => Assert.Equal(hz, KeyboardRepeat.RateHz(speed), 9);

    [Fact]
    public void The_repeat_speed_is_linear_between_the_ends()
    {
        double step = KeyboardRepeat.RateHz(1) - KeyboardRepeat.RateHz(0);
        Assert.Equal(27.5 / 31.0, step, 9);
        for (int s = 1; s <= 31; s++)
            Assert.Equal(step, KeyboardRepeat.RateHz(s) - KeyboardRepeat.RateHz(s - 1), 9);
    }

    [Fact]
    public void The_repeat_interval_ranges_from_about_33_to_400_ms()
    {
        Assert.Equal(400, KeyboardRepeat.IntervalMs(0), 9);
        Assert.Equal(1000.0 / 30, KeyboardRepeat.IntervalMs(31), 9);
    }

    [Fact]
    public void The_plan_combines_delay_and_interval()
    {
        var plan = KeyboardRepeat.Plan(spiDelayIndex: 1, spiSpeed: 31);
        Assert.Equal(500, plan.DelayMs);
        Assert.Equal(1000.0 / 30, plan.IntervalMs, 9);
    }

    [Fact]
    public void Repeats_start_after_the_delay_and_follow_the_interval()
    {
        var plan = new RepeatPlan(DelayMs: 500, IntervalMs: 100);

        Assert.Equal(0, KeyboardRepeat.RepeatsDueBy(0, plan));
        Assert.Equal(0, KeyboardRepeat.RepeatsDueBy(499.9, plan));
        Assert.Equal(1, KeyboardRepeat.RepeatsDueBy(500, plan));
        Assert.Equal(1, KeyboardRepeat.RepeatsDueBy(599.9, plan));
        Assert.Equal(2, KeyboardRepeat.RepeatsDueBy(600, plan));
        Assert.Equal(11, KeyboardRepeat.RepeatsDueBy(1500, plan));
        Assert.Equal(0, KeyboardRepeat.RepeatsDueBy(-5, plan));
    }

    [Fact]
    public void The_next_repeat_time_matches_the_count()
    {
        var plan = new RepeatPlan(500, 100);
        Assert.Equal(500, KeyboardRepeat.RepeatTimeMs(0, plan));
        Assert.Equal(600, KeyboardRepeat.RepeatTimeMs(1, plan));
        for (int i = 0; i < 20; i++)
            Assert.Equal(i + 1, KeyboardRepeat.RepeatsDueBy(KeyboardRepeat.RepeatTimeMs(i, plan), plan));
    }

    [Theory]
    [InlineData(ClickerInput.Keyboard, true, 100.0, DutyMode.Click, true)]
    [InlineData(ClickerInput.Keyboard, true, 99.0, DutyMode.Click, false)]
    [InlineData(ClickerInput.Keyboard, false, 100.0, DutyMode.Click, false)]   // duty off: not a hold
    [InlineData(ClickerInput.Keyboard, false, 45.0, DutyMode.Hold, true)]       // Hold mode always holds
    [InlineData(ClickerInput.Keyboard, true, 45.0, DutyMode.Hold, true)]
    [InlineData(ClickerInput.Mouse, true, 100.0, DutyMode.Click, false)]        // mouse presses are no key repeats
    [InlineData(ClickerInput.Mouse, true, 100.0, DutyMode.Hold, false)]
    public void Keyboard_hold_mode_is_full_duty_or_duty_mode_hold(ClickerInput input, bool dutyEnabled, double dutyPercent, DutyMode mode, bool expected)
        => Assert.Equal(expected, KeyboardRepeat.IsHoldMode(input, dutyEnabled, dutyPercent, mode));

    [Fact]
    public void Hold_mode_follows_the_settings()
    {
        var s = new ClickerSettings { InputType = ClickerInput.Keyboard, KeyboardVk = 0x41, DutyCycleEnabled = true, DutyCyclePercent = 100 };
        Assert.True(KeyboardRepeat.IsHoldMode(s));
        s.DutyCyclePercent = 60;
        Assert.False(KeyboardRepeat.IsHoldMode(s));
    }
}
