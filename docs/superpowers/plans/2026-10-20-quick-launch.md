# Schnellstart (Quick launch) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A "Schnellstart" widget that fetches and sends away the real windows of game clients and browsers (tile, per-app hotkey), each docked at its own remembered place.

**Architecture:** Pure logic (catalog data, detection, window picking, toggle decision, dock maths, hotkey ids, settings v10) is fully unit-tested. A thin native layer behind `IWindowSystem` does Win32 work. `LauncherService` orchestrates one press. A code-only WPF view registers as a normal widget.

**Tech Stack:** .NET 8 WPF, Win32 P/Invoke (user32, shell32, shlwapi, advapi32), xunit 2.9.2.

**Spec:** `docs/superpowers/specs/2026-10-20-quick-launch-design.md`. Executors read the spec and this plan.

## Global Constraints

- Repo `C:\Users\cr4zy\Documents\CLAUDE_Projects\WKI_Clipper`, branch `master`. Use the **PowerShell tool only** (the Bash tool is broken).
- All user-visible text goes through `L.T("Deutsch","English")`. Code and comments are in English, in the surrounding style.
- **Never:**
  - simulated input (`SendInput`/`keybd_event`, Alt trick)
  - `SetParent` across processes
  - starting anything elevated (`runas`)
  - touching game processes beyond `SetForegroundWindow` on their window
  - downloading or installing anything
- No polling outside a running press (100 ms steps, at most 20 s). No new NuGet packages.
- Hidden windows are never revealed with `ShowWindow`; tray apps get their open command instead.
- Never treat Internet Explorer as a browser (`IEXPLORE.EXE`). Edge is never "always installed".
- Controller combos are **not** offered for Schnellstart entries in this version.
- Tests go in `WKI_Clipper.Tests/<Topic>Tests.cs`, namespace `WKI_Clipper.Tests`, with underscore sentence names. Live tests are gated with `WKI_LIVE=1`.
- The full suite stays green with 0 build warnings after every task.
- Commit message is written to a file and committed with `git commit -F`. The last line is exactly `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- No version bump, `build.ps1` or install before Task 9. No visible windows and no mouse/keyboard injection on Phillip's desktop, except in Task 0 and Task 9, which run with him.
- **Pharao roles:**
  - Opus: Tasks 0, 5, 6 and the review in Task 9.
  - Sonnet: Tasks 1–4, 7, 8 and the fixes.
  - Run one task at a time and keep the usage window in mind.

## Review Focus

1. **The launcher shows a login or update window instead of its main window** (Steam "Bei Steam anmelden", a Battle.net updater). The press must bring that window to the front without docking it, and must not keep waiting for 20 s. Test: Task 3 `Login_window_counts_as_main_when_main_is_absent`.
2. **The window to return focus to is gone** (the game closed while Steam was shown). Sending Steam away must not crash or steal focus elsewhere. Test: Task 6 `Hide_with_vanished_return_target_just_minimizes`.
3. **Stub launchers hand over to another process** (`Battle.net Launcher.exe` starts `Battle.net.exe` and exits; custom launchers do the same). The window must still be found. Test: Task 3 `Custom_entry_follows_process_started_by_the_stub`.
4. **The browser runs in the background with no window** (Chrome "continue running background apps"). The press must open a new window, not report "already running". Test: Task 3 `Background_browser_without_window_needs_open`.
5. **The clipper exits while a fetched window is temporarily topmost.** Topmost must be cleared on dispose. Test: Task 6 `Dispose_clears_temporary_topmost`.

---

## File Structure

| File | Responsibility |
|---|---|
| `WKI_Clipper/Models/LauncherSettings.cs` (new) | `LauncherSettings`, `LauncherEntry`, `DockPlacement`, `LauncherEntryKind` |
| `WKI_Clipper/Models/AppSettings.cs` | `Launcher` section, `WidgetId.QuickLaunch`, `DefaultLayout` entry |
| `WKI_Clipper/Services/SettingsService.cs` | `CurrentSchemaVersion = 10`, `EnsureSections` guard, v10 migration |
| `WKI_Clipper/Services/Launch/KnownApps.cs` (new) | catalog data (`KnownApp`, `WindowRule`, `OpenCommand`, detection sources) from the Task 0 table |
| `WKI_Clipper/Services/Launch/AppDetector.cs` (new) | pure detection over `IRegistryReader` / `IFileProbe` |
| `WKI_Clipper/Services/Launch/WindowPicker.cs` (new) | `WindowInfo`, main window and group picking |
| `WKI_Clipper/Services/Launch/ToggleDecision.cs` (new) | what one press does |
| `WKI_Clipper/Services/Launch/DockMath.cs` (new) | rect ↔ `DockPlacement`, clamping |
| `WKI_Clipper/Services/Launch/LauncherHotkeys.cs` (new) | `App:<id>` action ids |
| `WKI_Clipper/Native/User32.cs`, `Native/Shell32.cs` | new P/Invokes |
| `WKI_Clipper/Services/Launch/Win32WindowSystem.cs` (new) | `IWindowSystem` implementation |
| `WKI_Clipper/Services/Launch/AppIconCache.cs` (new) | exe → 256 px PNG with alpha |
| `WKI_Clipper/Services/Launch/LauncherService.cs` (new) | press orchestration |
| `WKI_Clipper/Views/QuickLaunchView.cs` (new), `Views/BrowserMissingDialog.cs` (new) | UI |
| `WidgetCatalog.cs`, `WidgetHost.cs`, `HotkeyService.cs`, `HotkeysView.xaml.cs`, `App.xaml.cs`, `AppHost.cs` | wiring |

---

### Task 0: Live verification of all launchers and browsers (Opus with Phillip)

**Files:** Create `docs/superpowers/specs/2026-10-20-quick-launch-phase0.md`. Reuse the spike scripts in the scratchpad: `launcher_spike\probe.ps1`, `launcher_spike\dock.ps1`, `launcher_spike\icons.ps1`.

**Produces:** the table that Task 2 encodes. Each row is one app:
- Id
- window owner process names
- main window class + title rule (de/en)
- popup titles to exclude
- open command
- what happens when the exe is re-run (single instance? second window? updater?)
- elevated
- tray behaviour
- minimum size
- `SetForegroundWindow` result from `WM_HOTKEY` with Arma Reforger in front
- whether Arma's window is `WS_EX_TOPMOST` or exclusive fullscreen

- [ ] **Step 1:** Run a game check (`ArmaReforger|Wardogs|DayZ|arma3|Shipping|WorldOfTanks`) and tell Phillip what will move on screen. He logs in where a launcher asks; never enter credentials yourself.
- [ ] **Step 2:** For each of the 8 launchers and the Steam friends window, cover these states: cold start, open, minimized, closed to tray. Run `probe.ps1` and `dock.ps1` with the app-specific title rule, once on the desktop and once with Arma Reforger (borderless) in front. Record the result rows. Restore every window afterwards.
- [ ] **Step 3:** Browsers:
  - Read the StartMenuInternet key names for Opera, Opera GX and Firefox from their vendors' documentation or a test install that Phillip agrees to; otherwise mark the row "unverified, detect by exe name".
  - Record the window classes `Chrome_WidgetWin_1` and `MozillaWindowClass`.
  - Check every download URL from spec §6.2. It must answer HTTP 200 on the official domain.
- [ ] **Step 4:** Commit the table: `docs(spec): quick launch phase 0 results`.

### Task 1: Settings model and schema v10 (Sonnet)

**Files:**
- Create `WKI_Clipper/Models/LauncherSettings.cs`.
- Modify `Models/AppSettings.cs`: add the `WidgetId` enum value, a `Launcher` property, and the `DefaultLayout()` entry.
- Modify `Services/SettingsService.cs`.
- Test: `WKI_Clipper.Tests/LauncherSettingsTests.cs`. Update `SettingsMigrationTests.cs`; it currently expects 14 layout entries.

**Interfaces — Produces:**
```csharp
public enum LauncherEntryKind { Known, Custom }
public sealed class LauncherSettings { public List<LauncherEntry> Entries { get; set; } = new(); public string? DefaultBrowserId { get; set; } }
public sealed class LauncherEntry {
  public string Id { get; set; } = "";              // "steam", "steam-chat", "browser.chrome", "custom.<guid>"
  public LauncherEntryKind Kind { get; set; }
  public string? ExePath { get; set; }              // Custom only
  public string? DisplayName { get; set; }          // Custom only
  public string? WindowProcessName { get; set; }    // Custom only, optional override (Review Focus 3)
  public bool Hidden { get; set; }
  public bool Dock { get; set; } = true;
  public DockPlacement? Place { get; set; }
  public HotkeyBinding? Hotkey { get; set; }        // null/Key 0 = unbound
}
public sealed class DockPlacement { public string MonitorDevice { get; set; } = ""; public double RelX, RelY, RelW, RelH; public bool Maximized { get; set; } }
// AppSettings: public LauncherSettings Launcher { get; set; } = new();  WidgetId gets QuickLaunch (appended last)
```

- [ ] **Step 1: Failing tests:**
  - `Default_layout_contains_quick_launch_hidden`: the count is 15 and `QuickLaunch` is `Visible == false`.
  - `V9_settings_migrate_to_v10_with_empty_launcher`: the JSON fixture has `SchemaVersion` 9 and no `Launcher`. After load, `SchemaVersion == 10`, `Launcher.Entries` is empty, and the `QuickLaunch` widget state exists.
  - `Null_launcher_section_is_guarded`: JSON with `"Launcher": null` gives a non-null `Launcher` and non-null `Entries`.
  - `Launcher_round_trips`: one entry with a `Place` and a `Hotkey` survives save and load unchanged.
- [ ] **Step 2:** Run `dotnet test WKI_Clipper.Tests --filter "LauncherSettingsTests|SettingsMigrationTests"`. Expected: the new tests fail.
- [ ] **Step 3:** Implement:
  - `CurrentSchemaVersion = 10`.
  - The `if (s.SchemaVersion < 10)` migration: add the `QuickLaunch` widget state, hidden.
  - In `EnsureSections`: `s.Launcher ??= new(); s.Launcher.Entries ??= new();`.
- [ ] **Step 4:** Full suite green, 0 warnings. Commit `feat(launcher): settings model and schema v10`.

### Task 2: Catalog and detection (Sonnet)

**Files:**
- Create `Services/Launch/KnownApps.cs` and `Services/Launch/AppDetector.cs`.
- Test: `WKI_Clipper.Tests/AppDetectorTests.cs`.

**Interfaces — Consumes:** the Task 0 table. **Produces:**
```csharp
public enum AppCategory { GameClient, Browser }
public sealed record WindowRule(IReadOnlyList<string> OwnerProcesses, string? ClassName, IReadOnlyList<string> MainTitles,
                                IReadOnlyList<string> ExcludedTitles, bool Group /* steam-chat: all non-main windows */);
public sealed record OpenCommand(string? Uri, bool RerunExe);   // Steam main: Uri "steam://open/main"
public sealed record KnownApp(string Id, string Name, AppCategory Category, IReadOnlyList<string> DetectionSources,
                              string? DefaultExe, OpenCommand Open, WindowRule Window, string? DownloadUrl);
public static class KnownApps { public static IReadOnlyList<KnownApp> All { get; } public static KnownApp? Find(string id); }
public interface IRegistryReader { string? GetString(string hive, string key, string? value); IReadOnlyList<string> SubKeys(string hive, string key); }
public interface IFileProbe { bool Exists(string path); }
public sealed record DetectedApp(string Id, string Name, AppCategory Category, string? ExePath, bool Installed);
public static class AppDetector {
  public static DetectedApp Detect(KnownApp app, IRegistryReader reg, IFileProbe fs);
  public static IReadOnlyList<DetectedApp> Browsers(IRegistryReader reg, IFileProbe fs);  // catalog browsers + other StartMenuInternet entries
  public static string NormalizeExe(string raw);   // quotes, forward slashes, args, env vars
}
```
Detection sources use one string syntax, for example `"HKCU|Software\Valve\Steam|SteamExe"` and `"UNINSTALL|Battle.net|InstallLocation|Battle.net Launcher.exe"`. Entries must exist for all 8 clients, `steam-chat`, and the browsers Opera, Opera GX (`extra`), Chrome, Comet and Firefox.

- [ ] **Step 1: Failing tests** with a fake registry that copies Phillip's machine (spec §3):
  - `Steam_is_found_via_hkcu_steamexe_with_forward_slashes`: `ExePath == @"C:\Program Files (x86)\Steam\steam.exe"`.
  - `Comet_from_hkcu_startmenuinternet_is_installed`.
  - `Chrome_listed_in_hklm_and_wow6432_appears_once`.
  - `Iexplore_is_never_a_browser`.
  - `Edge_appx_stub_without_exe_is_not_installed`.
  - `Missing_exe_means_not_installed`: the registry hit exists but `fs.Exists` is false.
  - `Brave_is_listed_as_extra_browser`.
  - `NormalizeExe` theory: `"\"C:\a b\x.exe\" --flag"` gives `C:\a b\x.exe`, and `c:/x/y.exe` gives `C:\x\y.exe`.
- [ ] **Step 2:** Run the filter `AppDetectorTests`. Expected: fail.
- [ ] **Step 3:** Implement the catalog data from the Task 0 table and the detector. Exclude `IEXPLORE.EXE` by key name and by exe file name.
- [ ] **Step 4:** Green. Commit `feat(launcher): catalog and app detection`.

### Task 3: Window picking (Sonnet)

**Files:**
- Create `Services/Launch/WindowPicker.cs`.
- Test: `WKI_Clipper.Tests/WindowPickerTests.cs`.

**Interfaces — Produces:**
```csharp
public sealed record WindowInfo(IntPtr Handle, int Pid, int ParentPid, string ProcessName, string ClassName, string Title,
                                bool Visible, bool Minimized, bool Owned, bool ToolWindow, bool Topmost, int ZOrder /*0 = top*/);
public sealed record PickResult(WindowInfo? Main, IReadOnlyList<WindowInfo> Group, bool MainIsFallback /*login/updater*/);
public static class WindowPicker {
  public static PickResult Pick(WindowRule rule, IReadOnlyList<WindowInfo> windows);
  public static PickResult PickCustom(string exeProcessName, string? windowProcessName, IReadOnlySet<int> startedPids, IReadOnlyList<WindowInfo> windows);
}
```
**Rules:**
- Candidates: unowned, not a tool window, title not empty, visible or minimized. Hidden windows never count.
- Main window: a `MainTitles` match with the class matching.
- Fallback when there is no main window: any other candidate of the owner processes that is not in `ExcludedTitles`. Set `MainIsFallback = true`.
- Browsers: the candidate with the lowest `ZOrder`.
- `Group`: the main window plus nothing else, except when `rule.Group` is set (steam-chat). Then it is all candidates except the Steam main window and the excluded titles.

- [ ] **Step 1: Failing tests.** Use fixtures from the spike logs:
  - `Hidden_steam_main_with_visible_offers_popup_gives_no_main`: Steam `vis=False` and "Sonderangebote" visible give `Main == null`.
  - `Visible_steam_main_is_picked_over_popups`.
  - `Login_window_counts_as_main_when_main_is_absent` (Review Focus 1): gives `MainIsFallback`.
  - `Owned_and_tool_windows_are_ignored`.
  - `Steam_chat_group_has_friends_and_chats_but_not_main`.
  - `Browser_picks_topmost_in_z_order`.
  - `Background_browser_without_window_needs_open` (Review Focus 4): `Main == null`.
  - `Custom_entry_follows_process_started_by_the_stub` (Review Focus 3): the stub pid has exited, and the child's `ParentPid` is in `startedPids`, or `windowProcessName` matches.
- [ ] **Step 2:** Run. Expected: fail.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Green. Commit `feat(launcher): window picking`.

### Task 4: Toggle decision, dock maths, hotkey ids (Sonnet)

**Files:**
- Create `Services/Launch/ToggleDecision.cs`, `Services/Launch/DockMath.cs` and `Services/Launch/LauncherHotkeys.cs`.
- Modify `HotkeyService.FindCollision` and `HotkeyService.DescribeAction` so they include launcher entries.
- Tests: `ToggleDecisionTests.cs`, `DockMathTests.cs`, `LauncherHotkeysTests.cs`.

**Interfaces — Produces:**
```csharp
public enum ToggleAction { Hide, FocusBackOnly, ShowExisting, Open, OpenOnly }
public sealed record ToggleInput(PickResult Pick, IntPtr Foreground, bool Elevated);
public static class ToggleDecision {
  public static ToggleAction Decide(ToggleInput input);
  public static bool TakeFocusWhenLate(TimeSpan sincePress, bool gameNowInFront);   // false if > 3 s and a game is in front
}
public sealed record MonitorInfo(string Device, LayoutRect Bounds, LayoutRect WorkArea, bool Primary);
public static class DockMath {
  public static DockPlacement From(LayoutRect window, bool maximized, IReadOnlyList<MonitorInfo> monitors);
  public static (LayoutRect Rect, bool Maximized) To(DockPlacement place, IReadOnlyList<MonitorInfo> monitors);
}
public static class LauncherHotkeys { public const string Prefix = "App:"; public static string ActionFor(string id); public static bool TryParse(string action, out string id); }
```
**Decision table:**

| State | Elevated | Action |
|---|---|---|
| Any `Group` window is the foreground window and not minimized | no | `Hide` |
| Any `Group` window is the foreground window and not minimized | yes | `FocusBackOnly` |
| Otherwise, elevated | yes | `OpenOnly` |
| `Main == null` | no | `Open` |
| Otherwise | no | `ShowExisting` |

**Dock maths:**
- Coordinates are relative to the monitor work area.
- If the device is missing, use the primary monitor.
- Clamp so that at least 50 % of the area is visible.
- The size never exceeds the work area.

- [ ] **Step 1: Failing tests:**
  - **Decision table:** one test per row, including `Hide_when_any_steam_chat_window_is_foreground`.
  - `Late_window_does_not_steal_focus_from_game` (4 s, game in front → false).
  - `Late_window_takes_focus_on_desktop` (4 s, no game → true).
  - `Place_round_trips_on_same_monitor`.
  - `Place_on_unplugged_monitor_lands_on_primary`.
  - `Place_is_clamped_half_visible`.
  - `Place_keeps_maximized_flag`.
  - `Place_survives_dpi_scale_change` (same relative rect on a monitor with other bounds).
  - `ActionFor_and_TryParse_round_trip`.
  - `Launcher_hotkey_collides_with_F9` (via `FindCollision`).
  - `DescribeAction_shows_entry_name`.
- [ ] **Step 2:** Run. Expected: fail.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Green. Commit `feat(launcher): toggle decision, dock maths, hotkey ids`.

### Task 5: Native layer (Opus)

**Files:**
- Modify `Native/User32.cs` and `Native/Shell32.cs`.
- Create `Services/Launch/Win32WindowSystem.cs` and `Services/Launch/AppIconCache.cs`.
- Test: `WKI_Clipper.Tests/LauncherNativeLiveTests.cs` (gated) and `AppIconCacheTests.cs` (pure key logic).

**Interfaces — Produces:**
```csharp
public enum Integrity { Same, Higher, Unknown }
public interface IWindowSystem {
  IReadOnlyList<WindowInfo> Snapshot();                 // z-ordered top-level windows with process + parent pid
  IReadOnlyList<MonitorInfo> Monitors();
  IntPtr Foreground { get; }
  bool Restore(IntPtr h); bool Minimize(IntPtr h);
  bool MoveTo(IntPtr h, LayoutRect r, bool maximized);   // false + LastErrorAccessDenied on UIPI
  bool BringToFront(IntPtr h);                           // SetForegroundWindow + verify GetForegroundWindow == h
  void Flash(IntPtr h);
  bool SetTopmost(IntPtr h, bool on);
  Integrity CompareIntegrity(int pid);                   // ACCESS_DENIED with a valid limited handle => Higher
  bool IsExclusiveFullscreen();                          // SHQueryUserNotificationState == QUNS_RUNNING_D3D_FULL_SCREEN
  bool Open(OpenCommand cmd, string? exePath);           // AllowSetForegroundWindow(ASFW_ANY), UseShellExecute, never runas
  string? DefaultBrowserExe();                           // AssocQueryString(ASSOCSTR_EXECUTABLE, "http")
}
public sealed class AppIconCache { public AppIconCache(string cacheDir); public string? GetPng(string id, string exePath); public static string Key(string exePath, DateTime lastWriteUtc); }
```
The icon is read with `IShellItemImageFactory.GetImage(256, SIIGBF_ICONONLY|SIIGBF_BIGGERSIZEOK)`. Copy the DIB bits into 32bpp ARGB, because `FromHbitmap` drops alpha (proven in the spike). The cache lives in `%LOCALAPPDATA%\WKI_Clipper\AppIcons`.

- [ ] **Step 1: Failing tests:**
  - `Icon_key_changes_with_exe_timestamp`
  - `Icon_cache_reuses_png_for_same_key` (pure, temp dir)
  - Live (`WKI_LIVE=1`):
    - `Notepad_icon_is_256_with_alpha`
    - `Snapshot_contains_own_hidden_test_window_as_hidden` (a never-shown WinForms window)
    - `Own_process_integrity_is_same`
    - `Default_browser_exe_exists`
- [ ] **Step 2:** Run. Expected: fail.
- [ ] **Step 3:** Implement the P/Invokes. Use the `SetWindowPos` flags `SWP_NOZORDER|SWP_NOACTIVATE` for moving and `GetWindowPlacement`/`SetWindowPlacement` for maximized. Physical pixels, since the app is PerMonitorV2.
- [ ] **Step 4:** Green, including the live tests once with `WKI_LIVE=1`. Commit `feat(launcher): native window system and icon cache`.

### Task 6: LauncherService — one press end to end (Opus)

**Files:**
- Create `Services/Launch/LauncherService.cs`.
- Modify `AppHost.cs` to construct and dispose it.
- Test: `WKI_Clipper.Tests/LauncherServiceTests.cs`, with a fake `IWindowSystem` and a fake clock.

**Interfaces — Consumes:** Tasks 2–5. **Produces:**
```csharp
public sealed class LauncherService : IDisposable {
  public LauncherService(SettingsService settings, IWindowSystem win, Func<GameInfo?> currentGame, Action closeBoard,
                         Func<IntPtr> boardReturnTarget, Action<string> toast,
                         Func<TimeSpan, CancellationToken, Task> delay, Func<DateTime> utcNow);   // no FakeTimeProvider package
  public Task PressAsync(string entryId);              // one running press per entry; extra presses ignored
  public IReadOnlyList<LauncherTile> Tiles();          // detection + state for the view
  public event EventHandler? TilesChanged;
  public void Rescan();
  public void OnForegroundChanged(IntPtr newForeground); // wired to ForegroundTracker: clears temporary topmost
}
public sealed record LauncherTile(string Id, string Name, AppCategory Category, string? IconPng, TileState State, string? Hint);
public enum TileState { NotInstalled, NotRunning, Running, AdminOnly }
```
**Behaviour:** as in spec §4.1/§4.2, step by step. Two details:
- On `Show`, close the board first, then remember the return target: `boardReturnTarget()` if the board was open, otherwise the foreground window.
- On `Hide`, save the place, then minimize the group, then return focus: the return target, otherwise the game window.

- [ ] **Step 1: Failing tests (fakes):**
  - `Tray_steam_opens_via_uri_then_docks_and_fronts`: the window appears after 300 ms; it gets `MoveTo(place)` and `BringToFront` is true.
  - `Shown_steam_hides_saves_place_and_returns_focus_to_game`.
  - `Second_press_while_waiting_is_ignored`: the open command is called exactly once.
  - `Window_after_20s_gives_timeout_toast_and_no_second_open`.
  - `Slow_start_shows_starting_toast_after_1_5s`.
  - `Late_window_with_game_in_front_is_docked_but_not_fronted`.
  - `Elevated_launcher_open_only_then_focus_back_only`.
  - `Refused_foreground_flashes_and_toasts`.
  - `Topmost_game_makes_fetched_window_topmost_until_foreground_lost`.
  - `Board_is_closed_before_showing`.
  - `Hide_with_vanished_return_target_just_minimizes` (Review Focus 2).
  - `Dispose_clears_temporary_topmost` (Review Focus 5).
  - `Exclusive_fullscreen_toast_once_per_session`.
  - `Dock_off_never_moves_window`.
  - `Browser_default_resolves_to_windows_default`.
  - `Uninstalled_chosen_default_browser_falls_back_with_hint`.
  - `Missing_custom_exe_tile_is_not_installed`.
- [ ] **Step 2:** Run. Expected: fail.
- [ ] **Step 3:** Implement. `browser.default` means the chosen default browser, otherwise the Windows default. Waits use the injected `delay` and never block the UI thread. Call `IWindowSystem` on the UI dispatcher.
- [ ] **Step 4:** Green. Commit `feat(launcher): press orchestration`.

### Task 7: Widget, dialogs, wiring (Sonnet)

**Files:**
- Create `Views/QuickLaunchView.cs` and `Views/BrowserMissingDialog.cs`.
- Modify `Services/WidgetCatalog.cs` (Apps section, label "Schnellstart"/"Quick launch", Segoe glyph `\uE7FC`) and `Views/WidgetHost.cs` (`CreateContent`, `Order`, `Label`).
- Modify `Services/HotkeyService.cs`: `RegisterAll` registers each entry's `Hotkey` as `App:<id>`.
- Modify `App.xaml.cs`: the dispatch switch handles the `App:` prefix by calling `LauncherService.PressAsync`.
- Modify `Views/HotkeysView.xaml.cs`: one row "Standard-Browser" (`App:browser.default`).
- Tests: `WidgetCatalogTests.cs` (QuickLaunch in Apps), `HotkeyRegistrationTests.cs` (`App:` registered, `Launcher_entry_without_key_is_not_registered`), `QuickLaunchViewTests.cs` (STA render, as in `CrosshairTargetBarTests`).

**UI** per spec §7:
- Two groups. Each tile shows the logo (48 px), the name and a state dot.
- Click calls `PressAsync`.
- The `⋯` menu has: *Tastenkombi festlegen* (reuse the capture control the stream deck tiles use), *Andocken an/aus*, *Platz zurücksetzen*, *Ausblenden*, and for custom entries *Pfad ändern/entfernen*.
- The footer has: *Eigenes Programm hinzufügen…* (exe picker), *Ausgeblendete zeigen*, *Neu suchen*, and the default-browser selector (Windows default first).
- A browser that is not installed opens `BrowserMissingDialog` (download page / other browser / cancel).

- [ ] **Step 1: Failing tests:**
  - `Quick_launch_is_in_apps_section`
  - `App_prefix_hotkeys_are_registered`
  - `Launcher_entry_without_key_is_not_registered`
  - `View_renders_groups_and_not_installed_state` (STA, offscreen, never shown)
  - `Browser_missing_dialog_offers_installed_alternatives`
- [ ] **Step 2:** Run. Expected: fail.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Green. Commit `feat(launcher): Schnellstart widget and hotkeys`.

### Task 8: README section (Sonnet)

**Files:** Modify `README.md`: a new section "Quick launch" after "Gallery & editing", with the widget table row, known limits (controller not supported, elevated launchers start only, exclusive fullscreen minimizes) and the settings JSON example.

- [ ] **Step 1:** Write the section. Verify every claim against the code.
- [ ] **Step 2:** Commit `docs(readme): quick launch`.

### Task 9: Review, fixes, live test, release

- [ ] **Step 1 (Opus, one reviewer, sequential):** Adversarial review of `git diff <plan start>..HEAD`. Every finding needs a concrete scenario; check them against the Review Focus.
- [ ] **Step 2 (Sonnet):** Fix the confirmed findings. Suite green. Commit.
- [ ] **Step 3 (Opus with Phillip):** Build and install; run the game check first. Then the live checklist:
  - every launcher in each state: cold, tray, minimized, in front;
  - Arma Reforger in front: fetch, send away, then WASD goes to the game;
  - a missing browser opens the dialog;
  - unplug a monitor or change the DPI: the window is still visible.
- [ ] **Step 4:**
  - Version `0.17.0` in the csproj and `installer.iss` (keep the BOM).
  - Commit, tag, push.
  - Release notes and the SHA-256 on the GitHub release.
  - Screenshot of the widget.
