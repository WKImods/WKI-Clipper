using System;
using System.Collections.Generic;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>
/// Pure geometry of the shortcut bar: where it sits on the board's monitor and which widgets it pushes down. Free of WPF types,
/// like <see cref="WidgetLayout"/>; the host feeds in real work-area rectangles and applies the results.
/// </summary>
public static class BarLayout
{
    /// <summary>Distance from the top of the work area, the bar's height, and the margin kept to the left and right edge (DIPs).</summary>
    public const double TopMarginDip = 8, HeightDip = 56, SideMarginDip = 8;

    /// <summary>
    /// The bar's rectangle in physical pixels: centred on the monitor's work area, <see cref="TopMarginDip"/> from its top,
    /// <see cref="HeightDip"/> high. <paramref name="barWidth"/> is the content width in DIPs; a bar wider than the work area
    /// is cut to it (minus the side margins). Whole pixels, as SetWindowPos applies them.
    /// <paramref name="leftLimitPx"/> is the x (physical pixels) the bar must not reach to the left of - the right edge of the board's
    /// sidebar plus a gap: a bar wide enough to run into the sidebar slides right (and is cut at the right margin, the content then
    /// scrolls) instead of covering the sidebar's header and its fold button. A narrower bar stays centred.
    /// </summary>
    public static LayoutRect BarRect(LayoutRect monitorWorkArea, double barWidth, double scale, double leftLimitPx = double.NegativeInfinity)
    {
        if (scale <= 0) scale = 1;
        double maxWidth = Math.Max(0, monitorWorkArea.W - 2 * SideMarginDip * scale);
        double width = Math.Min(Math.Max(0, barWidth) * scale, maxWidth);
        double x = monitorWorkArea.X + (monitorWorkArea.W - width) / 2;
        if (x < leftLimitPx)
        {
            x = leftLimitPx;
            width = Math.Max(0, Math.Min(width, monitorWorkArea.Right - SideMarginDip * scale - x));
        }
        double y = monitorWorkArea.Y + TopMarginDip * scale;
        return new LayoutRect(Math.Round(x), Math.Round(y), Math.Round(width), Math.Round(HeightDip * scale));
    }

    /// <summary>
    /// The widgets that really intersect the bar, moved straight down to one <paramref name="gap"/> below it (X and size stay).
    /// Widgets beside, below or merely touching the bar are not listed. Applying the result and asking again gives nothing:
    /// a pushed widget does not intersect any more.
    /// </summary>
    public static IReadOnlyList<(WidgetId Id, LayoutRect NewRect)> PushBelow(
        LayoutRect bar, IReadOnlyList<(WidgetId Id, LayoutRect Rect)> widgets, double gap)
    {
        var moves = new List<(WidgetId Id, LayoutRect NewRect)>();
        foreach (var (id, rect) in widgets)
        {
            if (!rect.Overlaps(bar)) continue;
            moves.Add((id, rect.At(rect.X, Math.Round(bar.Bottom + gap))));
        }
        return moves;
    }
}
