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
/// start shortcut (World of Tanks: <c>wgc_api.exe --open</c>) or a launcher shortcut that names the game
/// (Valorant: <c>RiotClientServices.exe --launch-product=valorant</c>, Rainbow Six: <c>uplay://launch/...</c>).
/// Order: Steam, Epic, the start shortcut (exact target, then in the game's folders, then a launcher shortcut), exe.
/// </summary>
public static class GameLaunchResolver
{
    /// <summary>How many folders above the exe a vendor shortcut's target may sit (WorldOfTanks.exe lives in win64\, its start stub one level up).</summary>
    private const int MaxLevelsUp = 2;

    // "unins" also catches Inno Setup's unins000.exe; "deinstall" is the German spelling.
    private static readonly string[] UninstallMarkers = { "uninstall", "deinstall", "unins" };

    // Shortcuts that sit beside a game but are not the game.
    private static readonly string[] HelperMarkers = { "updater", "reporter", "repair", "readme" };

    // Programs of a game that are not the game: its dedicated server, editor, settings tool ... A marker that the game's own exe
    // name carries ("ArmaReforgerServer.exe") is no marker for that game.
    private static readonly string[] ToolMarkers =
        { "server", "dedicated", "editor", "config", "settings", "setup", "benchmark", "sdk", "tools", "modkit", "safemode" };

    // A .url that opens a web page or a file starts nothing of the game; any other scheme (uplay://, link2ea://, steam://,
    // com.epicgames.launcher://) hands the game to its launcher.
    private static readonly string[] NonLauncherSchemes = { "http", "https", "file" };

    // How far up the tree a folder name still says which game it is: "C:\Riot Games\VALORANT" does, "C:\Riot Games" does not.
    private const int MeaningfulFolderSegments = 3;

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

        // 3. The vendor's own start shortcut, or the launcher shortcut that names the game
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
        var toolMarkers = ToolMarkers.Where(m => !stem.Contains(m, StringComparison.Ordinal)).ToArray();
        var ancestors = MeaningfulAncestors(exeDir);

        ShellLinkInfo? best = null;
        var bestRank = (Tier: int.MaxValue, NameMatch: int.MaxValue, Levels: int.MaxValue, NameLength: int.MaxValue);

        foreach (var link in shortcuts)
        {
            if (string.IsNullOrWhiteSpace(link.Path)) continue;
            if (IsUninstallShortcut(link)) continue;
            if (ContainsAny(link.Name, HelperMarkers) || ContainsAny(ShortcutPaths.FileName(link.Target), HelperMarkers)) continue;

            var rank = InTreeRank(link, exe, exeDir, stem, toolMarkers, launcherExeNames)
                       ?? LauncherStartRank(link, stem, ancestors, toolMarkers);
            if (rank is not { } r) continue;

            if (best == null || r.CompareTo(bestRank) < 0
                || (r.CompareTo(bestRank) == 0 && string.Compare(link.Path, best.Path, StringComparison.OrdinalIgnoreCase) < 0))
            {
                best = link;
                bestRank = r;
            }
        }
        return best;
    }

    /// <summary>
    /// A shortcut whose target sits in the exe's folder or up to <see cref="MaxLevelsUp"/> above it: rank (exact target 0 / in-tree 1,
    /// how well the name fits, folder levels, name length), or null when it is not a start of THIS game.
    /// </summary>
    private static (int Tier, int NameMatch, int Levels, int NameLength)? InTreeRank(ShellLinkInfo link, string exe, string exeDir,
        string stem, string[] toolMarkers, IReadOnlySet<string> launcherExeNames)
    {
        if (string.IsNullOrWhiteSpace(link.Target)) return null;   // a .url has no folder to compare

        var target = ShortcutPaths.Normalize(link.Target);
        var targetDir = Path.GetDirectoryName(target);
        if (targetDir == null) return null;

        int levels = LevelsUp(exeDir, targetDir);
        if (levels < 0) return null;
        // A shared parent ("C:\Games", "C:\Program Files") says nothing about which game it is.
        if (levels > 0 && ShortcutPaths.SegmentCount(targetDir) < MeaningfulFolderSegments) return null;

        bool exact = ShortcutPaths.SamePath(target, exe);
        var folded = ShortcutPaths.Fold(link.Name);
        var folderName = ShortcutPaths.Fold(Path.GetFileName(targetDir));
        if (!exact)
        {
            // The shortcut must be about THIS game: pointing at the exe, or named after it (or its folder).
            // Everything else in a game folder is a tool ("Aslains Logs Archiver" in the World of Tanks folder).
            if (!Related(folded, stem) && !Related(folded, folderName)) return null;
            // ... and not one of the game's own tools (its server, editor, settings): "Foo Dedicated Server" is not "Foo".
            if (IsTool(link, toolMarkers)) return null;
            // A launcher exe is a fine start target only inside the game's own folder (wgc_api.exe in World_of_Tanks_EU);
            // in a shared folder it is the launcher itself and would start the wrong thing.
            if (IsLauncherName(ShortcutPaths.FileName(target), launcherExeNames) && !Related(folderName, stem)) return null;
        }

        int nameMatch = exact || folded == stem || folded == folderName ? 0 : 1;
        return (exact ? 0 : 1, nameMatch, levels, folded.Length);
    }

    /// <summary>
    /// The third start path: a launcher shortcut that starts THIS game by argument or URL - Riot's <c>RiotClientServices.exe
    /// --launch-product=valorant</c>, a <c>uplay://launch/...</c> .url - which lies outside the game's folders. It counts when its name
    /// relates to the exe's name or to a folder above the exe that names the game. Ranks behind every shortcut in the game's own tree.
    /// </summary>
    private static (int Tier, int NameMatch, int Levels, int NameLength)? LauncherStartRank(ShellLinkInfo link, string stem,
        IReadOnlyList<string> ancestors, string[] toolMarkers)
    {
        bool isUrl = string.IsNullOrWhiteSpace(link.Target);
        if (isUrl ? !IsLauncherUrl(link.Url) : string.IsNullOrWhiteSpace(link.Arguments)) return null;   // a bare launcher shortcut starts the launcher, not a game

        var folded = ShortcutPaths.Fold(link.Name);
        bool namesExe = Related(folded, stem);
        bool namesFolder = ancestors.Any(a => Related(folded, a));
        if (!namesExe && !namesFolder) return null;
        if (IsTool(link, toolMarkers)) return null;

        int nameMatch = folded == stem || ancestors.Contains(folded) ? 0 : 1;
        return (2, nameMatch, 0, folded.Length);
    }

    private static bool IsTool(ShellLinkInfo link, string[] toolMarkers)
    {
        var name = ShortcutPaths.Fold(link.Name);
        var file = ShortcutPaths.Fold(ShortcutPaths.FileName(link.Target));
        return toolMarkers.Any(m => name.Contains(m, StringComparison.Ordinal) || file.Contains(m, StringComparison.Ordinal));
    }

    /// <summary>A URL that hands the game to a launcher: a scheme of its own, not a web page or a file.</summary>
    private static bool IsLauncherUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        return !NonLauncherSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The folded names of the exe's folder and every folder above it that is deep enough to say which game it is.</summary>
    private static IReadOnlyList<string> MeaningfulAncestors(string exeDir)
    {
        var names = new List<string>();
        for (string? d = exeDir; d != null; d = Path.GetDirectoryName(d))
        {
            if (ShortcutPaths.SegmentCount(d) < MeaningfulFolderSegments) break;   // deeper-to-shallower: every folder above is shallower still
            var name = ShortcutPaths.Fold(Path.GetFileName(d));
            if (name.Length > 0) names.Add(name);
        }
        return names;
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
