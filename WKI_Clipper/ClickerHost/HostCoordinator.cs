using System;
using System.Threading;
using WKI_Clipper.Models;
using WKI_Clipper.Services.AutoClicker;

namespace WKI_Clipper.ClickerHost;

/// <summary>
/// The host's brain between the pipe, the hotkey tracker, the start-zone monitor and the engine (design sections 4 to 7): messages
/// from the clipper and hotkey changes go through the <see cref="ActivationStateMachine"/>, whose Start/Stop become engine calls;
/// the engine's own events (state, run ended) flow back to the clipper. Everything is serialized by one lock. Hotkey changes arrive
/// on the hotkey worker thread, never on the hook thread itself (see <see cref="HotkeyHook"/>).
/// </summary>
internal sealed class HostCoordinator
{
    private readonly IClickEngine _engine;
    private readonly HotkeyTracker _tracker;
    private readonly ZoneTracker _zones;
    private readonly Func<PixelPoint> _cursor;
    private readonly Action<ClickerMessage> _send;
    private readonly Func<bool> _overClipper;
    private readonly ActivationStateMachine _machine = new();
    private readonly object _gate = new();

    // Set while no run of ours is waiting for its run-ended message to be queued: Reset by a successful start (under the gate), Set by
    // OnRunEnded after the send. OnRunEnded takes the gate first, so a run's Set can never come before its Reset.
    private readonly ManualResetEventSlim _runEndedQueued = new(initialState: true);

    private ActivationMode _mode = ActivationMode.Toggle;
    private string? _lastReason;
    private StateMessage? _lastSent;

    // A hotkey with a mouse button or a wheel direction (a bare left button too) does not fire over the clipper's own windows: a
    // shift-click into a list or scrolling a page there is no start.
    private volatile bool _hotkeyHasMouse, _masterHasMouse;

    /// <param name="overClipper">Whether the cursor is over one of the clipper's windows right now (called on the hotkey worker thread, only for a mouse hotkey's press).</param>
    public HostCoordinator(IClickEngine engine, HotkeyTracker tracker, ZoneTracker zones, Func<PixelPoint> cursor, Action<ClickerMessage> send,
        Func<bool>? overClipper = null)
    {
        _engine = engine;
        _tracker = tracker;
        _zones = zones;
        _cursor = cursor;
        _send = send;
        _overClipper = overClipper ?? (() => false);

        _tracker.HotkeyChanged = OnHotkeyChanged;
        _tracker.MasterChanged = OnMasterChanged;
        _engine.StateChanged += _ => PublishState();
        _engine.RunEnded += OnRunEnded;
    }

    /// <summary>A message from the clipper (called on the pipe reader's thread). The version check of hello and shutdown are the server's business.</summary>
    public void Handle(ClickerMessage message)
    {
        switch (message)
        {
            case HelloMessage: PublishState(force: true); break;   // the clipper is ready: tell it where we stand
            case ConfigMessage c: ApplyConfig(c.Settings); break;
            case LockMessage l: Lock(l.Reason); break;
            case UnlockMessage: Unlock(); break;
            case StartMessage: StartFromClipper(); break;
            case StopMessage: StopFromClipper(); break;
            case PickerMessage p: Picker(p.Active); break;
        }
    }

    private void ApplyConfig(ClickerSettings raw)
    {
        var settings = ClickerSettingsRules.Normalize(raw);
        lock (_gate)
        {
            _mode = settings.ActivationMode;
            _hotkeyHasMouse = settings.Hotkey?.HasMouseInput == true;
            _masterHasMouse = settings.MasterHotkey?.HasMouseInput == true;
            _engine.Configure(settings);
            _zones.Configure(settings.ZonesEnabled, settings.Zones);
            Apply(_machine.Configure(settings.ActivationMode, settings.MasterHotkey is not null, settings.MasterMode));
        }
        // After the lock: the tracker raises its events on this thread and they take the lock themselves.
        _tracker.Configure(settings.Hotkey, settings.MasterHotkey, settings.StrictModifiers);
        PublishState();
    }

    private void Lock(string reason)
    {
        lock (_gate)
        {
            HostLog.Info("Locked by the clipper: " + reason);
            _engine.SetLocked(true);
            Apply(_machine.OnLock());
        }
        PublishState();
    }

    private void Unlock()
    {
        lock (_gate)
        {
            HostLog.Info("Unlocked by the clipper.");
            _engine.SetLocked(false);
            _machine.OnUnlock();
        }
        PublishState();
    }

    private void Picker(bool active)
    {
        lock (_gate) Apply(_machine.OnPicker(active));
        PublishState();
    }

    private void StartFromClipper()
    {
        lock (_gate) Apply(_machine.OnStartRequested(ActivationReason.None));
    }

    private void StopFromClipper()
    {
        lock (_gate)
        {
            if (_engine.IsRunning) _engine.Stop(StopReason.Manual);
        }
    }

    // ---- hotkeys ----

    private void OnHotkeyChanged(bool matched)
    {
        // Only the press is ignored: a release always gets through, so Hold mode still stops (one without a press changes nothing).
        if (matched && _hotkeyHasMouse && OverClipper()) return;
        lock (_gate) Apply(_machine.OnMatchChanged(matched));
    }

    private void OnMasterChanged(bool matched)
    {
        if (matched && _masterHasMouse && OverClipper()) return;
        lock (_gate) Apply(_machine.OnMasterChanged(matched));
        PublishState();
    }

    private bool OverClipper()
    {
        try { return _overClipper(); }
        catch (Exception ex)
        {
            HostLog.Warn("Cursor-over-clipper check failed: " + ex.Message);
            return false;
        }
    }

    // ---- start-zone monitor (the host calls this every 100 ms) ----

    public void PollStartZone()
    {
        lock (_gate)
        {
            bool canStart = !_engine.IsRunning && !_machine.IsLocked && !_machine.IsPickerOpen && _machine.MasterAllowed;
            switch (_zones.PollStart(_cursor(), _engine.IsRunning, canStart))
            {
                case StartZoneAction.Start:
                    var output = _machine.OnStartRequested(ActivationReason.StartZone);
                    if (output.Command != ActivationCommand.Start) _zones.OnStartRefused();
                    Apply(output);
                    break;
                case StartZoneAction.Stop:
                    _engine.Stop(StopReason.LeftStartZone);
                    break;
            }
        }
    }

    // ---- engine side ----

    /// <summary>Carries out what the state machine asked for (caller holds the gate).</summary>
    private void Apply(ActivationOutput output)
    {
        switch (output.Command)
        {
            case ActivationCommand.Start:
                var status = _engine.Start(new EngineStart(WaitForHotkeyRelease: output.Reason == ActivationReason.Hotkey && _mode == ActivationMode.Toggle));
                if (status == EngineStartStatus.Started) _runEndedQueued.Reset();
                else
                {
                    // The machine assumed the start happened.
                    _machine.OnStoppedByEngine();
                    if (output.Reason == ActivationReason.StartZone) _zones.OnStartRefused();
                    HostLog.Warn("Start refused: " + status);
                }
                break;
            case ActivationCommand.Stop:
                _engine.Stop(StopReasonText.From(output.Reason));
                break;
        }
    }

    private void OnRunEnded(RunSummary summary)
    {
        lock (_gate)
        {
            _machine.OnStoppedByEngine();
            _zones.OnRunEnded();
            _lastReason = summary.Reason.ToString();
        }
        _send(new RunEndedMessage(summary.StartUtc, summary.Seconds, summary.Clicks, summary.Reason.ToString(), summary.Detail));
        _runEndedQueued.Set();
        PublishState();
    }

    private void PublishState(bool force = false)
    {
        lock (_gate)
        {
            var snap = _engine.Snapshot;
            var state = new StateMessage(snap.Running, Armed: true, _machine.IsLocked, snap.Running ? null : _lastReason,
                snap.Clicks, snap.Cps, snap.ElapsedMs, snap.BlockedByElevated, _machine.MasterAllowed);
            if (!force && !snap.Running && state == _lastSent) return;
            _lastSent = state;
            // Inside the gate so the outbox order equals the order the snapshots were read in (a stale "running" must never overtake
            // the stopped state); Send is a non-blocking queue write.
            _send(state);
        }
    }

    /// <summary>The clipper is going away (or the pipe broke): ends a run and waits briefly for its run-ended message to be queued.</summary>
    public void Shutdown(TimeSpan wait)
    {
        _engine.SetLocked(true);
        _engine.Stop(StopReason.ClipperClosed);
        _runEndedQueued.Wait(wait);
    }
}
