# WKI Clipper — Gamer-Features: Phasenplan (Okt. 2026)

Stand: 17.10.2026, nach v0.15.3. Grundlage: 5 Recherche-Agenten (Code-Fakten, Discord, Sounds,
Controller, FPS/Anti-Cheat inkl. REA-Analyse von AMD Adrenalin und Xbox Game Bar), Gegenprüfung,
Ursachenanalyse Skalierungs-Bug. Jede Phase endet mit Tests, Review, Installation, Live-Test und
eigenem Release — so bleibt jeder Zwischenstand nutzbar und der Wein (Usage) planbar.

## Arbeitsweise (Pharao-Regeln)

| Rolle | Modell | Wofür |
|---|---|---|
| Königin (ich) | Opus | Planen, Specs schneiden, Ergebnisse prüfen, bauen, installieren, Live-Test, Release |
| Architekt / Diagnose / Kritiker | **Opus**-Agent | Ursachenanalyse, riskante Logik (Buffer, ETW, Layout), Plan- und Code-Reviews |
| Umsetzer | **Sonnet**-Agent | Klar spezifizierte Umsetzung mit TDD, UI, Migrationen, Tests |
| Prüfer | **Sonnet**-Agent | Spec-Abgleich, Fakten-Gegenprüfung |

- Pro Phase: (Opus-Spec, falls nötig) → Sonnet-Umsetzung mit Tests → Review (Sonnet Spec-Abgleich,
  Opus nur bei riskanten Phasen) → ich: Build, 100 % Tests, Spiel-Check, Installation, Live-Test →
  Commit, Push, Release.
- Kleine Workflows (≤ 4 Agenten), eine Phase nach der anderen — kein paralleles Großfeuer.

---

## Phase 0 — erledigt (v0.15.3)

- Hotkeys mit `MOD_NOREPEAT`: gehaltene/klemmende Taste = ein Clip, nicht einer alle paar Sekunden.
- Buffer-Ordner darf nicht im Clips-/Screenshot-Ordner liegen (Pfade-Seite verweigert, alte
  Konfigurationen fallen zur Laufzeit auf den Standard zurück), Galerie zeigt nie `seg_*`-Dateien.

---

## Phase 1 — Skalierungs-Bug: Fenster verrutschen nach 100 % → 175 % → 100 %

**Ursache (Opus-Analyse aus Log + Einstellungen, ~85 % sicher):** Positionen werden schon als
JSON gespeichert (`settings.json`, relativ zur Bildschirmfläche) — und das funktioniert: Die
Widgets, die nicht angefasst wurden (Aufnahme, Einstellungen, WhatsApp), kamen **byte-genau**
zurück. Kaputt ist, dass eine **System-Verschiebung als deine Wahl gespeichert** wird:
- Beim Öffnen schiebt das Board Widgets unter der Seitenleiste nach rechts
  (`KeepWidgetsClearOfSidebar`) und **speichert** das. Bei 175 % ist die Leiste 518 statt 296 px
  breit → Audio, Galerie, Leistung, Spotify wurden bei 175 % weggeschoben + oben eingeklemmt und so
  gespeichert → zurück bei 100 % landen sie ~390 px weiter rechts / 160 px tiefer.
- Das 56-px-Muster in den Daten stammt vom Neu-Einrasten im Raster danach (Leisten-Oberkante 16 statt
  der alten Startleiste bei 72) — Folge, nicht Ursache.
- Gleiche Fehlerklasse an weiteren Stellen (noch nicht ausgelöst): Platz-machen-Schieben der
  Nachbarn, Spotify-Größenwechsel speichert die eigene Klemmung, Speichern beim Fokusverlust kann
  WPFs DPI-Zwischenposition erwischen, Neuanordnung mit „Überlappen aus“.

**Fix (Opus-Spec, Sonnet-Umsetzung, Opus-Review):**
- Speichern wird **Opt-in**: Nur was du selbst ziehst/skalierst (oder per Raster/Überlappen-Schalter
  bewusst neu anordnest), wird gespeichert. Systemverschiebungen (Seitenleiste, Klemmen an den Rand,
  Platz machen, DPI-Wechsel) nie.
- Seitenleisten-Ausweichen wird zur reinen Anzeige-Korrektur (bei jedem Öffnen neu, nie gespeichert).
- Kein Speichern mehr beim Fokusverlust; Log-Zeile pro Speichern (Widget, Grund, Position, Skalierung),
  damit ein künftiger Bericht beweisbar ist.
- Tests mit deinem echten Layout: 100 % → 175 % → 100 % muss exakt die Ausgangspositionen ergeben.
- **Eigenes Layout pro Anzeige-Einstellung: nicht nötig** — mit dem Fix kehrt alles exakt zurück;
  ein Layout pro Konfiguration würde den Fehler nur verstecken (und bringt Schlüssel-/Fallback-
  Probleme). Später optional als „Layout-Schnappschüsse“, falls du z. B. am TV andere Anordnungen willst.
- **Dein aktuelles Layout** ist durch den Bug + Neu-Einrasten verändert; auf Wunsch stelle ich den
  Stand vom 16.10. aus der Sicherung wieder her.

---

## Phase 2 — Galerie: Notizen, Komprimieren für Discord, Fundament für den Editor

**Fakten:** Favoriten (Stern) gibt es schon (`GalleryMetaService`, `gallery-meta.json`) — aber nur
nach **Dateiname** verschlüsselt (gleichnamige Dateien in anderen Ordnern teilen sich den Stern),
nicht atomar gespeichert, verwaiste Einträge werden nie aufgeräumt. Notiz/Titel gibt es nicht.
Die Galerie scannt den Clips-Ordner + genau eine Ebene (= Spiel-Ordner), zeigt max. 200 Einträge,
keine Vorschaubilder/Dauer. `ffprobe` wird nicht mitgeliefert, `FFmpegService` liest stdout nie
(ein `-progress pipe:1` würde hängen), es gibt keinen Job-Runner mit Fortschritt/Abbruch.

**Discord-Limits (Stand 17.10.2026, Discords eigenes Help-Center):** kostenlos **20 MB** (seit
08/2026, vorher 10 MB), Nitro Basic **50 MB**, Nitro **„bis zu 1 GB“** (seit Ende 09/2026 — die
**500 MB sind veraltet**), Server-Boost Level 2/3 = 50/100 MB **nur in diesem Server**. Discord
experimentiert pro Account; Einheit (MB vs. MiB) uneinheitlich → wir rechnen mit **20.000.000 Bytes**
und machen die Ziele **wählbar** statt fest verdrahtet.

**Gemessen auf deiner RX 9070 XT (FFmpeg 8.1):** AMF trifft Zielgrößen nur oberhalb eines
„Qualitätsbodens“ (≈ 0,035 Bits pro Pixel) — darunter überschreitet er um 11–79 %. Echtes 2-Pass
gibt es bei Hardware-Encodern nicht. Robust: Bitrate rechnen → Auflösung/FPS per Leiter wählen
(1080p60 → 1080p30 → 720p60 → 720p30 → 540p30 → …) → kodieren → **echte Dateigröße prüfen** →
nachsteuern oder eine Stufe tiefer (max. 5 Versuche, ~1,3 s pro Versuch bei 30-s-Clip) → zur Not
libx264 2-Pass. Modus `cbr + enforce_hrd` landet fast exakt (97–98 % des Limits).

### 2a — FFmpeg-Job-Runner (Fundament, Sonnet, Opus-Review)
- Ein Dienst für Einmal-Jobs: Fortschritt über `-progress` in eine Datei oder stderr-`-stats`
  (kein stdout-Deadlock), Abbruch (q + Kill + Teildatei löschen), **Priorität „niedriger als
  normal“** und Thread-Limit, damit ein Encode neben dem Spiel nicht ruckelt; Warteschlange
  (ein Job zur Zeit). Dauer per `Duration:`-Zeile aus `ffmpeg -i` (kein ffprobe nötig).

### 2b — Notiz/Titel + robustere Favoriten (Sonnet)
- Pro Clip: Stern (gibt es) + **Notiz/Titel** (frei, z. B. „Admin-Fall Spieler X“). Anzeige als
  Titel in der Zeile, Suche durchsucht Dateiname **und** Notiz.
- Meta-Schlüssel auf relativen Pfad umstellen (Migration der vorhandenen Sterne), atomar
  speichern (temp + move), verwaiste Einträge aufräumen.

### 2c — „Für Discord komprimieren“: zwei Buttons (Sonnet für UI, Opus für die Größen-Schleife)
- Pro Clip zwei Aktionen, Standard **„Discord 20 MB“** (kostenlos) und **„Discord Nitro“**
  (Standard 500 MB — passt sicher auch, falls dein Account noch das alte Limit hat; in den
  Einstellungen auf 50 MB / 100 MB / 1 GB / frei änderbar). Zeile ist voll → zweite Zeile/Menü „…“.
  Ausgabe nach `Clips\Komprimiert 20MB\` bzw. `Clips\Komprimiert 500MB\` (Ordnername folgt dem
  gewählten Ziel), Dateiname mit Suffix `_20MB` / `_500MB`; Galerie zeigt diese Ordner als eigenen
  Filter „Komprimiert“ statt als „Spiel“.
- Verfahren wie oben gemessen: H.264 High + AAC (beste Discord-Kompatibilität), Größe nur über die
  fertige Datei prüfen (`-fs` ist kein sicherer Deckel), Fehlversuche löschen, Tonspuren
  Spiel + Mikro zu einer mischen. Ist die Datei schon unter dem Ziel: einfach kopieren.
- **20 MB + lange Clips:** 60 s → ~720p30, 5 min → ~360p30. Ist das Ziel selbst auf der untersten
  Stufe unrealistisch, sofort Meldung „Clip zu lang für 20 MB — schneiden oder größeres Ziel“
  (→ Phase 4) statt Matsch zu erzeugen.
- NVIDIA/Intel-Schwellen sind ungetestet (keine Hardware) → beim ersten Lauf kalibriert die
  Schleife sich selbst; Schwellen nicht hart verdrahten.
- Fortschritt + Abbrechen in der Zeile, Toast am Ende mit „Ordner öffnen“ und **Datei in die
  Zwischenablage** (direkt Strg+V in Discord).

---

## Phase 3 — Sounds: Clip, Screenshot, Aufnahme Start/Ende

**Quellen (alle CC0 = frei, auch zum Mitliefern im MIT-Repo, keine Namensnennung nötig):**
- Kenney „Interface Sounds“ (kenney.nl, ZIP ~815 KB, 100 Sounds): `confirmation_001` = Clip,
  `maximize_003` = Aufnahme Start, `minimize_003` = Aufnahme Ende (Alternativen vorhanden).
- Screenshot (Kamera-Auslöser): OpenGameArt „Camera“ (`photo.ogg`, 20 KB, CC0, ohne Login),
  Alternativen archive.org „Camera Shutter Sound“ (CC0) / Freesound Kodack (CC0, Login nötig).
- Pixabay, Mixkit, CC-BY-Sounds bewusst **nicht** (Weitergabe-/Namensnennungs-Pflichten).

**Umsetzung (Sonnet):**
- Dateien probehören (du entscheidest), auf ≤ 300 ms kürzen, normalisieren, als WAV eingebettet
  (Single-File-Build), Lizenz-Notiz ins Repo.
- Einstellungen: je Sound an/aus, Lautstärke, Ausgabegerät (Standard = Kopfhörer/Standardgerät).
- **Nicht im eigenen Clip:** Im Modus „nur Spiel-Audio“ ohnehin ausgeschlossen. Im Modus „alle
  Sounds“ würde der Clipper sich selbst mitschneiden → Prozess-Loopback mit **Ausschluss der
  eigenen App** (ab Windows-Build 20348; ältere Builds: Hinweis). Start-Ton spielt **vor** dem
  Aufnahmestart, damit er nicht in der ersten Sekunde landet.

---

## Phase 4 — Clip-Editor (Trimmen)

- Eigenes Editor-Fenster aus der Galerie („Schneiden“): Vorschau mit Abspielen/Scrubben,
  Zeitleiste mit **Start-/Endmarke**, Tasten I/O, Feinschritt per Pfeiltasten.
- **Schnell (verlustfrei):** Stream-Copy, springt auf Keyframes (hier alle 4 s) — in Sekunden
  fertig, ideal für Beweis-Clips. **Genau:** Neu-Encodieren auf den Frame genau (Hardware-Encoder,
  Job-Runner aus 2a).
- Speichern als neue Datei `…_cut.mp4` (Original bleibt), Notiz wird übernommen; danach direkt
  „Discord 20 MB/500 MB“ möglich.
- **Komplikation Vorschau:** WPF `MediaElement` spielt H.264; HEVC/AV1-Clips brauchen die
  Windows-Codec-Erweiterungen. Fallback: Vorschaubilder per FFmpeg (Bildleiste) + Abspielen im
  Standardplayer. Opus entwirft UX + Vorschau-Strategie, Sonnet setzt um.

---

## Phase 5 — F9-Clips bis 30 Minuten (z. B. Roleplay-Admin-Fälle)

**Harte Grenzen heute (Code-Fakten):**
- Buffer-Wächter startet bei **> 2 GiB** neu → bei 25–30 Mbit/s schon nach ~10 min Endlosschleife.
- Zusammensetzen hat fest **30 s Timeout**; 30 min ≈ 6,4 GB lesen + schreiben, `+faststart`
  schreibt alles **ein zweites Mal** (~25 GB I/O pro F9).
- Auswahl in der UI endet bei 180 s; Toast/Tray zeigen „1800 s“.
- **Automatik-Modus:** Alt-Tab zu Discord > 15 s = neue „Identität“ bzw. Lücke → F9 liefert nur
  den Teil **nach** der Rückkehr. Für Admin-Beweise fatal.

**Umsetzung (Opus-Spec, Sonnet-Umsetzung, Opus-Review):**
- Auswahl 5 / 10 / 15 / 20 / 30 min, Anzeige in „min“.
- Wächter-Grenze = Dauer × Bitrate × Puffer statt fix 2 GiB; Timeout nach Länge/Fortschritt statt
  30 s; `+faststart` bei langen Clips weglassen (lokal und in Discord egal); Toast „Speichere 30-min-Clip …“ mit Fortschritt.
- **Platzcheck:** 30 min ≈ 6–7 GB Buffer (bei aktueller Qualität), Warnung bei zu wenig Platz;
  Empfehlung: Buffer-Ordner auf D: legen.
- **Lücken-Regel für lange Clips:** Teile desselben Spielfensters vor und nach einem Alt-Tab
  zusammensetzen (mit sichtbarem Sprung), statt abzuschneiden. Alternativ-Empfehlung im UI:
  „Bestimmtes Fenster“ oder „Ganzer Monitor“ für lückenlose Beweise.
- Pflicht vor Release: echter 30-min-Test (A/V-Sync über 360 Segmente, Dauer des Speicherns).

---

## Phase 6 — Clippen per Controller

- **XInput** (liest auch im Hintergrund, kein Admin, keine Treiber, kein Eingriff ins Spiel).
  Windows.Gaming.Input/GameInput liefern ohne Fokus nichts bzw. brauchen Extra-Installationen.
- **Standard-Kombi: View + Menu (Back + Start) gleichzeitig ~0,6 s halten** → Clip (wie F9).
  Beides Menü-Tasten, kein Konflikt mit Game Bar/Steam; Haltezeit + „keine andere Taste gedrückt“
  + 2 s Sperre gegen Fehlauslösung. Share/Guide-Taste: per Standard-API nicht lesbar und von Game
  Bar belegt → nicht als Standard.
- Einstellbar (an/aus, Kombi, Haltezeit) im Hotkey-Tab; Bestätigung per Clip-Sound (kein Rumble —
  würde das Spiel-Rumble stören).
- **Spike zuerst:** mit deinem Controller im laufenden Spiel prüfen, dass XInput im Hintergrund
  liefert (Microsoft-Doku widerspricht der Praxis). PlayStation-Pads (Create + Options) als
  Folge-Schritt über Raw Input.

---

## Phase 7 — Leistungs-Overlay wie AMD: FPS, 1%-Lows & mehr (Vorlage: deine Adrenalin-Screenshots)

**Anti-Cheat-Ergebnis (REA-Analyse):**
- AMD Adrenalin nutzt das mitgelieferte `PresentMon-x64.exe` — nur Windows-ETW (StartTrace/
  ProcessTrace), **keine** Injection-/Hook-Funktionen. Xbox Game Bar hat PresentMon direkt
  eingebaut („XboxGameBar-PresentMon“). Beide = passiver Windows-Mitschnitt.
- RTSS/MSI Afterburner hängen sich per **DLL-Injection + 3D-API-Hooks** in jedes Spiel — Anti-Cheats
  müssen das whitelisten (BattlEye/EAC haben RTSS schon blockiert). Afterburners Treiber hatte eine
  schwere Lücke (CVE-2019-16098). → **Diesen Weg nehmen wir nicht.**
- Unser Weg: eigener ETW-Leser (PresentMon-Logik, MIT): zählt Present-Events nur der
  Vordergrund-Spiel-PID, öffnet **kein** Handle aufs Spiel, läuft nur solange das Overlay FPS zeigt.
- **Recht:** Mitglied in „Leistungsprotokollbenutzer“ — **dein Konto ist es bereits** (läuft bei
  dir ohne Admin). Andere Nutzer: einmalig Button „FPS freischalten“ (UAC, wie Game Bar), danach
  ab-/anmelden.
- Rest-Risiko: keine offizielle Anti-Cheat-Freigabe (gibt es für niemanden), einzelne Spiele
  können Daten verweigern (Battlefield 6 → nur „k. A.“, kein Ban). Testmatrix vor Release:
  Arma Reforger, DayZ, WoT, Wardogs + ein Vulkan-Spiel, Abgleich gegen das AMD-Overlay (±1 FPS).

**Werte (wie Adrenalin):**
- **FPS**, **mittlere Frametime**, **1 %-Low** und **0,1 %-Low** (bzw. „99 % FPS“), **Mikro-Stutter %**
  (Anteil Frames > 2× Median) — neu über ETW.
- GPU: Auslastung, Temperatur, Lüfter (gibt es), **Takt, Spannung, VRAM-Takt** (neu über dieselbe
  Treiber-Schnittstelle wie heute, D3DKMT), VRAM. **Leistung in Watt / Speichertemperatur:**
  nur über Hersteller-SDK (AMD ADLX, NVIDIA NVML) → optionaler Ausbau-Schritt.
- CPU: Auslastung (gibt es). **CPU-Temperatur/-Spannung/-Watt:** weiterhin nicht — bräuchte einen
  Kernel-Treiber (genau das, was Anti-Cheats flaggen). Bewusste Entscheidung, im UI erklärt.
- „Frame-Gen-Lag“ ist AMD-intern (AFMF) — nicht nachbaubar.

**Overlay-Einstellungen (wie Adrenalin „Überlagerung“):** an/aus, **nur bei erkanntem Spiel
zeigen**, Ecke, Größe %, **Grafikgröße %**, **Spalten**, **Hintergrund (Farbe + Transparenz)**,
**Farbe Name / Farbe Wert**, Haken je Wert, im Stream sichtbar ja/nein (gibt es).

Rollen: ETW-Leser + Metrik-Mathe = **Opus** (heikel, Low-Level), Overlay-UI/Einstellungen und
D3DKMT-Werte = **Sonnet**, Anti-Cheat-Testmatrix = du + ich.

---

## Phase 8 — Profile pro Spiel

- Pro Spiel eigene **F9-Cliplänge**, eigenes **Fadenkreuz** (Bild, Größe, Farben, Position relativ
  zur Bildschirmmitte), optional Controller-Kombi an/aus. **Nicht erkannt = globale Einstellungen.**
- Schlüssel = Programmdatei (`ArmaReforgerSteam.exe`), Anzeigename aus den Datei-Infos; „Desktop“/
  „Unbekannt“ = kein Profil.
- UI: oben im Fadenkreuz-/Aufnahme-Widget „Gilt für: Alle Spiele ▾ / Arma Reforger ▾“ mit
  „Eigene Einstellung für dieses Spiel“.
- **Komplikationen:** Fadenkreuz-Position ist heute in absoluten Pixeln → für Profile relativ zur
  Mitte speichern; Fadenkreuz an/aus pro Spiel kann den Aufnahmeweg umschalten (Buffer-Neustart);
  Buffer muss die **längste** Profil-Länge vorhalten (Platz!) oder bei Spielwechsel neu dimensioniert
  werden; es fehlt ein „Spiel gewechselt“-Ereignis für alle Modi (wird gebaut). Opus entwirft den
  „Effektive-Einstellungen“-Resolver, Sonnet baut UI + Migration.

---

## Plan-Kritik (Opus-Review) — eingearbeitet

**Kritisch**
- **K1 Live-Bug heute schon:** Buffer-Wächter startet ab 2 GiB neu, Aufräumen behält aber bis zu 2
  Ringe → bei 180 s + hoher Qualität Neustart-Schleife. → Hotfix in 0.15.4: Grenze aus der echten
  Ring-Formel, Zusammensetzen mit Fortschritts- statt 30-s-Timeout.
- **K2 Reihenfolge:** 30-min-Clips vor Editor und Discord-Komprimieren (Admin-Ablauf: langer Clip →
  schneiden → teilen; Editor muss für 6-GB-Dateien entworfen werden).
- **K3 Plattenplatz:** 30 min = bis ~13 GB (2 Ringe), „Sehr hoch“ nativ bis ~42 GB → echte Formel
  im Platzcheck, alte Generation nach Neustart sofort löschen, bei wenig Platz Lookback kürzen +
  Hinweis statt Schreibfehler-Schleife, alten Buffer-Ordner beim Wechsel leeren, Deinstaller räumt
  den tatsächlich genutzten Buffer-Ordner.
- **K4 Notizen = Beweisdaten:** `gallery-meta.json` mit Version, atomar (temp + Replace) + `.bak`,
  kaputte Datei sichern statt überschreiben; Aufräumen nur bei erreichbarem Ordner und nie
  pauschal; Schlüssel = Basis-Ordner + relativer Pfad; Migration ohne Verlust.

**Wichtig (Auszug, alle übernommen)**
- Ring-Überschreib-Race: Ring = benötigte Segmente + Reserve (~6), sonst überschreibt der Muxer das
  älteste Segment, während F9 es noch liest.
- Job-Runner: Windows Job Object (ffmpeg stirbt mit dem Clipper), Ausgaben als `*.part` und erst
  bei Erfolg umbenennen (auch F9: heute sieht die Galerie halbe Dateien), Platzcheck, F9 hat eine
  eigene Spur und wartet nie hinter einem Encode, Exit-Code erst nach vollständigem Beenden lesen.
- Komprimieren teilt sich den Hardware-Encoder mit dem Live-Buffer → Messung (Drops im Buffer,
  1%-Lows im Spiel) als Abnahme, drosseln, Option „erst wenn kein Spiel läuft“; „unter Ziel →
  kopieren“ nur bei H.264 + einer AAC-Spur; Ausgabe immer `+faststart`; Mindestauflösung 480p.
- Lücken bei Alt-Tab nicht verstecken: als MP4-Kapitel + automatische Notiz („Lücke 21:03–21:04“).
- Integrationstests mit echtem ffmpeg (lavfi-Testbild): Job-Runner, Zielgröße, Trim, 360-Segment-Concat
  ohne A/V-Drift.
- Editor erst nach Spike mit echtem 30-min-HEVC-Clip (Öffnen, Springen, Keyframes ohne ffprobe).
- „Spiel erkannt“ als Fundament (Ereignis „Spiel gewechselt“ in allen Modi, Blockliste Browser/
  Discord) vor FPS-Overlay und Profilen.
- FPS (7a) standardmäßig aus (Opt-in), verwaiste ETW-Session aufräumen (auch im Deinstaller),
  Event-Verluste → „k. A.“; den ETW-Code schreibt Opus. „FPS freischalten“-UAC-Helfer erst, wenn
  auf einem Zweitkonto testbar (dein Konto ist schon berechtigt).
- Sounds als MVP ohne Audio-Ausschluss-Umbau: Ton schon beim F9-Druck, Fehlerton bei Ablehnung,
  Hinweis zu Lautsprechern + Mikro.
- Controller: erst Spike mit deinem Xbox-Pad (angeschlossen ✔); Kombi so wählen, dass das Spiel
  nicht vorher Pause/Karte öffnet — notfalls „Kombi anlernen“.
- Einstellungen atomar speichern (heute: jede Slider-Bewegung schreibt direkt; Abbruch = alles weg).
- Profile schalten nie den Aufnahmeweg um (nur Bild/Größe/Farbe/Position des Fadenkreuzes + F9-Länge);
  Ring hält immer die größte Länge vor.
- Discord-Buttons nach Größe benennen („Discord 20 MB“ / „Discord 500 MB“), nicht „Nitro“.
- Schema-Versionen der Einstellungen vergibt allein die Königin, je Phase.

## Reihenfolge & Releases (nach Kritik)

| # | Inhalt | Version | Hauptrisiko |
|---|---|---|---|
| 1 | Skalierungs-Bug + Buffer-Hotfix (Wächter, Timeout, Ring-Reserve, `.part`) + atomare Einstellungen | 0.15.4 | Kern-Aufnahme anfassen |
| 2 | Sounds-MVP (Clip/Screenshot/Aufnahme, Fehlerton) | 0.15.5 | Selbst-Mitschnitt nur im „alle Sounds“-Modus |
| 3 | Job-Runner + Notizen/Meta-Schutz & Migration | 0.16.0 | Datenmigration |
| 4 | F9 bis 30 min (Platz, Lücken-Kapitel, fester Keyframe-Abstand prüfen) | 0.17.0 | Platte, A/V-Sync über 360 Segmente |
| 5 | Discord 20 MB / 500 MB komprimieren | 0.17.1 | Encoder-Konkurrenz zum Buffer |
| 6 | Clip-Editor (nach Spike) | 0.18.0 | Vorschau großer HEVC-Dateien |
| 7 | Controller (nach Spike) | 0.18.1 | Hintergrund-Input, Kombi-Konflikte |
| 8 | Spiel-Erkennung + FPS/1%-Lows (Opt-in), dann Adrenalin-Optik + Takt/Spannung | 0.19.x | ETW-Korrektheit, Anti-Cheat-Testmatrix |
| 9 | Profile pro Spiel (F9-Länge, Fadenkreuz-Look) | 0.20.0 | Effektive Einstellungen überall |

Am Ende: installieren, pushen, GitHub-Beschreibung + README aktualisieren, Release.

## Offene Entscheidungen für dich

1. **Zweiter Discord-Button:** „Nitro“ mit Standard 500 MB (passt sicher, auch falls dein Account
   noch das alte Limit hat) — oder direkt 1 GB? Oder lieber „50 MB“ (Nitro Basic / Boost-Server)?
2. **Sounds herunterladen:** Darf ich die Kenney-„Interface Sounds“ (ZIP ~815 KB, kenney.nl, CC0)
   und den Kamera-Sound `photo.ogg` (20 KB, opengameart.org, CC0) herunterladen? Danach hörst du
   die Kandidaten an und wählst.
3. **Lange Clips:** 30 min brauchen bei deiner Qualität ~6–7 GB Buffer. Buffer auf **D:** legen
   (empfohlen, C: wird geschont) oder auf C: lassen? Und: Teile vor/nach einem Alt-Tab
   zusammensetzen (mit Sprung) — einverstanden?
4. **Controller:** Welchen Controller nutzt du (Xbox / PlayStation / anderer)? Für den Pflicht-Test
   brauche ich ihn einmal im laufenden Spiel.
5. **Layout zurückholen:** Soll ich dein Widget-Layout vom 16.10. aus der Sicherung wiederherstellen?
6. **Reihenfolge:** passt 1 → 8 so, oder willst du etwas vorziehen (z. B. FPS oder 30-min-Clips)?
