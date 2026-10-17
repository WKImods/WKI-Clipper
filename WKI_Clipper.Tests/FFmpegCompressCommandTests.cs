using System;
using System.Linq;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// The compress argument lines, per encoder. The strings are the contract with ffmpeg: a flag that
/// moves, loses its quotes or changes spelling fails a Discord compress on someone else's machine,
/// where no one can see the stderr.
/// </summary>
public sealed class FFmpegCompressCommandTests
{
    private const string In = @"C:\Clips\Arma Reforger\Clip_1.mp4";
    private const string Out = @"C:\Clips\Komprimiert 20MB\Clip_1_20MB.mp4.part";

    private static CompressCommand Cmd(CompressEncoder encoder, int audioTracks = 1, bool hwDecode = true,
        int pass = 0, string? passLog = null, int? threads = null, int audioKbps = 128, long videoBps = 5_088_181)
        => new(In, Out, encoder, Width: 1920, Fps: 30, VideoBps: videoBps, AudioKbps: audioKbps,
               AudioTrackCount: audioTracks, HwDecode: hwDecode, Pass: pass, PassLogFile: passLog, Threads: threads);

    [Fact]
    public void Amf_single_pass_is_exactly_the_measured_recipe()
    {
        var args = FFmpegCommandBuilder.BuildCompress(Cmd(CompressEncoder.Amf));

        Assert.Equal(
            "-hide_banner -loglevel warning -hwaccel d3d11va -i \"C:\\Clips\\Arma Reforger\\Clip_1.mp4\" "
            + "-map 0:v:0 -vf \"scale=1920:-2:flags=bicubic,fps=30,format=yuv420p\" -map 0:a:0? "
            + "-c:v h264_amf -usage transcoding -quality quality -profile:v high -rc cbr -enforce_hrd 1 -b:v 5088181 "
            + "-c:a aac -b:a 128k -ar 48000 -ac 2 -movflags +faststart -f mp4 \"C:\\Clips\\Komprimiert 20MB\\Clip_1_20MB.mp4.part\"",
            args);
    }

    [Fact]
    public void Nvenc_uses_hq_cbr_with_a_two_second_buffer()
    {
        var args = FFmpegCommandBuilder.BuildCompress(Cmd(CompressEncoder.Nvenc, videoBps: 4_000_000));

        Assert.Contains("-c:v h264_nvenc -preset p5 -tune hq -rc cbr -b:v 4000000 -maxrate 4000000 -bufsize 8000000 ", args);
        Assert.DoesNotContain("h264_amf", args);
        Assert.DoesNotContain("-enforce_hrd", args);
    }

    [Fact]
    public void Quick_sync_uses_the_medium_preset_with_a_two_second_buffer()
    {
        var args = FFmpegCommandBuilder.BuildCompress(Cmd(CompressEncoder.Qsv, videoBps: 4_000_000));

        Assert.Contains("-c:v h264_qsv -preset medium -b:v 4000000 -maxrate 4000000 -bufsize 8000000 ", args);
    }

    [Fact]
    public void Software_pass_1_measures_only_and_writes_nowhere()
    {
        var args = FFmpegCommandBuilder.BuildCompress(Cmd(CompressEncoder.X264, pass: 1, passLog: @"C:\Temp\wki_abc"));

        Assert.Contains("-c:v libx264 -preset medium -b:v 5088181 -pass 1 -passlogfile \"C:\\Temp\\wki_abc\" ", args);
        Assert.EndsWith(" -an -f null NUL", args);
        Assert.DoesNotContain("-c:a", args);
        Assert.DoesNotContain("-map 0:a", args);
        Assert.DoesNotContain("-movflags", args);
        Assert.DoesNotContain(".part", args);   // nothing is written for the runner to rename
    }

    [Fact]
    public void Software_pass_2_reads_the_stats_and_writes_the_real_file_with_audio()
    {
        var args = FFmpegCommandBuilder.BuildCompress(Cmd(CompressEncoder.X264, pass: 2, passLog: @"C:\Temp\wki_abc"));

        Assert.Contains("-c:v libx264 -preset medium -b:v 5088181 -pass 2 -passlogfile \"C:\\Temp\\wki_abc\" ", args);
        Assert.Contains("-map 0:a:0? ", args);
        Assert.EndsWith(" -c:a aac -b:a 128k -ar 48000 -ac 2 -movflags +faststart -f mp4 \"" + Out + "\"", args);
    }

    [Fact]
    public void Both_software_passes_share_the_video_filter_or_the_stats_would_not_match()
    {
        var p1 = FFmpegCommandBuilder.BuildCompress(Cmd(CompressEncoder.X264, pass: 1, passLog: "p"));
        var p2 = FFmpegCommandBuilder.BuildCompress(Cmd(CompressEncoder.X264, pass: 2, passLog: "p"));
        const string vf = "-vf \"scale=1920:-2:flags=bicubic,fps=30,format=yuv420p\"";
        Assert.Contains(vf, p1);
        Assert.Contains(vf, p2);
    }

    [Fact]
    public void Software_without_a_pass_is_a_plain_single_pass_encode()
    {
        var args = FFmpegCommandBuilder.BuildCompress(Cmd(CompressEncoder.X264));

        Assert.Contains("-c:v libx264 -preset medium -b:v 5088181 ", args);
        Assert.DoesNotContain("-pass", args);
    }

    [Fact]
    public void Two_audio_tracks_are_mixed_into_one()
    {
        var args = FFmpegCommandBuilder.BuildCompress(Cmd(CompressEncoder.Amf, audioTracks: 2));

        Assert.Contains("-filter_complex \"[0:a:0][0:a:1]amix=inputs=2:duration=longest:normalize=0[a]\" -map \"[a]\" ", args);
        Assert.DoesNotContain("-map 0:a:0?", args);
        Assert.Contains("-map 0:v:0 ", args);   // the video is still mapped explicitly - never -map 0 (chapter data tracks)
    }

    [Fact]
    public void More_than_two_tracks_mix_the_first_two()
    {
        var args = FFmpegCommandBuilder.BuildCompress(Cmd(CompressEncoder.Amf, audioTracks: 3));
        Assert.Contains("amix=inputs=2:", args);
        Assert.DoesNotContain("[0:a:2]", args);
    }

    [Fact]
    public void A_clip_without_audio_gets_no_audio_flags()
    {
        var args = FFmpegCommandBuilder.BuildCompress(Cmd(CompressEncoder.Amf, audioTracks: 0, audioKbps: 0));

        Assert.Contains(" -an ", args);
        Assert.DoesNotContain("-c:a", args);
        Assert.DoesNotContain("-map 0:a", args);
    }

    [Fact]
    public void Nothing_is_ever_mapped_blindly()
    {
        foreach (var enc in Enum.GetValues<CompressEncoder>())
        {
            var args = FFmpegCommandBuilder.BuildCompress(Cmd(enc));
            Assert.DoesNotContain("-map 0 ", args);
            Assert.DoesNotContain("-map 0\"", args);
        }
    }

    [Fact]
    public void Hardware_decoding_can_be_switched_off()
    {
        var args = FFmpegCommandBuilder.BuildCompress(Cmd(CompressEncoder.Amf, hwDecode: false));
        Assert.DoesNotContain("-hwaccel", args);
        Assert.StartsWith("-hide_banner -loglevel warning -i ", args);
    }

    [Fact]
    public void Hardware_decoding_sits_in_front_of_the_input_it_applies_to()
    {
        var args = FFmpegCommandBuilder.BuildCompress(Cmd(CompressEncoder.Amf));
        Assert.True(args.IndexOf("-hwaccel d3d11va", StringComparison.Ordinal) < args.IndexOf(" -i ", StringComparison.Ordinal));
    }

    [Fact]
    public void The_overwrite_policy_is_left_to_the_runner()
    {
        foreach (var enc in Enum.GetValues<CompressEncoder>())
            Assert.DoesNotContain(FFmpegCommandBuilder.BuildCompress(Cmd(enc)).Split(' '), t => t is "-y" or "-n");
    }

    [Fact]
    public void The_output_path_is_quoted_and_last_so_the_runner_can_place_its_thread_cap_in_front_of_it()
    {
        foreach (var enc in new[] { CompressEncoder.Amf, CompressEncoder.Nvenc, CompressEncoder.Qsv })
            Assert.EndsWith("-f mp4 \"" + Out + "\"", FFmpegCommandBuilder.BuildCompress(Cmd(enc)));
    }

    [Fact]
    public void A_thread_limit_applies_to_the_software_encoder_only()
    {
        var x264 = FFmpegCommandBuilder.BuildCompress(Cmd(CompressEncoder.X264, pass: 2, passLog: "p", threads: 4));
        Assert.Contains("-threads 4 ", x264);

        var amf = FFmpegCommandBuilder.BuildCompress(Cmd(CompressEncoder.Amf, threads: 4));
        Assert.DoesNotContain("-threads", amf);

        var p1 = FFmpegCommandBuilder.BuildCompress(Cmd(CompressEncoder.X264, pass: 1, passLog: "p", threads: 4));
        Assert.EndsWith("-threads 4 -an -f null NUL", p1);
    }

    [Fact]
    public void The_scale_width_is_what_the_planner_chose()
    {
        var c = Cmd(CompressEncoder.Amf) with { Width = 2580, Fps = 60 };
        Assert.Contains("scale=2580:-2:flags=bicubic,fps=60,format=yuv420p", FFmpegCommandBuilder.BuildCompress(c));
    }

    [Theory]
    [InlineData(CompressEncoder.Amf)]
    [InlineData(CompressEncoder.Nvenc)]
    [InlineData(CompressEncoder.Qsv)]
    public void Hardware_encoders_have_no_file_pass(CompressEncoder enc)
        => Assert.Throws<ArgumentException>(() => FFmpegCommandBuilder.BuildCompress(Cmd(enc, pass: 1, passLog: "p")));

    [Fact]
    public void A_software_pass_needs_a_stats_file()
        => Assert.Throws<ArgumentException>(() => FFmpegCommandBuilder.BuildCompress(Cmd(CompressEncoder.X264, pass: 1)));

    [Fact]
    public void A_pass_other_than_0_1_2_is_rejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => FFmpegCommandBuilder.BuildCompress(Cmd(CompressEncoder.X264, pass: 3, passLog: "p")));

    [Fact]
    public void Nonsense_numbers_are_rejected_before_ffmpeg_sees_them()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FFmpegCommandBuilder.BuildCompress(Cmd(CompressEncoder.Amf, videoBps: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => FFmpegCommandBuilder.BuildCompress(Cmd(CompressEncoder.Amf) with { Width = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => FFmpegCommandBuilder.BuildCompress(Cmd(CompressEncoder.Amf) with { Fps = 0 }));
    }
}
