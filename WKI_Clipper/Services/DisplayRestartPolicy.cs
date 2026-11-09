using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WKI_Clipper.Services;

/// <summary>
/// When an unexpected ffmpeg exit is a display mode change (R2): ddagrab ends on DXGI ACCESS_LOST and friends or on
/// "Output parameters changed!" when an exclusive-fullscreen game switches the mode. Such an exit - or any exit shortly
/// after a display change - is restarted automatically, at most <see cref="MaxRestarts"/> times per
/// <see cref="RestartWindow"/> (R8); after that the normal error report takes over.
/// </summary>
public static class DisplayRestartPolicy
{
    /// <summary>
    /// ddagrab's mode-change errors in stderr (matched case-insensitively): ACCESS_LOST, NOT_CURRENTLY_AVAILABLE,
    /// DEVICE_REMOVED - and E_ACCESSDENIED at the start while the secure desktop (lock screen, UAC) has the input.
    /// </summary>
    public static readonly string[] ModeChangeMarkers =
        { "Output parameters changed", "887a0026", "887a0022", "887a0005", "Desktop duplication access denied" };

    /// <summary>An exit this soon after a display change counts as caused by it.</summary>
    public static readonly TimeSpan DisplayChangeWindow = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan RestartWindow = TimeSpan.FromSeconds(60);

    public const int MaxRestarts = 3;

    /// <summary>How long the display gets to settle after a mode switch - after its LAST display event - before the capture starts again.</summary>
    public static readonly TimeSpan RestartDelay = TimeSpan.FromMilliseconds(1500);

    /// <summary>The longest an automatic restart waits on top of its delay for the display to calm down; a display that keeps changing gets its restart anyway.</summary>
    public static readonly TimeSpan MaxSettleWait = TimeSpan.FromSeconds(10);

    /// <summary>How many of the last stderr lines of an ended ffmpeg are searched for the markers.</summary>
    public const int StderrLines = 20;

    /// <param name="recentStderr">The last stderr lines of the ended ffmpeg.</param>
    /// <param name="lastDisplayChangeUtc">The last display event (null = none known).</param>
    /// <param name="autoRestartsUtc">When the earlier automatic restarts happened.</param>
    public static bool ShouldAutoRestart(string? recentStderr, DateTime? lastDisplayChangeUtc, DateTime nowUtc,
                                         IReadOnlyList<DateTime> autoRestartsUtc)
        => IsDisplayCaused(recentStderr, lastDisplayChangeUtc, nowUtc)
           && autoRestartsUtc.Count(t => nowUtc - t < RestartWindow) < MaxRestarts;

    /// <summary>The exit is explained by the display: a mode-change marker in stderr or a display change just before.</summary>
    public static bool IsDisplayCaused(string? recentStderr, DateTime? lastDisplayChangeUtc, DateTime nowUtc)
        => HasModeChangeMarker(recentStderr)
           || lastDisplayChangeUtc is DateTime change && nowUtc - change <= DisplayChangeWindow;

    public static bool HasModeChangeMarker(string? stderr)
        => !string.IsNullOrEmpty(stderr)
           && ModeChangeMarkers.Any(m => stderr.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>How long a buffer run whose video input died gets to end on "q" before it is killed (its segments are small).</summary>
    public static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The same for a Ctrl+F9 part of <paramref name="partBytes"/>: its "+faststart" trailer rewrites the whole file, gigabytes
    /// after a long recording, and a kill in the middle leaves a file without its index. Sized like a clip's faststart pass.
    /// </summary>
    public static TimeSpan RecordingQuitTimeout(long partBytes) => QuitTimeout + ConcatWatch.FinalizeQuietPeriod(partBytes);

    /// <summary>How often a restart waiting for the desktop looks again.</summary>
    public static readonly TimeSpan DesktopPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Waits until <paramref name="available"/> says the input desktop is back (<see cref="InputDesktop"/>): a capture started
    /// on the lock screen or under a UAC prompt fails at once. No limit - the lock screen lasts as long as the user is away -
    /// but <paramref name="token"/> ends it. <paramref name="onWaiting"/> is called once when it has to wait at all.
    /// Returns whether it waited.
    /// </summary>
    internal static async Task<bool> WaitForInputDesktopAsync(Func<bool> available, TimeSpan poll, Action? onWaiting,
                                                              CancellationToken token)
    {
        if (available()) return false;
        onWaiting?.Invoke();
        do await Task.Delay(poll, token).ConfigureAwait(false);
        while (!available());
        return true;
    }

    /// <summary>
    /// ffmpeg's line for the video input (always input 0) failing while ffmpeg runs - ddagrab on a mode switch mid-run:
    /// "[in#0/lavfi @ ...] Error during demuxing: ...". ffmpeg does NOT exit on it while the audio input still delivers;
    /// it records audio without picture until someone ends it. A failure while opening ("Error opening input") exits by itself.
    /// </summary>
    public static bool IsVideoInputLost(string? line)
        => !string.IsNullOrEmpty(line)
           && line.Contains("[in#0", StringComparison.Ordinal)
           && line.Contains("Error during demuxing", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How much longer a restart waits so that it comes <paramref name="restartDelay"/> after the LAST display event (R8): a
    /// flip during the wait pushes it back, so a burst of flips costs one restart, not one per flip. Null when the display
    /// has been quiet that long (or no event is known).
    /// </summary>
    public static TimeSpan? SettleWait(DateTime? lastDisplayChangeUtc, DateTime nowUtc, TimeSpan restartDelay)
        => lastDisplayChangeUtc is DateTime last && last + restartDelay > nowUtc ? last + restartDelay - nowUtc : null;

    /// <summary>
    /// Waits <paramref name="restartDelay"/>, then for as long as <see cref="SettleWait"/> says - at most
    /// <paramref name="maxWait"/> more.
    /// </summary>
    internal static async Task WaitForQuietDisplayAsync(Func<DateTime?>? lastDisplayChangeUtc, TimeSpan restartDelay,
                                                        TimeSpan maxWait, CancellationToken token)
    {
        await Task.Delay(restartDelay, token).ConfigureAwait(false);
        var until = DateTime.UtcNow + maxWait;
        while (true)
        {
            var now = DateTime.UtcNow;
            if (now >= until || SettleWait(lastDisplayChangeUtc?.Invoke(), now, restartDelay) is not TimeSpan wait) return;
            var rest = until - now;
            await Task.Delay(wait < rest ? wait : rest, token).ConfigureAwait(false);
        }
    }
}

/// <summary>What the buffer did about an ffmpeg exit it had not asked for.</summary>
internal enum UnexpectedExitAction
{
    /// <summary>The display explains it: one warm restart is scheduled, no error toast.</summary>
    AutoRestart,
    /// <summary>The buffer drive is full: that is reported.</summary>
    FullDisk,
    /// <summary>The "stopped unexpectedly" toast, as before.</summary>
    Report
}

/// <summary>The automatic restarts of one buffer, across all its runs (thread-safe).</summary>
internal sealed class AutoRestartLedger
{
    private readonly object _lock = new();
    private readonly List<DateTime> _restartsUtc = new();

    /// <summary>
    /// True - and counted - when <see cref="DisplayRestartPolicy.ShouldAutoRestart"/> allows a restart at
    /// <paramref name="nowUtc"/>; <paramref name="attempt"/> is then its number within the window (1 to
    /// <see cref="DisplayRestartPolicy.MaxRestarts"/>).
    /// </summary>
    public bool TryTake(string? recentStderr, DateTime? lastDisplayChangeUtc, DateTime nowUtc, out int attempt)
    {
        lock (_lock)
        {
            _restartsUtc.RemoveAll(t => nowUtc - t >= DisplayRestartPolicy.RestartWindow);
            attempt = 0;
            if (!DisplayRestartPolicy.ShouldAutoRestart(recentStderr, lastDisplayChangeUtc, nowUtc, _restartsUtc)) return false;
            _restartsUtc.Add(nowUtc);
            attempt = _restartsUtc.Count;
            return true;
        }
    }
}

/// <summary>The last lines one ffmpeg run wrote to stderr (thread-safe): why a run ended is in its final lines.</summary>
internal sealed class StderrTail
{
    private readonly int _capacity;
    private readonly Queue<string> _lines = new();

    public StderrTail(int capacity) => _capacity = Math.Max(1, capacity);

    public void Add(string line)
    {
        lock (_lines)
        {
            _lines.Enqueue(line);
            while (_lines.Count > _capacity) _lines.Dequeue();
        }
    }

    /// <summary>The kept lines, joined by '\n'.</summary>
    public string Text
    {
        get { lock (_lines) return string.Join("\n", _lines); }
    }
}
