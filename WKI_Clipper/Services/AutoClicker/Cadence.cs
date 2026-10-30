using System;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>
/// The click cadence of a configuration, worked out once per run: interval of one cycle, how many cycles go into one batch, the
/// batch duration and the press hold of the duty cycle.
/// </summary>
public readonly record struct CadencePlan(double IntervalMs, double ClicksPerSecond, int CyclesPerBatch, double BatchMs, double HoldMs, bool DoubleClick)
{
    public static CadencePlan From(ClickerSettings s)
    {
        double interval = Cadence.IntervalMs(s);
        double cps = Cadence.ClicksPerSecond(interval);
        int cycles = Cadence.CyclesPerBatch(interval, s.DoubleClick);
        double hold = DutyCycle.HoldMs(interval, cps, s.DutyCycleEnabled, s.DutyCyclePercent);
        return new CadencePlan(interval, cps, cycles, Cadence.BatchMs(interval, cycles), hold, s.DoubleClick);
    }
}

/// <summary>Interval and batching of the click rate (design section 4). Pure math; the engine owns the clock.</summary>
public static class Cadence
{
    /// <summary>Shortest cycle the engine schedules.</summary>
    public const double MinIntervalMs = 1;

    // The thresholds are compared with a hair of slack so 3000 per minute counts as exactly 50 per second.
    private const double Epsilon = 1e-9;

    /// <summary>
    /// Time of one cycle in milliseconds: 1/n s, 60/n, 3600/n or 86400/n in rate mode, the duration in duration mode, never below 1 ms.
    /// A speed that is zero, negative or NaN counts as 1 per unit, so a settings value that skipped the normalizer cannot divide by zero.
    /// </summary>
    public static double IntervalMs(RateInputMode mode, double clickSpeed, RateUnit unit, long durationMs)
    {
        if (mode == RateInputMode.Duration) return Math.Max(MinIntervalMs, durationMs);
        double speed = clickSpeed > 0 ? clickSpeed : 1;
        return Math.Max(MinIntervalMs, 1000.0 * ClickerSettingsRules.SecondsPerUnit(unit) / speed);
    }

    public static double IntervalMs(ClickerSettings s) => IntervalMs(s.RateInputMode, s.ClickSpeed, s.ClickUnit, s.DurationMs);

    public static double ClicksPerSecond(double intervalMs) => 1000.0 / Math.Max(MinIntervalMs, intervalMs);

    /// <summary>More than 500 per second: 3 cycles per batch; 50 per second and up: 2; else 1. Double click is never batched.</summary>
    public static int CyclesPerBatch(double intervalMs, bool doubleClick)
    {
        if (doubleClick) return 1;
        double cps = ClicksPerSecond(intervalMs);
        if (cps > 500 + Epsilon) return 3;
        if (cps >= 50 - Epsilon) return 2;
        return 1;
    }

    public static double BatchMs(double intervalMs, int cycles) => intervalMs * Math.Max(1, cycles);
}
