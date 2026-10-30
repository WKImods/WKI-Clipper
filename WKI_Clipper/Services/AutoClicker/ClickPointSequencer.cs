using System;
using System.Collections.Generic;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>
/// Where the next click cycle goes. <see cref="FirstOfVisit"/> is the first cycle after arriving at the point; <see cref="Final"/> is
/// the last cycle of a route that stops when complete (the engine clicks it and then ends the run with "all click points done").
/// </summary>
public readonly record struct ClickTarget(int X, int Y, int PointIndex, bool FirstOfVisit, bool Final);

/// <summary>The random offset of a click-point click (design section 4).</summary>
public static class ClickPointJitter
{
    /// <summary>Chance that a jittered click gets an extra offset on top of the radius.</summary>
    public const double ExtraChance = 0.216;

    /// <summary>Size of that extra offset in pixels.</summary>
    public const double ExtraMaxPx = 2;

    /// <summary>
    /// The offset of one click: uniform inside the circle of <paramref name="radius"/>, and with <see cref="ExtraChance"/> a further
    /// offset of up to <see cref="ExtraMaxPx"/>. The extra offset applies at radius 0 as well.
    /// </summary>
    public static void Offset(Randomizer rng, int radius, out int dx, out int dy)
    {
        dx = 0;
        dy = 0;

        double x = 0, y = 0;
        if (radius > 0)
        {
            double r = radius * Math.Sqrt(rng.NextDouble());
            double angle = 2.0 * Math.PI * rng.NextDouble();
            x = r * Math.Cos(angle);
            y = r * Math.Sin(angle);
        }
        if (TryExtraOffset(rng, out double ex, out double ey))
        {
            x += ex;
            y += ey;
        }
        dx = (int)Math.Round(x);
        dy = (int)Math.Round(y);
    }

    /// <summary>With <see cref="ExtraChance"/>: a random offset of up to <see cref="ExtraMaxPx"/> in a random direction (uniform over the small disc).</summary>
    public static bool TryExtraOffset(Randomizer rng, out double dx, out double dy)
    {
        dx = 0;
        dy = 0;
        if (!rng.Chance(ExtraChance)) return false;
        double r = ExtraMaxPx * Math.Sqrt(rng.NextDouble());
        double angle = 2.0 * Math.PI * rng.NextDouble();
        dx = r * Math.Cos(angle);
        dy = r * Math.Sin(angle);
        return true;
    }
}

/// <summary>
/// The click-point route (design section 4): points in list order, each clicked its <c>Clicks</c> times (every click with its own jitter),
/// then the next point; the route loops, or with <c>StopWhenComplete</c> ends after the last click of the last point. Build a new one
/// when the point list changes.
/// </summary>
public sealed class ClickPointSequencer
{
    private readonly IReadOnlyList<ClickPoint> _points;
    private readonly bool _stopWhenComplete;
    private readonly Randomizer _rng;

    private int _index;
    private int _doneAtPoint;
    private bool _complete;

    public ClickPointSequencer(IReadOnlyList<ClickPoint> points, bool stopWhenComplete, Randomizer rng)
    {
        _points = points ?? Array.Empty<ClickPoint>();
        _stopWhenComplete = stopWhenComplete;
        _rng = rng;
    }

    public bool HasPoints => _points.Count > 0;

    /// <summary>The route finished (only with stop-when-complete); <see cref="TryNext"/> returns false from now on.</summary>
    public bool IsComplete => _complete;

    /// <summary>Index of the point the next cycle goes to.</summary>
    public int CurrentIndex => _index;

    /// <summary>
    /// Clicks still due at the current point. A double-click cycle that has only one click left should be sent as a single click, so
    /// the point gets exactly the count it asked for.
    /// </summary>
    public int RemainingAtPoint => _points.Count == 0 ? 0 : Math.Max(1, _points[_index].Clicks) - _doneAtPoint;

    /// <summary>
    /// The target of the next click cycle of <paramref name="clicksInCycle"/> clicks (1, or 2 for a double click), jittered, and counts
    /// those clicks toward the point. A cycle that overshoots the point's count simply moves the route on. False when there are no
    /// points or the route is complete.
    /// </summary>
    public bool TryNext(int clicksInCycle, out ClickTarget target)
    {
        target = default;
        if (_points.Count == 0 || _complete) return false;

        var point = _points[_index];
        ClickPointJitter.Offset(_rng, point.Radius, out int dx, out int dy);
        bool first = _doneAtPoint == 0;
        int pointIndex = _index;

        _doneAtPoint += Math.Max(1, clicksInCycle);
        bool final = false;
        if (_doneAtPoint >= Math.Max(1, point.Clicks))
        {
            _doneAtPoint = 0;
            _index++;
            if (_index >= _points.Count)
            {
                _index = 0;
                if (_stopWhenComplete)
                {
                    _complete = true;
                    final = true;
                }
            }
        }

        target = new ClickTarget(point.X + dx, point.Y + dy, pointIndex, first, final);
        return true;
    }

    /// <summary>Starts the route again from the first point.</summary>
    public void Reset()
    {
        _index = 0;
        _doneAtPoint = 0;
        _complete = false;
    }
}
