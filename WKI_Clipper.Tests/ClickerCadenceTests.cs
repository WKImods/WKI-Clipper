using System;
using System.Linq;
using WKI_Clipper.Models;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Interval, batching, duty hold and the randomized batch duration (design section 4).</summary>
public sealed class ClickerCadenceTests
{
    // ---- Cadence: interval ----

    [Theory]
    [InlineData(25, RateUnit.Second, 40.0)]
    [InlineData(1, RateUnit.Second, 1000.0)]
    [InlineData(500, RateUnit.Second, 2.0)]
    [InlineData(1000, RateUnit.Second, 1.0)]
    [InlineData(60, RateUnit.Minute, 1000.0)]
    [InlineData(1, RateUnit.Minute, 60_000.0)]
    [InlineData(3600, RateUnit.Hour, 1000.0)]
    [InlineData(1, RateUnit.Day, 86_400_000.0)]
    public void Rate_mode_interval_is_one_unit_divided_by_the_speed(double speed, RateUnit unit, double expectedMs)
        => Assert.Equal(expectedMs, Cadence.IntervalMs(RateInputMode.Rate, speed, unit, 999), 9);

    [Theory]
    [InlineData(40L, 40.0)]
    [InlineData(1L, 1.0)]
    [InlineData(3_599_999_999L, 3_599_999_999.0)]
    [InlineData(0L, 1.0)]
    public void Duration_mode_interval_is_the_duration_with_a_floor_of_one_millisecond(long durationMs, double expectedMs)
        => Assert.Equal(expectedMs, Cadence.IntervalMs(RateInputMode.Duration, 123, RateUnit.Hour, durationMs), 9);

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    public void A_rate_without_a_usable_speed_never_divides_by_zero(double speed)
    {
        double ms = Cadence.IntervalMs(RateInputMode.Rate, speed, RateUnit.Second, 40);
        Assert.True(double.IsFinite(ms));
        Assert.Equal(1000, ms, 9); // treated as the minimum speed of 1 per unit
    }

    [Fact]
    public void Interval_from_settings_follows_the_rate_input_mode()
    {
        var s = new ClickerSettings { ClickSpeed = 100, ClickUnit = RateUnit.Second, DurationMs = 250, RateInputMode = RateInputMode.Rate };
        Assert.Equal(10, Cadence.IntervalMs(s), 9);
        s.RateInputMode = RateInputMode.Duration;
        Assert.Equal(250, Cadence.IntervalMs(s), 9);
    }

    [Theory]
    [InlineData(40.0, 25.0)]
    [InlineData(2.0, 500.0)]
    [InlineData(1.0, 1000.0)]
    public void Clicks_per_second_is_the_reciprocal_of_the_interval(double intervalMs, double cps)
        => Assert.Equal(cps, Cadence.ClicksPerSecond(intervalMs), 9);

    // ---- Cadence: batching ----

    [Theory]
    [InlineData(1000.0, 1)]   // 1 cps
    [InlineData(40.0, 1)]     // 25 cps
    [InlineData(20.5, 1)]     // just under 50 cps
    [InlineData(20.0, 2)]     // exactly 50 cps
    [InlineData(10.0, 2)]     // 100 cps
    [InlineData(2.0, 2)]      // exactly 500 cps is not "more than 500"
    [InlineData(1.9, 3)]      // above 500 cps
    [InlineData(1.0, 3)]      // 1000 cps (extended limit)
    public void Batching_uses_one_two_or_three_cycles_by_rate(double intervalMs, int cycles)
        => Assert.Equal(cycles, Cadence.CyclesPerBatch(intervalMs, doubleClick: false));

    [Theory]
    [InlineData(100.0)]
    [InlineData(20.0)]
    [InlineData(2.0)]
    public void Double_click_is_never_batched(double intervalMs)
        => Assert.Equal(1, Cadence.CyclesPerBatch(intervalMs, doubleClick: true));

    [Fact]
    public void Batching_thresholds_hold_for_rates_given_per_minute()
    {
        // 3000 per minute is exactly 50 per second, 30000 per minute exactly 500.
        Assert.Equal(2, Cadence.CyclesPerBatch(Cadence.IntervalMs(RateInputMode.Rate, 3000, RateUnit.Minute, 0), false));
        Assert.Equal(2, Cadence.CyclesPerBatch(Cadence.IntervalMs(RateInputMode.Rate, 30000, RateUnit.Minute, 0), false));
        Assert.Equal(3, Cadence.CyclesPerBatch(Cadence.IntervalMs(RateInputMode.Rate, 30001, RateUnit.Minute, 0), false));
    }

    [Theory]
    [InlineData(40.0, 1, 40.0)]
    [InlineData(10.0, 2, 20.0)]
    [InlineData(1.0, 3, 3.0)]
    public void Batch_duration_is_interval_times_cycles(double intervalMs, int cycles, double batchMs)
        => Assert.Equal(batchMs, Cadence.BatchMs(intervalMs, cycles), 9);

    [Fact]
    public void The_plan_bundles_interval_batching_and_hold_for_the_settings()
    {
        var s = new ClickerSettings { ClickSpeed = 100, ClickUnit = RateUnit.Second, DutyCycleEnabled = true, DutyCyclePercent = 90 };
        var plan = CadencePlan.From(s);

        Assert.Equal(10, plan.IntervalMs, 9);
        Assert.Equal(100, plan.ClicksPerSecond, 9);
        Assert.Equal(2, plan.CyclesPerBatch);
        Assert.Equal(20, plan.BatchMs, 9);
        Assert.Equal(7, plan.HoldMs, 9);        // 100 cps caps the duty at 70 %
        Assert.False(plan.DoubleClick);
    }

    [Fact]
    public void The_plan_of_a_double_click_has_one_cycle_per_batch()
    {
        var s = new ClickerSettings { ClickSpeed = 10, DoubleClick = true };
        var plan = CadencePlan.From(s);

        Assert.True(plan.DoubleClick);
        Assert.Equal(1, plan.CyclesPerBatch);
        Assert.Equal(100, plan.BatchMs, 9);
    }

    // ---- DutyCycle ----

    [Theory]
    [InlineData(1.0, 100.0)]
    [InlineData(25.0, 100.0)]
    [InlineData(49.9, 100.0)]
    [InlineData(50.0, 98.0)]
    [InlineData(99.9, 98.0)]
    [InlineData(100.0, 70.0)]
    [InlineData(199.9, 70.0)]
    [InlineData(200.0, 30.0)]
    [InlineData(500.0, 30.0)]
    [InlineData(500.1, 1.0)]
    [InlineData(1000.0, 1.0)]
    public void The_rate_caps_the_duty_percent(double cps, double capPercent)
        => Assert.Equal(capPercent, DutyCycle.RateCapPercent(cps), 9);

    [Fact]
    public void Hold_is_the_interval_times_the_duty_percent()
        => Assert.Equal(18, DutyCycle.HoldMs(intervalMs: 40, cps: 25, enabled: true, percent: 45), 9);

    [Fact]
    public void A_disabled_duty_cycle_holds_for_zero_milliseconds()
        => Assert.Equal(0, DutyCycle.HoldMs(40, 25, enabled: false, percent: 45), 9);

    [Theory]
    [InlineData(10.0, 100.0, 90.0, 7.0)]    // 100 cps: capped at 70 %
    [InlineData(5.0, 200.0, 90.0, 1.5)]     // 200 cps: capped at 30 %
    [InlineData(2.0, 500.0, 90.0, 0.6)]     // 500 cps: still 30 %
    [InlineData(1.0, 1000.0, 90.0, 0.01)]   // above 500 cps: 1 %
    [InlineData(20.0, 50.0, 100.0, 19.6)]   // 50 cps: 98 %
    [InlineData(10.0, 100.0, 20.0, 2.0)]    // below the cap the user's value wins
    public void Hold_respects_the_rate_caps(double intervalMs, double cps, double percent, double holdMs)
        => Assert.Equal(holdMs, DutyCycle.HoldMs(intervalMs, cps, true, percent), 9);

    [Fact]
    public void Hold_never_exceeds_the_cycle_and_never_goes_negative()
    {
        Assert.Equal(40, DutyCycle.HoldMs(40, 25, true, 100), 9);
        Assert.Equal(40, DutyCycle.HoldMs(40, 25, true, 250), 9);
        Assert.Equal(0, DutyCycle.HoldMs(40, 25, true, -10), 9);
        Assert.Equal(0, DutyCycle.HoldMs(40, 25, true, double.NaN), 9);
    }

    // ---- Randomizer ----

    [Fact]
    public void The_same_seed_gives_the_same_sequence()
    {
        var a = new Randomizer(1234);
        var b = new Randomizer(1234);
        for (int i = 0; i < 200; i++)
        {
            Assert.Equal(a.NextDouble(), b.NextDouble());
            Assert.Equal(a.NextGaussian(), b.NextGaussian());
            Assert.Equal(a.NextDuration(40, 35), b.NextDuration(40, 35));
        }
    }

    [Fact]
    public void Different_seeds_give_different_sequences()
    {
        var a = new Randomizer(1);
        var b = new Randomizer(2);
        Assert.NotEqual(Enumerable.Range(0, 10).Select(_ => a.NextDouble()).ToArray(),
                        Enumerable.Range(0, 10).Select(_ => b.NextDouble()).ToArray());
    }

    [Fact]
    public void The_gaussian_is_standard_normal()
    {
        var r = new Randomizer(42);
        const int n = 200_000;
        double sum = 0, sumSq = 0;
        for (int i = 0; i < n; i++)
        {
            double z = r.NextGaussian();
            sum += z;
            sumSq += z * z;
        }
        double mean = sum / n;
        double variance = sumSq / n - mean * mean;
        Assert.InRange(mean, -0.02, 0.02);
        Assert.InRange(variance, 0.97, 1.03);
    }

    [Theory]
    [InlineData(40.0, 35.0)]     // the default rate and randomization
    [InlineData(20.0, 35.0)]
    [InlineData(100.0, 50.0)]
    [InlineData(200.0, 15.0)]
    [InlineData(40.0, 100.0)]    // the whole allowed range: a floored normal would run about 8 % long here
    [InlineData(40.0, 150.0)]
    [InlineData(40.0, 200.0)]    // and about 41 % long here (the rate 29 % too low)
    public void The_mean_of_the_drawn_durations_is_within_tolerance_of_the_base(double baseMs, double percent)
    {
        // The heavy right tail at 100 % and more makes the sample mean noisy (standard error about cv / sqrt(n)): more draws, 2 %.
        bool wide = percent >= 100;
        int n = wide ? 400_000 : 100_000;
        double tolerance = wide ? 0.02 : 0.01;
        var r = new Randomizer(7);
        double sum = 0;
        for (int i = 0; i < n; i++) sum += r.NextDuration(baseMs, percent);
        Assert.InRange(sum / n, baseMs * (1 - tolerance), baseMs * (1 + tolerance));
    }

    [Fact]
    public void A_drawn_duration_never_falls_below_one_millisecond()
    {
        var r = new Randomizer(99);
        for (int i = 0; i < 50_000; i++)
            Assert.True(r.NextDuration(3, 200) >= 1.0);
    }

    [Fact]
    public void Zero_percent_returns_the_base_exactly()
    {
        var r = new Randomizer(5);
        for (int i = 0; i < 20; i++) Assert.Equal(40, r.NextDuration(40, 0));
    }

    [Fact]
    public void The_spread_matches_the_percent()
    {
        var r = new Randomizer(11);
        const int n = 100_000;
        var draws = Enumerable.Range(0, n).Select(_ => r.NextDuration(100, 20)).ToArray();
        double mean = draws.Average();
        double sd = Math.Sqrt(draws.Sum(d => (d - mean) * (d - mean)) / n);
        Assert.InRange(sd, 19, 21);    // sigma = base * pct / 100 = 20 ms
    }

    [Fact]
    public void NextRange_and_Chance_stay_in_bounds()
    {
        var r = new Randomizer(3);
        for (int i = 0; i < 10_000; i++)
        {
            double v = r.NextRange(0.2, 0.6);
            Assert.InRange(v, 0.2, 0.6);
        }
        int hits = 0;
        for (int i = 0; i < 100_000; i++) if (r.Chance(0.25)) hits++;
        Assert.InRange(hits / 100_000.0, 0.24, 0.26);
        Assert.False(new Randomizer(1).Chance(0));
        Assert.True(new Randomizer(1).Chance(1));
    }
}
