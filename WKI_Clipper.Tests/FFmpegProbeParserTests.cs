using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Samples are REAL `ffmpeg -hide_banner -i clip.mp4` stderr, captured from ffmpeg 8.1
/// (only the long input path is shortened).
/// </summary>
public sealed class FFmpegProbeParserTests
{
    private const string H264Aac = """
Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'clip.mp4':
  Metadata:
    major_brand     : isom
    minor_version   : 512
    compatible_brands: isomiso2avc1mp41
    encoder         : Lavf62.12.100
  Duration: 00:00:05.00, start: 0.000000, bitrate: 876 kb/s
  Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(progressive), 640x360 [SAR 1:1 DAR 16:9], 796 kb/s, 30 fps, 30 tbr, 15360 tbn (default)
    Metadata:
      handler_name    : VideoHandler
      encoder         : Lavc62.28.100 libx264
  Stream #0:1[0x2](und): Audio: aac (LC) (mp4a / 0x6134706D), 44100 Hz, mono, fltp, 69 kb/s (default)
    Metadata:
      handler_name    : SoundHandler
At least one output file must be specified
""";

    private const string HevcTwoAac = """
Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'clip.mp4':
  Metadata:
    major_brand     : isom
    minor_version   : 512
    compatible_brands: isomiso2mp41
    encoder         : Lavf62.12.100
  Duration: 00:00:03.00, start: 0.000000, bitrate: 2677 kb/s
  Stream #0:0[0x1](und): Video: hevc (Main) (hev1 / 0x31766568), yuv420p(tv, progressive), 1280x720 [SAR 1:1 DAR 16:9], 2510 kb/s, 60 fps, 60 tbr, 15360 tbn (default)
    Metadata:
      handler_name    : VideoHandler
      encoder         : Lavc62.28.100 libx265
  Stream #0:1[0x2](und): Audio: aac (LC) (mp4a / 0x6134706D), 44100 Hz, mono, fltp, 69 kb/s (default)
    Metadata:
      handler_name    : SoundHandler
  Stream #0:2[0x3](und): Audio: aac (LC) (mp4a / 0x6134706D), 44100 Hz, mono, fltp, 68 kb/s
    Metadata:
      handler_name    : SoundHandler
At least one output file must be specified
""";

    private const string NoAudio = """
Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'clip.mp4':
  Metadata:
    major_brand     : isom
    minor_version   : 512
    compatible_brands: isomiso2avc1mp41
    encoder         : Lavf62.12.100
  Duration: 00:00:02.00, start: 0.000000, bitrate: 793 kb/s
  Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(progressive), 640x360 [SAR 1:1 DAR 16:9], 787 kb/s, 30 fps, 30 tbr, 15360 tbn (default)
    Metadata:
      handler_name    : VideoHandler
      encoder         : Lavc62.28.100 libx264
At least one output file must be specified
""";

    [Fact]
    public void H264_with_one_aac_track()
    {
        var p = FFmpegProbeParser.Parse(H264Aac);
        Assert.NotNull(p);
        Assert.Equal(5.0, p!.Duration!.Value.TotalSeconds, 2);
        Assert.Equal("h264", p.VideoCodec);
        Assert.Equal(640, p.Width);
        Assert.Equal(360, p.Height);
        Assert.Equal(30.0, p.Fps, 3);
        Assert.Equal(1, p.AudioStreamCount);
        Assert.Equal(new[] { "aac" }, p.AudioCodecs);
    }

    [Fact]
    public void Hevc_with_two_aac_tracks()
    {
        var p = FFmpegProbeParser.Parse(HevcTwoAac);
        Assert.NotNull(p);
        Assert.Equal(3.0, p!.Duration!.Value.TotalSeconds, 2);
        Assert.Equal("hevc", p.VideoCodec);
        Assert.Equal(1280, p.Width);
        Assert.Equal(720, p.Height);
        Assert.Equal(60.0, p.Fps, 3);
        Assert.Equal(2, p.AudioStreamCount);
        Assert.Equal(new[] { "aac", "aac" }, p.AudioCodecs);
    }

    [Fact]
    public void Video_without_any_audio_track()
    {
        var p = FFmpegProbeParser.Parse(NoAudio);
        Assert.NotNull(p);
        Assert.Equal(2.0, p!.Duration!.Value.TotalSeconds, 2);
        Assert.Equal("h264", p.VideoCodec);
        Assert.Equal(0, p.AudioStreamCount);
        Assert.Empty(p.AudioCodecs);
    }

    [Fact]
    public void Fractional_frame_rate_and_hour_long_duration()
    {
        const string text = """
Input #0, matroska,webm, from 'rec.mkv':
  Duration: 01:02:03.45, start: 0.000000, bitrate: 25000 kb/s
  Stream #0:0: Video: h264 (High), yuv420p(tv, bt709, progressive), 2560x1440 [SAR 1:1 DAR 16:9], 59.94 fps, 59.94 tbr, 1k tbn (default)
  Stream #0:1: Audio: opus, 48000 Hz, stereo, fltp (default)
  Stream #0:2: Audio: opus, 48000 Hz, stereo, fltp
""";
        var p = FFmpegProbeParser.Parse(text);
        Assert.NotNull(p);
        Assert.Equal(3723.45, p!.Duration!.Value.TotalSeconds, 2);
        Assert.Equal(59.94, p.Fps, 2);
        Assert.Equal(2560, p.Width);
        Assert.Equal(1440, p.Height);
        Assert.Equal(new[] { "opus", "opus" }, p.AudioCodecs);
    }

    [Fact]
    public void Cover_art_is_not_taken_for_the_video_stream()
    {
        const string text = """
Input #0, mp3, from 'song.mp3':
  Duration: 00:03:00.00, start: 0.025057, bitrate: 320 kb/s
  Stream #0:0: Audio: mp3, 44100 Hz, stereo, fltp, 320 kb/s
  Stream #0:1: Video: mjpeg (Baseline), yuvj420p(pc, bt470bg/unknown/unknown), 500x500 [SAR 1:1 DAR 1:1], 90k tbr, 90k tbn (attached pic)
""";
        var p = FFmpegProbeParser.Parse(text);
        Assert.NotNull(p);
        Assert.Null(p!.VideoCodec);
        Assert.Equal(1, p.AudioStreamCount);
    }

    [Fact]
    public void Unknown_duration_is_null_not_zero()
    {
        const string text = """
Input #0, mpegts, from 'live.ts':
  Duration: N/A, start: 1.4, bitrate: N/A
  Stream #0:0: Video: h264 (Main), yuv420p, 1920x1080, 25 fps, 25 tbr, 90k tbn
""";
        var p = FFmpegProbeParser.Parse(text);
        Assert.NotNull(p);
        Assert.Null(p!.Duration);
        Assert.Equal(25.0, p.Fps, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nothing.txt: Invalid data found when processing input")]
    [InlineData("clip.mp4: No such file or directory")]
    public void Garbage_or_missing_file_yields_null(string text)
    {
        Assert.Null(FFmpegProbeParser.Parse(text));
    }
}
