using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// F9 end to end: the real <see cref="ReplayBufferService.SaveLastAsync"/> over a buffer folder of synthetic 1-second
/// segments, with the real ffmpeg. The buffer's own state (folder, generations, identity, ring, "running" process) is
/// set through its private fields - the capture side (ddagrab/WGC/AMF) cannot run in a test, everything after it can.
/// Skips when no ffmpeg is installed.
/// </summary>
public sealed class ReplayBufferSaveEndToEndTests : IDisposable
{
    private static readonly SegmentIdentity Arma = new("wgc:100:160x90", "libx264|30|Native|a1|wgc|ForcedAtSegmentBoundary");
    private static readonly SegmentIdentity Discord = new("wgc:200:160x90", Arma.Stream);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_e2e_" + Guid.NewGuid().ToString("N"));
    private readonly string _rootPath = @"Software\WKI_Clipper_Tests_E2E_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;
    private readonly string? _ffmpeg = FFmpegTestSupport.Find();
    private readonly SettingsService _settings;
    private readonly ReplayBufferService _buffer;
    private FFmpegService? _fakeRunning;

    private string BufferDir => Path.Combine(_dir, "buffer");
    private string ClipsDir => Path.Combine(_dir, "clips");

    private readonly List<ReplaySavedInfo> _saved = new();
    private readonly List<SaveFailure> _failed = new();
    private readonly List<string> _errors = new();
    private readonly List<SaveKind> _started = new();

    public ReplayBufferSaveEndToEndTests()
    {
        Directory.CreateDirectory(BufferDir);
        _root = Registry.CurrentUser.CreateSubKey(_rootPath);
        _settings = new SettingsService(_dir);
        var o = _settings.Current.Output;
        o.BufferFolder = BufferDir;
        o.ClipsFolder = ClipsDir;
        o.ScreenshotsFolder = Path.Combine(_dir, "shots");
        o.SortByGame = false;
        _settings.Current.ReplayBuffer.DurationSeconds = 300;
        _settings.Current.ReplayBuffer.SegmentDurationSeconds = 1;

        _buffer = new ReplayBufferService(_settings, new BufferFolderRecord(_root)) { FfmpegPathOverride = _ffmpeg };
        _buffer.ReplaySaved += (_, info) => _saved.Add(info);
        _buffer.SaveFailed += (_, f) => _failed.Add(f);
        _buffer.SaveStarted += (_, k) => _started.Add(k);
        _buffer.BufferError += (_, m) => _errors.Add(m);
    }

    public void Dispose()
    {
        try { _fakeRunning?.Dispose(); } catch { }
        _buffer.Dispose();
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_rootPath, throwOnMissingSubKey: false);
        DeleteDir();
    }

    // The stand-in "running" ffmpeg may still be letting go of the folder a moment after it was killed.
    private void DeleteDir()
    {
        for (int i = 0; i < 20; i++)
        {
            try { Directory.Delete(_dir, recursive: true); return; }
            catch (DirectoryNotFoundException) { return; }
            catch { System.Threading.Thread.Sleep(100); }
        }
    }

    // ---- reflection into the parts of the buffer that a capture would have filled ----

    private static FieldInfo Field(string name)
        => typeof(ReplayBufferService).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
           ?? throw new MissingFieldException(nameof(ReplayBufferService), name);

    private void SetState(int generation, SegmentIdentity current, IEnumerable<GenerationInfo> generations)
    {
        Field("_bufferDir").SetValue(_buffer, BufferDir);
        Field("_generation").SetValue(_buffer, generation);
        Field("_currentIdentity").SetValue(_buffer, current);
        Field("_runningRing").SetValue(_buffer, RingPlan.Create(300, 1));
        Field("_epoch").SetValue(_buffer, new BufferEpoch(RingPlan.Create(300, 1), current, generation, BufferDir));
        var map = (ConcurrentDictionary<int, GenerationInfo>)Field("_generations").GetValue(_buffer)!;
        foreach (var g in generations) map[g.Generation] = g;

        // "The buffer is running": a long-lived process in the ffmpeg slot (it is only checked for being alive).
        _fakeRunning = new FFmpegService(_ffmpeg);
        var standInLog = new ConcurrentQueue<string>();
        _fakeRunning.StdErrLine += (_, line) => standInLog.Enqueue(line);
        _fakeRunning.Start("-hide_banner -loglevel error -re -f lavfi -i anullsrc -t 120 -f null -");
        System.Threading.Thread.Sleep(200);   // a stand-in that died at once would turn every test below into "not running"
        Assert.True(_fakeRunning.IsRunning, "the stand-in ffmpeg exited: " + string.Join(" | ", standInLog));
        Field("_ffmpeg").SetValue(_buffer, _fakeRunning);
    }

    /// <summary>
    /// Segments of one generation get write times that follow their real durations from <paramref name="start"/> on
    /// (the way a recording leaves them). Returns the time the last one ended.
    /// </summary>
    private static DateTime Stamp(IEnumerable<string> files, DateTime start)
    {
        var t = start;
        foreach (var f in files)
        {
            Assert.True(Mp4Duration.TryRead(f, out var d), f);
            t += d;
            File.SetLastWriteTimeUtc(f, t);
        }
        return t;
    }

    private static double ChapterCountAndDuration(string probe, out int chapters)
    {
        chapters = Regex.Matches(probe, @"Chapter #0:\d+:").Count;
        var parsed = FFmpegProbeParser.Parse(probe);
        return parsed?.Duration?.TotalSeconds ?? -1;
    }

    private string Probe(string clip) => FfRun.Run(_ffmpeg!, $"-hide_banner -i \"{clip}\"").StdErr;

    // ---- tests ----

    [Fact]
    public async Task A_clip_across_an_alt_tab_gap_is_one_file_with_two_chapters_and_a_discord_gap()
    {
        if (_ffmpeg is null) return;
        FfRun.GenerateSegments(_ffmpeg, BufferDir, 0, 60, KeyframeMode.ForcedAtSegmentBoundary);
        FfRun.GenerateSegments(_ffmpeg, BufferDir, 2, 60, KeyframeMode.ForcedAtSegmentBoundary);
        var gen0 = FfRun.SegmentsOf(BufferDir, 0);
        var gen2 = FfRun.SegmentsOf(BufferDir, 2);

        // Wall clock: 20 s of Arma, then 45 s in Discord, then Arma again until just now.
        double sum0 = gen0.Sum(f => { Mp4Duration.TryRead(f, out var d); return d.TotalSeconds; });
        double sum2 = gen2.Sum(f => { Mp4Duration.TryRead(f, out var d); return d.TotalSeconds; });
        var now = DateTime.UtcNow;
        var start0 = now.AddSeconds(-(sum0 + 45 + sum2 + 2));
        var end0 = Stamp(gen0, start0);
        var start2 = end0.AddSeconds(45);
        Stamp(gen2, start2);

        SetState(generation: 2, Arma, new[]
        {
            new GenerationInfo(0, Arma, "Arma", start0.AddSeconds(-1), end0),
            new GenerationInfo(1, Discord, "Discord", end0.AddSeconds(1), start2.AddSeconds(-1)),
            new GenerationInfo(2, Arma, "Arma", start2, null),
        });

        var path = await _buffer.SaveLastAsync();

        Assert.NotNull(path);
        Assert.True(File.Exists(path), "clip missing");
        Assert.Empty(_failed);
        Assert.Empty(_errors);
        Assert.Equal(new[] { SaveKind.Clip }, _started);

        var info = Assert.Single(_saved);
        Assert.Equal(path, info.Path);
        var gap = Assert.Single(info.Gaps);
        Assert.Equal(GapReason.OtherTarget, gap.Reason);
        Assert.Equal("Discord", gap.Detail);
        Assert.InRange(gap.Length.TotalSeconds, 44.9, 45.1);
        Assert.Equal(300, info.RequestedSeconds);
        Assert.Equal(new FileInfo(path).Length, info.Bytes);

        // gen 0 complete, gen 2 without its last (still open) segment
        double expected = sum0 + (sum2 - Duration(gen2[^1]));
        Assert.True(Math.Abs(info.Seconds - expected) < 0.01, $"{info.Seconds:0.000} vs {expected:0.000}");

        var probe = Probe(path);
        double outDuration = ChapterCountAndDuration(probe, out int chapters);
        Assert.Equal(2, chapters);
        Assert.True(Math.Abs(outDuration - info.Seconds) < 0.06, $"file {outDuration:0.000} s, reported {info.Seconds:0.000} s");
        Assert.Contains("Alt-Tab: Discord", probe);

        // no leftovers: helper files and the .part are gone, the buffer's own segments are untouched
        Assert.False(File.Exists(Path.Combine(BufferDir, "_concat_list.txt")));
        Assert.False(File.Exists(Path.Combine(BufferDir, "_chapters.txt")));
        Assert.Empty(Directory.GetFiles(ClipsDir, "*.part"));
        Assert.Equal(gen0.Count + gen2.Count, Directory.GetFiles(BufferDir, "seg_*.mp4").Length);
    }

    [Fact]
    public async Task A_clip_without_a_gap_has_no_chapters_and_no_gap_note_material()
    {
        if (_ffmpeg is null) return;
        FfRun.GenerateSegments(_ffmpeg, BufferDir, 0, 40, KeyframeMode.ForcedAtSegmentBoundary);
        var gen0 = FfRun.SegmentsOf(BufferDir, 0);
        var sum = gen0.Sum(f => { Mp4Duration.TryRead(f, out var d); return d.TotalSeconds; });
        Stamp(gen0, DateTime.UtcNow.AddSeconds(-(sum + 2)));
        SetState(0, Arma, new[] { new GenerationInfo(0, Arma, "Arma", DateTime.UtcNow.AddMinutes(-5), null) });

        var path = await _buffer.SaveLastAsync();

        Assert.NotNull(path);
        var info = Assert.Single(_saved);
        Assert.Empty(info.Gaps);
        ChapterCountAndDuration(Probe(path!), out int chapters);
        Assert.Equal(0, chapters);
        Assert.Single(Directory.GetFiles(ClipsDir, "Clip_*.mp4"));
    }

    [Fact]
    public async Task An_unreadable_segment_in_the_middle_is_skipped_and_marked_as_a_gap()
    {
        if (_ffmpeg is null) return;
        FfRun.GenerateSegments(_ffmpeg, BufferDir, 0, 40, KeyframeMode.ForcedAtSegmentBoundary);
        var gen0 = FfRun.SegmentsOf(BufferDir, 0);
        var sum = gen0.Sum(f => { Mp4Duration.TryRead(f, out var d); return d.TotalSeconds; });
        Stamp(gen0, DateTime.UtcNow.AddSeconds(-(sum + 2)));
        // Three segments whose end is lost (a crash, a full disk): bigger than 1 KB, but no moov atom. One segment alone
        // is a 1 s hole here, below the 2 s that count as a gap.
        foreach (var broken in gen0.Skip(20).Take(3))
        {
            var stamp = File.GetLastWriteTimeUtc(broken);
            var bytes = File.ReadAllBytes(broken);
            File.WriteAllBytes(broken, bytes.Take(Math.Max(2048, bytes.Length / 3)).ToArray());
            File.SetLastWriteTimeUtc(broken, stamp);
        }
        SetState(0, Arma, new[] { new GenerationInfo(0, Arma, "Arma", DateTime.UtcNow.AddMinutes(-5), null) });

        var path = await _buffer.SaveLastAsync();

        Assert.NotNull(path);
        var info = Assert.Single(_saved);
        var gap = Assert.Single(info.Gaps);
        Assert.Equal(GapReason.UnreadableSegment, gap.Reason);
        ChapterCountAndDuration(Probe(path!), out int chapters);
        Assert.Equal(2, chapters);
    }

    [Fact]
    public async Task With_stitching_off_only_the_newest_uninterrupted_run_is_saved()
    {
        if (_ffmpeg is null) return;
        _settings.Current.ReplayBuffer.StitchAcrossGaps = false;
        FfRun.GenerateSegments(_ffmpeg, BufferDir, 0, 40, KeyframeMode.ForcedAtSegmentBoundary);
        FfRun.GenerateSegments(_ffmpeg, BufferDir, 2, 40, KeyframeMode.ForcedAtSegmentBoundary);
        var gen0 = FfRun.SegmentsOf(BufferDir, 0);
        var gen2 = FfRun.SegmentsOf(BufferDir, 2);
        double sum0 = gen0.Sum(Duration), sum2 = gen2.Sum(Duration);
        var start0 = DateTime.UtcNow.AddSeconds(-(sum0 + 120 + sum2 + 2));   // a 2 minute hole: far over the old 15 s rule
        var end0 = Stamp(gen0, start0);
        Stamp(gen2, end0.AddSeconds(120));
        SetState(2, Arma, new[]
        {
            new GenerationInfo(0, Arma, "Arma", start0, end0),
            new GenerationInfo(2, Arma, "Arma", end0.AddSeconds(120), null),
        });

        var path = await _buffer.SaveLastAsync();

        Assert.NotNull(path);
        var info = Assert.Single(_saved);
        Assert.Empty(info.Gaps);
        Assert.True(info.Seconds < sum2 + 0.5, $"{info.Seconds:0.0} s saved, the newest run has {sum2:0.0} s");
        ChapterCountAndDuration(Probe(path!), out int chapters);
        Assert.Equal(0, chapters);
    }

    [Fact]
    public async Task Segments_of_another_encoding_are_never_joined_into_the_clip()
    {
        if (_ffmpeg is null) return;
        var hevc = new SegmentIdentity(Arma.Source, "hevc_amf|30|Native|a1|wgc|ForcedAtSegmentBoundary");
        FfRun.GenerateSegments(_ffmpeg, BufferDir, 0, 40, KeyframeMode.ForcedAtSegmentBoundary);
        FfRun.GenerateSegments(_ffmpeg, BufferDir, 1, 40, KeyframeMode.ForcedAtSegmentBoundary);
        var gen0 = FfRun.SegmentsOf(BufferDir, 0);
        var gen1 = FfRun.SegmentsOf(BufferDir, 1);
        double sum0 = gen0.Sum(Duration), sum1 = gen1.Sum(Duration);
        var start0 = DateTime.UtcNow.AddSeconds(-(sum0 + sum1 + 3));
        var end0 = Stamp(gen0, start0);
        Stamp(gen1, end0);   // directly after: no hole at all
        SetState(1, Arma, new[]
        {
            new GenerationInfo(0, hevc, "Arma", start0, end0),   // same window, but another codec
            new GenerationInfo(1, Arma, "Arma", end0, null),
        });

        var path = await _buffer.SaveLastAsync();

        Assert.NotNull(path);
        var info = Assert.Single(_saved);
        Assert.True(info.Seconds < sum1 + 0.5, $"{info.Seconds:0.0} s saved, only {sum1:0.0} s match the current encoding");
    }

    [Fact]
    public async Task A_buffer_that_has_no_complete_segment_yet_refuses_with_not_enough_material()
    {
        if (_ffmpeg is null) return;
        SetState(0, Arma, new[] { new GenerationInfo(0, Arma, "Arma", DateTime.UtcNow, null) });

        var path = await _buffer.SaveLastAsync();

        Assert.Null(path);
        Assert.Empty(_started);
        Assert.Equal(new[] { new SaveFailure(SaveKind.Clip, SaveFailureReason.NotEnoughMaterial) }, _failed);
    }

    [Fact]
    public async Task The_gif_save_still_works_over_the_new_segment_scan()
    {
        if (_ffmpeg is null) return;
        FfRun.GenerateSegments(_ffmpeg, BufferDir, 0, 30, KeyframeMode.ForcedAtSegmentBoundary);
        var gen0 = FfRun.SegmentsOf(BufferDir, 0);
        double sum = gen0.Sum(Duration);
        Stamp(gen0, DateTime.UtcNow.AddSeconds(-(sum + 2)));
        SetState(0, Arma, new[] { new GenerationInfo(0, Arma, "Arma", DateTime.UtcNow.AddMinutes(-5), null) });
        _settings.Current.Gif.DurationSeconds = 3;

        var path = await _buffer.SaveGifAsync();

        Assert.NotNull(path);
        Assert.EndsWith(".gif", path);
        Assert.True(new FileInfo(path!).Length > 1000);
        Assert.Empty(_failed);
    }

    // ---- retention and folder change, through the service's own state ----

    private void Invoke(string method)
        => typeof(ReplayBufferService).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_buffer, null);

    [Fact]
    public void Prune_drops_a_previous_encoding_at_once_and_keeps_the_same_encoding_inside_the_window()
    {
        var hevc = new SegmentIdentity(Arma.Source, "hevc_amf|30|Native|a1|wgc|ForcedAtSegmentBoundary");
        var now = DateTime.UtcNow;
        string Make(string name, double ageSeconds)
        {
            var p = Path.Combine(BufferDir, name);
            File.WriteAllBytes(p, new byte[4096]);
            File.SetLastWriteTimeUtc(p, now.AddSeconds(-ageSeconds));
            return p;
        }
        var otherCodec = Make("seg_0_00.mp4", 120);     // gen 0: another codec -> gone immediately
        var sameStream = Make("seg_1_00.mp4", 120);     // gen 1: same stream, inside the 300 s window -> kept
        var tooOld = Make("seg_1_01.mp4", 2000);        // beyond the age window -> gone
        var current = Make("seg_2_00.mp4", 5);          // the running generation -> kept
        var foreign = Make("holiday.mp4", 9000);        // not a segment name -> never touched
        SetStateWithoutProcess(2, Arma, new[]
        {
            new GenerationInfo(0, hevc, "Arma", now.AddMinutes(-30), now.AddMinutes(-20)),
            new GenerationInfo(1, Arma, "Arma", now.AddMinutes(-20), now.AddMinutes(-10)),
            new GenerationInfo(2, Arma, "Arma", now.AddMinutes(-10), null),
        });

        Invoke("PruneOldSegments");

        Assert.False(File.Exists(otherCodec));
        Assert.True(File.Exists(sameStream));
        Assert.False(File.Exists(tooOld));
        Assert.True(File.Exists(current));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void An_alt_tab_to_a_window_capture_does_not_make_prune_delete_the_games_footage()
    {
        var settingsKey = "h264_amf|60|Native|{0}|{1}|EncoderDefault";
        var game = new SegmentIdentity("mon:0:3440x1440", string.Format(settingsKey, "a2", "dda"));   // exclusive fullscreen
        var discord = new SegmentIdentity("wgc:200:1280x720", string.Format(settingsKey, "a1", "wgc")); // Alt-Tab target
        var now = DateTime.UtcNow;
        var gameSeg = SegFile("seg_0_00.mp4", 120);   // inside the 5 minute window
        var discordSeg = SegFile("seg_1_00.mp4", 5);
        SetStateWithoutProcess(1, discord, new[]
        {
            new GenerationInfo(0, game, "Arma", now.AddMinutes(-20), now.AddMinutes(-1)),
            new GenerationInfo(1, discord, "Discord", now.AddMinutes(-1), null),
        });

        Invoke("PruneOldSegments");   // the prune at the end of the restart that started the Discord generation

        Assert.True(File.Exists(gameSeg), "the whole lookback of the game was deleted by an Alt-Tab");
        Assert.True(File.Exists(discordSeg));
    }

    [Fact]
    public async Task Moving_the_buffer_folder_empties_the_old_one_in_the_background_and_records_the_new_one()
    {
        var oldDir = Path.Combine(_dir, "old_buffer");
        Directory.CreateDirectory(oldDir);
        File.WriteAllBytes(Path.Combine(oldDir, "seg_0_00.mp4"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(oldDir, "_concat_list.txt"), new byte[10]);
        File.WriteAllBytes(Path.Combine(oldDir, "notes.txt"), new byte[10]);
        new BufferFolderRecord(_root).Write(oldDir);
        File.WriteAllBytes(Path.Combine(BufferDir, "seg_0_00.mp4"), new byte[2048]);   // leftover in the NEW folder

        Field("_bufferDir").SetValue(_buffer, BufferDir);
        var moved = (bool)typeof(ReplayBufferService)
            .GetMethod("HandleBufferFolderChange", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_buffer, null)!;

        Assert.True(moved);
        Assert.Equal(BufferDir, new BufferFolderRecord(_root).Read());
        Assert.False(File.Exists(Path.Combine(BufferDir, "seg_0_00.mp4")));   // the new folder starts fresh
        for (int i = 0; i < 50 && BufferFolderRules.HasOwnFiles(oldDir); i++) await Task.Delay(100);   // files go one by one
        Assert.False(File.Exists(Path.Combine(oldDir, "seg_0_00.mp4")));
        Assert.False(File.Exists(Path.Combine(oldDir, "_concat_list.txt")));
        Assert.True(File.Exists(Path.Combine(oldDir, "notes.txt")));            // not ours
    }

    [Fact]
    public void Starting_in_the_recorded_folder_changes_nothing()
    {
        new BufferFolderRecord(_root).Write(BufferDir);
        File.WriteAllBytes(Path.Combine(BufferDir, "seg_0_00.mp4"), new byte[2048]);
        Field("_bufferDir").SetValue(_buffer, BufferDir);

        var moved = (bool)typeof(ReplayBufferService)
            .GetMethod("HandleBufferFolderChange", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_buffer, null)!;

        Assert.False(moved);
        Assert.True(File.Exists(Path.Combine(BufferDir, "seg_0_00.mp4")));
    }

    // ---- a save works on ONE snapshot of the running recording ----

    private object? InvokeScan(string method, BufferEpoch epoch, int arg, DateTime now)
        => typeof(ReplayBufferService).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_buffer, new object[] { epoch, arg, now });

    [Fact]
    public void A_clip_scan_uses_its_snapshot_even_when_a_restart_changes_the_live_recording_underneath()
    {
        if (_ffmpeg is null) return;
        // Auto mode: the game (gen 0), then an Alt-Tab to Discord (gen 1, another resolution) ~2 s before F9.
        FfRun.GenerateSegments(_ffmpeg, BufferDir, 0, 30, KeyframeMode.ForcedAtSegmentBoundary);
        FfRun.GenerateSegments(_ffmpeg, BufferDir, 1, 6, KeyframeMode.ForcedAtSegmentBoundary);   // the few seconds since the Alt-Tab
        var gen0 = FfRun.SegmentsOf(BufferDir, 0);
        var gen1 = FfRun.SegmentsOf(BufferDir, 1);
        var now = DateTime.UtcNow;
        var start0 = now.AddSeconds(-(gen0.Sum(Duration) + gen1.Sum(Duration) + 1));
        var end0 = Stamp(gen0, start0);
        Stamp(gen1, end0);
        SetStateWithoutProcess(0, Arma, new[]
        {
            new GenerationInfo(0, Arma, "Arma", start0, end0),
            new GenerationInfo(1, Discord, "Discord", end0, null),
        });
        var snapshot = (BufferEpoch)Field("_epoch").GetValue(_buffer)!;   // what the press saw: the game

        // The warm restart lands while the scan is still running: the live state now says Discord.
        var ring = RingPlan.Create(300, 1);
        Field("_currentIdentity").SetValue(_buffer, Discord);
        Field("_generation").SetValue(_buffer, 1);
        Field("_epoch").SetValue(_buffer, new BufferEpoch(ring, Discord, 1, BufferDir));

        var scan = InvokeScan("ScanForClip", snapshot, 300, now)!;
        var plan = (StitchPlan)scan.GetType().GetProperty("Plan")!.GetValue(scan)!;

        Assert.NotEmpty(plan.Segments);
        Assert.All(plan.Segments, s => Assert.StartsWith("seg_0_", Path.GetFileName(s.Path)));   // the game only, never a Discord segment
        Assert.Equal(gen0.Count - 1, plan.Segments.Count);   // minus the newest one: still "open" for generation 0
    }

    [Fact]
    public void A_gif_scan_uses_its_snapshot_too()
    {
        if (_ffmpeg is null) return;
        FfRun.GenerateSegments(_ffmpeg, BufferDir, 0, 30, KeyframeMode.ForcedAtSegmentBoundary);
        FfRun.GenerateSegments(_ffmpeg, BufferDir, 1, 6, KeyframeMode.ForcedAtSegmentBoundary);   // the few seconds since the Alt-Tab
        var gen0 = FfRun.SegmentsOf(BufferDir, 0);
        var gen1 = FfRun.SegmentsOf(BufferDir, 1);
        var now = DateTime.UtcNow;
        var start0 = now.AddSeconds(-(gen0.Sum(Duration) + gen1.Sum(Duration) + 1));
        var end0 = Stamp(gen0, start0);
        Stamp(gen1, end0);
        SetStateWithoutProcess(0, Arma, new[]
        {
            new GenerationInfo(0, Arma, "Arma", start0, end0),
            new GenerationInfo(1, Discord, "Discord", end0, null),
        });
        var snapshot = (BufferEpoch)Field("_epoch").GetValue(_buffer)!;
        Field("_currentIdentity").SetValue(_buffer, Discord);
        Field("_generation").SetValue(_buffer, 1);
        Field("_epoch").SetValue(_buffer, new BufferEpoch(RingPlan.Create(300, 1), Discord, 1, BufferDir));

        var scan = InvokeScan("ScanForGif", snapshot, 5, now)!;
        var pick = (IReadOnlyList<SegmentCandidate>)scan.GetType().GetProperty("Pick")!.GetValue(scan)!;

        Assert.NotEmpty(pick);
        Assert.All(pick, s => Assert.StartsWith("seg_0_", Path.GetFileName(s.Path)));
    }

    // ---- switching the buffer off and on while a clip is being written ----

    private int Generation => (int)Field("_generation").GetValue(_buffer)!;

    private string SegFile(string name, double ageSeconds)
    {
        var p = Path.Combine(BufferDir, name);
        File.WriteAllBytes(p, new byte[4096]);
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddSeconds(-ageSeconds));
        return p;
    }

    [Fact]
    public void A_cold_start_while_a_clip_is_written_keeps_its_segments_and_never_reuses_a_generation_number()
    {
        var a = SegFile("seg_0_00.mp4", 30);
        var b = SegFile("seg_0_01.mp4", 25);
        SetStateWithoutProcess(0, Arma, new[] { new GenerationInfo(0, Arma, "Arma", DateTime.UtcNow.AddMinutes(-5), null) });
        Field("_saving").SetValue(_buffer, 1);   // the concat is reading these files

        Invoke("ResetHistoryForColdStart");

        Assert.True(File.Exists(a) && File.Exists(b), "the running save lost its input");
        Assert.Equal(1, Generation);   // the next run writes seg_1_*, not seg_0_* over the files being read

        // after the save the retention rules remove them: everything older than the new session is unusable
        Field("_saving").SetValue(_buffer, 0);
        Invoke("PruneOldSegments");
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
    }

    [Fact]
    public void A_cold_start_without_a_save_clears_the_folder_at_once_and_still_counts_on()
    {
        var a = SegFile("seg_0_00.mp4", 30);
        SetStateWithoutProcess(4, Arma, new[] { new GenerationInfo(4, Arma, "Arma", DateTime.UtcNow.AddMinutes(-5), null) });

        Invoke("ResetHistoryForColdStart");

        Assert.False(File.Exists(a));
        Assert.Equal(5, Generation);
    }

    [Fact]
    public void Moving_the_buffer_folder_during_a_save_leaves_the_new_folder_alone_until_the_save_is_over()
    {
        var oldDir = Path.Combine(_dir, "old_buffer");
        Directory.CreateDirectory(oldDir);
        File.WriteAllBytes(Path.Combine(oldDir, "seg_0_00.mp4"), new byte[2048]);
        new BufferFolderRecord(_root).Write(oldDir);
        var leftover = SegFile("seg_0_00.mp4", 600);   // an old file in the NEW folder
        Field("_bufferDir").SetValue(_buffer, BufferDir);
        Field("_saving").SetValue(_buffer, 1);

        var moved = (bool)typeof(ReplayBufferService)
            .GetMethod("HandleBufferFolderChange", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_buffer, null)!;

        Assert.True(moved);
        Assert.True(File.Exists(leftover));
        Assert.True(File.Exists(Path.Combine(oldDir, "seg_0_00.mp4")));   // the save may still be reading the old folder
        Assert.Equal(new[] { oldDir }, new BufferFolderRecord(_root).ReadPending());
        Field("_saving").SetValue(_buffer, 0);
    }

    [Fact]
    public async Task The_old_folder_cleanup_waits_for_a_running_save_however_long_it_takes()
    {
        var oldDir = Path.Combine(_dir, "old_buffer");
        Directory.CreateDirectory(oldDir);
        File.WriteAllBytes(Path.Combine(oldDir, "seg_0_00.mp4"), new byte[2048]);
        new BufferFolderRecord(_root).Write(oldDir);
        Field("_bufferDir").SetValue(_buffer, BufferDir);
        Field("_saving").SetValue(_buffer, 1);
        typeof(ReplayBufferService).GetMethod("HandleBufferFolderChange", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_buffer, null);

        await Task.Delay(1500);   // several polls later: still waiting, nothing deleted, nothing given up
        Assert.True(File.Exists(Path.Combine(oldDir, "seg_0_00.mp4")));
        Assert.Equal(new[] { oldDir }, new BufferFolderRecord(_root).ReadPending());

        Field("_saving").SetValue(_buffer, 0);   // the save ends
        for (int i = 0; i < 50 && File.Exists(Path.Combine(oldDir, "seg_0_00.mp4")); i++) await Task.Delay(100);
        Assert.False(File.Exists(Path.Combine(oldDir, "seg_0_00.mp4")));
        for (int i = 0; i < 50 && new BufferFolderRecord(_root).ReadPending().Count > 0; i++) await Task.Delay(100);
        Assert.Empty(new BufferFolderRecord(_root).ReadPending());   // done: nothing left to retry
    }

    [Fact]
    public async Task A_folder_left_pending_by_an_earlier_run_is_emptied_at_the_first_start()
    {
        var oldDir = Path.Combine(_dir, "old_buffer");
        Directory.CreateDirectory(oldDir);
        File.WriteAllBytes(Path.Combine(oldDir, "seg_3_07.mp4"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(oldDir, "notes.txt"), new byte[10]);
        var record = new BufferFolderRecord(_root);
        record.Write(BufferDir);            // the record already names the current folder ...
        record.AddPending(oldDir);          // ... but the earlier run never finished emptying this one
        Field("_bufferDir").SetValue(_buffer, BufferDir);

        var moved = (bool)typeof(ReplayBufferService)
            .GetMethod("HandleBufferFolderChange", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_buffer, null)!;

        Assert.False(moved);   // the current folder did not change: no fresh start for it
        for (int i = 0; i < 50 && BufferFolderRules.HasOwnFiles(oldDir); i++) await Task.Delay(100);
        Assert.False(File.Exists(Path.Combine(oldDir, "seg_3_07.mp4")));
        Assert.True(File.Exists(Path.Combine(oldDir, "notes.txt")));
        for (int i = 0; i < 50 && record.ReadPending().Count > 0; i++) await Task.Delay(100);
        Assert.Empty(record.ReadPending());
    }

    [Fact]
    public void A_pending_entry_for_the_folder_now_in_use_is_dropped_and_never_emptied()
    {
        var record = new BufferFolderRecord(_root);
        record.Write(BufferDir);
        record.AddPending(BufferDir);
        var seg = SegFile("seg_0_00.mp4", 5);
        Field("_bufferDir").SetValue(_buffer, BufferDir);

        typeof(ReplayBufferService).GetMethod("HandleBufferFolderChange", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_buffer, null);

        Assert.True(File.Exists(seg));
        Assert.Empty(record.ReadPending());
    }

    [Fact]
    public void A_registry_value_that_never_updates_cannot_empty_the_folder_in_use_on_every_restart()
    {
        // The first start finds a stale record (the previous run used another folder) - a real move. The write of the
        // new value then keeps failing (read-only key here), so the record stays stale for the rest of the process.
        var stale = Path.Combine(_dir, "earlier_folder");
        Directory.CreateDirectory(stale);
        new BufferFolderRecord(_root).Write(stale);
        using var readOnlyRoot = Registry.CurrentUser.OpenSubKey(_rootPath, writable: false)!;
        using var buffer = new ReplayBufferService(_settings, new BufferFolderRecord(readOnlyRoot));
        typeof(ReplayBufferService).GetField("_bufferDir", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(buffer, BufferDir);
        var handle = typeof(ReplayBufferService).GetMethod("HandleBufferFolderChange", BindingFlags.NonPublic | BindingFlags.Instance)!;

        Assert.True((bool)handle.Invoke(buffer, null)!);           // first start: legitimately a move
        var seg = SegFile("seg_0_00.mp4", 5);                      // the buffer is recording into BufferDir now
        Assert.False((bool)handle.Invoke(buffer, null)!);          // warm restart: the record is stale, but not trusted again
        Assert.False((bool)handle.Invoke(buffer, null)!);

        Assert.True(File.Exists(seg), "a warm restart emptied the folder in use");
    }

    private void SetStateWithoutProcess(int generation, SegmentIdentity current, IEnumerable<GenerationInfo> generations)
    {
        Field("_bufferDir").SetValue(_buffer, BufferDir);
        Field("_generation").SetValue(_buffer, generation);
        Field("_currentIdentity").SetValue(_buffer, current);
        Field("_runningRing").SetValue(_buffer, RingPlan.Create(300, 1));
        Field("_epoch").SetValue(_buffer, new BufferEpoch(RingPlan.Create(300, 1), current, generation, BufferDir));
        Field("_sessionStartUtc").SetValue(_buffer, DateTime.UtcNow.AddHours(-1));
        var map = (ConcurrentDictionary<int, GenerationInfo>)Field("_generations").GetValue(_buffer)!;
        foreach (var g in generations) map[g.Generation] = g;
    }

    private static double Duration(string file)
    {
        Assert.True(Mp4Duration.TryRead(file, out var d), file);
        return d.TotalSeconds;
    }
}
