using System;
using System.Linq;

namespace WKI_Clipper.Services;

/// <summary>
/// The monitor a capture records, resolved once and consumed by the buffer, manual recording, screenshots and the views.
/// <see cref="DisplayKey"/> is the monitor's identity (lower-case device path, else <c>gdi:</c> + GDI name);
/// <see cref="OutputIndex"/> is ddagrab's <c>output_idx</c> on the default adapter (<see cref="AdapterLuid"/>);
/// <see cref="AmfIndex"/> is vsrc_amf's index, only when that mapping is certain (decision 6). The size is physical pixels
/// of the monitor's current mode.
/// </summary>
public sealed record CaptureSource(
    string DisplayKey,
    string GdiName,
    nint HMonitor,
    int OutputIndex,
    long AdapterLuid,
    int? AmfIndex,
    int Width,
    int Height,
    string Label,
    MonitorResolution Resolution);

public static class CaptureSourceRules
{
    /// <summary>The source for <paramref name="d"/>; null when it has no DXGI output on the default adapter (ddagrab cannot reach it).</summary>
    public static CaptureSource? ForDisplay(DisplayInfo d, DisplayTopology t, MonitorResolution r)
    {
        if (!IsCapturable(d, t)) return null;
        var adapter = t.Adapters.First(a => a.DxgiIndex == d.AdapterIndex);
        int output = d.OutputIndex!.Value;

        // vsrc_amf's monitor numbering is not documented: only trust it when every monitor is a DXGI output of the default
        // adapter, so both counts can only be the same list.
        bool amfCertain = t.Displays.All(o => o.OutputIndex is not null && o.AdapterIndex == t.DefaultAdapterIndex);

        return new CaptureSource(DisplayKeyOf(d), d.GdiName, d.HMonitor, output, adapter.Luid, amfCertain ? output : null,
                                 d.Width, d.Height, MonitorLabels.Build(d, t), r);
    }

    /// <summary>The same physical monitor (its mode, position or output index may differ).</summary>
    public static bool SameMonitor(CaptureSource a, CaptureSource b)
        => string.Equals(a.DisplayKey, b.DisplayKey, StringComparison.Ordinal);

    /// <summary>The same monitor recorded through the same adapter output at the same size: ffmpeg would get the same arguments.</summary>
    public static bool SameSource(CaptureSource a, CaptureSource b)
        => SameMonitor(a, b) && a.AdapterLuid == b.AdapterLuid && a.OutputIndex == b.OutputIndex
           && a.Width == b.Width && a.Height == b.Height;

    /// <summary>
    /// The log line of a capture start (decision 8): <c>Capture.Monitor: saved={path|gdi|null} -> {Status} ({Reason})
    /// display='{label}' gdi={gdi} adapter={luid} default={idx} output={o} {W}x{H}@({X},{Y}) primary={b} path={path}</c>.
    /// </summary>
    /// <param name="saved">The saved monitor identity that applies (Monitor mode); null when none does.</param>
    /// <param name="capturePath">"dda", "amf" or "wgc".</param>
    internal static string LogLine(MonitorRef? saved, CaptureSource s, DisplayTopology t, string capturePath)
    {
        var d = s.Resolution.Display;
        string at = d is null ? "" : $"@({d.X},{d.Y})";
        return $"Capture.Monitor: saved={saved?.DevicePath ?? saved?.GdiName ?? "null"} -> {s.Resolution.Status} ({s.Resolution.Reason}) " +
               $"display='{s.Label}' gdi={s.GdiName} adapter={DisplayTopology.LuidText(s.AdapterLuid)} default={t.DefaultAdapterIndex} " +
               $"output={s.OutputIndex} {s.Width}x{s.Height}{at} primary={d?.IsPrimary} path={capturePath}";
    }

    /// <summary>Lower-case device path, else <c>gdi:</c> + upper-case GDI name.</summary>
    internal static string DisplayKeyOf(DisplayInfo d)
        => string.IsNullOrEmpty(d.DevicePath) ? "gdi:" + d.GdiName.ToUpperInvariant() : d.DevicePath.ToLowerInvariant();

    /// <summary>ddagrab can record <paramref name="d"/>: it is a DXGI output of the (known) default adapter.</summary>
    internal static bool IsCapturable(DisplayInfo d, DisplayTopology t)
        => d.OutputIndex is not null
           && d.AdapterIndex is int adapter
           && adapter == t.DefaultAdapterIndex
           && t.Adapters.Any(a => a.DxgiIndex == adapter);
}
