using System;
using System.Linq;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Frame / preview time / ffmpeg time mapping and range resolution on synthetic indexes.</summary>
public sealed class ClipTimelineTests
{
    /// <summary>
    /// Index of <paramref name="frames"/> frames at <paramref name="fps"/>. Preview time starts at
    /// <paramref name="previewStart"/>, file time at <paramref name="fileStart"/>; a keyframe every
    /// <paramref name="keyEvery"/> frames; <paramref name="gapEvery"/> inserts a 5 ms gap like the segment seams of a long clip.
    /// </summary>
    internal static Mp4VideoIndex Index(int frames = 300, double fps = 30, int keyEvery = 30, double previewStart = 0,
                                        double fileStart = 0, bool bframes = false, int gapEvery = 0)
    {
        var preview = new double[frames];
        var file = new double[frames];
        double t = 0;
        for (int i = 0; i < frames; i++)
        {
            if (gapEvery > 0 && i > 0 && i % gapEvery == 0) t += 0.005;
            preview[i] = previewStart + t;
            file[i] = fileStart + t;
            t += 1.0 / fps;
        }
        var keys = Enumerable.Range(0, (frames + keyEvery - 1) / keyEvery).Select(k => k * keyEvery).ToArray();
        return new Mp4VideoIndex("avc1", 1920, 1080, preview, file, keys, bframes, frames * 1000L, 1, t, fileStart + t);
    }

    // ---- basics ----

    [Fact]
    public void Counts_durations_and_the_timeline_end()
    {
        var tl = new ClipTimeline(Index(90));

        Assert.Equal(90, tl.FrameCount);
        Assert.Equal(89, tl.LastFrame);
        Assert.Equal(1 / 30.0, tl.MedianFrameDuration, 9);
        Assert.Equal(1 / 30.0, tl.FrameDuration(10), 9);
        Assert.Equal(1 / 30.0, tl.FrameDuration(89), 9);      // the last frame has no successor: the median
        Assert.Equal(3.0, tl.PreviewEnd, 6);
    }

    [Fact]
    public void A_single_frame_clip_still_has_a_frame_duration()
    {
        var tl = new ClipTimeline(Index(1, keyEvery: 1));

        Assert.Equal(0, tl.LastFrame);
        Assert.True(tl.MedianFrameDuration > 0);
        Assert.Equal(0, tl.FrameAtPreview(5));
    }

    // ---- 1: preview time → frame ----

    [Fact]
    public void A_preview_time_maps_to_the_frame_that_is_on_screen_at_that_moment()
    {
        var tl = new ClipTimeline(Index(300));
        double f10 = 10 / 30.0;

        Assert.Equal(10, tl.FrameAtPreview(f10));                       // exactly on the start
        Assert.Equal(10, tl.FrameAtPreview(f10 + 0.5 / 30));            // in the middle
        Assert.Equal(10, tl.FrameAtPreview(f10 + 0.99 / 30));           // almost at the next one
        Assert.Equal(9, tl.FrameAtPreview(f10 - 0.001));                // just before
        Assert.Equal(299, tl.FrameAtPreview(1000));                     // after the end
        Assert.Equal(0, tl.FrameAtPreview(-5));
    }

    [Fact]
    public void Float_noise_below_a_microsecond_does_not_pick_the_previous_frame()
    {
        var tl = new ClipTimeline(Index(300));

        Assert.Equal(10, tl.FrameAtPreview(10 / 30.0 - 5e-7));
    }

    [Fact]
    public void A_source_whose_first_frame_is_displayed_late_maps_everything_before_it_to_frame_0()
    {
        var tl = new ClipTimeline(Index(300, previewStart: 0.0667, bframes: true));

        Assert.Equal(0, tl.FrameAtPreview(0));
        Assert.Equal(0, tl.FrameAtPreview(0.0667));
        Assert.Equal(1, tl.FrameAtPreview(0.0667 + 1 / 30.0));
    }

    // ---- 2: never frame = time * fps ----

    [Fact]
    public void Gaps_in_the_time_stamps_do_not_shift_the_frame_numbers()
    {
        // 5 ms after every 300th frame: after 1000 s that is 61 frames of drift against t * fps.
        var index = Index(30_000, gapEvery: 300);
        var tl = new ClipTimeline(index);

        foreach (var n in new[] { 0, 299, 300, 301, 5_000, 17_777, 29_999 })
        {
            Assert.Equal(n, tl.FrameAtPreview(index.PreviewSeconds[n] + 0.001));
            Assert.Equal(n, tl.FrameAtPreview(tl.PreviewSeekTarget(n)));
        }
        int naive = (int)(index.PreviewSeconds[29_999] * 30);
        Assert.NotEqual(29_999, naive);   // the trap this mapping avoids
    }

    // ---- 3: round trip of the seek target ----

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.0667, 0)]
    [InlineData(0, 300)]
    public void The_seek_target_is_the_frame_middle_and_maps_back_to_the_same_frame(double previewStart, int gapEvery)
    {
        var index = Index(900, previewStart: previewStart, gapEvery: gapEvery);
        var tl = new ClipTimeline(index);

        for (int i = 0; i < tl.FrameCount; i++)
        {
            Assert.Equal(i, tl.FrameAtPreview(tl.PreviewSeekTarget(i)));
            Assert.Equal(index.PreviewSeconds[i] + tl.FrameDuration(i) / 2, tl.PreviewSeekTarget(i), 9);
        }
    }

    // ---- 4: ffmpeg axis ----

    [Fact]
    public void The_file_times_carry_the_edit_list_offset()
    {
        var tl = new ClipTimeline(Index(90, fileStart: 0.5));

        Assert.Equal(0.5 + 10 / 30.0, tl.FileStartOf(10), 9);
        Assert.Equal(0.5 + 11 / 30.0, tl.FileEndExclusive(10), 9);        // where the next frame begins
        Assert.Equal(0.5 + 90 / 30.0, tl.FileEndExclusive(89), 6);        // the last frame ends one duration after its start
        Assert.Equal(0.5, tl.FileStartOf(0), 9);
    }

    // ---- 5: keyframes ----

    [Fact]
    public void Keyframe_search_finds_the_one_at_or_before_and_the_neighbours()
    {
        var tl = new ClipTimeline(Index(300, keyEvery: 30));

        Assert.Equal(60, tl.KeyframeAtOrBefore(60));       // exactly on one
        Assert.Equal(60, tl.KeyframeAtOrBefore(89));       // between two
        Assert.Equal(0, tl.KeyframeAtOrBefore(0));
        Assert.Equal(270, tl.KeyframeAtOrBefore(299));

        Assert.Equal(30, tl.PreviousKeyframe(60));         // strictly before
        Assert.Equal(60, tl.PreviousKeyframe(89));
        Assert.Null(tl.PreviousKeyframe(0));
        Assert.Equal(90, tl.NextKeyframe(60));             // strictly after
        Assert.Equal(90, tl.NextKeyframe(89));
        Assert.Null(tl.NextKeyframe(270));
        Assert.Null(tl.NextKeyframe(299));
    }

    [Fact]
    public void A_frame_before_the_first_keyframe_snaps_to_frame_0()
    {
        var index = Index(100, keyEvery: 30) with { Keyframes = new[] { 5, 35, 65 } };
        var tl = new ClipTimeline(index);

        Assert.Equal(0, tl.KeyframeAtOrBefore(3));
        Assert.Equal(5, tl.NextKeyframe(0));
    }

    // ---- 6: shift+arrows ----

    [Fact]
    public void Jumping_by_seconds_lands_on_the_frame_of_that_moment_and_clamps_at_both_ends()
    {
        var tl = new ClipTimeline(Index(300));

        Assert.Equal(130, tl.FrameAtPreviewOffset(100, 1.0));
        Assert.Equal(70, tl.FrameAtPreviewOffset(100, -1.0));
        Assert.Equal(0, tl.FrameAtPreviewOffset(10, -1.0));
        Assert.Equal(299, tl.FrameAtPreviewOffset(280, 1.0));
        Assert.Equal(100, tl.FrameAtPreviewOffset(100, 0));
    }

    // ---- 7 / 8: resolving a range ----

    [Fact]
    public void Fast_mode_moves_the_start_back_to_the_keyframe_before_it()
    {
        var tl = new ClipTimeline(Index(300, keyEvery: 30));

        var mid = tl.Resolve(95, 250, CutMode.Fast);
        Assert.Equal(new CutRange(90, 250, true, false), mid);

        var onKey = tl.Resolve(90, 250, CutMode.Fast);
        Assert.Equal(new CutRange(90, 250, false, false), onKey);
    }

    [Fact]
    public void Fast_mode_on_a_source_with_B_frames_marks_the_end_as_approximate()
    {
        var tl = new ClipTimeline(Index(300, keyEvery: 30, bframes: true));

        var r = tl.Resolve(95, 250, CutMode.Fast);

        Assert.True(r.EndApproximate);
        Assert.Equal(250, r.EndFrame);
        Assert.False(tl.Resolve(95, 250, CutMode.Exact).EndApproximate);
    }

    [Fact]
    public void Exact_mode_keeps_both_ends()
    {
        var tl = new ClipTimeline(Index(300, keyEvery: 30));

        Assert.Equal(new CutRange(95, 250, false, false), tl.Resolve(95, 250, CutMode.Exact));
    }

    [Fact]
    public void Out_of_range_marks_are_clamped_but_never_swapped()
    {
        var tl = new ClipTimeline(Index(300));

        var r = tl.Resolve(-4, 1000, CutMode.Exact);
        Assert.Equal(0, r.StartFrame);
        Assert.Equal(299, r.EndFrame);

        var reversed = tl.Resolve(200, 100, CutMode.Exact);
        Assert.Equal(200, reversed.StartFrame);
        Assert.Equal(100, reversed.EndFrame);
    }

    [Fact]
    public void Validation_reports_a_reversed_range_and_a_whole_clip()
    {
        var tl = new ClipTimeline(Index(300, keyEvery: 30));

        Assert.Equal(CutValidation.Reversed, tl.Validate(tl.Resolve(200, 100, CutMode.Exact), CutMode.Exact));
        Assert.Equal(CutValidation.WholeClip, tl.Validate(tl.Resolve(0, 299, CutMode.Exact), CutMode.Exact));
        Assert.Equal(CutValidation.Ok, tl.Validate(tl.Resolve(1, 299, CutMode.Exact), CutMode.Exact));
        Assert.Equal(CutValidation.Ok, tl.Validate(tl.Resolve(0, 298, CutMode.Exact), CutMode.Exact));
    }

    [Fact]
    public void Fast_mode_with_the_start_in_the_first_GOP_and_the_end_at_the_last_frame_is_the_whole_clip()
    {
        var tl = new ClipTimeline(Index(300, keyEvery: 30));

        var range = tl.Resolve(12, 299, CutMode.Fast);

        Assert.Equal(0, range.StartFrame);
        Assert.Equal(CutValidation.WholeClip, tl.Validate(range, CutMode.Fast));
        Assert.Equal(CutValidation.Ok, tl.Validate(tl.Resolve(12, 299, CutMode.Exact), CutMode.Exact));
    }

    // ---- 9: the note ----

    [Fact]
    public void The_note_range_is_relative_to_the_first_frame()
    {
        var tl = new ClipTimeline(Index(300, previewStart: 0.5));

        var (start, end) = tl.NoteRange(new CutRange(30, 89, false, false));

        Assert.Equal(1.0, start, 6);                       // frame 30 begins one second after frame 0
        Assert.Equal(3.0, end, 6);                         // frame 89 ends where frame 90 begins
    }
}
