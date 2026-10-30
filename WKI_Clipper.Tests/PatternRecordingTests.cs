using System;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Mouse-patterns design 4.2/5.1: compacting a recording into 1 ms bins - trim, pauses, the 60 s cap and empty recordings.</summary>
public sealed class PatternRecordingTests
{
    private static PatternData Ok(PatternRecording r)
    {
        var result = r.Finish();
        Assert.Equal(PatternRecordingOutcome.Ok, result.Outcome);
        Assert.NotNull(result.Data);
        return result.Data!;
    }

    [Fact]
    public void Leading_and_trailing_idle_is_trimmed()
    {
        var r = new PatternRecording(1000);
        r.AddRaw(1500, 0, 0);            // reported but no motion: not the start
        r.AddPointer(1500.2, 0, 0);
        r.AddRaw(2000, 3, -1);           // first movement = time 0
        r.AddPointer(2000, 2, -1);
        r.AddRaw(2010, 4, 0);
        r.AddPointer(2010.5, 5, 0);      // last movement: bin 11 covers (10, 11]
        r.AddRaw(4000, 0, 0);            // idle tail

        var d = Ok(r);
        Assert.Equal(new[] { 0, 10 }, d.Raw.AtMs);
        Assert.Equal(new[] { 3, 4 }, d.Raw.Dx);
        Assert.Equal(new[] { -1, 0 }, d.Raw.Dy);
        Assert.Equal(new[] { 0, 11 }, d.Pointer.AtMs);
        Assert.Equal(new[] { 2, 5 }, d.Pointer.Dx);
        Assert.Equal(11, d.DurationMs);   // ends with the last bin, so a pass covers every bin of both tracks
    }

    [Fact]
    public void Both_tracks_share_time_zero_whichever_moved_first_and_in_any_order()
    {
        var r = new PatternRecording(0);
        r.AddRaw(5003, 1, 0);            // drained first, but the pointer moved earlier
        r.AddRaw(5004, 1, 0);
        r.AddPointer(5000, 0, 2);
        r.AddPointer(5001, 0, 2);

        var d = Ok(r);
        Assert.Equal(new[] { 0, 1 }, d.Pointer.AtMs);
        Assert.Equal(new[] { 3, 4 }, d.Raw.AtMs);
        Assert.Equal(4, d.DurationMs);
    }

    [Fact]
    public void Pauses_inside_are_kept()
    {
        var r = new PatternRecording(0);
        r.AddRaw(100, 5, 5);
        r.AddRaw(1600, -5, 5);           // 1.5 s standing still in between
        r.AddRaw(1601, -5, 0);

        var d = Ok(r);
        Assert.Equal(new[] { 0, 1500, 1501 }, d.Raw.AtMs);
        Assert.Equal(PatternTrack.Empty.AtMs, d.Pointer.AtMs);
        Assert.Equal(1501, d.DurationMs);
        Assert.Equal((-5L, 10L), d.Raw.Total);
    }

    [Fact]
    public void Own_bins_sum_events_in_the_same_ms()
    {
        var r = new PatternRecording(0);
        r.AddPointer(10, 1, 1);          // bin 0
        r.AddPointer(10.3, 2, 0);        // bin 1 = (0, 1]
        r.AddPointer(10.9, 3, 0);
        r.AddPointer(11, 4, -1);
        r.AddPointer(11.2, 5, 0);        // bin 2
        r.AddPointer(13.5, 6, 0);        // bin 4 = (3, 4]
        r.AddPointer(13.7, -6, 0);       // the bin's dx cancels out
        r.AddPointer(14, 0, 7);          // its end still belongs to it: the bin keeps the dy

        var d = Ok(r);
        Assert.Equal(new[] { 0, 1, 2, 4 }, d.Pointer.AtMs);
        Assert.Equal(new[] { 1, 9, 5, 0 }, d.Pointer.Dx);
        Assert.Equal(new[] { 1, -1, 0, 7 }, d.Pointer.Dy);
    }

    [Fact]
    public void A_first_bin_that_cancels_out_does_not_leave_leading_idle()
    {
        var r = new PatternRecording(0);
        r.AddRaw(50, 4, 0);
        r.AddRaw(50, -4, 0);             // bin 0 sums to nothing
        r.AddRaw(53, 1, 1);

        var d = Ok(r);
        Assert.Equal(new[] { 0 }, d.Raw.AtMs);
        Assert.Equal(0, d.DurationMs);
    }

    [Fact]
    public void No_motion_is_Empty()
    {
        Assert.Equal(new PatternRecordingResult(PatternRecordingOutcome.Empty, null), new PatternRecording(0).Finish());

        var zero = new PatternRecording(0);
        zero.AddRaw(10, 0, 0);
        zero.AddPointer(20, 0, 0);
        Assert.Equal(PatternRecordingOutcome.Empty, zero.Finish().Outcome);

        var cancelled = new PatternRecording(0);
        cancelled.AddRaw(10, 3, -2);
        cancelled.AddRaw(10, -3, 2);     // same bin: back where it started in the same instant
        var result = cancelled.Finish();
        Assert.Equal(PatternRecordingOutcome.Empty, result.Outcome);
        Assert.Null(result.Data);
    }

    [Fact]
    public void Events_before_the_start_or_without_a_time_are_ignored()
    {
        var r = new PatternRecording(1000);
        r.AddRaw(999.9, 50, 50);         // stale: happened before the recording started
        r.AddRaw(double.NaN, 50, 50);
        r.AddPointer(double.PositiveInfinity, 50, 50);
        Assert.Equal(PatternRecordingOutcome.Empty, r.Finish().Outcome);

        r.AddRaw(1000, 1, 0);
        var d = Ok(r);
        Assert.Equal(new[] { 1 }, d.Raw.Dx);
    }

    [Fact]
    public void Sixty_seconds_cuts_and_reports_Cut()
    {
        var r = new PatternRecording(0);
        Assert.False(r.LimitReached(500_000));          // the limit counts from the first movement, not from the start
        r.AddRaw(100, 1, 0);
        Assert.False(r.LimitReached(100 + 59_999.9));
        r.AddRaw(100 + 60_000, 2, 0);                   // the last bin that still fits
        r.AddRaw(100 + 60_000.5, 4, 0);                 // beyond the limit: dropped
        r.AddPointer(100 + 70_000, 8, 0);
        Assert.True(r.LimitReached(100 + 60_000));

        var result = r.Finish();
        Assert.Equal(PatternRecordingOutcome.Cut, result.Outcome);
        Assert.Equal(new[] { 0, 60_000 }, result.Data!.Raw.AtMs);
        Assert.Equal((3L, 0L), result.Data.Raw.Total);
        Assert.Equal(0, result.Data.Pointer.Count);
        Assert.Equal(60_000, result.Data.DurationMs);
    }

    [Fact]
    public void Reaching_the_limit_is_Cut_even_without_later_events()
    {
        var r = new PatternRecording(0);
        r.AddPointer(10, 1, 1);
        r.AddPointer(20, 1, 1);
        Assert.True(r.LimitReached(10 + 60_000));       // the helper stops the recording here
        Assert.Equal(PatternRecordingOutcome.Cut, r.Finish().Outcome);
    }

    [Fact]
    public void An_event_dropped_after_an_earlier_first_movement_turned_up_is_Cut()
    {
        var r = new PatternRecording(0);
        r.AddRaw(60_050, 1, 0);          // fits while it is the first movement ...
        r.AddPointer(40, 1, 0);          // ... until the pointer track shows an earlier one (60 010 ms before it)
        var result = r.Finish();
        Assert.Equal(PatternRecordingOutcome.Cut, result.Outcome);
        Assert.Equal(0, result.Data!.Raw.Count);
        Assert.Equal(new[] { 0 }, result.Data.Pointer.AtMs);
    }

    [Fact]
    public void A_finished_recording_is_what_the_codec_accepts()
    {
        var r = new PatternRecording(0);
        var rng = new Random(7);
        double t = 300;
        for (int i = 0; i < 5000; i++)
        {
            t += rng.NextDouble() * 3;
            r.AddRaw(t, rng.Next(-20, 21), rng.Next(-20, 21));
            r.AddPointer(t + rng.NextDouble(), rng.Next(-30, 31), rng.Next(-30, 31));
        }

        var d = Ok(r);
        var text = PatternCodec.Encode(d);
        Assert.True(PatternCodec.TryDecode(text, out var back));
        Assert.Equal(d.Raw.AtMs, back!.Raw.AtMs);
        Assert.Equal(d.Pointer.Dx, back.Pointer.Dx);
        Assert.Equal(d.DurationMs, back.DurationMs);
        Assert.True(d.DurationMs >= Math.Max(d.Raw.AtMs[^1], d.Pointer.AtMs[^1]));
    }
}
