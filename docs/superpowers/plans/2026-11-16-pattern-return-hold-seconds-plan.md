# Pattern return-to-start + hold time in seconds Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mouse patterns can go back to their start after each pass (jump or reverse), and the clicker's hold time can be set in seconds.

**Architecture:** The return is a phase of a playback inside `PatternPlayer` (same tick, same anchor/relative bookkeeping), driven by a pure
`PatternReturnPath`. The hold time gets a unit (`DutyUnit`) next to the existing percent; `DutyCycle` computes the effective hold for both.

**Tech Stack:** C# / .NET 8 WPF, xUnit. Build `dotnet build`, tests `dotnet test WKI_Clipper.Tests --filter ...`.

**Spec:** `docs/superpowers/specs/2026-11-16-pattern-return-hold-seconds-design.md`

## Global Constraints

- UI text via `L.T("<deutsch>", "<english>")`; code, names and comments English.
- Settings stay backward compatible: a settings.json without the new fields loads with `PatternReturn.Off` and `DutyUnit.Percent`, `DutyCycleSeconds = 0.05`.
- Return mode Off and unit Percent behave exactly as today (every existing test passes unchanged).
- No new packages. Commit messages via temp file + `git commit -F`, trailer `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. Hold mode with Repeat + return: releasing the hotkey during a return finishes that return and starts no further pass (Task 4 test).
2. Pressing again during a Reverse return many times: the mouse never drifts - each new pass starts at the same point (Task 4 test).
3. Pointer replay whose path ran into the screen edge: the return ends exactly at the pass's start point (Task 4 test).
4. Tempo ≠ 1x with Reverse: the way back takes the same time as the way out and ends exactly at the start (Tasks 3 and 4 tests).
5. A seconds hold longer than the click interval at a high rate: capped like the percent; a seconds value never puts the keyboard into hold mode (Task 1 test).

---

### Task 1: Hold time unit and seconds (model + rules)

**Files:**
- Modify: `WKI_Clipper/Models/ClickerSettings.cs` (enum next to `DutyMode`; fields on `ClickerSettings` and `ClickerPreset`)
- Modify: `WKI_Clipper/Services/AutoClicker/DutyCycle.cs`, `Cadence.cs` (CadencePlan.From), `KeyboardRepeat.cs`, `ClickerSettingsRules.cs`, `ClickerPresets.cs` (ApplyFactory, Apply/Capture copies at ~172/191)
- Test: `WKI_Clipper.Tests/DutyCycleTests.cs` (create if missing), existing `KeyboardRepeat`/`ClickerSettingsRules`/`ClickerPresets` test files

**Interfaces (produces):**
- `[JsonConverter(typeof(JsonStringEnumConverter))] public enum DutyUnit { Percent, Seconds }`
- `ClickerSettings.DutyCycleUnit` (`DutyUnit`, default `Percent`), `ClickerSettings.DutyCycleSeconds` (`double`, default `DutyCycle.DefaultSeconds`); same two properties on `ClickerPreset`.
- `DutyCycle.MinSeconds = 0.001`, `DutyCycle.MaxSeconds = 3600`, `DutyCycle.DefaultSeconds = 0.05` (`public const double`).
- `DutyCycle.HoldMs(double intervalMs, double cps, bool enabled, DutyUnit unit, double percent, double seconds) : double` - Percent: as today; Seconds: `min(interval, interval * RateCapPercent(cps) / 100, seconds * 1000)`; disabled or seconds ≤ 0/NaN: 0. The old 4-argument overload stays (percent).
- `DutyCycle.SecondsCut(double intervalMs, double cps, double seconds, out double effectiveSeconds) : bool` - true when the seconds value is cut by interval or cap.
- `DutyCycle.Describe(DutyUnit unit, double percent, double seconds) : string` - `ClickerNumber.Format(percent) + " %"` or `ClickerNumber.Format(seconds) + " s"`.
- `KeyboardRepeat.IsHoldMode(ClickerInput input, bool dutyEnabled, DutyUnit unit, double dutyPercent, DutyMode dutyMode)` - the 100 % rule only for `DutyUnit.Percent`; the `ClickerSettings` overload passes the unit.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Seconds_hold_is_the_seconds_value_when_the_cycle_allows_it()
    => Assert.Equal(250, DutyCycle.HoldMs(1000, 1, true, DutyUnit.Seconds, 45, 0.25), 6);
[Fact] public void Seconds_hold_never_exceeds_the_interval()
    => Assert.Equal(100, DutyCycle.HoldMs(100, 10, true, DutyUnit.Seconds, 45, 0.5), 6);
[Fact] public void Seconds_hold_obeys_the_rate_cap()   // 100 per second: cap 70 %
    => Assert.Equal(7, DutyCycle.HoldMs(10, 100, true, DutyUnit.Seconds, 45, 0.05), 6);
[Fact] public void Percent_unit_is_unchanged()
    => Assert.Equal(18, DutyCycle.HoldMs(40, 25, true, DutyUnit.Percent, 45, 9), 6);
[Fact] public void Disabled_holds_nothing_in_either_unit()
{ Assert.Equal(0, DutyCycle.HoldMs(1000, 1, false, DutyUnit.Seconds, 45, 0.25)); Assert.Equal(0, DutyCycle.HoldMs(1000, 1, false, DutyUnit.Percent, 45, 0.25)); }
[Fact] public void A_cut_seconds_value_reports_what_it_really_is()
{ Assert.True(DutyCycle.SecondsCut(40, 25, 0.05, out var cut)); Assert.Equal(0.04, cut, 9);
  Assert.False(DutyCycle.SecondsCut(1000, 1, 0.25, out var kept)); Assert.Equal(0.25, kept, 9); }
[Fact] public void Describe_names_value_and_unit()
{ Assert.Equal(ClickerNumber.Format(45) + " %", DutyCycle.Describe(DutyUnit.Percent, 45, 0.05));
  Assert.Equal(ClickerNumber.Format(0.05) + " s", DutyCycle.Describe(DutyUnit.Seconds, 45, 0.05)); }
// KeyboardRepeat
[Fact] public void A_seconds_hold_never_puts_the_keyboard_into_hold_mode()
{ Assert.False(KeyboardRepeat.IsHoldMode(ClickerInput.Keyboard, true, DutyUnit.Seconds, 100, DutyMode.Click));
  Assert.True(KeyboardRepeat.IsHoldMode(ClickerInput.Keyboard, true, DutyUnit.Percent, 100, DutyMode.Click)); }
// ClickerSettingsRules
[Fact] public void Normalize_repairs_unit_and_seconds()   // unknown enum -> Percent; NaN -> 0.05; 0 -> 0.001; 5000 -> 3600
[Fact] public void Settings_without_the_new_fields_load_in_percent()   // deserialize {} -> Percent, 0.05
// ClickerPresets
[Fact] public void A_user_preset_keeps_unit_and_seconds()   // Capture then Apply round trip
[Fact] public void A_factory_preset_sets_the_percent_unit() // settings in Seconds -> ApplyFactory -> Percent
```

- [ ] **Step 2: Run them, expect compile errors / FAIL**
`dotnet test WKI_Clipper.Tests --filter "FullyQualifiedName~DutyCycle|FullyQualifiedName~KeyboardRepeat|FullyQualifiedName~ClickerSettingsRules|FullyQualifiedName~ClickerPresets"`
- [ ] **Step 3: Implement the interfaces above;** `CadencePlan.From` uses the 6-argument `HoldMs`; Normalize clamps seconds to `[MinSeconds, MaxSeconds]` with default `DefaultSeconds`; presets copy both fields both ways; `ApplyFactory` sets `DutyCycleUnit = Percent`.
- [ ] **Step 4: Run the filter again, expect PASS**
- [ ] **Step 5: Commit** `feat(clicker): hold time in seconds (model and rules)`

### Task 2: Hold time unit in the UI

**Files:**
- Modify: `WKI_Clipper/Views/AutoClicker/AdvancedClickerPanel.cs:34-38`, `WKI_Clipper/Views/AutoClicker/PresetsStatsPanel.cs:36-37`

**Interfaces:** consumes Task 1 (`DutyUnit`, `DutyCycleSeconds`, `SecondsCut`, `Describe`, `Cadence.IntervalMs(s)`, `Cadence.ClicksPerSecond`).

- [ ] **Step 1: Haltedauer row:** `Check(an)`, percent `Number` (visible for Percent) + `Label("(0-100)")` (Percent), seconds `Number` width 70 (visible for Seconds, tooltip `L.T("0,001 bis 3600 Sekunden. Nie länger als ein Takt.", "0.001 to 3600 seconds. Never longer than one cycle.")`), unit `Choice` `[(Percent, "%"), (Seconds, L.T("Sekunden", "seconds"))]` minWidth 100, and a note `TextBlock` set in `OnSync`: when Seconds and `SecondsCut(...)` -> `L.T($"tatsächlich {ClickerNumber.Format(x)} s", $"actually {...} s")`, else collapsed. `EnabledWhen` both numbers and the unit on `DutyCycleEnabled`.
- [ ] **Step 2: Presets detail** uses `DutyCycle.Describe(p.DutyCycleUnit, p.DutyCyclePercent, p.DutyCycleSeconds)` in both languages.
- [ ] **Step 3: Build** `dotnet build WKI_Clipper` - 0 warnings, 0 errors; run the Task 1 filter again (PASS).
- [ ] **Step 4: Commit** `feat(clicker): choose the hold time in % or seconds`

### Task 3: PatternReturnPath (pure)

**Files:**
- Modify: `WKI_Clipper/Models/MousePattern.cs` (enum + property), `WKI_Clipper/Services/AutoClicker/PatternCurve.cs` (`PatternCursor.CurveMs`)
- Create: `WKI_Clipper/Services/AutoClicker/PatternReturnPath.cs`
- Test: `WKI_Clipper.Tests/PatternReturnPathTests.cs`

**Interfaces (produces):**
- `[JsonConverter(typeof(JsonStringEnumConverter))] public enum PatternReturn { Off, Jump, Reverse }`; `MousePattern.Return` (default `Off`).
- `PatternCursor.CurveMs : double` - the curve time of the last `Advance` (non-repeating: `min(elapsed * speed, curve.DurationMs)`).
- `public sealed class PatternReturnPath(PatternCurve curve, double speed, double fromCurveMs, (long X, long Y) offset, PatternReturn mode)` - `offset` = what the forward part sent; `mode` Off throws `ArgumentException`; speed clamped like `PatternCursor`.
  - `(long X, long Y) Emitted` - what the return sent so far.
  - `bool Advance(double elapsedMs, out int dx, out int dy)` - like `PatternCursor.Advance`: false = back at the start with this call; later calls 0/0 false.
  - Jump: the whole `-offset` at the first call (int-clamped, rest follows).
  - Reverse: with `r = fromCurveMs - elapsed * speed`, target `offset - (round(At(fromCurveMs)) - round(At(r)))` while `r > 0`, `(0, 0)` once `r <= 0`; the move is target − offset − Emitted (cumulative, no rounding drift).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Jump_undoes_the_offset_in_one_move()            // offset (120,-40): Advance(0) -> (-120,40), false
[Fact] public void Reverse_retraces_the_path_and_ends_at_the_start() // Line 10 bins (1,0), from 10, offset (10,0): Σ after 3 ms = -3, done at 10 ms, Σ = -10
[Fact] public void Reverse_at_double_speed_takes_half_the_time()    // speed 2: done at 5 ms, Σ = -10
[Fact] public void Reverse_after_an_early_stop_retraces_only_the_driven_part() // from 4, offset (4,0): done at 4 ms, Σ = -4
[Fact] public void Reverse_never_drifts_by_rounding()                // fractional/wobbly track: Σdx == -offset.X, Σdy == -offset.Y exactly
[Fact] public void Off_is_no_return_path()                           // Assert.Throws<ArgumentException>
[Fact] public void Cursor_reports_the_curve_time_it_reached()        // PatternCursor speed 2, Advance(3) -> CurveMs 6; past the end -> DurationMs
```

- [ ] **Step 2: Run** `--filter FullyQualifiedName~PatternReturnPathTests` - FAIL
- [ ] **Step 3: Implement** the interfaces above.
- [ ] **Step 4: Run again** - PASS; also `--filter FullyQualifiedName~PatternCurveTests` stays green.
- [ ] **Step 5: Commit** `feat(patterns): the way back to a pattern's start`

### Task 4: The return phase in PatternPlayer

**Files:**
- Modify: `WKI_Clipper/ClickerHost/PatternPlayer.cs`
- Test: `WKI_Clipper.Tests/PatternPlayerTests.cs` (`Rig.Play` gets `PatternReturn ret = PatternReturn.Off`)

**Interfaces:**
- Consumes: Task 3 (`PatternReturn`, `PatternReturnPath`, `PatternCursor.CurveMs`).
- Produces: `internal sealed record PatternPlayRequest(string Id, PatternData Data, double Speed, bool Repeat, PatternReplay Replay, PatternReturn Return = PatternReturn.Off);`
  and `public void StopWithReturn(string id)` (any thread): a playback with `Return != Off` turns to its way back from where it is (one already on its way back starts no further pass); `Return == Off` = `Stop`. Raises `Finished` when back.

**Behavior (Return != Off; Off unchanged):**
- The forward cursor never repeats; the player repeats: pass → return (starts in the tick AFTER the pass's last move, never merged into it) → if Repeat and not stopped, the next pass in the following tick (new cursor, its own start time).
- A playback's offset for the absolute target and for `Remove` = forward Emitted + return Emitted.
- Absolute: each pass remembers its start point (the pointer target when the pass began). When its return ends and it is the only absolute playback, the anchor is set so the target is exactly that start point (what the edge swallowed included).
- `Play(request)` for an id that plays with `Return != Off`: the rest of its way back (all of it, if still on the way out) goes out at once as one move, then it is replaced by the new pass, which starts at the start point.
- `Stop`, `StopAll`, a refused move, a failure: no return (unchanged).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Jump_after_a_pass_goes_back_in_one_move()          // raw Line(20,1,0), Jump: Moved == (0,0); last moveby == (-20,0) one tick after the last forward move; Notes == ["finished:a"]
[Fact] public void Reverse_after_a_pass_retraces_it_and_ends_at_the_start() // Moved == (0,0); every move after the pass is (-1,0); finished at ~40 ms
[Fact] public void Repeat_with_a_return_starts_every_pass_at_the_start()   // Repeat+Jump, StopWithReturn at 65 ms: Moved == (0,0); ≥ 3 passes went out
[Fact] public void Releasing_during_a_return_finishes_it_without_a_further_pass() // Repeat+Reverse, StopWithReturn at 25 ms (on the way back): no forward move after 25 ms; Moved == (0,0); one Finished
[Fact] public void Stopping_mid_pass_with_return_goes_back_from_there()    // Reverse, StopWithReturn at 7.5: Moved == (0,0)
[Fact] public void Pressing_again_on_the_way_back_jumps_the_rest_and_starts_at_the_start() // Reverse; at 25 ms Play again; at the end Moved == (0,0); exactly one jump move at 25 ms
[Fact] public void Many_presses_never_drift()                              // 10 x (Play, StopWithReturn after 3 ms) -> Moved == (0,0)
[Fact] public void Pointer_return_at_the_screen_edge_ends_exactly_at_the_start_point() // Pointer replay, cursor (100,50), Line(20,0,-10): Jump and Reverse -> last target (100,50)
[Fact] public void Reverse_at_double_tempo_takes_as_long_as_the_way_out()  // speed 2: forward ends ~10 ms, back ~20 ms, Moved == (0,0)
[Fact] public void Stop_and_StopAll_never_return()                         // Jump; Stop at 7.5 -> no move after 7.5, Moved != (0,0)
```

- [ ] **Step 2: Run** `--filter FullyQualifiedName~PatternPlayerTests` - new tests FAIL, old ones PASS
- [ ] **Step 3: Implement** per the behavior list (phase field on `Playback`; `Begin` creates a non-repeating cursor when `Return != Off`).
- [ ] **Step 4: Run again** - all PASS
- [ ] **Step 5: Commit** `feat(patterns): go back to the start after a pass`

### Task 5: Coordinator, settings rules, UI, README

**Files:**
- Modify: `WKI_Clipper/ClickerHost/PatternCoordinator.cs` (`Options` gets `PatternReturn Return`; Start passes it; `ActivationCommand.Stop` → `_player.StopWithReturn`), `WKI_Clipper/Services/AutoClicker/MousePatternRules.cs:66` (Normalize), `WKI_Clipper/Services/AutoClicker/MousePatternUiRules.cs`, `WKI_Clipper/Views/AutoClicker/MousePatternsPanel.cs:265-316`, `README.md` (AutoClicker: mouse patterns + hold time)
- Test: `PatternCoordinatorTests.cs`, `MousePatternRulesTests.cs`, `MousePatternUiRulesTests.cs`

**Interfaces:**
- `MousePatternUiRules.ReturnName(PatternReturn r) : string` → `L.T("Aus","Off")`, `L.T("Springen","Jump")`, `L.T("Rückwärts","Reverse")`.
- UI: after "Wiederholen" a `[Label "Zum Start"] [Combo Off/Jump/Reverse]`, tooltip `L.T("Nach jedem Durchgang zum Startpunkt zurück: Springen = sofort in einem Zug, Rückwärts = den Pfad rückwärts im selben Tempo. Auch beim Stoppen mit dem eigenen Hotkey.", "Back to the start point after every pass: Jump = at once, Reverse = the path backwards at the same tempo. Also when stopped with its own hotkey.")`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Releasing_the_hotkey_takes_a_pattern_with_return_back()   // coordinator + real player rig: Hold, Jump; press, release -> Moved == (0,0)
[Fact] public void Its_program_leaving_the_front_stops_it_without_return()  // OnlyInProgram, Jump; Poll with another exe in front -> no move after the stop
[Fact] public void Normalize_repairs_an_unknown_return_to_off()             // (PatternReturn)7 -> Off
[Fact] public void A_pattern_saved_without_Return_loads_as_off()            // JSON without "Return" -> Off
[Fact] public void Return_names()                                          // German: Aus / Springen / Rückwärts (Language collection)
```

- [ ] **Step 2: Run** the three filters - FAIL
- [ ] **Step 3: Implement** coordinator wiring, Normalize, `ReturnName`, the combo, README lines (patterns: "Zum Start zurück: Springen/Rückwärts"; clicker: "Haltedauer in % oder Sekunden").
- [ ] **Step 4: Run the filters, then the full suite** `dotnet test WKI_Clipper.Tests` - all PASS, 0 warnings
- [ ] **Step 5: Commit** `feat(patterns): return to start in the settings and the UI`
