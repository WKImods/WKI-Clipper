using System;
using System.Collections.Generic;

namespace WKI_Clipper.Services;

/// <summary>
/// What a finished clip save tells the rest of the app. Replaces the bare path: the toast needs the real length and
/// the gaps, the gallery note needs the gaps, the game folder was decided at press time.
/// </summary>
/// <param name="Path">Final clip path.</param>
/// <param name="Seconds">Actual footage: the sum of the segment durations.</param>
/// <param name="RequestedSeconds">The clip length of this press (the game's own or the standard length, capped by the ring).</param>
/// <param name="Game">Folder/display name resolved at press time, null when unset.</param>
/// <param name="Gaps">Empty = gap-free.</param>
public sealed record ReplaySavedInfo(string Path, double Seconds, int RequestedSeconds, string? Game,
    IReadOnlyList<ClipGap> Gaps, long Bytes);

public enum SavePhase { Copying, Finalizing }

/// <summary>Progress of a running clip save (sampled from the growing output file once a second).</summary>
public sealed record SaveProgressInfo(SaveKind Kind, int ClipSeconds, double Fraction, SavePhase Phase, TimeSpan Elapsed);

/// <summary>Decisions around writing a long clip, kept pure (no ffmpeg, no clock) so every boundary is tested.</summary>
public static class ClipSavePolicy
{
    /// <summary>
    /// Above this size the +faststart pass is skipped. It rewrites the whole file a second time (6.5 GB at the default
    /// quality, ~25 GB at "very high"): tens of seconds to minutes of extra disk time with F8/F9 refused and prune
    /// blocked. What is lost - progressive playback before the download finished - is irrelevant for a local file,
    /// and a clip above 1 GiB is over every Discord limit anyway (the Discord copies are re-encoded with +faststart).
    /// </summary>
    public const long FaststartMaxBytes = 1L << 30;

    public static bool UseFaststart(long expectedBytes) => expectedBytes <= FaststartMaxBytes;

    /// <summary>
    /// Whether a failed save takes the "saving the clip ... 42 %" toast down. Only when the clip save itself ended
    /// without a clip: a second F8/F9 that is merely REFUSED (busy) leaves the first save running, and its progress
    /// toast has to stay.
    /// </summary>
    public static bool ClosesProgressToast(SaveFailure failure)
        => failure.Kind == SaveKind.Clip && failure.Reason != SaveFailureReason.Busy;

    /// <summary>Written / expected, clamped to [0, 0.99]: 100 % is only shown by the "saved" toast. Unknown expectation gives 0.</summary>
    public static double Fraction(long outputBytes, long expectedBytes)
    {
        if (expectedBytes <= 0 || outputBytes <= 0) return 0;
        return Math.Clamp((double)outputBytes / expectedBytes, 0, 0.99);
    }

    /// <summary>Finalizing once the copy reached <see cref="ConcatWatch.FinalizeFraction"/> of the expected size - only with +faststart.</summary>
    public static SavePhase Phase(long outputBytes, long expectedBytes, bool faststart)
        => faststart && expectedBytes > 0 && outputBytes >= expectedBytes * ConcatWatch.FinalizeFraction
            ? SavePhase.Finalizing
            : SavePhase.Copying;

    /// <summary>
    /// What a second F8/F9 says while a save runs. During a long clip save the old "one moment" is wrong (it can be
    /// a minute) and not helpful: name the clip, the progress, and what to do.
    /// </summary>
    public static string BusyMessage(SaveProgressInfo? current, SaveKind requested)
    {
        if (current is { Kind: SaveKind.Clip } c && c.ClipSeconds >= DurationText.LongClipSeconds)
        {
            int pct = (int)Math.Floor(c.Fraction * 100);
            string len = DurationText.Format(c.ClipSeconds);
            return requested == SaveKind.Gif
                ? L.T($"Speichere gerade den {len}-Clip ({pct} %) — GIF danach nochmal.",
                      $"Saving the {len} clip ({pct} %) — try the GIF again afterwards.")
                : L.T($"Speichere gerade den {len}-Clip ({pct} %) — Clip danach nochmal.",
                      $"Saving the {len} clip ({pct} %) — try the clip again afterwards.");
        }
        return L.T("Speichern läuft bereits — kurz warten.", "A save is already in progress — one moment.");
    }
}
