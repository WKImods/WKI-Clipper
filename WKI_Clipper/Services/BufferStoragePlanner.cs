using System;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using WKI_Clipper.Native;

namespace WKI_Clipper.Services;

/// <summary>Where the replay ring lives (settings: ReplayBuffer.StorageMode).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BufferStorageMode
{
    /// <summary>RAM when the lookback fits the RAM budget and the machine has the room, otherwise the disk ring.</summary>
    Auto,
    /// <summary>Always RAM; a lookback that does not fit is shortened.</summary>
    Memory,
    /// <summary>Always the hidden disk ring (seg_*.mp4).</summary>
    Disk
}

/// <summary>What a run of the buffer actually uses.</summary>
public enum BufferStorageKind { Disk, Memory }

public enum StorageReason
{
    /// <summary>Auto: the ring fits the RAM budget and the free RAM.</summary>
    FitsMemory,
    /// <summary>Memory chosen by the user.</summary>
    MemoryChosen,
    /// <summary>Disk chosen by the user.</summary>
    DiskChosen,
    /// <summary>Auto: the ring is larger than the RAM budget (long lookback / high bitrate).</summary>
    OverBudget,
    /// <summary>Not enough free RAM right now.</summary>
    LowMemory,
    /// <summary>The codec cannot go through MPEG-TS (AV1).</summary>
    CodecUnsupported,
    /// <summary>Auto: the low-RAM guard had to shorten the RAM lookback recently, so cold starts use the disk for a while.</summary>
    RecentLowMemory
}

/// <param name="LookbackSeconds">The lookback this store holds (shorter than configured when <paramref name="Shortened"/>).</param>
/// <param name="BudgetBytes">Hard RAM limit for a memory ring (0 for disk).</param>
public sealed record StorageDecision(BufferStorageKind Kind, StorageReason Reason, long EstimatedBytes, long BudgetBytes,
    int LookbackSeconds, bool Shortened);

/// <summary>Physical memory right now (GlobalMemoryStatusEx).</summary>
public readonly record struct RamStatus(long TotalBytes, long AvailableBytes);

/// <summary>
/// The RAM/disk decision for the replay ring, pure so every boundary is tested: up to about five minutes at the usual
/// bitrates the ring fits a 2 GB RAM budget and never touches the disk (no SSD wear, no files in Explorer); a longer
/// lookback - up to 30 minutes, gigabytes - goes to the hidden disk ring.
/// </summary>
public static class BufferStoragePlanner
{
    public const int MinMemoryMB = 256;
    public const int MaxMemoryMB = 16384;
    public const int DefaultMemoryCapMB = 2048;
    public const double DefaultMemoryShare = 0.15;
    /// <summary>Free RAM below max(1.5 GiB, 10 % of the machine) is the game's, not the buffer's.</summary>
    public const long SafetyFloorMinBytes = 1536L << 20;
    public const double SafetyFloorShare = 0.10;

    /// <summary>
    /// Auto decides at app start, usually before the game runs, so the free RAM it sees is not what it will get. It only
    /// picks RAM on a 16-GB-class machine (which reports ~15.x GiB usable, hence 15 GiB) or when the free RAM above the
    /// floor holds the ring AND a game's working set on top. 4 GiB: what current titles keep resident (Arma Reforger,
    /// typical shooters 3-6 GB); an 8 GB machine therefore stays on the disk ring, as before.
    /// </summary>
    public const long AutoMinTotalBytes = 15L << 30;
    public const long GameReserveBytes = 4L << 30;

    /// <summary>After the guard had to shorten the RAM ring, Auto uses the disk at cold starts for this long.</summary>
    public static readonly TimeSpan RecentLowMemoryWindow = TimeSpan.FromDays(7);

    /// <summary>The RAM budget in MB: the setting (clamped 256..16384), or 0 = min(2048, 15 % of the physical RAM).</summary>
    public static int EffectiveMaxMemoryMB(int configuredMB, long totalPhysicalBytes)
    {
        if (configuredMB > 0) return Math.Clamp(configuredMB, MinMemoryMB, MaxMemoryMB);
        if (totalPhysicalBytes <= 0) return DefaultMemoryCapMB;
        long share = (long)(totalPhysicalBytes / (1024.0 * 1024) * DefaultMemoryShare);
        return (int)Math.Clamp(Math.Min(DefaultMemoryCapMB, share), MinMemoryMB, MaxMemoryMB);
    }

    public static long SafetyFloorBytes(long totalPhysicalBytes)
        => Math.Max(SafetyFloorMinBytes, (long)(Math.Max(0, totalPhysicalBytes) * SafetyFloorShare));

    /// <summary>Bytes per second of the recording: video + audio tracks, with the container/CBR overhead of the disk plan.</summary>
    public static double BytesPerSecond(long videoBitsPerSecond, int audioTracks)
        => (Math.Max(0, videoBitsPerSecond) + (double)Math.Max(0, audioTracks) * DiskPlan.AudioBitsPerSec) / 8.0 * DiskPlan.Overhead;

    /// <summary>
    /// What a RAM ring of <paramref name="lookbackSeconds"/> holds at most: the age window the prune keeps (pick +
    /// reserve + slack, <see cref="SegmentRetention.AgeWindowSeconds"/>) plus the open segment, plus one partly used
    /// block per segment.
    /// </summary>
    public static long EstimateRingBytes(int lookbackSeconds, int segmentSeconds, long videoBitsPerSecond, int audioTracks,
        int blockSize = MemoryBlockPool.DefaultBlockSize)
    {
        int seg = Math.Max(1, segmentSeconds);
        var ring = RingPlan.Create(lookbackSeconds, seg);
        long seconds = SegmentRetention.AgeWindowSeconds(ring, ring) + seg;
        long segments = (seconds + seg - 1) / seg;
        return (long)Math.Ceiling(seconds * BytesPerSecond(videoBitsPerSecond, audioTracks)) + segments * blockSize;
    }

    /// <summary>
    /// Disk for an unsupported codec or when chosen; RAM when chosen (shortened to the longest offered length that fits the
    /// budget and the free RAM above the safety floor - disk when not even the shortest fits); Auto: RAM only when the
    /// whole lookback fits both, the machine has room for a game besides (<see cref="AutoMinTotalBytes"/>,
    /// <see cref="GameReserveBytes"/>) and the guard did not have to shorten a RAM ring recently
    /// (<paramref name="recentLowMemory"/>); otherwise disk. A null <paramref name="ram"/> (unknown) only applies the budget.
    /// <paramref name="ram"/> must count the buffer's own ring as available (it is ours to give back).
    /// </summary>
    public static StorageDecision Decide(BufferStorageMode mode, bool codecSupportsMemory, int lookbackSeconds,
        int segmentSeconds, long videoBitsPerSecond, int audioTracks, long budgetBytes, RamStatus? ram,
        bool recentLowMemory = false)
    {
        int lookback = DurationText.Clamp(lookbackSeconds);
        long estimate = EstimateRingBytes(lookback, segmentSeconds, videoBitsPerSecond, audioTracks);
        if (!codecSupportsMemory) return Disk(StorageReason.CodecUnsupported);
        if (mode == BufferStorageMode.Disk) return Disk(StorageReason.DiskChosen);

        long room = ram is { } r ? Math.Max(0, r.AvailableBytes - SafetyFloorBytes(r.TotalBytes)) : long.MaxValue;
        long limit = Math.Min(Math.Max(0, budgetBytes), room);

        if (mode == BufferStorageMode.Auto)
        {
            if (estimate > budgetBytes) return Disk(StorageReason.OverBudget);
            if (recentLowMemory) return Disk(StorageReason.RecentLowMemory);
            bool roomForGame = ram is not { } m || m.TotalBytes >= AutoMinTotalBytes || room >= estimate + GameReserveBytes;
            if (estimate > limit || !roomForGame) return Disk(StorageReason.LowMemory);
            return new StorageDecision(BufferStorageKind.Memory, StorageReason.FitsMemory, estimate, limit, lookback, false);
        }

        if (estimate <= limit)
            return new StorageDecision(BufferStorageKind.Memory, StorageReason.MemoryChosen, estimate, limit, lookback, false);

        for (int i = DurationText.Choices.Length - 1; i >= 0; i--)
        {
            int candidate = DurationText.Choices[i];
            if (candidate >= lookback) continue;
            long need = EstimateRingBytes(candidate, segmentSeconds, videoBitsPerSecond, audioTracks);
            if (need <= limit)
                return new StorageDecision(BufferStorageKind.Memory,
                    room < budgetBytes ? StorageReason.LowMemory : StorageReason.MemoryChosen, need, limit, candidate, true);
        }
        return Disk(StorageReason.LowMemory);

        StorageDecision Disk(StorageReason reason) => new(BufferStorageKind.Disk, reason, estimate, 0, lookback, false);
    }

    /// <summary>
    /// Runtime low-RAM guard: with free RAM below the floor the ring gives back the difference (oldest footage first),
    /// never going below <paramref name="minimumBytes"/>. Null = nothing to do.
    /// </summary>
    public static long? ShrinkBudget(long budgetBytes, long inUseBytes, RamStatus ram, long minimumBytes)
    {
        long floor = SafetyFloorBytes(ram.TotalBytes);
        if (ram.AvailableBytes >= floor) return null;
        long target = Math.Max(minimumBytes, Math.Min(budgetBytes, inUseBytes) - (floor - ram.AvailableBytes));
        return target < Math.Min(budgetBytes, inUseBytes) ? target : null;
    }

    /// <summary>
    /// The longest offered lookback Auto would keep in RAM with these settings and this RAM state; 0 = none (disk only).
    /// The settings page shows it instead of a fixed "about 5 minutes".
    /// </summary>
    public static int LongestAutoMemoryLookback(bool codecSupportsMemory, int segmentSeconds, long videoBitsPerSecond,
        int audioTracks, long budgetBytes, RamStatus? ram, bool recentLowMemory = false)
    {
        for (int i = DurationText.Choices.Length - 1; i >= 0; i--)
        {
            var d = Decide(BufferStorageMode.Auto, codecSupportsMemory, DurationText.Choices[i], segmentSeconds,
                videoBitsPerSecond, audioTracks, budgetBytes, ram, recentLowMemory);
            if (d.Kind == BufferStorageKind.Memory) return DurationText.Choices[i];
        }
        return 0;
    }

    /// <summary>The RAM state with the buffer's own ring counted as available (it would be given back first).</summary>
    public static RamStatus? WithOwnRing(RamStatus? ram, long ownRingBytes)
        => ram is { } r ? r with { AvailableBytes = r.AvailableBytes + Math.Max(0, ownRingBytes) } : null;

    /// <summary>The longest offered lookback (at most <paramref name="configuredSeconds"/>) whose ring fits <paramref name="bytes"/>.</summary>
    public static int LookbackForBytes(long bytes, int configuredSeconds, int segmentSeconds, long videoBitsPerSecond, int audioTracks)
    {
        int configured = DurationText.Clamp(configuredSeconds);
        if (EstimateRingBytes(configured, segmentSeconds, videoBitsPerSecond, audioTracks) <= bytes) return configured;
        for (int i = DurationText.Choices.Length - 1; i >= 0; i--)
        {
            int c = DurationText.Choices[i];
            if (c < configured && EstimateRingBytes(c, segmentSeconds, videoBitsPerSecond, audioTracks) <= bytes) return c;
        }
        return DurationText.MinSeconds;
    }

    /// <summary>"~850 MB" / "~6,5 GB".</summary>
    public static string FormatApprox(long bytes) => "~" + DiskPlan.FormatBytes(bytes);

    /// <summary>The storage line of the status pages.</summary>
    public static string StatusLine(BufferStorageKind kind, long bytes)
        => kind == BufferStorageKind.Memory
            ? L.T($"Zwischenspeicher: Arbeitsspeicher, belegt {FormatApprox(bytes)}",
                  $"Buffer storage: RAM, using {FormatApprox(bytes)}")
            : L.T($"Zwischenspeicher: Festplatte (versteckt), fest {FormatApprox(bytes)}, wird überschrieben",
                  $"Buffer storage: disk (hidden), fixed {FormatApprox(bytes)}, overwritten continuously");
}

/// <summary>GlobalMemoryStatusEx; null when it fails.</summary>
public static class PhysicalMemory
{
    public static RamStatus? Read()
    {
        try
        {
            var m = new Kernel32.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Kernel32.MEMORYSTATUSEX>() };
            if (!Kernel32.GlobalMemoryStatusEx(ref m)) return null;
            return new RamStatus((long)Math.Min(m.ullTotalPhys, long.MaxValue), (long)Math.Min(m.ullAvailPhys, long.MaxValue));
        }
        catch { return null; }
    }
}
