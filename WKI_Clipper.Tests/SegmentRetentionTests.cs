using System;
using System.Collections.Generic;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>One test per retention rule - first match wins, so each rule needs a case the earlier ones don't catch.</summary>
public sealed class SegmentRetentionTests
{
    private static readonly DateTime Now = new(2026, 10, 17, 21, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SessionStart = Now.AddHours(-3);
    private const int Window = 1840;   // 30 min ring

    private const string Stream = "h264_amf|60|Native|a1|wgc|EncoderDefault";
    private static readonly SegmentIdentity ArmaA = new("wgc:100:2560x1440", Stream);
    private static readonly SegmentIdentity ArmaB = new("wgc:200:2560x1440", Stream);
    private static readonly SegmentIdentity ArmaOtherCodec = new("wgc:100:2560x1440", "hevc_amf|60|Native|a1|wgc|EncoderDefault");

    private static GenerationInfo Gen(int g, SegmentIdentity id) => new(g, id, "x", SessionStart, null);

    private static RetentionContext Ctx(int current, SegmentIdentity currentId, Func<IntPtr, bool>? alive = null,
        params GenerationInfo[] gens)
    {
        var map = new Dictionary<int, GenerationInfo>();
        foreach (var g in gens) map[g.Generation] = g;
        return new RetentionContext(Now, SessionStart, Window, current, currentId, map, alive ?? (_ => true));
    }

    private static SegmentFileFacts File(string name, double ageSeconds) => new(name, Now.AddSeconds(-ageSeconds));

    // ---- rule 1: unknown names / generations ----

    [Fact]
    public void Rule1_foreign_file_from_before_the_session_is_deleted()
        => Assert.True(SegmentRetention.ShouldDelete(
            new SegmentFileFacts("seg_x_01.mp4", SessionStart.AddMinutes(-1)), Ctx(2, ArmaA, null, Gen(2, ArmaA))));

    [Fact]
    public void Rule1_unknown_generation_inside_the_session_is_kept_it_may_be_created_right_now()
        => Assert.False(SegmentRetention.ShouldDelete(File("seg_9_01.mp4", 1), Ctx(2, ArmaA, null, Gen(2, ArmaA))));

    [Fact]
    public void Rule1_unknown_generation_before_the_session_is_deleted()
        => Assert.True(SegmentRetention.ShouldDelete(
            new SegmentFileFacts("seg_9_01.mp4", SessionStart.AddSeconds(-5)), Ctx(2, ArmaA, null, Gen(2, ArmaA))));

    // ---- rule 2 ----

    [Fact]
    public void Rule2_known_generation_from_before_the_session_is_deleted()
        => Assert.True(SegmentRetention.ShouldDelete(
            new SegmentFileFacts("seg_2_01.mp4", SessionStart.AddSeconds(-1)), Ctx(2, ArmaA, null, Gen(2, ArmaA))));

    // ---- rule 3: current generation, age only ----

    [Fact]
    public void Rule3_current_generation_inside_the_window_is_kept()
        => Assert.False(SegmentRetention.ShouldDelete(File("seg_2_05.mp4", Window - 60), Ctx(2, ArmaA, null, Gen(2, ArmaA))));

    [Fact]
    public void Rule3_current_generation_older_than_the_window_is_deleted()
        => Assert.True(SegmentRetention.ShouldDelete(File("seg_2_05.mp4", Window + 5), Ctx(2, ArmaA, null, Gen(2, ArmaA))));

    // ---- rule 4: stream change deletes at once ----

    [Fact]
    public void Rule4_previous_generation_with_another_codec_goes_immediately()
        => Assert.True(SegmentRetention.ShouldDelete(File("seg_1_05.mp4", 30),
            Ctx(2, ArmaA, null, Gen(1, ArmaOtherCodec), Gen(2, ArmaA))));

    // ---- rule 4 only looks at the settings: the capture path and the audio tracks follow the TARGET ----

    private static SegmentIdentity Id(string source, string path = "wgc", int audio = 1, string codec = "h264_amf",
        int fps = 60, ResolutionPreset res = ResolutionPreset.Native, KeyframeMode kf = KeyframeMode.EncoderDefault)
        => new(source, SegmentIdentity.StreamKey(codec, fps, res, audio, path, kf));

    [Theory]
    [InlineData("dda", "wgc")]   // exclusive-fullscreen game on the monitor, Alt-Tab to a window
    [InlineData("wgc", "dda")]   // a window game, then an elevated window that WGC cannot capture
    [InlineData("amf", "dda")]   // crosshair toggled with the AMF capture opted in
    [InlineData("dda", "amf")]
    public void An_alt_tab_to_another_capture_path_does_not_delete_the_games_footage(string gamePath, string currentPath)
    {
        var game = Id("mon:0:3440x1440", gamePath);
        var other = Id("wgc:200:1280x720", currentPath);
        var ctx = Ctx(2, other, _ => true, Gen(1, game), Gen(2, other));

        Assert.False(SegmentRetention.ShouldDelete(File("seg_1_05.mp4", 120), ctx));
    }

    [Fact]
    public void A_failed_process_loopback_audio_init_for_the_new_target_does_not_delete_the_old_footage()
    {
        var twoTracks = Id("wgc:100:2560x1440", audio: 2);
        var oneTrack = Id("wgc:200:1280x720", audio: 1);
        var ctx = Ctx(2, oneTrack, _ => true, Gen(1, twoTracks), Gen(2, oneTrack));

        Assert.False(SegmentRetention.ShouldDelete(File("seg_1_05.mp4", 120), ctx));
    }

    [Fact]
    public void Footage_of_another_capture_path_still_ages_out()
    {
        var game = Id("mon:0:3440x1440", "dda");
        var other = Id("wgc:200:1280x720", "wgc");
        var ctx = Ctx(2, other, _ => true, Gen(1, game), Gen(2, other));

        Assert.True(SegmentRetention.ShouldDelete(File("seg_1_05.mp4", Window + 10), ctx));
    }

    [Fact]
    public void Footage_of_a_closed_window_goes_even_when_the_capture_path_differs()
    {
        var game = Id("wgc:100:2560x1440", "wgc");
        var current = Id("mon:0:3440x1440", "dda");
        var ctx = Ctx(2, current, h => h != new IntPtr(100), Gen(1, game), Gen(2, current));

        Assert.True(SegmentRetention.ShouldDelete(File("seg_1_05.mp4", 120), ctx));
    }

    [Theory]
    [InlineData("codec")]
    [InlineData("fps")]
    [InlineData("resolution")]
    [InlineData("keyframes")]
    public void A_changed_recording_setting_still_deletes_the_previous_generation_at_once(string what)
    {
        var before = Id("wgc:100:2560x1440");
        var after = what switch
        {
            "codec" => Id("wgc:100:2560x1440", codec: "hevc_amf"),
            "fps" => Id("wgc:100:2560x1440", fps: 30),
            "resolution" => Id("wgc:100:2560x1440", res: ResolutionPreset.FullHD),
            _ => Id("wgc:100:2560x1440", kf: KeyframeMode.ForcedAtSegmentBoundary),
        };
        var ctx = Ctx(2, after, _ => true, Gen(1, before), Gen(2, after));

        Assert.True(SegmentRetention.ShouldDelete(File("seg_1_05.mp4", 30), ctx));
    }

    // ---- rule 5: window closed ----

    [Fact]
    public void Rule5_other_window_that_no_longer_exists_goes_immediately()
        => Assert.True(SegmentRetention.ShouldDelete(File("seg_1_05.mp4", 30),
            Ctx(2, ArmaA, h => h != new IntPtr(200), Gen(1, ArmaB), Gen(2, ArmaA))));

    [Fact]
    public void Rule5_checks_the_handle_of_the_old_generation_not_the_current_one()
    {
        IntPtr? asked = null;
        SegmentRetention.ShouldDelete(File("seg_1_05.mp4", 30),
            Ctx(2, ArmaA, h => { asked = h; return true; }, Gen(1, ArmaB), Gen(2, ArmaA)));
        Assert.Equal(new IntPtr(200), asked);
    }

    // ---- rule 6/7 ----

    [Fact]
    public void Rule6_other_generation_that_aged_out_is_deleted()
        => Assert.True(SegmentRetention.ShouldDelete(File("seg_1_05.mp4", Window + 5),
            Ctx(2, ArmaA, null, Gen(1, ArmaA), Gen(2, ArmaA))));

    [Fact]
    public void Rule7_same_stream_older_generation_inside_the_window_is_kept()
        => Assert.False(SegmentRetention.ShouldDelete(File("seg_1_05.mp4", 600),
            Ctx(2, ArmaA, null, Gen(1, ArmaA), Gen(2, ArmaA))));

    [Fact]
    public void A_different_source_with_the_same_stream_and_a_live_window_is_kept_until_it_ages_out()
    {
        var ctx = Ctx(2, ArmaA, _ => true, Gen(1, ArmaB), Gen(2, ArmaA));
        Assert.False(SegmentRetention.ShouldDelete(File("seg_1_05.mp4", Window - 10), ctx));
        Assert.True(SegmentRetention.ShouldDelete(File("seg_1_05.mp4", Window + 10), ctx));
    }

    [Fact]
    public void A_monitor_source_never_asks_whether_a_window_is_alive()
    {
        var mon = new SegmentIdentity("mon:0:3440x1440", Stream);
        bool asked = false;
        SegmentRetention.ShouldDelete(File("seg_1_05.mp4", 30),
            Ctx(2, ArmaA, _ => { asked = true; return false; }, Gen(1, mon), Gen(2, ArmaA)));
        Assert.False(asked);
    }

    // ---- window size ----

    [Fact]
    public void Age_window_covers_the_pick_window_plus_the_reserve_and_two()
    {
        var ring = RingPlan.Create(1800, 5);
        // 360 picks x 5 s + (6 + 2) x 5 s
        Assert.Equal(1800 + 40, SegmentRetention.AgeWindowSeconds(ring, ring));
    }

    [Fact]
    public void Age_window_reaches_the_larger_of_running_and_configured_ring()
    {
        var small = RingPlan.Create(60, 5);
        var big = RingPlan.Create(1800, 5);
        Assert.Equal(SegmentRetention.AgeWindowSeconds(big, big), SegmentRetention.AgeWindowSeconds(small, big));
        Assert.Equal(SegmentRetention.AgeWindowSeconds(big, big), SegmentRetention.AgeWindowSeconds(big, small));
        Assert.True(SegmentRetention.AgeWindowSeconds(small, small) < SegmentRetention.AgeWindowSeconds(big, big));
    }

    [Fact]
    public void The_whole_ring_always_fits_into_the_age_window()
    {
        // The ring holds Wrap segments; the oldest is (Wrap - 1) x seg old at most. Prune must never eat live ring files.
        foreach (var d in DurationText.Choices)
        {
            var ring = RingPlan.Create(d, 5);
            Assert.True((ring.Wrap + 1) * ring.SegmentSeconds <= SegmentRetention.AgeWindowSeconds(ring, ring) + ring.SegmentSeconds,
                $"duration {d}");
        }
    }
}
