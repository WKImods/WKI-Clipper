using System;
using System.Collections.Generic;
using System.Linq;

namespace WKI_Clipper.Services;

/// <summary>
/// Monitor texts as Windows names them: "LG ULTRAGEAR+ · 3440×1440 · links · Hauptmonitor". The side is relative to the
/// primary monitor (what the arrangement picture in the Windows settings shows); identical names get the connector, then
/// " (2)". No invented "Monitor N" numbers: Windows' own number has no API.
/// </summary>
public static class MonitorLabels
{
    private const string Separator = " · ";

    public static string Build(DisplayInfo d, DisplayTopology t)
    {
        var parts = new List<string>(3) { DistinctName(d, t), $"{d.Width}×{d.Height}" };
        if (d.IsPrimary)
            parts.Add(L.T("Hauptmonitor", "primary"));
        else if (Side(d, t.Primary) is string side)
            parts.Add(side);
        return string.Join(Separator, parts);
    }

    /// <summary>The saved monitor while it is not connected.</summary>
    public static string BuildMissing(MonitorRef r)
    {
        string name = NameOf(r.FriendlyName);
        return L.T($"{name} (nicht verbunden)", $"{name} (not connected)");
    }

    /// <summary>The "nothing chosen" entry.</summary>
    public static string AutoPrimary => L.T("Hauptmonitor (automatisch)", "Primary monitor (automatic)");

    /// <summary>What is recorded while the saved monitor is missing.</summary>
    public static string Fallback(string primaryLabel, MonitorRef saved)
    {
        string name = NameOf(saved.FriendlyName);
        return L.T($"Ersatz: {primaryLabel} ({name} nicht verbunden)", $"Fallback: {primaryLabel} ({name} not connected)");
    }

    /// <summary>The Windows name, or the text for a monitor that reports none.</summary>
    internal static string NameOf(string? friendlyName)
        => string.IsNullOrWhiteSpace(friendlyName) ? L.T("Monitor ohne Namen", "Unnamed monitor") : friendlyName.Trim();

    private static string DistinctName(DisplayInfo d, DisplayTopology t)
    {
        string name = NameOf(d.FriendlyName);
        var namesake = t.Displays.Where(o => string.Equals(NameOf(o.FriendlyName), name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!namesake.Any(o => Same(o, d))) namesake.Add(d);
        if (namesake.Count < 2) return name;

        string withConnector = WithConnector(name, d.Connector);
        var stillEqual = namesake
            .Where(o => string.Equals(WithConnector(name, o.Connector), withConnector, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => o.X).ThenBy(o => o.Y).ThenBy(o => o.GdiName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        int position = stillEqual.FindIndex(o => Same(o, d));
        return position <= 0 ? withConnector : $"{withConnector} ({position + 1})";
    }

    private static string WithConnector(string name, string? connector)
        => string.IsNullOrEmpty(connector) ? name : $"{name} ({connector})";

    /// <summary>Left/right/above/below of the primary, from the centres of the two rects; null for the primary itself.</summary>
    private static string? Side(DisplayInfo d, DisplayInfo? primary)
    {
        if (primary is null || Same(primary, d)) return null;
        double dx = (d.X + d.Width / 2.0) - (primary.X + primary.Width / 2.0);
        double dy = (d.Y + d.Height / 2.0) - (primary.Y + primary.Height / 2.0);
        if (dx == 0 && dy == 0) return null;
        if (Math.Abs(dx) >= Math.Abs(dy))
            return dx < 0 ? L.T("links", "left") : L.T("rechts", "right");
        return dy < 0 ? L.T("oben", "above") : L.T("unten", "below");
    }

    /// <summary>The same entry of the topology (a resolved display may be a copy with the saved device path filled in).</summary>
    private static bool Same(DisplayInfo a, DisplayInfo b)
        => a.HMonitor == b.HMonitor && string.Equals(a.GdiName, b.GdiName, StringComparison.OrdinalIgnoreCase);
}
