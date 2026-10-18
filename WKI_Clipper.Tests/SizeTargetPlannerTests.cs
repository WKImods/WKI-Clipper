using System;
using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// The size-target planner is pure arithmetic, so every number below was worked out by hand from
/// the formulas (budget = limit x 0.98, audio x 1.02, mux = 2000 + 12 x dur x (fps + 47)) and then
/// compared with what the planner returns.
/// </summary>
public sealed class SizeTargetPlannerTests
{
    private const long Mb20 = 20_000_000;
    private const long Mb500 = 500_000_000;

    // ---- the ladder and the decision ----

    [Fact]
    public void Thirty_seconds_into_20_MB_of_1080p60_drops_to_1080p30()
    {
        var plan = SizeTargetPlanner.Plan(30, 1920, 1080, 60, Mb20);

        Assert.False(plan.TooLong);
        var r = plan.Start;
        Assert.Equal((1920, 1080, 30), (r.Width, r.Height, r.Fps));
        Assert.Equal(5_088_181, r.VideoBps);   // (156.8 Mbit - 3.9168 Mbit audio - 0.23776 Mbit mux) / 30 s
        Assert.Equal(128, r.AudioKbps);
        Assert.Equal(5_088_181 / (1920.0 * 1080 * 30), r.Bpp, 6);
        Assert.True(r.Bpp >= 0.05);
    }

    [Fact]
    public void Thirty_seconds_of_ultrawide_keeps_the_aspect_ratio_and_is_not_cropped()
    {
        var plan = SizeTargetPlanner.Plan(30, 3440, 1440, 60, Mb20);

        var r = plan.Start;
        Assert.Equal((2580, 1080, 30), (r.Width, r.Height, r.Fps));   // 1080 is the SHORT side
        Assert.Equal(5_088_181, r.VideoBps);
    }

    [Fact]
    public void One_minute_into_20_MB_needs_720p30()
    {
        var plan = SizeTargetPlanner.Plan(60, 1920, 1080, 60, Mb20);

        var r = plan.Start;
        Assert.Equal((1280, 720, 30), (r.Width, r.Height, r.Fps));
        Assert.Equal(2_475_114, r.VideoBps);
        Assert.Equal(128, r.AudioKbps);
        // 720p60 at the same budget is only 0.0447 bpp - below both 0.05 and the 60 fps floor of 0.045
        var p60 = plan.Rungs.Single(x => x.Height == 720 && x.Fps == 60);
        Assert.True(p60.Bpp < 0.045);
    }

    [Fact]
    public void Five_minutes_into_20_MB_of_1080p60_ends_at_the_480p30_floor_with_the_smallest_audio()
    {
        var plan = SizeTargetPlanner.Plan(300, 1920, 1080, 60, Mb20);

        Assert.False(plan.TooLong);
        var r = plan.Start;
        Assert.Equal((854, 480, 30), (r.Width, r.Height, r.Fps));
        Assert.Equal(64, r.AudioKbps);          // video would be < 600 kbit/s even with 96k audio
        Assert.Equal(449_941, r.VideoBps);
        Assert.InRange(r.Bpp, 0.035, 0.05);     // accepted only because nothing reached 0.05
    }

    [Fact]
    public void Five_minutes_into_20_MB_of_ultrawide_is_too_long()
    {
        var plan = SizeTargetPlanner.Plan(300, 3440, 1440, 60, Mb20);

        Assert.True(plan.TooLong);
        Assert.Equal(-1, plan.StartIndex);
        // the lowest rung is still listed, with the bpp that sank it: 1146x480@30 -> 0.0273 < 0.035
        var last = plan.Rungs[^1];
        Assert.Equal((1146, 480, 30), (last.Width, last.Height, last.Fps));
        Assert.True(last.Bpp < 0.035);
        Assert.Throws<InvalidOperationException>(() => plan.Start);
    }

    [Fact]
    public void Thirty_minutes_into_20_MB_is_far_too_long()
    {
        var plan = SizeTargetPlanner.Plan(1800, 1920, 1080, 60, Mb20);
        Assert.True(plan.TooLong);
        Assert.True(plan.Rungs.All(r => r.VideoBps < 50_000));
    }

    [Fact]
    public void Thirty_minutes_into_500_MB_of_1080p60_ends_at_720p30()
    {
        var plan = SizeTargetPlanner.Plan(1800, 1920, 1080, 60, Mb500);

        var r = plan.Start;
        Assert.Equal((1280, 720, 30), (r.Width, r.Height, r.Fps));
        Assert.Equal(128, r.AudioKbps);          // video is above 1.5 Mbit/s even with 128k audio
        Assert.InRange(r.VideoBps, 2_000_000, 2_100_000);
    }

    [Fact]
    public void Thirty_seconds_into_500_MB_of_ultrawide_keeps_the_top_rung()
    {
        var plan = SizeTargetPlanner.Plan(30, 3440, 1440, 60, Mb500);

        var r = plan.Start;
        Assert.Equal((2580, 1080, 60), (r.Width, r.Height, r.Fps));
        Assert.Equal(130_525_301, r.VideoBps);
    }

    [Fact]
    public void The_source_bitrate_caps_the_video_so_a_big_target_is_not_filled_for_nothing()
    {
        // 30 s into "500 MB" must not turn into a 487 MB file: CBR spends every bit it is given.
        var plan = SizeTargetPlanner.Plan(30, 3440, 1440, 60, Mb500, sourceBitsPerSecond: 70_000_000);

        Assert.Equal(70_000_000, plan.Start.VideoBps);
        Assert.All(plan.Rungs, r => Assert.True(r.VideoBps <= 70_000_000));
    }

    [Fact]
    public void Five_minutes_into_500_MB_of_1080p60_keeps_60_fps()
    {
        var plan = SizeTargetPlanner.Plan(300, 1920, 1080, 60, Mb500);

        var r = plan.Start;
        Assert.Equal((1920, 1080, 60), (r.Width, r.Height, r.Fps));
        Assert.Equal(12_925_781, r.VideoBps);
        Assert.Equal(128, r.AudioKbps);
    }

    // ---- never upscale, odd shapes ----

    [Fact]
    public void A_720p30_source_is_never_upscaled_and_the_ladder_starts_at_the_source()
    {
        var plan = SizeTargetPlanner.Plan(30, 1280, 720, 30, Mb20);

        Assert.Equal(new[] { (1280, 720, 30), (960, 540, 30), (854, 480, 30) },
            plan.Rungs.Select(r => (r.Width, r.Height, r.Fps)).ToArray());
        Assert.Equal(0, plan.StartIndex);
    }

    [Fact]
    public void A_source_below_480p_is_kept_at_its_own_size()
    {
        var plan = SizeTargetPlanner.Plan(20, 640, 360, 30, Mb20);

        Assert.Single(plan.Rungs);
        Assert.Equal((640, 360, 30), (plan.Start.Width, plan.Start.Height, plan.Start.Fps));
    }

    [Fact]
    public void The_frame_rate_never_goes_up_and_59_94_counts_as_60()
    {
        var p5994 = SizeTargetPlanner.Plan(30, 1920, 1080, 59.94, Mb500);
        Assert.Equal(60, p5994.Rungs[0].Fps);

        var p24 = SizeTargetPlanner.Plan(30, 1920, 1080, 24, Mb20);
        Assert.All(p24.Rungs, r => Assert.Equal(24, r.Fps));

        var p120 = SizeTargetPlanner.Plan(30, 1920, 1080, 120, Mb500);
        Assert.Equal(60, p120.Rungs[0].Fps);   // 60 is the ladder's ceiling
    }

    [Fact]
    public void An_unknown_frame_rate_is_planned_as_30()
    {
        var plan = SizeTargetPlanner.Plan(30, 1920, 1080, 0, Mb500);
        Assert.All(plan.Rungs, r => Assert.True(r.Fps <= 30));
    }

    [Fact]
    public void Widths_are_even_for_odd_aspect_ratios()
    {
        var plan = SizeTargetPlanner.Plan(30, 1366, 768, 30, Mb500);   // 1366 x 720/768 = 1280.6
        Assert.All(plan.Rungs, r => { Assert.Equal(0, r.Width % 2); Assert.Equal(0, r.Height % 2); });
        Assert.Equal((1280, 720), (plan.Rungs.First(r => r.Height == 720).Width, 720));
    }

    [Fact]
    public void A_portrait_source_measures_its_short_side_which_is_the_width()
    {
        var plan = SizeTargetPlanner.Plan(30, 1080, 1920, 60, Mb500);
        var r = plan.Rungs[0];
        Assert.Equal((1080, 1920), (r.Width, r.Height));
        var r720 = plan.Rungs.First(x => x.Width == 720);
        Assert.Equal(1280, r720.Height);
    }

    [Fact]
    public void Without_audio_the_whole_budget_goes_to_the_video()
    {
        var with = SizeTargetPlanner.Plan(30, 1920, 1080, 30, Mb20, hasAudio: true).Start;
        var without = SizeTargetPlanner.Plan(30, 1920, 1080, 30, Mb20, hasAudio: false).Start;

        Assert.Equal(0, without.AudioKbps);
        Assert.True(without.VideoBps > with.VideoBps);
    }

    [Theory]
    [InlineData(0, 1920, 1080, 60, 20_000_000)]
    [InlineData(-5, 1920, 1080, 60, 20_000_000)]
    [InlineData(30, 0, 1080, 60, 20_000_000)]
    [InlineData(30, 1920, 0, 60, 20_000_000)]
    [InlineData(30, 1920, 1080, 60, 0)]
    public void Impossible_input_is_rejected(double dur, int w, int h, double fps, long target)
        => Assert.Throws<ArgumentOutOfRangeException>(() => SizeTargetPlanner.Plan(dur, w, h, fps, target));

    // ---- audio steps ----

    [Theory]
    [InlineData(30, 128)]    // plenty of video bitrate
    [InlineData(150, 96)]    // 0.9 Mbit/s of video: under 1.5 M with 128k audio, over 600 k with 96k
    [InlineData(300, 64)]
    public void Audio_bitrate_steps_down_with_the_video_budget(double seconds, int expectedKbps)
    {
        var rung = SizeTargetPlanner.Plan(seconds, 1920, 1080, 30, Mb20).Rungs.Last();
        Assert.Equal(expectedKbps, rung.AudioKbps);
    }

    [Fact]
    public void The_audio_step_is_judged_on_the_video_rate_that_is_left_after_that_audio()
    {
        // Whatever step is chosen, the video rate must respect that step's own threshold.
        foreach (var seconds in new double[] { 20, 60, 90, 120, 150, 200, 300, 600 })
        {
            var rung = SizeTargetPlanner.Plan(seconds, 1920, 1080, 30, Mb20).Rungs.Last();
            if (rung.AudioKbps == 128) Assert.True(rung.VideoBps >= 1_500_000, $"{seconds}s");
            if (rung.AudioKbps == 96) Assert.True(rung.VideoBps >= 600_000, $"{seconds}s");
        }
    }

    // ---- the budget really fits the limit ----

    [Theory]
    [InlineData(30, Mb20)]
    [InlineData(60, Mb20)]
    [InlineData(300, Mb20)]
    [InlineData(1800, Mb500)]
    public void Budget_plus_audio_plus_mux_stays_under_the_limit(double seconds, long limit)
    {
        var plan = SizeTargetPlanner.Plan(seconds, 1920, 1080, 60, limit);
        Assert.False(plan.TooLong);
        var r = plan.Start;

        double videoBytes = r.VideoBps * seconds / 8;
        Assert.True(videoBytes + r.AudioBytes + r.MuxBytes <= limit * 0.98 + 4,
            $"{videoBytes + r.AudioBytes + r.MuxBytes} vs {limit * 0.98}");
        // ... and uses nearly all of it (floor() only throws away < 1 bit/s)
        Assert.True(videoBytes + r.AudioBytes + r.MuxBytes >= limit * 0.98 - seconds);
    }

    [Fact]
    public void The_measured_video_rate_of_a_finished_file_removes_audio_and_mux()
    {
        var plan = SizeTargetPlanner.Plan(30, 1920, 1080, 60, Mb20);
        var r = plan.Start;
        long fileBytes = (long)(r.VideoBps * 30 / 8.0) + r.AudioBytes + r.MuxBytes;

        long actual = plan.ActualVideoBps(plan.StartIndex, fileBytes);

        Assert.InRange(actual, r.VideoBps - 5, r.VideoBps + 5);
        Assert.True(plan.ActualVideoBps(plan.StartIndex, 10) >= 1);   // never zero or negative
    }

    // ---- choosing the start rung: the threshold edges ----

    private static CompressRung Rung(int h, int fps, double bpp) => new(h * 16 / 9, h, fps, 1_000_000, 128, bpp, 0, 0);

    [Fact]
    public void A_rung_at_exactly_0_05_bpp_wins_and_one_just_below_loses()
    {
        var rungs = new[] { Rung(1080, 30, 0.0499), Rung(720, 30, 0.05), Rung(480, 30, 0.2) };
        Assert.Equal(1, SizeTargetPlanner.ChooseStart(rungs));
    }

    [Fact]
    public void Without_a_rung_at_0_05_the_first_one_at_0_035_is_taken()
    {
        var rungs = new[] { Rung(1080, 30, 0.02), Rung(720, 30, 0.035), Rung(480, 30, 0.049) };
        Assert.Equal(1, SizeTargetPlanner.ChooseStart(rungs));
    }

    [Fact]
    public void Nothing_at_0_035_means_too_long()
    {
        var rungs = new[] { Rung(720, 30, 0.01), Rung(480, 30, 0.0349) };
        Assert.Equal(-1, SizeTargetPlanner.ChooseStart(rungs));
        Assert.Equal(-1, SizeTargetPlanner.ChooseStart(Array.Empty<CompressRung>()));
    }

    [Fact]
    public void A_high_frame_rate_rung_needs_at_least_0_045_even_in_the_fallback_pass()
    {
        // pass 1 finds nothing >= 0.05; in pass 2 the 60 fps rung at 0.04 is skipped for the 30 fps one
        var rungs = new[] { Rung(720, 60, 0.04), Rung(720, 30, 0.049) };
        Assert.Equal(1, SizeTargetPlanner.ChooseStart(rungs));

        // 0.0449 is still too low for 60 fps, 0.045 is enough
        Assert.Equal(1, SizeTargetPlanner.ChooseStart(new[] { Rung(720, 60, 0.0449), Rung(480, 30, 0.04) }));
        Assert.Equal(0, SizeTargetPlanner.ChooseStart(new[] { Rung(720, 60, 0.045), Rung(480, 30, 0.04) }));
    }

    // ---- ShouldCopyInstead ----

    private static MediaProbe Probe(string? video, params string[] audio)
        => new(TimeSpan.FromSeconds(30), video, 1920, 1080, 60, audio);

    [Theory]
    [InlineData("h264", "aac", 19_999_999L, true)]
    [InlineData("h264", "aac", 20_000_000L, true)]      // exactly the target still fits
    [InlineData("h264", "aac", 20_000_001L, false)]
    public void Copy_only_when_the_file_already_fits(string video, string audio, long bytes, bool expected)
        => Assert.Equal(expected, SizeTargetPlanner.ShouldCopyInstead(Probe(video, audio), bytes, Mb20));

    [Fact]
    public void Hevc_av1_and_vp9_are_re_encoded_for_discord_compatibility()
    {
        foreach (var codec in new[] { "hevc", "av1", "vp9", "mpeg4" })
            Assert.False(SizeTargetPlanner.ShouldCopyInstead(Probe(codec, "aac"), 1_000_000, Mb20), codec);
    }

    [Fact]
    public void A_separate_mic_track_or_a_foreign_audio_codec_is_re_encoded()
    {
        Assert.False(SizeTargetPlanner.ShouldCopyInstead(Probe("h264", "aac", "aac"), 1_000_000, Mb20));
        Assert.False(SizeTargetPlanner.ShouldCopyInstead(Probe("h264", "opus"), 1_000_000, Mb20));
        Assert.False(SizeTargetPlanner.ShouldCopyInstead(Probe("h264"), 1_000_000, Mb20));
    }

    [Fact]
    public void An_audio_only_file_is_never_copied()
        => Assert.False(SizeTargetPlanner.ShouldCopyInstead(Probe(null, "aac"), 1_000, Mb20));
}

/// <summary>The retry policy: proportional correction, the quality floor, the attempt cap and the software fallback.</summary>
public sealed class SizeTargetPlannerAttemptTests
{
    // 30 s of 1920x1080@60 into 20 MB: start rung 1080p30 (index 1 of 6 rungs - 1080p60 is listed first).
    private static SizePlan Plan() => SizeTargetPlanner.Plan(30, 1920, 1080, 60, 20_000_000);

    private static void AssertNear(double expected, long actual, double tolerance = 3)
        => Assert.InRange(actual, expected - tolerance, expected + tolerance);

    private static AttemptRecord Attempt(SizePlan plan, int rung, long request, double overshoot, bool software = false)
    {
        // a file that is "overshoot" times too large at the planned rate: bytes = limit * overshoot
        long actualVideo = (long)(plan.Rungs[rung].VideoBps * overshoot);
        long bytes = (long)(plan.TargetBytes * 0.98 * overshoot);
        return new AttemptRecord(rung, request, actualVideo, bytes, software);
    }

    [Fact]
    public void A_file_within_the_limit_is_done()
    {
        var plan = Plan();
        var step = SizeTargetPlanner.NextAttempt(plan, new[] { Attempt(plan, plan.StartIndex, plan.Start.VideoBps, 0.99) }, true);
        Assert.Equal(AttemptStepKind.Done, step.Kind);
    }

    [Fact]
    public void A_file_exactly_at_the_limit_is_done()
    {
        var plan = Plan();
        var a = new AttemptRecord(plan.StartIndex, plan.Start.VideoBps, plan.Start.VideoBps, plan.TargetBytes, false);
        Assert.Equal(AttemptStepKind.Done, SizeTargetPlanner.NextAttempt(plan, new[] { a }, true).Kind);
    }

    [Fact]
    public void A_ten_percent_overshoot_asks_for_the_budget_scaled_down_by_the_miss_and_3_percent()
    {
        var plan = Plan();
        long request = plan.Start.VideoBps;
        var a = Attempt(plan, plan.StartIndex, request, 1.10);

        var step = SizeTargetPlanner.NextAttempt(plan, new[] { a }, true);

        Assert.Equal(AttemptStepKind.Retry, step.Kind);
        Assert.Equal(plan.StartIndex, step.RungIndex);
        Assert.False(step.Software);
        // prev x (budget / actual) x 0.97 = budget x 0.97 / 1.10
        AssertNear(request * 0.97 / 1.10, step.RequestBps);
    }

    [Fact]
    public void The_second_correction_aims_at_the_budget_not_at_the_previous_request()
    {
        // Attempt 2 delivered 1.04 x the budget at a request of 0.88 x budget (the encoder is not linear).
        // Aiming at the previous request would ask for 0.88 x 0.88/1.04 = 0.74; the budget gives 0.88 x 0.97/1.04.
        var plan = Plan();
        long b = plan.Start.VideoBps;
        var a1 = new AttemptRecord(plan.StartIndex, b, (long)(b * 1.10), 22_000_000, false);
        var a2 = new AttemptRecord(plan.StartIndex, (long)(b * 0.88), (long)(b * 1.04), 20_700_000, false);

        var step = SizeTargetPlanner.NextAttempt(plan, new[] { a1, a2 }, true);

        Assert.Equal(AttemptStepKind.Retry, step.Kind);
        AssertNear(b * 0.88 * 0.97 / 1.04, step.RequestBps);
    }

    [Fact]
    public void A_retry_always_asks_for_at_least_3_percent_less_than_before()
    {
        // The file is over the limit although the video rate looks fine (the audio was bigger than
        // planned): the formula alone would ask for MORE than before. It must still go lower.
        var plan = Plan();
        long request = plan.Start.VideoBps;
        var a = new AttemptRecord(plan.StartIndex, request, (long)(request * 0.90), plan.TargetBytes + 1, false);

        var step = SizeTargetPlanner.NextAttempt(plan, new[] { a }, true);

        Assert.Equal(AttemptStepKind.Retry, step.Kind);
        Assert.True(step.RequestBps <= request * 0.97 + 1, $"{step.RequestBps} vs {request}");
    }

    [Fact]
    public void A_request_that_falls_20_percent_while_the_result_barely_moves_means_the_floor_is_reached()
    {
        var plan = Plan();
        long b = plan.Start.VideoBps;
        var a1 = new AttemptRecord(plan.StartIndex, b, (long)(b * 1.30), 26_000_000, false);
        var a2 = new AttemptRecord(plan.StartIndex, (long)(b * 0.74), (long)(b * 1.25), 25_000_000, false);   // -26 % request, -3.8 % result

        var step = SizeTargetPlanner.NextAttempt(plan, new[] { a1, a2 }, true);

        Assert.Equal(AttemptStepKind.NextRung, step.Kind);
        Assert.True(step.RungIndex > plan.StartIndex);
        var next = plan.Rungs[step.RungIndex];
        Assert.Equal(next.VideoBps, step.RequestBps);   // recomputed from the budget of that rung
    }

    [Fact]
    public void A_request_drop_under_20_percent_is_no_floor_even_if_the_result_did_not_move()
    {
        var plan = Plan();
        long b = plan.Start.VideoBps;
        var a1 = new AttemptRecord(plan.StartIndex, b, (long)(b * 1.12), 22_400_000, false);
        var a2 = new AttemptRecord(plan.StartIndex, (long)(b * 0.85), (long)(b * 1.12), 22_400_000, false);   // -15 % request

        Assert.Equal(AttemptStepKind.Retry, SizeTargetPlanner.NextAttempt(plan, new[] { a1, a2 }, true).Kind);
    }

    [Fact]
    public void A_result_that_dropped_15_percent_or_more_is_no_floor()
    {
        var plan = Plan();
        long b = plan.Start.VideoBps;
        var a1 = new AttemptRecord(plan.StartIndex, b, (long)(b * 1.30), 26_000_000, false);
        var a2 = new AttemptRecord(plan.StartIndex, (long)(b * 0.74), (long)(b * 1.10), 22_000_000, false);   // -15.4 % result

        Assert.Equal(AttemptStepKind.Retry, SizeTargetPlanner.NextAttempt(plan, new[] { a1, a2 }, true).Kind);
    }

    [Fact]
    public void A_floor_is_also_found_by_comparing_with_the_first_attempt_of_the_rung()
    {
        // Small steps (-15 % each) never trip a pairwise rule, but together they are -28 % request for -0.9 % result.
        var plan = Plan();
        long b = plan.Start.VideoBps;
        var a1 = new AttemptRecord(plan.StartIndex, b, (long)(b * 1.15), 23_000_000, false);
        var a2 = new AttemptRecord(plan.StartIndex, (long)(b * 0.85), (long)(b * 1.14), 22_800_000, false);
        var a3 = new AttemptRecord(plan.StartIndex, (long)(b * 0.715), (long)(b * 1.14), 22_800_000, false);

        Assert.Equal(AttemptStepKind.NextRung, SizeTargetPlanner.NextAttempt(plan, new[] { a1, a2, a3 }, true).Kind);
    }

    [Fact]
    public void Stepping_down_skips_rungs_that_would_not_help()
    {
        // From 1080p30 the next listed rung is 720p60: 11 % fewer pixels per second means only ~12 % more
        // bits per pixel - not enough to get away from a quality floor. The step lands on 720p30.
        var plan = Plan();
        Assert.Equal((1920, 1080, 30), (plan.Start.Width, plan.Start.Height, plan.Start.Fps));
        long b = plan.Start.VideoBps;
        var a1 = new AttemptRecord(plan.StartIndex, b, (long)(b * 1.30), 26_000_000, false);
        var a2 = new AttemptRecord(plan.StartIndex, (long)(b * 0.74), (long)(b * 1.25), 25_000_000, false);

        var step = SizeTargetPlanner.NextAttempt(plan, new[] { a1, a2 }, true);

        var next = plan.Rungs[step.RungIndex];
        Assert.Equal((1280, 720, 30), (next.Width, next.Height, next.Fps));
    }

    [Fact]
    public void At_the_lowest_rung_a_floor_goes_straight_to_software()
    {
        var plan = SizeTargetPlanner.Plan(300, 1920, 1080, 60, 20_000_000);   // starts on the last rung
        int last = plan.Rungs.Count - 1;
        Assert.Equal(last, plan.StartIndex);
        long b = plan.Start.VideoBps;
        var a1 = new AttemptRecord(last, b, (long)(b * 1.30), 26_000_000, false);
        var a2 = new AttemptRecord(last, (long)(b * 0.74), (long)(b * 1.25), 25_000_000, false);

        var step = SizeTargetPlanner.NextAttempt(plan, new[] { a1, a2 }, softwareFallbackAllowed: true);

        Assert.Equal(AttemptStepKind.SoftwareFallback, step.Kind);
        Assert.True(step.Software);
        Assert.Equal(last, step.RungIndex);
        Assert.Equal(plan.Rungs[last].VideoBps, step.RequestBps);

        Assert.Equal(AttemptStepKind.GiveUp, SizeTargetPlanner.NextAttempt(plan, new[] { a1, a2 }, softwareFallbackAllowed: false).Kind);
    }

    [Fact]
    public void After_five_attempts_the_lowest_rung_is_tried_in_software_once()
    {
        var plan = Plan();
        long b = plan.Start.VideoBps;
        var history = Enumerable.Range(0, 5)
            .Select(i => new AttemptRecord(plan.StartIndex, (long)(b * (1.0 - 0.02 * i)), (long)(b * 1.1), 22_000_000, false))
            .ToList();

        var step = SizeTargetPlanner.NextAttempt(plan, history, true);

        Assert.Equal(AttemptStepKind.SoftwareFallback, step.Kind);
        Assert.True(step.Software);
        Assert.Equal(plan.Rungs.Count - 1, step.RungIndex);                    // the lowest rung
        Assert.Equal(plan.Rungs[^1].VideoBps, step.RequestBps);
    }

    [Fact]
    public void Without_a_software_fallback_five_misses_give_up()
    {
        var plan = Plan();
        long b = plan.Start.VideoBps;
        var history = Enumerable.Range(0, 5)
            .Select(i => new AttemptRecord(plan.StartIndex, (long)(b * (1.0 - 0.02 * i)), (long)(b * 1.1), 22_000_000, false))
            .ToList();

        Assert.Equal(AttemptStepKind.GiveUp, SizeTargetPlanner.NextAttempt(plan, history, false).Kind);
    }

    [Fact]
    public void A_missed_software_fallback_gives_up()
    {
        var plan = Plan();
        var history = new[]
        {
            new AttemptRecord(plan.StartIndex, plan.Start.VideoBps, (long)(plan.Start.VideoBps * 1.1), 22_000_000, false),
            new AttemptRecord(plan.Rungs.Count - 1, plan.Rungs[^1].VideoBps, plan.Rungs[^1].VideoBps, 20_500_000, true),
        };
        Assert.Equal(AttemptStepKind.GiveUp, SizeTargetPlanner.NextAttempt(plan, history, true).Kind);
    }

    [Fact]
    public void When_software_is_the_only_encoder_it_retries_with_the_correction_and_never_looks_for_a_floor()
    {
        var plan = Plan();
        long b = plan.Start.VideoBps;
        var a1 = new AttemptRecord(plan.StartIndex, b, (long)(b * 1.05), 21_000_000, true);

        var step = SizeTargetPlanner.NextAttempt(plan, new[] { a1 }, softwareFallbackAllowed: false);

        Assert.Equal(AttemptStepKind.Retry, step.Kind);
        Assert.True(step.Software);
        AssertNear(b * 0.97 / 1.05, step.RequestBps);
    }

    [Fact]
    public void A_request_that_would_fall_to_nothing_counts_as_the_floor()
    {
        var plan = Plan();
        var a = new AttemptRecord(plan.StartIndex, 60_000, 60_000_000, 90_000_000, false);   // absurd overshoot
        var step = SizeTargetPlanner.NextAttempt(plan, new[] { a }, true);
        Assert.NotEqual(AttemptStepKind.Retry, step.Kind);
    }

    [Fact]
    public void Max_attempts_is_five()
        => Assert.Equal(5, SizeTargetPlanner.MaxAttempts);
}

