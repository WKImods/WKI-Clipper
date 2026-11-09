using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// A Ctrl+F9 recording across a display mode switch: ddagrab ends, the part up to the switch is kept and the recording
/// goes on in "{name}_Teil2.mp4" (owner decision). Any other end stays as it was.
/// </summary>
public sealed class ManualRecordingTests
{
    private const string ModeChangeStderr =
        "frame= 1234 fps= 60 q=-0.0 size=   20480KiB time=00:00:20.56\n" +
        "[Parsed_ddagrab_0 @ 0000020b1c3e4f40] Output parameters changed!\n" +
        "Conversion failed!";

    private const string AccessLostStderr =
        "frame= 1234 fps= 60 q=-0.0 size=   20480KiB time=00:00:20.56\n" +
        "[Parsed_ddagrab_0 @ 0000020b1c3e4f40] AcquireNextFrame failed: 887a0026\n" +
        "Conversion failed!";

    private const string GenericStderr =
        "frame= 1234 fps= 60 q=-0.0 size=   20480KiB time=00:00:20.56\n" +
        "[h264_amf @ 0000020b] Encoder failed\n" +
        "Conversion failed!";

    // ---- a part ffmpeg was killed on while it finished (faststart writes the index last) ----

    /// <summary>What a killed faststart part looks like: footage, but no moov (its index comes last).</summary>
    private static void WriteFootageWithoutIndex(string path)
        => File.WriteAllBytes(path, Mp4Builder.Concat(Mp4Builder.Box("ftyp", new byte[16]), Mp4Builder.Box("mdat", new byte[64 * 1024])));

    private static void WriteFootageWithIndex(string path)
        => File.WriteAllBytes(path, Mp4Builder.Concat(
            Mp4Builder.Box("ftyp", new byte[16]),
            Mp4Builder.Box("moov", Mp4Builder.Box("mvhd", new byte[] { 0, 0, 0, 0 }, Mp4Builder.U32(0), Mp4Builder.U32(0),
                                                  Mp4Builder.U32(1000), Mp4Builder.U32(60_000), new byte[80])),
            Mp4Builder.Box("mdat", new byte[64 * 1024])));

    [Fact]
    public void A_part_killed_while_finishing_without_its_index_is_not_saved_and_is_kept()
    {
        using var env = new RecordingEnv(TestDisplays.Topology(TestDisplays.Lg()));
        string first = env.Recording.Start();
        WriteFootageWithoutIndex(first);

        Assert.Equal(PartExitAction.Failed, env.Recording.OnPartExited(first, -1, stopRequested: true, "", killed: true));

        var result = Assert.Single(env.Stopped);
        Assert.False(result.Success);
        Assert.Contains("keinen Index", result.Error);
        Assert.True(File.Exists(first));                // footage that tools can still rescue: never deleted
        Assert.False(env.Recording.IsRecording);
    }

    [Fact]
    public void A_part_killed_while_finishing_that_has_its_index_is_saved()
    {
        using var env = new RecordingEnv(TestDisplays.Topology(TestDisplays.Lg()));
        string first = env.Recording.Start();
        WriteFootageWithIndex(first);

        Assert.Equal(PartExitAction.Saved, env.Recording.OnPartExited(first, -1, stopRequested: true, "", killed: true));
        Assert.True(Assert.Single(env.Stopped).Success);
    }

    [Fact]
    public void A_part_killed_without_its_index_after_a_display_change_does_not_go_on_into_part_2()
    {
        using var env = new RecordingEnv(TestDisplays.Topology(TestDisplays.Lg()));
        string first = env.Recording.Start();
        WriteFootageWithoutIndex(first);

        Assert.Equal(PartExitAction.Failed, env.Recording.OnPartExited(first, -1, stopRequested: false, AccessLostStderr, killed: true));
        Assert.Single(env.Starts);
        Assert.True(File.Exists(first));
        Assert.Contains("keinen Index", Assert.Single(env.Stopped).Error);
    }

    [Fact]
    public void A_part_that_ended_by_itself_is_judged_as_before()
    {
        using var env = new RecordingEnv(TestDisplays.Topology(TestDisplays.Lg()));
        string first = env.Recording.Start();
        env.WriteFootage(first);   // no index either - but ffmpeg was not killed, so nothing changes

        Assert.Equal(PartExitAction.Saved, env.Recording.OnPartExited(first, 0, stopRequested: true, ""));
    }

    [Fact]
    public async Task Part_2_after_the_lock_screen_or_a_UAC_prompt_took_the_desktop_waits_until_it_is_back()
    {
        using var env = new RecordingEnv(TestDisplays.Topology(TestDisplays.Lg()));
        string first = env.Recording.Start();
        env.WriteFootage(first);

        env.DesktopAvailable = false;
        Assert.Equal(PartExitAction.Continues, env.Recording.OnPartExited(first, 0, stopRequested: false, AccessLostStderr));
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.Single(env.Starts);                      // no part 2 on the secure desktop
        Assert.True(env.Recording.IsRecording);
        Assert.Empty(env.Stopped);

        env.DesktopAvailable = true;
        await Wait.Until(() => env.Starts.Count == 2);
        Assert.Empty(env.Stopped);
    }

    [Fact]
    public async Task Mode_change_exit_keeps_part_1_and_starts_part_2()
    {
        using var env = new RecordingEnv(TestDisplays.Topology(TestDisplays.Lg(w: 3840, h: 2160)));
        string first = env.Recording.Start();
        var (_, firstPlan) = Assert.Single(env.Starts);
        Assert.Equal(first, env.Starts.Single().Path);
        Assert.Equal(3840, firstPlan.Source.Width);
        env.WriteFootage(first);

        // An exclusive-fullscreen game switched the monitor to 1080p; ddagrab ended on it.
        env.Topology = TestDisplays.Topology(TestDisplays.Lg(w: 1920, h: 1080));
        var action = env.Recording.OnPartExited(first, 1, stopRequested: false, ModeChangeStderr);

        Assert.Equal(PartExitAction.Continues, action);
        Assert.True(File.Exists(first));                // part 1 is kept
        Assert.Empty(env.Stopped);                      // the recording has not ended: no "saved"/"failed", the buffer stays paused
        Assert.True(env.Recording.IsRecording);         // a Ctrl+F9 now stops it instead of starting a second one

        await Wait.Until(() => env.Starts.Count == 2);
        string second = Path.Combine(Path.GetDirectoryName(first)!, Path.GetFileNameWithoutExtension(first) + "_Teil2.mp4");
        var (secondPath, secondPlan) = env.Starts.ElementAt(1);
        Assert.Equal(second, secondPath);
        Assert.Equal(1920, secondPlan.Source.Width);    // the next part records the new mode
        Assert.Equal(firstPlan.Source.DisplayKey, secondPlan.Source.DisplayKey);
        await Wait.Until(() => !env.Continued.IsEmpty);
        var part = Assert.Single(env.Continued);
        Assert.Equal(new RecordingPart(first, second, 2), part);
        Assert.Equal("Auflösung gewechselt – Aufnahme läuft in Teil 2 weiter", part.Message);
        Assert.Equal(second, env.Recording.CurrentOutputPath);
        Assert.Empty(env.Stopped);

        // The user stops part 2: now the recording ends, as a success, with the last part.
        env.WriteFootage(second);
        Assert.Equal(PartExitAction.Saved, env.Recording.OnPartExited(second, -1, stopRequested: true, ""));
        var result = Assert.Single(env.Stopped);
        Assert.Equal(new RecordingResult(second, true, null), result);
    }

    [Fact]
    public async Task Exit_without_marker_still_fails_as_today()
    {
        using var env = new RecordingEnv(TestDisplays.Topology(TestDisplays.Lg()));
        string first = env.Recording.Start();
        env.WriteFootage(first);

        var action = env.Recording.OnPartExited(first, 1, stopRequested: false, GenericStderr);

        Assert.Equal(PartExitAction.Failed, action);
        var result = Assert.Single(env.Stopped);
        Assert.Equal(first, result.Path);
        Assert.False(result.Success);
        Assert.Contains("ffmpeg code 1", result.Error);
        Assert.True(File.Exists(first));   // a playable file is not thrown away, as before
        Assert.False(env.Recording.IsRecording);
        await Task.Delay(100);
        Assert.Single(env.Starts);         // no second part
        Assert.Empty(env.Continued);
    }

    [Fact]
    public void Exit_soon_after_a_display_change_continues_without_a_marker()
    {
        using var env = new RecordingEnv(TestDisplays.Topology(TestDisplays.Lg()), continueDelay: TimeSpan.FromMinutes(10));
        env.Recording.LastDisplayChangeUtc = () => DateTime.UtcNow.AddSeconds(-2);
        string first = env.Recording.Start();
        env.WriteFootage(first);

        Assert.Equal(PartExitAction.Continues, env.Recording.OnPartExited(first, 0, stopRequested: false, GenericStderr));
        Assert.Empty(env.Stopped);
    }

    [Fact]
    public void Broken_part_is_not_continued()
    {
        using var env = new RecordingEnv(TestDisplays.Topology(TestDisplays.Lg()));
        string first = env.Recording.Start();
        File.WriteAllBytes(first, new byte[100]);   // a moov-less stub

        Assert.Equal(PartExitAction.Failed, env.Recording.OnPartExited(first, 1, stopRequested: false, ModeChangeStderr));

        Assert.False(Assert.Single(env.Stopped).Success);
        Assert.False(File.Exists(first));
    }

    [Fact]
    public async Task Stop_between_two_parts_ends_with_the_kept_part()
    {
        using var env = new RecordingEnv(TestDisplays.Topology(TestDisplays.Lg()), continueDelay: TimeSpan.FromMinutes(10));
        string first = env.Recording.Start();
        env.WriteFootage(first);
        Assert.Equal(PartExitAction.Continues, env.Recording.OnPartExited(first, 1, stopRequested: false, ModeChangeStderr));

        await env.Recording.StopAsync();

        Assert.Equal(new RecordingResult(first, true, null), Assert.Single(env.Stopped));
        Assert.False(env.Recording.IsRecording);
        Assert.Null(env.Recording.CurrentOutputPath);
        Assert.Single(env.Starts);
    }

    [Fact]
    public async Task Next_part_that_cannot_start_ends_with_the_kept_part_and_says_why()
    {
        using var env = new RecordingEnv(TestDisplays.Topology(TestDisplays.Lg()));
        string first = env.Recording.Start();
        env.WriteFootage(first);
        env.Topology = DisplayTopology.Empty("test: all monitors gone");

        Assert.Equal(PartExitAction.Continues, env.Recording.OnPartExited(first, 1, stopRequested: false, ModeChangeStderr));

        await Wait.Until(() => !env.Stopped.IsEmpty);
        var result = Assert.Single(env.Stopped);
        Assert.Equal(first, result.Path);
        Assert.True(result.Success);       // part 1 is a good file
        Assert.Contains("Windows", result.Note);
        Assert.Empty(env.Continued);
        Assert.False(env.Recording.IsRecording);
    }

    [Fact]
    public async Task Failed_start_brings_the_paused_capture_back()
    {
        using var env = new RecordingEnv(DisplayTopology.Empty("test: no displays"));
        int suspended = 0, resumed = 0;
        env.Recording.SuspendCompetingCapture = () => { suspended++; return Task.CompletedTask; };
        env.Recording.ResumeCompetingCapture = () => resumed++;

        await Assert.ThrowsAsync<InvalidOperationException>(env.Recording.ToggleAsync);

        Assert.Equal(1, suspended);
        Assert.Equal(1, resumed);
        Assert.False(env.Recording.IsRecording);
    }

    [Fact]
    public async Task Ctrl_F9_while_a_part_end_is_still_handled_stops_the_recording()
    {
        using var env = new RecordingEnv(TestDisplays.Topology(TestDisplays.Lg()));
        string first = env.Recording.Start();
        env.WriteFootage(first);

        // ffmpeg ended on a mode switch; its end is still being handled (the part is let go, its last stderr lines are
        // awaited). The user presses Ctrl+F9 right then: that is a stop - not a second recording next to this one.
        Assert.True(env.Recording.IsRecording);
        await env.Recording.ToggleAsync();

        Assert.Equal(PartExitAction.Saved, env.Recording.OnPartExited(first, 1, stopRequested: false, ModeChangeStderr));
        Assert.Equal(new RecordingResult(first, true, null), Assert.Single(env.Stopped));
        Assert.False(env.Recording.IsRecording);
        await Task.Delay(100);
        Assert.Single(env.Starts);   // neither a second recording nor a part 2
        Assert.Empty(env.Continued);
    }

    [Fact]
    public void End_of_a_part_that_is_not_the_running_one_is_ignored()
    {
        using var env = new RecordingEnv(TestDisplays.Topology(TestDisplays.Lg()), continueDelay: TimeSpan.FromMinutes(10));
        string first = env.Recording.Start();
        env.WriteFootage(first);
        string stale = Path.Combine(Path.GetDirectoryName(first)!, "Rec_2026-11-09_19-00-00.mp4");
        env.WriteFootage(stale);

        Assert.Equal(PartExitAction.Ignored, env.Recording.OnPartExited(stale, 1, stopRequested: false, ModeChangeStderr));

        Assert.Empty(env.Stopped);
        Assert.True(env.Recording.IsRecording);
        Assert.Equal(first, env.Recording.CurrentOutputPath);
        Assert.Equal(PartExitAction.Continues, env.Recording.OnPartExited(first, 1, stopRequested: false, ModeChangeStderr));
    }

    [Fact]
    public async Task Next_part_on_the_fallback_monitor_ends_the_recording_with_the_reason()
    {
        var dell = TestDisplays.Dell();
        using var env = new RecordingEnv(TestDisplays.Topology(TestDisplays.Lg(), dell), saved: MonitorRef.Of(dell));
        string first = env.Recording.Start();
        Assert.Equal(dell.HMonitor, env.Starts.Single().Plan.Source.HMonitor);
        env.WriteFootage(first);

        // The chosen DELL is unplugged: ddagrab ends on ACCESS_LOST. Part 2 would record the primary LG instead - silently.
        env.Topology = TestDisplays.Topology(TestDisplays.Lg());
        Assert.Equal(PartExitAction.Continues, env.Recording.OnPartExited(first, 1, stopRequested: false, AccessLostStderr));

        await Wait.Until(() => !env.Stopped.IsEmpty);
        var result = Assert.Single(env.Stopped);
        Assert.Equal(first, result.Path);
        Assert.True(result.Success);   // part 1 is a good file
        Assert.Equal("Aufnahme beendet: „DELL S2721DGF“ ist nicht verbunden.", result.Note);
        Assert.Single(env.Starts);
        Assert.Empty(env.Continued);
        Assert.False(env.Recording.IsRecording);
    }

    [Fact]
    public async Task Next_part_back_on_the_chosen_monitor_ends_the_fallback_recording_with_a_note()
    {
        var dell = TestDisplays.Dell();
        using var env = new RecordingEnv(TestDisplays.Topology(TestDisplays.Lg()), saved: MonitorRef.Of(dell));
        string first = env.Recording.Start();
        Assert.True(env.Starts.Single().Plan.Source.Resolution.IsFallback);   // recording the primary for the missing DELL
        env.WriteFootage(first);

        env.Topology = TestDisplays.Topology(TestDisplays.Lg(), dell);         // the DELL is back
        Assert.Equal(PartExitAction.Continues, env.Recording.OnPartExited(first, 1, stopRequested: false, AccessLostStderr));

        await Wait.Until(() => !env.Stopped.IsEmpty);
        var result = Assert.Single(env.Stopped);
        Assert.True(result.Success);
        Assert.Equal("Aufnahme beendet: Der Monitor hat gewechselt („LG ULTRAGEAR+“ → „DELL S2721DGF“).", result.Note);
        Assert.Single(env.Starts);
        Assert.Empty(env.Continued);
    }

    [Fact]
    public async Task Next_part_on_the_same_monitor_without_its_device_path_continues()
    {
        // Nothing saved: the primary LG. While the DisplayPort link retrains Windows reports no device path for it - the
        // same monitor all the same (HMONITOR, GDI name, output).
        var lg = TestDisplays.Lg();
        using var env = new RecordingEnv(TestDisplays.Topology(lg, TestDisplays.Dell()));
        string first = env.Recording.Start();
        env.WriteFootage(first);

        env.Topology = TestDisplays.Topology(lg with { DevicePath = null }, TestDisplays.Dell());
        Assert.Equal(PartExitAction.Continues, env.Recording.OnPartExited(first, 1, stopRequested: false, ModeChangeStderr));

        await Wait.Until(() => env.Starts.Count == 2);
        Assert.Empty(env.Stopped);
    }

    [Fact]
    public async Task Next_part_waits_until_the_display_is_quiet()
    {
        // R8: a game flips the mode several times while it starts - part 2 starts once, after the last flip.
        using var env = new RecordingEnv(TestDisplays.Topology(TestDisplays.Lg()));
        bool flipping = true;
        env.Recording.LastDisplayChangeUtc = () => flipping ? DateTime.UtcNow : DateTime.UtcNow.AddMinutes(-1);
        string first = env.Recording.Start();
        env.WriteFootage(first);

        Assert.Equal(PartExitAction.Continues, env.Recording.OnPartExited(first, 1, stopRequested: false, ModeChangeStderr));
        await Task.Delay(400);   // twenty times the delay, but the display never calmed down
        Assert.Single(env.Starts);
        Assert.True(env.Recording.IsRecording);

        flipping = false;
        await Wait.Until(() => env.Starts.Count == 2);
        Assert.Single(env.Continued);
    }

    [Theory]
    [InlineData(2, @"C:\Clips\Arma\Rec_2026-11-09_20-15-00_Teil2.mp4")]
    [InlineData(3, @"C:\Clips\Arma\Rec_2026-11-09_20-15-00_Teil3.mp4")]
    public void Parts_are_numbered_next_to_the_first_file(int part, string expected)
        => Assert.Equal(expected, ManualRecordingService.PartPath(@"C:\Clips\Arma\Rec_2026-11-09_20-15-00.mp4", part));

    /// <summary>
    /// A recording service on temp folders and a fake display topology (<see cref="Topology"/>, changeable), with ffmpeg
    /// replaced: every part that would start is recorded in <see cref="Starts"/>; the tests play ffmpeg's end themselves.
    /// </summary>
    private sealed class RecordingEnv : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_rec_" + Guid.NewGuid().ToString("N"));

        public volatile DisplayTopology Topology;
        public ManualRecordingService Recording { get; }
        public ConcurrentQueue<(string Path, CaptureTargetResolver.CapturePlan Plan)> Starts { get; } = new();
        public ConcurrentQueue<RecordingResult> Stopped { get; } = new();
        public ConcurrentQueue<RecordingPart> Continued { get; } = new();
        /// <summary>False = the lock screen or a UAC prompt has the input.</summary>
        public volatile bool DesktopAvailable = true;

        /// <param name="saved">The chosen monitor; null = nothing saved (the primary monitor).</param>
        public RecordingEnv(DisplayTopology topology, TimeSpan? continueDelay = null, MonitorRef? saved = null)
        {
            Topology = topology;
            Directory.CreateDirectory(_dir);
            var settings = new SettingsService(_dir);
            var o = settings.Current.Output;
            o.ClipsFolder = Path.Combine(_dir, "clips");
            o.SortByGame = false;
            settings.Current.Audio.RecordSystemSound = false;
            settings.Current.Audio.RecordMicrophone = false;
            var capture = settings.Current.Capture;
            capture.Mode = CaptureMode.Monitor;
            capture.MonitorDevicePath = saved?.DevicePath;
            capture.MonitorDeviceName = saved?.GdiName;
            capture.MonitorFriendlyName = saved?.FriendlyName;
            capture.MonitorEdidMfg = saved?.EdidMfg;
            capture.MonitorEdidProduct = saved?.EdidProduct;

            Recording = new ManualRecordingService(settings)
            {
                // The key follows the topology, so a changed topology is read again.
                Topology = new DisplayTopologyCache(() => Topology, () => Topology.Fingerprint),
                ContinueDelay = continueDelay ?? TimeSpan.FromMilliseconds(20),
                // Not this machine's desktop: a test must not depend on whether the PC is locked while it runs.
                InputDesktopAvailable = () => DesktopAvailable,
                StartPartOverride = (plan, path) => Starts.Enqueue((path, plan))
            };
            Recording.RecordingStopped += (_, r) => Stopped.Enqueue(r);
            Recording.RecordingContinued += (_, p) => Continued.Enqueue(p);
        }

        /// <summary>What ffmpeg would have written: a file big enough to count as playable.</summary>
        public void WriteFootage(string path) => File.WriteAllBytes(path, new byte[64 * 1024]);

        public void Dispose()
        {
            Recording.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }
    }
}
