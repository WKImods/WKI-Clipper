# WKI AutoClicker mouse patterns — Implementation Plan

> For agentic workers: implement ONE task per run, test-first, commit, then stop. Read the spec first. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** record a physical mouse movement and replay it relative to the current position on a hotkey (several
patterns, Hold/Toggle, repeat, speed, "Nur in <Programm>"), independent of and concurrent with the clicker.
**Architecture:** spec section 5. Pure logic in `WKI_Clipper/Services/AutoClicker/` (namespace
`WKI_Clipper.Services.AutoClicker`), helper code in `WKI_Clipper/ClickerHost/`, UI in `WKI_Clipper/Views/AutoClicker/`.
**Tech:** .NET 8, WPF, C# latest, nullable on, xUnit 2.9 in `WKI_Clipper.Tests`.
**Spec:** `docs/superpowers/specs/2026-10-30-mouse-patterns-design.md` (section numbers below refer to it).

Repo `C:\Users\cr4zy\Documents\CLAUDE_Projects\WKI_Clipper`, branch `feat/mouse-patterns` (from master `1f7efd5`).

## Global Constraints

- Only the helper (`--clicker-host`) hooks, reads raw input and calls `SendInput`; the main app never does.
- Every injected event carries `InputInjector.Tag` (`0x574B4943`); nothing carrying it is ever recorded.
- Hook and raw-input callbacks stay allocation-free and lock-free; they only enqueue (existing HotkeyPump rule).
- Recording/playback timestamps come from `IClock` (`StopwatchClock`, QPC), never `Environment.TickCount`.
- The pattern player has its own thread and its own `HighResWaiter` (never share the engine's).
- Limits: `MaxPatterns = 50`, speed `0.25 .. 4.0` (default `1.0`), recording `MaxDurationMs = 60_000`, bin `1 ms`,
  name max `ClickerSettingsRules.MaxNameLength` (40), default name `L.T("Muster", "Pattern") + " " + n`.
- Defaults of a new pattern: `Active = true`, `Hotkey = null`, `Mode = ActivationMode.Hold`, `Repeat = false`,
  `Speed = 1.0`, `Replay = PatternReplay.Auto`, `OnlyInProgram = false`. `MousePatternsArmed` defaults to false.
- Proposed record hotkey default `Strg+Alt+R`; if `ClickerConflicts.ClipperOwner` reports a clash with a clipper
  default binding, pick the first free of `Strg+Alt+B`, `Strg+Alt+N` and note it in the commit message.
- Warnings never block a start (spec 4.8). Clicker conflict texts and existing tests stay unchanged.
- All user-visible strings via `L.T(german, english)` with real umlauts; only existing DynamicResource palette keys
  and styles. Code/comments English, matching surrounding style and comment density.
- Commit messages via a UTF-8 file WITHOUT BOM and `git commit -F`; last line
  `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never push. Never mention any other auto clicker
  product in code, comments, docs or commits.
- While working: targeted `dotnet test --filter` runs; build with 0 warnings. Full suite only at the end of a task
  (known flaky: `FFmpegJobRunnerIntegrationTests.A_healthy_slow_job_is_not_mistaken_for_a_hung_one` - rerun alone).
- Never run the app or the helper, never inject real input, never drive UI. Tests use fakes
  (`ClickerHostFakes.cs`, `ManualTime.cs`, `InMemoryPipe.cs`, `WpfTestSupport`).
- Timebox ~10 min per stuck problem; if still stuck, write it down in the task report and move on.

## Review Focus

1. A Hold hotkey whose release is lost (hook dropped, `Resync`) - the pattern must stop, never keep moving.
2. Any settings save while a pattern plays (config push, rebase rule) - a held Hold pattern keeps playing, does
   not restart, and stops on release.
3. Hotkey pressed before the pattern's data reached the helper (right after handshake) or with corrupt data -
   nothing plays, nothing throws.
4. Helper killed or locked during a recording - no partial pattern is created; the user gets a notice.
5. Absolute pointer replay on a multi-monitor desktop with a negative virtual-screen origin and at screen edges -
   correct normalisation, clamped, never a jump.

---

## Task 1 — Model, rules, sample codec and store, warnings (pure + file IO)

**Files:** modify `Models/ClickerSettings.cs`, `Services/AutoClicker/ClickerSettingsRules.cs`,
`Services/AutoClicker/ClickerConflicts.cs`, `Services/AutoClicker/ClickerUiRules.cs` (`HotkeyRecorder.RecordsSided`);
create `Models/MousePattern.cs`, `Services/AutoClicker/MousePatternRules.cs`, `Services/AutoClicker/PatternData.cs`,
`Services/AutoClicker/PatternCodec.cs`, `Services/AutoClicker/MousePatternConflicts.cs`, `Services/MousePatternStore.cs`;
tests `MousePatternRulesTests.cs`, `PatternCodecTests.cs`, `MousePatternStoreTests.cs`,
`MousePatternConflictsTests.cs`, update `ClickerSettingsTests.cs`.

**Produces:**
- `enum PatternReplay { Auto, Pointer, Raw }` (JsonStringEnumConverter, like `ActivationMode`).
- `sealed class MousePattern { string Id; string Name; ClickerHotkey? Hotkey; bool Active; ActivationMode Mode;
  bool Repeat; double Speed; PatternReplay Replay; string? Program; bool OnlyInProgram; double DurationMs;
  int Revision; }` - `Program` = exe file name; `Revision` grows with every new recording (helper sync key).
- `ClickerSettings`: `bool MousePatternsArmed`, `ClickerHotkey PatternRecordHotkey = MousePatternRules.DefaultRecordHotkey()`,
  `List<MousePattern> MousePatterns = new()`.
- `MousePatternRules`: constants above; `static bool HostWanted(ClickerSettings s) => s.Armed || s.MousePatternsArmed`;
  `static void Normalize(ClickerSettings s)` (ids like `NormalizeZones`, cap 50, clamp speed, `NaN` -> 1.0, name via
  `CleanName(name, fallback)`, `NormalizeHotkey(p.Hotkey, required:false)`, record hotkey required);
  `static string NextName(IEnumerable<MousePattern>)`; `static ClickerHotkey DefaultRecordHotkey()`.
- `ClickerSettingsRules.CleanName(string?, string fallback = "Preset")`; `SubTabs` = `simple, advanced, zones, points,
  patterns, presets`; `Normalize` calls `MousePatternRules.Normalize`.
- `sealed record PatternTrack(int[] AtMs, int[] Dx, int[] Dy)` (1 ms bins, `AtMs` strictly increasing, first >= 0,
  zero-motion bins omitted) with `static PatternTrack Empty`, `int Count`, `(long X, long Y) Total`;
  `sealed record PatternData(PatternTrack Raw, PatternTrack Pointer, double DurationMs)`.
- `PatternCodec.Encode(PatternData) -> string` (compact, versioned prefix, e.g. `"p1:"` + base64 of zig-zag varints
  of time deltas and dx/dy) and `PatternCodec.TryDecode(string?, out PatternData? data) -> bool` (rejects: wrong
  prefix, bad base64, non-increasing times, more than `MaxDurationMs` or 120_000 bins per track, length mismatch).
- `public interface IMousePatternStore { void Save(string id, PatternData data); bool TryLoad(string id, out PatternData? data); void Delete(string id); }`
  and `MousePatternStore(string appDataDir) : IMousePatternStore`: folder `mouse-patterns`, file `<id>.wkipattern`
  (the encoded string); Save writes `.tmp`, then replaces; TryLoad is false on missing/corrupt and never throws;
  ids validated as 32 hex chars.
- `sealed record PatternWarning(string PatternId, string Message, bool Info)`;
  `MousePatternConflicts.Evaluate(AppSettings s, Func<string, bool> hasData) -> IReadOnlyList<PatternWarning>`
  covering spec 4.8 (overlap between two ACTIVE patterns via `ClickerHotkey.Overlaps(a, b, strict)`; equal to the
  record hotkey; `ClipperOwner`; the risky-hotkey texts reused from `ClickerConflicts` (extract the text part of
  `AddHotkeyRisks` into an internal helper, clicker texts unchanged) plus the "wirkt überall" hint for a bare mouse
  button without `OnlyInProgram`; clicker uses click points and shares the hotkey; data missing; Info line when
  sharing the hotkey with the clicker without click points). `HotkeyRecorder.RecordsSided` also looks at pattern
  and record hotkeys.

- [ ] Step 1: tests. `Normalize_gives_patterns_ids_caps_and_clamps` (51 patterns -> 50; speed 9 -> 4, 0 -> 0.25,
  NaN -> 1; duplicate/empty ids replaced; blank name -> "Muster 1"); `Normalize_is_idempotent` with patterns;
  `SubTab_patterns_survives_normalize`; `HostWanted_is_true_when_either_switch_is_on` (4 cases);
  `Codec_round_trips_both_tracks`; `Codec_rejects_tampered_input` (each rejection above); `Store_round_trips_and_survives_corrupt_file`
  (corrupt file -> TryLoad false, no throw; Delete removes); `Warnings_flag_overlapping_active_patterns_only`
  (inactive duplicate -> no warning); `Warning_for_bare_mouse_button_without_program_binding`;
  `Sharing_the_clicker_hotkey_is_info_not_warning`; `Clicker_conflict_texts_are_unchanged` (existing tests green).
- [ ] Step 2: run the new tests, expect FAIL (types missing).
- [ ] Step 3: implement the Produces block.
- [ ] Step 4: `dotnet test --filter "FullyQualifiedName~MousePattern|FullyQualifiedName~PatternCodec|FullyQualifiedName~ClickerSettings|FullyQualifiedName~ClickerConflicts"` green; full suite green; commit `feat(clicker): mouse pattern model, codec, store and warnings`.

## Task 2 — Pure recording, playback and preview math

**Files:** create `Services/AutoClicker/PatternRecording.cs`, `Services/AutoClicker/PatternCurve.cs`,
`Services/AutoClicker/PatternReplayRules.cs`, `Services/AutoClicker/PatternPreview.cs`; tests
`PatternRecordingTests.cs`, `PatternCurveTests.cs`, `PatternReplayRulesTests.cs`, `PatternPreviewTests.cs`.

**Consumes:** `PatternTrack`, `PatternData`, `MousePatternRules` (Task 1).
**Produces:**
- `PatternRecording`: `PatternRecording(double startMs)`; `void AddRaw(double atMs, int dx, int dy)`;
  `void AddPointer(double atMs, int dx, int dy)`; `bool LimitReached(double nowMs)` (60 s after the FIRST
  movement); `PatternRecordingResult Finish()`. Trim: time 0 = first non-zero event of either track; end = last
  non-zero event; 1 ms bins summed; events after the limit dropped.
  `enum PatternRecordingOutcome { Ok, Cut, Empty, Cancelled }`;
  `sealed record PatternRecordingResult(PatternRecordingOutcome Outcome, PatternData? Data)`.
- `PatternCurve` (immutable, built from one `PatternTrack` + `durationMs`): `(double X, double Y) At(double tMs)`
  = cumulative offset with linear interpolation inside each bin's 1 ms (a bin at `AtMs = k` moves over `(k-1, k]`),
  clamped to the total after the end. `double DurationMs`, `(long X, long Y) Total`.
- `PatternCursor` (mutable per running pattern): `PatternCursor(PatternCurve curve, double speed, bool repeat)`;
  `bool Advance(double elapsedMs, out int dx, out int dy)` - target = `round(At(elapsed*speed))` plus
  `Total * passes` when repeating; returns the integer difference to what it already returned (drift-free);
  returns false once a non-repeating pass has delivered its full total; `(long X, long Y) Emitted`.
- `enum ResolvedReplay { RelativeRaw, RelativePointer, AbsolutePointer }`;
  `PatternReplayRules.Resolve(PatternReplay setting, bool accelerationOn, bool pointerVisible, bool hasRaw)`:
  Raw -> RelativeRaw (RelativePointer if !hasRaw); Pointer -> AbsolutePointer; Auto -> spec 4.3 exactly
  (acceleration off -> RelativeRaw, or RelativePointer if !hasRaw; acceleration on -> AbsolutePointer if pointer
  visible else RelativeRaw/RelativePointer).
- `PatternPreview.Fit(PatternTrack track, double width, double height, double padding) -> IReadOnlyList<DipPoint>`:
  cumulative path from (0,0), aspect kept, centred, at most 200 points (keep first/last), empty track -> empty.

- [ ] Step 1: tests. `Leading_and_trailing_idle_is_trimmed`; `Pauses_inside_are_kept`; `No_motion_is_Empty`;
  `Sixty_seconds_cuts_and_reports_Cut`; `Own_bins_sum_events_in_the_same_ms`; `Curve_interpolates_inside_a_bin`;
  `Cursor_is_drift_free_at_quarter_one_and_four_times_speed` (sum of all dx/dy equals `Total` exactly, also after
  1000 small ticks); `Repeat_continues_from_the_end_without_jump` (3 passes -> 3 * Total);
  `Non_repeating_cursor_stops_after_one_pass`; `Resolve_matches_spec_table` (Theory over all combinations);
  `Preview_fits_keeps_aspect_and_caps_points`.
- [ ] Step 2: run, expect FAIL. Step 3: implement. Step 4: filtered tests + full suite green; commit
  `feat(clicker): pattern recording, playback curve and preview math`.

## Task 3 — Native layer, relative injection, recorder (helper)

**Files:** modify `Native/User32.Input.cs` (RAWMOUSE padding fix; `CURSORINFO`, `GetCursorInfo`, `CURSOR_SHOWING`;
`SPI_GETMOUSE` read helper if missing), `ClickerHost/InputInjector.cs`, `ClickerHost/RawInputMonitor.cs`,
`ClickerHost/HotkeyHook.cs`, `ClickerHost/HostMain.cs` (monitor created here and injected); create
`ClickerHost/PatternRecorder.cs`; tests `RawInputEventsTests.cs` (+offsets), `InputInjectorTests.cs`,
`PatternRecorderTests.cs`; update fakes in `ClickerHostFakes.cs`.

**Consumes:** `PatternRecording`, `PatternRecordingResult` (Task 2).
**Produces:**
- `IInputSink.MoveBy(int dx, int dy) -> bool` (relative `MOUSEEVENTF_MOVE`, tagged, no press tracking; dx=dy=0
  returns true without sending).
- `internal interface IRawMouseSink { void OnRawMouse(double atMs, int dx, int dy); }`, set via
  `RawInputMonitor.MouseSink` (volatile); called from `WindowProc` only for `RIM_TYPEMOUSE`, relative
  (`(usFlags & MOUSE_MOVE_ABSOLUTE) == 0`), `hDevice != 0`, `ulExtraInformation != (uint)InputInjector.Tag`,
  non-zero motion. `RawInputMonitor(IClock clock)` gets the clock; `HotkeyHook` takes the monitor in its
  constructor instead of creating it (still disposes it).
- `internal interface IPointerSink { void OnPointerMove(double atMs, int dx, int dy); }`, set via
  `HotkeyHook.PointerSink` (volatile); `MouseProc` for `WM_MOUSEMOVE` without our tag computes
  `pt - GetCursorPos()` (position before the event) and calls it only while the sink is set; otherwise
  `WM_MOUSEMOVE` stays an immediate pass-through.
- `PatternRecorder : IRawMouseSink, IPointerSink, IDisposable` - `PatternRecorder(IClock clock)`;
  `bool IsRecording`; `void Start(string? program)`; `PatternRecordingResult Stop()`; `void Cancel()`;
  `string? Program`; `event Action? LimitReached`. Callbacks write into two preallocated single-producer rings
  (no allocation, no lock); a drain (on `Stop` and every ~20 ms from a timer while recording) moves them into a
  `PatternRecording`; ring overflow drops events and is logged once.

- [ ] Step 1: tests. `Rawmouse_layout_matches_native` (`Marshal.OffsetOf`: usFlags 0, usButtonFlags 4,
  usButtonData 6, ulRawButtons 8, lLastX 12, lLastY 16, ulExtraInformation 20, size 24);
  `MoveBy_sends_one_relative_tagged_move`; `MoveBy_zero_sends_nothing`; `Raw_sink_skips_absolute_injected_and_deviceless_records`
  (via the pure filter function used by `WindowProc`); `Recorder_collects_both_tracks_and_trims`;
  `Recorder_cancel_discards`; `Recorder_reports_limit_once`; existing `RawInputEvents`/watchdog tests green after
  the layout fix (adjust expectations only where they encoded the wrong offset, and say so in the commit).
- [ ] Step 2: run, expect FAIL. Step 3: implement. Step 4: filtered + full suite green; commit
  `feat(clicker-host): relative injection, raw/pointer recording sinks, RAWMOUSE layout fix`.

## Task 4 — Pattern player (helper)

**Files:** create `ClickerHost/PatternPlayer.cs`, `ClickerHost/PatternEnvironment.cs`; test `PatternPlayerTests.cs`.

**Consumes:** `IInputSink.MoveBy/MoveTo`, `IClock`, `IWaiter`, `PatternCurve`, `PatternCursor`,
`PatternReplayRules.Resolve`.
**Produces:**
- `internal interface IPatternEnvironment { bool PointerVisible { get; } bool AccelerationOn { get; } PixelPoint CursorPosition { get; } }`
  (`WindowsPatternEnvironment`: `GetCursorInfo` showing and `hCursor != 0`; `SPI_GETMOUSE[2] != 0`; `GetCursorPos`).
- `sealed record PatternPlayRequest(string Id, PatternData Data, double Speed, bool Repeat, PatternReplay Replay)`.
- `PatternPlayer(IInputSink sink, IWaiter waiter, IClock clock, IPatternEnvironment env) : IDisposable`;
  `void Play(PatternPlayRequest r)` (restarts if already playing); `void Stop(string id)`; `void StopAll()`;
  `IReadOnlyList<string> Playing` (snapshot); `event Action<string>? Finished` (one pass done or stopped);
  `event Action<bool>? Blocked` (true when `SendInput` refused - all playbacks stop).
  Thread sleeps on the waiter while idle; `TickMs = 1.0` while playing. Per tick: each relative pattern adds its
  `Advance` delta, one `MoveBy` with the sum; absolute patterns: anchor = cursor at their start, target =
  anchor + sum of their emitted offsets + offsets of absolute patterns started earlier, one `MoveTo` only when the
  target changed. Resolve the replay type once per start. A `Stop` takes effect before the next tick.

- [ ] Step 1: tests (ManualTime + fake waiter + recording sink, like `ClickEngineTests`). `Relative_replay_sums_to_track_total`;
  `Speed_two_finishes_in_half_the_time`; `Stop_mid_pattern_sends_nothing_more`; `Two_patterns_add_up`;
  `Absolute_replay_is_anchor_plus_offset`; `Absolute_replay_on_negative_origin_desktop` (Review Focus 5);
  `Failed_send_stops_all_and_raises_Blocked`; `Idle_player_does_not_tick`; `Non_repeating_pattern_raises_Finished`.
- [ ] Step 2: run, expect FAIL. Step 3: implement. Step 4: filtered + full suite green; commit
  `feat(clicker-host): pattern player`.

## Task 5 — Hotkey bindings, pattern coordinator, protocol v2, helper wiring

**Files:** modify `ClickerHost/HotkeyHook.cs` (`HotkeyTracker`), `ClickerHost/HostCoordinator.cs`,
`ClickerHost/HostMain.cs`, `Services/AutoClicker/ClickerProtocol.cs`; create `ClickerHost/PatternCoordinator.cs`;
tests `HotkeyTrackerTests.cs`, `PatternCoordinatorTests.cs`, `HostCoordinatorTests.cs`, `ClickerProtocolTests.cs`.

**Consumes:** Tasks 1-4.
**Produces:**
- `sealed record TrackedBinding(string Id, ClickerHotkey Hotkey)`; `HotkeyTracker.Configure(ClickerHotkey? hotkey,
  ClickerHotkey? master, bool strict, IReadOnlyList<TrackedBinding> bindings)` (old 3-argument overload delegates
  with an empty list); `Action<string, bool>? BindingChanged`; same rebase rule and dispatch lock for bindings in
  `Configure`, `OnKey` and `Resync`.
- `PatternCoordinator.RecordBindingId = "record"`; `PatternCoordinator(PatternPlayer player, PatternRecorder recorder,
  Func<ForegroundInfo> foreground, Func<bool> overClipper, Action<ClickerMessage> send)`;
  `IReadOnlyList<TrackedBinding> Configure(ClickerSettings s)` (returns the bindings: record hotkey + every active
  pattern with a hotkey, none when `!MousePatternsArmed`; stops playbacks of removed/deactivated patterns, cancels a
  recording when disarmed); `void SetData(string id, int revision, PatternData data)`; `void OnBinding(string id, bool matched)`;
  `void Lock()` (stop all + cancel recording, synchronous); `void Unlock()`; `void OnSuppression(bool active)`
  (active: stop all, cancel a recording - result `Cancelled` sent - and ignore edges until inactive);
  `void Poll()` (every 50 ms: task switcher via `StopRules.IsTaskSwitcher(class, exe)` -> stop all; bound pattern
  whose `Program` is not the foreground exe (case-insensitive file name) -> stop it); subscribes to
  `PatternRecorder.LimitReached` (stop + send the `Cut` result) and `PatternPlayer.Blocked` (remember the
  foreground exe name as `BlockedBy`, cleared by the next successful start);
  one `ActivationStateMachine` per pattern (`Configure(p.Mode, hasMaster:false, ...)`); rising edge ignored when
  the binding's hotkey has mouse input and `overClipper()`; rising edge of a program-bound pattern ignored when
  its program is not in front; pattern edges ignored while recording; a pattern without data never starts.
  Record binding rising edge: start (program = foreground exe name) / stop -> send result.
- Protocol: `Version = 2`; `PatternDataMessage(string Id, int Revision, string Data)` (main -> helper);
  `PatternRecordedMessage(string Outcome, string? Data, string? Program, double DurationMs)` (helper -> main,
  Outcome = `PatternRecordingOutcome` name); `StateMessage` gains `bool Recording = false, string? Playing = null,
  string? PatternsBlockedBy = null` (comma-joined ids; exe name of an elevated window that refused the moves).
  Both new types in `[JsonDerivedType]` AND `KnownTypes`.
- `HostCoordinator`: `ApplyConfig` builds bindings via `PatternCoordinator.Configure` and calls the 4-argument
  tracker `Configure` once (outside the gate, as today); when `!settings.Armed` the clicker side gets hotkey and
  master `null`, start zones and `StartMessage` do nothing; `Lock` calls `patterns.Lock()` BEFORE `PublishState`;
  picker/suppression forwarded; `PatternDataMessage` -> decode -> `SetData` (ignored when undecodable, logged);
  `PublishState` includes `Recording`/`Playing`.
- `HostMain`: create recorder, second `HighResWaiter`, `WindowsPatternEnvironment`, player, coordinator; set
  `RawInputMonitor.MouseSink`/`HotkeyHook.PointerSink` to the recorder; 50 ms poll timer; teardown stops the
  player and cancels a recording before `injector.ReleaseAll`.

- [ ] Step 1: tests. Tracker: `Binding_fires_with_clicker_on_same_hotkey`; `Inactive_binding_is_not_tracked`;
  `Binding_rebase_matches_clicker_rule`; `Resync_releases_a_held_binding` (Review Focus 1). Coordinator:
  `Hold_plays_while_held_and_stops_on_release`; `Hold_without_repeat_plays_one_pass`; `Toggle_second_press_stops`;
  `Configure_while_held_keeps_playing_and_release_stops` (Review Focus 2); `Missing_data_never_starts` (Review Focus 3);
  `Lock_stops_playback_and_cancels_recording_synchronously`; `Suppression_ignores_edges`; `Mouse_hotkey_over_clipper_is_ignored`;
  `Program_bound_pattern_needs_its_program_in_front`; `Poll_stops_on_task_switcher`; `Record_toggle_sends_result`;
  `Pattern_edges_ignored_while_recording`; `Disarming_patterns_stops_everything`;
  `Suppression_cancels_recording_and_stops_playback`; `Blocked_send_reports_blocking_program`. HostCoordinator:
  `Clicker_hotkey_dead_when_only_patterns_armed`; `Lock_confirmation_after_patterns_stopped`. Protocol:
  `New_messages_round_trip`; `New_types_are_known`; `Old_state_line_without_new_fields_decodes`.
- [ ] Step 2: run, expect FAIL. Step 3: implement. Step 4: filtered + full suite green; commit
  `feat(clicker-host): pattern hotkeys, coordinator and protocol v2`.

## Task 6 — Main side: lifecycle, data sync, recording intake, state, indicator

**Files:** modify `Services/AutoClicker/ClickerController.cs`, `Services/AutoClickerGuard.cs` (doc of
`IClickerLock.Armed` = helper wanted), `Services/AutoClicker/ClickerIndicatorRules.cs`,
`Services/AutoClicker/ClickerNotices.cs`, `AppHost.cs`, `Views/AutoClicker/ClickerIndicatorWindow.cs`; tests
`ClickerControllerTests.cs`, `ClickerPresentationTests.cs`.

**Consumes:** Tasks 1, 5.
**Produces:**
- `ClickerController` takes an `IMousePatternStore` (Task 1).
  Every helper decision that read `Armed` uses `MousePatternRules.HostWanted`; `IClickerLock.Armed` returns
  HostWanted.
- Data sync: after the handshake's config push, one `PatternDataMessage` per pattern whose data loads; afterwards
  on every settings change resend patterns whose `Revision` differs from the last sent one (dictionary per session).
- `string? RecordTarget { get; }`, `void SetRecordTarget(string? patternId)`.
- `PatternRecordedMessage` handling on the UI thread (`_post`, like `ApplyRuns`): `Ok`/`Cut` -> target pattern (if
  still present) gets the new data, `Program`, `DurationMs`, `Revision++`; otherwise a new pattern with
  `MousePatternRules.NextName` and the Global-Constraints defaults; store saved, then settings saved; target
  cleared. `Empty`/`Cancelled` -> nothing saved. Raises `event Action<PatternRecordedInfo>? PatternRecorded`
  (`sealed record PatternRecordedInfo(string Outcome, string? Name, double Seconds)`).
- Deleting a pattern: `void DeletePattern(string id)` removes it from settings, deletes the file, saves.
- `ClickerState` gains `bool PatternsArmed = false, bool Recording = false, string? Playing = null,
  string? PatternsBlockedBy = null` (copied from the helper's `StateMessage`).
- Indicator: `ClickerIndicatorRules.ShouldShow` true when the clicker runs (as today), or a pattern plays (with
  `ShowActiveIndicator`), or a recording runs (regardless of `ShowActiveIndicator`); `CanShow` (AMF rule) unchanged;
  text = clicker part, `"Mausbewegung · <Name>"` (names from settings by id, first one + "+n"), or
  `"Aufnahme läuft · <record hotkey> beendet"`, joined with `"  ·  "`.
- `AppHost`: store construction (`SettingsService.AppDataDir`), toast on `PatternRecorded`
  (`"'Muster 3' aufgenommen (2,4 s)"`, Cut adds "- nach 60 s abgeschnitten", Empty "Keine Bewegung aufgenommen",
  Cancelled "Aufnahme verworfen").

- [ ] Step 1: tests. `Helper_runs_when_only_patterns_armed`; `Helper_stops_when_both_switches_off`;
  `Pattern_data_follows_config_on_handshake`; `Changed_revision_is_resent_once`; `Recording_creates_new_pattern_with_defaults`;
  `Recording_replaces_target_and_keeps_options`; `Empty_or_cancelled_recording_saves_nothing`;
  `Session_end_during_recording_creates_nothing` (Review Focus 4); `Delete_pattern_removes_file_and_entry`;
  presentation: `Indicator_shows_recording_even_with_indicator_option_off`; `Indicator_text_joins_clicker_and_pattern`;
  `Indicator_hidden_under_amf_capture`.
- [ ] Step 2: run, expect FAIL. Step 3: implement. Step 4: filtered + full suite green; commit
  `feat(clicker): main-side pattern lifecycle, data sync and recording intake`.

## Task 7 — UI

**Files:** modify `Views/AutoClickerView.xaml(.cs)` (second switch "Mausbewegung bereit" next to "Clicker bereit",
sub-tab "Mausbewegung"/"Mouse patterns" after "Klickpunkte", anti-cheat text mentions movement macros, status-row
line "n Muster-Hinweise" when pattern warnings exist); create `Views/AutoClicker/MousePatternsPanel.cs`,
`Services/AutoClicker/MousePatternUiRules.cs` (pure texts); tests `GamingUtilitiesTests.cs`,
`MousePatternUiRulesTests.cs`, a WPF build test.

**Consumes:** Tasks 1, 2 (`PatternPreview.Fit`), 6 (`SetRecordTarget`, `DeletePattern`, `ClickerState` fields).
**Produces:** the panel of spec 4.7: explanation + status line (`MousePatternUiRules.StatusLine(ClickerState,
ClickerSettings) -> string`: "Mausbewegung ist aus - oben 'Mausbewegung bereit' einschalten", "Bereit · 2 Muster
aktiv", "Aufnahme läuft …", "Spielt: Kreis", "Gestoppt: <exe> läuft als Administrator und nimmt keine Bewegung an"); record hotkey row (`HotkeyCaptureBox(required: true)` with
cancel/clear); re-record target line with cancel; empty state text; two-line cards per pattern keyed by a row key
string (`_keepRows` pattern, stable ids, raw controls only inside rows - no `OnSync` in rows): line 1 [aktiv],
name box (MaxLength 40, commit on Enter/focus loss), preview `Canvas` + `Polyline` (`AccentBrush`, start dot),
duration, [Nur in <Programm>] (hidden when `Program` is null); line 2 `HotkeyCaptureBox(required:false)` +
cancel/clear, Halten/Umschalten combo, [Wiederholen], Tempo number box (commit on Enter/focus loss, shows "x"),
Wiedergabe combo (Automatisch/Zeiger/Spiel (roh)), "Neu aufnehmen", "Löschen" (confirmation dialog);
warnings for the card from `MousePatternConflicts.Evaluate` under it. Preview data comes from the store (loaded
once per revision, cached in the panel).

- [ ] Step 1: tests. `Subtabs_are_six_in_order` (update the existing five-tab test and its labels);
  `Status_line_texts` (Theory); `Panel_builds_with_two_patterns_and_no_data_file` (WPF, `[Collection("WpfApplication")]`).
- [ ] Step 2: run, expect FAIL. Step 3: implement. Step 4: filtered + full suite green; commit
  `feat(clicker-ui): mouse patterns tab`.

## Task 8 — Measuring helper

**Files:** create `tools/measure-moves.ps1` (model: `tools/measure-clicks.ps1`).
A topmost window that shows, live, the raw counts it receives (its own Raw Input registration, foreground) and the
pointer path inside it, with totals and a "Reset" button; draws both paths; prints summed dx/dy and duration per
burst (a burst ends after 300 ms without motion). No injection itself. Window placed top-right of the primary
work area. Verify by parsing the script (`[scriptblock]::Create`) only; it is exercised in the live test.
Commit `tools: measure-moves for pattern replay checks`.

## After the tasks

Full `dotnet build` (0 warnings) and `dotnet test` once; one lean whole-branch review (correctness lenses: helper
threading/locks, protocol, lifecycle/lock confirmation, UI refresh loops), one verifier per finding, one fix
round. Then live test with Phillip (spec section 6).
