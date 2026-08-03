using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Reads the REAL performance counters on this machine. Gated behind PERF_LIVE=1 —
/// values depend on the hardware, so it must not run in a normal test pass.
/// Covers the two bugs: GPU stuck at 0 %, and CPU reporting above 100 %.
/// </summary>
public sealed class PerformanceLiveTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("PERF_LIVE") == "1";

    [Fact]
    public async Task Reports_gpu_usage_and_keeps_cpu_within_bounds()
    {
        if (!Enabled) return;

        using var svc = new PerformanceMonitorService();
        var samples = new List<PerfSample>();
        svc.Sampled += s => { lock (samples) samples.Add(s); };

        svc.AddViewer();                       // starts the 1 Hz polling
        await Task.Delay(TimeSpan.FromSeconds(9));   // spans at least one full sweep
        svc.RemoveViewer();

        List<PerfSample> snapshot;
        lock (samples) snapshot = new List<PerfSample>(samples);
        Assert.True(snapshot.Count >= 5, $"expected several samples, got {snapshot.Count}");

        // The bug was: every GPU reading was 0 because a fresh rate counter always
        // returns 0 on its first read. Something is always rendering (desktop/OBS).
        Assert.Contains(snapshot, s => s.GpuPercent > 0);

        // "% Processor Utility" can exceed 100 on a turbo-boosted CPU — must be clamped.
        Assert.All(snapshot, s =>
        {
            Assert.InRange(s.CpuPercent, 0, 100);
            Assert.InRange(s.GpuPercent, 0, 100);
        });

        Assert.All(snapshot, s => Assert.True(s.RamTotalBytes > 0));
    }

    [Fact]
    public async Task Polling_stays_cheap_enough_for_a_gaming_overlay()
    {
        if (!Enabled) return;

        using var svc = new PerformanceMonitorService();
        svc.AddViewer();
        await Task.Delay(TimeSpan.FromSeconds(6));   // warm up past the first sweep

        var proc = Process.GetCurrentProcess();
        var cpuBefore = proc.TotalProcessorTime;
        var sw = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(6));
        sw.Stop();
        var used = proc.TotalProcessorTime - cpuBefore;
        svc.RemoveViewer();

        double percentOfOneCore = used.TotalMilliseconds / sw.Elapsed.TotalMilliseconds * 100;
        // Reading every engine instance each second used to cost ~190 ms/s (19 %).
        Assert.True(percentOfOneCore < 8,
            $"performance polling used {percentOfOneCore:0.0} % of one core — too heavy for an overlay");
    }
}
