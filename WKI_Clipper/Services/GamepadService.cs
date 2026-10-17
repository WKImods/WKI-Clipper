using System;
using System.Collections.Generic;
using System.Threading;
using WKI_Clipper.Models;
using WKI_Clipper.Native;

namespace WKI_Clipper.Services;

/// <summary>One reading of a controller: XInput's packet number (moves when anything changed) and the buttons.</summary>
public readonly record struct PadSnapshot(uint Packet, ushort Buttons);

/// <summary>Where controller states come from. XInput in the app, a fake in the tests.</summary>
public interface IPadSource
{
    /// <summary>False when no XInput library could be loaded; the feature then stays off.</summary>
    bool IsAvailable { get; }

    /// <summary>True when a controller is connected in the slot (0..3); <paramref name="state"/> is then valid.</summary>
    bool TryRead(int slot, out PadSnapshot state);
}

/// <summary>XInputGetState via <see cref="XInput"/>. Read-only.</summary>
internal sealed class XInputPadSource : IPadSource
{
    private readonly XInput.GetStateDelegate? _getState;

    public XInputPadSource()
    {
        try
        {
            _getState = XInput.LoadGetState(out var library);
            if (_getState != null) Logger.Info($"Gamepad: XInput loaded ({library})");
            else Logger.Warn("Gamepad: no XInput library found - controller combos are not available");
        }
        catch (Exception ex)
        {
            _getState = null;
            Logger.Warn("Gamepad: XInput could not be loaded: " + ex.Message);
        }
    }

    public bool IsAvailable => _getState != null;

    public bool TryRead(int slot, out PadSnapshot state)
    {
        state = default;
        if (_getState is null) return false;
        if (_getState((uint)slot, out var raw) != XInput.ERROR_SUCCESS) return false;
        state = new PadSnapshot(raw.dwPacketNumber, raw.Gamepad.wButtons);
        return true;
    }
}

/// <summary>
/// Controller combos. Polls the XInput slots on its own background thread (not a DispatcherTimer: the UI
/// thread must not be what decides whether a clip button press is noticed), feeds the pure
/// <see cref="GamepadComboDetector"/> and raises <see cref="ActionTriggered"/> with the hotkey action name.
/// The app routes that through the same switch as the keyboard hotkeys.
///
/// Connected slots are read every 16 ms, empty slots are probed every 2 s (probing an empty slot is the
/// expensive XInput call). It only reads: no rumble, no input injection.
/// </summary>
public sealed class GamepadService : IDisposable
{
    public const int SlotCount = 4;
    public const int PollIntervalMs = 16;
    public const int ProbeIntervalMs = 2000;
    private const int MaxConsecutiveFailures = 600;   // ~10 s of every poll failing

    private sealed class Pad
    {
        public bool Connected;
        /// <summary>False until the first reading after connecting / after a gate: forces one processing pass.</summary>
        public bool HaveLast;
        public uint LastPacket;
        public ushort LastButtons;
        public bool Probed;
        public uint LastProbe;
    }

    private readonly SettingsService _settings;
    private readonly IPadSource _source;
    private readonly Func<uint> _clock;
    private readonly bool _followSettings;
    private readonly object _gate = new();
    private readonly GamepadComboDetector _detector = new();
    private readonly Pad[] _pads = new Pad[SlotCount];

    private Thread? _thread;
    private ManualResetEvent? _stop;
    private GamepadComboLearner? _learner;
    private Action<LearnOutcome>? _learnDone;
    private int _readErrors;
    private bool _disposed;
    // Settings saves during startup only refresh the config; the thread starts with the first ApplySettings
    // call, after the app has subscribed to ActionTriggered.
    private bool _activated;

    /// <summary>Raised on the poll thread with the action name (<see cref="HotkeyActions"/>). The app marshals it to the UI thread.</summary>
    public event Action<string>? ActionTriggered;

    /// <summary>Raised on the poll thread when a pad connected or disconnected, or the service stopped.</summary>
    public event Action? StatusChanged;

    public GamepadService(SettingsService settings)
        : this(settings, new XInputPadSource(), () => unchecked((uint)Environment.TickCount64), followSettings: true)
    {
    }

    // Test seam: a fake pad source and clock; the tests drive PollOnce themselves.
    internal GamepadService(SettingsService settings, IPadSource source, Func<uint> clock, bool followSettings)
    {
        _settings = settings;
        _source = source;
        _clock = clock;
        _followSettings = followSettings;
        for (int i = 0; i < SlotCount; i++) _pads[i] = new Pad();
        ReloadConfig();
        // Every settings save lands here (toggle, hold slider, a newly learned combo).
        if (_followSettings) _settings.SettingsChanged += OnSettingsChanged;
    }

    public bool IsAvailable => _source.IsAvailable;

    public bool IsRunning
    {
        get { lock (_gate) return _thread != null; }
    }

    /// <summary>Slots (0-based) that currently have a controller.</summary>
    public IReadOnlyList<int> ConnectedSlots
    {
        get
        {
            lock (_gate)
            {
                var list = new List<int>();
                for (int i = 0; i < SlotCount; i++)
                    if (_pads[i].Connected) list.Add(i);
                return list;
            }
        }
    }

    // ---- configuration / lifecycle ----

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (_disposed) return;
        try
        {
            if (_activated) ApplySettings();
            else ReloadConfig();
        }
        catch (Exception ex) { Logger.Error("Gamepad: applying settings failed", ex); }
    }

    /// <summary>Pushes hold time and combos from the settings into the detector.</summary>
    internal void ReloadConfig()
    {
        var g = _settings.Current.Gamepad;
        lock (_gate)
        {
            _detector.HoldMs = g?.HoldMs ?? GamepadComboDetector.DefaultHoldMs;
            _detector.SetBindings(GamepadButtons.Sanitize(g?.Bindings));
        }
    }

    /// <summary>Re-reads the settings and starts or stops the poll thread to match the enable switch.</summary>
    public void ApplySettings()
    {
        _activated = true;
        ReloadConfig();
        if (_settings.Current.Gamepad?.Enabled ?? true) Start();
        else Stop();
    }

    /// <summary>Starts the poll thread. Does nothing when already running or when XInput is not available.</summary>
    public void Start()
    {
        if (_disposed || !_source.IsAvailable) return;
        lock (_gate)
        {
            if (_thread != null) return;
            for (int i = 0; i < SlotCount; i++) _pads[i] = new Pad();
            _stop = new ManualResetEvent(false);
            var stop = _stop;
            _thread = new Thread(() => Run(stop)) { IsBackground = true, Name = "WKI_Clipper.Gamepad" };
            _thread.Start();
        }
    }

    /// <summary>Stops the poll thread and forgets all pads. An open learn attempt is reported as cancelled.</summary>
    public void Stop()
    {
        Thread? thread;
        ManualResetEvent? stop;
        lock (_gate)
        {
            thread = _thread;
            stop = _stop;
            _thread = null;
            _stop = null;
        }
        if (thread is null) return;

        stop?.Set();
        bool joined = true;
        // A subscriber that saves settings from the poll thread would land here: never wait for ourselves.
        if (thread != Thread.CurrentThread) joined = thread.Join(1000);
        if (joined) stop?.Dispose();
        Teardown();
    }

    private void Run(ManualResetEvent stop)
    {
        Logger.Info("Gamepad: polling started");
        int failures = 0;
        while (true)
        {
            try
            {
                if (stop.WaitOne(0)) break;
            }
            catch (ObjectDisposedException) { break; }

            try
            {
                PollOnce(_clock());
                failures = 0;
            }
            catch (Exception ex)
            {
                failures++;
                if (failures == 1) Logger.Error("Gamepad: poll failed", ex);
                if (failures >= MaxConsecutiveFailures)
                {
                    Logger.Error("Gamepad: giving up after repeated poll failures");
                    break;
                }
            }

            try
            {
                if (stop.WaitOne(PollIntervalMs)) break;
            }
            catch (ObjectDisposedException) { break; }
        }

        bool endedByItself;
        lock (_gate)
        {
            endedByItself = _thread == Thread.CurrentThread;
            if (endedByItself)
            {
                _thread = null;
                _stop = null;
            }
        }
        if (endedByItself) Teardown();   // otherwise Stop() does it
        Logger.Info("Gamepad: polling stopped");
    }

    private void Teardown()
    {
        bool hadPads = false;
        Action<LearnOutcome>? learnDone = null;
        lock (_gate)
        {
            for (int i = 0; i < SlotCount; i++)
            {
                if (_pads[i].Connected) hadPads = true;
                _pads[i] = new Pad();
            }
            _detector.Reset();
            if (_learner != null)
            {
                learnDone = _learnDone;
                _learner = null;
                _learnDone = null;
            }
        }
        if (hadPads) RaiseStatusChanged();
        if (learnDone != null) InvokeLearnDone(learnDone, new LearnOutcome(LearnStatus.Cancelled));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_followSettings) _settings.SettingsChanged -= OnSettingsChanged;
        Stop();
    }

    // ---- learn mode ----

    /// <summary>
    /// Listens for a new combo for <paramref name="action"/>. While it runs no combo fires. <paramref name="done"/>
    /// is called once, on the poll thread, with the outcome (also Cancelled). False when no pad is connected or
    /// an attempt is already running.
    /// </summary>
    public bool BeginLearn(string action, Action<LearnOutcome> done)
    {
        lock (_gate)
        {
            if (_learner != null) return false;
            bool any = false;
            for (int i = 0; i < SlotCount; i++) any |= _pads[i].Connected;
            if (!any) return false;

            var current = GamepadButtons.Sanitize(_settings.Current.Gamepad?.Bindings);
            _learner = new GamepadComboLearner(action, current, _clock());
            _learnDone = done;
            return true;
        }
    }

    /// <summary>Aborts a running learn attempt (reported as Cancelled).</summary>
    public void CancelLearn()
    {
        Action<LearnOutcome>? done;
        lock (_gate)
        {
            if (_learner is null) return;
            done = _learnDone;
            _learner = null;
            _learnDone = null;
            GateAllPadsLocked();
        }
        if (done != null) InvokeLearnDone(done, new LearnOutcome(LearnStatus.Cancelled));
    }

    /// <summary>
    /// Buttons that are still held when learning ends (or is cancelled) must not fire an action: every pad has
    /// to be released once first.
    /// </summary>
    private void GateAllPadsLocked()
    {
        for (int i = 0; i < SlotCount; i++)
        {
            if (!_pads[i].Connected) continue;
            _detector.RequireRelease(i);
            _pads[i].HaveLast = false;   // an idle pad must still be fed once so the gate can clear
        }
    }

    // ---- polling ----

    /// <summary>One pass over all slots. The poll thread calls it every 16 ms; the tests call it with a fake clock.</summary>
    internal void PollOnce(uint now)
    {
        string? fired = null;
        int firedSlot = -1;
        bool statusChanged = false;
        Action<LearnOutcome>? learnDone = null;
        LearnOutcome learnOutcome = default;

        lock (_gate)
        {
            if (_learner != null)
            {
                var o = _learner.Tick(now);
                if (o.IsFinal) EndLearnLocked(o, out learnDone, out learnOutcome);
            }

            for (int slot = 0; slot < SlotCount; slot++)
            {
                var pad = _pads[slot];

                // Empty slots are only probed every ProbeIntervalMs: that is the expensive XInput call.
                if (!pad.Connected)
                {
                    if (pad.Probed && GamepadComboDetector.Elapsed(now, pad.LastProbe) < ProbeIntervalMs) continue;
                    pad.Probed = true;
                    pad.LastProbe = now;
                }

                if (!TryReadSafe(slot, out var state))
                {
                    if (pad.Connected)
                    {
                        pad.Connected = false;
                        pad.HaveLast = false;
                        pad.Probed = true;
                        pad.LastProbe = now;
                        _detector.ResetSlot(slot);
                        statusChanged = true;
                        Logger.Info($"Gamepad disconnected: XInput slot {slot + 1}");
                    }
                    continue;
                }

                if (!pad.Connected)
                {
                    pad.Connected = true;
                    pad.HaveLast = false;
                    statusChanged = true;
                    Logger.Info($"Gamepad connected: XInput slot {slot + 1}");
                }

                ushort buttons = (ushort)(state.Buttons & GamepadButtons.ValidMask);

                // Nothing changed and nothing is held: the detector has nothing to count, skip the work. A held
                // button is always processed - its hold time keeps running while the packet number stands still.
                if (pad.HaveLast && state.Packet == pad.LastPacket && buttons == pad.LastButtons && buttons == 0)
                    continue;
                pad.HaveLast = true;
                pad.LastPacket = state.Packet;
                pad.LastButtons = buttons;

                if (_learner != null)
                {
                    var o = _learner.Feed(now, slot, buttons);
                    if (o.IsFinal) EndLearnLocked(o, out learnDone, out learnOutcome);
                }
                else
                {
                    var action = _detector.Update(now, slot, buttons);
                    if (action != null && fired == null)
                    {
                        fired = action;
                        firedSlot = slot;
                    }
                }
            }
        }

        // Everything below runs outside the lock: subscribers are free to call back into the service.
        if (statusChanged) RaiseStatusChanged();
        if (learnDone != null) InvokeLearnDone(learnDone, learnOutcome);
        if (fired != null)
        {
            Logger.Info($"Gamepad combo fired: {fired} (XInput slot {firedSlot + 1})");
            RaiseActionTriggered(fired);
        }
    }

    private void EndLearnLocked(LearnOutcome outcome, out Action<LearnOutcome>? done, out LearnOutcome result)
    {
        done = _learnDone;
        result = outcome;
        _learner = null;
        _learnDone = null;
        GateAllPadsLocked();
    }

    private bool TryReadSafe(int slot, out PadSnapshot state)
    {
        try
        {
            return _source.TryRead(slot, out state);
        }
        catch (Exception ex)
        {
            state = default;
            // A broken driver call counts as "no pad"; log it once, then every 1000th time.
            if (_readErrors++ % 1000 == 0) Logger.Error("Gamepad: reading a slot failed", ex);
            return false;
        }
    }

    // ---- events (one throwing subscriber must not take the poll thread or the others down) ----

    private void RaiseActionTriggered(string action)
    {
        var handlers = ActionTriggered;
        if (handlers is null) return;
        foreach (var h in handlers.GetInvocationList())
        {
            try { ((Action<string>)h)(action); }
            catch (Exception ex) { Logger.Error("Gamepad: ActionTriggered subscriber threw", ex); }
        }
    }

    private void RaiseStatusChanged()
    {
        var handlers = StatusChanged;
        if (handlers is null) return;
        foreach (var h in handlers.GetInvocationList())
        {
            try { ((Action)h)(); }
            catch (Exception ex) { Logger.Error("Gamepad: StatusChanged subscriber threw", ex); }
        }
    }

    private static void InvokeLearnDone(Action<LearnOutcome> done, LearnOutcome outcome)
    {
        try { done(outcome); }
        catch (Exception ex) { Logger.Error("Gamepad: learn callback threw", ex); }
    }
}

/// <summary>The texts of the controller section, kept pure so they can be tested.</summary>
public static class GamepadStatusText
{
    /// <summary>The status line: off / unavailable / no controller / connected (slot numbers are 1-based).</summary>
    public static string Format(bool enabled, bool available, IReadOnlyList<int> connectedSlots)
    {
        if (!enabled) return L.T("Controller-Kombis sind aus", "Controller combos are off");
        if (!available)
            return L.T("Controller-Erkennung nicht verfügbar (XInput fehlt)", "Controller detection unavailable (XInput missing)");
        if (connectedSlots.Count == 0) return L.T("Kein Controller", "No controller");

        var numbers = new List<string>();
        foreach (var s in connectedSlots) numbers.Add((s + 1).ToString());
        var list = string.Join(", ", numbers);
        return connectedSlots.Count == 1
            ? L.T($"Xbox-Controller verbunden (Slot {list})", $"Xbox controller connected (slot {list})")
            : L.T($"Xbox-Controller verbunden (Slots {list})", $"Xbox controllers connected (slots {list})");
    }

    /// <summary>One line for the result of a learn attempt; empty for Pending and Cancelled.</summary>
    public static string DescribeLearn(LearnOutcome outcome, Func<string, string> actionLabel)
    {
        switch (outcome.Status)
        {
            case LearnStatus.Captured:
                var combo = GamepadButtons.Describe(outcome.Mask);
                return L.T($"Gespeichert: {combo}", $"Saved: {combo}");
            case LearnStatus.TooFewButtons:
                return L.T("Mindestens 2 Tasten gleichzeitig halten.", "Hold at least 2 buttons at the same time.");
            case LearnStatus.AlreadyUsed:
                var label = actionLabel(outcome.UsedBy ?? "");
                return L.T($"Schon belegt durch \"{label}\".", $"Already used by \"{label}\".");
            case LearnStatus.TimedOut:
                return L.T("Nichts erkannt - bitte erneut versuchen.", "Nothing detected - please try again.");
            default:
                return "";
        }
    }
}
