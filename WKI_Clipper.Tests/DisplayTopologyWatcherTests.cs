using System;
using WKI_Clipper.Services;
using Xunit;
using CaptureSource = WKI_Clipper.Services.CaptureSource;

namespace WKI_Clipper.Tests;

/// <summary>
/// The display watcher: display events are debounced into one topology read, the lists hear about a changed topology, and
/// the buffer is retargeted only when the capture would record another picture (decision 7, R8).
/// </summary>
public sealed class DisplayTopologyWatcherTests
{
    [Fact]
    public void Burst_of_display_events_causes_one_read_and_at_most_one_retarget()
    {
        var dell = TestDisplays.Dell();
        using var env = new WatcherEnv(TestDisplays.Topology(TestDisplays.Lg(), dell), MonitorRef.Of(dell));
        int readsBefore = env.Reads;

        // The chosen DELL is unplugged; Windows reports a handful of events while the desktop settles.
        env.Topology = TestDisplays.Topology(TestDisplays.Lg());
        for (int i = 0; i < 5; i++)
        {
            env.Events.Fire();
            env.Time.Advance(TimeSpan.FromMilliseconds(400));
        }

        Assert.Equal(readsBefore, env.Reads);   // nothing is read while the events keep coming
        Assert.Equal(0, env.Retargets);

        env.Time.Advance(DisplayTopologyWatcher.Debounce);

        Assert.Equal(readsBefore + 1, env.Reads);
        Assert.Equal(1, env.Retargets);
        Assert.Equal(1, env.Changes);

        env.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(readsBefore + 1, env.Reads);
        Assert.Equal(1, env.Retargets);
    }

    [Fact]
    public void Mode_change_of_the_same_monitor_does_not_retarget()
    {
        // Nothing saved: the primary LG at 4K. An exclusive-fullscreen game switches it to 1080p.
        using var env = new WatcherEnv(TestDisplays.Topology(TestDisplays.Lg(w: 3840, h: 2160), TestDisplays.Dell()));
        var at4k = env.Current!;
        env.Topology = TestDisplays.Topology(TestDisplays.Lg(w: 1920, h: 1080), TestDisplays.Dell());

        env.Events.Fire();
        env.Time.Advance(DisplayTopologyWatcher.Debounce);

        Assert.Equal(1, env.Changes);   // the lists show the new size
        // ddagrab ends on the switch by itself and the buffer's automatic restart picks up the new mode: one restart, not two.
        Assert.Equal(0, env.Retargets);
        var at1080 = env.Resolve()!;
        Assert.False(CaptureSourceRules.SameSource(at4k, at1080));
        Assert.False(DisplayTopologyWatcher.NeedsRetarget(at4k, at1080));

        // Another monitor changing its mode does not concern the recorded one either.
        env.Topology = TestDisplays.Topology(TestDisplays.Lg(w: 1920, h: 1080), TestDisplays.Dell(w: 1920, h: 1080));
        env.Events.Fire();
        env.Time.Advance(DisplayTopologyWatcher.Debounce);
        Assert.Equal(2, env.Changes);
        Assert.Equal(0, env.Retargets);
    }

    [Fact]
    public void Monitor_gone_or_back_retargets()
    {
        var dell = TestDisplays.Dell();
        using var env = new WatcherEnv(TestDisplays.Topology(TestDisplays.Lg(), dell), MonitorRef.Of(dell));
        Assert.Equal(dell.HMonitor, env.Current!.HMonitor);

        env.Topology = TestDisplays.Topology(TestDisplays.Lg());   // unplugged
        env.Events.Fire();
        env.Time.Advance(DisplayTopologyWatcher.Debounce);
        Assert.Equal(1, env.Retargets);

        env.Current = env.Resolve();   // the buffer restarted on the primary
        Assert.True(env.Current!.Resolution.IsFallback);

        env.Topology = TestDisplays.Topology(TestDisplays.Lg(), dell);   // plugged in again
        env.Events.Fire("resume");
        env.Time.Advance(DisplayTopologyWatcher.Debounce);
        Assert.Equal(2, env.Retargets);
        Assert.Equal(2, env.Changes);
    }

    [Fact]
    public void New_primary_that_renumbers_the_output_retargets()
    {
        var dell = TestDisplays.Dell(output: 1);
        using var env = new WatcherEnv(TestDisplays.Topology(TestDisplays.Lg(), dell), MonitorRef.Of(dell));

        // Win+P / Windows settings: the DELL became the primary - DXGI output 0 now; ddagrab's old index would show the LG.
        env.Topology = TestDisplays.Topology(TestDisplays.Lg(output: 1, primary: false, x: -3440),
                                             TestDisplays.Dell(output: 0, primary: true, x: 0));
        env.Events.Fire();
        env.Time.Advance(DisplayTopologyWatcher.Debounce);

        Assert.Equal(1, env.Retargets);
    }

    [Fact]
    public void Monitor_that_only_lost_its_device_path_does_not_retarget()
    {
        // Nothing saved (primary). A sleeping DisplayPort monitor keeps its HMONITOR, GDI name and output, but Windows
        // reports no device path for a while - its display key falls back to the GDI name. Same picture: nothing to move.
        var lg = TestDisplays.Lg();
        using var env = new WatcherEnv(TestDisplays.Topology(lg, TestDisplays.Dell()));
        env.Topology = TestDisplays.Topology(lg with { DevicePath = null }, TestDisplays.Dell());

        env.Events.Fire();
        env.Time.Advance(DisplayTopologyWatcher.Debounce);
        Assert.Equal(0, env.Retargets);

        env.Topology = TestDisplays.Topology(lg, TestDisplays.Dell());   // awake again
        env.Events.Fire();
        env.Time.Advance(DisplayTopologyWatcher.Debounce);
        Assert.Equal(0, env.Retargets);
        Assert.Equal(2, env.Changes);
    }

    [Fact]
    public void Nothing_recorded_means_nothing_to_retarget()
    {
        var dell = TestDisplays.Dell();
        using var env = new WatcherEnv(TestDisplays.Topology(TestDisplays.Lg(), dell), MonitorRef.Of(dell));
        env.Current = null;   // buffer off

        env.Topology = TestDisplays.Topology(TestDisplays.Lg());
        env.Events.Fire();
        env.Time.Advance(DisplayTopologyWatcher.Debounce);

        Assert.Equal(0, env.Retargets);
        Assert.Equal(1, env.Changes);   // the lists still follow
    }

    [Fact]
    public void Event_without_a_topology_change_raises_nothing()
    {
        using var env = new WatcherEnv(TestDisplays.Topology(TestDisplays.Lg(), TestDisplays.Dell()));
        int readsBefore = env.Reads;

        env.Events.Fire();
        env.Time.Advance(DisplayTopologyWatcher.Debounce);

        Assert.Equal(readsBefore + 1, env.Reads);   // read again (the event invalidated the cache) ...
        Assert.Equal(0, env.Changes);              // ... but the fingerprint is the same
        Assert.Equal(0, env.Retargets);
    }

    [Fact]
    public void Last_change_time_is_set_on_every_event()
    {
        using var env = new WatcherEnv(TestDisplays.Topology(TestDisplays.Lg()));
        Assert.Null(env.Watcher.LastChangeUtc);

        env.Events.Fire();
        Assert.Equal(env.Time.GetUtcNow().UtcDateTime, env.Watcher.LastChangeUtc);
        Assert.Equal(DateTimeKind.Utc, env.Watcher.LastChangeUtc!.Value.Kind);

        env.Time.Advance(TimeSpan.FromMilliseconds(700));
        env.Events.Fire("resume");
        Assert.Equal(env.Time.GetUtcNow().UtcDateTime, env.Watcher.LastChangeUtc);

        env.Time.Advance(TimeSpan.FromSeconds(5));   // the debounce fired in between; the time stays the last event's
        Assert.Equal(env.Time.GetUtcNow().UtcDateTime - TimeSpan.FromSeconds(5), env.Watcher.LastChangeUtc);
    }

    [Fact]
    public void Dispose_unsubscribes()
    {
        var env = new WatcherEnv(TestDisplays.Topology(TestDisplays.Lg(), TestDisplays.Dell()), MonitorRef.Of(TestDisplays.Dell()));
        Assert.Equal(1, env.Events.Subscribers);

        // An event just before the end: its debounce must not fire after Dispose.
        env.Topology = TestDisplays.Topology(TestDisplays.Lg());
        env.Events.Fire();
        int reads = env.Reads;
        env.Dispose();

        Assert.Equal(0, env.Events.Subscribers);
        env.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(reads, env.Reads);
        Assert.Equal(0, env.Retargets);
        Assert.Equal(0, env.Changes);

        env.Dispose();   // twice is harmless
    }

    /// <summary>Plays Windows' display events.</summary>
    private sealed class FakeEvents : IDisplayEvents
    {
        public event Action<string>? Raised;
        public int Subscribers => Raised?.GetInvocationList().Length ?? 0;
        public void Fire(string what = "display settings") => Raised?.Invoke(what);
    }

    /// <summary>
    /// A watcher on a fake topology (<see cref="Topology"/> = what Windows reports now) on manual time. The re-resolve is the
    /// Monitor-mode rule with <see cref="Saved"/> (null = the primary); <see cref="Current"/> plays what the buffer records.
    /// </summary>
    private sealed class WatcherEnv : IDisposable
    {
        public ManualTime Time { get; } = new();
        public FakeEvents Events { get; } = new();
        public DisplayTopology Topology;
        public MonitorRef? Saved;
        public CaptureSource? Current;
        public int Reads, Retargets, Changes;
        public DisplayTopologyCache Cache { get; }
        public DisplayTopologyWatcher Watcher { get; }

        public WatcherEnv(DisplayTopology topology, MonitorRef? saved = null)
        {
            Topology = topology;
            Saved = saved;
            Cache = new DisplayTopologyCache(() => { Reads++; return Topology; }, () => "fixed");
            Watcher = new DisplayTopologyWatcher(Cache, Time, () => Current, Resolve, () => Retargets++, Events);
            Watcher.TopologyChanged += () => Changes++;
            Current = Resolve();
        }

        public CaptureSource? Resolve()
        {
            var t = Cache.Get();
            var r = MonitorTargetRules.Resolve(Saved, t);
            return r.Display is null ? null : CaptureSourceRules.ForDisplay(r.Display, t, r);
        }

        public void Dispose() => Watcher.Dispose();
    }
}
