using System;
using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>
/// Creating, changing and cleaning up game profiles in the settings (the read side is
/// <see cref="EffectiveSettings"/>). Pure: callers save the settings afterwards.
/// </summary>
public static class GameProfiles
{
    /// <summary>
    /// Clean-up for a loaded section: null entries and keys that mean no exe are dropped, the others get their
    /// key normalised (a hand-edited "Cs2.EXE" must still be found). Two keys that mean the same exe: the first wins.
    /// </summary>
    public static Dictionary<string, GameProfile> Normalize(Dictionary<string, GameProfile>? raw)
    {
        var clean = new Dictionary<string, GameProfile>();
        if (raw is null) return clean;
        foreach (var (key, profile) in raw)
        {
            if (profile is null || EffectiveSettings.ProfileKey(key) is not { } k) continue;
            clean.TryAdd(k, profile);
        }
        return clean;
    }

    /// <summary>The profile for this exe, created when missing. null when the exe names no game (none, Desktop, Unknown).</summary>
    public static GameProfile? GetOrAdd(AppSettings settings, string? exe, string? displayName)
    {
        if (EffectiveSettings.ProfileKey(exe) is not { } key) return null;
        settings.GameProfiles ??= new();
        if (!settings.GameProfiles.TryGetValue(key, out var profile))
            settings.GameProfiles[key] = profile = new GameProfile();
        if (!string.IsNullOrWhiteSpace(displayName)) profile.DisplayName = displayName.Trim();
        return profile;
    }

    /// <summary>
    /// Sets (or, with null, clears) the game's own F9 length. False when the exe names no game. A profile that
    /// is left with nothing in it is removed.
    /// </summary>
    public static bool SetClipSeconds(AppSettings settings, string? exe, string? displayName, int? seconds)
    {
        if (EffectiveSettings.ProfileKey(exe) is not { } key) return false;
        if (seconds is null)
        {
            if (EffectiveSettings.ProfileFor(settings, exe) is { } existing) existing.ClipSeconds = null;
            Prune(settings, key);
            return true;
        }
        GetOrAdd(settings, exe, displayName)!.ClipSeconds = DurationText.Clamp(seconds.Value);
        return true;
    }

    /// <summary>The look of the game's profile for editing: profile and look are created when missing. null for a non-game.</summary>
    public static CrosshairLook? LookForEditing(AppSettings settings, string? exe, string? displayName)
    {
        var profile = GetOrAdd(settings, exe, displayName);
        if (profile is null) return null;
        return profile.Crosshair ??= new CrosshairLook();
    }

    /// <summary>A drag of the crosshair while a profile is active: the new place as an offset (DIPs from the game monitor's centre).</summary>
    public static void SetCrosshairOffset(AppSettings settings, string? exe, string? displayName, double offsetX, double offsetY)
    {
        var look = LookForEditing(settings, exe, displayName);
        if (look is null) return;
        look.OffsetX = offsetX;
        look.OffsetY = offsetY;
    }

    /// <summary>"Wie Standard": drops every crosshair override of the game (the F9 length stays). The empty profile goes too.</summary>
    public static void ResetCrosshair(AppSettings settings, string? exe)
    {
        if (EffectiveSettings.ProfileKey(exe) is not { } key) return;
        if (EffectiveSettings.ProfileFor(settings, exe) is { } profile) profile.Crosshair = null;
        Prune(settings, key);
    }

    /// <summary>Removes the whole profile (length and look). False when there was none.</summary>
    public static bool Remove(AppSettings settings, string? exe)
        => EffectiveSettings.ProfileKey(exe) is { } key && settings.GameProfiles.Remove(key);

    /// <summary>Removes the profile when it overrides nothing any more.</summary>
    public static void Prune(AppSettings settings, string key)
    {
        if (settings.GameProfiles.TryGetValue(key, out var p) && p.ClipSeconds is null && !HasOverrides(p.Crosshair))
            settings.GameProfiles.Remove(key);
    }

    /// <summary>A library image was deleted: profiles must not keep pointing at it.</summary>
    public static void ForgetCrosshairId(AppSettings settings, string id)
    {
        foreach (var p in settings.GameProfiles.Values)
            if (p?.Crosshair is { } look && string.Equals(look.ActiveId, id, StringComparison.Ordinal))
                look.ActiveId = null;
    }

    /// <summary>True when the look sets at least one field.</summary>
    public static bool HasOverrides(CrosshairLook? look) => OverriddenFields(look).Count > 0;

    /// <summary>Names of the fields the look sets (for the log and the "has own values" hints).</summary>
    public static List<string> OverriddenFields(CrosshairLook? look)
    {
        var names = new List<string>();
        if (look is null) return names;
        if (!string.IsNullOrEmpty(look.ActiveId)) names.Add(nameof(look.ActiveId));
        if (look.Scale is not null) names.Add(nameof(look.Scale));
        if (look.Opacity is not null) names.Add(nameof(look.Opacity));
        if (look.Brightness is not null) names.Add(nameof(look.Brightness));
        if (look.Contrast is not null) names.Add(nameof(look.Contrast));
        if (look.Saturation is not null) names.Add(nameof(look.Saturation));
        if (look.GainR is not null) names.Add(nameof(look.GainR));
        if (look.GainG is not null) names.Add(nameof(look.GainG));
        if (look.GainB is not null) names.Add(nameof(look.GainB));
        if (look.OffsetX is not null) names.Add(nameof(look.OffsetX));
        if (look.OffsetY is not null) names.Add(nameof(look.OffsetY));
        return names;
    }

    /// <summary>The key of the profile that applies while this game is in front; null = none (the standard settings apply).</summary>
    public static string? ActiveKey(AppSettings settings, string? exe)
        => EffectiveSettings.ProfileFor(settings, exe) is not null ? EffectiveSettings.ProfileKey(exe) : null;

    /// <summary>One log line's worth: what this game's profile changes, or that the standard settings apply.</summary>
    public static string Describe(AppSettings settings, string? exe)
    {
        var p = EffectiveSettings.ProfileFor(settings, exe);
        if (p is null) return "no profile - standard settings";
        var parts = new List<string>();
        if (p.ClipSeconds is > 0) parts.Add($"clip length {EffectiveSettings.ClipSecondsFor(settings, exe)} s");
        var fields = OverriddenFields(p.Crosshair);
        if (fields.Count > 0) parts.Add("crosshair: " + string.Join(", ", fields));
        return parts.Count == 0 ? "profile without overrides - standard settings" : string.Join("; ", parts);
    }
}
