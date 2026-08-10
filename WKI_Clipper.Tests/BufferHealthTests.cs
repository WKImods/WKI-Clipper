using System;
using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Pins down the real incident of 2026-08-10: the ring stopped turning while ffmpeg stayed
/// alive, and F9 kept serving the same 8-second, 15-hour-old clip because the only time
/// filter was the session start.
/// </summary>
public class BufferHealthTests
{
    private static readonly DateTime Now = new(2026, 8, 10, 3, 30, 33, DateTimeKind.Utc);

    [Fact]
    public void A_ring_that_keeps_turning_is_healthy()
    {
        // Newest completed segment is one segment old — normal operation.
        Assert.Equal(BufferMaterial.Ok, BufferHealth.Judge(Now.AddSeconds(-5), Now, 5));
    }

    [Fact]
    public void The_real_incident_is_recognised_as_stalled()
    {
        // The last completed segment was written 15.5 h earlier; everything since then went
        // into one open 46 MB segment. This is the case that produced the 8-second clip.
        var newestCompleted = new DateTime(2026, 8, 9, 12, 1, 17, DateTimeKind.Utc);
        Assert.Equal(BufferMaterial.Stalled, BufferHealth.Judge(newestCompleted, Now, 5));
    }

    [Theory]
    [InlineData(5, 19, BufferMaterial.Ok)]      // just inside the 20 s floor
    [InlineData(5, 21, BufferMaterial.Stalled)]
    [InlineData(30, 110, BufferMaterial.Ok)]    // 4x segment length = 120 s
    [InlineData(30, 130, BufferMaterial.Stalled)]
    public void Threshold_scales_with_segment_length_but_never_below_twenty_seconds(
        int segSec, int ageSec, BufferMaterial expected)
        => Assert.Equal(expected, BufferHealth.Judge(Now.AddSeconds(-ageSec), Now, segSec));

    [Fact]
    public void Absurd_segment_settings_cannot_produce_a_zero_threshold()
    {
        Assert.True(BufferHealth.StaleAfter(0) >= TimeSpan.FromSeconds(20));
        Assert.True(BufferHealth.StaleAfter(-5) >= TimeSpan.FromSeconds(20));
        Assert.True(BufferHealth.MaxGap(0) >= TimeSpan.FromSeconds(15));
    }

    // ---- contiguity ----

    private static List<DateTime> Series(params int[] secondsAgo)
        => secondsAgo.Select(s => Now.AddSeconds(-s)).OrderBy(t => t).ToList();

    [Fact]
    public void An_unbroken_run_starts_at_the_beginning()
    {
        var t = Series(25, 20, 15, 10, 5);
        Assert.Equal(0, BufferHealth.ContiguousTailStart(t, 5));
    }

    [Fact]
    public void Segments_from_before_a_long_gap_are_cut_off()
    {
        // Three fresh segments, and one from an hour ago that must not be spliced in.
        var t = Series(3600, 15, 10, 5);
        int start = BufferHealth.ContiguousTailStart(t, 5);

        Assert.Equal(1, start);                       // drop the ancient one
        Assert.Equal(3, t.Count - start);
    }

    [Fact]
    public void With_several_gaps_only_the_newest_run_survives()
    {
        var t = Series(7200, 7195, 600, 595, 15, 10, 5);
        int start = BufferHealth.ContiguousTailStart(t, 5);
        Assert.Equal(3, t.Count - start);             // the last three only
    }

    [Fact]
    public void Empty_and_single_element_series_are_harmless()
    {
        Assert.Equal(0, BufferHealth.ContiguousTailStart(new List<DateTime>(), 5));
        Assert.Equal(0, BufferHealth.ContiguousTailStart(Series(5), 5));
        Assert.Equal(0, BufferHealth.ContiguousTailStart(null!, 5));
    }
}
