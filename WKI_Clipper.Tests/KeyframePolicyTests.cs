using System.Text.Json;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Fixed keyframe interval: the arguments per encoder family (names verified on ffmpeg 8.1).</summary>
public sealed class KeyframePolicyTests
{
    [Fact]
    public void Encoder_default_adds_nothing()
    {
        Assert.Equal("", KeyframePolicy.EncoderArgs("h264_amf", 60, 5, KeyframeMode.EncoderDefault));
        Assert.Null(KeyframePolicy.SegmentTimeDelta(60, KeyframeMode.EncoderDefault));
    }

    [Theory]
    [InlineData("h264_amf", "-forced_idr 1")]
    [InlineData("hevc_amf", "-forced_idr 1")]
    [InlineData("av1_amf", "-forced_idr 1")]
    [InlineData("h264_qsv", "-forced_idr 1")]
    [InlineData("hevc_qsv", "-forced_idr 1")]
    [InlineData("h264_nvenc", "-forced-idr 1")]
    [InlineData("hevc_nvenc", "-forced-idr 1")]
    [InlineData("libx264", "-forced-idr 1")]
    [InlineData("libx265", "-forced-idr 1")]
    public void Forced_keyframes_must_be_IDR_frames_with_the_encoders_own_switch(string codec, string idrFlag)
    {
        var args = KeyframePolicy.EncoderArgs(codec, 60, 5, KeyframeMode.ForcedAtSegmentBoundary);
        Assert.Contains(idrFlag, args);
        Assert.Contains("-force_key_frames", args);
    }

    [Fact]
    public void Forced_keyframe_expression_is_exact()
        => Assert.Contains("\"expr:gte(t,n_forced*5)\"",
            KeyframePolicy.EncoderArgs("h264_amf", 60, 5, KeyframeMode.ForcedAtSegmentBoundary));

    [Fact]
    public void Forced_keyframe_expression_follows_the_segment_length()
        => Assert.Contains("n_forced*1)", KeyframePolicy.EncoderArgs("libx264", 30, 1, KeyframeMode.ForcedAtSegmentBoundary));

    [Fact]
    public void An_unknown_encoder_still_gets_the_forced_keyframes_but_no_idr_flag()
    {
        var args = KeyframePolicy.EncoderArgs("mpeg4", 30, 5, KeyframeMode.ForcedAtSegmentBoundary);
        Assert.Contains("-force_key_frames", args);
        Assert.DoesNotContain("idr", args);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    public void Fixed_gop_is_one_second(int fps)
        => Assert.Equal($"-g {fps}", KeyframePolicy.EncoderArgs("h264_amf", fps, 5, KeyframeMode.FixedOneSecondGop));

    [Fact]
    public void Fixed_gop_with_hevc_amf_also_pins_one_gop_per_idr()
        => Assert.Equal("-g 60 -gops_per_idr 1", KeyframePolicy.EncoderArgs("hevc_amf", 60, 5, KeyframeMode.FixedOneSecondGop));

    [Theory]
    [InlineData("h264_amf", 60, 5, "-force_key_frames \"expr:gte(t,n_forced*5)\" -forced_idr 1 -g 300")]
    [InlineData("hevc_nvenc", 30, 5, "-force_key_frames \"expr:gte(t,n_forced*5)\" -forced-idr 1 -g 150")]
    [InlineData("libx264", 30, 1, "-force_key_frames \"expr:gte(t,n_forced*1)\" -forced-idr 1 -g 30")]
    public void Forced_boundary_with_segment_gop_is_the_forced_variant_plus_a_gop_as_long_as_the_segment(
        string codec, int fps, int seg, string expected)
        => Assert.Equal(expected, KeyframePolicy.EncoderArgs(codec, fps, seg, KeyframeMode.ForcedBoundaryWithSegmentGop));

    [Fact]
    public void Every_keyframe_mode_except_the_default_gets_the_half_frame_segment_delta()
    {
        foreach (var mode in System.Enum.GetValues<KeyframeMode>())
            Assert.Equal(mode != KeyframeMode.EncoderDefault, KeyframePolicy.SegmentTimeDelta(60, mode) is not null);
    }

    [Fact]
    public void The_new_mode_is_read_from_settings_json_by_name()
    {
        var rb = JsonSerializer.Deserialize<ReplayBufferSettings>("{\"KeyframeModeOverride\":\"ForcedBoundaryWithSegmentGop\"}")!;
        Assert.Equal(KeyframeMode.ForcedBoundaryWithSegmentGop, rb.KeyframeModeOverride);
    }

    [Fact]
    public void The_original_modes_keep_their_numbers_so_nothing_stored_changes_meaning()
    {
        Assert.Equal(0, (int)KeyframeMode.EncoderDefault);
        Assert.Equal(1, (int)KeyframeMode.ForcedAtSegmentBoundary);
        Assert.Equal(2, (int)KeyframeMode.FixedOneSecondGop);
    }

    [Fact]
    public void Segment_time_delta_is_half_a_frame()
    {
        Assert.Equal(0.5 / 60, KeyframePolicy.SegmentTimeDelta(60, KeyframeMode.ForcedAtSegmentBoundary)!.Value, 9);
        Assert.Equal(0.5 / 30, KeyframePolicy.SegmentTimeDelta(30, KeyframeMode.FixedOneSecondGop)!.Value, 9);
    }

    [Fact]
    public void Default_stays_the_encoder_default_until_the_measurement_has_passed()
        => Assert.Equal(KeyframeMode.EncoderDefault, KeyframePolicy.Default);

    [Fact]
    public void Effective_honours_the_override()
    {
        var rb = new ReplayBufferSettings();
        Assert.Equal(KeyframePolicy.Default, KeyframePolicy.Effective(rb));
        rb.KeyframeModeOverride = KeyframeMode.ForcedAtSegmentBoundary;
        Assert.Equal(KeyframeMode.ForcedAtSegmentBoundary, KeyframePolicy.Effective(rb));
    }

    [Fact]
    public void The_override_is_read_from_settings_json_by_name()
    {
        var rb = JsonSerializer.Deserialize<ReplayBufferSettings>("{\"KeyframeModeOverride\":\"FixedOneSecondGop\"}")!;
        Assert.Equal(KeyframeMode.FixedOneSecondGop, rb.KeyframeModeOverride);
        Assert.True(rb.StitchAcrossGaps);
    }

    [Fact]
    public void Stitching_can_be_switched_off_in_settings_json()
    {
        var rb = JsonSerializer.Deserialize<ReplayBufferSettings>("{\"StitchAcrossGaps\":false}")!;
        Assert.False(rb.StitchAcrossGaps);
    }
}
