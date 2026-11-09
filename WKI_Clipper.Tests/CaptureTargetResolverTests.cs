using System;
using System.Collections.Generic;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;
using CaptureSource = WKI_Clipper.Services.CaptureSource;

namespace WKI_Clipper.Tests;

/// <summary>Ready-made capture sources for the command builder tests (no topology needed).</summary>
internal static class TestSources
{
    /// <summary>A monitor recorded through ddagrab output <paramref name="output"/>; <paramref name="amf"/> = vsrc_amf's index when certain.</summary>
    public static CaptureSource Output(int output, int? amf = null, int width = 2560, int height = 1440)
    {
        var display = TestDisplays.Dell(output: output, w: width, h: height);
        var resolution = new MonitorResolution(MonitorResolutionStatus.Exact, display, "test", UpgradeSavedRef: false);
        return new CaptureSource(display.DevicePath!.ToLowerInvariant(), display.GdiName, display.HMonitor, output,
                                 TestDisplays.Dgpu.Luid, amf, width, height, "DELL S2721DGF · test", resolution);
    }

    /// <summary>The same as AMF would see it: the mapping is certain, both indices agree.</summary>
    public static CaptureSource Certain(int output = 0) => Output(output, amf: output);

    public static DisplayTopologyCache Cache(DisplayTopology topology) => new(() => topology, () => "fixed");
}

public sealed class CaptureTargetResolverTests
{
    private static AppSettings Settings() => new();

    private static CaptureProfile MonitorProfile(MonitorRef? saved) => new()
    {
        Mode = CaptureMode.Monitor,
        MonitorDevicePath = saved?.DevicePath,
        MonitorDeviceName = saved?.GdiName,
        MonitorFriendlyName = saved?.FriendlyName,
        MonitorEdidMfg = saved?.EdidMfg,
        MonitorEdidProduct = saved?.EdidProduct
    };

    /// <summary>Counts the upgrades raised while it is alive (the event is static).</summary>
    private sealed class UpgradeRecorder : IDisposable
    {
        public List<MonitorRef> Raised { get; } = new();
        public UpgradeRecorder() => CaptureTargetResolver.MonitorRefUpgrade += OnUpgrade;
        private void OnUpgrade(MonitorRef r) { lock (Raised) Raised.Add(r); }
        public void Dispose() => CaptureTargetResolver.MonitorRefUpgrade -= OnUpgrade;
    }

    [Fact]
    public void Monitor_mode_uses_the_dxgi_output_of_the_saved_path()
    {
        // The DELL is first in the enumeration and owns \\.\DISPLAY1 now; DXGI says it is output 2.
        var dell = TestDisplays.Dell(gdi: @"\\.\DISPLAY1", output: 2);
        var lg = TestDisplays.Lg(gdi: @"\\.\DISPLAY3");
        var topo = TestDisplays.Topology(dell, lg);
        var saved = MonitorRef.Of(dell) with { GdiName = @"\\.\DISPLAY3" };   // stale slot: now the LG's

        var plan = CaptureTargetResolver.Resolve(MonitorProfile(saved), Settings(), TestSources.Cache(topo));

        Assert.Equal(2, plan.Source.OutputIndex);
        Assert.Equal(dell.HMonitor, plan.Source.HMonitor);
        Assert.Equal(dell.DevicePath!.ToLowerInvariant(), plan.Source.DisplayKey);
        Assert.Equal(MonitorResolutionStatus.Exact, plan.Source.Resolution.Status);
        Assert.Null(plan.Warning);
        Assert.Equal("DELL S2721DGF · 2560×1440 · rechts", plan.VideoLabel);
        Assert.Contains("ddagrab=output_idx=2:", FFmpegCommandBuilder.Build(Settings(), "out.mp4", segmentOutput: false, source: plan.Source));
    }

    [Fact]
    public void Monitor_mode_fallback_sets_warning_and_label()
    {
        var lg = TestDisplays.Lg();
        var topo = TestDisplays.Topology(TestDisplays.Dell(), lg);
        var samsung = TestDisplays.Samsung();
        var profile = MonitorProfile(samsung);
        using var upgrades = new UpgradeRecorder();

        var plan = CaptureTargetResolver.Resolve(profile, Settings(), TestSources.Cache(topo));

        Assert.True(plan.Source.Resolution.IsFallback);
        Assert.Equal(lg.HMonitor, plan.Source.HMonitor);
        Assert.Equal(0, plan.Source.OutputIndex);
        Assert.Equal("Ersatz: LG ULTRAGEAR+ · 3440×1440 · Hauptmonitor (SAMSUNG Odyssey G9 nicht verbunden)", plan.VideoLabel);
        Assert.Equal("Gewählter Monitor „SAMSUNG Odyssey G9“ ist nicht verbunden – nehme Hauptmonitor „LG ULTRAGEAR+“",
                     plan.Warning);
        // A fallback never touches the setting.
        Assert.Empty(upgrades.Raised.FindAll(r => r.DevicePath == samsung.DevicePath || r.FriendlyName == lg.FriendlyName));
        Assert.Equal(samsung.DevicePath, profile.MonitorDevicePath);
        Assert.Equal(samsung.FriendlyName, profile.MonitorFriendlyName);
    }

    [Fact]
    public void Reresolve_keeps_window_and_audio_and_follows_the_monitor()
    {
        var dell = TestDisplays.Dell();
        var profile = MonitorProfile(MonitorRef.Of(dell));
        var plan = CaptureTargetResolver.Resolve(profile, Settings(), TestSources.Cache(TestDisplays.Topology(TestDisplays.Lg(), dell)))
            with { SysMode = SystemAudioMode.Process, AudioPid = 4242, AudioLabel = "Arma Reforger", TargetProcess = "ArmaReforgerSteam" };

        // The DELL switched to 1080p (exclusive-fullscreen game).
        var small = TestDisplays.Dell(w: 1920, h: 1080);
        var next = CaptureTargetResolver.Reresolve(plan, profile, TestSources.Cache(TestDisplays.Topology(TestDisplays.Lg(), small)));

        Assert.Equal((1920, 1080), (next.Source.Width, next.Source.Height));
        Assert.True(CaptureSourceRules.SameMonitor(plan.Source, next.Source));
        Assert.Equal((plan.SysMode, plan.AudioPid, plan.AudioLabel, plan.TargetProcess, plan.Hwnd, plan.UseWgc),
                     (next.SysMode, next.AudioPid, next.AudioLabel, next.TargetProcess, next.Hwnd, next.UseWgc));
        Assert.Equal("DELL S2721DGF · 1920×1080 · rechts", next.VideoLabel);
        Assert.Null(next.Warning);

        // The chosen monitor is gone meanwhile: the primary, with the warning.
        var gone = CaptureTargetResolver.Reresolve(plan, profile, TestSources.Cache(TestDisplays.Topology(TestDisplays.Lg())));
        Assert.True(gone.Source.Resolution.IsFallback);
        Assert.NotNull(gone.Warning);
        Assert.Throws<InvalidOperationException>(() =>
            CaptureTargetResolver.Reresolve(plan, profile, TestSources.Cache(DisplayTopology.Empty("test: no displays"))));
    }

    [Fact]
    public void Monitor_on_another_gpu_names_both_gpus_in_the_warning()
    {
        var onIgpu = TestDisplays.Dell(gdi: @"\\.\DISPLAY6", adapter: 1, output: 0);
        var topo = TestDisplays.Topology(TestDisplays.Lg(), onIgpu);

        var plan = CaptureTargetResolver.Resolve(MonitorProfile(MonitorRef.Of(onIgpu)), Settings(), TestSources.Cache(topo));

        Assert.Equal(0, plan.Source.OutputIndex);
        Assert.Equal("Gewählter Monitor „DELL S2721DGF“ hängt an „AMD Radeon(TM) Graphics“, die Aufnahme erreicht nur "
                   + "„AMD Radeon RX 9070 XT“ – nehme Hauptmonitor „LG ULTRAGEAR+“", plan.Warning);
    }

    [Fact]
    public void Legacy_gdi_setting_raises_one_upgrade()
    {
        var dell = TestDisplays.Dell();
        var topo = TestDisplays.Topology(TestDisplays.Lg(), dell);
        var cache = TestSources.Cache(topo);
        var profile = new CaptureProfile { Mode = CaptureMode.Monitor, MonitorDeviceName = @"\\.\DISPLAY2" };
        using var upgrades = new UpgradeRecorder();

        var plan = CaptureTargetResolver.Resolve(profile, Settings(), cache);

        Assert.Equal(MonitorResolutionStatus.Legacy, plan.Source.Resolution.Status);
        Assert.Equal(1, plan.Source.OutputIndex);
        var raised = Assert.Single(upgrades.Raised.FindAll(r => r.DevicePath == dell.DevicePath));
        Assert.Equal(MonitorRef.Of(dell), raised);
        // The resolver only reports it; writing the settings is the app's job.
        Assert.Null(profile.MonitorDevicePath);

        // What the app saves: from then on the path is found exactly, and nothing is raised again.
        profile.MonitorDevicePath = raised.DevicePath;
        profile.MonitorDeviceName = raised.GdiName;
        profile.MonitorFriendlyName = raised.FriendlyName;
        profile.MonitorEdidMfg = raised.EdidMfg;
        profile.MonitorEdidProduct = raised.EdidProduct;
        var again = CaptureTargetResolver.Resolve(profile, Settings(), cache);

        Assert.Equal(MonitorResolutionStatus.Exact, again.Source.Resolution.Status);
        Assert.Single(upgrades.Raised.FindAll(r => r.DevicePath == dell.DevicePath));
    }

    [Fact]
    public void No_displays_throws_with_the_reason()
    {
        var topo = DisplayTopology.Empty("DXGI failed");

        var ex = Assert.Throws<InvalidOperationException>(
            () => CaptureTargetResolver.Resolve(MonitorProfile(null), Settings(), TestSources.Cache(topo)));

        Assert.Contains("Windows meldet keinen Monitor", ex.Message);
    }

    [Fact]
    public void Window_mode_maps_its_hwnd_monitor_through_the_topology()
    {
        // The window's monitor is first in the enumeration but DXGI output 1: never a list position.
        var dell = TestDisplays.Dell(output: 1);
        var topo = TestDisplays.Topology(dell, TestDisplays.Lg());
        var profile = new CaptureProfile { Mode = CaptureMode.Window, TargetProcessName = "notepad" };

        var video = CaptureTargetResolver.ResolveVideo(profile, "notepad", hwnd: 0x1234, windowMonitor: dell.HMonitor,
                                                        useWgc: false, topo);

        Assert.Equal(dell.HMonitor, video.Source.HMonitor);
        Assert.Equal(1, video.Source.OutputIndex);
        Assert.Equal(MonitorResolutionStatus.FollowsTarget, video.Source.Resolution.Status);
        Assert.Null(video.Warning);
        Assert.Equal("notepad → DELL S2721DGF · 2560×1440 · rechts", video.Label);
    }

    [Fact]
    public void Window_without_a_known_monitor_takes_the_primary_without_a_warning()
    {
        var lg = TestDisplays.Lg();
        var topo = TestDisplays.Topology(TestDisplays.Dell(), lg);
        var auto = new CaptureProfile { Mode = CaptureMode.Auto };

        var noWindow = CaptureTargetResolver.ResolveVideo(auto, null, hwnd: 0, windowMonitor: 0, useWgc: false, topo);
        var unknown = CaptureTargetResolver.ResolveVideo(auto, "game", hwnd: 0x1234, windowMonitor: 0x999, useWgc: false, topo);

        Assert.Equal(lg.HMonitor, noWindow.Source.HMonitor);
        Assert.Equal(MonitorResolutionStatus.PrimaryByDefault, noWindow.Source.Resolution.Status);
        Assert.Null(noWindow.Warning);
        Assert.Equal("Automatik: LG ULTRAGEAR+ · 3440×1440 · Hauptmonitor", noWindow.Label);
        Assert.Equal(lg.HMonitor, unknown.Source.HMonitor);
        Assert.Null(unknown.Warning);
    }

    [Fact]
    public void Window_on_a_monitor_of_another_gpu_warns_only_when_the_monitor_is_recorded()
    {
        var onIgpu = TestDisplays.Dell(gdi: @"\\.\DISPLAY6", adapter: 1, output: 0);
        var lg = TestDisplays.Lg();
        var topo = TestDisplays.Topology(lg, onIgpu);
        var window = new CaptureProfile { Mode = CaptureMode.Window, TargetProcessName = "game" };

        var dda = CaptureTargetResolver.ResolveVideo(window, "game", hwnd: 0x1234, windowMonitor: onIgpu.HMonitor, useWgc: false, topo);
        var wgc = CaptureTargetResolver.ResolveVideo(window, "game", hwnd: 0x1234, windowMonitor: onIgpu.HMonitor, useWgc: true, topo);

        Assert.Equal(lg.HMonitor, dda.Source.HMonitor);
        Assert.True(dda.Source.Resolution.IsFallback);
        Assert.Contains("„DELL S2721DGF“ hängt an „AMD Radeon(TM) Graphics“", dda.Warning);
        Assert.Contains("nehme Hauptmonitor „LG ULTRAGEAR+“", dda.Warning);
        Assert.Equal("game → Ersatz: LG ULTRAGEAR+ · 3440×1440 · Hauptmonitor", dda.Label);
        // WGC records the window itself: the monitor does not matter for the footage.
        Assert.Null(wgc.Warning);
    }

    [Fact]
    public void Screenshot_uses_the_resolved_output_index()
    {
        var dell = TestDisplays.Dell(output: 2);
        var topo = TestDisplays.Topology(dell, TestDisplays.Lg());
        var plan = CaptureTargetResolver.Resolve(MonitorProfile(MonitorRef.Of(dell)), Settings(), TestSources.Cache(topo));

        var args = FFmpegCommandBuilder.BuildScreenshot("shot.png", outputIndex: plan.Source.OutputIndex);

        Assert.Contains("ddagrab=output_idx=2:framerate=1", args);
    }

    [Theory]
    [InlineData("LG ULTRAGEAR+", "Shot_LG-ULTRAGEAR+_2026-11-09_12-00-00.png")]
    [InlineData("DELL S2721DGF", "Shot_DELL-S2721DGF_2026-11-09_12-00-00.png")]
    [InlineData("A/B: \"C\"", "Shot_A-B-C_2026-11-09_12-00-00.png")]
    [InlineData(null, "Shot_Display_2026-11-09_12-00-00.png")]
    [InlineData("   ", "Shot_Display_2026-11-09_12-00-00.png")]
    public void Screenshot_file_is_named_after_the_monitor(string? friendlyName, string expected)
        => Assert.Equal(expected, ScreenshotService.FileName(friendlyName, "2026-11-09_12-00-00"));
}

/// <summary>
/// The resolver on the REAL topology of this machine. Gated behind DISPLAY_LIVE=1 like the reader's live tests: the
/// result depends on the monitors. Reads only (topology, foreground window, cursor); changes nothing.
/// </summary>
public sealed class CaptureTargetResolverLiveTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public CaptureTargetResolverLiveTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("DISPLAY_LIVE") == "1";

    [Fact]
    public void Primary_and_active_monitor_resolve_to_dxgi_outputs_of_the_default_adapter()
    {
        if (!Enabled) return;
        var cache = new DisplayTopologyCache(DisplayTopologyReader.Read, DisplayTopologyReader.QuickKey);
        var topo = cache.Get();
        _out.WriteLine(topo.Dump());

        var primary = CaptureTargetResolver.Resolve(new CaptureProfile { Mode = CaptureMode.Monitor }, new AppSettings(), cache);
        var active = CaptureTargetResolver.ResolveActiveMonitor(cache);
        _out.WriteLine($"primary: {primary.VideoLabel} output={primary.Source.OutputIndex} amf={primary.Source.AmfIndex?.ToString() ?? "-"}");
        _out.WriteLine($"active: {active.Label} output={active.OutputIndex} ({active.Resolution.Status}: {active.Resolution.Reason})");

        Assert.Equal(MonitorResolutionStatus.PrimaryByDefault, primary.Source.Resolution.Status);
        Assert.Equal(0, primary.Source.OutputIndex);   // DXGI output 0 is the primary monitor
        Assert.Equal(topo.Primary!.HMonitor, primary.Source.HMonitor);
        Assert.NotNull(topo.ByHMonitor(active.HMonitor));
    }
}
