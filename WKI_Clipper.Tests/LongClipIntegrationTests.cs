using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Runs the real ffmpeg for the integration tests below.</summary>
internal static class FfRun
{
    public static (int ExitCode, string StdErr) Run(string ffmpeg, string args, int timeoutSeconds = 180)
    {
        var psi = new ProcessStartInfo(ffmpeg, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var outp = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(timeoutSeconds * 1000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("ffmpeg did not finish: " + args);
        }
        Task.WaitAll(err, outp);
        return (p.ExitCode, err.Result);
    }

    /// <summary>Test footage: one white frame and one 1 kHz beep at the start of every second, 160x90 @ 30 fps.</summary>
    public static string GeneratorArgs(int seconds, string codecArgs, string outputPattern, int segmentSeconds, int wrap, double? delta)
        => "-hide_banner -loglevel error " +
           "-f lavfi -i \"color=c=black:s=160x90:r=30,drawbox=x=0:y=0:w=iw:h=ih:color=white:t=fill:enable='lt(mod(t\\,1)\\,0.03)'\" " +
           "-f lavfi -i \"aevalsrc='if(lt(mod(t,1),0.03),0.8*sin(2*PI*1000*t),0)':s=48000:c=stereo\" " +
           $"-t {seconds} -map 0:v -map 1:a -c:v libx264 -preset ultrafast {codecArgs} -c:a aac -b:a 192k " +
           FFmpegCommandBuilder.SegmentMuxerArgs(segmentSeconds, wrap, delta) + $"\"{outputPattern}\"";

    /// <summary>Generates <paramref name="seconds"/> of test footage as 1-second segments seg_{gen}_NNN.mp4.</summary>
    public static void GenerateSegments(string ffmpeg, string dir, int generation, int seconds, KeyframeMode mode)
    {
        var codecArgs = KeyframePolicy.EncoderArgs("libx264", 30, 1, mode);
        var (code, err) = Run(ffmpeg, GeneratorArgs(seconds, codecArgs,
            Path.Combine(dir, $"seg_{generation}_%03d.mp4"), 1, 400, KeyframePolicy.SegmentTimeDelta(30, mode)));
        Assert.True(code == 0, "generator failed: " + err);
    }

    /// <summary>Segments of one generation in recording order.</summary>
    public static List<string> SegmentsOf(string dir, int generation)
        => Directory.EnumerateFiles(dir, $"seg_{generation}_*.mp4")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

    public static void WriteConcatList(string listPath, IEnumerable<string> files)
        => File.WriteAllLines(listPath, files.Select(f => $"file '{f.Replace("'", @"'\''")}'"));

    private static readonly Regex BlackEnd = new(@"black_end:([0-9.]+)", RegexOptions.CultureInvariant);
    private static readonly Regex SilenceEnd = new(@"silence_end:\s*([0-9.]+)", RegexOptions.CultureInvariant);

    /// <summary>Times at which a white frame appears after black (flash onsets).</summary>
    public static List<double> FlashOnsets(string ffmpeg, string file)
    {
        var (_, err) = Run(ffmpeg, $"-hide_banner -i \"{file}\" -map 0:v -vf blackdetect=d=0.1:pix_th=0.5 -f null -", 300);
        return BlackEnd.Matches(err).Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
    }

    /// <summary>Times at which sound starts after silence (beep onsets).</summary>
    public static List<double> BeepOnsets(string ffmpeg, string file)
    {
        var (_, err) = Run(ffmpeg, $"-hide_banner -i \"{file}\" -map 0:a:0 -af silencedetect=n=-30dB:d=0.005 -f null -", 300);
        return SilenceEnd.Matches(err).Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
    }

    public static double Median(IEnumerable<double> values)
    {
        var s = values.OrderBy(v => v).ToList();
        if (s.Count == 0) throw new InvalidOperationException("no values");
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    /// <summary>Beep minus flash for each flash that has a beep within half a second.</summary>
    public static List<(double Flash, double Delta)> Pair(IReadOnlyList<double> flashes, IReadOnlyList<double> beeps)
    {
        var pairs = new List<(double, double)>();
        foreach (var f in flashes)
        {
            double? best = null;
            foreach (var b in beeps)
                if (Math.Abs(b - f) < 0.5 && (best is null || Math.Abs(b - f) < Math.Abs(best.Value - f))) best = b;
            if (best is { } hit) pairs.Add((f, hit - f));
        }
        return pairs;
    }
}

/// <summary>360 one-second segments, generated once for the tests that look at the same footage.</summary>
public sealed class LongFootageFixture : IDisposable
{
    public string? Ffmpeg { get; } = FFmpegTestSupport.Find();
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "wki_longclip_" + Guid.NewGuid().ToString("N"));
    public List<string> Segments { get; private set; } = new();

    public LongFootageFixture()
    {
        Directory.CreateDirectory(Dir);
        if (Ffmpeg is null) return;
        FfRun.GenerateSegments(Ffmpeg, Dir, generation: 0, seconds: 362, KeyframeMode.ForcedAtSegmentBoundary);
        Segments = FfRun.SegmentsOf(Dir, 0);
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch { }
    }
}

/// <summary>
/// The 30-minute clip, end to end with the real ffmpeg: stitching hundreds of stream-copy segments keeps audio and
/// video together, the length is what the segments add up to, and a gap becomes a chapter at the exact position.
/// Every test returns early when no ffmpeg is installed.
/// </summary>
public sealed class LongClipIntegrationTests : IClassFixture<LongFootageFixture>, IDisposable
{
    private const double Frame = 1.0 / 30;
    private readonly LongFootageFixture _fx;
    private readonly string _work = Path.Combine(Path.GetTempPath(), "wki_longclip_w_" + Guid.NewGuid().ToString("N"));

    public LongClipIntegrationTests(LongFootageFixture fx)
    {
        _fx = fx;
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch { }
    }

    private string Concat(IEnumerable<string> segments, string name, bool faststart = false, string? chapters = null)
    {
        var list = Path.Combine(_work, name + ".txt");
        // Exactly what the service writes: file + duration lines.
        var candidates = segments.Select(s =>
        {
            Assert.True(Mp4Duration.TryRead(s, out var d), "unreadable header: " + s);
            return new SegmentCandidate(s, 0, DateTime.UtcNow, 0, d);
        }).ToList();
        File.WriteAllLines(list, ClipStitcher.ConcatListLines(candidates));
        var output = Path.Combine(_work, name + ".mp4");
        var (code, err) = FfRun.Run(_fx.Ffmpeg!, FFmpegCommandBuilder.BuildConcat(list, output + ".part", faststart, chapters).Replace(
            "-hide_banner -loglevel warning", "-hide_banner -loglevel error"));
        Assert.True(code == 0, "concat failed: " + err);
        File.Move(output + ".part", output);
        return output;
    }

    private static TimeSpan Seconds(IEnumerable<string> segments)
    {
        var total = TimeSpan.Zero;
        foreach (var s in segments)
        {
            Assert.True(Mp4Duration.TryRead(s, out var d), "unreadable header: " + s);
            total += d;
        }
        return total;
    }

    private TimeSpan ProbeDuration(string file)
    {
        var (_, err) = FfRun.Run(_fx.Ffmpeg!, $"-hide_banner -i \"{file}\"");
        var probe = FFmpegProbeParser.Parse(err);
        Assert.NotNull(probe);
        Assert.NotNull(probe!.Duration);
        return probe.Duration!.Value;
    }

    // ---- 1 ----

    [Fact]
    public void Concat_of_360_segments_keeps_audio_and_video_in_sync()
    {
        if (_fx.Ffmpeg is null) return;
        var newest360 = _fx.Segments.TakeLast(360).ToList();
        Assert.Equal(360, newest360.Count);

        var clip = Concat(newest360, "sync");
        var flashes = FfRun.FlashOnsets(_fx.Ffmpeg, clip);
        var beeps = FfRun.BeepOnsets(_fx.Ffmpeg, clip);
        var pairs = FfRun.Pair(flashes, beeps);

        // One flash per second; the very first one has no black before it, the last may be cut by the end.
        Assert.InRange(flashes.Count, 359, 361);
        Assert.True(pairs.Count >= 350, $"only {pairs.Count} flash/beep pairs");

        double first = FfRun.Median(pairs.Take(10).Select(p => p.Delta));
        double last = FfRun.Median(pairs.TakeLast(10).Select(p => p.Delta));
        // The medians ignore the end artefact of the last pair (measured ~38 ms).
        Assert.True(Math.Abs(last) < Frame, $"audio/video offset at the end: {last * 1000:0.0} ms");
        Assert.True(Math.Abs(last - first) < Frame, $"drift over 360 segments: {(last - first) * 1000:0.0} ms (start {first * 1000:0.0}, end {last * 1000:0.0})");
    }

    // ---- 2 ----

    [Fact]
    public void Output_duration_is_the_sum_of_segment_durations()
    {
        if (_fx.Ffmpeg is null) return;
        var newest360 = _fx.Segments.TakeLast(360).ToList();
        var clip = Concat(newest360, "duration");

        var sum = Seconds(newest360).TotalSeconds;
        var output = ProbeDuration(clip).TotalSeconds;

        Assert.True(Math.Abs(output - sum) < 2 * Frame, $"output {output:0.000} s vs segments {sum:0.000} s");
        // Each boundary adds the audio overhang of at most one AAC frame (measured ~8.7 ms); nominal footage is 360 s.
        Assert.True(output - 360 <= 359 * 0.025, $"output {output:0.000} s is {output - 360:0.000} s longer than the footage");
    }

    // ---- 3 ----

    [Fact]
    public void Forced_keyframes_make_every_segment_exactly_one_segment_long()
    {
        if (_fx.Ffmpeg is null) return;
        Assert.True(_fx.Segments.Count >= 360);
        foreach (var seg in _fx.Segments.Take(_fx.Segments.Count - 1))
        {
            Assert.True(Mp4Duration.TryRead(seg, out var d), seg);
            Assert.True(Math.Abs(d.TotalSeconds - 1.0) <= Frame, $"{Path.GetFileName(seg)} lasts {d.TotalSeconds:0.000} s");
        }
    }

    // ---- 4 ----

    [Fact]
    public void Mp4Duration_matches_ffmpeg()
    {
        if (_fx.Ffmpeg is null) return;
        foreach (var seg in _fx.Segments.Skip(10).Take(5))
        {
            Assert.True(Mp4Duration.TryRead(seg, out var mine));
            var theirs = ProbeDuration(seg);
            // ffmpeg prints the duration in centiseconds
            Assert.True(Math.Abs((mine - theirs).TotalSeconds) < 0.006,
                $"{Path.GetFileName(seg)}: mvhd {mine.TotalSeconds:0.0000} s, ffmpeg {theirs.TotalSeconds:0.0000} s");
        }
    }

    // ---- 5 ----

    [Fact]
    public void Two_generations_with_a_gap_become_one_clip_with_chapters()
    {
        if (_fx.Ffmpeg is null) return;
        var dir = Path.Combine(_work, "gens");
        Directory.CreateDirectory(dir);
        FfRun.GenerateSegments(_fx.Ffmpeg, dir, 0, 60, KeyframeMode.ForcedAtSegmentBoundary);
        FfRun.GenerateSegments(_fx.Ffmpeg, dir, 1, 60, KeyframeMode.ForcedAtSegmentBoundary);
        var gen0 = FfRun.SegmentsOf(dir, 0);
        var gen1 = FfRun.SegmentsOf(dir, 1);

        // Wall clock: generation 1 starts 45 s after generation 0 ended.
        var cursor = new DateTime(2026, 10, 17, 21, 0, 0, DateTimeKind.Utc);
        var candidates = new List<SegmentCandidate>();
        void Stamp(IEnumerable<string> files, int gen)
        {
            foreach (var f in files)
            {
                Assert.True(Mp4Duration.TryRead(f, out var d));
                cursor += d;
                File.SetLastWriteTimeUtc(f, cursor);
                candidates.Add(new SegmentCandidate(f, gen, cursor, new FileInfo(f).Length, d));
            }
        }
        Stamp(gen0, 0);
        var gen0End = cursor;
        cursor += TimeSpan.FromSeconds(45);
        Stamp(gen1, 1);

        var selected = ClipStitcher.SelectWindow(candidates, cursor.AddSeconds(2), 600, 1000);
        var plan = ClipStitcher.Plan(selected,
            (_, _) => (GapReason.OtherTarget, "Discord"),
            (run, from, to, gap) => ClipStitcher.ChapterTitle(run, from, to, gap, t => t.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));

        Assert.Single(plan.Gaps);
        Assert.Equal(TimeSpan.FromSeconds(45), plan.Gaps[0].Length);
        Assert.Equal(2, plan.Chapters.Count);

        var chaptersFile = Path.Combine(_work, "_chapters.txt");
        File.WriteAllText(chaptersFile, ClipStitcher.ChapterMetadata(plan.Chapters), new UTF8Encoding(false));
        var clip = Concat(plan.Segments.Select(s => s.Path), "gaps", faststart: false, chapters: chaptersFile);

        var (_, probe) = FfRun.Run(_fx.Ffmpeg, $"-hide_banner -i \"{clip}\"");
        var chapters = Regex.Matches(probe, @"Chapter #0:(\d+): start ([0-9.]+), end ([0-9.]+)")
            .Select(m => (Index: int.Parse(m.Groups[1].Value), Start: double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                          End: double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture))).ToList();
        Assert.Equal(2, chapters.Count);

        double gen0Sum = Seconds(gen0).TotalSeconds;
        Assert.True(Math.Abs(chapters[1].Start - gen0Sum) <= Frame, $"chapter 2 starts at {chapters[1].Start:0.000}, generation 0 lasts {gen0Sum:0.000}");
        Assert.Equal(0, chapters[0].Start, 3);
        Assert.Contains("Lücke 45 s", probe);
        Assert.Contains("Discord", probe);

        // video + audio + the QuickTime chapter text track
        var streams = Regex.Matches(probe, @"Stream #0:\d+[^:]*: (Video|Audio|Data)").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(new[] { "Video", "Audio", "Data" }, streams);

        // The first flash after the chapter start is at most one frame behind it: the chapter marks the real cut.
        var flashes = FfRun.FlashOnsets(_fx.Ffmpeg, clip);
        var firstAfter = flashes.Where(f => f >= chapters[1].Start - 0.002).OrderBy(f => f).First();
        Assert.True(firstAfter - chapters[1].Start <= Frame, $"first flash {firstAfter:0.000} vs chapter {chapters[1].Start:0.000}");
        Assert.True(gen0End < cursor);
    }

    // ---- 6 ----

    [Fact]
    public void Concat_without_faststart_is_playable()
    {
        if (_fx.Ffmpeg is null) return;
        var clip = Concat(_fx.Segments.Skip(20).Take(60), "nofaststart", faststart: false);

        var (code, err) = FfRun.Run(_fx.Ffmpeg, $"-hide_banner -loglevel error -i \"{clip}\" -f null -");
        Assert.True(code == 0 && err.Trim().Length == 0, "decode errors: " + err);
        Assert.InRange(ProbeDuration(clip).TotalSeconds, 59.9, 61.0);
    }

    [Fact]
    public void Concat_with_faststart_puts_the_index_in_front()
    {
        if (_fx.Ffmpeg is null) return;
        var clip = Concat(_fx.Segments.Skip(20).Take(10), "faststart", faststart: true);
        var head = new byte[4096];
        using (var fs = File.OpenRead(clip)) fs.ReadExactly(head);
        Assert.Contains("moov", Encoding.Latin1.GetString(head));
    }

    [Fact]
    public void Without_faststart_the_index_stays_at_the_end()
    {
        if (_fx.Ffmpeg is null) return;
        var clip = Concat(_fx.Segments.Skip(20).Take(10), "tail", faststart: false);
        var head = new byte[4096];
        using (var fs = File.OpenRead(clip)) fs.ReadExactly(head);
        Assert.DoesNotContain("moov", Encoding.Latin1.GetString(head));
    }
}
