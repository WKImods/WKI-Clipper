using System;

namespace WKI_Clipper.Services;

public enum ConcatVerdict
{
    /// <summary>Still making progress - keep waiting.</summary>
    Progressing,
    /// <summary>The output stopped growing for longer than the stall limit.</summary>
    Stalled,
    /// <summary>Over the absolute safety cap, however alive it looks.</summary>
    TimedOut,
}

/// <summary>
/// Decides whether a running one-shot ffmpeg (clip concat, GIF encode) is still worth waiting
/// for. A flat timeout cannot tell a slow disk from a hung process: it killed healthy 3 GB
/// concats and then deleted their output. What proves a hang is output that stopped growing;
/// an absolute cap on top catches a process that crawls forever.
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

    /// <summary>Absolute upper bound for a job producing <paramref name="clipSeconds"/> of footage.</summary>
    public static TimeSpan SafetyCap(int clipSeconds)
        => TimeSpan.FromSeconds(Math.Max(60L, 4L * Math.Max(0, clipSeconds)));

    private readonly DateTime _startUtc;
    private readonly TimeSpan _stallLimit;
    private readonly TimeSpan _cap;
    private long _lastLength;
    private DateTime _lastGrowthUtc;

    /// <param name="stallLimit">
    /// Defaults to <see cref="DefaultStallLimit"/>. A phase that legitimately writes nothing for a
    /// while (palettegen buffers every frame before the first GIF byte) passes the cap instead.
    /// </param>
    public ConcatWatch(DateTime startUtc, int clipSeconds, TimeSpan? stallLimit = null)
    {
        _startUtc = startUtc;
        _lastGrowthUtc = startUtc;
        _cap = SafetyCap(clipSeconds);
        _stallLimit = stallLimit ?? DefaultStallLimit;
    }

    /// <summary>Feed the current time and output size (0 while the file does not exist yet).</summary>
    public ConcatVerdict Observe(DateTime nowUtc, long outputLength)
    {
        if (outputLength > _lastLength)
        {
            _lastLength = outputLength;
            _lastGrowthUtc = nowUtc;
        }

        if (nowUtc - _startUtc > _cap) return ConcatVerdict.TimedOut;
        if (nowUtc - _lastGrowthUtc > _stallLimit) return ConcatVerdict.Stalled;
        return ConcatVerdict.Progressing;
    }
}
