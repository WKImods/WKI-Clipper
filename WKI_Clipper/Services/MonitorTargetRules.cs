using System;
using System.Linq;

namespace WKI_Clipper.Services;

/// <summary>How the monitor to capture was found.</summary>
public enum MonitorResolutionStatus
{
    /// <summary>The saved device path is connected.</summary>
    Exact,
    /// <summary>The saved path is gone (or Windows reports none right now), but exactly one connected monitor has the saved
    /// EDID model - with several identical models, exactly one of them with the saved GDI name.</summary>
    Matched,
    /// <summary>Only a GDI name is saved (0.19 and older) and a monitor has it.</summary>
    Legacy,
    /// <summary>Nothing is saved: the primary monitor, no warning.</summary>
    PrimaryByDefault,
    /// <summary>The saved monitor is not connected or the capture cannot reach it: the primary monitor, with a warning.</summary>
    FallbackPrimary,
    /// <summary>No monitor the capture can reach.</summary>
    NoDisplays
}

/// <param name="Display">
/// The monitor to capture: the saved one, the primary for <see cref="MonitorResolutionStatus.PrimaryByDefault"/> and
/// <see cref="MonitorResolutionStatus.FallbackPrimary"/>, null for <see cref="MonitorResolutionStatus.NoDisplays"/>. When not
/// null it has a DXGI output on the default adapter (<see cref="CaptureSourceRules.ForDisplay"/> gives a source). When the
/// monitor was matched while Windows reports no device path for it (a sleeping DisplayPort monitor), it carries the saved
/// path, so its identity stays the same.
/// </param>
/// <param name="Reason">Why: for the log, and the user text of a fallback or of <see cref="MonitorResolutionStatus.NoDisplays"/>.</param>
/// <param name="UpgradeSavedRef">
/// The saved identity is outdated or incomplete: save <c>MonitorRef.Of(Display)</c>. Only set when that ref differs from the
/// saved one and keeps every value the saved one has - never for a fallback (Display is then another monitor).
/// </param>
public sealed record MonitorResolution(MonitorResolutionStatus Status, DisplayInfo? Display, string Reason, bool UpgradeSavedRef)
{
    public bool IsFallback => Status == MonitorResolutionStatus.FallbackPrimary;
}

/// <summary>
/// Finds the saved monitor in the current topology. Never through a list position: the identity is the device path, then a
/// unique EDID model (identical models only with the saved GDI name as tie-breaker), then - for old settings that saved
/// nothing else - the GDI name. A monitor that is missing or hangs on a GPU the capture does not reach falls back to the
/// primary monitor, visibly (status <see cref="MonitorResolutionStatus.FallbackPrimary"/> with a reason).
/// </summary>
public static class MonitorTargetRules
{
    public static MonitorResolution Resolve(MonitorRef? saved, DisplayTopology topo)
    {
        if (saved is null)
            return OnPrimary(topo, MonitorResolutionStatus.PrimaryByDefault, L.T("kein Monitor gewählt", "no monitor chosen"));

        var (found, status, reason) = Find(saved, topo);
        if (found is null)
        {
            string missing = Quote(saved.FriendlyName);
            return OnPrimary(topo, MonitorResolutionStatus.FallbackPrimary,
                             L.T($"{missing} ist nicht verbunden", $"{missing} is not connected"));
        }

        string? unreachable = Unreachable(found, topo, Quote(found.FriendlyName ?? saved.FriendlyName));
        if (unreachable is not null)
            return OnPrimary(topo, MonitorResolutionStatus.FallbackPrimary, unreachable);

        var next = MonitorRef.Of(found);
        return new MonitorResolution(status, found, reason, next != saved && saved.LosesNothingTo(next));
    }

    private static (DisplayInfo? Found, MonitorResolutionStatus Status, string Reason) Find(MonitorRef saved, DisplayTopology topo)
    {
        if (saved.DevicePath is string path)
        {
            var byPath = topo.Displays.FirstOrDefault(d => string.Equals(d.DevicePath, path, StringComparison.OrdinalIgnoreCase));
            if (byPath is not null)
                return (byPath, MonitorResolutionStatus.Exact, L.T("Gerätepfad", "device path"));
        }

        if (saved.HasEdid)
        {
            var sameModel = topo.Displays.Where(d => d.EdidMfg == saved.EdidMfg && d.EdidProduct == saved.EdidProduct).ToList();
            DisplayInfo? pick = null;
            string reason = L.T("gleiches Modell (EDID)", "same model (EDID)");
            if (sameModel.Count == 1)
                pick = sameModel[0];
            else if (sameModel.Count > 1 && saved.GdiName is string gdi)
            {
                var byGdi = sameModel.Where(d => string.Equals(d.GdiName, gdi, StringComparison.OrdinalIgnoreCase)).ToList();
                if (byGdi.Count == 1)
                {
                    pick = byGdi[0];
                    reason = L.T("gleiches Modell (EDID) und GDI-Name", "same model (EDID) and GDI name");
                }
            }

            if (pick is not null)
            {
                if (string.IsNullOrEmpty(pick.DevicePath) && saved.DevicePath is not null)
                {
                    pick = pick with { DevicePath = saved.DevicePath };
                    reason += L.T(", Windows meldet gerade keinen Gerätepfad", ", Windows reports no device path right now");
                }
                return (pick, MonitorResolutionStatus.Matched, reason);
            }
            // The saved model is not (unambiguously) there. The GDI name alone would only guess.
            return (null, MonitorResolutionStatus.FallbackPrimary, "");
        }

        // Old settings: nothing stronger than the GDI name was saved. A saved path that is gone never comes here -
        // GDI names are adapter slots and may now belong to another monitor.
        if (saved.DevicePath is null && saved.GdiName is string legacy)
        {
            var byGdi = topo.Displays.FirstOrDefault(d => string.Equals(d.GdiName, legacy, StringComparison.OrdinalIgnoreCase));
            if (byGdi is not null)
                return (byGdi, MonitorResolutionStatus.Legacy, L.T("alte Einstellung (GDI-Name)", "old setting (GDI name)"));
        }

        return (null, MonitorResolutionStatus.FallbackPrimary, "");
    }

    /// <summary>The primary monitor with <paramref name="status"/>, or <see cref="MonitorResolutionStatus.NoDisplays"/> when the capture cannot reach it.</summary>
    private static MonitorResolution OnPrimary(DisplayTopology topo, MonitorResolutionStatus status, string reason)
    {
        var primary = topo.Primary;
        string? problem = primary is null
            ? topo.Displays.Count == 0
                ? L.T("Windows meldet keinen Monitor", "Windows reports no monitor")
                : L.T("Windows meldet keinen Hauptmonitor", "Windows reports no primary monitor")
            : Unreachable(primary, topo, L.T($"Hauptmonitor {Quote(primary.FriendlyName)}", $"Primary monitor {Quote(primary.FriendlyName)}"));

        if (problem is null)
            return new MonitorResolution(status, primary, reason, UpgradeSavedRef: false);
        return new MonitorResolution(MonitorResolutionStatus.NoDisplays, null,
                                     status == MonitorResolutionStatus.FallbackPrimary ? $"{reason}; {problem}" : problem,
                                     UpgradeSavedRef: false);
    }

    /// <summary>Why the capture cannot reach <paramref name="d"/> (named <paramref name="who"/>); null when it can.</summary>
    private static string? Unreachable(DisplayInfo d, DisplayTopology topo, string who)
    {
        if (CaptureSourceRules.IsCapturable(d, topo)) return null;

        if (d.AdapterIndex is int adapter && d.OutputIndex is not null && adapter != topo.DefaultAdapterIndex)
        {
            string on = AdapterName(topo, adapter);
            string reach = AdapterName(topo, topo.DefaultAdapterIndex);
            return L.T($"{who} hängt an „{on}“, die Aufnahme erreicht nur „{reach}“",
                       $"{who} is connected to “{on}”, the capture only reaches “{reach}”");
        }
        return L.T($"{who} hat keinen DXGI-Ausgang, den die Aufnahme erreicht",
                   $"{who} has no DXGI output the capture can reach");
    }

    private static string Quote(string? friendlyName)
    {
        string name = MonitorLabels.NameOf(friendlyName);
        return L.T($"„{name}“", $"“{name}”");
    }

    private static string AdapterName(DisplayTopology topo, int index)
        => topo.Adapters.FirstOrDefault(a => a.DxgiIndex == index)?.Name ?? L.T("unbekannte GPU", "unknown GPU");
}
