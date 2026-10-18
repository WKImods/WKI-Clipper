using System;
using System.Collections.Generic;
using System.Diagnostics;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Lifecycle and status of <see cref="FrameTimeMonitor"/> against a fake session: nothing runs
/// until someone subscribes, ref-counted start/stop, permission and failure states, own-process
/// exclusion, lost events. The real ETW session is covered by FrameTimeEtwIntegrationTests.
/// </summary>
public class FrameTimeMonitorTests
{
    private const int Game = 4242;
    private static readonly long Freq = Stopwatch.Frequency;

    private sealed class FakeSession : IPresentSession
    {
        public PresentSessionStart Result = PresentSessionStart.Started;
        public long Lost;
        public int Starts, Disposals;
        public Action<PresentEvent>? OnPresent;
        public Action<long>? OnLost;
        public int TargetProcessId { get; set; }

        public PresentSessionStart Start(Action<PresentEvent> onPresent, Action<long> onLost)
        {
            Starts++;
            OnPresent = onPresent;
            OnLost = onLost;
            return Result;
        }

        public long QueryLostCount() => Lost;
        public void Dispose() => Disposals++;
    }

    private sealed class Rig
    {
        public readonly List<FakeSession> Sessions = new();
        public PresentSessionStart NextResult = PresentSessionStart.Started;
        public long Now = 1_000_000_000;
        public readonly FrameTimeMonitor Monitor;

        public Rig(bool allowOwnProcess = false)
        {
            Monitor = new FrameTimeMonitor(() =>
            {
                var s = new FakeSession { Result = NextResult };
                Sessions.Add(s);
                return s;
            }, allowOwnProcess, () => Now, Freq, autoTick: false);
        }

        public FakeSession Session => Sessions[^1];

        /// <summary>Presents of the target at <paramref name="fps"/> for <paramref name="seconds"/>, advancing the clock.</summary>
        public void Present(double fps, double seconds, int pid = Game)
        {
            long interval = (long)(Freq / fps);
            int n = (int)(fps * seconds);
            for (int i = 0; i < n; i++)
            {
                Now += interval;
                Session.OnPresent!(new PresentEvent(pid, PresentSource.Dxgi, 0x1000, Now));
            }
        }
    }

    [Fact]
    public void Nothing_runs_until_someone_subscribes()
    {
        var rig = new Rig();
        rig.Monitor.SetTargetProcess(Game);
        rig.Monitor.Tick();

        Assert.Empty(rig.Sessions);
        Assert.Equal(FrameMetricsStatus.Unavailable, rig.Monitor.Current.Status);
    }

    [Fact]
    public void The_session_is_reference_counted()
    {
        var rig = new Rig();
        var a = rig.Monitor.Subscribe();
        var b = rig.Monitor.Subscribe();
        Assert.Single(rig.Sessions);
        Assert.Equal(1, rig.Session.Starts);

        a.Dispose();
        a.Dispose();   // a lease counts once, however often it is disposed
        Assert.Equal(0, rig.Session.Disposals);
        Assert.Equal(1, rig.Monitor.SubscriberCount);

        b.Dispose();
        Assert.Equal(1, rig.Session.Disposals);
        Assert.Equal(0, rig.Monitor.SubscriberCount);
        Assert.Equal(FrameMetricsStatus.Unavailable, rig.Monitor.Current.Status);
    }

    [Fact]
    public void A_target_set_before_subscribing_reaches_the_session_prefilter()
    {
        var rig = new Rig();
        rig.Monitor.SetTargetProcess(Game);
        using var lease = rig.Monitor.Subscribe();
        Assert.Equal(Game, rig.Session.TargetProcessId);

        rig.Monitor.SetTargetProcess(Game + 1);
        Assert.Equal(Game + 1, rig.Session.TargetProcessId);
        Assert.Equal(Game + 1, rig.Monitor.TargetProcessId);
    }

    [Fact]
    public void Presents_of_the_target_produce_metrics_and_an_update()
    {
        var rig = new Rig();
        rig.Monitor.SetTargetProcess(Game);
        using var lease = rig.Monitor.Subscribe();
        FrameMetricsSnapshot? raised = null;
        rig.Monitor.Updated += s => raised = s;

        rig.Present(120, 2);
        rig.Monitor.Tick();

        var cur = rig.Monitor.Current;
        Assert.Equal(FrameMetricsStatus.Ok, cur.Status);
        Assert.Equal(120, cur.Fps!.Value, 0);
        Assert.Equal(Game, cur.ProcessId);
        Assert.NotNull(cur.Low1);
        Assert.Same(cur, raised);
    }

    [Fact]
    public void No_target_means_no_game()
    {
        var rig = new Rig();
        using var lease = rig.Monitor.Subscribe();
        rig.Monitor.Tick();
        Assert.Equal(FrameMetricsStatus.NoGame, rig.Monitor.Current.Status);
    }

    [Fact]
    public void The_clipper_never_measures_itself()
    {
        // WPF presents through D3D9 — its own frame rate must never pose as the game's.
        var rig = new Rig();
        using var lease = rig.Monitor.Subscribe();
        rig.Monitor.SetTargetProcess(Environment.ProcessId);

        Assert.Equal(0, rig.Monitor.TargetProcessId);
        Assert.Equal(0, rig.Session.TargetProcessId);
        rig.Present(120, 2, pid: Environment.ProcessId);
        rig.Monitor.Tick();
        Assert.Equal(FrameMetricsStatus.NoGame, rig.Monitor.Current.Status);
    }

    [Fact]
    public void Tests_may_measure_their_own_process()
    {
        var rig = new Rig(allowOwnProcess: true);
        using var lease = rig.Monitor.Subscribe();
        rig.Monitor.SetTargetProcess(Environment.ProcessId);
        rig.Present(60, 2, pid: Environment.ProcessId);
        rig.Monitor.Tick();
        Assert.Equal(FrameMetricsStatus.Ok, rig.Monitor.Current.Status);
    }

    [Fact]
    public void Missing_permission_is_reported_and_not_retried()
    {
        var rig = new Rig { NextResult = PresentSessionStart.AccessDenied };
        rig.Monitor.SetTargetProcess(Game);
        using var lease = rig.Monitor.Subscribe();

        Assert.Equal(FrameMetricsStatus.NoPermission, rig.Monitor.Current.Status);
        Assert.Equal(1, rig.Session.Disposals);   // the half-started session is cleaned up

        rig.Now += Freq * 60;
        rig.Monitor.Tick();
        Assert.Single(rig.Sessions);
        Assert.Equal(FrameMetricsStatus.NoPermission, rig.Monitor.Current.Status);
    }

    [Fact]
    public void A_failed_start_is_unavailable_and_retried_later()
    {
        var rig = new Rig { NextResult = PresentSessionStart.Failed };
        rig.Monitor.SetTargetProcess(Game);
        using var lease = rig.Monitor.Subscribe();
        Assert.Equal(FrameMetricsStatus.Unavailable, rig.Monitor.Current.Status);

        rig.NextResult = PresentSessionStart.Started;
        rig.Now += Freq * 5;
        rig.Monitor.Tick();
        Assert.Single(rig.Sessions);              // not yet

        rig.Now += Freq * (long)FrameTimeMonitor.RetrySeconds;
        rig.Monitor.Tick();
        Assert.Equal(2, rig.Sessions.Count);
        Assert.Equal(Game, rig.Session.TargetProcessId);
        Assert.Equal(FrameMetricsStatus.NoData, rig.Monitor.Current.Status);
    }

    [Fact]
    public void Lost_events_counted_by_the_session_make_the_values_unknown()
    {
        var rig = new Rig();
        rig.Monitor.SetTargetProcess(Game);
        using var lease = rig.Monitor.Subscribe();
        rig.Present(120, 2);
        rig.Monitor.Tick();
        Assert.Equal(FrameMetricsStatus.Ok, rig.Monitor.Current.Status);

        rig.Session.Lost = 3;
        rig.Monitor.Tick();
        Assert.Equal(FrameMetricsStatus.EventsLost, rig.Monitor.Current.Status);
        Assert.Null(rig.Monitor.Current.Fps);

        rig.Present(120, 1);
        rig.Monitor.Tick();
        Assert.Equal(FrameMetricsStatus.Ok, rig.Monitor.Current.Status);
    }

    [Fact]
    public void A_lost_event_notice_in_the_stream_makes_the_values_unknown()
    {
        var rig = new Rig();
        rig.Monitor.SetTargetProcess(Game);
        using var lease = rig.Monitor.Subscribe();
        rig.Present(120, 2);
        rig.Session.OnLost!(rig.Now);
        rig.Monitor.Tick();
        Assert.Equal(FrameMetricsStatus.EventsLost, rig.Monitor.Current.Status);
    }

    [Fact]
    public void A_session_stopped_from_outside_is_restarted()
    {
        // e.g. "logman stop" or another tool: the query fails, the consumer has ended.
        var rig = new Rig();
        rig.Monitor.SetTargetProcess(Game);
        using var lease = rig.Monitor.Subscribe();
        rig.Session.Lost = -1;
        rig.Monitor.Tick();

        Assert.Equal(2, rig.Sessions.Count);
        Assert.Equal(1, rig.Sessions[0].Disposals);
        Assert.Equal(Game, rig.Session.TargetProcessId);
        rig.Present(120, 2);
        rig.Monitor.Tick();
        Assert.Equal(FrameMetricsStatus.Ok, rig.Monitor.Current.Status);
    }

    [Fact]
    public void Subscribing_again_starts_a_fresh_session_without_old_values()
    {
        var rig = new Rig();
        rig.Monitor.SetTargetProcess(Game);
        var lease = rig.Monitor.Subscribe();
        rig.Present(120, 2);
        lease.Dispose();

        using var again = rig.Monitor.Subscribe();
        Assert.Equal(2, rig.Sessions.Count);
        Assert.Equal(Game, rig.Session.TargetProcessId);
        rig.Monitor.Tick();
        Assert.Equal(FrameMetricsStatus.NoData, rig.Monitor.Current.Status);
    }

    [Fact]
    public void Disposing_stops_the_session_and_refuses_new_subscribers()
    {
        var rig = new Rig();
        var lease = rig.Monitor.Subscribe();
        rig.Monitor.Dispose();

        Assert.Equal(1, rig.Session.Disposals);
        lease.Dispose();   // harmless after the monitor is gone
        Assert.Equal(1, rig.Session.Disposals);
        Assert.Throws<ObjectDisposedException>(() => rig.Monitor.Subscribe());
    }
}
