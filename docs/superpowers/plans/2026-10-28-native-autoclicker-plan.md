# WKI AutoClicker (native) — Implementation Plan

> For agentic workers: implement ONE task per run, test-first, commit, then stop. Read the design
> (`docs/superpowers/specs/2026-10-28-native-autoclicker-design.md`) first.

**Goal:** native auto clicker in the AutoClicker tab of the Gaming Utilities widget, running in a helper process.
**Architecture:** see design §2. Pure logic in `WKI_Clipper/Services/AutoClicker/` (namespace
`WKI_Clipper.Services.AutoClicker`), host in `WKI_Clipper/ClickerHost/`, UI in `WKI_Clipper/Views/AutoClicker/`.
**Tech:** .NET 8, WPF, C# latest, nullable on, warnings are errors, xUnit tests in `WKI_Clipper.Tests`.

Repo: `C:\Users\cr4zy\Documents\CLAUDE_Projects\WKI_Clipper`. Branch `feat/native-autoclicker` (created from
`feat/gaming-utilities`). Conventions for every task:
- Match surrounding code style, comment density and idioms (read neighbouring files first). Code/comments English.
- All user-visible strings via `L.T(german, english)` with real umlauts. Only DynamicResource palette keys and the
  existing styles (TabButtonStyle, AccentButton, MutedStyle, PanelBrush ...), no hardcoded colours.
- Commit messages via a UTF-8 file WITHOUT BOM and `git commit -F`; last line
  `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never push.
- Verify before reporting done: full solution `dotnet build` with 0 warnings, `dotnet test` green. Known flaky:
  `FFmpegJobRunnerIntegrationTests.A_healthy_slow_job_is_not_mistaken_for_a_hung_one` (if only it fails, rerun it
  alone and report both).
- Never run the app or the host, never inject real input, never drive UI. Tests use fakes only.

---

## Task 1 — Settings model, migration v12, pure rules (settings + hotkeys)

Files: `Models/AppSettings.cs` (extend `AutoClickerSettings` with `ClickerSettings Clicker`), new
`Services/AutoClicker/ClickerSettingsRules.cs`, `Services/AutoClicker/ClickerHotkey.cs`,
`Services/AutoClicker/HotkeyMatcher.cs`, `Services/AutoClicker/ActivationStateMachine.cs`,
`Services/AutoClicker/ClickerConflicts.cs`, `Services/AutoClicker/ClickerPresets.cs`,
`Services/SettingsService.cs` (v11 → v12), tests.

1. `ClickerSettings` with every field and default of design §3 (enums: `RateInputMode`, `RateUnit`,
   `DutyMode`, `ClickerInput`, `ClickerMouseButton`, `KeyCase`, `ActivationMode`, `TimeLimitUnit`, `ZoneAction`),
   `ClickerZone`, `ClickPoint`, `ClickerPreset` (name + a copy of the click/failsafe/limit/zone/point/whitelist
   fields; no UI fields, no stats), `ClickerStats`, `RunRecord`. JSON-serialisable like the rest of AppSettings.
2. `ClickerSettingsRules.Normalize(ClickerSettings)` clamps every range of §3 (speed cap depends on
   ExtendedSpeedLimit; DoubleClick caps speed at 10 per second; NaN/negative → default; lists capped: zones 64,
   points 500, presets 100, LastRuns 100; preset names trimmed to 40 chars, unique case-insensitively).
3. Migration v11 → v12: adds `Clicker` with defaults, Armed=false, log line like the previous migrations.
4. `ClickerHotkey` (Modifiers flags with generic + left/right variants, `Keys` list of 1..5 VKs incl. mouse VKs),
   default Ctrl+Alt+Y; `ToDisplayString()` localized ("Strg+Alt+Y", "LStrg", "Maus 4" ...).
5. `HotkeyMatcher.IsMatch(hotkey, IReadOnlySet<int> downVks, bool strict)`: generic modifier = either side
   (VK_LCONTROL/VK_RCONTROL ...), all keys down, strict → no extra modifier down.
6. `ActivationStateMachine` (pure, event-driven): inputs `OnMatchChanged(bool)`, `OnMasterChanged(bool)`,
   `OnLock/Unlock`, `OnPicker(bool)`, `OnStoppedByEngine(reason)`; outputs Start/Stop commands with reasons for
   Toggle and Hold, master Toggle/Hold, picker blocking, lock blocking (§5).
7. `ClickerConflicts`: output-key vs hotkey conflict (§5), clash with the clipper's own RegisterHotKey bindings
   (`AppSettings.Hotkeys` + shortcut-bar hotkeys) → warning, empty enabled whitelist → cannot start.
8. `ClickerPresets`: 3 factory presets (§9), apply (copies fields into settings), save-as, rename, delete,
   export/import JSON (import validates + normalizes, rejects unknown version).
Tests: migration, normalize every clamp, matcher (L/R/generic, strict, mouse VKs, 5 keys), state machines
(toggle, hold, master toggle/hold, lock, picker), conflicts, presets round trip + bad import.

## Task 2 — Pure engine math, stop rules, click points

Files: `Services/AutoClicker/Cadence.cs`, `DutyCycle.cs`, `Randomizer.cs` (seedable, Box-Muller),
`DoubleClickTiming.cs`, `KeyboardRepeat.cs`, `StopRules.cs`, `ZoneTracker.cs`, `ClickPointSequencer.cs`,
`CursorPath.cs`, tests.

1. `Cadence`: interval (TimeSpan/double ms) from rate+unit or duration; cycles per batch (§4); batch duration.
2. `DutyCycle.HoldMs(intervalMs, cps, enabled, percent)` with the rate caps of §4.
3. `Randomizer`: log-normal draw (mean = base, sigma = base x pct / 100) with floor 1 ms; a test proves the mean of 100k draws is within 1 % of the base.
4. `DoubleClickTiming` (§4) given cycle, duty hold, double-click time → press1 hold, gap,
   press2 hold; invariants tested (fits cycle, second press within 90 % of DCT).
5. `KeyboardRepeat`: SPI delay index 0..3 → 250/500/750/1000 ms; speed 0..31 → 2.5..30 per s linear.
6. `StopRules.Evaluate(StopContext)` → first reason in the order of §4 (inputs: clicks, limits, elapsed, cursor,
   monitors as rects, zones, foreground class name + exe, whitelist, locked); reasons are an enum with localized
   text in the main app.
7. `ZoneTracker`: pause in/out, start-zone enter/exit state machine (§4).
8. `ClickPointSequencer`: order, per-point counts, loop vs StopWhenComplete, jitter (uniform in radius + 21.6 %
   chance of up to 2 px extra, seedable), `CursorPath.Bezier(from, to, durationMs)` quad-eased points; jump when
   cps >= 50; duration 20–60 % of the interval, max 200 ms.
Tests for all of it, incl. corner/edge per monitor with negative-coordinate monitors.

## Task 3 — Host process: entry mode, native layer, engine loop, IPC server

Files: `App.xaml.cs` (host-mode branch at the very start of OnStartup), `ClickerHost/HostMain.cs`,
`ClickerHost/HostLog.cs`, `ClickerHost/InputInjector.cs` (+ `IInputSink`), `ClickerHost/HighResWaiter.cs`
(+ `IWaiter`, `IClock`), `ClickerHost/HotkeyHook.cs`, `ClickerHost/HostEnvironment.cs` (+ `IHostEnvironment`:
cursor pos, monitors, foreground hwnd → class name + exe + elevated, SPI keyboard delay/speed, GetDoubleClickTime,
CapsLock), `ClickerHost/ClickEngine.cs`, `ClickerHost/HostServer.cs`, `Services/AutoClicker/ClickerProtocol.cs`
(message records + JSON line codec, shared by both sides), `Native/User32.cs` / `Native/Kernel32.cs` additions,
tests.

1. Host mode: `--clicker-host <pipeName> <parentPid>` detected before the single-instance mutex and before any
   main-app init; no windows, no tray; `GCSettings.LatencyMode = SustainedLowLatency`; logs to
   `wki_clicker_host.log`; exits when the pipe closes, `shutdown` arrives or the parent process exits.
2. `InputInjector`: SendInput mouse down/up (L/R/M), absolute virtual-desk moves (`MOUSEEVENTF_ABSOLUTE |
   MOVE | VIRTUALDESK`, normalised to 0..65535 over the virtual screen), keyboard scancode down/up with extended
   flag, Shift wrapping for case, `dwExtraInfo = 0x574B4943`; checks the return count; tracks what it pressed and
   `ReleaseAll()`; batch API sending several pairs in one call.
3. `HighResWaiter`: `CreateWaitableTimerExW(..., CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS)`,
   absolute due times, `WaitForMultipleObjects(timer, stopEvent)`, wake ~1 ms early then spin to the deadline;
   fallback normal timer + `timeBeginPeriod(1)` only while running.
4. `HotkeyHook`: own thread with a message loop, `WH_KEYBOARD_LL` + `WH_MOUSE_LL` (delegates kept alive), skips
   events whose `dwExtraInfo` is our tag, tracks physical down-set, raises match changes through `HotkeyMatcher`;
   always calls `CallNextHookEx` (never swallows); a hook removed by Windows (timeout) is re-installed.
5. `ClickEngine`: the loop of design §4 using the Task 1/2 pure parts, `IInputSink`, `IWaiter`, `IClock`,
   `IHostEnvironment`; start waits for hotkey modifiers released in Toggle mode; stop checks each iteration; pause
   zones; keyboard hold mode; double click; click points; counts clicks; reports state ≤ 10/s; on stop releases all.
   Deterministic tests with fakes: rates/batching produce the right number of down/up events over virtual time,
   duty holds, limits, stop order, pause/resume, Hold duty mode, keyboard repeat, double click counts, release on stop.
6. `ClickerProtocol` + `HostServer`/client codec: messages of design §6, versioned, unknown types ignored; tests for
   round trip, version mismatch, malformed lines.
7. `ActivationStateMachine` + `ZoneTracker` start-zone monitor (100 ms) wired in the host.

## Task 4 — Main-app controller, guard integration, indicator

Files: `Services/AutoClicker/ClickerController.cs` (+ `IClickerHostLauncher` seam), `Services/AutoClickerGuard.cs`
(native target), `AppHost.cs` wiring + dispose, `Views/AutoClicker/ClickerIndicatorWindow.cs`, tests.

1. `ClickerController`: creates the pipe server (current-user ACL) and starts `Environment.ProcessPath
   --clicker-host <pipe> <pid>` (no window) while `Clicker.Armed`; assigns it to `KillOnCloseJob`; handshake
   timeout 5 s; restarts once on unexpected exit, then error state; pushes config on settings change (debounced
   200 ms); exposes `State` + `StateChanged` (UI thread marshalling done by the view); `Start()`, `Stop()`,
   `SetPicker(bool)`; writes `runEnded` into `Clicker.Stats` (LastRuns capped 100) and saves settings; disposes
   cleanly (shutdown message, wait 1 s, kill).
2. Guard: blocker present and not ignored → controller `Lock(entry)` (host stops, refuses starts) else `Unlock()`;
   lock not confirmed within 1 s → kill host (it restarts locked). Toast on a lock that
   stopped a running clicker: "AutoClicker gestoppt - <Spiel> steht auf der Blacklist". Pure decision extended +
   tests.
3. Indicator: click-through, topmost, no-activate label "AutoClicker · 25 cps" while running (setting
   ShowActiveIndicator), excluded from capture with the existing capture-exclusion mechanism; must not trigger a
   replay-buffer restart; if the active capture path cannot exclude it, it is not shown. Find how WhatsApp's
   `ExcludeFromCapture` and the crosshair interact with `FFmpegCommandBuilder`/capture path and follow that rule set.
4. Elevated-window notice and limit/blacklist toasts as in design §4/§7.
Tests: controller with a fake launcher + in-memory pipe pair (lifecycle, restart-once, config debounce, stats
write), guard decisions.

## Task 5 — Pickers

Files: `Views/AutoClicker/ClickPointPickerWindow.cs`, `Views/AutoClicker/ZonePickerWindow.cs`,
`Services/AutoClicker/PickerGeometry.cs` (pure DIP↔physical conversion per monitor), tests for the geometry.
Behaviour of design §8; one window per monitor (Screen.AllScreens + per-monitor DPI via the existing
`DisplayGeometry` helpers); controller `SetPicker(true/false)` around it; results written to settings.

## Task 6 — UI

Files: `Views/AutoClickerView.xaml(.cs)` restructured into the status row + sub-tab host, new
`Views/AutoClicker/SimpleClickerPanel`, `AdvancedClickerPanel`, `ZonesSafetyPanel` (zones + corners/edges +
Alt+Tab + the EXISTING blacklist UI moved here + whitelist), `ClickPointsPanel`, `PresetsStatsPanel`,
`HotkeyCaptureBox` (design §9), README section.
Every control binds to settings and saves through the existing settings save path; the status row reflects
controller state (Aus/Bereit/Läuft · cps · Klicks/Gesperrt/Gestoppt: Grund/Admin-Fenster blockiert). Sub-tab and
widget content survive relocalize/theme rebuild like other widgets. Conflicts from Task 1 shown inline.
README: Gaming Utilities → AutoClicker section rewritten (native clicker features, helper process, blacklist,
whitelist).

## Task 7 — Measurement tool

`tools/measure-clicks.ps1`: opens a WinForms window that records every mouse-down timestamp (QueryPerformanceCounter)
and, after N seconds or on close, prints count, mean CPS, stddev of intervals, min/max. Usage text at the top.
Not run by the agent.
