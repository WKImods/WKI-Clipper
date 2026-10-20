using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// The encode attempts of a Discord compress, driven without ffmpeg: a scripted "ffmpeg" (the job seam) and a scripted
/// probe. About the game check before every ffmpeg that is started, the disk space an attempt asks for, the
/// pass-1 statistics files, and when hardware decoding is given up.
/// </summary>
public sealed class DiscordCompressAttemptTests : DiscordCompressFixture
{
    private static string SomeExistingFile => typeof(DiscordCompressAttemptTests).Assembly.Location;
    private string StatsDir => Path.Combine(Dir, "stats");

    private sealed class Collect : IProgress<CompressProgress>
    {
        public List<CompressProgress> Items { get; } = new();
        public void Report(CompressProgress value) { lock (Items) Items.Add(value); }
        public CompressStage[] Stages { get { lock (Items) return Items.Select(i => i.Stage).ToArray(); } }
    }

    private sealed class Rig : IDisposable
    {
        public readonly List<string> Events = new();
        public readonly List<FFmpegJob> Jobs = new();
        public FFmpegJobRunner Runner = null!;
        public DiscordCompressService Service = null!;
        public void Dispose() { Service.Dispose(); Runner.Dispose(); }
    }

    /// <summary>The 30 MB source of a 30 s clip with two audio tracks (so it is encoded, not copied).</summary>
    private string Clip()
    {
        var path = Path.Combine(Clips, "clip.mp4");
        File.WriteAllBytes(path, new byte[30_000_000]);
        return path;
    }

    private static Task<MediaProbe?> Probe(string path, CancellationToken ct)
        => Task.FromResult<MediaProbe?>(new MediaProbe(TimeSpan.FromSeconds(30), "h264", 1920, 1080, 60, new[] { "aac", "aac" }));

    /// <summary>What the runner returns for a finished job: a file of <paramref name="bytes"/> at the final name (none for a discarding pass).</summary>
    private static FFmpegJobResult Done(FFmpegJob job, int bytes)
    {
        if (job.DiscardOutput) return new FFmpegJobResult(FFmpegJobStatus.Succeeded, null, 0, "", "");
        var path = FFmpegJobRules.UniquePath(job.FinalPath, File.Exists);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return new FFmpegJobResult(FFmpegJobStatus.Succeeded, path, 0, "", "");
    }

    private static FFmpegJobResult Failed(int exitCode, string stderr)
        => new(FFmpegJobStatus.Failed, null, exitCode, $"ffmpeg exited with code {exitCode}", stderr);

    private Rig Build(AppSettings settings, Func<FFmpegJob, int, FFmpegJobResult> script,
        string[]? encoders = null, Func<bool>? game = null, Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<string, long?>? freeSpace = null)
    {
        var rig = new Rig { Runner = new FFmpegJobRunner(SomeExistingFile, null, PartsFile) };
        rig.Service = new DiscordCompressService(rig.Runner, () => settings, NewMeta())
        {
            AvailableEncoders = () => encoders ?? new[] { "h264_amf", "libx264" },
            GameInForeground = () => { rig.Events.Add("game"); return game?.Invoke() ?? false; },
            PlaceOnClipboard = Clipboard.Add,
            Notify = (kind, title, body, path) => Toasts.Add((kind, title, body, path)),
            Delay = delay is null
                ? (_, _) => { rig.Events.Add("wait"); return Task.CompletedTask; }
                : (span, ct) => { rig.Events.Add("wait"); return delay(span, ct); },
            GamePollInterval = TimeSpan.FromMilliseconds(1),
            RunJob = (job, progress, ct) =>
            {
                rig.Jobs.Add(job);
                rig.Events.Add("job");
                return Task.FromResult(script(job, rig.Jobs.Count));
            },
            Probe = Probe,
            StatsFolder = StatsDir,
            FreeSpace = freeSpace ?? (_ => long.MaxValue)
        };
        return rig;
    }

    // ---- the game check holds for every ffmpeg the job starts ----

    [Fact]
    public async Task With_the_option_on_every_attempt_looks_for_a_game_before_its_ffmpeg_starts()
    {
        using var rig = Build(Settings(onlyWhenNoGame: true),
            (job, n) => Done(job, n == 1 ? 25_000_000 : 12_000_000));   // the first result is over 20 MB: a second attempt follows

        var result = await rig.Service.CompressAsync(Clip(), 20_000_000, null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, rig.Jobs.Count);
        for (int i = 0; i < rig.Events.Count; i++)
            if (rig.Events[i] == "job")
                Assert.True(i > 0 && rig.Events[i - 1] == "game", "an ffmpeg started without a look at the game: " + string.Join(",", rig.Events));
    }

    [Fact]
    public async Task A_game_that_comes_up_between_two_attempts_holds_the_next_one_back_until_it_is_gone()
    {
        int looks = 0;
        var progress = new Collect();
        using var rig = Build(Settings(onlyWhenNoGame: true),
            (job, n) => Done(job, n == 1 ? 25_000_000 : 12_000_000),
            game: () => { looks++; return looks is 3 or 4; });   // the check before attempt 2 and one poll: a game is in front

        var result = await rig.Service.CompressAsync(Clip(), 20_000_000, progress, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        int firstJob = rig.Events.IndexOf("job");
        int secondJob = rig.Events.LastIndexOf("job");
        Assert.Equal(2, rig.Jobs.Count);
        Assert.Contains("wait", rig.Events.Skip(firstJob).Take(secondJob - firstJob));   // waited between the two ffmpegs
        Assert.Contains(CompressStage.WaitingForGame, progress.Stages);
    }

    [Fact]
    public async Task With_the_option_off_a_game_is_never_asked_about_for_any_attempt()
    {
        using var rig = Build(Settings(onlyWhenNoGame: false),
            (job, n) => Done(job, n == 1 ? 25_000_000 : 12_000_000), game: () => true);

        var result = await rig.Service.CompressAsync(Clip(), 20_000_000, null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.DoesNotContain("game", rig.Events);
        Assert.DoesNotContain("wait", rig.Events);
    }

    [Fact]
    public async Task The_second_pass_of_a_software_encode_waits_for_the_game_too()
    {
        int looks = 0;
        using var rig = Build(Settings(onlyWhenNoGame: true), (job, n) => Done(job, 12_000_000),
            encoders: new[] { "libx264" },
            // 1: before the job, 2: before pass 1; 3 and 4: between the passes (the look and the first poll of the wait), then it is gone
            game: () => { looks++; return looks is 3 or 4; });

        var result = await rig.Service.CompressAsync(Clip(), 20_000_000, null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, rig.Jobs.Count);                      // pass 1 (discarding) and pass 2
        Assert.True(rig.Jobs[0].DiscardOutput);
        Assert.Equal(new[] { "game", "game", "job", "game", "game", "wait", "game", "job" }, rig.Events);
    }

    [Fact]
    public async Task A_job_waiting_for_a_game_between_attempts_can_still_be_cancelled()
    {
        using var cts = new CancellationTokenSource();
        int looks = 0;
        using var rig = Build(Settings(onlyWhenNoGame: true), (job, n) => Done(job, 25_000_000),
            game: () => ++looks >= 3,
            delay: (span, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });

        var result = await rig.Service.CompressAsync(Clip(), 20_000_000, null, cts.Token);

        Assert.Equal(CompressStatus.Cancelled, result.Status);
        Assert.Single(rig.Jobs);                              // the second attempt never started
    }

    // ---- disk space: what the attempt can write, not the whole target ----

    [Fact]
    public async Task A_short_clip_for_the_500_MB_target_asks_for_its_own_size_in_free_space()
    {
        using var rig = Build(Settings(), (job, n) => Done(job, 30_000_000));

        var result = await rig.Service.CompressAsync(Clip(), 500_000_000, null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var job = Assert.Single(rig.Jobs);
        Assert.InRange(job.EstimatedOutputBytes, 30_000_000, 100_000_000);          // 30 s at the source's own rate, not 500 MB
        Assert.True(FFmpegJobRules.HasEnoughDiskSpace(900_000_000, job.EstimatedOutputBytes));    // 900 MB free is plenty
        Assert.False(FFmpegJobRules.HasEnoughDiskSpace(500_000_000, job.EstimatedOutputBytes));   // and the reserve still counts
    }

    [Fact]
    public async Task A_clip_that_really_fills_the_target_still_asks_for_the_target()
    {
        // 30 MB source, 20 MB target: the budget (~5 Mbit/s) is below the source's own rate
        using var rig = Build(Settings(), (job, n) => Done(job, 12_000_000));

        await rig.Service.CompressAsync(Clip(), 20_000_000, null, CancellationToken.None);

        var job = Assert.Single(rig.Jobs);
        Assert.InRange(job.EstimatedOutputBytes, 15_000_000, 20_000_000);
    }

    [Fact]
    public async Task The_second_software_pass_asks_for_the_attempt_size_too()
    {
        using var rig = Build(Settings(), (job, n) => Done(job, 30_000_000), encoders: new[] { "libx264" });

        await rig.Service.CompressAsync(Clip(), 500_000_000, null, CancellationToken.None);

        Assert.Equal(2, rig.Jobs.Count);
        Assert.Equal(0, rig.Jobs[0].EstimatedOutputBytes);                                  // the measuring pass writes no file
        Assert.InRange(rig.Jobs[1].EstimatedOutputBytes, 30_000_000, 100_000_000);
    }

    // ---- the pass-1 statistics ----

    private static string PassLogOf(FFmpegJob job)
    {
        const string marker = "-passlogfile \"";
        int at = job.Arguments.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, "no -passlogfile in " + job.Arguments);
        int start = at + marker.Length;
        return job.Arguments[start..job.Arguments.IndexOf('"', start)];
    }

    private static void WriteStats(string passLog)
    {
        File.WriteAllText(passLog + "-0.log", "stats");
        File.WriteAllText(passLog + "-0.log.mbtree", "tree");
    }

    [Fact]
    public async Task The_statistics_go_to_our_own_folder_under_a_name_of_their_own_and_are_gone_afterwards()
    {
        string? seen = null;
        using var rig = Build(Settings(), (job, n) =>
        {
            if (job.DiscardOutput) { seen = PassLogOf(job); WriteStats(seen); }
            return Done(job, 12_000_000);
        }, encoders: new[] { "libx264" });

        var result = await rig.Service.CompressAsync(Clip(), 20_000_000, null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.NotNull(seen);
        Assert.Equal(StatsDir, Path.GetDirectoryName(seen));
        Assert.True(DiscordCompressRules.IsPassStatsFile(Path.GetFileName(seen!) + "-0.log"));
        Assert.Empty(Directory.GetFiles(StatsDir));          // cleaned up in the finally
    }

    [Fact]
    public async Task Every_attempt_gets_its_own_statistics_prefix()
    {
        var prefixes = new List<string>();
        using var rig = Build(Settings(), (job, n) =>
        {
            if (job.DiscardOutput) prefixes.Add(PassLogOf(job));
            return Done(job, n <= 2 ? 25_000_000 : 12_000_000);   // pass 1, pass 2 (too big), pass 1 again, ...
        }, encoders: new[] { "libx264" });

        await rig.Service.CompressAsync(Clip(), 20_000_000, null, CancellationToken.None);

        Assert.True(prefixes.Count >= 2);
        Assert.Equal(prefixes.Count, prefixes.Distinct().Count());
    }

    [Fact]
    public async Task A_failing_first_pass_still_cleans_up_what_it_wrote()
    {
        using var rig = Build(Settings(), (job, n) =>
        {
            WriteStats(PassLogOf(job));
            return Failed(1, "something broke");
        }, encoders: new[] { "libx264" });

        var result = await rig.Service.CompressAsync(Clip(), 20_000_000, null, CancellationToken.None);

        Assert.Equal(CompressStatus.Failed, result.Status);
        Assert.Empty(Directory.GetFiles(StatsDir));
    }

    [Fact]
    public async Task Too_little_room_for_the_statistics_fails_before_the_first_pass_starts()
    {
        using var rig = Build(Settings(), (job, n) => Done(job, 12_000_000), encoders: new[] { "libx264" },
            freeSpace: _ => 100_000_000);   // 100 MB: not even the reserve

        var result = await rig.Service.CompressAsync(Clip(), 20_000_000, null, CancellationToken.None);

        Assert.Equal(CompressStatus.Failed, result.Status);
        Assert.Contains("2-Pass-Statistik", result.Message);
        Assert.Empty(rig.Jobs);
    }

    [Fact]
    public async Task Plenty_of_room_lets_the_two_passes_run()
    {
        using var rig = Build(Settings(), (job, n) => Done(job, 12_000_000), encoders: new[] { "libx264" },
            freeSpace: _ => 50_000_000_000);

        var result = await rig.Service.CompressAsync(Clip(), 20_000_000, null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, rig.Jobs.Count);
    }

    // ---- stale statistics a crash left behind ----

    [Fact]
    public void The_sweep_removes_old_statistics_and_nothing_else()
    {
        Directory.CreateDirectory(StatsDir);
        const string hex = "0123456789abcdef0123456789abcdef";
        var oldLog = Path.Combine(StatsDir, $"wki_{hex}-0.log");
        var oldTree = Path.Combine(StatsDir, $"wki_{hex}-0.log.mbtree");
        var fresh = Path.Combine(StatsDir, "wki_fedcba9876543210fedcba9876543210-0.log");
        var foreign = Path.Combine(StatsDir, "notes.txt");
        var lookAlike = Path.Combine(StatsDir, "wki_other.log");
        foreach (var f in new[] { oldLog, oldTree, fresh, foreign, lookAlike }) File.WriteAllText(f, "x");
        var now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(oldLog, now.AddHours(-3));
        File.SetLastWriteTimeUtc(oldTree, now.AddHours(-3));
        File.SetLastWriteTimeUtc(foreign, now.AddHours(-3));
        File.SetLastWriteTimeUtc(lookAlike, now.AddHours(-3));
        File.SetLastWriteTimeUtc(fresh, now.AddMinutes(-1));   // a job may be writing it right now

        int removed = DiscordCompressService.SweepStaleStats(new[] { StatsDir, Path.Combine(Dir, "no-such-folder") }, now, TimeSpan.FromMinutes(10));

        Assert.Equal(2, removed);
        Assert.False(File.Exists(oldLog));
        Assert.False(File.Exists(oldTree));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(lookAlike));
    }

    // ---- hardware decoding is only given up when it was the cause ----

    private const string DecoderError = "[h264 @ 0000] Failed setup for format d3d11: hwaccel initialisation returned error.";
    private const string EncoderError = "[vost#0:0/h264_amf @ 0000] Error while opening encoder - maybe incorrect parameters";

    private static readonly FFmpegJobResult Ok = new(FFmpegJobStatus.Succeeded, null, 0, "", "");   // a job that wrote nothing we look at
    private static FFmpegJob Marker(bool hw) => new($"hw={hw}", "x.mp4", TimeSpan.FromSeconds(1), 0);

    private static IProgress<FFmpegProgress> NoProgress => new Progress<FFmpegProgress>();

    [Fact]
    public async Task An_encoder_failure_is_not_retried_and_does_not_switch_hardware_decoding_off()
    {
        var used = new List<string>();
        using var rig = Build(Settings(), (job, n) => { used.Add(job.Arguments); return Failed(1, EncoderError); });

        var first = await rig.Service.RunWithHwDecodeFallbackAsync(Marker, NoProgress, CancellationToken.None);
        var second = await rig.Service.RunWithHwDecodeFallbackAsync(Marker, NoProgress, CancellationToken.None);

        Assert.Equal(FFmpegJobStatus.Failed, first.Status);
        Assert.Equal(FFmpegJobStatus.Failed, second.Status);
        Assert.Equal(new[] { "hw=True", "hw=True" }, used);   // one run per call, both with hardware decoding
    }

    [Fact]
    public async Task A_decoder_failure_that_works_without_hardware_decoding_switches_it_off_for_later_jobs()
    {
        var used = new List<string>();
        using var rig = Build(Settings(), (job, n) =>
        {
            used.Add(job.Arguments);
            return job.Arguments == "hw=True" ? Failed(1, DecoderError) : Ok;
        });

        var first = await rig.Service.RunWithHwDecodeFallbackAsync(Marker, NoProgress, CancellationToken.None);
        var later = await rig.Service.RunWithHwDecodeFallbackAsync(Marker, NoProgress, CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.True(later.Succeeded);
        Assert.Equal(new[] { "hw=True", "hw=False", "hw=False" }, used);   // proven cause: later jobs start without it
    }

    [Fact]
    public async Task A_decoder_looking_failure_that_fails_without_hardware_decoding_too_changes_nothing()
    {
        var used = new List<string>();
        using var rig = Build(Settings(), (job, n) => { used.Add(job.Arguments); return Failed(1, DecoderError); });

        var first = await rig.Service.RunWithHwDecodeFallbackAsync(Marker, NoProgress, CancellationToken.None);
        var second = await rig.Service.RunWithHwDecodeFallbackAsync(Marker, NoProgress, CancellationToken.None);

        Assert.Equal(FFmpegJobStatus.Failed, first.Status);
        Assert.Equal(new[] { "hw=True", "hw=False", "hw=True", "hw=False" }, used);   // not proven: hardware decoding stays on
    }

    [Theory]
    [InlineData(FFmpegJobStatus.Cancelled, -1)]
    [InlineData(FFmpegJobStatus.Failed, -1)]    // never started / stalled / not enough disk: the runner's own verdicts
    [InlineData(FFmpegJobStatus.Failed, 0)]
    public async Task A_cancelled_or_never_started_job_is_not_retried(FFmpegJobStatus status, int exitCode)
    {
        int runs = 0;
        using var rig = Build(Settings(), (job, n) =>
        {
            runs++;
            return new FFmpegJobResult(status, null, exitCode, "x", DecoderError);   // even with decoder words in the tail
        });

        await rig.Service.RunWithHwDecodeFallbackAsync(Marker, NoProgress, CancellationToken.None);

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task The_retry_without_hardware_decoding_waits_for_its_turn()
    {
        var order = new List<string>();
        using var rig = Build(Settings(), (job, n) =>
        {
            order.Add(job.Arguments);
            return job.Arguments == "hw=True" ? Failed(1, DecoderError) : Ok;
        });

        await rig.Service.RunWithHwDecodeFallbackAsync(Marker, NoProgress, CancellationToken.None,
            beforeRetry: () => { order.Add("turn"); return Task.CompletedTask; });

        Assert.Equal(new[] { "hw=True", "turn", "hw=False" }, order);
    }
}
