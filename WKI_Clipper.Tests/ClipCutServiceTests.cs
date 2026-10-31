using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.Views;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Shared plumbing of the cut service tests: throwaway clips folder, notes store, recorded side effects.</summary>
public abstract class ClipCutFixture : IDisposable
{
    protected readonly string Dir = Path.Combine(Path.GetTempPath(), "wki_cut_" + Guid.NewGuid().ToString("N"));
    protected string Clips => Path.Combine(Dir, "Clips");
    protected string PartsFile => Path.Combine(Dir, "parts.txt");

    protected readonly List<(ToastKind Kind, string Title, string Body, string? Path)> Toasts = new();

    protected ClipCutFixture() => Directory.CreateDirectory(Clips);

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch { }
    }

    protected AppSettings Settings(bool toasts = true)
    {
        var s = new AppSettings();
        s.Output.ClipsFolder = Clips;
        s.Behavior.ShowToastNotifications = toasts;
        return s;
    }

    protected GalleryMetaService NewMeta() => new(Path.Combine(Dir, "meta", "gallery-meta.json"));

    /// <summary>A file standing in for a clip: only its existence and size matter to the service.</summary>
    protected string Source(string name = "Clip_1.mp4", string? game = "Arma Reforger", int bytes = 5000)
    {
        var dir = game is null ? Clips : Path.Combine(Clips, game);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }
}

/// <summary>Everything about the cut service that needs no ffmpeg: checks, plan, verification, notes, the Recycle Bin.</summary>
public sealed class ClipCutServiceTests : ClipCutFixture
{
    // A real file standing in for ffmpeg.exe: only started when a test goes through the real runner.
    private static string SomeExistingFile => typeof(ClipCutServiceTests).Assembly.Location;

    private sealed class Collect : IProgress<CutProgress>
    {
        public List<CutProgress> Items { get; } = new();
        public void Report(CutProgress value) { lock (Items) Items.Add(value); }
        public CutStage[] Stages { get { lock (Items) return Items.Select(i => i.Stage).Distinct().ToArray(); } }
    }

    private sealed class Harness : IDisposable
    {
        public readonly List<FFmpegJob> Jobs = new();
        public readonly List<string> Finished = new();
        public readonly List<string> Recycled = new();
        public FFmpegJobRunner Runner = null!;
        public ClipCutService Service = null!;
        public void Dispose() { Service.Dispose(); Runner.Dispose(); }
    }

    private Harness Build(
        Func<string, Mp4VideoIndex?>? read = null,
        Func<string, RecycleOutcome>? recycle = null,
        IEnumerable<string>? encoders = null,
        bool toasts = true,
        GalleryMetaService? meta = null,
        Func<FFmpegJob, IProgress<FFmpegProgress>?, CancellationToken, Task<FFmpegJobResult>>? job = null,
        bool useRunner = false,
        Func<TimeSpan, Task>? delay = null)
    {
        var h = new Harness { Runner = new FFmpegJobRunner(SomeExistingFile, null, PartsFile) };
        var settings = Settings(toasts);
        h.Service = new ClipCutService(h.Runner, () => settings, meta ?? NewMeta())
        {
            AvailableEncoders = () => encoders ?? new[] { "h264_amf", "libx264" },
            ReadIndex = read ?? (_ => null),
            Recycle = recycle ?? (p => { h.Recycled.Add(p); return RecycleOutcome.Recycled; }),
            Notify = (kind, title, body, path) => Toasts.Add((kind, title, body, path)),
            RunJob = useRunner ? null : job ?? ((j, p, ct) => Succeed(h, j, p)),
            Delay = delay ?? (_ => Task.CompletedTask)
        };
        h.Service.Finished += h.Finished.Add;
        return h;
    }

    /// <summary>What the runner would do on success: the finished file appears at the final name.</summary>
    private static Task<FFmpegJobResult> Succeed(Harness h, FFmpegJob job, IProgress<FFmpegProgress>? progress)
    {
        h.Jobs.Add(job);
        progress?.Report(new FFmpegProgress(0.5, TimeSpan.FromSeconds(1)));
        var path = FFmpegJobRules.UniquePath(job.FinalPath, File.Exists);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[2048]);
        progress?.Report(new FFmpegProgress(1.0, job.ExpectedDuration));
        return Task.FromResult(new FFmpegJobResult(FFmpegJobStatus.Succeeded, path, 0, "", ""));
    }

    private static Mp4VideoIndex SourceIndex(bool bframes = false, int frames = 300) => ClipTimelineTests.Index(frames, keyEvery: 30, bframes: bframes);

    private static CutRequest Request(string source, int from, int to, CutMode mode, bool recycle = false, bool bframes = false)
    {
        var index = SourceIndex(bframes);
        var range = new ClipTimeline(index).Resolve(from, to, mode);
        return new CutRequest(source, index, range, mode, recycle);
    }

    private static Func<string, Mp4VideoIndex?> ResultHasFrames(int frames) => _ => ClipTimelineTests.Index(frames, keyEvery: 30);

    // ---- 1: nothing is started for a request that cannot work ----

    [Fact]
    public async Task A_reversed_range_fails_without_starting_a_job()
    {
        using var h = Build();
        var source = Source();

        var result = await h.Service.CutAsync(Request(source, 200, 100, CutMode.Exact), null, CancellationToken.None);

        Assert.Equal(CutStatus.Failed, result.Status);
        Assert.Contains("vor dem Start", result.Message);
        Assert.Empty(h.Jobs);
        Assert.Empty(h.Finished);
    }

    [Fact]
    public async Task The_whole_clip_fails_without_starting_a_job()
    {
        using var h = Build();
        var source = Source();

        var result = await h.Service.CutAsync(Request(source, 0, 299, CutMode.Exact), null, CancellationToken.None);

        Assert.Equal(CutStatus.Failed, result.Status);
        Assert.Contains("ganzen Clip", result.Message);
        Assert.Empty(h.Jobs);
    }

    [Fact]
    public async Task A_missing_source_fails_without_starting_a_job()
    {
        using var h = Build();

        var result = await h.Service.CutAsync(
            Request(Path.Combine(Clips, "gone.mp4"), 10, 100, CutMode.Exact), null, CancellationToken.None);

        Assert.Equal(CutStatus.Failed, result.Status);
        Assert.Contains("nicht gefunden", result.Message);
        Assert.Empty(h.Jobs);
    }

    [Theory]
    [InlineData("clip.mkv")]
    [InlineData("clip.gif")]
    [InlineData("clip.txt")]
    public async Task Only_mp4_and_mov_are_cut(string name)
    {
        using var h = Build();
        var source = Source(name);

        var result = await h.Service.CutAsync(Request(source, 10, 100, CutMode.Exact), null, CancellationToken.None);

        Assert.Equal(CutStatus.Failed, result.Status);
        Assert.Contains("MP4", result.Message);
        Assert.Empty(h.Jobs);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task A_mov_source_is_accepted_and_the_result_is_an_mp4()
    {
        using var h = Build(read: ResultHasFrames(91));
        var source = Source("clip.mov");

        var result = await h.Service.CutAsync(Request(source, 10, 100, CutMode.Exact), null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(source)!, "clip_cut.mp4"), result.OutputPath);
    }

    [Fact]
    public async Task A_range_outside_the_clip_fails_without_starting_a_job()
    {
        using var h = Build();
        var source = Source();
        var index = SourceIndex();

        var result = await h.Service.CutAsync(
            new CutRequest(source, index, new CutRange(10, 5000, false, false), CutMode.Exact, false), null, CancellationToken.None);

        Assert.Equal(CutStatus.Failed, result.Status);
        Assert.Empty(h.Jobs);
    }

    [Fact]
    public async Task A_failed_request_says_so_in_a_warning_toast()
    {
        using var h = Build();
        var source = Source();

        await h.Service.CutAsync(Request(source, 200, 100, CutMode.Exact), null, CancellationToken.None);

        var toast = Assert.Single(Toasts);
        Assert.Equal(ToastKind.Warning, toast.Kind);
        Assert.Contains("Clip_1.mp4", toast.Body);
    }

    // ---- 2: the job that is started ----

    [Fact]
    public async Task A_fast_cut_is_a_stream_copy_from_the_keyframe_to_the_end_frame()
    {
        using var h = Build(read: ResultHasFrames(161));
        var source = Source();

        var result = await h.Service.CutAsync(Request(source, 95, 250, CutMode.Fast), null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var job = Assert.Single(h.Jobs);
        var index = SourceIndex();
        var expectedArgs = ClipCutRules.BuildFastArgs(source, job.PartPath, index.FileSeconds[90], index.FileSeconds[251], faststart: true);
        Assert.Equal(expectedArgs, job.Arguments);
        Assert.Contains("-c copy", job.Arguments);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(source)!, "Clip_1_cut.mp4"), job.FinalPath);
        Assert.Equal(TimeSpan.FromSeconds(index.FileSeconds[251] - index.FileSeconds[90]), job.ExpectedDuration);
        Assert.Null(job.ThreadCap);
        Assert.True(job.EstimatedOutputBytes > 0);
    }

    [Fact]
    public async Task A_cut_to_the_last_frame_has_no_duration_limit()
    {
        using var h = Build(read: ResultHasFrames(210));
        var source = Source();

        await h.Service.CutAsync(Request(source, 95, 299, CutMode.Fast), null, CancellationToken.None);

        Assert.DoesNotContain(" -t ", Assert.Single(h.Jobs).Arguments);
    }

    [Fact]
    public async Task An_exact_cut_re_encodes_with_the_best_hardware_encoder_and_the_source_bitrate()
    {
        using var h = Build(read: ResultHasFrames(156), encoders: new[] { "libx264", "h264_nvenc" });
        var source = Source();

        var result = await h.Service.CutAsync(Request(source, 95, 250, CutMode.Exact), null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var job = Assert.Single(h.Jobs);
        var index = SourceIndex();
        long bps = ClipCutRules.ExactVideoBitrate(index.VideoBytes, new ClipTimeline(index).PreviewEnd, "avc1");
        Assert.Contains("-c:v h264_nvenc", job.Arguments);
        Assert.Contains($"-b:v {bps}", job.Arguments);
        Assert.Contains("-fps_mode passthrough", job.Arguments);
        Assert.Contains("-ss " + (index.FileSeconds[95] - 0.001).ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture), job.Arguments);
        Assert.Null(job.ThreadCap);
    }

    [Fact]
    public async Task The_software_encoder_is_throttled_so_a_game_keeps_its_cores()
    {
        using var h = Build(read: ResultHasFrames(156), encoders: new[] { "libx264" });
        var source = Source();

        await h.Service.CutAsync(Request(source, 95, 250, CutMode.Exact), null, CancellationToken.None);

        var job = Assert.Single(h.Jobs);
        Assert.Contains("-c:v libx264", job.Arguments);
        Assert.Equal(Math.Max(2, Environment.ProcessorCount / 2), job.ThreadCap);
    }

    [Fact]
    public async Task Without_any_detected_encoder_the_cut_falls_back_to_libx264()
    {
        using var h = Build(read: ResultHasFrames(156), encoders: Array.Empty<string>());
        var source = Source();

        await h.Service.CutAsync(Request(source, 95, 250, CutMode.Exact), null, CancellationToken.None);

        Assert.Contains("-c:v libx264", Assert.Single(h.Jobs).Arguments);
    }

    [Fact]
    public async Task An_exact_cut_of_a_source_wider_than_4096_px_uses_libx264_because_hardware_h264_cannot_open_it()
    {
        // 32:9 monitor: h264_amf / h264_nvenc / h264_qsv refuse 5120x1440, so the hardware encoders are skipped
        using var h = Build(read: ResultHasFrames(156), encoders: new[] { "h264_amf", "h264_nvenc", "h264_qsv", "libx264" });
        var source = Source();
        var wide = SourceIndex() with { Width = 5120, Height = 1440 };
        var range = new ClipTimeline(wide).Resolve(95, 250, CutMode.Exact);

        var result = await h.Service.CutAsync(new CutRequest(source, wide, range, CutMode.Exact, false), null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var job = Assert.Single(h.Jobs);
        Assert.Contains("-c:v libx264", job.Arguments);
        Assert.DoesNotContain("_amf", job.Arguments);
        Assert.DoesNotContain("_nvenc", job.Arguments);
        Assert.DoesNotContain("_qsv", job.Arguments);
        Assert.Equal(Math.Max(2, Environment.ProcessorCount / 2), job.ThreadCap);   // software: the game keeps its cores
        Assert.Contains("-map 0:v:0 -map 0:a? -fps_mode passthrough", job.Arguments);   // streams as for every exact cut
        Assert.Contains("-c:a aac -b:a 192k", job.Arguments);
    }

    [Fact]
    public async Task An_exact_cut_of_a_4096_px_wide_source_still_uses_the_hardware_encoder()
    {
        using var h = Build(read: ResultHasFrames(156), encoders: new[] { "h264_amf", "libx264" });
        var source = Source();
        var uhd = SourceIndex() with { Width = 4096, Height = 2160 };
        var range = new ClipTimeline(uhd).Resolve(95, 250, CutMode.Exact);

        await h.Service.CutAsync(new CutRequest(source, uhd, range, CutMode.Exact, false), null, CancellationToken.None);

        var job = Assert.Single(h.Jobs);
        Assert.Contains("-c:v h264_amf", job.Arguments);
        Assert.Null(job.ThreadCap);
    }

    [Fact]
    public async Task A_fast_cut_of_a_wide_source_is_still_a_stream_copy()
    {
        using var h = Build(read: ResultHasFrames(161), encoders: new[] { "h264_amf", "libx264" });
        var source = Source();
        var wide = SourceIndex() with { Width = 5120, Height = 1440 };
        var range = new ClipTimeline(wide).Resolve(95, 250, CutMode.Fast);

        await h.Service.CutAsync(new CutRequest(source, wide, range, CutMode.Fast, false), null, CancellationToken.None);

        Assert.Contains("-c copy", Assert.Single(h.Jobs).Arguments);
    }

    [Fact]
    public async Task A_result_that_would_be_huge_skips_faststart()
    {
        // 3000 frames = 100 s at ~80 Mbit/s = ~1 GB: past the 1 GiB limit of ClipSavePolicy with the cbr margin
        var big = ClipTimelineTests.Index(9000, keyEvery: 30) with { VideoBytes = 20_000_000_000L };   // 300 s source
        var range = new ClipTimeline(big).Resolve(0, 6000, CutMode.Exact);
        using var h = Build(read: _ => ClipTimelineTests.Index(6001, keyEvery: 30));
        var source = Source();

        await h.Service.CutAsync(new CutRequest(source, big, range, CutMode.Exact, false), null, CancellationToken.None);

        Assert.DoesNotContain("faststart", Assert.Single(h.Jobs).Arguments);
    }

    [Fact]
    public async Task A_small_result_gets_faststart()
    {
        using var h = Build(read: ResultHasFrames(161));
        var source = Source();

        await h.Service.CutAsync(Request(source, 95, 250, CutMode.Fast), null, CancellationToken.None);

        Assert.Contains("-movflags +faststart", Assert.Single(h.Jobs).Arguments);
    }

    [Fact]
    public async Task Without_the_test_seam_the_cut_goes_through_the_job_runner()
    {
        // the "ffmpeg" is a dll that cannot start: the runner reports it, the service turns it into a failed result
        using var h = Build(useRunner: true);
        var source = Source();

        var result = await h.Service.CutAsync(Request(source, 95, 250, CutMode.Exact), null, CancellationToken.None);

        Assert.Equal(CutStatus.Failed, result.Status);
        Assert.Contains("ffmpeg", result.Message);
        Assert.Empty(h.Finished);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(source)!, "Clip_1_cut.mp4")));
        Assert.Equal(ToastKind.Warning, Assert.Single(Toasts).Kind);
    }

    // ---- progress ----

    [Fact]
    public async Task Progress_runs_from_queued_through_cutting_to_verifying()
    {
        using var h = Build(read: ResultHasFrames(156));
        var source = Source();
        var progress = new Collect();

        await h.Service.CutAsync(Request(source, 95, 250, CutMode.Exact), progress, CancellationToken.None);

        Assert.Equal(new[] { CutStage.Queued, CutStage.Cutting, CutStage.Verifying }, progress.Stages);
        Assert.Equal(0.5, progress.Items.First(p => p.Stage == CutStage.Cutting).Fraction);
        Assert.Equal(CutStage.Queued, progress.Items[0].Stage);
    }

    [Fact]
    public async Task The_recycling_stage_only_appears_when_the_original_is_moved()
    {
        using var h = Build(read: ResultHasFrames(156));
        var source = Source();
        var progress = new Collect();

        await h.Service.CutAsync(Request(source, 95, 250, CutMode.Exact, recycle: true), progress, CancellationToken.None);

        Assert.Equal(new[] { CutStage.Queued, CutStage.Cutting, CutStage.Verifying, CutStage.Recycling }, progress.Stages);
    }

    // ---- 3 / 17: note and star follow the clip ----

    [Fact]
    public async Task The_result_gets_the_note_with_the_range_and_the_star()
    {
        var meta = NewMeta();
        using var h = Build(read: ResultHasFrames(161), meta: meta);
        var source = Source();
        var from = GalleryMetaKey.For(GalleryBase.Clips, Clips, source);
        meta.SetNote(from, "Beweis");
        meta.SetFavorite(from, true);

        var result = await h.Service.CutAsync(Request(source, 95, 250, CutMode.Fast), null, CancellationToken.None);

        var to = GalleryMetaKey.For(GalleryBase.Clips, Clips, result.OutputPath!);
        Assert.Equal("clips|Arma Reforger/Clip_1_cut.mp4", to);
        // fast: the start snapped back to frame 90 (3.0 s); the end is frame 250, which ends at 251/30 = 8.37 s → 9
        Assert.Equal("Beweis (Ausschnitt 00:03–00:09)", meta.GetNote(to));
        Assert.True(meta.IsFavorite(to));
        Assert.Equal("Beweis", meta.GetNote(from));        // the original keeps what it had
        Assert.True(meta.IsFavorite(from));
    }

    [Fact]
    public async Task A_clip_without_a_note_gets_just_the_range_and_no_star()
    {
        var meta = NewMeta();
        using var h = Build(read: ResultHasFrames(156), meta: meta);
        var source = Source();

        var result = await h.Service.CutAsync(Request(source, 95, 250, CutMode.Exact), null, CancellationToken.None);

        var to = GalleryMetaKey.For(GalleryBase.Clips, Clips, result.OutputPath!);
        // exact: starts at frame 95 (3.17 s → 3), ends after frame 250 (8.37 s → 9)
        Assert.Equal("Ausschnitt 00:03–00:09", meta.GetNote(to));
        Assert.False(meta.IsFavorite(to));
    }

    // ---- 4: the check of the result ----

    [Fact]
    public async Task A_result_with_exactly_the_asked_frames_has_no_warning()
    {
        using var h = Build(read: ResultHasFrames(156));
        var source = Source();

        var result = await h.Service.CutAsync(Request(source, 95, 250, CutMode.Exact), null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(156, result.FrameCount);
        Assert.Equal(156, result.ExpectedFrames);
        Assert.Null(result.Warning);
        Assert.Null(result.Recycle);
        Assert.Equal(2048, result.OutputBytes);
    }

    [Fact]
    public async Task A_result_with_a_different_frame_count_is_kept_with_a_warning_and_the_original_is_not_recycled()
    {
        using var h = Build(read: ResultHasFrames(155));
        var source = Source();

        var result = await h.Service.CutAsync(Request(source, 95, 250, CutMode.Exact, recycle: true), null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(155, result.FrameCount);
        Assert.Equal(156, result.ExpectedFrames);
        Assert.Contains("155 statt 156", result.Warning);
        Assert.Empty(h.Recycled);
        Assert.Null(result.Recycle);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(result.OutputPath!));
        Assert.Contains(result.OutputPath!, h.Finished);
    }

    [Fact]
    public async Task A_result_that_cannot_be_read_is_a_warning_not_a_failure()
    {
        using var h = Build(read: _ => null);
        var source = Source();

        var result = await h.Service.CutAsync(Request(source, 95, 250, CutMode.Exact, recycle: true), null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(-1, result.FrameCount);
        Assert.Contains("nicht geprüft", result.Warning);
        Assert.Empty(h.Recycled);
    }

    [Theory]
    [InlineData(161, true)]    // exactly as asked
    [InlineData(165, true)]    // up to four frames later (B-frames)
    [InlineData(166, false)]
    [InlineData(160, false)]   // never fewer
    public async Task A_fast_cut_of_a_B_frame_source_tolerates_a_slightly_late_end(int frames, bool clean)
    {
        using var h = Build(read: ResultHasFrames(frames));
        var source = Source();

        var result = await h.Service.CutAsync(Request(source, 95, 250, CutMode.Fast, bframes: true), null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(clean, result.Warning is null);
    }

    [Fact]
    public async Task A_fast_cut_of_a_source_without_B_frames_must_be_exact()
    {
        using var h = Build(read: ResultHasFrames(162));
        var source = Source();

        var result = await h.Service.CutAsync(Request(source, 95, 250, CutMode.Fast), null, CancellationToken.None);

        Assert.NotNull(result.Warning);
    }

    // ---- 6: the original ----

    [Fact]
    public async Task A_verified_cut_moves_the_original_to_the_recycle_bin_when_asked()
    {
        using var h = Build(read: ResultHasFrames(156));
        var source = Source();

        var result = await h.Service.CutAsync(Request(source, 95, 250, CutMode.Exact, recycle: true), null, CancellationToken.None);

        Assert.Equal(new[] { source }, h.Recycled);
        Assert.Equal(RecycleOutcome.Recycled, result.Recycle);
        Assert.Null(result.Warning);
    }

    [Fact]
    public async Task Without_the_option_the_original_is_never_touched()
    {
        using var h = Build(read: ResultHasFrames(156));
        var source = Source();

        var result = await h.Service.CutAsync(Request(source, 95, 250, CutMode.Exact, recycle: false), null, CancellationToken.None);

        Assert.Empty(h.Recycled);
        Assert.Null(result.Recycle);
        Assert.True(File.Exists(source));
    }

    [Theory]
    [InlineData(RecycleOutcome.InUse, "in Verwendung")]
    [InlineData(RecycleOutcome.Declined, "wurde behalten")]
    [InlineData(RecycleOutcome.Failed, "konnte nicht")]
    [InlineData(RecycleOutcome.NotSupported, "konnte nicht")]
    public async Task A_recycle_that_does_not_happen_is_a_warning_and_the_cut_still_counts(RecycleOutcome outcome, string expectedText)
    {
        using var h = Build(read: ResultHasFrames(156), recycle: _ => outcome);
        var source = Source();

        var result = await h.Service.CutAsync(Request(source, 95, 250, CutMode.Exact, recycle: true), null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(outcome, result.Recycle);
        Assert.Contains(expectedText, result.Warning);
        Assert.True(File.Exists(source));
        Assert.Contains(result.OutputPath!, h.Finished);
    }

    [Fact]
    public void RecycleOriginal_moves_a_local_file_and_tells_the_gallery()
    {
        using var h = Build();
        var source = Source();

        var outcome = h.Service.RecycleOriginal(source);

        Assert.Equal(RecycleOutcome.Recycled, outcome);
        Assert.Equal(new[] { source }, h.Recycled);
        Assert.Equal(new[] { source }, h.Finished);
    }

    [Fact]
    public void RecycleOriginal_does_not_announce_a_move_that_did_not_happen()
    {
        using var h = Build(recycle: _ => RecycleOutcome.InUse);
        var source = Source();

        Assert.Equal(RecycleOutcome.InUse, h.Service.RecycleOriginal(source));
        Assert.Empty(h.Finished);
    }

    [Fact]
    public void RecycleOriginal_leaves_a_file_that_is_not_there_alone()
    {
        using var h = Build();

        Assert.Equal(RecycleOutcome.NotSupported, h.Service.RecycleOriginal(Path.Combine(Clips, "gone.mp4")));
        Assert.Empty(h.Recycled);
    }

    [Fact]
    public void RecycleOriginal_tells_the_gallery_about_a_permanent_delete_because_the_file_is_gone_either_way()
    {
        using var h = Build(recycle: _ => RecycleOutcome.DeletedPermanently);
        var source = Source();

        Assert.Equal(RecycleOutcome.DeletedPermanently, h.Service.RecycleOriginal(source));
        Assert.Equal(new[] { source }, h.Finished);
    }

    [Fact]
    public async Task A_permanent_delete_after_the_cut_is_a_warning_that_does_not_claim_the_recycle_bin()
    {
        using var h = Build(read: ResultHasFrames(156), recycle: _ => RecycleOutcome.DeletedPermanently);
        var source = Source();

        var result = await h.Service.CutAsync(Request(source, 95, 250, CutMode.Exact, recycle: true), null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(RecycleOutcome.DeletedPermanently, result.Recycle);
        Assert.Contains("nicht im Papierkorb", result.Warning);
        Assert.DoesNotContain("liegt im Papierkorb", result.Warning);
    }

    // ---- 6b: the original is recycled with a short retry (the preview releases the file a moment late) ----

    [Fact]
    public async Task RecycleOriginalAsync_retries_while_the_file_is_in_use_and_then_moves_it()
    {
        int calls = 0;
        var waits = new List<TimeSpan>();
        using var h = Build(recycle: _ => ++calls < 3 ? RecycleOutcome.InUse : RecycleOutcome.Recycled,
                            delay: t => { waits.Add(t); return Task.CompletedTask; });
        var source = Source();

        var outcome = await h.Service.RecycleOriginalAsync(source);

        Assert.Equal(RecycleOutcome.Recycled, outcome);
        Assert.Equal(3, calls);
        Assert.Equal(2, waits.Count);
        Assert.True(waits[1] > waits[0]);                 // backing off
        Assert.Equal(new[] { source }, h.Finished);       // the gallery hears about it once, at the end
    }

    [Fact]
    public async Task RecycleOriginalAsync_gives_up_after_the_last_delay_and_keeps_the_file()
    {
        int calls = 0;
        var waits = new List<TimeSpan>();
        using var h = Build(recycle: _ => { calls++; return RecycleOutcome.InUse; },
                            delay: t => { waits.Add(t); return Task.CompletedTask; });
        var source = Source();

        var outcome = await h.Service.RecycleOriginalAsync(source);

        Assert.Equal(RecycleOutcome.InUse, outcome);
        Assert.Equal(1 + RecycleBin.RetryDelays.Length, calls);
        Assert.Equal(RecycleBin.RetryDelays, waits);
        Assert.Empty(h.Finished);
        Assert.True(File.Exists(source));
    }

    [Theory]
    [InlineData(RecycleOutcome.Recycled)]
    [InlineData(RecycleOutcome.DeletedPermanently)]
    [InlineData(RecycleOutcome.Declined)]       // asking "delete permanently?" again would be nagging
    [InlineData(RecycleOutcome.Failed)]
    [InlineData(RecycleOutcome.NotSupported)]
    public async Task RecycleOriginalAsync_only_retries_when_the_file_is_in_use(RecycleOutcome outcome)
    {
        int calls = 0;
        var waits = new List<TimeSpan>();
        using var h = Build(recycle: _ => { calls++; return outcome; }, delay: t => { waits.Add(t); return Task.CompletedTask; });
        var source = Source();

        Assert.Equal(outcome, await h.Service.RecycleOriginalAsync(source));
        Assert.Equal(1, calls);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task RecycleOriginalAsync_does_not_wait_for_a_file_that_is_not_there()
    {
        var waits = new List<TimeSpan>();
        using var h = Build(delay: t => { waits.Add(t); return Task.CompletedTask; });

        Assert.Equal(RecycleOutcome.NotSupported, await h.Service.RecycleOriginalAsync(Path.Combine(Clips, "gone.mp4")));
        Assert.Empty(waits);
        Assert.Empty(h.Recycled);
    }

    // ---- 7: finishing ----

    [Fact]
    public async Task Finished_fires_once_with_the_result_path_after_a_successful_cut()
    {
        using var h = Build(read: ResultHasFrames(156));
        var source = Source();

        var result = await h.Service.CutAsync(Request(source, 95, 250, CutMode.Exact), null, CancellationToken.None);

        Assert.Equal(new[] { result.OutputPath! }, h.Finished);
    }

    [Fact]
    public async Task A_cut_that_fails_or_is_cancelled_raises_no_finished_event_and_writes_no_note()
    {
        var meta = NewMeta();
        using var failing = Build(meta: meta, job: (j, p, ct) => Task.FromResult(
            new FFmpegJobResult(FFmpegJobStatus.Failed, null, 1, "ffmpeg ist mit Fehlercode 1 beendet", "")));
        var source = Source();

        var failed = await failing.Service.CutAsync(Request(source, 95, 250, CutMode.Exact), null, CancellationToken.None);

        Assert.Equal(CutStatus.Failed, failed.Status);
        Assert.Contains("Fehlercode 1", failed.Message);
        Assert.Empty(failing.Finished);
        Assert.Equal("", meta.GetNote(GalleryMetaKey.For(GalleryBase.Clips, Clips, Path.Combine(Path.GetDirectoryName(source)!, "Clip_1_cut.mp4"))));

        using var cancelling = Build(meta: meta, job: (j, p, ct) => Task.FromResult(
            new FFmpegJobResult(FFmpegJobStatus.Cancelled, null, -1, "Abgebrochen.", "")));
        var cancelled = await cancelling.Service.CutAsync(Request(source, 95, 250, CutMode.Exact), null, CancellationToken.None);

        Assert.Equal(CutStatus.Cancelled, cancelled.Status);
        Assert.Empty(cancelling.Finished);
        Assert.Single(Toasts);   // the failure was news, the cancel was not
    }

    [Fact]
    public async Task Cancelling_the_token_while_the_job_runs_gives_a_cancelled_result()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        using var h = Build(job: async (j, p, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        var source = Source();

        var run = h.Service.CutAsync(Request(source, 95, 250, CutMode.Exact), null, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(CutStatus.Cancelled, result.Status);
        Assert.Empty(h.Finished);
        Assert.Empty(Toasts);
    }

    [Fact]
    public async Task A_successful_cut_shows_a_clip_toast_that_opens_the_folder()
    {
        using var h = Build(read: ResultHasFrames(156));
        var source = Source();

        var result = await h.Service.CutAsync(Request(source, 95, 250, CutMode.Exact), null, CancellationToken.None);

        var toast = Assert.Single(Toasts);
        Assert.Equal(ToastKind.Clip, toast.Kind);
        Assert.Contains("Clip_1_cut.mp4", toast.Body);
        Assert.Equal(Path.GetDirectoryName(result.OutputPath), toast.Path);
    }

    [Fact]
    public async Task With_toasts_switched_off_nothing_pops_up()
    {
        using var h = Build(read: ResultHasFrames(156), toasts: false);
        var source = Source();

        await h.Service.CutAsync(Request(source, 95, 250, CutMode.Exact), null, CancellationToken.None);
        await h.Service.CutAsync(Request(source, 200, 100, CutMode.Exact), null, CancellationToken.None);

        Assert.Empty(Toasts);
    }

    [Fact]
    public async Task A_disposed_service_starts_nothing()
    {
        using var h = Build();
        var source = Source();
        h.Service.Dispose();

        var result = await h.Service.CutAsync(Request(source, 95, 250, CutMode.Exact), null, CancellationToken.None);

        Assert.Equal(CutStatus.Cancelled, result.Status);
        Assert.Empty(h.Jobs);
    }

    [Fact]
    public async Task Disposing_the_service_cancels_a_running_cut()
    {
        var started = new TaskCompletionSource();
        using var h = Build(job: async (j, p, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        var source = Source();

        var run = h.Service.CutAsync(Request(source, 95, 250, CutMode.Exact), null, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Service.Dispose();

        Assert.Equal(CutStatus.Cancelled, (await run.WaitAsync(TimeSpan.FromSeconds(10))).Status);
    }

    [Fact]
    public async Task A_crash_in_the_job_is_a_failed_result_not_an_exception()
    {
        using var h = Build(job: (j, p, ct) => throw new InvalidOperationException("boom"));
        var source = Source();

        var result = await h.Service.CutAsync(Request(source, 95, 250, CutMode.Exact), null, CancellationToken.None);

        Assert.Equal(CutStatus.Failed, result.Status);
        Assert.Contains("boom", result.Message);
    }
}
