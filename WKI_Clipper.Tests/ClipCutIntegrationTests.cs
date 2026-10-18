using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>The stamped test footage, generated once per run with the real ffmpeg (nothing is made without one).</summary>
public sealed class StampSourcesFixture : IDisposable
{
    public string? Ffmpeg { get; } = FFmpegTestSupport.Find();
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "wki_stamp_" + Guid.NewGuid().ToString("N"));
    public string Plain => Path.Combine(Dir, "plain.mp4");
    public string BFrames => Path.Combine(Dir, "bf.mp4");
    public string VideoOffset => Path.Combine(Dir, "voff.mp4");
    public string Chapters => Path.Combine(Dir, "chapters.mp4");
    public string TwoAudio => Path.Combine(Dir, "audio2.mp4");

    public StampSourcesFixture()
    {
        Directory.CreateDirectory(Dir);
        if (Ffmpeg is null) return;
        StampFootage.Make(Ffmpeg, Plain);
        StampFootage.Make(Ffmpeg, BFrames, bframes: true);
        StampFootage.Make(Ffmpeg, TwoAudio, audioTracks: 2);
        StampFootage.MakeVideoOffset(Ffmpeg, Plain, VideoOffset);
        StampFootage.MakeChapters(Ffmpeg, Plain, Chapters, Path.Combine(Dir, "chapters.txt"));
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch { }
    }
}

/// <summary>
/// The clip editor's index reader and cut, end to end with the real ffmpeg on footage whose frames number themselves.
/// Every test returns early when no ffmpeg.exe can be found.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ClipCutIntegrationTests : ClipCutFixture, IClassFixture<StampSourcesFixture>
{
    private readonly StampSourcesFixture _fx;

    public ClipCutIntegrationTests(StampSourcesFixture fx) => _fx = fx;

    private string Ffmpeg => _fx.Ffmpeg!;

    private string Copy(string source, string name = "Clip_1.mp4")
    {
        var destination = Path.Combine(Clips, "Arma Reforger", name);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination);
        return destination;
    }

    private async Task<CutResult> Cut(string source, int from, int to, CutMode mode, string[]? encoders = null,
        GalleryMetaService? meta = null, IProgress<CutProgress>? progress = null, CancellationToken ct = default)
    {
        var index = Mp4Index.TryRead(source)!;
        var range = new ClipTimeline(index).Resolve(from, to, mode);
        using var runner = new FFmpegJobRunner(Ffmpeg, null, PartsFile);
        using var service = new ClipCutService(runner, () => Settings(toasts: false), meta ?? NewMeta())
        {
            AvailableEncoders = () => encoders ?? new[] { "libx264" }
        };
        return await service.CutAsync(new CutRequest(source, index, range, mode, false), progress, ct);
    }

    private static int Frames(string file) => Mp4Index.TryRead(file)!.FrameCount;

    // ---- 1-4: the index agrees with ffmpeg ----

    [Fact]
    public void The_index_of_the_stamped_source_matches_what_ffmpeg_reports()
    {
        if (_fx.Ffmpeg is null) return;
        var index = Mp4Index.TryRead(_fx.Plain)!;

        Assert.Equal(StampFootage.Frames, index.FrameCount);
        Assert.Equal(Enumerable.Range(0, 12).Select(k => k * 30), index.Keyframes);
        Assert.False(index.HasBFrames);
        Assert.Equal("avc1", index.Codec);
        Assert.Equal(320, index.Width);
        Assert.Equal(180, index.Height);
        Assert.Equal(1, index.AudioTrackCount);

        var (_, keys) = StampFootage.Framecrc(Ffmpeg, _fx.Plain, "0:v:0", "-discard nokey");
        Assert.Equal(12, keys.Count);
        for (int i = 0; i < keys.Count; i++)
            Assert.True(Math.Abs(keys[i].Pts - index.FileSeconds[index.Keyframes[i]]) < 0.0001,
                $"keyframe {i}: ffmpeg {keys[i].Pts:0.000000} s, index {index.FileSeconds[index.Keyframes[i]]:0.000000} s");

        var (_, all) = StampFootage.Framecrc(Ffmpeg, _fx.Plain, "0:v:0", "");
        Assert.Equal(all.Sum(p => p.Size), index.VideoBytes);
        Assert.Equal(StampFootage.Frames, all.Count);
    }

    [Fact]
    public void A_video_that_starts_half_a_second_late_has_two_different_time_axes()
    {
        if (_fx.Ffmpeg is null) return;
        var index = Mp4Index.TryRead(_fx.VideoOffset)!;

        Assert.Equal(0.0, index.PreviewSeconds[0], 3);          // what the preview shows
        Assert.True(Math.Abs(index.FileSeconds[0] - 0.5) < 0.001, $"file time of frame 0: {index.FileSeconds[0]:0.000000}");   // what ffmpeg means
        Assert.Equal(StampFootage.Frames, index.FrameCount);
    }

    [Fact]
    public void B_frames_are_detected_and_the_keyframes_stay_where_they_are()
    {
        if (_fx.Ffmpeg is null) return;
        var index = Mp4Index.TryRead(_fx.BFrames)!;

        Assert.True(index.HasBFrames);
        Assert.Equal(12, index.Keyframes.Length);
        Assert.Equal(StampFootage.Frames, index.FrameCount);
    }

    [Fact]
    public void The_chapter_text_track_is_not_a_video_or_audio_track()
    {
        if (_fx.Ffmpeg is null) return;
        Assert.Contains("Stream #0:2", StampFootage.Describe(Ffmpeg, _fx.Chapters));   // the data track really is there

        var index = Mp4Index.TryRead(_fx.Chapters)!;

        Assert.Equal(1, index.AudioTrackCount);
        Assert.Equal(StampFootage.Frames, index.FrameCount);
    }

    // ---- 5-7: fast cuts ----

    [Fact]
    public async Task A_fast_cut_in_the_middle_starts_on_the_keyframe_before_and_ends_on_the_exact_frame()
    {
        if (_fx.Ffmpeg is null) return;
        var source = Copy(_fx.Plain);

        var result = await Cut(source, 95, 250, CutMode.Fast);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(90, StampFootage.FirstStamp(Ffmpeg, result.OutputPath!));
        Assert.Equal(250, StampFootage.LastStamp(Ffmpeg, result.OutputPath!));
        Assert.Equal(161, Frames(result.OutputPath!));
        Assert.Equal(161, result.FrameCount);                    // 18: the service's own check agrees
        Assert.Equal(161, result.ExpectedFrames);
        Assert.Null(result.Warning);

        var (code, err) = FfRun.Run(Ffmpeg, $"-hide_banner -v error -i \"{result.OutputPath}\" -f null -");
        Assert.Equal(0, code);
        Assert.Equal("", err.Trim());
        var streams = Regex.Matches(StampFootage.Describe(Ffmpeg, result.OutputPath!), @"Stream #0:\d+[^:]*: (\w+)").Select(m => m.Groups[1].Value);
        Assert.Equal(new[] { "Video", "Audio" }, streams);       // no data track carried over from the source
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task A_fast_cut_to_the_end_keeps_the_last_frame()
    {
        if (_fx.Ffmpeg is null) return;

        var result = await Cut(Copy(_fx.Plain), 95, 359, CutMode.Fast);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(359, StampFootage.LastStamp(Ffmpeg, result.OutputPath!));
        Assert.Equal(360 - 90, Frames(result.OutputPath!));
    }

    [Fact]
    public async Task A_fast_cut_starting_in_the_first_GOP_starts_at_frame_0()
    {
        if (_fx.Ffmpeg is null) return;

        var result = await Cut(Copy(_fx.Plain), 12, 200, CutMode.Fast);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(0, StampFootage.FirstStamp(Ffmpeg, result.OutputPath!));
        Assert.Equal(200, StampFootage.LastStamp(Ffmpeg, result.OutputPath!));
    }

    // ---- 8-12: exact cuts ----

    [Fact]
    public async Task An_exact_cut_starts_and_ends_on_the_asked_frames_with_audio_and_video_in_step()
    {
        if (_fx.Ffmpeg is null) return;

        var result = await Cut(Copy(_fx.Plain), 95, 250, CutMode.Exact);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(95, StampFootage.FirstStamp(Ffmpeg, result.OutputPath!));
        Assert.Equal(250, StampFootage.LastStamp(Ffmpeg, result.OutputPath!));
        Assert.Equal(156, Frames(result.OutputPath!));
        Assert.Equal(156, result.FrameCount);
        Assert.Null(result.Warning);
        // Both streams start with the cut: the video exactly, the audio at most one AAC frame (21 ms, the encoder's
        // priming samples, hidden by the edit list) before it. The file as a whole starts at 0.
        Assert.InRange(StampFootage.FirstPts(Ffmpeg, result.OutputPath!, "0:v:0"), -0.005, 0.005);
        Assert.InRange(StampFootage.FirstPts(Ffmpeg, result.OutputPath!, "0:a:0"), -0.025, 0.005);
        var start = Regex.Match(StampFootage.Describe(Ffmpeg, result.OutputPath!), @"start: (-?[0-9.]+)");
        Assert.True(start.Success);
        Assert.InRange(double.Parse(start.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), -0.005, 0.005);
    }

    [Fact]
    public async Task An_exact_cut_from_the_first_frame_and_one_to_the_last_frame_keep_those_frames()
    {
        if (_fx.Ffmpeg is null) return;
        var source = Copy(_fx.Plain);

        var head = await Cut(source, 0, 200, CutMode.Exact);
        Assert.True(head.Succeeded, head.Message);
        Assert.Equal(0, StampFootage.FirstStamp(Ffmpeg, head.OutputPath!));
        Assert.Equal(200, StampFootage.LastStamp(Ffmpeg, head.OutputPath!));
        Assert.Equal(201, Frames(head.OutputPath!));

        var tail = await Cut(source, 100, 359, CutMode.Exact);
        Assert.True(tail.Succeeded, tail.Message);
        Assert.Equal(100, StampFootage.FirstStamp(Ffmpeg, tail.OutputPath!));
        Assert.Equal(359, StampFootage.LastStamp(Ffmpeg, tail.OutputPath!));
        Assert.Equal(260, Frames(tail.OutputPath!));
    }

    [Fact]
    public async Task An_exact_cut_of_a_source_with_a_video_offset_finds_the_right_frames_through_the_file_axis()
    {
        if (_fx.Ffmpeg is null) return;

        var result = await Cut(Copy(_fx.VideoOffset), 31, 150, CutMode.Exact);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(31, StampFootage.FirstStamp(Ffmpeg, result.OutputPath!));
        Assert.Equal(150, StampFootage.LastStamp(Ffmpeg, result.OutputPath!));
        Assert.Equal(120, Frames(result.OutputPath!));
    }

    [Fact]
    public async Task An_exact_cut_of_a_source_with_B_frames_is_frame_accurate()
    {
        if (_fx.Ffmpeg is null) return;

        var result = await Cut(Copy(_fx.BFrames), 95, 250, CutMode.Exact);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(95, StampFootage.FirstStamp(Ffmpeg, result.OutputPath!));
        Assert.Equal(250, StampFootage.LastStamp(Ffmpeg, result.OutputPath!));
        Assert.Equal(156, Frames(result.OutputPath!));
    }

    [Fact]
    public async Task An_exact_cut_with_the_amd_hardware_encoder_is_just_as_exact()
    {
        if (_fx.Ffmpeg is null) return;
        using (var probe = new FFmpegService(Ffmpeg))
            if (!await probe.HasEncoderAsync("h264_amf")) return;

        var result = await Cut(Copy(_fx.Plain), 95, 250, CutMode.Exact, encoders: new[] { "h264_amf", "libx264" });

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(95, StampFootage.FirstStamp(Ffmpeg, result.OutputPath!));
        Assert.Equal(250, StampFootage.LastStamp(Ffmpeg, result.OutputPath!));
        Assert.Equal(156, Frames(result.OutputPath!));
    }

    // ---- 13-14: tracks and chapters ----

    [Theory]
    [InlineData(CutMode.Fast)]
    [InlineData(CutMode.Exact)]
    public async Task Both_audio_tracks_survive_either_mode(CutMode mode)
    {
        if (_fx.Ffmpeg is null) return;

        var result = await Cut(Copy(_fx.TwoAudio), 95, 250, mode);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, FFmpegProbeParser.Parse(StampFootage.Describe(Ffmpeg, result.OutputPath!))!.AudioStreamCount);
    }

    [Theory]
    [InlineData(CutMode.Fast)]
    [InlineData(CutMode.Exact)]
    public async Task A_cut_across_a_chapter_boundary_keeps_the_chapters_and_starts_at_zero(CutMode mode)
    {
        if (_fx.Ffmpeg is null) return;

        var result = await Cut(Copy(_fx.Chapters), 95, 270, mode);   // 3.2 s .. 9.0 s, the chapters switch at 6 s

        Assert.True(result.Succeeded, result.Message);
        var text = StampFootage.Describe(Ffmpeg, result.OutputPath!);
        var chapters = Regex.Matches(text, @"Chapter #0:(\d+): start ([0-9.]+), end ([0-9.]+)").ToList();
        Assert.Equal(2, chapters.Count);
        Assert.Equal(0.0, double.Parse(chapters[0].Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), 2);
        Assert.Contains("Eins", text);
        Assert.Contains("Zwei", text);
    }

    // ---- 15: cancel ----

    [Fact]
    public async Task Cancelling_an_exact_cut_after_its_first_progress_leaves_neither_a_result_nor_a_part_file()
    {
        if (_fx.Ffmpeg is null) return;
        var source = Path.Combine(Clips, "Arma Reforger", "Long_60s.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        var (code, err) = FfRun.Run(Ffmpeg, "-hide_banner -loglevel error -y " +
            "-f lavfi -i \"testsrc2=size=1280x720:rate=30,noise=alls=20:allf=t\" -f lavfi -i \"sine=frequency=440\" " +
            $"-t 60 -map 0:v -map 1:a -c:v libx264 -preset ultrafast -crf 20 -g 30 -pix_fmt yuv420p -c:a aac \"{source}\"", 300);
        Assert.True(code == 0, err);

        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var progress = new SyncProgress<CutProgress>(p =>
        {
            if (p.Stage == CutStage.Cutting && p.Fraction > 0) { started.TrySetResult(); cts.Cancel(); }
        });

        var result = await Cut(source, 150, 1500, CutMode.Exact, progress: progress, ct: cts.Token).WaitAsync(TimeSpan.FromSeconds(120));

        Assert.True(started.Task.IsCompleted);
        Assert.Equal(CutStatus.Cancelled, result.Status);
        Assert.Null(result.OutputPath);
        var leftovers = Directory.GetFiles(Clips, "*", SearchOption.AllDirectories).Where(f => f != source).ToArray();
        Assert.Empty(leftovers);
    }

    // ---- 16: names ----

    [Fact]
    public async Task An_existing_cut_is_never_overwritten_the_new_one_gets_a_number()
    {
        if (_fx.Ffmpeg is null) return;
        var source = Copy(_fx.Plain);
        var taken = Path.Combine(Path.GetDirectoryName(source)!, "Clip_1_cut.mp4");
        File.WriteAllText(taken, "an earlier cut");

        var result = await Cut(source, 95, 250, CutMode.Fast);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(source)!, "Clip_1_cut (2).mp4"), result.OutputPath);
        Assert.Equal("an earlier cut", File.ReadAllText(taken));
    }

    // ---- 17: the note ----

    [Fact]
    public async Task The_note_and_the_star_follow_the_cut()
    {
        if (_fx.Ffmpeg is null) return;
        var meta = NewMeta();
        var source = Copy(_fx.Plain);
        var from = GalleryMetaKey.For(GalleryBase.Clips, Clips, source);
        meta.SetNote(from, "Beweis");
        meta.SetFavorite(from, true);

        var result = await Cut(source, 95, 250, CutMode.Fast, meta: meta);

        Assert.True(result.Succeeded, result.Message);
        var to = GalleryMetaKey.For(GalleryBase.Clips, Clips, result.OutputPath!);
        Assert.Equal("Beweis (Ausschnitt 00:03–00:09)", meta.GetNote(to));   // 3.0 s rounded down, 251/30 = 8.37 s rounded up
        Assert.True(meta.IsFavorite(to));
        Assert.Equal("Beweis", meta.GetNote(from));
    }

    // ---- 19: the fallback preview ----

    [Fact]
    public async Task The_frame_grab_shows_the_frame_the_file_time_names()
    {
        if (_fx.Ffmpeg is null) return;
        var index = Mp4Index.TryRead(_fx.Plain)!;
        using var grabs = new FramePreviewService(Ffmpeg);

        foreach (var frame in new[] { 0, 95, 359 })
        {
            var bmp = await grabs.GrabAsync(_fx.Plain, new ClipTimeline(index).FileStartOf(frame), 960, CancellationToken.None);

            Assert.NotNull(bmp);
            Assert.Equal(frame, StampFootage.StampOfBmp(bmp!));
        }
    }

    [Fact]
    public async Task A_newer_frame_grab_cancels_the_older_one_and_a_missing_file_gives_null()
    {
        if (_fx.Ffmpeg is null) return;
        using var grabs = new FramePreviewService(Ffmpeg);

        Assert.Null(await grabs.GrabAsync(Path.Combine(Clips, "nope.mp4"), 1, 960, CancellationToken.None));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Null(await grabs.GrabAsync(_fx.Plain, 1, 960, cts.Token));
    }
}
