using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>
/// The settings that really apply while a given game is in front: a game profile merged over the standard
/// settings. Pure (no I/O, no clock), so every merge rule is unit-tested.
///
/// A null / blank / "Desktop" / "Unknown" exe, or a game without a profile, resolves to the standard settings -
/// the "backup" for a game that is not recognised.
/// </summary>
public static class EffectiveSettings
{
    /// <summary>
    /// The profile key for an exe: the file name in lower case (".exe" added when it does not end in it). null for nothing, for
    /// the folder names the capture uses when no game is involved ("Desktop", "Unknown") and for a bare drive.
    /// </summary>
    public static string? ProfileKey(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return null;
        var name = Path.GetFileName(exe.Trim());
        if (name.Length == 0) return null;
        if (name.Equals(GameFolderNaming.DesktopFolder, StringComparison.OrdinalIgnoreCase)
            || name.Equals(GameFolderNaming.UnknownFolder, StringComparison.OrdinalIgnoreCase)) return null;
        // Only ".exe" counts as the extension: a dotted process name ("Minecraft.Windows", as the capture settings
        // write it) is still missing its ".exe" and must meet the detected game's "minecraft.windows.exe".
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
        return name.ToLowerInvariant();
    }

    /// <summary>The game's profile, or null (no game, not profiled).</summary>
    public static GameProfile? ProfileFor(AppSettings settings, string? exe)
        => ProfileKey(exe) is { } key && settings.GameProfiles is { } all && all.TryGetValue(key, out var p) ? p : null;

    // ---- F9 length ----

    /// <summary>
    /// How many seconds F9 saves while this game is in front: the profile's length, else the standard one -
    /// both clamped like every configured length (a hand-edited file cannot ask for hours). The buffer's own
    /// limits (disk guards, material actually recorded) still cap it.
    /// </summary>
    public static int ClipSecondsFor(AppSettings settings, string? exe)
    {
        int? own = ProfileFor(settings, exe)?.ClipSeconds;
        return DurationText.Clamp(own is > 0 ? own.Value : settings.ReplayBuffer.DurationSeconds);
    }

    /// <summary>
    /// The longest length among the standard and every profile. The buffer ring is planned for THIS, once:
    /// resizing it when a game comes to the front would restart the capture exactly then and lose the lookback.
    /// </summary>
    public static int LongestClipSeconds(AppSettings settings)
        => LongestClipSeconds(settings.ReplayBuffer.DurationSeconds,
                              settings.GameProfiles?.Values.Select(p => p?.ClipSeconds));

    /// <summary>The pure core of <see cref="LongestClipSeconds(AppSettings)"/>: nulls and non-positive lengths are ignored.</summary>
    public static int LongestClipSeconds(int globalSeconds, IEnumerable<int?>? profileSeconds)
    {
        int longest = DurationText.Clamp(globalSeconds);
        if (profileSeconds is null) return longest;
        foreach (var s in profileSeconds)
            if (s is > 0) longest = Math.Max(longest, DurationText.Clamp(s.Value));
        return longest;
    }

    // ---- crosshair ----

    /// <summary>
    /// The crosshair look to show for this game, as a detached copy (editing it never changes the settings).
    /// Fields the profile sets replace the standard ones; <see cref="CrosshairSettings.Enabled"/>, snapping,
    /// grid and the standard position always come from the standard settings. A profile OFFSET is not applied
    /// here (it needs the monitor): see <see cref="CrosshairOffsetFor"/> and <see cref="CrosshairPlacement"/>.
    /// </summary>
    public static CrosshairSettings CrosshairLookFor(AppSettings settings, string? exe)
    {
        var g = settings.Crosshair;
        var p = ProfileFor(settings, exe)?.Crosshair;
        return new CrosshairSettings
        {
            Enabled = g.Enabled,   // never from a profile: it decides the capture path
            ActiveId = string.IsNullOrEmpty(p?.ActiveId) ? g.ActiveId : p!.ActiveId,
            CenterX = g.CenterX,
            CenterY = g.CenterY,
            SnapToGrid = g.SnapToGrid,
            GridSize = g.GridSize,
            Scale = p?.Scale ?? g.Scale,
            Opacity = p?.Opacity ?? g.Opacity,
            Brightness = p?.Brightness ?? g.Brightness,
            Contrast = p?.Contrast ?? g.Contrast,
            Saturation = p?.Saturation ?? g.Saturation,
            RedGain = p?.GainR ?? g.RedGain,
            GreenGain = p?.GainG ?? g.GreenGain,
            BlueGain = p?.GainB ?? g.BlueGain,
        };
    }

    /// <summary>
    /// The profile's crosshair position as an offset in DIPs from the game monitor's centre; null = the profile
    /// sets none (the standard position applies). A single missing axis counts as 0.
    /// </summary>
    public static (double X, double Y)? CrosshairOffsetFor(AppSettings settings, string? exe)
    {
        var p = ProfileFor(settings, exe)?.Crosshair;
        if (p is null || (p.OffsetX is null && p.OffsetY is null)) return null;
        return (p.OffsetX ?? 0, p.OffsetY ?? 0);
    }
}
