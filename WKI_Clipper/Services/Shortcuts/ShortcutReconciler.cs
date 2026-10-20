using System;
using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>
/// Pure bookkeeping of the shortcut bar: merges a scan into the saved item list and keeps the recent-games list.
/// Entries the scan manages are the catalog launchers ("launcher:&lt;catalogId&gt;") and browsers ("browser:&lt;id&gt;").
/// A <see cref="ShortcutKind.CatalogLauncher"/> entry's Target is what the scan found to start (Start-menu shortcut or exe),
/// a <see cref="ShortcutKind.Browser"/> entry's Target is the browser exe, so both start without a fresh scan.
/// </summary>
public static class ShortcutReconciler
{
    public const int MaxRecentGames = 30;

    public const string DefaultBrowserEntryId = "browser:default";
    public static string LauncherEntryId(string catalogId) => "launcher:" + catalogId;
    public static string BrowserEntryId(string browserId) => "browser:" + browserId;

    /// <summary>The "standard browser" entry. Its name is shown localized by the UI; the stored one is only a fallback.</summary>
    public static ShortcutEntry NewDefaultBrowserEntry() => new()
    {
        Id = DefaultBrowserEntryId,
        Group = ShortcutGroup.Browser,
        Kind = ShortcutKind.DefaultBrowser,
        Target = "default",
        DisplayName = "Default browser",
    };

    /// <summary>An entry for a browser the user picked (<see cref="Reconcile"/> never adds one by itself).</summary>
    public static ShortcutEntry NewBrowserEntry(BrowserInfo browser) => new()
    {
        Id = BrowserEntryId(browser.Id),
        Group = ShortcutGroup.Browser,
        Kind = ShortcutKind.Browser,
        Target = browser.ExePath ?? "",
        IconSource = browser.ExePath,
        DisplayName = browser.Name,
        Missing = !browser.Installed,
    };

    /// <summary>
    /// Merges a scan into the saved items and returns a NEW list (the input and its entries are not touched), grouped
    /// Game, Launcher, Browser with each group in its saved order:
    /// <list type="bullet">
    /// <item>Order, Hidden and Hotkey of saved entries are kept. Games are passed through unchanged.</item>
    /// <item>A newly detected launcher is appended to the end of the launcher group, visible.</item>
    /// <item>A saved catalog launcher that was not detected gets Missing = true and stays; one that is found again loses it and takes the new start path.</item>
    /// <item>A "standard browser" entry always exists, first in the browser group.</item>
    /// <item>Browsers are never added here, only checked: a saved one that is not installed (or unknown) is Missing.</item>
    /// </list>
    /// </summary>
    public static List<ShortcutEntry> Reconcile(IReadOnlyList<ShortcutEntry> saved, IReadOnlyList<CatalogHit> launchers, IReadOnlyList<BrowserInfo> browsers)
    {
        var games = new List<ShortcutEntry>();
        var launcherEntries = new List<ShortcutEntry>();
        var browserEntries = new List<ShortcutEntry>();
        var seenManaged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var original in saved)
        {
            var e = Clone(original);
            switch (e.Group)
            {
                case ShortcutGroup.Game:
                    games.Add(e);
                    break;
                case ShortcutGroup.Launcher:
                    if (IsManaged(e, "launcher:") && !seenManaged.Add(e.Id)) break;   // duplicate id in a hand-edited file
                    launcherEntries.Add(e);
                    break;
                default:
                    if (e.Kind == ShortcutKind.DefaultBrowser)
                    {
                        if (seenManaged.Add(DefaultBrowserEntryId)) browserEntries.Add(e);
                        break;
                    }
                    if (IsManaged(e, "browser:") && !seenManaged.Add(e.Id)) break;
                    browserEntries.Add(e);
                    break;
            }
        }

        // Launchers: refresh what the scan found, grey out what it did not, append what is new.
        var hitsById = new Dictionary<string, CatalogHit>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in launchers) hitsById.TryAdd(LauncherEntryId(hit.Id), hit);

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in launcherEntries)
        {
            if (!IsManaged(e, "launcher:")) continue;
            present.Add(e.Id);
            if (hitsById.TryGetValue(e.Id, out var hit))
            {
                e.Kind = hit.Kind;
                e.Target = hit.Target;
                e.IconSource = hit.IconSource;
                e.DisplayName = hit.Name;
                e.Missing = false;
            }
            else
            {
                e.Missing = true;
            }
        }
        foreach (var hit in launchers)
        {
            var id = LauncherEntryId(hit.Id);
            if (!present.Add(id)) continue;
            launcherEntries.Add(new ShortcutEntry
            {
                Id = id,
                Group = ShortcutGroup.Launcher,
                Kind = hit.Kind,
                Target = hit.Target,
                IconSource = hit.IconSource,
                DisplayName = hit.Name,
            });
        }

        // Browsers: the standard browser is always there and always first; picked ones follow the installed state.
        var defaultBrowser = browserEntries.FirstOrDefault(e => e.Kind == ShortcutKind.DefaultBrowser) ?? NewDefaultBrowserEntry();
        defaultBrowser.Missing = false;
        browserEntries.Remove(defaultBrowser);
        browserEntries.Insert(0, defaultBrowser);

        var browsersById = new Dictionary<string, BrowserInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in browsers) browsersById.TryAdd(BrowserEntryId(b.Id), b);
        foreach (var e in browserEntries)
        {
            if (e.Kind != ShortcutKind.Browser || !IsManaged(e, "browser:")) continue;
            if (browsersById.TryGetValue(e.Id, out var info) && info is { Installed: true, ExePath: not null })
            {
                e.Target = info.ExePath;
                e.IconSource = info.ExePath;
                e.DisplayName = info.Name;
                e.Missing = false;
            }
            else
            {
                e.Missing = true;
            }
        }

        var result = new List<ShortcutEntry>(games.Count + launcherEntries.Count + browserEntries.Count);
        result.AddRange(games);
        result.AddRange(launcherEntries);
        result.AddRange(browserEntries);
        return result;
    }

    /// <summary>
    /// Puts a game the clipper saw in front at the top of <see cref="ShortcutSettings.RecentGames"/> (newest first, no duplicates,
    /// at most <see cref="MaxRecentGames"/>). Nothing is remembered for blank paths or when <paramref name="isExcluded"/> says the
    /// exe is not a game (see <see cref="ExclusionFilter"/>).
    /// </summary>
    public static void RememberGame(ShortcutSettings s, string exePath, string name, DateTime utcNow, Func<string, bool> isExcluded)
    {
        if (string.IsNullOrWhiteSpace(exePath) || isExcluded(exePath)) return;

        s.RecentGames.RemoveAll(g => ShortcutPaths.SamePath(g.ExePath, exePath));
        s.RecentGames.Insert(0, new SeenGame
        {
            ExePath = exePath,
            DisplayName = string.IsNullOrWhiteSpace(name) ? System.IO.Path.GetFileNameWithoutExtension(exePath) : name,
            LastSeenUtc = utcNow,
        });
        if (s.RecentGames.Count > MaxRecentGames)
            s.RecentGames.RemoveRange(MaxRecentGames, s.RecentGames.Count - MaxRecentGames);
    }

    /// <summary>
    /// The "is this NOT a game?" test for <see cref="RememberGame"/>: launchers (and the processes they run), every browser,
    /// and the clipper itself - the game detection can land on any of them while the board or a launcher is in front.
    /// </summary>
    /// <param name="ownExePath">The running clipper; its file name is excluded wherever a copy runs from (dev builds). Null = the default name.</param>
    public static Func<string, bool> ExclusionFilter(IReadOnlySet<string> launcherExeNames, IReadOnlyList<BrowserInfo> browsers, string? ownExePath)
    {
        var ownName = ShortcutPaths.FileName(ownExePath);
        if (ownName.Length == 0) ownName = "WKI_Clipper.exe";
        var browserPaths = browsers.Where(b => b.ExePath != null).Select(b => ShortcutPaths.Normalize(b.ExePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return exePath =>
        {
            var normalized = ShortcutPaths.Normalize(exePath);
            if (normalized.Length == 0) return true;
            var leaf = ShortcutPaths.FileName(normalized);
            return leaf.Equals(ownName, StringComparison.OrdinalIgnoreCase)
                || launcherExeNames.Any(n => n.Equals(leaf, StringComparison.OrdinalIgnoreCase))
                || BrowserCatalog.BrowserExeNames.Contains(leaf)
                || browserPaths.Contains(normalized);
        };
    }

    private static bool IsManaged(ShortcutEntry e, string idPrefix)
        => e.Id.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase);

    private static ShortcutEntry Clone(ShortcutEntry e) => new()
    {
        Id = e.Id,
        Group = e.Group,
        Kind = e.Kind,
        Target = e.Target,
        Arguments = e.Arguments,
        DisplayName = e.DisplayName,
        IconSource = e.IconSource,
        Hidden = e.Hidden,
        Missing = e.Missing,
        Hotkey = e.Hotkey == null ? null : new HotkeyBinding { Modifiers = e.Hotkey.Modifiers, Key = e.Hotkey.Key },
    };
}
