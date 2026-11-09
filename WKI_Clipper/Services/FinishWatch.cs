using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace WKI_Clipper.Services;

/// <summary>
/// How long an ffmpeg that was told to end ("q") may take before it is killed - judged by its signs of life, not by the
/// clock. A recording part's "+faststart" trailer rewrites the whole file: gigabytes after a long recording, many seconds to
/// minutes on a slow drive, without a single progress line - and a kill in the middle leaves a file without its index. It
/// reads all the time while it does that (<see cref="WKI_Clipper.Native.ProcessIo"/>); a hung ffmpeg reads nothing.
/// </summary>
public static class FinishWatch
{
    /// <summary>A known read counter that stands still this long means ffmpeg hangs.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The absolute limit for finishing a part of <paramref name="bytes"/>: two full rewrites at a slow 20 MiB/s plus a
    /// minute. It also is the only limit when the read counter cannot be asked.
    /// </summary>
    public static TimeSpan Cap(long bytes) => TimeSpan.FromMinutes(1) + ConcatWatch.FinalizeQuietPeriod(bytes) * 2;

    /// <summary>
    /// Waits for <paramref name="hasExited"/>. True when it came; false (kill it) once a known read counter stood still for
    /// <paramref name="quiet"/>, or at <paramref name="cap"/>. A counter that answers null is unknown and never counts as quiet.
    /// </summary>
    public static async Task<bool> WaitAsync(Func<bool> hasExited, Func<ulong?> readBytes, TimeSpan quiet, TimeSpan cap, TimeSpan poll)
    {
        var total = Stopwatch.StartNew();
        var sinceRead = Stopwatch.StartNew();
        ulong? last = readBytes();
        while (!hasExited())
        {
            if (total.Elapsed >= cap || last is not null && sinceRead.Elapsed >= quiet) return hasExited();
            await Task.Delay(poll).ConfigureAwait(false);
            if (readBytes() is { } now && now != last)
            {
                last = now;
                sinceRead.Restart();
            }
        }
        return true;
    }
}
