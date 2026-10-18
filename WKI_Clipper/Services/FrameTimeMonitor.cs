using System;
using System.Diagnostics;
using System.Threading;

namespace WKI_Clipper.Services;

internal enum PresentSessionStart { Started, AccessDenied, Failed }

/// <summary>A source of present events (the real one is <see cref="EtwPresentSession"/>). Dispose = stop.</summary>
internal interface IPresentSession : IDisposable
{
    /// <summary>Starts collecting. The callbacks run on the session's own thread.</summary>
    PresentSessionStart Start(Action<PresentEvent> onPresent, Action<long> onLost);

    /// <summary>Only this process's presents are delivered (0 = none). Settable while running.</summary>
    int TargetProcessId { get; set; }

    /// <summary>Events + buffers ETW reports lost since the start; -1 = unknown.</summary>
    long QueryLostCount();
}

/// <summary>
/// FPS, frame times, 1 % / 0.1 % lows and micro-stutter of ONE target process (the foreground
/// game, chosen by the caller), measured the way AMD Adrenalin's PresentMon and the Xbox Game Bar
/// do: a passive real-time ETW session that receives the graphics runtimes' Present events.
/// No injection, no hooks, no handle to the game — nothing an anti-cheat can object to.
///
/// Costs nothing until the first <see cref="Subscribe"/>: the session, its consumer thread and the
/// 1 Hz evaluation run only while at least one lease is alive (like PerformanceMonitorService's
/// viewers). <see cref="Updated"/> fires about once a second on a pool thread.
///
/// Needs admin or membership in "Performance Log Users" (S-1-5-32-559); without it the status is
/// <see cref="FrameMetricsStatus.NoPermission"/> and nothing else happens.
/// </summary>
public sealed class FrameTimeMonitor : IDisposable
{
    /// <summary>How long to wait before retrying a session that failed to start for reasons other than permission.</summary>
    public const double RetrySeconds = 15;

    private enum State { Stopped, Running, NoPermission, Failed }

    private readonly Func<IPresentSession> _sessionFactory;
    private readonly bool _allowOwnProcess;
    private readonly Func<long> _clock;
    private readonly long _freq;
    private readonly bool _autoTick;

    private readonly object _gate = new();          // lifecycle: subscribers, session, timer
    private readonly object _trackerGate = new();   // the ETW thread's hot path; never take _gate inside
    private readonly PresentTracker _tracker;

    private int _subscribers;
    private bool _disposed;
    private State _state = State.Stopped;
    private volatile IPresentSession? _session;
    private System.Threading.Timer? _timer;
    private long _lastStartAttempt;
    private long _lastLost;
    private int _ticking;
    private volatile int _targetPid;
    private volatile FrameMetricsSnapshot _current = FrameMetricsSnapshot.Unavailable;

    public FrameTimeMonitor()
        : this(() => new EtwPresentSession(EtwPresentSession.SessionNameForCurrentUser()),
               allowOwnProcess: false, clock: null, Stopwatch.Frequency, autoTick: true)
    {
    }

    /// <param name="allowOwnProcess">Tests only: lets the test process measure its own presents.</param>
    /// <param name="clock">QPC-compatible "now" (defaults to <see cref="Stopwatch.GetTimestamp"/>, same clock as ETW raw timestamps).</param>
    /// <param name="autoTick">False = no timer, the caller drives <see cref="Tick"/>.</param>
    internal FrameTimeMonitor(Func<IPresentSession> sessionFactory, bool allowOwnProcess,
        Func<long>? clock, long ticksPerSecond, bool autoTick)
    {
        _sessionFactory = sessionFactory;
        _allowOwnProcess = allowOwnProcess;
        _clock = clock ?? Stopwatch.GetTimestamp;
        _freq = ticksPerSecond;
        _autoTick = autoTick;
        _tracker = new PresentTracker(ticksPerSecond);
    }

    /// <summary>
    /// Stops this user's session left behind by a crashed clipper (ETW would keep the providers
    /// enabled until reboot). Never touches a session this process runs; a cheap no-op otherwise.
    /// </summary>
    public static bool StopOrphanedSession()
    {
        try { return EtwPresentSession.StopOrphanedSession(); }
        catch (Exception ex)
        {
            Logger.Warn("FPS: orphan cleanup failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>The latest reading. <see cref="FrameMetricsStatus.Unavailable"/> while nobody subscribes.</summary>
    public FrameMetricsSnapshot Current => _current;

    /// <summary>Raised about once per second while subscribed (pool thread).</summary>
    public event Action<FrameMetricsSnapshot>? Updated;

    public int TargetProcessId => _targetPid;

    public int SubscriberCount { get { lock (_gate) return _subscribers; } }

    /// <summary>
    /// The process to measure (the foreground game); 0 = none. The clipper's own process is
    /// refused (WPF presents through D3D9 and would pose as a game). A different pid starts the
    /// statistics over; the same pid again is a no-op, so callers may simply repeat it.
    /// </summary>
    public void SetTargetProcess(int pid)
    {
        if (pid < 0 || (pid == Environment.ProcessId && !_allowOwnProcess)) pid = 0;
        lock (_trackerGate)
        {
            if (pid == _targetPid && _tracker.TargetPid == pid) return;
            _targetPid = pid;
            _tracker.SetTarget(pid);
        }
        var session = _session;
        if (session != null) session.TargetProcessId = pid;
    }

    /// <summary>Starts measuring (first lease starts the ETW session). Dispose the lease to stop.</summary>
    public IDisposable Subscribe()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _subscribers++;
            if (_subscribers == 1)
            {
                StartSessionLocked();
                if (_autoTick) _timer = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
            }
        }
        return new Lease(this);
    }

    private void Unsubscribe()
    {
        lock (_gate)
        {
            if (_disposed || _subscribers == 0) return;
            _subscribers--;
            if (_subscribers == 0) StopLocked();
        }
    }

    /// <summary>One evaluation: lost-event check, retry of a failed start, snapshot, <see cref="Updated"/>.</summary>
    internal void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;   // a slow tick is still running
        try
        {
            FrameMetricsSnapshot snapshot;
            lock (_gate)
            {
                if (_subscribers == 0 || _disposed) return;
                long now = _clock();

                if (_state == State.Failed && now - _lastStartAttempt >= (long)(RetrySeconds * _freq))
                    StartSessionLocked();

                if (_state == State.Running && _session != null)
                {
                    // Self-heal a prefilter that lost a race between SetTargetProcess and a start.
                    if (_session.TargetProcessId != _targetPid) _session.TargetProcessId = _targetPid;
                    long lost = _session.QueryLostCount();
                    if (lost < 0)
                    {
                        // The session no longer exists (stopped from outside): its consumer has ended.
                        Logger.Warn("FPS: ETW session vanished — restarting it.");
                        DisposeSessionLocked();
                        StartSessionLocked();
                    }
                    else
                    {
                        if (_lastLost >= 0 && lost > _lastLost)
                        {
                            Logger.Warn($"FPS: ETW lost {lost - _lastLost} event(s)/buffer(s) — values unknown for this window.");
                            lock (_trackerGate) _tracker.OnEventsLost(now);
                        }
                        _lastLost = lost;
                    }
                }

                snapshot = SnapshotLocked(now);
                _current = snapshot;
            }
            RaiseUpdated(snapshot);
        }
        catch (Exception ex)
        {
            Logger.Warn("FPS tick failed: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    private FrameMetricsSnapshot SnapshotLocked(long now) => _state switch
    {
        State.Running => SnapshotTracker(now),
        State.NoPermission => FrameMetricsSnapshot.NoPermission,
        _ => FrameMetricsSnapshot.Unavailable,
    };

    private FrameMetricsSnapshot SnapshotTracker(long now)
    {
        lock (_trackerGate) return _tracker.Snapshot(now);
    }

    private void StartSessionLocked()
    {
        _lastStartAttempt = _clock();
        lock (_trackerGate) _tracker.SetTarget(_targetPid);   // never show values from a previous run

        IPresentSession session;
        try
        {
            session = _sessionFactory();
        }
        catch (Exception ex)
        {
            Logger.Warn("FPS: session could not be created: " + ex.Message);
            _state = State.Failed;
            _current = FrameMetricsSnapshot.Unavailable;
            return;
        }

        session.TargetProcessId = _targetPid;
        PresentSessionStart result;
        try
        {
            result = session.Start(OnPresent, OnLost);
        }
        catch (Exception ex)
        {
            Logger.Warn("FPS: session start threw: " + ex.Message);
            result = PresentSessionStart.Failed;
        }

        if (result != PresentSessionStart.Started)
        {
            try { session.Dispose(); } catch (Exception ex) { Logger.Warn("FPS: cleanup after failed start: " + ex.Message); }
            _state = result == PresentSessionStart.AccessDenied ? State.NoPermission : State.Failed;
            _current = _state == State.NoPermission ? FrameMetricsSnapshot.NoPermission : FrameMetricsSnapshot.Unavailable;
            if (_state == State.NoPermission)
                Logger.Info("FPS: no permission for an ETW session (needs 'Performance Log Users' or admin).");
            return;
        }

        _session = session;
        session.TargetProcessId = _targetPid;   // a SetTargetProcess may have raced the start
        _state = State.Running;
        _lastLost = session.QueryLostCount();
        _current = SnapshotLocked(_clock());
    }

    private void StopLocked()
    {
        _timer?.Dispose();
        _timer = null;
        DisposeSessionLocked();
        lock (_trackerGate) _tracker.SetTarget(_targetPid);
        _current = FrameMetricsSnapshot.Unavailable;
    }

    private void DisposeSessionLocked()
    {
        var session = _session;
        _session = null;
        _state = State.Stopped;
        if (session != null)
        {
            try { session.Dispose(); } catch (Exception ex) { Logger.Warn("FPS: session stop failed: " + ex.Message); }
        }
    }

    // --- session thread ---

    private void OnPresent(PresentEvent e)
    {
        lock (_trackerGate) _tracker.OnPresent(e);
    }

    private void OnLost(long timestamp)
    {
        if (timestamp <= 0) timestamp = _clock();
        lock (_trackerGate) _tracker.OnEventsLost(timestamp);
    }

    private void RaiseUpdated(FrameMetricsSnapshot snapshot)
    {
        try { Updated?.Invoke(snapshot); }
        catch (Exception ex) { Logger.Warn("FPS: Updated handler threw: " + ex.Message); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            StopLocked();
            _subscribers = 0;
            _disposed = true;
        }
    }

    private sealed class Lease : IDisposable
    {
        private FrameTimeMonitor? _owner;
        public Lease(FrameTimeMonitor owner) => _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Unsubscribe();
    }
}
