# WKI AutoClicker — mouse patterns (record a movement, replay it on a hotkey)

Status: requested by Phillip on 2026-10-30, design agreed in chat and approved ("Wir versuchen's einfach"),
not built yet.
Extends `2026-10-28-native-autoclicker-design.md` and `2026-10-29-free-hotkeys-design.md`.
Branch: `feat/mouse-patterns` (from master `1f7efd5`, release 0.18.0).

## 1. Intent

The AutoClicker becomes an all-round input tool ("Allround-Tool für alles: Workstations, Automatisierung auf dem
Desktop"). Phillip wants to record a mouse movement once and replay exactly that movement on a hotkey, as often as
he likes, starting wherever the mouse happens to be:

- **Fishing game (single-player):** a special circling motion has to be made again and again to counter the fish.
  There is no mouse pointer in that game. "Die Maus soll nur die Bewegung machen, die ich dann erlernt habe" - if
  the replay used screen coordinates, the line would be yanked ("sonst verreiße ich die Angel").
- **Skateboard game:** a mouse motion on a field is mirrored onto the board; same motion = same trick. Several
  tricks, each on its own key. Example hotkey: the **left mouse button**, held - the pattern starts the moment he
  presses it, wherever the pointer is (or without any pointer).
- **Minecraft crafting:** he points at an item, holds the left mouse button himself, presses the hotkey, and the
  mouse travels along the recorded path to the inventory slot at the configured speed.
- Desktop automation in general.

Success = the same recorded motion produces the same result in the game every time, it starts instantly on the
hotkey, it stops instantly when the hotkey is released, and it never interferes with the auto clicker.

## 2. Decided by Phillip

1. **Relative only.** A pattern is a movement shape, never a set of screen coordinates. It starts from the current
   mouse position; it also works where no pointer is visible (game camera / raw mouse input).
2. **Movement only.** The pattern does not click. Clicking and holding is done by Phillip (or by the clicker).
3. **Hold to play.** The pattern runs while the hotkey is held and stops immediately on release.
4. **Repeat on/off** per pattern (like the click-point route's "start over"): on = when the pattern ends it
   continues seamlessly with the next pass (relative, from where it ended); off = one pass per press.
5. **Adjustable speed** per pattern; the shape stays the same, only the tempo changes.
6. **Several patterns**, each with its **own hotkey** (any key, mouse button or wheel - the free-hotkey rules) and
   an **"aktiv" checkbox**. The same hotkey may be entered on several patterns; the user ticks the one he needs.
7. **Independent of the clicker.** Patterns and clicker can be armed separately, run at the same time, and may use
   the **same hotkey** (one press starts both).
8. Recording flow (proposed by me, accepted): press a record hotkey, make the motion in the game, press the record
   hotkey again. Idle time at the start and the end is cut off. The tab shows the recorded path as a small drawing.
9. Blacklist lock and the anti-cheat warning apply to patterns too.
10. **"Nur in <Programm>"** per pattern (proposed by me after the review, accepted): a pattern remembers the program
    it was recorded in and by default only plays while that program is in front - otherwise a left-mouse-button
    pattern made for the skateboard game would also fire on every left click on the desktop or in the browser.
    Unticking it makes the pattern work everywhere (desktop automation).
11. The games are offline (the fishing game can also be played as an online party); no anti-cheat concern beyond
    the existing banner. The early in-game check (section 6) stays.

## 3. Goals, non-goals, anti-goals

Goals: everything in section 2; exact, drift-free replay; works in games with a hidden/locked pointer and on the
desktop; no lag between hotkey and movement; persistent patterns that survive restarts and updates.

Non-goals (this change): recording or replaying clicks or keys inside a pattern (full macros); scaling the size of
a pattern; editing a recorded path point by point; export/import or sharing of patterns; a list of several
programs per pattern (one program binding only, decision 10); changing any Windows mouse setting.

Anti-goals (a failure even if it technically works):
- A pattern that jumps to a screen position instead of moving relative to the start (yanks the fishing line, jerks
  a game camera).
- A pattern that keeps moving after the hotkey was released (Hold) or that cannot be stopped.
- The clicker and patterns blocking or disturbing each other, or clicker statistics counting pattern playbacks.
- Recording our own injected movement (from the clicker's click-point glides or from another running pattern).
- The main clipper process hooking or injecting input (architecture rule from the native-clicker design).
- Pattern data bloating `settings.json` or the per-change config push to the helper.

## 4. Behaviour

### 4.1 Pattern list

- Each pattern: Id (stable), Name (max 40 chars, default "Muster 1", "Muster 2", ...), Hotkey (optional; a pattern
  without a hotkey never plays), Active (checkbox), Mode (Halten / Umschalten, default Halten), Repeat (default
  off), Speed (0.25x - 4x, default 1x), Replay type (Automatisch / Zeiger / Spiel (roh), default Automatisch, see
  4.3), Program (exe name of the foreground process when the recording started) with the checkbox
  "Nur in <Programm>" (default on, decision 10), plus read-only facts from the recording (duration).
- Program binding: with "Nur in" on, the hotkey's rising edge only starts the pattern while that program owns the
  foreground window, and a running playback stops as soon as another program comes to the front. Re-recording
  replaces the program with the one of the new recording and keeps the checkbox state. Comparison by exe file
  name, case-insensitive (same rule as the blacklist).
- At most 50 patterns. A pattern only exists after a successful recording (no empty patterns).
- Delete asks for confirmation (like "Alle Klickpunkte löschen?").

### 4.2 Recording

- **Record hotkey**: one global hotkey in the patterns tab (free-hotkey rules; proposed default `Strg+Alt+R` - the
  builder verifies it collides with no clipper default and picks another if it does). Recording requires
  "Mausbewegung bereit" (4.5); otherwise the helper is not running and nothing happens.
- First press starts a recording, second press stops it. While recording, the on-screen label shows
  "Aufnahme läuft · <record hotkey> beendet" (4.6).
- **Target**: by default a recording becomes a **new** pattern. It starts **active** with **no hotkey**, so it is
  harmless until a hotkey is set. Each pattern row has
  "Neu aufnehmen": it marks that pattern as the target of the next recording (shown in the tab: "Nächste Aufnahme
  ersetzt 'Kreis' - <record hotkey> im Spiel drücken", with a cancel link). Re-recording keeps name, hotkey and all
  options and only replaces the motion. The target is UI/main-side state, not persisted.
- What is recorded: only **physical** mouse movement. Our own injected input (tag `0x574B4943`) is never recorded.
  Clicks, keys and the wheel are not recorded.
- Idle time before the first and after the last movement is trimmed. Pauses inside the motion are kept.
- Maximum length 60 s; at 60 s the recording stops by itself and is kept (notice says it was cut).
- A recording with no movement is discarded with a notice ("Keine Bewegung aufgenommen").
- While recording, pattern hotkeys are ignored (no playback can start); the clicker keeps working normally.
- A recording ends without a result (discarded, notice) when: the blacklist lock engages, a hotkey field or picker
  in the clipper opens (existing suppression), "Mausbewegung bereit" is switched off, or the helper ends.
- After a recording a toast confirms it: "'Muster 3' aufgenommen (2,4 s)".

### 4.3 Two tracks, two replay types

Windows gives two different views of the same mouse motion, and each kind of target needs a different one
(research findings, sources in the research notes of this design's session):

- **Raw track** - the sensor counts from Raw Input (`WM_INPUT`, relative records only, physical devices only). This
  is exactly what games with a hidden/locked pointer read. Replayed with **relative** `SendInput` moves
  (`MOUSEEVENTF_MOVE` without `ABSOLUTE`): those arrive in other apps' Raw Input 1:1 and are not affected by pointer
  speed or "Zeigerbeschleunigung verbessern".
- **Pointer track** - the on-screen pointer offsets in pixels (low-level mouse hook positions, after Windows'
  pointer ballistics). This is what a GUI with a visible pointer (Minecraft inventory, desktop) sees. Replayed as
  **anchor + accumulated offset** with absolute moves (`MOUSEEVENTF_MOVE | ABSOLUTE | VIRTUALDESK`), the anchor being
  the pointer position when the playback started. Pixel-exact, independent of mouse acceleration, and still
  relative to the start point (decision 1).

Both tracks are recorded every time. The **replay type** picks one:

- **Automatisch** (default), decided at each playback start:
  - Windows pointer ballistics linear ("Zeigerbeschleunigung verbessern" off, read via `SPI_GETMOUSE`): always
    **Spiel** (relative raw counts). With linear ballistics a relative replay moves the pointer exactly like the
    original hand movement did, and raw-input games get the exact counts - one path that is right everywhere and
    never sends an absolute move. Phillip's PC (checked 2026-10-30): acceleration off, pointer speed 10/20 (1:1).
  - Ballistics non-linear (acceleration on): **Zeiger** if the pointer is visible at that moment
    (`GetCursorInfo`: showing and `hCursor != 0`), otherwise **Spiel**. An absolute move while a game hides the
    pointer would jerk the camera (anti-goal).
  - No classification at recording time; nothing to detect but the two facts above.
- **Zeiger** / **Spiel (roh)**: manual override, always used as chosen.
- If a pattern has no usable raw track (e.g. recorded with a pen tablet, which reports absolute positions),
  Automatisch uses Zeiger while the pointer is visible, otherwise the pointer track as relative moves.

Known limit (documented, not solved): with acceleration on, a game that hides the pointer and reads pointer
movement instead of raw input sees the replay through the non-linear curve, so the motion can come out larger or
smaller than recorded (still identical on every replay). The tab hint names the Windows option.

Consequences to state in the UI hint: in Zeiger mode the pattern takes over the pointer, physical mouse movement
during playback is overridden; in Spiel mode physical movement adds on top.

### 4.4 Playback

- **Hold** (default): the rising edge of the hotkey starts the pattern from the current position; release stops
  it at once, mid-pattern, without finishing or snapping anywhere. Repeat off: after one pass the pattern stops
  even if the key is still held; the next pass needs a new press. Repeat on: it loops until release.
- **Toggle**: a press starts, the next press stops. Repeat off: it also stops by itself after one pass.
- **Speed** scales time only (pauses included); 2x = same shape in half the time.
- Timing follows the recording (the user's own tempo), replayed drift-free: the player computes the cumulative
  target at the current time and sends the difference to what it has already sent, so rounding never accumulates,
  at any speed and over any number of repeats.
- Start latency: the first movement goes out within a few milliseconds of the hotkey press.
- Several patterns running at the same time (same hotkey, or several keys held) add up; no jump when a second one
  joins.
- A pattern that is edited (speed, type, repeat, new recording) while it plays keeps its old settings until it
  stops; deleting or deactivating it stops it.
- The left mouse button as Hold hotkey works and still reaches the game (hooks never swallow input; existing rule).

### 4.5 Independence from the clicker

- New switch **"Mausbewegung bereit"** next to "Clicker bereit" in the AutoClicker status row (always visible,
  whichever sub-tab is open). After the update it is off (like the clicker after 0.18.0).
- The helper process runs while **either** switch is on. With only "Mausbewegung bereit" on, the clicker hotkey,
  start zones and the start button do nothing; with only "Clicker bereit" on, pattern and record hotkeys do nothing.
- The clicker's master-switch hotkey only gates the clicker (my call; patterns have their own switch).
- One press of a hotkey that belongs to the clicker and to patterns starts all of them together.
- Pattern playbacks never appear in clicker runs/statistics and never change the clicker's status pill.
- Known interaction, documented not prevented: when the clicker runs with click points it moves the pointer to
  absolute points and fights a pattern; the patterns tab warns if a pattern shares its hotkey with a clicker that
  uses click points. The clicker's corner/edge/zone failsafes keep watching the pointer as before, also when a
  pattern moved it (safety first).

### 4.6 Safety and feedback

- Blacklist lock: stops every playback and discards a running recording before the helper confirms the lock (the
  main side kills the helper if the confirmation does not arrive within 1 s).
- Hotkey recording or a picker in the clipper (existing suppression) stops playbacks and ignores pattern/record
  hotkeys, so recording a hotkey never starts a pattern.
- A playback stops when the task switcher or task view opens (Alt+Tab, Win+Tab; the clicker's existing
  task-switcher detection) - my call, the safety net for Toggle + Repeat besides the pattern's own hotkey. A plain
  foreground change does not stop it: desktop automation may drag across windows, and a clicker running on the same
  hotkey may click other windows to the front.
- The clicker's whitelist and its stop/pause/start zones stay clicker-only (patterns are independent, decision 7).
- An administrator window in front cannot receive the input (Windows rule); the playback stops and the tab says
  so (existing "BlockedByElevated" idea).
- Mouse-button pattern hotkeys pressed over clipper windows are ignored (existing rule for clicker hotkeys), so a
  click in the clipper's UI never moves the mouse.
- On-screen label (the existing capture-excluded indicator): "Mausbewegung · <Name>" while a pattern plays (shares
  the clicker's "show indicator" option), "Aufnahme läuft · <record hotkey> beendet" while recording (always shown
  when the indicator can be shown at all - otherwise the user cannot tell that recording is on). When the clicker
  runs too, both parts are shown. With AMD's own capture the label stays hidden as today; the toast after the
  recording is the fallback.
- The anti-cheat banner text mentions movement macros as well.

### 4.7 UI: sub-tab "Mausbewegung" / "Mouse patterns"

Placed after "Klickpunkte", before "Presets". Contents, top to bottom:

1. Short explanation + status line ("Bereit · 2 Muster aktiv", "Aufnahme läuft ...", "Spielt: Kreis",
   "Mausbewegung ist aus - oben 'Mausbewegung bereit' einschalten").
2. Record hotkey field (+ cancel/clear like the other hotkey fields), hint how recording works, re-record target
   line (4.2).
3. Pattern list as two-line cards (the widget is narrow; one line would be clipped):
   - Line 1: [aktiv] Name (editable) - small path drawing - duration - [Nur in <Programm>].
   - Line 2: Hotkey field + Abbrechen/Leeren, Halten/Umschalten, [Wiederholen], Tempo (number box, x),
     Wiedergabe (Automatisch / Zeiger / Spiel), "Neu aufnehmen", "Löschen".
   - Under the card: warnings for this pattern (4.8).
4. Empty state: "Noch keine Muster. <record hotkey> drücken, Bewegung machen, nochmal drücken."

The path drawing shows the pointer track (fallback: raw track) scaled to fit, start marked.

### 4.8 Warnings (never block)

Per pattern, shown under its card; aggregated count in the AutoClicker status row:
- two **active** patterns share an overlapping hotkey ("startet zusammen mit 'Kreis'"),
- the pattern hotkey equals the record hotkey,
- the pattern hotkey collides with a clipper hotkey (existing `ClipperOwner` check),
- the existing risky-hotkey hints (bare LMB/RMB, wheel, modifier-only, Esc),
- shares the hotkey with a clicker that uses click points (4.5),
- recording data missing or unreadable ("Aufnahme fehlt - neu aufnehmen"; the pattern cannot play).
Sharing a hotkey with the clicker without click points is a neutral info line, not a warning (it is wanted).

## 5. Architecture (constraints and recommended shape)

Constraint kept: only the helper (`--clicker-host`) hooks, reads raw input and calls `SendInput`.

### 5.1 Helper

- **Recorder** - docks onto the existing `RawInputMonitor.WindowProc` (a process can register raw mouse input to
  only one window; a second registration would steal the watchdog's reference). The monitor moves out of the
  private ownership of `HotkeyHook` (created in `HostMain`, injected). Raw records accepted for the raw track:
  `RIM_TYPEMOUSE`, relative (`usFlags & MOUSE_MOVE_ABSOLUTE == 0`), `hDevice != 0`, `ulExtraInformation != Tag`.
  Pointer track from the low-level mouse hook: `WM_MOUSEMOVE` without our tag, delta = hook `pt` minus the cursor
  position just before the event (successive `pt` differences break in games that re-centre the pointer).
  Both callbacks stay allocation-free and lock-free and only enqueue while a recording is on (outside a recording
  `WM_MOUSEMOVE` stays a no-op); timestamps from the QPC clock (`StopwatchClock`), not `Environment.TickCount`.
  Compaction (1 ms bins, trim, cap) is pure code in `Services/AutoClicker`, unit-tested.
  Note: Windows 11 throttles background raw-input listeners to about 125 Hz with summed deltas - the path stays
  exact, the time resolution of the raw track is ~8 ms; the player interpolates within bins.
- **Player** - its own thread and its own `HighResWaiter` (the engine's waiter has a sticky wake and must not be
  shared). It sleeps while nothing plays; while something plays it ticks at about 1-2 ms, sums all running
  patterns, and issues one `SendInput` per tick (relative for Spiel, absolute for Zeiger; never a 0/0 move). Pure
  planning (cumulative curve, speed, repeat, interpolation, summing) in `Services/AutoClicker`, unit-tested with a
  fake clock and a fake input sink like `ClickEngineTests`.
- **Injector** - `IInputSink` gains a relative `MoveBy(dx, dy)` (tagged like everything else).
- **Hotkeys** - `HotkeyTracker` generalised from "one hotkey + master" to "clicker hotkey + master + a list of
  named bindings" (record hotkey, each active pattern with a hotkey), evaluated in the same `OnKey`/`Resync` under
  the same dispatch lock with the same rebase rule, so one key press fires the clicker and the patterns in one
  ordered pass. Inactive patterns are not registered.
- **Pattern coordinator** - its own gate, one `ActivationStateMachine` per pattern (Hold/Toggle), fan-out of
  lock/unlock/suppression, the over-clipper rule, the program binding (foreground exe at the rising edge, and a
  cheap periodic check while a bound pattern plays - `WindowsHostEnvironment.Foreground` is not thread-safe, so the
  player/coordinator uses its own lookup), the task-switcher stop. Separate from `HostCoordinator`'s gate so a
  playback never waits for the click engine and vice versa. `HostCoordinator` forwards config/lock/picker and now
  also honours `ClickerSettings.Armed` for the clicker side.

### 5.2 Main side

- **Lifecycle**: one rule `HostWanted = Clicker.Armed || MousePatternsArmed`, used everywhere `Armed` decides about
  the helper today (`ClickerController` reconcile/restart/state, `IClickerLock.Armed` feeding the blacklist guard).
- **Settings** (`ClickerSettings`): `MousePatternsArmed`, `PatternRecordHotkey`, `List<MousePattern>` with the
  metadata of 4.1 (no samples). Normalisation like zones (ids, caps, clamps, name fallback "Muster"). The sub-tab
  key list gets the new tab. Schema bump only if the builder needs a forced value.
- **Sample storage**: one file per pattern under `%APPDATA%\WKI_Clipper\mouse-patterns\` (model:
  `CrosshairLibraryService` / `GalleryMetaService`, with corrupt-file handling). Compact encoding; versioned.
- **Protocol**: version bump to 2 (main and helper are always the same exe). Pattern samples travel in their own
  messages (main -> helper after the config at handshake and whenever a recording changes; helper -> main when a
  recording finishes), never inside the per-change config push. Every message stays far below the 8 MiB line
  limit (60 s cap). State back to main carries "recording" and "playing" so the tab, the label and the toast can
  show them. New message types go into both the `JsonDerivedType` attributes and `KnownTypes`.

## 6. Testing

- Unit (pure, no real input): compaction/trim/cap/empty, replay-type choice, encoding round trip and corrupt input,
  playback planning (drift-free at 0.25x/1x/4x, repeat continuation, Hold release mid-pattern, Toggle one-pass,
  summing two patterns, no 0/0 moves), tracker with N bindings incl. same hotkey on clicker + 2 patterns and the
  rebase rule, coordinator (lock stops playback before the lock is confirmed, suppression, over-clipper,
  program binding at start and while playing, task switcher, recording discarded on lock), protocol round trips, `HostWanted` lifecycle, settings
  normalisation + idempotence, warnings, sub-tab list (now 6). Full suite green, 0 warnings.
- Early in-game check (first thing after the helper can record and replay, before the UI is built): record a
  motion and replay it with relative injection in the actual fishing and skateboard games, to prove those games
  accept injected relative movement (they see it with `hDevice == 0` / the injected flag; a game that filters it
  cannot be served by `SendInput` at all, and a virtual-device driver is out of scope).
- Live with Phillip (after "Hände weg" and a game check), with a measuring helper similar to
  `tools/measure-clicks.ps1` that shows received raw counts and pointer path, so recorded vs replayed motion can be
  compared without a game: record + replay at 1x/2x/0.5x with Repeat, Zeiger and Spiel; then Minecraft inventory,
  the skateboard game with LMB as Hold hotkey, the fishing game; clicker + pattern on the same hotkey.

## 7. My calls (Phillip: please check these while reading)

1. A new pattern starts active and without hotkey (harmless until a hotkey is set).
2. Toggle exists for patterns too (default Halten).
3. Repeat off in Hold mode = one pass per press, even if the key stays held.
4. Speed range 0.25x - 4x; max 60 s per recording; max 50 patterns.
5. "Mausbewegung bereit" sits next to "Clicker bereit" in the status row; the master-switch hotkey does not affect
   patterns.
6. Playback stops on Alt+Tab / task view, not on every foreground change.
7. While recording, pattern hotkeys are ignored.
8. Proposed record hotkey default `Strg+Alt+R`.
9. Automatic Zeiger/Spiel choice from the Windows acceleration setting and pointer visibility, manual override
   (4.3).
10. The clicker's whitelist and zones do not apply to patterns.

## 8. Left to the builder

Exact class names and file split, sample encoding, tick rate within 1-2 ms, classification thresholds, how the
player combines Zeiger and Spiel patterns running at the same time (rule: no jumps, motions add up), protocol
message shapes, card layout details within 4.7, toast texts beyond the ones quoted, the measuring helper.

## 9. Found on the way (fix in the same change)

`Native/User32.Input.cs` `RAWMOUSE` lacks the 2-byte padding after `usFlags`: `usButtonFlags` sits at offset 2
instead of 4 (`lLastX`/`lLastY`/`ulExtraInformation` are correct). Today the watchdog therefore never sees pure
button/wheel raw events (harmless direction). The recorder works in exactly this struct, so it gets fixed with a
`Marshal.OffsetOf` test.
