using System;
using System.Collections.Generic;
using System.Linq;

namespace WKI_Clipper.Services;

/// <summary>One entry of the capture view's monitor list; <see cref="Value"/> null = the primary monitor, automatically.</summary>
public sealed record CaptureMonitorChoice(string Display, MonitorRef? Value);

/// <summary>
/// The monitor list for "Ganzer Monitor" (decision 3): "Hauptmonitor (automatisch)", then every monitor Windows reports,
/// left to right, labelled like Windows ("LG ULTRAGEAR+ · 3440×1440 · Hauptmonitor"), and the saved monitor as
/// "X (nicht verbunden)" while it is not connected. No "Monitor N" numbers.
/// </summary>
public static class CaptureMonitorList
{
    /// <returns>The entries and the one to select: the saved monitor, the automatic entry when nothing is saved.</returns>
    public static (IReadOnlyList<CaptureMonitorChoice> Items, int SelectedIndex) Build(DisplayTopology t, MonitorRef? saved)
    {
        var items = new List<CaptureMonitorChoice> { new(MonitorLabels.AutoPrimary, null) };
        if (saved is null)
        {
            items.AddRange(Ordered(t).Select(d => new CaptureMonitorChoice(MonitorLabels.Build(d, t), MonitorRef.Of(d))));
            return (items, 0);
        }

        // Found as the resolver finds it - also when the capture cannot reach it: it is still the user's choice.
        var found = MonitorTargetRules.Locate(saved, t);
        int selected = -1;
        foreach (var d in Ordered(t))
        {
            bool isSaved = found is not null && Same(found, d);
            if (isSaved) selected = items.Count;
            // The saved monitor's entry carries what was found: matched while asleep, that keeps the saved device path.
            items.Add(new CaptureMonitorChoice(MonitorLabels.Build(d, t), MonitorRef.Of(isSaved ? found! : d)));
        }

        if (selected < 0)
        {
            selected = items.Count;
            items.Add(new CaptureMonitorChoice(MonitorLabels.BuildMissing(saved), saved));
        }
        return (items, selected);
    }

    private static IEnumerable<DisplayInfo> Ordered(DisplayTopology t)
        => t.Displays.OrderBy(d => d.X).ThenBy(d => d.Y).ThenBy(d => d.GdiName, StringComparer.OrdinalIgnoreCase);

    /// <summary>The same entry of the topology (a matched display may be a copy with the saved device path).</summary>
    private static bool Same(DisplayInfo a, DisplayInfo b)
        => a.HMonitor == b.HMonitor && string.Equals(a.GdiName, b.GdiName, StringComparison.OrdinalIgnoreCase);
}
