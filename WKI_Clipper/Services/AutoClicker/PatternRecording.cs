using System;
using System.Collections.Generic;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>How a recording ended. <see cref="PatternRecordingOutcome.Cancelled"/> comes from the helper (lock, suppression, disarm),
/// never from <see cref="PatternRecording.Finish"/>.</summary>
public enum PatternRecordingOutcome { Ok, Cut, Empty, Cancelled }

/// <summary>The result of a recording: <see cref="Data"/> is set for <see cref="PatternRecordingOutcome.Ok"/> and <see cref="PatternRecordingOutcome.Cut"/> only.</summary>
public sealed record PatternRecordingResult(PatternRecordingOutcome Outcome, PatternData? Data);

/// <summary>
/// Compacts one recording into a <see cref="PatternData"/> (mouse-patterns design 4.2, 5.1). The helper's recorder feeds it the physical
/// mouse motion of both tracks with QPC timestamps (each track in its own order; the two may arrive interleaved in any order) and calls
/// <see cref="Finish"/> when the recording stops.
/// <list type="bullet">
/// <item>Time 0 is the first event with motion in either track; idle time before it and after the last one is trimmed, pauses inside stay.</item>
/// <item>Events are summed into 1 ms bins (<see cref="MousePatternRules.BinMs"/>): the bin at k holds the events in (k-1, k] ms, which is
/// the interval <see cref="PatternCurve"/> spreads it over again. Bins that sum to no motion are left out.</item>
/// <item>Events more than <see cref="MousePatternRules.MaxDurationMs"/> after time 0 are dropped and the result is
/// <see cref="PatternRecordingOutcome.Cut"/>.</item>
/// </list>
/// Not thread-safe: the recorder drains its rings into it from one thread.
/// </summary>
public sealed class PatternRecording
{
    /// <summary>Below the QPC resolution: an event that lands on a bin edge up to floating-point noise belongs to the bin ending there.</summary>
    private const double EdgeMs = 1e-6;

    private readonly record struct Sample(double AtMs, int Dx, int Dy);

    private readonly double _startMs;
    private readonly List<Sample> _raw = new();
    private readonly List<Sample> _pointer = new();
    private double _firstMs = double.PositiveInfinity;
    private bool _cut;

    /// <param name="startMs">When the recording started (the recorder's clock); earlier events are stale and ignored.</param>
    public PatternRecording(double startMs) => _startMs = startMs;

    /// <summary>Raw Input counts of a physical mouse (relative records only).</summary>
    public void AddRaw(double atMs, int dx, int dy) => Add(_raw, atMs, dx, dy);

    /// <summary>On-screen pointer offsets in pixels (low-level mouse hook).</summary>
    public void AddPointer(double atMs, int dx, int dy) => Add(_pointer, atMs, dx, dy);

    /// <summary>
    /// True from <see cref="MousePatternRules.MaxDurationMs"/> after the first movement on (never before any movement): the helper stops
    /// the recording then. Once it returned true, <see cref="Finish"/> reports <see cref="PatternRecordingOutcome.Cut"/>.
    /// </summary>
    public bool LimitReached(double nowMs)
    {
        if (!(nowMs - _firstMs >= MousePatternRules.MaxDurationMs)) return false;   // also false for NaN and before the first movement
        _cut = true;
        return true;
    }

    /// <summary>
    /// The compacted recording: <see cref="PatternRecordingOutcome.Empty"/> without any motion, otherwise both tracks shifted so the first
    /// bin of either sits at 0 ms, and <see cref="PatternData.DurationMs"/> = the last bin of either (a pass covers every bin of both).
    /// The tracks meet <see cref="PatternCodec"/>'s rules. Leaves the recording as it is, so it can be called again.
    /// </summary>
    public PatternRecordingResult Finish()
    {
        var raw = Sum(_raw, out bool rawCut);
        var pointer = Sum(_pointer, out bool pointerCut);
        int first = Math.Min(FirstMoving(raw), FirstMoving(pointer));
        if (first == int.MaxValue) return new PatternRecordingResult(PatternRecordingOutcome.Empty, null);

        // An integral shift keeps every bin edge where it was; it only matters when the first bin summed to nothing.
        var rawTrack = ToTrack(raw, first, out int rawEnd);
        var pointerTrack = ToTrack(pointer, first, out int pointerEnd);
        var data = new PatternData(rawTrack, pointerTrack, Math.Max(rawEnd, pointerEnd));
        bool cut = _cut || rawCut || pointerCut;
        return new PatternRecordingResult(cut ? PatternRecordingOutcome.Cut : PatternRecordingOutcome.Ok, data);
    }

    private void Add(List<Sample> track, double atMs, int dx, int dy)
    {
        if (dx == 0 && dy == 0 || !(atMs >= _startMs) || double.IsPositiveInfinity(atMs)) return;   // no motion, stale or not a time
        if (atMs - _firstMs > MousePatternRules.MaxDurationMs + EdgeMs)
        {
            _cut = true;   // beyond the limit: the helper is about to stop the recording
            return;
        }
        if (atMs < _firstMs) _firstMs = atMs;
        track.Add(new Sample(atMs, dx, dy));
    }

    /// <summary>Per-bin sums (index = bin) of one track, relative to the first movement. Events beyond the limit (possible when an earlier
    /// first movement turned up in the other track after they were added) are dropped and reported.</summary>
    private (long[] X, long[] Y) Sum(List<Sample> samples, out bool cut)
    {
        cut = false;
        int last = -1;
        foreach (var s in samples)
        {
            int bin = BinOf(s.AtMs);
            if (bin <= MousePatternRules.MaxDurationMs) last = Math.Max(last, bin);
        }

        var x = new long[last + 1];
        var y = new long[last + 1];
        foreach (var s in samples)
        {
            int bin = BinOf(s.AtMs);
            if (bin > MousePatternRules.MaxDurationMs)
            {
                cut = true;
                continue;
            }
            x[bin] += s.Dx;
            y[bin] += s.Dy;
        }
        return (x, y);
    }

    /// <summary>The bin of an event: the k with (k-1, k] holding its time after the first movement (0 for the first movement itself).</summary>
    private int BinOf(double atMs)
    {
        double ms = Math.Ceiling(atMs - _firstMs - EdgeMs);
        return ms > MousePatternRules.MaxDurationMs ? MousePatternRules.MaxDurationMs + 1 : (int)Math.Max(0, ms);
    }

    private static int FirstMoving((long[] X, long[] Y) sums)
    {
        for (int i = 0; i < sums.X.Length; i++)
            if (sums.X[i] != 0 || sums.Y[i] != 0) return i;
        return int.MaxValue;
    }

    /// <summary>The bins with motion, shifted by <paramref name="shift"/>; <paramref name="end"/> = the last bin's time (0 when empty).
    /// A bin summing beyond the int range (impossible for a real mouse in 1 ms) saturates.</summary>
    private static PatternTrack ToTrack((long[] X, long[] Y) sums, int shift, out int end)
    {
        end = 0;
        var at = new List<int>(); var dx = new List<int>(); var dy = new List<int>();
        for (int i = 0; i < sums.X.Length; i++)
        {
            if (sums.X[i] == 0 && sums.Y[i] == 0) continue;
            at.Add(i - shift);
            dx.Add(Saturate(sums.X[i]));
            dy.Add(Saturate(sums.Y[i]));
            end = i - shift;
        }
        return at.Count == 0 ? PatternTrack.Empty : new PatternTrack(at.ToArray(), dx.ToArray(), dy.ToArray());
    }

    private static int Saturate(long v) => (int)Math.Clamp(v, int.MinValue, int.MaxValue);
}
