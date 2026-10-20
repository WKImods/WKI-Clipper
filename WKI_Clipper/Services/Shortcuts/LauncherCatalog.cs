using System;
using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>
/// An installed launcher found by <see cref="LauncherCatalog.Detect"/>. <see cref="Target"/> is what the bar starts: the
/// Start-menu shortcut (started like a double-click), the exe from the registry fallback, or for the Xbox app its AUMID.
/// <see cref="IconSource"/> is where the logo is read from.
/// </summary>
public sealed record CatalogHit(string Id, string Name, ShortcutKind Kind, string Target, string? IconSource);

/// <summary>
/// The launchers the bar knows (shortcut bar spec, section 3). The OFFICIAL start target is the one the Start menu uses, which is
/// not always the main exe (Rockstar starts LauncherPatcher.exe, Wargaming wgc.exe), so a row is matched by the target file
/// name of a Start-menu shortcut, with a registry exe as the fallback where one is known.
/// </summary>
public static class LauncherCatalog
{
    public const string XboxAumid = "Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App";

    private sealed record Row(string Id, string Name, string? TargetFile, string? FallbackHive, string? FallbackKey, string? FallbackValue);

    // Catalog order is the order of the launcher group on a fresh install.
    private static readonly Row[] Rows =
    {
        new("steam",     "Steam",                     "steam.exe",               "HKCU", @"Software\Valve\Steam",                  "SteamExe"),
        new("ea",        "EA app",                    "EALauncher.exe",          "HKLM", @"SOFTWARE\Electronic Arts\EA Desktop",   "LauncherAppPath"),
        new("epic",      "Epic Games",                "EpicGamesLauncher.exe",   null, null, null),
        new("gog",       "GOG GALAXY",                "GalaxyClient.exe",        null, null, null),
        new("ubisoft",   "Ubisoft Connect",           "UbisoftConnect.exe",      null, null, null),
        new("battlenet", "Battle.net",                "Battle.net Launcher.exe", null, null, null),
        new("rockstar",  "Rockstar Games",            "LauncherPatcher.exe",     null, null, null),
        new("wgc",       "Wargaming.net Game Center", "wgc.exe",                 null, null, null),
        new("xbox",      "Xbox",                      null,                      null, null, null),   // Store app: AUMID, no exe
    };

    // Shortcuts with these words in the name are helpers of a launcher, not the launcher.
    private static readonly string[] HelperMarkers = { "updater", "reporter", "deinstall", "uninstall", "unins", "recovery" };

    /// <summary>
    /// The exe names of all launchers: the catalog's target files plus processes a launcher runs that the clipper's game detection
    /// can mistake for a game (EADesktop.exe, Battle.net.exe, Launcher.exe, wgc_api.exe). Case-insensitive.
    /// </summary>
    public static IReadOnlySet<string> LauncherExeNames { get; } = BuildLauncherExeNames();

    private static HashSet<string> BuildLauncherExeNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "EADesktop.exe", "Battle.net.exe", "Launcher.exe", "wgc_api.exe",
        };
        foreach (var row in Rows)
            if (row.TargetFile != null) names.Add(row.TargetFile);
        return names;
    }

    /// <summary>
    /// Launchers that are installed. A row matches a shortcut when the shortcut's target is the row's file, the shortcut passes
    /// no arguments (GOG's "Cyberpunk 2077.lnk" runs GalaxyClient.exe /command=runGame ... and EA's "App Recovery" runs
    /// EALauncher.exe -recovery - those are not the launcher) and is no updater, error reporter or uninstaller. A Start-menu
    /// copy wins over a desktop copy. Without a usable shortcut the row's registry exe is used, if the file exists.
    /// </summary>
    /// <param name="storeAppInstalled">Asked with an AUMID: is that Store app installed?</param>
    public static IReadOnlyList<CatalogHit> Detect(IReadOnlyList<ShellLinkInfo> startMenu, IRegistryView reg, IFileSystemView fs,
        Func<string, bool> storeAppInstalled)
    {
        var hits = new List<CatalogHit>();
        foreach (var row in Rows)
        {
            if (row.TargetFile == null)
            {
                if (row.Id == "xbox" && storeAppInstalled(XboxAumid))
                    hits.Add(new CatalogHit(row.Id, row.Name, ShortcutKind.StoreApp, XboxAumid, @"shell:AppsFolder\" + XboxAumid));
                continue;
            }

            var link = FindShortcut(row.TargetFile, startMenu, fs);
            if (link != null)
            {
                hits.Add(new CatalogHit(row.Id, row.Name, ShortcutKind.CatalogLauncher, link.Path, link.Path));
                continue;
            }

            if (row.FallbackHive != null && row.FallbackKey != null)
            {
                var exe = ShortcutPaths.Normalize(reg.GetString(row.FallbackHive, row.FallbackKey, row.FallbackValue));
                if (exe.Length > 0 && fs.FileExists(exe))
                    hits.Add(new CatalogHit(row.Id, row.Name, ShortcutKind.CatalogLauncher, exe, exe));
            }
        }
        return hits;
    }

    private static ShellLinkInfo? FindShortcut(string targetFile, IReadOnlyList<ShellLinkInfo> startMenu, IFileSystemView fs)
    {
        return startMenu
            .Where(l => !string.IsNullOrWhiteSpace(l.Path) && !string.IsNullOrWhiteSpace(l.Target))
            .Where(l => string.Equals(ShortcutPaths.FileName(l.Target), targetFile, StringComparison.OrdinalIgnoreCase))
            .Where(l => string.IsNullOrWhiteSpace(l.Arguments))
            .Where(l => !HelperMarkers.Any(m => l.Name.Contains(m, StringComparison.OrdinalIgnoreCase)))
            .Where(l => fs.FileExists(ShortcutPaths.Normalize(l.Target)))
            .OrderBy(l => l.Path.Contains(@"\Start Menu\", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(l => l.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}
