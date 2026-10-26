using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>One line of the edit dialog's lists.</summary>
public sealed record EditorRow(string Id, ShortcutGroup Group, string Name, bool Hidden, bool Missing, bool CanRemove,
    bool CanMoveUp, bool CanMoveDown, HotkeyBinding? Hotkey, string? IconPng, bool IsDefaultBrowser);

/// <summary>A game the user can add: where it comes from decides how it starts (<see cref="Target"/>). <see cref="AlreadyAdded"/> = it is on the bar.</summary>
public sealed record GameChoice(string Name, LaunchTarget Target, string? IconSource, bool AlreadyAdded);

/// <summary>An entry of the standard-browser selector. <see cref="Id"/> null = "whatever Windows has as default".</summary>
public sealed record BrowserOption(string? Id, string Label);

/// <summary>
/// What picking a browser led to: it is on the bar (<see cref="Added"/>), or it is not installed (<see cref="Missing"/> is set, nothing was
/// added) and the dialog should offer the download page, one of the installed <see cref="Alternatives"/>, or cancel.
/// </summary>
public sealed record BrowserPick(bool Added, BrowserInfo? Missing, IReadOnlyList<BrowserInfo> Alternatives);

public enum AddFileResult { Added, NotFound, Unsupported, Uninstaller }

/// <summary>
/// Everything the edit dialog decides, without a window: the rows of the three lists, the places games come from (suggestions,
/// Steam, Epic, a picked file - all resolved to the right start path by <see cref="GameLaunchResolver"/>), and the browser picks.
/// Reads go through the injected file-system, registry and shell views, changes through <see cref="ShortcutService"/>; the slow
/// reads (libraries, the Start-menu scan) belong on a background thread, which is the window's business.
/// </summary>
public sealed class ShortcutEditorModel
{
    // Files in a game folder that are not the game: for guessing which exe's icon stands for a Steam game.
    private static readonly string[] NotTheGame = { "unins", "uninstall", "setup", "redist", "crash", "diag", "_be", "dxsetup", "vcredist", "dotnet" };

    private readonly ShortcutService _service;
    private readonly IShortcutShell _shell;
    private readonly IFileSystemView _fs;
    private readonly IRegistryView _reg;
    private readonly string _steamRoot;
    private readonly string _epicManifests;
    private readonly Func<string, string?> _productNameOf;

    /// <param name="steamRoot">The Steam folder (libraryfolders.vdf lives in its steamapps).</param>
    /// <param name="epicManifests">The Epic Games Launcher's manifests folder (*.item).</param>
    /// <param name="productNameOf">The product name of an exe (its version resource), or null - the name a picked exe gets.</param>
    public ShortcutEditorModel(ShortcutService service, IShortcutShell shell, IFileSystemView fs, IRegistryView reg,
        string steamRoot, string epicManifests, Func<string, string?> productNameOf)
    {
        _service = service;
        _shell = shell;
        _fs = fs;
        _reg = reg;
        _steamRoot = steamRoot;
        _epicManifests = epicManifests;
        _productNameOf = productNameOf;
    }

    /// <summary>The model for this PC: Steam where its registry key says (else the default folder), Epic's standard manifests folder.</summary>
    public static ShortcutEditorModel ForThisPc(ShortcutService service, IShortcutShell shell)
    {
        var fs = new DiskFileSystemView();
        var reg = new WindowsRegistryView();
        var steam = reg.GetString("HKCU", @"Software\Valve\Steam", "SteamPath") ?? @"C:\Program Files (x86)\Steam";
        var epic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Epic\EpicGamesLauncher\Data\Manifests");
        return new ShortcutEditorModel(service, shell, fs, reg, steam, epic, ProductNameOf);
    }

    private static string? ProductNameOf(string exePath)
    {
        try
        {
            var name = FileVersionInfo.GetVersionInfo(exePath).ProductName;
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch (Exception) { return null; }
    }

    // ---------------------------------------------------------------- the three lists

    /// <summary>The rows of one list, in the saved order. Hidden and greyed-out entries are rows, too.</summary>
    public IReadOnlyList<EditorRow> Rows(ShortcutGroup group)
    {
        var inGroup = _service.Entries().Where(e => e.Group == group).ToList();
        // Nothing passes the standard browser: the first entry that can move is the one after it.
        int firstMovable = group == ShortcutGroup.Browser && inGroup.Count > 0 && inGroup[0].Kind == ShortcutKind.DefaultBrowser ? 1 : 0;

        var rows = new List<EditorRow>(inGroup.Count);
        for (int i = 0; i < inGroup.Count; i++)
        {
            var e = inGroup[i];
            bool isDefault = e.Kind == ShortcutKind.DefaultBrowser;
            rows.Add(new EditorRow(
                e.Id, e.Group, RowName(e), e.Hidden, e.Missing,
                CanRemove: e.Group == ShortcutGroup.Game || e.Kind == ShortcutKind.Browser,
                CanMoveUp: !isDefault && i > firstMovable,
                CanMoveDown: !isDefault && i < inGroup.Count - 1,
                e.Hotkey, _service.IconOf(e.Id), isDefault));
        }
        return rows;
    }

    private static string RowName(ShortcutEntry e) => ShortcutReconciler.DisplayNameOf(e);

    public void Move(string id, int delta) => _service.Move(id, delta);
    public void SetHidden(string id, bool hidden) => _service.SetHidden(id, hidden);
    public void Remove(string id) => _service.Remove(id);
    public void SetHotkey(string id, HotkeyBinding? hotkey) => _service.SetHotkey(id, hotkey);

    /// <summary>The owner of a combo that is taken (the clipper, a stream-deck tile, another shortcut), or null when it is free for this entry.</summary>
    public string? HotkeyCollision(string id, HotkeyBinding candidate) => _service.HotkeyCollision(id, candidate);

    // ---------------------------------------------------------------- games

    /// <summary>
    /// What the clipper has seen in front, newest first, resolved to the way each game starts (Steam, Epic, the vendor's shortcut, the
    /// exe). Launchers, browsers and the clipper itself are left out, and so are exes that are gone from the disk.
    /// </summary>
    public IReadOnlyList<GameChoice> Suggestions()
    {
        var recent = _service.RecentGames();
        if (recent.Count == 0) return Array.Empty<GameChoice>();

        var ctx = LoadContext();
        var excluded = ShortcutReconciler.ExclusionFilter(LauncherCatalog.LauncherExeNames, _service.DetectBrowsers(), Environment.ProcessPath);
        var onBar = OnBar();

        var choices = new List<GameChoice>();
        foreach (var seen in recent)
        {
            if (excluded(seen.ExePath) || !_fs.FileExists(seen.ExePath)) continue;
            var target = Resolve(seen.ExePath, ctx);
            choices.Add(new GameChoice(seen.DisplayName, target, seen.ExePath, onBar.Contains(ShortcutService.GameEntryId(target))));
        }
        return choices;
    }

    /// <summary>Every Steam app of every library, by name - the Steamworks redistributables are not games and never listed.</summary>
    public IReadOnlyList<GameChoice> SteamGames()
    {
        var onBar = OnBar();
        return GameLibraries.SteamApps(GameLibraries.SteamLibraries(_steamRoot, _fs), _fs)
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a =>
            {
                var target = new LaunchTarget(ShortcutKind.SteamApp, a.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture), null);
                var folder = ShortcutPaths.Combine(a.Library, "steamapps", "common", a.InstallDir);
                return new GameChoice(a.Name, target, GuessMainExe(folder, a.Name), onBar.Contains(ShortcutService.GameEntryId(target)));
            })
            .ToList();
    }

    /// <summary>The games of the Epic Games Launcher, by name; the logo is the launch exe's.</summary>
    public IReadOnlyList<GameChoice> EpicGames()
    {
        var onBar = OnBar();
        return GameLibraries.EpicApps(_epicManifests, _fs)
            .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(a =>
            {
                var target = new LaunchTarget(ShortcutKind.EpicApp, a.AppName, null);
                string? icon = null;
                if (!string.IsNullOrWhiteSpace(a.LaunchExecutable))
                {
                    var exe = ShortcutPaths.Combine(a.InstallLocation, a.LaunchExecutable);
                    if (_fs.FileExists(exe)) icon = exe;
                }
                return new GameChoice(a.DisplayName, target, icon, onBar.Contains(ShortcutService.GameEntryId(target)));
            })
            .ToList();
    }

    public void AddGame(GameChoice choice) => _service.AddGame(choice.Target, choice.Name, choice.IconSource);

    /// <summary>
    /// A file the user picked: a shortcut (.lnk/.url) starts itself, an exe goes through the same rules as a suggestion (Steam, Epic, the
    /// vendor's shortcut, else the exe). An uninstall shortcut is refused - it would be a game that removes itself when clicked.
    /// </summary>
    public AddFileResult AddGameFromFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return AddFileResult.NotFound;
        var ext = Path.GetExtension(path);
        bool isLink = ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase);
        bool isExe = ext.Equals(".exe", StringComparison.OrdinalIgnoreCase);
        if (!isLink && !isExe) return AddFileResult.Unsupported;
        if (!_fs.FileExists(path)) return AddFileResult.NotFound;

        var stem = Path.GetFileNameWithoutExtension(path);
        var info = isLink ? _shell.ReadLink(path) ?? new ShellLinkInfo(path, stem, null, null, null, null)
                          : new ShellLinkInfo(path, stem, path, null, null, null);
        if (GameLaunchResolver.IsUninstallShortcut(info)) return AddFileResult.Uninstaller;

        if (isLink)
        {
            _service.AddGame(new LaunchTarget(ShortcutKind.ShellLink, path, null), stem, path);
        }
        else
        {
            var target = Resolve(path, LoadContext());
            _service.AddGame(target, _productNameOf(path) ?? stem, path);
        }
        return AddFileResult.Added;
    }

    private HashSet<string> OnBar()
        => _service.Entries().Where(e => e.Group == ShortcutGroup.Game).Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private sealed record Context(IReadOnlyList<SteamApp> Steam, IReadOnlyList<EpicApp> Epic, IReadOnlyList<ShellLinkInfo> Links);

    /// <summary>The libraries and the Start-menu shortcuts a resolution needs - read once per request.</summary>
    private Context LoadContext() => new(
        GameLibraries.SteamApps(GameLibraries.SteamLibraries(_steamRoot, _fs), _fs),
        GameLibraries.EpicApps(_epicManifests, _fs),
        _shell.StartMenuAndDesktopLinks());

    private static LaunchTarget Resolve(string exePath, Context c)
        => GameLaunchResolver.Resolve(exePath, c.Steam, c.Epic, c.Links, LauncherCatalog.LauncherExeNames);

    /// <summary>
    /// A Steam game's manifest names no exe. The icon is a nicety, so a guess does: the exe in the game's folder that looks most like
    /// the game (its name contains the game's, shortest first), never a crash reporter, setup, BattlEye or diagnostic build.
    /// Null when there is none - the bar then draws a letter tile.
    /// </summary>
    private string? GuessMainExe(string folder, string gameName)
    {
        var key = ShortcutPaths.Fold(gameName);
        var candidates = _fs.EnumerateFiles(folder, "*.exe")
            .Where(f => !NotTheGame.Any(j => Path.GetFileNameWithoutExtension(f).Contains(j, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (candidates.Count == 0) return null;

        return candidates
            .OrderByDescending(f =>
            {
                var stem = ShortcutPaths.Fold(Path.GetFileNameWithoutExtension(f));
                return key.Length >= 3 && (stem.Contains(key, StringComparison.Ordinal) || key.Contains(stem, StringComparison.Ordinal)) ? 1 : 0;
            })
            .ThenBy(f => Path.GetFileNameWithoutExtension(f).Length)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    // ---------------------------------------------------------------- browsers

    /// <summary>The standard-browser selector: the Windows default first (naming it), then every installed browser.</summary>
    public IReadOnlyList<BrowserOption> DefaultBrowserOptions()
    {
        var browsers = _service.DetectBrowsers();
        var windowsExe = _service.WindowsDefaultBrowserExe();
        string who = "";
        if (!string.IsNullOrWhiteSpace(windowsExe))
        {
            var match = browsers.FirstOrDefault(b => ShortcutPaths.SamePath(b.ExePath, windowsExe));
            who = match?.Name ?? Path.GetFileNameWithoutExtension(windowsExe);
        }

        var options = new List<BrowserOption>
        {
            new(null, who.Length > 0
                ? L.T($"Windows-Standard ({who})", $"Windows default ({who})")
                : L.T("Windows-Standard", "Windows default")),
        };
        options.AddRange(browsers.Where(b => b.Installed).Select(b => new BrowserOption(b.Id, b.Name)));
        return options;
    }

    public string? SelectedDefaultBrowserId => _service.DefaultBrowserId;

    public void SetDefaultBrowser(string? browserId) => _service.SetDefaultBrowser(browserId);

    /// <summary>The browsers that can be put on the bar: the four named candidates and whatever else is registered, minus those already there. Not-installed ones are listed - picking one explains.</summary>
    public IReadOnlyList<BrowserInfo> AddableBrowsers()
    {
        var onBar = _service.Entries().Where(e => e.Kind == ShortcutKind.Browser).Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _service.DetectBrowsers().Where(b => !onBar.Contains(ShortcutReconciler.BrowserEntryId(b.Id))).ToList();
    }

    /// <summary>
    /// The user picked a browser to add. Installed: it goes on the bar. Not installed: nothing is added and the result says which browser
    /// is missing and which installed ones (not yet on the bar) could be taken instead - for the dialog that offers the download page.
    /// </summary>
    public BrowserPick PickBrowser(BrowserInfo browser)
    {
        if (_service.AddBrowser(browser)) return new BrowserPick(true, null, Array.Empty<BrowserInfo>());

        var alternatives = AddableBrowsers()
            .Where(b => b.Installed && !string.Equals(b.Id, browser.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return new BrowserPick(false, browser, alternatives);
    }

    /// <summary>The "use another browser" answer of the missing-browser dialog.</summary>
    public bool AddBrowser(BrowserInfo browser) => _service.AddBrowser(browser);
}
