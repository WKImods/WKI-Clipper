# Farbpaletten (Akzent + Glas) und frei platzierbares Leistungs-Overlay — Design

Stand 2026-10-20 · WKI Clipper · Entscheidungen von Phillip im Chat 2026-10-20.

## A. Farbpaletten

### Absicht
Wie beim Leistungs-Overlay, aber für den ganzen Clipper: In der linken Seitenleiste ein **Farb-Knopf**; er öffnet ein
Panel mit **zwei Reitern: „Akzent“ und „Glas“** (frei kombinierbar). Pro Reiter fertige Paletten + **eigene Farbe**.
Wirkt **sofort** (ohne Neustart), wird gespeichert.

### Paletten (meine Wahl, Namen DE/EN)
- **Akzent:** WKI-Orange `#FF7A2C` (Standard) · Episches Blau `#4C7DFF` · Karmesinrot `#DC143C` · Smaragd `#14C98B`
  · Königsviolett `#9B5CF6` · Gold `#F5B700` · Eisblau `#22D3EE` · Magenta `#FF4FA3`.
- **Glas:** Violett (Standard, heutige Werte) · Nachtblau · Graphit · Bordeaux · Waldgrün · Schwarz (OLED).
- **Eigene Farbe:** Farbton- und Sättigungs-Regler + Hex-Feld + Vorschau; „Standard“ setzt den Reiter zurück.

### Was sich färbt
- **Akzent** treibt: `AccentColor`, `AccentLightColor` (+15 % Helligkeit), `AccentDim` (dunkler, entsättigt),
  `AccentBrush`, `AccentDimBrush`, `AccentGradientBrush`, `ActiveBrush` (Akzent α 0x4D → dunkler Akzent α 0x42),
  `BarBrush` (dunkler Akzent → Akzent → hell). Dazu die zwei hartcodierten Akzentstellen
  (`WidgetSidebarWindow.cs:249` Strich, sonst keine). Das Leistungs-Overlay behält **eigene** Farbwahl.
- **Glas** treibt: `BgColor`, `PanelColor`, `PanelHoverColor`, `BorderColor`, `MutedColor`, `FaintColor` und
  `SurfaceBrush`-Stopps — aus **Farbton + Sättigung** abgeleitet mit den heutigen Helligkeiten (Violett = heutige
  Werte exakt, Test).
- **Nie umgefärbt:** Text (`TextColor`), Signalfarben `DangerColor`, `SuccessColor`, Warn-Gelb, die 61 hartcodierten
  Status-/Signalfarben im Code, Spiele-/App-Logos.
- Lesbarkeit: Akzent-Paletten, deren Kontrast auf dem Glas zu gering ist, sind ausgeschlossen bzw. bei eigener
  Farbe wird die Helligkeit auf ein Minimum angehoben (WCAG-Kontrast ≥ 3:1 gegen `PanelColor`), Test.

### Technik (Machbarkeit geprüft 2026-10-20)
- `Theme.xaml`: 12 Farben, 14 Pinsel, 11 Verläufe; 180 `StaticResource`-Verwendungen, 0 `DynamicResource`;
  226 `FindResource`-Aufrufe in Code-gebauter UI.
- WPF friert Pinsel ein, die in Styles als Setter-Wert benutzt werden → Pinsel „an Ort und Stelle“ umfärben ist
  **nicht** sicher. Deshalb:
  1. **Paletten-Schlüssel** (Liste oben) werden in allen XAML-Dateien inkl. `Theme.xaml`-Styles/-Templates auf
     `DynamicResource` umgestellt; alle anderen Schlüssel bleiben `StaticResource`.
  2. `ThemeService.Apply(palette)` ersetzt die Einträge in `Application.Current.Resources` durch **neue** Instanzen.
  3. **Code-gebaute UI** (226 `FindResource`) wird über den vorhandenen Neuaufbau-Weg neu gebaut
     (`WidgetHost.RebuildForLanguageChange` → verallgemeinert zu `RebuildUi(reason)`; Web-Widgets bleiben stehen,
     ihr Rahmen färbt sich über DynamicResource um). `TrayHost`, Toasts, Editor-Fenster: DynamicResource bzw.
     Neuaufbau beim nächsten Öffnen.
- Einstellungen: neuer Abschnitt `Theme { AccentPreset = "wki-orange"; AccentCustom?; GlassPreset = "violet";
  GlassCustom? }`, Null-Schutz in `EnsureSections`, kein Schema-Sprung nötig.
- UI: Farb-Knopf im Seitenleisten-Fuß (Glyphe Palette), öffnet `ThemePickerWindow` (Board-Stil, no-activate wie
  Widgets, schließt bei Klick daneben). Klick auf Palette = sofort anwenden + speichern.

## B. Leistungs-Overlay frei platzierbar

### Absicht
Statt nur 4 Ecken: **8 feste Plätze** (4 Ecken + 4 Kantenmitten) und **„Frei“** — bei offenem Board mit der Maus ziehen
(wie das Fadenkreuz), im Spiel immer durchklickbar.

### Verhalten
- `ScreenCorner` → `OverlayAnchor { TopLeft, TopCenter, TopRight, MiddleLeft, MiddleRight, BottomLeft, BottomCenter,
  BottomRight, Free }`; alte Werte werden 1:1 übernommen (Migration der vier Ecken, Test).
- **Ziehen:** nur bei offenem Board; das Overlay wird dann trefferbar (`WS_EX_TRANSPARENT` weg) und zeigt einen
  gestrichelten Rahmen; Board zu → wieder durchklickbar. Einrasten: innerhalb 24 DIP an einem der 8 festen Punkte →
  dieser Anker; sonst `Free`.
- **Free speichert** Monitor (Gerätename) + **Anker-Kanten** (links/mitte/rechts, oben/mitte/unten = nächstes Drittel)
  + Abstand von dieser Kante relativ zur Monitorgröße. Wächst/schrumpft das Overlay (mehr Werte, Skalierung), bleibt
  die Ankerkante stehen. Monitor fehlt → Hauptmonitor. Immer ≥ 1 vollständig sichtbare Zeile (Klemmen).
- Einstellungen-UI: Auswahl „Position“ mit den 9 Einträgen + Hinweis „Frei: Board öffnen und das Overlay ziehen“.

## Nicht-Ziele
Eigene Schriftarten/Theme-Dateien teilen; Farbverläufe frei editierbar; Overlay auf anderen Monitoren als dem
gewählten Spielmonitor; Overlay im Spiel ziehen (nur bei offenem Board).
