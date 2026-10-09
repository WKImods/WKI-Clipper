# WKI Clipper Redesign (v0.15) — Design

Agreed with Phillip on 2026-10-09 from a reference shot (dark violet glass sidebar with
coral accents) and an approved HTML mockup (v2). Scope: new look + new sidebar; widget
contents follow automatically through the theme.

## Decisions

- Colour world: **WKI orange on violet** — violet-black depth and glassy surfaces from the
  reference, accent stays WKI orange (#FF7A2C, gradient to #FF9F4A).
- The top launcher pill is **replaced by a sidebar** on the left of the board monitor.
- Effects limited to what WPF layered windows render cheaply (no large blurred shadows).
- Not included: "3 new" gallery hint, light mode, re-layout of individual widget contents,
  changes to toasts/REC badge/perf overlay beyond the palette.

## 1. Tokens (Theme.xaml)

Background #140E1F · panel #221A30 · panel hover #2D2340 · text #EEEAF6 · muted #8E86A3 ·
faint #5E5772 (section titles) · border #3A2F4D · danger #E5484D · accent #FF7A2C /
#FF9F4A · active = warm gradient (#4DFF7A2C → #42963E2C). Radii: windows 16, rows 11,
pills full. Font: Segoe UI Variable Text → Segoe UI. Icons: Segoe Fluent Icons (via
`IconGlyph`). Backdrop dim tinted violet instead of black.

## 2. Sidebar (replaces WidgetLauncherWindow)

- Left edge of the board monitor's work area, 16 DIP margin, full height. Expanded 280 DIP,
  collapsed 72 DIP; collapsed state persisted (`WidgetSettings.SidebarCollapsed`).
- Header: "W" avatar, "WKI Clipper", live status (buffer active · N s / buffer paused /
  recording mm:ss) with a coloured dot; collapse button.
- Sections (pure `WidgetCatalog`): **Aufnahme** Capture, Audio, Gallery, Performance,
  Crosshair · **Stream** Streaming, Mixer, Sources, Go Live, Chat · **Apps** WhatsApp,
  Stream music, Spotify. Settings sits in the footer.
- Rows: icon + label, open widget = warm highlight + orange edge strip, WhatsApp unread as
  a pill (collapsed: dot on the icon). Collapsed: icons only, tooltips.
- Footer: Raster and Überlappen chips, Einstellungen, clock.
- The sidebar is a fixed obstacle for the grid; widgets lying under it are moved to its
  right edge when the board opens (saved).

## 3. Widget windows

Radius 16, translucent violet surface with a soft top highlight, hairline border; title bar
transparent with the widget's icon, title, then muted opacity slider / pin / close.
WhatsApp (direct rendering) keeps a solid surface; Windows rounds it at 8 px.

## 4. Controls (implicit theme styles)

Buttons (subtle; `AccentButton` = orange gradient), CheckBox (filled orange when checked),
Slider (thin track, orange fill, round thumb), ComboBox, TextBox, slim ScrollBar,
`LauncherToggleTemplate` chips (warm active), settings tabs.

## 5. Testing

Unit: every widget except Settings in exactly one section, section order, status text.
Visual: screenshots of open board, collapsed sidebar, a widget, and the board closed.
