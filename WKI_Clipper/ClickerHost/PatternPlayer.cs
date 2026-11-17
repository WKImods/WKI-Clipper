using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using WKI_Clipper.Models;
using WKI_Clipper.Services.AutoClicker;

namespace WKI_Clipper.ClickerHost;

/// <summary>
/// One playback as the coordinator starts it: the pattern's samples and the options it has at that moment (an edit while it plays
/// applies to the next start, mouse-patterns design 4.4). <paramref name="Return"/>: back to the start after every pass and when its
/// own hotkey stops it (pattern return design 1).
/// </summary>
internal sealed record PatternPlayRequest(string Id, PatternData Data, double Speed, bool Repeat, PatternReplay Replay,
    PatternReturn Return = PatternReturn.Off);

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
/// <item>A playback with a way back (<see cref="PatternPlayRequest.Return"/>, pattern return design 1) plays a pass, then its way back
/// (<see cref="PatternReturnPath"/>, from the tick after the pass's last move), and with repeat the next pass in the tick after that,
/// each pass from the same point. Its offset is what its way out and its way back sent together. While it is the only absolute
/// playback, its way back also retraces what an edge swallowed on the way out - the edge holds the pointer instead - so it ends
/// exactly where the pass began.</item>
/// </list>
/// The replay type is decided once per start (<see cref="PatternReplayRules.Resolve"/>). <see cref="Play"/>, <see cref="Stop"/>,
/// <see cref="StopWithReturn"/> and <see cref="StopAll"/> may be called from any thread; a tick runs under the same lock, so once a
/// stop returns nothing of that playback is sent any more. A move Windows refuses (an elevated window in front) ends every playback.
/// </summary>
internal sealed class PatternPlayer : IDisposable
{
    /// <summary>The tick while something plays.</summary>
    public const double TickMs = 1.0;

    /// <summary>One running pattern. Everything but the constructor runs under the player's lock.</summary>
    private sealed class Playback
    {
        public Playback(PatternPlayRequest request, double startMs)
        {
            Request = request;
            StartMs = LastMs = startMs;
        }

        /// <summary>Replaced by <see cref="Next"/> when the pass after a press on the way begins.</summary>
        public PatternPlayRequest Request;
        public string Id => Request.Id;
        public bool Returns => Request.Return != PatternReturn.Off;

        // Curve, Cursor and Absolute are set by its first tick; Curve stays null until then and when the data cannot be played.
        public PatternCurve? Curve;
        public PatternCursor? Cursor;         // the current pass, on its way out
        public PatternReturnPath? Back;       // ... and on its way back, once it turned
        public long CarriedX, CarriedY;       // what an earlier way back of this pass sent before a press turned it into a jump
        // What an edge swallowed of this pass's way out while it was the only absolute playback, with the place on the curve where it
        // happened (increasing): its way back gives each part back at the same place.
        public readonly List<(double CurveMs, long X, long Y)> Swallowed = new();
        public double StartMs;                // of the current pass
        public double LastMs;                 // its last move on the way out
        public double BackStartMs;
        public bool Absolute;                 // counted in _absolute
        public bool Stopping;                 // its hotkey stopped it: back to the start, then no further pass
        public bool PassDue;                  // back at the start: the next pass begins with the next tick
        public PatternPlayRequest? Next;      // pressed again on the way: the next pass plays this request
        public bool Done;

        /// <summary>Everything the current pass sent: its way out and its way back.</summary>
        public (long X, long Y) Offset
        {
            get
            {
                long x = CarriedX, y = CarriedY;
                if (Cursor is { } cursor)
                {
                    x += cursor.Emitted.X;
                    y += cursor.Emitted.Y;
                }
                if (Back is { } back)
                {
                    x += back.Emitted.X;
                    y += back.Emitted.Y;
                }
                return (x, y);
            }
        }
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
    /// A playback ended by itself: a pass without repeat is complete (raised after its last move went out; with a way back once it is
    /// back, also after <see cref="StopWithReturn"/>), or the player ended it (a refused move, data it cannot play). Not raised for
    /// <see cref="Stop"/>, <see cref="StopAll"/> or a restart by <see cref="Play"/> - the caller knows - nor when the id plays again by the
    /// time it would be raised. On the player's thread, outside its lock.
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

    /// <summary>
    /// Starts a playback from where the pointer is now; one with the same id that still plays starts over from there. One that plays with
    /// a way back first sends the rest of its way back (all of it, if it is still on its way out) at once as one move, with the next tick;
    /// the new pass follows in the tick after, from where the old one began. Any thread.
    /// </summary>
    public void Play(PatternPlayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Data);
        lock (_lock)
        {
            if (_disposed) return;
            int i = IndexOf(request.Id);
            if (i >= 0 && _playbacks[i] is { Curve: not null, Returns: true } p)
            {
                if (!p.PassDue) TurnBack(p, _clock.NowMs, PatternReturn.Jump);
                p.Next = request;
                p.Stopping = false;
                _playbacks.RemoveAt(i);
                _playbacks.Add(p);   // start order
            }
            else
            {
                if (i >= 0) Remove(i);
                _playbacks.Add(new Playback(request, _clock.NowMs));
            }
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

    /// <summary>
    /// Its own hotkey stopped it (released or pressed again): a playback with a way back turns back from where its last move took it - one
    /// already on its way back finishes that - and starts no further pass; <see cref="Finished"/> follows once it is back, and it plays
    /// until then. Without a way back the same as <see cref="Stop"/>. No effect when it does not play. Any thread.
    /// </summary>
    public void StopWithReturn(string id)
    {
        lock (_lock)
        {
            int i = IndexOf(id);
            if (i < 0) return;
            var p = _playbacks[i];
            if (!p.Returns)
            {
                Remove(i);
                return;
            }
            p.Stopping = true;   // the next tick turns it back
            p.Next = null;
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
            Advance(p, now, out int dx, out int dy);
            if (p.Absolute) continue;
            relX += dx;
            relY += dy;
        }

        if ((relX != 0 || relY != 0) && !Sent(_sink.MoveBy(Saturate(relX), Saturate(relY)))) return false;

        if (_absolute > 0)
        {
            _anchorX += relX;   // the relative move took the pointer along: the absolute target follows, so both add up
            _anchorY += relY;
            Playback? sole = null;
            if (_absolute == 1)
                foreach (var p in _playbacks)
                    if (p.Absolute) { sole = p; break; }
            // The only absolute playback on its way back gets back what the edge swallowed of its way out at the same place on the
            // curve: the anchor is then what it was there, so the pointer retraces its real way out - held at the edge where it was
            // held - without a jump, and ends exactly where the pass began.
            if (sole?.Back is { } back) GiveBackSwallowed(sole, back.IsHome ? double.NegativeInfinity : back.CurveMs);

            long tx = _anchorX, ty = _anchorY;
            foreach (var p in _playbacks)
            {
                if (!p.Absolute) continue;
                var (ex, ey) = p.Offset;
                tx += ex;
                ty += ey;
            }
            var (x, y) = ClampToScreen(tx, ty, _env.Monitors);
            // What the edge swallowed is gone: the way on starts at the edge, as with a hand on the mouse. On the way back of the only
            // absolute playback the edge only holds the pointer (that part was given back above).
            if (sole?.Back is null)
            {
                _anchorX += x - tx;
                _anchorY += y - ty;
                // Kept for the way back - unless a relative move of this tick took part in pushing it there.
                if (sole is { Returns: true, Cursor: { } cursor } && (x != tx || y != ty) && relX == 0 && relY == 0)
                    sole.Swallowed.Add((cursor.CurveMs, x - tx, y - ty));
            }
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
    /// One playback's step of this tick: the relative move in <paramref name="dx"/>/<paramref name="dy"/> (an absolute one's lands in its
    /// <see cref="Playback.Offset"/>). Sets <see cref="Playback.Done"/> when it ends with this tick.
    /// </summary>
    private void Advance(Playback p, double now, out int dx, out int dy)
    {
        dx = dy = 0;
        if (p.Curve is null)
        {
            // Its first tick; stopped with its way back before it moved, it is where it began.
            if (p.Stopping || !Begin(p))
            {
                p.Done = true;
                return;
            }
        }
        else if (p.PassDue && (p.Stopping || !NextPass(p, now)))
        {
            p.Done = true;
            return;
        }

        if (p.Back is null)
        {
            if (!p.Stopping)
            {
                // The last move of a pass comes with the false: it is sent like any other.
                bool more = p.Cursor!.Advance(now - p.StartMs, out dx, out dy);
                p.LastMs = now;
                if (more) return;
                if (p.Returns) TurnBack(p, now, p.Request.Return);   // its way back begins with the next tick, never in this move
                else p.Done = true;
                return;
            }
            TurnBack(p, p.LastMs, p.Request.Return);   // stopped on its way out: back from where its last move took it
        }

        if (p.Back!.Advance(now - p.BackStartMs, out dx, out dy)) return;
        // Back at the start with this move.
        if (!p.Stopping && (p.Next is not null || p.Request.Repeat)) p.PassDue = true;
        else p.Done = true;
    }

    /// <summary>
    /// The first tick of a playback: its replay type, its first pass and, for the first absolute playback, the anchor. false = its data
    /// cannot be played (the playback ends without a move).
    /// </summary>
    private bool Begin(Playback p)
    {
        if (!Resolve(p.Request, out var curve, out bool absolute)) return false;
        p.Curve = curve;
        if (absolute) JoinAbsolute(p);
        StartPass(p, p.StartMs);
        return true;
    }

    /// <summary>
    /// The pass after a way back, from where the last one began. Pressed again on the way, it plays the new request: its replay type is
    /// decided anew, like at any start. false = that data cannot be played.
    /// </summary>
    private bool NextPass(Playback p, double now)
    {
        if (p.Next is { } next)
        {
            p.Next = null;
            p.Request = next;
            if (!Resolve(next, out var curve, out bool absolute)) return false;
            p.Curve = curve;
            if (absolute && !p.Absolute) JoinAbsolute(p);
            else if (!absolute && p.Absolute) LeaveAbsolute(p);
        }
        StartPass(p, now);
        return true;
    }

    private void StartPass(Playback p, double startMs)
    {
        // With a way back the player repeats (pass, way back, pass); without one the cursor repeats seamlessly.
        p.Cursor = new PatternCursor(p.Curve!, p.Request.Speed, repeat: p.Request.Repeat && !p.Returns);
        p.Back = null;
        p.CarriedX = p.CarriedY = 0;
        p.Swallowed.Clear();   // given back by now, or not attributable (another absolute playback joined)
        p.StartMs = p.LastMs = startMs;
        p.PassDue = false;
    }

    /// <summary>
    /// Turns the current pass back to where it began, from <paramref name="startMs"/> on. A way back already under way (a press turns it
    /// into a jump) keeps what it sent. What an edge swallowed of the way out is given back by <see cref="Tick"/> as the way back passes
    /// the place where it happened (a jump: at once).
    /// </summary>
    private void TurnBack(Playback p, double startMs, PatternReturn mode)
    {
        var cursor = p.Cursor!;
        if (p.Back is { } back)
        {
            p.CarriedX += back.Emitted.X;
            p.CarriedY += back.Emitted.Y;
        }
        var sent = (cursor.Emitted.X + p.CarriedX, cursor.Emitted.Y + p.CarriedY);
        p.Back = new PatternReturnPath(p.Curve!, p.Request.Speed, cursor.CurveMs, sent, mode);
        p.BackStartMs = startMs;
    }

    /// <summary>
    /// The anchor gets back the parts the edge swallowed of <paramref name="p"/>'s way out beyond <paramref name="curveMs"/> (all of them for
    /// negative infinity: back at the start). Under the lock.
    /// </summary>
    private void GiveBackSwallowed(Playback p, double curveMs)
    {
        var log = p.Swallowed;
        while (log.Count > 0 && log[^1].CurveMs > curveMs)
        {
            var (_, x, y) = log[^1];
            _anchorX -= x;
            _anchorY -= y;
            log.RemoveAt(log.Count - 1);
        }
    }

    /// <summary>The track a request plays (replay type and curve). false = its data cannot be played.</summary>
    private bool Resolve(PatternPlayRequest r, [NotNullWhen(true)] out PatternCurve? curve, out bool absolute)
    {
        var data = r.Data;
        var replay = PatternReplayRules.Resolve(r.Replay, _env.AccelerationOn, _env.PointerVisible, hasRaw: data.Raw.Count > 0);
        // Auto never chooses a track without motion while the other one has some (a recording whose pointer track stayed empty).
        if (r.Replay == PatternReplay.Auto && replay == ResolvedReplay.AbsolutePointer && data.Pointer.Count == 0 && data.Raw.Count > 0)
            replay = ResolvedReplay.RelativeRaw;
        absolute = replay == ResolvedReplay.AbsolutePointer;

        try
        {
            var curves = _curves.GetValue(data, static _ => new Curves());
            curve = replay == ResolvedReplay.RelativeRaw
                ? curves.Raw ??= new PatternCurve(data.Raw, data.DurationMs)
                : curves.Pointer ??= new PatternCurve(data.Pointer, data.DurationMs);
            return true;
        }
        catch (ArgumentException ex)
        {
            HostLog.Warn("Pattern " + r.Id + " cannot be played: " + ex.Message);
            curve = null;
            return false;
        }
    }

    /// <summary>Counts an absolute playback; the first one anchors at the pointer.</summary>
    private void JoinAbsolute(Playback p)
    {
        p.Absolute = true;
        if (_absolute++ > 0) return;
        var c = _env.CursorPosition;
        (_anchorX, _anchorY, _lastX, _lastY) = (c.X, c.Y, c.X, c.Y);
    }

    /// <summary>An absolute playback leaves: its offset stays in the anchor, so the pointer remains where it took it.</summary>
    private void LeaveAbsolute(Playback p)
    {
        var (x, y) = p.Offset;
        _anchorX += x;
        _anchorY += y;
        _absolute--;
        p.Absolute = false;
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
        if (p.Absolute) LeaveAbsolute(p);
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
