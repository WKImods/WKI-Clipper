using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace WKI_Clipper.Services;

public sealed record DiskDecision(int EffectiveSeconds, bool Shortened, bool CannotStart, long RequiredBytes, long UsableBytes);

/// <summary>What a given lookback would cost on the buffer drive (settings hint, pre-start check).</summary>
public sealed record DiskForecast(int DurationSeconds, long RequiredBytes, long FreeBytes, string Drive,
                                  bool Known, DiskDecision Decision);

/// <summary>
/// Disk arithmetic for the replay ring. A 30-minute buffer is ~6.5 GB at the default quality and ~25 GB at
/// "very high" on an ultrawide, so the buffer must know what it costs before it starts - and shrink its
/// lookback instead of filling the drive. Pure, so every boundary is tested without a disk.
/// </summary>
public static class DiskPlan
{
    public const long HeadroomBytes = 1L << 30;      // must stay free once the ring is full (pre-start rule)
    public const long LowSpaceBytes = 512L << 20;    // runtime guard trigger
    public const long CriticalBytes = 128L << 20;    // stop the buffer
    public const double Overhead = 1.10;             // container + CBR overshoot
    public const int PruneLagSeconds = 30;           // watchdog interval
    public const int AudioBitsPerSec = 192_000, EstimatedAudioTracks = 2;

    /// <summary>ceil(seg x (video + tracks x audio) / 8 x overhead): what one segment may weigh.</summary>
    public static long EstimateSegmentBytes(int segmentSeconds, long videoBitsPerSec)
    {
        double bitsPerSec = Math.Max(0, videoBitsPerSec) + (double)EstimatedAudioTracks * AudioBitsPerSec;
        return (long)Math.Ceiling(Math.Max(1, segmentSeconds) * bitsPerSec / 8.0 * Overhead);
    }

    /// <summary>
    /// Bytes per second the recording really produced, from the newest complete segments (ascending by end time).
    /// Null with fewer than four entries or no time span. The first entry only anchors the span: its bytes were
    /// written BEFORE its end time.
    /// </summary>
    public static long? ObservedBytesPerSecond(IReadOnlyList<(long Bytes, DateTime EndUtc)> newestAscending)
    {
        if (newestAscending is null || newestAscending.Count < 4) return null;
        double span = (newestAscending[^1].EndUtc - newestAscending[0].EndUtc).TotalSeconds;
        if (span <= 0) return null;
        long bytes = 0;
        for (int i = 1; i < newestAscending.Count; i++) bytes += newestAscending[i].Bytes;
        return (long)(bytes / span);
    }

    /// <summary>The larger of the estimate and 105 % of what was observed - an underestimated bitrate corrects itself.</summary>
    public static long SegmentBytes(long estimate, long? observedBps, int segmentSeconds)
    {
        if (observedBps is not { } o) return estimate;
        long observed = (long)Math.Ceiling(o * (double)Math.Max(1, segmentSeconds) * 1.05);
        return Math.Max(estimate, observed);
    }

    /// <summary>Segments the ring can hold on disk: pick + reserve + open segment + tail + prune lag.</summary>
    public static int RingSegments(int durationSeconds, int segmentSeconds)
    {
        var plan = RingPlan.Create(durationSeconds, segmentSeconds);
        int seg = Math.Max(1, segmentSeconds);
        return plan.PickCount + RingPlan.Reserve + 3 + (int)Math.Ceiling((double)PruneLagSeconds / seg);
    }

    public static long RequiredBytes(int durationSeconds, int segmentSeconds, long segmentBytes)
        => (long)RingSegments(durationSeconds, segmentSeconds) * Math.Max(0, segmentBytes);

    /// <summary>
    /// The configured length when it fits (required + headroom does not exceed what is usable); otherwise the
    /// largest offered length below it that fits (<see cref="DiskDecision.Shortened"/>); none fits means the
    /// buffer cannot start (<see cref="DiskDecision.CannotStart"/>, EffectiveSeconds 0).
    /// </summary>
    public static DiskDecision Decide(int configuredSeconds, int segmentSeconds, long segmentBytes, long usableBytes)
    {
        long requiredConfigured = RequiredBytes(configuredSeconds, segmentSeconds, segmentBytes);
        if (requiredConfigured + HeadroomBytes <= usableBytes)
            return new DiskDecision(configuredSeconds, false, false, requiredConfigured, usableBytes);

        for (int i = DurationText.Choices.Length - 1; i >= 0; i--)
        {
            int candidate = DurationText.Choices[i];
            if (candidate >= configuredSeconds) continue;
            if (RequiredBytes(candidate, segmentSeconds, segmentBytes) + HeadroomBytes <= usableBytes)
                return new DiskDecision(candidate, true, false, requiredConfigured, usableBytes);
        }
        return new DiskDecision(0, false, true, requiredConfigured, usableBytes);
    }

    /// <summary>"6,5 GB" in the current culture; GB = 1024^3 (the Status page always counted like this).</summary>
    public static string FormatBytes(long bytes)
    {
        const double GB = 1024.0 * 1024 * 1024;
        const double TB = GB * 1024;
        var c = CultureInfo.CurrentCulture;
        if (bytes >= TB) return (bytes / TB).ToString("F2", c) + " TB";
        if (bytes >= GB) return (bytes / GB).ToString("F1", c) + " GB";
        return (bytes / (1024.0 * 1024)).ToString("F0", c) + " MB";
    }
}

/// <summary>Free space of the drive a folder lives on. Network (UNC) and unknown drives report "unknown".</summary>
public static class DiskProbe
{
    /// <summary>False for UNC paths, relative paths and drives that are not ready; never throws.</summary>
    public static bool TryGetFreeSpace(string folder, out long freeBytes, out string drive)
    {
        freeBytes = 0;
        drive = "";
        try
        {
            if (string.IsNullOrWhiteSpace(folder)) return false;
            string full = Path.GetFullPath(folder);
            string? root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            var info = new DriveInfo(root);
            if (!info.IsReady) return false;
            freeBytes = info.AvailableFreeSpace;
            drive = root.TrimEnd('\\');
            return true;
        }
        catch { return false; }
    }

    /// <summary>Total size of the buffer segments already in the folder: they get freed again once pruned.</summary>
    public static long OwnSegmentBytes(string dir)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "seg_*.mp4"))
            {
                if (!SegmentName.TryParseGeneration(Path.GetFileName(f), out _)) continue;
                try { total += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return total;
    }
}
