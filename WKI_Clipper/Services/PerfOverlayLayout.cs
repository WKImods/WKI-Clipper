using System;
using System.Collections.Generic;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>One line of the on-screen overlay.</summary>
public sealed record PerfOverlayLine(string Label, string Value, double? Fraction, IReadOnlyList<double>? Graph, bool Critical);

/// <summary>
/// Pure logic of the performance overlay — which lines to show, their texts, graph points
/// and the corner placement. Free of WPF so it can be unit-tested.
/// </summary>
public static class PerfOverlayLayout
{
    /// <summary>Top-left of an overlay of the given size in a corner of the area (all physical pixels).</summary>
    public static (double X, double Y) Place(ScreenCorner corner, double width, double height, LayoutRect area, double margin)
    {
        bool right = corner is ScreenCorner.TopRight or ScreenCorner.BottomRight;
        bool bottom = corner is ScreenCorner.BottomLeft or ScreenCorner.BottomRight;
        double x = right ? area.Right - margin - width : area.X + margin;
        double y = bottom ? area.Bottom - margin - height : area.Y + margin;
        return (x, y);
    }

    public static double ClampScale(double scale) => Math.Clamp(double.IsFinite(scale) ? scale : 1.0, 0.75, 2.0);

    /// <summary>The lines to draw for the current sample, in a fixed order, honouring the ticks.</summary>
    public static List<PerfOverlayLine> Lines(PerfOverlaySettings s, PerfSample now, IReadOnlyList<PerfSample> history, DateTime clock)
    {
        var lines = new List<PerfOverlayLine>();
        IReadOnlyList<double>? G(Func<PerfSample, double?> pick) => s.ShowGraphs ? Series(history, pick) : null;

        if (s.ShowCpu)
            lines.Add(new("CPU", $"{now.CpuPercent:F0} %", now.CpuPercent / 100, G(p => p.CpuPercent / 100), now.CpuPercent >= 90));
        if (s.ShowGpu)
            lines.Add(new("GPU", $"{now.GpuPercent:F0} %", now.GpuPercent / 100, G(p => p.GpuPercent / 100), now.GpuPercent >= 95));
        if (s.ShowGpuTemp && now.GpuTempC is double t)
            lines.Add(new("GPU °C", $"{t:F0} °C", TempFraction(t), G(p => p.GpuTempC is double v ? TempFraction(v) : null), t >= 85));
        if (s.ShowGpuFan && now.GpuFanRpm is uint rpm)
            lines.Add(new(L.T("Lüfter", "Fan"), $"{rpm} rpm", null, null, false));
        if (s.ShowRam && now.RamTotalBytes > 0)
            lines.Add(new("RAM", $"{Gb(now.RamUsedBytes):F1} GB", now.RamPercent / 100, G(p => p.RamPercent / 100), now.RamPercent >= 90));
        if (s.ShowVram && now.VramUsedBytes > 0)
            lines.Add(new("VRAM", $"{Gb(now.VramUsedBytes):F1} GB", now.VramPercent / 100,
                G(p => p.VramPercent / 100), now.VramPercent >= 95));
        if (s.ShowClock)
            lines.Add(new(L.T("Uhr", "Time"), clock.ToString("HH:mm"), null, null, false));
        return lines;
    }

    /// <summary>GPU temperature mapped onto a 30–100 °C scale for the graph.</summary>
    public static double TempFraction(double celsius) => Math.Clamp((celsius - 30) / 70, 0, 1);

    /// <summary>Graph values 0..1 (missing readings skipped), oldest first.</summary>
    public static IReadOnlyList<double> Series(IReadOnlyList<PerfSample> history, Func<PerfSample, double?> pick)
    {
        var list = new List<double>(history.Count);
        foreach (var p in history)
            if (pick(p) is double v && double.IsFinite(v)) list.Add(Math.Clamp(v, 0, 1));
        return list;
    }

    private static double Gb(ulong bytes) => bytes / (1024.0 * 1024 * 1024);
}
