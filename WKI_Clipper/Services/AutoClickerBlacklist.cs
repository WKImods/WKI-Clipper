using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>Where a suggestion for the blacklist comes from.</summary>
public enum BlacklistSource { Detected, Profile, ShortcutBar }

/// <summary>A game the user may put on the blacklist. <see cref="AlreadyListed"/> entries are shown but cannot be added again.</summary>
public sealed record BlacklistCandidate(string ExeName, string DisplayName, BlacklistSource Source, bool AlreadyListed);

/// <summary>
/// The AutoClicker blacklist as pure list rules: one entry per exe FILE NAME (case-insensitive, ".exe" optional on input, a full path
/// reduces to its file name), the same key the game profiles use. Callers save the settings afterwards.
/// </summary>
public static class AutoClickerBlacklist
{
    /// <summary>
    /// The identity of an exe: file name in lower case with ".exe" (the game profile key). null for blank input and for names that
    /// are no program ("Desktop", "Unknown", a bare drive).
    /// </summary>
    public static string? Key(string? exe) => EffectiveSettings.ProfileKey(Unquote(exe));

    /// <summary>The exe as the list shows it: file name only, ".exe" appended when missing, the user's casing kept. null when <see cref="Key"/> is null.</summary>
    public static string? Canonical(string? exe)
    {
        if (Key(exe) is null) return null;
        var name = Path.GetFileName(Unquote(exe)!.Trim());
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name + ".exe";
    }

    /// <summary>Clean-up for a loaded list: null entries and non-programs are dropped, exes canonicalised, the first of two equal exes wins.</summary>
    public static List<BlacklistEntry> Normalize(IEnumerable<BlacklistEntry?>? raw)
    {
        var clean = new List<BlacklistEntry>();
        if (raw is null) return clean;
        var seen = new HashSet<string>();
        foreach (var e in raw)
        {
            if (e is null || Key(e.ExeName) is not { } key || Canonical(e.ExeName) is not { } exe) continue;
            if (!seen.Add(key)) continue;
            clean.Add(new BlacklistEntry { ExeName = exe, DisplayName = LabelFor(exe, e.DisplayName) });
        }
        return clean;
    }

    /// <summary>Adds the program. False when it names no program or is on the list already (the existing entry is kept as it is).</summary>
    public static bool Add(List<BlacklistEntry> list, string? exe, string? displayName)
    {
        if (Key(exe) is not { } key || Canonical(exe) is not { } canonical) return false;
        if (list.Any(e => Key(e.ExeName) == key)) return false;
        list.Add(new BlacklistEntry { ExeName = canonical, DisplayName = LabelFor(canonical, displayName) });
        return true;
    }

    /// <summary>Removes the program. False when it was not on the list.</summary>
    public static bool Remove(List<BlacklistEntry> list, string? exe)
        => Key(exe) is { } key && list.RemoveAll(e => Key(e.ExeName) == key) > 0;

    public static bool Contains(IEnumerable<BlacklistEntry> list, string? exe)
        => Key(exe) is { } key && list.Any(e => Key(e.ExeName) == key);

    /// <summary>
    /// What the "Spiel hinzufügen" dialog offers: the game in front now, the games with a profile and the games on the shortcut bar
    /// (plus the ones the clipper saw in front lately). No program twice; the first source wins the label.
    /// </summary>
    /// <param name="listed">The list that marks a candidate as already on it; the blacklist when null (the whitelist dialog passes the whitelist).</param>
    public static IReadOnlyList<BlacklistCandidate> Candidates(AppSettings settings, string? detectedExe, string? detectedName,
        IEnumerable<BlacklistEntry>? listed = null)
    {
        listed ??= settings.AutoClicker?.Blacklist ?? new List<BlacklistEntry>();
        var result = new List<BlacklistCandidate>();
        var seen = new HashSet<string>();

        void Offer(string? exe, string? name, BlacklistSource source)
        {
            if (Key(exe) is not { } key || Canonical(exe) is not { } canonical || !seen.Add(key)) return;
            result.Add(new BlacklistCandidate(canonical, LabelFor(canonical, name), source, Contains(listed, canonical)));
        }

        Offer(detectedExe, detectedName, BlacklistSource.Detected);

        if (settings.GameProfiles is { } profiles)
            foreach (var (key, profile) in profiles)
                Offer(key, profile?.DisplayName, BlacklistSource.Profile);

        var shortcuts = settings.Shortcuts;
        if (shortcuts?.Items is { } items)
            foreach (var item in items.Where(i => i?.Group == ShortcutGroup.Game))
                Offer(ExeOf(item), item.DisplayName, BlacklistSource.ShortcutBar);
        if (shortcuts?.RecentGames is { } recent)
            foreach (var seenGame in recent.Where(g => g is not null))
                Offer(seenGame.ExePath, seenGame.DisplayName, BlacklistSource.ShortcutBar);

        return result;
    }

    /// <summary>The program a game entry of the bar starts, when the entry says so: an exe target, else an icon source that is an exe (Steam/Epic entries keep the game's main exe there).</summary>
    internal static string? ExeOf(ShortcutEntry item)
    {
        if (item.Kind == ShortcutKind.Exe && IsExe(item.Target)) return item.Target;
        return IsExe(item.IconSource) ? item.IconSource : null;
    }

    private static bool IsExe(string? path) => path?.Trim().Trim('"').EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true;

    private static string? Unquote(string? s) => s?.Trim().Trim('"');

    private static string LabelFor(string canonicalExe, string? name)
        => string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(canonicalExe) : name.Trim();
}
