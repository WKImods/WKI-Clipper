using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.Views;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Shared plumbing: a throwaway clips folder, settings pointing at it, a notes store, recorded side effects.</summary>
public abstract class DiscordCompressFixture : IDisposable
{
    protected readonly string Dir = Path.Combine(Path.GetTempPath(), "wki_discord_" + Guid.NewGuid().ToString("N"));
    protected string Clips => Path.Combine(Dir, "Clips");
    protected string PartsFile => Path.Combine(Dir, "parts.txt");

    protected readonly List<string> Clipboard = new();
    protected readonly List<(ToastKind Kind, string Title, string Body, string? Path)> Toasts = new();

    protected DiscordCompressFixture() => Directory.CreateDirectory(Clips);

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch { }
    }

    protected AppSettings Settings(bool onlyWhenNoGame = false, bool toasts = true)
    {
        var s = new AppSettings();
        s.Output.ClipsFolder = Clips;
        s.Discord.OnlyWhenNoGame = onlyWhenNoGame;
        s.Behavior.ShowToastNotifications = toasts;
        return s;
    }

    protected GalleryMetaService NewMeta() => new(Path.Combine(Dir, "meta", "gallery-meta.json"));

    protected DiscordCompressService NewService(
        FFmpegJobRunner runner, AppSettings settings, GalleryMetaService? meta = null,
        IEnumerable<string>? encoders = null, Func<bool>? game = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        => new(runner, () => settings, meta ?? NewMeta())
        {
            AvailableEncoders = () => encoders ?? new[] { "libx264" },
            GameInForeground = game ?? (() => false),
            PlaceOnClipboard = Clipboard.Add,
            Notify = (kind, title, body, path) => Toasts.Add((kind, title, body, path)),
            Delay = delay ?? ((_, _) => Task.CompletedTask),
            GamePollInterval = TimeSpan.FromMilliseconds(1)
        };
}

/// <summary>Everything about the service that needs no ffmpeg: queueing, waiting for games, the job list, notes.</summary>
public sealed class DiscordCompressServiceTests : DiscordCompressFixture
{
    // A real file standing in for ffmpeg.exe: never started here.
    private static string SomeExistingFile => typeof(DiscordCompressServiceTests).Assembly.Location;

    private FFmpegJobRunner Runner() => new(SomeExistingFile, null, PartsFile);

    private sealed class Collect : IProgress<CompressProgress>
    {
        public List<CompressProgress> Items { get; } = new();
        public void Report(CompressProgress value) { lock (Items) Items.Add(value); }
        public CompressStage[] Stages { get { lock (Items) return Items.Select(i => i.Stage).ToArray(); } }
    }

    private string Missing(string name = "missing.mp4") => Path.Combine(Clips, name);

    // ---- waiting for the game ----

    [Fact]
    public async Task With_the_option_off_a_game_in_front_is_not_even_asked_about()
    {
        using var runner = Runner();
        int asked = 0;
        using var service = NewService(runner, Settings(onlyWhenNoGame: false), game: () => { asked++; return true; });

        var result = await service.CompressAsync(Missing(), 20_000_000, null, CancellationToken.None);

        Assert.Equal(0, asked);
        Assert.Equal(CompressStatus.Failed, result.Status);   // fails on the missing file, not on the game
    }

    [Fact]
    public async Task With_the_option_on_the_job_polls_until_the_game_is_gone()
    {
        using var runner = Runner();
        int polls = 0, delays = 0;
        var progress = new Collect();
        using var service = NewService(runner, Settings(onlyWhenNoGame: true),
            game: () => ++polls <= 4,                                // true for the check + 3 polls, then false
            delay: (_, _) => { delays++; return Task.CompletedTask; });

        var result = await service.CompressAsync(Missing(), 20_000_000, progress, CancellationToken.None);

        Assert.Equal(3, delays);
        Assert.Contains(CompressStage.WaitingForGame, progress.Stages);
        Assert.Equal(CompressStatus.Failed, result.Status);
        Assert.Contains("nicht gefunden", result.Message);
    }

    [Fact]
    public async Task No_game_in_front_means_no_waiting_at_all()
    {
        using var runner = Runner();
        int delays = 0;
        var progress = new Collect();
        using var service = NewService(runner, Settings(onlyWhenNoGame: true), game: () => false,
            delay: (_, _) => { delays++; return Task.CompletedTask; });

        await service.CompressAsync(Missing(), 20_000_000, progress, CancellationToken.None);

        Assert.Equal(0, delays);
        Assert.DoesNotContain(CompressStage.WaitingForGame, progress.Stages);
    }

    [Fact]
    public async Task A_job_waiting_for_a_game_can_be_cancelled()
    {
        using var runner = Runner();
        using var cts = new CancellationTokenSource();
        using var service = NewService(runner, Settings(onlyWhenNoGame: true), game: () => true,
            delay: async (span, ct) => { cts.Cancel(); await Task.Delay(span, ct); });

        var result = await service.CompressAsync(Missing(), 20_000_000, null, cts.Token);

        Assert.Equal(CompressStatus.Cancelled, result.Status);
    }

    // ---- one compress at a time ----

    [Fact]
    public async Task A_second_compress_waits_for_the_first_to_finish_completely()
    {
        using var runner = Runner();
        var release = new TaskCompletionSource();
        bool gameOn = true;
        using var service = NewService(runner, Settings(onlyWhenNoGame: true), game: () => gameOn,
            delay: async (_, ct) => { await release.Task.WaitAsync(ct); gameOn = false; });

        var first = new Collect();
        var second = new Collect();
        var a = service.CompressAsync(Missing("a.mp4"), 20_000_000, first, CancellationToken.None);
        await WaitUntil(() => first.Stages.Contains(CompressStage.WaitingForGame));
        var b = service.CompressAsync(Missing("b.mp4"), 20_000_000, second, CancellationToken.None);
        await Task.Delay(100);

        // B has only announced that it is queued; it has not looked at its file or at the game yet
        Assert.Equal(new[] { CompressStage.Queued }, second.Stages);
        Assert.False(a.IsCompleted);

        release.SetResult();
        var results = await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(results, r => Assert.Contains("nicht gefunden", r.Message));
        Assert.Equal(new[] { CompressStage.Queued }, second.Stages);   // b never had to wait for a game of its own
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(10);
        }
    }

    // ---- inputs that cannot be compressed ----

    [Theory]
    [InlineData("clip.gif")]
    [InlineData("shot.png")]
    [InlineData("notes.txt")]
    public async Task Only_videos_are_accepted(string name)
    {
        using var runner = Runner();
        using var service = NewService(runner, Settings());
        var path = Path.Combine(Clips, name);
        File.WriteAllText(path, "x");

        var result = await service.CompressAsync(path, 20_000_000, null, CancellationToken.None);

        Assert.Equal(CompressStatus.Failed, result.Status);
        Assert.Contains("Videos", result.Message);
        Assert.True(File.Exists(path));   // the source is never touched
        Assert.False(Directory.Exists(Path.Combine(Clips, "Komprimiert 20MB")));
    }

    [Fact]
    public async Task A_failed_compress_says_so_in_a_warning_toast()
    {
        using var runner = Runner();
        using var service = NewService(runner, Settings(toasts: true));

        await service.CompressAsync(Missing(), 20_000_000, null, CancellationToken.None);

        var toast = Assert.Single(Toasts);
        Assert.Equal(ToastKind.Warning, toast.Kind);
        Assert.Contains("missing.mp4", toast.Body);
    }

    [Fact]
    public async Task With_toasts_switched_off_nothing_pops_up()
    {
        using var runner = Runner();
        using var service = NewService(runner, Settings(toasts: false));

        await service.CompressAsync(Missing(), 20_000_000, null, CancellationToken.None);

        Assert.Empty(Toasts);
    }

    // ---- the job list of the gallery ----

    [Fact]
    public async Task A_started_job_is_listed_and_a_second_start_for_the_same_clip_is_refused()
    {
        using var runner = Runner();
        var release = new TaskCompletionSource();
        using var service = NewService(runner, Settings(onlyWhenNoGame: true), game: () => !release.Task.IsCompleted,
            delay: async (_, ct) => await release.Task.WaitAsync(ct));
        var path = Missing("busy.mp4");

        Assert.True(service.Start(path, 20_000_000));
        await WaitUntil(() => service.Snapshot(path)?.Stage == CompressStage.WaitingForGame);

        var snap = service.Snapshot(path)!;
        Assert.True(snap.Active);
        Assert.Equal(20_000_000, snap.TargetBytes);
        Assert.False(service.Start(path, 500_000_000));       // already running
        Assert.True(service.Start(Missing("other.mp4"), 20_000_000));   // another clip is fine

        release.SetResult();
        await WaitUntil(() => service.Snapshot(path) is { Active: false });
    }

    [Fact]
    public async Task A_failed_job_keeps_its_error_until_it_is_cleared_or_started_again()
    {
        using var runner = Runner();
        using var service = NewService(runner, Settings());
        var path = Missing("gone.mp4");
        CompressJobSnapshot? last = null;
        service.JobChanged += s => last = s;

        Assert.True(service.Start(path, 20_000_000));
        await WaitUntil(() => service.Snapshot(path) is { Active: false });

        var snap = service.Snapshot(path)!;
        Assert.Equal(CompressStatus.Failed, snap.Outcome);
        Assert.Contains("nicht gefunden", snap.Error);
        Assert.Equal(CompressStatus.Failed, last!.Outcome);

        service.ClearFinished();
        Assert.Null(service.Snapshot(path));

        Assert.True(service.Start(path, 20_000_000));   // and again after a failure
        await WaitUntil(() => service.Snapshot(path) is { Active: false });
    }

    [Fact]
    public async Task A_cancelled_job_leaves_no_trace_in_the_list()
    {
        using var runner = Runner();
        var never = new TaskCompletionSource();
        using var service = NewService(runner, Settings(onlyWhenNoGame: true), game: () => true,
            delay: async (_, ct) => await never.Task.WaitAsync(ct));
        var path = Missing("cancel.mp4");
        CompressJobSnapshot? last = null;
        service.JobChanged += s => last = s;

        service.Start(path, 20_000_000);
        await WaitUntil(() => service.Snapshot(path)?.Stage == CompressStage.WaitingForGame);

        Assert.True(service.Cancel(path));
        await WaitUntil(() => last is { Outcome: not null });

        Assert.Equal(CompressStatus.Cancelled, last!.Outcome);
        Assert.Null(last.Error);
        Assert.Null(service.Snapshot(path));
        Assert.False(service.Cancel(path));   // nothing left to cancel
    }

    [Fact]
    public void Cancelling_a_clip_without_a_job_is_a_no_op()
    {
        using var runner = Runner();
        using var service = NewService(runner, Settings());
        Assert.False(service.Cancel(Missing()));
        Assert.Null(service.Snapshot(Missing()));
    }

    [Fact]
    public async Task Disposing_the_service_cancels_what_is_running_and_refuses_new_work()
    {
        using var runner = Runner();
        var never = new TaskCompletionSource();
        var service = NewService(runner, Settings(onlyWhenNoGame: true), game: () => true,
            delay: async (_, ct) => await never.Task.WaitAsync(ct));
        var path = Missing("shutdown.mp4");
        CompressJobSnapshot? last = null;
        service.JobChanged += s => last = s;
        service.Start(path, 20_000_000);
        await WaitUntil(() => service.Snapshot(path)?.Stage == CompressStage.WaitingForGame);

        service.Dispose();

        await WaitUntil(() => last is { Outcome: not null });
        Assert.Equal(CompressStatus.Cancelled, last!.Outcome);
        Assert.False(service.Start(Missing("late.mp4"), 20_000_000));
        Assert.Equal(CompressStatus.Cancelled, (await service.CompressAsync(Missing("late2.mp4"), 20_000_000, null, CancellationToken.None)).Status);
    }

    // ---- notes follow the clip ----

    [Fact]
    public void The_copy_gets_the_note_with_the_discord_marker_and_the_star()
    {
        using var runner = Runner();
        var meta = NewMeta();
        using var service = NewService(runner, Settings(), meta);
        var source = Path.Combine(Clips, "Arma Reforger", "Clip_1.mp4");
        var output = Path.Combine(Clips, "Komprimiert 20MB", "Clip_1_20MB.mp4");
        var from = GalleryMetaKey.For(GalleryBase.Clips, Clips, source);
        meta.SetNote(from, "Admin-Beweis 14.10.");
        meta.SetFavorite(from, true);

        service.CopyMeta(Clips, source, output, 20_000_000);

        var to = GalleryMetaKey.For(GalleryBase.Clips, Clips, output);
        Assert.Equal("clips|Komprimiert 20MB/Clip_1_20MB.mp4", to);
        Assert.Equal("Admin-Beweis 14.10. (Discord 20 MB)", meta.GetNote(to));
        Assert.True(meta.IsFavorite(to));
        Assert.Equal("Admin-Beweis 14.10.", meta.GetNote(from));   // the original is untouched
    }

    [Fact]
    public void A_clip_without_note_or_star_leaves_no_entry_for_the_copy()
    {
        using var runner = Runner();
        var meta = NewMeta();
        using var service = NewService(runner, Settings(), meta);
        var output = Path.Combine(Clips, "Komprimiert 20MB", "x_20MB.mp4");

        service.CopyMeta(Clips, Path.Combine(Clips, "x.mp4"), output, 20_000_000);

        var to = GalleryMetaKey.For(GalleryBase.Clips, Clips, output);
        Assert.Equal("", meta.GetNote(to));
        Assert.False(meta.IsFavorite(to));
    }
}

/// <summary>Real ffmpeg, real files. Skips (returns) when no ffmpeg.exe can be found.</summary>
[Trait("Category", "Integration")]
public sealed class DiscordCompressIntegrationTests : DiscordCompressFixture
{
    private readonly string? _ffmpeg = FFmpegTestSupport.Find();

    private FFmpegJobRunner Runner() => new(_ffmpeg!, null, PartsFile);

    /// <summary>Runs ffmpeg to make a test source; false if it could not be made (e.g. an encoder is missing).</summary>
    private bool TryGenerate(string args, out string error)
    {
        var psi = new ProcessStartInfo(_ffmpeg!, "-hide_banner -loglevel error -y " + args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0;
    }

    /// <summary>A noisy synthetic clip (noise keeps the bitrate honest), H.264 + AAC, with one or two audio tracks.</summary>
    private string NoisyClip(string name, int seconds, string size = "1280x720", int fps = 30, int audioTracks = 1, string videoCodec = "libx264")
    {
        var path = Path.Combine(Clips, name);
        var inputs = "-f lavfi -i \"testsrc2=size=" + size + ":rate=" + fps + ",noise=alls=20:allf=t\" -f lavfi -i \"sine=frequency=440\" "
                     + (audioTracks > 1 ? "-f lavfi -i \"sine=frequency=660\" " : "");
        var maps = "-map 0:v -map 1:a " + (audioTracks > 1 ? "-map 2:a " : "");
        var enc = videoCodec == "libx265" ? "-c:v libx265 -preset ultrafast -x265-params log-level=error -tag:v hvc1" : $"-c:v {videoCodec} -preset ultrafast -crf 20";
        Assert.True(TryGenerate($"{inputs}-t {seconds} {maps}{enc} -pix_fmt yuv420p -c:a aac \"{path}\"", out var err), err);
        return path;
    }

    private async Task<MediaProbe> Probe(FFmpegJobRunner runner, string path)
        => (await runner.ProbeAsync(path))!;

    private string[] AllVideos() => Directory.GetFiles(Clips, "*.*", SearchOption.AllDirectories)
        .Where(f => f.EndsWith(".mp4") || f.EndsWith(".part")).ToArray();

    [Fact]
    public async Task Software_compress_fits_a_20_second_clip_into_2_MB_and_sorts_the_result_into_its_folder()
    {
        if (_ffmpeg is null) return;
        using var runner = Runner();
        var meta = NewMeta();
        using var service = NewService(runner, Settings(), meta, encoders: new[] { "libx264" });
        var source = NoisyClip("Clip_20s.mp4", 20, audioTracks: 2);   // 2 audio tracks: system + mic
        var sourceBytes = new FileInfo(source).Length;
        Assert.True(sourceBytes > 2_000_000, "the test source must be bigger than the target");
        var key = GalleryMetaKey.For(GalleryBase.Clips, Clips, source);
        meta.SetNote(key, "Beweis");
        var tempBefore = Directory.GetFiles(Path.GetTempPath(), "wki_*-0.log*").Length;
        var progress = new List<CompressProgress>();

        var result = await service.CompressAsync(source, 2_000_000, new InlineProgress(progress.Add), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.False(result.Copied);
        var expected = Path.Combine(Clips, "Komprimiert 2MB", "Clip_20s_2MB.mp4");
        Assert.Equal(expected, result.OutputPath);
        Assert.True(File.Exists(expected));
        Assert.InRange(new FileInfo(expected).Length, 1_000_000, 2_000_000);
        Assert.Equal(result.OutputBytes, new FileInfo(expected).Length);

        var probe = await Probe(runner, expected);
        Assert.Equal("h264", probe.VideoCodec);
        Assert.Equal(1, probe.AudioStreamCount);                       // both tracks mixed into one
        Assert.Equal(20.0, probe.Duration!.Value.TotalSeconds, 1);
        Assert.Equal(480, probe.Height);                               // 2 MB over 20 s only supports 480p
        Assert.Equal(854, probe.Width);

        Assert.Equal(new[] { expected }, Clipboard);                   // ready for Ctrl+V
        var toast = Assert.Single(Toasts);
        Assert.Equal(ToastKind.Clip, toast.Kind);
        Assert.Equal(Path.GetDirectoryName(expected), toast.Path);     // a click opens the folder
        Assert.Equal("Beweis (Discord 2 MB)", meta.GetNote(GalleryMetaKey.For(GalleryBase.Clips, Clips, expected)));

        Assert.Equal(sourceBytes, new FileInfo(source).Length);        // the original is untouched
        Assert.Equal(new[] { source, expected }.OrderBy(x => x), AllVideos().OrderBy(x => x));   // no .part, no failed attempt
        Assert.Equal(tempBefore, Directory.GetFiles(Path.GetTempPath(), "wki_*-0.log*").Length);   // 2-pass statistics cleaned up
        Assert.Contains(progress, p => p.Stage == CompressStage.Encoding && p.Fraction > 0);
        Assert.True(progress.Where(p => p.Stage == CompressStage.Encoding).Max(p => p.Fraction) > 0.9);
    }

    [Fact]
    public async Task A_second_compress_of_the_same_clip_gets_a_numbered_name_and_leaves_the_first_alone()
    {
        if (_ffmpeg is null) return;
        using var runner = Runner();
        using var service = NewService(runner, Settings());
        var source = NoisyClip("Clip_twice.mp4", 8);

        var first = await service.CompressAsync(source, 1_000_000, null, CancellationToken.None);
        var firstBytes = File.ReadAllBytes(first.OutputPath!);
        var second = await service.CompressAsync(source, 1_000_000, null, CancellationToken.None);

        Assert.True(first.Succeeded && second.Succeeded, first.Message + second.Message);
        Assert.EndsWith("Clip_twice_1MB.mp4", first.OutputPath);
        Assert.EndsWith("Clip_twice_1MB (2).mp4", second.OutputPath);
        Assert.Equal(firstBytes, File.ReadAllBytes(first.OutputPath!));
    }

    [Fact]
    public async Task A_clip_that_already_fits_and_is_plain_h264_aac_is_copied_not_encoded()
    {
        if (_ffmpeg is null) return;
        using var runner = Runner();
        using var service = NewService(runner, Settings());
        Directory.CreateDirectory(Path.Combine(Clips, "Arma Reforger"));
        var source = Path.Combine(Clips, "Arma Reforger", "Clip_small.mp4");
        Assert.True(TryGenerate($"-f lavfi -i testsrc2=size=640x360:rate=30 -f lavfi -i sine=frequency=440 -t 3 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac \"{source}\"", out var err), err);
        var progress = new List<CompressProgress>();

        var result = await service.CompressAsync(source, 20_000_000, new InlineProgress(progress.Add), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Copied);
        Assert.Equal(Path.Combine(Clips, "Komprimiert 20MB", "Clip_small_20MB.mp4"), result.OutputPath);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(result.OutputPath!));   // byte-identical
        Assert.Contains(progress, p => p.Stage == CompressStage.Copying);
        Assert.DoesNotContain(progress, p => p.Stage == CompressStage.Encoding);
        Assert.Equal(new[] { result.OutputPath }, Clipboard);
    }

    [Fact]
    public async Task A_small_hevc_clip_is_re_encoded_to_h264_because_discord_must_be_able_to_play_it()
    {
        if (_ffmpeg is null) return;
        using var runner = Runner();
        using var service = NewService(runner, Settings());
        var source = Path.Combine(Clips, "hevc.mp4");
        if (!TryGenerate($"-f lavfi -i testsrc2=size=640x360:rate=30 -f lavfi -i sine=frequency=440 -t 3 -c:v libx265 -preset ultrafast -x265-params log-level=error -tag:v hvc1 -pix_fmt yuv420p -c:a aac \"{source}\"", out _))
            return;   // this ffmpeg has no libx265
        Assert.Equal("hevc", (await Probe(runner, source)).VideoCodec);

        var result = await service.CompressAsync(source, 20_000_000, null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.False(result.Copied);
        Assert.Equal("h264", (await Probe(runner, result.OutputPath!)).VideoCodec);
    }

    [Fact]
    public async Task A_clip_without_audio_still_compresses()
    {
        if (_ffmpeg is null) return;
        using var runner = Runner();
        using var service = NewService(runner, Settings());
        var source = Path.Combine(Clips, "silent.mp4");
        Assert.True(TryGenerate($"-f lavfi -i \"testsrc2=size=640x360:rate=30,noise=alls=20:allf=t\" -t 6 -c:v libx264 -preset ultrafast -crf 18 -pix_fmt yuv420p \"{source}\"", out var err), err);
        Assert.True(new FileInfo(source).Length > 1_000_000);

        var result = await service.CompressAsync(source, 1_000_000, null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(0, (await Probe(runner, result.OutputPath!)).AudioStreamCount);
        Assert.True(result.OutputBytes <= 1_000_000);
    }

    [Fact]
    public async Task A_clip_too_long_for_the_target_is_refused_before_anything_is_encoded_or_created()
    {
        if (_ffmpeg is null) return;
        using var runner = Runner();
        using var service = NewService(runner, Settings(toasts: false));
        var source = Path.Combine(Clips, "long.mp4");
        // 2000 s at 1 fps: a tiny file that LOOKS 33 minutes long
        Assert.True(TryGenerate($"-f lavfi -i testsrc2=size=320x180:rate=1 -f lavfi -i sine=frequency=440:sample_rate=8000 -t 2000 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -b:a 8k \"{source}\"", out var err), err);

        var result = await service.CompressAsync(source, 1_000_000, null, CancellationToken.None);

        Assert.Equal(CompressStatus.Failed, result.Status);
        Assert.True(result.TooLong);
        Assert.Contains("1 MB", result.Message);
        Assert.Contains("erst schneiden", result.Message);
        Assert.Equal(new[] { source }, AllVideos());
        Assert.False(Directory.Exists(Path.Combine(Clips, "Komprimiert 1MB")));
        Assert.Empty(Clipboard);
    }

    [Fact]
    public async Task Cancelling_mid_encode_leaves_neither_a_result_nor_a_part_file()
    {
        if (_ffmpeg is null) return;
        using var runner = Runner();
        using var service = NewService(runner, Settings());
        var source = NoisyClip("Clip_cancel.mp4", 30, size: "1280x720", fps: 60);
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var progress = new InlineProgress(p =>
        {
            if (p.Stage == CompressStage.Encoding && p.Fraction > 0.02) started.TrySetResult();
        });

        var run = service.CompressAsync(source, 3_000_000, progress, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(60));
        cts.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(CompressStatus.Cancelled, result.Status);
        Assert.Equal(new[] { source }, AllVideos());
        Assert.Empty(Clipboard);
        Assert.Empty(Toasts);   // a cancel is not news
    }

    [Fact]
    public async Task A_missed_target_never_leaves_the_oversize_attempt_behind()
    {
        if (_ffmpeg is null) return;
        using var runner = Runner();
        using var service = NewService(runner, Settings(), encoders: new[] { "libx264" });
        // very small target for noisy content: whatever happens, only a file within the limit may remain
        var source = NoisyClip("Clip_hard.mp4", 12, size: "1280x720");

        var result = await service.CompressAsync(source, 1_000_000, null, CancellationToken.None);

        var leftovers = AllVideos().Where(f => f != source).ToArray();
        if (result.Succeeded)
        {
            Assert.Equal(new[] { result.OutputPath! }, leftovers);
            Assert.True(new FileInfo(result.OutputPath!).Length <= 1_000_000);
        }
        else
        {
            Assert.Empty(leftovers);
        }
    }

    /// <summary>
    /// The AMF encoder on this machine (skips without one): the size target is hit by the hardware
    /// encoder's own rate control, within the limit, in few attempts.
    /// </summary>
    [Fact]
    [Trait("Encoder", "AMF")]
    public async Task Amf_hits_an_8_MB_target_for_a_20_second_1080p60_clip()
    {
        if (_ffmpeg is null) return;
        using var ffmpegService = new FFmpegService(_ffmpeg);
        if (!await ffmpegService.HasEncoderAsync("h264_amf")) return;

        using var runner = Runner();
        using var service = NewService(runner, Settings(), encoders: new[] { "h264_amf", "libx264" });
        var source = NoisyClip("Clip_amf.mp4", 20, size: "1920x1080", fps: 60);
        Assert.True(new FileInfo(source).Length > 8_000_000);

        var result = await service.CompressAsync(source, 8_000_000, null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.InRange(result.OutputBytes, 4_000_000, 8_000_000);
        Assert.InRange(result.Attempts, 1, 3);
        var probe = await Probe(runner, result.OutputPath!);
        Assert.Equal("h264", probe.VideoCodec);
        Assert.Equal(20.0, probe.Duration!.Value.TotalSeconds, 1);
    }

    private sealed class InlineProgress : IProgress<CompressProgress>
    {
        private readonly Action<CompressProgress> _report;
        public InlineProgress(Action<CompressProgress> report) => _report = report;
        public void Report(CompressProgress value) => _report(value);
    }
}
