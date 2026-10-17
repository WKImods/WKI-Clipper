using System;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Ring sizing + the watchdog's size rule. Two real defects pinned down here:
/// 1) a fixed 2 GiB cap restarted the buffer every 30 s once two rings of footage (which
///    prune deliberately keeps) outgrew it, and longer buffers would always have looped;
/// 2) segment_wrap was pick+1, so the oldest segment of a save was the very next one the
///    muxer truncates - a slow concat could read a half-overwritten file.
/// </summary>
public class BufferRingTests
{
    private const long MiB = 1024L * 1024;

    // ---- RingPlan ----

    [Fact]
    public void Default_buffer_gets_the_reserve_on_top_of_the_old_wrap()
    {
        var p = RingPlan.Create(durationSeconds: 60, segmentSeconds: 5);

        Assert.Equal(12, p.PickCount);
        Assert.Equal(12 + 1 + RingPlan.Reserve, p.Wrap);         // old formula was 13
        Assert.Equal(p.PickCount + p.Wrap + 2, p.Keep);          // same keep shape as before
    }

    [Fact]
    public void Reserve_is_six_segments()
        => Assert.Equal(6, RingPlan.Reserve);

    [Theory]
    [InlineData(15, 5)]
    [InlineData(60, 5)]
    [InlineData(180, 5)]
    [InlineData(1800, 5)]
    [InlineData(61, 5)]     // not a multiple: ceil
    [InlineData(30, 30)]
    [InlineData(10, 30)]    // buffer shorter than one segment
    public void Oldest_picked_segment_survives_at_least_the_reserve_of_new_segments(int dur, int seg)
    {
        var p = RingPlan.Create(dur, seg);

        // Steady state: segment k is open, picks are k-1 ... k-PickCount. Count how many
        // further segments get opened before one reuses the oldest pick's file name.
        const int k = 1000;
        int oldestIndex = (k - p.PickCount) % p.Wrap;
        int openings = 0;
        for (int n = 1; n <= p.Wrap; n++)
        {
            if ((k + n) % p.Wrap == oldestIndex) { openings = n; break; }
        }

        Assert.True(openings >= RingPlan.Reserve,
            $"oldest picked segment is truncated after only {openings} new segments");
        Assert.Equal(p.Wrap - p.PickCount, openings);
    }

    [Fact]
    public void Keep_holds_the_whole_current_ring_plus_the_previous_generation_window()
    {
        var p = RingPlan.Create(180, 5);
        Assert.True(p.Keep >= p.Wrap + p.PickCount);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-30, -5)]
    [InlineData(3, 10)]
    public void Absurd_settings_still_give_a_usable_plan(int dur, int seg)
    {
        var p = RingPlan.Create(dur, seg);
        Assert.True(p.PickCount >= 1);
        Assert.True(p.Wrap >= p.PickCount + 1 + RingPlan.Reserve);
        Assert.True(p.Keep >= p.Wrap);
    }

    [Fact]
    public void Segment_longer_than_the_buffer_picks_exactly_one_segment()
    {
        var p = RingPlan.Create(durationSeconds: 3, segmentSeconds: 10);
        Assert.Equal(1, p.PickCount);
    }

    [Theory]
    [InlineData(100, 12)]   // GIF wants more than the buffer holds -> capped, reserve stays intact
    [InlineData(12, 12)]
    [InlineData(5, 5)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    public void ClampPick_never_exceeds_what_the_reserve_allows(int wanted, int expected)
        => Assert.Equal(expected, RingPlan.Create(60, 5).ClampPick(wanted));

    // ---- watchdog size rule ----

    // 30 Mbit/s = 3.75 MB/s; 5 s segments ~ 18.75 MB.
    private static long SegBytes(double mbitPerSec, int segSec) => (long)(mbitPerSec / 8 * MiB * segSec);

    [Theory]
    [InlineData(25, 180)]    // the reported loop: two rings of 180 s approach 2 GiB
    [InlineData(30, 180)]
    [InlineData(40, 300)]    // already past 3 GiB
    [InlineData(30, 1800)]   // planned 30 min buffer, ~13 GiB legitimately on disk
    public void A_ring_pruned_to_keep_is_never_a_runaway(double mbit, int dur)
    {
        var plan = RingPlan.Create(dur, 5);
        long seg = SegBytes(mbit, 5);
        long total = plan.Keep * seg;                        // everything prune lets survive

        Assert.False(BufferHealth.IsRunaway(total, plan.Keep, seg));
    }

    [Fact]
    public void A_healthy_ring_beyond_the_old_two_gib_cap_is_left_alone()
    {
        var plan = RingPlan.Create(180, 5);
        long seg = SegBytes(45, 5);
        long total = plan.Keep * seg;

        Assert.True(total > 2048 * MiB);                     // the old fixed cap would have fired here...
        Assert.False(BufferHealth.IsRunaway(total, plan.Keep, seg));   // ...for a ring that is perfectly fine
    }

    [Fact]
    public void Prune_not_working_is_a_runaway()
    {
        var plan = RingPlan.Create(180, 5);
        long seg = SegBytes(30, 5);
        long total = 2L * plan.Keep * seg;                   // twice what prune would ever leave

        Assert.True(BufferHealth.IsRunaway(total, plan.Keep, seg));
    }

    [Fact]
    public void Limit_is_keep_times_largest_segment_times_one_and_a_half_plus_64_mib()
    {
        long limit = BufferHealth.RunawayLimitBytes(keepSegments: 10, largestSegmentBytes: 20 * MiB);
        Assert.Equal(10 * 20 * MiB * 3 / 2 + 64 * MiB, limit);
    }

    [Fact]
    public void Exactly_at_the_limit_is_still_fine_one_byte_more_is_not()
    {
        long limit = BufferHealth.RunawayLimitBytes(10, 20 * MiB);
        Assert.False(BufferHealth.IsRunaway(limit, 10, 20 * MiB));
        Assert.True(BufferHealth.IsRunaway(limit + 1, 10, 20 * MiB));
    }

    [Fact]
    public void An_empty_or_nonsensical_directory_keeps_a_floor_and_never_throws()
    {
        Assert.False(BufferHealth.IsRunaway(0, 0, 0));
        Assert.False(BufferHealth.IsRunaway(10 * MiB, -5, -1));
        Assert.Equal(64 * MiB, BufferHealth.RunawayLimitBytes(0, 0));
        Assert.True(BufferHealth.IsRunaway(65 * MiB, 0, 0));
    }

    [Fact]
    public void A_bigger_segment_after_a_bitrate_change_raises_the_limit()
    {
        // Warm restart with a higher quality: the largest segment on disk is the new one.
        long small = BufferHealth.RunawayLimitBytes(33, 10 * MiB);
        long big = BufferHealth.RunawayLimitBytes(33, 40 * MiB);
        Assert.True(big > small);
    }
}
