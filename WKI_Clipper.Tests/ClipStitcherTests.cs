using System;
using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Stitching rules. German texts are asserted on purpose: L stays German in the test process (nothing
/// initialises it), and a wording change should be a conscious one.
/// </summary>
public sealed class ClipStitcherTests
{
    private static readonly DateTime T0 = new(2026, 10, 17, 21, 0, 0, DateTimeKind.Utc);
    private static readonly SegmentIdentity Arma = new("wgc:100:2560x1440", "s");
    private static readonly SegmentIdentity Discord = new("wgc:200:1280x720", "s");

    private static string Clock(DateTime t) => t.ToString("HH:mm:ss");

    /// <summary>A segment of <paramref name="seconds"/> that ended at T0 + endOffset.</summary>
    private static SegmentCandidate Seg(string name, double endOffset, double seconds = 5, int gen = 0, long bytes = 1000)
        => new(name, gen, T0.AddSeconds(endOffset), bytes, TimeSpan.FromSeconds(seconds));

    /// <summary>n back-to-back 5 s segments, the first one ending at firstEnd.</summary>
    private static List<SegmentCandidate> Run(int n, double firstEnd, string prefix = "s", int gen = 0)
        => Enumerable.Range(0, n).Select(i => Seg($"{prefix}{i}", firstEnd + 5 * i, 5, gen)).ToList();

    private static readonly Func<DateTime, DateTime, (GapReason, string?)> NoClassify =
        (_, _) => (GapReason.BufferRestart, null);

    private static string Title(int run, DateTime f, DateTime t, ClipGap? g) => $"run{run}";

    // ---- SelectWindow ----

    [Fact]
    public void SelectWindow_keeps_what_ended_inside_the_window()
    {
        var all = Run(20, 5);   // ends at 5..100
        var now = T0.AddSeconds(103);
        var picked = ClipStitcher.SelectWindow(all, now, 60, maxCount: 100);
        // cutoff = 43 s; first with End > 43 is the one ending at 45
        Assert.Equal(12, picked.Count);
        Assert.Equal(45, (picked[0].EndUtc - T0).TotalSeconds);
        Assert.Equal(100, (picked[^1].EndUtc - T0).TotalSeconds);
    }

    [Fact]
    public void SelectWindow_caps_to_the_newest_max_count()
    {
        var all = Run(20, 5);
        var picked = ClipStitcher.SelectWindow(all, T0.AddSeconds(103), 600, maxCount: 7);
        Assert.Equal(7, picked.Count);
        Assert.Equal("s19", picked[^1].Path);
        Assert.Equal("s13", picked[0].Path);
    }

    [Fact]
    public void SelectWindow_excludes_a_segment_that_ended_exactly_at_the_cutoff()
    {
        var all = new List<SegmentCandidate> { Seg("edge", 40), Seg("in", 45) };
        var picked = ClipStitcher.SelectWindow(all, T0.AddSeconds(100), 60, 10);
        Assert.Single(picked);
        Assert.Equal("in", picked[0].Path);
    }

    [Fact]
    public void SelectWindow_of_nothing_is_nothing()
        => Assert.Empty(ClipStitcher.SelectWindow(new List<SegmentCandidate>(), T0, 60, 10));

    // ---- Plan ----

    [Fact]
    public void Contiguous_segments_have_no_gaps_and_no_chapters()
    {
        var plan = ClipStitcher.Plan(Run(12, 5), NoClassify, Title);
        Assert.Empty(plan.Gaps);
        Assert.Empty(plan.Chapters);
        Assert.Equal(12, plan.Segments.Count);
        Assert.Equal(TimeSpan.FromSeconds(60), plan.TotalDuration);
        Assert.Equal(12_000, plan.TotalBytes);
    }

    [Fact]
    public void A_45_second_hole_is_one_gap_and_two_chapters_at_the_summed_durations()
    {
        var first = Run(6, 5);                       // 0..30
        var second = Run(6, 30 + 45 + 5, "t");       // starts 75, ends 80..105
        var plan = ClipStitcher.Plan(first.Concat(second).ToList(), (f, t) => (GapReason.OtherTarget, "Discord"), Title);

        var gap = Assert.Single(plan.Gaps);
        Assert.Equal(T0.AddSeconds(30), gap.FromUtc);
        Assert.Equal(T0.AddSeconds(75), gap.ToUtc);
        Assert.Equal(GapReason.OtherTarget, gap.Reason);
        Assert.Equal("Discord", gap.Detail);
        Assert.Equal(TimeSpan.FromSeconds(45), gap.Length);

        Assert.Equal(2, plan.Chapters.Count);
        Assert.Equal(TimeSpan.Zero, plan.Chapters[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(30), plan.Chapters[0].End);
        Assert.Equal(TimeSpan.FromSeconds(30), plan.Chapters[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(60), plan.Chapters[1].End);
        Assert.Equal(TimeSpan.FromSeconds(60), plan.TotalDuration);
    }

    [Fact]
    public void Chapter_positions_come_from_the_real_durations_not_from_the_nominal_segment_length()
    {
        // 5.008 s segments (audio overhang): the second run starts at 3 x 5.008, not at 15.
        var a = Enumerable.Range(0, 3).Select(i => Seg($"a{i}", 5 * (i + 1), 5.008)).ToList();
        var b = Enumerable.Range(0, 2).Select(i => Seg($"b{i}", 100 + 5 * i, 5.008)).ToList();
        var plan = ClipStitcher.Plan(a.Concat(b).ToList(), NoClassify, Title);

        Assert.Equal(2, plan.Chapters.Count);
        Assert.Equal(3 * 5.008, plan.Chapters[0].End.TotalSeconds, 6);
        Assert.Equal(plan.Chapters[0].End, plan.Chapters[1].Start);
        Assert.Equal(5 * 5.008, plan.Chapters[1].End.TotalSeconds, 6);
        Assert.Equal(plan.TotalDuration, plan.Chapters[^1].End);
    }

    [Fact]
    public void A_1_5_second_hole_is_jitter_not_a_gap()
    {
        var first = Run(2, 5);                       // ends 5, 10
        var second = new List<SegmentCandidate> { Seg("late", 10 + 1.5 + 5) };   // starts at 11.5
        var plan = ClipStitcher.Plan(first.Concat(second).ToList(), NoClassify, Title);
        Assert.Empty(plan.Gaps);
        Assert.Empty(plan.Chapters);
    }

    [Fact]
    public void A_hole_of_exactly_two_seconds_is_a_gap()
    {
        var segs = new List<SegmentCandidate> { Seg("a", 5), Seg("b", 5 + 2 + 5) };
        Assert.Single(ClipStitcher.Plan(segs, NoClassify, Title).Gaps);
    }

    [Fact]
    public void An_unreadable_segment_is_dropped_and_leaves_an_unreadable_gap()
    {
        var segs = new List<SegmentCandidate>
        {
            Seg("a", 5), Seg("b", 10),
            new("broken", 0, T0.AddSeconds(15), 1000, null),
            Seg("c", 20), Seg("d", 25),
        };
        var plan = ClipStitcher.Plan(segs, (f, t) => (GapReason.BufferRestart, null), Title);

        Assert.DoesNotContain(plan.Segments, s => s.Path == "broken");
        Assert.Equal(4, plan.Segments.Count);
        var gap = Assert.Single(plan.Gaps);
        Assert.Equal(GapReason.UnreadableSegment, gap.Reason);
        Assert.Equal(T0.AddSeconds(10), gap.FromUtc);
        Assert.Equal(T0.AddSeconds(15), gap.ToUtc);
        Assert.Equal(2, plan.Chapters.Count);
    }

    [Fact]
    public void An_unreadable_segment_at_the_edge_only_shortens_the_clip()
    {
        var segs = new List<SegmentCandidate>
        {
            new("broken-first", 0, T0.AddSeconds(0), 1000, null),
            Seg("a", 5), Seg("b", 10),
            new("broken-last", 0, T0.AddSeconds(15), 1000, null),
        };
        var plan = ClipStitcher.Plan(segs, NoClassify, Title);
        Assert.Empty(plan.Gaps);
        Assert.Equal(2, plan.Segments.Count);
    }

    [Fact]
    public void A_zero_length_segment_counts_as_unreadable()
    {
        var segs = new List<SegmentCandidate> { Seg("a", 5), new("zero", 0, T0.AddSeconds(10), 10, TimeSpan.Zero), Seg("b", 15) };
        var plan = ClipStitcher.Plan(segs, NoClassify, Title);
        Assert.Equal(2, plan.Segments.Count);
    }

    [Fact]
    public void Chapter_titles_get_the_run_number_the_wall_clock_span_and_the_gap_before()
    {
        var first = Run(2, 5);
        var second = Run(2, 60, "t");
        ClipGap? seen = null;
        DateTime from2 = default, to2 = default;
        var plan = ClipStitcher.Plan(first.Concat(second).ToList(), (f, t) => (GapReason.OtherTarget, "Discord"),
            (run, f, t, g) =>
            {
                if (run == 2) { seen = g; from2 = f; to2 = t; }
                return $"run{run}";
            });
        Assert.Equal("run1", plan.Chapters[0].Title);
        Assert.Equal("run2", plan.Chapters[1].Title);
        Assert.NotNull(seen);
        Assert.Equal(GapReason.OtherTarget, seen!.Reason);
        Assert.Equal(T0.AddSeconds(55), from2);    // first segment of the run ended at 60 and is 5 s long
        Assert.Equal(T0.AddSeconds(65), to2);
        Assert.Equal(T0.AddSeconds(10), seen!.FromUtc);
        Assert.Equal(T0.AddSeconds(55), seen.ToUtc);
    }

    [Fact]
    public void Three_runs_make_three_chapters()
    {
        var segs = Run(2, 5).Concat(Run(2, 60, "b")).Concat(Run(2, 200, "c")).ToList();
        var plan = ClipStitcher.Plan(segs, NoClassify, Title);
        Assert.Equal(2, plan.Gaps.Count);
        Assert.Equal(3, plan.Chapters.Count);
        Assert.Equal(new[] { "run1", "run2", "run3" }, plan.Chapters.Select(c => c.Title));
    }

    [Fact]
    public void Planning_nothing_is_an_empty_plan()
    {
        var plan = ClipStitcher.Plan(new List<SegmentCandidate>(), NoClassify, Title);
        Assert.Empty(plan.Segments);
        Assert.Equal(TimeSpan.Zero, plan.TotalDuration);
    }

    // ---- ClassifyGap ----

    private static GenerationInfo Gen(int g, SegmentIdentity id, string label, double start, double? stop)
        => new(g, id, label, T0.AddSeconds(start), stop is null ? null : T0.AddSeconds(stop.Value));

    private static readonly DateTime Now = T0.AddSeconds(2000);

    [Fact]
    public void A_discord_generation_inside_the_gap_is_another_target()
    {
        var gens = new[] { Gen(0, Arma, "arma", 0, 30), Gen(1, Discord, "Discord", 31, 74), Gen(2, Arma, "arma", 75, null) };
        var (reason, detail) = ClipStitcher.ClassifyGap(T0.AddSeconds(30), T0.AddSeconds(75), Now, Arma, gens, Array.Empty<SuspendInterval>());
        Assert.Equal(GapReason.OtherTarget, reason);
        Assert.Equal("Discord", detail);
    }

    [Fact]
    public void The_target_with_the_largest_overlap_is_named()
    {
        var gens = new[]
        {
            Gen(1, Discord, "Discord", 31, 40),
            Gen(2, new SegmentIdentity("wgc:300:1x1", "s"), "Browser", 40, 74),
        };
        var (_, detail) = ClipStitcher.ClassifyGap(T0.AddSeconds(30), T0.AddSeconds(75), Now, Arma, gens, Array.Empty<SuspendInterval>());
        Assert.Equal("Browser", detail);
    }

    [Fact]
    public void A_suspension_inside_the_gap_is_a_manual_recording()
    {
        var gens = new[] { Gen(0, Arma, "arma", 0, 30), Gen(1, Arma, "arma", 75, null) };
        var susp = new[] { new SuspendInterval(T0.AddSeconds(31), T0.AddSeconds(74)) };
        var (reason, _) = ClipStitcher.ClassifyGap(T0.AddSeconds(30), T0.AddSeconds(75), Now, Arma, gens, susp);
        Assert.Equal(GapReason.ManualRecording, reason);
    }

    [Fact]
    public void A_still_open_suspension_runs_until_now()
    {
        var susp = new[] { new SuspendInterval(T0.AddSeconds(31), null) };
        var (reason, _) = ClipStitcher.ClassifyGap(T0.AddSeconds(30), T0.AddSeconds(75), Now, Arma, Array.Empty<GenerationInfo>(), susp);
        Assert.Equal(GapReason.ManualRecording, reason);
    }

    [Fact]
    public void Another_target_beats_a_manual_recording()
    {
        var gens = new[] { Gen(1, Discord, "Discord", 40, 50) };
        var susp = new[] { new SuspendInterval(T0.AddSeconds(31), T0.AddSeconds(74)) };
        var (reason, _) = ClipStitcher.ClassifyGap(T0.AddSeconds(30), T0.AddSeconds(75), Now, Arma, gens, susp);
        Assert.Equal(GapReason.OtherTarget, reason);
    }

    [Fact]
    public void The_same_identity_running_through_the_gap_is_an_interruption()
    {
        var gens = new[] { Gen(0, Arma, "arma", 0, null) };
        var (reason, _) = ClipStitcher.ClassifyGap(T0.AddSeconds(30), T0.AddSeconds(75), Now, Arma, gens, Array.Empty<SuspendInterval>());
        Assert.Equal(GapReason.Interruption, reason);
    }

    [Fact]
    public void Nothing_running_during_the_gap_is_a_buffer_restart()
    {
        var gens = new[] { Gen(0, Arma, "arma", 0, 29), Gen(1, Arma, "arma", 76, null) };
        var (reason, detail) = ClipStitcher.ClassifyGap(T0.AddSeconds(30), T0.AddSeconds(75), Now, Arma, gens, Array.Empty<SuspendInterval>());
        Assert.Equal(GapReason.BufferRestart, reason);
        Assert.Null(detail);
    }

    [Fact]
    public void A_generation_that_only_touches_the_gap_edge_does_not_count()
    {
        var gens = new[] { Gen(1, Discord, "Discord", 0, 30) };   // ends exactly where the gap starts
        var (reason, _) = ClipStitcher.ClassifyGap(T0.AddSeconds(30), T0.AddSeconds(75), Now, Arma, gens, Array.Empty<SuspendInterval>());
        Assert.Equal(GapReason.BufferRestart, reason);
    }

    // ---- concat list ----

    [Fact]
    public void Concat_list_pins_every_file_to_its_header_duration()
    {
        var lines = ClipStitcher.ConcatListLines(new[]
        {
            Seg("C:\\buf\\seg_0_00.mp4", 5, 5.0096),
            Seg("C:\\buf\\seg_0_01.mp4", 10, 5.0),
        }).ToList();

        Assert.Equal(new[]
        {
            "file 'C:\\buf\\seg_0_00.mp4'", "duration 5.0096",
            "file 'C:\\buf\\seg_0_01.mp4'", "duration 5",
        }, lines);
    }

    [Fact]
    public void Concat_list_escapes_single_quotes_in_paths()
        => Assert.Equal(@"file 'C:\it'\''s\seg.mp4'",
            ClipStitcher.ConcatListLines(new[] { Seg(@"C:\it's\seg.mp4", 5) }).First());

    [Fact]
    public void Concat_list_without_a_duration_has_only_the_file_line()
    {
        var lines = ClipStitcher.ConcatListLines(new[] { new SegmentCandidate("a.mp4", 0, T0, 1, null) }).ToList();
        Assert.Single(lines);
    }

    [Fact]
    public void Concat_list_durations_use_a_dot_whatever_the_culture()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Contains("duration 1.0096", ClipStitcher.ConcatListLines(new[] { Seg("a.mp4", 1, 1.0096) }));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
    }

    // ---- ffmetadata ----

    [Fact]
    public void Chapter_metadata_has_header_timebase_and_millisecond_positions()
    {
        var text = ClipStitcher.ChapterMetadata(new[]
        {
            new ClipChapter(TimeSpan.Zero, TimeSpan.FromSeconds(30.0004), "Teil 1"),
            new ClipChapter(TimeSpan.FromSeconds(30.0004), TimeSpan.FromSeconds(60), "Teil 2"),
        });
        Assert.StartsWith(";FFMETADATA1\n", text);
        Assert.Equal(2, text.Split("[CHAPTER]").Length - 1);
        Assert.Contains("TIMEBASE=1/1000\nSTART=0\nEND=30000\ntitle=Teil 1\n", text);
        Assert.Contains("START=30000\nEND=60000\ntitle=Teil 2\n", text);
    }

    [Fact]
    public void Chapter_titles_escape_the_ffmetadata_special_characters()
    {
        var text = ClipStitcher.ChapterMetadata(new[]
        {
            new ClipChapter(TimeSpan.Zero, TimeSpan.FromSeconds(1), "a=b;c#d\\e\nf"),
        });
        Assert.Contains("title=a\\=b\\;c\\#d\\\\e\\\nf\n", text);
    }

    [Fact]
    public void Umlauts_survive_in_chapter_titles()
        => Assert.Contains("title=Lücke", ClipStitcher.ChapterMetadata(new[] { new ClipChapter(TimeSpan.Zero, TimeSpan.FromSeconds(1), "Lücke") }));

    // ---- texts ----

    [Fact]
    public void First_chapter_title_is_part_one_with_the_time_span()
        => Assert.Equal("Teil 1: 21:00:05–21:03:10",
            ClipStitcher.ChapterTitle(1, T0.AddSeconds(5), T0.AddSeconds(190), null, Clock));

    [Fact]
    public void Later_chapter_titles_name_the_gap_before_them()
    {
        var gap = new ClipGap(T0.AddSeconds(190), T0.AddSeconds(235), GapReason.OtherTarget, "Discord");
        Assert.Equal("Teil 2: 21:03:55–21:30:00 (nach Lücke 45 s, Alt-Tab: Discord)",
            ClipStitcher.ChapterTitle(2, T0.AddSeconds(235), T0.AddSeconds(1800), gap, Clock));
    }

    [Fact]
    public void Gap_note_for_one_gap()
    {
        var gap = new ClipGap(T0.AddSeconds(190), T0.AddSeconds(235), GapReason.OtherTarget, "Discord");
        Assert.Equal("Automatisch: Lücke 21:03:10–21:03:55 (45 s, Alt-Tab: Discord).", ClipStitcher.GapNote(new[] { gap }, Clock));
    }

    [Fact]
    public void Gap_note_for_several_gaps_leads_with_the_count()
    {
        var gaps = new[]
        {
            new ClipGap(T0.AddSeconds(190), T0.AddSeconds(235), GapReason.OtherTarget, "Discord"),
            new ClipGap(T0.AddSeconds(600), T0.AddSeconds(630), GapReason.ManualRecording, null),
        };
        var note = ClipStitcher.GapNote(gaps, Clock);
        Assert.StartsWith("Automatisch: 2 Lücken — 21:03:10–21:03:55 (45 s, Alt-Tab: Discord); 21:10:00–21:10:30 (30 s, Strg+F9-Aufnahme)", note);
        Assert.EndsWith(".", note);
    }

    [Fact]
    public void Gap_note_with_forty_gaps_stays_in_the_limit_and_ends_with_an_ellipsis_at_a_gap_boundary()
    {
        var gaps = Enumerable.Range(0, 40)
            .Select(i => new ClipGap(T0.AddSeconds(i * 100), T0.AddSeconds(i * 100 + 45), GapReason.OtherTarget, "Discord"))
            .ToList();
        var note = ClipStitcher.GapNote(gaps, Clock);

        Assert.True(note.Length <= GalleryMetaService.MaxNoteLength, $"length {note.Length}");
        Assert.EndsWith("; …", note);
        Assert.StartsWith("Automatisch: 40 Lücken — ", note);
        // cut at a boundary: the last listed entry is complete
        Assert.Contains("(45 s, Alt-Tab: Discord); …", note);
    }

    [Fact]
    public void Gap_note_of_nothing_is_empty()
        => Assert.Equal("", ClipStitcher.GapNote(Array.Empty<ClipGap>(), Clock));

    [Fact]
    public void Toast_line_for_one_gap_names_length_and_reason()
    {
        var gap = new ClipGap(T0.AddSeconds(190), T0.AddSeconds(235), GapReason.OtherTarget, "Discord");
        Assert.Equal("1 Lücke (45 s, Alt-Tab: Discord) — als Kapitel + Notiz markiert", ClipStitcher.GapToastLine(new[] { gap }));
    }

    [Fact]
    public void Toast_line_for_several_gaps_sums_them_up()
    {
        var gaps = new[]
        {
            new ClipGap(T0.AddSeconds(0), T0.AddSeconds(45), GapReason.OtherTarget, "Discord"),
            new ClipGap(T0.AddSeconds(100), T0.AddSeconds(130), GapReason.ManualRecording, null),
        };
        Assert.Equal("2 Lücken (zusammen 1:15 min) — als Kapitel + Notiz markiert", ClipStitcher.GapToastLine(gaps));
    }

    [Theory]
    [InlineData(GapReason.OtherTarget, "Discord", "Alt-Tab: Discord")]
    [InlineData(GapReason.OtherTarget, null, "Alt-Tab")]
    [InlineData(GapReason.ManualRecording, null, "Strg+F9-Aufnahme")]
    [InlineData(GapReason.BufferRestart, null, "Buffer-Neustart")]
    [InlineData(GapReason.Interruption, null, "Unterbrechung")]
    [InlineData(GapReason.UnreadableSegment, null, "Segment unlesbar")]
    public void Reason_texts(GapReason reason, string? detail, string expected)
        => Assert.Equal(expected, ClipStitcher.ReasonText(reason, detail));
}
