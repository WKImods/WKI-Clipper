using System;
using System.IO;
using System.Linq;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>The MP4 index parser, driven with hand-built boxes (no ffmpeg).</summary>
public sealed class Mp4IndexTests
{
    private const double Ts = 15360.0;

    private static Mp4VideoIndex? Read(Mp4Builder b) => Mp4Index.TryRead(new MemoryStream(b.Build()));

    private static Mp4VideoIndex Must(Mp4Builder b)
    {
        var index = Read(b);
        Assert.NotNull(index);
        return index!;
    }

    private static Mp4Builder SimpleVideo(int frames = 10, int[]? stss = null)
    {
        var b = new Mp4Builder();
        var v = Mp4Builder.Video(frames);
        v.Stss = stss;
        b.Tracks.Add(v);
        return b;
    }

    // ---- 1 / 2: the basic layout, moov at the end and in front ----

    [Fact]
    public void A_minimal_mp4_gives_the_frame_times_and_keyframes()
    {
        var b = SimpleVideo(10, new[] { 1, 6 });
        var index = Must(b);

        Assert.Equal(10, index.FrameCount);
        Assert.Equal("avc1", index.Codec);
        Assert.Equal(1920, index.Width);
        Assert.Equal(1080, index.Height);
        Assert.Equal(0, index.AudioTrackCount);
        Assert.False(index.HasBFrames);
        Assert.Equal(10_000, index.VideoBytes);
        Assert.Equal(10.0, index.MovieDurationSeconds, 6);
        Assert.Equal(new[] { 0, 5 }, index.Keyframes);
        for (int k = 0; k < 10; k++)
        {
            Assert.Equal(k / 30.0, index.PreviewSeconds[k], 9);
            Assert.Equal(k / 30.0, index.FileSeconds[k], 9);
        }
    }

    [Fact]
    public void The_codec_and_size_come_from_the_sample_entry()
    {
        var b = SimpleVideo();
        b.Tracks[0].Codec = "hvc1";
        b.Tracks[0].Width = 3440;
        b.Tracks[0].Height = 1440;

        var index = Must(b);

        Assert.Equal("hvc1", index.Codec);
        Assert.Equal(3440, index.Width);
        Assert.Equal(1440, index.Height);
    }

    [Fact]
    public void A_faststart_file_gives_the_same_index()
    {
        var tail = Must(SimpleVideo(10, new[] { 1, 6 }));
        var b = SimpleVideo(10, new[] { 1, 6 });
        b.MoovFirst = true;
        var head = Must(b);

        Assert.Equal(tail.PreviewSeconds, head.PreviewSeconds);
        Assert.Equal(tail.FileSeconds, head.FileSeconds);
        Assert.Equal(tail.Keyframes, head.Keyframes);
    }

    // ---- 3: empty edit shifts the file time axis, not the preview axis ----

    [Fact]
    public void An_empty_edit_on_the_video_moves_the_file_time_but_not_the_preview_time()
    {
        var b = SimpleVideo();
        b.Tracks[0].Elst = new[] { (21L, -1L, (short)1) };      // 21 ms of the movie timescale (1000)
        b.Tracks.Add(Mp4Builder.Audio());                          // the audio starts at 0 → the file starts at 0
        var index = Must(b);

        Assert.Equal(0.0, index.PreviewSeconds[0], 9);
        Assert.Equal(323 / Ts, index.FileSeconds[0], 9);          // round(21 * 15360 / 1000) = 323 ticks, like ffmpeg
        Assert.Equal(323 / Ts + 1 / 30.0, index.FileSeconds[1], 9);
        Assert.Equal(1, index.AudioTrackCount);
    }

    [Fact]
    public void A_half_second_empty_edit_is_half_a_second_on_the_file_axis()
    {
        var b = SimpleVideo();
        b.Tracks[0].Elst = new[] { (500L, -1L, (short)1) };
        b.Tracks.Add(Mp4Builder.Audio());
        var index = Must(b);

        Assert.Equal(0.0, index.PreviewSeconds[0], 9);
        Assert.Equal(0.5, index.FileSeconds[0], 6);
    }

    // ---- 4 / 5: the other tracks only matter for where the file starts ----

    [Fact]
    public void An_audio_encoder_delay_edit_does_not_move_the_file_start()
    {
        var b = SimpleVideo();
        var audio = Mp4Builder.Audio();
        audio.Elst = new[] { (10_000L, 1024L, (short)1) };       // media_time 1024 = priming samples, not a start offset
        b.Tracks.Add(audio);

        var index = Must(b);

        Assert.Equal(0.0, index.FileSeconds[0], 9);
    }

    [Fact]
    public void An_audio_empty_edit_with_a_video_that_starts_at_zero_leaves_the_frames_alone()
    {
        var b = SimpleVideo();
        var audio = Mp4Builder.Audio();
        audio.Elst = new[] { (15L, -1L, (short)1) };
        b.Tracks.Add(audio);

        var index = Must(b);

        Assert.Equal(0.0, index.FileSeconds[0], 9);
        Assert.Equal(1 / 30.0, index.FileSeconds[1], 9);
    }

    [Fact]
    public void A_video_that_starts_later_than_the_audio_is_offset_by_the_difference()
    {
        var b = SimpleVideo();
        b.Tracks[0].Elst = new[] { (500L, -1L, (short)1) };
        var audio = Mp4Builder.Audio();
        audio.Elst = new[] { (200L, -1L, (short)1) };             // the audio starts at 0.2 s, the video at 0.5 s
        b.Tracks.Add(audio);

        var index = Must(b);

        Assert.Equal(0.3, index.FileSeconds[0], 6);               // the file starts at the earliest stream (0.2)
    }

    // ---- 6: B-frames ----

    private static Mp4Builder BFrames(int[]? stss = null)
    {
        // decode order I0 P3 B1 B2 P6 B4 B5, display delay of two frames (ctts offsets in 512-tick frames)
        var b = new Mp4Builder();
        var v = Mp4Builder.Video(7);
        v.Ctts = new[] { (1u, 1024), (1u, 2048), (2u, 512), (1u, 2048), (2u, 512) };
        v.Elst = new[] { (10_000L, 1024L, (short)1) };            // media_time = the two frames of delay
        v.Stss = stss ?? new[] { 1, 5 };
        b.Tracks.Add(v);
        return b;
    }

    [Fact]
    public void B_frames_are_sorted_into_display_order_with_raw_preview_times_and_shifted_file_times()
    {
        var index = Must(BFrames());

        Assert.True(index.HasBFrames);
        Assert.Equal(7, index.FrameCount);
        for (int k = 0; k < 7; k++)
        {
            Assert.Equal((k + 2) * 512 / Ts, index.PreviewSeconds[k], 9);   // no edit list: starts at 2 frames
            Assert.Equal(k / 30.0, index.FileSeconds[k], 9);                // media_time 1024 removed → starts at 0
        }
        Assert.Equal(new[] { 0, 6 }, index.Keyframes);                      // sample 5 (decode order) is display frame 6
    }

    [Fact]
    public void Streams_without_reordering_have_no_B_frames_even_with_a_ctts_box()
    {
        var b = SimpleVideo();
        b.Tracks[0].Ctts = new[] { (10u, 1024) };

        Assert.False(Must(b).HasBFrames);
    }

    // ---- 7: ctts version 1 ----

    [Fact]
    public void Negative_composition_offsets_of_ctts_version_1_are_read_signed()
    {
        var b = SimpleVideo(5);
        b.Tracks[0].CttsVersion = 1;
        b.Tracks[0].Ctts = new[] { (1u, -1024), (4u, 0) };

        var index = Must(b);

        Assert.Equal(-1024 / Ts, index.PreviewSeconds[0], 9);
        Assert.Equal(512 / Ts, index.PreviewSeconds[1], 9);
        Assert.Equal(0.0, index.FileSeconds[0], 9);
        Assert.Equal((512 + 1024) / Ts, index.FileSeconds[1], 9);
    }

    // ---- 7b: the end of the last video frame (what the concat list pins per disk segment) ----

    [Fact]
    public void Video_end_is_the_last_frame_plus_its_own_duration()
    {
        var index = Must(SimpleVideo(10));

        Assert.Equal(10 / 30.0, index.VideoEndSeconds, 9);
    }

    [Fact]
    public void The_duration_of_the_last_frame_comes_from_its_own_stts_entry()
    {
        var b = SimpleVideo(10);
        b.Tracks[0].Stts = new[] { (9u, 512u), (1u, 256u) };     // the last frame is only half a frame long

        Assert.Equal((9 * 512 + 256) / Ts, Must(b).VideoEndSeconds, 9);
    }

    [Fact]
    public void A_segment_zero_like_file_ends_after_its_video_start_offset_not_at_the_audio_end()
    {
        // 480 frames at 60 fps, the video starts 21 ms after the audio (empty edit, rounded to 323 ticks like ffmpeg),
        // the movie header already says 8.0213 s because the audio runs one AAC frame longer than the video.
        var b = new Mp4Builder { MovieDuration = 8021 };
        var video = Mp4Builder.Video(480, delta: 256);
        video.Elst = new[] { (21L, -1L, (short)1), (8000L, 0L, (short)1) };
        b.Tracks.Add(video);
        b.Tracks.Add(Mp4Builder.Audio(samples: 376));

        var index = Must(b);

        Assert.Equal(323 / Ts, index.FileSeconds[0], 9);
        Assert.Equal((323 + 480 * 256) / Ts, index.VideoEndSeconds, 9);     // 8.0210286 s
    }

    [Fact]
    public void A_later_segment_ends_at_its_video_length_when_only_the_audio_starts_late()
    {
        var b = new Mp4Builder { MovieDuration = 4000 };
        b.Tracks.Add(Mp4Builder.Video(240, delta: 256));
        var audio = Mp4Builder.Audio(samples: 187);
        audio.Elst = new[] { (10L, -1L, (short)1) };              // the audio starts 10 ms late, the file at the video (0)
        b.Tracks.Add(audio);

        Assert.Equal(240 * 256 / Ts, Must(b).VideoEndSeconds, 9);   // exactly 4 s
    }

    [Fact]
    public void B_frames_use_the_duration_of_the_last_frame_in_display_order()
    {
        // BFrames(): decode order I0 P3 B1 B2 P6 B4 B5, so P6 (decode sample 4) is the last one on screen.
        // Its duration is the stts delta of sample 4 (256), not that of the last sample in decode order (512).
        var b = BFrames();
        b.Tracks[0].Stts = new[] { (4u, 512u), (1u, 256u), (2u, 512u) };
        b.Tracks[0].Ctts = new[] { (1u, 1024), (1u, 2048), (2u, 512), (1u, 2048), (2u, 768) };   // same display times as BFrames()

        var index = Must(b);

        Assert.Equal(6 / 30.0, index.FileSeconds[6], 9);
        Assert.Equal(6 / 30.0 + 256 / Ts, index.VideoEndSeconds, 9);
    }

    // ---- 8 / 9: keyframes and sizes ----

    [Fact]
    public void Without_a_sync_sample_box_every_frame_is_a_keyframe()
    {
        var index = Must(SimpleVideo(6));

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, index.Keyframes);
    }

    [Fact]
    public void Video_bytes_come_from_a_uniform_sample_size()
    {
        var b = SimpleVideo(8);
        b.Tracks[0].UniformSize = 2500;

        Assert.Equal(20_000, Must(b).VideoBytes);
    }

    [Fact]
    public void Video_bytes_come_from_the_size_table()
    {
        var b = SimpleVideo(4);
        b.Tracks[0].UniformSize = 0;
        b.Tracks[0].Sizes = new uint[] { 100, 200, 300, 400 };

        Assert.Equal(1000, Must(b).VideoBytes);
    }

    // ---- 10: track order and counting ----

    [Fact]
    public void The_video_track_does_not_have_to_come_first_and_every_audio_track_is_counted()
    {
        var b = new Mp4Builder();
        b.Tracks.Add(Mp4Builder.Audio());
        b.Tracks.Add(Mp4Builder.Video(12));
        b.Tracks.Add(Mp4Builder.Audio());

        var index = Must(b);

        Assert.Equal(12, index.FrameCount);
        Assert.Equal(2, index.AudioTrackCount);
    }

    [Fact]
    public void A_chapter_text_track_is_ignored()
    {
        var b = SimpleVideo();
        b.Tracks.Add(Mp4Builder.Audio());
        var text = Mp4Builder.Audio(3, 600, 1000);
        text.Handler = "text";
        text.Codec = "text";
        b.Tracks.Add(text);

        var index = Must(b);

        Assert.Equal(1, index.AudioTrackCount);
        Assert.Equal(10, index.FrameCount);
        Assert.Equal(0.0, index.FileSeconds[0], 9);
    }

    // ---- 11: 64-bit boxes ----

    [Fact]
    public void Version_1_mvhd_mdhd_and_elst_are_read()
    {
        var b = SimpleVideo();
        b.MvhdVersion = 1;
        b.Tracks[0].MdhdVersion = 1;
        b.Tracks[0].ElstVersion = 1;
        b.Tracks[0].Elst = new[] { (21L, -1L, (short)1) };
        b.Tracks.Add(Mp4Builder.Audio());
        b.Tracks[1].MdhdVersion = 1;

        var index = Must(b);

        Assert.Equal(10, index.FrameCount);
        Assert.Equal(323 / Ts, index.FileSeconds[0], 9);
        Assert.Equal(10.0, index.MovieDurationSeconds, 6);
    }

    // ---- 12: box sizes ----

    [Fact]
    public void A_largesize_box_before_the_moov_is_skipped()
    {
        var b = SimpleVideo();
        b.LargeSizeMdat = true;

        Assert.Equal(10, Must(b).FrameCount);
    }

    [Fact]
    public void A_moov_with_size_zero_reaches_to_the_end_of_the_file()
    {
        var b = SimpleVideo();
        b.LastBoxSizeZero = true;

        Assert.Equal(10, Must(b).FrameCount);
    }

    // ---- 13: broken input is null ----

    [Fact]
    public void A_file_without_a_moov_is_not_indexed()
    {
        var bytes = Mp4Builder.Concat(
            Mp4Builder.Box("ftyp", new byte[16]),
            Mp4Builder.Box("mdat", new byte[200]));

        Assert.Null(Mp4Index.TryRead(new MemoryStream(bytes)));
    }

    [Fact]
    public void A_truncated_moov_is_not_indexed()
    {
        var bytes = SimpleVideo().Build();
        var cut = bytes.Take(bytes.Length - 20).ToArray();

        Assert.Null(Mp4Index.TryRead(new MemoryStream(cut)));
    }

    [Fact]
    public void A_box_smaller_than_its_own_header_is_refused()
    {
        var bytes = Mp4Builder.Concat(
            Mp4Builder.Box("ftyp", new byte[16]),
            Mp4Builder.U32(4), System.Text.Encoding.ASCII.GetBytes("junk"), new byte[32]);

        Assert.Null(Mp4Index.TryRead(new MemoryStream(bytes)));
    }

    [Fact]
    public void A_box_reaching_past_the_end_of_the_file_is_refused()
    {
        var bytes = Mp4Builder.Concat(
            Mp4Builder.Box("ftyp", new byte[16]),
            Mp4Builder.U32(100_000), System.Text.Encoding.ASCII.GetBytes("mdat"), new byte[64]);

        Assert.Null(Mp4Index.TryRead(new MemoryStream(bytes)));
    }

    [Fact]
    public void More_than_64_top_level_boxes_are_refused()
    {
        var b = SimpleVideo();
        b.Mp4TopLevelFiller = 70;

        Assert.Null(Read(b));
    }

    [Fact]
    public void Tables_that_disagree_on_the_sample_count_are_refused()
    {
        var b = SimpleVideo(10);
        b.Tracks[0].SampleCountOverride = 9;

        Assert.Null(Read(b));
    }

    [Fact]
    public void Two_real_edits_are_not_supported()
    {
        var b = SimpleVideo();
        b.Tracks[0].Elst = new[] { (4000L, 0L, (short)1), (4000L, 5000L, (short)1) };

        Assert.Null(Read(b));
    }

    [Fact]
    public void A_playback_rate_other_than_one_is_not_supported()
    {
        var b = SimpleVideo();
        b.Tracks[0].Elst = new[] { (10_000L, 0L, (short)2) };

        Assert.Null(Read(b));
    }

    [Fact]
    public void A_file_without_a_video_track_is_not_indexed()
    {
        var b = new Mp4Builder();
        b.Tracks.Add(Mp4Builder.Audio());

        Assert.Null(Read(b));
    }

    [Fact]
    public void A_video_track_without_samples_is_not_indexed()
    {
        var b = new Mp4Builder();
        var v = Mp4Builder.Video(0);
        b.Tracks.Add(v);

        Assert.Null(Read(b));
    }

    [Fact]
    public void An_empty_stream_is_not_indexed()
    {
        Assert.Null(Mp4Index.TryRead(new MemoryStream()));
    }

    // ---- 14: never throws ----

    private sealed class ThrowingStream : MemoryStream
    {
        public ThrowingStream(byte[] bytes) : base(bytes) { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("disk gone");
        public override int Read(Span<byte> buffer) => throw new IOException("disk gone");
    }

    [Fact]
    public void A_stream_that_throws_gives_null_instead_of_an_exception()
    {
        Assert.Null(Mp4Index.TryRead(new ThrowingStream(SimpleVideo().Build())));
    }

    // ---- the path overload ----

    [Fact]
    public void A_file_on_disk_is_read_even_while_another_process_has_it_open()
    {
        var path = Path.Combine(Path.GetTempPath(), "wki_mp4index_" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            File.WriteAllBytes(path, SimpleVideo().Build());
            using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

            Assert.Equal(10, Mp4Index.TryRead(path)!.FrameCount);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void A_missing_file_gives_null()
    {
        Assert.Null(Mp4Index.TryRead(Path.Combine(Path.GetTempPath(), "wki_nope_" + Guid.NewGuid().ToString("N") + ".mp4")));
    }
}
