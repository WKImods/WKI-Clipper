# WKI Clipper

Replay clipper, screen recorder and gaming overlay for Windows — one tray app instead of a
pile of tools. Instant replay of up to 30 minutes, a gallery with notes, trimming and
one-click Discord compression, an Xbox-style widget board with a shortcut bar for your games,
launchers and browsers, OBS control, Spotify and WhatsApp, a performance overlay with FPS that
you can place anywhere, your own accent and glass colours, a crosshair with per-game profiles,
a built-in auto clicker with a game blacklist, and clip combos on your controller. No account,
no telemetry, no cloud.

**[Download the latest release](https://github.com/WKImods/WKI-Clipper/releases/latest)**

![The widget board: shortcut bar at the top, sidebar on the left, widgets over the dimmed desktop](docs/screenshots/board.png)

<sub>The widget board with `Ctrl+Alt+G`. The shortcut bar is at the top centre. The empty
space at the top right is the WhatsApp widget — it is invisible to every capture, this
screenshot included; the small dashed box at the bottom left is the performance overlay in
drag mode. The UI starts in German; switch to English under Settings → About.</sub>

## Hotkeys

| Hotkey | Action |
|--------|--------|
| `F9` | Save the last 15 s – 30 min as MP4 (instant replay) |
| `F8` | Save the last seconds as a GIF |
| `F10` | Screenshot of the active monitor (PNG) |
| `Ctrl+F9` | Start/stop manual recording |
| `Ctrl+F10` | Pause/resume the replay buffer |
| `Ctrl+Alt+G` | Open/close the widget board |
| `Ctrl+Alt+C` | Show/hide the crosshair |
| `Ctrl+Alt+Y` | Start/stop the [AutoClicker](#autoclicker) — only while *Clicker ready* is on; set in the AutoClicker tab |
| *(unbound)* | Spotify play/pause, next, previous · performance overlay on/off · any [shortcut bar](#shortcut-bar) entry (bound in its edit dialog) |

All hotkeys are rebindable in Settings → Hotkeys (press-to-bind, with collision checks); the
AutoClicker's own hotkeys are recorded in its tab.

### Controller

Hold a button combination on an Xbox controller to run the same action as the hotkey —
also while the game has focus. Out of the box, **View + Menu** saves a clip like `F9`.

<img src="docs/screenshots/hotkeys.png" height="360" alt="Settings → Hotkeys → Controller: combos per action, hold time and Learn combo">

- **Actions:** save clip (`F9`), screenshot (`F10`) and recording start/stop (`Ctrl+F9`),
  each with its own combo.
- **Settings → Hotkeys → Controller:** the *Enable controller combos* switch, the hold time
  and *Learn combo* per action — hold at least two buttons for one second; a combo that
  another action already uses is refused.
- **Hold time:** 300–1500 ms (default 500 ms) — longer means fewer accidental triggers.
  After it fires, a combo is spent until every one of its buttons is released.
- **Read-only:** the pad is polled through XInput — no rumble, no input injection.
  Xbox/XInput controllers only: PlayStation pads are not supported yet, and Share and
  Guide are invisible to XInput. The game sees the buttons too, so pick a combo that is
  harmless there.

## The widget board

<img src="docs/screenshots/sidebar.png" align="right" width="180" alt="Sidebar, expanded and collapsed to an icon rail">

`Ctrl+Alt+G` opens the board over the game: a dimmed backdrop, a **sidebar** on the left, a
[shortcut bar](#shortcut-bar) at the top centre and the widgets you have open. Every widget
is its own frameless window — drag it, resize it, **pin** it to keep it on screen while you
play, or close it. A transparency slider in each title bar dials a pinned widget down; it
turns fully opaque again under the pointer.

**Sidebar.** Widgets are grouped into *Capture*, *Stream* and *Apps*; open ones are
highlighted, the WhatsApp unread count shows as a pill. The header shows what the capture
is doing right now (buffer active · 60 s / paused / recording with its running time), the
footer holds the layout switches, the [*Colours*](#colours) chip, settings and the clock. It
collapses to an icon rail.

**Grid.** With *Grid* on, widgets snap to a grid and to each other's edges while you drag
or resize them (hold `Shift` to place freely). By default they never overlap — a dropped
widget moves to the nearest free spot, a growing one pushes its neighbours aside.
*Overlap* allows stacking while keeping the snapping.

**Any display scaling.** Places are stored relative to the screen, sizes in scalable units:
switching Windows between 100, 125 and 150 % keeps your layout where you put it.

<br clear="right">

| Section | Widget | What it does |
|---------|--------|--------------|
| Capture | **Capture** | What gets clipped next, target mode, window picker, audio coupling |
| | **Audio** | Devices, levels, gain, sync offset, separate mic track, sound cues |
| | **Gallery** | Clips, recordings, GIFs and screenshots — search, favourites, notes, trim, Discord compress, game filter |
| | **Performance** | CPU, GPU, GPU temperature and fan, RAM, VRAM, FPS — plus the on-screen overlay |
| | **Gaming Utilities** | Tab *Crosshair*: PNG crosshair overlay, optionally with its own look per game. Tab *AutoClicker*: a built-in auto clicker (rate, hotkeys, failsafes, zones, click points, presets, statistics) with a game blacklist and whitelist |
| Stream | **Streaming** | Software stream deck for OBS |
| | **Mixer** | Fader, dB readout and mute per OBS audio input, synced both ways |
| | **Sources** | Scene switcher and per-source visibility for the current scene |
| | **Go Live** | Preflight checklist, stream health, one-click start sequence |
| | **Chat** | Read-only Twitch chat, click-through when pinned |
| Apps | **WhatsApp** | WhatsApp Web, hidden from stream and clips |
| | **Stream music** | Music player for the stream with separate stream/monitor levels |
| | **Spotify** | Your Spotify app: compact player or the full Spotify interface |

![Gallery widget: favourites, notes and the row menu with Trim and Discord compress](docs/screenshots/gallery.png)

## Capture

<p>
  <img src="docs/screenshots/capture.png" height="340" alt="Capture widget: target, audio coupling and the live buffer status (here 2 min in RAM)">
  <img src="docs/screenshots/audio.png" height="340" alt="Audio widget: separate mic track, sync offset and game-only audio">
  <img src="docs/screenshots/sounds.png" height="340" alt="Audio widget: sound cues with volume, output device and own WAV per cue">
</p>

| Mode | Behaviour |
|------|-----------|
| **Automatic** | Follows the window you click (after ~1.5 s dwell). A running `Ctrl+F9` recording stays on the window it started on; an `F9` clip only ever contains one window, never two mixed. |
| **Specific window** | Occlusion-proof window capture (Windows Graphics Capture) — keeps recording the window even when it is covered. The mode for a main game that must never switch. |
| **Whole monitor** | A whole display via Desktop Duplication (`ddagrab`). |

- **Game-only audio:** the WASAPI process-loopback API records just the game (plus your
  microphone) — no Discord, no browser. Optional second audio track for the mic.
- **Per-game folders:** clips, recordings, GIFs and screenshots land in one subfolder per
  game (`Clips\Arma Reforger\…`). The name comes from the program's version info, not the
  window title, so the Arma Reforger Workbench gets its own folder. Read with the most
  limited process right Windows offers — the game's memory is never touched.
- **Sound cues:** short sounds when a clip or GIF save starts, a screenshot is taken, a
  recording starts or stops — and when something goes wrong. The clip sound plays at the key
  press, not when the file is finished. Each cue has its own switch; volume, output device
  and your own WAV per cue are in the Audio widget → *Sound cues*. Over speakers the
  microphone can pick a cue up (and in *Capture all sounds* mode it can land in the next
  clip) — headphones recommended.
- **Hardware encoding:** codecs are detected at startup with a real test encode.

| Codec | GPU |
|-------|-----|
| `h264_amf` / `hevc_amf` / `av1_amf` | AMD (default on AMD) |
| `h264_nvenc` / `hevc_nvenc` / `av1_nvenc` | NVIDIA |
| `h264_qsv` / `hevc_qsv` / `av1_qsv` | Intel |
| `libx264` / `libx265` | CPU fallback |

AMD's own driver-level capture (`vsrc_amf`) is an opt-in in `settings.json`
(`Video.UseAmfCapture`). It only applies to whole-monitor capture at native resolution with
an AMF encoder, is off by default and is switched off automatically whenever something on
screen must stay out of the footage (crosshair, WhatsApp, the hidden performance overlay),
because it ignores Windows' capture exclusion.

### Replay buffer

`F9` saves the last **15 seconds to 30 minutes** — pick the length under Settings → Video →
Replay buffer. The same page shows a live estimate of what that length costs in RAM or on
disk (about 6–7 GB for 30 minutes at the default quality, up to ~25 GB at *Very high*).

<img src="docs/screenshots/replay.png" height="360" alt="Settings → Video → Replay buffer: length with RAM estimate, own length per game, buffer storage">

- **Buffer storage:** *Automatic* keeps the lookback in **RAM** — no files, no SSD wear,
  nothing in Explorer — while it fits the RAM budget (the smaller of 2 GB and 15 % of your
  RAM) and the machine has room for a game beside it; the page shows how long that is on
  your PC. Longer lookbacks go to a **hidden ring of segment files** in the buffer folder.
  *Memory (RAM)* and *Disk (hidden buffer folder)* force one or the other.
- **AV1 always uses the disk** — it cannot be buffered in RAM.
- **Low RAM or disk space:** when free RAM runs low, the RAM lookback is shortened (oldest
  footage first) so the game keeps its memory, and *Automatic* then picks the disk at new
  starts for 7 days — or until you change the storage setting. On disk, a length that does
  not fit is shortened — or the buffer refuses to start — instead of filling the drive.
- **Gaps are marked, never hidden:** time missing inside the lookback — an Alt-Tab to
  another window in *Automatic* mode, a `Ctrl+F9` recording, a buffer restart — is bridged.
  The parts are joined into one MP4 with a chapter per part, and an automatic gallery note
  names every gap. Footage of another window or with other encoding settings is never
  joined. For gap-free evidence use *Whole monitor* or *Specific window*.
- **Long clips:** saving 30 minutes takes 20–90 s depending on the disk, with a progress
  toast; `F8` and `F9` are blocked until it is done. Clips stitched from the disk ring join
  frame-exactly at 60 fps — no dropped frame at a segment boundary, audio in sync.

### Per-game profiles

The clipper knows which game is in front: a program whose window covers its whole monitor
(fullscreen or borderless), or the program you chose as *Specific window* — never browsers,
chat apps, launchers, editors or Windows' own windows. Only the window and the program's
file are looked at, with the most limited process right Windows offers. A game can have its
own:

- **F9 length:** Settings → Video → *Own length per game* — press *Add current game* while
  it is in front, then pick the length. The buffer always holds the longest of all lengths,
  so switching games never restarts it; each `F9` press saves the length of the game the
  clip belongs to (the window being captured, else the game in front), capped by what the
  buffer holds.
- **Crosshair look and position:** see [Crosshair](#crosshair).

No game detected, or a game without a profile: the standard settings apply. The same
detection drives the overlay's *Only show while a game is detected*, the FPS measurement
and Discord's *Only compress while no game is in the foreground*.

## Gallery & editing

The Gallery widget lists clips, recordings, GIFs and screenshots. The less common actions
sit in the `…` menu of each row (also on right-click).

- **Favourites and notes:** star a file (★) and filter with *Favorites*. A note of up to 500
  characters becomes the row's title and is searched along with the file name; F9 clips with
  gaps get an automatic note. Notes live in `%APPDATA%\WKI_Clipper\gallery-meta.v2.json`,
  written atomically with a backup — a locked or damaged file is never overwritten, and a
  banner warns until everything is saved.
- **Trim…** opens the clip editor: preview with scrubbing and frame stepping, start and end
  marks (`I` / `O`) and two modes — *Fast (lossless)* copies the stream, so the start snaps
  back to the previous keyframe; *Exact (re-encode)* cuts to the frame and takes longer
  (H.264 on the GPU; a picture wider than 4096 px, e.g. 32:9 monitors, is encoded on the
  CPU because hardware H.264 cannot open it).
  The result lands next to the original as `<name>_cut.mp4` with its note and star. The
  original stays unless you tick *Move the original to the Recycle Bin afterwards* (the
  Recycle Bin, never a permanent delete: on a drive without one — USB sticks, network
  drives — or when the file would not fit in it, the original is kept). MP4 videos only.
- **Discord 20 MB / Discord 500 MB:** re-encodes the clip to fit an upload limit (H.264,
  on the GPU when possible), saves it into `Komprimiert 20MB` or `Komprimiert 500MB` inside
  the clips folder (filter *Compressed*) and puts it on the clipboard, so `Ctrl+V` in
  Discord attaches it. The copy keeps the note and star. A clip that cannot fit the target
  at an acceptable quality is refused — trim it first or pick a larger target. The two sizes
  (10 MB – 1 GB) and *Only compress while no game is in the foreground* (off by default —
  compressing costs FPS) are under Settings → About → Discord.

<img src="docs/screenshots/trim.png" height="420" alt="Trim… editor: preview, timeline with start and end marks, fast or exact cut">

## Shortcut bar

A strip at the top centre of the board, in the style of the Xbox Game Bar. Click a game, a
launcher or a browser: the board closes and the program starts the way the Start menu would
start it — nothing is embedded, moved or parked, the program just opens.

![Expanded shortcut bar: games, launchers, browsers and the gear](docs/screenshots/shortcut-bar.png)

<img src="docs/screenshots/shortcut-bar-collapsed.png" width="300" alt="Collapsed shortcut bar: a chip with the game in front, or the WKI logo">

- **Collapsed or expanded:** collapsed, it is a chip with the game in front — or the WKI
  logo — and a `›`; expanded, it shows games | launchers | browsers | gear. The state is
  remembered.
- **How things start:**
  - *Steam and Epic games* through their launcher links, so DRM and updates run as usual.
  - *Other games* through the vendor's own shortcut — never an uninstaller or a side tool such
    as a server or an editor. A game hosted by a launcher (Riot, Ubisoft …) starts through that
    launcher's shortcut for the game; without any shortcut, the exe.
  - *Launchers* through their official start path: Steam, EA app, Epic Games, GOG GALAXY,
    Ubisoft Connect, Battle.net, Rockstar Games, Wargaming.net Game Center and Xbox — the
    installed ones only.
  - Nothing is started as administrator. A program that has been uninstalled turns grey and a
    notice says so.
- **Logos** are the programs' own icons; a letter tile stands in when there is none.
- **Detection:** launchers are found from your Start-menu and desktop shortcuts, in a
  background scan whenever the board opens (at most once a minute). The game chip and the
  *Add game…* suggestions come from the clipper's own [game detection](#per-game-profiles).
- **Browsers:** the *Default browser* button starts the Windows default — never Internet
  Explorer — or another installed browser of your choice. Opera, Chrome, Comet and Firefox, and
  any other browser Windows lists, go on the bar with *Add browser…*. A browser that is not
  installed opens a dialog: its official download page, or another browser to use instead.
- **Only starts programs:** no window management, no controller combos for shortcuts — a
  keyboard hotkey per entry works. A widget that sits under the bar is moved below it once.

The gear opens **Edit shortcuts**; every change shows on the bar at once.

<p>
  <img src="docs/screenshots/shortcut-editor.png" height="420" alt="Edit shortcuts: games, launchers and browsers with show tick, optional hotkey, order and remove">
  <img src="docs/screenshots/shortcut-library.png" height="420" alt="Add game… → Steam library: pick several games, those already on the bar are greyed out">
</p>

- **Per entry:** a tick to show or hide it, up/down to reorder, an optional global hotkey
  (unbound by default; it starts the program even while the board is closed) and, for games
  and browsers you added, remove.
- **Add game…:** suggestions from recently detected games, your Steam library or Epic Games
  (several at once), or *Pick a shortcut or exe…* — an uninstall shortcut is refused.
- **Rescan** looks at the PC again and removes launchers and browsers that are gone.

## Colours

The **Colours** chip in the sidebar footer opens a panel with two tabs, *Accent* and *Glass*,
that you combine freely. A click on a swatch applies it at once — no restart — and is saved.

![Colours panel: the Accent and Glass tabs side by side](docs/screenshots/colours.png)

- **Accent:** WKI orange (default), Epic blue, Crimson, Emerald, Royal violet, Gold, Ice
  blue, Magenta.
- **Glass:** Violet (default), Night blue, Graphite, Bordeaux, Forest green, Black (OLED).
- **Custom colour:** hue and saturation sliders or a hex value on either tab; *Default*
  resets the tab.
- **Stays readable:** text on a light accent turns dark, an accent too dark for the glass is
  lightened, and the signal colours — red, green, amber — never change.

<img src="docs/screenshots/theme-blue.png" width="720" alt="The whole board in Epic blue on Night blue glass">

## Performance overlay

Bare numbers — no window, a soft shadow for legibility — over the game, click-through while
you play. Pick the position, the size (75–200 %) and the values — FPS, frame time, 1 % /
0.1 % lows, micro-stutter, CPU, GPU, GPU clock and voltage, GPU temperature, fan, RAM, VRAM,
VRAM clock, time of day — optionally with 60-second history graphs. For an Adrenalin-style
look there are 1–3 columns, a graph size, a black or violet backdrop with adjustable
transparency and colour presets for names and values; *Only show while a game is detected*
keeps the desktop clean. **Hidden from stream and clips by default**; a tick shows it for
benchmark videos.

**Position:** the four corners and the middle of each edge — eight anchors — or *Free*
(Performance widget → On-screen overlay → *Position*). To place it freely, open the board:
the overlay gets a dashed outline and can be dragged, and close to one of the eight anchors it
snaps there. A free spot is stored relative to its monitor's edge, so the overlay stays
anchored when it grows (more values, a larger size) and stays on screen after a monitor or
resolution change — a monitor that is gone falls back to the primary one. With the board
closed it is click-through again. Corner settings from before 0.17.0 keep working.

**FPS, frame time, 1 % / 0.1 % lows and micro-stutter** come from a passive ETW session —
the same approach as AMD Adrenalin's overlay and the Xbox Game Bar: Windows' graphics
runtimes log their `Present` calls and the clipper only reads that log. Nothing is
injected, hooked or opened in the game, and no driver of our own is involved. The values
are opt-in: the measurement runs only while one is ticked and visible, and it follows the
game in front. The lows are time-weighted over the last 20 seconds; micro-stutter is the
share of frames slower than twice the median.

**Permission:** a real-time ETW session needs admin rights or membership in the Windows
group **Performance Log Users** (*Leistungsprotokollbenutzer* on German Windows). Without
it the FPS values read "n/a (permission missing)" and the Performance widget tells you what
to do: Computer Management → Local Users and Groups → Groups → Performance Log Users → add
your account, then sign out and back in. The per-user installer does not change group
membership.

GPU temperature, fan and VRAM size come from the same driver interface Task Manager uses —
user mode, no admin, no driver of our own, nothing an anti-cheat could object to. GPU
clock, voltage and VRAM clock come from the same interface and appear only when the driver
reports a value. CPU temperature is deliberately not shown: Windows only exposes it
through a kernel driver.

<img src="docs/screenshots/performance.png" height="360" alt="Performance widget: FPS (once a game is detected), CPU and GPU load, GPU temperature and fan, GPU and VRAM clock">

## Gaming Utilities

One widget, two tabs: **Crosshair** (see [Crosshair](#crosshair)) and **AutoClicker**. The last tab you
used is remembered.

<img src="docs/screenshots/gaming-utilities-crosshair.png" height="420" alt="Gaming Utilities widget on the Crosshair tab: the Crosshair and AutoClicker tabs at the top, Applies to, the crosshair library and the image adjustments">

**New in 0.18.0:** the Crosshair widget is now **Gaming Utilities** — the crosshair works exactly
as before on its own tab (same hotkey, same per-game profiles), and the new **AutoClicker** tab
is a complete auto clicker built into the clipper: rate, hotkeys, failsafes, zones, click points,
presets, statistics and a game blacklist.

### AutoClicker

A **built-in auto clicker**, part of the clipper itself (MIT like the rest of it) - no extra program
needed.

- **Warning:** auto clickers can break the rules of online games with anti-cheat (BattlEye,
  Easy Anti-Cheat, Vanguard ...) and lead to bans. A banner says so until you press *Understood*;
  nothing is locked by it. Use it at your own risk.
- **Clicker ready:** the switch at the top starts a small helper process (`WKI_Clipper.exe --clicker-host`)
  that watches the hotkey (default `Ctrl+Alt+Y`) and sends the clicks. Only this helper installs the
  keyboard and mouse hooks and sends input; the clipper itself installs no input hooks and never
  injects input. Off = no helper, no hook, no clicks. The status pill shows *Off*, *Ready*,
  *Running · 25 cps · 1,234 clicks*, *Locked: <game> is on the blacklist*, *Stopped: <reason>*, or
  that an administrator window blocks the clicks. *Start*/*Stop* work like the hotkey.

<img src="docs/screenshots/autoclicker-simple.png" height="420" alt="AutoClicker tab, Simple: Clicker ready switch with the status pill, rate per second, mouse button or keyboard key, Toggle or Hold and the hotkey box">

- **Simple:** clicks per second, minute, hour or day (1–500), a mouse button (left, right, middle)
  or a keyboard key (with upper/lower case for letters), Toggle or Hold, and the hotkey.
  **Any input can be the hotkey:** click the box and press it — keys incl. Esc, Backspace, Print
  Screen, F13-F24 and media keys, modifiers alone (Ctrl+Shift, AltGr), up to 5 keys, any mouse
  button incl. the left one, or the mouse wheel (up/down/tilt; one scroll gesture counts as one
  press, held while you keep scrolling), mouse input over the box. The combo is taken when you let
  go. *Cancel*, a click elsewhere or leaving the window cancels; *Default* resets it to `Ctrl+Alt+Y`.
  Risky choices (the left button alone, modifiers only, Esc, the wheel, the output key inside the
  hotkey ...) are allowed and get a warning under the box; mouse hotkeys do not fire over the
  clipper's own windows.

<img src="docs/screenshots/autoclicker-advanced.png" height="420" alt="AutoClicker tab, Advanced: cycle length, extended limit, random variation, hold time, double click, click and time limits, strict modifiers and the master switch hotkey">

- **Advanced:**
  - *Rate or interval:* a cycle length (1 ms up to 999 h) instead of a rate, and an *extended
    limit* that raises the maximum from 500 to 1000 per unit.
  - *Randomisation:* every interval varies at random around the set rate (spread 0–200 %,
    35 % by default); the average rate stays what you set.
  - *Hold time (duty cycle):* how much of each cycle the button or key stays down (0–100 %), or
    *hold down until stopped* instead of clicking.
  - *Double click* (capped at 10 double clicks per second).
  - *Click limit* and *time limit* (seconds, minutes or hours) end a run on their own.
  - *Strict modifiers:* an extra modifier held on top of the hotkey's own stops it from matching.
  - *Master switch:* a second hotkey (Toggle or Hold; recorded like the hotkey, *Clear* removes it)
    that has to allow the clicker before the hotkey can start it; switching it off stops a run.
  - *Running indicator:* the small "AutoClicker · 25 cps" label while it runs — never in your
    clips, stream or screenshots; hidden when the capture method cannot exclude it.

<img src="docs/screenshots/autoclicker-zones.png" height="420" alt="AutoClicker tab, Zones and safety: corner, edge and Alt+Tab stops, the zone list with Draw zones, the game blacklist with Ignore blacklist and the whitelist">

- **Zones & safety:** moving the mouse into a screen corner or to an edge stops it (per monitor,
  size adjustable), so does Alt+Tab / Task View. Draw **stop**, **pause** and **start** zones on
  screen: inside a stop zone the run ends, inside a pause zone it waits, entering a start zone
  starts it and leaving it stops that run again (a cursor already parked in the zone starts
  nothing). The **game blacklist** lives here: while a listed game runs or is in front, the
  clicker is locked (a toast tells you when it stopped a run). *Ignore blacklist* lifts the lock
  until the clipper restarts. The **whitelist** (optional): a run only starts while one of the
  listed programs is in front and stops as soon as another program comes to the front.
- **Click points:** a sequence of screen points (x, y, clicks, jitter radius) picked on screen; the
  clicker moves there before clicking and walks the route in order, optionally stopping when the
  route is done.
- **Presets:** three built-in ones (Standard, Rapid Fire, Precision), your own presets (save, rename,
  delete, export/import as JSON; hotkeys are never part of a preset) and **statistics** (sessions,
  total clicks and time, the last 100 runs with their stop reason, reset).
- **Measuring:** `tools/measure-clicks.ps1` opens a counting window and prints the real rate and the
  timing jitter (mean CPS, interval stddev, min/max) of whatever clicks into it, e.g.
  `powershell -ExecutionPolicy Bypass -File tools\measure-clicks.ps1 -Seconds 20 -TargetCps 100`.
  `tools/measure-moves.ps1` does the same for mouse patterns (*Mausbewegung*): a measuring window
  that draws and sums the raw mouse counts and the pointer path it receives, split into bursts
  (duration, dx/dy, physical vs. injected), so a replay can be compared with its recording, e.g.
  `powershell -ExecutionPolicy Bypass -File tools\measure-moves.ps1 -GapMs 1000`.
## Crosshair

Import your own PNGs into a small library, pick one, place it, toggle it with `Ctrl+Alt+C`.

<img src="docs/screenshots/crosshair.png" align="right" height="420" alt="Crosshair widget: Applies to (per-game profile), library, grid and image adjustments">

- **Never in the footage** — excluded from every capture, while you still see it.
- **Click-through while playing**; draggable only while the board is open.
- **Snap to grid** anchored at the monitor centre, so dead centre is always reachable.
- **1:1 screen pixels** at any display scaling — it neither grows nor blurs at 150 %.
- Size, opacity, brightness, contrast, saturation and per-channel colour gain.
- **Per-game profiles:** the *Applies to* selector at the top of the widget picks what you
  edit — *All games (default)* or one game (the running game is offered, as are existing
  profiles). A profile overrides only what you change — image, size, opacity, brightness,
  contrast, saturation, colour gain and position (stored relative to the centre of the
  game's monitor, so another resolution or monitor still lines up) — and applies while that
  game is in front. No game detected, or no profile: the standard look. *Same as default*
  drops a game's crosshair values (its F9 length stays); on/off, snap and grid stay global.

<br clear="right">

## WhatsApp

The official WhatsApp Web inside a widget, linked once by QR code like any browser.

- **Private by default:** invisible in stream, clips, recordings and screenshots, while you
  still see it; switchable in its toolbar. Sounds are muted by default for the same reason.
- **No pop-ups over the game:** the only signal is the unread count in the sidebar.
- Links from chats open in your normal browser (http/https only); downloads go to your
  downloads folder, and the notification opens the folder, never the file.
- No unofficial WhatsApp APIs — no ban risk.

## Spotify

- **Compact view** for pinning over a game: cover, title, seekable progress, play/skip,
  shuffle, repeat and Spotify's own volume. It controls **your Spotify app** through the
  Windows media session — no developer account, no extra login.
- **Full view:** the Spotify web interface (search, playlists, library) with your own
  account, as a remote for the app. If the web player starts playing by itself, the widget
  says so and tells you how to switch back to your PC.
- Unbound hotkeys for play/pause, next and previous.

## Streaming (OBS)

Streaming, Mixer, Sources and Go Live talk to OBS through its built-in WebSocket server
(v5, OBS 28+: Tools → WebSocket Server Settings, default port 4455). The password is stored
DPAPI-encrypted. OBS may start after the clipper — the connection re-establishes by itself.

- **Streaming:** a button grid — switch scene, start/stop stream or recording, OBS replay
  buffer, mute inputs, toggle sources, virtual camera, studio transition. Per tile: label,
  colour, action and an optional global hotkey. Live badges (LIVE/REC/MUTE/BUFFER).
- **Mixer / Sources:** volume, mute, scenes and source visibility without focusing OBS,
  synced both ways and event-driven.
- **Go Live:** a traffic-light checklist (OBS connected, mic live, replay buffers, scene,
  free disk on both the clips and the OBS recording drive), stream health while live
  (uptime, bitrate, frames dropped *right now*) with a warning even when the widget is
  closed, and a confirmed start sequence: start scene → stream → replay buffer → countdown
  → target scene, aborting honestly if OBS rejects the start.
- **Chat:** anonymous read-only Twitch chat — no OAuth, just the channel name (a `twitch.tv`
  link works too). Colours and badges, raids/subs/gifts as highlighted blocks, messages
  addressing you tinted, a pop-up for raids and gifted subs, and a status dot that turns amber
  when the chat goes silent. A renamed or misspelled channel — Twitch gives no answer within
  10 s — gets a red notice in the widget and a one-time toast.
- **Stream music:** plays a folder into the stream device (e.g. a virtual cable) with an
  optional monitor output at its own level; writes "now playing" for an OBS text source.

## Installation

Download `WKI_Clipper_Setup_X.X.X.exe` from the
[latest release](https://github.com/WKImods/WKI-Clipper/releases/latest) and run it: a
per-user install (no admin) with a self-contained .NET 8 runtime and FFmpeg with all
hardware encoders. The uninstaller keeps your clips and settings.
The install folder can be on any drive: type the full path or use *Browse*. Pick a new or
empty folder — the drive itself (`D:\`) is refused. To move an existing install, run the
setup again and pick the new folder: the old copy is removed once the new one is installed
(cancelling the setup leaves the old install as it was), and your clips, settings and
autostart choice are kept.

- **Windows 10 (2004) or 11, 64-bit.** WhatsApp and Spotify's full view need the Microsoft
  Edge WebView2 Runtime, which ships with current Windows 10/11.
- **SmartScreen:** the installer is not code-signed, so Windows may warn on first launch —
  *More info → Run anyway*.

### Build from source

Prerequisites: .NET 8 SDK, FFmpeg (`winget install Gyan.FFmpeg`), Inno Setup 6.

```powershell
git clone https://github.com/WKImods/WKI-Clipper.git
cd WKI-Clipper
.\build.ps1
```

This produces `installer_output\WKI_Clipper_Setup_X.X.X.exe`, the same installer as in the
releases.

Dev build without the installer:

```powershell
dotnet build WKI_Clipper.sln -c Debug
.\WKI_Clipper\bin\Debug\net8.0-windows10.0.22621.0\WKI_Clipper.exe
```

## Settings

Everything is configurable in the board. The file behind it is
`%APPDATA%\WKI_Clipper\settings.json` (versioned and migrated automatically):

```jsonc
{
  "Capture":      { "Mode": "Auto", "TargetProcessName": null, "CoupleAudio": true },
  "Audio":        { "RecordMicrophone": true, "RecordSystemSound": true },
  "Video":        { "Resolution": "Native", "Framerate": 60, "Codec": "h264_amf", "UseAmfCapture": false },
  "ReplayBuffer": { "Enabled": true, "DurationSeconds": 60, "StorageMode": "Auto" },  // Auto | Memory | Disk
  "Output":       { "SortByGame": true },
  "Sounds":       { "Enabled": true, "Volume": 0.6 },
  "Gamepad":      { "Enabled": true, "HoldMs": 500 },
  "PerfOverlay":  { "Enabled": true, "Anchor": "TopRight" },  // 8 anchors | Free (then "Free" holds the spot)
  "Shortcuts":    { "Expanded": true, "DefaultBrowserId": null },  // plus "Items", the bar's entries
  "Theme":        { "AccentPreset": "wki-orange", "GlassPreset": "violet" },  // "AccentCustom" / "GlassCustom": "#RRGGBB"
  "Discord":      { "SmallTargetBytes": 20000000, "LargeTargetBytes": 500000000, "OnlyWhenNoGame": false },
  "GameProfiles": { "armareforgersteam.exe": { "DisplayName": "Arma Reforger", "ClipSeconds": 300 } },
  "AutoClicker":  { "Blacklist": [ { "ExeName": "cs2.exe", "DisplayName": "Counter-Strike 2" } ],
                    "Clicker": { "Armed": false, "ClickSpeed": 25, "ClickUnit": "Second" } },  // plus zones, click points, presets, stats
  "Behavior":     { "Language": "Deutsch" }
}
```

`ReplayBuffer.MaxMemoryMB` (settings.json only) sets the RAM budget of the in-memory buffer;
0 means automatic — the smaller of 2048 MB and 15 % of your RAM.

| Setting | Where |
|---------|-------|
| Replay length, *Buffer storage*, *Own length per game* | Settings → Video → Replay buffer |
| Controller combos | Settings → Hotkeys → Controller |
| Buffer, clips and screenshots folders | Settings → Paths |
| Discord sizes, *Only compress while no game is in the foreground* | Settings → About → Discord |
| Sound cues: switches, volume, output device, own WAVs | Audio widget → Sound cues |
| FPS values, overlay look and position | Performance widget → On-screen overlay (drag it with the board open for *Free*) |
| Shortcut bar: entries, order, hotkeys, default browser | Gear on the shortcut bar |
| Accent and glass colours | Sidebar footer → *Colours* |
| Crosshair per game | Gaming Utilities widget → *Crosshair* tab → *Applies to* |
| AutoClicker rate, output, hotkey | Gaming Utilities widget → *AutoClicker* tab → *Simple* / *Advanced* |
| AutoClicker failsafes, zones, blacklist / whitelist | Gaming Utilities widget → *AutoClicker* tab → *Zones & safety* |
| AutoClicker presets and statistics | Gaming Utilities widget → *AutoClicker* tab → *Presets* |

**Folder rules.** The buffer folder must be a full path — not a bare drive (`D:`), not a
drive root, not inside the install folder, and not the clips or screenshots folder or
inside them (the gallery would list every buffer segment). The Paths page refuses such
input, and an older saved buffer folder that breaks a rule is replaced by the default for
that run. Clips and screenshots folders inside the install folder (updates and uninstall
work there) are refused too; one saved earlier is not moved, but the page shows a standing
warning.

The UI is fully bilingual (German by default, English under Settings → About) and switches
immediately, without a restart.

## Architecture

```
WKI_Clipper.exe (.NET 8 / WPF, tray)
  Capture
  +-- CaptureTargetResolver    what gets captured, with which audio (single source of truth)
  +-- WgcWindowCapture         occlusion-proof window capture (WGC + D3D11)
  +-- VideoPipeService         BGRA frames -> named pipe -> FFmpeg
  +-- AudioPipeService         WASAPI loopback + mic -> mix -> named pipe
  +-- ProcessLoopbackCapture   game-only audio (WASAPI process loopback)
  +-- ReplayBufferService      FFmpeg segment ring (F9 / GIF), in RAM or on a hidden disk ring
  +-- MemorySegmentStore       the RAM ring: MPEG-TS cut at key frames into pooled blocks
  +-- BufferStoragePlanner     RAM or disk, low-RAM shortening (pure, tested)
  +-- ClipStitcher             joins runs across gaps, chapters + gap notes (pure, tested)
  +-- ManualRecordingService   FFmpeg single-file recording
  +-- ScreenshotService        ddagrab grab with GDI fallback
  +-- GameContext              which game a capture belongs to (per-game folders)
  +-- SoundCueService          feedback sounds (WASAPI shared mode)
  Games and input
  +-- GameDetector             which game is in front (pure rules, 1.5 s debounce)
  +-- EffectiveSettings        a game's profile merged over the standard settings
  +-- FrameTimeMonitor         FPS and lows via a passive ETW session (EtwPresentSession)
  +-- GamepadService           XInput polling, hold-to-fire combos (GamepadComboDetector)
  Gallery
  +-- GalleryMetaService       favourites and notes, atomic store with backup
  +-- FFmpegJobRunner          one FFmpeg job at a time: progress, .part files, cancel
  +-- ClipCutService           trim: stream copy or re-encode
  +-- DiscordCompressService   re-encode to a size target (SizeTargetPlanner)
  Board
  +-- WidgetHost               board, sidebar, widgets, pinning, crosshair and overlays
  +-- WidgetCatalog            sections, labels, icons, status line (pure, tested)
  +-- WidgetLayout             grid, snapping, free-spot search, scale-independent places
  +-- DisplayGeometry          physical-pixel placement at any display scaling
  +-- WebAppHost               WebView2 for WhatsApp/Spotify (one browser, separate profiles)
  +-- SpotifyMediaService      Spotify via the Windows media session + per-app volume
  +-- PerformanceMonitorService  counters + GPU sensors (D3DKMT), only while visible
  +-- PerfOverlayPlacement     eight anchors or a free spot on one monitor (pure, tested)
  +-- ShortcutService          shortcut bar: scan, start paths, hotkeys (resolver and catalogs tested)
  +-- AutoClickerGuard         locks the AutoClicker while a blacklisted game runs or is in front (decision pure, tested)
  +-- ClickerController        runs the AutoClicker helper process (--clicker-host) while "Clicker ready" is on, pipe protocol, stats
  AutoClicker helper (WKI_Clipper.exe --clicker-host, only while "Clicker ready" is on)
  +-- HotkeyHook               the only low-level keyboard/mouse hooks (hotkey and master switch)
  +-- ClickEngine              timing, randomisation, hold time, limits, zones, click-point route
  +-- InputInjector            sends the clicks and key presses (SendInput)
  +-- ThemeService             accent + glass palettes applied live (ThemePaletteBuilder tested)
  Streaming
  +-- ObsWebSocketService      OBS control via obs-websocket v5 (auto-reconnect, events)
  +-- TwitchChatService        anonymous Twitch IRC over WebSocket
  +-- MusicPlayerService       stream music (NAudio, two outputs)
  +-- PreflightChecks / StreamHealth   pure go-live checks and stream math (tested)
```

## Known limits

- **Legacy exclusive fullscreen** cannot be captured per window; the clipper falls back to
  the game's monitor. Borderless windowed works everywhere.
- **Anti-cheat:** nothing is injected into or read from the game process — only Windows'
  own capture APIs and, for FPS, Windows' own present log (a passive ETW session). Should
  be fine with BattlEye/EAC, but there is no guarantee.
- **AutoClicker and anti-cheat:** the clicks are ordinary Windows input, but Windows marks
  them as injected and many online games forbid auto clickers — a ban is possible. Put such
  games on the blacklist. An administrator window in front cannot receive the clicks (the
  status pill says so).
- **FPS needs a Windows permission:** admin rights or membership in the group *Performance
  Log Users*; the Performance widget says how to add it. FPS is measured for the game in
  front only.
- **Game detection** (crosshair profiles, FPS, *Only show while a game is detected*)
  recognises a fullscreen or borderless window, or your *Specific window* target. A game in
  a smaller window is not detected for these.
- **Replay length** can deviate by up to ~5 s because of segment boundaries.
- **AV1 stays on the disk:** it cannot be buffered in RAM, so an AV1 buffer is always the
  hidden disk ring (30 minutes ≈ 6–7 GB at the default quality).
- **A new lookback starts empty:** switching *Buffer storage*, or changing the codec, frame
  rate, resolution or the captured window's size (for example toggling fullscreen), ends
  the earlier lookback.
- **Controllers:** Xbox/XInput only — no PlayStation pads yet, no Share or Guide button.
- **Shortcuts only start programs:** the bar neither moves nor embeds windows, and there are no
  controller combos for it — a keyboard hotkey per entry works.
- **Colours:** the theme does not change the tray menu and tray icon.
- **CPU temperature** is not shown (needs a kernel driver).
- **Flicker with the board open?** Windows' *content adaptive brightness* reacts to the dim
  backdrop and can make the whole screen flicker on OLED/VRR displays — turn it off in
  Settings → System → Display.

## License

[MIT](LICENSE). The bundled sound cues are CC0 (Kenney, OpenGameArt) — see
[`Assets/Sounds/LICENSE.md`](WKI_Clipper/Assets/Sounds/LICENSE.md).
