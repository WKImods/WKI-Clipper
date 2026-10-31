using System;
using System.Collections.Generic;
using System.Threading;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.Services.AutoClicker;

namespace WKI_Clipper.ClickerHost;

/// <summary>What the mouse patterns add to the host's state message: recording, the ids playing (comma-joined), the program that refused the moves.</summary>
internal sealed record PatternStatus(bool Recording, string? Playing, string? BlockedBy)
{
    public static readonly PatternStatus Idle = new(false, null, null);
}

/// <summary>
/// The mouse patterns' side of the host (mouse-patterns design 4.2, 4.4 - 4.6, 5.1), next to <see cref="HostCoordinator"/> and with its
/// own gate, so a playback never waits for the click engine and the clicker never waits for a playback.
/// <list type="bullet">
/// <item><see cref="Configure"/> turns the settings into the tracker's bindings: the record hotkey first, then every active pattern with
/// a hotkey; none while "Mausbewegung bereit" is off. A pattern that is no longer bound stops.</item>
/// <item>Each bound pattern has its own <see cref="ActivationStateMachine"/> (Hold/Toggle, no master); its Start/Stop become
/// <see cref="PatternPlayer"/> calls. A press does not count while locked, suppressed (a picker or hotkey field in the clipper),
/// recording or while the record hotkey is held; a mouse hotkey's start press over the clipper's windows and a program-bound pattern's
/// start while its program is not in front do not count either (a toggle's stop press does). A release always gets through. A pattern
/// without samples of its current revision never starts.</item>
/// <item>The record hotkey starts a recording (stopping every playback: no pattern moves the mouse into a recording) and stops it
/// again; the result goes to the clipper as a <see cref="PatternRecordedMessage"/>, as does the cancel by lock, suppression or disarm.
/// The recorder's 60 s limit stops it the same way.</item>
/// <item><see cref="Poll"/> (every 50 ms) stops everything while the task switcher is in front, and a program-bound pattern whose program
/// left the front. Nothing is read while nothing plays.</item>
/// <item>A move Windows refused ends every playback; the program in front is reported as <see cref="PatternStatus.BlockedBy"/> until the
/// next start.</item>
/// </list>
/// <see cref="StateChanged"/> is raised outside the gate whenever <see cref="Status"/> changed, which the host coordinator publishes.
/// </summary>
internal sealed class PatternCoordinator
{
    /// <summary>The binding id of the record hotkey (never a pattern id: those are 32 hex characters).</summary>
    public const string RecordBindingId = "record";

    /// <summary>A pattern's options as the last config had them; a playback keeps the ones it started with.</summary>
    private sealed record Options(ActivationMode Mode, ClickerHotkey Hotkey, bool Repeat, double Speed, PatternReplay Replay,
        string? ProgramKey, bool OnlyInProgram, int Revision);

    private sealed class Entry
    {
        public Entry(string id, Options options)
        {
            Id = id;
            Options = options;
        }

        public string Id { get; }
        public Options Options;
        public readonly ActivationStateMachine Machine = new();
    }

    /// <summary>Samples as the clipper sent them; <paramref name="Generation"/> = configs seen when they arrived.</summary>
    private sealed record Stored(int Revision, PatternData Data, int Generation);

    private readonly PatternPlayer _player;
    private readonly PatternRecorder _recorder;
    private readonly Func<ForegroundInfo> _foreground;
    private readonly Func<bool> _overClipper;
    private readonly Action<ClickerMessage> _send;
    private readonly object _gate = new();

    // ---- under _gate ----
    private Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);   // the bound patterns
    private readonly Dictionary<string, Stored> _data = new(StringComparer.OrdinalIgnoreCase);
    private int _generation;
    private bool _armed, _locked, _suppressed, _recording, _recordHeld, _recordHasMouse;
    private string? _blockedBy;
    private PatternStatus _status = PatternStatus.Idle;   // written under the gate, read anywhere

    /// <param name="foreground">The window in front; called on several threads, so it must be thread-safe (<see cref="WindowsHostEnvironment.ReadForeground"/>).</param>
    /// <param name="overClipper">Whether the cursor is over one of the clipper's windows (only for a mouse hotkey's press).</param>
    /// <param name="send">Queues a message for the clipper; must not block.</param>
    public PatternCoordinator(PatternPlayer player, PatternRecorder recorder, Func<ForegroundInfo> foreground, Func<bool> overClipper,
        Action<ClickerMessage> send)
    {
        _player = player;
        _recorder = recorder;
        _foreground = foreground;
        _overClipper = overClipper;
        _send = send;

        _player.Finished += OnFinished;
        _player.Blocked += OnBlocked;
        _recorder.LimitReached += OnLimitReached;
    }

    /// <summary>
    /// <see cref="Status"/> changed. Raised outside the coordinator's gate on whichever thread changed it (the hotkey worker, the pipe
    /// reader, the poll timer, the player or the recorder timer); the handler re-reads <see cref="Status"/>.
    /// </summary>
    public event Action? StateChanged;

    /// <summary>The latest status. Any thread, lock-free.</summary>
    public PatternStatus Status => Volatile.Read(ref _status);

    /// <summary>
    /// Applies the (normalized) settings and returns the bindings for the tracker: the record hotkey first, then every active pattern with
    /// a hotkey; none while the patterns are disarmed. Stops the playbacks of patterns that are no longer bound (deleted, deactivated,
    /// hotkey removed, disarmed) and cancels a recording when disarmed. A pattern that stays bound keeps playing; an edit applies to its
    /// next start.
    /// </summary>
    public IReadOnlyList<TrackedBinding> Configure(ClickerSettings settings)
    {
        var bindings = new List<TrackedBinding>();
        bool changed;
        lock (_gate)
        {
            _generation++;
            _armed = settings.MousePatternsArmed;
            if (!_armed) CancelRecording(notify: true);
            _recordHasMouse = false;
            if (_armed && settings.PatternRecordHotkey is { IsEmpty: false } record)
            {
                bindings.Add(new TrackedBinding(RecordBindingId, record.Clone()));
                _recordHasMouse = record.HasMouseInput;
            }

            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var next = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in settings.MousePatterns ?? new())
            {
                if (p is null || string.IsNullOrEmpty(p.Id) || !listed.Add(p.Id)) continue;
                if (!_armed || !p.Active || p.Hotkey is not { IsEmpty: false } hotkey) continue;
                var options = new Options(p.Mode, hotkey.Clone(), p.Repeat, p.Speed, p.Replay, AutoClickerBlacklist.Key(p.Program),
                    p.OnlyInProgram, p.Revision);
                if (_entries.TryGetValue(p.Id, out var entry)) entry.Options = options;
                else entry = new Entry(p.Id, options);
                entry.Machine.Configure(options.Mode, hasMaster: false, ActivationMode.Toggle);   // no master: never a command
                next[p.Id] = entry;
                bindings.Add(new TrackedBinding(p.Id, options.Hotkey));
            }
            foreach (var id in _entries.Keys)
                if (!next.ContainsKey(id)) _player.Stop(id);
            _entries = next;
            PruneData(listed);
            changed = UpdateStatus();
        }
        if (changed) RaiseStateChanged();
        return bindings;
    }

    // Samples of a pattern the config no longer lists are dropped - unless they arrived after the previous config: a new recording's
    // samples may overtake the config that names the new pattern.
    private void PruneData(HashSet<string> listed)
    {
        List<string>? stale = null;
        foreach (var (id, stored) in _data)
            if (!listed.Contains(id) && stored.Generation < _generation - 1) (stale ??= new()).Add(id);
        if (stale is not null) foreach (var id in stale) _data.Remove(id);
    }

    /// <summary>The samples of pattern <paramref name="id"/> for its <paramref name="revision"/> (replacing older ones). A playback keeps the data it started with.</summary>
    public void SetData(string id, int revision, PatternData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!MousePatternRules.IsValidId(id))
        {
            HostLog.Warn("Pattern samples with an invalid id ignored.");
            return;
        }
        lock (_gate) _data[id] = new Stored(revision, data, _generation);
    }

    /// <summary>A binding's match changed (the tracker's <see cref="HotkeyTracker.BindingChanged"/>, on the hotkey worker thread).</summary>
    public void OnBinding(string id, bool matched)
    {
        bool changed;
        lock (_gate)
        {
            if (string.Equals(id, RecordBindingId, StringComparison.Ordinal)) changed = OnRecordBinding(matched);
            else
            {
                if (!_entries.TryGetValue(id, out var entry)) return;
                // Only the press is filtered: a release always reaches the machine, so Hold still stops.
                if (matched && !PressCounts(entry)) return;
                changed = Apply(entry, entry.Machine.OnMatchChanged(matched));
            }
        }
        if (changed) RaiseStateChanged();
    }

    private bool PressCounts(Entry entry)
    {
        if (!_armed || _locked || _suppressed || _recording || _recordHeld) return false;
        // The clipper's windows and the program binding guard the start only; a toggle's stop press always counts (a path that runs
        // over the clipper must still be stoppable there).
        if (entry.Machine.IsRunning) return true;
        if (entry.Options.Hotkey.HasMouseInput && OverClipper()) return false;
        if (entry.Options is { OnlyInProgram: true, ProgramKey: { } program }
            && !string.Equals(program, AutoClickerBlacklist.Key(Foreground().Exe), StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>Carries out what a pattern's machine asked for (under the gate). true = the status changed.</summary>
    private bool Apply(Entry entry, ActivationOutput output)
    {
        switch (output.Command)
        {
            case ActivationCommand.Start:
                if (!_data.TryGetValue(entry.Id, out var stored) || stored.Revision != entry.Options.Revision)
                {
                    entry.Machine.OnStoppedByEngine();   // the machine assumed the start happened
                    HostLog.Info("Pattern " + entry.Id + " has no samples of its current recording (yet); it does not start.");
                    return false;
                }
                var o = entry.Options;
                _player.Play(new PatternPlayRequest(entry.Id, stored.Data, o.Speed, o.Repeat, o.Replay));
                _blockedBy = null;
                return UpdateStatus();
            case ActivationCommand.Stop:
                _player.Stop(entry.Id);
                return UpdateStatus();
        }
        return false;
    }

    // ---- recording ----

    // Under the gate. A pattern's press is ignored while the record hotkey is held, so a pattern on the same key never starts with it.
    private bool OnRecordBinding(bool matched)
    {
        _recordHeld = matched;
        if (!matched || !_armed || _locked || _suppressed) return false;
        if (!_recording && _recordHasMouse && OverClipper()) return false;   // only the start: the stop press ends it anywhere
        if (_recording) StopRecording();
        else StartRecording();
        return UpdateStatus();
    }

    private void StartRecording()
    {
        string? program = Foreground().ExeName;
        StopAll();   // nothing of ours moves the mouse during a recording, and pattern hotkeys are ignored until it ends
        _recorder.Start(program);
        _recording = _recorder.IsRecording;
    }

    private void StopRecording()
    {
        var result = _recorder.Stop();
        _recording = false;
        _send(Recorded(result, _recorder.Program));
    }

    /// <summary>Ends a recording without a result; <paramref name="notify"/> tells the clipper (lock, suppression, disarm).</summary>
    private void CancelRecording(bool notify)
    {
        if (!_recording) return;
        _recording = false;
        string? program = _recorder.Program;
        _recorder.Cancel();
        if (notify) _send(new PatternRecordedMessage(nameof(PatternRecordingOutcome.Cancelled), null, program, 0));
    }

    private static PatternRecordedMessage Recorded(PatternRecordingResult result, string? program)
    {
        if (result.Outcome is not (PatternRecordingOutcome.Ok or PatternRecordingOutcome.Cut) || result.Data is not { } data)
            return new PatternRecordedMessage(result.Outcome.ToString(), null, program, 0);
        try
        {
            return new PatternRecordedMessage(result.Outcome.ToString(), PatternCodec.Encode(data), program, data.DurationMs);
        }
        catch (ArgumentException ex)
        {
            HostLog.Error("Pattern recording: the samples could not be encoded; the recording is discarded", ex);
            return new PatternRecordedMessage(nameof(PatternRecordingOutcome.Cancelled), null, program, 0);
        }
    }

    // The recorder's timer thread, outside the recorder's lock.
    private void OnLimitReached()
    {
        bool changed;
        lock (_gate)
        {
            if (!_recording || !_recorder.IsRecording) return;   // stopped or cancelled meanwhile
            StopRecording();
            changed = UpdateStatus();
        }
        if (changed) RaiseStateChanged();
    }

    // ---- lock, suppression, poll ----

    /// <summary>The clipper locks (a blacklisted game runs): every playback and a recording end before this returns; presses are refused until <see cref="Unlock"/>.</summary>
    public void Lock()
    {
        bool changed;
        lock (_gate)
        {
            _locked = true;
            StopAll();
            CancelRecording(notify: true);
            changed = UpdateStatus();
        }
        if (changed) RaiseStateChanged();
    }

    public void Unlock()
    {
        lock (_gate) _locked = false;
    }

    /// <summary>A picker or a hotkey field opened (true) or closed in the clipper: while open, everything stops and presses are ignored.</summary>
    public void OnSuppression(bool active)
    {
        bool changed;
        lock (_gate)
        {
            _suppressed = active;
            if (active)
            {
                StopAll();
                CancelRecording(notify: true);
            }
            changed = UpdateStatus();
        }
        if (changed) RaiseStateChanged();
    }

    /// <summary>
    /// Every 50 ms: the task switcher in front stops every playback, a program-bound pattern stops when its program left the front.
    /// Reads nothing while nothing plays.
    /// </summary>
    public void Poll()
    {
        if (Status.Playing is null) return;
        var fg = Foreground();
        bool changed;
        lock (_gate)
        {
            if (StopRules.IsTaskSwitcher(fg.Class, fg.Exe)) StopAll();
            else
            {
                string? front = AutoClickerBlacklist.Key(fg.Exe);
                foreach (var entry in _entries.Values)
                {
                    if (entry.Options is not { OnlyInProgram: true, ProgramKey: { } program } || string.Equals(program, front, StringComparison.Ordinal)) continue;
                    if (!_player.IsPlaying(entry.Id)) continue;
                    _player.Stop(entry.Id);
                    entry.Machine.OnStoppedByEngine();
                }
            }
            changed = UpdateStatus();
        }
        if (changed) RaiseStateChanged();
    }

    /// <summary>The helper ends: every playback and a recording end, nothing is reported (the clipper is going away).</summary>
    public void Shutdown()
    {
        lock (_gate)
        {
            _armed = false;
            StopAll();
            CancelRecording(notify: false);
            UpdateStatus();
        }
    }

    // ---- player events (the player's thread, outside its lock) ----

    private void OnFinished(string id)
    {
        bool changed;
        lock (_gate)
        {
            if (_player.IsPlaying(id)) return;   // started again meanwhile: that playback is not over
            if (_entries.TryGetValue(id, out var entry)) entry.Machine.OnStoppedByEngine();
            changed = UpdateStatus();
        }
        if (changed) RaiseStateChanged();
    }

    // Raised after the Finished of every playback that ended. Cleared by the next start, not by a move that went through again.
    private void OnBlocked(bool blocked)
    {
        if (!blocked) return;
        string blockedBy = Foreground().ExeName ?? "?";
        bool changed;
        lock (_gate)
        {
            _blockedBy = blockedBy;
            changed = UpdateStatus();
        }
        if (changed) RaiseStateChanged();
    }

    // ---- helpers (under the gate unless said otherwise) ----

    /// <summary>Stops every playback; each machine forgets its run, so the next press starts again.</summary>
    private void StopAll()
    {
        _player.StopAll();
        foreach (var entry in _entries.Values) entry.Machine.OnStoppedByEngine();
    }

    private bool UpdateStatus()
    {
        var playing = _player.Playing;
        var next = new PatternStatus(_recording, playing.Count == 0 ? null : string.Join(",", playing), _blockedBy);
        if (next == _status) return false;
        Volatile.Write(ref _status, next);
        return true;
    }

    private ForegroundInfo Foreground()
    {
        try { return _foreground() ?? ForegroundInfo.None; }
        catch (Exception ex)
        {
            HostLog.Warn("Pattern coordinator: the foreground window could not be read: " + ex.Message);
            return ForegroundInfo.None;
        }
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

    // Outside the gate.
    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(); }
        catch (Exception ex) { HostLog.Error("Pattern state handler failed", ex); }
    }
}
