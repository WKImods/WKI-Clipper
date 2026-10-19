using System;
using System.Collections.Generic;

namespace WKI_Clipper.Services;

public enum CutMode { Fast, Exact }

/// <param name="StartFrame">First frame of the result (inclusive).</param>
/// <param name="EndFrame">Last frame of the result (inclusive).</param>
/// <param name="StartSnapped">Fast mode: the start was moved back to the keyframe before it.</param>
/// <param name="EndApproximate">Fast mode with B-frames: the end can lie a few frames later.</param>
public readonly record struct CutRange(int StartFrame, int EndFrame, bool StartSnapped, bool EndApproximate);

public enum CutValidation { Ok, Reversed, WholeClip }

/// <summary>
/// Frame numbers, the preview's time axis and ffmpeg's time axis of one clip, all from an <see cref="Mp4VideoIndex"/>.
/// Frame numbers are never computed as "time x fps": a clip stitched from 5-second segments has gaps in its
/// time stamps (~61 frames of drift after 1000 s). The index is the only truth.
/// </summary>
public sealed class ClipTimeline
{
    /// <summary>Times from the float arithmetic of the index and of MediaElement agree to far better than this.</summary>
    private const double Epsilon = 1e-6;
    private const double FallbackFrameDuration = 1.0 / 30;

    private readonly Mp4VideoIndex _index;
    private readonly double[] _preview;
    private readonly double[] _file;

    public ClipTimeline(Mp4VideoIndex index)
    {
        if (index.FrameCount <= 0) throw new ArgumentException("The index has no frames.", nameof(index));
        _index = index;
        _preview = index.PreviewSeconds;
        _file = index.FileSeconds;
        MedianFrameDuration = ComputeMedianDuration(_preview);
    }

    public int FrameCount => _preview.Length;

    /// <summary>FrameCount - 1.</summary>
    public int LastFrame => _preview.Length - 1;

    public double MedianFrameDuration { get; }

    /// <summary>Length of the timeline: where the last frame ends on the preview axis.</summary>
    public double PreviewEnd => _preview[LastFrame] + FrameDuration(LastFrame);

    /// <summary>Distance to the next frame; the last frame has none and gets the median.</summary>
    public double FrameDuration(int frame)
        => frame < LastFrame ? _preview[frame + 1] - _preview[frame] : MedianFrameDuration;

    /// <summary>The frame on screen at that preview time: the last one that started at or before it. Clamped to the clip.</summary>
    public int FrameAtPreview(double previewSeconds)
    {
        double p = previewSeconds + Epsilon;
        int lo = 0, hi = LastFrame;
        // largest i with _preview[i] <= p
        while (lo < hi)
        {
            int mid = lo + (hi - lo + 1) / 2;
            if (_preview[mid] <= p) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>
    /// Where to seek the preview to show a frame: its middle. The beginning is not reliable — rounding sometimes
    /// shows the frame before (measured: 1 duplicate in 8 steps at the exact start, 32 of 32 right in the middle).
    /// </summary>
    public double PreviewSeekTarget(int frame) => _preview[frame] + FrameDuration(frame) / 2;

    /// <summary>ffmpeg time (-ss) of a frame's start.</summary>
    public double FileStartOf(int frame) => _file[frame];

    /// <summary>ffmpeg time where the result ends after <paramref name="lastFrame"/>: the start of the next frame, or one duration past the last.</summary>
    public double FileEndExclusive(int lastFrame)
        => lastFrame < LastFrame ? _file[lastFrame + 1] : _file[lastFrame] + FrameDuration(lastFrame);

    /// <summary>The keyframe at or before <paramref name="frame"/>; frame 0 when there is none.</summary>
    public int KeyframeAtOrBefore(int frame)
    {
        int at = LastKeyIndexAtOrBefore(frame);
        return at < 0 ? 0 : _index.Keyframes[at];
    }

    /// <summary>The keyframe strictly before <paramref name="frame"/>.</summary>
    public int? PreviousKeyframe(int frame)
    {
        int at = LastKeyIndexAtOrBefore(frame - 1);
        return at < 0 ? null : _index.Keyframes[at];
    }

    /// <summary>The keyframe strictly after <paramref name="frame"/>.</summary>
    public int? NextKeyframe(int frame)
    {
        int at = LastKeyIndexAtOrBefore(frame) + 1;
        return at < _index.Keyframes.Length ? _index.Keyframes[at] : null;
    }

    /// <summary>Shift+arrows: the frame that is on screen <paramref name="deltaSeconds"/> away. Clamped to the clip.</summary>
    public int FrameAtPreviewOffset(int frame, double deltaSeconds)
        => FrameAtPreview(_preview[frame] + deltaSeconds);

    /// <summary>
    /// Clamps both marks into the clip but never swaps them (<see cref="Validate"/> reports a reversed pair).
    /// Fast: the start moves back to the keyframe before it, so the moment the user asked for is surely inside.
    /// </summary>
    public CutRange Resolve(int requestedIn, int requestedOut, CutMode mode)
    {
        int a = Math.Clamp(requestedIn, 0, LastFrame);
        int b = Math.Clamp(requestedOut, 0, LastFrame);
        if (mode == CutMode.Exact) return new CutRange(a, b, false, false);

        int start = KeyframeAtOrBefore(a);
        return new CutRange(start, b, start != a, _index.HasBFrames);
    }

    public CutValidation Validate(CutRange range, CutMode mode)
    {
        if (range.EndFrame < range.StartFrame) return CutValidation.Reversed;
        if (range.StartFrame == 0 && range.EndFrame == LastFrame) return CutValidation.WholeClip;
        return CutValidation.Ok;
    }

    /// <summary>
    /// For the gallery note: seconds relative to the first frame. The start is rounded down and the end up by the
    /// caller, so the range surely contains what it names.
    /// </summary>
    public (double Start, double End) NoteRange(CutRange range)
        => (_preview[range.StartFrame] - _preview[0],
            _preview[range.EndFrame] + FrameDuration(range.EndFrame) - _preview[0]);

    // ---------------------------------------------------------------- helpers

    /// <summary>Position in the keyframe list of the last keyframe &lt;= frame; -1 if none.</summary>
    private int LastKeyIndexAtOrBefore(int frame)
    {
        var keys = _index.Keyframes;
        int lo = 0, hi = keys.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (keys[mid] <= frame) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    private static double ComputeMedianDuration(double[] preview)
    {
        if (preview.Length < 2) return FallbackFrameDuration;
        var deltas = new List<double>(preview.Length - 1);
        for (int i = 1; i < preview.Length; i++)
        {
            double d = preview[i] - preview[i - 1];
            if (d > 0) deltas.Add(d);
        }
        if (deltas.Count == 0) return FallbackFrameDuration;
        deltas.Sort();
        return deltas[deltas.Count / 2];
    }
}
