using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.Native;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Finds an ffmpeg.exe for the integration tests; they skip (return) when there is none.</summary>
internal static class FFmpegTestSupport
{
    public static string? Find()
    {
        // 1) the bundled one next to the built app
        var bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "ffmpeg", "ffmpeg.exe");
        if (File.Exists(bundled)) return bundled;

        // 2) WinGet (Gyan.FFmpeg)
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrEmpty(local))
        {
            var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            try
            {
                if (Directory.Exists(packages))
                {
                    foreach (var pkg in Directory.EnumerateDirectories(packages, "Gyan.FFmpeg*"))
                    {
                        var hit = Directory.EnumerateFiles(pkg, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                        if (hit != null) return hit;
                    }
                }
            }
            catch { /* unreadable → fall through to PATH */ }
        }

        // 3) PATH
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir, "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }
}

internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _onReport;
    public SyncProgress(Action<T> onReport) => _onReport = onReport;
    public void Report(T value) => _onReport(value);
}

/// <summary>Everything about the runner that needs no ffmpeg.</summary>
public sealed class FFmpegJobRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_jobs_" + Guid.NewGuid().ToString("N"));

    public FFmpegJobRunnerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // A real file standing in for ffmpeg.exe: it is never started in these tests.
    private static string SomeExistingFile => typeof(FFmpegJobRunnerTests).Assembly.Location;

    private string PartsFile => Path.Combine(_dir, "parts.txt");

    private FFmpegJobRunner NewRunner(string? ffmpeg = null, Func<string, long?>? free = null)
        => new(ffmpeg ?? SomeExistingFile, free, PartsFile);

    [Fact]
    public void Runner_arguments_start_with_the_owned_flags_and_cap_the_threads_before_the_output()
    {
        var job = new FFmpegJob("-i in.mp4 -f mp4 \"C:\\o\\x.mp4.part\"", @"C:\o\x.mp4", TimeSpan.FromSeconds(5), 1000, ThreadCap: 2);
        var args = FFmpegJobRunner.BuildArguments(job);

        Assert.StartsWith("-hide_banner -nostats -n -progress pipe:1 ", args);
        Assert.EndsWith("-f mp4 -threads 2 \"C:\\o\\x.mp4.part\"", args);
        Assert.DoesNotContain("-nostdin", args);   // cancel needs stdin for "q"
    }

    [Fact]
    public void A_callers_own_overwrite_flags_never_reach_ffmpeg_next_to_the_runners()
    {
        // ffmpeg: "-n ... -y" → "Error opening output files: Invalid argument" for every job.
        var job = new FFmpegJob("-y -i in.mp4 -n -f mp4 -y \"C:\\o\\x.mp4.part\"", @"C:\o\x.mp4", TimeSpan.FromSeconds(5), 1000);
        var args = FFmpegJobRunner.BuildArguments(job);

        var tokens = args.Split(' ');
        Assert.DoesNotContain("-y", tokens);
        Assert.Single(tokens, t => t == "-n");   // exactly the runner's own
        Assert.EndsWith("-i in.mp4 -f mp4 \"C:\\o\\x.mp4.part\"", args);
    }

    [Fact]
    public void Stall_timeout_defaults_to_60_seconds_and_is_configurable_per_job()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), new FFmpegJob("", "x", TimeSpan.Zero, 0).EffectiveStallTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), new FFmpegJob("", "x", TimeSpan.Zero, 0, StallTimeout: TimeSpan.FromSeconds(5)).EffectiveStallTimeout);
        Assert.Equal(TimeSpan.Zero, new FFmpegJob("", "x", TimeSpan.Zero, 0, StallTimeout: TimeSpan.Zero).EffectiveStallTimeout);
    }

    // ---- disposed runner ----

    [Fact]
    public async Task A_disposed_runner_starts_nothing()
    {
        var runner = NewRunner(free: _ => long.MaxValue);
        runner.Dispose();
        var final = Path.Combine(_dir, "after", "x.mp4");

        var result = await runner.RunAsync(new FFmpegJob("-i a b", final, TimeSpan.FromSeconds(1), 1000), null, CancellationToken.None);

        Assert.Equal(FFmpegJobStatus.Cancelled, result.Status);
        Assert.False(Directory.Exists(Path.GetDirectoryName(final)));
        Assert.Null(await runner.ProbeAsync(SomeExistingFile));
        runner.Dispose();   // twice is fine
    }

    // ---- finished file must reach its final name (virus scanner holding it) ----

    private string MakePart(string content = "finished encode")
    {
        var part = Path.Combine(_dir, "done.mp4.part");
        File.WriteAllText(part, content);
        return part;
    }

    [Fact]
    public async Task Rename_retries_both_io_and_access_denied_errors_with_backoff()
    {
        var part = MakePart();
        var final = Path.Combine(_dir, "done.mp4");
        var waits = new List<TimeSpan>();
        int attempts = 0;

        var result = await FFmpegJobRunner.FinishFileAsync(part, final,
            move: (s, d) =>
            {
                attempts++;
                if (attempts == 1) throw new UnauthorizedAccessException("scanner has it");
                if (attempts == 2) throw new IOException("sharing violation");
                File.Move(s, d);
            },
            delay: span => { waits.Add(span); return Task.CompletedTask; });

        Assert.Equal(final, result);
        Assert.Equal("finished encode", File.ReadAllText(final));
        Assert.False(File.Exists(part));
        Assert.Equal(2, waits.Count);
        Assert.True(waits[1] > waits[0]);   // backs off
    }

    [Fact]
    public async Task Rename_keeps_trying_for_about_ten_seconds_before_it_falls_back_to_copying()
    {
        var part = MakePart();
        var final = Path.Combine(_dir, "done.mp4");
        var waits = new List<TimeSpan>();
        int copies = 0;

        var result = await FFmpegJobRunner.FinishFileAsync(part, final,
            move: (_, _) => throw new UnauthorizedAccessException("denied"),
            copy: (s, d) => { copies++; File.Copy(s, d); },
            delay: span => { waits.Add(span); return Task.CompletedTask; });

        Assert.Equal(final, result);
        Assert.Equal(1, copies);
        Assert.Equal("finished encode", File.ReadAllText(final));
        Assert.False(File.Exists(part));   // the copy is the result, the part is cleaned up
        var total = waits.Aggregate(TimeSpan.Zero, (a, b) => a + b);
        Assert.InRange(total.TotalSeconds, 9, 12);
    }

    [Fact]
    public async Task When_neither_rename_nor_copy_works_the_finished_file_is_kept_and_the_error_is_thrown()
    {
        var part = MakePart();
        var final = Path.Combine(_dir, "done.mp4");

        await Assert.ThrowsAsync<IOException>(() => FFmpegJobRunner.FinishFileAsync(part, final,
            move: (_, _) => throw new IOException("locked"),
            copy: (_, _) => throw new UnauthorizedAccessException("denied"),
            delay: _ => Task.CompletedTask));

        Assert.True(File.Exists(part));
        Assert.Equal("finished encode", File.ReadAllText(part));
        Assert.False(File.Exists(final));
    }

    [Fact]
    public async Task A_name_taken_in_the_meantime_moves_on_to_the_next_number_without_waiting()
    {
        var part = MakePart();
        var final = Path.Combine(_dir, "done.mp4");
        var waits = new List<TimeSpan>();
        bool first = true;

        var result = await FFmpegJobRunner.FinishFileAsync(part, final,
            move: (s, d) =>
            {
                if (first) { first = false; File.WriteAllText(d, "someone else won the name"); throw new IOException("exists"); }
                File.Move(s, d);
            },
            delay: span => { waits.Add(span); return Task.CompletedTask; });

        Assert.Equal(Path.Combine(_dir, "done (2).mp4"), result);
        Assert.Equal("someone else won the name", File.ReadAllText(final));
        Assert.Empty(waits);
    }

    [Fact]
    public void Part_path_is_the_final_path_plus_part()
    {
        Assert.Equal(@"C:\o\x.mp4.part", new FFmpegJob("", @"C:\o\x.mp4", TimeSpan.Zero, 0).PartPath);
    }

    [Fact]
    public async Task Missing_ffmpeg_fails_with_a_message_and_creates_nothing()
    {
        using var runner = NewRunner(ffmpeg: Path.Combine(_dir, "no_ffmpeg_here.exe"));
        var final = Path.Combine(_dir, "out", "x.mp4");

        var result = await runner.RunAsync(new FFmpegJob("-i a b", final, TimeSpan.FromSeconds(1), 1000), null, CancellationToken.None);

        Assert.Equal(FFmpegJobStatus.Failed, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.False(File.Exists(final));
        Assert.False(File.Exists(final + ".part"));
    }

    [Fact]
    public async Task Too_little_disk_space_fails_before_ffmpeg_starts()
    {
        using var runner = NewRunner(free: _ => 100L * 1024 * 1024);   // 100 MB free
        var final = Path.Combine(_dir, "x.mp4");

        var result = await runner.RunAsync(new FFmpegJob("-i a b", final, TimeSpan.FromSeconds(1), 50L * 1024 * 1024), null, CancellationToken.None);

        Assert.Equal(FFmpegJobStatus.Failed, result.Status);
        Assert.Equal(-1, result.ExitCode);   // never ran
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.Equal("", result.StderrTail);
    }

    [Fact]
    public async Task Already_cancelled_token_returns_cancelled_without_touching_the_disk()
    {
        using var runner = NewRunner(free: _ => long.MaxValue);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var final = Path.Combine(_dir, "sub", "x.mp4");

        var result = await runner.RunAsync(new FFmpegJob("-i a b", final, TimeSpan.FromSeconds(1), 1000), null, cts.Token);

        Assert.Equal(FFmpegJobStatus.Cancelled, result.Status);
        Assert.False(Directory.Exists(Path.GetDirectoryName(final)));
    }

    [Fact]
    public void Cleanup_removes_only_recorded_stale_parts_never_foreign_files_in_the_same_folder()
    {
        var clips = Path.Combine(_dir, "clips");
        Directory.CreateDirectory(clips);

        var now = DateTime.UtcNow;
        string Make(string name, TimeSpan age)
        {
            var p = Path.Combine(clips, name);
            File.WriteAllText(p, "x");
            File.SetLastWriteTimeUtc(p, now - age);
            return p;
        }
        var staleCrashed = Make("old.mp4.part", TimeSpan.FromMinutes(30));
        var freshCrashed = Make("fresh.mp4.part", TimeSpan.FromMinutes(2));
        var browserDownload = Make("video.mp4.part", TimeSpan.FromDays(30));   // old, right suffix, NOT ours
        var otherDownload = Make("setup.part", TimeSpan.FromDays(30));
        var finished = Make("done.mp4", TimeSpan.FromDays(30));
        var vanished = Path.Combine(clips, "vanished.mp4.part");
        File.WriteAllLines(PartsFile, new[] { staleCrashed, freshCrashed, vanished });

        using var runner = NewRunner();
        Assert.Equal(1, runner.CleanupStaleParts(now));

        Assert.False(File.Exists(staleCrashed));
        Assert.True(File.Exists(freshCrashed));       // may still be written
        Assert.True(File.Exists(browserDownload));    // the Firefox case
        Assert.True(File.Exists(otherDownload));
        Assert.True(File.Exists(finished));
        // the list keeps what may still become stale, and drops what is gone
        Assert.Equal(new[] { freshCrashed }, File.ReadAllLines(PartsFile));
    }

    [Fact]
    public void Cleanup_never_deletes_a_file_that_is_not_called_part_even_if_the_list_says_so()
    {
        var precious = Path.Combine(_dir, "precious.mp4");
        File.WriteAllText(precious, "x");
        File.SetLastWriteTimeUtc(precious, DateTime.UtcNow.AddDays(-30));
        File.WriteAllLines(PartsFile, new[] { precious });

        using var runner = NewRunner();
        Assert.Equal(0, runner.CleanupStaleParts());
        Assert.True(File.Exists(precious));
    }

    [Fact]
    public void Cleanup_with_nothing_recorded_is_a_no_op()
    {
        using var runner = NewRunner();
        Assert.Equal(0, runner.CleanupStaleParts());
        Assert.False(File.Exists(PartsFile) && File.ReadAllLines(PartsFile).Length > 0);
    }

    [Fact]
    public async Task Disposing_the_job_object_kills_the_processes_in_it()
    {
        using var job = KillOnCloseJob.TryCreate();
        Assert.NotNull(job);

        using var sleeper = Process.Start(new ProcessStartInfo
        {
            FileName = "ping.exe",
            Arguments = "-n 60 127.0.0.1",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        })!;
        try
        {
            Assert.True(job!.TryAssign(sleeper));
            Assert.False(sleeper.HasExited);

            job.Dispose();   // what happens when the clipper dies

            Assert.True(await Task.Run(() => sleeper.WaitForExit(5000)), "the process survived the job object");
        }
        finally
        {
            try { if (!sleeper.HasExited) sleeper.Kill(); } catch { }
        }
    }
}

/// <summary>
/// Real ffmpeg, real files. Skips (returns) when no ffmpeg.exe can be found, so the suite stays
/// green on a machine without one.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FFmpegJobRunnerIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_jobs_it_" + Guid.NewGuid().ToString("N"));
    private readonly string? _ffmpeg = FFmpegTestSupport.Find();

    public FFmpegJobRunnerIntegrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string PartsFile => Path.Combine(_dir, "parts.txt");

    private FFmpegJobRunner NewRunner() => new(_ffmpeg!, null, PartsFile);

    private string[] RecordedParts() => File.Exists(PartsFile) ? File.ReadAllLines(PartsFile) : Array.Empty<string>();

    /// <summary>A synthetic clip. Seconds is the length; realtime paces it like a live encode.</summary>
    private static FFmpegJob TestClip(string finalPath, int seconds, bool realtime = false, int? threads = null)
    {
        var part = FFmpegJob.PartPathFor(finalPath);
        var pace = realtime ? "-re " : "";
        var args = $"{pace}-f lavfi -i testsrc2=size=640x360:rate=30 {pace}-f lavfi -i sine=frequency=440 -t {seconds} "
                 + $"-c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -f mp4 \"{part}\"";
        return new FFmpegJob(args, finalPath, TimeSpan.FromSeconds(seconds), 2_000_000, threads);
    }

    [Fact]
    public async Task A_finished_job_renames_part_to_final_reports_full_progress_and_probes_correctly()
    {
        if (_ffmpeg is null) return;
        using var runner = NewRunner();
        var final = Path.Combine(_dir, "clips", "five.mp4");

        var reports = new List<FFmpegProgress>();
        var result = await runner.RunAsync(TestClip(final, 5), new SyncProgress<FFmpegProgress>(p => { lock (reports) reports.Add(p); }),
                                           CancellationToken.None);

        Assert.True(result.Succeeded, result.Message + Environment.NewLine + result.StderrTail);
        Assert.Equal(final, result.OutputPath);
        Assert.True(File.Exists(final));
        Assert.False(File.Exists(final + ".part"));

        lock (reports)
        {
            Assert.NotEmpty(reports);
            Assert.True(reports.Max(r => r.Fraction) >= 0.99, "progress never got near 1");
            Assert.Equal(1.0, reports[^1].Fraction, 3);
            // never goes backwards
            for (int i = 1; i < reports.Count; i++) Assert.True(reports[i].Fraction >= reports[i - 1].Fraction);
        }

        var probe = await runner.ProbeAsync(final);
        Assert.NotNull(probe);
        Assert.Equal(5.0, probe!.Duration!.Value.TotalSeconds, 1);
        Assert.Equal("h264", probe.VideoCodec);
        Assert.Equal(640, probe.Width);
        Assert.Equal(360, probe.Height);
        Assert.Equal(1, probe.AudioStreamCount);
    }

    [Fact]
    public async Task A_taken_final_name_gets_a_suffix_and_the_existing_file_stays_untouched()
    {
        if (_ffmpeg is null) return;
        using var runner = NewRunner();
        var final = Path.Combine(_dir, "taken.mp4");
        File.WriteAllText(final, "precious");

        var result = await runner.RunAsync(TestClip(final, 1), null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message + result.StderrTail);
        Assert.Equal(Path.Combine(_dir, "taken (2).mp4"), result.OutputPath);
        Assert.Equal("precious", File.ReadAllText(final));
    }

    [Fact]
    public async Task A_thread_capped_job_still_works()
    {
        if (_ffmpeg is null) return;
        using var runner = NewRunner();
        var final = Path.Combine(_dir, "capped.mp4");

        var result = await runner.RunAsync(TestClip(final, 1, threads: 1), null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message + result.StderrTail);
        Assert.True(File.Exists(final));
    }

    [Fact]
    public async Task A_cancelled_job_leaves_neither_output_nor_part()
    {
        if (_ffmpeg is null) return;
        using var runner = NewRunner();
        var final = Path.Combine(_dir, "cancelled.mp4");
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        // 10 minutes paced in real time: it cannot finish by itself.
        var task = runner.RunAsync(TestClip(final, 600, realtime: true),
                                   new SyncProgress<FFmpegProgress>(_ => running.TrySetResult()), cts.Token);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var watch = Stopwatch.StartNew();
        cts.Cancel();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(FFmpegJobStatus.Cancelled, result.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "cancel took " + watch.Elapsed);
        Assert.False(File.Exists(final));
        Assert.False(File.Exists(final + ".part"));
    }

    [Fact]
    public async Task A_failing_job_returns_the_stderr_tail_and_leaves_nothing_behind()
    {
        if (_ffmpeg is null) return;
        using var runner = NewRunner();
        var final = Path.Combine(_dir, "bad.mp4");
        var args = $"-this_option_does_not_exist 1 -f mp4 \"{FFmpegJob.PartPathFor(final)}\"";

        var result = await runner.RunAsync(new FFmpegJob(args, final, TimeSpan.FromSeconds(1), 1000), null, CancellationToken.None);

        Assert.Equal(FFmpegJobStatus.Failed, result.Status);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("this_option_does_not_exist", result.StderrTail, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(final));
        Assert.False(File.Exists(final + ".part"));
        Assert.Empty(RecordedParts());   // an ended job is never swept later
    }

    [Fact]
    public async Task A_callers_own_dash_y_does_not_break_the_job()
    {
        if (_ffmpeg is null) return;
        using var runner = NewRunner();
        var final = Path.Combine(_dir, "with-y.mp4");
        var job = TestClip(final, 1);

        // -n (runner) + -y (caller) used to make ffmpeg refuse every job: exit -22, "Invalid argument"
        var withY = job with { Arguments = "-y " + job.Arguments };
        var result = await runner.RunAsync(withY, null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message + Environment.NewLine + result.StderrTail);
        Assert.True(File.Exists(final));

        var withN = job with { Arguments = "-n " + job.Arguments, FinalPath = Path.Combine(_dir, "with-n.mp4") };
        withN = withN with { Arguments = withN.Arguments.Replace(job.PartPath, withN.PartPath) };
        var result2 = await runner.RunAsync(withN, null, CancellationToken.None);
        Assert.True(result2.Succeeded, result2.Message + Environment.NewLine + result2.StderrTail);
    }

    [Fact]
    public async Task The_part_path_is_recorded_while_the_job_runs_and_forgotten_when_it_ends()
    {
        if (_ffmpeg is null) return;
        using var runner = NewRunner();
        var final = Path.Combine(_dir, "tracked.mp4");
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var task = runner.RunAsync(TestClip(final, 600, realtime: true),
                                   new SyncProgress<FFmpegProgress>(_ => running.TrySetResult()), cts.Token);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(new[] { final + ".part" }, RecordedParts());   // exactly this file, not the folder

        cts.Cancel();
        await task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(RecordedParts());
    }

    [Fact]
    public async Task A_finished_job_is_forgotten_too()
    {
        if (_ffmpeg is null) return;
        using var runner = NewRunner();
        var result = await runner.RunAsync(TestClip(Path.Combine(_dir, "ok.mp4"), 1), null, CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Empty(RecordedParts());
    }

    // ---- hung jobs, CancelAll, Dispose ----

    [Fact]
    public async Task A_hung_job_is_terminated_by_the_stall_watchdog_and_the_queue_moves_on()
    {
        if (_ffmpeg is null) return;
        using var runner = NewRunner();
        var hungFinal = Path.Combine(_dir, "hung.mp4");
        // Reads its input from stdin, which nobody feeds: never produces a frame, never advances.
        var hung = new FFmpegJob($"-i pipe:0 -f mp4 \"{FFmpegJob.PartPathFor(hungFinal)}\"", hungFinal,
                                 TimeSpan.FromSeconds(10), 1000, StallTimeout: TimeSpan.FromSeconds(1));
        var next = runner.RunAsync(TestClip(Path.Combine(_dir, "after-hung.mp4"), 1), null, CancellationToken.None);

        var watch = Stopwatch.StartNew();
        var result = await runner.RunAsync(hung, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(FFmpegJobStatus.Failed, result.Status);
        Assert.Contains("1", result.Message);   // names the stall duration
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "took " + watch.Elapsed);
        Assert.False(File.Exists(hungFinal));
        Assert.False(File.Exists(hungFinal + ".part"));
        Assert.Empty(RecordedParts());
        Assert.True((await next.WaitAsync(TimeSpan.FromSeconds(30))).Succeeded);   // not blocked behind the hung job
    }

    [Fact]
    public async Task A_healthy_slow_job_is_not_mistaken_for_a_hung_one()
    {
        if (_ffmpeg is null) return;
        using var runner = NewRunner();
        // paced in real time for 3 s; the watchdog allows only 1.5 s without progress
        var job = TestClip(Path.Combine(_dir, "slow.mp4"), 3, realtime: true) with { StallTimeout = TimeSpan.FromSeconds(1.5) };

        var result = await runner.RunAsync(job, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(result.Succeeded, result.Message + result.StderrTail);
    }

    [Fact]
    public async Task CancelAll_stops_the_running_job_and_everything_queued_but_not_later_jobs()
    {
        if (_ffmpeg is null) return;
        using var runner = NewRunner();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = runner.RunAsync(TestClip(Path.Combine(_dir, "c1.mp4"), 600, realtime: true),
                                    new SyncProgress<FFmpegProgress>(_ => running.TrySetResult()), CancellationToken.None);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var queued = runner.RunAsync(TestClip(Path.Combine(_dir, "c2.mp4"), 1), null, CancellationToken.None);

        runner.CancelAll();

        Assert.Equal(FFmpegJobStatus.Cancelled, (await first.WaitAsync(TimeSpan.FromSeconds(30))).Status);
        Assert.Equal(FFmpegJobStatus.Cancelled, (await queued.WaitAsync(TimeSpan.FromSeconds(30))).Status);
        Assert.False(File.Exists(Path.Combine(_dir, "c2.mp4.part")));

        var later = await runner.RunAsync(TestClip(Path.Combine(_dir, "c3.mp4"), 1), null, CancellationToken.None);
        Assert.True(later.Succeeded, later.Message + later.StderrTail);
    }

    [Fact]
    public async Task Dispose_ends_the_running_job_and_never_starts_the_queued_ones()
    {
        if (_ffmpeg is null) return;
        var runner = NewRunner();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = runner.RunAsync(TestClip(Path.Combine(_dir, "d1.mp4"), 600, realtime: true),
                                    new SyncProgress<FFmpegProgress>(_ => running.TrySetResult()), CancellationToken.None);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var second = runner.RunAsync(TestClip(Path.Combine(_dir, "d2.mp4"), 1), null, CancellationToken.None);
        var third = runner.RunAsync(TestClip(Path.Combine(_dir, "d3.mp4"), 1), null, CancellationToken.None);

        runner.Dispose();   // what the app does on exit

        var results = await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.All(results, r => Assert.NotEqual(FFmpegJobStatus.Succeeded, r.Status));
        Assert.Equal(FFmpegJobStatus.Cancelled, results[1].Status);
        Assert.Equal(FFmpegJobStatus.Cancelled, results[2].Status);
        Assert.False(File.Exists(Path.Combine(_dir, "d2.mp4.part")));   // never started
        Assert.False(File.Exists(Path.Combine(_dir, "d3.mp4.part")));
        Assert.False(File.Exists(Path.Combine(_dir, "d1.mp4")));
    }

    [Fact]
    public async Task Jobs_run_one_at_a_time_in_order()
    {
        if (_ffmpeg is null) return;
        using var runner = NewRunner();
        long firstEndedAt = 0, secondStartedAt = 0;

        var first = runner.RunAsync(TestClip(Path.Combine(_dir, "a.mp4"), 2, realtime: true), null, CancellationToken.None)
                          .ContinueWith(t => { Interlocked.Exchange(ref firstEndedAt, Stopwatch.GetTimestamp()); return t.Result; });
        var second = runner.RunAsync(TestClip(Path.Combine(_dir, "b.mp4"), 1),
                                     new SyncProgress<FFmpegProgress>(_ => Interlocked.CompareExchange(ref secondStartedAt, Stopwatch.GetTimestamp(), 0)),
                                     CancellationToken.None);

        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.All(results, r => Assert.True(r.Succeeded, r.Message + r.StderrTail));
        Assert.True(secondStartedAt != 0 && secondStartedAt >= firstEndedAt, "the second job started before the first one ended");
    }

    [Fact]
    public async Task A_job_cancelled_while_queued_never_starts_and_does_not_let_the_next_one_overtake()
    {
        if (_ffmpeg is null) return;
        using var runner = NewRunner();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ctsFirst = new CancellationTokenSource();
        using var ctsQueued = new CancellationTokenSource();

        var first = runner.RunAsync(TestClip(Path.Combine(_dir, "first.mp4"), 600, realtime: true),
                                    new SyncProgress<FFmpegProgress>(_ => running.TrySetResult()), ctsFirst.Token);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var queued = runner.RunAsync(TestClip(Path.Combine(_dir, "queued.mp4"), 1), null, ctsQueued.Token);
        var third = runner.RunAsync(TestClip(Path.Combine(_dir, "third.mp4"), 1), null, CancellationToken.None);

        ctsQueued.Cancel();
        var queuedResult = await queued.WaitAsync(TimeSpan.FromSeconds(5));   // prompt, although the first job still runs
        Assert.Equal(FFmpegJobStatus.Cancelled, queuedResult.Status);
        Assert.False(File.Exists(Path.Combine(_dir, "queued.mp4")));

        await Task.Delay(1500);
        Assert.False(third.IsCompleted, "the third job overtook the still-running first one");

        ctsFirst.Cancel();
        Assert.Equal(FFmpegJobStatus.Cancelled, (await first.WaitAsync(TimeSpan.FromSeconds(30))).Status);
        Assert.True((await third.WaitAsync(TimeSpan.FromSeconds(30))).Succeeded);
    }

    [Fact]
    public async Task Probe_of_a_non_media_file_is_null()
    {
        if (_ffmpeg is null) return;
        using var runner = NewRunner();
        var text = Path.Combine(_dir, "notes.mp4");
        File.WriteAllText(text, "this is not a video");

        Assert.Null(await runner.ProbeAsync(text));
        Assert.Null(await runner.ProbeAsync(Path.Combine(_dir, "missing.mp4")));
    }
}
