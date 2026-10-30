using System;
using System.Collections.Generic;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>One step of a cursor move: the position and when to place it, measured from the start of the move.</summary>
public readonly record struct PathPoint(int X, int Y, double AtMs);

/// <summary>
/// The cursor move between click points (design section 4): below 50 clicks per second the cursor glides along a quadratic Bezier curve
/// with quad ease-in-out timing; from 50 per second it jumps. Positions are absolute virtual-desktop pixels (negative coordinates ok).
/// </summary>
public static class CursorPath
{
    /// <summary>From this rate on the cursor jumps instead of gliding.</summary>
    public const double JumpFromCps = 50;

    /// <summary>The move takes this fraction of the cycle interval, drawn uniformly between the two ...</summary>
    public const double MinMoveFraction = 0.2;
    public const double MaxMoveFraction = 0.6;

    /// <summary>... but never longer than this.</summary>
    public const double MaxMoveMs = 200;

    /// <summary>Time between two path positions.</summary>
    public const double StepMs = 4;

    /// <summary>Upper bound on the number of positions of one move.</summary>
    public const int MaxSteps = 100;

    /// <summary>How far the curve may bend away from the straight line, as a fraction of the distance (control point offset).</summary>
    public const double MaxCurve = 0.15;

    public static bool ShouldJump(double cps) => cps >= JumpFromCps - 1e-9;

    /// <summary>How long the glide to the next point takes: 20-60 % of the cycle interval, at most 200 ms.</summary>
    public static double MoveDurationMs(double intervalMs, Randomizer rng)
        => Math.Min(MaxMoveMs, intervalMs * rng.NextRange(MinMoveFraction, MaxMoveFraction));

    /// <summary>Quad ease-in-out of t in [0, 1] (values outside are clamped).</summary>
    public static double Ease(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        return t < 0.5 ? 2 * t * t : 1 - (-2 * t + 2) * (-2 * t + 2) / 2;
    }

    public static List<PathPoint> Bezier(PixelPoint from, PixelPoint to, double durationMs, Randomizer? rng = null)
    {
        var result = new List<PathPoint>();
        Bezier(from, to, durationMs, rng, result);
        return result;
    }

    /// <summary>
    /// Fills <paramref name="into"/> (cleared first; reuse one list to avoid allocations) with the positions after <paramref name="from"/>:
    /// every <see cref="StepMs"/> a point on the eased curve, positions equal to the previous one skipped, the last one exactly
    /// <paramref name="to"/> at <paramref name="durationMs"/>. With a <paramref name="rng"/> the curve bends by up to
    /// <see cref="MaxCurve"/> of the distance to either side; without, the path is a straight eased line. No distance or no time gives
    /// just the target at 0 ms.
    /// </summary>
    public static void Bezier(PixelPoint from, PixelPoint to, double durationMs, Randomizer? rng, List<PathPoint> into)
    {
        into.Clear();
        double dx = to.X - (double)from.X, dy = to.Y - (double)from.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (!(durationMs > 0) || length < 0.5)
        {
            into.Add(new PathPoint(to.X, to.Y, 0));
            return;
        }

        double curve = rng is null ? 0 : rng.NextRange(-MaxCurve, MaxCurve);
        double cx = (from.X + to.X) / 2.0 - dy / length * curve * length;
        double cy = (from.Y + to.Y) / 2.0 + dx / length * curve * length;

        int steps = Math.Clamp((int)Math.Ceiling(durationMs / StepMs), 1, MaxSteps);
        int lastX = from.X, lastY = from.Y;
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            if (i == steps)
            {
                // The target is placed once, at the end: an earlier step that already rounded onto it makes way.
                if (into.Count > 0 && into[^1].X == to.X && into[^1].Y == to.Y) into.RemoveAt(into.Count - 1);
                into.Add(new PathPoint(to.X, to.Y, durationMs));
                break;
            }
            double e = Ease(t);
            double u = 1 - e;
            int x = (int)Math.Round(u * u * from.X + 2 * u * e * cx + e * e * to.X);
            int y = (int)Math.Round(u * u * from.Y + 2 * u * e * cy + e * e * to.Y);
            if (x == lastX && y == lastY) continue;
            lastX = x;
            lastY = y;
            into.Add(new PathPoint(x, y, durationMs * t));
        }
    }
}
