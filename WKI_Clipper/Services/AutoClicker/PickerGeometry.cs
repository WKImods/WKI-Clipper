using System;
using System.Collections.Generic;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>A position in a picker window's device-independent pixels, relative to the window's top-left.</summary>
public readonly record struct DipPoint(double X, double Y);

public readonly record struct DipSize(double W, double H);

public readonly record struct DipRect(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
}

/// <summary>One picker window's monitor: its bounds in virtual-screen physical pixels and its display scale (1.0 = 100 %).</summary>
public readonly record struct PickerMonitor(PixelRect Bounds, double Scale);

/// <summary>
/// The arithmetic behind the click-point and zone pickers (design section 8). Settings and the host speak virtual-screen PHYSICAL pixels,
/// each per-monitor-aware picker window lays out in its own monitor's DIPs. Everything here is pure, so the mapping is tested without
/// windows; the windows only feed in their monitor and scale.
/// </summary>
public static class PickerGeometry
{
    /// <summary>A drag narrower or lower than this (physical px) is treated as a stray click, not as a zone.</summary>
    public const int MinZoneSizePx = 4;

    /// <summary>Gap (DIP) between a fresh zone and its Stop/Pause/Start chooser, and between the chooser and the window edge.</summary>
    public const double ChooserMargin = 8;

    /// <summary>NaN, infinite, zero or negative scales (a failed DPI query) count as 100 %.</summary>
    public static double SafeScale(double scale) => double.IsFinite(scale) && scale > 0 ? scale : 1.0;

    public static DipSize WindowSize(PickerMonitor m)
    {
        double s = SafeScale(m.Scale);
        return new DipSize(m.Bounds.W / s, m.Bounds.H / s);
    }

    /// <summary>The centre of physical pixel <paramref name="p"/> in the monitor window's DIPs (markers sit on the pixel's middle).</summary>
    public static DipPoint ToDip(PickerMonitor m, PixelPoint p)
    {
        double s = SafeScale(m.Scale);
        return new DipPoint((p.X - m.Bounds.X + 0.5) / s, (p.Y - m.Bounds.Y + 0.5) / s);
    }

    /// <summary>A physical rectangle in the monitor window's DIPs (edges, not pixel centres).</summary>
    public static DipRect ToDip(PickerMonitor m, PixelRect r)
    {
        double s = SafeScale(m.Scale);
        return new DipRect((r.X - m.Bounds.X) / s, (r.Y - m.Bounds.Y) / s, r.W / s, r.H / s);
    }

    /// <summary>
    /// The physical pixel under a window-local DIP position. Positions beyond the window (mouse capture while dragging) map linearly;
    /// the result is clamped to the settings' coordinate range.
    /// </summary>
    public static PixelPoint ToPhysical(PickerMonitor m, DipPoint d)
    {
        double s = SafeScale(m.Scale);
        return Clamp(new PixelPoint(Floor(m.Bounds.X + d.X * s), Floor(m.Bounds.Y + d.Y * s)));
    }

    /// <summary>Index of the monitor containing <paramref name="p"/>, or -1 (a gap between monitors of different sizes).</summary>
    public static int MonitorIndexAt(IReadOnlyList<PickerMonitor> monitors, PixelPoint p)
    {
        for (int i = 0; i < monitors.Count; i++)
            if (monitors[i].Bounds.Contains(p)) return i;
        return -1;
    }

    /// <summary>The overlap of two rectangles, or null when they only touch or are apart.</summary>
    public static PixelRect? Intersect(PixelRect a, PixelRect b)
    {
        int x = Math.Max(a.X, b.X), y = Math.Max(a.Y, b.Y);
        int right = Math.Min(a.Right, b.Right), bottom = Math.Min(a.Bottom, b.Bottom);
        return right > x && bottom > y ? new PixelRect(x, y, right - x, bottom - y) : null;
    }

    /// <summary>The rectangle a drag covers so far, both end pixels included; at least 1×1 so the preview never vanishes.</summary>
    public static PixelRect DragRect(PixelPoint a, PixelPoint b)
    {
        a = Clamp(a);
        b = Clamp(b);
        int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return new PixelRect(x, y, Math.Abs(a.X - b.X) + 1, Math.Abs(a.Y - b.Y) + 1);
    }

    /// <summary>The zone a finished drag describes, or null when it is smaller than <see cref="MinZoneSizePx"/> in either direction.</summary>
    public static PixelRect? ZoneFromDrag(PixelPoint a, PixelPoint b)
    {
        var r = DragRect(a, b);
        return r.W >= MinZoneSizePx && r.H >= MinZoneSizePx ? r : null;
    }

    /// <summary>
    /// Where the inline Stop/Pause/Start chooser goes for a zone drawn at <paramref name="zone"/> (window DIPs): below it, else above it,
    /// else just inside its top; always fully inside the window.
    /// </summary>
    public static DipPoint ChooserPosition(DipRect zone, DipSize chooser, DipSize window)
    {
        double m = ChooserMargin;
        double maxX = Math.Max(m, window.W - chooser.W - m);
        double maxY = Math.Max(m, window.H - chooser.H - m);
        double x = Math.Clamp(zone.X, m, maxX);

        double below = zone.Bottom + m;
        if (below + chooser.H <= window.H - m) return new DipPoint(x, below);
        double above = zone.Y - m - chooser.H;
        if (above >= m) return new DipPoint(x, above);
        return new DipPoint(x, Math.Clamp(zone.Y + m, m, maxY));
    }

    private static int Floor(double v) => (int)Math.Clamp(Math.Floor(v), int.MinValue, int.MaxValue);

    private static PixelPoint Clamp(PixelPoint p)
    {
        int lim = ClickerSettingsRules.CoordinateLimit;
        return new PixelPoint(Math.Clamp(p.X, -lim, lim), Math.Clamp(p.Y, -lim, lim));
    }
}

/// <summary>
/// The list edits a picker makes. The pickers work on copies (<see cref="Copy(IEnumerable{ClickPoint})"/>) and write the result to the
/// settings once when they close, so the host gets one config push and a crash mid-pick leaves the settings untouched.
/// </summary>
public static class PickerEdits
{
    /// <summary>Appends a point with the defaults for new points (1 click, radius 0). False when the list is full.</summary>
    public static bool TryAddPoint(List<ClickPoint> points, PixelPoint p)
    {
        if (points.Count >= ClickerSettingsRules.MaxClickPoints) return false;
        points.Add(new ClickPoint { X = p.X, Y = p.Y, Clicks = 1, Radius = 0 });
        return true;
    }

    public static bool RemoveLastPoint(List<ClickPoint> points)
    {
        if (points.Count == 0) return false;
        points.RemoveAt(points.Count - 1);
        return true;
    }

    /// <summary>Appends an enabled Stop zone named "Zone n" (lowest free n) with a fresh id. Null when the list is full.</summary>
    public static ClickerZone? TryAddZone(List<ClickerZone> zones, PixelRect r)
    {
        if (zones.Count >= ClickerSettingsRules.MaxZones) return null;
        var zone = new ClickerZone
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = NextZoneName(zones),
            X = r.X, Y = r.Y, W = r.W, H = r.H,
            Action = ZoneAction.Stop,
            Enabled = true
        };
        zones.Add(zone);
        return zone;
    }

    public static string NextZoneName(IReadOnlyCollection<ClickerZone> zones)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var z in zones) taken.Add(z.Name ?? "");
        for (int n = 1; ; n++)
        {
            string name = "Zone " + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!taken.Contains(name)) return name;
        }
    }

    public static List<ClickPoint> Copy(IEnumerable<ClickPoint>? points)
    {
        var result = new List<ClickPoint>();
        if (points is null) return result;
        foreach (var p in points)
            if (p is not null) result.Add(new ClickPoint { X = p.X, Y = p.Y, Clicks = p.Clicks, Radius = p.Radius });
        return result;
    }

    public static List<ClickerZone> Copy(IEnumerable<ClickerZone>? zones)
    {
        var result = new List<ClickerZone>();
        if (zones is null) return result;
        foreach (var z in zones)
            if (z is not null)
                result.Add(new ClickerZone { Id = z.Id, Name = z.Name, X = z.X, Y = z.Y, W = z.W, H = z.H, Action = z.Action, Enabled = z.Enabled });
        return result;
    }
}
