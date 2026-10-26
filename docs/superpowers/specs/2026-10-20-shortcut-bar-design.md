# Verknüpfungsleiste (Game-Bar-Stil) — Design

Stand 2026-10-20 · WKI Clipper (.NET 8 WPF) · ersetzt `2026-10-20-quick-launch-design.md` (Fenster holen/parken —
von Phillip verworfen).

## 1. Absicht
Wie die Xbox Game Bar: oben mittig im Board eine schmale Leiste. **Eingeklappt** zeigt sie das erkannte Spiel
(Logo) mit „›“. **Aufgeklappt** zeigt sie Verknüpfungen: **Spiele | Launcher | Browser | ⚙**. Ein Klick schließt
das Board und **startet das Programm ganz normal** (wie im Startmenü) — kein WKI-Fenster, kein Verschieben,
kein Holen/Wegschicken. Die linke Seitenleiste bleibt unverändert.

## 2. Ziele / Nicht-Ziele / Anti-Ziele
**Ziele**
- Leistenfenster oben mittig, nur bei offenem Board sichtbar, Clipper-Glas-Look, eingeklappt/aufgeklappt (Zustand
  gespeichert, Standard: aufgeklappt).
- Launcher automatisch: Steam, EA app, Epic Games, GOG GALAXY, Ubisoft Connect, Battle.net, Rockstar Games,
  Wargaming.net Game Center, Xbox-App. Nur installierte erscheinen.
- Spiele: Vorschläge aus vom Clipper erkannten Spielen + Auswahl aus Steam-/Epic-Bibliothek + eigene
  Verknüpfung (.lnk/.url/.exe). Spiele starten über den richtigen Weg (Steam/Epic-Link, Hersteller-Verknüpfung).
- Browser: Standard-Browser (= Windows-Standard, änderbar) + Opera, Chrome, Comet, Firefox wählbar; fehlt einer →
  Dialog „Download-Seite öffnen / anderen Browser nehmen“. Zusätzlich erkannte (Brave, Opera GX, Edge) wählbar.
- Logos aus den Programmdateien/Verknüpfungen (256 px, Alpha).
- Bearbeiten über ⚙: ein-/ausblenden, Reihenfolge, hinzufügen/entfernen, Standard-Browser, optionale
  Tastenkombi pro Verknüpfung (Standard unbelegt).

**Nicht-Ziele:** Fenster holen/parken/einbetten; Web-Versionen; Clipper lädt/installiert nichts; Spielezeit-
Statistiken; Controller-Kombis für Verknüpfungen (Windows gibt ohne Tastatur/Maus-Eingabe kein Vordergrundrecht);
Drag & Drop aus dem Explorer (das Board verdeckt den Desktop).

**Anti-Ziele:** Spiel startet am Launcher vorbei und scheitert (DRM/Updates) · Deinstallations-Verknüpfung wird
gestartet · etwas startet mit Admin-Rechten aus dem Clipper · die Leiste verdeckt Widgets dauerhaft oder schiebt das
Layout bei jedem Öffnen umher · Ruckler im Spiel (Scans nur beim Öffnen des Bearbeiten-Dialogs/Board-Öffnen, gecacht).

## 3. Belegte Fakten (Phillips PC, 2026-10-20)
- **Offizielle Startziele aus dem Startmenü** (≠ Haupt-Exe!): Steam `steam.exe`; EA `EALauncher.exe`; Epic
  `…\Portal\Binaries\Win32\EpicGamesLauncher.exe`; GOG `GalaxyClient.exe`; Ubisoft `UbisoftConnect.exe`; Battle.net
  `Battle.net Launcher.exe`; Rockstar `LauncherPatcher.exe`; Wargaming `wgc.exe`. World of Tanks:
  `C:\Games\World_of_Tanks_EU\wgc_api.exe --open`; daneben „… deinstallieren.lnk“ → `wgc_api.exe --uninstall`.
- **Xbox-App** installiert: `Microsoft.GamingApp_8wekyb3d8bbwe` (Start über `shell:AppsFolder\<AUMID>`; AUMID `Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App`, per `Get-StartApps` bestätigt).
- **Steam:** `libraryfolders.vdf` → Bibliotheken `C:\Program Files (x86)\Steam`, `D:\SteamLibrary`;
  `appmanifest_<id>.acf` liefert appid/name/installdir (u. a. Arma Reforger 1874880, DayZ 221100, WARDOGS 1867240).
  Daneben Nicht-Spiele (Steamworks Common Redistributables 228980, Soundtracks, Tools).
- **Epic:** `C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests\*.item` (JSON: AppName, DisplayName,
  InstallLocation, LaunchExecutable) — Fortnite, Marvel's Spider-Man Remastered.
- **Logos:** `IShellItemImageFactory.GetImage(256, ICONONLY|BIGGERSIZEOK)` funktioniert für .exe und .lnk
  (Alpha: DIB-Bits als 32bpp ARGB übernehmen, `FromHbitmap` verliert Alpha).
- **Browser** (StartMenuInternet HKLM/WOW6432Node/HKCU): Chrome, Brave, Comet (HKCU). Windows-Standard = Comet.
  `IEXPLORE.EXE` registriert, IE aber seit 2023 abgeschaltet → nie anbieten. **Edge nicht installiert** (Appx-Stub).

## 4. Verhalten
### 4.1 Leiste
- Eigenes Board-Fenster (topmost, no-activate wie die Widgets), oben mittig auf dem Board-Monitor, Höhe ~56 DIP.
  Erscheint/verschwindet mit dem Board. Eine breite Leiste rutscht nach rechts, statt die Seitenleiste (Kopf, Einklapp-Knopf) zu
  überdecken, und wird am rechten Rand abgeschnitten (sie scrollt dann). Bei offenem Board liegt sie über dem Leistungs-Overlay
  (Anker „Oben Mitte“), damit Chip und Kacheln klickbar bleiben; ein offenes Bearbeiten-Fenster schließt mit dem Board.
- **Hindernis wie die Seitenleiste:** Widgets, die das Leistenrechteck schneiden, werden beim Öffnen nach unten
  geschoben — mit demselben Mechanismus wie `KeepWidgetsClearOfSidebar`, nur wenn sie wirklich schneiden, und die
  verschobene Position wird gespeichert (kein Umherspringen bei jedem Öffnen).
- Eingeklappt: Chip mit Logo des erkannten Spiels (`GameDetector.Current`, sonst WKI-Logo) + „›“. Klick auf „›“ →
  aufgeklappt. Aufgeklappt: „‹“ | Spiele | Trenner | Launcher | Trenner | Browser | ⚙. Tooltip = Name.
  Leere Gruppe → kein Trenner.
- Klick auf Verknüpfung → `CloseBoard()` → Start (§4.2). Fehler → Toast mit Grund.

### 4.2 Starten
Reihenfolge der Startwege pro Eintrag (erster passender gewinnt), immer `UseShellExecute`, nie `runas`, vorher
`AllowSetForegroundWindow(ASFW_ANY)`:
1. Steam-Spiel → `steam://rungameid/<appid>`; Epic-Spiel →
   `com.epicgames.launcher://apps/<AppName>?action=launch&silent=true`.
2. Hersteller-Verknüpfung (.lnk/.url) → die Verknüpfung selbst ausführen (Ziel + Argumente + Arbeitsordner wie
   beim Doppelklick).
3. Xbox-App/Store-App → `explorer.exe shell:AppsFolder\<AUMID>`.
4. Exe direkt (Arbeitsordner = Exe-Ordner).
Läuft das Programm schon, entscheidet das Programm selbst (Launcher holen sich meist nach vorne). Der Clipper
greift nicht in fremde Fenster ein.

### 4.3 Spiele-Quellen
- **Zuletzt erkannt:** Bei jedem `GameChanged` mit einem Spiel wird `(ExePath, DisplayName, LastSeenUtc)` in
  `Shortcuts.RecentGames` gemerkt (max. 30, neueste zuerst, ohne Duplikate). Der Bearbeiten-Dialog schlägt sie vor.
- **Startweg-Auflösung (`GameLaunchResolver`)** für eine Exe, erster Treffer gewinnt: (1) liegt sie unter `<Steam-Bibliothek>\steamapps\common\
  <installdir>` → Steam-appid; (2) unter einer Epic-`InstallLocation` → Epic-AppName; (3) sonst eine Startmenü-/Desktop-Verknüpfung, in
  dieser Rangfolge: **a)** eine, die genau auf die Exe zeigt; **b)** eine, deren Ziel im selben Spielordner (oder einem Elternordner bis
  zwei Ebenen darüber, mindestens drei Pfadsegmente tief) liegt und deren Name zur Exe oder zum Zielordner passt (der bessere Name
  vor der kürzeren Entfernung); **c)** eine **Launcher-Verknüpfung außerhalb des Spielordners, die das Spiel per Argument oder URL
  startet** — eine .lnk mit Argumenten (Riot: `RiotClientServices.exe --launch-product=valorant`) oder eine .url mit eigenem Schema
  (`uplay://`, `link2ea://`, `steam://`, `com.epicgames.launcher://`; nicht http/https/file) —, sofern ihr Name zum Exe-Namen oder zu
  einem Ordner über der Exe passt, der das Spiel benennt (mindestens drei Pfadsegmente tief: „C:\Riot Games\VALORANT“ ja, „C:\Riot Games“
  nein). Eine Launcher-Verknüpfung ohne Argument startet den Launcher, nicht das Spiel, und zählt nie. **Nie** Deinstallations-Verknüpfungen
  (Name/Argumente/Ziel/URL enthalten „uninstall“, „deinstall“, „unins“), nie Hilfs-Verknüpfungen (Updater, Reporter, Reparatur, Readme)
  und nie Werkzeuge des Spiels (Server, Dedicated, Editor, Config, Settings, Setup, Benchmark, SDK, Tools, Modkit, Safe Mode), außer die
  Verknüpfung zeigt genau auf die Exe oder das Wort steckt im Namen der Exe selbst; (4) sonst die Exe.
- **Bibliotheken:** Steam (alle appmanifests außer 228980; Soundtracks/Tools bleiben wählbar — der Nutzer entscheidet)
  und Epic-Manifeste als Listen im Dialog.
- **Eigene:** Dateiauswahl (.lnk/.url/.exe).

### 4.4 Browser
- Standard-Browser = Windows-Standard (`AssocQueryString(ASSOCSTR_EXECUTABLE, "http")`); im Dialog änderbar auf
  jeden installierten. Gewählter deinstalliert → zurück auf Windows-Standard + Hinweis.
- Kandidaten Opera, Chrome, Comet, Firefox (+ erkannte weitere). Fehlt der gewählte → Dialog: **Download-Seite
  öffnen** (Chrome `https://www.google.com/chrome/`, Firefox `https://www.firefox.com/` (mozilla.org leitet dorthin um), Opera
  `https://www.opera.com/download`, Comet `https://www.perplexity.ai/comet` — geprüft 2026-10-20: alle vier HTTP 200, Opera nur mit Browser-User-Agent) · **Anderen nehmen** ·
  Abbrechen. Nach Installation: „Neu suchen“.
- Start: Browser-Exe ohne Argumente (Standard-Browser: dessen Exe).

## 5. Einstellungen (Schema v9 → v10)
`Shortcuts { bool Expanded = true; List<ShortcutEntry> Items; string? DefaultBrowserId; List<SeenGame> RecentGames }`
`ShortcutEntry { Id; Group (Game|Launcher|Browser); Kind (CatalogLauncher|Browser|DefaultBrowser|SteamApp|EpicApp|
ShellLink|Exe|StoreApp); Target; Arguments?; DisplayName; Hidden; HotkeyBinding? Hotkey }`. Reihenfolge = Listenreihenfolge.
Katalog-Launcher und erkannte Browser werden beim Scan ergänzt (neu → sichtbar ans Gruppenende); entfernte
Programme bleiben ausgegraut bis „Neu suchen“ sie entfernt. Null-Schutz in `EnsureSections`.

## 6. Technik
- Reine Logik (getestet): VDF-Parser, Steam-/Epic-Bibliothek, `GameLaunchResolver`, Verknüpfungs-Klassifizierung
  (Uninstall/Launcher), Katalog-Abgleich über Startmenü-Ziele, Browser-Erkennung, Eintrags-Abgleich (Scan → Items),
  RecentGames-Pflege, Hotkey-Ids `App:<id>`, Hindernis-Berechnung.
- Native dünn: .lnk/.url auflösen (`IShellLinkW`/INI), Icons, ShellExecute, AssocQueryString, Start-Apps (AUMID).
- Wiring: `WidgetHost.OpenBoard/CloseBoard` zeigt/versteckt die Leiste; `HotkeyService.RegisterAll` + Dispatch-Präfix
  `App:` (Präzedenz `StreamButton:`); `AppHost` verdrahtet `GameChanged` → RecentGames.
- Alle Texte `L.T`. Keine neuen NuGet-Pakete.

## 7. Rollen & Ablauf
Task 0 Fakten (Opus, ich) → Tasks 1–7 Sonnet, eine nach der anderen → ein schlanker Opus-Review → Fixes (Sonnet) →
Live-Test mit Phillip (jeden Launcher/ein Steam-, ein Epic-, ein WoT-Spiel starten, Board-Layout prüfen) → 0.17.0.
