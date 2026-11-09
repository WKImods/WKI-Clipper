using System;
using System.Collections.Generic;
using System.Linq;

namespace WKI_Clipper.Services;

/// <summary>One DXGI adapter (GPU) in <c>IDXGIFactory1.EnumAdapters1</c> order; <see cref="DxgiIndex"/> is that position.</summary>
public sealed record DisplayAdapterInfo(int DxgiIndex, long Luid, string Name, bool IsSoftware);

/// <summary>
/// One active monitor, joined from three Windows sources: EnumDisplayMonitors (<see cref="HMonitor"/>, <see cref="GdiName"/>,
/// rect, primary flag), CCD (<see cref="DevicePath"/>, <see cref="FriendlyName"/>, <see cref="Connector"/>, EDID ids) and
/// DXGI (<see cref="AdapterIndex"/> + <see cref="OutputIndex"/>, the numbers ddagrab counts). A part Windows did not answer
/// is null. The rect is in physical pixels.
/// <para>The GDI name (<c>\\.\DISPLAYn</c>) is a source slot of the adapter, not a property of the monitor: it can change
/// after replugging or a driver reset. The device path is the stable identity.</para>
/// </summary>
public sealed record DisplayInfo(
    nint HMonitor,
    string GdiName,
    string? DevicePath,
    string? FriendlyName,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsPrimary,
    int? AdapterIndex,
    int? OutputIndex,
    string? Connector,
    ushort? EdidMfg,
    ushort? EdidProduct);

/// <summary>
/// A snapshot of the monitors and GPUs as the capture sees them. <see cref="DefaultAdapterIndex"/> is the adapter of
/// <c>D3D11CreateDevice(null, Hardware)</c> - the one ffmpeg's ddagrab enumerates its <c>output_idx</c> on (-1 when there
/// is no adapter). <see cref="Problem"/> names every part that could not be read (null when everything was read).
/// </summary>
public sealed record DisplayTopology(
    IReadOnlyList<DisplayAdapterInfo> Adapters,
    IReadOnlyList<DisplayInfo> Displays,
    int DefaultAdapterIndex,
    string? Problem)
{
    public static DisplayTopology Empty(string problem)
        => new(Array.Empty<DisplayAdapterInfo>(), Array.Empty<DisplayInfo>(), -1, problem);

    public DisplayInfo? Primary => Displays.FirstOrDefault(d => d.IsPrimary);

    public DisplayInfo? ByHMonitor(nint hMonitor)
        => hMonitor == 0 ? null : Displays.FirstOrDefault(d => d.HMonitor == hMonitor);

    /// <summary>
    /// Identity, rect, primary flag and adapter/output of every display plus the default adapter, independent of the
    /// enumeration order. Equal fingerprints = nothing the capture depends on changed; a mode change changes it.
    /// The HMONITOR and the friendly name are left out (a new handle for the same monitor captures the same picture).
    /// </summary>
    public string Fingerprint
    {
        get
        {
            var displays = Displays
                .Select(d =>
                {
                    string identity = string.IsNullOrEmpty(d.DevicePath) ? "-" : d.DevicePath.ToLowerInvariant();
                    return $"{identity}|{d.GdiName.ToUpperInvariant()}|{Edid(d)}|{d.X},{d.Y},{d.Width}x{d.Height}"
                         + $"|a{N(d.AdapterIndex)}:{LuidOf(d.AdapterIndex)}|o{N(d.OutputIndex)}|{(d.IsPrimary ? "P" : "-")}";
                })
                .OrderBy(s => s, StringComparer.Ordinal);
            return $"default={N(DefaultAdapterIndex)}:{LuidOf(DefaultAdapterIndex)};" + string.Join(";", displays);
        }
    }

    /// <summary>For the log: a header (counts, default adapter, problem), then one line per adapter and per display.</summary>
    public string Dump()
    {
        var lines = new List<string>(1 + Adapters.Count + Displays.Count);
        string header = $"Display topology: {Displays.Count} display(s), {Adapters.Count} adapter(s), default adapter {N(DefaultAdapterIndex)}";
        if (Problem is not null) header += $"; problem: {Problem}";
        lines.Add(header);

        foreach (var a in Adapters.OrderBy(a => a.DxgiIndex))
            lines.Add($"  adapter {a.DxgiIndex} '{a.Name}' luid={LuidText(a.Luid)}"
                    + (a.DxgiIndex == DefaultAdapterIndex ? " default" : "")
                    + (a.IsSoftware ? " software" : ""));

        foreach (var d in Displays)
            lines.Add($"  display {d.GdiName} {(d.FriendlyName is null ? "(no name)" : $"'{d.FriendlyName}'")}"
                    + $" {d.Width}x{d.Height}@({d.X},{d.Y}){(d.IsPrimary ? " primary" : "")}"
                    + $" adapter={N(d.AdapterIndex)} output={N(d.OutputIndex)}"
                    + (d.Connector is null ? "" : $" {d.Connector}")
                    + $" edid={Edid(d)} hmon=0x{(long)d.HMonitor:X} path={d.DevicePath ?? "-"}");

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>A LUID as Windows tools print it: high part, colon, low part.</summary>
    public static string LuidText(long luid) => $"{(int)(luid >> 32):X8}:{(uint)luid:X8}";

    private string LuidOf(int? adapterIndex)
    {
        var adapter = adapterIndex is int i ? Adapters.FirstOrDefault(a => a.DxgiIndex == i) : null;
        return adapter is null ? "-" : LuidText(adapter.Luid);
    }

    private static string Edid(DisplayInfo d)
        => d.EdidMfg is ushort mfg && d.EdidProduct is ushort product ? $"{mfg:X4}:{product:X4}" : "-";

    private static string N(int? value) => value is int v && v >= 0 ? v.ToString() : "-";
}
