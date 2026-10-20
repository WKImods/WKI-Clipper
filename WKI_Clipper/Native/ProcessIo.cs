using System;
using System.Diagnostics;

namespace WKI_Clipper.Native;

/// <summary>
/// I/O accounting of another process. The stall watchdog of the ffmpeg job runner uses the bytes it has READ as a
/// sign of life: the +faststart rewrite at the end of an encode reads the whole file again while it prints no
/// progress at all, a hung driver or a vanished network input reads nothing. Writes are deliberately not counted -
/// ffmpeg prints its own progress lines every half second, even when it is stuck.
/// </summary>
internal static class ProcessIo
{
    /// <summary>Bytes the process has read so far; null when that cannot be asked (exited and released, no access).</summary>
    public static ulong? TryGetReadBytes(Process process)
    {
        try { return Kernel32.GetProcessIoCounters(process.SafeHandle, out var counters) ? counters.ReadTransferCount : null; }
        catch (Exception) { return null; }
    }
}
