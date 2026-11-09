using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace WKI_Clipper.Services;

/// <summary>
/// How long an ffmpeg that was told to end ("q") may take before it is killed - judged by its signs of life, not by the
/// clock. A recording part's "+faststart" trailer rewrites the whole file: gigabytes after a long recording, many seconds to
/// minutes on a slow drive, without a single progress line - and a kill in the middle leaves a file without its index. It
/// reads all the time while it does that (<see cref="WKI_Clipper.Native.ProcessIo"/>), in blocks the size of its index; a
/// hung ffmpeg reads nothing.
/// </summary>
public static class FinishWatch
{
    /// <summary>
    /// A known read counter that stands still this long means ffmpeg hangs. Generous: on a very slow drive (an SD card, a NAS
    /// over Wi-Fi) one block of the rewrite can take many seconds.
    /// </summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    /// <summary>The cap never outlasts this many times itself, however busy ffmpeg looks.</summary>
    public const int HardCapFactor = 4;

    /// <summary>
    /// The time a part of <paramref name="bytes"/> gets: two full rewrites at a slow 20 MiB/s plus a minute. While ffmpeg
    /// visibly reads it may go on past it within <see cref="ReadBudget"/>; without a known counter it is the limit.
    /// </summary>
    public static TimeSpan Cap(long bytes) => TimeSpan.FromMinutes(1) + ConcatWatch.FinalizeQuietPeriod(bytes) * 2;

    /// <summary>What finishing a part may read: the faststart rewrite reads the file once - twice that plus 64 MiB is plenty.</summary>
    public static ulong ReadBudget(long bytes) => 2UL * (ulong)Math.Max(0, bytes) + 64UL * 1024 * 1024;

    /// <summary>
    /// Waits for <paramref name="hasExited"/>. True when it came; false (kill it) once a known read counter stood still for
    /// <paramref name="quiet"/>, once <paramref name="cap"/> is over and ffmpeg read <paramref name="readBudget"/> since the
    /// start (or its counter is unknown), and at <see cref="HardCapFactor"/> times the cap in any case. Time counts only while
    /// the PC is awake (<paramref name="clock"/>, default: the unbiased interrupt time) - a sleep in the middle of a long
    /// rewrite is no sign of a hang.
    /// </summary>
    public static async Task<bool> WaitAsync(Func<bool> hasExited, Func<ulong?> readBytes, TimeSpan quiet, TimeSpan cap,
                                             ulong readBudget, TimeSpan poll, Func<TimeSpan>? clock = null)
    {
        clock ??= AwakeTime;
        var start = clock();
        var lastReadAt = start;
        ulong? first = readBytes(), last = first;
        while (!hasExited())
        {
            var elapsed = clock() - start;
            bool overBudget = first is null || last is null || last.Value - first.Value >= readBudget;
            if (elapsed >= cap * HardCapFactor
                || elapsed >= cap && overBudget
                || last is not null && clock() - lastReadAt >= quiet)
                return hasExited();
            await Task.Delay(poll).ConfigureAwait(false);
            if (readBytes() is { } now && now != last)
            {
                first ??= now;
                last = now;
                lastReadAt = clock();
            }
        }
        return true;
    }

    /// <summary>Time since boot without the time spent asleep or hibernated (unlike Stopwatch, which counts it).</summary>
    public static TimeSpan AwakeTime()
        => QueryUnbiasedInterruptTime(out ulong ticks) ? TimeSpan.FromTicks((long)ticks) : TimeSpan.FromMilliseconds(Environment.TickCount64);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);
}
