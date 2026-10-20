using System;
using System.IO;
using System.Text.Json;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Names, labels, settings and encoder choice of the Discord compress - everything that needs no ffmpeg.</summary>
public sealed class DiscordCompressRulesTests
{
    // ---- target labels ----

    [Theory]
    [InlineData(20_000_000L, "20 MB", "20MB")]
    [InlineData(500_000_000L, "500 MB", "500MB")]
    [InlineData(10_000_000L, "10 MB", "10MB")]
    [InlineData(50_000_000L, "50 MB", "50MB")]
    [InlineData(100_000_000L, "100 MB", "100MB")]
    [InlineData(1_000_000_000L, "1 GB", "1GB")]
    [InlineData(8_500_000L, "8.5 MB", "8.5MB")]
    [InlineData(1_500_000_000L, "1.5 GB", "1.5GB")]
    [InlineData(25_000_000L, "25 MB", "25MB")]
    public void A_target_is_labelled_in_decimal_units(long bytes, string label, string compact)
    {
        Assert.Equal(label, DiscordCompressRules.TargetLabel(bytes));
        Assert.Equal(compact, DiscordCompressRules.CompactLabel(bytes));
    }

    [Fact]
    public void The_label_does_not_depend_on_the_system_culture()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("8.5 MB", DiscordCompressRules.TargetLabel(8_500_000));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
    }

    // ---- where the result goes ----

    [Fact]
    public void The_result_goes_into_a_komprimiert_folder_directly_under_the_clips_base()
    {
        var path = DiscordCompressRules.OutputPath(@"C:\Clips", @"C:\Clips\Arma Reforger\Clip_1.mp4", 20_000_000);
        Assert.Equal(@"C:\Clips\Komprimiert 20MB\Clip_1_20MB.mp4", path);
    }

    [Fact]
    public void Folder_and_suffix_follow_the_target()
    {
        Assert.Equal(@"C:\Clips\Komprimiert 500MB\Rec_x_500MB.mp4",
            DiscordCompressRules.OutputPath(@"C:\Clips", @"C:\Clips\Rec_x.mkv", 500_000_000));
        Assert.Equal(@"C:\Clips\Komprimiert 1GB\a_1GB.mp4",
            DiscordCompressRules.OutputPath(@"C:\Clips", @"C:\Clips\a.mov", 1_000_000_000));
    }

    [Fact]
    public void A_base_with_a_trailing_separator_gives_the_same_path()
        => Assert.Equal(@"C:\Clips\Komprimiert 20MB\a_20MB.mp4",
            DiscordCompressRules.OutputPath(@"C:\Clips\", @"C:\Clips\a.mp4", 20_000_000));

    [Theory]
    [InlineData("Komprimiert 20MB", true)]
    [InlineData("Komprimiert 500MB", true)]
    [InlineData("Komprimiert 1GB", true)]
    [InlineData("Komprimiert 8.5MB", true)]
    [InlineData("komprimiert 20mb", true)]
    [InlineData("Komprimiert", false)]
    [InlineData("Komprimiert Spiele", false)]
    [InlineData("Komprimiert 20", false)]
    [InlineData("Komprimiert 20MB extra", false)]
    [InlineData("Arma Reforger", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_folders_this_feature_creates_are_not_games(string? folder, bool expected)
        => Assert.Equal(expected, GameFolderNaming.IsCompressedFolder(folder));

    [Fact]
    public void Every_folder_the_feature_creates_is_recognised_as_such()
    {
        foreach (var bytes in new long[] { 10_000_000, 20_000_000, 50_000_000, 100_000_000, 500_000_000, 1_000_000_000, 8_500_000 })
            Assert.True(GameFolderNaming.IsCompressedFolder(DiscordCompressRules.FolderName(bytes)), bytes.ToString());
    }

    [Fact]
    public void The_gallery_scan_still_lists_files_in_the_komprimiert_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "wki_komp_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Komprimiert 20MB"));
            File.WriteAllText(Path.Combine(root, "Komprimiert 20MB", "a_20MB.mp4"), "");
            var media = GameFolderNaming.EnumerateMedia(root, f => f.Extension == ".mp4");
            var hit = Assert.Single(media);
            Assert.True(GameFolderNaming.IsCompressedFolder(hit.Game));   // the gallery turns this into the "Komprimiert" kind
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // ---- the note of the compressed copy ----

    [Fact]
    public void The_note_of_the_copy_says_where_it_came_from()
        => Assert.Equal("Kill auf Pavlov (Discord 20 MB)", DiscordCompressRules.NoteForCopy("Kill auf Pavlov", 20_000_000));

    [Fact]
    public void A_clip_without_a_note_gets_none()
    {
        Assert.Equal("", DiscordCompressRules.NoteForCopy("", 20_000_000));
        Assert.Equal("", DiscordCompressRules.NoteForCopy("   ", 20_000_000));
        Assert.Equal("", DiscordCompressRules.NoteForCopy(null, 20_000_000));
    }

    [Fact]
    public void A_note_at_the_length_limit_loses_its_end_not_the_marker()
    {
        var longNote = new string('x', GalleryMetaService.MaxNoteLength);
        var result = DiscordCompressRules.NoteForCopy(longNote, 500_000_000);

        Assert.True(result.Length <= GalleryMetaService.MaxNoteLength);
        Assert.EndsWith(" (Discord 500 MB)", result);
        Assert.Equal(result, GalleryMetaService.NormalizeNote(result));   // survives the store's own cut unchanged
    }

    // ---- messages ----

    [Fact]
    public void The_too_long_message_names_the_target()
    {
        Assert.Contains("20 MB", DiscordCompressRules.TooLongMessage(20_000_000));
        Assert.Contains("erst schneiden", DiscordCompressRules.TooLongMessage(20_000_000));
    }

    // ---- encoder choice ----

    [Fact]
    public void The_best_detected_h264_encoder_is_taken_amf_then_nvenc_then_qsv_then_software()
    {
        Assert.Equal(CompressEncoder.Amf, DiscordCompressRules.PickEncoder(new[] { "libx264", "h264_nvenc", "h264_amf", "h264_qsv" }));
        Assert.Equal(CompressEncoder.Nvenc, DiscordCompressRules.PickEncoder(new[] { "h264_qsv", "h264_nvenc", "libx264" }));
        Assert.Equal(CompressEncoder.Qsv, DiscordCompressRules.PickEncoder(new[] { "h264_qsv", "libx264" }));
        Assert.Equal(CompressEncoder.X264, DiscordCompressRules.PickEncoder(new[] { "libx264", "libx265" }));
    }

    [Fact]
    public void Hevc_and_av1_encoders_are_never_a_compress_encoder()
        => Assert.Equal(CompressEncoder.X264, DiscordCompressRules.PickEncoder(new[] { "hevc_amf", "av1_nvenc", "hevc_qsv" }));

    [Fact]
    public void Nothing_detected_yet_means_software()
    {
        Assert.Equal(CompressEncoder.X264, DiscordCompressRules.PickEncoder(Array.Empty<string>()));
        Assert.Equal(CompressEncoder.Amf, DiscordCompressRules.PickEncoder(new[] { "H264_AMF" }));   // case-insensitive
    }

    // ---- disk space the result needs (the runner asks for this much free space, plus its reserve) ----

    [Fact]
    public void A_short_clip_needs_room_for_its_real_size_not_for_the_whole_target()
    {
        // 30 s at 10.7 Mbit/s video + 128 kbit/s audio: about 41 MB, however big the 500 MB target is
        long bytes = DiscordCompressRules.EstimateOutputBytes(10_700_000, 30, audioBytes: 490_000, muxBytes: 70_000, targetBytes: 500_000_000);

        Assert.InRange(bytes, 40_000_000, 60_000_000);
    }

    [Fact]
    public void The_estimate_never_asks_for_more_than_the_target_and_has_room_for_an_overshoot()
    {
        // a clip that really fills the target: the target is the bound
        Assert.Equal(20_000_000, DiscordCompressRules.EstimateOutputBytes(2_700_000, 60, 100_000, 10_000, 20_000_000));
        // video 1 000 000 bit/s for 8 s = 1 MB; a hardware encoder may overshoot by a quarter
        Assert.Equal(1_250_000L + 50_000 + 5_000, DiscordCompressRules.EstimateOutputBytes(1_000_000, 8, 50_000, 5_000, 500_000_000));
    }

    [Fact]
    public void The_estimate_is_never_negative_or_zero_for_nonsense_input()
    {
        Assert.True(DiscordCompressRules.EstimateOutputBytes(0, 0, 0, 0, 20_000_000) >= 0);
        Assert.True(DiscordCompressRules.EstimateOutputBytes(-5, -5, 0, 0, 20_000_000) >= 0);
    }

    // ---- x264 pass-1 statistics (.log and .mbtree) ----

    [Fact]
    public void The_pass_one_statistics_of_a_ten_minute_1080p60_clip_are_about_600_MB()
    {
        // 8160 macroblocks x 2 bytes + a text line per frame, 36 000 frames
        long bytes = DiscordCompressRules.EstimatePassLogBytes(1920, 1080, 60, TimeSpan.FromMinutes(10));

        Assert.InRange(bytes, 550_000_000, 700_000_000);
    }

    [Fact]
    public void The_statistics_estimate_scales_with_picture_rate_and_length_and_survives_nonsense()
    {
        long a = DiscordCompressRules.EstimatePassLogBytes(854, 480, 30, TimeSpan.FromMinutes(10));
        long b = DiscordCompressRules.EstimatePassLogBytes(1920, 1080, 60, TimeSpan.FromMinutes(10));

        Assert.True(a < b / 5);
        Assert.Equal(0, DiscordCompressRules.EstimatePassLogBytes(1920, 1080, 60, TimeSpan.Zero));
        Assert.Equal(0, DiscordCompressRules.EstimatePassLogBytes(0, 0, 0, TimeSpan.FromMinutes(1)));
    }

    // ---- stale statistics files ----

    [Theory]
    [InlineData("wki_0123456789abcdef0123456789abcdef-0.log", true)]
    [InlineData("wki_0123456789abcdef0123456789abcdef-0.log.mbtree", true)]
    [InlineData("wki_0123456789abcdef0123456789abcdef-0.log.temp", true)]
    [InlineData("wki_0123456789abcdef0123456789abcdef-0.log.mbtree.temp", true)]
    [InlineData("WKI_0123456789ABCDEF0123456789ABCDEF-0.LOG", true)]
    [InlineData("wki_0123456789abcdef0123456789abcdef-1.log", true)]
    [InlineData("wki_test.log", false)]
    [InlineData("wki_0123456789abcdef-0.log", false)]
    [InlineData("0123456789abcdef0123456789abcdef-0.log", false)]
    [InlineData("wki_0123456789abcdef0123456789abcdef-0.log.bak", false)]
    [InlineData("wki_0123456789abcdef0123456789abcdef.mp4", false)]
    public void Only_the_pass_statistics_files_of_the_compress_are_recognised(string name, bool expected)
        => Assert.Equal(expected, DiscordCompressRules.IsPassStatsFile(name));

    // ---- which ffmpeg failure is the decoder's ----

    [Theory]
    [InlineData("[h264 @ 000001] Failed setup for format d3d11: hwaccel initialisation returned error.", true)]
    [InlineData("[AVHWDeviceContext @ 0000] Device creation failed: -12.", true)]
    [InlineData("Failed to create a D3D11VA device", true)]
    [InlineData("Error initializing the decoder: hwaccel", true)]
    [InlineData("[vost#0:0/h264_amf @ 0000] Error while opening encoder - maybe incorrect parameters such as bit_rate", false)]
    [InlineData("av_interleaved_write_frame(): No space left on device", false)]
    [InlineData("C:\\clips\\a.mp4: Invalid data found when processing input", false)]
    [InlineData("[AMF] Failed to create AMF context: AMF_DIRECTX_FAILED", false)]
    [InlineData("", false)]
    public void Only_a_decoder_or_hwaccel_message_makes_the_hardware_decoding_the_suspect(string stderr, bool expected)
        => Assert.Equal(expected, DiscordCompressRules.LooksLikeHwDecodeFailure(stderr));

    [Fact]
    public void A_missing_stderr_is_no_decoder_failure()
        => Assert.False(DiscordCompressRules.LooksLikeHwDecodeFailure(null));

    // ---- settings ----

    [Fact]
    public void Defaults_are_20_and_500_MB_and_a_game_does_not_hold_the_job_back()
    {
        var s = new DiscordCompressSettings();
        Assert.Equal(20_000_000, s.SmallTargetBytes);
        Assert.Equal(500_000_000, s.LargeTargetBytes);
        Assert.False(s.OnlyWhenNoGame);
        Assert.Equal(20_000_000, new AppSettings().Discord.SmallTargetBytes);
    }

    [Fact]
    public void A_settings_file_from_before_the_section_existed_loads_with_the_defaults()
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>("{ \"SchemaVersion\": 9 }")!;
        SettingsService.EnsureSections(loaded);
        Assert.Equal(20_000_000, loaded.Discord.SmallTargetBytes);
        Assert.Equal(500_000_000, loaded.Discord.LargeTargetBytes);
    }

    [Fact]
    public void An_explicit_null_section_is_replaced_by_the_defaults()
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>("{ \"Discord\": null }")!;
        SettingsService.EnsureSections(loaded);
        Assert.NotNull(loaded.Discord);
        Assert.Equal(500_000_000, loaded.Discord.LargeTargetBytes);
    }

    [Fact]
    public void The_section_survives_a_round_trip()
    {
        var s = new AppSettings();
        s.Discord.SmallTargetBytes = 10_000_000;
        s.Discord.LargeTargetBytes = 1_000_000_000;
        s.Discord.OnlyWhenNoGame = true;

        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s))!;

        Assert.Equal(10_000_000, back.Discord.SmallTargetBytes);
        Assert.Equal(1_000_000_000, back.Discord.LargeTargetBytes);
        Assert.True(back.Discord.OnlyWhenNoGame);
    }

    [Theory]
    [InlineData(0L, 20_000_000L)]
    [InlineData(-5L, 20_000_000L)]
    [InlineData(500L, 1_000_000L)]
    [InlineData(20_000_000L, 20_000_000L)]
    public void A_nonsense_target_in_a_hand_edited_file_is_clamped(long stored, long effective)
        => Assert.Equal(effective, DiscordCompressRules.EffectiveTarget(stored, 20_000_000));

    [Fact]
    public void The_menu_presets_are_10_20_50_100_500_and_1000_MB()
        => Assert.Equal(new long[] { 10_000_000, 20_000_000, 50_000_000, 100_000_000, 500_000_000, 1_000_000_000 },
                        DiscordCompressRules.TargetPresets);

    // ---- where a foreground window counts as a game ----

    // The compress wait no longer has its own pixel rule: it asks the shared GameDetector (see
    // GameForegroundDetectorTests, which also covers the borderless/minimized cases through that one rule).
    [Theory]
    [InlineData(0, 0, 3440, 1440, true)]     // borderless fullscreen
    [InlineData(-1, -1, 3441, 1441, true)]   // some drivers overshoot by a pixel
    [InlineData(0, 0, 3440, 1400, false)]    // maximized window with a taskbar
    [InlineData(100, 100, 1500, 900, false)] // ordinary window
    [InlineData(-32000, -32000, -31840, -31972, false)] // minimized
    public void A_window_covering_its_whole_monitor_is_taken_for_a_game(int l, int t, int r, int b, bool expected)
        => Assert.Equal(expected, GameDetector.CoversMonitor(new LayoutRect(l, t, r - l, b - t), new LayoutRect(0, 0, 3440, 1440)));
}
