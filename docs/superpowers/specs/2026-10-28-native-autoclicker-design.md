# WKI AutoClicker (native) — Design

Status: approved by Phillip on 2026-10-28 (design, one blacklist, hotkey Ctrl+Alt+Y).

## 1. Goal

A native auto clicker inside the "Gaming Utilities" widget, AutoClicker tab: rate or duration, randomization, duty
cycle, mouse or keyboard output, double click, hotkeys with a master switch, limits, failsafe corners/edges, zones,
click points, whitelist, presets and statistics, integrated with the clipper (theme, localisation, game blacklist,
capture exclusion). The clicker has no app chrome of its own (themes, background media, updater, portable mode,
autostart, diagnostics export); it lives inside the clipper.

## 2. Architecture

```
WKI_Clipper.exe (main, WPF)                       WKI_Clipper.exe --clicker-host <pipe> <parentPid>
  AutoClickerView (sub-tabs)                         (no WPF windows, no tray, no mutex, own log file)
  ClickerController ──── named pipe, JSON lines ────  HostServer (pipe, protocol v1)
    starts/stops host, mirrors state                  HotkeyHook thread (WH_KEYBOARD_LL + WH_MOUSE_LL, message loop)
  AutoClickerGuard (blacklist:                        ClickEngine thread (high-res waitable timer, SendInput)
    native lock)                                      StartZoneMonitor (100 ms)
  Pickers (click points, zones), indicator overlay
```

Why a separate process (same exe, special mode):
- The main clipper deliberately does no input injection (see Native/XInput.cs). Global LL hooks and SendInput
  live only in the host, which runs only while the user has switched "Clicker bereit" on.
- Click timing is not disturbed by GC pauses of the big WPF/capture process.
- The guard can always stop clicking for sure (lock, and kill as last resort). The host is put into the existing
  `KillOnCloseJob` and also exits when the pipe closes or the parent process exits.
- A killed helper cannot let go of what it held (keyboard hold, duty hold). Whenever the clipper kills a helper that got
  through the handshake (lock not confirmed, goodbye timeout, dispose, unhandled exception of the clipper), it starts
  `WKI_Clipper.exe --clicker-release <button|-> <vk> <shift>`: a short process outside the job that sends blind ups for
  the configured output and exits (detected before the mutex like host mode). Known gap, accepted: when the clipper is
  ended from outside (TerminateProcess), the job kills the helper at the same moment and nobody releases.

Host mode is detected at the very start of `App.OnStartup` (before the single-instance mutex, settings load, tray,
logger file of the main app). It runs the host and calls `Shutdown` when the host ends. It logs to
`%LOCALAPPDATA%\WKI_Clipper\wki_clicker_host.log` (own file, size-capped like the main log).

## 3. Settings (schema v12)

`AppSettings.AutoClicker` (exists since v11: `Blacklist`, `WarningAcknowledged`, tab key) gains a `Clicker`
sub-object `ClickerSettings`. All values are clamped by a pure `ClickerSettingsRules.Normalize`.

| Field | Default | Range / values |
|---|---|---|
| Armed ("Clicker bereit") | false | host runs only while true |
| RateInputMode | Rate | Rate, Duration |
| ClickSpeed | 25 | 1..500, 1..1000 with ExtendedSpeedLimit |
| ClickUnit | Second | Second, Minute, Hour, Day |
| DurationMs | 40 | 1 .. 999h59m59s999ms |
| ExtendedSpeedLimit | false | |
| SpeedRandomizationEnabled / Percent | true / 35 | 0..200 |
| DutyCycleEnabled / Percent | true / 45 | 0..100 |
| DutyMode | Click | Click, Hold (button/key stays down until stop) |
| InputType | Mouse | Mouse, Keyboard |
| MouseButton | Left | Left, Right, Middle |
| KeyboardVk / KeyCase | 0 / Lower | VK; case applies to A–Z only |
| DoubleClick | false | UI caps rate at 10 cps while on |
| Hotkey | Ctrl+Alt+Y | ClickerHotkey (see 5) |
| ActivationMode | Toggle | Toggle, Hold |
| StrictModifiers | false | extra held modifiers block a match |
| MasterHotkey / MasterMode | none / Toggle | none = always allowed |
| ClickLimitEnabled / ClickLimit | false / 1000 | 1..100,000,000 |
| TimeLimitEnabled / TimeLimit / TimeLimitUnit | false / 60 / Seconds | Seconds, Minutes, Hours |
| CornerStopEnabled / CornerSizePx | true / 50 | 1..500, per monitor |
| EdgeStopEnabled / EdgeSizePx | true / 40 | 1..500, per monitor |
| TaskSwitcherStopEnabled | true | Alt+Tab / Task View in front |
| ZonesEnabled / Zones | false / [] | ClickerZone {Id, Name, X, Y, W, H (virtual-screen physical px), Action Stop/Pause/Start, Enabled} |
| ClickPointsEnabled / ClickPoints / StopWhenComplete | false / [] / false | ClickPoint {X, Y, Clicks 1..999999, Radius 0..9999} |
| WhitelistEnabled / Whitelist | false / [] | same entry type as the blacklist; foreground exe must match |
| Presets | [] | user presets (factory presets are code constants) |
| Stats | empty | TotalClicks, TotalSeconds, Sessions, LastRuns (max 100: StartUtc, Seconds, Clicks, StopReason) |
| ShowActiveIndicator | true | |
| SubTab | "simple" | |

The game **blacklist (v11) is the only blacklist**: a blacklisted program that runs or is in front locks the native
clicker. "Blacklist ignorieren" (session only) lifts the lock. `WhitelistEnabled`: clicks only while the foreground
exe is on the whitelist; an empty enabled whitelist shows a warning and blocks starting.

## 4. Click engine (host)

Rate: interval = 1/n s, 60/n, 3600/n or 86400/n; duration mode = DurationMs. Batching (no double click):
> 500 cps → 3 cycles per batch, >= 50 → 2, else 1. Randomization: each batch duration drawn from a log-normal
distribution (mean = base, σ = base × pct / 100), floor 1 ms; the schedule is absolute (next deadline += drawn
duration), so the long-run mean equals the set rate. Exception: if the schedule falls more than 1 s behind
(suspend/freeze), the deadline is reset to now and the missed clicks are dropped instead of burst; smaller lags are caught up.
Duty: hold = interval × duty%, capped by rate: > 500 cps → 1 %,
>= 200 → 30 %, >= 100 → 70 %, >= 50 → 98 %; duty disabled → hold 0; a 0 ms hold with several cycles per batch sends
all down/up pairs in one SendInput call. DutyMode Hold → press once, release on stop.

Keyboard: scancode SendInput (extended flag where needed), our own `dwExtraInfo` tag (`0x574B4943`, "WKIC").
KeyCase for A–Z wraps Shift when CapsLock differs from the wanted case. Keyboard hold (keyboard and (duty >= 100 or
DutyMode Hold)): key down, wait the Windows repeat delay (SPI_GETKEYBOARDDELAY → 250/500/750/1000 ms), then key-down
repeats at the Windows repeat rate (SPI_GETKEYBOARDSPEED → 2.5..30/s), key up on stop; each repeat counts.
Every SendInput return value is checked; a failed release is retried; Shift pressed for case is always released.

Double click: one cycle = press → release → gap → press → release, both presses honour the duty
hold, the second press starts within 90 % of GetDoubleClickTime after the first, everything capped to fit the cycle;
counts 2 clicks; the click limit can cut the last pair to a single click; no batching.

Timing: dedicated thread, highest priority; `CreateWaitableTimerExW(CREATE_WAITABLE_TIMER_HIGH_RESOLUTION)` with
absolute deadlines, woken ~1 ms early, then a short spin to the deadline; stop/pause signals wake the wait
immediately (WaitForMultipleObjects on timer + stop event). No global timer-resolution change.
Fallback when the high-resolution timer is unavailable: normal waitable timer + timeBeginPeriod(1) only while running.
Host GC: workstation, `GCLatencyMode.SustainedLowLatency`, no allocations in the loop.

Start: in Toggle mode the first click waits until the hotkey's modifiers are physically released (so the
first click is no Ctrl+click). On every stop all buttons/keys the engine pressed are released.

Click points: cursor jumps (cps >= 50) or moves along a quad-eased Bézier path (< 50 cps, 20–60 % of the interval,
max 200 ms) to the point, clicks `Clicks` times, next point; loops unless StopWhenComplete ("Alle Klickpunkte
erledigt"). Jitter: uniform inside `Radius`, plus a 21.6 % chance of an extra offset up to 2 px. Without click points
the cursor is never moved. Moves use absolute virtual-desktop coordinates (multi-monitor, negative coordinates).

Stop checks every loop iteration, in this order, each with a localized reason shown in the UI:
click limit → stop zone under cursor → corner (per monitor) → edge (per monitor) → Alt+Tab/Task View in front
(window classes TaskSwitcherWnd, TaskViewWindow, WindowsSwitchWindow, MultitaskingViewFrame, XamlExplorerHostIslandWindow when its process is
explorer.exe - deliberately broad: other explorer XAML-island surfaces such as Snap Assist may also stop the run; over-stopping is
the accepted failsafe direction) → whitelist → lock from the clipper (blacklist) → time limit. Other reasons: hotkey, hold released,
master switch, all click points done, left start zone, picker opened, clipper closed.
Pause zones: clicking suspends while the cursor is inside (10 ms poll) and resumes on exit. Start zones: a 100 ms
monitor starts the clicker when the cursor enters (while armed, idle, unlocked) and stops it on exit if the zone
started it.

Elevated foreground: when SendInput injects fewer events than requested or the foreground process is elevated
and the host is not, the state carries `BlockedByElevatedWindow = <exe>`; the UI shows "Ein Admin-Fenster (<exe>)
blockiert die Klicks". No restart-as-admin.

## 5. Hotkeys (host)

`ClickerHotkey { Modifiers: set of Ctrl|LCtrl|RCtrl|Alt|LAlt|RAlt|Shift|LShift|RShift|Win|LWin|RWin; Keys: 0..5 VKs
incl. mouse VK_LBUTTON/RBUTTON/MBUTTON/XBUTTON1/XBUTTON2 and the wheel pseudo keys 0x101..0x104 }`, at least one
modifier or key (free hotkeys, `2026-10-29-free-hotkeys-design.md`: modifier-only, bare left button, Esc, every VK and
the wheel are allowed; a wheel direction counts as held while notches keep coming, released after 250 ms of quiet).
Generic modifier = either side. Match = all required
modifiers down, all keys down, and (StrictModifiers) no other modifier down. Detection from LL hooks that track
the physical state and skip events carrying our tag (other injected input is still counted as physical). Hooks
observe only, never swallow. Toggle: rising edge of the match flips running. Hold: match starts, losing the match
stops ("Hold-Hotkey losgelassen"). Master switch: Toggle flips allowed, Hold = allowed only while held; not allowed →
hotkey ignored, running clicker stops ("Master-Schalter"). Hotkeys are ignored while a picker is open (clipper tells
the host). Conflict rules (pure, in the main app): only a missing output key and an empty whitelist block the start.
Everything else is a warning shown in the UI: output key/button inside a hotkey, bare left/right button, modifier-only,
Esc, wheel, start and master hotkey overlapping, a combo equal to one of the clipper's RegisterHotKey bindings.
Mouse hotkeys (buttons and wheel) do not fire over the clipper's own windows.

## 6. IPC protocol v1 (JSON lines over a named pipe)

Pipe name `WKI_Clipper.Clicker.<parentPid>.<16 hex>`; the main app creates the server, the host connects (so a
foreign process cannot impersonate the main app easily; pipe ACL = current user only).
Main → host: `hello{v}`, `config{settings}`, `lock{reason}`, `unlock`, `start`, `stop`, `picker{active}`, `shutdown`.
Host → main: `hello{v,pid}`, `state{running, armed, locked, reason, clicks, cps, elapsedMs, blockedByElevated}`
(on change, at most 10/s while running), `runEnded{startUtc, seconds, clicks, reason}`, `log{level,text}`.
Unknown message types are ignored; a version mismatch ends the host.

## 7. Main-app side

`ClickerController` (owned by AppHost): starts the host while Armed (and the app runs), restarts it once on an
unexpected exit (then shows an error), pushes config on every settings change (debounced 200 ms), mirrors state to
the UI, writes stats on `runEnded`. `AutoClickerGuard` gains the native target: blocker present and not ignored →
`lock{reason}` (+ stop), else `unlock`; if the host does not confirm a lock within 1 s it is killed.
Indicator: small click-through topmost label "AutoClicker · 25 cps" while running, excluded from capture via
the existing capture-exclusion mechanism; it must never appear in clips/stream and must not cause a replay-buffer
restart when it appears; if the active capture path cannot exclude it, it is not shown.
Toasts only for a blacklist lock and for a click/time limit end; every other stop reason shows in the state pill.

## 8. Pickers (main app)

One transparent topmost window per monitor (PerMonitorV2-correct physical coordinates). Click-point picker: click
adds a point (shows numbered markers incl. existing points), right-click or Esc finishes, Backspace removes the last.
Zone picker: drag a rectangle; after release a small inline chooser sets Stop/Pause/Start (default Stop); Esc
finishes. Existing zones are drawn (disabled ones dimmed). While a picker is open the host gets `picker{true}` and a
running clicker stops.

## 9. UI (AutoClicker tab)

Always visible on top: warning banner (until acknowledged, existing), status row: toggle "Clicker bereit
(Hotkey: Strg+Alt+Y)", state pill (Aus / Bereit / Läuft · 25 cps · 1.234 Klicks / Gesperrt: <Spiel> auf der
Blacklist / Gestoppt: <Grund>), Start/Stop button.
Sub-tabs (TabButtonStyle strip): Einfach | Erweitert | Zonen & Schutz | Klickpunkte | Presets.
- Einfach: rate + unit, mouse button or keyboard key (+ case), Toggle/Hold, hotkey capture.
- Erweitert: duration mode, extended limit, randomization, duty cycle + Click/Hold, double click, click and time
  limits, strict modifiers, master hotkey + mode.
- Zonen & Schutz: corners/edges (+ sizes), Alt+Tab stop, zones (list, picker, action, enable, delete), Blacklist
  (existing UI moves here) + "Blacklist ignorieren", whitelist mode + list.
- Klickpunkte: enable, list (x, y, clicks, radius, up/down, delete), "Punkte wählen", stop when complete.
- Presets: 3 factory presets (Standard 25/45/35, Rapid Fire 100/20/15 + limit 5000, Precision 10/60/10) and user
  presets (apply, save as, rename, delete, export/import JSON, names ≤ 40 chars); statistics (totals, last runs, reset).
Hotkey capture control (main and master hotkey): click to capture; every input counts - keys incl. Esc, Backspace,
Print Screen, System/media keys (read from the window's own messages so the VKs equal the helper's hook; sided
modifiers when "Links/rechts unterscheiden" is on, AltGr = LCtrl+RAlt / Ctrl+Alt), modifiers alone, up to 5 keys,
mouse buttons incl. the left one and the wheel while the cursor is over the field. Done when everything is released
(a wheel notch at once). Buttons next to the field: "Abbrechen" (while capturing) and "Standard" (main hotkey back to
Ctrl+Alt+Y) / "Leeren" (no master hotkey); a click beside the field or focus loss cancels. The hotkey's warnings
show under its row.

## 10. Testing

Pure logic fully unit-tested: settings normalize + migration v12, cadence/batching/duty/randomization (statistical
mean), double-click timing, keyboard repeat timing, stop-check order, zones (pause/start state machine), hotkey
matching + toggle/hold/master state machines + conflicts, click-point sequencing + jitter bounds + Bézier path,
presets import/export, IPC message round trip and version handling, guard native lock decisions.
Engine loop tested deterministically with fake clock / fake input sink / fake environment (no real input).
Live (only with Phillip present, hands off): `tools/measure-clicks.ps1` opens a counting window; the clicker runs at
10/50/100/500 cps with the cursor over it; measured CPS and jitter are compared with the target.
