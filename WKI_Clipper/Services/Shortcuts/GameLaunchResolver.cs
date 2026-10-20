using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>How to start a program: the kind of start path, what to start, optional arguments (see the spec, section 4.2).</summary>
public sealed record LaunchTarget(ShortcutKind Kind, string Target, string? Arguments);

/// <summary>
/// Finds the proper way to start a game from its running exe. The right way is almost never the exe itself:
/// Steam and Epic games must start through their launcher (DRM, updates), and some vendors ship their own
/// start shortcut (World of Tanks: <c>wgc_api.exe --open</c>). Order: Steam, Epic, vendor shortcut, exe.
/// </summary>
public static class GameLaunchResolver
{
    /// <summary>How many folders above the exe a vendor shortcut's target may sit (WorldOfTanks.exe lives in win64\, its start stub one level up).</summary>
    private const int MaxLevelsUp = 2;

    // "unins" also catches Inno Setup's unins000.exe; "deinstall" is the German spelling.
    private static readonly string[] UninstallMarkers = { "uninstall", "deinstall", "unins" };

    // Shortcuts that sit beside a game but are not the game.
    private static readonly string[] HelperMarkers = { "updater", "reporter", "repair", "readme" };

    public static LaunchTarget Resolve(string exePath, IReadOnlyList<SteamApp> steam, IReadOnlyList<EpicApp> epic,
        IReadOnlyList<ShellLinkInfo> shortcuts, IReadOnlySet<string> launcherExeNames)
    {
        var exe = ShortcutPaths.Normalize(exePath);
        if (exe.Length == 0) return new LaunchTarget(ShortcutKind.Exe, exePath, null);

        // 1. Steam: <library>\steamapps\common\<installdir>\...  (an empty installdir would match the whole "common" folder)
        foreach (var app in steam)
        {
            if (string.IsNullOrWhiteSpace(app.InstallDir) || string.IsNullOrWhiteSpace(app.Library)) continue;
            var installFolder = ShortcutPaths.Combine(app.Library, "steamapps", "common", app.InstallDir);
            if (ShortcutPaths.IsSameOrUnder(exe, installFolder))
                return new LaunchTarget(ShortcutKind.SteamApp, app.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture), null);
        }

        // 2. Epic: anywhere below the app's InstallLocation
        foreach (var app in epic)
        {
            if (string.IsNullOrWhiteSpace(app.InstallLocation) || string.IsNullOrWhiteSpace(app.AppName)) continue;
            if (ShortcutPaths.IsSameOrUnder(exe, app.InstallLocation))
                return new LaunchTarget(ShortcutKind.EpicApp, app.AppName, null);
        }

        // 3. The vendor's own start shortcut
        var link = FindVendorShortcut(exe, shortcuts, launcherExeNames);
        if (link != null) return new LaunchTarget(ShortcutKind.ShellLink, link.Path, null);

        // 4. The exe itself
        return new LaunchTarget(ShortcutKind.Exe, exePath, null);
    }

    /// <summary>
    /// True for shortcuts that remove the program: "uninstall", "deinstall" or "unins" in the shortcut's name, its
    /// arguments, the file name of its target or its URL. Only the target's FILE name counts, not its folders.
    /// </summary>
    public static bool IsUninstallShortcut(ShellLinkInfo link)
    {
        return ContainsAny(link.Name, UninstallMarkers)
            || ContainsAny(link.Arguments, UninstallMarkers)
            || ContainsAny(ShortcutPaths.FileName(link.Target), UninstallMarkers)
            || ContainsAny(link.Url, UninstallMarkers);
    }

    private static bool ContainsAny(string? text, string[] markers)
        => !string.IsNullOrEmpty(text) && markers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));

    private static ShellLinkInfo? FindVendorShortcut(string exe, IReadOnlyList<ShellLinkInfo> shortcuts, IReadOnlySet<string> launcherExeNames)
    {
        var exeDir = Path.GetDirectoryName(exe);
        if (exeDir == null) return null;
        var stem = ShortcutPaths.Fold(Path.GetFileNameWithoutExtension(exe));

        ShellLinkInfo? best = null;
        (int Exact, int Levels) bestRank = (int.MaxValue, int.MaxValue);

        foreach (var link in shortcuts)
        {
            if (string.IsNullOrWhiteSpace(link.Path) || string.IsNullOrWhiteSpace(link.Target)) continue;   // .url files have no folder to compare
            if (IsUninstallShortcut(link)) continue;
            if (ContainsAny(link.Name, HelperMarkers) || ContainsAny(ShortcutPaths.FileName(link.Target), HelperMarkers)) continue;

            var target = ShortcutPaths.Normalize(link.Target);
            var targetDir = Path.GetDirectoryName(target);
            if (targetDir == null) continue;

            int levels = LevelsUp(exeDir, targetDir);
            if (levels < 0) continue;
            // A shared parent ("C:\Games", "C:\Program Files") says nothing about which game it is.
            if (levels > 0 && ShortcutPaths.SegmentCount(targetDir) < 3) continue;

            bool exact = ShortcutPaths.SamePath(target, exe);
            var folderName = ShortcutPaths.Fold(Path.GetFileName(targetDir));
            // The shortcut must be about THIS game: pointing at the exe, or named after it (or its folder).
            // Everything else in a game folder is a tool ("Aslains Logs Archiver" in the World of Tanks folder).
            if (!exact && !Related(ShortcutPaths.Fold(link.Name), stem) && !Related(ShortcutPaths.Fold(link.Name), folderName)) continue;

            // A launcher exe is a fine start target only inside the game's own folder (wgc_api.exe in World_of_Tanks_EU);
            // in a shared folder it is the launcher itself and would start the wrong thing.
            if (!exact && IsLauncherName(ShortcutPaths.FileName(target), launcherExeNames) && !Related(folderName, stem)) continue;

            var rank = (exact ? 0 : 1, levels);
            if (best == null || rank.CompareTo(bestRank) < 0
                || (rank.CompareTo(bestRank) == 0 && string.Compare(link.Path, best.Path, StringComparison.OrdinalIgnoreCase) < 0))
            {
                best = link;
                bestRank = rank;
            }
        }
        return best;
    }

    /// <summary>0 when <paramref name="folder"/> is the exe's folder, 1 for its parent, ... up to <see cref="MaxLevelsUp"/>; -1 otherwise.</summary>
    private static int LevelsUp(string exeDir, string folder)
    {
        string? d = exeDir;
        for (int level = 0; level <= MaxLevelsUp && d != null; level++, d = Path.GetDirectoryName(d))
            if (ShortcutPaths.SamePath(d, folder)) return level;
        return -1;
    }

    /// <summary>Folded names relate when one contains the other (and both are long enough to mean something).</summary>
    private static bool Related(string a, string b)
        => a.Length >= 3 && b.Length >= 3 && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal));

    private static bool IsLauncherName(string fileName, IReadOnlySet<string> launcherExeNames)
        => fileName.Length > 0 && launcherExeNames.Any(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase));
}
