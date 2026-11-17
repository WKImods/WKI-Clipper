using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.ClickerHost;
using WKI_Clipper.Models;
using WKI_Clipper.Native;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Mouse-patterns design 4.3/4.4/5.1: the helper's pattern player on virtual time - relative and absolute replay, speed, stop and
/// restart, several patterns at once, the edges of a multi-monitor desktop, a refused SendInput, the sleeping thread. The sink is a
/// <see cref="FakeInput"/> (its relative moves take the fake cursor along 1:1, like linear pointer ballistics).
/// </summary>
public sealed class PatternPlayerTests
{
    private sealed record Note(string What, double At, int Sent);

    private sealed class Rig
    {
        public readonly FakeClock Clock = new();
        public readonly FakeWaiter Waiter;
        public readonly FakeEnvironment Host = new();
        public readonly FakePatternEnvironment Env;
        public readonly FakeInput Input;
        public readonly PatternPlayer Player;

        /// <summary>The player's events in order, with the virtual time and how many inputs had gone out by then.</summary>
        public readonly List<Note> Log = new();

        public Rig()
        {
            Waiter = new FakeWaiter(Clock);
            Input = new FakeInput(Clock, Host);
            Env = new FakePatternEnvironment(Host);
            Player = new PatternPlayer(Input, Waiter, Clock, Env, ownThread: false);
            Player.Finished += id => Log.Add(new Note("finished:" + id, Clock.Now, Input.Events.Count));
            Player.Blocked += b => Log.Add(new Note("blocked:" + b, Clock.Now, Input.Events.Count));
        }

        public void Play(string id, PatternData data, double speed = 1, bool repeat = false, PatternReplay replay = PatternReplay.Auto,
            PatternReturn ret = PatternReturn.Off)
            => Player.Play(new PatternPlayRequest(id, data, speed, repeat, replay, ret));

        public void Run() => Player.RunSync();

        public string[] Notes => Log.Select(n => n.What).ToArray();

        /// <summary>Everything the relative moves added up to.</summary>
        public (long X, long Y) Moved => (Input.Of("moveby").Sum(e => (long)e.A), Input.Of("moveby").Sum(e => (long)e.B));

        /// <summary>The absolute moves' targets, in order.</summary>
        public List<PixelPoint> Targets => Input.Of("move").Select(e => new PixelPoint(e.A, e.B)).ToList();
    }

    /// <summary>
    /// A waiter for a player with its own thread: a tick's wait moves the <see cref="FakeClock"/> on at once, the idle wait (no deadline)
    /// blocks until <see cref="Wake"/> and is counted - so a test sees whether the thread sleeps.
    /// </summary>
    private sealed class GateWaiter : IWaiter
    {
        private readonly FakeClock _clock;
        private readonly ManualResetEventSlim _wake = new(false);
        private int _idleWaits, _sessionsBegun, _sessionsEnded;

        public GateWaiter(FakeClock clock) => _clock = clock;

        public int IdleWaits => Volatile.Read(ref _idleWaits);
        public int SessionsBegun => Volatile.Read(ref _sessionsBegun);
        public int SessionsEnded => Volatile.Read(ref _sessionsEnded);

        public bool WaitUntil(double deadlineMs)
        {
            if (_wake.IsSet) return false;
            if (double.IsPositiveInfinity(deadlineMs))
            {
                Interlocked.Increment(ref _idleWaits);
                _wake.Wait();
                return false;
            }
            if (deadlineMs > _clock.Now) _clock.Now = deadlineMs;
            return true;
        }

        public void Wake() => _wake.Set();
        public void ClearWake() => _wake.Reset();
        public void BeginSession() => Interlocked.Increment(ref _sessionsBegun);
        public void EndSession() => Interlocked.Increment(ref _sessionsEnded);
        public void Dispose() { }
    }

    /// <summary>What a real <see cref="InputInjector"/> hands to SendInput.</summary>
    private sealed class SentInputs : ISendInput
    {
        public readonly List<User32.INPUT> Sent = new();

        public uint Send(User32.INPUT[] inputs, int count)
        {
            for (int i = 0; i < count; i++) Sent.Add(inputs[i]);
            return (uint)count;
        }
    }

    // ---- tracks ----

    private static PatternTrack Bins(int n, Func<int, (int At, int Dx, int Dy)> bin)
    {
        var at = new int[n]; var dx = new int[n]; var dy = new int[n];
        for (int i = 0; i < n; i++) (at[i], dx[i], dy[i]) = bin(i);
        return new PatternTrack(at, dx, dy);
    }

    private static PatternTrack Track(params (int At, int Dx, int Dy)[] bins) => Bins(bins.Length, i => bins[i]);

    /// <summary><paramref name="n"/> bins of the same motion, one every <paramref name="step"/> ms from 0.</summary>
    private static PatternTrack Line(int n, int dx, int dy, int step = 1) => Bins(n, i => (i * step, dx, dy));

    /// <summary>1 px to the right per ms from 0 to <paramref name="n"/> ms (the bin at k ms moves over (k-1, k]): its way back is 1 px per ms too.</summary>
    private static PatternTrack Ramp(int n) => Bins(n, i => (i + 1, 1, 0));

    /// <summary>Uneven values, pauses and both directions, so rounding has every chance to drift.</summary>
    private static PatternTrack Wobbly(int n)
    {
        int t = 0;
        return Bins(n, i =>
        {
            int at = t;
            t += 1 + i % 4;
            return (at, i % 7 - 2, i % 7 == 2 ? 3 : -(i % 3) - 1);
        });
    }

    /// <summary>The samples of a recording: its length is where the later track ends.</summary>
    private static PatternData Data(PatternTrack raw, PatternTrack pointer)
    {
        int end = Math.Max(raw.Count > 0 ? raw.AtMs[^1] : 0, pointer.Count > 0 ? pointer.AtMs[^1] : 0);
        return new PatternData(raw, pointer, end);
    }

    private static IEnumerable<(int Dx, int Dy)> Steps(IReadOnlyList<PixelPoint> path)
        => path.Zip(path.Skip(1), (a, b) => (b.X - a.X, b.Y - a.Y));

    private static void NoJump(IReadOnlyList<PixelPoint> path)
        => Assert.All(Steps(path), s => Assert.True(Math.Abs(s.Dx) <= 1 && Math.Abs(s.Dy) <= 1, $"a jump of {s}"));

    // ---- relative replay ----

    [Theory]
    [InlineData(PatternReplay.Raw)]
    [InlineData(PatternReplay.Auto)]   // linear pointer ballistics (the rig's default): Auto plays the raw counts too
    public void Relative_replay_sums_to_track_total(PatternReplay replay)
    {
        var rig = new Rig();
        var raw = Wobbly(300);
        rig.Play("a", Data(raw, Line(5, 9, 9)), replay: replay);

        rig.Run();

        Assert.Equal(raw.Total, rig.Moved);
        Assert.Equal(raw.AtMs[^1], rig.Input.Events.Max(e => e.T));             // the last bin goes out at its own time
        Assert.Empty(rig.Input.Of("move"));                                      // never an absolute move
        Assert.Equal(0, rig.Input.ZeroMoves);                                    // never a 0/0 move
        Assert.All(rig.Input.Events.GroupBy(e => e.T), g => Assert.Single(g));   // one SendInput per tick
        Assert.Equal(new[] { "finished:a" }, rig.Notes);
        Assert.Empty(rig.Player.Playing);
    }

    [Theory]
    [InlineData(1.0, 100)]
    [InlineData(2.0, 50)]
    [InlineData(4.0, 25)]
    [InlineData(0.5, 200)]
    public void Speed_two_finishes_in_half_the_time(double speed, double end)
    {
        var rig = new Rig();
        var raw = Line(51, 2, -1, step: 2);   // 0 .. 100 ms
        rig.Play("a", Data(raw, PatternTrack.Empty), speed: speed);

        rig.Run();

        Assert.Equal(raw.Total, rig.Moved);   // the same shape at every speed
        Assert.Equal(end, rig.Input.Of("moveby").Max(e => e.T));
        Assert.Equal(end, Assert.Single(rig.Log).At);
    }

    [Fact]
    public void Stop_mid_pattern_sends_nothing_more()
    {
        var rig = new Rig();
        rig.Play("a", Data(Line(50, 1, 0, step: 2), PatternTrack.Empty), repeat: true);
        rig.Waiter.At(37.5, () => rig.Player.Stop("a"));

        rig.Run();

        Assert.NotEmpty(rig.Input.Events);
        Assert.All(rig.Input.Events, e => Assert.True(e.T < 37.5, $"moved at {e.T}"));
        Assert.Empty(rig.Player.Playing);
        Assert.Empty(rig.Log);                 // a requested stop raises no Finished: the caller knows
        Assert.Equal(rig.Waiter.SessionsBegun, rig.Waiter.SessionsEnded);

        rig.Player.Stop("a");                  // again, or something that never played: nothing happens
        rig.Player.Stop("unknown");
        rig.Run();
        Assert.Empty(rig.Log);
    }

    [Fact]
    public void StopAll_ends_every_playback_before_the_next_tick()
    {
        var rig = new Rig();
        rig.Play("a", Data(Line(50, 1, 0), PatternTrack.Empty), repeat: true);
        rig.Play("b", Data(PatternTrack.Empty, Line(50, 0, 1)), repeat: true, replay: PatternReplay.Pointer);
        IReadOnlyList<string>? playing = null, afterStop = null;
        rig.Waiter.At(5, () => playing = rig.Player.Playing);
        rig.Waiter.At(12.5, () =>
        {
            rig.Player.StopAll();
            afterStop = rig.Player.Playing;
        });

        rig.Run();

        Assert.Equal(new[] { "a", "b" }, playing);   // in start order
        Assert.Empty(afterStop!);                    // at once, not at the next tick
        Assert.All(rig.Input.Events, e => Assert.True(e.T < 12.5, $"moved at {e.T}"));
        Assert.Empty(rig.Log);
    }

    [Fact]
    public void Two_patterns_add_up()
    {
        var rig = new Rig();
        rig.Play("a", Data(Line(40, 1, 0), PatternTrack.Empty));                                    // 0 .. 39 ms
        rig.Waiter.At(10, () => rig.Play("b", Data(Line(20, 0, 2), PatternTrack.Empty)));            // joins at 10 ms, 20 ms long

        rig.Run();

        Assert.Equal((40L, 40L), rig.Moved);
        Assert.All(rig.Input.Events.GroupBy(e => e.T), g => Assert.Single(g));                       // one move per tick carries both
        Assert.All(rig.Input.Of("moveby").Where(e => e.T is >= 10 and <= 29), e => Assert.Equal((1, 2), (e.A, e.B)));
        Assert.Equal(new[] { "finished:b", "finished:a" }, rig.Notes);
        Assert.Equal(new[] { 29.0, 39.0 }, rig.Log.Select(n => n.At));
    }

    [Fact]
    public void Playing_again_restarts_from_the_beginning_without_Finished()
    {
        var rig = new Rig();
        var data = Data(Line(20, 1, 0), PatternTrack.Empty);
        rig.Play("a", data);
        rig.Waiter.At(10, () => rig.Play("a", data));

        rig.Run();

        Assert.Equal((30L, 0L), rig.Moved);                // 10 ms of the first pass, then a whole new one
        Assert.Equal(new[] { "finished:a" }, rig.Notes);    // only the second pass ends by itself
        Assert.Equal(29, rig.Log.Single().At);
    }

    [Fact]
    public void Non_repeating_pattern_raises_Finished()
    {
        var rig = new Rig();
        var raw = Line(10, 3, -2, step: 3);   // 0 .. 27 ms
        rig.Play("a", Data(raw, PatternTrack.Empty));

        rig.Run();

        var note = Assert.Single(rig.Log);
        Assert.Equal("finished:a", note.What);
        Assert.Equal(27, note.At);
        Assert.Equal(rig.Input.Events.Count, note.Sent);   // raised after its last move went out
        Assert.Equal(raw.Total, rig.Moved);

        rig.Play("r", Data(raw, PatternTrack.Empty), repeat: true);   // a repeating one never ends by itself
        rig.Waiter.At(200, () => rig.Player.Stop("r"));
        rig.Run();
        Assert.Single(rig.Log);
    }

    // ---- absolute replay ----

    [Fact]
    public void Absolute_replay_is_anchor_plus_offset()
    {
        var rig = new Rig();
        rig.Host.Cursor = new PixelPoint(500, 400);
        var pointer = Track((0, 3, 1), (5, 10, -4), (10, -2, 7));
        rig.Play("a", Data(Line(10, 50, 50), pointer), replay: PatternReplay.Pointer);
        rig.Waiter.At(7, () => rig.Host.Cursor = new PixelPoint(900, 900));   // the hand moves the mouse meanwhile: Zeiger takes over the pointer

        rig.Run();

        Assert.Equal(new[] { new PixelPoint(503, 401), new PixelPoint(513, 397), new PixelPoint(511, 404) }, rig.Targets);
        Assert.Equal(new[] { 0.0, 5.0, 10.0 }, rig.Input.Of("move").Select(e => e.T));   // a move only when the target changed
        Assert.Empty(rig.Input.Of("moveby"));
        Assert.Equal(new[] { "finished:a" }, rig.Notes);
    }

    [Fact]
    public void Absolute_replay_on_negative_origin_desktop()
    {
        // A monitor left of and above the primary one; the pattern runs 30 px left into the desktop's edge, then 30 px back.
        var monitors = new List<PixelRect> { new(-1920, -300, 1920, 1200), new(0, 0, 2560, 1440) };
        var start = new PixelPoint(-1900, -280);
        var data = Data(PatternTrack.Empty, Bins(60, i => (i, i < 30 ? -1 : 1, 0)));
        var rig = new Rig();
        rig.Host.MonitorList = monitors;
        rig.Host.Cursor = start;
        rig.Play("a", data, replay: PatternReplay.Pointer);

        rig.Run();

        var targets = rig.Targets;
        Assert.All(targets, p => Assert.Contains(monitors, m => m.Contains(p)));   // clamped onto the screen
        Assert.Equal(new PixelPoint(-1901, -280), targets[0]);
        Assert.Equal(-1920, targets.Min(p => p.X));                                // it stops at the edge ...
        Assert.Equal(new PixelPoint(-1890, -280), targets[^1]);                    // ... and the way back starts there, as with a hand on the mouse
        Assert.Equal(50, targets.Count);                                           // nothing is sent while the target stands at the edge
        NoJump(targets);

        // The same playback through the real injector: every absolute move lands on its target pixel of the virtual desktop.
        var vs = new PixelRect(-1920, -300, 4480, 1740);
        var sent = new SentInputs();
        var clock = new FakeClock();
        var host = new FakeEnvironment { Cursor = start, MonitorList = monitors };
        var player = new PatternPlayer(new InputInjector(sent, () => vs, vk => vk), new FakeWaiter(clock), clock,
            new FakePatternEnvironment(host), ownThread: false);
        player.Play(new PatternPlayRequest("a", data, 1, false, PatternReplay.Pointer));
        player.RunSync();

        const uint absolute = User32.MOUSEEVENTF_MOVE | User32.MOUSEEVENTF_ABSOLUTE | User32.MOUSEEVENTF_VIRTUALDESK;
        Assert.All(sent.Sent, i =>
        {
            Assert.Equal(absolute, i.u.mi.dwFlags);
            Assert.Equal(InputInjector.Tag, (ulong)i.u.mi.dwExtraInfo);
        });
        var landed = sent.Sent.Select(i => new PixelPoint(
            vs.X + (int)((long)i.u.mi.dx * vs.W / 65536), vs.Y + (int)((long)i.u.mi.dy * vs.H / 65536))).ToList();
        Assert.Equal(targets, landed);
    }

    [Fact]
    public void Absolute_replay_never_jumps_over_a_gap_between_monitors()
    {
        // No screen above the primary monitor (x >= 0); the left monitor reaches higher. The pattern goes up 20 px, then left 200 px.
        var monitors = new List<PixelRect> { new(-1920, -300, 1920, 1200), new(0, 0, 2560, 1440) };
        var rig = new Rig();
        rig.Host.MonitorList = monitors;
        rig.Host.Cursor = new PixelPoint(100, 10);
        rig.Play("a", Data(PatternTrack.Empty, Bins(220, i => (i, i < 20 ? 0 : -1, i < 20 ? -1 : 0))), replay: PatternReplay.Pointer);

        rig.Run();

        var targets = rig.Targets;
        Assert.All(targets, p => Assert.Contains(monitors, m => m.Contains(p)));
        NoJump(targets);   // the bounding box would have let the target float 10 px above the primary monitor and drop onto the left one
        Assert.Equal(new PixelPoint(-100, 0), targets[^1]);
    }

    [Fact]
    public void Absolute_patterns_join_and_leave_without_a_jump()
    {
        var rig = new Rig();
        rig.Host.Cursor = new PixelPoint(300, 300);
        rig.Play("right", Data(PatternTrack.Empty, Line(60, 1, 0)), repeat: true, replay: PatternReplay.Pointer);
        rig.Waiter.At(20, () => rig.Play("down", Data(PatternTrack.Empty, Line(30, 0, 1)), replay: PatternReplay.Pointer));
        rig.Waiter.At(30, () => rig.Player.Stop("right"));   // released while the other one runs on

        rig.Run();

        NoJump(rig.Targets);
        Assert.Equal(new PixelPoint(330, 330), rig.Targets[^1]);
        Assert.Equal(new[] { "finished:down" }, rig.Notes);
    }

    [Fact]
    public void Relative_and_absolute_patterns_add_up_without_a_jump()
    {
        var rig = new Rig();
        rig.Host.Cursor = new PixelPoint(500, 500);
        rig.Play("game", Data(Line(100, 1, 0), PatternTrack.Empty), replay: PatternReplay.Raw);                                       // 100 px right
        rig.Waiter.At(20, () => rig.Play("gui", Data(PatternTrack.Empty, Line(50, 0, 1)), replay: PatternReplay.Pointer));          // 50 px down

        rig.Run();

        Assert.Equal(new PixelPoint(600, 550), rig.Host.Cursor);
        // Replays the sends: an absolute move never takes back what a relative one did.
        var path = new List<PixelPoint> { new(500, 500) };
        foreach (var e in rig.Input.Events)
            path.Add(e.Kind == "moveby" ? new PixelPoint(path[^1].X + e.A, path[^1].Y + e.B) : new PixelPoint(e.A, e.B));
        NoJump(path);
    }

    // ---- back to the start (pattern return design 1) ----

    /// <summary>Replays the relative moves as positions: none leaves 0 .. <paramref name="max"/> px, so every pass starts at the start.</summary>
    private static void StaysWithin(IEnumerable<InputEvent> moves, long max)
    {
        long x = 0;
        foreach (var e in moves)
        {
            x += e.A;
            Assert.InRange(x, 0, max);
        }
    }

    [Fact]
    public void Jump_after_a_pass_goes_back_in_one_move()
    {
        var rig = new Rig();
        rig.Play("a", Data(Line(20, 1, 0), PatternTrack.Empty), ret: PatternReturn.Jump);   // 0 .. 19 ms

        rig.Run();

        Assert.Equal((0L, 0L), rig.Moved);
        var moves = rig.Input.Of("moveby").ToList();
        Assert.Equal(21, moves.Count);
        Assert.All(moves.Take(20), e => Assert.Equal((1, 0), (e.A, e.B)));
        Assert.Equal(19, moves[^2].T);
        Assert.Equal(new InputEvent(20, "moveby", -20, 0), moves[^1]);   // one tick after the pass's last move, never merged into it
        Assert.Equal(new[] { "finished:a" }, rig.Notes);
        Assert.Equal(20, rig.Log.Single().At);                           // Finished once it is back
        Assert.Empty(rig.Player.Playing);
    }

    [Fact]
    public void Reverse_after_a_pass_retraces_it_and_ends_at_the_start()
    {
        var rig = new Rig();
        rig.Play("a", Data(Ramp(20), PatternTrack.Empty), ret: PatternReturn.Reverse);
        bool playingOnTheWayBack = false;
        rig.Waiter.At(30, () => playingOnTheWayBack = rig.Player.IsPlaying("a"));

        rig.Run();

        Assert.Equal((0L, 0L), rig.Moved);
        var moves = rig.Input.Of("moveby").ToList();
        Assert.Equal(Enumerable.Range(1, 20).Select(t => (double)t), moves.Take(20).Select(e => e.T));    // out: 1 .. 20 ms
        Assert.All(moves.Take(20), e => Assert.Equal((1, 0), (e.A, e.B)));
        Assert.Equal(Enumerable.Range(21, 20).Select(t => (double)t), moves.Skip(20).Select(e => e.T));   // back: 21 .. 40 ms
        Assert.All(moves.Skip(20), e => Assert.Equal((-1, 0), (e.A, e.B)));
        Assert.True(playingOnTheWayBack);   // the way back belongs to the playback
        Assert.Equal(new[] { "finished:a" }, rig.Notes);
        Assert.Equal(40, rig.Log.Single().At);
    }

    [Fact]
    public void Repeat_with_a_return_starts_every_pass_at_the_start()
    {
        var rig = new Rig();
        rig.Play("a", Data(Line(20, 1, 0), PatternTrack.Empty), repeat: true, ret: PatternReturn.Jump);
        rig.Waiter.At(65, () => rig.Player.StopWithReturn("a"));

        rig.Run();

        Assert.Equal((0L, 0L), rig.Moved);
        var moves = rig.Input.Of("moveby").ToList();
        var jumps = moves.Where(e => e.A == -20).ToList();
        Assert.True(jumps.Count >= 3, $"only {jumps.Count} whole passes");
        Assert.All(jumps, j => Assert.Equal(j.T + 1, moves.First(e => e.T > j.T).T));   // the next pass follows in the next tick
        StaysWithin(moves, 20);
        Assert.Equal(new[] { "finished:a" }, rig.Notes);
        Assert.Empty(rig.Player.Playing);
    }

    [Fact]
    public void Releasing_during_a_return_finishes_it_without_a_further_pass()
    {
        var rig = new Rig();
        rig.Play("a", Data(Line(20, 1, 0), PatternTrack.Empty), repeat: true, ret: PatternReturn.Reverse);
        rig.Waiter.At(25, () => rig.Player.StopWithReturn("a"));   // on the way back of the first pass (20 .. 38 ms)

        rig.Run();

        Assert.All(rig.Input.Of("moveby").Where(e => e.T >= 25), e => Assert.True(e.A < 0, $"a forward move at {e.T}"));
        Assert.Equal((0L, 0L), rig.Moved);
        Assert.Equal(new[] { "finished:a" }, rig.Notes);
        Assert.Equal(38, rig.Log.Single().At);
    }

    [Fact]
    public void Releasing_between_the_way_back_and_the_next_pass_starts_no_further_pass()
    {
        var rig = new Rig();
        rig.Play("a", Data(Line(20, 1, 0), PatternTrack.Empty), repeat: true, ret: PatternReturn.Jump);
        rig.Waiter.At(20.5, () => rig.Player.StopWithReturn("a"));   // back at 20 ms, the next pass was due at 21 ms

        rig.Run();

        Assert.Equal(20, rig.Input.Events.Max(e => e.T));
        Assert.Equal((0L, 0L), rig.Moved);
        Assert.Equal(new[] { "finished:a" }, rig.Notes);
    }

    [Theory]
    [InlineData(PatternReturn.Jump)]
    [InlineData(PatternReturn.Reverse)]
    public void Stopping_mid_pass_with_return_goes_back_from_there(PatternReturn ret)
    {
        var rig = new Rig();
        rig.Play("a", Data(Line(20, 1, 0), PatternTrack.Empty), ret: ret);
        rig.Waiter.At(7.5, () => rig.Player.StopWithReturn("a"));

        rig.Run();

        Assert.Equal((0L, 0L), rig.Moved);
        Assert.All(rig.Input.Of("moveby").Where(e => e.T > 7.5), e => Assert.True(e.A < 0, $"a forward move at {e.T}"));
        Assert.Equal(new[] { "finished:a" }, rig.Notes);
        if (ret == PatternReturn.Jump) Assert.Equal(new InputEvent(8, "moveby", -8, 0), rig.Input.Events[^1]);
        else Assert.Equal(14, rig.Log.Single().At);   // back over the 7 ms it drove
    }

    [Fact]
    public void Released_before_its_first_move_is_back_already()
    {
        var rig = new Rig();
        rig.Play("a", Data(Line(20, 1, 0), PatternTrack.Empty), ret: PatternReturn.Reverse);
        rig.Player.StopWithReturn("a");

        rig.Run();

        Assert.Empty(rig.Input.Events);
        Assert.Equal(new[] { "finished:a" }, rig.Notes);
    }

    [Fact]
    public void Pressing_again_on_the_way_back_jumps_the_rest_and_starts_at_the_start()
    {
        var rig = new Rig();
        var data = Data(Line(20, 1, 0), PatternTrack.Empty);
        rig.Play("a", data, ret: PatternReturn.Reverse);
        rig.Waiter.At(25, () => rig.Play("a", data, ret: PatternReturn.Reverse));   // on the way back: 5 of 20 px are back

        rig.Run();

        Assert.Equal((0L, 0L), rig.Moved);
        var moves = rig.Input.Of("moveby").ToList();
        Assert.Equal(new InputEvent(25, "moveby", -15, 0), Assert.Single(moves, e => e.T == 25));   // the rest at once, in one move
        Assert.Equal(new InputEvent(26, "moveby", 1, 0), moves.First(e => e.T > 25));                // then the new pass from the start
        StaysWithin(moves, 20);
        Assert.Equal(new[] { "finished:a" }, rig.Notes);   // a restart is no end
    }

    [Fact]
    public void Pressing_again_on_the_way_out_jumps_all_of_it_back()
    {
        var rig = new Rig();
        var data = Data(Line(20, 1, 0), PatternTrack.Empty);
        rig.Play("a", data, ret: PatternReturn.Reverse);
        rig.Waiter.At(7.5, () => rig.Play("a", data, ret: PatternReturn.Reverse));

        rig.Run();

        Assert.Equal((0L, 0L), rig.Moved);
        Assert.Equal(new InputEvent(7.5, "moveby", -8, 0), Assert.Single(rig.Input.Of("moveby"), e => e.A < 0 && e.T < 20));
        StaysWithin(rig.Input.Of("moveby"), 20);
    }

    [Theory]
    [InlineData(PatternReplay.Raw)]
    [InlineData(PatternReplay.Pointer)]
    public void Many_presses_never_drift(PatternReplay replay)
    {
        var rig = new Rig();
        var start = new PixelPoint(700, 500);
        rig.Host.Cursor = start;
        var track = Wobbly(60);
        var data = replay == PatternReplay.Raw ? Data(track, PatternTrack.Empty) : Data(PatternTrack.Empty, track);
        void Press() => rig.Play("a", data, speed: 1.37, replay: replay, ret: PatternReturn.Reverse);
        Press();
        for (int k = 0; k < 10; k++)
        {
            rig.Waiter.At(9 * k + 3.5, () => rig.Player.StopWithReturn("a"));
            if (k < 9) rig.Waiter.At(9 * k + 5.25, Press);   // again while it is on its way back
        }

        rig.Run();

        Assert.Empty(rig.Player.Playing);
        if (replay == PatternReplay.Raw) Assert.Equal((0L, 0L), rig.Moved);
        else Assert.Equal(start, rig.Targets[^1]);
        Assert.Equal(start, rig.Host.Cursor);
    }

    [Theory]
    [InlineData(PatternReturn.Jump)]
    [InlineData(PatternReturn.Reverse)]
    public void Pointer_return_at_the_screen_edge_ends_exactly_at_the_start_point(PatternReturn ret)
    {
        var rig = new Rig();
        rig.Host.Cursor = new PixelPoint(100, 50);
        // 200 px up: 150 of them run into the top edge of the screen.
        rig.Play("a", Data(PatternTrack.Empty, Line(20, 0, -10)), replay: PatternReplay.Pointer, ret: ret);

        rig.Run();

        var targets = rig.Targets;
        Assert.Contains(new PixelPoint(100, 0), targets);
        Assert.Equal(new PixelPoint(100, 50), targets[^1]);            // exactly where the pass began
        Assert.All(targets, p => Assert.InRange(p.Y, 0, 50));          // the way back never overshoots it: the edge held the pointer
        Assert.Equal(new[] { "finished:a" }, rig.Notes);
    }

    [Fact]
    public void Reverse_at_double_tempo_takes_as_long_as_the_way_out()
    {
        var rig = new Rig();
        rig.Play("a", Data(Line(20, 1, 0), PatternTrack.Empty), speed: 2, ret: PatternReturn.Reverse);   // 19 ms of curve

        rig.Run();

        var moves = rig.Input.Of("moveby").ToList();
        Assert.Equal(10, moves.Last(e => e.A > 0).T);   // out: 0 .. 10 ms
        Assert.Equal(20, moves[^1].T);                  // back: 10 .. 20 ms
        Assert.Equal(20, rig.Log.Single().At);
        Assert.Equal((0L, 0L), rig.Moved);
    }

    [Fact]
    public void Stop_and_StopAll_never_return()
    {
        var rig = new Rig();
        rig.Play("a", Data(Line(20, 1, 0), PatternTrack.Empty), repeat: true, ret: PatternReturn.Jump);
        rig.Waiter.At(7.5, () => rig.Player.Stop("a"));

        rig.Run();

        Assert.All(rig.Input.Events, e => Assert.True(e.T < 7.5, $"moved at {e.T}"));
        Assert.Equal((8L, 0L), rig.Moved);
        Assert.Empty(rig.Log);

        var all = new Rig();
        all.Play("a", Data(Line(20, 1, 0), PatternTrack.Empty), ret: PatternReturn.Reverse);
        all.Play("b", Data(PatternTrack.Empty, Line(20, 0, 1)), replay: PatternReplay.Pointer, ret: PatternReturn.Jump);
        all.Waiter.At(25, () => all.Player.StopAll());   // a on its way back, b already back
        all.Run();

        Assert.All(all.Input.Events, e => Assert.True(e.T < 25, $"moved at {e.T}"));
        Assert.NotEqual((0L, 0L), all.Moved);
        Assert.Equal(new[] { "finished:b" }, all.Notes);
    }

    [Fact]
    public void StopWithReturn_without_a_return_is_Stop()
    {
        var rig = new Rig();
        rig.Play("a", Data(Line(20, 1, 0), PatternTrack.Empty), repeat: true);
        bool? playing = null;
        rig.Waiter.At(7.5, () =>
        {
            rig.Player.StopWithReturn("a");
            playing = rig.Player.IsPlaying("a");
        });
        rig.Player.StopWithReturn("unknown");   // nothing plays under that id: nothing happens

        rig.Run();

        Assert.False(playing);
        Assert.Equal((8L, 0L), rig.Moved);
        Assert.Empty(rig.Log);
    }

    [Fact]
    public void A_refused_move_on_the_way_back_ends_it_there()
    {
        var rig = new Rig();
        rig.Play("a", Data(Line(20, 1, 0), PatternTrack.Empty), repeat: true, ret: PatternReturn.Reverse);
        rig.Waiter.At(25, () => rig.Input.FailEverything = true);

        rig.Run();

        Assert.Equal(25, rig.Clock.Now);
        Assert.Empty(rig.Player.Playing);
        Assert.Equal(new[] { "finished:a", "blocked:True" }, rig.Notes);
    }

    // ---- the replay type ----

    [Fact]
    public void Auto_is_decided_once_per_start()
    {
        var rig = new Rig();
        rig.Env.Acceleration = true;   // non-linear ballistics and a visible pointer: Zeiger
        var data = Data(Line(20, 1, 0), Line(20, 2, 0));
        rig.Play("a", data);
        rig.Waiter.At(5, () => rig.Env.Visible = false);   // a game hides the pointer meanwhile: this playback stays as it began

        rig.Run();

        Assert.Empty(rig.Input.Of("moveby"));
        Assert.Equal(new PixelPoint(960 + 40, 540), rig.Targets[^1]);

        rig.Play("a", data);           // the next start sees the hidden pointer: the raw counts
        rig.Run();
        Assert.Equal((20L, 0L), rig.Moved);
    }

    [Fact]
    public void Auto_never_picks_a_track_without_motion()
    {
        var rig = new Rig();
        rig.Env.Acceleration = true;   // Zeiger would be the choice, but only the raw track has motion
        rig.Play("a", Data(Line(10, 1, 1), PatternTrack.Empty));

        rig.Run();

        Assert.Equal((10L, 10L), rig.Moved);
        Assert.Empty(rig.Input.Of("move"));
    }

    [Fact]
    public void Malformed_data_ends_the_playback_without_moving()
    {
        var rig = new Rig();
        var broken = new PatternTrack(new[] { 5, 3 }, new[] { 1, 1 }, new[] { 0, 0 });   // times not increasing
        rig.Play("bad", new PatternData(broken, PatternTrack.Empty, 5), replay: PatternReplay.Raw);
        rig.Play("good", Data(Line(5, 1, 0), PatternTrack.Empty));

        rig.Run();

        Assert.Equal((5L, 0L), rig.Moved);
        Assert.Equal(new[] { "finished:bad", "finished:good" }, rig.Notes);
    }

    // ---- a refused move ----

    [Fact]
    public void Failed_send_stops_all_and_raises_Blocked()
    {
        var rig = new Rig();
        rig.Play("a", Data(Line(100, 1, 0), PatternTrack.Empty), repeat: true);
        rig.Play("b", Data(PatternTrack.Empty, Line(100, 0, 1)), repeat: true, replay: PatternReplay.Pointer);
        rig.Waiter.At(20, () => rig.Input.FailEverything = true);   // an elevated window came to the front

        rig.Run();

        Assert.Equal(20, rig.Clock.Now);   // no tick after the refused one
        Assert.Empty(rig.Player.Playing);
        Assert.Equal(new[] { "finished:a", "finished:b", "blocked:True" }, rig.Notes);
        Assert.Equal(rig.Waiter.SessionsBegun, rig.Waiter.SessionsEnded);

        rig.Input.FailEverything = false;  // the next start goes through again
        rig.Play("a", Data(Line(3, 1, 0), PatternTrack.Empty));
        rig.Run();
        Assert.Equal(new[] { "finished:a", "finished:b", "blocked:True", "blocked:False", "finished:a" }, rig.Notes);
    }

    // ---- the thread ----

    [Fact]
    public async Task Idle_player_does_not_tick()
    {
        // Without a thread: nothing to play is no tick, no wait, no question to Windows.
        var rig = new Rig();
        rig.Player.RunSync();
        Assert.Equal(0, rig.Clock.Now);
        Assert.Empty(rig.Input.Events);
        Assert.Equal(0, rig.Env.Reads);
        Assert.Equal(0, rig.Waiter.SessionsBegun);

        // With its own thread: asleep until a pattern comes, and back to sleep once it is over.
        var clock = new FakeClock();
        var host = new FakeEnvironment();
        var input = new FakeInput(clock, host);
        var env = new FakePatternEnvironment(host);
        var waiter = new GateWaiter(clock);
        var player = new PatternPlayer(input, waiter, clock, env);
        try
        {
            await Wait.Until(() => waiter.IdleWaits == 1);
            Assert.Equal(0, clock.Now);
            Assert.Empty(input.Events);
            Assert.Equal(0, env.Reads);

            var finished = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            player.Finished += id => finished.TrySetResult(id);
            player.Play(new PatternPlayRequest("a", Data(Line(30, 1, 1), PatternTrack.Empty), 1, false, PatternReplay.Auto));
            Assert.Equal("a", await finished.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            await Wait.Until(() => waiter.IdleWaits == 2);

            Assert.Equal(29, clock.Now);
            Assert.Equal(30, input.Of("moveby").Count());
            Assert.Equal(1, waiter.SessionsBegun);
            Assert.Equal(1, waiter.SessionsEnded);

            await Task.Delay(50);
            Assert.Equal(2, waiter.IdleWaits);   // still asleep: no tick without a pattern
            Assert.Equal(29, clock.Now);
        }
        finally { player.Dispose(); }

        player.Play(new PatternPlayRequest("a", Data(Line(30, 1, 1), PatternTrack.Empty), 1, false, PatternReplay.Auto));
        Assert.Empty(player.Playing);            // disposed: plays nothing
    }

    // ---- Windows ----

    [Fact]
    public void The_windows_environment_answers_without_throwing()
    {
        var monitors = new List<PixelRect> { new(0, 0, 1920, 1080) };
        var env = new WindowsPatternEnvironment(() => monitors);

        _ = env.PointerVisible;
        _ = env.AccelerationOn;
        _ = env.CursorPosition;
        Assert.Same(monitors, env.Monitors);
    }
}
