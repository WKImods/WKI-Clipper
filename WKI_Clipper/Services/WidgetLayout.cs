using System;

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
}
