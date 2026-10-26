using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// The packet timeline of an mp4 as <c>ffmpeg -f framecrc</c> prints it (dts and duration per packet, in seconds),
/// so a stitched clip can be checked without ffprobe.
/// </summary>
internal sealed class PacketTimeline
{
    public List<(double Dts, double Duration)> Video { get; } = new();
    public List<(double Dts, double Duration)> Audio { get; } = new();

    private static readonly Regex TimeBase = new(@"^#tb (\d+): (\d+)/(\d+)", RegexOptions.CultureInvariant);
    private static readonly Regex MediaType = new(@"^#media_type (\d+): (\w+)", RegexOptions.CultureInvariant);
    private static readonly Regex Packet = new(@"^(\d+),\s*(-?\d+),\s*(-?\d+),\s*(\d+),", RegexOptions.CultureInvariant);

    /// <summary>First video and first audio stream of <paramref name="file"/>, stream-copied into a framecrc file.</summary>
    public static PacketTimeline Read(string ffmpeg, string file, string scratchDir)
    {
        var crc = Path.Combine(scratchDir, Guid.NewGuid().ToString("N") + ".crc");
        var (code, err) = FfRun.Run(ffmpeg, $"-hide_banner -loglevel error -y -i \"{file}\" -map 0:v:0 -map 0:a:0 -c copy -f framecrc \"{crc}\"");
        Assert.True(code == 0, "framecrc failed: " + err);

        var timebase = new Dictionary<int, double>();
        var isVideo = new Dictionary<int, bool>();
        var result = new PacketTimeline();
        foreach (var line in File.ReadLines(crc))
        {
            if (TimeBase.Match(line) is { Success: true } tb)
                timebase[int.Parse(tb.Groups[1].Value)] = double.Parse(tb.Groups[2].Value, CultureInfo.InvariantCulture)
                                                         / double.Parse(tb.Groups[3].Value, CultureInfo.InvariantCulture);
            else if (MediaType.Match(line) is { Success: true } mt)
                isVideo[int.Parse(mt.Groups[1].Value)] = mt.Groups[2].Value == "video";
            else if (Packet.Match(line) is { Success: true } p)
            {
                int stream = int.Parse(p.Groups[1].Value);
                double scale = timebase[stream];
                var entry = (long.Parse(p.Groups[2].Value, CultureInfo.InvariantCulture) * scale,
                             long.Parse(p.Groups[4].Value, CultureInfo.InvariantCulture) * scale);
                (isVideo[stream] ? result.Video : result.Audio).Add(entry);
            }
        }
        File.Delete(crc);
        return result;
    }

    /// <summary>The (signed) deviation of the packet-to-packet distance from <paramref name="expected"/> that is furthest off.</summary>
    public static double WorstDeltaError(List<(double Dts, double Duration)> packets, double expected)
    {
        double worst = 0;
        for (int i = 1; i < packets.Count; i++)
        {
            double error = packets[i].Dts - packets[i - 1].Dts - expected;
            if (Math.Abs(error) > Math.Abs(worst)) worst = error;
        }
        return worst;
    }

    public double VideoEnd => Video[^1].Dts + Video[^1].Duration;
    public double AudioEnd => Audio[^1].Dts + Audio[^1].Duration;
}

/// <summary>40 s of 60 fps test footage, cut into disk-buffer segments with the exact segment arguments of the buffer.</summary>
public sealed class DiskSegmentFixture : IDisposable
{
    public string? Ffmpeg { get; } = FFmpegTestSupport.Find();
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "wki_diskstitch_" + Guid.NewGuid().ToString("N"));
    public List<string> Segments { get; private set; } = new();

    public DiskSegmentFixture()
    {
        Directory.CreateDirectory(Dir);
        if (Ffmpeg is null) return;

        // testsrc at 60 fps + a sine through AAC, GOP 240 (4 s) - the same shape as the buffer's own recording.
        var args = "-hide_banner -loglevel error " +
                   "-f lavfi -i \"testsrc=size=320x180:rate=60\" -f lavfi -i \"sine=frequency=1000:sample_rate=48000\" " +
                   "-t 40 -map 0:v -map 1:a -c:v libx264 -preset ultrafast -g 240 -pix_fmt yuv420p -c:a aac -b:a 128k " +
                   FFmpegCommandBuilder.SegmentMuxerArgs(5, 400, null) +
                   $"\"{Path.Combine(Dir, "seg_0_%03d.mp4")}\"";
        var (code, err) = FfRun.Run(Ffmpeg, args);
        Assert.True(code == 0, "generator failed: " + err);
        // The last file is the open segment of a running buffer: the service never picks it.
        Segments = FfRun.SegmentsOf(Dir, 0);
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch { }
    }
}

/// <summary>
/// The bug behind "clips over ~5 minutes stutter": the disk ring pinned every segment's concat <c>duration</c> to the
/// movie header (mvhd = max of video and audio). The audio of a segment overhangs its last video frame by up to one AAC
/// frame (0-21 ms), so every following segment was pushed back by the overhang and the video got a hole of 5-16 ms at
/// every boundary (a clip of 15 min: 59.87 fps instead of 60). Pinned to the end of the last video frame, the stitched
/// clip has the packet timing of one continuous recording. All tests return early without an ffmpeg.
/// </summary>
public sealed class DiskSegmentStitchIntegrationTests : IClassFixture<DiskSegmentFixture>, IDisposable
{
    private const double VideoFrame = 1.0 / 60;
    private const double AacFrame = 1024.0 / 48000;
    private const double OneMs = 0.001;

    private readonly DiskSegmentFixture _fx;
    private readonly string _work = Path.Combine(Path.GetTempPath(), "wki_diskstitch_w_" + Guid.NewGuid().ToString("N"));

    public DiskSegmentStitchIntegrationTests(DiskSegmentFixture fx)
    {
        _fx = fx;
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch { }
    }

    private delegate bool DurationReader(string path, out TimeSpan duration);

    /// <summary>What the service writes for each disk segment, with the duration taken from <paramref name="read"/>.</summary>
    private List<SegmentCandidate> Candidates(IEnumerable<string> files, DurationReader read)
        => files.Select(f =>
        {
            Assert.True(read(f, out var d), "unreadable header: " + f);
            return new SegmentCandidate(f, 0, DateTime.UtcNow, new FileInfo(f).Length, d);
        }).ToList();

    private (string Clip, string Warnings) Stitch(IEnumerable<SegmentCandidate> segments, string name, string? chapters = null)
    {
        var list = Path.Combine(_work, name + ".txt");
        File.WriteAllLines(list, ClipStitcher.ConcatListLines(segments));
        var output = Path.Combine(_work, name + ".mp4");
        // Default log level (warning), so a non-monotonous timestamp the muxer had to repair shows up in the result.
        var (code, err) = FfRun.Run(_fx.Ffmpeg!, FFmpegCommandBuilder.BuildConcat(list, output, faststart: false, chaptersFile: chapters));
        Assert.True(code == 0, "concat failed: " + err);
        return (output, err);
    }

    /// <summary>Everything a smooth clip has to satisfy, as readable findings; empty = the clip is seamless.</summary>
    private static List<string> Problems(PacketTimeline t, string warnings)
    {
        var problems = new List<string>();

        double videoWorst = PacketTimeline.WorstDeltaError(t.Video, VideoFrame);
        if (Math.Abs(videoWorst) > OneMs)
            problems.Add($"video packet delta off by {videoWorst * 1000:0.00} ms");

        double audioWorst = PacketTimeline.WorstDeltaError(t.Audio, AacFrame);
        if (Math.Abs(audioWorst) > OneMs)
            problems.Add($"audio packet delta off by {audioWorst * 1000:0.00} ms");

        double fps = t.Video.Count / (t.VideoEnd - t.Video[0].Dts);
        if (Math.Abs(fps - 60) > 0.005)
            problems.Add($"average {fps:0.0000} fps instead of 60");

        if (Math.Abs(t.AudioEnd - t.VideoEnd) >= VideoFrame)
            problems.Add($"audio ends {(t.AudioEnd - t.VideoEnd) * 1000:0.0} ms after the video");

        if (Regex.IsMatch(warnings, "onoton", RegexOptions.IgnoreCase))
            problems.Add("the muxer repaired non-monotonous timestamps: " + warnings.Trim());
        return problems;
    }

    // ---- 1 ----

    [Fact]
    public void Footage_has_several_segments_whose_audio_overhangs_the_video()
    {
        if (_fx.Ffmpeg is null) return;
        var complete = _fx.Segments.Take(_fx.Segments.Count - 1).ToList();
        Assert.True(complete.Count >= 4, $"only {complete.Count} complete segments");

        // The precondition of the whole bug: the movie header is longer than the video of at least one segment.
        int overhanging = 0;
        foreach (var seg in complete)
        {
            Assert.True(Mp4Duration.TryRead(seg, out var movie));
            Assert.True(Mp4Duration.TryReadConcatDuration(seg, out var video));
            // mvhd counts in whole milliseconds, so it can sit a hair below the video end.
            Assert.True(movie - video >= TimeSpan.FromMilliseconds(-1), $"{Path.GetFileName(seg)}: video ends after the movie");
            if (movie - video > TimeSpan.FromMilliseconds(5)) overhanging++;
        }
        Assert.True(overhanging >= 2, $"only {overhanging} segments with an audio overhang");
    }

    // ---- 2 ----

    [Fact]
    public void The_concat_duration_matches_the_end_of_the_last_video_packet_of_every_segment()
    {
        if (_fx.Ffmpeg is null) return;
        foreach (var seg in _fx.Segments.Take(_fx.Segments.Count - 1))
        {
            Assert.True(Mp4Duration.TryReadConcatDuration(seg, out var mine));
            var t = PacketTimeline.Read(_fx.Ffmpeg, seg, _work);

            // ffmpeg's own packets: the file starts at the earlier of the two streams, the video ends after its last packet.
            double fileStart = Math.Min(t.Video[0].Dts, t.Audio[0].Dts);
            double theirs = t.VideoEnd - fileStart;
            Assert.True(Math.Abs(mine.TotalSeconds - theirs) < 0.0001,
                $"{Path.GetFileName(seg)}: header says {mine.TotalSeconds:0.000000} s, ffmpeg's packets {theirs:0.000000} s");
        }
    }

    // ---- 3 ----

    [Fact]
    public void Segments_pinned_to_the_end_of_their_video_stitch_without_a_hole_at_any_boundary()
    {
        if (_fx.Ffmpeg is null) return;
        var segments = _fx.Segments.Take(_fx.Segments.Count - 1).ToList();
        var (clip, warnings) = Stitch(Candidates(segments, Mp4Duration.TryReadConcatDuration), "videoend");

        var timeline = PacketTimeline.Read(_fx.Ffmpeg, clip, _work);
        var problems = Problems(timeline, warnings);

        Assert.True(problems.Count == 0, string.Join("; ", problems));
        Assert.True(timeline.Video.Count > 60 * 20, "the clip is too short to cross several boundaries");
    }

    // ---- 4 ----

    [Fact]
    public void Segments_pinned_to_the_movie_header_do_stutter_and_this_check_sees_it()
    {
        if (_fx.Ffmpeg is null) return;
        var segments = _fx.Segments.Take(_fx.Segments.Count - 1).ToList();
        var (clip, warnings) = Stitch(Candidates(segments, Mp4Duration.TryRead), "mvhd");

        var problems = Problems(PacketTimeline.Read(_fx.Ffmpeg, clip, _work), warnings);

        // The old behaviour: holes in the video and an average below 60 fps.
        Assert.Contains(problems, p => p.StartsWith("video packet delta", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("fps", StringComparison.Ordinal));
    }

    // ---- 5 ----

    [Fact]
    public void The_planned_length_and_chapters_are_where_the_stitched_video_really_is()
    {
        if (_fx.Ffmpeg is null) return;
        var files = _fx.Segments.Take(_fx.Segments.Count - 1).ToList();
        Assert.True(files.Count >= 4);

        // Two runs with a 45 s hole in between (wall clock), exactly what the service plans.
        var all = Candidates(files, Mp4Duration.TryReadConcatDuration);
        var cursor = new DateTime(2026, 10, 26, 20, 0, 0, DateTimeKind.Utc);
        var stamped = new List<SegmentCandidate>();
        for (int i = 0; i < all.Count; i++)
        {
            if (i == 2) cursor += TimeSpan.FromSeconds(45);
            cursor += all[i].Duration!.Value;
            stamped.Add(all[i] with { EndUtc = cursor });
        }
        var plan = ClipStitcher.Plan(stamped,
            (_, _) => (GapReason.OtherTarget, "Discord"),
            (run, from, to, gap) => ClipStitcher.ChapterTitle(run, from, to, gap, t => t.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
        Assert.Single(plan.Gaps);
        Assert.Equal(2, plan.Chapters.Count);

        var chaptersFile = Path.Combine(_work, "_chapters.txt");
        File.WriteAllText(chaptersFile, ClipStitcher.ChapterMetadata(plan.Chapters), new UTF8Encoding(false));
        var (clip, warnings) = Stitch(plan.Segments, "plan", chaptersFile);
        var timeline = PacketTimeline.Read(_fx.Ffmpeg, clip, _work);

        Assert.Empty(Problems(timeline, warnings));

        // The saved length is the sum of the pinned durations = where the last video frame ends.
        Assert.True(Math.Abs(timeline.VideoEnd - plan.TotalDuration.TotalSeconds) < OneMs,
            $"video ends at {timeline.VideoEnd:0.0000} s, the plan says {plan.TotalDuration.TotalSeconds:0.0000} s");

        // The second chapter starts on the first video frame of the second run (chapters are written in whole ms).
        double chapterStart = plan.Chapters[1].Start.TotalSeconds;
        var firstAfter = timeline.Video.First(p => p.Dts >= chapterStart - OneMs);
        Assert.True(Math.Abs(firstAfter.Dts - chapterStart) < OneMs,
            $"chapter 2 starts at {chapterStart:0.0000} s, the first frame of the second run is at {firstAfter.Dts:0.0000} s");
    }
}
