using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using WKI_Clipper.Services;
using Xunit;
using Xunit.Abstractions;

namespace WKI_Clipper.Tests;

/// <summary>MPEG-TS test footage made by the real ffmpeg (flash + beep at the start of every second, key frame every second).</summary>
internal static class TsFootage
{
    public static string Args(int seconds, string output, bool realtime = false)
        => "-hide_banner -loglevel error " + (realtime ? "-re " : "") +
           "-f lavfi -i \"color=c=black:s=160x90:r=30,drawbox=x=0:y=0:w=iw:h=ih:color=white:t=fill:enable='lt(mod(t\\,1)\\,0.03)'\" " +
           (realtime ? "-re " : "") +
           "-f lavfi -i \"aevalsrc='if(lt(mod(t,1),0.03),0.8*sin(2*PI*1000*t),0)':s=48000:c=stereo\" " +
           $"-t {seconds} -map 0:v -map 1:a -c:v libx264 -preset ultrafast -tune zerolatency " +
           KeyframePolicy.EncoderArgs("libx264", 30, 1, KeyframeMode.ForcedAtSegmentBoundary) +
           " -c:a aac -b:a 192k " +
           (output == "pipe:1" ? FFmpegCommandBuilder.MpegTsPipeMuxerArgs : $"-f mpegts -pes_payload_size 0 -y \"{output}\"");

    public static byte[] Make(string ffmpeg, string dir, string name, int seconds)
    {
        var path = Path.Combine(dir, name);
        var (code, err) = FfRun.Run(ffmpeg, Args(seconds, path));
        Assert.True(code == 0, "TS generator failed: " + err);
        return File.ReadAllBytes(path);
    }

    /// <summary>Feeds bytes through the segmenter into a writer, in chunks of the given sizes (cycled).</summary>
    public static void Feed(MemoryGenerationWriter writer, byte[] ts, int[] chunks, bool graceful = true)
    {
        var seg = new MpegTsSegmenter(writer, 1, KeyframePolicy.SegmentTimeDelta(30, KeyframeMode.ForcedAtSegmentBoundary));
        int pos = 0, k = 0;
        while (pos < ts.Length)
        {
            int n = Math.Min(chunks[k++ % chunks.Length], ts.Length - pos);
            seg.Feed(ts.AsSpan(pos, n));
            pos += n;
        }
        writer.Finish(graceful, seg.EstimatedEndPts);
    }
}

/// <summary>Collects segments in a list (segmenter tests without a store).</summary>
internal sealed class ListSink : ITsSegmentSink
{
    public readonly List<(long Start, long End, bool Pat, byte[] Bytes)> Done = new();
    private MemoryStream? _open;
    private long _start;
    private bool _pat;
    public void SegmentStarted(long startPts, byte[] psi, bool startsWithPat) { _open = new MemoryStream(); _start = startPts; _pat = startsWithPat; }
    public void Append(ReadOnlySpan<byte> bytes) => _open?.Write(bytes);
    public void SegmentEnded(long endPts) { if (_open != null) Done.Add((_start, endPts, _pat, _open.ToArray())); _open = null; }
}

public sealed class TsFootageFixture : IDisposable
{
    public string? Ffmpeg { get; } = FFmpegTestSupport.Find();
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "wki_ts_" + Guid.NewGuid().ToString("N"));
    public byte[] Forty { get; } = Array.Empty<byte>();
    public byte[] Twenty { get; } = Array.Empty<byte>();

    public TsFootageFixture()
    {
        Directory.CreateDirectory(Dir);
        if (Ffmpeg is null) return;
        Forty = TsFootage.Make(Ffmpeg, Dir, "forty.ts", 40);
        Twenty = TsFootage.Make(Ffmpeg, Dir, "twenty.ts", 20);
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch { }
    }
}

/// <summary>
/// The RAM buffer with the real ffmpeg: ffmpeg-made MPEG-TS split at key frames, segments that decode on their own (also
/// from the AMD encoders when present), and F9/F8 from the RAM ring end to end - length, chapters, gaps and A/V sync.
/// Every test returns early without ffmpeg.
/// </summary>
public sealed class MemoryBufferIntegrationTests : IClassFixture<TsFootageFixture>, IDisposable
{
    private const double Frame = 1.0 / 30;
    private static readonly SegmentIdentity Arma = new("wgc:100:160x90", "libx264|30|Native|a1|wgc|ForcedAtSegmentBoundary");
    private static readonly SegmentIdentity Discord = new("wgc:200:160x90", Arma.Stream);

    private readonly TsFootageFixture _fx;
    private readonly ITestOutputHelper _out;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_ram_" + Guid.NewGuid().ToString("N"));
    private readonly string _rootPath = @"Software\WKI_Clipper_Tests_RAM_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;
    private readonly SettingsService _settings;
    private readonly ReplayBufferService _buffer;
    private FFmpegService? _fakeRunning;
    private readonly List<ReplaySavedInfo> _saved = new();
    private readonly List<SaveFailure> _failed = new();
    private readonly List<string> _errors = new();
    private readonly List<SaveKind> _started = new();

    private string BufferDir => Path.Combine(_dir, "buffer");
    private string ClipsDir => Path.Combine(_dir, "clips");
    private string? Ffmpeg => _fx.Ffmpeg;

    public MemoryBufferIntegrationTests(TsFootageFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
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
        _buffer = new ReplayBufferService(_settings, new BufferFolderRecord(_root)) { FfmpegPathOverride = Ffmpeg };
        _buffer.ReplaySaved += (_, i) => _saved.Add(i);
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
        for (int i = 0; i < 20; i++)
        {
            try { Directory.Delete(_dir, recursive: true); return; }
            catch (DirectoryNotFoundException) { return; }
            catch { Thread.Sleep(100); }
        }
    }

    // ---- segmenter ----

    [Fact]
    public void Ffmpeg_ts_is_split_at_every_key_frame_into_segments_that_start_with_pat_pmt()
    {
        if (Ffmpeg is null) return;
        var whole = new ListSink();
        var s1 = new MpegTsSegmenter(whole, 1, KeyframePolicy.SegmentTimeDelta(30, KeyframeMode.ForcedAtSegmentBoundary));
        s1.Feed(_fx.Forty);
        Assert.Equal(0x1B, s1.VideoStreamType);

        Assert.InRange(whole.Done.Count, 38, 40);
        Assert.All(whole.Done, d =>
        {
            Assert.True(d.Pat, "segment without its own PAT");
            Assert.Equal(0, d.Bytes.Length % MpegTs.PacketSize);
            Assert.Equal(MpegTs.SyncByte, d.Bytes[0]);
            Assert.Equal(90_000, d.End - d.Start);   // a forced key frame every second at 30 fps
        });
        for (int i = 1; i < whole.Done.Count; i++) Assert.Equal(whole.Done[i - 1].End, whole.Done[i].Start);

        // Arbitrary pipe reads (odd sizes, split packets) give byte-identical segments.
        var chunked = new ListSink();
        var s2 = new MpegTsSegmenter(chunked, 1, KeyframePolicy.SegmentTimeDelta(30, KeyframeMode.ForcedAtSegmentBoundary));
        var rnd = new Random(7);
        for (int pos = 0; pos < _fx.Forty.Length;)
        {
            int n = Math.Min(rnd.Next(1, 70_000), _fx.Forty.Length - pos);
            s2.Feed(_fx.Forty.AsSpan(pos, n));
            pos += n;
        }
        Assert.Equal(whole.Done.Count, chunked.Done.Count);
        for (int i = 0; i < whole.Done.Count; i++) Assert.True(whole.Done[i].Bytes.AsSpan().SequenceEqual(chunked.Done[i].Bytes), $"segment {i} differs");
    }

    [Fact]
    public void A_single_ram_segment_decodes_on_its_own()
    {
        if (Ffmpeg is null) return;
        var sink = new ListSink();
        new MpegTsSegmenter(sink, 1).Feed(_fx.Forty);
        var path = Path.Combine(_dir, "one.ts");
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(path, sink.Done[17].Bytes);
        var (code, err) = FfRun.Run(Ffmpeg, $"-hide_banner -v error -i \"{path}\" -f null -");
        Assert.Equal(0, code);
        Assert.True(string.IsNullOrWhiteSpace(err), err);
    }

    [Theory]
    [InlineData("h264_amf", (byte)0x1B)]
    [InlineData("hevc_amf", (byte)0x24)]
    public async Task Amd_encoders_flag_their_idr_frames_so_the_ram_ring_can_cut_there(string codec, byte streamType)
    {
        if (Ffmpeg is null || !await new FFmpegService(Ffmpeg).HasEncoderAsync(codec)) return;
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, codec + ".ts");
        var (code, err) = FfRun.Run(Ffmpeg,
            "-hide_banner -loglevel error -f lavfi -i testsrc2=s=1280x720:r=30 -f lavfi -i sine=f=1000:r=48000 -t 6 " +
            $"-map 0:v -map 1:a -c:v {codec} -quality balanced -rc cbr -b:v 8M " +
            KeyframePolicy.EncoderArgs(codec, 30, 1, KeyframeMode.ForcedAtSegmentBoundary) +
            $" -c:a aac -b:a 192k -ac 2 -f mpegts -pes_payload_size 0 -y \"{path}\"");
        Assert.True(code == 0, err);

        var sink = new ListSink();
        var seg = new MpegTsSegmenter(sink, 1, KeyframePolicy.SegmentTimeDelta(30, KeyframeMode.ForcedAtSegmentBoundary));
        seg.Feed(File.ReadAllBytes(path));
        Assert.Equal(streamType, seg.VideoStreamType);
        Assert.True(sink.Done.Count >= 4, $"{codec}: only {sink.Done.Count} segments - key frames not flagged?");
        Assert.All(sink.Done, d => Assert.InRange((d.End - d.Start) / 90_000.0, 0.95, 1.05));

        var one = Path.Combine(_dir, codec + "_one.ts");
        File.WriteAllBytes(one, sink.Done[2].Bytes);
        var (dc, derr) = FfRun.Run(Ffmpeg, $"-hide_banner -v error -i \"{one}\" -f null -");
        Assert.True(dc == 0 && string.IsNullOrWhiteSpace(derr), $"{codec} segment does not decode alone: {derr}");
    }

    [Fact]
    public async Task Av1_does_not_come_back_from_mpegts_as_video_so_it_records_to_disk()
    {
        if (Ffmpeg is null || !await new FFmpegService(Ffmpeg).HasEncoderAsync("av1_amf")) return;
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "av1.ts");
        var (code, err) = FfRun.Run(Ffmpeg, "-hide_banner -loglevel error -f lavfi -i testsrc2=s=1280x720:r=30 -t 2 " +
                                            $"-c:v av1_amf -b:v 8M -f mpegts -y \"{path}\"");
        Assert.True(code == 0, err);
        var probe = FfRun.Run(Ffmpeg, $"-hide_banner -i \"{path}\"").StdErr;
        Assert.DoesNotContain("Video:", probe);
        Assert.False(MpegTs.SupportsCodec("av1_amf"));
    }

    // ---- F9 / F8 from RAM through the service ----

    private static FieldInfo Field(string name)
        => typeof(ReplayBufferService).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
           ?? throw new MissingFieldException(nameof(ReplayBufferService), name);

    private void SetMemoryState(MemorySegmentStore store, int generation, SegmentIdentity current, IEnumerable<GenerationInfo> generations)
    {
        var ring = RingPlan.Create(300, 1);
        Field("_bufferDir").SetValue(_buffer, BufferDir);
        Field("_generation").SetValue(_buffer, generation);
        Field("_currentIdentity").SetValue(_buffer, current);
        Field("_runningRing").SetValue(_buffer, ring);
        Field("_memory").SetValue(_buffer, store);
        Field("_epoch").SetValue(_buffer, new BufferEpoch(ring, current, generation, BufferDir, store));
        Field("_sessionStartUtc").SetValue(_buffer, DateTime.UtcNow.AddHours(-1));
        var map = (ConcurrentDictionary<int, GenerationInfo>)Field("_generations").GetValue(_buffer)!;
        foreach (var g in generations) map[g.Generation] = g;

        _fakeRunning = new FFmpegService(Ffmpeg);
        _fakeRunning.Start("-hide_banner -loglevel error -re -f lavfi -i anullsrc -t 120 -f null -");
        Thread.Sleep(200);
        Assert.True(_fakeRunning.IsRunning, "the stand-in ffmpeg exited");
        Field("_ffmpeg").SetValue(_buffer, _fakeRunning);
    }

    /// <summary>Segments of one generation get end times that follow their durations from <paramref name="start"/> on.</summary>
    private static DateTime Stamp(IEnumerable<MemorySegment> segments, DateTime start)
    {
        var t = start;
        foreach (var s in segments)
        {
            t += s.Duration;
            s.EndUtc = t;
        }
        return t;
    }

    private string Probe(string file) => FfRun.Run(Ffmpeg!, $"-hide_banner -i \"{file}\"").StdErr;

    private static double ProbeSeconds(string probe) => FFmpegProbeParser.Parse(probe)?.Duration?.TotalSeconds ?? -1;

    private void AssertNoLeftovers()
    {
        Assert.Empty(Directory.GetFiles(ClipsDir, "*.wkitmp.*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(ClipsDir, "*.part", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(BufferDir));   // RAM mode writes nothing into the buffer folder
    }

    private void AssertInSync(string clip, int minPairs)
    {
        var pairs = FfRun.Pair(FfRun.FlashOnsets(Ffmpeg!, clip), FfRun.BeepOnsets(Ffmpeg!, clip));
        Assert.True(pairs.Count >= minPairs, $"only {pairs.Count} flash/beep pairs");
        double first = FfRun.Median(pairs.Take(5).Select(p => p.Delta));
        double last = FfRun.Median(pairs.TakeLast(5).Select(p => p.Delta));
        _out.WriteLine($"A/V offset: start {first * 1000:0.0} ms, end {last * 1000:0.0} ms ({pairs.Count} pairs)");
        Assert.True(Math.Abs(first) < Frame, $"audio/video offset at the start: {first * 1000:0.0} ms");
        Assert.True(Math.Abs(last) < Frame, $"audio/video offset at the end: {last * 1000:0.0} ms");
    }

    [Fact]
    public async Task F9_from_ram_with_the_real_reader_thread_gives_an_mp4_of_the_right_length_in_sync()
    {
        if (Ffmpeg is null) return;
        using var store = new MemorySegmentStore(256L << 20);
        // ffmpeg in real time onto stdout, drained by the same reader the buffer uses.
        using var producer = new FFmpegService(Ffmpeg);
        producer.Start(TsFootage.Args(9, "pipe:1", realtime: true));
        var feed = new MemoryBufferFeed(producer.StandardOutputStream!, store.OpenWriter(0), 1,
            KeyframePolicy.SegmentTimeDelta(30, KeyframeMode.ForcedAtSegmentBoundary), () => producer.StopRequested);
        feed.Start();
        Assert.True(feed.WaitForEnd(TimeSpan.FromSeconds(40)), "reader did not finish");
        feed.Dispose();

        var segs = store.Snapshot();
        Assert.InRange(segs.Count, 7, 9);   // ffmpeg ended on its own: the open last segment is dropped
        // Real time: the segments closed about a second apart.
        Assert.InRange((segs[^1].EndUtc - segs[0].EndUtc).TotalSeconds, segs.Count - 2.5, segs.Count + 0.5);

        SetMemoryState(store, 0, Arma, new[] { new GenerationInfo(0, Arma, "Arma", DateTime.UtcNow.AddMinutes(-1), null) });
        var path = await _buffer.SaveLastAsync();

        Assert.NotNull(path);
        Assert.Empty(_failed);
        Assert.Empty(_errors);
        Assert.Equal(new[] { SaveKind.Clip }, _started);
        var info = Assert.Single(_saved);
        Assert.Empty(info.Gaps);
        double expected = segs.Sum(s => s.Duration.TotalSeconds);
        Assert.Equal(expected, info.Seconds, 3);
        double file = ProbeSeconds(Probe(path!));
        Assert.True(Math.Abs(file - info.Seconds) < 2 * Frame, $"file {file:0.000} s, reported {info.Seconds:0.000} s");
        AssertInSync(path!, 5);
        AssertNoLeftovers();
        Assert.Equal(0, store.Snapshot().Sum(s => s.Pins));   // the save let go of its pins
    }

    [Fact]
    public async Task A_ram_clip_across_an_alt_tab_gap_has_two_chapters_the_gap_note_and_stays_in_sync()
    {
        if (Ffmpeg is null) return;
        using var store = new MemorySegmentStore(256L << 20);
        TsFootage.Feed(store.OpenWriter(0), _fx.Twenty, new[] { 65_536 });
        var gen0 = store.Snapshot().ToList();
        TsFootage.Feed(store.OpenWriter(2), _fx.Twenty, new[] { 100_000, 7_777 });
        var gen2 = store.Snapshot().Where(s => s.Generation == 2).ToList();

        double sum0 = gen0.Sum(s => s.Duration.TotalSeconds), sum2 = gen2.Sum(s => s.Duration.TotalSeconds);
        var now = DateTime.UtcNow;
        var start0 = now.AddSeconds(-(sum0 + 45 + sum2 + 1));
        var end0 = Stamp(gen0, start0);
        var start2 = end0.AddSeconds(45);
        Stamp(gen2, start2);
        SetMemoryState(store, 2, Arma, new[]
        {
            new GenerationInfo(0, Arma, "Arma", start0.AddSeconds(-1), end0),
            new GenerationInfo(1, Discord, "Discord", end0.AddSeconds(1), start2.AddSeconds(-1)),
            new GenerationInfo(2, Arma, "Arma", start2, null),
        });

        var path = await _buffer.SaveLastAsync();

        Assert.NotNull(path);
        Assert.Empty(_failed);
        var info = Assert.Single(_saved);
        var gap = Assert.Single(info.Gaps);
        Assert.Equal(GapReason.OtherTarget, gap.Reason);
        Assert.Equal("Discord", gap.Detail);
        Assert.InRange(gap.Length.TotalSeconds, 44.9, 45.1);
        Assert.Equal(sum0 + sum2, info.Seconds, 3);

        var probe = Probe(path!);
        Assert.Equal(2, Regex.Matches(probe, @"Chapter #0:\d+:").Count);
        Assert.Contains("Alt-Tab: Discord", probe);
        double file = ProbeSeconds(probe);
        Assert.True(Math.Abs(file - info.Seconds) < 2 * Frame, $"file {file:0.000} s, reported {info.Seconds:0.000} s");

        // The second chapter starts exactly where generation 0 ends, and the first flash after it is the cut.
        var chapterStart = Regex.Match(probe, @"Chapter #0:1: start ([0-9.]+)");
        Assert.True(chapterStart.Success, probe);
        double c1 = double.Parse(chapterStart.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(Math.Abs(c1 - sum0) <= Frame, $"chapter 2 at {c1:0.000}, generation 0 lasts {sum0:0.000}");
        AssertInSync(path!, 30);
        AssertNoLeftovers();
    }

    [Fact]
    public async Task A_per_press_length_saves_its_own_seconds_and_never_more_than_the_ring_holds()
    {
        if (Ffmpeg is null) return;
        using var store = new MemorySegmentStore(256L << 20);
        TsFootage.Feed(store.OpenWriter(0), _fx.Forty, new[] { 65_536 });
        Stamp(store.Snapshot(), DateTime.UtcNow.AddSeconds(-41));
        SetMemoryState(store, 0, Arma, new[] { new GenerationInfo(0, Arma, "Arma", DateTime.UtcNow.AddMinutes(-5), null) });
        _settings.Current.ReplayBuffer.DurationSeconds = 30;

        _buffer.ClipSecondsAtPress = () => 10;
        var shortClip = await _buffer.SaveLastAsync();
        Assert.NotNull(shortClip);
        var first = _saved[^1];
        Assert.Equal(10, first.RequestedSeconds);
        Assert.InRange(first.Seconds, 8.9, 11.1);   // whole 1 s segments that ended inside the last 10 s

        await Task.Delay(1100);   // the next clip gets another file name (one per second)
        _buffer.ClipSecondsAtPress = () => 600;     // longer than the ring: capped at the 30 s lookback
        var capped = await _buffer.SaveLastAsync();
        Assert.NotNull(capped);
        var second = _saved[^1];
        Assert.Equal(30, second.RequestedSeconds);
        Assert.InRange(second.Seconds, 27.5, 31.1);   // the window moved on by the first save
        Assert.Empty(_failed);
    }

    [Fact]
    public async Task A_gif_from_ram()
    {
        if (Ffmpeg is null) return;
        using var store = new MemorySegmentStore(256L << 20);
        TsFootage.Feed(store.OpenWriter(0), _fx.Twenty, new[] { 188 * 300 });
        Stamp(store.Snapshot(), DateTime.UtcNow.AddSeconds(-21));
        SetMemoryState(store, 0, Arma, new[] { new GenerationInfo(0, Arma, "Arma", DateTime.UtcNow.AddMinutes(-5), null) });
        _settings.Current.Gif.DurationSeconds = 3;

        var path = await _buffer.SaveGifAsync();

        Assert.NotNull(path);
        Assert.EndsWith(".gif", path);
        Assert.True(new FileInfo(path!).Length > 1000);
        Assert.Empty(_failed);
        AssertNoLeftovers();
    }

    [Fact]
    public async Task A_materialised_run_starts_with_its_video_audio_from_before_the_key_frame_is_left_out()
    {
        if (Ffmpeg is null) return;
        using var store = new MemorySegmentStore(256L << 20);
        TsFootage.Feed(store.OpenWriter(0), _fx.Twenty, new[] { 4096 });
        var run = store.Snapshot().Skip(5).Take(4).ToList();
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "run.ts");
        await using (var fs = File.Create(path))
            await MemoryClipMaterializer.WriteRunAsync(run, fs, null, CancellationToken.None);

        var ffprobe = Path.Combine(Path.GetDirectoryName(Ffmpeg)!, "ffprobe.exe");
        if (!File.Exists(ffprobe)) return;
        var (code, csv) = FfRunStdout(ffprobe, $"-v error -show_entries stream=codec_type,start_time -of csv=p=0 \"{path}\"");
        Assert.Equal(0, code);
        var starts = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().Split(','))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0])   // the program section repeats the streams
            .ToDictionary(g => g.Key, g => double.Parse(g.First()[1], System.Globalization.CultureInfo.InvariantCulture));
        double videoStart = (run[0].StartPts & (MpegTs.PtsModulus - 1)) / 90_000.0;
        Assert.Equal(videoStart, starts["video"], 3);
        Assert.True(starts["audio"] >= starts["video"] - 0.0005, $"audio {starts["audio"]:0.0000} before video {starts["video"]:0.0000}");
        Assert.True(starts["audio"] - starts["video"] < 0.03, "more than one AAC frame of audio lost at the start");
    }

    private static (int Code, string StdOut) FfRunStdout(string exe, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit(60_000);
        Task.WaitAll(o, e);
        return (p.ExitCode, o.Result);
    }

    // ---- disk ring: hidden files and the segment muxer's wrap ----

    [Fact]
    public async Task The_segment_muxer_keeps_wrapping_while_finished_segments_are_hidden()
    {
        if (Ffmpeg is null) return;
        // ffmpeg cannot reopen a hidden file (CREATE_ALWAYS -> "Permission denied"): hiding everything would kill the ring
        // at the first wrap. 1 s segments, 20 slots, 26 s of recording = more than one full turn of the ring.
        File.WriteAllBytes(Path.Combine(BufferDir, "seg_0_03.mp4"), new byte[4096]);
        File.SetAttributes(Path.Combine(BufferDir, "seg_0_03.mp4"), FileAttributes.Hidden);   // a leftover of an earlier run
        SegmentVisibility.ShowGeneration(BufferDir, 0);

        using var ff = new FFmpegService(Ffmpeg);
        var errors = new ConcurrentQueue<string>();
        ff.StdErrLine += (_, l) => { if (!string.IsNullOrWhiteSpace(l)) errors.Enqueue(l); };
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        ff.Exited += (_, code) => exited.TrySetResult(code);
        ff.Start("-hide_banner -loglevel error -re -f lavfi -i testsrc2=s=160x90:r=30 -t 26 -c:v libx264 -preset ultrafast " +
                 KeyframePolicy.EncoderArgs("libx264", 30, 1, KeyframeMode.ForcedAtSegmentBoundary) + " " +
                 FFmpegCommandBuilder.SegmentMuxerArgs(1, 20, KeyframePolicy.SegmentTimeDelta(30, KeyframeMode.ForcedAtSegmentBoundary)) +
                 $"\"{Path.Combine(BufferDir, "seg_0_%02d.mp4")}\"");

        int maxHidden = 0;
        while (!exited.Task.IsCompleted)
        {
            SegmentVisibility.Apply(BufferDir, 0, 20, 1);
            int hidden = Directory.GetFiles(BufferDir, "seg_0_*.mp4").Count(f => File.GetAttributes(f).HasFlag(FileAttributes.Hidden));
            maxHidden = Math.Max(maxHidden, hidden);
            await Task.WhenAny(exited.Task, Task.Delay(500));
        }
        int exitCode = await exited.Task;

        Assert.True(exitCode == 0, "ffmpeg failed: " + string.Join(" | ", errors));
        Assert.Empty(errors);
        Assert.True(maxHidden >= 3, $"only {maxHidden} segments were ever hidden");
        Assert.Equal(20, Directory.GetFiles(BufferDir, "seg_0_*.mp4").Length);
        foreach (var f in Directory.GetFiles(BufferDir)) File.SetAttributes(f, FileAttributes.Normal);
    }

    // ---- measurement (opt-in: set WKI_MEASURE=1) ----

    [Fact]
    public async Task Measure_reader_cpu_and_managed_memory_for_60_s_and_5_min_rings_at_25_mbit()
    {
        if (Ffmpeg is null || Environment.GetEnvironmentVariable("WKI_MEASURE") != "1") return;
        Directory.CreateDirectory(_dir);
        bool amf = await new FFmpegService(Ffmpeg).HasEncoderAsync("h264_amf");
        foreach (int seconds in new[] { 60, 300 })
        {
            var path = Path.Combine(_dir, $"m{seconds}.ts");
            string enc = amf
                ? "-c:v h264_amf -quality speed -rc cbr -b:v 25M -maxrate 25M -bufsize 25M -filler_data 1"
                : "-c:v libx264 -preset ultrafast -b:v 25M -maxrate 25M -bufsize 25M -x264-params nal-hrd=cbr";
            var (code, err) = FfRun.Run(Ffmpeg,
                $"-hide_banner -loglevel error -f lavfi -i testsrc2=s=1920x1080:r=60 -f lavfi -i sine=f=440:r=48000 -t {seconds} " +
                $"-map 0:v -map 1:a {enc} {KeyframePolicy.EncoderArgs(amf ? "h264_amf" : "libx264", 60, 5, KeyframeMode.ForcedAtSegmentBoundary)} " +
                $"-c:a aac -b:a 192k -ac 2 -f mpegts -pes_payload_size 0 -y \"{path}\"", 900);
            Assert.True(code == 0, err);
            long fileBytes = new FileInfo(path).Length;

            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            long heapBefore = GC.GetTotalMemory(true);
            var store = new MemorySegmentStore(4L << 30);
            var feed = new MemoryBufferFeed(File.OpenRead(path), store.OpenWriter(0), 5, null, () => true);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            feed.Start();
            Assert.True(feed.WaitForEnd(TimeSpan.FromMinutes(5)));
            sw.Stop();
            feed.Dispose();
            long heapAfter = GC.GetTotalMemory(true);
            long payload = store.PayloadBytes;
            long held = store.BytesInUse;
            double cpuPerRealSecond = feed.CpuTime.TotalSeconds / seconds;
            _out.WriteLine($"{seconds} s ring @ {fileBytes * 8.0 / seconds / 1e6:0.0} Mbit/s ({(amf ? "h264_amf" : "libx264")}): " +
                           $"{store.Count} segments, payload {payload / 1048576.0:0.0} MiB, blocks held {held / 1048576.0:0.0} MiB, " +
                           $"managed heap +{(heapAfter - heapBefore) / 1048576.0:0.0} MiB (overhead {(heapAfter - heapBefore - payload) / 1048576.0:0.0} MiB = " +
                           $"{(heapAfter - heapBefore - payload) * 100.0 / Math.Max(1, payload):0.00} %), " +
                           $"reader CPU {feed.CpuTime.TotalMilliseconds:0} ms for {seconds} s of footage = {cpuPerRealSecond * 100:0.000} % of one core in real time " +
                           $"(drained at {fileBytes / 1048576.0 / sw.Elapsed.TotalSeconds:0} MiB/s)");
            store.Dispose();
            File.Delete(path);
        }
    }
}
