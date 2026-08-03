# WKI Clipper

Lightweight replay clipper and screen recorder for Windows. A handful of hotkeys, no bloat.

## Features

| Hotkey | Action |
|--------|--------|
| `F9` | Save the last 15–180 s as MP4 (instant replay) |
| `F10` | Screenshot of the whole active monitor (PNG) |
| `Ctrl+F9` | Start/stop manual recording |
| `Ctrl+F10` | Pause/resume the replay buffer |
| `Ctrl+Alt+G` | Open/close the widget overlay |
| `Ctrl+Alt+C` | Show/hide the crosshair overlay |

All hotkeys are rebindable in the Hotkeys settings (press-to-bind).

## Widget overlay

The overlay is a modular, Xbox-Game-Bar-style board rather than a single window. Each
widget is its own frameless window: drag it anywhere, **pin** it to keep it on screen
while you play, or close it. Layout, size, pin and visibility are remembered per monitor.

| Widget | What it does |
|--------|--------------|
| **Capture** | Live "what gets clipped next" plus target mode, window picker and audio coupling |
| **Audio** | Devices, levels, gain and sync offset |
| **Gallery** | Clips, recordings and screenshots with search and favorites |
| **Performance** | Live CPU / GPU / RAM / VRAM usage (polled only while visible) |
| **Crosshair** | PNG crosshair overlay — see below |
| **Streaming** | Software stream deck for OBS — see below |
| **Mixer** | Fader, live dB readout and mute per OBS audio input, synced both ways |
| **Go Live** | Traffic-light preflight checklist plus a one-click stream start sequence |
| **Chat** | Read-only Twitch chat, click-through when pinned — see below |
| **Music** | Stream music player with separate stream and monitor levels — see below |
| **Settings** | Video, hotkeys, paths and about |

Each widget's title bar carries a **transparency slider** next to the pin and close
buttons — dial a pinned widget down so it does not cover the game, and it fades back to
fully opaque while the pointer is over it. The level is remembered per widget.

## Crosshair overlay

Import your own PNG crosshairs into a small library (they are copied into the app's data
folder, so the originals can move or disappear). Pick one, place it, and toggle it with
`Ctrl+Alt+C`.

- **Click-through while playing** — the crosshair never intercepts a shot; it only becomes
  draggable while the overlay board is open.
- **Snap to grid**, anchored at the *monitor center*, so dead center is always reachable
  and offsets stay symmetric. Grid step is adjustable, or place freely.
- **Image controls**: size, opacity, brightness, contrast, saturation and per-channel
  red/green/blue gain. Import white crosshairs for maximum tinting freedom — the gains are
  multiplicative, so a channel that is zero in the source cannot be recovered.

## Streaming widget (software stream deck)

A configurable button grid that drives OBS over its built-in WebSocket server (v5,
OBS 28+) — no Elgato hardware or software, everything is built into the clipper.

- **Actions**: switch scene, start/stop/toggle stream, toggle/pause OBS recording,
  save/toggle the OBS replay buffer, mute inputs, toggle scene items, virtual camera,
  studio-mode transition. OBS's replay buffer is deliberately labeled "OBS: …" —
  it is a separate system from the clipper's own F9 replay.
- **Per-tile config**: label, color, action with parameters (scenes/inputs are loaded
  live from OBS; names can be typed while OBS is offline), and an optional **global
  hotkey** (press-to-bind, with collision checks against every other binding).
- **Live state on the tiles**: active scene highlighted, LIVE/REC/MUTE/BUFFER badges
  from OBS events; the grid greys out while disconnected.
- **Auto-reconnect**: OBS can start after the clipper (or restart mid-session) — the
  connection re-establishes by itself.
- Setup: OBS → Tools → WebSocket Server Settings (default port 4455). The password is
  stored DPAPI-encrypted, never as plaintext.

## Mixer widget

A mini audio mixer for OBS, so levels can be changed without focusing OBS. One row per
audio input with a fader, a live dB readout and a mute button. The inputs are read from
OBS at runtime — nothing is hardcoded. Sync goes both ways: a fader moved in OBS moves
here too. Fader drags are coalesced into one request instead of one per pixel, and
incoming updates never yank the knob while you are dragging it.

## Go Live widget (preflight)

A traffic-light checklist for the moments before a stream: OBS connected · microphone
not muted · OBS replay buffer · current scene · clipper replay buffer · free disk space.
Red blocks going live, amber only warns.

Free space is checked on **both** volumes that can lose footage — the clipper's clips
folder and OBS's own recording folder, which usually live on different drives. A second
row appears only when they really are different.

While live, the widget also shows **stream health**: uptime, current bitrate and the
share of frames dropped *right now*. The recent share matters more than the cumulative
one OBS displays, because a connection that starts breaking up two hours in barely moves
the total. Sustained drops raise a popup even when the widget is closed.

The **Go live** button then runs the whole start sequence: start scene → start stream →
replay buffer on → visible countdown → target scene. Scenes, countdown length and the
microphone input name are configurable, with scene names pulled live from OBS.

Because going live is public and hard to take back, the button always asks for
confirmation first, and the countdown can be cancelled at any point — cancelling never
stops a stream that is already running. **End stream** is the deliberate counterpart: it
appears only while live and confirms before it ends the broadcast.

## Chat widget

Reads a public Twitch chat so it can be followed mid-match without alt-tabbing. The
connection is **anonymous** (the classic `justinfan` IRC login over WebSocket) — no
OAuth, no API key, no account, and nothing to configure beyond the channel name.

- Display names in their Twitch colors (dark colors are lifted so they stay readable on
  the dark overlay) with broadcaster/mod/VIP/sub badges
- **Raids, subs, gifted subs and announcements** appear as highlighted blocks instead of
  being dropped, and cheered bits are marked on the message that carried them
- An optional popup for raids and gift bombs — the two events worth interrupting a match
  for — which also fires while the chat window is closed
- Auto-scrolls to the newest line, and stops doing so while you scroll up to read
- **Click-through while pinned**: over a game the window passes every click to whatever
  is underneath, so it can never swallow a shot. Fully interactive again as soon as the
  widget board is open.
- Reconnects on its own with a backoff (Twitch drops idle connections)
- **The status dot reports what arrives, not just that a socket exists.** A WebSocket can
  sit open and silent for hours; the connection is pinged every minute and the dot turns
  amber once nothing has been received for a while, so a frozen chat is visible instead
  of looking healthy.

## Music widget

Plays a folder of tracks straight into the stream, so no second program is needed for
music. Built on NAudio, which the clipper already uses.

- Output goes to the **stream device** (typically a virtual audio cable that OBS picks
  up), with an optional **monitor output** on a second device so you hear the music too.
  Both sides have their **own independent volume**.
- Both outputs are fed from one decoder (the monitor is tapped off the main pull), so
  the two can never drift apart.
- Shuffle, repeat, auto-advance, click-to-play track list, folder picker
- **Now playing** is written to a text file for an OBS text source; the file is emptied
  when playback stops. Artist/title come from the file name (`Artist - Title.mp3`),
  which is exactly how NCS downloads are named — no tag library needed.

## Capture modes

| Mode | Behavior |
|------|----------|
| **Automatic** | Tracks the app in the foreground. `F9` and `Ctrl+F9` pin the window that is active when triggered — switching to Discord afterwards does not change what gets captured. |
| **Specific window** | Occlusion-proof window capture via Windows Graphics Capture (WGC). The clip stays on the chosen window even when it is covered by other windows. |
| **Full monitor** | Captures an entire display (Desktop Duplication) — for tutorials and full-screen walkthroughs. |

Audio can be coupled to the video target: with "game-only" audio enabled, the clip contains only the captured app plus your microphone — no Discord, no browser.

## Why?

- **Xbox Game Bar records the microphone even when it is disabled.** Not here. Audio toggles take effect before the encoder even runs.
- **No logins, no telemetry, no cloud, no auto-updates.**
- **Lightweight.** One EXE in the tray, done.

## Language

The UI is fully bilingual (German/English). Switch it in Settings → About → "Sprache / Language"; it applies immediately across every window, no restart needed.

## Audio

System sound and microphone are captured in-process via WASAPI (NAudio). No Stereo Mix, no VB-Cable, no workarounds. Game-only audio uses the WASAPI process loopback API to capture a single process tree at the OS level. Every source can be toggled individually in the settings.

## Supported codecs

| Codec | GPU | Note |
|-------|-----|------|
| `h264_amf` | AMD (RX 6000/7000/9000) | Default |
| `hevc_amf` | AMD | Smaller files |
| `h264_nvenc` | NVIDIA | GeForce GTX 900+ |
| `hevc_nvenc` | NVIDIA | |
| `h264_qsv` | Intel | Intel Arc / iGPU |
| `libx264` | CPU | Fallback, runs everywhere |

Available codecs are detected at startup with a real test encode; change them in the Video tab or directly in `settings.json`.

## Installation

### Installer (recommended)

Download the setup EXE from [Releases](https://github.com/WKImods/WKI-Clipper/releases) and run it. It contains everything:
- Self-contained .NET 8 runtime (no separate install required)
- FFmpeg with all hardware encoders (AMF/NVENC/QSV)
- Start menu entry, optional desktop shortcut and autostart

Per-user install, no admin required. The uninstaller cleans up; user data (clips, settings) is kept.

### Build it yourself

Prerequisites: .NET 8 SDK, FFmpeg (e.g. `winget install Gyan.FFmpeg`), Inno Setup 6.

```powershell
git clone https://github.com/WKImods/WKI-Clipper.git
cd WKI-Clipper
.\build.ps1
```

Produces `installer_output\WKI_Clipper_Setup_X.X.X.exe`.

Dev build without the installer:
```powershell
dotnet build WKI_Clipper.sln -c Debug
.\WKI_Clipper\bin\Debug\net8.0-windows10.0.22621.0\WKI_Clipper.exe
```

## Settings

`%APPDATA%\WKI_Clipper\settings.json` — created on first start, directly editable. Everything is also configurable in the overlay UI (including press-to-bind hotkey rebinding in the Hotkeys tab).

```jsonc
{
  "Capture": {
    "Mode": "Auto",               // Auto | Window | Monitor
    "TargetProcessName": null,    // window mode: process to capture
    "CoupleAudio": true           // audio follows the video target
  },
  "Audio": {
    "RecordMicrophone": true,
    "RecordSystemSound": true
  },
  "Video": {
    "Resolution": "Native",       // FullHD | WQHD | UHD | Native
    "Framerate": 60,
    "Codec": "h264_amf"
  },
  "ReplayBuffer": {
    "Enabled": true,
    "DurationSeconds": 60
  },
  "Behavior": {
    "Language": "Deutsch"         // Deutsch | English
  },
  "Output": {
    "ClipsFolder": "%USERPROFILE%\\Videos\\WKI_Clipper\\Clips",
    "ScreenshotsFolder": "%USERPROFILE%\\Videos\\WKI_Clipper\\Screenshots"
  }
}
```

## Architecture

```
WKI_Clipper.exe (.NET 8 / WPF)
  +-- HotkeyService           Win32 RegisterHotKey
  +-- CaptureTargetResolver   single source of truth: what gets captured, with which audio
  +-- WgcWindowCapture        occlusion-proof window capture (WGC + D3D11)
  +-- VideoPipeService        raw BGRA frames -> named pipe -> FFmpeg (CFR pacing)
  +-- ForegroundTracker       SetWinEventHook-based foreground tracking (Auto mode)
  +-- AudioPipeService        WASAPI loopback + mic -> mix -> named pipe
  +-- ProcessLoopbackCapture  game-only audio (WASAPI process loopback)
  +-- ReplayBufferService     FFmpeg segmented recording (rolling ring buffer)
  +-- ManualRecordingService  FFmpeg single-file recording
  +-- ScreenshotService       whole-monitor grab (ddagrab, GDI fallback)
  +-- SettingsService         JSON config in %APPDATA% (versioned + migrated)
  +-- WidgetHost              owns the widget board, pinning and the crosshair overlay
  +-- CrosshairLibraryService PNG crosshair library (copies + JSON index)
  +-- ObsWebSocketService     OBS control via obs-websocket v5 (auto-reconnect, live events)
  +-- PerformanceMonitorService  CPU/GPU/RAM/VRAM counters, 1 Hz, only while visible
  +-- PreflightChecks         pure go-live checklist evaluation (unit-tested)
  +-- StreamHealth            pure output-stats math: uptime, bitrate, recent drops
```

Window capture runs through Windows.Graphics.Capture (occlusion-proof, survives covered windows); full-monitor capture uses `ddagrab` (Desktop Duplication API). Audio is captured in-process via NAudio (WASAPI), mixed, and fed to FFmpeg through a named pipe.

## Known limits

- **Legacy exclusive fullscreen** cannot be captured per-window; the app detects this and falls back to capturing the game's monitor automatically. Borderless window works everywhere.
- **Anti-cheat:** no hooking inside the game process — only WGC/Desktop Duplication. Should be fine with BattlEye/EAC, but no guarantee.
- **Replay clip length** deviates by up to ~5 s due to segment boundaries.

## License

[MIT](LICENSE) — do whatever you want with it.
