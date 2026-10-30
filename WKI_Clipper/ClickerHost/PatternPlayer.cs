using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using WKI_Clipper.Models;
using WKI_Clipper.Services.AutoClicker;

namespace WKI_Clipper.ClickerHost;

/// <summary>
/// One playback as the coordinator starts it: the pattern's samples and the options it has at that moment (an edit while it plays
/// applies to the next start, mouse-patterns design 4.4).
/// </summary>
internal sealed record PatternPlayRequest(string Id, PatternData Data, double Speed, bool Repeat, PatternReplay Replay);

/// <summary>
/// Plays mouse patterns in the helper (mouse-patterns design 4.3, 4.4, 5.1) on its own thread with its own <see cref="IWaiter"/> (the
/// engine's has a sticky wake and is never shared). The thread sleeps while nothing plays; while something plays it ticks every
/// <see cref="TickMs"/>. Per tick:
/// <list type="bullet">
/// <item>Every playback advances its <see cref="PatternCursor"/> (cumulative, so neither rounding nor a late tick drifts).</item>
/// <item>The relative ones ("Spiel", or the pointer track of a pattern without raw counts) go out summed as ONE
/// <see cref="IInputSink.MoveBy"/>, never 0/0.</item>
/// <item>The absolute ones ("Zeiger") share one anchor, the pointer when the first of them started; their target - the anchor plus
/// all their offsets - goes out as ONE <see cref="IInputSink.MoveTo"/>, only when it changed. One that joins starts its offset at
/// zero and one that ends leaves its offset in the anchor, and a relative move of the same tick shifts the anchor too, so playbacks
/// add up and none moves the pointer by starting or stopping. The target stays on the monitors, and what an edge swallows is gone, as
/// with a hand on the mouse: never a jump, also with negative coordinates or gaps between monitors.</item>
/// </list>
/// The replay type is decided once per start (<see cref="PatternReplayRules.Resolve"/>). <see cref="Play"/>, <see cref="Stop"/> and
/// <see cref="StopAll"/> may be called from any thread; a tick runs under the same lock, so once a stop returns nothing of that
/// playback is sent any more. A move Windows refuses (an elevated window in front) ends every playback.
/// </summary>
internal sealed class PatternPlayer : IDisposable
{
    /// <summary>The tick while something plays.</summary>
    public const double TickMs = 1.0;

    private sealed class Playback
    {
        public Playback(PatternPlayRequest request, double startMs)
        {
            Request = request;
            StartMs = startMs;
        }

        public PatternPlayRequest Request { get; }
        public string Id => Request.Id;
        public double StartMs { get; }

        // Set by its first tick, under the player's lock. Cursor stays null when the data cannot be played.
        public PatternCursor? Cursor;
        public bool Absolute;
        public bool Done;
    }

    /// <summary>The curves of one <see cref="PatternData"/>: built at its first start, shared by every later one.</summary>
    private sealed class Curves
    {
        public PatternCurve? Raw, Pointer;
    }

    private readonly IInputSink _sink;
    private readonly IWaiter _waiter;
    private readonly IClock _clock;
    private readonly IPatternEnvironment _env;
    private readonly Thread? _thread;
    private readonly object _lock = new();
    private readonly List<Playback> _playbacks = new();   // in start order
    private readonly ConditionalWeakTable<PatternData, Curves> _curves = new();
    private readonly List<string> _ended = new();         // the stepping thread's: Finished to raise after the lock

    // ---- under _lock ----
    private int _absolute;             // started absolute playbacks
    private long _anchorX, _anchorY;   // their shared anchor
    private int _lastX, _lastY;        // the absolute target sent last
    private bool _blocked;             // the last move was refused
    private bool _wakePending;         // the waiter was woken and not cleared yet
    private bool _inSession;
    private volatile bool _disposed;

    /// <param name="ownThread">false: no thread - a test drives the loop with <see cref="RunSync"/>.</param>
    public PatternPlayer(IInputSink sink, IWaiter waiter, IClock clock, IPatternEnvironment env, bool ownThread = true)
    {
        _sink = sink;
        _waiter = waiter;
        _clock = clock;
        _env = env;
        if (!ownThread) return;
        _thread = new Thread(Loop)
        {
            Name = "WKI Pattern player",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
        _thread.Start();
    }

    /// <summary>
    /// A playback ended by itself: a pass without repeat is complete (raised after its last move went out), or the player ended it (a
    /// refused move, data it cannot play). Not raised for <see cref="Stop"/>, <see cref="StopAll"/> or a restart by <see cref="Play"/> -
    /// the caller knows - nor when the id plays again by the time it would be raised. On the player's thread, outside its lock.
    /// </summary>
    public event Action<string>? Finished;

    /// <summary>
    /// true: Windows refused a move and every playback ended (raised after their <see cref="Finished"/>); false: a move went through
    /// again after that. On the player's thread, outside its lock.
    /// </summary>
    public event Action<bool>? Blocked;

    /// <summary>The ids playing right now (also one whose first tick is still due), in start order. A copy. Any thread.</summary>
    public IReadOnlyList<string> Playing
    {
        get
        {
            lock (_lock)
            {
                if (_playbacks.Count == 0) return Array.Empty<string>();
                var ids = new string[_playbacks.Count];
                for (int i = 0; i < ids.Length; i++) ids[i] = _playbacks[i].Id;
                return ids;
            }
        }
    }

    /// <summary>Whether <paramref name="id"/> plays right now. Any thread.</summary>
    public bool IsPlaying(string id)
    {
        lock (_lock) return IndexOf(id) >= 0;
    }

    /// <summary>Starts a playback from where the pointer is now; one with the same id that still plays starts over. Any thread.</summary>
    public void Play(PatternPlayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Data);
        lock (_lock)
        {
            if (_disposed) return;
            int i = IndexOf(request.Id);
            if (i >= 0) Remove(i);
            _playbacks.Add(new Playback(request, _clock.NowMs));
            Wake();   // the first move goes out now, not at the next tick (or after the idle sleep)
        }
    }

    /// <summary>Ends the playback before the next tick; nothing of it is sent once this returns. No effect when it does not play. Any thread.</summary>
    public void Stop(string id)
    {
        lock (_lock)
        {
            int i = IndexOf(id);
            if (i >= 0) Remove(i);
        }
    }

    /// <summary>Ends every playback before the next tick; nothing is sent once this returns. Any thread.</summary>
    public void StopAll()
    {
        lock (_lock)
        {
            while (_playbacks.Count > 0) Remove(_playbacks.Count - 1);
        }
    }

    /// <summary>Test seam for a player without a thread: runs the loop on the calling thread until nothing plays any more (or the next tick is after <paramref name="untilMs"/>).</summary>
    internal void RunSync(double untilMs = double.PositiveInfinity)
    {
        if (_thread is not null) throw new InvalidOperationException("The player runs on its own thread.");
        while (RunOnce() is { } next && next <= untilMs) _waiter.WaitUntil(next);
    }

    private void Loop()
    {
        while (!_disposed)
        {
            try
            {
                double? next = RunOnce();
                if (_disposed) break;
                _waiter.WaitUntil(next ?? double.PositiveInfinity);   // woken early: the next step clears the wake
            }
            catch (Exception ex)
            {
                HostLog.Error("Pattern player: waiting failed", ex);
                Thread.Sleep(10);
            }
        }
    }

    /// <summary>One step of the loop; when it is due next, null = nothing plays (sleep until woken). Never throws.</summary>
    private double? RunOnce()
    {
        try { return Step(); }
        catch (Exception ex)
        {
            HostLog.Error("Pattern player failed; every pattern stopped", ex);
            lock (_lock)
            {
                EndAll();
                EndSession();
            }
            RaiseEnded();
            return null;
        }
    }

    private double? Step()
    {
        double? next = null;
        bool? blocked = null;
        lock (_lock)
        {
            if (_wakePending)
            {
                _wakePending = false;
                _waiter.ClearWake();
            }
            if (_disposed || _playbacks.Count == 0)
            {
                EndSession();
                return null;
            }

            BeginSession();
            double now = _clock.NowMs;
            bool wasBlocked = _blocked;
            if (Tick(now))
            {
                if (wasBlocked && !_blocked) blocked = false;
            }
            else
            {
                EndAll();
                _blocked = true;
                blocked = true;
            }
            if (_playbacks.Count > 0) next = now + TickMs;
            else EndSession();
        }

        RaiseEnded();
        if (blocked is { } b) RaiseBlocked(b);
        return next;
    }

    // ---- one tick (under _lock) ----

    /// <summary>Advances every playback to <paramref name="now"/> and sends what is due. false = Windows refused a move.</summary>
    private bool Tick(double now)
    {
        long relX = 0, relY = 0;
        foreach (var p in _playbacks)
        {
            if (p.Cursor is null && !Begin(p))
            {
                p.Done = true;
                continue;
            }
            // The last move of a pass comes with the false: it is sent like any other.
            if (!p.Cursor!.Advance(now - p.StartMs, out int dx, out int dy)) p.Done = true;
            if (p.Absolute) continue;
            relX += dx;
            relY += dy;
        }

        if ((relX != 0 || relY != 0) && !Sent(_sink.MoveBy(Saturate(relX), Saturate(relY)))) return false;

        if (_absolute > 0)
        {
            _anchorX += relX;   // the relative move took the pointer along: the absolute target follows, so both add up
            _anchorY += relY;
            long tx = _anchorX, ty = _anchorY;
            foreach (var p in _playbacks)
            {
                if (!p.Absolute || p.Cursor is not { } cursor) continue;
                var (ex, ey) = cursor.Emitted;
                tx += ex;
                ty += ey;
            }
            var (x, y) = ClampToScreen(tx, ty, _env.Monitors);
            _anchorX += x - tx;   // what the edge swallowed is gone: the way back starts at the edge
            _anchorY += y - ty;
            if (x != _lastX || y != _lastY)
            {
                if (!Sent(_sink.MoveTo(x, y))) return false;
                (_lastX, _lastY) = (x, y);
            }
        }

        for (int i = 0; i < _playbacks.Count; i++)
        {
            if (!_playbacks[i].Done) continue;
            _ended.Add(_playbacks[i].Id);
            Remove(i--);
        }
        return true;
    }

    /// <summary>
    /// The first tick of a playback: its replay type, its cursor and, for the first absolute playback, the anchor. false = its data
    /// cannot be played (the playback ends without a move).
    /// </summary>
    private bool Begin(Playback p)
    {
        var r = p.Request;
        var data = r.Data;
        var replay = PatternReplayRules.Resolve(r.Replay, _env.AccelerationOn, _env.PointerVisible, hasRaw: data.Raw.Count > 0);
        // Auto never chooses a track without motion while the other one has some (a recording whose pointer track stayed empty).
        if (r.Replay == PatternReplay.Auto && replay == ResolvedReplay.AbsolutePointer && data.Pointer.Count == 0 && data.Raw.Count > 0)
            replay = ResolvedReplay.RelativeRaw;

        try
        {
            var curves = _curves.GetValue(data, static _ => new Curves());
            var curve = replay == ResolvedReplay.RelativeRaw
                ? curves.Raw ??= new PatternCurve(data.Raw, data.DurationMs)
                : curves.Pointer ??= new PatternCurve(data.Pointer, data.DurationMs);
            p.Cursor = new PatternCursor(curve, r.Speed, r.Repeat);
        }
        catch (ArgumentException ex)
        {
            HostLog.Warn("Pattern " + r.Id + " cannot be played: " + ex.Message);
            return false;
        }

        p.Absolute = replay == ResolvedReplay.AbsolutePointer;
        if (p.Absolute && _absolute++ == 0)
        {
            var c = _env.CursorPosition;
            (_anchorX, _anchorY, _lastX, _lastY) = (c.X, c.Y, c.X, c.Y);
        }
        return true;
    }

    /// <summary>
    /// The point itself when a monitor shows it, otherwise the nearest pixel of the nearest monitor - where the pointer would stop. The
    /// virtual screen's bounding box is not enough: monitors of different sizes leave gaps in it. Without monitors only the int range.
    /// </summary>
    internal static (int X, int Y) ClampToScreen(long x, long y, IReadOnlyList<PixelRect> monitors)
    {
        long bestX = Math.Clamp(x, int.MinValue, int.MaxValue), bestY = Math.Clamp(y, int.MinValue, int.MaxValue);
        double best = double.PositiveInfinity;
        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            if (m.W <= 0 || m.H <= 0) continue;
            long cx = Math.Clamp(x, m.X, m.Right - 1L), cy = Math.Clamp(y, m.Y, m.Bottom - 1L);
            if (cx == x && cy == y) return ((int)x, (int)y);
            double ddx = x - cx, ddy = y - cy;
            double d = ddx * ddx + ddy * ddy;
            if (d >= best) continue;
            best = d;
            (bestX, bestY) = (cx, cy);
        }
        return ((int)bestX, (int)bestY);
    }

    /// <summary>One move carries at most an int (never reached by a recorded pattern).</summary>
    private static int Saturate(long v) => (int)Math.Clamp(v, int.MinValue, int.MaxValue);

    private bool Sent(bool ok)
    {
        if (ok) _blocked = false;
        return ok;
    }

    // ---- bookkeeping (under _lock) ----

    private int IndexOf(string id)
    {
        for (int i = 0; i < _playbacks.Count; i++)
            if (string.Equals(_playbacks[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;   // ids are file names
        return -1;
    }

    private void Remove(int index)
    {
        var p = _playbacks[index];
        _playbacks.RemoveAt(index);
        if (!p.Absolute || p.Cursor is not { } cursor) return;
        var (x, y) = cursor.Emitted;   // stays in the anchor: the pointer remains where this playback took it
        _anchorX += x;
        _anchorY += y;
        _absolute--;
    }

    /// <summary>The player ends every playback itself (a refused move, a failure): each gets its <see cref="Finished"/>.</summary>
    private void EndAll()
    {
        foreach (var p in _playbacks) _ended.Add(p.Id);
        _playbacks.Clear();
        _absolute = 0;
    }

    private void Wake()
    {
        _wakePending = true;
        _waiter.Wake();
    }

    private void BeginSession()
    {
        if (_inSession) return;
        _inSession = true;
        _waiter.BeginSession();
    }

    private void EndSession()
    {
        if (!_inSession) return;
        _inSession = false;
        _waiter.EndSession();
    }

    // ---- events (the stepping thread, outside _lock) ----

    private void RaiseEnded()
    {
        for (int i = 0; i < _ended.Count; i++)
        {
            string id = _ended[i];
            if (IsPlaying(id)) continue;   // started again meanwhile: that playback is not over
            try { Finished?.Invoke(id); }
            catch (Exception ex) { HostLog.Error("Pattern Finished handler failed", ex); }
        }
        _ended.Clear();
    }

    private void RaiseBlocked(bool blocked)
    {
        if (blocked) HostLog.Info("Pattern playback: Windows refused a move (an elevated window in front?); every pattern stopped.");
        try { Blocked?.Invoke(blocked); }
        catch (Exception ex) { HostLog.Error("Pattern Blocked handler failed", ex); }
    }

    /// <summary>Ends every playback (nothing is sent once this returns) and the thread.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _playbacks.Clear();
            _absolute = 0;
            EndSession();
            Wake();
        }
        if (_thread is not null && _thread != Thread.CurrentThread && !_thread.Join(1000))
            HostLog.Warn("The pattern player's thread did not end in time.");
    }
}
