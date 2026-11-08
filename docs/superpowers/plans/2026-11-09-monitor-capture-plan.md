# Monitor capture fix — Implementation Plan

> For agentic workers: implement ONE task per run, test-first, commit, then stop. Read the spec first. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** "Ganzer Monitor" records exactly the chosen physical monitor, keeps it across restarts and display
changes, labels monitors like Windows, and survives resolution changes (R1-R8) without errors.
**Architecture:** spec section 4. Pure rules in `WKI_Clipper/Services/` (namespace `WKI_Clipper.Services`), P/Invoke in
`WKI_Clipper/Native/`, thin adapters for DXGI (Vortice) and CCD; the replay buffer, manual recording, screenshots and
the capture/status views consume one resolved `CaptureSource`.
**Tech:** .NET 8, WPF + WinForms interop, C# latest, nullable on, Vortice.Direct3D11/DXGI 3.8.3 (already referenced),
xUnit 2.9 in `WKI_Clipper.Tests`.
**Spec:** `docs/superpowers/specs/2026-11-09-monitor-capture-design.md` (R1-R8 = section 3, decisions = section 2).

Repo `C:\Users\cr4zy\Documents\CLAUDE_Projects\WKI_Clipper`, branch `fix/monitor-capture` (from master `4876b9a`).
Investigation notes with file:line refs and the working CCD/DXGI probe code (session scratchpad
`C:\Users\cr4zy\AppData\Local\Temp\claude\227c7b327006c932\d11e53f0-1ff9-4ebc-96e5-2c15ea5d0ff3\scratchpad\`):
`monitor-synth.md`, `monitor-code.md`, `monitor-research.md`, `monitor-probe\MonProbe.cs`.

## Global Constraints

- Never map a monitor through a list position. Every capture index comes from DXGI (`EnumOutputs` on the default
  adapter = the LUID of `D3D11CreateDevice(null, Hardware)`); `IndexOfScreen` and `MonitorByDevice` are deleted.
- Monitor identity = CCD `monitorDevicePath` (case-insensitive), then unique EDID mfg+product, then GDI name
  (legacy only). Resolution, refresh rate, position, HDR and the primary flag are never part of identity (R1).
- Sizes are physical pixels of the monitor rect (the app is PerMonitorV2; the reader must also return physical
  pixels on a DPI-unaware thread).
- Preset output size is constant for every source size (scale `force_original_aspect_ratio=decrease` + even pad,
  as today); Native output follows the source and the size is part of the segment source key (R4, R5).
- Display-change debounce `1500 ms`; automatic restarts after a display-related ffmpeg exit: at most `3` per `60 s`,
  then the existing "Buffer unerwartet beendet" toast once (R2, R8).
- Fallback to the primary monitor is never silent: `Logger.Warn` + topology `Dump()` + ONE notice per transition;
  the setting is never changed by a fallback.
- Topology reads are cached: `DisplayTopologyCache.Get()` must not call DXGI/D3D11/CCD when nothing changed (the
  capture view refreshes every second on the UI thread).
- Labels: friendly name · W×H · side · primary; no "Monitor N" numbers anywhere in new text.
- User texts via `L.T(german, english)` with real umlauts; code/comments English, matching the surrounding style.
- Commit messages via a UTF-8 file WITHOUT BOM and `git commit -F`; last line
  `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never push.
- Targeted `dotnet test --filter` WITH build (`--no-build` can run a stale binary), build 0 warnings; full suite only
  at the end (known flaky `FFmpegJobRunnerIntegrationTests.A_healthy_slow_job_is_not_mistaken_for_a_hung_one`).
- Never start the app, change display settings or inject input during build tasks. Timebox ~10 min per stuck problem.

## Review Focus

1. An exclusive-fullscreen game switches the mode down (4K → 1080p) and back while the buffer runs with the 1440p
   preset: one warm restart each way, history kept, output stays 2560×1440, no warning toast, no "monitor missing".
2. The capture view's 1-s refresh on the UI thread: served from the cache, no D3D11 device per second, no hitch.
3. The chosen monitor sleeps (DisplayPort) or is unplugged and comes back while the buffer runs: one fallback notice,
   automatic return, segments of the two monitors never joined into one clip.
4. Two identical monitors and a changed primary (Win+P or Windows settings): the same physical monitor stays
   selected, the labels tell the two apart.
5. Ctrl+F9 recording while the mode switches: the footage up to the switch is kept and the recording continues
   (Task 5, decision pending with the owner - see the end of this plan).

---

## Task 1 — Topology layer and cache

**Files:** create `Native/DisplayConfig.cs`, `Services/DisplayTopology.cs`, `Services/DisplayTopologyReader.cs`,
`Services/DisplayTopologyCache.cs`; tests `DisplayTopologyTests.cs`, `DisplayTopologyCacheTests.cs`,
`DisplayTopologyReaderLiveTests.cs` (gated like `GpuSensorsLiveTests`: env `DISPLAY_LIVE=1`, early return otherwise).

**Produces:**
- `sealed record DisplayAdapterInfo(int DxgiIndex, long Luid, string Name, bool IsSoftware)`.
- `sealed record DisplayInfo(nint HMonitor, string GdiName, string? DevicePath, string? FriendlyName, int X, int Y,
  int Width, int Height, bool IsPrimary, int? AdapterIndex, int? OutputIndex, string? Connector, ushort? EdidMfg,
  ushort? EdidProduct)`; `Connector` = short text of `outputTechnology` (`DP`, `HDMI`, `DVI`, `USB-C`, `intern`, else null).
- `sealed record DisplayTopology(IReadOnlyList<DisplayAdapterInfo> Adapters, IReadOnlyList<DisplayInfo> Displays,
  int DefaultAdapterIndex, string? Problem)` with `static DisplayTopology Empty(string problem)`,
  `DisplayInfo? Primary`, `DisplayInfo? ByHMonitor(nint)`, `string Fingerprint` (identity + rect + adapter/output of
  every display, order-independent) and `string Dump()` (one line per adapter and per display).
- `static class DisplayTopologyReader { static DisplayTopology Read(); }` — EnumDisplayMonitors + `GetMonitorInfo`
  (MONITORINFOEX) for HMONITOR/GDI name/rect/primary; CCD `QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)` +
  `DisplayConfigGetDeviceInfo` SOURCE_NAME/TARGET_NAME joined by GDI name; DXGI `CreateDXGIFactory1` →
  `EnumAdapters1` → `EnumOutputs` joined by `DXGI_OUTPUT_DESC.Monitor`; default adapter by LUID of
  `D3D11CreateDevice(null, Hardware)`. Runs the whole read under `SetThreadDpiAwarenessContext(PER_MONITOR_AWARE_V2)`
  and restores the previous context. Never throws: any failed part yields nulls and a `Problem` text.
- `sealed class DisplayTopologyCache(Func<DisplayTopology> read, Func<string> quickKey)`; `static DisplayTopologyCache
  Shared` (real reader; `quickKey` = EnumDisplayMonitors HMONITOR+GDI+rect list, no DXGI); `DisplayTopology Get()`
  (re-reads only when `quickKey` changed or after `Invalidate()`); `void Invalidate()`; thread-safe (lock).

- [ ] **Step 1: Failing tests** — `Fingerprint_is_stable_for_equal_topologies_and_changes_with_a_mode_change`,
  `Fingerprint_ignores_enumeration_order`, `Dump_lists_every_adapter_and_display_with_gdi_name_and_output`,
  `Cache_reads_once_while_the_quick_key_is_unchanged`, `Cache_rereads_after_the_quick_key_changes`,
  `Cache_rereads_after_Invalidate`, `Cache_is_safe_from_several_threads` (fake `read`/`quickKey` counting calls).
  Live (gated): `Every_enum_display_monitor_has_a_display_with_its_hmonitor`,
  `Primary_is_output_0_of_the_default_adapter`, `Friendly_name_and_device_path_are_present`.
- [ ] **Step 2: Run** `dotnet test --filter "FullyQualifiedName~DisplayTopology"` → compile errors (types missing).
- [ ] **Step 3: Implement** the four files (CCD/DXGI interop: port from `monitor-probe/MonProbe.cs`).
- [ ] **Step 4: Run** the filter → pass; `DISPLAY_LIVE=1` run of the live tests once → pass on this PC.
- [ ] **Step 5: Commit** `feat(capture): read the display topology (CCD + DXGI) with a cache`.

## Task 2 — Pure rules and settings fields

**Files:** modify `Models/AppSettings.cs` (`CaptureProfile`, the `WidgetState` comment); create
`Services/MonitorRef.cs`, `Services/MonitorTargetRules.cs`, `Services/CaptureSourceRules.cs`,
`Services/MonitorLabels.cs`, `Services/DisplayRestartPolicy.cs`; tests `MonitorTargetRulesTests.cs`,
`CaptureSourceRulesTests.cs`, `MonitorLabelsTests.cs`, `DisplayRestartPolicyTests.cs`.

**Consumes:** Task 1 types.

**Produces:**
- `CaptureProfile`: `string? MonitorDevicePath`, `string? MonitorFriendlyName`, `ushort? MonitorEdidMfg`,
  `ushort? MonitorEdidProduct` (all default null; old settings load unchanged). Comments: GDI names are adapter slots,
  not stable (both places).
- `sealed record MonitorRef(string? DevicePath, string? GdiName, string? FriendlyName, ushort? EdidMfg,
  ushort? EdidProduct)` with `static MonitorRef? FromProfile(CaptureProfile)` (null when all are null) and
  `static MonitorRef Of(DisplayInfo)`.
- `enum MonitorResolutionStatus { Exact, Matched, Legacy, PrimaryByDefault, FallbackPrimary, NoDisplays }`;
  `sealed record MonitorResolution(MonitorResolutionStatus Status, DisplayInfo? Display, string Reason,
  bool UpgradeSavedRef)` with `bool IsFallback => Status == FallbackPrimary`.
- `MonitorTargetRules.Resolve(MonitorRef? saved, DisplayTopology topo) -> MonitorResolution`, order:
  path exact → Exact; else exactly one display with the same EDID mfg+product (or several and exactly one of them
  with the saved GDI name) → Matched + upgrade; else saved has only a GDI name and it exists → Legacy + upgrade;
  saved null → PrimaryByDefault; else FallbackPrimary with reason „„X“ ist nicht verbunden“ /
  „„X“ is not connected“. A found display whose `AdapterIndex != topo.DefaultAdapterIndex` (or without output) →
  FallbackPrimary with reason „„X“ hängt an „GPU A“, die Aufnahme erreicht nur „GPU B““ / English equivalent.
  No displays or primary without a DXGI output → NoDisplays.
- `sealed record CaptureSource(string DisplayKey, string GdiName, nint HMonitor, int OutputIndex, long AdapterLuid,
  int? AmfIndex, int Width, int Height, string Label, MonitorResolution Resolution)`.
  `DisplayKey` = lower-case device path, else `gdi:` + GDI name.
- `CaptureSourceRules.ForDisplay(DisplayInfo d, DisplayTopology t, MonitorResolution r) -> CaptureSource?` (null when
  `d` has no output on the default adapter); `AmfIndex` = `OutputIndex` only when `d.AdapterIndex ==
  t.DefaultAdapterIndex` and exactly one adapter has outputs, else null;
  `SameMonitor(CaptureSource a, CaptureSource b)` = equal `DisplayKey`;
  `SameSource(a, b)` = `SameMonitor` && equal `AdapterLuid`, `OutputIndex`, `Width`, `Height`.
- `MonitorLabels.Build(DisplayInfo d, DisplayTopology t) -> string`: `{name} · {W}×{H}[ · {side}][ · Hauptmonitor]`;
  name = friendly name, else „Monitor ohne Namen“ / “Unnamed monitor”; duplicate names get ` ({Connector})`, if
  still equal ` (2)` by X then Y; side from the rect centre relative to the primary: links/rechts/oben/unten
  (left/right/above/below), none for the primary. `BuildMissing(MonitorRef r)` → „{name} (nicht verbunden)“;
  `AutoPrimary` → „Hauptmonitor (automatisch)“; `Fallback(string primaryLabel, MonitorRef saved)` →
  „Ersatz: {primary} ({name} nicht verbunden)“.
- `DisplayRestartPolicy` (pure): `static readonly string[] ModeChangeMarkers = { "Output parameters changed",
  "887a0026", "887a0022", "887a0005" }` (case-insensitive); `static bool ShouldAutoRestart(string? recentStderr,
  DateTime? lastDisplayChangeUtc, DateTime nowUtc, IReadOnlyList<DateTime> autoRestartsUtc)` = (marker in stderr
  OR display change within the last `10 s`) AND fewer than `3` entries of `autoRestartsUtc` within `60 s`.

- [ ] **Step 1: Failing tests** (fake topologies; all named):
  `Primary_not_first_in_enum_order_maps_to_its_dxgi_output`, `Saved_path_wins_over_swapped_gdi_names`,
  `Gdi_name_only_resolves_as_legacy_and_requests_upgrade`, `Missing_monitor_falls_back_to_primary_never_to_index_0`,
  `Null_setting_is_primary_without_fallback`, `Identical_models_with_changed_path_are_not_guessed`,
  `Identical_models_with_changed_path_resolve_by_gdi_tie_breaker`,
  `Monitor_on_another_adapter_falls_back_with_both_gpu_names`, `No_displays_is_reported`,
  `Same_monitor_in_another_mode_and_position_and_primary_flag_still_resolves_exact` (R1),
  `Mode_change_is_same_monitor_but_not_same_source`, `Other_monitor_with_same_output_and_size_is_not_same_monitor`,
  `Amf_index_only_on_a_single_output_adapter_topology`, labels (`Label_has_name_size_side_and_primary`,
  `Duplicate_names_get_connector_then_number`, `Missing_monitor_label_says_not_connected`,
  `Fallback_label_names_both`), policy (`Marker_in_stderr_allows_a_restart`, `Exit_soon_after_display_change_allows_a_restart`,
  `Exit_without_marker_or_display_change_does_not_restart`, `Fourth_restart_within_a_minute_is_refused`).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~MonitorTargetRules|FullyQualifiedName~CaptureSourceRules|FullyQualifiedName~MonitorLabels|FullyQualifiedName~DisplayRestartPolicy"` → compile errors.
- [ ] **Step 3: Implement** the five files and the settings fields.
- [ ] **Step 4: Run** the filter + `SettingsMigration|SettingsPersistence` → pass.
- [ ] **Step 5: Commit** `feat(capture): monitor target, source and label rules`.

## Task 3 — Resolver, ffmpeg arguments, screenshots, labels

**Files:** modify `Services/CaptureTargetResolver.cs`, `Services/FFmpegCommandBuilder.cs`,
`Services/ScreenshotService.cs`, `Services/ManualRecordingService.cs` (arguments only), `AppHost.cs:548`,
`Views/StatusView.xaml.cs:164-186`, `Views/CaptureView.xaml.cs:318-334`; tests extend `CapturePathTests.cs`,
`LongClipCommandTests.cs`, `RecordingFeaturesTests.cs` as needed; new `CaptureTargetResolverTests.cs`.

**Consumes:** Tasks 1-2.

**Produces:**
- `CapturePlan`: replace `MonitorIndex`, `MonitorDevice`, `MonitorWidth`, `MonitorHeight` by
  `CaptureSource Source`; keep `SysMode`, `AudioPid`, `TargetProcess`, `Hwnd`, `VideoLabel`, `AudioLabel`, `UseWgc`;
  add `string? Warning` (the fallback text, null otherwise).
- `CaptureTargetResolver.Resolve(CaptureProfile, AppSettings, DisplayTopologyCache? topology = null)`
  (null → `DisplayTopologyCache.Shared`). Monitor mode → `MonitorTargetRules.Resolve(MonitorRef.FromProfile(p), topo)`;
  Auto/Window with hwnd → `MonitorFromWindow(hwnd, DEFAULTTONEAREST)` → `topo.ByHMonitor`; without hwnd → primary.
  NoDisplays → throws `InvalidOperationException` with a bilingual message (the buffer reports it, Task 4).
  `VideoLabel` for Monitor mode = `MonitorLabels.Build` or `Fallback(...)`; Auto/Window labels keep their wording
  with the monitor part from `MonitorLabels.Build`.
- `CaptureTargetResolver.ResolveActiveMonitor(DisplayTopologyCache? topology = null) -> CaptureSource`
  (foreground hwnd not our own → its monitor, else cursor `MonitorFromPoint`).
- `event Action<MonitorRef>? MonitorRefUpgrade` on `CaptureTargetResolver` (static) raised when
  `Resolution.UpgradeSavedRef`; `AppHost` writes the four profile fields + GDI name on the UI thread and saves WITHOUT
  requesting a buffer restart (guard against the SettingsChanged → restart path).
- `FFmpegCommandBuilder.Build(..., CaptureSource? source = null, ...)` replaces `int monitorIndex`: ddagrab
  `output_idx=source.OutputIndex`; `UsesAmfNativeCapture(settings, rawInput, int? amfIndex)` false when
  `amfIndex` is null (log once `"AMF capture skipped: monitor mapping not certain"`); `EffectiveBitrate(settings,
  CaptureSource? source)` uses `source.Width*Height` for Native; `BuildScreenshot(string outputPath, int outputIndex)`.
  `MaxEffectiveBitrate` (all screens) unchanged.
- Screenshot file name `Shot_{sanitised friendly name or "Display"}_{ts}.png`; GDI fallback uses the source rect.

- [ ] **Step 1: Failing tests** — resolver with a fake cache: `Monitor_mode_uses_the_dxgi_output_of_the_saved_path`,
  `Monitor_mode_fallback_sets_warning_and_label`, `Legacy_gdi_setting_raises_one_upgrade`,
  `Window_mode_maps_its_hwnd_monitor_through_the_topology`; builder:
  `Ddagrab_uses_the_resolved_output_index`, `Amf_is_used_only_with_a_certain_index`,
  `Native_bitrate_uses_the_source_size_not_a_screen_index`,
  `Preset_1440p_scales_and_pads_a_1080p_source_to_2560x1440`, `Preset_1440p_scales_and_pads_a_2160p_source_to_2560x1440`
  (assert the exact `scale=2560:1440:force_original_aspect_ratio=decrease,pad=2560:1440:...` chain);
  screenshot argument test `Screenshot_uses_the_resolved_output_index`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~CaptureTargetResolver|FullyQualifiedName~CapturePath|FullyQualifiedName~LongClipCommand|FullyQualifiedName~RecordingFeatures"` → fail.
- [ ] **Step 3: Implement**; delete `IndexOfScreen`, `MonitorByDevice`, `PrimaryMonitor`; fix every compile error the
  `CapturePlan` change causes in the buffer by passing `plan.Source` through (buffer logic itself is Task 4).
- [ ] **Step 4: Run** the filter + the broad `Capture|FFmpegCommand|Screenshot|ReplayBuffer|Recording` filter → pass.
- [ ] **Step 5: Commit** `fix(capture): record the chosen monitor by its real DXGI output`.

## Task 4 — Replay buffer: identity, fallback notice, automatic restart

**Files:** modify `Services/ReplayBufferService.cs` (`StartCore` ~396-436, exit handler ~545-595,
`RetargetAsync` ~1140-1170, `StopCoreAsync` ~1064, segment identity ~502-511), `Services/SegmentModel.cs:18-19`,
`Services/ClipStitcher.cs:141` (label only), `App.xaml.cs:155-162` (new notice toast); tests `SegmentModelTests.cs`,
new `ReplayBufferMonitorTests.cs` (use the existing buffer test seams/fakes of `ReplayBufferSaveEndToEndTests` /
`MemoryBufferUnitTests`; extract a small pure helper where the service has no seam).

**Consumes:** Tasks 1-3.

**Produces:**
- `SegmentIdentity.SourceKey(bool wgc, IntPtr hwnd, int width, int height, string displayKey)` → monitors
  `mon:{displayKey}:{W}x{H}` (output index deliberately excluded: a new primary renumbers outputs without changing
  what is captured). Check whether source keys are persisted across app restarts; if so, an old
  key must simply not match.
- `CaptureSource? _lastStartedSource` set in `StartCore`, never cleared by `StopCoreAsync`; the session-reset decision
  is `static bool SessionRules.ShouldResetSession(CaptureSource? prev, CaptureSource next, bool folderMoved)` =
  `folderMoved || prev is not null && !CaptureSourceRules.SameMonitor(prev, next)` (a mode change of the same
  monitor keeps the session; Native size changes are already separated by the source key).
- `RetargetAsync`: `videoUnchanged` = `CaptureSourceRules.SameSource` (+ the existing WGC conditions).
- `event EventHandler<string>? CaptureNotice` raised once when `plan.Warning` appears or changes, and once with
  „Aufnahme wieder auf „X““ / “Capturing “X” again” when it clears; `App.xaml.cs` shows Warning/Info toasts titled
  „Aufnahme-Monitor“ / “Capture monitor”. Each transition also logs `Logger.Warn` + `topology.Dump()`.
- Start log line (decision 8): `Capture.Monitor: saved={path|gdi|null} -> {Status} ({Reason}) display='{label}'
  gdi={gdi} adapter={luid} default={idx} output={o} {W}x{H}@({X},{Y}) primary={b} path={dda|amf}`; the existing
  `ReplayBuffer.Start` line keeps its place with `video=` from the new label.
- `NoDisplays` / resolver exception → do not start, `BufferError` with the message.
- `public Func<DateTime?>? LastDisplayChangeUtc { get; set; }` on `ReplayBufferService` (Task 5 sets it; null = no
  display change known).
- Unexpected ffmpeg exit (exit handler): keep the last 20 stderr lines in a ring; when
  `DisplayRestartPolicy.ShouldAutoRestart(string.Join("\n", ring), LastDisplayChangeUtc?.Invoke(), now,
  _autoRestarts)` and the buffer is still wanted on (the state `ToggleAsync`/`Stop`/manual-recording pause
  keep), schedule ONE warm restart after `1500 ms` (coalesced with any pending restart; `StartCore(clearHistory:false)`),
  log `ReplayBuffer: auto-restart after display change (n/3)`; no `BufferError`. Otherwise today's behaviour.

- [ ] **Step 1: Failing tests** — `Same_monitor_same_size_keeps_the_source_key_across_restarts`,
  `Other_monitor_same_output_and_size_gets_another_source_key`, `Native_size_change_gets_another_source_key`,
  `Session_resets_when_the_monitor_changes_across_a_warm_restart` (the old `CurrentPlan = null` bug),
  `Session_is_kept_when_only_the_mode_of_the_same_monitor_changes`, `Retarget_restarts_video_when_the_source_changes`,
  `Fallback_notice_is_raised_once_per_transition_and_once_on_return`,
  `Mode_change_exit_restarts_warm_once_without_error_toast`, `Exit_burst_is_bounded_then_reports_once`,
  `Exit_without_marker_keeps_todays_error_toast`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SegmentModel|FullyQualifiedName~ReplayBufferMonitor"` → fail.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the filter + `ReplayBuffer|Segment|ClipStitch|DiskSegment|MemoryBuffer` → pass.
- [ ] **Step 5: Commit** `fix(capture): keep the buffer on the same monitor and restart after mode switches`.

## Task 5 — Display watcher, capture list, Ctrl+F9 across a mode switch

**Files:** create `Services/DisplayTopologyWatcher.cs`; modify `AppHost.cs` (wiring, dispose, start-up dump),
`Views/CaptureView.xaml.cs:176-189, 290-308`, `Services/ManualRecordingService.cs` (exit handler ~128-175);
tests `DisplayTopologyWatcherTests.cs`, `CaptureMonitorListTests.cs` (pure list builder), `ManualRecordingTests`
area as it exists.

**Consumes:** Tasks 1-4.

**Produces:**
- `sealed class DisplayTopologyWatcher : IDisposable` — ctor `(DisplayTopologyCache cache, TimeProvider time,
  Func<CaptureSource?> currentSource, Action requestRetarget)`; subscribes `SystemEvents.DisplaySettingsChanged` and
  `PowerModeChanged` (Resume) (unsubscribes in `Dispose`); on an event: `LastChangeUtc = now`, `cache.Invalidate()`,
  (re)start a `1500 ms` timer; when it fires: read, log `Display topology changed: {old} -> {new}; target
  unchanged|changed -> restart` + `Dump()` on a fingerprint change, raise `event Action? TopologyChanged`, and call
  `requestRetarget` only when the re-resolved source is not `SameSource` as `currentSource()`.
  `DateTime? LastChangeUtc` feeds Task 4's policy.
- `AppHost`: create the watcher with `DisplayTopologyCache.Shared`, `TimeProvider.System`,
  `() => ReplayBuffer.CurrentPlan?.Source`, `ReplayBuffer.RequestRetarget`; pass `() => watcher.LastChangeUtc` to the
  buffer; log `DisplayTopologyCache.Shared.Get().Dump()` once at start; dispose with the app.
- `sealed record CaptureMonitorChoice(string Display, MonitorRef? Value)` (replaces the view's private
  `MonitorChoice`) and `static (IReadOnlyList<CaptureMonitorChoice> Items, int SelectedIndex)
  CaptureMonitorList.Build(DisplayTopology t, MonitorRef? saved)` — first entry `MonitorLabels.AutoPrimary` (value
  null), then one per display (`MonitorLabels.Build`, value `MonitorRef.Of(d)`), plus `BuildMissing(saved)` (value
  `saved`) when the saved monitor is not connected; selected = the saved one (resolved with `MonitorTargetRules`),
  the auto entry when nothing is saved.
- `ManualRecordingService` gets the same `Func<DateTime?>? LastDisplayChangeUtc` property as the buffer (Task 4). `CaptureView` uses it, rebuilds on `TopologyChanged` (both instances, dispatcher), keeps the
  selection, ignores the `SelectionChanged` its own rebuild causes, and on a user pick stores path + friendly + EDID +
  GDI name (or clears all four for the auto entry), saves and `RequestRestart()` as today.
- Ctrl+F9 across a mode switch (pending owner decision, default below): when the recording's ffmpeg exits with a
  `DisplayRestartPolicy` marker and the file is valid, the part is kept as a success and the recording continues
  automatically into `{name}_Teil2.mp4` (`_Teil3` …) after `1500 ms`; toast „Auflösung gewechselt – Aufnahme läuft in
  Teil 2 weiter“ / “Resolution changed – recording continues in part 2”; the same bounds as the buffer.

- [ ] **Step 1: Failing tests** — `Burst_of_display_events_causes_one_read_and_at_most_one_retarget`,
  `Mode_change_of_the_same_monitor_does_not_retarget`, `Monitor_gone_or_back_retargets`,
  `Last_change_time_is_set_on_every_event`, `Dispose_unsubscribes`;
  list: `Unset_selects_the_auto_entry`, `Saved_missing_monitor_is_listed_as_not_connected_and_selected`,
  `Rebuild_keeps_the_selection`; manual: `Mode_change_exit_keeps_part_1_and_starts_part_2`,
  `Exit_without_marker_still_fails_as_today`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~DisplayTopologyWatcher|FullyQualifiedName~CaptureMonitorList|FullyQualifiedName~ManualRecording"` → fail.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the filter → pass; then `dotnet build WKI_Clipper.sln --no-incremental` (0 warnings) and the FULL
  `dotnet test` once.
- [ ] **Step 5: Commit** `fix(capture): follow display changes in the buffer and the capture list`.

## After the tasks

1. Lean whole-branch review (2 lenses: capture correctness + R1-R8 walked through the code; lifecycle/threading/UI),
   one self-verifying reviewer each, one fix round, full suite once.
2. Live test with the owner (game check first, "Hände weg"): `DISPLAY_LIVE=1` reader tests; test build via
   `explorer.exe`; settings backup via `\\localhost\c$`; switch the monitor mode with
   `C:\Users\cr4zy\AppData\Local\Temp\claude\C--Users-cr4zy-Claude-Stuff\d11e53f0-1ff9-4ebc-96e5-2c15ea5d0ff3\scratchpad\resmode\setmode.ps1`
   (e.g. 3440×1440 → 2560×1080 → back; this monitor offers no 2560×1440) while the buffer runs with a preset → log shows one
   auto-restart each way, F9 clip across the switch plays, output size = preset; then the same with Native.
3. Ask the reporter (Faryco) for `wki_clipper.log` after updating: the `Capture.Monitor:` lines show his topology.
4. Release only on the owner's word.

## Pending owner decision

- Ctrl+F9 recording during a mode switch: continue automatically in `…_Teil2.mp4` (plan default) — or end the
  recording cleanly and keep the file with a notice?
