using System;
using System.Collections.Generic;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>One line of the on-screen overlay.</summary>
public sealed record PerfOverlayLine(string Label, string Value, double? Fraction, IReadOnlyList<double>? Graph, bool Critical);

/// <summary>The values the FPS engine delivers, in overlay order.</summary>
public enum FpsValue { Fps, FrameTime, Low1, Low01, Stutter }

/// <summary>An 8-bit colour with alpha — the overlay's colours without a WPF dependency.</summary>
public readonly record struct Rgba(byte A, byte R, byte G, byte B);

/// <summary>
/// Pure logic of the performance overlay — which lines to show, their texts, graph points,
/// the column flow, the colours and the corner placement. Free of WPF so it can be unit-tested.
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

    /// <summary>Graph width/height factor, 50 – 200 %.</summary>
    public static double ClampGraphScale(double scale) => Math.Clamp(double.IsFinite(scale) ? scale : 1.0, 0.5, 2.0);

    public static int ClampColumns(int columns) => Math.Clamp(columns, 1, 3);

    public static int ClampTransparency(int percent) => Math.Clamp(percent, 0, 100);

    // ---- visibility and the opt-in FPS measurement ----

    /// <summary>The overlay is up when it is switched on and — if asked for — a game is in front.</summary>
    public static bool ShouldShow(PerfOverlaySettings s, bool gameDetected) => s.Enabled && (!s.OnlyWhenGame || gameDetected);

    /// <summary>At least one value that needs the FPS measurement is ticked.</summary>
    public static bool AnyFpsValue(PerfOverlaySettings s)
        => s.ShowFps || s.ShowFrameTime || s.ShowLow1 || s.ShowLow01 || s.ShowStutter;

    /// <summary>
    /// The FPS measurement (an ETW session) runs only while a ticked FPS value can actually be seen —
    /// on the overlay or in the Performance widget. Nothing ticked or nothing visible = no session.
    /// </summary>
    public static bool ShouldMeasureFps(PerfOverlaySettings s, bool overlayShown, bool widgetShown)
        => AnyFpsValue(s) && (overlayShown || widgetShown);

    // ---- columns ----

    /// <summary>
    /// Splits the lines into at most <paramref name="columns"/> columns that fill column-wise: the
    /// first column takes the first ceil(n / columns) lines, the next one the following ones, and so
    /// on. Columns that would stay empty are not produced.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<PerfOverlayLine>> Columns(IReadOnlyList<PerfOverlayLine> lines, int columns)
    {
        columns = ClampColumns(columns);
        var result = new List<IReadOnlyList<PerfOverlayLine>>();
        if (lines.Count == 0) return result;

        int rows = (lines.Count + columns - 1) / columns;
        for (int start = 0; start < lines.Count; start += rows)
        {
            int count = Math.Min(rows, lines.Count - start);
            var column = new List<PerfOverlayLine>(count);
            for (int i = 0; i < count; i++) column.Add(lines[start + i]);
            result.Add(column);
        }
        return result;
    }

    // ---- colours and background ----

    public static Rgba ColorOf(OverlayColor color) => color switch
    {
        OverlayColor.Gray => new Rgba(255, 0xA8, 0xA3, 0xB8),
        OverlayColor.Orange => new Rgba(255, 0xFF, 0x7A, 0x2C),   // WKI accent
        OverlayColor.Green => new Rgba(255, 0x46, 0xD0, 0x7A),
        OverlayColor.Cyan => new Rgba(255, 0x22, 0xD3, 0xEE),
        OverlayColor.Yellow => new Rgba(255, 0xFA, 0xCC, 0x15),
        OverlayColor.Red => new Rgba(255, 0xE5, 0x48, 0x4D),
        _ => new Rgba(255, 0xFF, 0xFF, 0xFF),                      // White, and anything unknown
    };

    /// <summary>
    /// Fill of the panel behind the numbers; null = no panel. Transparency is Adrenalin's
    /// "Transparenz": 0 % = opaque, 100 % = invisible.
    /// </summary>
    public static Rgba? BackdropFill(OverlayBackground background, int transparencyPercent)
    {
        byte alpha = (byte)Math.Round(255.0 * (100 - ClampTransparency(transparencyPercent)) / 100.0, MidpointRounding.AwayFromZero);
        return background switch
        {
            OverlayBackground.Black => new Rgba(alpha, 0, 0, 0),
            OverlayBackground.Violet => new Rgba(alpha, 0x2B, 0x1B, 0x4A),
            _ => null,
        };
    }

    /// <summary>Space between the numbers and the edge of the window (device-independent pixels).</summary>
    public static double BackdropPadding(OverlayBackground background) => background == OverlayBackground.Off ? 6 : 10;

    // ---- lines ----

    /// <summary>
    /// The lines to draw for the current sample, in a fixed order, honouring the ticks. FPS values come
    /// first (like Adrenalin); <paramref name="frame"/> is the FPS engine's latest snapshot (null = none).
    /// </summary>
    public static List<PerfOverlayLine> Lines(PerfOverlaySettings s, PerfSample now, IReadOnlyList<PerfSample> history,
        DateTime clock, FrameMetricsSnapshot? frame = null)
    {
        var lines = new List<PerfOverlayLine>();
        IReadOnlyList<double>? G(Func<PerfSample, double?> pick) => s.ShowGraphs ? Series(history, pick) : null;

        AddFrameLines(lines, s, frame);

        if (s.ShowCpu)
            lines.Add(new("CPU", $"{now.CpuPercent:F0} %", now.CpuPercent / 100, G(p => p.CpuPercent / 100), now.CpuPercent >= 90));
        if (s.ShowGpu)
            lines.Add(new("GPU", $"{now.GpuPercent:F0} %", now.GpuPercent / 100, G(p => p.GpuPercent / 100), now.GpuPercent >= 95));
        if (s.ShowGpuClock && now.GpuClockMhz is double mhz)
            lines.Add(new(L.T("GPU-Takt", "GPU clock"), $"{mhz:F0} MHz", null, null, false));
        if (s.ShowGpuVoltage && now.GpuVoltageMv is uint mv)
            lines.Add(new(L.T("GPU-Spannung", "GPU voltage"), $"{mv} mV", null, null, false));
        if (s.ShowGpuTemp && now.GpuTempC is double t)
            lines.Add(new("GPU °C", $"{t:F0} °C", TempFraction(t), G(p => p.GpuTempC is double v ? TempFraction(v) : null), t >= 85));
        if (s.ShowGpuFan && now.GpuFanRpm is uint rpm)
            lines.Add(new(L.T("Lüfter", "Fan"), $"{rpm} rpm", null, null, false));
        if (s.ShowRam && now.RamTotalBytes > 0)
            lines.Add(new("RAM", $"{Gb(now.RamUsedBytes):F1} GB", now.RamPercent / 100, G(p => p.RamPercent / 100), now.RamPercent >= 90));
        if (s.ShowVram && now.VramUsedBytes > 0)
            lines.Add(new("VRAM", $"{Gb(now.VramUsedBytes):F1} GB", now.VramPercent / 100,
                G(p => p.VramPercent / 100), now.VramPercent >= 95));
        if (s.ShowVramClock && now.VramClockMhz is double vmhz)
            lines.Add(new(L.T("VRAM-Takt", "VRAM clock"), $"{vmhz:F0} MHz", null, null, false));
        if (s.ShowClock)
            lines.Add(new(L.T("Uhr", "Time"), clock.ToString("HH:mm"), null, null, false));
        return lines;
    }

    /// <summary>
    /// FPS, average frame time, 1 % / 0.1 % low and micro-stutter, in this order. A status other than Ok
    /// replaces the numbers with its text; a value the engine does not have yet (the lows need ≥ 100
    /// frames) is "k. A.".
    /// </summary>
    private static void AddFrameLines(List<PerfOverlayLine> lines, PerfOverlaySettings s, FrameMetricsSnapshot? frame)
    {
        foreach (var kind in Enum.GetValues<FpsValue>())
            if (IsTicked(s, kind))
                lines.Add(new(FrameLabel(kind), FrameValueText(kind, frame), null, null, false));
    }

    private static bool IsTicked(PerfOverlaySettings s, FpsValue kind) => kind switch
    {
        FpsValue.Fps => s.ShowFps,
        FpsValue.FrameTime => s.ShowFrameTime,
        FpsValue.Low1 => s.ShowLow1,
        FpsValue.Low01 => s.ShowLow01,
        _ => s.ShowStutter,
    };

    /// <summary>Name of an FPS value on the overlay and in the widget.</summary>
    public static string FrameLabel(FpsValue kind) => kind switch
    {
        FpsValue.Fps => "FPS",
        FpsValue.FrameTime => L.T("Framezeit", "Frame time"),
        FpsValue.Low1 => "1 % Low",
        FpsValue.Low01 => L.T("0,1 % Low", "0.1 % low"),
        _ => L.T("Mikro-Stutter", "Micro-stutter"),
    };

    /// <summary>The number of an FPS value, or the status text when the engine has none (see <see cref="FrameStatusText"/>).</summary>
    public static string FrameValueText(FpsValue kind, FrameMetricsSnapshot? frame)
    {
        var status = frame?.Status ?? FrameMetricsStatus.Unavailable;
        double? value = kind switch
        {
            FpsValue.Fps => frame?.Fps,
            FpsValue.FrameTime => frame?.AvgFrameTimeMs,
            FpsValue.Low1 => frame?.Low1,
            FpsValue.Low01 => frame?.Low01,
            _ => frame?.StutterPercent,
        };
        if (status != FrameMetricsStatus.Ok || value is not double v || !double.IsFinite(v)) return FrameStatusText(status);
        return kind switch
        {
            FpsValue.FrameTime => $"{v:F1} ms",
            FpsValue.Stutter => $"{v:F1} %",
            _ => $"{v:F0}",
        };
    }
    /// <summary>What an FPS value shows when the engine has no number for it.</summary>
    public static string FrameStatusText(FrameMetricsStatus status) => status switch
    {
        FrameMetricsStatus.NoPermission => L.T("k. A. (Recht fehlt)", "n/a (permission missing)"),
        FrameMetricsStatus.NoGame => "–",
        _ => L.T("k. A.", "n/a"),
    };

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
