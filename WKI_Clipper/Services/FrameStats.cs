using System;

namespace WKI_Clipper.Services;

/// <summary>
/// Frame metrics of one present stream. Null = not enough data for an honest value.
/// All rates are in FPS, times in milliseconds.
/// </summary>
public readonly record struct FrameMetrics(
    double? Fps,
    double? AvgFrameTimeMs,
    double? Low1,
    double? Low01,
    double? StutterPercent);

/// <summary>
/// Pure frame-time statistics over a rolling window of present timestamps (QPC ticks).
/// No clock, no ETW — fed by <see cref="PresentTracker"/>, fully unit-tested.
///
/// Definitions (deliberate, documented because tools disagree on them):
/// <list type="bullet">
/// <item><b>Frame time</b> = distance between two consecutive presents of the stream (PresentMon's
///   "MsBetweenPresents").</item>
/// <item><b>FPS / average frame time</b>: over the newest frames covering at most 1 s
///   (always at least one frame); FPS = frames / covered time. Reported once ≥ 0.5 s is covered.</item>
/// <item><b>1 % / 0.1 % low</b> — TIME-based integral definition over the rolling
///   <see cref="LowsWindowSeconds"/> window: sort frame times descending, accumulate them until
///   1 % (0.1 %) of the window's total duration is covered; the low is the FPS of the frame that
///   crosses that line. It answers "how slow were the frames the player spent the worst 1 % of the
///   time in" — unlike count-based percentiles, a few very long hitches weigh by their duration.
///   Requires ≥ <see cref="MinFramesForLows"/> frames.</item>
/// <item><b>Micro-stutter %</b>: share of frames in the same window whose frame time is greater
///   than <see cref="StutterFactor"/> × the window's median frame time.</item>
/// <item>A gap over <see cref="GapResetSeconds"/> (alt-tab, menu that stops rendering) starts
///   over: the gap is not a frame, and old frames would only distort the lows.</item>
/// </list>
/// Not thread-safe; the owner serializes access.
/// </summary>
public sealed class FrameStats
{
    public const double FpsWindowSeconds = 1.0;
    public const double MinFpsSpanSeconds = 0.5;
    public const double LowsWindowSeconds = 20.0;
    public const int MinFramesForLows = 100;
    public const double GapResetSeconds = 2.0;
    public const double StutterFactor = 2.0;
    /// <summary>Hard cap on remembered frames (≈ 13 s at 5000 fps) — keeps memory and the per-second sort bounded.</summary>
    public const int MaxFrames = 65_536;

    private readonly double _ticksPerMs;
    private readonly long _lowsWindowTicks;
    private readonly long _gapTicks;

    // Ring buffer of frames, oldest at _head. End = present timestamp that closed the frame.
    private readonly long[] _end = new long[MaxFrames];
    private readonly double[] _ms = new double[MaxFrames];
    private int _head, _count;

    private bool _hasLast;
    private long _last;

    // Reused by Compute so the 1 Hz evaluation doesn't produce large-object garbage.
    private double[] _sortScratch = new double[1024];

    public FrameStats(long ticksPerSecond)
    {
        if (ticksPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));
        _ticksPerMs = ticksPerSecond / 1000.0;
        _lowsWindowTicks = (long)(LowsWindowSeconds * ticksPerSecond);
        _gapTicks = (long)(GapResetSeconds * ticksPerSecond);
    }

    /// <summary>Frames currently in the rolling window.</summary>
    public int FrameCount => _count;

    /// <summary>Time covered by the frames in the window, in seconds.</summary>
    public double WindowSeconds
    {
        get
        {
            double sum = 0;
            for (int i = 0; i < _count; i++) sum += _ms[(_head + i) % MaxFrames];
            return sum / 1000.0;
        }
    }

    /// <summary>Forget everything, including the baseline present.</summary>
    public void Reset()
    {
        _head = 0;
        _count = 0;
        _hasLast = false;
        _last = 0;
    }

    /// <summary>One present of the stream at QPC <paramref name="timestamp"/>.</summary>
    public void Add(long timestamp)
    {
        if (!_hasLast)
        {
            _last = timestamp;
            _hasLast = true;
            return;
        }

        long delta = timestamp - _last;
        // Late (another CPU's buffer) or duplicate event: not a frame, and it must not
        // become the new baseline either.
        if (delta <= 0) return;
        _last = timestamp;

        if (delta > _gapTicks)
        {
            // Keep this present as the baseline of the new run.
            _head = 0;
            _count = 0;
            return;
        }

        if (_count == MaxFrames)
        {
            _head = (_head + 1) % MaxFrames;
            _count--;
        }
        int slot = (_head + _count) % MaxFrames;
        _end[slot] = timestamp;
        _ms[slot] = delta / _ticksPerMs;
        _count++;

        long cutoff = timestamp - _lowsWindowTicks;
        while (_count > 0 && _end[_head] <= cutoff)
        {
            _head = (_head + 1) % MaxFrames;
            _count--;
        }
    }

    public FrameMetrics Compute()
    {
        if (_count == 0) return default;

        // --- average over the newest ≤ 1 s ---
        double fpsWindowMs = FpsWindowSeconds * 1000;
        double span = 0;
        int n = 0;
        for (int i = _count - 1; i >= 0; i--)
        {
            double ft = _ms[(_head + i) % MaxFrames];
            if (n > 0 && span + ft > fpsWindowMs) break;
            span += ft;
            n++;
        }
        double? fps = null, avg = null;
        if (span >= MinFpsSpanSeconds * 1000 && span > 0)
        {
            fps = n * 1000.0 / span;
            avg = span / n;
        }

        if (_count < MinFramesForLows)
            return new FrameMetrics(fps, avg, null, null, null);

        // --- lows + stutter over the rolling window ---
        if (_sortScratch.Length < _count)
            _sortScratch = new double[Math.Max(_count, _sortScratch.Length * 2)];
        double total = 0;
        for (int i = 0; i < _count; i++)
        {
            double ft = _ms[(_head + i) % MaxFrames];
            _sortScratch[i] = ft;
            total += ft;
        }
        Array.Sort(_sortScratch, 0, _count);   // ascending; the worst frames are at the end

        double low1 = TimeBasedLow(_sortScratch, _count, total, 0.01);
        double low01 = TimeBasedLow(_sortScratch, _count, total, 0.001);

        double median = _count % 2 == 1
            ? _sortScratch[_count / 2]
            : (_sortScratch[_count / 2 - 1] + _sortScratch[_count / 2]) / 2;
        double limit = StutterFactor * median;
        int stutter = 0;
        for (int i = _count - 1; i >= 0 && _sortScratch[i] > limit; i--) stutter++;

        return new FrameMetrics(fps, avg, low1, low01, stutter * 100.0 / _count);
    }

    /// <summary>
    /// Walks the ascending-sorted frame times from the slowest down, accumulating until
    /// <paramref name="share"/> of <paramref name="totalMs"/> is covered; FPS of that frame.
    /// </summary>
    private static double TimeBasedLow(double[] ascending, int count, double totalMs, double share)
    {
        double threshold = totalMs * share;
        double acc = 0;
        for (int i = count - 1; i >= 0; i--)
        {
            acc += ascending[i];
            if (acc >= threshold) return 1000.0 / ascending[i];
        }
        return 1000.0 / ascending[0];   // unreachable for share ≤ 1, kept for safety
    }
}
