using System;
using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>One line of the "Eigene Länge pro Spiel" list.</summary>
public sealed record ProfileLengthRow(string Key, string DisplayName, int Seconds);

public enum AddCurrentReason { None, NoGame, AlreadyHasLength }

/// <summary>State of the "Aktuelles Spiel hinzufügen" button.</summary>
public sealed record AddCurrentState(bool CanAdd, string? Key, string Name, AddCurrentReason Reason);

/// <summary>The profile whose length makes the ring longer than the standard length.</summary>
public sealed record RingDriver(string Key, string DisplayName, int Seconds);

/// <summary>
/// The logic behind the video settings' per-game F9 length list, kept pure (no controls, no host) so the rows, the add
/// button and the "this profile sizes the buffer" note are tested.
/// </summary>
public static class ProfileLengths
{
    private static string NameOf(string key, GameProfile? profile)
        => string.IsNullOrWhiteSpace(profile?.DisplayName) ? key : profile!.DisplayName.Trim();

    /// <summary>Every profile that has an F9 length, sorted by name; lengths clamped like everywhere else.</summary>
    public static IReadOnlyList<ProfileLengthRow> Rows(AppSettings settings)
        => (settings.GameProfiles ?? new())
            .Where(kv => kv.Value?.ClipSeconds is > 0)
            .Select(kv => new ProfileLengthRow(kv.Key, NameOf(kv.Key, kv.Value), DurationText.Clamp(kv.Value.ClipSeconds!.Value)))
            .OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Can the detected game be added? Needs a game (not "Desktop"/none) that has no length of its own yet; a profile that
    /// only carries a crosshair look does not count.
    /// </summary>
    public static AddCurrentState AddCurrent(AppSettings settings, string? exe, string? displayName)
    {
        if (EffectiveSettings.ProfileKey(exe) is not { } key)
            return new AddCurrentState(false, null, "", AddCurrentReason.NoGame);
        var profile = EffectiveSettings.ProfileFor(settings, exe);
        string name = !string.IsNullOrWhiteSpace(displayName) ? displayName!.Trim() : NameOf(key, profile);
        return profile?.ClipSeconds is > 0
            ? new AddCurrentState(false, key, name, AddCurrentReason.AlreadyHasLength)
            : new AddCurrentState(true, key, name, AddCurrentReason.None);
    }

    /// <summary>The ring length for a chosen standard length: the longest of it and every profile.</summary>
    public static int RingSeconds(AppSettings settings, int globalSeconds)
        => EffectiveSettings.LongestClipSeconds(globalSeconds, settings.GameProfiles?.Values.Select(p => p?.ClipSeconds));

    /// <summary>The profile (the longest; ties by name) that makes the ring longer than the standard length - null when none does.</summary>
    public static RingDriver? DriverOf(AppSettings settings, int globalSeconds)
    {
        int global = DurationText.Clamp(globalSeconds);
        var top = Rows(settings).OrderByDescending(r => r.Seconds).FirstOrDefault();
        return top is not null && top.Seconds > global ? new RingDriver(top.Key, top.DisplayName, top.Seconds) : null;
    }
}
