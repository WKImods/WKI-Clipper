using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class StallRestartPolicyTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    [Fact]
    public void A_ring_that_ran_and_stalled_restarts_with_the_toast_at_once()
    {
        Assert.Equal(StallAction.Restart, StallRestartPolicy.Decide(true, false, 0, Grace, Grace));
        Assert.Equal(StallAction.Restart, StallRestartPolicy.Decide(true, true, 7, TimeSpan.FromHours(2), Grace));
    }

    [Fact]
    public void The_first_start_without_a_picture_on_an_awake_monitor_restarts_with_the_toast_after_the_grace()
    {
        Assert.Equal(StallAction.Wait, StallRestartPolicy.Decide(false, false, 0, Grace - TimeSpan.FromSeconds(1), Grace));
        Assert.Equal(StallAction.Restart, StallRestartPolicy.Decide(false, false, 0, Grace, Grace));
    }

    [Fact]
    public void A_sleeping_monitor_never_gets_the_toast()
    {
        Assert.Equal(StallAction.RestartQuietly, StallRestartPolicy.Decide(false, true, 0, Grace, Grace));
        Assert.Equal(StallAction.RestartQuietly, StallRestartPolicy.Decide(false, true, 3, TimeSpan.FromHours(1), Grace));
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(5, 480)]
    [InlineData(6, 600)]
    [InlineData(1000, 600)]
    public void Starts_in_a_row_without_a_picture_back_off_up_to_ten_minutes(int streak, int extraSeconds)
    {
        var due = Grace + TimeSpan.FromSeconds(extraSeconds);

        Assert.Equal(TimeSpan.FromSeconds(extraSeconds), StallRestartPolicy.NoPictureBackoff(streak));
        Assert.Equal(StallAction.Wait, StallRestartPolicy.Decide(false, false, streak, due - TimeSpan.FromSeconds(1), Grace));
        Assert.Equal(StallAction.RestartQuietly, StallRestartPolicy.Decide(false, false, streak, due, Grace));
    }

    /// <summary>The night of 2026-11-08: 222 restarts with a toast each in two hours of deep standby.</summary>
    [Fact]
    public void Two_hours_of_deep_standby_cost_a_handful_of_quiet_restarts_and_no_toast()
    {
        var watchdogTick = TimeSpan.FromSeconds(30);
        var night = TimeSpan.FromHours(2);
        int restarts = 0, toasts = 0, streak = 0;
        var elapsed = TimeSpan.Zero;
        var runStart = TimeSpan.Zero;
        while (elapsed < night)
        {
            elapsed += watchdogTick;
            var action = StallRestartPolicy.Decide(false, true, streak, elapsed - runStart, Grace);
            if (action == StallAction.Wait) continue;
            if (action == StallAction.Restart) toasts++;
            restarts++;
            streak++;
            runStart = elapsed;
        }

        Assert.Equal(0, toasts);
        Assert.InRange(restarts, 1, 20);
    }

    [Fact]
    public void A_monitor_listed_without_its_device_or_not_at_all_sleeps()
    {
        var topology = Topology(Display(@"\\.\DISPLAY1", null), Display(@"\\.\DISPLAY2", @"\\?\DISPLAY#GSM780B#7&2e952966&2&UID256"));

        Assert.True(StallRestartPolicy.IsAsleep(topology, @"\\.\DISPLAY1"));
        Assert.False(StallRestartPolicy.IsAsleep(topology, @"\\.\DISPLAY2"));
        Assert.False(StallRestartPolicy.IsAsleep(topology, @"\\.\display2"));
        Assert.True(StallRestartPolicy.IsAsleep(topology, @"\\.\DISPLAY3"));
        Assert.False(StallRestartPolicy.IsAsleep(topology, null));
        Assert.False(StallRestartPolicy.IsAsleep(topology, ""));
    }

    [Fact]
    public void A_disk_ring_counts_only_its_own_files_as_delivered()
    {
        var earlierRun = new[] { 41, 41, 41, 41 };

        Assert.False(StallRestartPolicy.DeliveredByRun(earlierRun, 42));                   // a warm restart that never delivered
        Assert.False(StallRestartPolicy.DeliveredByRun(earlierRun.Append(42), 42));        // only the open file
        Assert.True(StallRestartPolicy.DeliveredByRun(earlierRun.Append(42).Append(42), 42));
        Assert.False(StallRestartPolicy.DeliveredByRun(new[] { -1, -1, -1 }, 42));          // names it cannot read
    }

    private static DisplayInfo Display(string gdi, string? path)
        => new(1, gdi, path, "LG ULTRAGEAR+", 0, 0, 3440, 1440, true, 0, 0, "DP", 0x6D1E, 0x780B);

    private static DisplayTopology Topology(params DisplayInfo[] displays)
        => new(Array.Empty<DisplayAdapterInfo>(), displays, 0, null);
}

public sealed class VideoInputLostTests
{
    [Theory]
    [InlineData("[in#0/lavfi @ 000001c354519580] Error during demuxing: Generic error in an external library")]
    [InlineData("[in#0/lavfi @ 0000023eec179580] Error during demuxing: Invalid argument")]
    [InlineData("[in#0/rawvideo @ 0000023eec179580] Error during demuxing: I/O error")]
    public void The_video_input_failing_mid_run_is_seen(string line)
        => Assert.True(DisplayRestartPolicy.IsVideoInputLost(line));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[in#1/s16le @ 000001777ac7b1c0] Error during demuxing: I/O error")]          // the audio input
    [InlineData("[Parsed_ddagrab_0 @ 000001c354522580] AcquireNextFrame failed: 887a0026")]
    [InlineData("[Parsed_ddagrab_0 @ 000001c354522580] EOF timestamp not reliable")]
    [InlineData("[in#0 @ 0000024eb7fd9580] Error opening input: Generic error in an external library")]   // exits by itself
    public void Other_lines_are_not(string? line)
        => Assert.False(DisplayRestartPolicy.IsVideoInputLost(line));

    [Fact]
    public void A_start_refused_on_the_secure_desktop_counts_as_display_caused()
        => Assert.True(DisplayRestartPolicy.HasModeChangeMarker(
            "[Parsed_ddagrab_0 @ 01] Desktop duplication access denied\n[in#0 @ 02] Error opening input: Operation not permitted"));

}

/// <summary>
/// Ending a Ctrl+F9 part: its "+faststart" trailer rewrites the whole file (minutes after a long recording on a slow drive)
/// without a progress line. The kill comes only when ffmpeg stops reading, or at a cap sized to the file.
/// </summary>
public sealed class FinishWatchTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(5);

    [Theory]
    [InlineData(0L, 100)]                             // a minute + twice the 20 s floor
    [InlineData(4L * 1024 * 1024 * 1024, 469)]        // a minute + 2 x 4 GiB at 20 MiB/s
    [InlineData(11L * 1024 * 1024 * 1024, 1186)]      // an hour of recording: about 20 minutes at the very most
    public void The_cap_is_sized_to_the_part(long bytes, int seconds)
        => Assert.Equal(seconds, (int)FinishWatch.Cap(bytes).TotalSeconds);

    [Fact]
    public async Task An_ffmpeg_that_keeps_reading_is_not_killed_after_the_quiet_time()
    {
        ulong read = 0;
        int polls = 0;
        // Exits after ~60 polls (~300 ms), far beyond the 50 ms quiet time - it read all along.
        bool exited = await FinishWatch.WaitAsync(() => ++polls > 60, () => read += 4096,
                                                  TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(10), Poll);
        Assert.True(exited);
    }

    [Fact]
    public async Task An_ffmpeg_that_stops_reading_is_given_up_after_the_quiet_time()
    {
        var sw = Stopwatch.StartNew();
        bool exited = await FinishWatch.WaitAsync(() => false, () => 1234UL, TimeSpan.FromMilliseconds(100),
                                                  TimeSpan.FromSeconds(10), Poll);
        Assert.False(exited);
        Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task An_unknown_read_counter_never_counts_as_quiet_only_the_cap_ends_the_wait()
    {
        var sw = Stopwatch.StartNew();
        bool exited = await FinishWatch.WaitAsync(() => false, () => null, TimeSpan.FromMilliseconds(10),
                                                  TimeSpan.FromMilliseconds(300), Poll);
        Assert.False(exited);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(300), $"gave up after {sw.Elapsed}");
    }

    [Fact]
    public async Task A_reading_ffmpeg_still_ends_at_the_cap()
    {
        ulong read = 0;
        bool exited = await FinishWatch.WaitAsync(() => false, () => read += 1, TimeSpan.FromMilliseconds(50),
                                                  TimeSpan.FromMilliseconds(200), Poll);
        Assert.False(exited);
    }

    /// <summary>The real thing: "q" ends a recording-shaped ffmpeg cleanly, as a stop, without a kill.</summary>
    [Fact]
    public async Task Finish_ends_a_running_ffmpeg_cleanly_as_a_stop()
    {
        var ffmpeg = FFmpegTestSupport.Find();
        if (ffmpeg is null) return;
        using var service = new FFmpegService(ffmpeg);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Exited += (_, code) => exited.TrySetResult(code);
        service.Start("-hide_banner -nostats -re -f lavfi -i testsrc2=size=64x64:rate=10 -re -f lavfi -i anullsrc=r=48000:cl=stereo " +
                      "-map 0:v -map 1:a -c:v rawvideo -c:a pcm_s16le -f null -");
        await Task.Delay(TimeSpan.FromSeconds(1));

        await service.FinishAsync(FinishWatch.Cap(0), asStop: true);

        Assert.Same(exited.Task, await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.Equal(0, await exited.Task);
        Assert.True(service.StopRequested);
        Assert.False(service.Killed);
    }
}

public sealed class InputDesktopWaitTests
{
    [Theory]
    [InlineData("Default", "Default", true)]
    [InlineData("default", "Default", true)]
    [InlineData("Screen-saver", "Default", false)]     // a screen saver's desktop
    [InlineData("KeePassSecureDesktop", "Default", false)]
    [InlineData(null, "Default", true)]                // a name it cannot read never blocks a restart
    [InlineData("Default", null, true)]
    public void Only_the_clippers_own_desktop_counts_as_available(string? input, string? own, bool same)
        => Assert.Equal(same, InputDesktop.IsSameDesktop(input, own));

    /// <summary>The real P/Invoke reads the name (independent of whether the PC is locked while the tests run).</summary>
    [Fact]
    public void The_clipper_s_own_desktop_name_is_read()
        => Assert.Equal("Default", InputDesktop.OwnDesktopName(), ignoreCase: true);

    [Fact]
    public async Task An_available_desktop_does_not_wait()
    {
        int waits = 0;
        Assert.False(await DisplayRestartPolicy.WaitForInputDesktopAsync(() => true, TimeSpan.FromMilliseconds(10), () => waits++,
                                                                        CancellationToken.None));
        Assert.Equal(0, waits);
    }

    [Fact]
    public async Task A_locked_desktop_is_waited_for_and_announced_once()
    {
        int polls = 0, waits = 0;
        Assert.True(await DisplayRestartPolicy.WaitForInputDesktopAsync(() => ++polls >= 4, TimeSpan.FromMilliseconds(10),
                                                                       () => waits++, CancellationToken.None));
        Assert.Equal(4, polls);
        Assert.Equal(1, waits);
    }

    [Fact]
    public async Task A_cancelled_wait_ends()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DisplayRestartPolicy.WaitForInputDesktopAsync(() => false, TimeSpan.FromMilliseconds(10), null, cts.Token));
    }
}

/// <summary>
/// <see cref="FFmpegService.QuitAsync"/> with a real ffmpeg in the buffer's shape: a video input that ends and an audio input
/// that never does. ffmpeg keeps running on the audio alone (the 0.19.1 bug), and QuitAsync ends it as an exit of its own.
/// </summary>
public sealed class FFmpegQuitTests
{
    [Fact]
    public async Task Quit_ends_an_ffmpeg_that_runs_on_after_its_video_input_ended_without_marking_a_stop()
    {
        var ffmpeg = FFmpegTestSupport.Find();
        if (ffmpeg is null) return;

        using var service = new FFmpegService(ffmpeg);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Exited += (_, code) => exited.TrySetResult(code);
        // -re: real time like a capture, and no CPU burst that would starve the timing tests running alongside.
        service.Start("-hide_banner -nostats -re -f lavfi -i testsrc2=size=64x64:rate=10:duration=1 " +
                      "-re -f lavfi -i anullsrc=r=48000:cl=stereo -map 0:v -map 1:a -c:v rawvideo -c:a pcm_s16le -f null -");

        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.True(service.IsRunning, "ffmpeg should still run on its audio input after the video input ended");

        var sw = Stopwatch.StartNew();
        await service.QuitAsync(TimeSpan.FromSeconds(3));
        var done = await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(exited.Task, done);
        Assert.False(service.IsRunning);
        Assert.False(service.StopRequested);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"quit took {sw.Elapsed}");
    }
}
