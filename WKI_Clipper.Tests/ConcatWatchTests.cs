using System;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// The concat used to get a flat 30 s and then delete the output. A long clip on a busy disk
/// can legitimately need longer; what proves a hang is output that stopped growing.
/// </summary>
public class ConcatWatchTests
{
    private static readonly DateTime T0 = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    private static DateTime At(double sec) => T0.AddSeconds(sec);

    [Theory]
    [InlineData(5, 60)]       // floor
    [InlineData(15, 60)]
    [InlineData(16, 64)]      // 4x clip length takes over
    [InlineData(30, 120)]
    [InlineData(180, 720)]
    [InlineData(0, 60)]
    [InlineData(-10, 60)]
    public void Safety_cap_is_the_larger_of_sixty_seconds_and_four_times_the_clip(int clipSec, int expectedSec)
        => Assert.Equal(TimeSpan.FromSeconds(expectedSec), ConcatWatch.SafetyCap(clipSec));

    [Fact]
    public void Default_stall_limit_is_twenty_seconds()
        => Assert.Equal(TimeSpan.FromSeconds(20), ConcatWatch.DefaultStallLimit);

    [Fact]
    public void Output_that_keeps_growing_is_never_aborted_for_being_slow_alone()
    {
        // 180 s clip -> cap 720 s. Growing every second for 600 s is a slow but healthy concat.
        var w = new ConcatWatch(T0, clipSeconds: 180);
        for (int s = 1; s <= 600; s++)
            Assert.Equal(ConcatVerdict.Progressing, w.Observe(At(s), outputLength: s * 1_000_000L));
    }

    [Fact]
    public void A_thirty_second_wait_no_longer_kills_a_concat_that_is_still_writing()
    {
        var w = new ConcatWatch(T0, clipSeconds: 60);
        Assert.Equal(ConcatVerdict.Progressing, w.Observe(At(29), 100 * 1024 * 1024));
        Assert.Equal(ConcatVerdict.Progressing, w.Observe(At(31), 120 * 1024 * 1024));  // old code gave up here
        Assert.Equal(ConcatVerdict.Progressing, w.Observe(At(45), 150 * 1024 * 1024));
    }

    [Fact]
    public void No_growth_for_more_than_twenty_seconds_is_a_stall()
    {
        var w = new ConcatWatch(T0, clipSeconds: 60);
        Assert.Equal(ConcatVerdict.Progressing, w.Observe(At(5), 5_000_000));   // last growth at t=5

        Assert.Equal(ConcatVerdict.Progressing, w.Observe(At(24), 5_000_000));  // 19 s
        Assert.Equal(ConcatVerdict.Progressing, w.Observe(At(25), 5_000_000));  // exactly 20 s
        Assert.Equal(ConcatVerdict.Stalled, w.Observe(At(26), 5_000_000));      // 21 s
    }

    [Fact]
    public void Growth_resets_the_stall_clock()
    {
        var w = new ConcatWatch(T0, clipSeconds: 60);
        w.Observe(At(5), 1000);
        Assert.Equal(ConcatVerdict.Progressing, w.Observe(At(24), 1000));
        Assert.Equal(ConcatVerdict.Progressing, w.Observe(At(25), 2000));   // grew -> clock restarts at 25
        Assert.Equal(ConcatVerdict.Progressing, w.Observe(At(44), 2000));   // 19 s since growth
        Assert.Equal(ConcatVerdict.Stalled, w.Observe(At(46), 2000));
    }

    [Fact]
    public void An_output_file_that_never_appears_stalls_after_twenty_seconds()
    {
        var w = new ConcatWatch(T0, clipSeconds: 60);
        Assert.Equal(ConcatVerdict.Progressing, w.Observe(At(20), 0));
        Assert.Equal(ConcatVerdict.Stalled, w.Observe(At(21), 0));
    }

    [Fact]
    public void A_shrinking_file_is_not_progress()
    {
        var w = new ConcatWatch(T0, clipSeconds: 60);
        w.Observe(At(1), 5000);
        Assert.Equal(ConcatVerdict.Stalled, w.Observe(At(22), 100));
    }

    [Fact]
    public void Crawling_growth_still_ends_at_the_absolute_cap()
    {
        // 10 s clip -> cap 60 s. One byte per poll never trips the stall rule, the cap must.
        var w = new ConcatWatch(T0, clipSeconds: 10);
        long len = 0;
        for (int s = 1; s <= 60; s++)
            Assert.Equal(ConcatVerdict.Progressing, w.Observe(At(s), ++len));

        Assert.Equal(ConcatVerdict.TimedOut, w.Observe(At(61), ++len));
    }

    [Fact]
    public void Cap_beats_stall_when_both_apply()
    {
        var w = new ConcatWatch(T0, clipSeconds: 10);
        w.Observe(At(1), 100);
        Assert.Equal(ConcatVerdict.TimedOut, w.Observe(At(100), 100));
    }

    [Fact]
    public void A_longer_stall_limit_covers_a_phase_that_produces_no_output_at_all()
    {
        // GIF encode: palettegen emits nothing until it has seen every frame. The caller opts
        // out of the 20 s rule by passing the cap itself as the stall limit.
        var cap = ConcatWatch.SafetyCap(30);                      // 120 s
        var w = new ConcatWatch(T0, clipSeconds: 30, stallLimit: cap);

        Assert.Equal(ConcatVerdict.Progressing, w.Observe(At(60), 0));
        Assert.Equal(ConcatVerdict.Progressing, w.Observe(At(120), 0));
        Assert.Equal(ConcatVerdict.TimedOut, w.Observe(At(121), 0));
    }
}
