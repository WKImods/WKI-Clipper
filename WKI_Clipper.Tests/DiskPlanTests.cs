using System;
using System.Collections.Generic;
using System.Globalization;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class DiskPlanTests
{
    private const long GiB = 1L << 30;

    [Fact]
    public void A_thirty_minute_ring_has_375_segments()
        => Assert.Equal(375, DiskPlan.RingSegments(1800, 5));

    [Fact]
    public void Default_buffer_ring_size()
        // pick 12 + reserve 6 + open 1 + tail 1 + hold 1 + prune lag 6
        => Assert.Equal(12 + RingPlan.Reserve + 3 + 6, DiskPlan.RingSegments(60, 5));

    [Fact]
    public void Segment_estimate_matches_the_reference_at_25_mbit()
    {
        long seg = DiskPlan.EstimateSegmentBytes(5, 25_000_000);
        Assert.InRange(seg, 17_400_000, 17_500_000);   // ~17.45 MB
    }

    [Fact]
    public void Thirty_minutes_at_25_mbit_need_about_six_and_a_half_gb()
    {
        long seg = DiskPlan.EstimateSegmentBytes(5, 25_000_000);
        long required = DiskPlan.RequiredBytes(1800, 5, seg);
        Assert.InRange(required, 6.5e9 * 0.99, 6.5e9 * 1.01);
    }

    [Fact]
    public void Very_high_native_ultrawide_needs_about_24_gb()
    {
        // 70 Mbit/s WQHD base x (3440x1440 / 2560x1440 = 1.34375) = 94.06 Mbit/s
        long bitrate = (long)(70_000_000 * 1.34375);
        long seg = DiskPlan.EstimateSegmentBytes(5, bitrate);
        long required = DiskPlan.RequiredBytes(1800, 5, seg);
        Assert.InRange(required, 24.3e9 * 0.99, 24.3e9 * 1.01);
    }

    [Fact]
    public void Default_effective_bitrate_for_the_real_presets_matches_the_reference()
    {
        // The reference numbers must come from the real preset table, not only from hand-typed bitrates.
        var s = new AppSettings();
        s.Video.Quality = QualityPreset.Mittel;
        s.Video.Resolution = ResolutionPreset.WQHD;
        Assert.Equal(25_000_000, FFmpegCommandBuilder.EffectiveBitrate(s, 0));
    }

    [Fact]
    public void Configured_length_that_fits_is_kept()
    {
        long seg = 17_450_000;
        long need = DiskPlan.RequiredBytes(1800, 5, seg);
        var d = DiskPlan.Decide(1800, 5, seg, need + 2 * GiB);
        Assert.Equal(1800, d.EffectiveSeconds);
        Assert.False(d.Shortened);
        Assert.False(d.CannotStart);
        Assert.Equal(need, d.RequiredBytes);
    }

    [Fact]
    public void Exactly_required_plus_headroom_fits()
    {
        long seg = 17_450_000;
        long need = DiskPlan.RequiredBytes(1800, 5, seg);
        Assert.False(DiskPlan.Decide(1800, 5, seg, need + DiskPlan.HeadroomBytes).Shortened);
        Assert.True(DiskPlan.Decide(1800, 5, seg, need + DiskPlan.HeadroomBytes - 1).Shortened);
    }

    [Fact]
    public void One_byte_short_picks_the_largest_smaller_choice_that_fits()
    {
        long seg = 17_450_000;
        long need1800 = DiskPlan.RequiredBytes(1800, 5, seg);
        var d = DiskPlan.Decide(1800, 5, seg, need1800 + DiskPlan.HeadroomBytes - 1);
        Assert.True(d.Shortened);
        Assert.Equal(1200, d.EffectiveSeconds);
    }

    [Fact]
    public void A_small_disk_gets_a_much_shorter_lookback()
    {
        long seg = 17_450_000;
        long usable = DiskPlan.RequiredBytes(300, 5, seg) + DiskPlan.HeadroomBytes + 1;
        var d = DiskPlan.Decide(1800, 5, seg, usable);
        Assert.Equal(300, d.EffectiveSeconds);
        Assert.True(d.Shortened);
    }

    [Fact]
    public void Nothing_that_fits_means_the_buffer_cannot_start()
    {
        var d = DiskPlan.Decide(1800, 5, 17_450_000, DiskPlan.HeadroomBytes);
        Assert.True(d.CannotStart);
        Assert.Equal(0, d.EffectiveSeconds);
        Assert.False(d.Shortened);
    }

    [Fact]
    public void A_length_between_two_choices_falls_back_to_the_choice_below_it()
    {
        long seg = 17_450_000;
        long usable = DiskPlan.RequiredBytes(100, 5, seg) + DiskPlan.HeadroomBytes - 1;   // 100 s itself does not fit
        var d = DiskPlan.Decide(100, 5, seg, usable);
        Assert.Equal(90, d.EffectiveSeconds);
    }

    [Fact]
    public void Observed_rate_needs_four_entries_and_a_time_span()
    {
        var t = new DateTime(2026, 10, 17, 21, 0, 0, DateTimeKind.Utc);
        var three = new List<(long, DateTime)> { (10, t), (10, t.AddSeconds(5)), (10, t.AddSeconds(10)) };
        Assert.Null(DiskPlan.ObservedBytesPerSecond(three));

        var zeroSpan = new List<(long, DateTime)> { (10, t), (10, t), (10, t), (10, t) };
        Assert.Null(DiskPlan.ObservedBytesPerSecond(zeroSpan));
    }

    [Fact]
    public void Observed_rate_skips_the_first_segment_because_it_only_anchors_the_span()
    {
        var t = new DateTime(2026, 10, 17, 21, 0, 0, DateTimeKind.Utc);
        var list = new List<(long, DateTime)>
        {
            (999_000_000, t),                 // ignored: written before t
            (5_000_000, t.AddSeconds(5)),
            (5_000_000, t.AddSeconds(10)),
            (5_000_000, t.AddSeconds(15)),
        };
        Assert.Equal(1_000_000, DiskPlan.ObservedBytesPerSecond(list));
    }

    [Fact]
    public void Segment_bytes_takes_the_larger_of_estimate_and_observation()
    {
        Assert.Equal(20_000_000, DiskPlan.SegmentBytes(20_000_000, 1_000_000, 5));       // observed 5.25 MB < estimate
        Assert.Equal((long)Math.Ceiling(8_000_000 * 5 * 1.05), DiskPlan.SegmentBytes(20_000_000, 8_000_000, 5));
        Assert.Equal(20_000_000, DiskPlan.SegmentBytes(20_000_000, null, 5));
    }

    [Fact]
    public void Format_bytes_uses_binary_gigabytes_in_the_current_culture()
    {
        var c = CultureInfo.CurrentCulture;
        Assert.Equal((6.5).ToString("F1", c) + " GB", DiskPlan.FormatBytes((long)(6.5 * GiB)));
        Assert.Equal("512 MB", DiskPlan.FormatBytes(512L << 20));
        Assert.Equal((2.0).ToString("F2", c) + " TB", DiskPlan.FormatBytes(2L << 40));
    }

    [Fact]
    public void Thresholds_are_ordered()
    {
        Assert.True(DiskPlan.CriticalBytes < DiskPlan.LowSpaceBytes);
        Assert.True(DiskPlan.LowSpaceBytes < DiskPlan.HeadroomBytes);
    }
}

public sealed class DiskProbeTests
{
    [Fact]
    public void A_unc_path_is_unknown()
        => Assert.False(DiskProbe.TryGetFreeSpace(@"\\server\share\buffer", out _, out _));

    [Fact]
    public void A_blank_path_is_unknown()
        => Assert.False(DiskProbe.TryGetFreeSpace("  ", out _, out _));

    [Fact]
    public void The_temp_drive_reports_free_space_and_a_label()
    {
        Assert.True(DiskProbe.TryGetFreeSpace(System.IO.Path.GetTempPath(), out long free, out string drive));
        Assert.True(free > 0);
        Assert.EndsWith(":", drive);
    }
}
