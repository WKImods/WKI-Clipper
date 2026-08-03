using System;

namespace WKI_Clipper.Services;

/// <summary>One raw poll of OBS's stream output. Doubles instead of the library's
/// integer types so the math stays independent of obs-websocket-dotnet.</summary>
public readonly record struct StreamSample(
    long TimestampTicks,
    double Bytes,
    double TotalFrames,
    double SkippedFrames,
    double DurationMs);

/// <summary>
/// Derived stream health. Immutable reference type on purpose: it is published from
/// the poll thread and read by the UI thread, and a reference assignment is atomic
/// (a struct field would tear).
/// </summary>
public sealed record StreamHealthReading(
    TimeSpan Uptime,
    double Kbps,
    double RecentDropPercent,
    double TotalDropPercent,
    bool HasRate);

/// <summary>
/// Turns consecutive OBS output samples into the numbers a streamer actually reacts to.
///
/// The important part is <b>recent</b> drop percentage: OBS itself shows the cumulative
/// ratio, which hides a connection that started dropping five minutes into a three-hour
/// stream. Comparing two samples exposes the problem while it is happening.
/// </summary>
public static class StreamHealth
{
    /// <summary>Recent dropped frames from here on are worth a warning.</summary>
    public const double DropWarnPercent = 1.0;
    /// <summary>Recent dropped frames from here on mean the stream is visibly breaking up.</summary>
    public const double DropBadPercent = 5.0;

    public static StreamHealthReading Compute(StreamSample? prev, StreamSample cur)
    {
        var uptime = TimeSpan.FromMilliseconds(Math.Max(0, cur.DurationMs));
        double totalDrop = Percent(cur.SkippedFrames, cur.TotalFrames);

        if (prev is not { } p)
            return new StreamHealthReading(uptime, 0, 0, totalDrop, HasRate: false);

        double seconds = (cur.TimestampTicks - p.TimestampTicks) / (double)TimeSpan.TicksPerSecond;
        double frameDelta = cur.TotalFrames - p.TotalFrames;
        double skipDelta = cur.SkippedFrames - p.SkippedFrames;
        double byteDelta = cur.Bytes - p.Bytes;

        // A restarted output resets the counters — negative deltas mean the previous
        // sample belongs to a different stream, so rates are meaningless this round.
        if (seconds <= 0 || frameDelta < 0 || skipDelta < 0 || byteDelta < 0)
            return new StreamHealthReading(uptime, 0, 0, totalDrop, HasRate: false);

        double kbps = byteDelta * 8.0 / 1000.0 / seconds;
        return new StreamHealthReading(uptime, kbps, Percent(skipDelta, frameDelta), totalDrop, HasRate: true);
    }

    /// <summary>Traffic-light state for a reading, mirroring the two thresholds.</summary>
    public static CheckState Rate(StreamHealthReading? r)
    {
        if (r is null || !r.HasRate) return CheckState.Unknown;
        if (r.RecentDropPercent >= DropBadPercent) return CheckState.Fail;
        if (r.RecentDropPercent >= DropWarnPercent) return CheckState.Warn;
        return CheckState.Ok;
    }

    /// <summary>"2 h 14 m" / "14 m 07 s" — compact enough for a widget row.</summary>
    public static string FormatUptime(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} m" : $"{t.Minutes} m {t.Seconds:00} s";

    /// <summary>"6.2 Mbit/s" above a megabit, otherwise kilobits.</summary>
    public static string FormatBitrate(double kbps)
        => kbps >= 1000 ? $"{kbps / 1000.0:0.0} Mbit/s" : $"{kbps:0} kbit/s";

    private static double Percent(double part, double whole) => whole <= 0 ? 0 : part / whole * 100.0;
}
