# Farbpaletten + Overlay-Position Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Two features:
- Live-switchable colour themes for the whole app, with separate Accent and Glass tabs, presets and custom colours.
- A performance overlay that can sit at 8 anchors or anywhere on screen, dragged while the board is open.

**Architecture:**
- Pure palette derivation and placement maths are fully unit-tested.
- Palette resource keys switch to `DynamicResource`. `ThemeService` swaps resource instances, and code-built UI is rebuilt through the existing language-rebuild path.
- The overlay gains a drag mode that is active only while the board is open.

**Tech Stack:** .NET 8 WPF, xunit 2.9.2.

**Spec:** `docs/superpowers/specs/2026-10-20-themes-and-overlay-position-design.md`. Read it together with this plan.

## Global Constraints
- **Repo and tools:** `C:\Users\cr4zy\Documents\CLAUDE_Projects\WKI_Clipper`, branch `master`. Use the PowerShell tool only (Bash is broken).
- **Text and code style:** All UI text goes through `L.T("Deutsch","English")`. Code and comments are in English, in the surrounding style.
- **Tests:** TDD. STA render tests run offscreen only. No visible windows, no input injection. The full suite stays green with 0 warnings.
  - Known flaky: `FFmpegJobRunnerIntegrationTests.A_healthy_slow_job_is_not_mistaken_for_a_hung_one` under full load. If it is the only failure, rerun it alone.
- **Commits:** Write the message to a file without a BOM and commit with `git commit -F`. The last line is exactly `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Stage only your own files.
- **Not yet:** no version bump, no build, no install. The release happens together with the shortcut bar (its plan's Task 9).
- **Colours that never change:** `TextColor`, `DangerColor`, `SuccessColor`, the warning amber, and the performance overlay's own colour choice.
- **Order and roles:** Run O1 → O2 → T1 → T2 → T3, one agent at a time; all tasks are Sonnet. One Opus review covers everything before 0.17.0.

## Review Focus
1. **The default theme must be pixel-identical to today.** Violet glass + WKI orange reproduce the current hex values exactly. Covered by T1 `Default_palette_reproduces_current_theme_exactly`.
2. **A theme switch with the board open must not lose widget geometry or pinned widgets.** Covered by T2 `Theme_rebuild_keeps_geometry_and_pins`.
3. **Web widgets (WhatsApp/Spotify) must not reload when the theme changes.** Their chrome recolours through `DynamicResource`. Covered by T2 `Web_widgets_are_not_rebuilt_on_theme_change`.
4. **A custom accent with too little contrast on the glass must still be readable.** Covered by T1 `Dark_custom_accent_is_lifted_to_contrast_3`.
5. **An overlay left `Free` on an unplugged monitor, or after a resolution change, must stay fully visible.** Covered by O1 `Free_overlay_on_missing_monitor_lands_on_primary_visible`.

---

### Task O1: Overlay anchors and placement maths
**Files:**
- Modify `Models/AppSettings.cs`: replace the `ScreenCorner Corner` property with `OverlayAnchor Anchor` and add `OverlayFreePlace? Free`.
- Modify `Services/SettingsService.cs`: migrate the old `Corner` JSON value into `Anchor` (or use a JSON alias; keep old settings files loading).
- Modify `Services/PerfOverlayLayout.cs`.
- Test: `PerfOverlayPlacementTests.cs`.

**Produces:**
```csharp
public enum OverlayAnchor { TopLeft, TopCenter, TopRight, MiddleLeft, MiddleRight, BottomLeft, BottomCenter, BottomRight, Free }
public sealed class OverlayFreePlace { public string Monitor { get; set; } = ""; public int HEdge { get; set; } /*0 left 1 centre 2 right*/
  public int VEdge { get; set; } /*0 top 1 middle 2 bottom*/ public double RelDX { get; set; } public double RelDY { get; set; } }
public static class PerfOverlayPlacement {
  public static LayoutRect Place(OverlayAnchor a, OverlayFreePlace? free, double w, double h, IReadOnlyList<MonitorBox> monitors, string? preferredMonitor, double margin);
  public static (OverlayAnchor Anchor, OverlayFreePlace? Free) FromDrop(LayoutRect dropped, MonitorBox monitor, double snapDip, double margin);
}
public sealed record MonitorBox(string Device, LayoutRect WorkArea, bool Primary);
```
- [ ] **Step 1: Failing tests**
  - `Old_corner_values_migrate_to_same_anchor` (all 4)
  - `Each_fixed_anchor_places_with_margin` (8 theory rows)
  - `Drop_near_top_center_snaps_to_TopCenter`
  - `Drop_in_open_area_becomes_free_with_nearest_third_edges`
  - `Free_place_keeps_right_edge_when_overlay_grows`
  - `Free_overlay_on_missing_monitor_lands_on_primary_visible` (Review Focus 5)
  - `Free_overlay_is_clamped_after_resolution_change`
- [ ] **Step 2:** Run `dotnet test WKI_Clipper.Tests --filter PerfOverlayPlacementTests`. Expected: FAIL.
- [ ] **Step 3:** Implement. The existing corner code delegates to `Place`.
- [ ] **Step 4:** Full suite green. Commit `feat(overlay): eight anchors and free placement maths`.

### Task O2: Overlay drag mode and settings UI
**Files:**
- Modify `Views/PerfOverlayWindow.cs` and `Views/WidgetHost.cs` (`OpenBoard`/`CloseBoard` call `PerfOverlayWindow.SetDragMode(bool)`).
- Modify `Views/PerformanceView.xaml.cs`: a "Position" selector with 9 entries and the hint text.
- Test: `PerfOverlayDragTests.cs` (pure drag-state logic + STA render of the selector).

**Behaviour:**
- Drag mode on: remove `WS_EX_TRANSPARENT`, set `IsHitTestVisible = true`, and show a dashed outline (`AccentBrush` via `DynamicResource`).
- Mouse down/move/up moves the window. On drop, call `FromDrop`, then save.
- Drag mode off: restore click-through. Board closed means never hit-testable.

- [ ] **Step 1: Failing tests**
  - `Drag_mode_only_while_board_open`
  - `Drop_saves_anchor_or_free`
  - `Position_selector_lists_nine_entries`
  - `Selecting_fixed_anchor_clears_free_place`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Green. Commit `feat(overlay): drag the performance overlay while the board is open`.

### Task T1: Palette model and derivation (pure)
**Files:**
- Create `Services/Theme/ThemePalette.cs`.
- Modify `Models/AppSettings.cs` (`ThemeSettings`) and `Services/SettingsService.cs` (`EnsureSections` guard).
- Test: `ThemePaletteTests.cs`.

**Produces:**
```csharp
public sealed class ThemeSettings { public string AccentPreset { get; set; } = "wki-orange"; public string? AccentCustom { get; set; }
  public string GlassPreset { get; set; } = "violet"; public string? GlassCustom { get; set; } }
public sealed record ThemePreset(string Id, string NameDe, string NameEn, string Hex);
public static class ThemePresets { public static IReadOnlyList<ThemePreset> Accent { get; } public static IReadOnlyList<ThemePreset> Glass { get; } }
public sealed record ThemePalette(IReadOnlyDictionary<string, Color> Colors,             // key → colour for every palette key in spec A
                                  IReadOnlyDictionary<string, Color[]> GradientStops);   // ActiveBrush, AccentGradientBrush, BarBrush, SurfaceBrush
public static class ThemePaletteBuilder { public static ThemePalette Build(ThemeSettings s); public static string? NormalizeHex(string? input); }
```
- Accent preset hex values come from spec A. The glass presets are defined by hue and saturation. Violet must reproduce the current values exactly: store its exact hex set as the reference, and derive the others from it by HSL hue and saturation substitution.
- Colours to reproduce exactly:
  - `BgColor #140E1F`, `PanelColor #221A30`, `PanelHoverColor #2D2340`, `BorderColor #3A2F4D`, `MutedColor #8E86A3`, `FaintColor #5E5772`.
  - Surface stops `#F2261D36 / #EB1A1426 / #EB1A1426`.
  - `AccentLightColor #FF9F4A`, `AccentDim #8F4A2E`, Active `#4DFF7A2C → #42963E2C`, Bar `#C8461A → accent → light`.

- [ ] **Step 1: Failing tests**
  - `Default_palette_reproduces_current_theme_exactly` (Review Focus 1)
  - `Every_accent_preset_builds_all_keys`
  - `Every_glass_preset_builds_all_keys`
  - `Custom_hex_overrides_preset`
  - `Invalid_custom_hex_falls_back_to_preset`
  - `Dark_custom_accent_is_lifted_to_contrast_3` (Review Focus 4)
  - `Signal_and_text_colours_are_not_in_the_palette`
  - `Missing_theme_section_gives_defaults`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Green. Commit `feat(theme): palette model and derivation`.

### Task T2: Live theme switching
**Files:**
- Modify `Views/Theme.xaml`: palette keys inside styles and templates use `DynamicResource`.
- Modify every other `*.xaml` with palette keys, changing `StaticResource` to `DynamicResource` for the spec A keys only.
- Create `Services/Theme/ThemeService.cs`.
- Modify `Views/WidgetHost.cs`: rename `RebuildForLanguageChange` to `RebuildUi(string reason)`, and call it from both `L.LanguageChanged` and `ThemeService.Changed`.
- Modify `App.xaml.cs`: apply the saved theme before the first window opens.
- Modify `Views/WidgetSidebarWindow.cs:249` (hard-coded accent stroke).
- Test: `ThemeServiceTests.cs` (STA).

**Produces:**
```csharp
public sealed class ThemeService { public event Action? Changed; public void Apply(ThemePalette p);   // replaces Application.Current.Resources entries with NEW instances
  public static void ApplyTo(ResourceDictionary target, ThemePalette p); }
```
- [ ] **Step 1: Failing tests** (STA, offscreen, using a `ResourceDictionary` loaded from `Theme.xaml`)
  - `Styled_button_picks_up_new_accent_after_apply`
  - `Gradient_brushes_are_rebuilt_with_new_stops`
  - `Static_non_palette_keys_unchanged`
  - `Theme_rebuild_keeps_geometry_and_pins` (Review Focus 2)
  - `Web_widgets_are_not_rebuilt_on_theme_change` (Review Focus 3)
  - `No_palette_key_left_as_StaticResource`: scan all `*.xaml` files in the project and fail on any `{StaticResource <palette key>}`.
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement. Never mutate an existing brush instance; it may be frozen inside a sealed style.
- [ ] **Step 4:** Green. Commit `feat(theme): switch colours live`.

### Task T3: Colour picker UI
**Files:**
- Create `Views/ThemePickerWindow.cs`.
- Modify `Views/WidgetSidebarWindow.cs`: add a footer button with a palette glyph and tooltip "Farben"/"Colours".
- Test: `ThemePickerTests.cs` (pure view logic + STA render).

**UI per spec A:**
- Two tabs, "Akzent" and "Glas". Each tab has:
  - a preset swatch grid (circle plus name)
  - "Eigene Farbe" with a hue slider, a saturation slider, a hex field and a preview
  - a "Standard" reset button
- Clicking a swatch applies the colours and saves at once.
- The window uses the board style and is no-activate. It closes on an outside click or `Esc`.

- [ ] **Step 1: Failing tests**
  - `Picker_lists_all_presets_per_tab`
  - `Choosing_preset_saves_and_applies`
  - `Hex_field_rejects_invalid_and_keeps_previous`
  - `Standard_resets_only_its_tab`
  - `Picker_renders_both_tabs`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Green. Commit `feat(theme): colour picker in the sidebar`.

### Final (with the shortcut bar's Task 9)
- [ ] **One Opus review** of the shortcut bar, chat fix, themes and overlay changes, then fixes.
- [ ] **README:** "Colours" and "Overlay position".
- [ ] **Live test with Phillip:**
  - every accent and glass preset
  - a custom colour
  - board open while switching
  - WhatsApp stays logged in
  - dragging the overlay, an anchor snap, and a restart
- [ ] **Release 0.17.0.**
