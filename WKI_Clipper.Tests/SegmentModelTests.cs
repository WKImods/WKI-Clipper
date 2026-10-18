using System;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class SegmentNameTests
{
    [Theory]
    [InlineData("seg_12_07.mp4", 12)]
    [InlineData("SEG_3_00.MP4", 3)]
    [InlineData("seg_0_00.mp4", 0)]
    [InlineData("seg_2147483647_01.mp4", int.MaxValue)]
    public void Parses_the_generation_of_a_segment(string name, int expected)
    {
        Assert.True(SegmentName.TryParseGeneration(name, out int gen));
        Assert.Equal(expected, gen);
    }

    [Theory]
    [InlineData("seg_x_01.mp4")]
    [InlineData("seg_1.mp4")]
    [InlineData("seg_1_2_3.mp4")]
    [InlineData("_concat_list.txt")]
    [InlineData("seg_99999999999_01.mp4")]
    [InlineData("seg_-1_01.mp4")]
    [InlineData("seg_1_01.mp4.part")]
    [InlineData("")]
    public void Rejects_everything_that_is_not_a_segment_name(string name)
        => Assert.False(SegmentName.TryParseGeneration(name, out _));

    [Fact]
    public void Rejects_null()
        => Assert.False(SegmentName.TryParseGeneration(null, out _));
}

public sealed class SegmentIdentityTests
{
    private static string Stream(string codec = "h264_amf", int fps = 60, ResolutionPreset res = ResolutionPreset.Native,
        int audio = 1, string path = "wgc", KeyframeMode kf = KeyframeMode.EncoderDefault)
        => SegmentIdentity.StreamKey(codec, fps, res, audio, path, kf);

    [Fact]
    public void Equal_source_and_stream_are_equal_identities()
    {
        var a = new SegmentIdentity("mon:0:3440x1440", Stream());
        var b = new SegmentIdentity("mon:0:3440x1440", Stream());
        Assert.Equal(a, b);
    }

    [Fact]
    public void Each_encoder_parameter_changes_the_stream_key()
    {
        var baseKey = Stream();
        Assert.NotEqual(baseKey, Stream(codec: "hevc_amf"));
        Assert.NotEqual(baseKey, Stream(fps: 30));
        Assert.NotEqual(baseKey, Stream(res: ResolutionPreset.FullHD));
        Assert.NotEqual(baseKey, Stream(audio: 2));
        Assert.NotEqual(baseKey, Stream(audio: 0));
        Assert.NotEqual(baseKey, Stream(path: "dda"));
        Assert.NotEqual(baseKey, Stream(kf: KeyframeMode.ForcedAtSegmentBoundary));
    }

    [Fact]
    public void The_settings_key_is_what_the_user_configured_not_how_the_target_was_captured()
    {
        var wgc = new SegmentIdentity("wgc:100:1x1", Stream(path: "wgc", audio: 2));
        var dda = new SegmentIdentity("mon:0:1x1", Stream(path: "dda", audio: 1));
        var amf = new SegmentIdentity("mon:0:1x1", Stream(path: "amf", audio: 0));

        Assert.NotEqual(wgc.Stream, dda.Stream);                 // never joined into one clip ...
        Assert.Equal(wgc.SettingsKey, dda.SettingsKey);          // ... but the same recording settings
        Assert.Equal(wgc.SettingsKey, amf.SettingsKey);
    }

    [Fact]
    public void Each_recording_setting_changes_the_settings_key()
    {
        string Key(string codec = "h264_amf", int fps = 60, ResolutionPreset res = ResolutionPreset.Native,
                   KeyframeMode kf = KeyframeMode.EncoderDefault)
            => new SegmentIdentity("s", Stream(codec, fps, res, kf: kf)).SettingsKey;

        var baseKey = Key();
        Assert.NotEqual(baseKey, Key(codec: "hevc_amf"));
        Assert.NotEqual(baseKey, Key(fps: 30));
        Assert.NotEqual(baseKey, Key(res: ResolutionPreset.FullHD));
        Assert.NotEqual(baseKey, Key(kf: KeyframeMode.ForcedAtSegmentBoundary));
    }

    [Fact]
    public void The_settings_key_is_built_by_one_formatter_and_read_back_by_the_identity()
        => Assert.Equal(
            SegmentIdentity.SettingsKeyOf("hevc_nvenc", 120, ResolutionPreset.UHD, KeyframeMode.FixedOneSecondGop),
            new SegmentIdentity("s", Stream("hevc_nvenc", 120, ResolutionPreset.UHD, 2, "amf", KeyframeMode.FixedOneSecondGop)).SettingsKey);

    [Fact]
    public void A_stream_key_in_an_unknown_format_is_its_own_settings_key()
        => Assert.Equal("anything", new SegmentIdentity("s", "anything").SettingsKey);

    [Fact]
    public void Source_keys_match_the_strings_the_buffer_always_used()
    {
        Assert.Equal("wgc:65872:2560x1440", SegmentIdentity.SourceKey(true, new IntPtr(65872), 2560, 1440, 0));
        Assert.Equal("mon:1:3440x1440", SegmentIdentity.SourceKey(false, IntPtr.Zero, 3440, 1440, 1));
    }

    [Fact]
    public void A_window_source_knows_its_handle()
    {
        var id = new SegmentIdentity(SegmentIdentity.SourceKey(true, new IntPtr(65872), 2560, 1440, 0), "x");
        Assert.True(id.IsWgc);
        Assert.Equal(new IntPtr(65872), id.WgcHwnd);
    }

    [Fact]
    public void A_monitor_source_has_no_handle()
    {
        var id = new SegmentIdentity("mon:0:1920x1080", "x");
        Assert.False(id.IsWgc);
        Assert.Equal(IntPtr.Zero, id.WgcHwnd);
    }

    [Fact]
    public void A_mangled_window_source_yields_no_handle()
        => Assert.Equal(IntPtr.Zero, new SegmentIdentity("wgc:not-a-number:1x1", "x").WgcHwnd);
}
