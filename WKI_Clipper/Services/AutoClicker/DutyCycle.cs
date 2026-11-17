using System;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>How long each press is held inside its cycle (design section 4), given in percent of the cycle or in seconds.</summary>
public static class DutyCycle
{
    /// <summary>Range and default of the seconds unit: 0.001 s to 1 h, default 50 ms.</summary>
    public const double MinSeconds = 0.001;
    public const double MaxSeconds = 3600;
    public const double DefaultSeconds = 0.05;

    /// <summary>Decimals a seconds value keeps (milliseconds).</summary>
    public const int SecondsDecimals = 3;

    // A seconds value this close above the limit (ms) is no cut; it only went through a multiplication.
    private const double CutEpsilonMs = 1e-9;

    /// <summary>Highest duty percent the rate allows: above 500 per second 1 %, from 200 30 %, from 100 70 %, from 50 98 %, else uncapped.</summary>
    public static double RateCapPercent(double cps)
    {
        if (cps > 500) return 1;
        if (cps >= 200) return 30;
        if (cps >= 100) return 70;
        if (cps >= 50) return 98;
        return 100;
    }

    /// <summary>The user's percent limited by the rate cap; 0 when the duty cycle is off. NaN and negative values count as 0.</summary>
    public static double EffectivePercent(double cps, bool enabled, double percent)
    {
        if (!enabled || double.IsNaN(percent) || percent <= 0) return 0;
        return Math.Min(Math.Min(percent, 100), RateCapPercent(cps));
    }

    /// <summary>Press hold in milliseconds for one cycle of <paramref name="intervalMs"/>: interval times the effective duty, at most the interval.</summary>
    public static double HoldMs(double intervalMs, double cps, bool enabled, double percent)
    {
        double interval = Math.Max(0, intervalMs);
        return Math.Min(interval, interval * EffectivePercent(cps, enabled, percent) / 100.0);
    }

    /// <summary>
    /// Press hold in milliseconds in the chosen unit. Percent: as <see cref="HoldMs(double, double, bool, double)"/>. Seconds: the
    /// seconds value, but never more than the interval or the rate cap allows. Off, or a seconds value that is NaN or not positive: 0.
    /// </summary>
    public static double HoldMs(double intervalMs, double cps, bool enabled, DutyUnit unit, double percent, double seconds)
    {
        if (unit != DutyUnit.Seconds) return HoldMs(intervalMs, cps, enabled, percent);
        if (!enabled || double.IsNaN(seconds) || seconds <= 0) return 0;
        return Math.Min(SecondsLimitMs(intervalMs, cps), seconds * 1000.0);
    }

    /// <summary>
    /// True when the cycle cuts the seconds value (interval or rate cap); <paramref name="effectiveSeconds"/> is the hold that really
    /// goes out (the value itself when nothing is cut, 0 for a value that is NaN or not positive).
    /// </summary>
    public static bool SecondsCut(double intervalMs, double cps, double seconds, out double effectiveSeconds)
    {
        if (double.IsNaN(seconds) || seconds <= 0)
        {
            effectiveSeconds = 0;
            return false;
        }
        double limitMs = SecondsLimitMs(intervalMs, cps);
        if (seconds * 1000.0 > limitMs + CutEpsilonMs)
        {
            effectiveSeconds = limitMs / 1000.0;
            return true;
        }
        effectiveSeconds = seconds;
        return false;
    }

    /// <summary>"45 %" or "0,05 s" (the app language's decimal separator; seconds with up to three decimals).</summary>
    public static string Describe(DutyUnit unit, double percent, double seconds)
        => unit == DutyUnit.Seconds
            ? ClickerNumber.Format(seconds, SecondsDecimals) + " s"
            : ClickerNumber.Format(percent) + " %";

    private static double SecondsLimitMs(double intervalMs, double cps)
    {
        double interval = Math.Max(0, intervalMs);
        return Math.Min(interval, interval * RateCapPercent(cps) / 100.0);
    }
}
