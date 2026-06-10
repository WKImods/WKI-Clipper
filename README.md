# WKI Clipper

Schlanker Replay-Clipper und Screen-Recorder fuer Windows. Drei Hotkeys, kein Schnickschnack.

## Features

| Hotkey | Aktion |
|--------|--------|
| `F9` | Letzte 30/60 s als MP4 speichern (Instant Replay) |
| `F10` | Screenshot vom aktiven Fenster (PNG) |
| `Strg+F9` | Manuelles Recording Start/Stop |
| `Strg+F10` | Replay-Buffer pausieren/fortsetzen |
| `Strg+Alt+G` | Overlay oeffnen/schliessen |

## Warum?

- **Xbox Game Bar nimmt das Mikro auf, auch wenn es deaktiviert ist.** Hier nicht. Audio-Toggles greifen bevor der Encoder ueberhaupt laeuft.
- **Keine Logins, keine Telemetry, keine Cloud, keine Auto-Updates.**
- **Leichtgewichtig.** Eine EXE im Tray, fertig.

## Audio

System-Sound und Mikrofon werden In-Process ueber WASAPI aufgenommen (NAudio). Kein Stereomix, kein VB-Cable, keine Umwege. Beides einzeln an/abschaltbar in den Settings.

## Unterstuetzte Codecs

| Codec | GPU | Hinweis |
|-------|-----|---------|
| `h264_amf` | AMD (RX 6000/7000/9000) | Default |
| `hevc_amf` | AMD | Kleinere Dateien |
| `h264_nvenc` | NVIDIA | GeForce GTX 900+ |
| `hevc_nvenc` | NVIDIA | |
| `h264_qsv` | Intel | Intel Arc / iGPU |
| `libx264` | CPU | Fallback, laeuft ueberall |

Codec in den Settings (Tab "Video") oder direkt in `settings.json` aendern.

## Installation

### Installer (empfohlen)

Setup-EXE von [Releases](https://github.com/WKImods/WKI-Clipper/releases) herunterladen und ausfuehren. Enthaelt alles:
- Self-contained .NET 8 Runtime (kein separates Install noetig)
- FFmpeg mit allen Hardware-Encodern (AMF/NVENC/QSV)
- Start-Menu-Eintrag, optionaler Desktop-Shortcut und Autostart

Per-User Install, kein Admin noetig. Uninstaller raeumt auf, User-Daten (Clips, Settings) bleiben.

### Selber bauen

Voraussetzungen: .NET 8 SDK, FFmpeg (z.B. `winget install Gyan.FFmpeg`), Inno Setup 6.

```powershell
git clone https://github.com/WKImods/WKI-Clipper.git
cd WKI-Clipper
.\build.ps1
```

Erzeugt `installer_output\WKI_Clipper_Setup_X.X.X.exe`.

Dev-Build ohne Installer:
```powershell
dotnet build WKI_Clipper.sln -c Debug
.\WKI_Clipper\bin\Debug\net8.0-windows10.0.19041.0\WKI_Clipper.exe
```

## Settings

`%APPDATA%\WKI_Clipper\settings.json` — wird beim ersten Start angelegt, direkt editierbar.

```jsonc
{
  "Audio": {
    "RecordMicrophone": false,
    "RecordSystemSound": true
  },
  "Video": {
    "Resolution": "Native",       // FullHD | WQHD | UHD | Native
    "Framerate": 60,
    "Codec": "h264_amf",
    "Bitrate": 25000000
  },
  "ReplayBuffer": {
    "Enabled": true,
    "DurationSeconds": 60,
    "SegmentDurationSeconds": 5
  },
  "Output": {
    "ClipsFolder": "%USERPROFILE%\\Videos\\WKI_Clipper\\Clips",
    "ScreenshotsFolder": "%USERPROFILE%\\Videos\\WKI_Clipper\\Screenshots"
  }
}
```

## Architektur

```
WKI_Clipper.exe (.NET 8 / WPF)
  +-- HotkeyService          Win32 RegisterHotKey
  +-- AudioPipeService        WASAPI Loopback + Mic -> Mix -> Named Pipe
  +-- ReplayBufferService     FFmpeg segmented recording (rolling ring buffer)
  +-- ManualRecordingService  FFmpeg single-file recording
  +-- ScreenshotService       PrintWindow / ddagrab fallback
  +-- SettingsService         JSON config in %APPDATA%
  +-- OverlayWindow           WPF Overlay (Settings, Clips, Status)
```

Video-Capture laeuft ueber `ddagrab` (Desktop Duplication API). Audio wird In-Process ueber NAudio (WASAPI) aufgenommen, gemischt und per Named Pipe an FFmpeg gefuettert.

## Bekannte Limits

- **True Fullscreen (exklusiv)** kann von ddagrab nicht erfasst werden. Spiel auf "Borderless Window" stellen.
- **Anti-Cheat:** Kein Hooking im Spielprozess — nur Desktop Duplication. Sollte mit BattlEye/EAC kein Problem sein, aber keine Garantie.
- **Replay-Clip-Laenge** weicht wegen Segment-Grenzen um ca. 5 s ab.
- **Hotkey-Rebinding** aktuell nur ueber `settings.json` (kein UI-Editor).

## Lizenz

[MIT](LICENSE) — Mach damit was du willst.
