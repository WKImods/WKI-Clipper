using System;
using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>
/// A monitor as the overlay sees it: its device name (<c>\\.\DISPLAY1</c>), the area the overlay may use and whether it
/// is the primary one. The host passes the monitor's full bounds - in a fullscreen game that IS the screen.
/// </summary>
public sealed record MonitorBox(string Device, LayoutRect WorkArea, bool Primary);

/// <summary>
/// Where the performance overlay sits: eight fixed anchors (corners and edge middles) on one monitor, or a free spot
/// stored relative to one edge of one monitor. Pure maths in one unit - the host passes physical pixels, with the DIP
/// margin and snap distance already multiplied by the display scale. Free of WPF so it can be unit-tested.
/// </summary>
public static class PerfOverlayPlacement
{
    /// <summary>Gap between a fixed anchor and the monitor edge, in DIPs.</summary>
    public const double MarginDip = 10;

    /// <summary>A dropped overlay within this distance (DIPs) of a fixed anchor snaps to it.</summary>
    public const double SnapDip = 24;

    private static readonly OverlayAnchor[] Fixed =
    {
        OverlayAnchor.TopLeft, OverlayAnchor.TopCenter, OverlayAnchor.TopRight,
        OverlayAnchor.MiddleLeft, OverlayAnchor.MiddleRight,
        OverlayAnchor.BottomLeft, OverlayAnchor.BottomCenter, OverlayAnchor.BottomRight,
    };

    /// <summary>The monitor the overlay is on: a free place's own monitor, else the preferred one - and the primary when that one is gone.</summary>
    public static MonitorBox? ResolveMonitor(OverlayAnchor anchor, OverlayFreePlace? free, IReadOnlyList<MonitorBox> monitors, string? preferredMonitor)
    {
        if (monitors.Count == 0) return null;
        string? want = anchor == OverlayAnchor.Free && free is not null ? free.Monitor : preferredMonitor;
        if (!string.IsNullOrEmpty(want))
        {
            var named = monitors.FirstOrDefault(m => string.Equals(m.Device, want, StringComparison.OrdinalIgnoreCase));
            if (named is not null) return named;
        }
        return monitors.FirstOrDefault(m => m.Primary) ?? monitors[0];
    }

    /// <summary>
    /// The overlay's rectangle for the anchor. A free place keeps the edge it was stored against when the overlay grows or
    /// shrinks. The result always lies inside the monitor - if the overlay is larger than the monitor, its top-left stays visible.
    /// </summary>
    public static LayoutRect Place(OverlayAnchor anchor, OverlayFreePlace? free, double w, double h,
                                   IReadOnlyList<MonitorBox> monitors, string? preferredMonitor, double margin)
    {
        var monitor = ResolveMonitor(anchor, free, monitors, preferredMonitor);
        if (monitor is null) return new LayoutRect(0, 0, w, h);
        var area = monitor.WorkArea;

        double x, y;
        if (anchor == OverlayAnchor.Free && free is not null)
        {
            x = FreeAxis(free.HEdge, free.RelDX, area.X, area.W, w);
            y = FreeAxis(free.VEdge, free.RelDY, area.Y, area.H, h);
        }
        else
        {
            var (hEdge, vEdge) = EdgesOf(anchor);
            x = hEdge switch { 0 => area.X + margin, 1 => area.X + (area.W - w) / 2, _ => area.Right - margin - w };
            y = vEdge switch { 0 => area.Y + margin, 1 => area.Y + (area.H - h) / 2, _ => area.Bottom - margin - h };
        }
        return Clamp(new LayoutRect(x, y, w, h), area);
    }

    /// <summary>
    /// What a finished drag means: within <paramref name="snap"/> of a fixed anchor -> that anchor (the nearest one);
    /// otherwise a free place on <paramref name="monitor"/>, measured from the thirds of the monitor the overlay's centre is in.
    /// </summary>
    public static (OverlayAnchor Anchor, OverlayFreePlace? Free) FromDrop(LayoutRect dropped, MonitorBox monitor, double snap, double margin)
    {
        OverlayAnchor? best = null;
        double bestDistance = double.MaxValue;
        var only = new[] { monitor };
        foreach (var a in Fixed)
        {
            var at = Place(a, null, dropped.W, dropped.H, only, monitor.Device, margin);
            double distance = Math.Sqrt((dropped.X - at.X) * (dropped.X - at.X) + (dropped.Y - at.Y) * (dropped.Y - at.Y));
            if (distance <= snap && distance < bestDistance) { best = a; bestDistance = distance; }
        }
        if (best is { } snapped) return (snapped, null);
        return (OverlayAnchor.Free, FreeFrom(dropped, monitor));
    }

    /// <summary>The free place that puts an overlay of this size exactly at <paramref name="rect"/> (kept inside the monitor).</summary>
    public static OverlayFreePlace FreeFrom(LayoutRect rect, MonitorBox monitor)
    {
        var area = monitor.WorkArea;
        rect = Clamp(rect, area);
        double cx = rect.X + rect.W / 2, cy = rect.Y + rect.H / 2;
        int hEdge = cx < area.X + area.W / 3 ? 0 : cx < area.X + area.W * 2 / 3 ? 1 : 2;
        int vEdge = cy < area.Y + area.H / 3 ? 0 : cy < area.Y + area.H * 2 / 3 ? 1 : 2;

        return new OverlayFreePlace
        {
            Monitor = monitor.Device,
            HEdge = hEdge,
            VEdge = vEdge,
            RelDX = hEdge switch { 0 => rect.X - area.X, 1 => cx - (area.X + area.W / 2), _ => area.Right - rect.Right } / area.W,
            RelDY = vEdge switch { 0 => rect.Y - area.Y, 1 => cy - (area.Y + area.H / 2), _ => area.Bottom - rect.Bottom } / area.H,
        };
    }

    /// <summary>The monitor holding the centre of <paramref name="rect"/>; when it is on none (stale rectangle, a gap in the layout), the nearest one.</summary>
    public static MonitorBox? MonitorAt(LayoutRect rect, IReadOnlyList<MonitorBox> monitors)
    {
        double cx = rect.X + rect.W / 2, cy = rect.Y + rect.H / 2;
        MonitorBox? nearest = null;
        double nearestDistance = double.MaxValue;
        foreach (var m in monitors)
        {
            var a = m.WorkArea;
            double dx = Math.Max(Math.Max(a.X - cx, 0), cx - a.Right);
            double dy = Math.Max(Math.Max(a.Y - cy, 0), cy - a.Bottom);
            double distance = dx * dx + dy * dy;
            if (distance < nearestDistance) { nearest = m; nearestDistance = distance; }
        }
        return nearest;
    }

    /// <summary>
    /// The overlay can be grabbed only while the board is open. Closed, it is click-through again, so it can never
    /// intercept a click meant for the game.
    /// </summary>
    public static bool DragAllowed(bool boardOpen) => boardOpen;

    /// <summary>Takes a finished drag into the settings: a fixed anchor (and no free place) or <see cref="OverlayAnchor.Free"/> with its place.</summary>
    public static void ApplyDrop(PerfOverlaySettings s, LayoutRect dropped, IReadOnlyList<MonitorBox> monitors, double snap, double margin)
    {
        var monitor = MonitorAt(dropped, monitors);
        if (monitor is null) return;
        (s.Anchor, s.Free) = FromDrop(dropped, monitor, snap, margin);
    }

    /// <summary>The user picked a place from the list. A fixed anchor forgets the old free place; "Free" keeps a stored one.</summary>
    public static void SelectAnchor(PerfOverlaySettings s, OverlayAnchor anchor)
    {
        s.Anchor = anchor;
        if (anchor != OverlayAnchor.Free) s.Free = null;
    }

    // ---- internals ----

    /// <summary>(horizontal, vertical) edge of a fixed anchor: 0 left/top, 1 centre/middle, 2 right/bottom. Anything unknown is top right.</summary>
    private static (int H, int V) EdgesOf(OverlayAnchor a) => a switch
    {
        OverlayAnchor.TopLeft => (0, 0),
        OverlayAnchor.TopCenter => (1, 0),
        OverlayAnchor.TopRight => (2, 0),
        OverlayAnchor.MiddleLeft => (0, 1),
        OverlayAnchor.MiddleRight => (2, 1),
        OverlayAnchor.BottomLeft => (0, 2),
        OverlayAnchor.BottomCenter => (1, 2),
        OverlayAnchor.BottomRight => (2, 2),
        _ => (2, 0),
    };

    /// <summary>Start of the overlay on one axis for a stored edge and relative distance.</summary>
    private static double FreeAxis(int edge, double rel, double start, double size, double length)
    {
        double d = (double.IsFinite(rel) ? rel : 0) * size;
        return edge switch
        {
            <= 0 => start + d,
            1 => start + size / 2 + d - length / 2,
            _ => start + size - d - length,
        };
    }

    /// <summary>Moves the rectangle fully inside the area; where it is larger than the area, it starts at the area's start.</summary>
    private static LayoutRect Clamp(LayoutRect r, LayoutRect area)
    {
        double x = r.W >= area.W ? area.X : Math.Clamp(r.X, area.X, area.Right - r.W);
        double y = r.H >= area.H ? area.Y : Math.Clamp(r.Y, area.Y, area.Bottom - r.H);
        return r.At(x, y);
    }
}
