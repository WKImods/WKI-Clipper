using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WKI_Clipper.Models;

public sealed class AppSettings
{
    public AudioSettings Audio { get; set; } = new();
    public VideoSettings Video { get; set; } = new();
    public ReplayBufferSettings ReplayBuffer { get; set; } = new();
    public OutputSettings Output { get; set; } = new();
    public Dictionary<string, HotkeyBinding> Hotkeys { get; set; } = HotkeyDefaults();
    public BehaviorSettings Behavior { get; set; } = new();

    private static Dictionary<string, HotkeyBinding> HotkeyDefaults() => new()
    {
        [HotkeyActions.SaveReplay]      = new HotkeyBinding { Modifiers = 0,                                Key = 0x78 }, // F9
        [HotkeyActions.Screenshot]      = new HotkeyBinding { Modifiers = 0,                                Key = 0x79 }, // F10
        [HotkeyActions.ToggleRecording] = new HotkeyBinding { Modifiers = HotkeyModifier.Control,           Key = 0x78 }, // Ctrl+F9
        [HotkeyActions.ToggleOverlay]   = new HotkeyBinding { Modifiers = HotkeyModifier.Control | HotkeyModifier.Alt, Key = 0x47 }, // Ctrl+Alt+G (Ctrl+Shift+G kollidiert mit Discord)
        [HotkeyActions.ToggleBuffer]    = new HotkeyBinding { Modifiers = HotkeyModifier.Control,           Key = 0x79 }  // Ctrl+F10
    };
}

public static class HotkeyActions
{
    public const string SaveReplay = "SaveReplay";
    public const string Screenshot = "Screenshot";
    public const string ToggleRecording = "ToggleRecording";
    public const string ToggleOverlay = "ToggleOverlay";
    public const string ToggleBuffer = "ToggleBuffer";
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

public sealed class BehaviorSettings
{
    public bool StartWithWindows { get; set; } = false;
    public bool StartBufferOnLaunch { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool ShowToastNotifications { get; set; } = true;
    public int OverlayAutoCloseSeconds { get; set; } = 10;
}

public sealed class HotkeyBinding
{
    public HotkeyModifier Modifiers { get; set; }
    public uint Key { get; set; }
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
