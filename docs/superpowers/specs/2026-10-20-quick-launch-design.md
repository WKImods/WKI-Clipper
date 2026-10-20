> **VERWORFEN (2026-10-20):** Phillip hat die Fenster-holen/parken-Variante verworfen. Gültig ist docs/superpowers/specs/2026-10-20-shortcut-bar-design.md.

# Schnellstart: Spiele-Clients und Browser aus dem Clipper holen — Design

Stand 2026-10-20 · Projekt WKI Clipper (.NET 8 WPF) · Autor: Claude (Opus) mit Phillip

## 1. Absicht

Phillip will seine Spiele-Clients (Steam, EA app, Epic, GOG Galaxy, Ubisoft Connect, Battle.net,
Rockstar Games Launcher, Wargaming.net Game Center, …) und Browser **ohne Alt+Tab** erreichen:

- **Am Desktop:** Board auf (`Strg+Alt+G`), Kachel „Steam“ klicken → das echte Steam-Fenster ist vorne.
- **Im Spiel:** Ein Kumpel schreibt → Board auf, Steam klicken → Steam liegt über dem Spiel, das Spiel
  läuft weiter (randlos/Vollbild-Optimierung von Windows, wie heute bei WhatsApp).
- **Per Tastenkombi pro Client:** einmal drücken → Client erscheint an *seinem festen Platz*; nochmal
  drücken → Client geht weg (minimiert) und das Spiel hat wieder den Fokus. „Kein Einbahnticket.“
- **Browser** genauso: Opera, Chrome, Comet, Firefox als feste Kacheln; ein Standard-Browser.

Es ist immer das **Original-Programm** (keine Webseiten-Version, kein Einbetten).

## 2. Ziele / Nicht-Ziele / Anti-Ziele

**Ziele**
- Neues Widget **„Schnellstart“** (EN „Quick launch“) im Abschnitt *Apps* der Seitenleiste.
- Kacheln für installierte Spiele-Clients + Browser, mit dem **echten Programm-Logo** (aus der Exe).
- Ein Druck = **holen oder wegschicken** (Toggle) — per Kachel, Tastenkombi oder Controller-Kombi.
- **Fester Platz pro Programm:** das echte Fenster wird beim Holen an seinen gemerkten Platz
  (Position + Größe) geschoben. Wo du es hinschiebst/aufziehst, merkt sich der Clipper beim Wegschicken.
- Browser: 4 feste Kacheln (Opera, Chrome, Comet, Firefox) + **Standard-Browser**; fehlt ein Browser,
  fragt der Clipper: *Installieren* (öffnet die offizielle Download-Seite) oder *anderen wählen*.
- Eigene Programme per Exe-Pfad hinzufügen (z. B. Riot, Xbox, Amazon, itch, Battlestate).

**Nicht-Ziele (diese Version)**
- Fremdfenster **einbetten** (SetParent über Prozessgrenzen) — instabil (Eingaben, DPI, Fokus brechen).
- Web-Versionen (Steam-Webchat o. ä.) — ausdrücklich nicht gewünscht.
- **Spiele-Bibliothek** / einzelne Spiele starten (Steam-AppIDs, Epic-Manifeste …) — spätere Ausbaustufe.
- Clipper lädt/installiert nichts selbst; keine Logins, keine Konten.
- Fremdfenster dauerhaft „immer oben“ machen.

**Anti-Ziele (wäre ein Fehlschlag, auch wenn es „funktioniert“)**
- Ein Launcher wird bei jedem Druck **neu gestartet** / zweites Fenster / Update-Schleife.
- Fokus bleibt nach dem Wegschicken hängen (WASD landet im Chat, Spiel reagiert nicht).
- Ein Fenster verschwindet **außerhalb des Bildschirms** (Monitor abgesteckt, Auflösung geändert).
- Irgendetwas, was ein Anti-Cheat stören könnte: keine Eingabe-Injektion, keine Hooks, kein Zugriff
  auf Spielprozesse (außer dem bereits bestehenden SetForegroundWindow zurück aufs Spielfenster).
- Ruckler im Spiel durch Polling (kein Dauer-Polling, nur ereignisgesteuert bzw. kurz nach Druck).

## 3. Belegte Fakten (Spike 2026-10-20 auf Phillips PC)

- **Installiert (Uninstall-Registry):** Steam, EA app, Epic Games Launcher, GOG GALAXY, Ubisoft Connect,
  Battle.net, Rockstar Games Launcher, Wargaming.net Game Center. Exe-Pfade alle vorhanden:
  - Steam `HKCU\Software\Valve\Steam\SteamExe` → `C:\Program Files (x86)\Steam\steam.exe`
  - EA `HKLM\SOFTWARE\Electronic Arts\EA Desktop\DesktopAppPath` → `…\EA Desktop\EADesktop.exe`
  - Epic `…\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe` (InstallLocation + relativ)
  - GOG `…\GOG Galaxy\GalaxyClient.exe`; Ubisoft `…\Ubisoft Game Launcher\UbisoftConnect.exe`
    (DisplayIcon); Battle.net `…\Battle.net\Battle.net Launcher.exe` (+ `Battle.net.exe`);
    Rockstar `C:\Program Files\Rockstar Games\Launcher\Launcher.exe`;
    Wargaming `C:\ProgramData\Wargaming.net\GameCenter\wgc.exe` (DisplayIcon).
- **Logos:** `IShellItemImageFactory.GetImage(256, ICONONLY|BIGGERSIZEOK)` liefert für alle 8 ein
  256×256-PNG mit Alpha (`FromHbitmap` verliert Alpha → Bits als 32bpp ARGB übernehmen).
- **Steam:** läuft **nicht erhöht**. Hauptfenster gehört `steamwebhelper.exe`, Klasse `SDL_app`, Titel
  `Steam`; daneben Popups (`Sonderangebote`, `Menu`, …) mit derselben Klasse. „Schließen“ = Fenster
  bleibt **versteckt** bestehen (Tray). `steam://open/main` zeigt es in ~125 ms, Steam aktiviert es selbst.
- **Holen/Wegschicken aus einem normalen (nicht erhöhten) Prozess funktioniert:**
  `SetWindowPos` (1200×800, Steam passt sein Layout an), `ShowWindow(SW_RESTORE/SW_MINIMIZE)`,
  `SetForegroundWindow` → **true und wirksam**, sowohl aus einem `WM_HOTKEY` als auch aus einem Klick
  in ein `WS_EX_NOACTIVATE`-Fenster (wie Clipper-Widgets). `SetWindowPlacement` stellt den alten
  Platz wieder her.
- **Fehlerfall gefunden:** „größtes sichtbares Fenster des Prozesses“ wählte bei verstecktem
  Hauptfenster das Popup *Sonderangebote* → **pro Launcher feste Fensterregel nötig**.
- **Browser (StartMenuInternet):** Chrome, Brave (HKLM), Comet (HKCU, Pfad in `%LOCALAPPDATA%`).
  Windows-Standard (http UserChoice) = **Comet**. `IEXPLORE.EXE` ist registriert, aber **IE ist seit
  2023 abgeschaltet** (startet Edge). **Edge ist bei Phillip nicht installiert** (nur Appx-Stub) —
  Edge darf also nie als „immer vorhanden“ gelten.
- Offen (Phase 0): die 7 anderen Launcher live — Fensterregel, Single-Instance-Verhalten, Erhöhung.

## 4. Verhalten im Detail

### 4.1 Kachel-Klick, Tastenkombi, Controller-Kombi — ein Toggle

`Press(entry)`:

1. **Hauptfenster suchen** (Regel des Eintrags, §5.2).
2. **Ist es vorne** (= `GetForegroundWindow`, nicht minimiert)? → **Wegschicken**:
   - aktuellen Fensterplatz als *festen Platz* merken (wenn Andocken an ist),
   - `SW_MINIMIZE`,
   - Fokus zurück: an das Fenster, das vor dem Holen vorne war (falls noch da); sonst an das erkannte
     Spiel (`GameDetector.Current`); sonst nichts.
3. Sonst → **Holen**:
   - Rückkehr-Ziel merken = aktuelles Vordergrundfenster (nicht der Clipper selbst; ist das Board offen,
     das Fenster, das vor dem Board vorne war — `WidgetHost._returnFocusTo`).
   - **Board offen → zuerst Board schließen** (mit Phillip so vereinbart), ohne dass dessen eigene
     Fokus-Rückgabe das gerade geholte Fenster wieder überdeckt (Reihenfolge: Board zu, dann holen).
   - Kein Hauptfenster da → **Öffnen-Befehl** des Eintrags (§5.3) und bis zu 20 s asynchron warten
     (100-ms-Takt, nur in dieser Zeit). Toast „Steam startet …“ nach 1,5 s ohne Fenster.
     Zeitüberschreitung → Toast „Steam hat kein Fenster gezeigt“ — kein zweiter Start.
   - Minimiert → `SW_RESTORE`; versteckt (Tray) → nie selbst aufdecken, immer Öffnen-Befehl.
   - Andocken an und Platz gemerkt → `SetWindowPos` auf den Platz (vorher auf einen sichtbaren Monitor
     klemmen, §4.3). Noch kein Platz → Fenster bleibt, wo es ist (der erste Wegschick-Druck merkt ihn).
   - `SetForegroundWindow`; Ergebnis prüfen. Abgelehnt → `FlashWindowEx` + Toast „… ist offen, ein
     Klick in die Taskleiste holt es“. **Keine** simulierten Tasten (kein Alt-Trick).
4. **Tastenkombi/Controller gedrückt, während das Board offen ist** → wie Kachel-Klick.

Doppeldrücke: ein laufendes `Press` pro Eintrag; weitere Drücke während des Wartens werden ignoriert.

**Vordergrund-Recht (Builder-Check, meine Entscheidungen):**
- Windows erlaubt `SetForegroundWindow` nur, wenn der Clipper die letzte Eingabe bekam. Das gilt für
  Kachel-Klick und `WM_HOTKEY` (Spike belegt, mit Spiel vorne in Phase 0 nachzuweisen) — **nicht** für
  Controller-Kombis (XInput-Polling ist keine Eingabe an den Clipper). **Controller-Kombis für
  Schnellstart-Einträge entfallen in dieser Version**; Tasten-Simulation (Alt-Trick) bleibt verboten.
- Vor jedem Öffnen-Befehl `AllowSetForegroundWindow(ASFW_ANY)`, damit der Launcher sich selbst
  aktivieren darf, solange der Clipper das Recht hat.
- **Spätes Fenster:** erscheint das Fenster erst > 3 s nach dem Druck und ist inzwischen wieder ein
  Spiel vorne (Nutzer spielt weiter), wird **kein** Fokus gestohlen: Fenster an den Platz, nicht nach
  vorne, Toast „Steam ist bereit — Taste nochmal drücken“.
- **„Immer-oben“-Spiele:** ist das Vordergrundfenster beim Holen `WS_EX_TOPMOST`, wird das geholte
  Fenster **vorübergehend** topmost (sonst bekäme es den Fokus, bliebe aber unsichtbar hinter dem
  Spiel). Zurückgesetzt beim Wegschicken, sobald es den Vordergrund verliert (bestehender
  WinEvent-Hook im `ForegroundTracker`) und beim Beenden des Clippers.

### 4.2 Sonderfälle
- **Exklusiv-Vollbild:** `SHQueryUserNotificationState == QUNS_RUNNING_D3D_FULL_SCREEN` → einmaliger
  Toast „Das Spiel läuft im Exklusiv-Vollbild — Windows minimiert es beim Wechsel“. Holen trotzdem.
- **Erhöhter Launcher** (höhere Integritätsstufe als der Clipper): Windows blockt Verschieben,
  Minimieren und Vordergrund (UIPI). Erkennung: `OpenProcessToken` → Integrität vergleichen;
  **ACCESS_DENIED bei gültigem `PROCESS_QUERY_LIMITED_INFORMATION`-Handle zählt als erhöht**;
  zusätzlich ein fehlgeschlagenes `SetWindowPos` mit ERROR_ACCESS_DENIED. Dann: Holen = nur
  Öffnen-Befehl; zweiter Druck = nur Fokus zurück ans Spiel (ohne Minimieren). Kachel zeigt
  „läuft als Administrator — nur Starten möglich“. Nie still scheitern.
- **Programm nicht installiert / Pfad weg** → Kachel ausgegraut; Klick: Browser → Installieren-Dialog
  (§6.2), Client/eigenes Programm → „nicht gefunden — Pfad ändern / ausblenden“.
- **Mehrere Fenster** (Browser) → das zuletzt benutzte (oberstes in der Z-Reihenfolge) gilt als Hauptfenster.

### 4.3 Fester Platz
- Gespeichert **relativ zum Monitor** (Gerätename + Anteile von Arbeitsbereich), wie Widget-Plätze —
  übersteht DPI-/Auflösungswechsel. Monitor weg → auf den Hauptmonitor klemmen; mind. 50 % sichtbar.
- Pro Eintrag „Andocken“ an/aus (Standard **an**) und „Platz zurücksetzen“.
- Mindestgrößen der Programme werden respektiert (SetWindowPos klemmt selbst; gemerkt wird das Ergebnis).
- Maximierte Fenster: beim Merken `IsZoomed` → als „maximiert auf Monitor X“ merken, beim Holen so wiederherstellen.

## 5. Programme

### 5.1 Katalog (eingebaut)
Spiele-Clients: Steam, EA app, Epic Games, GOG GALAXY, Ubisoft Connect, Battle.net, Rockstar Games,
Wargaming.net Game Center. Browser: Opera, Chrome, Comet, Firefox (+ automatisch erkannte weitere
installierte Browser wie Brave/Edge/**Opera GX** als zusätzliche Kacheln — meine Entscheidung; die
Kachel „Opera“ meint das klassische Opera, GX ist ein eigener Eintrag mit eigenem Registry-Schlüssel).
**Nie** IE.

Jeder Katalog-Eintrag ist reine Daten (`KnownApp`): Id, Name, Kategorie, Erkennungsquellen, Öffnen-Befehl,
Fensterregel, Download-URL (Browser). Daten werden in **Phase 0** gegen die echten Programme geprüft.

**Steam-Chat als eigene Kachel (Phillips Entscheidung 2026-10-20):** Die Kachel „Steam“ holt **nur das
Steam-Hauptfenster**. Zusätzlich gibt es die Kachel **„Steam-Chat“** mit eigener Tastenkombi und eigenem
festen Platz: Öffnen-Befehl `steam://open/friends`, Fenstergruppe = Freundesliste + offene Chatfenster
(Steam-Fenster außer dem Hauptfenster und bekannten Popups; genaue Titel/Klassen in Phase 0, Titel sind
lokalisiert bzw. tragen Freundesnamen). „Vorne“ = irgendein Fenster der Gruppe ist vorne; Wegschicken
minimiert alle Fenster der Gruppe; Holen dockt die Freundesliste an ihren Platz und stellt offene
Chatfenster an ihrer letzten Stelle wieder her. Tipp im Kachel-Menü: in Steam „Chats als Tabs“
einstellen, dann gibt es nur ein Chatfenster. Für Battle.net/EA gibt es (noch) keine Chat-Kachel.

### 5.2 Fensterregel (pro Eintrag)
`Prozessnamen` (Fensterbesitzer, z. B. Steam = `steamwebhelper`, nicht `steam`), optional `Klasse`,
optional `Titel exakt/Präfix`, Ausschlüsse (Popups). Kandidaten: Top-Level, nicht owned, kein Toolwindow,
sichtbar oder minimiert, Titel nicht leer. Versteckte Fenster zählen **nicht** (→ Öffnen-Befehl).
Eigene Programme: Prozess der Exe + ihre Kindprozesse (Prozessbaum ab Start), oberstes Fenster.

### 5.3 Öffnen-Befehl
Steam: `steam://open/main`. Sonst: Exe erneut starten (Single-Instance-Launcher zeigen dabei ihr
laufendes Fenster) — **pro Launcher in Phase 0 bestätigen**; wo ein Neustart ein zweites Fenster oder
einen Update-Dialog erzeugt, wird stattdessen ein dokumentiertes Protokoll genutzt oder der Eintrag
auf „nur Starten wenn nicht laufend“ gesetzt. Browser: Exe ohne Argumente (neues Fenster).
Gestartet wird über die Shell (`UseShellExecute`), **nie** mit erhöhten Rechten.

### 5.4 Erkennung
Registry (Uninstall-Keys HKLM/WOW6432Node/HKCU, herstellerspezifische Keys, StartMenuInternet), danach
Standardpfade; Ergebnis: Exe-Pfad muss existieren. Erkennung beim Widget-Öffnen (gecacht, im Hintergrund)
+ Knopf „Neu suchen“. Kein Polling.

### 5.5 Logos
Aus der Exe per `IShellItemImageFactory` (256 px, Alpha korrekt), Cache
`%LOCALAPPDATA%\WKI_Clipper\AppIcons\<id>.png`, gültig solange Exe-Pfad + Änderungsdatum gleich.
Fehlschlag → Segoe-Glyphe. Keine Logos im Repo (Markenrechte); sie entstehen lokal.

## 6. Browser

### 6.1 Standard-Browser
Standard = **Windows-Standardbrowser** (http-Zuordnung, `AssocQueryString(ASSOCSTR_EXECUTABLE, "http")`).
Änderbar auf jeden installierten Browser („Standard für den Clipper“). Eigene Kachel + eigene
Tastenkombi „Browser“. Ist der gewählte deinstalliert → zurück auf Windows-Standard + Hinweis.

### 6.2 „Nicht installiert“
Klick auf Opera/Chrome/Comet/Firefox, wenn nicht installiert → Dialog im Clipper-Stil:
„Chrome ist nicht installiert. **Download-Seite öffnen** · **Anderen Browser nehmen** (Liste der
installierten) · Abbrechen“. Download-Seite = offizielle URL (Phase 0 prüft jede URL):
Chrome `https://www.google.com/chrome/`, Firefox `https://www.mozilla.org/firefox/new/`,
Opera `https://www.opera.com/download`, Comet `https://www.perplexity.ai/comet`.
Geöffnet im aktuellen Standard-Browser. Nach Rückkehr: „Neu suchen“ erkennt den neuen Browser.

## 7. Oberfläche

- **Widget „Schnellstart“** (Abschnitt *Apps*): zwei Gruppen *Spiele-Clients* und *Browser*,
  Kachel = Logo 48 px + Name + kleiner Punkt (läuft / läuft nicht / nicht installiert / Admin).
  Klick = Press. `⋯`/Rechtsklick: *Tastenkombi festlegen*, *Andocken an/aus*,
  *Platz zurücksetzen*, *Ausblenden*, (eigene) *Pfad ändern/entfernen*.
  Fuß: *Eigenes Programm hinzufügen …*, *Ausgeblendete zeigen*, *Neu suchen*, Standard-Browser-Auswahl.
- **Tastenkombis** pro Eintrag, Standard **unbelegt**; Aktionsname `App:<id>` (Präzedenz
  `StreamButton:{slot}`: eigener Speicher pro Eintrag, Registrierung in `RegisterAll`, Kollisionsprüfung
  mit allen anderen). Zusätzlich im Hotkey-Tab eine Zeile „Standard-Browser“.
- **Controller:** entfällt für Schnellstart-Einträge (siehe §4.1, Vordergrund-Recht).
- Alle Texte über `L.T("…","…")`.

## 8. Technik & Einbindung (Fakten aus der Recherche)

- Widget-Registrierung: `WidgetId` (AppSettings.cs ~563), `WidgetCatalog` (Sections/Order/Label/Glyph),
  `WidgetHost.CreateContent/Order/Label`, `DefaultLayout()` (Test erwartet 14 → 15),
  `WidgetCatalogTests`, `SettingsMigrationTests`.
- Settings: neuer Abschnitt `Launcher { List<LauncherEntry> Entries; string? DefaultBrowserId; }`,
  `LauncherEntry { Id; Kind (Known|Custom); ExePath?; Hidden; Dock=true; DockPlacement?; Hotkey (Key 0) }`.
  Schema **v9 → v10**, Null-Schutz in `EnsureSections` (nicht nur in der Migration).
- Hotkeys: `HotkeyActions` sind String-Konstanten; Dispatch-Switch in `App.xaml.cs` (~230) bekommt
  den Präfix `App:` (wie `StreamButton:`); Board-API `WidgetHost.CloseBoard()`.
- Native neu: `ShowWindow`, `SetWindowPos`, `GetWindowPlacement/SetWindowPlacement`, `FlashWindowEx`,
  `GetWindow`, `GetClassName`, `SHQueryUserNotificationState`, Token-Integrität, `IShellItemImageFactory`,
  `AssocQueryString`.
- Aufteilung: **reine Logik** (Katalog-Daten, Erkennung hinter Registry-/Dateisystem-Schnittstelle,
  Fensterwahl aus Fensterliste, Toggle-Entscheidung, Platz-Mathematik, Hotkey-Ids, Migration) —
  vollständig unit-getestet; **Native-Schicht** dünn; **Widget/UI** zuletzt.

## 9. Phasen & Rollen (Pharao-Regel, Usage sparsam: eine Phase nach der anderen)

| Phase | Inhalt | Wer |
|---|---|---|
| 0 | Live-Prüfung aller 8 Launcher + 4 Browser-URLs mit Phillip: Fensterregel (inkl. Chat-/Freundesfenster), Öffnen-Befehl, Single-Instance, Erhöhung, Tray-Verhalten — **jeweils auch mit einem Spiel im Vordergrund** (Arma Reforger: topmost? exklusiv?) → Tabelle im Plan | Opus (ich) + Phillip (Logins) |
| 1 | Reine Logik + Settings v10, TDD | Sonnet |
| 2 | Native-Schicht (Fenster, Integrität, Logos, Assoc) + gegatete Live-Tests | Opus |
| 3 | Widget, Kacheln, Dialoge, Hotkeys `App:`, Controller, Board-Integration | Sonnet |
| 4 | Ein schlanker Opus-Review (sequenziell), Fixes, Live-Test mit Spiel im Vordergrund, Release | Opus Review, Sonnet Fixes |

Live-Test-Checkliste (Phase 4): jeder Launcher kalt/Tray/minimiert/vorne; Spiel randlos vorne → holen →
wegschicken → Fokus im Spiel (WASD geht ins Spiel); Monitor-/DPI-Wechsel; Browser fehlt → Dialog.

## 10. Dem Builder überlassen
Interne Klassennamen, Layout-Feinheiten der Kacheln, Toast-Wortlaut im Rahmen der Beispiele, Cache-Format.
