# Monitor capture: the right monitor, stable identity, resolution changes

Date: 2026-11-09. Base: master `4876b9a` (0.19.0). Reported by a user: with capture mode "Ganzer Monitor"
the clipper now and then records another monitor, and its monitor labels do not match Windows Settings.
The owner approved the fix design below and added one hard requirement: resolution differences and
resolution changes (game in 1080p on a 4K monitor, clipper set to 1440p) must never cause errors or
complications.

Investigation notes (read-only, code + ffmpeg 8.1 source + live probe): scratchpad of session d11e53f0,
summarised here.

## 1. Root causes (verified in code)

1. **Wrong index meaning.** `CaptureTargetResolver` passes the position of the monitor in
   `Screen.AllScreens` (EnumDisplayMonitors order, no guaranteed order) as `ddagrab output_idx`.
   ddagrab counts the DXGI outputs of ONE adapter: the default D3D11 adapter of ffmpeg.exe
   (`vsrc_ddagrab.c`: `av_hwdevice_ctx_create(..., D3D11VA, NULL, ...)` → `IDXGIAdapter_EnumOutputs`).
   DXGI output 0 is the primary monitor (Microsoft docs). With more than one monitor the two orders
   only match by chance; monitors on another adapter (iGPU / mainboard port, DisplayLink) are not
   reachable at all ("Failed to enumerate DXGI output N"). Same flaw for `vsrc_amf monitor_index`
   (opt-in AMF capture) and for F10 screenshots (`ResolveActiveMonitor` → `IndexOfScreen`).
2. **Unstable identity and a silent fallback.** The chosen monitor is stored as the GDI name
   `\\.\DISPLAYn`, which is an adapter source slot (probe: DISPLAY1-5 = dGPU, DISPLAY6-10 = iGPU), not a
   monitor property. When it is not found, `MonitorByDevice` silently takes the primary monitor;
   `IndexOfScreen` returns 0 on a miss.
3. **Re-resolved on every buffer (re)start, nothing listens to display changes.** Ctrl+F9 end,
   settings changes, watchdog, GameOnly retarget all re-resolve. Only `WidgetHost` listens to
   `DisplaySettingsChanged`. When ddagrab dies on a mode change (ACCESS_LOST, "Output parameters
   changed!") the buffer stays dead with a toast; no automatic restart.
4. **Amplifiers.** The session reset on a target change never fires (`StopCoreAsync` sets
   `CurrentPlan = null` before `StartCore` reads it). `SourceKey` is `mon:{index}:{W}x{H}`, so a
   different physical monitor with the same index and size is spliced into one clip.
5. **Labels.** "Monitor N" is the `AllScreens` position + 1. Windows' own "Identify" number has no
   supported API (OBS, NirSoft and others do not reproduce it either).

## 2. Decisions (owner approved)

1. The clipper asks Windows/DXGI which output is which monitor; no index guessing anywhere.
2. The chosen monitor is stored by a stable identity: the monitor device path (CCD
   `DISPLAYCONFIG_TARGET_DEVICE_NAME.monitorDevicePath`, what OBS uses), plus friendly name and EDID
   manufacturer/product for display and re-matching. The GDI name stays as a legacy value and
   tie-breaker; settings from 0.19 and older are upgraded on first resolve.
3. Labels use the real name like Windows: "LG ULTRAGEAR+ · 3440×1440 · links · Hauptmonitor"
   (side relative to the primary from the coordinates; duplicates get the connector or "(2)").
   No invented "Monitor N" numbers. A missing saved monitor shows "X (nicht verbunden)"; the
   unset choice shows "Hauptmonitor (automatisch)".
4. Chosen monitor missing → record the primary monitor, never silently: warning in the log with the
   topology dump, ONE toast per transition ("Gewählter Monitor „X“ nicht verbunden – nehme
   Hauptmonitor „Y“"), status/label "Ersatz: Y (X nicht verbunden)", setting unchanged, automatic
   return when the monitor is back.
5. A monitor on a non-default adapter (another GPU) is NOT captured via a foreign index. Step 1:
   treated like missing, with the reason "hängt an GPU X, Aufnahme erreicht nur Y" in the warning.
   Step 2 (explicit adapter for ddagrab) is built later only if user data shows such setups.
6. AMF capture (opt-in, `vsrc_amf`) is used only when the mapping is certain (the monitor is on the
   default adapter and only one adapter has outputs); otherwise ddagrab with a log line.
7. Display topology changes (DisplaySettingsChanged, resume from sleep) are watched, debounced
   1500 ms, re-resolved; the buffer restarts only when the capture source really changed, and both
   capture settings lists rebuild.
8. Every buffer start and manual recording logs one line with saved identity → status, display name,
   GDI name, adapter, default adapter, output index, size, position, primary, path (dda/amf); the
   topology dump is logged at app start, on every fingerprint change and on every fallback.

## 3. Hard requirement: resolutions (owner)

Example: 4K monitor, game in 1920×1080, clipper output preset 1440p. None of the following may produce
errors, a dead buffer, a wrong monitor, a fallback toast or a broken clip:

- **R1 Identity is not resolution.** Resolution, refresh rate, position, HDR and the primary flag are
  never part of matching the chosen monitor. A mode change is never "monitor missing" or "other monitor".
- **R2 Exclusive fullscreen with a lower mode.** The display mode switches (e.g. 3840×2160 → 1920×1080),
  ddagrab exits (ACCESS_LOST / "Output parameters changed!"). The buffer restarts automatically on the
  same physical monitor at the new mode within about 2 s, without an error toast; same when the game
  exits and the mode switches back. Bounded: at most 3 automatic restarts per 60 s, then the existing
  "Buffer unerwartet beendet" toast once.
- **R3 Borderless / windowed game in a lower resolution.** The desktop stays at the monitor's mode;
  nothing restarts.
- **R4 Output size is the preset, always.** With a preset (720p/1080p/1440p/…) the encoder output is
  that size for every source size, scaled and letterboxed as today (source larger → downscale, source
  smaller → upscale; odd source sizes are fine because the pad target is even). Segments before and
  after a mode change of the same monitor keep the same stream parameters, so the history survives
  and a clip across the change works (spliced or stitched by the existing generation logic, never a
  corrupt file).
- **R5 Native preset.** Output follows the source size. Segments of different sizes are never joined
  by stream copy (the source key contains the size); a clip across the change is handled like any
  other generation change today (gap note / stitch), never a corrupt file.
- **R6 Sizes are physical pixels** (PerMonitorV2, as the app). Labels show the current mode of the monitor.
- **R7 Bitrate.** Native uses the current source pixels of the resolved monitor (not `AllScreens[index]`);
  presets unchanged.
- **R8 Mode flapping.** A game that switches modes several times while starting causes at most one
  restart after the debounce, not one per flip.

## 4. Architecture (names fixed)

Pure rules + thin Windows adapters, xUnit tests with fake topologies.

- `Native/DisplayConfig.cs`: P/Invoke `GetDisplayConfigBufferSizes`, `QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)`,
  `DisplayConfigGetDeviceInfo` (SOURCE_NAME, TARGET_NAME). The probe's working code is in the session
  scratchpad `monitor-probe/MonProbe.cs`.
- `Services/DisplayTopology.cs`: `DisplayAdapterInfo(int DxgiIndex, long Luid, string Name, bool IsSoftware)`,
  `DisplayInfo(nint HMonitor, string GdiName, string? DevicePath, string? FriendlyName, int X, int Y, int Width,
  int Height, bool IsPrimary, int? AdapterIndex, int? OutputIndex, string? Connector, ushort? EdidMfg,
  ushort? EdidProduct)`, `DisplayTopology(Adapters, Displays, int DefaultAdapterIndex)` with `Fingerprint`
  (identity + modes) and `Dump()`.
- `Services/DisplayTopologyReader.cs`: `static DisplayTopology Read()` — EnumDisplayMonitors directly (not the
  cached `Screen.AllScreens`), CCD names joined by GDI name, DXGI via Vortice (`CreateDXGIFactory1`,
  `EnumAdapters1`, `EnumOutputs`, joined by HMONITOR), default adapter = LUID of `D3D11CreateDevice(null,
  Hardware)` (what ffmpeg does). Never throws; missing parts are null with a reason.
- `Services/MonitorTargetRules.cs`: `MonitorResolution Resolve(MonitorRef? saved, DisplayTopology topo)`,
  status Exact (device path) / Matched (unique EDID mfg+product, path upgraded; identical models only with
  the GDI name as tie-breaker) / Legacy (GDI name only, upgrade requested) / PrimaryByDefault (nothing saved)
  / FallbackPrimary (missing or on another adapter, with reason) / NoDisplays.
- `Services/CaptureSourceRules.cs`: `DdaSource? ForDdagrab(DisplayInfo, DisplayTopology)` (output index on the
  default adapter, null otherwise), `int? ForAmf(...)` (decision 6), `bool SameSource(a, b)` (identity +
  adapter + output + size).
- `Services/MonitorLabels.cs`: `Build(DisplayInfo, DisplayTopology)`, `BuildMissing(MonitorRef)`.
- `Services/DisplayTopologyWatcher.cs`: DisplaySettingsChanged + PowerModeChanged(Resume), 1500 ms debounce,
  `TopologyChanged` event, asks the buffer to retarget when the source changed.
- `Services/DisplayRestartPolicy.cs`: `ShouldAutoRestart(...)` for R2/R8 (ffmpeg exit shortly after a display
  change or with a known ddagrab mode-change error, bounded attempts).
- `CaptureTargetResolver`: resolves through the topology and the rules; `CapturePlan` carries the resolved
  source (display identity, GDI name, HMONITOR, output index, AMF index, width, height, resolution status,
  warning) instead of a bare `MonitorIndex`. Auto/Window fallbacks and screenshots go hwnd/point → HMONITOR →
  topology. `IndexOfScreen` / `MonitorByDevice` are removed.
- `FFmpegCommandBuilder`: `ddagrab=output_idx=<real DXGI index>`; AMF index only when certain; bitrate (R7) and
  screenshots from the plan.
- `ReplayBufferService` / `SegmentModel`: session-reset decision from the last STARTED source (kept across
  `StopCoreAsync`), `SourceKey` for monitors from the display identity + size (the output index is left out:
  a new primary renumbers outputs without changing what is captured; 0.19 segments intentionally do not match), retarget compares `SameSource`, labels from `MonitorLabels`, auto restart per
  `DisplayRestartPolicy`.
- `CaptureProfile`: `MonitorDevicePath`, `MonitorFriendlyName`, `MonitorEdidMfg`, `MonitorEdidProduct`; the
  "stable across replugging" comments are corrected.
- `CaptureView` / `StatusView`: list rebuilt on `TopologyChanged`, entries per decision 3, labels with the
  fallback hint.

## 5. Not in scope

- Explicit adapter selection for monitors on another GPU (step 2), `gfxcapture`.
- Windows' "Identify" number.
- The paused widget layout issue with exclusive-fullscreen games (separate bug).
