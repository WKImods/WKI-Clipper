using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WKI_Clipper.Models;

public sealed class AppSettings
{
    /// <summary>
    /// Bumped when the settings shape changes so <see cref="Services.SettingsService"/>
    /// can migrate old files. 0 = pre-CaptureProfile (legacy CaptureSource + GameProcessName).
    /// </summary>
    public int SchemaVersion { get; set; }

    public AudioSettings Audio { get; set; } = new();
    public VideoSettings Video { get; set; } = new();
    public CaptureProfile Capture { get; set; } = new();
    public ReplayBufferSettings ReplayBuffer { get; set; } = new();
    public OutputSettings Output { get; set; } = new();
    public Dictionary<string, HotkeyBinding> Hotkeys { get; set; } = HotkeyDefaults();
    public BehaviorSettings Behavior { get; set; } = new();
    public WidgetSettings Widgets { get; set; } = new();
    public CrosshairSettings Crosshair { get; set; } = new();
    public GifSettings Gif { get; set; } = new();
    public StreamingSettings Streaming { get; set; } = new();
    public ChatSettings Chat { get; set; } = new();
    public MusicSettings Music { get; set; } = new();

    private static Dictionary<string, HotkeyBinding> HotkeyDefaults() => new()
    {
        [HotkeyActions.SaveReplay]      = new HotkeyBinding { Modifiers = 0,                                Key = 0x78 }, // F9
        [HotkeyActions.Screenshot]      = new HotkeyBinding { Modifiers = 0,                                Key = 0x79 }, // F10
        [HotkeyActions.ToggleRecording] = new HotkeyBinding { Modifiers = HotkeyModifier.Control,           Key = 0x78 }, // Ctrl+F9
        [HotkeyActions.ToggleOverlay]   = new HotkeyBinding { Modifiers = HotkeyModifier.Control | HotkeyModifier.Alt, Key = 0x47 }, // Ctrl+Alt+G (Ctrl+Shift+G kollidiert mit Discord)
        [HotkeyActions.ToggleBuffer]    = new HotkeyBinding { Modifiers = HotkeyModifier.Control,           Key = 0x79 }, // Ctrl+F10
        [HotkeyActions.ToggleCrosshair] = new HotkeyBinding { Modifiers = HotkeyModifier.Control | HotkeyModifier.Alt, Key = 0x43 }, // Ctrl+Alt+C
        [HotkeyActions.SaveGif]         = new HotkeyBinding { Modifiers = 0,                                Key = 0x77 }  // F8
    };
}

public static class HotkeyActions
{
    public const string SaveReplay = "SaveReplay";
    public const string Screenshot = "Screenshot";
    public const string ToggleRecording = "ToggleRecording";
    public const string ToggleOverlay = "ToggleOverlay";
    public const string ToggleBuffer = "ToggleBuffer";
    public const string ToggleCrosshair = "ToggleCrosshair";
    public const string SaveGif = "SaveGif";
}

public sealed class AudioSettings
{
    public bool RecordMicrophone { get; set; } = true;
    public bool RecordSystemSound { get; set; } = true;
    public string MicDeviceId { get; set; } = "default";
    public string SystemDeviceId { get; set; } = "default";
    /// <summary>1.0 = neutral. 2.0 = +6 dB (lauter). 0.5 = -6 dB (leiser).</summary>
    public double MicVolume { get; set; } = 2.0;
    public double SystemVolume { get; set; } = 1.0;
    /// <summary>
    /// Compensates WASAPI capture latency. Negative = audio shifted EARLIER
    /// (typical fix when audio plays delayed vs video). Range roughly -500..+500 ms.
    /// </summary>
    public int OffsetMilliseconds { get; set; } = 0;

    /// <summary>
    /// AllAudio = standard loopback (everything). GameOnly = only the selected process.
    /// </summary>
    public AudioCaptureMode SystemCaptureMode { get; set; } = AudioCaptureMode.AllAudio;

    /// <summary>
    /// Process name (without .exe) for GameOnly mode. null = auto-detect foreground window.
    /// </summary>
    public string? GameProcessName { get; set; }

    /// <summary>
    /// When true AND both mic and system audio are captured, the microphone is written
    /// to its OWN second audio track (track 1 = system, track 2 = mic) instead of being
    /// mixed in — so it can be edited separately. No effect if only one source is on.
    /// </summary>
    public bool SeparateMicTrack { get; set; } = false;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioCaptureMode
{
    /// <summary>Standard WASAPI Loopback — captures ALL system audio.</summary>
    AllAudio,
    /// <summary>Process Loopback — captures ONLY audio from a specific process tree.</summary>
    GameOnly
}

/// <summary>
/// The single source of truth for "what gets clipped" — consumed identically by
/// the replay buffer, manual recording, screenshots and (when coupled) audio.
/// </summary>
public sealed class CaptureProfile
{
    /// <summary>How the video target is chosen.</summary>
    public CaptureMode Mode { get; set; } = CaptureMode.Auto;

    /// <summary>
    /// For <see cref="CaptureMode.Monitor"/>: the display device name (stable
    /// across replugging) to capture. null = primary monitor.
    /// </summary>
    public string? MonitorDeviceName { get; set; }

    /// <summary>
    /// For <see cref="CaptureMode.Window"/>: the process name (without .exe) whose
    /// window's monitor is captured and whose audio is used when coupled.
    /// </summary>
    public string? TargetProcessName { get; set; }

    /// <summary>
    /// When true, the audio route follows the resolved video target's process
    /// (capture only the game's sound). When false, audio uses the Audio-tab
    /// settings (all sounds / manual GameOnly).
    /// </summary>
    public bool CoupleAudio { get; set; } = true;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CaptureMode
{
    /// <summary>Capture the monitor of the current foreground app, pinned. Audio follows that app.</summary>
    Auto,
    /// <summary>Capture the monitor of a specific chosen window/process, pinned.</summary>
    Window,
    /// <summary>Capture a whole chosen monitor (e.g. for tutorials).</summary>
    Monitor
}

/// <summary>Runtime decision for the system-audio source of a capture session.</summary>
public enum SystemAudioMode
{
    /// <summary>No system audio (mic-only or nothing).</summary>
    None,
    /// <summary>WASAPI loopback of the whole render device.</summary>
    AllAudio,
    /// <summary>Process loopback of a specific PID tree.</summary>
    Process
}

/// <summary>Which sources an audio pipe may open — used to split mic and system onto separate tracks.</summary>
public enum AudioSourceMask
{
    /// <summary>Mic + system, mixed into one stream (default single-track behaviour).</summary>
    Both,
    /// <summary>System audio only (track 1 of a separate-mic recording).</summary>
    SystemOnly,
    /// <summary>Microphone only (track 2 of a separate-mic recording).</summary>
    MicOnly
}

public sealed class VideoSettings
{
    public ResolutionPreset Resolution { get; set; } = ResolutionPreset.Native;
    public int Framerate { get; set; } = 60;
    public string Codec { get; set; } = "h264_amf";
    public QualityPreset Quality { get; set; } = QualityPreset.Mittel;
    public int Bitrate { get; set; } = 25_000_000;   // used when Quality == Custom
    public CaptureSource CaptureSource { get; set; } = CaptureSource.Display;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CaptureSource
{
    /// <summary>Capture the whole desktop via ddagrab (DXGI Desktop Duplication).</summary>
    Display,
    /// <summary>Capture the foreground window at recording start via gdigrab title=.</summary>
    ActiveWindow
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResolutionPreset { FullHD, WQHD, UHD, Native }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QualityPreset { Niedrig, Mittel, Hoch, SehrHoch, Custom }

public static class QualityPresets
{
    /// <summary>Returns bitrate in bps for a (quality, resolution) combination.</summary>
    public static int ComputeBitrate(QualityPreset q, ResolutionPreset r)
    {
        // Effective tier: Native is treated like WQHD until we wire monitor detection through.
        var tier = r switch
        {
            ResolutionPreset.FullHD => 0,
            ResolutionPreset.WQHD   => 1,
            ResolutionPreset.UHD    => 2,
            ResolutionPreset.Native => 1,
            _                       => 1
        };

        // Rows = quality (Niedrig..SehrHoch), Cols = tier (FHD/QHD/UHD)
        // Mbps × 1e6
        int[,] mbps =
        {
            {  8, 14, 25 },  // Niedrig
            { 15, 25, 45 },  // Mittel (default)
            { 25, 40, 80 },  // Hoch
            { 40, 70, 150 }, // Sehr hoch
        };

        int qi = q switch
        {
            QualityPreset.Niedrig => 0,
            QualityPreset.Mittel  => 1,
            QualityPreset.Hoch    => 2,
            QualityPreset.SehrHoch => 3,
            _ => 1
        };

        return mbps[qi, tier] * 1_000_000;
    }
}

public sealed class ReplayBufferSettings
{
    public bool Enabled { get; set; } = true;
    public int DurationSeconds { get; set; } = 60;
    public int SegmentDurationSeconds { get; set; } = 5;
}

public sealed class OutputSettings
{
    public string ClipsFolder { get; set; } = @"%USERPROFILE%\Videos\WKI_Clipper\Clips";
    public string ScreenshotsFolder { get; set; } = @"%USERPROFILE%\Videos\WKI_Clipper\Screenshots";
    public string BufferFolder { get; set; } = @"%LOCALAPPDATA%\WKI_Clipper\buffer";
    public string FilenameTemplate { get; set; } = "Clip_{date}_{time}";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AppLanguage { Deutsch, English }

public sealed class BehaviorSettings
{
    public AppLanguage Language { get; set; } = AppLanguage.Deutsch;
    public bool StartWithWindows { get; set; } = false;
    public bool StartBufferOnLaunch { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool ShowToastNotifications { get; set; } = true;
    public int OverlayAutoCloseSeconds { get; set; } = 10;

    /// <summary>
    /// Show a small "REC" indicator while a manual recording runs. The indicator
    /// window is excluded from screen capture, so it never appears in the recording.
    /// </summary>
    public bool ShowRecordingIndicator { get; set; } = true;
}

/// <summary>
/// The "Streaming" widget: a software stream deck whose buttons drive OBS over
/// obs-websocket v5. Fully self-built — no Elgato software or hardware involved.
/// </summary>
public sealed class StreamingSettings
{
    public ObsConnectionSettings Obs { get; set; } = new();
    public int GridColumns { get; set; } = 4;
    public int GridRows { get; set; } = 3;
    public List<StreamButtonConfig> Buttons { get; set; } = new();
    public PreflightSettings Preflight { get; set; } = new();
}

/// <summary>Read-only Twitch chat overlay (anonymous IRC — no account or key needed).</summary>
public sealed class ChatSettings
{
    /// <summary>Twitch channel to read, without the leading '#'.</summary>
    public string Channel { get; set; } = "oskar_blitz";
    public int MaxMessages { get; set; } = 200;
    public double FontSize { get; set; } = 13;
    /// <summary>Connect on app start (the service reconnects on its own if Twitch is down).</summary>
    public bool AutoConnect { get; set; } = true;
    /// <summary>
    /// Toast on raids and gifted subs, so they are not missed while the chat widget is
    /// closed. Deliberately limited to those two — a toast per regular sub or message
    /// would cover the game.
    /// </summary>
    public bool EventToasts { get; set; } = true;
}

/// <summary>
/// Stream music player. The main output feeds the stream (usually a virtual cable);
/// the optional monitor output lets the streamer hear the same track, with its own level.
/// </summary>
public sealed class MusicSettings
{
    public string Folder { get; set; } = @"D:\Desktop\Stream\Musik";
    /// <summary>MMDevice id of the stream output (VB-Cable). null = system default.</summary>
    public string? OutputDeviceId { get; set; }
    /// <summary>Second output so you hear the music yourself.</summary>
    public bool MonitorEnabled { get; set; }
    public string? MonitorDeviceId { get; set; }
    /// <summary>Level of what listeners hear (0…1).</summary>
    public float Volume { get; set; } = 0.5f;
    /// <summary>Level of what YOU hear — independent of the stream level.</summary>
    public float MonitorVolume { get; set; } = 0.5f;
    public bool Shuffle { get; set; } = true;
    public bool Repeat { get; set; } = true;
    /// <summary>Text file an OBS text source can read.</summary>
    public string NowPlayingFile { get; set; } = @"%APPDATA%\WKI_Clipper\now_playing.txt";
    public string NowPlayingTemplate { get; set; } = "{artist} - {title}";
}

/// <summary>Go-live checklist + the automated start sequence.</summary>
public sealed class PreflightSettings
{
    /// <summary>OBS audio input treated as "the microphone" in the checklist.</summary>
    public string MicInputName { get; set; } = "Mikrofon";
    /// <summary>Scene shown while the countdown runs (your "Starting Soon" screen).</summary>
    public string StartScene { get; set; } = "Start";
    /// <summary>Scene switched to after the countdown.</summary>
    public string TargetScene { get; set; } = "Arma Reforger";
    /// <summary>Seconds between going live and the switch to the target scene.</summary>
    public int CountdownSeconds { get; set; } = 30;
    /// <summary>Turn OBS's replay buffer on as part of the sequence.</summary>
    public bool StartReplayBuffer { get; set; } = true;
}

public sealed class ObsConnectionSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 4455;
    /// <summary>
    /// DPAPI-protected (CurrentUser) base64 — never the plaintext password.
    /// null/empty = no password (OBS with auth disabled).
    /// </summary>
    public string? PasswordProtected { get; set; }
    public bool AutoConnect { get; set; } = true;
}

/// <summary>One tile of the stream-deck grid.</summary>
public sealed class StreamButtonConfig
{
    /// <summary>Grid slot index (row-major). Also the identity for the hotkey routing.</summary>
    public int Slot { get; set; }
    public string Label { get; set; } = "";
    /// <summary>Tile background as #RRGGBB.</summary>
    public string Color { get; set; } = "#3A3A44";
    public StreamAction Action { get; set; } = StreamAction.SetScene;
    /// <summary>Scene name / input name, depending on the action.</summary>
    public string? Param { get; set; }
    /// <summary>Second parameter — the scene-item name for ToggleSceneItem.</summary>
    public string? Param2 { get; set; }
    /// <summary>Optional global hotkey. null / Key==0 = none.</summary>
    public HotkeyBinding? Hotkey { get; set; }
}

/// <summary>What a stream-deck button does in OBS (obs-websocket v5 requests).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StreamAction
{
    SetScene,               // SetCurrentProgramScene {Param = scene}
    ToggleStream,
    StartStream,
    StopStream,
    ToggleRecord,
    PauseResumeRecord,      // PauseRecord/ResumeRecord depending on state
    SaveReplayBuffer,       // OBS's own replay buffer — NOT the clipper's F9 buffer
    ToggleReplayBuffer,
    ToggleInputMute,        // {Param = input name}
    ToggleSceneItem,        // {Param = scene, Param2 = item name}
    ToggleVirtualCam,
    StudioModeTransition
}

/// <summary>Settings for the instant-GIF export (last N seconds of the buffer as a GIF).</summary>
public sealed class GifSettings
{
    /// <summary>How many seconds from the end of the buffer to turn into a GIF.</summary>
    public int DurationSeconds { get; set; } = 5;
    /// <summary>GIF frame rate — kept low; GIFs balloon in size fast.</summary>
    public int Framerate { get; set; } = 15;
    /// <summary>GIF width in pixels (height keeps aspect). 0 = source width.</summary>
    public int Width { get; set; } = 640;
}

public sealed class HotkeyBinding
{
    public HotkeyModifier Modifiers { get; set; }
    public uint Key { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WidgetId { Capture, Audio, Gallery, Performance, Settings, Crosshair, Streaming, Mixer, Preflight, Chat, Music }

/// <summary>
/// The PNG crosshair overlay: which image from the library is active, where it sits
/// and how it is tinted. The image files themselves live in the crosshair library
/// (%APPDATA%\WKI_Clipper\crosshairs) — only the id is stored here.
/// </summary>
public sealed class CrosshairSettings
{
    /// <summary>Master switch — the overlay is shown when true (toggled by Ctrl+Alt+C).</summary>
    public bool Enabled { get; set; }
    /// <summary>Id of the active library entry. null = none selected yet.</summary>
    public string? ActiveId { get; set; }
    /// <summary>
    /// Screen position of the crosshair's CENTER in virtual-desktop pixels.
    /// null = not placed yet → centered on the primary screen. (Nullable rather than
    /// NaN: JsonSerializer refuses to write NaN, which would break settings saving.)
    /// </summary>
    public double? CenterX { get; set; }
    public double? CenterY { get; set; }
    /// <summary>
    /// Snap dragging to a grid anchored at the MONITOR CENTER — so the exact center is
    /// always hittable and offsets stay symmetric.
    /// </summary>
    public bool SnapToGrid { get; set; } = true;
    /// <summary>Grid step in pixels.</summary>
    public int GridSize { get; set; } = 25;
    /// <summary>Render scale, 1.0 = native PNG size.</summary>
    public double Scale { get; set; } = 1.0;
    /// <summary>0..1 window opacity.</summary>
    public double Opacity { get; set; } = 1.0;
    /// <summary>-1..+1, 0 = unchanged.</summary>
    public double Brightness { get; set; }
    /// <summary>0.5..2.0, 1 = unchanged.</summary>
    public double Contrast { get; set; } = 1.0;
    /// <summary>0..2, 1 = unchanged (0 = grayscale).</summary>
    public double Saturation { get; set; } = 1.0;
    /// <summary>Optional color tint multipliers, 1 = unchanged.</summary>
    public double RedGain { get; set; } = 1.0;
    public double GreenGain { get; set; } = 1.0;
    public double BlueGain { get; set; } = 1.0;
}

/// <summary>
/// One floating widget's persisted placement. Position is stored relative to the
/// monitor named by <see cref="MonitorDeviceName"/> (device name is stable across
/// replugging); on load it is clamped back onto a visible work area.
/// </summary>
public sealed class WidgetState
{
    public WidgetId Id { get; set; }
    /// <summary>Shown when the board is open.</summary>
    public bool Visible { get; set; } = true;
    /// <summary>Stays visible over the game after the board closes.</summary>
    public bool Pinned { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    /// <summary>Device name of the monitor the position is relative to. null = primary.</summary>
    public string? MonitorDeviceName { get; set; }
    /// <summary>
    /// Window opacity 0.3–1.0 (property initializer = default for settings files
    /// written before this existed). Hovering temporarily restores full opacity.
    /// </summary>
    public double Opacity { get; set; } = 1.0;
    /// <summary>
    /// When pinned and the board is closed, let mouse clicks pass straight through to
    /// whatever is underneath (WS_EX_TRANSPARENT). For read-only overlays like chat.
    /// </summary>
    public bool ClickThrough { get; set; }
}

public sealed class WidgetSettings
{
    public List<WidgetState> Widgets { get; set; } = DefaultLayout();

    /// <summary>
    /// The five built-in widgets at sensible starting sizes/offsets. Positions are
    /// left at 0,0 here and laid out on first show by the host (staggered), so a
    /// fresh install doesn't need monitor geometry baked into the model.
    /// </summary>
    public static List<WidgetState> DefaultLayout() => new()
    {
        new WidgetState { Id = WidgetId.Capture,     Visible = true,  Width = 360, Height = 430 },
        new WidgetState { Id = WidgetId.Audio,       Visible = true,  Width = 380, Height = 500 },
        new WidgetState { Id = WidgetId.Gallery,     Visible = true,  Width = 520, Height = 560 },
        new WidgetState { Id = WidgetId.Performance, Visible = true,  Width = 320, Height = 300 },
        new WidgetState { Id = WidgetId.Settings,    Visible = false, Width = 480, Height = 540 },
        new WidgetState { Id = WidgetId.Crosshair,   Visible = false, Width = 400, Height = 560 },
        new WidgetState { Id = WidgetId.Streaming,   Visible = false, Width = 460, Height = 520 },
        new WidgetState { Id = WidgetId.Mixer,       Visible = false, Width = 400, Height = 340 },
        new WidgetState { Id = WidgetId.Preflight,   Visible = false, Width = 420, Height = 460 },
        // Chat defaults to click-through: pinned over a game it must never eat a shot.
        new WidgetState { Id = WidgetId.Chat,        Visible = false, Width = 360, Height = 480, ClickThrough = true },
        new WidgetState { Id = WidgetId.Music,       Visible = false, Width = 420, Height = 520 },
    };

    /// <summary>Returns the stored state for an id, creating a default if missing.</summary>
    public WidgetState GetOrAdd(WidgetId id)
    {
        foreach (var w in Widgets) if (w.Id == id) return w;
        var created = DefaultLayout().Find(w => w.Id == id) ?? new WidgetState { Id = id, Width = 360, Height = 400 };
        Widgets.Add(created);
        return created;
    }
}

[System.Flags]
public enum HotkeyModifier : uint
{
    None    = 0,
    Alt     = 0x0001,
    Control = 0x0002,
    Shift   = 0x0004,
    Win     = 0x0008,
    NoRepeat = 0x4000
}
