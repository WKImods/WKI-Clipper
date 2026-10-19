using System;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>Where the length of a press came from.</summary>
public enum ClipLengthSource
{
    /// <summary>The standard length (no game, an unknown game, or a game without a length of its own).</summary>
    Global,
    /// <summary>The detected game's own F9 length.</summary>
    Profile
}

/// <summary>
/// The clip length of ONE F9 press. <paramref name="Seconds"/> is what is saved (never more than the ring holds),
/// <paramref name="WantedSeconds"/> what the profile or the standard asked for before the cap.
/// </summary>
public readonly record struct PressClip(int Seconds, ClipLengthSource Source, string? ProfileKey, int WantedSeconds)
{
    /// <summary>The ring (RAM/disk caps) holds less than was asked for.</summary>
    public bool CappedByRing => WantedSeconds > Seconds;
}

/// <summary>
/// Two lengths that must never be mixed up: the RING length (the longest of the standard and every profile - see
/// <see cref="EffectiveSettings.LongestClipSeconds(AppSettings)"/>, never resized when the game changes) and the clip
/// length of one press (this class: the game's own length or the standard, capped by what the running ring holds).
/// Pure, so every case is unit-tested.
/// </summary>
public static class PressClipLength
{
    /// <summary>
    /// The length a press in <paramref name="exe"/> saves. No game, "Desktop"/"Unknown", an exe without a profile or a
    /// profile without a length: the standard length - the backup when a game is not recognised.
    /// </summary>
    /// <param name="ringSeconds">The lookback the running ring really holds (<c>EffectiveDurationSeconds</c>).</param>
    public static PressClip Resolve(int ringSeconds, AppSettings settings, string? exe)
    {
        bool own = EffectiveSettings.ProfileFor(settings, exe)?.ClipSeconds is > 0;
        int wanted = EffectiveSettings.ClipSecondsFor(settings, exe);
        return new PressClip(Math.Min(wanted, ringSeconds),
                             own ? ClipLengthSource.Profile : ClipLengthSource.Global,
                             own ? EffectiveSettings.ProfileKey(exe) : null,
                             wanted);
    }

    /// <summary>
    /// The exe a press is attributed to: the first source that answers wins, later ones are not even asked (so a pinned
    /// window never costs a foreground probe). A source that throws or answers blank is skipped. The order the app uses:
    /// the window a WGC capture pins (it IS the footage), what is really in front now (the folder follows it), and only
    /// last the detector's debounced state. No answer = the standard length.
    /// </summary>
    public static string? ResolveExe(params Func<string?>[] sources)
    {
        foreach (var source in sources)
        {
            try
            {
                var exe = source();
                if (!string.IsNullOrWhiteSpace(exe)) return exe;
            }
            catch (Exception ex) { Logger.Warn("Game at press: a source failed: " + ex.Message); }
        }
        return null;
    }

    /// <summary>A length asked for by an override (the per-press hook): capped by the ring, the source stays that of the game.</summary>
    public static PressClip WithRequested(PressClip clip, int ringSeconds, int requested)
        => clip with { Seconds = Math.Min(requested, ringSeconds), WantedSeconds = requested };

    /// <summary>One log line's worth: the length and whether it came from a profile (exe) or the standard.</summary>
    public static string Describe(PressClip clip)
    {
        string length = DurationText.Format(clip.Seconds);
        string text = clip.Source == ClipLengthSource.Profile
            ? $"press length {length} from profile '{clip.ProfileKey}'"
            : $"press length {length} (standard)";
        if (clip.CappedByRing)
            text += $" (the ring holds {length}, the {(clip.Source == ClipLengthSource.Profile ? "profile" : "standard")} asks for {DurationText.Format(clip.WantedSeconds)})";
        return text;
    }

    /// <summary>"F9 in Arma Reforger: 2 min" / "F9 saves 1 min": the status note as text.</summary>
    public static string StatusText((string? Game, int Seconds) note)
    {
        string len = DurationText.Format(note.Seconds);
        return note.Game is null
            ? L.T($"F9 speichert {len}", $"F9 saves {len}")
            : L.T($"F9 in {note.Game}: {len}", $"F9 in {note.Game}: {len}");
    }

    /// <summary>
    /// What the status lines add when F9 saves less than the ring holds: the game in front with its own length, or (no
    /// profile there) the standard length with <c>Game</c> null - so "30 min ready for F9" is never silently wrong when a
    /// profile elsewhere made the ring long. Null = F9 saves the whole ring, nothing to add.
    /// </summary>
    public static (string? Game, int Seconds)? StatusNote(AppSettings settings, string? exe, string? displayName, int ringSeconds)
    {
        var clip = Resolve(ringSeconds, settings, exe);
        if (clip.Seconds >= ringSeconds) return null;
        if (clip.Source != ClipLengthSource.Profile) return (null, clip.Seconds);
        var profile = EffectiveSettings.ProfileFor(settings, exe);
        string name = !string.IsNullOrWhiteSpace(displayName) ? displayName!.Trim()
            : !string.IsNullOrWhiteSpace(profile?.DisplayName) ? profile!.DisplayName : clip.ProfileKey ?? "";
        return (name, clip.Seconds);
    }
}
