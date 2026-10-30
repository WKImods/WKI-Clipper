using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>One helper process the controller started: its end of the pipe and the process itself. Disposing releases both.</summary>
public interface IClickerHostSession : IDisposable
{
    /// <summary>Waits until the helper connected to the pipe and returns the pipe.</summary>
    Task<Stream> ConnectAsync(CancellationToken ct);

    bool HasExited { get; }

    /// <summary>The process ended (any thread). Not raised for a session whose process never started.</summary>
    event Action? Exited;

    /// <summary>true when the process is gone (now or within <paramref name="milliseconds"/>).</summary>
    bool WaitForExit(int milliseconds);

    /// <summary>Ends the process at once. Never throws.</summary>
    void Kill();
}

/// <summary>Creates the pipe server for <c>pipeName</c> and starts <c>WKI_Clipper.exe --clicker-host</c> against it. The seam of the controller tests.</summary>
public interface IClickerHostLauncher
{
    IClickerHostSession Launch(string pipeName, int parentPid);

    /// <summary>Starts <c>WKI_Clipper.exe --clicker-release</c>: lets go of what a killed helper may have held. May throw.</summary>
    void LaunchRelease(ClickerRelease release);
}

/// <summary>What the guard needs from the native clicker: whether its helper is wanted, and the lock for a blacklisted program.</summary>
public interface IClickerLock
{
    /// <summary>
    /// The helper process is wanted: "Clicker bereit" or "Mausbewegung bereit" is on (<see cref="MousePatternRules.HostWanted"/>), so
    /// it runs or is about to. The lock stops the clicker and the mouse patterns alike.
    /// </summary>
    bool Armed { get; }

    /// <summary>A blacklisted program runs: a running clicker stops and starts are refused until <see cref="Unlock"/>. Idempotent.</summary>
    void Lock(BlacklistEntry entry);

    void Unlock();
}

/// <summary>Where the controller stands, as the status row shows it. The phase is the helper's: it also runs for the mouse patterns alone.</summary>
public enum ClickerPhase
{
    /// <summary>"Clicker bereit" and "Mausbewegung bereit" are off; no helper process.</summary>
    Off,
    /// <summary>The helper process is starting or restarting.</summary>
    Starting,
    /// <summary>The helper runs and waits for the hotkey or Start.</summary>
    Ready,
    Running,
    /// <summary>The helper could not be kept alive; see <see cref="ClickerState.Fault"/>.</summary>
    Error
}

public enum ClickerFault { None, StartFailed, HandshakeFailed, Crashed, LockNotConfirmed }

/// <summary>
/// What the UI mirrors. <see cref="LastStop"/> is the reason of the last run's end while no run is going (null before the first run);
/// the text is made at display time (<see cref="ClickerStateText"/>), so a language switch needs no new state. <see cref="MasterAllowed"/>
/// is false while a master hotkey is set and its switch is off: every hotkey press and Start is refused then.
/// The mouse patterns (mouse-patterns design 4.6) add <see cref="PatternsArmed"/> ("Mausbewegung bereit") and, copied from the helper,
/// <see cref="Recording"/>, <see cref="Playing"/> (the ids of the patterns playing, comma-joined; null when none) and
/// <see cref="PatternsBlockedBy"/> (exe file name of the elevated window that refused the pattern moves).
/// </summary>
public sealed record ClickerState(ClickerPhase Phase, bool Locked, string? LockedBy, long Clicks, double Cps, long ElapsedMs,
    string? BlockedByElevated, StopReason? LastStop, string? LastStopDetail, ClickerFault Fault, bool MasterAllowed = true,
    bool PatternsArmed = false, bool Recording = false, string? Playing = null, string? PatternsBlockedBy = null)
{
    public static readonly ClickerState Off = new(ClickerPhase.Off, false, null, 0, 0, 0, null, null, null, ClickerFault.None);

    public bool IsRunning => Phase == ClickerPhase.Running;
}

/// <summary>A run that ended, as the controller saw it. <paramref name="LockedBy"/> is the display name of the lock holder at that moment.</summary>
public sealed record ClickerRunInfo(RunRecord Record, StopReason Reason, string? Detail, string? LockedBy);

/// <summary>
/// A pattern recording ended, as the clipper took it in. <paramref name="Outcome"/> is a <see cref="PatternRecordingOutcome"/> name
/// (Ok, Cut, Empty, Cancelled) or one of the clipper's own <see cref="Full"/> and <see cref="Failed"/>; only Ok and Cut saved a pattern.
/// <paramref name="Name"/> is the saved pattern's name, <paramref name="Seconds"/> the length of its motion.
/// </summary>
public sealed record PatternRecordedInfo(string Outcome, string? Name, double Seconds)
{
    /// <summary>A new recording and no target, but the list already holds <see cref="MousePatternRules.MaxPatterns"/> patterns.</summary>
    public const string Full = "Full";

    /// <summary>The samples were unreadable or could not be written: nothing was saved.</summary>
    public const string Failed = "Failed";
}

/// <summary>The waiting times of the controller. The timers run on the controller's <see cref="TimeProvider"/>, so tests drive them by hand.</summary>
public sealed record ClickerControllerTiming(TimeSpan HandshakeTimeout, TimeSpan ConfigDebounce, TimeSpan ShutdownWait, TimeSpan LockConfirm)
{
    public static readonly ClickerControllerTiming Default = new(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
}

/// <summary>
/// The clipper's side of the AutoClicker (design section 7). While "Clicker bereit" (<see cref="ClickerSettings.Armed"/>) or
/// "Mausbewegung bereit" (<see cref="ClickerSettings.MousePatternsArmed"/>) is on - <see cref="MousePatternRules.HostWanted"/> - it
/// keeps one helper process alive: creates the pipe, starts the process, does the hello handshake, pushes the settings (debounced
/// 200 ms), mirrors the helper's state, writes every finished run into the statistics and tells the helper about the lock (blacklist)
/// and the pickers. An unexpected end of the helper is answered with one restart, then <see cref="ClickerPhase.Error"/> until the
/// switch is turned off and on again (<see cref="Retry"/>). A helper that does not confirm a lock within a second is killed; its
/// replacement starts locked.
/// <para>
/// Mouse patterns (mouse-patterns design 5.2): the samples live in <see cref="IMousePatternStore"/> and travel in their own messages,
/// never inside the config: after the handshake's config one <see cref="PatternDataMessage"/> per pattern whose samples load, later
/// the samples of every pattern whose <see cref="MousePattern.Revision"/> differs from the one this helper last got, right after the
/// config that names it. A finished recording (<see cref="PatternRecordedMessage"/>) becomes a new pattern or replaces the motion of
/// <see cref="RecordTarget"/>: samples written first, then the settings saved. A helper that ends while it records leaves nothing
/// behind but a "Cancelled" <see cref="PatternRecorded"/>.
/// </para>
///
/// Threads: everything may be called from any thread unless said otherwise; <see cref="StateChanged"/>, <see cref="RunFinished"/>
/// and <see cref="ArmedChanged"/> are raised on pool threads (the view marshals itself), <see cref="PatternRecorded"/> on the settings'
/// thread. Nothing here blocks on the pipe except <see cref="Dispose"/>, which waits up to the shutdown time for the helper to say goodbye.
/// </summary>
public sealed class ClickerController : IClickerLock, IDisposable
{
    private const int MaxLockKills = 3;

    /// <summary>Samples ready to go to the helper (<paramref name="Text"/> is <see cref="PatternCodec"/> text) for one revision of a pattern.</summary>
    private sealed record PatternText(int Revision, string Text);

    private readonly Func<ClickerSettings> _settings;
    private readonly Action _save;
    private readonly IClickerHostLauncher _launcher;
    private readonly IMousePatternStore _patternStore;
    private readonly Action<Action> _post;
    private readonly ClickerControllerTiming _timing;
    private readonly TimeProvider _time;
    private readonly int _parentPid;

    private readonly object _gate = new();
    private readonly object _publishGate = new();
    private readonly ITimer _debounce;
    private readonly ITimer _lockTimer;
    private readonly List<RunRecord> _pendingRuns = new();
    private readonly List<PatternRecordedMessage> _pendingRecordings = new();

    // Samples loaded ahead (handshake) or taken from a recording, until they went to a helper (under the gate). Keyed by pattern id.
    private readonly Dictionary<string, PatternText> _patternText = new(StringComparer.OrdinalIgnoreCase);
    private volatile string? _recordTarget;

    private Session? _session;
    private int _nextSessionId;
    private int _followQueued;
    private bool _initialized, _disposed, _disposing;
    private bool _armedSeen;
    private volatile bool _aborted;
    private BlacklistEntry? _lockedBy;
    private int _lockGeneration, _armedLockGeneration;
    private int _lockKills;
    private bool _picker, _hotkeyCapture;
    private int _restartsLeft = 1;
    private ClickerFault _fault;
    private StateMessage? _hostState;
    private RunEndedMessage? _lastRunEnded;
    private string? _lastPushed;
    private ClickerState _state = ClickerState.Off;

    /// <param name="settings">The live section (read on every use, so edits apply at once).</param>
    /// <param name="save">Saves the settings (after a run was added to the statistics, after a recording or a deleted pattern).</param>
    /// <param name="patternStore">Where the samples of the mouse patterns live.</param>
    /// <param name="post">Runs a change of the settings on the thread that owns them (the UI thread); tests pass a direct call.</param>
    /// <param name="time">Clock of the handshake, debounce, lock and goodbye timers (tests pass a manual one). Only <see cref="Dispose"/> waits in real time.</param>
    public ClickerController(Func<ClickerSettings> settings, Action save, IClickerHostLauncher launcher, IMousePatternStore patternStore,
        Action<Action>? post = null, ClickerControllerTiming? timing = null, int? parentPid = null, TimeProvider? time = null)
    {
        _settings = settings;
        _save = save;
        _launcher = launcher;
        _patternStore = patternStore;
        _post = post ?? (work => work());
        _timing = timing ?? ClickerControllerTiming.Default;
        _time = time ?? TimeProvider.System;
        _parentPid = parentPid ?? Environment.ProcessId;
        _debounce = _time.CreateTimer(_ => PushConfigNow(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _lockTimer = _time.CreateTimer(OnLockTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    // ---------------- observable surface ----------------

    /// <summary>The latest state (a snapshot; never null).</summary>
    public ClickerState State => Volatile.Read(ref _state);

    /// <summary>Raised when <see cref="State"/> changed (pool thread, in the order the states were made).</summary>
    public event Action<ClickerState>? StateChanged;

    /// <summary>Raised for every run the helper reports, after it went into the statistics.</summary>
    public event Action<ClickerRunInfo>? RunFinished;

    /// <summary>
    /// A pattern recording ended (settings' thread): raised after an Ok/Cut result was saved, for a result that saved nothing, and with
    /// "Cancelled" when the helper ended during a recording.
    /// </summary>
    public event Action<PatternRecordedInfo>? PatternRecorded;

    /// <summary><see cref="Armed"/> flipped: "Clicker bereit" or "Mausbewegung bereit" was switched so that the helper is wanted now, or no longer (pool thread).</summary>
    public event Action? ArmedChanged;

    /// <summary>The helper is wanted: "Clicker bereit" or "Mausbewegung bereit" is on (<see cref="MousePatternRules.HostWanted"/>).</summary>
    public bool Armed => MousePatternRules.HostWanted(_settings());

    /// <summary>The pattern the next recording replaces ("Neu aufnehmen"); null = the next recording becomes a new pattern. Not saved.</summary>
    public string? RecordTarget => _recordTarget;

    /// <summary>Marks the pattern the next recording replaces, or clears the mark (null).</summary>
    public void SetRecordTarget(string? patternId) => _recordTarget = string.IsNullOrWhiteSpace(patternId) ? null : patternId;

    /// <summary>
    /// Removes a pattern: out of the settings (saved, so the helper stops it with the next config), then its samples file. Call on the
    /// settings' thread (the UI thread).
    /// </summary>
    public void DeletePattern(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        var patterns = _settings().MousePatterns;
        int removed = patterns?.RemoveAll(p => p is not null && string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? 0;
        if (string.Equals(_recordTarget, id, StringComparison.OrdinalIgnoreCase)) _recordTarget = null;
        lock (_gate) _patternText.Remove(id);
        if (removed > 0) _save();
        // After the save: a crash in between leaves an unused file behind, never a pattern without samples.
        _patternStore.Delete(id);
    }

    // ---------------- lifecycle ----------------

    /// <summary>The app is up: from now on the controller follows <see cref="Armed"/>. Starts the helper when it is already wanted.</summary>
    public void Initialize()
    {
        lock (_gate)
        {
            if (_disposed || _initialized) return;
            _initialized = true;
        }
        FollowSettings();
    }

    /// <summary>
    /// The settings were saved (any thread): follow the switch, push a changed configuration after the debounce. The work runs on the
    /// pool - starting the helper is a process start, which must not hold the UI thread that saved - and calls that arrive meanwhile
    /// share one run.
    /// </summary>
    public void OnSettingsChanged()
    {
        if (Interlocked.Exchange(ref _followQueued, 1) == 1) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Volatile.Write(ref _followQueued, 0);
            FollowSettings();
        });
    }

    private void FollowSettings()
    {
        bool armedChanged;
        lock (_gate)
        {
            if (_disposed || !_initialized) return;
            armedChanged = ReconcileLocked();
            if (_session is { Ready: true }) ArmDebounceLocked();
        }
        PublishState();
        if (armedChanged) RaiseArmedChanged();
    }

    /// <summary>Starts a helper again after an error, without switching it off and on.</summary>
    public void Retry()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _fault = ClickerFault.None;
            _restartsLeft = 1;
            _lockKills = 0;
        }
        OnSettingsChanged();
    }

    /// <summary>Starts the clicker like a press of the hotkey would. False when the helper is not up (the Start button is disabled then).</summary>
    public bool Start() => SendIfReady(new StartMessage());

    /// <summary>Stops a running clicker. False when the helper is not up.</summary>
    public bool Stop() => SendIfReady(new StopMessage());

    /// <summary>
    /// The clipper is crashing: the kill-on-close job would end the helper without a release. Ends it now and lets go of what it may
    /// hold. Best effort and without the gate (the crashing thread may hold it). A hard end of the clipper (killed from outside) still
    /// leaves the job's kill without a release - the one gap left by design (the job guarantees the helper never outlives the clipper).
    /// </summary>
    public void Abort()
    {
        _aborted = true;   // no new helper in a dying process
        if (Volatile.Read(ref _session) is not { } s) return;
        s.Deliberate = true;
        KillHost(s);
    }

    /// <summary>A picker window opened or closed: hotkeys and zones are ignored meanwhile and a running clicker stops.</summary>
    public void SetPicker(bool open) => SetSuppression(ref _picker, open);

    /// <summary>
    /// A hotkey capture box started or ended recording: like a picker, hotkeys and zones are ignored meanwhile and a running clicker
    /// stops - pressing the current hotkey to record it again must not start clicks into the clipper's own window.
    /// </summary>
    public void SetHotkeyCapture(bool active) => SetSuppression(ref _hotkeyCapture, active);

    /// <summary>The helper knows one "picker" switch; it is on while a picker is open or a hotkey is being recorded (either one).</summary>
    private void SetSuppression(ref bool flag, bool value)
    {
        lock (_gate)
        {
            bool before = _picker || _hotkeyCapture;
            flag = value;
            bool after = _picker || _hotkeyCapture;
            if (before != after && _session is { Ready: true } s) Enqueue(s, new PickerMessage(after));
        }
    }

    public void Lock(BlacklistEntry entry)
    {
        lock (_gate)
        {
            if (_disposed) return;
            bool first = _lockedBy is null;
            _lockedBy = new BlacklistEntry { ExeName = entry.ExeName, DisplayName = entry.DisplayName };
            if (first && _session is { Ready: true } s)
            {
                Logger.Info($"AutoClicker: locked by '{entry.ExeName}'.");
                Enqueue(s, new LockMessage(entry.ExeName));
                ArmLockTimerLocked();
            }
        }
        PublishState();
    }

    public void Unlock()
    {
        lock (_gate)
        {
            if (_lockedBy is null) return;
            _lockedBy = null;
            CancelLockTimerLocked();
            _lockKills = 0;
            Logger.Info("AutoClicker: unlocked.");
            if (_session is { Ready: true } s) Enqueue(s, new UnlockMessage());
        }
        PublishState();
    }

    public void Dispose()
    {
        Session? s;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _disposing = true;
            s = _session;
            if (s is not null) RetireLocked(s, kill: false);
            _debounce.Dispose();
            _lockTimer.Dispose();
        }

        if (s is not null)
        {
            // The helper stops its run and reports it on the way out; wait for that, then make sure it is gone.
            if (!s.Run.Wait(_timing.ShutdownWait))
            {
                try { s.Cts.Cancel(); } catch (ObjectDisposedException) { }
                // The session's own end kills it too, but that may still be on its way: the clipper must not leave without it.
                if (!s.Run.Wait(TimeSpan.FromMilliseconds(500))) KillHost(s);
            }
        }

        RunRecord[] pending;
        PatternRecordedMessage[] recordings;
        lock (_gate)
        {
            pending = _pendingRuns.ToArray();
            _pendingRuns.Clear();
            recordings = _pendingRecordings.ToArray();
            _pendingRecordings.Clear();
            _disposing = false;
        }
        if (pending.Length > 0) ApplyRuns(pending);
        foreach (var recording in recordings) ApplyRecording(recording);
        if (_launcher is IDisposable launcher) launcher.Dispose();
    }

    // ---------------- reconcile ----------------

    /// <summary>
    /// Brings the helper in line with the switches (caller holds the gate): starts it when it should run and does not, shuts it down
    /// when it runs and should not. Returns true when <see cref="Armed"/> flipped since the last call.
    /// </summary>
    private bool ReconcileLocked()
    {
        bool armed = MousePatternRules.HostWanted(_settings());
        bool flipped = armed != _armedSeen;
        _armedSeen = armed;
        if (!armed)
        {
            // Turning the switch off ends an error and gives the next start its restart back.
            _fault = ClickerFault.None;
            _restartsLeft = 1;
            _lockKills = 0;
            if (_session is { } running) RetireLocked(running, kill: false);
        }
        else if (flipped)
        {
            _restartsLeft = 1;
            _lockKills = 0;
            _fault = ClickerFault.None;
        }

        if (armed && !_disposed && !_aborted && _initialized && _session is null && _fault == ClickerFault.None) StartSessionLocked();
        return flipped;
    }

    private void StartSessionLocked()
    {
        string pipeName = ClickerProtocol.NewPipeName(_parentPid);
        IClickerHostSession host;
        try
        {
            host = _launcher.Launch(pipeName, _parentPid);
        }
        catch (Exception ex)
        {
            Logger.Error("AutoClicker: could not start the helper process", ex);
            if (_restartsLeft > 0) { _restartsLeft--; StartSessionLocked(); }
            else _fault = ClickerFault.StartFailed;
            return;
        }

        var s = new Session(host, ++_nextSessionId);
        _session = s;
        Logger.Info($"AutoClicker: helper process started (session {s.Id}).");
        host.Exited += () => OnHostExited(s);
        s.Run = Task.Run(() => RunAsync(s));
        if (host.HasExited) OnHostExited(s);
    }

    /// <summary>The process ended: a helper that never connected has nothing more to say, so stop waiting for it. A connected one is read to its end.</summary>
    private void OnHostExited(Session s)
    {
        if (s.Connected) return;
        try { s.Cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Takes the session out of service (caller holds the gate). With <paramref name="kill"/> the process dies at once; otherwise
    /// it is asked to shut down and given the shutdown time, then ended.
    /// </summary>
    private void RetireLocked(Session s, bool kill)
    {
        s.Deliberate = true;
        if (ReferenceEquals(_session, s)) _session = null;
        _hostState = null;
        _lastPushed = null;
        CancelLockTimerLocked();
        try { _debounce.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }

        // A helper that is still connecting listens to nobody: there is nothing to say goodbye to.
        if (kill || !s.Ready)
        {
            try { s.Cts.Cancel(); } catch (ObjectDisposedException) { }
            KillHost(s);
            return;
        }

        s.Outbox.Writer.TryWrite(new ShutdownMessage());
        s.Outbox.Writer.TryComplete();
        if (_disposing) return;   // Dispose waits for the session itself
        _ = Task.Run(async () =>
        {
            await Task.WhenAny(s.Run, Task.Delay(_timing.ShutdownWait, _time)).ConfigureAwait(false);
            if (s.Run.IsCompleted) return;
            try { s.Cts.Cancel(); } catch (ObjectDisposedException) { }
        });
    }

    // ---------------- the session ----------------

    private async Task RunAsync(Session s)
    {
        try
        {
            if (await HandshakeAsync(s).ConfigureAwait(false)) await ReadLoopAsync(s).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
        {
            Logger.Warn("AutoClicker: the pipe to the helper ended: " + ex.Message);
        }
        catch (Exception ex)
        {
            Logger.Error("AutoClicker: the helper connection failed", ex);
        }
        finally
        {
            OnSessionEnded(s);
        }
    }

    /// <summary>
    /// The host speaks first (hello with its version and pid); the answer is hello, the lock and picker state, the configuration, then
    /// the samples of the mouse patterns.
    /// </summary>
    private async Task<bool> HandshakeAsync(Session s)
    {
        using (var timeout = new CancellationTokenSource(_timing.HandshakeTimeout, _time))
        using (var hs = CancellationTokenSource.CreateLinkedTokenSource(s.Cts.Token, timeout.Token))
        {
            var stream = await s.Host.ConnectAsync(hs.Token).ConfigureAwait(false);
            s.Pipe = new ClickerChannel(stream);
            s.Connected = true;

            while (true)
            {
                var decoded = await s.Pipe.ReadAsync(hs.Token).ConfigureAwait(false);
                if (decoded is null) return false;
                if (decoded.Value.Status != DecodeStatus.Ok) continue;
                // An older helper may have forwarded a log line ahead of its hello: keep it, it is no broken handshake.
                if (decoded.Value.Message is LogMessage early)
                {
                    Logger.Info("AutoClicker helper (before hello): " + early.Text);
                    continue;
                }
                if (decoded.Value.Message is not HelloMessage hello)
                {
                    Logger.Warn("AutoClicker: the helper did not start with hello.");
                    return false;
                }
                if (hello.V != ClickerProtocol.Version)
                {
                    Logger.Error($"AutoClicker: the helper speaks protocol v{hello.V}, the clipper v{ClickerProtocol.Version}.");
                    return false;
                }
                break;
            }
        }

        PreloadPatternTexts();   // the files are read here, not under the gate the push below holds
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_session, s)) return false;
            Enqueue(s, new HelloMessage(ClickerProtocol.Version));
            if (_lockedBy is not null)
            {
                Enqueue(s, new LockMessage(_lockedBy.ExeName));
                ArmLockTimerLocked();
            }
            if (_picker || _hotkeyCapture) Enqueue(s, new PickerMessage(true));
            PushConfigLocked(s, force: true);
            s.Ready = true;
        }
        s.Writer = Task.Run(() => WriteLoopAsync(s));
        PublishState();
        return true;
    }

    private async Task ReadLoopAsync(Session s)
    {
        while (true)
        {
            var decoded = await s.Pipe!.ReadAsync(s.Cts.Token).ConfigureAwait(false);
            if (decoded is null) return;
            switch (decoded.Value.Status)
            {
                case DecodeStatus.Unknown:
                    Logger.Info("AutoClicker: ignoring a helper message of unknown type: " + decoded.Value.Detail);
                    continue;
                case DecodeStatus.Malformed:
                    Logger.Warn("AutoClicker: ignoring a malformed helper message: " + decoded.Value.Detail);
                    continue;
            }
            try { Handle(s, decoded.Value.Message!); }
            catch (Exception ex) { Logger.Error("AutoClicker: handling a helper message failed", ex); }
        }
    }

    private async Task WriteLoopAsync(Session s)
    {
        try
        {
            await foreach (var message in s.Outbox.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                await s.Pipe!.WriteAsync(message, s.Cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The helper is gone; the read side notices the same and ends the session.
            s.Outbox.Writer.TryComplete();
        }
    }

    private void Handle(Session s, ClickerMessage message)
    {
        switch (message)
        {
            case StateMessage state:
                s.RecordingSeen = state.Recording;   // also for a session on its way out: its end may still need a notice
                lock (_gate)
                {
                    if (!ReferenceEquals(_session, s)) return;
                    _hostState = state;
                    if (state.Locked && _lockedBy is not null) { CancelLockTimerLocked(); _lockKills = 0; }
                }
                PublishState();
                break;

            case RunEndedMessage ended:
                OnRunEnded(ended);
                break;

            case PatternRecordedMessage recorded:
                // The result comes before the state that ends the recording: this recording has had its answer.
                s.RecordingSeen = false;
                OnPatternRecorded(recorded);
                break;

            case LogMessage log:
                if (log.Level == "error") Logger.Error("AutoClicker helper: " + log.Text);
                else if (log.Level == "warn") Logger.Warn("AutoClicker helper: " + log.Text);
                else Logger.Info("AutoClicker helper: " + log.Text);
                break;
        }
    }

    private void OnRunEnded(RunEndedMessage ended)
    {
        var reason = Enum.TryParse<StopReason>(ended.Reason, out var parsed) && Enum.IsDefined(parsed) ? parsed : StopReason.None;
        var record = new RunRecord { StartUtc = ended.StartUtc, Seconds = ended.Seconds, Clicks = ended.Clicks, StopReason = reason.ToString() };

        string? lockedBy;
        bool deferred;
        lock (_gate)
        {
            _lastRunEnded = ended;
            lockedBy = _lockedBy is null ? null : (string.IsNullOrWhiteSpace(_lockedBy.DisplayName) ? _lockedBy.ExeName : _lockedBy.DisplayName);
            deferred = _disposing;
            if (deferred) _pendingRuns.Add(record);   // Dispose applies it on its own thread: the settings' thread is blocked in there
        }
        if (!deferred) _post(() => ApplyRuns(new[] { record }));

        try { RunFinished?.Invoke(new ClickerRunInfo(record, reason, ended.Detail, lockedBy)); }
        catch (Exception ex) { Logger.Warn("AutoClicker: a run-finished handler threw: " + ex.Message); }
    }

    /// <summary>Adds finished runs to the statistics and saves (on the settings' thread).</summary>
    private void ApplyRuns(IReadOnlyList<RunRecord> runs)
    {
        try
        {
            var clicker = _settings();
            clicker.Stats ??= new ClickerStats();
            foreach (var run in runs) clicker.Stats.AddRun(run);
            _save();
        }
        catch (Exception ex)
        {
            Logger.Warn("AutoClicker: the run statistics could not be saved: " + ex.Message);
        }
    }

    // ---------------- mouse pattern recordings ----------------

    private void OnPatternRecorded(PatternRecordedMessage recorded)
    {
        bool deferred;
        lock (_gate)
        {
            deferred = _disposing;
            if (deferred) _pendingRecordings.Add(recorded);   // Dispose applies it on its own thread, like a run
        }
        if (!deferred) _post(() => ApplyRecording(recorded));
    }

    /// <summary>Takes a recording's result in and tells about it (on the settings' thread).</summary>
    private void ApplyRecording(PatternRecordedMessage recorded)
    {
        PatternRecordedInfo info;
        try
        {
            info = StoreRecording(recorded);
        }
        catch (Exception ex)
        {
            Logger.Warn("MousePatterns: the recording could not be taken in: " + ex.Message);
            info = new PatternRecordedInfo(PatternRecordedInfo.Failed, null, 0);
        }
        RaisePatternRecorded(info);
    }

    /// <summary>
    /// Ok/Cut: the samples go to the store first; only when they are on disk does the pattern change (target: program, length, revision;
    /// new: defaults of design 4.2) and the settings get saved, which pushes the config and the new samples to the helper. Anything else
    /// saves nothing.
    /// </summary>
    private PatternRecordedInfo StoreRecording(PatternRecordedMessage recorded)
    {
        string outcome = recorded.Outcome;
        if (outcome is not (nameof(PatternRecordingOutcome.Ok) or nameof(PatternRecordingOutcome.Cut)))
        {
            Logger.Info($"MousePatterns: recording ended without a pattern ({outcome}).");
            return new PatternRecordedInfo(outcome == nameof(PatternRecordingOutcome.Empty) ? outcome : nameof(PatternRecordingOutcome.Cancelled), null, 0);
        }
        if (!PatternCodec.TryDecode(recorded.Data, out var data))
        {
            Logger.Warn("MousePatterns: the helper sent unreadable samples - the recording is discarded.");
            return new PatternRecordedInfo(PatternRecordedInfo.Failed, null, 0);
        }

        var clicker = _settings();
        clicker.MousePatterns ??= new List<MousePattern>();
        var patterns = clicker.MousePatterns;
        var target = _recordTarget is { } targetId
            ? patterns.FirstOrDefault(p => p is not null && string.Equals(p.Id, targetId, StringComparison.OrdinalIgnoreCase))
            : null;
        if (target is null && patterns.Count(p => p is not null) >= MousePatternRules.MaxPatterns)
        {
            Logger.Warn("MousePatterns: a new recording was discarded - the list is full.");
            return new PatternRecordedInfo(PatternRecordedInfo.Full, null, 0);
        }

        string id = target?.Id ?? Guid.NewGuid().ToString("N");
        try
        {
            _patternStore.Save(id, data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Logger.Warn($"MousePatterns: the samples of {id} could not be written - the recording is discarded: {ex.Message}");
            return new PatternRecordedInfo(PatternRecordedInfo.Failed, null, 0);
        }

        string? program = AutoClickerBlacklist.Canonical(recorded.Program);
        if (target is null)
        {
            // Active and without a hotkey: harmless until the user gives it one.
            target = new MousePattern { Id = id, Name = MousePatternRules.NextName(patterns), Program = program, Revision = 1 };
            patterns.Add(target);
        }
        else
        {
            target.Program = program;
            if (program is null) target.OnlyInProgram = false;   // nothing left to bind to
            target.Revision = target.Revision >= int.MaxValue ? 1 : Math.Max(0, target.Revision) + 1;
        }
        target.DurationMs = data.DurationMs;
        lock (_gate) _patternText[id] = new PatternText(target.Revision, recorded.Data!);
        _recordTarget = null;
        _save();

        Logger.Info($"MousePatterns: recorded {id} r{target.Revision} ({outcome}, {data.DurationMs:0} ms).");
        return new PatternRecordedInfo(outcome, target.Name, data.DurationMs / 1000.0);
    }

    /// <summary>The helper ended while it recorded: nothing was kept, say so (not while the clipper itself goes away).</summary>
    private void OnRecordingLost()
    {
        lock (_gate)
        {
            if (_disposed || _aborted) return;
        }
        Logger.Info("MousePatterns: the helper ended during a recording - nothing was kept.");
        _post(() => RaisePatternRecorded(new PatternRecordedInfo(nameof(PatternRecordingOutcome.Cancelled), null, 0)));
    }

    private void RaisePatternRecorded(PatternRecordedInfo info)
    {
        try { PatternRecorded?.Invoke(info); }
        catch (Exception ex) { Logger.Warn("AutoClicker: a pattern-recorded handler threw: " + ex.Message); }
    }

    /// <summary>The session's read loop ended (pipe closed, error, cancel): clean up and, when nobody asked for it, restart once.</summary>
    private void OnSessionEnded(Session s)
    {
        bool current;
        lock (_gate)
        {
            current = ReferenceEquals(_session, s);
            if (current)
            {
                _session = null;
                _hostState = null;
                _lastPushed = null;
                CancelLockTimerLocked();
            }
        }

        s.Outbox.Writer.TryComplete();
        bool forced = s.Cts.IsCancellationRequested;   // cancelled by us: the goodbye time (or the handshake time) is over
        try { s.Cts.Cancel(); } catch (ObjectDisposedException) { }
        // One that never got through the handshake is ended at once; one that closed the pipe but still runs gets a moment to leave.
        if (!s.Host.HasExited && (!s.Ready || forced || !s.Host.WaitForExit(500))) KillHost(s);
        try { s.Pipe?.Dispose(); } catch { }
        try { s.Host.Dispose(); } catch { }
        s.Cts.Dispose();

        if (current)
        {
            lock (_gate)
            {
                if (!_disposed && !s.Deliberate && MousePatternRules.HostWanted(_settings()))
                {
                    var fault = s.Ready ? ClickerFault.Crashed : ClickerFault.HandshakeFailed;
                    if (_restartsLeft > 0)
                    {
                        _restartsLeft--;
                        Logger.Warn($"AutoClicker: the helper ended unexpectedly ({fault}) - starting it again.");
                    }
                    else
                    {
                        _fault = fault;
                        Logger.Error($"AutoClicker: the helper ended unexpectedly again ({fault}) - giving up until the switch is turned off and on.");
                    }
                }
                ReconcileLocked();
            }
        }
        PublishState();
        // Killed, crashed or shut down mid-recording (lock timeout, both switches off): no result will come (design 4.2).
        if (s.RecordingSeen) OnRecordingLost();
    }

    /// <summary>
    /// Ends the helper at once. One that got through the handshake may have had a key or button down (keyboard hold, duty hold) and
    /// dies without letting go, so a release process follows - after the kill, so the dying helper cannot press again. Once per session.
    /// </summary>
    private void KillHost(Session s)
    {
        s.Host.Kill();
        if (!s.Ready || Interlocked.Exchange(ref s.ReleaseLaunched, 1) == 1) return;
        try { _launcher.LaunchRelease(ClickerRelease.For(_settings())); }
        catch (Exception ex) { Logger.Warn("AutoClicker: the release process could not be started: " + ex.Message); }
    }

    // ---------------- sending ----------------

    private bool SendIfReady(ClickerMessage message)
    {
        lock (_gate)
        {
            if (_session is not { Ready: true } s) return false;
            Enqueue(s, message);
            return true;
        }
    }

    private static void Enqueue(Session s, ClickerMessage message) => s.Outbox.Writer.TryWrite(message);

    /// <summary>The settings as the wire carries them, as a deep copy: later edits of the live object cannot change a message already queued.</summary>
    private bool TryMakeConfig(out string encoded, out ConfigMessage? snapshot)
    {
        encoded = "";
        snapshot = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                encoded = ClickerProtocol.Encode(new ConfigMessage(_settings()));
                snapshot = ClickerProtocol.Decode(encoded).Message as ConfigMessage;
                return snapshot is not null;
            }
            catch (InvalidOperationException) { /* a list changed while it was written: look again */ }
        }
        return false;
    }

    private void PushConfigLocked(Session s, bool force)
    {
        if (!TryMakeConfig(out var encoded, out var snapshot))
        {
            _lastPushed = null;
            ArmDebounceLocked();
            return;
        }
        if (!force && encoded == _lastPushed) return;   // a new recording changes a revision, so it is never "unchanged"
        _lastPushed = encoded;
        Enqueue(s, snapshot!);
        PushPatternDataLocked(s, snapshot!.Settings);
    }

    /// <summary>
    /// Right after the config that names them: the samples of every pattern whose revision this helper has not had yet (all of them
    /// on a new helper). The helper plays a pattern only with samples of the revision its config names. A pattern without readable
    /// samples is sent nothing (and not tried again until its revision changes); it simply cannot play.
    /// </summary>
    private void PushPatternDataLocked(Session s, ClickerSettings pushed)
    {
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in pushed.MousePatterns ?? new List<MousePattern>())
        {
            if (p is null || !MousePatternRules.IsValidId(p.Id) || !listed.Add(p.Id)) continue;
            if (s.SentPatterns.TryGetValue(p.Id, out int sent) && sent == p.Revision) continue;
            s.SentPatterns[p.Id] = p.Revision;
            if (TakePatternTextLocked(p.Id, p.Revision) is { } text) Enqueue(s, new PatternDataMessage(p.Id, p.Revision, text));
            else Logger.Info($"MousePatterns: no samples for {p.Id} r{p.Revision} - the pattern cannot play until it is recorded again.");
        }
        foreach (var gone in s.SentPatterns.Keys.Where(id => !listed.Contains(id)).ToList()) s.SentPatterns.Remove(gone);
    }

    /// <summary>
    /// The samples of one revision: the ones loaded ahead or taken from the recording (used once), otherwise read from the store here,
    /// under the gate - only for a revision that changed without a recording of this clipper, which is rare.
    /// </summary>
    private string? TakePatternTextLocked(string id, int revision)
    {
        if (_patternText.TryGetValue(id, out var ready) && ready.Revision == revision)
        {
            _patternText.Remove(id);
            return ready.Text;
        }
        return LoadPatternText(id);
    }

    /// <summary>The samples of every listed pattern from the store into <see cref="_patternText"/>, outside the gate (the handshake's push takes them).</summary>
    private void PreloadPatternTexts()
    {
        var wanted = new List<(string Id, int Revision)>();
        try
        {
            foreach (var p in _settings().MousePatterns ?? new List<MousePattern>())
                if (p is not null && MousePatternRules.IsValidId(p.Id)) wanted.Add((p.Id, p.Revision));
        }
        catch (InvalidOperationException) { /* the list changed while it was read: the push loads what is missing itself */ }

        foreach (var (id, revision) in wanted)
        {
            lock (_gate)
            {
                if (_patternText.TryGetValue(id, out var ready) && ready.Revision == revision) continue;
            }
            if (LoadPatternText(id) is not { } text) continue;
            lock (_gate) _patternText[id] = new PatternText(revision, text);
        }
    }

    private string? LoadPatternText(string id)
    {
        if (!_patternStore.TryLoad(id, out var data)) return null;
        try { return PatternCodec.Encode(data); }
        catch (ArgumentException ex)
        {
            Logger.Warn($"MousePatterns: the samples of {id} cannot be sent: {ex.Message}");
            return null;
        }
    }

    private void ArmDebounceLocked()
    {
        try { _debounce.Change(_timing.ConfigDebounce, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private void PushConfigNow()
    {
        lock (_gate)
        {
            if (_disposed || _session is not { Ready: true } s) return;
            PushConfigLocked(s, force: false);
        }
    }

    // ---------------- lock confirmation ----------------

    /// <summary>Starts the second the helper has to confirm a lock (a state message with Locked set). Re-arming replaces a pending timer.</summary>
    private void ArmLockTimerLocked()
    {
        _armedLockGeneration = ++_lockGeneration;
        try { _lockTimer.Change(_timing.LockConfirm, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private void CancelLockTimerLocked()
    {
        _lockGeneration++;
        try { _lockTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>The helper did not confirm the lock in time: end it. Its replacement is told about the lock before anything else.</summary>
    private void OnLockTimer(object? _)
    {
        lock (_gate)
        {
            if (_disposed || _lockedBy is null || _session is not { Ready: true } s) return;
            if (_armedLockGeneration != _lockGeneration) return;   // confirmed or cancelled meanwhile
            if (++_lockKills > MaxLockKills)
            {
                Logger.Error("AutoClicker: the helper never confirmed the lock - giving up.");
                RetireLocked(s, kill: true);
                _fault = ClickerFault.LockNotConfirmed;
            }
            else
            {
                Logger.Warn("AutoClicker: the helper did not confirm the lock within the time limit - ending it; the new one starts locked.");
                RetireLocked(s, kill: true);
                ReconcileLocked();
            }
        }
        PublishState();
    }

    // ---------------- state ----------------

    private ClickerState BuildStateLocked()
    {
        var settings = _settings();
        if (_disposed || !_initialized || !MousePatternRules.HostWanted(settings)) return ClickerState.Off;

        string? lockedBy = _lockedBy is null ? null : (string.IsNullOrWhiteSpace(_lockedBy.DisplayName) ? _lockedBy.ExeName : _lockedBy.DisplayName);
        var host = _hostState;
        var s = _session;
        bool patternsArmed = settings.MousePatternsArmed;

        if (_fault != ClickerFault.None)
            return ClickerState.Off with { Phase = ClickerPhase.Error, Locked = _lockedBy is not null, LockedBy = lockedBy, Fault = _fault, PatternsArmed = patternsArmed };
        if (s is not { Ready: true } || host is null)
            return ClickerState.Off with { Phase = ClickerPhase.Starting, Locked = _lockedBy is not null, LockedBy = lockedBy, PatternsArmed = patternsArmed };

        StopReason? lastStop = null;
        string? detail = null;
        if (!host.Running && host.Reason is { } name && Enum.TryParse<StopReason>(name, out var reason) && Enum.IsDefined(reason) && reason != StopReason.None)
        {
            lastStop = reason;
            if (_lastRunEnded is { } ended && ended.Reason == name) detail = ended.Detail;
        }
        return new ClickerState(host.Running ? ClickerPhase.Running : ClickerPhase.Ready, _lockedBy is not null, lockedBy,
            host.Clicks, host.Cps, host.ElapsedMs, host.BlockedByElevated, lastStop, detail, ClickerFault.None, host.MasterAllowed,
            patternsArmed, host.Recording, host.Playing, host.PatternsBlockedBy);
    }

    /// <summary>Makes the state, and when it differs from the last one raises <see cref="StateChanged"/>. Serialized, so handlers see the states in order.</summary>
    private void PublishState()
    {
        lock (_publishGate)
        {
            ClickerState next;
            lock (_gate)
            {
                next = BuildStateLocked();
                if (next == _state) return;
                Volatile.Write(ref _state, next);
            }
            try { StateChanged?.Invoke(next); }
            catch (Exception ex) { Logger.Warn("AutoClicker: a state handler threw: " + ex.Message); }
        }
    }

    private void RaiseArmedChanged()
    {
        try { ArmedChanged?.Invoke(); }
        catch (Exception ex) { Logger.Warn("AutoClicker: an armed handler threw: " + ex.Message); }
    }

    private sealed class Session
    {
        public Session(IClickerHostSession host, int id)
        {
            Host = host;
            Id = id;
        }

        public IClickerHostSession Host { get; }
        public int Id { get; }
        public CancellationTokenSource Cts { get; } = new();
        public Channel<ClickerMessage> Outbox { get; } = Channel.CreateUnbounded<ClickerMessage>(new UnboundedChannelOptions { SingleReader = true });
        public ClickerChannel? Pipe;
        public Task? Writer;
        public Task Run = Task.CompletedTask;
        public volatile bool Connected;
        public volatile bool Ready;
        public int ReleaseLaunched;

        /// <summary>Pattern id -> the revision whose samples this helper got (or that had none to send). Under the controller's gate.</summary>
        public readonly Dictionary<string, int> SentPatterns = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The helper's last word was "recording" and no result followed yet (written by the read loop only).</summary>
        public volatile bool RecordingSeen;

        /// <summary>The controller ended it on purpose (switch off, lock timeout, dispose): no restart, no error.</summary>
        public volatile bool Deliberate;
    }
}
