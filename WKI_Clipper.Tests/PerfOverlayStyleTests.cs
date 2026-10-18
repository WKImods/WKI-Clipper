using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Phase 8b: the Adrenalin-style overlay — columns, background, colours, the FPS values with their
/// status texts, GPU clock / voltage / VRAM clock, the game-only visibility and the opt-in FPS
/// measurement. All pure layout logic plus the settings defaults.
/// </summary>
public sealed class PerfOverlayStyleTests
{
    // ---- helpers ----

    private static PerfSample Sample(double? clock = 2310, uint? mv = 1020, double? vramClock = 2505)
        => new(40, 80, 16UL << 30, 48UL << 30, 6UL << 30, 16UL << 30, 62, 1200, clock, mv, vramClock);

    private static readonly FrameMetricsSnapshot Good = new(FrameMetricsStatus.Ok, 4242, PresentApi.Dxgi,
        Fps: 143.6, AvgFrameTimeMs: 6.96, Low1: 118.2, Low01: 101.0, StutterPercent: 2.5);

    private static PerfOverlaySettings OnlyFps() => new()
    {
        ShowCpu = false, ShowGpu = false, ShowGpuTemp = false, ShowGpuFan = false, ShowRam = false,
        ShowVram = false, ShowClock = false, ShowGraphs = false,
        ShowFps = true, ShowFrameTime = true, ShowLow1 = true, ShowLow01 = true, ShowStutter = true,
    };

    private static System.Collections.Generic.List<PerfOverlayLine> LinesOf(PerfOverlaySettings s, FrameMetricsSnapshot? f,
        PerfSample? sample = null)
        => PerfOverlayLayout.Lines(s, sample ?? Sample(), Array.Empty<PerfSample>(), new DateTime(2026, 10, 17, 20, 15, 0), f);

    private static PerfOverlayLine[] Many(int n)
        => Enumerable.Range(0, n).Select(i => new PerfOverlayLine("L" + i, "v", null, null, false)).ToArray();

    // ---- settings defaults ----

    [Fact]
    public void New_settings_default_to_the_old_look_and_no_fps_measurement()
    {
        var s = new PerfOverlaySettings();
        Assert.False(s.OnlyWhenGame);
        Assert.Equal(1.0, s.GraphScale);
        Assert.Equal(1, s.Columns);
        Assert.Equal(OverlayBackground.Off, s.Background);
        Assert.Equal(OverlayColor.Orange, s.NameColor);   // the former accent colour
        Assert.Equal(OverlayColor.White, s.ValueColor);
        Assert.False(s.ShowFps || s.ShowFrameTime || s.ShowLow1 || s.ShowLow01 || s.ShowStutter);   // FPS is opt-in
        Assert.False(s.ShowGpuClock || s.ShowGpuVoltage || s.ShowVramClock);
        Assert.False(PerfOverlayLayout.AnyFpsValue(s));
    }

    [Fact]
    public void A_settings_file_from_before_phase_8b_loads_with_the_new_defaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wki_perfstyle_" + Guid.NewGuid().ToString("N"));
        try
        {
            var svc = new SettingsService(dir);
            svc.Current.SchemaVersion = SettingsService.CurrentSchemaVersion;
            svc.Current.Output.ClipsFolder = Path.Combine(dir, "clips");
            svc.Current.Output.ScreenshotsFolder = Path.Combine(dir, "shots");
            svc.Current.Output.BufferFolder = Path.Combine(dir, "buffer");
            svc.Current.PerfOverlay.Enabled = true;
            svc.Current.PerfOverlay.Scale = 1.5;
            svc.Save();

            // Strip every field Phase 8b added, as a file written by the previous version would lack them.
            var root = JsonNode.Parse(File.ReadAllText(svc.SettingsFilePath))!.AsObject();
            var perf = root["PerfOverlay"]!.AsObject();
            foreach (var name in new[] { "OnlyWhenGame", "GraphScale", "Columns", "Background", "BackgroundTransparency",
                                         "NameColor", "ValueColor", "ShowFps", "ShowFrameTime", "ShowLow1", "ShowLow01",
                                         "ShowStutter", "ShowGpuClock", "ShowGpuVoltage", "ShowVramClock" })
                Assert.True(perf.Remove(name), name + " should have been serialized");
            File.WriteAllText(svc.SettingsFilePath, root.ToJsonString());

            var loaded = new SettingsService(dir).Load().PerfOverlay;
            Assert.True(loaded.Enabled);
            Assert.Equal(1.5, loaded.Scale);                 // untouched fields survive
            Assert.Equal(1, loaded.Columns);
            Assert.Equal(1.0, loaded.GraphScale);
            Assert.Equal(OverlayBackground.Off, loaded.Background);
            Assert.Equal(OverlayColor.Orange, loaded.NameColor);
            Assert.False(loaded.ShowFps);
            Assert.False(loaded.OnlyWhenGame);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // ---- clamps ----

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(9, 3)]
    public void Columns_stay_between_1_and_3(int input, int expected)
        => Assert.Equal(expected, PerfOverlayLayout.ClampColumns(input));

    [Theory]
    [InlineData(0.1, 0.5)]
    [InlineData(9.0, 2.0)]
    [InlineData(1.25, 1.25)]
    [InlineData(double.NaN, 1.0)]
    public void Graph_size_stays_between_50_and_200_percent(double input, double expected)
        => Assert.Equal(expected, PerfOverlayLayout.ClampGraphScale(input));

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(40, 40)]
    [InlineData(250, 100)]
    public void Transparency_stays_between_0_and_100_percent(int input, int expected)
        => Assert.Equal(expected, PerfOverlayLayout.ClampTransparency(input));

    // ---- columns ----

    [Fact]
    public void One_column_keeps_everything_in_order()
    {
        var cols = PerfOverlayLayout.Columns(Many(5), 1);
        Assert.Single(cols);
        Assert.Equal(new[] { "L0", "L1", "L2", "L3", "L4" }, cols[0].Select(l => l.Label));
    }

    [Fact]
    public void Values_flow_column_wise_down_each_column_first()
    {
        var cols = PerfOverlayLayout.Columns(Many(5), 2);
        Assert.Equal(2, cols.Count);
        Assert.Equal(new[] { "L0", "L1", "L2" }, cols[0].Select(l => l.Label));
        Assert.Equal(new[] { "L3", "L4" }, cols[1].Select(l => l.Label));
    }

    [Fact]
    public void Three_columns_fill_evenly_from_the_left()
    {
        var cols = PerfOverlayLayout.Columns(Many(7), 3);
        Assert.Equal(new[] { 3, 3, 1 }, cols.Select(c => c.Count));
    }

    [Fact]
    public void Empty_columns_are_not_produced()
    {
        // 4 lines in 3 columns -> 2 rows per column -> only two columns are needed.
        Assert.Equal(new[] { 2, 2 }, PerfOverlayLayout.Columns(Many(4), 3).Select(c => c.Count));
        Assert.Equal(new[] { 1, 1 }, PerfOverlayLayout.Columns(Many(2), 3).Select(c => c.Count));
        Assert.Empty(PerfOverlayLayout.Columns(Many(0), 3));
    }

    [Fact]
    public void An_out_of_range_column_count_is_clamped_not_trusted()
        => Assert.Equal(3, PerfOverlayLayout.Columns(Many(9), 77).Count);

    // ---- background and colours ----

    [Fact]
    public void No_background_means_no_fill()
        => Assert.Null(PerfOverlayLayout.BackdropFill(OverlayBackground.Off, 0));

    [Fact]
    public void Transparency_percent_becomes_the_inverse_alpha()
    {
        Assert.Equal((byte)255, PerfOverlayLayout.BackdropFill(OverlayBackground.Black, 0)!.Value.A);
        Assert.Equal((byte)0, PerfOverlayLayout.BackdropFill(OverlayBackground.Black, 100)!.Value.A);
        Assert.Equal((byte)128, PerfOverlayLayout.BackdropFill(OverlayBackground.Black, 50)!.Value.A);
        Assert.Equal((byte)255, PerfOverlayLayout.BackdropFill(OverlayBackground.Black, -20)!.Value.A);   // clamped
    }

    [Fact]
    public void Black_is_black_and_violet_is_a_violet()
    {
        var black = PerfOverlayLayout.BackdropFill(OverlayBackground.Black, 0)!.Value;
        Assert.Equal((0, 0, 0), (black.R, black.G, black.B));
        var violet = PerfOverlayLayout.BackdropFill(OverlayBackground.Violet, 0)!.Value;
        Assert.True(violet.B > violet.G && violet.R > violet.G, "violet = blue and red above green");
    }

    [Fact]
    public void A_background_panel_gets_more_padding_than_bare_numbers()
        => Assert.True(PerfOverlayLayout.BackdropPadding(OverlayBackground.Black) > PerfOverlayLayout.BackdropPadding(OverlayBackground.Off));

    [Fact]
    public void Every_preset_colour_is_opaque_and_they_are_all_different()
    {
        var all = Enum.GetValues<OverlayColor>().Select(PerfOverlayLayout.ColorOf).ToArray();
        Assert.All(all, c => Assert.Equal((byte)255, c.A));
        Assert.Equal(all.Length, all.Distinct().Count());
    }

    [Fact]
    public void The_wki_orange_preset_is_the_brand_colour_ff7a2c()
    {
        var c = PerfOverlayLayout.ColorOf(OverlayColor.Orange);
        Assert.Equal((0xFF, 0x7A, 0x2C), (c.R, c.G, c.B));
    }

    [Fact]
    public void An_unknown_colour_value_falls_back_to_white()
    {
        var c = PerfOverlayLayout.ColorOf((OverlayColor)99);
        Assert.Equal((0xFF, 0xFF, 0xFF), (c.R, c.G, c.B));
    }

    // ---- visibility ----

    [Theory]
    [InlineData(true, false, false, true)]     // always on
    [InlineData(true, true, false, false)]     // game-only, no game
    [InlineData(true, true, true, true)]       // game-only, game in front
    [InlineData(false, false, true, false)]    // switched off
    [InlineData(false, true, true, false)]
    public void The_overlay_shows_when_enabled_and_if_asked_only_with_a_game(bool enabled, bool onlyWhenGame, bool game, bool shown)
        => Assert.Equal(shown, PerfOverlayLayout.ShouldShow(new PerfOverlaySettings { Enabled = enabled, OnlyWhenGame = onlyWhenGame }, game));

    // ---- opt-in FPS measurement ----

    [Fact]
    public void Each_fps_tick_alone_counts_as_an_fps_value()
    {
        foreach (var set in new Action<PerfOverlaySettings>[]
        {
            s => s.ShowFps = true, s => s.ShowFrameTime = true, s => s.ShowLow1 = true,
            s => s.ShowLow01 = true, s => s.ShowStutter = true,
        })
        {
            var s = new PerfOverlaySettings();
            set(s);
            Assert.True(PerfOverlayLayout.AnyFpsValue(s));
        }
    }

    [Theory]
    [InlineData(true, true, false, true)]      // ticked + overlay shown
    [InlineData(true, false, true, true)]      // ticked + widget shown
    [InlineData(true, true, true, true)]
    [InlineData(true, false, false, false)]    // ticked but nothing visible: no ETW session
    [InlineData(false, true, true, false)]     // visible but nothing ticked: no ETW session
    [InlineData(false, false, false, false)]
    public void The_fps_session_runs_only_for_a_ticked_value_that_somebody_can_see(
        bool ticked, bool overlayShown, bool widgetShown, bool measure)
    {
        var s = new PerfOverlaySettings { ShowLow01 = ticked };
        Assert.Equal(measure, PerfOverlayLayout.ShouldMeasureFps(s, overlayShown, widgetShown));
    }

    // ---- FPS lines ----

    [Fact]
    public void Fps_values_come_first_in_a_fixed_order_with_their_numbers()
    {
        var s = OnlyFps();
        s.ShowCpu = true;
        var lines = LinesOf(s, Good);

        Assert.Equal(new[]
        {
            "FPS", L.T("Framezeit", "Frame time"), "1 % Low", L.T("0,1 % Low", "0.1 % low"),
            L.T("Mikro-Stutter", "Micro-stutter"), "CPU"
        }, lines.Select(l => l.Label));
        Assert.Equal("144", lines[0].Value);
        Assert.Equal($"{6.96:F1} ms", lines[1].Value);
        Assert.Equal("118", lines[2].Value);
        Assert.Equal("101", lines[3].Value);
        Assert.Equal($"{2.5:F1} %", lines[4].Value);
    }

    [Fact]
    public void Only_ticked_fps_values_appear()
    {
        var s = OnlyFps();
        s.ShowFrameTime = s.ShowLow1 = s.ShowStutter = false;
        var lines = LinesOf(s, Good);
        Assert.Equal(new[] { "FPS", L.T("0,1 % Low", "0.1 % low") }, lines.Select(l => l.Label));
    }

    [Fact]
    public void Without_any_fps_tick_the_snapshot_is_ignored()
        => Assert.DoesNotContain(LinesOf(new PerfOverlaySettings(), Good), l => l.Label == "FPS");

    [Fact]
    public void A_missing_permission_is_said_instead_of_a_number()
    {
        var lines = LinesOf(OnlyFps(), FrameMetricsSnapshot.NoPermission);
        Assert.Equal(5, lines.Count);
        Assert.All(lines, l => Assert.Equal(L.T("k. A. (Recht fehlt)", "n/a (permission missing)"), l.Value));
    }

    [Fact]
    public void No_game_shows_a_dash()
        => Assert.All(LinesOf(OnlyFps(), FrameMetricsSnapshot.NoGame), l => Assert.Equal("–", l.Value));

    [Theory]
    [InlineData(FrameMetricsStatus.NoData)]
    [InlineData(FrameMetricsStatus.EventsLost)]
    [InlineData(FrameMetricsStatus.Unavailable)]
    public void Other_states_without_values_show_not_available(FrameMetricsStatus status)
        => Assert.All(LinesOf(OnlyFps(), new FrameMetricsSnapshot(status, 7)), l => Assert.Equal(L.T("k. A.", "n/a"), l.Value));

    [Fact]
    public void No_snapshot_at_all_is_not_available_too()
        => Assert.All(LinesOf(OnlyFps(), null), l => Assert.Equal(L.T("k. A.", "n/a"), l.Value));

    [Fact]
    public void Lows_that_need_more_frames_are_not_available_while_the_fps_is()
    {
        var early = new FrameMetricsSnapshot(FrameMetricsStatus.Ok, 1, PresentApi.Dxgi, Fps: 60, AvgFrameTimeMs: 16.7);
        var lines = LinesOf(OnlyFps(), early);
        Assert.Equal("60", lines[0].Value);
        Assert.Equal(L.T("k. A.", "n/a"), lines[2].Value);
        Assert.Equal(L.T("k. A.", "n/a"), lines[4].Value);
    }

    // ---- GPU clock / voltage / VRAM clock ----

    [Fact]
    public void Gpu_clock_voltage_and_vram_clock_are_shown_in_their_units()
    {
        var s = new PerfOverlaySettings
        {
            ShowCpu = false, ShowGpu = false, ShowGpuTemp = false, ShowRam = false, ShowVram = false,
            ShowGpuClock = true, ShowGpuVoltage = true, ShowVramClock = true
        };
        var lines = LinesOf(s, null);
        Assert.Equal(new[] { L.T("GPU-Takt", "GPU clock"), L.T("GPU-Spannung", "GPU voltage"), L.T("VRAM-Takt", "VRAM clock") },
                     lines.Select(l => l.Label));
        Assert.Equal("2310 MHz", lines[0].Value);
        Assert.Equal("1020 mV", lines[1].Value);
        Assert.Equal("2505 MHz", lines[2].Value);
    }

    [Fact]
    public void Clock_and_voltage_the_driver_does_not_report_are_hidden()
    {
        var s = new PerfOverlaySettings
        {
            ShowCpu = false, ShowGpu = false, ShowGpuTemp = false, ShowRam = false, ShowVram = false,
            ShowGpuClock = true, ShowGpuVoltage = true, ShowVramClock = true
        };
        Assert.Empty(LinesOf(s, null, Sample(clock: null, mv: null, vramClock: null)));
    }

    [Fact]
    public void Gpu_clock_sits_with_the_gpu_values_and_the_vram_clock_with_vram()
    {
        var s = new PerfOverlaySettings { ShowGpuClock = true, ShowGpuVoltage = true, ShowVramClock = true, ShowGraphs = false };
        var labels = LinesOf(s, null).Select(l => l.Label).ToList();
        int gpu = labels.IndexOf("GPU"), clk = labels.IndexOf(L.T("GPU-Takt", "GPU clock"));
        int temp = labels.IndexOf("GPU °C");
        int vram = labels.IndexOf("VRAM"), vclk = labels.IndexOf(L.T("VRAM-Takt", "VRAM clock"));
        Assert.True(gpu < clk && clk < temp, "GPU, GPU clock, voltage, temperature");
        Assert.True(vram < vclk, "VRAM before VRAM clock");
    }
}
