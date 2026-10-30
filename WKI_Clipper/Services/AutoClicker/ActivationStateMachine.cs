using WKI_Clipper.Models;

namespace WKI_Clipper.Services.AutoClicker;

public enum ActivationCommand { None, Start, Stop }

/// <summary>Why the machine asks for a start or stop. The host maps these to the reason texts the UI shows.</summary>
public enum ActivationReason { None, Hotkey, HoldReleased, MasterSwitch, Locked, PickerOpened, StartZone }

/// <summary>One output of <see cref="ActivationStateMachine"/>: <see cref="ActivationCommand.None"/> carries no reason.</summary>
public readonly record struct ActivationOutput(ActivationCommand Command, ActivationReason Reason)
{
    public static readonly ActivationOutput None = new(ActivationCommand.None, ActivationReason.None);
    public static ActivationOutput Start(ActivationReason reason) => new(ActivationCommand.Start, reason);
    public static ActivationOutput Stop(ActivationReason reason) => new(ActivationCommand.Stop, reason);
}

/// <summary>
/// The start/stop logic behind the clicker's hotkeys (design section 5), free of threads and input: the host feeds it match changes
/// of the hotkeys and the lock/picker state, and executes the Start/Stop it returns.
/// <list type="bullet">
/// <item>A start needs a RISING edge of the hotkey match while unlocked, no picker open and the master switch allowing it. A hotkey that
/// was already held when the lock lifted or the master switch opened starts nothing.</item>
/// <item>Toggle: a rising edge flips running. Hold: a rising edge starts, losing the match stops (<see cref="ActivationReason.HoldReleased"/>).</item>
/// <item>Master switch: no master = always allowed. Master Toggle flips on each rising edge and starts OFF (nothing clicks until the
/// user switches it on); Master Hold allows only while held. Losing the permission stops a run (<see cref="ActivationReason.MasterSwitch"/>).</item>
/// <item>Lock (blacklist) and picker stop a run and refuse starts; the master toggle does not flip while a picker is open.</item>
/// <item>The machine assumes a Start it returned is executed. When the engine ends on its own (limit, failsafe, start refused ...) the host
/// calls <see cref="OnStoppedByEngine"/>; no restart happens until the hotkey is released and pressed again.</item>
/// </list>
/// </summary>
public sealed class ActivationStateMachine
{
    private bool _running;
    private bool _locked;
    private bool _pickerOpen;
    private bool _hotkeyMatched;
    private bool _masterMatched;
    private bool _masterToggleOn;

    private ActivationMode _mode = ActivationMode.Toggle;
    private bool _hasMaster;
    private ActivationMode _masterMode = ActivationMode.Toggle;

    public bool IsRunning => _running;
    public bool IsLocked => _locked;
    public bool IsPickerOpen => _pickerOpen;

    /// <summary>No master switch configured, or it currently allows the clicker.</summary>
    public bool MasterAllowed => !_hasMaster || (_masterMode == ActivationMode.Hold ? _masterMatched : _masterToggleOn);

    /// <summary>
    /// Applies the hotkey modes. Switching the master off (<paramref name="hasMaster"/> false) allows everything; switching it on again
    /// starts from its natural state (Toggle: off, Hold: whatever is held now). A running clicker the new setup no longer allows is
    /// stopped. Changing the master mode resets the toggle latch to off.
    /// </summary>
    public ActivationOutput Configure(ActivationMode mode, bool hasMaster, ActivationMode masterMode)
    {
        if (!hasMaster || masterMode != _masterMode) _masterToggleOn = false;
        _mode = mode;
        _hasMaster = hasMaster;
        _masterMode = masterMode;
        return StopIfNotAllowed();
    }

    /// <summary>The start hotkey's match changed (<see cref="HotkeyMatcher.IsMatch"/>).</summary>
    public ActivationOutput OnMatchChanged(bool matched)
    {
        bool rising = matched && !_hotkeyMatched;
        bool falling = !matched && _hotkeyMatched;
        _hotkeyMatched = matched;

        if (_mode == ActivationMode.Toggle)
        {
            if (!rising) return ActivationOutput.None;
            if (_running) return Stopped(ActivationReason.Hotkey);
            return CanStart ? Started(ActivationReason.Hotkey) : ActivationOutput.None;
        }

        if (rising) return !_running && CanStart ? Started(ActivationReason.Hotkey) : ActivationOutput.None;
        if (falling && _running) return Stopped(ActivationReason.HoldReleased);
        return ActivationOutput.None;
    }

    /// <summary>The master hotkey's match changed. Tracked even without a configured master and while a picker is open.</summary>
    public ActivationOutput OnMasterChanged(bool matched)
    {
        bool rising = matched && !_masterMatched;
        _masterMatched = matched;

        if (_hasMaster && _masterMode == ActivationMode.Toggle && rising && !_pickerOpen)
            _masterToggleOn = !_masterToggleOn;

        return StopIfNotAllowed();
    }

    public ActivationOutput OnLock()
    {
        _locked = true;
        return _running ? Stopped(ActivationReason.Locked) : ActivationOutput.None;
    }

    public void OnUnlock() => _locked = false;

    public ActivationOutput OnPicker(bool open)
    {
        _pickerOpen = open;
        return open && _running ? Stopped(ActivationReason.PickerOpened) : ActivationOutput.None;
    }

    /// <summary>The engine ended the run itself (limit, failsafe, start refused ...). Produces no command.</summary>
    public void OnStoppedByEngine() => _running = false;

    /// <summary>
    /// A start from outside the hotkeys (the start zone). Honours lock, picker and master switch exactly like a hotkey start; a
    /// running clicker is left alone.
    /// </summary>
    public ActivationOutput OnStartRequested(ActivationReason reason)
        => !_running && CanStart ? Started(reason) : ActivationOutput.None;

    private bool CanStart => !_locked && !_pickerOpen && MasterAllowed;

    private ActivationOutput StopIfNotAllowed()
        => _running && !MasterAllowed ? Stopped(ActivationReason.MasterSwitch) : ActivationOutput.None;

    private ActivationOutput Started(ActivationReason reason)
    {
        _running = true;
        return ActivationOutput.Start(reason);
    }

    private ActivationOutput Stopped(ActivationReason reason)
    {
        _running = false;
        return ActivationOutput.Stop(reason);
    }
}
