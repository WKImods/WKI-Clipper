using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>What the bar draws for one entry. <see cref="IconPng"/> is a cached 256 px PNG, null while it is not (yet) known.</summary>
public sealed record BarItem(string Id, ShortcutGroup Group, string Name, string? IconPng, bool Missing);

/// <summary>
/// The shortcut bar's brain: keeps the item list in the settings, scans the PC in the background, and starts things.
/// Pure decisions live in the catalogs, the reconciler and the start plan; this class orchestrates them behind the small
/// <see cref="IShortcutShell"/>/<see cref="IFileSystemView"/>/<see cref="IRegistryView"/> seams.
///
/// Threading: the item list is replaced, never modified in place (copy on write), and every change happens under one lock, so
/// the UI thread (bar, editor), the hotkey dispatch and the pool threads (scan, game detector) can call in freely. Saving goes
/// through <see cref="SettingsService.Save"/> (its own lock) after the lock is released. <see cref="ItemsChanged"/> is raised on
/// the thread that changed something - UI consumers marshal themselves.
/// </summary>
public sealed class ShortcutService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly IShortcutShell _shell;
    private readonly IFileSystemView _fs;
    private readonly IRegistryView _reg;
    private readonly Action _closeBoard;
    private readonly Action<string> _toast;

    private readonly object _gate = new();
    private Dictionary<string, string?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<BrowserInfo> _browsers = Array.Empty<BrowserInfo>();
    private Task? _scan;
    private DateTime? _lastScanUtc;
    private volatile bool _disposed;

    /// <summary>The list or something the bar shows (order, hidden, missing, icons) changed. Not raised for the recent-games list.</summary>
    public event EventHandler? ItemsChanged;

    /// <summary>When the last scan finished (UTC), null before the first one. The board rescans when this is older than a minute.</summary>
    public DateTime? LastScanUtc { get { lock (_gate) return _lastScanUtc; } }

    /// <param name="closeBoard">Closes the widget board. Must be harmless while the board is closed (a hotkey launch).</param>
    /// <param name="toast">Shows a short notice to the user.</param>
    public ShortcutService(SettingsService settings, IShortcutShell shell, IFileSystemView fs, IRegistryView reg,
        Action closeBoard, Action<string> toast)
    {
        _settings = settings;
        _shell = shell;
        _fs = fs;
        _reg = reg;
        _closeBoard = closeBoard;
        _toast = toast;

        // The standard browser needs no scan: the bar offers it from the very first open.
        lock (_gate)
        {
            var items = Section.Items;
            if (!items.Any(e => e.Kind == ShortcutKind.DefaultBrowser))
            {
                var list = items.ToList();
                int at = list.FindIndex(e => e.Group == ShortcutGroup.Browser);
                list.Insert(at < 0 ? list.Count : at, ShortcutReconciler.NewDefaultBrowserEntry());
                Section.Items = list;
            }
        }
    }

    private ShortcutSettings Section => _settings.Current.Shortcuts;

    // ---------------------------------------------------------------- reading

    /// <summary>What the bar shows: games, launchers, browsers, each group in its saved order, hidden entries left out.</summary>
    public IReadOnlyList<BarItem> VisibleItems()
    {
        lock (_gate)
        {
            return Section.Items
                .Where(e => !e.Hidden)
                .OrderBy(e => (int)e.Group)   // stable: the saved order is kept inside a group
                .Select(e => new BarItem(e.Id, e.Group, NameOf(e), _icons.GetValueOrDefault(e.Id), e.Missing))
                .ToList();
        }
    }

    private static string NameOf(ShortcutEntry e)
    {
        if (e.Kind == ShortcutKind.DefaultBrowser) return L.T("Standard-Browser", "Default browser");
        if (!string.IsNullOrWhiteSpace(e.DisplayName)) return e.DisplayName;
        var file = ShortcutPaths.FileName(e.Target);
        return file.Length > 0 ? file : e.Id;
    }

    private ShortcutEntry? Find(string id)
        => Section.Items.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- scanning

    /// <summary>
    /// Looks at this PC again (Start menu, registry, browsers) on a background thread and merges the result into the list:
    /// new launchers are appended, vanished ones are greyed out (Missing), icons are read. With <paramref name="pruneMissing"/>
    /// (the editor's "Neu suchen") launchers and browsers that are still missing are removed; games never are.
    /// One scan runs at a time: asking again while one runs joins it (a prune request is queued behind it, never lost).
    /// A failing scan is logged and leaves the list as it was.
    /// </summary>
    public Task RescanAsync(bool pruneMissing = false)
    {
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            var running = _scan;
            if (running is { IsCompleted: false })
            {
                if (!pruneMissing) return running;
                return _scan = running.ContinueWith(_ => ScanCore(true), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
            return _scan = Task.Run(() => ScanCore(pruneMissing));
        }
    }

    private void ScanCore(bool prune)
    {
        try
        {
            if (_disposed) return;

            // The slow part - disk, registry, shell - runs without any lock.
            var links = _shell.StartMenuAndDesktopLinks();
            var launchers = LauncherCatalog.Detect(links, _reg, _fs, _shell.StoreAppInstalled);
            var browsers = BrowserCatalog.Detect(_reg, _fs);
            var windowsDefault = _shell.WindowsDefaultBrowserExe();

            List<ShortcutEntry> snapshot;
            string? chosen;
            lock (_gate)
            {
                snapshot = Section.Items.ToList();
                chosen = Section.DefaultBrowserId;
            }
            var defaultExe = BrowserCatalog.ResolveDefault(chosen, browsers, windowsDefault, out _);
            var icons = ResolveIcons(Merge(snapshot, launchers, browsers, defaultExe, prune));

            // The user may have hidden, moved or added something while this ran: merge into what is there NOW.
            bool changed;
            lock (_gate)
            {
                if (_disposed) return;
                var current = Section.Items;
                var merged = Merge(current, launchers, browsers, defaultExe, prune);
                changed = JsonSerializer.Serialize(current) != JsonSerializer.Serialize(merged);
                if (changed) Section.Items = merged;

                var kept = new Dictionary<string, string?>(_icons, StringComparer.OrdinalIgnoreCase);
                foreach (var (id, png) in icons)
                    if (png != null) kept[id] = png;   // an icon that could not be read this time keeps its older one
                var ids = Section.Items.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var gone in kept.Keys.Where(k => !ids.Contains(k)).ToList()) kept.Remove(gone);
                _icons = kept;

                _browsers = browsers;
                _lastScanUtc = DateTime.UtcNow;
            }

            if (changed) _settings.Save();
            RaiseItemsChanged();
        }
        catch (Exception ex)
        {
            Logger.Warn("Shortcut scan failed: " + ex.Message);
        }
    }

    private static List<ShortcutEntry> Merge(IReadOnlyList<ShortcutEntry> saved, IReadOnlyList<CatalogHit> launchers,
        IReadOnlyList<BrowserInfo> browsers, string? defaultBrowserExe, bool prune)
    {
        var result = ShortcutReconciler.Reconcile(saved, launchers, browsers);
        if (prune) result.RemoveAll(e => e.Missing && e.Group != ShortcutGroup.Game);
        var def = result.FirstOrDefault(e => e.Kind == ShortcutKind.DefaultBrowser);
        if (def != null) def.IconSource = defaultBrowserExe;
        return result;
    }

    private Dictionary<string, string?> ResolveIcons(IReadOnlyList<ShortcutEntry> entries)
    {
        var icons = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (_disposed) break;
            icons[e.Id] = TryIcon(IconSourceOf(e), e.Id);
        }
        return icons;
    }

    private string? TryIcon(string? source, string id)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        try { return _shell.IconPng(source, id); }
        catch (Exception ex)
        {
            Logger.Warn($"Icon for '{id}' failed: {ex.Message}");
            return null;
        }
    }

    private static string? IconSourceOf(ShortcutEntry e)
    {
        if (!string.IsNullOrWhiteSpace(e.IconSource)) return e.IconSource;
        return e.Kind switch
        {
            ShortcutKind.StoreApp => @"shell:AppsFolder\" + e.Target,
            ShortcutKind.Exe or ShortcutKind.ShellLink or ShortcutKind.CatalogLauncher or ShortcutKind.Browser => e.Target,
            _ => null,
        };
    }

    // ---------------------------------------------------------------- starting

    /// <summary>
    /// Starts an entry like the Start menu would. The board closes first (nothing the board shows is wanted over a starting
    /// program; a no-op when it is not open), then the shell starts it. A target that is gone is not started: the entry turns
    /// grey, a notice says so, and the board stays open so the grey entry is seen. Never throws. True when the shell started it.
    /// </summary>
    public bool Launch(string id)
    {
        ShortcutEntry? entry;
        lock (_gate) entry = _disposed ? null : Find(id);
        if (entry == null) return false;
        var name = NameOf(entry);

        var target = ResolveTarget(entry);
        if (target == null) return false;   // already explained to the user

        if (!Present(target))
        {
            SetMissing(entry);
            Toast(L.T($"\"{name}\" wurde nicht gefunden - vielleicht deinstalliert. Der Eintrag ist ausgegraut; \"Neu suchen\" im Bearbeiten-Dialog entfernt ihn.",
                      $"\"{name}\" was not found - maybe it was uninstalled. The entry is greyed out; \"Rescan\" in the edit dialog removes it."));
            return false;
        }
        if (entry.Missing)
        {
            entry.Missing = false;   // it is back
            Commit();
        }

        try { _closeBoard(); }
        catch (Exception ex) { Logger.Warn("Closing the board for a shortcut start failed: " + ex.Message); }

        bool started;
        string? error;
        try { started = _shell.Start(target, out error); }
        catch (Exception ex) { started = false; error = ex.Message; }

        if (started) return true;

        Toast(L.T($"\"{name}\" konnte nicht gestartet werden: {error}", $"Could not start \"{name}\": {error}"));
        if (!Present(target)) SetMissing(entry);   // it vanished between the check and the start
        return false;
    }

    /// <summary>The start request for an entry, or null (after telling the user) when there is nothing to start.</summary>
    private LaunchTarget? ResolveTarget(ShortcutEntry e)
    {
        switch (e.Kind)
        {
            case ShortcutKind.DefaultBrowser:
                return ResolveDefaultBrowser();
            case ShortcutKind.Browser:
                return new LaunchTarget(ShortcutKind.Browser, e.Target, null);
            case ShortcutKind.CatalogLauncher:
                // A Start-menu shortcut is started itself (it carries its own arguments and folder); a registry exe starts bare.
                return e.Target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || e.Target.EndsWith(".url", StringComparison.OrdinalIgnoreCase)
                    ? new LaunchTarget(ShortcutKind.ShellLink, e.Target, null)
                    : new LaunchTarget(ShortcutKind.Exe, e.Target, e.Arguments);
            default:
                return new LaunchTarget(e.Kind, e.Target, e.Arguments);
        }
    }

    /// <summary>The browser behind the standard-browser button, decided at click time: the chosen one if installed, else the Windows default - never Internet Explorer.</summary>
    private LaunchTarget? ResolveDefaultBrowser()
    {
        string? chosen;
        lock (_gate) chosen = Section.DefaultBrowserId;

        var exe = BrowserCatalog.ResolveDefault(chosen, BrowserCatalog.Detect(_reg, _fs), _shell.WindowsDefaultBrowserExe(), out bool fellBack);
        if (fellBack && exe != null)
            Toast(L.T("Der gewählte Standard-Browser ist nicht installiert - es wird der Windows-Standard genutzt.",
                      "The chosen default browser is not installed - the Windows default is used instead."));
        if (exe == null)
        {
            Toast(L.T("Kein Standard-Browser gefunden. Im Bearbeiten-Dialog (Zahnrad) einen Browser als Standard wählen.",
                      "No default browser found. Choose one in the edit dialog (gear)."));
            return null;
        }
        return new LaunchTarget(ShortcutKind.DefaultBrowser, exe, null);
    }

    /// <summary>Files must exist; a Store app must still be installed. Steam and Epic games are the launcher's business.</summary>
    private bool Present(LaunchTarget t) => t.Kind switch
    {
        ShortcutKind.SteamApp or ShortcutKind.EpicApp => true,
        ShortcutKind.StoreApp => _shell.StoreAppInstalled(t.Target),
        _ => _fs.FileExists(t.Target),
    };

    private void SetMissing(ShortcutEntry entry)
    {
        bool was = entry.Missing;
        entry.Missing = true;
        if (!was) Commit(); else RaiseItemsChanged();
    }

    private void Toast(string message)
    {
        try { _toast(message); }
        catch (Exception ex) { Logger.Warn("Shortcut toast failed: " + ex.Message); }
    }

    // ---------------------------------------------------------------- editing

    /// <summary>"game." plus a hash of what is started - the same program (or the same shortcut with other arguments) is always the same entry.</summary>
    public static string GameEntryId(LaunchTarget t)
    {
        bool isPath = t.Kind is ShortcutKind.Exe or ShortcutKind.ShellLink or ShortcutKind.CatalogLauncher or ShortcutKind.Browser or ShortcutKind.DefaultBrowser;
        var key = isPath ? ShortcutPaths.Normalize(t.Target).ToLowerInvariant() : (t.Target ?? "").Trim();
        var raw = $"{t.Kind}|{key}|{(t.Arguments ?? "").Trim()}";
        return "game." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)), 0, 8).ToLowerInvariant();
    }

    /// <summary>
    /// Adds a game at the end of the games. A second add of the same target keeps the first entry. <paramref name="iconSource"/> is
    /// the exe/shortcut the icon is read from (null: the target itself where that makes sense).
    /// </summary>
    public void AddGame(LaunchTarget t, string name, string? iconSource)
    {
        if (t.Kind is not (ShortcutKind.SteamApp or ShortcutKind.EpicApp or ShortcutKind.ShellLink or ShortcutKind.Exe or ShortcutKind.StoreApp)
            || string.IsNullOrWhiteSpace(t.Target)) return;

        var id = GameEntryId(t);
        lock (_gate) if (Find(id) != null) return;

        var entry = new ShortcutEntry
        {
            Id = id,
            Group = ShortcutGroup.Game,
            Kind = t.Kind,
            Target = t.Target.Trim(),
            Arguments = string.IsNullOrWhiteSpace(t.Arguments) ? null : t.Arguments.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(name) ? ShortcutPaths.FileName(t.Target) : name.Trim(),
            IconSource = string.IsNullOrWhiteSpace(iconSource) ? null : iconSource,
        };
        var icon = TryIcon(IconSourceOf(entry), id);   // one icon, read before the lock is taken

        lock (_gate)
        {
            if (Find(id) != null) return;   // added by another caller in the meantime
            var list = Section.Items.ToList();
            list.Insert(list.FindLastIndex(e => e.Group == ShortcutGroup.Game) + 1, entry);
            Section.Items = list;
            if (icon != null) _icons[id] = icon;
        }
        Commit();
    }

    /// <summary>Removes a game or a browser the user picked. Launchers and the standard browser cannot be removed (hide them instead).</summary>
    public void Remove(string id)
    {
        lock (_gate)
        {
            var entry = Find(id);
            if (entry == null || !(entry.Group == ShortcutGroup.Game || (entry.Group == ShortcutGroup.Browser && entry.Kind == ShortcutKind.Browser))) return;
            Section.Items = Section.Items.Where(e => !ReferenceEquals(e, entry)).ToList();
            _icons.Remove(entry.Id);
        }
        Commit();
    }

    public void SetHidden(string id, bool hidden)
    {
        lock (_gate)
        {
            var entry = Find(id);
            if (entry == null || entry.Hidden == hidden) return;
            entry.Hidden = hidden;
        }
        Commit();
    }

    /// <summary>
    /// Moves an entry <paramref name="delta"/> places inside its group (negative = earlier), stopping at the ends. Entries never
    /// change group, and nothing goes in front of the standard browser, which stays first among the browsers.
    /// </summary>
    public void Move(string id, int delta)
    {
        if (delta == 0) return;
        lock (_gate)
        {
            var list = Section.Items.ToList();
            int index = list.FindIndex(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return;
            var entry = list[index];
            if (entry.Kind == ShortcutKind.DefaultBrowser) return;

            var slots = Enumerable.Range(0, list.Count).Where(i => list[i].Group == entry.Group).ToList();
            int pos = slots.IndexOf(index);
            int first = entry.Group == ShortcutGroup.Browser && list[slots[0]].Kind == ShortcutKind.DefaultBrowser ? 1 : 0;
            int to = Math.Clamp(pos + delta, first, slots.Count - 1);
            if (to == pos) return;

            var group = slots.Select(i => list[i]).ToList();
            group.RemoveAt(pos);
            group.Insert(to, entry);
            for (int k = 0; k < slots.Count; k++) list[slots[k]] = group[k];
            Section.Items = list;
        }
        Commit();
    }

    // ---------------------------------------------------------------- recent games

    /// <summary>
    /// The game detector saw a game in front: remember its exe as a suggestion for the edit dialog. Launchers, browsers and the
    /// clipper itself never count (<see cref="ShortcutReconciler.ExclusionFilter"/>). Safe from any thread.
    /// </summary>
    public void RememberGame(string exePath, string name)
    {
        IReadOnlyList<BrowserInfo> browsers;
        lock (_gate) browsers = _browsers;
        var excluded = ShortcutReconciler.ExclusionFilter(LauncherCatalog.LauncherExeNames, browsers, Environment.ProcessPath);

        lock (_gate)
        {
            var before = Section.RecentGames;
            var copy = new ShortcutSettings { RecentGames = before.ToList() };   // the shared list is never modified in place
            ShortcutReconciler.RememberGame(copy, exePath, name, DateTime.UtcNow, excluded);
            if (copy.RecentGames.Count == before.Count && ReferenceEquals(copy.RecentGames.FirstOrDefault(), before.FirstOrDefault())) return;   // excluded
            Section.RecentGames = copy.RecentGames;
        }
        _settings.Save();
    }

    // ---------------------------------------------------------------- plumbing

    private void Commit()
    {
        _settings.Save();
        RaiseItemsChanged();
    }

    private void RaiseItemsChanged()
    {
        if (_disposed) return;
        try { ItemsChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Logger.Warn("ItemsChanged handler threw: " + ex.Message); }
    }

    public void Dispose() => _disposed = true;
}
