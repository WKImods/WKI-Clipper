# Verknüpfungsleiste Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Game-Bar-style shortcut bar at the top of the board. It starts games, launchers and browsers exactly as the Start menu would.

**Architecture:**
- **Pure logic, unit-tested:** VDF, Steam and Epic libraries, launch-target resolution, catalog matching, reconciling a scan into the item list, recent games, obstacle maths, `App:` hotkey ids. It sits behind small filesystem and shell interfaces.
- **Native layer, thin:** resolving .lnk/.url files, icons, ShellExecute, the default browser.
- **`ShortcutService`:** orchestrates the above.
- **`ShortcutBarWindow`:** a board window that is shown and hidden by `WidgetHost`.

**Tech Stack:** .NET 8 WPF, Win32/COM interop (shell32, shlwapi), xunit 2.9.2.

**Spec:** `docs/superpowers/specs/2026-10-20-shortcut-bar-design.md`. Read it together with this plan.

## Global Constraints
- **Repo:** `C:\Users\cr4zy\Documents\CLAUDE_Projects\WKI_Clipper`, branch `master`. Use the PowerShell tool only; the Bash tool is broken.
- **Text and naming:** all UI text goes through `L.T("Deutsch","English")`. Code and comments are in English, in the surrounding style.
- **Starting programs:**
  - Always `UseShellExecute`. Never `runas` and nothing elevated.
  - Call `AllowSetForegroundWindow(ASFW_ANY)` before starting.
  - Never touch other programs' windows: no `SetForegroundWindow`/`SetWindowPos` on them.
- **Never:**
  - simulated input
  - downloads or installs
  - Internet Explorer as a browser
  - assuming Edge is installed
- **Scans:** run only on board open, from "Neu suchen" and from the edit dialog. They run in the background and are cached; nothing polls while a game is in front.
- **Dependencies:** no new NuGet packages. COM interop is declared by hand.
- **Tests:**
  - File `WKI_Clipper.Tests/<Topic>Tests.cs`, namespace `WKI_Clipper.Tests`, underscore-sentence names.
  - Live tests are gated with `WKI_LIVE=1`.
  - STA render tests stay offscreen and the window is never shown.
- **After every task:** the full suite is green with 0 build warnings.
- **Commits:**
  - Write the message to a file without a BOM and use `git commit -F`.
  - The last line is exactly `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
  - Stage only your own files.
- **Before Task 9:** no version bump, no `build.ps1`, no install. No visible windows and no input injection on Phillip's desktop.
- **Pharao roles:**
  - Tasks 1–8 are Sonnet, one after another.
  - The Task 9 review is Opus (one reviewer).
  - Fixes are Sonnet.
  - Keep the usage limit in mind at all times.

## Review Focus
1. **The WoT folder holds both "… deinstallieren.lnk" (`wgc_api.exe --uninstall`) and the start shortcut.** The uninstall shortcut must never become the start path. Covered by Task 2, `Uninstall_shortcut_is_never_a_launch_target`.
2. **A Steam game on a second library with different casing or trailing slashes** (`d:\steamlibrary\` vs `D:\SteamLibrary`) must still resolve to its appid. Covered by Task 2, `Steam_game_on_second_library_with_different_case_resolves_appid`.
3. **A program uninstalled after it was added** must, when clicked, show a toast and grey out the entry; it must never crash. Covered by Task 5, `Missing_target_launch_reports_and_greys`.
4. **A widget already sitting under the bar** is pushed down once and the result is saved; it must not jump on every open. Covered by Task 6, `Widget_under_bar_moves_once_and_stays`.
5. **The clipper's own exe, a launcher or a browser detected as the "game"** must never land in the recent games. Covered by Task 3, `RecentGames_skips_launchers_browsers_and_self`.

---

## File Structure
| File | Responsibility |
|---|---|
| `Models/ShortcutSettings.cs` (new) | `ShortcutSettings`, `ShortcutEntry`, `SeenGame`, enums |
| `Models/AppSettings.cs`, `Services/SettingsService.cs` | `Shortcuts` section, schema v10 |
| `Services/Shortcuts/Vdf.cs` (new) | minimal KeyValues (VDF/ACF) parser |
| `Services/Shortcuts/GameLibraries.cs` (new) | Steam and Epic library readers |
| `Services/Shortcuts/GameLaunchResolver.cs` (new) | exe → best launch target |
| `Services/Shortcuts/LauncherCatalog.cs` (new) | catalog data + Start-menu matching |
| `Services/Shortcuts/BrowserCatalog.cs` (new) | browser candidates, detection, default |
| `Services/Shortcuts/ShortcutReconciler.cs` (new) | scan result + saved items → item list; recent games |
| `Services/Shortcuts/ShortcutNative.cs` (new) | `IShortcutShell` implementation (lnk/url, Start-menu scan, icons, ShellExecute, assoc) |
| `Services/Shortcuts/ShortcutService.cs` (new) | orchestration |
| `Services/Shortcuts/BarLayout.cs` (new) | bar rect + widget obstacle maths |
| `Views/ShortcutBarWindow.cs` (new), `Views/ShortcutEditorWindow.cs` (new), `Views/BrowserMissingDialog.cs` (new) | UI |
| `Views/WidgetHost.cs`, `AppHost.cs`, `App.xaml.cs`, `Services/HotkeyService.cs` | wiring |

### Shared interfaces (defined in Task 2, extended in Task 3)
```csharp
public interface IFileSystemView { bool FileExists(string p); bool DirExists(string p); string? ReadAllText(string p);
                                   IEnumerable<string> EnumerateFiles(string dir, string pattern); }
public sealed record ShellLinkInfo(string Path, string Name, string? Target, string? Arguments, string? WorkingDir, string? Url);
public interface IRegistryView { string? GetString(string hive, string key, string? value); IReadOnlyList<string> SubKeys(string hive, string key); }
```

---

### Task 1: Settings model and schema v10
**Files:**
- Create `Models/ShortcutSettings.cs`.
- Modify `Models/AppSettings.cs` (add the property `public ShortcutSettings Shortcuts { get; set; } = new();`) and `Services/SettingsService.cs`.
- Test: `ShortcutSettingsTests.cs`.

**Produces:**
```csharp
public enum ShortcutGroup { Game, Launcher, Browser }
public enum ShortcutKind { CatalogLauncher, StoreApp, Browser, DefaultBrowser, SteamApp, EpicApp, ShellLink, Exe }
public sealed class ShortcutEntry { public string Id { get; set; } = ""; public ShortcutGroup Group { get; set; } public ShortcutKind Kind { get; set; }
  public string Target { get; set; } = "";   // exe/lnk/url path, appid, Epic AppName, AUMID, catalog/browser id
  public string? Arguments { get; set; } public string DisplayName { get; set; } = ""; public string? IconSource { get; set; }
  public bool Hidden { get; set; } public bool Missing { get; set; } public HotkeyBinding? Hotkey { get; set; } }
public sealed class SeenGame { public string ExePath { get; set; } = ""; public string DisplayName { get; set; } = ""; public DateTime LastSeenUtc { get; set; } }
public sealed class ShortcutSettings { public bool Expanded { get; set; } = true; public List<ShortcutEntry> Items { get; set; } = new();
  public string? DefaultBrowserId { get; set; } public List<SeenGame> RecentGames { get; set; } = new(); }
```
- [ ] **Step 1: Write the failing tests.**
  - `V9_settings_migrate_to_v10_with_expanded_empty_shortcuts`: `SchemaVersion == 10`, `Expanded == true`, `Items` is empty, `RecentGames` is empty.
  - `Null_shortcuts_section_and_lists_are_guarded`: `"Shortcuts": null` and `"Items": null` both give non-null values.
  - `Shortcuts_round_trip`: an entry with a hotkey and a `SeenGame` survive save and load.
- [ ] **Step 2:** Run `dotnet test WKI_Clipper.Tests --filter ShortcutSettingsTests`. Expected: the tests fail.
- [ ] **Step 3:** Set `CurrentSchemaVersion = 10` and add an `if (s.SchemaVersion < 10)` block that needs nothing beyond the defaults. In `EnsureSections`: `s.Shortcuts ??= new(); s.Shortcuts.Items ??= new(); s.Shortcuts.RecentGames ??= new();`.
- [ ] **Step 4:** Full suite green. Commit `feat(shortcuts): settings model and schema v10`.

### Task 2: VDF, libraries, launch-target resolution
**Files:**
- Create `Services/Shortcuts/Vdf.cs`, `GameLibraries.cs` and `GameLaunchResolver.cs`, plus the shared interfaces above (`Services/Shortcuts/ShortcutAbstractions.cs`).
- Tests: `VdfTests.cs`, `GameLibrariesTests.cs`, `GameLaunchResolverTests.cs`, with fake `IFileSystemView` fixtures that copy the spec §3 facts.

**Produces:**
```csharp
public static class Vdf { public static Dictionary<string, object> Parse(string text); }   // nested string → string | dict; case-insensitive keys
public sealed record SteamApp(int AppId, string Name, string InstallDir, string Library);
public sealed record EpicApp(string AppName, string DisplayName, string InstallLocation, string? LaunchExecutable);
public static class GameLibraries {
  public static IReadOnlyList<string> SteamLibraries(string steamRoot, IFileSystemView fs);  // root + libraryfolders.vdf "path"s, deduped case-insensitively
  public static IReadOnlyList<SteamApp> SteamApps(IReadOnlyList<string> libraries, IFileSystemView fs);   // all appmanifest_*.acf except appid 228980
  public static IReadOnlyList<EpicApp> EpicApps(string manifestsDir, IFileSystemView fs);   // *.item JSON; bad files skipped
}
public sealed record LaunchTarget(ShortcutKind Kind, string Target, string? Arguments);
public static class GameLaunchResolver {
  public static LaunchTarget Resolve(string exePath, IReadOnlyList<SteamApp> steam, IReadOnlyList<EpicApp> epic,
                                     IReadOnlyList<ShellLinkInfo> shortcuts, IReadOnlySet<string> launcherExeNames);
  public static bool IsUninstallShortcut(ShellLinkInfo link);   // name/args/target contain "uninstall", "deinstall", "unins"
}
```
**Resolve order:**
1. The exe lies under `<lib>\steamapps\common\<installdir>\`: return `SteamApp`, Target = appid.
2. The exe lies under an Epic `InstallLocation`: return `EpicApp`, Target = AppName.
3. A shortcut whose target lies in the exe's game folder: return `ShellLink`, Target = the .lnk path.
   - The game folder is the exe directory, or its first ancestor that still contains the shortcut target.
   - Skip uninstall shortcuts and shortcuts whose target exe name is in `launcherExeNames`.
4. Otherwise `Exe`.

Path comparisons are case-insensitive and normalize trailing slashes and `/`.

- [ ] **Step 1: Write the failing tests.**
  - `Parses_libraryfolders_with_nested_blocks_and_escaped_backslashes`
  - `Steam_libraries_are_root_plus_vdf_paths_deduped`
  - `Steam_apps_exclude_redistributables_228980`
  - `Epic_manifest_with_bad_json_is_skipped`
  - `Arma_exe_resolves_to_steam_1874880`: `D:\SteamLibrary\steamapps\common\Arma Reforger\ArmaReforgerSteam.exe`.
  - `Steam_game_on_second_library_with_different_case_resolves_appid` (Review Focus 2)
  - `Spiderman_exe_resolves_to_epic_appname`
  - `Wot_exe_resolves_to_wgc_api_open_shortcut`: `C:\Games\World_of_Tanks_EU\WorldOfTanks.exe` → `World of Tanks EU.lnk`.
  - `Uninstall_shortcut_is_never_a_launch_target` (Review Focus 1)
  - `Unknown_exe_falls_back_to_exe`
- [ ] **Step 2:** Run the filter `VdfTests|GameLibrariesTests|GameLaunchResolverTests`. Expected: fail.
- [ ] **Step 3:** Implement the three files.
- [ ] **Step 4:** Green. Commit `feat(shortcuts): game libraries and launch-target resolution`.

### Task 3: Catalogs, reconciling, recent games
**Files:**
- Create `Services/Shortcuts/LauncherCatalog.cs`, `BrowserCatalog.cs` and `ShortcutReconciler.cs`.
- Tests: `LauncherCatalogTests.cs`, `BrowserCatalogTests.cs`, `ShortcutReconcilerTests.cs`.

**Catalog data** (spec §3; every row is matched by the **target file name of a Start-menu shortcut**, with the fallback exe used if no shortcut exists):

| Id | Name | Start-menu target file | Fallback |
|---|---|---|---|
| `steam` | Steam | `steam.exe` | `HKCU\Software\Valve\Steam\SteamExe` |
| `ea` | EA app | `EALauncher.exe` | `HKLM\SOFTWARE\Electronic Arts\EA Desktop\LauncherAppPath` |
| `epic` | Epic Games | `EpicGamesLauncher.exe` | none |
| `gog` | GOG GALAXY | `GalaxyClient.exe` | none |
| `ubisoft` | Ubisoft Connect | `UbisoftConnect.exe` | none |
| `battlenet` | Battle.net | `Battle.net Launcher.exe` | none |
| `rockstar` | Rockstar Games | `LauncherPatcher.exe` | none |
| `wgc` | Wargaming.net Game Center | `wgc.exe` | none |
| `xbox` | Xbox | StoreApp | AUMID `Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App`; installed if the package exists |

- Shortcuts named "…Updater", "Error Reporter" or "deinstall…" never match a catalog row.
- `LauncherExeNames` = all target file names plus `EADesktop.exe`, `Battle.net.exe`, `Launcher.exe`, `wgc_api.exe`.

**Produces:**
```csharp
public sealed record CatalogHit(string Id, string Name, ShortcutKind Kind, string Target, string? IconSource);
public static class LauncherCatalog { public static IReadOnlySet<string> LauncherExeNames { get; }
  public static IReadOnlyList<CatalogHit> Detect(IReadOnlyList<ShellLinkInfo> startMenu, IRegistryView reg, IFileSystemView fs, Func<string, bool> storeAppInstalled); }
public sealed record BrowserInfo(string Id, string Name, string? ExePath, bool Installed, string? DownloadUrl);
public static class BrowserCatalog {   // candidates: opera, chrome, comet, firefox (+ any other StartMenuInternet entry), never IEXPLORE.EXE
  public static IReadOnlyList<BrowserInfo> Detect(IRegistryView reg, IFileSystemView fs);
  public static string? ResolveDefault(string? chosenId, IReadOnlyList<BrowserInfo> browsers, string? windowsDefaultExe, out bool fellBack); }
public static class ShortcutReconciler {
  public static List<ShortcutEntry> Reconcile(IReadOnlyList<ShortcutEntry> saved, IReadOnlyList<CatalogHit> launchers, IReadOnlyList<BrowserInfo> browsers);
  public static void RememberGame(ShortcutSettings s, string exePath, string name, DateTime utcNow, Func<string, bool> isExcluded);   // max 30, newest first
}
```
**Reconcile rules:**
- Saved order and Hidden flags are kept.
- A newly detected launcher is appended to the end of its group, visible.
- A saved launcher that is no longer detected gets `Missing = true` and stays in the list.
- A `DefaultBrowser` entry always exists at the start of the browser group.
- Browsers are only added when the user picks them; they are never added automatically.

- [ ] **Step 1: Write the failing tests.**
  - `Rockstar_matches_launcherpatcher_shortcut`
  - `Ea_matches_ealauncher_not_updater`
  - `Epic_matches_win32_shortcut`
  - `Xbox_store_app_detected_when_package_installed`
  - `Comet_detected_from_hkcu_and_iexplore_ignored`
  - `Chrome_in_hklm_and_wow6432_listed_once`
  - `Edge_without_exe_not_installed`
  - `Opera_not_installed_has_download_url`
  - `Chosen_default_uninstalled_falls_back_to_windows_default`
  - `Reconcile_appends_new_launcher_and_keeps_order`
  - `Reconcile_marks_uninstalled_launcher_missing`
  - `Reconcile_always_has_default_browser_first`
  - `RememberGame_dedupes_and_caps_30`
  - `RecentGames_skips_launchers_browsers_and_self` (Review Focus 5)
- [ ] **Step 2:** Run. Expected: fail.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Green. Commit `feat(shortcuts): catalogs, reconciling, recent games`.

### Task 4: Native shell layer
**Files:**
- Create `Services/Shortcuts/ShortcutNative.cs`.
- Modify `Native/Shell32.cs`.
- Tests: `ShortcutNativeLiveTests.cs` (gated) and `ShortcutIconKeyTests.cs` (pure).

**Produces:**
```csharp
public interface IShortcutShell {
  IReadOnlyList<ShellLinkInfo> StartMenuAndDesktopLinks();   // both Start Menu roots + user + public Desktop, recursive, *.lnk and *.url
  ShellLinkInfo? ReadLink(string path);                      // IShellLinkW for .lnk, [InternetShortcut] URL= for .url
  string? WindowsDefaultBrowserExe();                         // AssocQueryString(ASSOCF_NONE, ASSOCSTR_EXECUTABLE, "http")
  bool StoreAppInstalled(string aumid);                        // SHCreateItemFromParsingName("shell:AppsFolder\\" + aumid) succeeds
  string? IconPng(string sourcePath, string cacheKey);         // exe/lnk/url or "shell:AppsFolder\<AUMID>", 256 px, alpha kept, cached in %LOCALAPPDATA%\WKI_Clipper\AppIcons
  bool Start(LaunchTarget target, out string? error);          // rules in spec §4.2
}
public sealed class Win32ShortcutShell : IShortcutShell { }
public static class ShortcutIconKey { public static string For(string sourcePath, DateTime lastWriteUtc); }
```
**Start rules:**
- `SteamApp` starts `steam://rungameid/{id}`.
- `EpicApp` starts `com.epicgames.launcher://apps/{AppName}?action=launch&silent=true`.
- `ShellLink`/`.url` starts the link file itself.
- `StoreApp` starts `explorer.exe` with `shell:AppsFolder\{AUMID}`.
- `Exe` starts with `WorkingDirectory` set to the exe's directory.
- `Browser`/`DefaultBrowser` start the exe without arguments.

Proven icon code is in the scratchpad: `launcher_spike\icons.ps1`. It calls `IShellItemImageFactory.GetImage` with flags `0x4|0x1`, then copies the DIB bits into 32bpp ARGB.

- [ ] **Step 1: Write the failing tests.**
  - `Icon_key_changes_with_timestamp`
  - Live (`WKI_LIVE=1`):
    - `Reads_steam_start_menu_link_target`
    - `Notepad_icon_is_256_with_alpha`
    - `Windows_default_browser_exe_exists`
    - `Xbox_store_app_is_installed_here`
    - `Start_rejects_missing_exe_with_error` (no process started)
- [ ] **Step 2:** Run. Expected: fail.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Green, and the live tests pass once with `WKI_LIVE=1`; they start nothing. Commit `feat(shortcuts): native shell layer`.

### Task 5: ShortcutService, hotkeys, wiring
**Files:**
- Create `Services/Shortcuts/ShortcutService.cs`.
- Modify `AppHost.cs`: construct the service; `GameChanged` → `RememberGame`, with the exe path from the game's pid via `GameContext`; dispose.
- Modify `Services/HotkeyService.cs`: `RegisterAll` registers `App:<id>`; `FindCollision` and `DescribeAction` know shortcut entries.
- Modify `App.xaml.cs`: the dispatch switch gets the prefix `App:` and calls `Launch`.
- Test: `ShortcutServiceTests.cs`, with a fake `IShortcutShell`.

**Produces:**
```csharp
public sealed record BarItem(string Id, ShortcutGroup Group, string Name, string? IconPng, bool Missing);
public sealed class ShortcutService : IDisposable {
  public ShortcutService(SettingsService settings, IShortcutShell shell, IFileSystemView fs, IRegistryView reg, Action closeBoard, Action<string> toast);
  public Task RescanAsync();                                   // background; raises ItemsChanged
  public IReadOnlyList<BarItem> VisibleItems();                // order: games, launchers, browsers; hidden excluded
  public bool Launch(string id);                               // closeBoard() first, then shell.Start; missing → toast + Missing=true + save
  public void AddGame(LaunchTarget t, string name, string? iconSource);   // Id "game.<stable hash of target>"
  public void Remove(string id); public void SetHidden(string id, bool hidden); public void Move(string id, int delta);
  public void RememberGame(string exePath, string name);
  public event EventHandler? ItemsChanged;
}
public static class ShortcutHotkeys { public const string Prefix = "App:"; public static string ActionFor(string id); public static bool TryParse(string a, out string id); }
```
- [ ] **Step 1: Write the failing tests.**
  - `Launch_closes_board_before_starting`
  - `Missing_target_launch_reports_and_greys` (Review Focus 3)
  - `Rescan_adds_detected_launchers_once`
  - `Visible_items_are_grouped_games_launchers_browsers`
  - `Move_and_hide_persist`
  - `Add_game_twice_keeps_one`
  - `App_prefix_round_trips`
  - `Shortcut_hotkey_collides_with_F9`
  - `Entry_without_key_is_not_registered`
  - `RememberGame_from_game_changed_uses_resolver_exclusions`
- [ ] **Step 2:** Run. Expected: fail.
- [ ] **Step 3:** Implement. Rescan runs on a background task; settings are saved on the UI thread via the existing save path.
- [ ] **Step 4:** Green. Commit `feat(shortcuts): service, hotkeys, wiring`.

### Task 6: Bar window and board integration
**Files:**
- Create `Services/Shortcuts/BarLayout.cs` and `Views/ShortcutBarWindow.cs`.
- Modify `Views/WidgetHost.cs`:
  - `OpenBoard` shows the bar after the sidebar and calls `RescanAsync()` when the last scan is older than 60 s; `CloseBoard` hides it.
  - Run the widget obstacle step next to `KeepWidgetsClearOfSidebar`.
- Tests: `BarLayoutTests.cs`, `ShortcutBarWindowTests.cs` (STA, offscreen).

**Produces:**
```csharp
public static class BarLayout {
  public static LayoutRect BarRect(LayoutRect monitorWorkArea, double barWidth, double scale);   // centred, top margin 8 DIP, height 56 DIP
  public static IReadOnlyList<(WidgetId Id, LayoutRect NewRect)> PushBelow(LayoutRect bar, IReadOnlyList<(WidgetId Id, LayoutRect Rect)> widgets, double gap);
}
```
**UI:**
- Glass look from `Theme.xaml`.
- Collapsed: game chip (icon of `GameDetector.Current`, otherwise the WKI logo) + "›".
- Expanded: "‹", the groups with separators, then ⚙.
- Icons are 32 px and the tooltip shows the name.
- Clicking an item calls `ShortcutService.Launch`; ⚙ opens `ShortcutEditorWindow`.
- `Expanded` is persisted.
- The window is no-activate and topmost, like the widgets, and DPI-correct with the `DisplayGeometry` helpers.

- [ ] **Step 1: Write the failing tests.**
  - `Bar_is_centred_on_work_area_with_top_margin`
  - `Widget_under_bar_moves_once_and_stays` (Review Focus 4): the second call returns no moves.
  - `Widget_not_under_bar_is_untouched`
  - `Collapsed_bar_renders_chip_only`
  - `Expanded_bar_renders_groups_and_separators`
  - `Empty_group_has_no_separator`
- [ ] **Step 2:** Run. Expected: fail.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Green. Commit `feat(shortcuts): bar window in the board`.

### Task 7: Edit dialog and browser dialog
**Files:**
- Create `Views/ShortcutEditorWindow.cs` and `Views/BrowserMissingDialog.cs`.
- Test: `ShortcutEditorTests.cs`; pure helpers for the list rows plus STA render.

**UI** per spec §2/§4: three lists (Spiele / Launcher / Browser).
- **Every row:**
  - visibility checkbox
  - ↑ / ↓ buttons
  - remove (only for games and picked browsers)
  - a hotkey field that reuses the existing hotkey capture control (unbound by default)
- **"Spiel hinzufügen…"** offers four sources:
  - Vorschläge (`RecentGames`, resolved via `GameLaunchResolver`)
  - Steam-Bibliothek
  - Epic
  - Verknüpfung/Exe wählen… (an `OpenFileDialog` for `*.lnk;*.url;*.exe`, starting in Desktop)
- **Browser area:**
  - the standard-browser selector (Windows default first)
  - "Browser hinzufügen" with Opera, Chrome, Comet, Firefox and any others detected. Picking one that is not installed opens `BrowserMissingDialog`, which offers to open the download page in the current default browser, use another browser, or cancel.
- **"Neu suchen"** calls `RescanAsync`.

- [ ] **Step 1: Write the failing tests.**
  - `Suggestions_resolve_arma_to_steam_and_wot_to_shortcut`
  - `Steam_library_list_excludes_redistributables`
  - `Missing_browser_pick_requests_dialog_not_add`
  - `Dialog_offers_installed_alternatives_only`
  - `Editor_renders_three_groups` (STA)
- [ ] **Step 2:** Run. Expected: fail.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Green. Commit `feat(shortcuts): editor and browser dialog`.

### Task 8: README
- [ ] **Step 1:** Add a section "Shortcut bar" after "Gallery & editing" and a row in the widget table.
  - Content: how it works, what is detected, start paths (Steam/Epic/vendor shortcut), the browser default and install prompt.
  - Limits: no controller, no window management.
  - Settings JSON excerpt.
  - Verify every claim against the code.
- [ ] **Step 2:** Commit `docs(readme): shortcut bar`.

### Task 9: Review, fixes, live test, release
- [ ] **Step 1 (Opus, one reviewer):** adversarial review of the whole diff since this plan's commit. Every finding needs a concrete scenario; the Review Focus is a must.
- [ ] **Step 2 (Sonnet):** fix confirmed findings; suite green; commit.
- [ ] **Step 3 (with Phillip, after the game check):**
  - Build and install, then confirm the hash.
  - Board: the bar appears collapsed and expanded, and the layout stays.
  - Start each launcher once, and the Xbox app.
  - Start Arma (Steam), Fortnite or Spider-Man (Epic) and World of Tanks (shortcut). Only start them once Phillip says so; otherwise click and cancel at the launcher.
  - The default browser, plus Opera (missing) → dialog.
- [ ] **Step 4:** Version `0.17.0` in the csproj and `installer.iss` (keep the BOM). Commit, tag, push, release with notes and SHA-256, README screenshot of the bar.
