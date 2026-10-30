using System;
using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.ClickerHost;
using WKI_Clipper.Models;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>A clock that only moves when a test says so (or the fake waiter sleeps).</summary>
internal sealed class FakeClock : IClock
{
    public double Now;
    public double NowMs => Now;
    public DateTime UtcNow => new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc).AddMilliseconds(Now);
}

/// <summary>
/// Sleeps by moving the <see cref="FakeClock"/>. Actions scheduled with <see cref="At"/> run at their virtual time, in order, while the
/// engine is "asleep" - that is how a test moves the cursor, presses a key or asks for a stop in the middle of a run.
/// </summary>
internal sealed class FakeWaiter : IWaiter
{
    private readonly FakeClock _clock;
    private readonly List<(double At, Action Run)> _events = new();
    private bool _wake;

    public int SessionsBegun;
    public int SessionsEnded;

    public FakeWaiter(FakeClock clock) => _clock = clock;

    public void At(double timeMs, Action action)
    {
        _events.Add((timeMs, action));
        _events.Sort((a, b) => a.At.CompareTo(b.At));
    }

    public bool WaitUntil(double deadlineMs)
    {
        while (true)
        {
            if (_wake) return false;
            if (_events.Count > 0 && _events[0].At <= deadlineMs)
            {
                var (at, run) = _events[0];
                _events.RemoveAt(0);
                if (at > _clock.Now) _clock.Now = at;
                run();
                continue;
            }
            if (deadlineMs > _clock.Now) _clock.Now = deadlineMs;
            return true;
        }
    }

    public void Wake() => _wake = true;
    public void ClearWake() => _wake = false;
    public void BeginSession() => SessionsBegun++;
    public void EndSession() => SessionsEnded++;
    public void Dispose() { }
}

internal sealed record InputEvent(double T, string Kind, int A = 0, int B = 0);

/// <summary>Records what the engine sends, with the virtual time. Mouse moves update the fake environment's cursor like Windows would.</summary>
internal sealed class FakeInput : IInputSink
{
    private readonly FakeClock _clock;
    private readonly FakeEnvironment? _env;
    private readonly HashSet<ClickerMouseButton> _held = new();
    private readonly HashSet<int> _heldKeys = new();

    public readonly List<InputEvent> Events = new();
    public int ReleaseAllCalls;
    public bool FailEverything;
    public Exception? ThrowOnClick;

    public FakeInput(FakeClock clock, FakeEnvironment? env = null)
    {
        _clock = clock;
        _env = env;
    }

    private bool Add(string kind, int a = 0, int b = 0)
    {
        if (FailEverything) return false;
        Events.Add(new InputEvent(_clock.Now, kind, a, b));
        return true;
    }

    public IEnumerable<InputEvent> Of(string kind) => Events.Where(e => e.Kind == kind);

    public bool MoveTo(int x, int y)
    {
        if (!Add("move", x, y)) return false;
        if (_env is not null) _env.Cursor = new PixelPoint(x, y);
        return true;
    }

    public bool MouseDown(ClickerMouseButton button)
    {
        if (ThrowOnClick is not null) throw ThrowOnClick;
        if (!Add("down", (int)button)) return false;
        _held.Add(button);
        return true;
    }

    public bool MouseUp(ClickerMouseButton button)
    {
        if (!_held.Remove(button)) return true;
        return Add("up", (int)button);
    }

    public bool MouseClicks(ClickerMouseButton button, int pairs)
    {
        if (ThrowOnClick is not null) throw ThrowOnClick;
        return Add("burst", pairs, (int)button);
    }

    public bool KeyDown(int vk, bool shift)
    {
        if (!Add("kdown", vk, shift ? 1 : 0)) return false;
        _heldKeys.Add(vk);
        return true;
    }

    public bool KeyRepeat(int vk) => Add("krepeat", vk);

    public bool KeyUp(int vk)
    {
        if (!_heldKeys.Remove(vk)) return true;
        return Add("kup", vk);
    }

    public bool KeyClicks(int vk, bool shift, int pairs) => Add("kburst", pairs, shift ? 1 : 0);

    public bool ReleaseAll()
    {
        ReleaseAllCalls++;
        foreach (var b in _held.ToList()) Add("up", (int)b);
        foreach (var k in _heldKeys.ToList()) Add("kup", k);
        _held.Clear();
        _heldKeys.Clear();
        return true;
    }

    public int HeldCount => _held.Count + _heldKeys.Count;
}

internal sealed class FakeEnvironment : IHostEnvironment
{
    public PixelPoint Cursor = new(960, 540);
    public List<PixelRect> MonitorList = new() { new PixelRect(0, 0, 1920, 1080) };
    public ForegroundInfo Fg = new(new IntPtr(1), 100, "UnrealWindow", @"C:\Games\game.exe", false);
    public bool Elevated;
    public int DelayIndex = 0;      // 250 ms
    public int Speed = 31;          // 30 repeats per second
    public int DoubleClickMs = 500;
    public bool Caps;
    public readonly HashSet<int> Down = new();

    public PixelPoint CursorPosition => Cursor;
    public IReadOnlyList<PixelRect> Monitors => MonitorList;
    public PixelRect VirtualScreen => MonitorList.Aggregate((a, b) =>
    {
        int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return new PixelRect(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
    });
    public ForegroundInfo Foreground => Fg;
    public bool HostElevated => Elevated;
    public int KeyboardDelayIndex => DelayIndex;
    public int KeyboardSpeed => Speed;
    public int DoubleClickTimeMs => DoubleClickMs;
    public bool CapsLockOn => Caps;
    public bool IsKeyDown(int vk) => Down.Contains(vk);
}

/// <summary>An engine on virtual time with every collaborator faked.</summary>
internal sealed class EngineRig
{
    public readonly FakeClock Clock = new();
    public readonly FakeWaiter Waiter;
    public readonly FakeEnvironment Env = new();
    public readonly FakeInput Input;
    public readonly ZoneTracker Zones = new();
    public readonly ClickEngine Engine;
    public readonly ClickerSettings S;
    public readonly List<EngineSnapshot> Snapshots = new();

    /// <summary>Settings with the randomness and the hold switched off: the schedule is then exactly the set rate.</summary>
    public EngineRig(Action<ClickerSettings>? configure = null, int seed = 1)
    {
        Waiter = new FakeWaiter(Clock);
        Input = new FakeInput(Clock, Env);
        S = new ClickerSettings { SpeedRandomizationEnabled = false, DutyCycleEnabled = false };
        configure?.Invoke(S);
        Engine = new ClickEngine(Input, Waiter, Clock, Env, Zones, new Randomizer(seed));
        Engine.StateChanged += s => Snapshots.Add(s);
        Apply();
    }

    /// <summary>Pushes <see cref="S"/> (after the test changed it) to the engine and the zone tracker.</summary>
    public void Apply()
    {
        Engine.Configure(S);
        Zones.Configure(S.ZonesEnabled, S.Zones);
    }

    public RunSummary Run(bool waitForRelease = false)
    {
        var summary = Engine.RunSync(new EngineStart(waitForRelease), out var status);
        Assert.Equal(EngineStartStatus.Started, status);
        return summary!;
    }

    public RunSummary RunFor(double seconds)
    {
        S.TimeLimitEnabled = true;
        S.TimeLimit = seconds;
        S.TimeLimitUnit = TimeLimitUnit.Seconds;
        Apply();
        return Run();
    }
}

/// <summary>An engine for the coordinator tests: records calls, answers Start with a chosen status and ends runs on demand.</summary>
internal sealed class FakeEngine : IClickEngine
{
    public bool Running;
    public EngineStartStatus StartStatus = EngineStartStatus.Started;
    public readonly List<EngineStart> Starts = new();
    public readonly List<StopReason> Stops = new();
    public readonly List<ClickerSettings> Configs = new();
    public bool Locked;
    public EngineSnapshot Current = new(false, false, 0, 0, 0, null);

    public bool IsRunning => Running;
    public EngineSnapshot Snapshot => Current;
    public event Action<EngineSnapshot>? StateChanged;
    public event Action<RunSummary>? RunEnded;

    public void Configure(ClickerSettings settings) => Configs.Add(settings);

    public EngineStartStatus Start(EngineStart request)
    {
        Starts.Add(request);
        if (StartStatus != EngineStartStatus.Started) return StartStatus;
        Running = true;
        Current = new EngineSnapshot(true, false, 0, 25, 0, null);
        StateChanged?.Invoke(Current);
        return EngineStartStatus.Started;
    }

    public void Stop(StopReason reason)
    {
        if (Running) Stops.Add(reason);
    }

    public void SetLocked(bool locked) => Locked = locked;

    /// <summary>The run ends on its own (or after a requested stop).</summary>
    public void End(StopReason reason, long clicks = 7)
    {
        Running = false;
        Current = new EngineSnapshot(false, false, clicks, 25, 1000, null);
        RunEnded?.Invoke(new RunSummary(new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc), 1.0, clicks, reason, null));
        StateChanged?.Invoke(Current);
    }
}
