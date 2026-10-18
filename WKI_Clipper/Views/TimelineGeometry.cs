using System;
using System.Collections.Generic;

namespace WKI_Clipper.Views;

internal enum TimelineHit { None, InHandle, OutHandle, Track }

/// <summary>x position on the timeline control &lt;-&gt; seconds, and what a click at x hits. Pure, so the mouse maths is tested.</summary>
internal static class TimelineGeometry
{
    /// <summary>Left and right margin, so the marks at 0 and at the end can still be grabbed.</summary>
    public const double Padding = 10;

    public const double HandleHitRadius = 7;

    private static double Usable(double width) => Math.Max(0, width - 2 * Padding);

    /// <summary>x of a time; times outside the clip stick to its ends. Unknown length or a control too narrow gives the left margin.</summary>
    public static double XForSeconds(double seconds, double totalSeconds, double width)
    {
        double usable = Usable(width);
        if (totalSeconds <= 0 || usable <= 0 || double.IsNaN(seconds)) return Padding;
        return Padding + Math.Clamp(seconds / totalSeconds, 0, 1) * usable;
    }

    /// <summary>The time under x, clamped to [0, total].</summary>
    public static double SecondsForX(double x, double totalSeconds, double width)
    {
        double usable = Usable(width);
        if (totalSeconds <= 0 || usable <= 0 || double.IsNaN(x)) return 0;
        return Math.Clamp((x - Padding) / usable, 0, 1) * totalSeconds;
    }

    /// <summary>
    /// A handle when x is within <see cref="HandleHitRadius"/> of one; the nearer wins. When both sit on the same spot,
    /// left of it grabs the start and the rest grabs the end, so either can be pulled out again.
    /// </summary>
    public static TimelineHit HitTest(double x, double inX, double outX)
    {
        if (double.IsNaN(x)) return TimelineHit.None;

        double dIn = Math.Abs(x - inX), dOut = Math.Abs(x - outX);
        double nearest = Math.Min(dIn, dOut);
        if (nearest > HandleHitRadius) return TimelineHit.Track;

        if (dIn < dOut) return TimelineHit.InHandle;
        if (dOut < dIn) return TimelineHit.OutHandle;
        return x < inX ? TimelineHit.InHandle : TimelineHit.OutHandle;
    }

    /// <summary>Keeps at most one tick per <paramref name="minSpacing"/> pixels of an ascending list.</summary>
    public static IEnumerable<double> ThinTicks(IEnumerable<double> xs, double minSpacing)
    {
        double last = double.NegativeInfinity;
        foreach (var x in xs)
        {
            if (x - last < minSpacing) continue;
            last = x;
            yield return x;
        }
    }
}
