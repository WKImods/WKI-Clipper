using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace WKI_Clipper.Services;

/// <summary>An installed Steam app: <c>appmanifest_&lt;AppId&gt;.acf</c> of one library.</summary>
public sealed record SteamApp(int AppId, string Name, string InstallDir, string Library);

/// <summary>An installed Epic Games Launcher app: one <c>*.item</c> manifest.</summary>
public sealed record EpicApp(string AppName, string DisplayName, string InstallLocation, string? LaunchExecutable);

/// <summary>Reads what is installed through Steam and the Epic Games Launcher. Pure: all disk access goes through <see cref="IFileSystemView"/>.</summary>
public static class GameLibraries
{
    /// <summary>"Steamworks Common Redistributables" - installed by Steam itself, never a game.</summary>
    public const int SteamRedistributablesAppId = 228980;

    /// <summary>
    /// The Steam root plus every library listed in <c>steamapps\libraryfolders.vdf</c>, each once
    /// (compared case-insensitively, trailing and doubled slashes ignored), root first.
    /// Understands the current format (<c>"0" { "path" ... }</c>) and the pre-2021 flat one (<c>"1" "D:\Lib"</c>).
    /// </summary>
    public static IReadOnlyList<string> SteamLibraries(string steamRoot, IFileSystemView fs)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            var normalized = ShortcutPaths.Normalize(path);
            if (normalized.Length > 0 && seen.Add(normalized)) result.Add(normalized);
        }

        if (string.IsNullOrWhiteSpace(steamRoot)) return result;
        Add(steamRoot);

        var text = fs.ReadAllText(ShortcutPaths.Combine(steamRoot, "steamapps", "libraryfolders.vdf"));
        if (string.IsNullOrEmpty(text)) return result;

        var folders = Vdf.Block(Vdf.Parse(text), "libraryfolders");
        if (folders == null) return result;

        // "0", "1", ... in numeric order; other keys (TimeNextStatsReport, ContentStatsID) are not libraries.
        foreach (var entry in folders
                     .Where(kv => kv.Key.Length > 0 && kv.Key.All(char.IsAsciiDigit))
                     .OrderBy(kv => int.TryParse(kv.Key, out var n) ? n : int.MaxValue))
        {
            Add(entry.Value switch
            {
                string flat => flat,
                Dictionary<string, object> block => Vdf.Str(block, "path"),
                _ => null,
            });
        }
        return result;
    }

    /// <summary>All <c>appmanifest_*.acf</c> of the given libraries except the Steamworks redistributables. Unreadable manifests are skipped.</summary>
    public static IReadOnlyList<SteamApp> SteamApps(IReadOnlyList<string> libraries, IFileSystemView fs)
    {
        var apps = new List<SteamApp>();
        foreach (var library in libraries)
        {
            var steamapps = ShortcutPaths.Combine(library, "steamapps");
            foreach (var file in fs.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                var text = fs.ReadAllText(file);
                if (string.IsNullOrEmpty(text)) continue;

                var state = Vdf.Block(Vdf.Parse(text), "AppState");
                if (state == null) continue;
                if (!int.TryParse(Vdf.Str(state, "appid"), System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var appId)) continue;
                if (appId == SteamRedistributablesAppId) continue;

                var installDir = Vdf.Str(state, "installdir");
                if (string.IsNullOrWhiteSpace(installDir)) continue;
                var name = Vdf.Str(state, "name");

                apps.Add(new SteamApp(appId, string.IsNullOrWhiteSpace(name) ? installDir : name,
                    installDir, ShortcutPaths.Normalize(library)));
            }
        }
        return apps;
    }

    /// <summary>
    /// The games of the Epic Games Launcher: <c>*.item</c> (JSON) in its manifests folder. Files that are not valid
    /// JSON, lack AppName or InstallLocation, or belong to a DLC (MainGameAppName names another app) are skipped.
    /// </summary>
    public static IReadOnlyList<EpicApp> EpicApps(string manifestsDir, IFileSystemView fs)
    {
        var apps = new List<EpicApp>();
        foreach (var file in fs.EnumerateFiles(manifestsDir, "*.item"))
        {
            var text = fs.ReadAllText(file);
            if (string.IsNullOrWhiteSpace(text)) continue;
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                var appName = JsonString(root, "AppName");
                var installLocation = JsonString(root, "InstallLocation");
                if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(installLocation)) continue;

                var mainGame = JsonString(root, "MainGameAppName");
                if (!string.IsNullOrWhiteSpace(mainGame) && !string.Equals(mainGame, appName, StringComparison.OrdinalIgnoreCase))
                    continue;   // DLC: shares the base game's folder but cannot be launched

                var display = JsonString(root, "DisplayName");
                var exe = JsonString(root, "LaunchExecutable");
                apps.Add(new EpicApp(appName, string.IsNullOrWhiteSpace(display) ? appName : display,
                    installLocation, string.IsNullOrWhiteSpace(exe) ? null : exe));
            }
            catch (JsonException)
            {
                // A manifest that is being rewritten or hand-damaged must not hide the other games.
            }
        }
        return apps;
    }

    private static string? JsonString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
