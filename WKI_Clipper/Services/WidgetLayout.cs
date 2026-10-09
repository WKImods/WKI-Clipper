using System;
using System.Collections.Generic;

namespace WKI_Clipper.Services;

/// <summary>
/// Pure geometry helpers for widget placement. Kept free of any WPF/Screen types so
/// it is unit-testable; the host feeds in real monitor work-area bounds.
/// </summary>
public static class WidgetLayout
{
    /// <summary>
    /// Clamps a widget rectangle so it stays visible inside the given work area.
    /// If the widget is larger than the area it is pinned to the top-left corner and
    /// left oversized (the resize grip lets the user shrink it). A margin keeps the
    /// title bar reachable.
    /// </summary>
    public static (double X, double Y) Clamp(
        double x, double y, double width, double height,
        double areaLeft, double areaTop, double areaRight, double areaBottom,
        double margin = 8)
    {
        double maxX = areaRight - width - margin;
        double maxY = areaBottom - height - margin;
        double minX = areaLeft + margin;
        double minY = areaTop + margin;

        // When the widget is wider/taller than the area, min > max — prefer the
        // top-left so the title bar (and thus drag/resize) stays on screen.
        double cx = maxX < minX ? minX : Math.Min(Math.Max(x, minX), maxX);
        double cy = maxY < minY ? minY : Math.Min(Math.Max(y, minY), maxY);
        return (cx, cy);
    }

    /// <summary>
    /// Snaps a point to a grid ANCHORED AT (anchorX, anchorY) — used for the crosshair,
    /// where the grid origin is the monitor center. Anchoring there (instead of at 0,0)
    /// guarantees the exact center is always a valid snap position and that offsets left
    /// and right of it stay symmetric. A grid &lt;= 0 returns the point unchanged.
    /// </summary>
    public static (double X, double Y) SnapToGrid(double x, double y, double anchorX, double anchorY, double grid)
    {
        if (grid <= 0) return (x, y);
        double sx = anchorX + Math.Round((x - anchorX) / grid) * grid;
        double sy = anchorY + Math.Round((y - anchorY) / grid) * grid;
        return (sx, sy);
    }

    /// <summary>
    /// True if the rectangle has a real, on-area size. Used to decide whether a
    /// stored geometry is usable or the widget should fall back to a staggered
    /// default position.
    /// </summary>
    public static bool HasValidGeometry(double width, double height)
        => width >= 120 && height >= 80 && !double.IsNaN(width) && !double.IsNaN(height);

    // ---- Scale-independent placement ----------------------------------------------
    //
    // A widget's place is stored as the position of its CENTER relative to the monitor's
    // work area (0..1), its size in DIPs. Changing Windows' display scaling (100 → 150 %)
    // changes how many DIPs fit on the screen, but "top right corner" stays top right:
    // the center fraction is re-applied and the grown window clamped back on screen.

    /// <summary>Center of <paramref name="r"/> as a fraction of <paramref name="area"/>.</summary>
    public static (double RelX, double RelY) ToRelative(LayoutRect r, LayoutRect area)
        => (area.W > 0 ? (r.X + r.W / 2 - area.X) / area.W : 0.5,
            area.H > 0 ? (r.Y + r.H / 2 - area.Y) / area.H : 0.5);

    /// <summary>
    /// Top-left for a window of the given size whose center sits at the relative point,
    /// clamped into the area (keeping <paramref name="margin"/> to the edges).
    /// </summary>
    public static (double X, double Y) FromRelative(double relX, double relY, double width, double height,
                                                    LayoutRect area, double margin)
    {
        double x = area.X + Math.Clamp(relX, 0, 1) * area.W - width / 2;
        double y = area.Y + Math.Clamp(relY, 0, 1) * area.H - height / 2;
        return Clamp(x, y, width, height, area.X, area.Y, area.Right, area.Bottom, margin);
    }

    // ---- Grid & snapping ----------------------------------------------------------
    //
    // The grid has gutters: window STARTS sit on o + k·grid, window ENDS on o + k·grid − gap
    // (o = area start + gap). Two grid-aligned neighbours are therefore always exactly one
    // gap apart, and a window snapped next to another lands on the grid again.

    /// <summary>
    /// Live snapping while a window is dragged: an edge within <paramref name="threshold"/>
    /// of a neighbour's edge (aligned, or one gap beside it) or of the screen edge wins;
    /// otherwise the window moves in grid steps. Size is unchanged.
    /// </summary>
    public static (double X, double Y) SnapMove(LayoutRect r, IReadOnlyList<LayoutRect> others, LayoutRect area,
                                               double grid, double threshold, double gap)
    {
        double x = SnapStart(r.X, r.W, area.X, area.Right, others, horizontal: true, grid, threshold, gap);
        double y = SnapStart(r.Y, r.H, area.Y, area.Bottom, others, horizontal: false, grid, threshold, gap);
        return (x, y);
    }

    private static double SnapStart(double start, double size, double areaStart, double areaEnd,
                                    IReadOnlyList<LayoutRect> others, bool horizontal,
                                    double grid, double threshold, double gap)
    {
        double best = double.NaN, bestDist = double.MaxValue;
        void Consider(double candidate)
        {
            double d = Math.Abs(candidate - start);
            if (d <= threshold && d < bestDist) { best = candidate; bestDist = d; }
        }

        Consider(areaStart + gap);
        Consider(areaEnd - gap - size);
        foreach (var o in others)
        {
            double os = horizontal ? o.X : o.Y, oe = horizontal ? o.Right : o.Bottom;
            Consider(os);              // starts aligned
            Consider(oe - size);       // ends aligned
            Consider(oe + gap);        // right after it
            Consider(os - gap - size); // right before it
        }
        if (!double.IsNaN(best)) return best;
        if (grid <= 0) return start;
        double origin = areaStart + gap;
        return origin + Math.Round((start - origin) / grid) * grid;
    }

    /// <summary>
    /// Snapping while a window is resized from its bottom-right corner: the right and
    /// bottom edges stick to neighbours and the screen edge, else to the grid's end lines.
    /// Returns the new size (never below the minimum).
    /// </summary>
    public static (double W, double H) SnapResize(LayoutRect r, IReadOnlyList<LayoutRect> others, LayoutRect area,
                                                 double grid, double threshold, double gap, double minW, double minH)
    {
        double right = SnapEnd(r.Right, area.X, area.Right, others, horizontal: true, grid, threshold, gap);
        double bottom = SnapEnd(r.Bottom, area.Y, area.Bottom, others, horizontal: false, grid, threshold, gap);
        return (Math.Max(minW, right - r.X), Math.Max(minH, bottom - r.Y));
    }

    private static double SnapEnd(double end, double areaStart, double areaEnd,
                                  IReadOnlyList<LayoutRect> others, bool horizontal,
                                  double grid, double threshold, double gap)
    {
        double best = double.NaN, bestDist = double.MaxValue;
        void Consider(double candidate)
        {
            double d = Math.Abs(candidate - end);
            if (d <= threshold && d < bestDist) { best = candidate; bestDist = d; }
        }

        Consider(areaEnd - gap);
        foreach (var o in others)
        {
            double os = horizontal ? o.X : o.Y, oe = horizontal ? o.Right : o.Bottom;
            Consider(os - gap);   // stop one gap before it
            Consider(oe);         // ends aligned
        }
        if (!double.IsNaN(best)) return best;
        if (grid <= 0) return end;
        double origin = areaStart + gap;
        return origin + Math.Round((end + gap - origin) / grid) * grid - gap;
    }

    /// <summary>Snaps a length so a grid-aligned window of it ends on a grid end line.</summary>
    public static double SnapLength(double length, double grid, double gap, double min)
    {
        if (grid <= 0) return length;
        double cells = Math.Max(1, Math.Round((length + gap) / grid));
        return Math.Max(min, cells * grid - gap);
    }

    /// <summary>
    /// The free position closest to where <paramref name="desired"/> is, at least
    /// <paramref name="gap"/> away from every obstacle and inside the area. Returns the
    /// desired spot itself when it is already free; null when the window cannot fit
    /// anywhere (the caller then keeps it where it is).
    ///
    /// For axis-aligned rectangles the closest free spot always has each coordinate
    /// either unchanged, on a screen edge, or flush (one gap) against an obstacle edge —
    /// so trying exactly those combinations is complete, and cheap for a dozen widgets.
    /// </summary>
    public static (double X, double Y)? FindFreeSpot(LayoutRect desired, IReadOnlyList<LayoutRect> obstacles,
                                                     LayoutRect area, double gap)
    {
        double minX = area.X + gap, maxX = area.Right - gap - desired.W;
        double minY = area.Y + gap, maxY = area.Bottom - gap - desired.H;
        if (maxX < minX || maxY < minY) return null;

        var xs = new List<double> { Math.Clamp(desired.X, minX, maxX), minX, maxX };
        var ys = new List<double> { Math.Clamp(desired.Y, minY, maxY), minY, maxY };
        foreach (var o in obstacles)
        {
            xs.Add(o.Right + gap); xs.Add(o.X - gap - desired.W);
            ys.Add(o.Bottom + gap); ys.Add(o.Y - gap - desired.H);
        }

        (double X, double Y)? best = null;
        double bestDist = double.MaxValue;
        foreach (var x in xs)
        {
            if (x < minX - 0.01 || x > maxX + 0.01) continue;
            foreach (var y in ys)
            {
                if (y < minY - 0.01 || y > maxY + 0.01) continue;
                double d = (x - desired.X) * (x - desired.X) + (y - desired.Y) * (y - desired.Y);
                if (d >= bestDist) continue;
                var candidate = desired.At(x, y);
                bool blocked = false;
                foreach (var o in obstacles)
                    if (candidate.Overlaps(o, gap)) { blocked = true; break; }
                if (blocked) continue;
                best = (x, y);
                bestDist = d;
            }
        }
        return best;
    }

    /// <summary>
    /// Tidies a set of windows in place: top-left ones keep their spot, every following
    /// one moves to the nearest free spot next to them. Nothing overlaps afterwards
    /// (unless the screen is simply too full). With <paramref name="snapToGrid"/> starts
    /// and sizes are aligned to the grid first. <paramref name="fixedObstacles"/> stay put
    /// and are avoided (e.g. the window the user just resized, or the launcher).
    /// With <paramref name="resolveOverlaps"/> off (stacking allowed) windows are only
    /// grid-aligned and kept on screen; overlaps stay as the user built them.
    /// </summary>
    public static LayoutRect[] Arrange(IReadOnlyList<LayoutRect> windows, LayoutRect area, double grid, double gap,
                                       double minW, double minH, bool snapToGrid,
                                       IReadOnlyList<LayoutRect>? fixedObstacles = null,
                                       bool resolveOverlaps = true)
    {
        var result = new LayoutRect[windows.Count];
        var placed = new List<LayoutRect>(fixedObstacles ?? Array.Empty<LayoutRect>());

        var order = new List<int>();
        for (int i = 0; i < windows.Count; i++) order.Add(i);
        order.Sort((a, b) =>
        {
            int c = windows[a].Y.CompareTo(windows[b].Y);
            return c != 0 ? c : windows[a].X.CompareTo(windows[b].X);
        });

        foreach (int i in order)
        {
            var r = windows[i];
            if (snapToGrid && grid > 0)
            {
                double w = Math.Min(SnapLength(r.W, grid, gap, minW), area.W - 2 * gap);
                double h = Math.Min(SnapLength(r.H, grid, gap, minH), area.H - 2 * gap);
                double origin = area.X + gap, originY = area.Y + gap;
                r = new LayoutRect(origin + Math.Round((r.X - origin) / grid) * grid,
                                   originY + Math.Round((r.Y - originY) / grid) * grid, w, h);
            }
            var (cx, cy) = Clamp(r.X, r.Y, r.W, r.H, area.X, area.Y, area.Right, area.Bottom, gap);
            r = r.At(cx, cy);
            if (resolveOverlaps && FindFreeSpot(r, placed, area, gap) is { } spot) r = r.At(spot.X, spot.Y);
            result[i] = r;
            placed.Add(r);
        }
        return result;
    }
}

/// <summary>Axis-aligned rectangle in one consistent unit (the host uses physical pixels).</summary>
public readonly record struct LayoutRect(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;

    public LayoutRect At(double x, double y) => this with { X = x, Y = y };

    /// <summary>True when the two are closer than <paramref name="gap"/> (touching at exactly one gap is fine).</summary>
    public bool Overlaps(LayoutRect o, double gap = 0)
    {
        const double eps = 0.5;
        return X < o.Right + gap - eps && o.X < Right + gap - eps
            && Y < o.Bottom + gap - eps && o.Y < Bottom + gap - eps;
    }
}
