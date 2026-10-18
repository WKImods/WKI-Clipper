using System.Linq;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Phase 4 command-line changes: the concat with chapters, the segment muxer, keyframe arguments.</summary>
public sealed class LongClipCommandTests
{
    private const string Pipe = @"-f s16le -ar 48000 -ac 2 -i \\.\pipe\x";

    /// <summary>Fixed resolution on purpose: Native would read the monitor of the machine running the tests.</summary>
    private static AppSettings Fixed()
    {
        var s = new AppSettings();
        s.Video.Resolution = ResolutionPreset.WQHD;
        s.Video.Quality = QualityPreset.Mittel;
        s.Video.Codec = "h264_amf";
        s.Video.Framerate = 60;
        s.Audio.OffsetMilliseconds = 0;
        return s;
    }

    // The exact buffer command line before Phase 4. EncoderDefault must keep reproducing it byte for byte:
    // an unnoticed change here would alter what every user's encoder does.
    private const string BufferSnapshot =
        "-hide_banner -loglevel warning -nostats -fflags +nobuffer+flush_packets -flags low_delay " +
        "-f lavfi -i \"ddagrab=output_idx=0:framerate=60\" " +
        Pipe + " " +
        "-map 0:v -map 1:a " +
        "-vf \"hwdownload,format=bgra,scale=2560:1440:force_original_aspect_ratio=decrease," +
        "pad=2560:1440:(ow-iw)/2:(oh-ih)/2:black,setsar=1\" " +
        "-c:v h264_amf -quality balanced -rc cbr -b:v 25000000 " +
        "-c:a aac -b:a 192k " +
        "-f segment -segment_time 5 -segment_wrap 12 -reset_timestamps 1 -segment_format mp4 " +
        "\"C:\\buf\\seg_0_%02d.mp4\"";

    [Fact]
    public void Buffer_command_line_is_unchanged_with_the_encoder_default()
    {
        var args = FFmpegCommandBuilder.Build(Fixed(), @"C:\buf\seg_0_%02d.mp4", segmentOutput: true,
            segmentDurationSec: 5, segmentWrap: 12, audioPipeArgs: new[] { Pipe }, monitorIndex: 0);
        Assert.Equal(BufferSnapshot, args);
    }

    [Fact]
    public void Segment_muxer_args_without_a_delta_are_the_old_ones()
        => Assert.Equal("-f segment -segment_time 5 -segment_wrap 12 -reset_timestamps 1 -segment_format mp4 ",
            FFmpegCommandBuilder.SegmentMuxerArgs(5, 12, null));

    [Fact]
    public void Segment_muxer_args_with_a_delta_add_it_before_reset_timestamps()
        => Assert.Equal("-f segment -segment_time 5 -segment_wrap 367 -segment_time_delta 0.008333 -reset_timestamps 1 -segment_format mp4 ",
            FFmpegCommandBuilder.SegmentMuxerArgs(5, 367, 0.5 / 60));

    [Fact]
    public void Forced_keyframes_add_the_expression_the_idr_flag_and_the_delta()
    {
        var args = FFmpegCommandBuilder.Build(Fixed(), @"C:\buf\seg_0_%02d.mp4", segmentOutput: true,
            segmentDurationSec: 5, segmentWrap: 12, audioPipeArgs: new[] { Pipe }, monitorIndex: 0,
            keyframes: KeyframeMode.ForcedAtSegmentBoundary);

        Assert.Contains("-force_key_frames \"expr:gte(t,n_forced*5)\" -forced_idr 1 ", args);
        Assert.Contains("-segment_time_delta 0.008333 ", args);
        // after the rate control, before the audio codec
        Assert.True(args.IndexOf("-b:v 25000000", System.StringComparison.Ordinal)
                    < args.IndexOf("-force_key_frames", System.StringComparison.Ordinal));
        Assert.True(args.IndexOf("-force_key_frames", System.StringComparison.Ordinal)
                    < args.IndexOf("-c:a aac", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Forced_keyframes_for_a_cpu_encoder_sit_before_the_pixel_format()
    {
        var s = Fixed();
        s.Video.Codec = "libx264";
        var args = FFmpegCommandBuilder.Build(s, "seg_0_%02d.mp4", segmentOutput: true, audioPipeArgs: new[] { Pipe },
            keyframes: KeyframeMode.ForcedAtSegmentBoundary);
        Assert.Contains("-forced-idr 1 -pix_fmt yuv420p", args);
    }

    [Fact]
    public void A_manual_recording_never_gets_keyframe_arguments()
    {
        var args = FFmpegCommandBuilder.Build(Fixed(), "out.mp4", segmentOutput: false, audioPipeArgs: new[] { Pipe },
            keyframes: KeyframeMode.ForcedAtSegmentBoundary);
        Assert.DoesNotContain("force_key_frames", args);
        Assert.DoesNotContain("segment_time_delta", args);
        Assert.DoesNotContain("-forced_idr", args);
    }

    [Fact]
    public void Fixed_gop_adds_g_and_the_delta()
    {
        var args = FFmpegCommandBuilder.Build(Fixed(), "seg_0_%02d.mp4", segmentOutput: true, audioPipeArgs: new[] { Pipe },
            keyframes: KeyframeMode.FixedOneSecondGop);
        Assert.Contains("-g 60 ", args);
        Assert.Contains("-segment_time_delta 0.008333", args);
    }

    // ---- concat ----

    [Fact]
    public void Concat_defaults_are_the_old_command()
        => Assert.Equal(
            "-hide_banner -loglevel warning -f concat -safe 0 -i \"list.txt\" -map 0 -c copy -movflags +faststart -f mp4 \"out.mp4.part\"",
            FFmpegCommandBuilder.BuildConcat("list.txt", "out.mp4.part"));

    [Fact]
    public void Concat_without_faststart_has_no_movflags()
    {
        var args = FFmpegCommandBuilder.BuildConcat("list.txt", @"C:\c\Clip.mp4.part", faststart: false);
        Assert.DoesNotContain("faststart", args);
        Assert.DoesNotContain("-movflags", args);
        Assert.EndsWith("-f mp4 \"C:\\c\\Clip.mp4.part\"", args);
    }

    [Fact]
    public void Concat_with_chapters_reads_the_metadata_file_before_mapping_and_takes_chapters_from_it()
    {
        var args = FFmpegCommandBuilder.BuildConcat("list.txt", "out.mp4.part", faststart: false, chaptersFile: @"C:\buf\_chapters.txt");
        int meta = args.IndexOf("-f ffmetadata -i \"C:\\buf\\_chapters.txt\"", System.StringComparison.Ordinal);
        int map = args.IndexOf("-map 0 -map_chapters 1", System.StringComparison.Ordinal);
        Assert.True(meta > 0, args);
        Assert.True(map > meta, args);
        Assert.True(args.IndexOf("-i \"list.txt\"", System.StringComparison.Ordinal) < meta);
        Assert.Contains("-c copy", args);
        Assert.EndsWith("-f mp4 \"out.mp4.part\"", args);
    }

    [Fact]
    public void Concat_without_chapters_does_not_mention_metadata()
    {
        var args = FFmpegCommandBuilder.BuildConcat("list.txt", "out.mp4.part");
        Assert.DoesNotContain("ffmetadata", args);
        Assert.DoesNotContain("map_chapters", args);
    }

    // ---- capture-path identity ----

    [Fact]
    public void Amf_native_capture_is_only_for_the_full_monitor_without_scaling_and_without_a_window_pipe()
    {
        var s = new AppSettings();
        s.Video.Codec = "h264_amf";
        s.Video.UseAmfCapture = true;
        s.Video.Resolution = ResolutionPreset.Native;
        s.Crosshair.Enabled = false;
        s.Widgets.Widgets.Clear();
        s.PerfOverlay.Enabled = false;

        Assert.True(FFmpegCommandBuilder.UsesAmfNativeCapture(s, rawInput: false));
        Assert.False(FFmpegCommandBuilder.UsesAmfNativeCapture(s, rawInput: true));

        s.Video.Resolution = ResolutionPreset.FullHD;
        Assert.False(FFmpegCommandBuilder.UsesAmfNativeCapture(s, rawInput: false));
    }

    [Fact]
    public void Amf_native_capture_needs_the_opt_in()
    {
        var s = new AppSettings();
        s.Video.Resolution = ResolutionPreset.Native;
        s.Video.UseAmfCapture = false;
        Assert.False(FFmpegCommandBuilder.UsesAmfNativeCapture(s, rawInput: false));
    }

    [Fact]
    public void The_build_and_the_identity_agree_on_the_amf_capture_path()
    {
        var s = new AppSettings();
        s.Video.Codec = "h264_amf";
        s.Video.UseAmfCapture = true;
        s.Video.Resolution = ResolutionPreset.Native;
        s.Crosshair.Enabled = false;
        s.Widgets.Widgets.Clear();
        s.PerfOverlay.Enabled = false;
        bool expected = FFmpegCommandBuilder.UsesAmfNativeCapture(s, rawInput: false);
        var args = FFmpegCommandBuilder.Build(s, "o.mp4", segmentOutput: false);
        Assert.Equal(expected, args.Contains("vsrc_amf"));
    }
}
