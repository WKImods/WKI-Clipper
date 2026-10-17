using System;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Review follow-ups for <see cref="ConcatWatch"/>:
/// - the +faststart pass shifts the whole mdat INSIDE the finished file, so the length stays flat
///   for as long as that takes (minutes for a multi-GB clip on an HDD) - that is not a hang;
/// - a concat that copies slower than the recording rate gets overtaken by the segment muxer and
///   silently reads overwritten segments: a spliced clip, worse than no clip.
/// </summary>
public class ConcatWatchExpectationTests
{
    private const long MiB = 1024L * 1024;
    private static readonly DateTime T0 = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    private static DateTime At(double sec) => T0.AddSeconds(sec);

    // ---- quiet period of the faststart pass ----

    [Theory]
    [InlineData(100, 20.0)]     // small clip: the 20 s floor
    [InlineData(400, 20.0)]
    [InlineData(1024, 51.2)]    // 1 GiB at 20 MB/s
    [InlineData(2048, 102.4)]
    public void Quiet_period_scales_with_the_expected_size_but_never_drops_below_twenty_seconds(
        long expectedMiB, double expectedSec)
        => Assert.Equal(expectedSec, ConcatWatch.FinalizeQuietPeriod(expectedMiB * MiB).TotalSeconds, precision: 3);

    [Fact]
    public void Length_flat_at_the_expected_size_for_forty_seconds_is_not_a_stall()
    {
        // 1 GiB expected. The copy phase finishes at t=8, then faststart shifts data in place.
        var expected = new ConcatExpectation(TotalBytes: 1024 * MiB, RecordingBytesPerSec: 4 * MiB);
        var w = new ConcatWatch(T0, clipSeconds: 280, expected: expected);

        Assert.Equal(ConcatVerdict.Progressing, w.Observe(At(8), 1024 * MiB + 60 * 1024));   // + moov
        for (int s = 9; s <= 48; s++)
            Assert.Equal(ConcatVerdict.Progressing, w.Observe(At(s), 1024 * MiB + 60 * 1024));
    }

    [Fact]
    public void The_longer_quiet_period_still_ends_eventually()
    {
        var expected = new ConcatExpectation(1024 * MiB, 0);   // quiet period 51.2 s
        var w = new ConcatWatch(T0, clipSeconds: 280, expected: expected);
        w.Observe(At(8), 1024 * MiB);

        Assert.Equal(ConcatVerdict.Progressing, w.Observe(At(59), 1024 * MiB));   // 51 s of silence
        Assert.Equal(ConcatVerdict.Stalled, w.Observe(At(60), 1024 * MiB));       // 52 s
    }

    [Fact]
    public void Reaching_ninety_five_percent_is_what_unlocks_the_long_quiet_period()
    {
        // 94 %: still copying, so the short 20 s rule applies
        var copying = new ConcatWatch(T0, 280, expected: new ConcatExpectation(2048 * MiB, 0));
        copying.Observe(At(5), (long)(2048 * MiB * 0.94));
        Assert.Equal(ConcatVerdict.Progressing, copying.Observe(At(25), (long)(2048 * MiB * 0.94)));
        Assert.Equal(ConcatVerdict.Stalled, copying.Observe(At(26), (long)(2048 * MiB * 0.94)));

        // 95 %: finalizing, 2 GiB -> 102.4 s of quiet allowed
        var finalizing = new ConcatWatch(T0, 560, expected: new ConcatExpectation(2048 * MiB, 0));
        finalizing.Observe(At(5), (long)(2048 * MiB * 0.95));
        Assert.Equal(ConcatVerdict.Progressing, finalizing.Observe(At(26), (long)(2048 * MiB * 0.95)));
        Assert.Equal(ConcatVerdict.Progressing, finalizing.Observe(At(100), (long)(2048 * MiB * 0.95)));
    }

    [Fact]
    public void A_hang_halfway_through_the_copy_still_stalls_after_twenty_seconds()
    {
        var w = new ConcatWatch(T0, clipSeconds: 280, expected: new ConcatExpectation(1024 * MiB, 0));
        w.Observe(At(5), 512 * MiB);

        Assert.Equal(ConcatVerdict.Progressing, w.Observe(At(25), 512 * MiB));
        Assert.Equal(ConcatVerdict.Stalled, w.Observe(At(26), 512 * MiB));
    }

    [Fact]
    public void The_quiet_period_is_still_bounded_by_the_absolute_cap()
    {
        // 100 GiB would allow 5120 s of quiet, but a 10 s clip has a 60 s cap.
        var w = new ConcatWatch(T0, clipSeconds: 10, expected: new ConcatExpectation(100 * 1024 * MiB, 0));
        w.Observe(At(1), 100 * 1024 * MiB);

        Assert.Equal(ConcatVerdict.Progressing, w.Observe(At(60), 100 * 1024 * MiB));
        Assert.Equal(ConcatVerdict.TimedOut, w.Observe(At(61), 100 * 1024 * MiB));
    }

    [Fact]
    public void A_zero_expectation_behaves_like_no_expectation()
    {
        var w = new ConcatWatch(T0, clipSeconds: 60, expected: new ConcatExpectation(0, 0));
        w.Observe(At(5), 5_000_000);
        Assert.Equal(ConcatVerdict.Stalled, w.Observe(At(26), 5_000_000));
    }

    // ---- too slow: the muxer would overtake the reader ----

    [Fact]
    public void A_copy_slower_than_ninety_percent_of_the_recording_rate_is_aborted_after_ten_seconds()
    {
        // Recording produces 4 MiB/s; this concat writes 3 MiB/s.
        var w = new ConcatWatch(T0, clipSeconds: 280,
            expected: new ConcatExpectation(1024 * MiB, RecordingBytesPerSec: 4 * MiB));

        for (int s = 1; s <= 10; s++)
            Assert.Equal(ConcatVerdict.Progressing, w.Observe(At(s), s * 3 * MiB));   // grace period

        Assert.Equal(ConcatVerdict.TooSlow, w.Observe(At(11), 11 * 3 * MiB));         // 33 MiB < 39.6 MiB
    }

    [Fact]
    public void A_copy_keeping_up_with_the_recording_is_left_alone()
    {
        var w = new ConcatWatch(T0, clipSeconds: 1000,
            expected: new ConcatExpectation(4096 * MiB, RecordingBytesPerSec: 4 * MiB));

        for (int s = 1; s <= 200; s++)
            Assert.Equal(ConcatVerdict.Progressing, w.Observe(At(s), (long)(s * 3.7 * MiB)));   // 0.925 x rate
    }

    [Fact]
    public void A_fast_concat_that_is_already_finalizing_is_never_judged_too_slow()
    {
        // 1 GiB at 200 MiB/s is done copying after ~5 s; faststart then takes its time.
        var w = new ConcatWatch(T0, clipSeconds: 280,
            expected: new ConcatExpectation(1024 * MiB, RecordingBytesPerSec: 4 * MiB));

        Assert.Equal(ConcatVerdict.Progressing, w.Observe(At(5), 1024 * MiB));
        Assert.Equal(ConcatVerdict.Progressing, w.Observe(At(20), 1024 * MiB));
    }

    [Fact]
    public void Without_a_recording_rate_the_too_slow_rule_is_off()
    {
        var w = new ConcatWatch(T0, clipSeconds: 280,
            expected: new ConcatExpectation(1024 * MiB, RecordingBytesPerSec: 0));

        Assert.Equal(ConcatVerdict.Progressing, w.Observe(At(15), 1 * MiB));
    }

    // ---- building the expectation from the picked segments ----

    [Fact]
    public void Expectation_sums_the_segments_and_derives_the_recording_rate()
    {
        var e = ConcatExpectation.FromSegments(new long[] { 10 * MiB, 20 * MiB, 30 * MiB }, segmentSeconds: 5);

        Assert.Equal(60 * MiB, e.TotalBytes);
        Assert.Equal(4.0 * MiB, e.RecordingBytesPerSec, precision: 3);   // 60 MiB over 3 x 5 s
    }

    [Fact]
    public void Expectation_of_nothing_is_empty_and_harmless()
    {
        var e = ConcatExpectation.FromSegments(Array.Empty<long>(), segmentSeconds: 5);
        Assert.Equal(0, e.TotalBytes);
        Assert.Equal(0, e.RecordingBytesPerSec);

        var z = ConcatExpectation.FromSegments(new long[] { 5 * MiB }, segmentSeconds: 0);   // seg length clamped to 1 s
        Assert.Equal(5 * MiB, z.TotalBytes);
        Assert.Equal(5.0 * MiB, z.RecordingBytesPerSec, precision: 3);
    }
}
