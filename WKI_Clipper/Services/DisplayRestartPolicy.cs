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

    /// <param name="recentStderr">The last stderr lines of the ended ffmpeg.</param>
    /// <param name="lastDisplayChangeUtc">The last display event (null = none known).</param>
    /// <param name="autoRestartsUtc">When the earlier automatic restarts happened.</param>
    public static bool ShouldAutoRestart(string? recentStderr, DateTime? lastDisplayChangeUtc, DateTime nowUtc,
                                         IReadOnlyList<DateTime> autoRestartsUtc)
    {
        bool displayCaused = HasModeChangeMarker(recentStderr)
                             || lastDisplayChangeUtc is DateTime change && nowUtc - change <= DisplayChangeWindow;
        return displayCaused && autoRestartsUtc.Count(t => nowUtc - t < RestartWindow) < MaxRestarts;
    }

    public static bool HasModeChangeMarker(string? stderr)
        => !string.IsNullOrEmpty(stderr)
           && ModeChangeMarkers.Any(m => stderr.Contains(m, StringComparison.OrdinalIgnoreCase));
}
