# WKI Clipper

Lightweight replay clipper and screen recorder for Windows. A handful of hotkeys, no bloat.

## Features

| Hotkey | Action |
|--------|--------|
| `F9` | Save the last 15–180 s as MP4 (instant replay) |
| `F10` | Screenshot of the active window (PNG) |
| `Ctrl+F9` | Start/stop manual recording |
| `Ctrl+F10` | Pause/resume the replay buffer |
| `Ctrl+Alt+G` | Open/close the overlay |

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

The UI is fully bilingual (German/English). Switch it in the About tab → "Sprache / Language" (restart applies it everywhere).

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
  +-- ScreenshotService       PrintWindow / ddagrab fallback
  +-- SettingsService         JSON config in %APPDATA%
  +-- OverlayWindow           WPF overlay (capture, settings, clips, status)
```

Window capture runs through Windows.Graphics.Capture (occlusion-proof, survives covered windows); full-monitor capture uses `ddagrab` (Desktop Duplication API). Audio is captured in-process via NAudio (WASAPI), mixed, and fed to FFmpeg through a named pipe.

## Known limits

- **Legacy exclusive fullscreen** cannot be captured per-window; the app detects this and falls back to capturing the game's monitor automatically. Borderless window works everywhere.
- **Anti-cheat:** no hooking inside the game process — only WGC/Desktop Duplication. Should be fine with BattlEye/EAC, but no guarantee.
- **Replay clip length** deviates by up to ~5 s due to segment boundaries.

## License

[MIT](LICENSE) — do whatever you want with it.
