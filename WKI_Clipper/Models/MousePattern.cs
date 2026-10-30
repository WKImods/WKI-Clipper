using System.Text.Json.Serialization;
using WKI_Clipper.Services;
using WKI_Clipper.Services.AutoClicker;

namespace WKI_Clipper.Models;

/// <summary>
/// Which recorded track a pattern replays (mouse-patterns design 4.3): Auto decides at each playback start from the Windows pointer
/// acceleration and the pointer's visibility; Pointer = anchor + pointer offsets as absolute moves; Raw = the raw counts as relative moves.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PatternReplay { Auto, Pointer, Raw }

/// <summary>
/// A recorded mouse movement that replays relative to where the mouse is (mouse-patterns design 4.1). Metadata only: the samples
/// live in their own file (<see cref="MousePatternStore"/>), never in settings.json or the per-change config push.
/// <see cref="MousePatternRules.Normalize"/> repairs every field after a load or a UI edit.
/// </summary>
public sealed class MousePattern
{
    /// <summary>Stable identity, 32 hex characters (the sample file's name); generated when missing, invalid or doubled.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>null = no hotkey: the pattern never plays (a new recording starts like this).</summary>
    public ClickerHotkey? Hotkey { get; set; }

    /// <summary>Only active patterns with a hotkey are tracked by the helper.</summary>
    public bool Active { get; set; } = true;

    public ActivationMode Mode { get; set; } = ActivationMode.Hold;

    /// <summary>On: a finished pass continues seamlessly with the next one; off: one pass per press.</summary>
    public bool Repeat { get; set; }

    /// <summary>Tempo factor, <see cref="MousePatternRules.MinSpeed"/> .. <see cref="MousePatternRules.MaxSpeed"/>; the shape stays the same.</summary>
    public double Speed { get; set; } = MousePatternRules.DefaultSpeed;

    public PatternReplay Replay { get; set; } = PatternReplay.Auto;

    /// <summary>Exe file name of the foreground program when the recording started; null when unknown.</summary>
    public string? Program { get; set; }

    /// <summary>"Nur in &lt;Programm&gt;": plays only while <see cref="Program"/> is in front. Off by default.</summary>
    public bool OnlyInProgram { get; set; }

    /// <summary>Length of the recorded motion (read-only fact of the recording).</summary>
    public double DurationMs { get; set; }

    /// <summary>Grows with every new recording; the main side resends the samples to the helper when it changed.</summary>
    public int Revision { get; set; }
}
