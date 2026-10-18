using System;
using System.Globalization;
using System.IO;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Tests that switch the app language run alone: <see cref="L"/> is process-wide, and a parallel test asserting
/// German text would otherwise see English.
/// </summary>
[CollectionDefinition("Language", DisableParallelization = true)]
public sealed class LanguageCollection { }

internal static class TestLanguage
{
    public static IDisposable English() => new Switch(AppLanguage.English);

    private sealed class Switch : IDisposable
    {
        public Switch(AppLanguage language) => Set(language);
        public void Dispose() => Set(AppLanguage.Deutsch);
        private static void Set(AppLanguage language)
        {
            var s = new AppSettings();
            s.Behavior.Language = language;
            L.Init(s);
        }
    }
}

public sealed class ClipCutRulesTests
{
    private const string Dash = "\u2013";

    // ---- 1: output name ----

    [Theory]
    [InlineData(@"C:\Clips\Clip_x.mp4", @"C:\Clips\Clip_x_cut.mp4")]
    [InlineData(@"C:\Clips\Clip_x.mov", @"C:\Clips\Clip_x_cut.mp4")]
    [InlineData(@"C:\Clips\Clip_x_cut.mp4", @"C:\Clips\Clip_x_cut.mp4")]
    [InlineData(@"C:\Clips\Clip_x_cut (3).mp4", @"C:\Clips\Clip_x_cut.mp4")]
    [InlineData(@"C:\Clips\Arma Reforger\Clip_1.mp4", @"C:\Clips\Arma Reforger\Clip_1_cut.mp4")]
    [InlineData(@"C:\Clips\Größe Mühe.mp4", @"C:\Clips\Größe Mühe_cut.mp4")]
    [InlineData(@"C:\Clips\a_cutlass.mp4", @"C:\Clips\a_cutlass_cut.mp4")]
    public void The_result_sits_next_to_the_source_as_stem_cut_mp4(string source, string expected)
        => Assert.Equal(expected, ClipCutRules.OutputPath(source));

    // ---- 2: the note ----

    [Fact]
    public void A_note_gets_the_range_appended()
        => Assert.Equal($"Beweis (Ausschnitt 03:12{Dash}05:40)", ClipCutRules.NoteForCut("Beweis", 192.4, 339.2));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n ")]
    public void A_clip_without_a_note_gets_only_the_marker(string? note)
        => Assert.Equal($"Ausschnitt 03:12{Dash}05:40", ClipCutRules.NoteForCut(note, 192.4, 339.2));

    [Fact]
    public void The_start_is_rounded_down_and_the_end_up_so_the_range_surely_contains_what_it_names()
    {
        Assert.Equal($"Ausschnitt 00:20{Dash}00:57", ClipCutRules.NoteForCut(null, 20.027, 56.1));
        Assert.Equal($"Ausschnitt 00:03{Dash}00:09", ClipCutRules.NoteForCut(null, 3.0, 251 / 30.0));   // 8.37 s → 9
        Assert.Equal($"Ausschnitt 00:00{Dash}00:10", ClipCutRules.NoteForCut(null, 0, 10.0));          // exactly 10 stays 10
    }

    [Fact]
    public void Past_one_hour_the_hours_are_shown()
        => Assert.Equal($"x (Ausschnitt 1:02:05{Dash}1:30:00)", ClipCutRules.NoteForCut("x", 3725.5, 5400));

    [Fact]
    public void A_long_note_gives_way_never_the_marker_and_never_splits_a_surrogate_pair()
    {
        var marker = $" (Ausschnitt 00:03{Dash}00:09)";
        int room = GalleryMetaService.MaxNoteLength - marker.Length;
        // the character that would end the kept text is the first half of an emoji
        var note = new string('a', room - 1) + "\U0001F600" + new string('b', 100);

        var result = ClipCutRules.NoteForCut(note, 3.0, 8.4);

        Assert.EndsWith(marker, result);
        Assert.True(result.Length <= GalleryMetaService.MaxNoteLength);
        Assert.Equal(new string('a', room - 1) + marker, result);
        Assert.DoesNotContain(result, c => char.IsSurrogate(c));
    }

    [Fact]
    public void A_note_exactly_at_the_limit_is_kept()
    {
        var marker = $" (Ausschnitt 00:03{Dash}00:09)";
        var note = new string('a', GalleryMetaService.MaxNoteLength - marker.Length);

        Assert.Equal(note + marker, ClipCutRules.NoteForCut(note, 3.0, 8.4));
    }

    // ---- 3: clock and position ----

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(59.9995, "00:59")]
    [InlineData(3599.999, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(5025.5, "1:23:45")]
    [InlineData(-3, "00:00")]
    public void The_clock_counts_whole_seconds(double seconds, string expected)
        => Assert.Equal(expected, ClipCutRules.FormatClock(seconds));

    [Theory]
    [InlineData(0, "00:00.000")]
    [InlineData(59.9995, "01:00.000")]
    [InlineData(3599.999, "59:59.999")]
    [InlineData(3600, "1:00:00.000")]
    [InlineData(5025.5, "1:23:45.500")]
    [InlineData(195.233, "03:15.233")]
    [InlineData(-1, "00:00.000")]
    public void The_position_shows_milliseconds(double seconds, string expected)
        => Assert.Equal(expected, ClipCutRules.FormatPosition(seconds));

    [Fact]
    public void The_position_uses_a_dot_whatever_the_system_culture_is()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("00:01.500", ClipCutRules.FormatPosition(1.5));
            Assert.Equal("00:02", ClipCutRules.FormatClock(2.5));
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void Sizes_are_shown_in_binary_units_with_a_german_decimal_comma()
    {
        Assert.Equal("980 MB", ClipCutRules.FormatBytes(980L * 1024 * 1024));
        Assert.Equal("1,0 GB", ClipCutRules.FormatBytes(1L << 30));
        Assert.Equal("6,3 GB", ClipCutRules.FormatBytes((long)(6.3 * (1L << 30))));
        Assert.Equal("512 KB", ClipCutRules.FormatBytes(512 * 1024));
    }

    // ---- 4: estimates ----

    [Fact]
    public void A_fast_cut_is_estimated_by_the_share_of_the_file()
    {
        long estimate = ClipCutRules.EstimateFastBytes(1_000_000_000, 60, 600);

        Assert.Equal(102_000_000L + (1L << 20), estimate);
        Assert.Equal(1_000_000_000L * 102 / 100 + (1L << 20), ClipCutRules.EstimateFastBytes(1_000_000_000, 600, 600));
        Assert.Equal(1_000_000_000L * 102 / 100 + (1L << 20), ClipCutRules.EstimateFastBytes(1_000_000_000, 900, 600));   // never more than the file
        Assert.Equal(1_000_000_000L * 102 / 100 + (1L << 20), ClipCutRules.EstimateFastBytes(1_000_000_000, 5, 0));        // unknown total
    }

    [Theory]
    [InlineData("avc1", 20_000_000L)]
    [InlineData("avc3", 20_000_000L)]
    [InlineData("hvc1", 28_000_000L)]
    [InlineData("hev1", 28_000_000L)]
    [InlineData("av01", 32_000_000L)]
    [InlineData("vp09", 24_000_000L)]
    public void The_exact_bitrate_follows_the_source_with_a_codec_factor(string codec, long expected)
        => Assert.Equal(expected, ClipCutRules.ExactVideoBitrate(25_000_000, 10, codec));

    [Fact]
    public void The_exact_bitrate_is_clamped_and_rounded_to_1000()
    {
        Assert.Equal(2_000_000, ClipCutRules.ExactVideoBitrate(100_000, 10, "avc1"));               // 80 kbit/s → floor of 2 Mbit/s
        Assert.Equal(80_000_000, ClipCutRules.ExactVideoBitrate(5_000_000_000, 10, "avc1"));        // 4 Gbit/s → ceiling of 80
        Assert.Equal(2_667_000, ClipCutRules.ExactVideoBitrate(3_333_333, 10, "avc1"));             // 2 666 666.4 → 2 667 000
        Assert.Equal(12_000_000, ClipCutRules.ExactVideoBitrate(0, 10, "avc1"));                    // unknown: a sane default
    }

    [Fact]
    public void An_exact_cut_estimate_counts_video_audio_and_cbr_overshoot()
        => Assert.Equal(175_812_000L, ClipCutRules.EstimateExactBytes(20_000_000, 2, 60));

    [Fact]
    public void The_exact_duration_is_4_5x_real_time_at_1080p60_and_slower_for_bigger_pictures()
    {
        Assert.Equal(60 / 4.5, ClipCutRules.EstimateExactSeconds(60, 1920, 1080, 60), 6);
        double ultrawide = ClipCutRules.EstimateExactSeconds(60, 3440, 1440, 60);
        Assert.Equal(60 / (4.5 * 1920 * 1080 / (3440.0 * 1440)), ultrawide, 6);
        Assert.True(ultrawide > 60 / 4.5);
        Assert.Equal(60 / 0.5, ClipCutRules.EstimateExactSeconds(60, 7680, 4320, 240), 6);          // never slower than 0.5x
        Assert.Equal(60 / 20.0, ClipCutRules.EstimateExactSeconds(60, 320, 180, 30), 6);            // never faster than 20x
        Assert.Equal(60 / 4.5, ClipCutRules.EstimateExactSeconds(60, 0, 0, 0), 6);                  // unknown picture
    }

    // ---- 5: fast arguments ----

    private const string Src = @"C:\Clips\a b.mp4";
    private const string Part = @"C:\Clips\a b_cut.mp4.part";

    [Fact]
    public void Fast_arguments_are_exactly_the_measured_ones()
    {
        var args = ClipCutRules.BuildFastArgs(Src, Part, 10.021029, 31.5, faststart: true);

        Assert.Equal("-hide_banner -loglevel warning -ss 10.022029 -i \"C:\\Clips\\a b.mp4\" -t 21.476971 " +
                     "-map 0:v:0 -map 0:a? -c copy -avoid_negative_ts make_zero -movflags +faststart -f mp4 \"C:\\Clips\\a b_cut.mp4.part\"", args);
    }

    [Fact]
    public void A_fast_cut_to_the_end_has_no_duration_and_faststart_is_optional()
    {
        var args = ClipCutRules.BuildFastArgs(Src, Part, 10.021029, null, faststart: false);

        Assert.DoesNotContain(" -t ", args);
        Assert.DoesNotContain("faststart", args);
        Assert.Contains("-ss 10.022029", args);   // -ss is always given
        Assert.EndsWith("-avoid_negative_ts make_zero -f mp4 \"C:\\Clips\\a b_cut.mp4.part\"", args);
    }

    [Fact]
    public void Fast_arguments_use_dots_under_a_german_culture_and_never_map_everything_or_overwrite()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var args = ClipCutRules.BuildFastArgs(Src, Part, 10.5, 20.25, faststart: false);

            Assert.Contains("-ss 10.501000", args);
            Assert.Contains("-t 9.748000", args);
            Assert.DoesNotContain(",", args.Replace("a b", ""));
            Assert.DoesNotContain("-map 0 ", args);
            Assert.DoesNotContain(" -y", args);
            Assert.DoesNotContain(" -n ", args);
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    // ---- 6: exact arguments ----

    [Fact]
    public void Exact_arguments_with_the_amd_encoder_are_exactly_the_measured_ones()
    {
        var args = ClipCutRules.BuildExactArgs(Src, Part, 3.166667, 8.366667, CompressEncoder.Amf, 28_000_000, 2, faststart: true);

        Assert.Equal("-hide_banner -loglevel warning -ss 3.165667 -i \"C:\\Clips\\a b.mp4\" -t 5.199000 " +
                     "-map 0:v:0 -map 0:a? -fps_mode passthrough " +
                     "-c:v h264_amf -usage transcoding -quality quality -profile:v high -rc cbr -b:v 28000000 " +
                     "-c:a aac -b:a 192k -movflags +faststart -f mp4 \"C:\\Clips\\a b_cut.mp4.part\"", args);
    }

    [Fact]
    public void An_exact_cut_from_the_very_start_has_no_seek()
    {
        var args = ClipCutRules.BuildExactArgs(Src, Part, 0, 5.0, CompressEncoder.Amf, 10_000_000, 1, faststart: false);

        Assert.DoesNotContain("-ss", args);
        Assert.Contains("-t 4.999000", args);
    }

    [Fact]
    public void An_exact_cut_to_the_end_has_no_duration()
    {
        var args = ClipCutRules.BuildExactArgs(Src, Part, 3.0, null, CompressEncoder.Amf, 10_000_000, 1, faststart: false);

        Assert.DoesNotContain(" -t ", args);
        Assert.Contains("-ss 2.999000", args);
    }

    [Fact]
    public void Every_encoder_has_its_own_video_part_and_the_rest_is_identical()
    {
        const long bps = 20_000_000;
        string Video(CompressEncoder e)
        {
            var args = ClipCutRules.BuildExactArgs(Src, Part, 1, 5, e, bps, 1, false);
            Assert.Contains("-fps_mode passthrough", args);
            Assert.Contains("-c:a aac -b:a 192k", args);
            return args;
        }

        Assert.Contains("-c:v h264_nvenc -preset p5 -tune hq -rc cbr -b:v 20000000 -maxrate 20000000 -bufsize 40000000", Video(CompressEncoder.Nvenc));
        Assert.Contains("-c:v h264_qsv -preset medium -b:v 20000000 -maxrate 20000000 -bufsize 40000000", Video(CompressEncoder.Qsv));
        Assert.Contains("-c:v libx264 -preset veryfast -crf 18 -maxrate 20000000 -bufsize 40000000", Video(CompressEncoder.X264));
        Assert.Contains("-c:v h264_amf -usage transcoding -quality quality -profile:v high -rc cbr -b:v 20000000", Video(CompressEncoder.Amf));
    }

    [Fact]
    public void A_clip_without_audio_drops_audio_instead_of_encoding_nothing()
    {
        var args = ClipCutRules.BuildExactArgs(Src, Part, 1, 5, CompressEncoder.Amf, 10_000_000, 0, false);

        Assert.Contains(" -an ", args + " ");
        Assert.DoesNotContain("-c:a", args);
    }

    [Fact]
    public void Exact_arguments_never_use_hardware_decoding_colour_flags_or_map_everything()
    {
        var args = ClipCutRules.BuildExactArgs(Src, Part, 1, 5, CompressEncoder.Amf, 10_000_000, 2, false);

        Assert.DoesNotContain("-hwaccel", args);
        Assert.DoesNotContain("-color", args);
        Assert.DoesNotContain("-pix_fmt", args);
        Assert.DoesNotContain("-map 0 ", args);
    }

    // ---- 7: still frame ----

    [Fact]
    public void The_frame_grab_asks_for_one_scaled_bmp_on_stdout()
    {
        Assert.Equal("-hide_banner -loglevel error -nostdin -ss 95.999000 -i \"C:\\Clips\\a b.mp4\" " +
                     "-frames:v 1 -an -sn -dn -vf \"scale='min(960,iw)':-2\" -f image2pipe -c:v bmp -",
            ClipCutRules.BuildFrameGrabArgs(Src, 96.0, 960));
        Assert.DoesNotContain("-ss", ClipCutRules.BuildFrameGrabArgs(Src, 0, 960));
    }

    // ---- 8: the runner must not rewrite them ----

    [Fact]
    public void The_job_runner_leaves_these_arguments_alone()
    {
        var fast = ClipCutRules.BuildFastArgs(Src, Part, 10, 20, true);
        var exact = ClipCutRules.BuildExactArgs(Src, Part, 10, 20, CompressEncoder.X264, 8_000_000, 2, true);

        Assert.Equal(fast, FFmpegJobRules.StripOverwriteFlags(fast));
        Assert.Equal(exact, FFmpegJobRules.StripOverwriteFlags(exact));
    }
}

/// <summary>The texts that follow the app language.</summary>
[Collection("Language")]
public sealed class ClipCutRulesEnglishTests
{
    private const string Dash = "\u2013";

    [Fact]
    public void The_english_note_says_excerpt()
    {
        using var _ = TestLanguage.English();

        Assert.Equal($"Proof (excerpt 03:12{Dash}05:40)", ClipCutRules.NoteForCut("Proof", 192.4, 339.2));
        Assert.Equal($"Excerpt 03:12{Dash}05:40", ClipCutRules.NoteForCut(null, 192.4, 339.2));
    }

    [Fact]
    public void English_sizes_use_a_decimal_point()
    {
        using var _ = TestLanguage.English();

        Assert.Equal("1.0 GB", ClipCutRules.FormatBytes(1L << 30));
        Assert.Equal("980 MB", ClipCutRules.FormatBytes(980L * 1024 * 1024));
    }

    [Fact]
    public void The_language_is_german_again_afterwards()
    {
        using (TestLanguage.English()) Assert.True(L.English);
        Assert.False(L.English);
        Assert.Equal("Ausschnitt 00:00" + Dash + "00:01", ClipCutRules.NoteForCut(null, 0, 1));
    }
}
