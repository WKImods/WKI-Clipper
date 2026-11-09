using System;
using System.Collections.Generic;
using System.Linq;

namespace WKI_Clipper.Services;

/// <summary>
/// When an unexpected ffmpeg exit is a display mode change (R2): ddagrab ends on DXGI ACCESS_LOST and friends or on
/// "Output parameters changed!" when an exclusive-fullscreen game switches the mode. Such an exit - or any exit shortly
/// after a display change - is restarted automatically, at most <see cref="MaxRestarts"/> times per
/// <see cref="RestartWindow"/> (R8); after that the normal error report takes over.
/// </summary>
public static class DisplayRestartPolicy
{
    /// <summary>ddagrab's mode-change errors in stderr (matched case-insensitively): ACCESS_LOST, NOT_CURRENTLY_AVAILABLE, DEVICE_REMOVED.</summary>
    public static readonly string[] ModeChangeMarkers = { "Output parameters changed", "887a0026", "887a0022", "887a0005" };

    /// <summary>An exit this soon after a display change counts as caused by it.</summary>
    public static readonly TimeSpan DisplayChangeWindow = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan RestartWindow = TimeSpan.FromSeconds(60);

    public const int MaxRestarts = 3;

    /// <summary>How long the display gets to settle after a mode switch before the capture starts again.</summary>
    public static readonly TimeSpan RestartDelay = TimeSpan.FromMilliseconds(1500);

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
