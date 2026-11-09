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

    private const string GenericStderr =
        "frame= 1234 fps= 60 q=-0.0 size=   20480KiB time=00:00:20.56\n" +
        "[h264_amf @ 0000020b] Encoder failed\n" +
        "Conversion failed!";

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

        public RecordingEnv(DisplayTopology topology, TimeSpan? continueDelay = null)
        {
            Topology = topology;
            Directory.CreateDirectory(_dir);
            var settings = new SettingsService(_dir);
            var o = settings.Current.Output;
            o.ClipsFolder = Path.Combine(_dir, "clips");
            o.SortByGame = false;
            settings.Current.Audio.RecordSystemSound = false;
            settings.Current.Audio.RecordMicrophone = false;
            settings.Current.Capture.Mode = CaptureMode.Monitor;   // nothing saved: the primary monitor

            Recording = new ManualRecordingService(settings)
            {
                // The key follows the topology, so a changed topology is read again.
                Topology = new DisplayTopologyCache(() => Topology, () => Topology.Fingerprint),
                ContinueDelay = continueDelay ?? TimeSpan.FromMilliseconds(20),
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
