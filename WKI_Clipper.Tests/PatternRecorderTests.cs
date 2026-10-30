using System;
using WKI_Clipper.ClickerHost;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Mouse-patterns design 4.2/5.1: the helper's recorder between the input callbacks and <see cref="PatternRecording"/> - both tracks,
/// start/stop/cancel, the drain timer, the 60 s limit and full rings. Virtual time: the clock is a <see cref="FakeClock"/>, the drain
/// timer runs on a <see cref="ManualTime"/>.
/// </summary>
public sealed class PatternRecorderTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(PatternRecorder.DrainMs);

    private sealed class Rig : IDisposable
    {
        public readonly FakeClock Clock = new() { Now = 1000 };
        public readonly ManualTime Time = new();
        public readonly PatternRecorder Recorder;
        public int Limits;

        public Rig()
        {
            Recorder = new PatternRecorder(Clock, Time);
            Recorder.LimitReached += () => Limits++;
        }

        /// <summary>Moves both clocks on, so the drain timer runs as often as it falls due.</summary>
        public void Advance(double ms)
        {
            Clock.Now += ms;
            Time.Advance(TimeSpan.FromMilliseconds(ms));
        }

        public void Dispose() => Recorder.Dispose();
    }

    private static PatternData Data(PatternRecordingResult result, PatternRecordingOutcome expected = PatternRecordingOutcome.Ok)
    {
        Assert.Equal(expected, result.Outcome);
        Assert.NotNull(result.Data);
        return result.Data!;
    }

    [Fact]
    public void Recorder_collects_both_tracks_and_trims()
    {
        using var rig = new Rig();
        var rec = rig.Recorder;
        rec.OnRawMouse(1001, 9, 9);          // not recording yet: dropped
        rec.OnPointerMove(1001, 9, 9);

        rec.Start("game.exe");
        Assert.True(rec.IsRecording);
        Assert.Equal("game.exe", rec.Program);
        rig.Advance(1000);                   // idle lead-in, the drain timer finds nothing
        rec.OnRawMouse(2000, 3, -1);         // first movement = time 0
        rec.OnPointerMove(2000, 2, -1);
        rig.Advance(Tick.TotalMilliseconds); // drained into the recording while it runs
        rec.OnRawMouse(2010, 4, 0);
        rec.OnPointerMove(2010.5, 5, 0);     // last movement: bin 11 covers (10, 11]
        rig.Advance(2000);                   // idle tail

        var d = Data(rec.Stop());

        Assert.False(rec.IsRecording);
        Assert.Equal("game.exe", rec.Program);   // still there for the result message
        Assert.Equal(new[] { 0, 10 }, d.Raw.AtMs);
        Assert.Equal(new[] { 3, 4 }, d.Raw.Dx);
        Assert.Equal(new[] { -1, 0 }, d.Raw.Dy);
        Assert.Equal(new[] { 0, 11 }, d.Pointer.AtMs);
        Assert.Equal(new[] { 2, 5 }, d.Pointer.Dx);
        Assert.Equal(11, d.DurationMs);
    }

    [Fact]
    public void Motion_after_stop_is_not_recorded_and_a_new_recording_starts_empty()
    {
        using var rig = new Rig();
        var rec = rig.Recorder;
        rec.Start(null);
        rec.OnRawMouse(1100, 1, 1);
        Data(rec.Stop());

        rec.OnRawMouse(1200, 50, 50);        // after the stop: dropped
        rec.OnPointerMove(1200, 50, 50);
        rig.Advance(500);
        rec.Start(null);
        Assert.Null(rec.Program);

        Assert.Equal(PatternRecordingOutcome.Empty, rec.Stop().Outcome);
    }

    [Fact]
    public void Recorder_cancel_discards()
    {
        using var rig = new Rig();
        var rec = rig.Recorder;
        rec.Start("game.exe");
        rec.OnRawMouse(1100, 4, 4);
        rec.OnPointerMove(1100, 4, 4);
        rig.Advance(100);
        rec.OnRawMouse(1150, 4, 4);          // still in the ring when the cancel comes

        rec.Cancel();

        Assert.False(rec.IsRecording);
        rec.OnRawMouse(1160, 4, 4);          // after the cancel: dropped
        var stray = rec.Stop();              // nothing is running any more
        Assert.Equal(PatternRecordingOutcome.Cancelled, stray.Outcome);
        Assert.Null(stray.Data);

        rec.Start(null);                     // nothing of the cancelled recording comes back
        rig.Advance(100);
        Assert.Equal(PatternRecordingOutcome.Empty, rec.Stop().Outcome);
    }

    [Fact]
    public void Recorder_reports_limit_once()
    {
        using var rig = new Rig();
        var rec = rig.Recorder;
        rec.Start(null);
        rig.Advance(90_000);                 // a minute and a half without any movement: no limit before the first one
        Assert.Equal(0, rig.Limits);

        double first = rig.Clock.Now;
        rec.OnRawMouse(first, 1, 0);
        rig.Advance(MousePatternRules.MaxDurationMs - 100);
        Assert.Equal(0, rig.Limits);
        rig.Advance(100 + Tick.TotalMilliseconds);
        Assert.Equal(1, rig.Limits);
        rec.OnRawMouse(rig.Clock.Now, 1, 0); // beyond the limit
        rig.Advance(1000);
        Assert.Equal(1, rig.Limits);         // once per recording

        var d = Data(rec.Stop(), PatternRecordingOutcome.Cut);
        Assert.Equal(new[] { 0 }, d.Raw.AtMs);

        rec.Start(null);                     // a new recording has its own limit
        rec.OnPointerMove(rig.Clock.Now, 0, 1);
        rig.Advance(MousePatternRules.MaxDurationMs + Tick.TotalMilliseconds);
        Assert.Equal(2, rig.Limits);
    }

    [Fact]
    public void No_limit_is_reported_after_the_recording_stopped()
    {
        using var rig = new Rig();
        var rec = rig.Recorder;
        rec.Start(null);
        rec.OnRawMouse(rig.Clock.Now, 1, 0);
        Data(rec.Stop());

        rig.Advance(2 * MousePatternRules.MaxDurationMs);

        Assert.Equal(0, rig.Limits);
    }

    [Fact]
    public void The_drain_timer_keeps_a_fast_mouse_from_filling_the_rings()
    {
        using var rig = new Rig();
        var rec = rig.Recorder;
        rec.Start(null);
        int events = 3 * PatternRecorder.RingCapacity;
        int perTick = PatternRecorder.RingCapacity / 4;   // far more than an 8 kHz mouse sends between two drains
        double t = rig.Clock.Now;
        for (int i = 0; i < events; i++)
        {
            t += 0.01;
            rec.OnRawMouse(t, 1, -1);
            rec.OnPointerMove(t, 2, 0);
            if ((i + 1) % perTick == 0)
            {
                rig.Clock.Now = t;
                rig.Time.Advance(Tick);
            }
        }

        var d = Data(rec.Stop());

        Assert.Equal(((long)events, (long)-events), d.Raw.Total);
        Assert.Equal((2L * events, 0L), d.Pointer.Total);
    }

    [Fact]
    public void A_full_ring_drops_the_newest_events_and_keeps_the_rest()
    {
        using var rig = new Rig();
        var rec = rig.Recorder;
        rec.Start(null);
        double t = rig.Clock.Now;
        for (int i = 0; i < PatternRecorder.RingCapacity + 10; i++)   // no drain in between
        {
            t += 0.01;
            rec.OnRawMouse(t, 1, 0);
        }
        rec.OnPointerMove(t, 0, 1);          // the other track has its own ring

        var d = Data(rec.Stop());

        Assert.Equal(((long)PatternRecorder.RingCapacity, 0L), d.Raw.Total);
        Assert.Equal((0L, 1L), d.Pointer.Total);
    }

    [Fact]
    public void A_disposed_recorder_records_nothing()
    {
        var rig = new Rig();
        var rec = rig.Recorder;
        rec.Start(null);
        rec.Dispose();

        Assert.False(rec.IsRecording);
        rec.OnRawMouse(1100, 1, 1);
        rig.Advance(100);
        Assert.Equal(PatternRecordingOutcome.Cancelled, rec.Stop().Outcome);
    }
}
