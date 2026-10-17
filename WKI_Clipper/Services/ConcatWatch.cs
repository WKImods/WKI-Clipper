using System;
using System.Collections.Generic;
using System.Linq;

namespace WKI_Clipper.Services;

public enum ConcatVerdict
{
    /// <summary>Still making progress - keep waiting.</summary>
    Progressing,
    /// <summary>The output stopped growing for longer than the stall limit.</summary>
    Stalled,
    /// <summary>Over the absolute safety cap, however alive it looks.</summary>
    TimedOut,
    /// <summary>
    /// Copying slower than the recording produces new footage: the segment muxer would overtake
    /// the reader and the clip would silently contain overwritten (spliced) segments.
    /// </summary>
    TooSlow,
}

/// <summary>
/// What a concat over already-recorded segments should produce: the byte total of the picked
/// segments, and how fast the recording fills the ring (picked bytes per second of footage).
/// A zero total means "unknown" and switches the size-aware rules off.
/// </summary>
public sealed record ConcatExpectation(long TotalBytes, double RecordingBytesPerSec)
{
    public static ConcatExpectation FromSegments(IReadOnlyCollection<long> segmentLengths, int segmentSeconds)
    {
        long total = segmentLengths.Where(l => l > 0).Sum();
        int count = segmentLengths.Count(l => l > 0);
        if (total <= 0 || count == 0) return new ConcatExpectation(0, 0);
        return new ConcatExpectation(total, total / ((double)count * Math.Max(1, segmentSeconds)));
    }
}

/// <summary>
/// Decides whether a running one-shot ffmpeg (clip concat, GIF encode) is still worth waiting
/// for. A flat timeout cannot tell a slow disk from a hung process: it killed healthy 3 GB
/// concats and then deleted their output. What proves a hang is output that stopped growing;
/// an absolute cap on top catches a process that crawls forever.
///
/// With a <see cref="ConcatExpectation"/> it also knows two things a bare "no growth" rule cannot:
/// - the +faststart pass shifts the whole mdat inside the finished file, so the length stays flat
///   for as long as that takes (minutes for several GB on an HDD) - once the output reached ~95 %
///   of the expected size it gets a size-dependent quiet period instead of 20 s;
/// - copying slower than the recording rate means the muxer overtakes the reader: abort rather
///   than hand out a clip spliced from overwritten segments.
///
/// Pure on purpose - the caller feeds in the time and the current output size, so every
/// boundary is unit-tested without an ffmpeg or a clock.
/// </summary>
public sealed class ConcatWatch
{
    /// <summary>No growth for longer than this means the process is hung.</summary>
    public static readonly TimeSpan DefaultStallLimit = TimeSpan.FromSeconds(20);

    /// <summary>How often the caller should sample the output size.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>Share of the expected size after which the copy phase counts as done.</summary>
    public const double FinalizeFraction = 0.95;

    /// <summary>Pessimistic in-place shift speed (HDD) used to size the faststart quiet period.</summary>
    public const long FinalizeBytesPerSec = 20L * 1024 * 1024;

    /// <summary>The too-slow rule only applies after this long (ffmpeg start-up, first reads).</summary>
    public static readonly TimeSpan TooSlowGrace = TimeSpan.FromSeconds(10);

    /// <summary>Output must reach this share of what the recording produced in the same time.</summary>
    public const double TooSlowFactor = 0.9;

    /// <summary>Absolute upper bound for a job producing <paramref name="clipSeconds"/> of footage.</summary>
    public static TimeSpan SafetyCap(int clipSeconds)
        => TimeSpan.FromSeconds(Math.Max(60L, 4L * Math.Max(0, clipSeconds)));

    /// <summary>How long a finished-copying output may stay flat while faststart rewrites it.</summary>
    public static TimeSpan FinalizeQuietPeriod(long expectedBytes)
    {
        var shift = TimeSpan.FromSeconds((double)Math.Max(0, expectedBytes) / FinalizeBytesPerSec);
        return shift > DefaultStallLimit ? shift : DefaultStallLimit;
    }

    private readonly DateTime _startUtc;
    private readonly TimeSpan _stallLimit;
    private readonly TimeSpan _cap;
    private readonly ConcatExpectation? _expected;
    private long _lastLength;
    private DateTime _lastGrowthUtc;

    /// <param name="stallLimit">
    /// Defaults to <see cref="DefaultStallLimit"/>. A phase that legitimately writes nothing for a
    /// while (palettegen buffers every frame before the first GIF byte) passes the cap instead.
    /// </param>
    /// <param name="expected">Size and recording rate of the picked segments; null for jobs without that notion.</param>
    public ConcatWatch(DateTime startUtc, int clipSeconds, TimeSpan? stallLimit = null,
        ConcatExpectation? expected = null)
    {
        _startUtc = startUtc;
        _lastGrowthUtc = startUtc;
        _cap = SafetyCap(clipSeconds);
        _stallLimit = stallLimit ?? DefaultStallLimit;
        _expected = expected is { TotalBytes: > 0 } ? expected : null;
    }

    /// <summary>Feed the current time and output size (0 while the file does not exist yet).</summary>
    public ConcatVerdict Observe(DateTime nowUtc, long outputLength)
    {
        if (outputLength > _lastLength)
        {
            _lastLength = outputLength;
            _lastGrowthUtc = nowUtc;
        }

        var elapsed = nowUtc - _startUtc;
        if (elapsed > _cap) return ConcatVerdict.TimedOut;

        var stallLimit = _stallLimit;
        if (_expected is { } e)
        {
            long copyDone = (long)(e.TotalBytes * FinalizeFraction);
            if (outputLength >= copyDone)
            {
                // Copy phase over; whatever follows (faststart) rewrites in place without growing.
                var quiet = FinalizeQuietPeriod(e.TotalBytes);
                if (quiet > stallLimit) stallLimit = quiet;
            }
            else if (e.RecordingBytesPerSec > 0 && elapsed > TooSlowGrace
                     && outputLength < elapsed.TotalSeconds * e.RecordingBytesPerSec * TooSlowFactor)
            {
                return ConcatVerdict.TooSlow;
            }
        }

        if (nowUtc - _lastGrowthUtc > stallLimit) return ConcatVerdict.Stalled;
        return ConcatVerdict.Progressing;
    }
}
