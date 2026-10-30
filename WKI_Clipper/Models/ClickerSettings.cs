using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using WKI_Clipper.Services.AutoClicker;

namespace WKI_Clipper.Models;

/// <summary>Whether the click rate is given as "n per unit" or as the time of one cycle.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RateInputMode { Rate, Duration }

/// <summary>The unit of a "n per ..." click rate.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RateUnit { Second, Minute, Hour, Day }

/// <summary>Click = a short press per cycle (hold from the duty cycle); Hold = the button/key stays down until the clicker stops.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DutyMode { Click, Hold }

/// <summary>What the clicker sends.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClickerInput { Mouse, Keyboard }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClickerMouseButton { Left, Right, Middle }

/// <summary>Case of a keyboard output key; only A-Z are affected.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KeyCase { Lower, Upper }

/// <summary>Toggle = a press flips running; Hold = running only while the hotkey is held. Used for the start hotkey and the master hotkey.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActivationMode { Toggle, Hold }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TimeLimitUnit { Seconds, Minutes, Hours }

/// <summary>What a zone does while the cursor is inside: Stop ends the run, Pause suspends clicking, Start starts the clicker on entering.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ZoneAction { Stop, Pause, Start }

/// <summary>A rectangle in virtual-screen (physical) pixels with an action. Invalid sizes are dropped by <see cref="ClickerSettingsRules.Normalize"/>.</summary>
public sealed class ClickerZone
{
    /// <summary>Stable identity within the list (generated when empty or doubled).</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public ZoneAction Action { get; set; } = ZoneAction.Stop;
    public bool Enabled { get; set; } = true;
}

/// <summary>One stop of the click-point route: a position in virtual-screen pixels, how often to click there and a jitter radius.</summary>
public sealed class ClickPoint
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Clicks { get; set; } = 1;
    public int Radius { get; set; }
}

/// <summary>
/// A named copy of the click, failsafe, limit, zone, click-point and whitelist fields of <see cref="ClickerSettings"/>. No hotkeys,
/// no armed state, no UI fields, no stats - applying a preset never changes how the clicker is started.
/// </summary>
public sealed class ClickerPreset
{
    public string Name { get; set; } = "";

    public RateInputMode RateInputMode { get; set; } = RateInputMode.Rate;
    public double ClickSpeed { get; set; } = ClickerSettings.DefaultClickSpeed;
    public RateUnit ClickUnit { get; set; } = RateUnit.Second;
    public long DurationMs { get; set; } = ClickerSettings.DefaultDurationMs;
    public bool ExtendedSpeedLimit { get; set; }
    public bool SpeedRandomizationEnabled { get; set; } = true;
    public double SpeedRandomizationPercent { get; set; } = 35;
    public bool DutyCycleEnabled { get; set; } = true;
    public double DutyCyclePercent { get; set; } = 45;
    public DutyMode DutyMode { get; set; } = DutyMode.Click;
    public ClickerInput InputType { get; set; } = ClickerInput.Mouse;
    public ClickerMouseButton MouseButton { get; set; } = ClickerMouseButton.Left;
    public int KeyboardVk { get; set; }
    public KeyCase KeyCase { get; set; } = KeyCase.Lower;
    public bool DoubleClick { get; set; }

    public bool ClickLimitEnabled { get; set; }
    public int ClickLimit { get; set; } = 1000;
    public bool TimeLimitEnabled { get; set; }
    public double TimeLimit { get; set; } = 60;
    public TimeLimitUnit TimeLimitUnit { get; set; } = TimeLimitUnit.Seconds;

    public bool CornerStopEnabled { get; set; } = true;
    public int CornerSizePx { get; set; } = 50;
    public bool EdgeStopEnabled { get; set; } = true;
    public int EdgeSizePx { get; set; } = 40;
    public bool TaskSwitcherStopEnabled { get; set; } = true;

    public bool ZonesEnabled { get; set; }
    public List<ClickerZone> Zones { get; set; } = new();

    public bool ClickPointsEnabled { get; set; }
    public List<ClickPoint> ClickPoints { get; set; } = new();
    public bool StopWhenComplete { get; set; }

    public bool WhitelistEnabled { get; set; }
    public List<BlacklistEntry> Whitelist { get; set; } = new();
}

/// <summary>One finished run, as the statistics list it.</summary>
public sealed class RunRecord
{
    public DateTime StartUtc { get; set; }
    public double Seconds { get; set; }
    public long Clicks { get; set; }

    /// <summary>Why the run ended (a reason code or text from the host); only displayed.</summary>
    public string StopReason { get; set; } = "";
}

/// <summary>Usage totals plus the newest runs (newest first, at most <see cref="MaxRuns"/>).</summary>
public sealed class ClickerStats
{
    public const int MaxRuns = 100;

    public long TotalClicks { get; set; }
    public double TotalSeconds { get; set; }
    public long Sessions { get; set; }
    public List<RunRecord> LastRuns { get; set; } = new();

    /// <summary>Counts a finished run into the totals and puts it at the front of <see cref="LastRuns"/> (older runs beyond the cap fall off).</summary>
    public void AddRun(RunRecord run)
    {
        TotalClicks += Math.Max(0, run.Clicks);
        TotalSeconds += Math.Max(0, run.Seconds);
        Sessions++;
        LastRuns.Insert(0, run);
        if (LastRuns.Count > MaxRuns) LastRuns.RemoveRange(MaxRuns, LastRuns.Count - MaxRuns);
    }
}

/// <summary>
/// The native AutoClicker (design section 3). Every field has an initializer, so a settings file written before schema v12 loads the
/// defaults; <see cref="ClickerSettingsRules.Normalize"/> clamps every range after a load, an import or a UI edit.
/// </summary>
public sealed class ClickerSettings
{
    public const double DefaultClickSpeed = 25;
    public const long DefaultDurationMs = 40;

    /// <summary>"Clicker bereit": the helper process runs only while this is true. Always false after the v12 migration.</summary>
    public bool Armed { get; set; }

    public RateInputMode RateInputMode { get; set; } = RateInputMode.Rate;
    public double ClickSpeed { get; set; } = DefaultClickSpeed;
    public RateUnit ClickUnit { get; set; } = RateUnit.Second;
    public long DurationMs { get; set; } = DefaultDurationMs;
    public bool ExtendedSpeedLimit { get; set; }

    public bool SpeedRandomizationEnabled { get; set; } = true;
    public double SpeedRandomizationPercent { get; set; } = 35;
    public bool DutyCycleEnabled { get; set; } = true;
    public double DutyCyclePercent { get; set; } = 45;
    public DutyMode DutyMode { get; set; } = DutyMode.Click;

    public ClickerInput InputType { get; set; } = ClickerInput.Mouse;
    public ClickerMouseButton MouseButton { get; set; } = ClickerMouseButton.Left;

    /// <summary>Virtual-key code of the keyboard output key; 0 = none chosen.</summary>
    public int KeyboardVk { get; set; }
    public KeyCase KeyCase { get; set; } = KeyCase.Lower;
    public bool DoubleClick { get; set; }

    public ClickerHotkey Hotkey { get; set; } = ClickerHotkey.Default();
    public ActivationMode ActivationMode { get; set; } = ActivationMode.Toggle;

    /// <summary>true = an extra modifier held on top of the hotkey's own breaks the match.</summary>
    public bool StrictModifiers { get; set; }

    /// <summary>
    /// The hotkey boxes (start and master hotkey) record left and right modifiers apart (LCtrl instead of Ctrl, AltGr as LCtrl+RAlt).
    /// null = not chosen yet: follows the saved hotkeys (see <c>HotkeyRecorder.RecordsSided</c>).
    /// </summary>
    public bool? SidedModifiers { get; set; }

    /// <summary>null = no master switch (always allowed).</summary>
    public ClickerHotkey? MasterHotkey { get; set; }
    public ActivationMode MasterMode { get; set; } = ActivationMode.Toggle;

    public bool ClickLimitEnabled { get; set; }
    public int ClickLimit { get; set; } = 1000;
    public bool TimeLimitEnabled { get; set; }
    public double TimeLimit { get; set; } = 60;
    public TimeLimitUnit TimeLimitUnit { get; set; } = TimeLimitUnit.Seconds;

    public bool CornerStopEnabled { get; set; } = true;
    public int CornerSizePx { get; set; } = 50;
    public bool EdgeStopEnabled { get; set; } = true;
    public int EdgeSizePx { get; set; } = 40;

    /// <summary>Alt+Tab / Task View in front stops the clicker.</summary>
    public bool TaskSwitcherStopEnabled { get; set; } = true;

    public bool ZonesEnabled { get; set; }
    public List<ClickerZone> Zones { get; set; } = new();

    public bool ClickPointsEnabled { get; set; }
    public List<ClickPoint> ClickPoints { get; set; } = new();
    public bool StopWhenComplete { get; set; }

    /// <summary>Clicks only while the foreground exe is on <see cref="Whitelist"/>. Same entry type as the game blacklist.</summary>
    public bool WhitelistEnabled { get; set; }
    public List<BlacklistEntry> Whitelist { get; set; } = new();

    /// <summary>User presets; the factory presets are code (<see cref="ClickerPresets"/>).</summary>
    public List<ClickerPreset> Presets { get; set; } = new();

    public ClickerStats Stats { get; set; } = new();

    /// <summary>Show the small "AutoClicker · 25 cps" label while running.</summary>
    public bool ShowActiveIndicator { get; set; } = true;

    /// <summary>Selected sub-tab of the AutoClicker tab, one of <see cref="ClickerSettingsRules.SubTabs"/>.</summary>
    public string SubTab { get; set; } = "simple";
}
