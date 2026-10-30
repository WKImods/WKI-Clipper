using System;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>How long each press is held inside its cycle (design section 4).</summary>
public static class DutyCycle
{
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
}
