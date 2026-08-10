using System;
using System.Collections.Generic;

namespace WKI_Clipper.Services;

/// <summary>How usable the ring buffer's material is right now.</summary>
public enum BufferMaterial
{
    /// <summary>Recent, contiguous segments — a clip will contain what just happened.</summary>
    Ok,
    /// <summary>The ring stopped turning. Whatever is on disk is old and must not be served as a clip.</summary>
    Stalled,
    /// <summary>Running, but nothing complete yet (just (re)started).</summary>
    NotEnoughYet,
}

/// <summary>
/// Judges whether the replay ring is still alive, from segment timestamps alone.
///
/// Why this is not obvious: a stalled ffmpeg does not exit. The process stays alive, the
/// open segment keeps growing (one was found at 46 MB after 15 hours), and IsRunning stays
/// true — so the app, the tray icon and the UI all report a healthy buffer. The only signal
/// that survives is the age of the newest COMPLETED segment: while the ring turns, a new one
/// appears every few seconds; once it stops, that age grows without bound.
/// Checking the newest file instead would prove nothing, since that is the open segment whose
/// timestamp keeps refreshing as it grows.
/// </summary>
public static class BufferHealth
{
    /// <summary>No completed segment for this long means the ring stopped turning.</summary>
    public static TimeSpan StaleAfter(int segmentSeconds)
        => TimeSpan.FromSeconds(Math.Max(20, Math.Max(1, segmentSeconds) * 4));

    /// <summary>Largest gap between consecutive segments that still counts as one take.</summary>
    public static TimeSpan MaxGap(int segmentSeconds)
        => TimeSpan.FromSeconds(Math.Max(15, Math.Max(1, segmentSeconds) * 3));

    /// <summary>
    /// Index at which the newest uninterrupted run of segments starts. Everything before it
    /// belongs to an older era (a stall, a sleeping machine) and must not be spliced into a
    /// clip labelled "the last N seconds".
    /// </summary>
    public static int ContiguousTailStart(IReadOnlyList<DateTime> ascendingUtc, int segmentSeconds)
    {
        if (ascendingUtc is null || ascendingUtc.Count == 0) return 0;
        var maxGap = MaxGap(segmentSeconds);
        for (int i = ascendingUtc.Count - 1; i > 0; i--)
            if (ascendingUtc[i] - ascendingUtc[i - 1] > maxGap) return i;
        return 0;
    }

    /// <summary>Verdict from the age of the newest completed segment.</summary>
    public static BufferMaterial Judge(DateTime newestCompletedUtc, DateTime nowUtc, int segmentSeconds)
        => nowUtc - newestCompletedUtc > StaleAfter(segmentSeconds)
            ? BufferMaterial.Stalled
            : BufferMaterial.Ok;
}
