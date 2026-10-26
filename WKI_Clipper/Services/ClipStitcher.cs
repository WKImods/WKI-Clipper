using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace WKI_Clipper.Services;

public enum GapReason { OtherTarget, ManualRecording, BufferRestart, Interruption, UnreadableSegment }

/// <summary>Time that is missing between two parts of a clip. Wall-clock based, so approximate (segment close time, +-0.5 s).</summary>
public sealed record ClipGap(DateTime FromUtc, DateTime ToUtc, GapReason Reason, string? Detail)
{
    public TimeSpan Length => ToUtc - FromUtc;
}

/// <param name="EndUtc">When the segment was closed (its last write).</param>
/// <param name="Duration">The time the segment takes in the stitched clip: for a disk segment the end of its last video
/// frame (<see cref="Mp4Duration.TryReadConcatDuration"/>), null when it could not be read.</param>
public sealed record SegmentCandidate(string Path, int Generation, DateTime EndUtc, long Bytes, TimeSpan? Duration)
{
    /// <summary>The RAM segment behind this candidate (RAM buffer); null for a segment file. <see cref="Path"/> is then only a label.</summary>
    internal MemorySegment? Memory { get; init; }
}

public sealed record ClipChapter(TimeSpan Start, TimeSpan End, string Title);

public sealed record StitchPlan(IReadOnlyList<SegmentCandidate> Segments, IReadOnlyList<ClipGap> Gaps,
    IReadOnlyList<ClipChapter> Chapters, TimeSpan TotalDuration, long TotalBytes);

/// <summary>
/// Turns "the complete segments of the current identity" into one clip: which segments, where time is missing,
/// and the chapters/notes that say so. A 30-minute lookback in Auto mode spans Alt-Tabs and Ctrl+F9 recordings;
/// instead of silently serving only the last uninterrupted minutes, the parts are joined and every hole is marked.
/// Pure on purpose - times and labels come in as arguments, so each rule is unit-tested without ffmpeg or a clock.
/// </summary>
public static class ClipStitcher
{
    /// <summary>Holes shorter than this are jitter (muxer flush, timestamp rounding), not missing footage.</summary>
    public static readonly TimeSpan MinReportedGap = TimeSpan.FromSeconds(2);

    /// <summary>
    /// <paramref name="ascending"/> = complete segments of the CURRENT identity, oldest first (session filter applied,
    /// the open segment already removed). Keeps those that ended after <c>nowUtc - windowSeconds</c>, then the newest
    /// <paramref name="maxCount"/> (the ring's pick: more would reach into the overwrite reserve).
    /// </summary>
    public static IReadOnlyList<SegmentCandidate> SelectWindow(IReadOnlyList<SegmentCandidate> ascending,
        DateTime nowUtc, int windowSeconds, int maxCount)
    {
        var cutoff = nowUtc - TimeSpan.FromSeconds(Math.Max(0, windowSeconds));
        return ascending.Where(c => c.EndUtc > cutoff)
                        .TakeLast(Math.Max(1, maxCount))
                        .ToList();
    }

    /// <summary>
    /// Candidates without a readable duration are dropped (their time span becomes - or extends - a gap with
    /// <see cref="GapReason.UnreadableSegment"/>). Between consecutive kept segments a and b the hole is
    /// <c>(b.End - b.Duration) - a.End</c>; from <see cref="MinReportedGap"/> on it is a gap whose reason comes from
    /// <paramref name="classify"/>. Chapters exist only when there is at least one gap: one per uninterrupted run,
    /// positioned by the cumulative sum of the real segment durations (the concat output is exactly that long).
    /// </summary>
    public static StitchPlan Plan(IReadOnlyList<SegmentCandidate> selected,
        Func<DateTime, DateTime, (GapReason Reason, string? Detail)> classify,
        Func<int, DateTime, DateTime, ClipGap?, string> chapterTitle)
    {
        var kept = selected.Where(c => c.Duration is { } d && d > TimeSpan.Zero).ToList();
        var unreadable = selected.Where(c => c.Duration is not { } d || d <= TimeSpan.Zero).ToList();

        var gaps = new List<ClipGap>();
        var gapBefore = new Dictionary<int, ClipGap>();   // index into kept -> the gap that precedes it
        for (int i = 1; i < kept.Count; i++)
        {
            var a = kept[i - 1];
            var b = kept[i];
            var from = a.EndUtc;
            var to = b.EndUtc - b.Duration!.Value;
            if (to - from < MinReportedGap) continue;

            bool unreadableInside = unreadable.Any(u => u.EndUtc > from && u.EndUtc <= to + TimeSpan.FromSeconds(1));
            var (reason, detail) = unreadableInside ? (GapReason.UnreadableSegment, null) : classify(from, to);
            var gap = new ClipGap(from, to, reason, detail);
            gaps.Add(gap);
            gapBefore[i] = gap;
        }

        var chapters = new List<ClipChapter>();
        var total = TimeSpan.Zero;
        long bytes = 0;
        if (gaps.Count > 0)
        {
            int runStart = 0;
            int run = 1;
            var cursor = TimeSpan.Zero;
            for (int i = 1; i <= kept.Count; i++)
            {
                bool runEnds = i == kept.Count || gapBefore.ContainsKey(i);
                if (!runEnds) continue;

                var runDuration = TimeSpan.Zero;
                for (int k = runStart; k < i; k++) runDuration += kept[k].Duration!.Value;

                var runFrom = kept[runStart].EndUtc - kept[runStart].Duration!.Value;
                var runTo = kept[i - 1].EndUtc;
                gapBefore.TryGetValue(runStart, out var before);
                chapters.Add(new ClipChapter(cursor, cursor + runDuration, chapterTitle(run, runFrom, runTo, before)));

                cursor += runDuration;
                runStart = i;
                run++;
            }
        }

        foreach (var c in kept)
        {
            total += c.Duration!.Value;
            bytes += c.Bytes;
        }
        return new StitchPlan(kept, gaps, chapters, total, bytes);
    }

    /// <summary>
    /// Why the footage between two moments is missing, strongest evidence first: another capture target was recorded
    /// meanwhile (any generation with a different identity overlapping the span - the largest overlap names it), a
    /// manual recording paused the buffer, the same capture kept running (stall, standby), or the buffer was simply
    /// restarted. Open intervals end at <paramref name="nowUtc"/>.
    /// </summary>
    public static (GapReason, string?) ClassifyGap(DateTime fromUtc, DateTime toUtc, DateTime nowUtc,
        SegmentIdentity current, IEnumerable<GenerationInfo> generations, IEnumerable<SuspendInterval> suspensions)
    {
        var gens = generations.ToList();

        GenerationInfo? best = null;
        double bestOverlap = 0;
        foreach (var g in gens)
        {
            if (g.Identity == current) continue;
            double o = OverlapSeconds(fromUtc, toUtc, g.StartedUtc, g.StoppedUtc ?? nowUtc);
            if (o > bestOverlap) { bestOverlap = o; best = g; }
        }
        if (best is not null) return (GapReason.OtherTarget, best.TargetLabel);

        foreach (var s in suspensions)
            if (OverlapSeconds(fromUtc, toUtc, s.FromUtc, s.ToUtc ?? nowUtc) > 0)
                return (GapReason.ManualRecording, null);

        foreach (var g in gens)
            if (g.Identity == current && OverlapSeconds(fromUtc, toUtc, g.StartedUtc, g.StoppedUtc ?? nowUtc) > 0)
                return (GapReason.Interruption, null);

        return (GapReason.BufferRestart, null);
    }

    private static double OverlapSeconds(DateTime aFrom, DateTime aTo, DateTime bFrom, DateTime bTo)
    {
        var start = aFrom > bFrom ? aFrom : bFrom;
        var end = aTo < bTo ? aTo : bTo;
        return (end - start).TotalSeconds;
    }

    // ---- ffmpeg concat list ----

    /// <summary>
    /// The concat demuxer list: <c>file '...'</c> per segment, each followed by <c>duration</c> from
    /// <see cref="SegmentCandidate.Duration"/>. Without it ffmpeg places file k at the sum of the files' own durations
    /// (video/audio extent; measured: the movie header says ~0.86 ms more per file), so after 360 segments a chapter
    /// computed from the headers would sit ~0.3 s after the real cut. With the duration given, the output timeline is
    /// exactly the sum of the durations the chapters are computed from.
    ///
    /// For a disk segment that duration has to be the end of its last VIDEO frame (<see cref="Mp4Duration.TryReadConcatDuration"/>),
    /// not the movie header. Measured with the buffer's segment muxer (60 fps, AAC): the audio of a segment overhangs its
    /// last video frame by 0-21 ms (up to one AAC frame), and the header is the longer of the two. Pinned to the header,
    /// every following segment started that overhang late, so the video got a hole of 5-16 ms (often a full frame) at
    /// every boundary - a 15 min clip averaged 59.87 fps with 32.7 ms and 21.7 ms deltas at each seam. Pinned to the video
    /// end, all video and audio packet deltas stay within 1 ms of 16.667 ms / 21.333 ms and the average is exactly 60 fps.
    /// The audio overhang is under one AAC frame, so the first audio dts of the next file still lies behind the last one
    /// of this file: timestamps stay strictly increasing (the mp4 muxer rejects equal or decreasing DTS).
    /// </summary>
    public static IEnumerable<string> ConcatListLines(IEnumerable<SegmentCandidate> segments)
    {
        foreach (var c in segments)
        {
            yield return $"file '{c.Path.Replace("'", @"'\''")}'";
            if (c.Duration is { } d && d > TimeSpan.Zero)
                yield return "duration " + d.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }

    // ---- ffmpeg chapter file ----

    /// <summary>
    /// ";FFMETADATA1" plus one [CHAPTER] per chapter (TIMEBASE=1/1000, START/END in ms). Titles are escaped the
    /// ffmetadata way: <c>\ = ; #</c> and newline get a backslash. Lines end in "\n"; write the file as UTF-8
    /// without BOM (titles contain umlauts).
    /// </summary>
    public static string ChapterMetadata(IReadOnlyList<ClipChapter> chapters)
    {
        var sb = new StringBuilder(";FFMETADATA1\n");
        foreach (var c in chapters)
        {
            sb.Append("[CHAPTER]\n");
            sb.Append("TIMEBASE=1/1000\n");
            sb.Append("START=").Append(Ms(c.Start)).Append('\n');
            sb.Append("END=").Append(Ms(c.End)).Append('\n');
            sb.Append("title=").Append(Escape(c.Title)).Append('\n');
        }
        return sb.ToString();
    }

    private static string Ms(TimeSpan t)
        => ((long)Math.Round(t.TotalMilliseconds, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '\\': case '=': case ';': case '#':
                    sb.Append('\\').Append(ch);
                    break;
                case '\r':
                    break;
                case '\n':
                    sb.Append('\\').Append('\n');
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    // ---- texts ----

    /// <summary>"Teil 1: 21:00:05–21:03:10"; later parts name the gap before them.</summary>
    public static string ChapterTitle(int run, DateTime fromUtc, DateTime toUtc, ClipGap? gapBefore, Func<DateTime, string> clock)
    {
        string span = $"{clock(fromUtc)}–{clock(toUtc)}";
        if (run <= 1 || gapBefore is null)
            return L.T($"Teil {run}: {span}", $"Part {run}: {span}");
        string len = DurationText.FormatActual(gapBefore.Length.TotalSeconds);
        string why = ReasonText(gapBefore.Reason, gapBefore.Detail);
        return L.T($"Teil {run}: {span} (nach Lücke {len}, {why})", $"Part {run}: {span} (after gap {len}, {why})");
    }

    public static string ReasonText(GapReason r, string? detail) => r switch
    {
        GapReason.OtherTarget => string.IsNullOrWhiteSpace(detail) ? "Alt-Tab" : $"Alt-Tab: {detail}",
        GapReason.ManualRecording => L.T("Strg+F9-Aufnahme", "Ctrl+F9 recording"),
        GapReason.BufferRestart => L.T("Buffer-Neustart", "buffer restart"),
        GapReason.Interruption => L.T("Unterbrechung", "interruption"),
        GapReason.UnreadableSegment => L.T("Segment unlesbar", "unreadable segment"),
        _ => r.ToString()
    };

    private static string Entry(ClipGap g, Func<DateTime, string> clock)
        => $"{clock(g.FromUtc)}–{clock(g.ToUtc)} ({DurationText.FormatActual(g.Length.TotalSeconds)}, {ReasonText(g.Reason, g.Detail)})";

    /// <summary>
    /// The gallery note: "Automatisch: Lücke 21:03:10–21:03:55 (45 s, Alt-Tab: Discord)." For several gaps the count
    /// comes first and the entries are listed with "; ". Never longer than <see cref="GalleryMetaService.MaxNoteLength"/>;
    /// when it does not fit, it is cut at a gap boundary and ends with "…".
    /// </summary>
    public static string GapNote(IReadOnlyList<ClipGap> gaps, Func<DateTime, string> clock)
    {
        if (gaps.Count == 0) return "";
        int max = GalleryMetaService.MaxNoteLength;
        string prefix = gaps.Count == 1
            ? L.T("Automatisch: Lücke ", "Automatic: gap ")
            : L.T($"Automatisch: {gaps.Count} Lücken — ", $"Automatic: {gaps.Count} gaps — ");
        var entries = gaps.Select(g => Entry(g, clock)).ToList();

        string full = prefix + string.Join("; ", entries) + ".";
        if (full.Length <= max) return full;

        for (int k = entries.Count - 1; k >= 1; k--)
        {
            string cut = prefix + string.Join("; ", entries.Take(k)) + "; …";
            if (cut.Length <= max) return cut;
        }
        string hard = prefix + entries[0];
        return hard.Length <= max ? hard : hard[..(max - 1)] + "…";
    }

    /// <summary>One line for the "clip saved" toast.</summary>
    public static string GapToastLine(IReadOnlyList<ClipGap> gaps)
    {
        if (gaps.Count == 0) return "";
        if (gaps.Count == 1)
        {
            var g = gaps[0];
            string what = $"{DurationText.FormatActual(g.Length.TotalSeconds)}, {ReasonText(g.Reason, g.Detail)}";
            return L.T($"1 Lücke ({what}) — als Kapitel + Notiz markiert", $"1 gap ({what}) — marked as chapter + note");
        }
        var total = TimeSpan.Zero;
        foreach (var g in gaps) total += g.Length;
        string sum = DurationText.FormatActual(total.TotalSeconds);
        return L.T($"{gaps.Count} Lücken (zusammen {sum}) — als Kapitel + Notiz markiert",
                   $"{gaps.Count} gaps ({sum} in total) — marked as chapters + note");
    }
}
