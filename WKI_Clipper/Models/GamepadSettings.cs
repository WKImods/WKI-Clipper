using System.Collections.Generic;
using WKI_Clipper.Services;

namespace WKI_Clipper.Models;

/// <summary>
/// Controller combos: hold a button combination on an Xbox controller to run a clipper action, the
/// same as pressing its keyboard hotkey. The section has an initializer for every value, so a settings
/// file written before it existed simply loads the defaults - no schema migration.
/// </summary>
public sealed class GamepadSettings
{
    /// <summary>Master switch; off stops the polling thread entirely.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long the exact combo has to be held (300..1500 ms).</summary>
    public int HoldMs { get; set; } = GamepadComboDetector.DefaultHoldMs;

    /// <summary>
    /// Action name (<see cref="HotkeyActions"/>) to button mask (<see cref="GamepadButtons"/> bits). An
    /// action that is missing here has no controller combo. View + Menu saves the replay by default.
    /// </summary>
    public Dictionary<string, ushort> Bindings { get; set; } = new()
    {
        [HotkeyActions.SaveReplay] = GamepadButtons.View | GamepadButtons.Menu
    };
}
