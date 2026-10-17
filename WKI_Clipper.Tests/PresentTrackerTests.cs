using System;
using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Stream selection and status logic between the parsed ETW events and the frame math:
/// target process, dominant swap chain, runtime-vs-kernel arbitration (never double count
/// D3D11/12), resets, lost events and staleness. Synthetic events, QPC at 10 MHz.
/// </summary>
public class PresentTrackerTests
{
    private const long Freq = 10_000_000;
    private const long T0 = 7_000_000_000;
    private const int Game = 4242;
    private const int OtherGame = 5151;
    private const ulong MainChain = 0x1_0000_1000;
    private const ulong SideChain = 0x1_0000_2000;

    private static long Ms(double ms) => (long)Math.Round(ms * Freq / 1000.0);

    private static IEnumerable<PresentEvent> Stream(PresentSource source, ulong chain, long start, double intervalMs, double seconds, int pid = Game)
    {
        int count = (int)Math.Round(seconds * 1000 / intervalMs);
        for (int i = 0; i < count; i++)
            yield return new PresentEvent(pid, source, chain, start + Ms(i * intervalMs));
    }

    /// <summary>Feeds the merged streams in timestamp order, like ETW delivers them; returns the last timestamp.</summary>
    private static long Feed(PresentTracker t, params IEnumerable<PresentEvent>[] streams)
    {
        long last = 0;
        foreach (var e in streams.SelectMany(s => s).OrderBy(e => e.Timestamp))
        {
            t.OnPresent(e);
            last = e.Timestamp;
        }
        return last;
    }

    private static PresentTracker NewTracker(int pid = Game)
    {
        var t = new PresentTracker(Freq);
        t.SetTarget(pid);
        return t;
    }

    [Fact]
    public void Without_a_target_there_is_no_game()
    {
        var t = new PresentTracker(Freq);
        var s = t.Snapshot(T0);
        Assert.Equal(FrameMetricsStatus.NoGame, s.Status);
        Assert.Null(s.Fps);
    }

    [Fact]
    public void A_steady_dxgi_game_reads_its_frame_rate()
    {
        var t = NewTracker();
        long last = Feed(t, Stream(PresentSource.Dxgi, MainChain, T0, 10, 2));

        var s = t.Snapshot(last + Ms(500));
        Assert.Equal(FrameMetricsStatus.Ok, s.Status);
        Assert.Equal(Game, s.ProcessId);
        Assert.Equal(PresentApi.Dxgi, s.Api);
        Assert.Equal(100, s.Fps!.Value, 1);
        Assert.Equal(10, s.AvgFrameTimeMs!.Value, 2);
        Assert.Equal(100, s.Low1!.Value, 1);
        Assert.Equal(0, s.StutterPercent!.Value, 3);
    }

    [Fact]
    public void Presents_of_other_processes_are_ignored()
    {
        var t = NewTracker();
        long last = Feed(t, Stream(PresentSource.Dxgi, MainChain, T0, 10, 2, pid: OtherGame));
        var s = t.Snapshot(last);
        Assert.Equal(FrameMetricsStatus.NoData, s.Status);
        Assert.Null(s.Fps);
    }

    [Fact]
    public void Switching_the_target_process_starts_over()
    {
        var t = NewTracker();
        long last = Feed(t, Stream(PresentSource.Dxgi, MainChain, T0, 10, 2));
        Assert.Equal(FrameMetricsStatus.Ok, t.Snapshot(last).Status);

        t.SetTarget(OtherGame);
        Assert.Equal(FrameMetricsStatus.NoData, t.Snapshot(last).Status);
        Assert.Equal(0, t.FrameCount);

        last = Feed(t, Stream(PresentSource.Dxgi, MainChain, last + Ms(5), 20, 2, pid: OtherGame));
        var s = t.Snapshot(last);
        Assert.Equal(OtherGame, s.ProcessId);
        Assert.Equal(50, s.Fps!.Value, 1);
    }

    [Fact]
    public void A_secondary_swap_chain_of_the_game_is_not_counted()
    {
        var t = NewTracker();
        long last = Feed(t,
            Stream(PresentSource.Dxgi, MainChain, T0, 10, 3),
            Stream(PresentSource.Dxgi, SideChain, T0 + Ms(3), 100, 3));
        Assert.Equal(100, t.Snapshot(last).Fps!.Value, 1);
    }

    [Fact]
    public void A_splash_swap_chain_that_presents_first_loses_to_the_busy_one()
    {
        var t = NewTracker();
        long last = Feed(t,
            Stream(PresentSource.Dxgi, SideChain, T0, 100, 3),               // first event comes from here
            Stream(PresentSource.Dxgi, MainChain, T0 + Ms(7), 10, 3));
        var s = t.Snapshot(last);
        Assert.Equal(100, s.Fps!.Value, 1);
    }

    [Fact]
    public void Two_equally_busy_swap_chains_do_not_flap()
    {
        // e.g. a game with a second window. Counting one of them steadily is right; resetting
        // every second would keep the lows from ever appearing.
        var t = NewTracker();
        long last = Feed(t,
            Stream(PresentSource.Dxgi, MainChain, T0, 1000.0 / 60, 10),
            Stream(PresentSource.Dxgi, SideChain, T0 + Ms(5), 1000.0 / 60, 10));

        var s = t.Snapshot(last);
        Assert.Equal(60, s.Fps!.Value, 0);          // one chain, not both (120)
        Assert.True(t.FrameCount >= 590, $"stats were reset along the way: {t.FrameCount} frames");
        Assert.NotNull(s.Low1);
    }

    [Fact]
    public void A_recreated_swap_chain_takes_over_and_starts_fresh()
    {
        // Resolution change / alt-enter: the old chain stops, a new one carries on.
        var t = NewTracker();
        long last = Feed(t,
            Stream(PresentSource.Dxgi, MainChain, T0, 10, 3),
            Stream(PresentSource.Dxgi, SideChain, T0 + Ms(3005), 10, 3));

        var s = t.Snapshot(last);
        Assert.Equal(100, s.Fps!.Value, 1);
        Assert.InRange(t.FrameCount, 100, 200);     // reset at the switch, then ≥ 1 s of the new chain
        Assert.Equal(100, s.Low01!.Value, 1);        // no bogus long frame across the switch
    }

    [Fact]
    public void A_d3d11_game_emitting_dxgi_and_kernel_events_is_not_double_counted()
    {
        // Worst case: the kernel event of each frame is delivered before the DXGI one.
        var t = NewTracker();
        long last = Feed(t,
            Stream(PresentSource.KernelPresentHistory, 0, T0, 10, 3),
            Stream(PresentSource.KernelPresentInfo, 0, T0 + Ms(0.5), 10, 3),
            Stream(PresentSource.Dxgi, MainChain, T0 + Ms(1), 10, 3));

        var s = t.Snapshot(last);
        Assert.Equal(PresentApi.Dxgi, s.Api);
        Assert.Equal(100, s.Fps!.Value, 1);
    }

    [Fact]
    public void A_vulkan_game_is_measured_from_kernel_events()
    {
        var t = NewTracker();
        long last = Feed(t, Stream(PresentSource.KernelPresentHistory, 0, T0, 1000.0 / 144, 3));

        var s = t.Snapshot(last);
        Assert.Equal(FrameMetricsStatus.Ok, s.Status);
        Assert.Equal(PresentApi.Other, s.Api);
        Assert.Equal(144, s.Fps!.Value, 0);
    }

    [Fact]
    public void Two_kernel_markers_per_frame_count_once()
    {
        var t = NewTracker();
        long last = Feed(t,
            Stream(PresentSource.KernelPresentHistory, 0, T0, 1000.0 / 144, 3),
            Stream(PresentSource.KernelPresentInfo, 0, T0 + Ms(0.3), 1000.0 / 144, 3));
        Assert.Equal(144, t.Snapshot(last).Fps!.Value, 0);
    }

    [Fact]
    public void Runtime_events_take_over_from_the_kernel_fallback()
    {
        var t = NewTracker();
        long last = Feed(t, Stream(PresentSource.KernelPresentHistory, 0, T0, 10, 3));
        Assert.Equal(PresentApi.Other, t.Snapshot(last).Api);

        long start = last + Ms(10);
        last = Feed(t,
            Stream(PresentSource.KernelPresentHistory, 0, start, 10, 3),
            Stream(PresentSource.Dxgi, MainChain, start + Ms(1), 10, 3));

        var s = t.Snapshot(last);
        Assert.Equal(PresentApi.Dxgi, s.Api);
        Assert.Equal(100, s.Fps!.Value, 1);
    }

    [Fact]
    public void Lost_events_report_unknown_instead_of_a_too_low_rate()
    {
        var t = NewTracker();
        long last = Feed(t, Stream(PresentSource.Dxgi, MainChain, T0, 10, 3));
        long lossAt = last;
        t.OnEventsLost(lossAt);

        var s = t.Snapshot(lossAt + Ms(10));
        Assert.Equal(FrameMetricsStatus.EventsLost, s.Status);
        Assert.Null(s.Fps);
        Assert.Null(s.Low1);

        // Buffers delivered late with frames from before the loss must not slip back in.
        Feed(t, Stream(PresentSource.Dxgi, MainChain, lossAt - Ms(500), 10, 0.4));
        Assert.Equal(0, t.FrameCount);

        last = Feed(t, Stream(PresentSource.Dxgi, MainChain, lossAt + Ms(10), 10, 0.3));
        Assert.Equal(FrameMetricsStatus.EventsLost, t.Snapshot(last).Status);   // 0.3 s is not enough yet

        last = Feed(t, Stream(PresentSource.Dxgi, MainChain, last + Ms(10), 10, 0.5));
        s = t.Snapshot(last);
        Assert.Equal(FrameMetricsStatus.Ok, s.Status);
        Assert.Equal(100, s.Fps!.Value, 1);
    }

    [Fact]
    public void A_game_that_stopped_presenting_has_no_data()
    {
        var t = NewTracker();
        long last = Feed(t, Stream(PresentSource.Dxgi, MainChain, T0, 10, 2));

        Assert.Equal(FrameMetricsStatus.Ok, t.Snapshot(last + Ms(1500)).Status);   // ETW flush latency
        var s = t.Snapshot(last + Ms(3500));
        Assert.Equal(FrameMetricsStatus.NoData, s.Status);
        Assert.Null(s.Fps);
    }

    [Fact]
    public void A_pending_loss_does_not_outlive_the_game()
    {
        var t = NewTracker();
        long last = Feed(t, Stream(PresentSource.Dxgi, MainChain, T0, 10, 2));
        t.OnEventsLost(last);
        Assert.Equal(FrameMetricsStatus.NoData, t.Snapshot(last + Ms(4000)).Status);
    }

    [Fact]
    public void Lost_event_notices_without_a_target_are_harmless()
    {
        var t = new PresentTracker(Freq);
        t.OnEventsLost(T0);
        Assert.Equal(FrameMetricsStatus.NoGame, t.Snapshot(T0).Status);
        t.SetTarget(Game);
        long last = Feed(t, Stream(PresentSource.Dxgi, MainChain, T0 + Ms(10), 10, 1));
        Assert.Equal(FrameMetricsStatus.Ok, t.Snapshot(last).Status);
    }
}
