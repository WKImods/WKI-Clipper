using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Which capture path the command builder picks, and why it matters.
///
/// The ddagrab path copies every frame GPU→RAM (hwdownload) and the encoder uploads it
/// back. Measured on the dev machine over 10 s of 3440×1440@60 capture: 5.24 s of CPU
/// versus 0.84 s for AMF's own capture source — plus roughly 1.2 GB/s of bus traffic the
/// game is competing for. These tests keep the fast path from silently disappearing, and
/// keep it off the cases where it does not apply.
/// </summary>
public class CapturePathTests
{
    private static AppSettings Amf(ResolutionPreset res = ResolutionPreset.Native)
    {
        var s = new AppSettings();
        s.Video.Codec = "h264_amf";
        s.Video.Resolution = res;
        return s;
    }

    [Fact]
    public void Amf_full_monitor_capture_stays_on_the_gpu()
    {
        var args = FFmpegCommandBuilder.Build(Amf(), "out.mp4", segmentOutput: false, monitorIndex: 0);

        Assert.Contains("vsrc_amf", args);
        Assert.Contains("-init_hw_device amf", args);
        Assert.DoesNotContain("hwdownload", args);   // the whole point
        Assert.DoesNotContain("ddagrab", args);
    }

    [Fact]
    public void The_monitor_index_is_carried_over_to_the_amf_source()
    {
        var args = FFmpegCommandBuilder.Build(Amf(), "out.mp4", segmentOutput: false, monitorIndex: 2);
        Assert.Contains("vsrc_amf=monitor_index=2", args);
    }

    [Theory]
    [InlineData(ResolutionPreset.FullHD)]
    [InlineData(ResolutionPreset.WQHD)]
    [InlineData(ResolutionPreset.UHD)]
    public void A_downscale_falls_back_to_ddagrab(ResolutionPreset res)
    {
        // AMF surfaces cannot be scaled and letterboxed by the software scale/pad chain,
        // so a non-native resolution keeps the old, slower but correct path.
        var args = FFmpegCommandBuilder.Build(Amf(res), "out.mp4", segmentOutput: false);

        Assert.Contains("ddagrab", args);
        Assert.Contains("hwdownload", args);
        Assert.DoesNotContain("vsrc_amf", args);
    }

    [Fact]
    public void Window_capture_is_untouched()
    {
        // WGC frames arrive through a rawvideo pipe and are already in system memory.
        var args = FFmpegCommandBuilder.Build(Amf(), "out.mp4", segmentOutput: false,
            videoInputArgs: @"-f rawvideo -pixel_format bgra -video_size 800x600 -framerate 60 -i \\.\pipe\v");

        Assert.DoesNotContain("vsrc_amf", args);
        Assert.DoesNotContain("ddagrab", args);
        Assert.DoesNotContain("hwdownload", args);
    }

    [Theory]
    [InlineData("h264_nvenc")]
    [InlineData("h264_qsv")]
    [InlineData("libx264")]
    public void Non_amd_encoders_keep_the_ddagrab_path(string codec)
    {
        // vsrc_amf is an AMD component; nothing else can consume its surfaces.
        var s = new AppSettings();
        s.Video.Codec = codec;
        s.Video.Resolution = ResolutionPreset.Native;

        var args = FFmpegCommandBuilder.Build(s, "out.mp4", segmentOutput: false);

        Assert.Contains("ddagrab", args);
        Assert.Contains("hwdownload", args);
        Assert.DoesNotContain("vsrc_amf", args);
    }

    [Fact]
    public void An_enabled_crosshair_forces_the_ddagrab_path()
    {
        // vsrc_amf ignores WDA_EXCLUDEFROMCAPTURE, so AMF's capture records the aiming
        // overlay into every clip; ddagrab honours it. Verified side by side on the real
        // screen. Correct footage wins over the cheaper path.
        var s = Amf();
        s.Crosshair.Enabled = true;

        var args = FFmpegCommandBuilder.Build(s, "out.mp4", segmentOutput: false);

        Assert.Contains("ddagrab", args);
        Assert.Contains("hwdownload", args);
        Assert.DoesNotContain("vsrc_amf", args);
    }

    [Fact]
    public void Without_a_crosshair_the_fast_path_returns()
    {
        var s = Amf();
        s.Crosshair.Enabled = false;

        var args = FFmpegCommandBuilder.Build(s, "out.mp4", segmentOutput: false);

        Assert.Contains("vsrc_amf", args);
        Assert.DoesNotContain("hwdownload", args);
    }

    [Fact]
    public void Audio_mapping_survives_the_new_video_input()
    {
        var args = FFmpegCommandBuilder.Build(Amf(), "out.mp4", segmentOutput: false,
            audioPipeArgs: new[] { "-i a", "-i b" });

        Assert.Contains("-map 0:v", args);
        Assert.Contains("-map 1:a", args);
        Assert.Contains("-map 2:a", args);
    }
}
