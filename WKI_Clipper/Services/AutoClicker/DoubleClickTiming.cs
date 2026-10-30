using System;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>One double-click cycle: press 1 held <see cref="Hold1Ms"/>, released for <see cref="GapMs"/>, press 2 held <see cref="Hold2Ms"/>.</summary>
public readonly record struct DoubleClickPlan(double Hold1Ms, double GapMs, double Hold2Ms)
{
    /// <summary>Time from the first press to the second press.</summary>
    public double SecondPressOffsetMs => Hold1Ms + GapMs;

    public double TotalMs => Hold1Ms + GapMs + Hold2Ms;
}

/// <summary>
/// Timing of a double click (design section 4): both presses honour the duty hold, the second press starts within
/// 90 % of the system double-click time after the first, and the whole pair fits into the cycle with the button up again before the next one.
/// </summary>
public static class DoubleClickTiming
{
    /// <summary>The second press must come within this fraction of GetDoubleClickTime.</summary>
    public const double WindowFraction = 0.9;

    /// <summary>The button is up for at least this long between the presses (when the cycle has room for it).</summary>
    public const double MinGapMs = 1;

    /// <summary>A GetDoubleClickTime below this counts as this value (Windows allows 1 ms, which would leave no room for a pair).</summary>
    public const double MinDoubleClickTimeMs = 20;

    /// <summary>
    /// Works out the pair. The holds shrink (equally) when the cycle or the double-click window cannot take them; the free up time
    /// of the cycle is split evenly between the gap inside the pair and the rest after it (the gap is at most half of it, and never
    /// past the 90 % window), so the pair never touches the next cycle's first press.
    /// </summary>
    public static DoubleClickPlan Plan(double cycleMs, double holdMs, double doubleClickTimeMs)
    {
        double cycle = double.IsNaN(cycleMs) ? 0 : Math.Max(0, cycleMs);
        double dct = double.IsNaN(doubleClickTimeMs) ? MinDoubleClickTimeMs : Math.Max(MinDoubleClickTimeMs, doubleClickTimeMs);
        double window = WindowFraction * dct;

        double minGap = Math.Min(MinGapMs, cycle / 2);
        double maxHold = Math.Max(0, Math.Min((cycle - 2 * minGap) / 2, window - minGap));
        double hold = double.IsNaN(holdMs) ? 0 : Math.Clamp(holdMs, 0, maxHold);

        // Split the up time evenly between the in-pair gap and the rest after the pair, so the pair never touches the next cycle.
        double gap = Math.Max(minGap, Math.Min(window - hold, (cycle - 2 * hold) / 2));
        return new DoubleClickPlan(hold, gap, hold);
    }
}
