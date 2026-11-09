using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Win32;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;
using CaptureSource = WKI_Clipper.Services.CaptureSource;

namespace WKI_Clipper.Tests;

/// <summary>
/// The buffer and the monitor it records: segment source keys from the display identity, the session reset across warm
/// restarts, retarget, the fallback notice and the automatic restart after a display-caused ffmpeg exit (R2, R8).
/// </summary>
public sealed class ReplayBufferMonitorTests
{
    private const string ModeChangeStderr =
        "frame= 1234 fps= 60 q=-0.0 size=N/A time=00:00:20.56\n" +
        "[Parsed_ddagrab_0 @ 0000020b1c3e4f40] Output parameters changed!\n" +
        "[out#0/segment @ 0000020b] Conversion failed!";

    private const string GenericStderr =
        "frame= 1234 fps= 60 q=-0.0 size=N/A time=00:00:20.56\n" +
        "[h264_amf @ 0000020b] Encoder failed\n" +
        "[out#0/segment @ 0000020b] Conversion failed!";

    private static CaptureSource SourceOf(DisplayInfo d, DisplayTopology topo)
        => CaptureSourceRules.ForDisplay(d, topo, MonitorTargetRules.Resolve(MonitorRef.Of(d), topo))!;

    private static string KeyOf(CaptureSource s, ResolutionPreset preset = ResolutionPreset.Native)
        => SegmentIdentity.SourceKey(false, IntPtr.Zero, s.Width, s.Height, s.DisplayKey, preset);

    private static CaptureTargetResolver.CapturePlan PlanOf(CaptureSource s, string? warning = null, bool wgc = false,
        nint hwnd = 0)
        => new(s, SystemAudioMode.AllAudio, null, null, hwnd, s.Label, "Alle Sounds", wgc, warning);

    // ---- source key ----

    [Fact]
    public void Same_monitor_same_size_keeps_the_source_key_across_restarts()
    {
        var dell = TestDisplays.Dell(output: 1);
        var before = SourceOf(dell, TestDisplays.Topology(TestDisplays.Lg(), dell));
        // The DELL became the primary: DXGI renumbers it to output 0 and Windows handed out the GDI slots anew.
        var dellPrimary = TestDisplays.Dell(output: 0, gdi: @"\\.\DISPLAY1", x: 0, primary: true);
        var lgSecondary = TestDisplays.Lg(output: 1, gdi: @"\\.\DISPLAY2", x: -3440, primary: false);
        var after = SourceOf(dellPrimary, TestDisplays.Topology(lgSecondary, dellPrimary));

        Assert.NotEqual(before.OutputIndex, after.OutputIndex);
        Assert.Equal(KeyOf(before), KeyOf(after));
        Assert.Equal($"mon:{dell.DevicePath!.ToLowerInvariant()}:2560x1440", KeyOf(after));
    }

    [Fact]
    public void Other_monitor_same_output_and_size_gets_another_source_key()
    {
        // Two identical DELLs: the one at output 1 is swapped for its twin - same model, size and output, another device.
        var first = TestDisplays.Dell(uid: 257, output: 1);
        var twin = TestDisplays.Dell(uid: 258, output: 1);
        var a = SourceOf(first, TestDisplays.Topology(TestDisplays.Lg(), first));
        var b = SourceOf(twin, TestDisplays.Topology(TestDisplays.Lg(), twin));

        Assert.Equal(a.OutputIndex, b.OutputIndex);
        Assert.NotEqual(KeyOf(a), KeyOf(b));
    }

    [Fact]
    public void Preset_mode_change_of_the_same_monitor_keeps_the_source_key()
    {
        // R4: 4K monitor, an exclusive-fullscreen game switches it to 1080p, the clipper records 1440p. Every generation is
        // encoded at 2560x1440, so the footage before and after the switch is one history and one clip.
        var uhd = TestDisplays.Lg(w: 3840, h: 2160);
        var fullHd = TestDisplays.Lg(w: 1920, h: 1080);
        var at4k = SourceOf(uhd, TestDisplays.Topology(uhd, TestDisplays.Dell()));
        var at1080 = SourceOf(fullHd, TestDisplays.Topology(fullHd, TestDisplays.Dell()));
        var dell = SourceOf(TestDisplays.Dell(), TestDisplays.Topology(fullHd, TestDisplays.Dell()));

        Assert.Equal(KeyOf(at4k, ResolutionPreset.WQHD), KeyOf(at1080, ResolutionPreset.WQHD));
        Assert.NotEqual(KeyOf(at1080, ResolutionPreset.WQHD), KeyOf(dell, ResolutionPreset.WQHD));   // another monitor never joins
        Assert.NotEqual(KeyOf(at4k), KeyOf(at1080));                                                // Native: the size follows the source (R5)
    }

    [Fact]
    public void Native_size_change_gets_another_source_key()
    {
        var at1440 = SourceOf(TestDisplays.Dell(), TestDisplays.Topology(TestDisplays.Lg(), TestDisplays.Dell()));
        var small = TestDisplays.Dell(w: 1920, h: 1080);
        var at1080 = SourceOf(small, TestDisplays.Topology(TestDisplays.Lg(), small));

        Assert.True(CaptureSourceRules.SameMonitor(at1440, at1080));
        Assert.NotEqual(KeyOf(at1440), KeyOf(at1080));
        Assert.EndsWith(":1920x1080", KeyOf(at1080));
    }

    // ---- session ----

    [Fact]
    public async Task Session_resets_when_the_monitor_changes_across_a_warm_restart()
    {
        var topo = TestDisplays.Topology(TestDisplays.Lg(), TestDisplays.Dell());
        var dell = SourceOf(TestDisplays.Dell(), topo);
        var lg = SourceOf(TestDisplays.Lg(), topo);
        using var env = new BufferEnv();
        env.SetLastStartedSource(dell);

        // A warm restart stops first - which empties CurrentPlan - and only then starts and decides.
        await env.Buffer.StopAsync();

        Assert.Null(env.Buffer.CurrentPlan);
        Assert.Same(dell, env.Buffer.LastStartedSource);
        Assert.True(SessionRules.ShouldResetSession(env.Buffer.LastStartedSource, lg, folderMoved: false));
    }

    [Fact]
    public void Session_is_kept_when_only_the_mode_of_the_same_monitor_changes()
    {
        var uhd = TestDisplays.Lg(w: 3840, h: 2160);
        var fullHd = TestDisplays.Lg(w: 1920, h: 1080);
        var at4k = SourceOf(uhd, TestDisplays.Topology(uhd));
        var at1080 = SourceOf(fullHd, TestDisplays.Topology(fullHd));

        Assert.False(SessionRules.ShouldResetSession(at4k, at1080, folderMoved: false));
        Assert.False(SessionRules.ShouldResetSession(null, at1080, folderMoved: false));   // nothing started before
        Assert.True(SessionRules.ShouldResetSession(at4k, at1080, folderMoved: true));      // a moved folder starts over
        Assert.True(SessionRules.ShouldResetSession(null, at1080, folderMoved: true));
    }

    [Fact]
    public void Retarget_restarts_video_when_the_source_changes()
    {
        var topo = TestDisplays.Topology(TestDisplays.Lg(), TestDisplays.Dell());
        var lg = SourceOf(TestDisplays.Lg(), topo);
        var lgAgain = SourceOf(TestDisplays.Lg(), topo);
        var dell = SourceOf(TestDisplays.Dell(), topo);
        var smallLg = TestDisplays.Lg(w: 2560, h: 1080);
        var lgOtherMode = SourceOf(smallLg, TestDisplays.Topology(smallLg, TestDisplays.Dell()));
        var lgRenumbered = lg with { OutputIndex = 1 };

        Assert.True(SessionRules.KeepsVideo(PlanOf(lg), PlanOf(lgAgain)));
        Assert.False(SessionRules.KeepsVideo(PlanOf(lg), PlanOf(dell)));
        Assert.False(SessionRules.KeepsVideo(PlanOf(lg), PlanOf(lgOtherMode)));
        Assert.False(SessionRules.KeepsVideo(PlanOf(lg), PlanOf(lgRenumbered)));
        // WGC: another window needs a new video pipeline, and so does the switch between WGC and ddagrab.
        Assert.True(SessionRules.KeepsVideo(PlanOf(lg, wgc: true, hwnd: 100), PlanOf(lgAgain, wgc: true, hwnd: 100)));
        Assert.False(SessionRules.KeepsVideo(PlanOf(lg, wgc: true, hwnd: 100), PlanOf(lgAgain, wgc: true, hwnd: 200)));
        Assert.False(SessionRules.KeepsVideo(PlanOf(lg), PlanOf(lgAgain, wgc: true, hwnd: 100)));
    }

    // ---- fallback notice ----

    [Fact]
    public void Fallback_notice_is_raised_once_per_transition_and_once_on_return()
    {
        var topo = TestDisplays.Topology(TestDisplays.Lg(), TestDisplays.Dell());
        var lg = SourceOf(TestDisplays.Lg(), topo);
        var dell = SourceOf(TestDisplays.Dell(), topo);
        const string missing = "Gewählter Monitor „DELL S2721DGF“ ist nicht verbunden – nehme Hauptmonitor „LG ULTRAGEAR+“";
        const string otherGpu = "Gewählter Monitor „DELL S2721DGF“ hängt an „GPU A“, die Aufnahme erreicht nur „GPU B“ – nehme Hauptmonitor „LG ULTRAGEAR+“";
        var notices = new CaptureNoticeTracker();

        Assert.Null(notices.Next(PlanOf(dell)));   // nothing was wrong: nothing to say

        Assert.Equal(new CaptureNoticeInfo(missing, IsWarning: true), notices.Next(PlanOf(lg, missing)));
        Assert.Null(notices.Next(PlanOf(lg, missing)));   // every further restart while it lasts: silent
        Assert.Null(notices.Next(PlanOf(lg, missing)));

        Assert.Equal(new CaptureNoticeInfo(otherGpu, IsWarning: true), notices.Next(PlanOf(lg, otherGpu)));

        var back = notices.Next(PlanOf(dell));
        Assert.NotNull(back);
        Assert.False(back!.IsWarning);
        Assert.Contains("DELL S2721DGF", back.Message);
        Assert.Null(notices.Next(PlanOf(dell)));
    }

    // ---- automatic restart after an ffmpeg exit ----

    [Fact]
    public async Task Mode_change_exit_restarts_warm_once_without_error_toast()
    {
        using var env = new BufferEnv(autoRestartDelay: TimeSpan.FromMilliseconds(50), topology: TestDisplays.Topology(TestDisplays.Lg()));
        await env.Buffer.ToggleAsync();
        Assert.Single(env.Starts);

        var action = env.Buffer.OnUnexpectedExit(1, ModeChangeStderr, generation: 0);

        Assert.Equal(UnexpectedExitAction.AutoRestart, action);
        Assert.Empty(env.Errors);   // no "Buffer stopped unexpectedly"
        Assert.True(env.Buffer.AutoRestartPending);

        await WaitUntil(() => !env.Buffer.AutoRestartPending);
        Assert.Equal(2, env.Starts.Count);   // started again once, warm
        Assert.Empty(env.Errors);
    }

    [Fact]
    public async Task Restart_after_the_lock_screen_or_a_UAC_prompt_took_the_desktop_waits_until_it_is_back()
    {
        using var env = new BufferEnv(autoRestartDelay: TimeSpan.FromMilliseconds(50), topology: TestDisplays.Topology(TestDisplays.Lg()));
        await env.Buffer.ToggleAsync();
        Assert.Single(env.Starts);

        // Win+L: ddagrab loses its output (ACCESS_LOST); a start before the user is back would fail with "access denied".
        env.DesktopAvailable = false;
        Assert.Equal(UnexpectedExitAction.AutoRestart,
                     env.Buffer.OnUnexpectedExit(0, "[Parsed_ddagrab_0 @ 01] AcquireNextFrame failed: 887a0026", generation: 0));
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.Single(env.Starts);
        Assert.True(env.Buffer.AutoRestartPending);

        env.DesktopAvailable = true;
        await WaitUntil(() => !env.Buffer.AutoRestartPending);
        Assert.Equal(2, env.Starts.Count);
        Assert.Empty(env.Errors);
    }

    [Fact]
    public void A_start_refused_while_the_secure_desktop_has_the_input_restarts_by_itself()
    {
        using var env = new BufferEnv(topology: TestDisplays.Topology(TestDisplays.Lg()));
        const string denied = "[Parsed_ddagrab_0 @ 01] Desktop duplication access denied\n" +
                              "[in#0 @ 02] Error opening input: Operation not permitted";

        Assert.Equal(UnexpectedExitAction.AutoRestart, env.Buffer.OnUnexpectedExit(1, denied, generation: 0));
        Assert.Empty(env.Errors);
    }

    [Fact]
    public async Task Restart_that_finds_no_monitor_says_why_and_waits_for_one()
    {
        using var env = new BufferEnv(autoRestartDelay: TimeSpan.FromMilliseconds(50), topology: TestDisplays.Topology(TestDisplays.Lg()));
        await env.Buffer.ToggleAsync();
        Assert.NotNull(env.Buffer.CurrentPlan);

        // The only monitor is switched off: ddagrab ends, and when the restart comes DXGI reports no output yet.
        env.Topo = DisplayTopology.Empty("test: monitor off");
        Assert.Equal(UnexpectedExitAction.AutoRestart, env.Buffer.OnUnexpectedExit(1, ModeChangeStderr, generation: 0));
        await WaitUntil(() => !env.Buffer.AutoRestartPending);

        var error = Assert.Single(env.Errors);
        Assert.DoesNotContain("ffmpeg code", error);
        Assert.Contains("Windows", error);
        Assert.Contains("von selbst", error);         // it comes back by itself
        Assert.Null(env.Buffer.CurrentPlan);           // no label of a dead capture
        Assert.True(env.Buffer.WaitingForDisplay);     // still on: a manual recording pauses it
        Assert.Single(env.Starts);

        // A display event that still brings no monitor: no second toast.
        await env.Buffer.ReviveAsync();
        Assert.Single(env.Errors);
        Assert.True(env.Buffer.WaitingForDisplay);

        // The monitor is back: the display settled, the buffer starts again - warm.
        env.Topo = TestDisplays.Topology(TestDisplays.Lg());
        env.Buffer.RequestRevive();
        await WaitUntil(() => env.Starts.Count == 2);
        Assert.False(env.Buffer.WaitingForDisplay);
        Assert.NotNull(env.Buffer.CurrentPlan);

        env.Buffer.RequestRevive();   // nothing to bring back any more
        await Task.Delay(100);
        Assert.Equal(2, env.Starts.Count);
    }

    [Fact]
    public async Task Buffer_switched_on_without_a_monitor_starts_once_one_is_there()
    {
        using var env = new BufferEnv();
        await env.Buffer.ToggleAsync();
        Assert.Single(env.Errors);
        Assert.True(env.Buffer.WaitingForDisplay);

        env.Topo = TestDisplays.Topology(TestDisplays.Lg());
        await env.Buffer.ReviveAsync();

        var plan = Assert.Single(env.Starts);
        Assert.Equal("LG ULTRAGEAR+", plan.Source.Resolution.Display!.FriendlyName);
        Assert.False(env.Buffer.WaitingForDisplay);
    }

    [Fact]
    public async Task Ctrl_F10_while_waiting_for_a_monitor_says_again_why_nothing_starts()
    {
        using var env = new BufferEnv();
        await env.Buffer.ToggleAsync();
        Assert.True(env.Buffer.WaitingForDisplay);

        await env.Buffer.ToggleAsync();   // shown as "off": the press is a start, and it gets an answer

        Assert.Equal(2, env.Errors.Count);
        Assert.True(env.Buffer.WaitingForDisplay);
    }

    [Fact]
    public async Task Switched_off_or_paused_buffer_is_not_brought_back()
    {
        using var env = new BufferEnv();
        await env.Buffer.ToggleAsync();
        Assert.True(env.Buffer.WaitingForDisplay);
        await env.Buffer.StopAsync();
        Assert.False(env.Buffer.WaitingForDisplay);

        env.Topo = TestDisplays.Topology(TestDisplays.Lg());
        await env.Buffer.ReviveAsync();
        Assert.Empty(env.Starts);

        // Paused for a manual recording while it waited: the recording's end brings it back, not a display change.
        using var paused = new BufferEnv();
        await paused.Buffer.ToggleAsync();
        await paused.Buffer.SuspendAsync();
        paused.Topo = TestDisplays.Topology(TestDisplays.Lg());
        await paused.Buffer.ReviveAsync();
        Assert.Empty(paused.Starts);
        paused.Buffer.Resume();
        await WaitUntil(() => paused.Starts.Count == 1);
    }

    [Fact]
    public async Task Display_caused_exit_beyond_the_bound_waits_for_the_display()
    {
        using var env = new BufferEnv(topology: TestDisplays.Topology(TestDisplays.Lg()));
        await env.Buffer.ToggleAsync();

        for (int i = 0; i < DisplayRestartPolicy.MaxRestarts; i++)
            Assert.Equal(UnexpectedExitAction.AutoRestart, env.Buffer.OnUnexpectedExit(1, ModeChangeStderr, generation: 0));
        Assert.Equal(UnexpectedExitAction.Report, env.Buffer.OnUnexpectedExit(1, ModeChangeStderr, generation: 0));

        Assert.Contains("ffmpeg code 1", Assert.Single(env.Errors));
        Assert.Null(env.Buffer.CurrentPlan);
        Assert.True(env.Buffer.WaitingForDisplay);

        await env.Buffer.ReviveAsync();   // the next settled display change
        Assert.Equal(2, env.Starts.Count);
    }

    [Fact]
    public async Task Crash_without_a_display_cause_is_not_revived()
    {
        using var env = new BufferEnv(topology: TestDisplays.Topology(TestDisplays.Lg()));
        await env.Buffer.ToggleAsync();

        Assert.Equal(UnexpectedExitAction.Report, env.Buffer.OnUnexpectedExit(255, GenericStderr, generation: 0));

        Assert.Null(env.Buffer.CurrentPlan);
        Assert.False(env.Buffer.WaitingForDisplay);
        await env.Buffer.ReviveAsync();
        Assert.Single(env.Starts);
    }

    [Fact]
    public async Task Automatic_restart_waits_until_the_display_is_quiet()
    {
        // R8: a game flips the mode several times while it starts - the restart comes once, after the last flip.
        using var env = new BufferEnv(autoRestartDelay: TimeSpan.FromMilliseconds(50), topology: TestDisplays.Topology(TestDisplays.Lg()));
        await env.Buffer.ToggleAsync();
        bool flipping = true;
        env.Buffer.LastDisplayChangeUtc = () => flipping ? DateTime.UtcNow : DateTime.UtcNow.AddMinutes(-1);

        Assert.Equal(UnexpectedExitAction.AutoRestart, env.Buffer.OnUnexpectedExit(1, ModeChangeStderr, generation: 0));
        await Task.Delay(500);   // ten times the delay, but the display never calmed down
        Assert.Single(env.Starts);
        Assert.True(env.Buffer.AutoRestartPending);

        flipping = false;
        await WaitUntil(() => !env.Buffer.AutoRestartPending);
        Assert.Equal(2, env.Starts.Count);
    }

    [Fact]
    public async Task Display_that_never_calms_down_still_gets_its_restart()
    {
        using var env = new BufferEnv(autoRestartDelay: TimeSpan.FromMilliseconds(50), topology: TestDisplays.Topology(TestDisplays.Lg()));
        env.Buffer.AutoRestartMaxWait = TimeSpan.FromMilliseconds(300);
        await env.Buffer.ToggleAsync();
        env.Buffer.LastDisplayChangeUtc = () => DateTime.UtcNow;

        Assert.Equal(UnexpectedExitAction.AutoRestart, env.Buffer.OnUnexpectedExit(1, ModeChangeStderr, generation: 0));

        await WaitUntil(() => env.Starts.Count == 2);
        Assert.False(env.Buffer.AutoRestartPending);
    }

    [Fact]
    public void Settle_wait_runs_from_the_last_display_event()
    {
        var now = new DateTime(2026, 11, 9, 20, 0, 0, DateTimeKind.Utc);
        var delay = TimeSpan.FromMilliseconds(1500);

        Assert.Null(DisplayRestartPolicy.SettleWait(null, now, delay));
        Assert.Null(DisplayRestartPolicy.SettleWait(now.AddSeconds(-2), now, delay));
        Assert.Null(DisplayRestartPolicy.SettleWait(now.AddMilliseconds(-1500), now, delay));
        Assert.Equal(TimeSpan.FromMilliseconds(1100), DisplayRestartPolicy.SettleWait(now.AddMilliseconds(-400), now, delay));
        Assert.Equal(delay, DisplayRestartPolicy.SettleWait(now, now, delay));
    }

    [Fact]
    public void Buffer_counts_as_coming_back_until_an_unexpected_exit_is_decided()
    {
        using var env = new BufferEnv();
        bool pendingWhileReleasing = false;

        // Between the process end and the decision the run is let go and its last stderr lines are awaited (up to 500 ms):
        // a Ctrl+F9 then must pause the buffer, or the restart would start next to the recording.
        var action = env.Buffer.OnRunEndedUnexpectedly(255, generation: 0,
            release: () => pendingWhileReleasing = env.Buffer.AutoRestartPending, recentStderr: () => GenericStderr);

        Assert.True(pendingWhileReleasing);
        Assert.Equal(UnexpectedExitAction.Report, action);
        Assert.False(env.Buffer.AutoRestartPending);
    }

    [Fact]
    public void Exit_soon_after_a_display_change_restarts_without_a_marker()
    {
        using var env = new BufferEnv();
        env.Buffer.LastDisplayChangeUtc = () => DateTime.UtcNow.AddSeconds(-2);

        Assert.Equal(UnexpectedExitAction.AutoRestart, env.Buffer.OnUnexpectedExit(1, GenericStderr, generation: 0));
        Assert.Empty(env.Errors);
    }

    [Fact]
    public void Exit_burst_is_bounded_then_reports_once()
    {
        using var env = new BufferEnv();

        var actions = Enumerable.Range(0, 4).Select(_ => env.Buffer.OnUnexpectedExit(1, ModeChangeStderr, generation: 0)).ToList();

        Assert.Equal(new[] { UnexpectedExitAction.AutoRestart, UnexpectedExitAction.AutoRestart,
                             UnexpectedExitAction.AutoRestart, UnexpectedExitAction.Report }, actions);
        var error = Assert.Single(env.Errors);
        Assert.Contains("ffmpeg code 1", error);
    }

    [Fact]
    public void Exit_without_marker_keeps_todays_error_toast()
    {
        using var env = new BufferEnv();

        Assert.Equal(UnexpectedExitAction.Report, env.Buffer.OnUnexpectedExit(255, GenericStderr, generation: 0));

        var error = Assert.Single(env.Errors);
        Assert.Contains("ffmpeg code 255", error);
        Assert.False(env.Buffer.AutoRestartPending);
    }

    [Fact]
    public async Task No_reachable_monitor_does_not_start_and_reports_why()
    {
        using var env = new BufferEnv();

        await env.Buffer.ToggleAsync();

        Assert.False(env.Buffer.IsRunning);
        Assert.Null(env.Buffer.CurrentPlan);
        var error = Assert.Single(env.Errors);
        Assert.Contains("Windows", error);   // "… Windows meldet keinen Monitor" / "… Windows reports no monitor"
    }

    [Fact]
    public void Restarts_older_than_the_window_no_longer_count()
    {
        var t0 = new DateTime(2026, 11, 9, 20, 0, 0, DateTimeKind.Utc);
        var ledger = new AutoRestartLedger();

        for (int i = 1; i <= DisplayRestartPolicy.MaxRestarts; i++)
        {
            Assert.True(ledger.TryTake(ModeChangeStderr, null, t0.AddSeconds(i), out int attempt));
            Assert.Equal(i, attempt);
        }
        Assert.False(ledger.TryTake(ModeChangeStderr, null, t0.AddSeconds(30), out _));

        Assert.True(ledger.TryTake(ModeChangeStderr, null, t0.AddSeconds(61.5), out int afterWindow));
        Assert.Equal(DisplayRestartPolicy.MaxRestarts, afterWindow);   // the first one has left the window

        Assert.False(new AutoRestartLedger().TryTake(GenericStderr, null, t0, out _));   // not caused by the display
    }

    [Fact]
    public void Stderr_tail_keeps_the_last_lines()
    {
        var tail = new StderrTail(DisplayRestartPolicy.StderrLines);
        for (int i = 1; i <= 25; i++) tail.Add($"line {i}");

        var lines = tail.Text.Split('\n');
        Assert.Equal(20, lines.Length);
        Assert.Equal("line 6", lines[0]);
        Assert.Equal("line 25", lines[^1]);
    }

    // ---- start log line ----

    [Fact]
    public void Start_log_line_names_saved_identity_status_output_and_path()
    {
        var dell = TestDisplays.Dell(output: 2);
        var topo = TestDisplays.Topology(TestDisplays.Lg(), dell);
        var saved = MonitorRef.Of(dell);
        var source = CaptureSourceRules.ForDisplay(dell, topo, MonitorTargetRules.Resolve(saved, topo))!;

        Assert.Equal(
            $"Capture.Monitor: saved={dell.DevicePath} -> Exact ({source.Resolution.Reason}) display='{source.Label}' " +
            @"gdi=\\.\DISPLAY2 adapter=00000000:00015B44 default=0 output=2 2560x1440@(3440,0) primary=False path=dda",
            CaptureSourceRules.LogLine(saved, source, topo, "dda"));

        var legacy = new MonitorRef(null, @"\\.\DISPLAY2", null, null, null);
        Assert.StartsWith(@"Capture.Monitor: saved=\\.\DISPLAY2 -> ", CaptureSourceRules.LogLine(legacy, source, topo, "amf"));
        Assert.StartsWith("Capture.Monitor: saved=null -> ", CaptureSourceRules.LogLine(null, source, topo, "dda"));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "condition not reached within 10 s");
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// A buffer on temp folders and a test registry key, on a fake display topology (<see cref="Topo"/>, changeable; by
    /// default without any monitor, so a start stops at "no monitor"). A start that finds a monitor never runs ffmpeg: its
    /// plan is recorded in <see cref="Starts"/>.
    /// </summary>
    private sealed class BufferEnv : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_bufmon_" + Guid.NewGuid().ToString("N"));
        private readonly string _rootPath = @"Software\WKI_Clipper_Tests_BufMon_" + Guid.NewGuid().ToString("N");
        private readonly RegistryKey _root;

        public volatile DisplayTopology Topo;
        /// <summary>False = the lock screen or a UAC prompt has the input.</summary>
        public volatile bool DesktopAvailable = true;
        public ReplayBufferService Buffer { get; }
        public ConcurrentQueue<string> Errors { get; } = new();
        public ConcurrentQueue<CaptureTargetResolver.CapturePlan> Starts { get; } = new();

        public BufferEnv(TimeSpan? autoRestartDelay = null, DisplayTopology? topology = null)
        {
            Topo = topology ?? DisplayTopology.Empty("test: no displays");
            Directory.CreateDirectory(_dir);
            _root = Registry.CurrentUser.CreateSubKey(_rootPath);
            var settings = new SettingsService(_dir);
            var o = settings.Current.Output;
            o.BufferFolder = Path.Combine(_dir, "buffer");
            o.ClipsFolder = Path.Combine(_dir, "clips");
            o.ScreenshotsFolder = Path.Combine(_dir, "shots");
            o.SortByGame = false;
            settings.Current.Audio.RecordSystemSound = false;
            settings.Current.Audio.RecordMicrophone = false;
            settings.Current.Capture.Mode = CaptureMode.Monitor;

            Buffer = new ReplayBufferService(settings, new BufferFolderRecord(_root))
            {
                // The key follows the topology, so a changed topology is read again.
                Topology = new DisplayTopologyCache(() => Topo, () => Topo.Fingerprint),
                // Long by default: a scheduled restart must not run in the middle of a test that does not wait for it.
                AutoRestartDelay = autoRestartDelay ?? TimeSpan.FromMinutes(10),
                // Not this machine's desktop: a test must not depend on whether the PC is locked while it runs.
                InputDesktopAvailable = () => DesktopAvailable,
                StartRunOverride = plan => Starts.Enqueue(plan)
            };
            Buffer.BufferError += (_, m) => Errors.Enqueue(m);
        }

        public void SetLastStartedSource(CaptureSource source)
            => typeof(ReplayBufferService).GetField("_lastStartedSource", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(Buffer, source);

        public void Dispose()
        {
            Buffer.Dispose();
            _root.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(_rootPath, throwOnMissingSubKey: false);
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }
    }
}
