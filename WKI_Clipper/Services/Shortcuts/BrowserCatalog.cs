using System;
using System.Collections.Generic;
using System.Linq;

namespace WKI_Clipper.Services;

/// <summary>A browser the bar can start. <see cref="ExePath"/> is null unless <see cref="Installed"/>; <see cref="DownloadUrl"/> is set for the four named candidates.</summary>
public sealed record BrowserInfo(string Id, string Name, string? ExePath, bool Installed, string? DownloadUrl);

/// <summary>
/// Finds the installed browsers from the StartMenuInternet registry keys (HKLM, WOW6432Node, HKCU). Opera, Chrome, Comet and
/// Firefox are always listed (installed or not, with their download page); anything else the registry knows (Brave, Opera GX,
/// Edge ...) is added. Internet Explorer is registered on every Windows but switched off since 2023: it is never listed.
/// Edge is not assumed to exist - it is listed only when the registry names an exe.
/// </summary>
public static class BrowserCatalog
{
    private static readonly (string Hive, string Key)[] Roots =
    {
        ("HKLM", @"SOFTWARE\Clients\StartMenuInternet"),
        ("HKLM", @"SOFTWARE\WOW6432Node\Clients\StartMenuInternet"),
        ("HKCU", @"SOFTWARE\Clients\StartMenuInternet"),
    };

    // Download pages checked on 2026-10-20 (all four answer 200; Opera only to a browser user agent).
    private static readonly (string Id, string Name, string Url)[] Candidates =
    {
        ("opera",   "Opera",           "https://www.opera.com/download"),
        ("chrome",  "Google Chrome",   "https://www.google.com/chrome/"),
        ("comet",   "Comet",           "https://www.perplexity.ai/comet"),
        ("firefox", "Mozilla Firefox", "https://www.firefox.com/"),
    };

    /// <summary>Exe names of browsers, installed or not. The game detection must never take one for a game.</summary>
    public static IReadOnlySet<string> BrowserExeNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "firefox.exe", "opera.exe", "comet.exe", "brave.exe", "msedge.exe", "iexplore.exe",
        "vivaldi.exe", "chromium.exe",
    };

    public static IReadOnlyList<BrowserInfo> Detect(IRegistryView reg, IFileSystemView fs)
    {
        var found = new Dictionary<string, BrowserInfo>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var (hive, root) in Roots)
        {
            foreach (var sub in reg.SubKeys(hive, root))
            {
                var key = root + "\\" + sub;
                var exe = ExeFromCommand(reg.GetString(hive, key + @"\shell\open\command", null));
                if (exe.Length == 0) continue;
                var leaf = ShortcutPaths.FileName(exe);
                if (leaf.Equals("iexplore.exe", StringComparison.OrdinalIgnoreCase)
                    || sub.Equals("IEXPLORE.EXE", StringComparison.OrdinalIgnoreCase)) continue;

                var registryName = FirstNonBlank(reg.GetString(hive, key, null), reg.GetString(hive, key + @"\Capabilities", "ApplicationName"));
                var (id, name) = Classify(exe, sub, registryName);
                bool installed = fs.FileExists(exe);
                var info = new BrowserInfo(id, name, installed ? exe : null, installed, CandidateUrl(id));

                // Chrome sits in HKLM and WOW6432Node: one row. An installed entry beats a dead one.
                if (!found.TryGetValue(id, out var existing))
                {
                    found[id] = info;
                    order.Add(id);
                }
                else if (!existing.Installed && info.Installed)
                {
                    found[id] = info;
                }
            }
        }

        var result = new List<BrowserInfo>();
        foreach (var (id, name, url) in Candidates)
            result.Add(found.TryGetValue(id, out var hit) ? hit : new BrowserInfo(id, name, null, false, url));

        var candidateIds = Candidates.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        result.AddRange(order
            .Where(id => !candidateIds.Contains(id))
            .Select(id => found[id])
            .OrderByDescending(b => b.Installed)
            .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>
    /// The exe the "standard browser" button starts: the browser chosen in the edit dialog if it is installed, otherwise the
    /// Windows default. <paramref name="fellBack"/> is true when a choice exists but could not be used (uninstalled or unknown),
    /// so the caller can say so once. Null when nothing usable is known - Internet Explorer as Windows default counts as nothing.
    /// </summary>
    public static string? ResolveDefault(string? chosenId, IReadOnlyList<BrowserInfo> browsers, string? windowsDefaultExe, out bool fellBack)
    {
        fellBack = false;
        if (!string.IsNullOrWhiteSpace(chosenId))
        {
            var chosen = browsers.FirstOrDefault(b => string.Equals(b.Id, chosenId, StringComparison.OrdinalIgnoreCase));
            if (chosen is { Installed: true, ExePath: not null }) return chosen.ExePath;
            fellBack = true;
        }

        if (string.IsNullOrWhiteSpace(windowsDefaultExe)) return null;
        if (ShortcutPaths.FileName(windowsDefaultExe).Equals("iexplore.exe", StringComparison.OrdinalIgnoreCase)) return null;
        return windowsDefaultExe;
    }

    // ---- helpers ----

    private static string? CandidateUrl(string id)
        => Candidates.Where(c => c.Id == id).Select(c => c.Url).FirstOrDefault();

    private static string? FirstNonBlank(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>The exe out of a shell command: <c>"C:\a b\x.exe" --arg</c> or the unquoted <c>C:\a b\x.exe -arg</c>.</summary>
    internal static string ExeFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "";
        var c = command.Trim();
        if (c[0] == '"')
        {
            int end = c.IndexOf('"', 1);
            return ShortcutPaths.Normalize(end > 1 ? c[1..end] : c[1..]);
        }
        int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return ShortcutPaths.Normalize(exe >= 0 ? c[..(exe + 4)] : c);
    }

    /// <summary>Stable id and display name. The four candidates and Edge get fixed ids; others are named after their registry entry or exe.</summary>
    private static (string Id, string Name) Classify(string exe, string subKey, string? registryName)
    {
        var leaf = ShortcutPaths.FileName(exe).ToLowerInvariant();
        var hint = ShortcutPaths.Fold(exe + " " + subKey + " " + registryName);

        switch (leaf)
        {
            case "opera.exe":
                return hint.Contains("operagx", StringComparison.Ordinal) ? ("operagx", "Opera GX") : ("opera", "Opera");
            case "chrome.exe":   return ("chrome", "Google Chrome");
            case "firefox.exe":  return ("firefox", "Mozilla Firefox");
            case "comet.exe":    return ("comet", "Comet");
            case "msedge.exe":   return ("edge", FirstNonBlank(registryName) ?? "Microsoft Edge");
        }

        var stem = System.IO.Path.GetFileNameWithoutExtension(leaf);
        var id = ShortcutPaths.Fold(stem);
        return (id.Length > 0 ? id : "browser", FirstNonBlank(registryName) ?? stem);
    }
}
