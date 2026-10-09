using System;
using System.Linq;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>v0.14: on-screen performance overlay — layout logic and capture safety.</summary>
public sealed class PerfOverlayTests
{
    private static readonly LayoutRect Screen = new(0, 0, 3440, 1440);

    [Theory]
    [InlineData(ScreenCorner.TopLeft, 10, 10)]
    [InlineData(ScreenCorner.TopRight, 3440 - 10 - 200, 10)]
    [InlineData(ScreenCorner.BottomLeft, 10, 1440 - 10 - 100)]
    [InlineData(ScreenCorner.BottomRight, 3440 - 10 - 200, 1440 - 10 - 100)]
    public void The_overlay_sits_in_the_chosen_corner(ScreenCorner corner, double x, double y)
        => Assert.Equal((x, y), PerfOverlayLayout.Place(corner, 200, 100, Screen, 10));

    [Theory]
    [InlineData(0.1, 0.75)]
    [InlineData(5.0, 2.0)]
    [InlineData(1.25, 1.25)]
    [InlineData(double.NaN, 1.0)]
    public void Size_stays_in_its_range(double input, double expected)
        => Assert.Equal(expected, PerfOverlayLayout.ClampScale(input));

    private static PerfSample Sample(double cpu = 40, double? temp = 62, uint? fan = 1200)
        => new(cpu, 80, 16UL << 30, 48UL << 30, 6UL << 30, 16UL << 30, temp, fan);

    [Fact]
    public void Only_ticked_values_are_shown_in_a_fixed_order()
    {
        var s = new PerfOverlaySettings { ShowCpu = true, ShowGpu = false, ShowGpuTemp = true, ShowGpuFan = true,
                                          ShowRam = false, ShowVram = true, ShowClock = true, ShowGraphs = false };
        var lines = PerfOverlayLayout.Lines(s, Sample(), Array.Empty<PerfSample>(), new DateTime(2026, 10, 9, 21, 5, 0));

        Assert.Equal(new[] { "CPU", "GPU °C", "VRAM" }, lines.Select(l => l.Label).Where(l => l is "CPU" or "GPU °C" or "VRAM"));
        Assert.Equal(5, lines.Count);                      // CPU, GPU temp, fan, VRAM, clock
        Assert.Equal("21:05", lines[^1].Value);
        Assert.All(lines, l => Assert.Null(l.Graph));     // graphs off
    }

    [Fact]
    public void Values_the_driver_does_not_report_are_left_out()
    {
        var s = new PerfOverlaySettings { ShowGpuTemp = true, ShowGpuFan = true };
        var lines = PerfOverlayLayout.Lines(s, Sample(temp: null, fan: null), Array.Empty<PerfSample>(), DateTime.Now);
        Assert.DoesNotContain(lines, l => l.Label == "GPU °C");
    }

    [Fact]
    public void Hot_and_busy_values_are_flagged()
    {
        var s = new PerfOverlaySettings();
        var lines = PerfOverlayLayout.Lines(s, Sample(cpu: 97, temp: 88), Array.Empty<PerfSample>(), DateTime.Now);
        Assert.True(lines.Single(l => l.Label == "CPU").Critical);
        Assert.True(lines.Single(l => l.Label == "GPU °C").Critical);
        Assert.False(lines.Single(l => l.Label == "RAM").Critical);
    }

    [Fact]
    public void Graphs_follow_the_history_scaled_to_0_1()
    {
        var history = new[] { Sample(cpu: 0), Sample(cpu: 50), Sample(cpu: 100) };
        var lines = PerfOverlayLayout.Lines(new PerfOverlaySettings(), Sample(cpu: 100), history, DateTime.Now);
        Assert.Equal(new[] { 0.0, 0.5, 1.0 }, lines.Single(l => l.Label == "CPU").Graph);
    }

    [Theory]
    [InlineData(20, 0.0)]
    [InlineData(65, 0.5)]
    [InlineData(120, 1.0)]
    public void Gpu_temperature_maps_onto_30_to_100_degrees(double c, double fraction)
        => Assert.Equal(fraction, PerfOverlayLayout.TempFraction(c), 3);

    [Fact]
    public void Vram_percentage_needs_a_known_total()
    {
        Assert.Equal(37.5, Sample().VramPercent!.Value, 3);              // 6 of 16 GB
        Assert.Null((Sample() with { VramTotalBytes = 0 }).VramPercent);
    }

    [Fact]
    public void Defaults_are_off_and_private()
    {
        var s = new PerfOverlaySettings();
        Assert.False(s.Enabled);
        Assert.True(s.HideFromCapture);
        Assert.Equal(ScreenCorner.TopRight, s.Corner);
        Assert.Equal(0u, new AppSettings().Hotkeys[HotkeyActions.TogglePerfOverlay].Key);   // unbound
    }

    [Fact]
    public void A_capture_hidden_overlay_rules_out_amf_capture()
    {
        var s = new AppSettings();
        s.Video.Codec = "h264_amf";
        s.Video.Resolution = ResolutionPreset.Native;
        s.Video.UseAmfCapture = true;
        s.Widgets.GetOrAdd(WidgetId.WhatsApp).ExcludeFromCapture = false;
        Assert.False(FFmpegCommandBuilder.MustHonorCaptureExclusion(s));

        s.PerfOverlay.Enabled = true;                       // hidden from capture by default
        Assert.True(FFmpegCommandBuilder.MustHonorCaptureExclusion(s));
        Assert.DoesNotContain("vsrc_amf", FFmpegCommandBuilder.Build(s, "out.mp4", segmentOutput: false));

        s.PerfOverlay.HideFromCapture = false;              // shown on stream: nothing to hide
        Assert.False(FFmpegCommandBuilder.MustHonorCaptureExclusion(s));
    }
}
