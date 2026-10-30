using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using WKI_Clipper.Models;
using WKI_Clipper.Services.AutoClicker;

namespace WKI_Clipper.ClickerHost;

/// <summary>What an engine run looks like from outside; sent at most ten times a second while running.</summary>
internal readonly record struct EngineSnapshot(bool Running, bool Paused, long Clicks, double Cps, long ElapsedMs, string? BlockedByElevated);

/// <summary>How a run ended. <see cref="Detail"/> is the number or program the reason refers to (see <see cref="RunEndedMessage"/>).</summary>
internal sealed record RunSummary(DateTime StartUtc, double Seconds, long Clicks, StopReason Reason, string? Detail);

/// <summary>Why a start was not carried out.</summary>
internal enum EngineStartStatus { Started, Busy, Locked, NoOutputKey, EmptyWhitelist }

/// <param name="WaitForHotkeyRelease">
/// Toggle-mode hotkey start: the first output waits until the hotkey's modifier keys (no Ctrl+click) and a hotkey key that is also the
/// output are released - otherwise the user's own release of that key would end the clicker's first press or hold.
/// </param>
internal readonly record struct EngineStart(bool WaitForHotkeyRelease);

/// <summary>The engine as the host's coordinator uses it (a fake stands in for it in tests).</summary>
internal interface IClickEngine
{
    bool IsRunning { get; }
    EngineSnapshot Snapshot { get; }

    /// <summary>The host's copy of the settings; takes effect at the next batch of a running click loop.</summary>
    void Configure(ClickerSettings settings);

    /// <summary>Starts a run on the engine's own thread. Never blocks.</summary>
    EngineStartStatus Start(EngineStart request);

    /// <summary>Asks the run to end with this reason and wakes its wait at once. Never blocks; no effect when idle.</summary>
    void Stop(StopReason reason);

    void SetLocked(bool locked);

    /// <summary>Called on the engine thread, at most ten times a second while running and once more, after <see cref="RunEnded"/>, when the run is over.</summary>
    event Action<EngineSnapshot>? StateChanged;

    /// <summary>Called on the engine thread after a run ended (already idle again, so a handler may start the next one).</summary>
    event Action<RunSummary>? RunEnded;
}

/// <summary>
/// The click loop (design section 4), built from the pure parts of the earlier tasks: cadence and duty (<see cref="CadencePlan"/>),
/// log-normal batch durations (<see cref="Randomizer"/>), double-click timing, keyboard repeat, the failsafes (<see cref="StopRules"/>),
/// pause zones (<see cref="ZoneTracker"/>) and the click-point route. It touches the outside world only through
/// <see cref="IInputSink"/>, <see cref="IWaiter"/>, <see cref="IClock"/> and <see cref="IHostEnvironment"/>, so the tests drive it with
/// a virtual clock.
/// <list type="bullet">
/// <item>One run is one call of <see cref="RunToEnd"/> on one thread. The schedule is absolute (the next batch is due at the previous
/// deadline plus the drawn duration), kept in a clock that stands still while a pause zone holds the clicker.</item>
/// <item>The failsafes are checked at every batch and between waits of at most <see cref="SliceMs"/>, so even one click per minute
/// stops within about 10 ms of the cursor entering a corner.</item>
/// <item>A hold shorter than <see cref="MinTimedHoldMs"/> counts as 0: all presses of a batch go out in one SendInput call.</item>
/// <item>Hold modes (duty mode Hold, or a keyboard at 100 % duty) press once and keep it down (the key additionally repeats at the
/// Windows rate); their kind is fixed for the run, a config push during such a run applies to the next one.</item>
/// <item>On every end all buttons and keys the engine pressed are released.</item>
/// </list>
/// </summary>
internal sealed class ClickEngine : IClickEngine
{
    /// <summary>Longest wait without a look at the failsafes.</summary>
    public const double SliceMs = 10;

    /// <summary>A hold below this is sent as an instant press and release.</summary>
    public const double MinTimedHoldMs = 0.1;

    /// <summary>State reports are at most this far apart while running (10 per second).</summary>
    public const double ReportIntervalMs = 100;

    /// <summary>Behind schedule by more than this (the PC was suspended or frozen), the missed clicks are dropped instead of fired in a burst. Smaller lags are caught up so the long-run mean holds (design section 4).</summary>
    public const double MaxCatchUpMs = 1000;

    private readonly IInputSink _input;
    private readonly IWaiter _waiter;
    private readonly IClock _clock;
    private readonly IHostEnvironment _env;
    private readonly ZoneTracker _zones;
    private readonly Randomizer _rng;
    private readonly List<PathPoint> _path = new();

    // ---- shared with other threads ----
    private ClickerSettings _config = ClickerSettingsRules.Normalize(new ClickerSettings());
    private int _configVersion;
    private int _state;                 // 0 idle, 1 running
    private int _stopRequest;           // (int)StopReason, 0 = none
    private volatile bool _locked;
    private volatile int _outputVk;
    private readonly object _snapshotLock = new();   // the snapshot is several fields: a plain read could tear
    private EngineSnapshot _snapshot;

    // ---- the running session (engine thread only) ----
    private ClickerSettings _s = null!;
    private CadencePlan _plan;
    private StopContext _ctx = null!;
    private ClickPointSequencer? _sequencer;
    private IReadOnlyList<ClickPoint>? _routePoints;
    private bool _routeStop;
    private int _appliedVersion;
    private double _t0;
    private double _pausedMs;
    private double _pauseStartedAt;
    private long _clicks;
    private bool _resumed;
    private bool _ending;
    private StopReason _endReason;
    private bool _finalPointClicked;
    private bool _injectFailed;
    private string? _blockedBy;
    private double _lastReportAt;

    public event Action<EngineSnapshot>? StateChanged;
    public event Action<RunSummary>? RunEnded;

    public ClickEngine(IInputSink input, IWaiter waiter, IClock clock, IHostEnvironment env, ZoneTracker zones, Randomizer rng)
    {
        _input = input;
        _waiter = waiter;
        _clock = clock;
        _env = env;
        _zones = zones;
        _rng = rng;
    }

    public bool IsRunning => Volatile.Read(ref _state) != 0;

    /// <summary>The virtual key or mouse button (VK_LBUTTON ...) the running session sends; 0 while idle. Any thread.</summary>
    public int OutputVk => _outputVk;

    public EngineSnapshot Snapshot
    {
        get { lock (_snapshotLock) return _snapshot; }
    }

    public void Configure(ClickerSettings settings)
    {
        _config = ClickerSettingsRules.Normalize(settings);
        Interlocked.Increment(ref _configVersion);
    }

    public void SetLocked(bool locked) => _locked = locked;

    public void Stop(StopReason reason)
    {
        if (!IsRunning) return;
        Interlocked.CompareExchange(ref _stopRequest, (int)reason, 0);   // the first reason wins
        _waiter.Wake();
    }

    public EngineStartStatus Start(EngineStart request)
    {
        var status = TryBegin(out var settings);
        if (status != EngineStartStatus.Started) return status;
        var thread = new Thread(() => RunToEnd(request, settings))
        {
            Name = "WKI Clicker engine",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
        try { thread.Start(); }
        catch (Exception ex)
        {
            HostLog.Error("Engine thread did not start", ex);
            Volatile.Write(ref _state, 0);
            return EngineStartStatus.Busy;
        }
        return EngineStartStatus.Started;
    }

    /// <summary>Runs one session on the calling thread and returns how it ended (tests; the product uses <see cref="Start"/>).</summary>
    internal RunSummary? RunSync(EngineStart request, out EngineStartStatus status)
    {
        status = TryBegin(out var settings);
        return status == EngineStartStatus.Started ? RunToEnd(request, settings) : null;
    }

    private EngineStartStatus TryBegin(out ClickerSettings settings)
    {
        settings = _config;
        if (_locked) return EngineStartStatus.Locked;
        if (settings.InputType == ClickerInput.Keyboard && settings.KeyboardVk <= 0) return EngineStartStatus.NoOutputKey;
        // An output key that is part of a hotkey is only a warning (free-hotkeys design, section 3): the hook ignores our own input.
        if (settings.WhitelistEnabled && settings.Whitelist.Count == 0) return EngineStartStatus.EmptyWhitelist;
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return EngineStartStatus.Busy;
        Interlocked.Exchange(ref _stopRequest, 0);
        _waiter.ClearWake();
        return EngineStartStatus.Started;
    }

    // ---- one run ----

    private RunSummary RunToEnd(EngineStart request, ClickerSettings settings)
    {
        double t0 = _clock.NowMs;
        var startUtc = _clock.UtcNow;
        _endReason = StopReason.Manual;
        string? detail = null;
        try
        {
            _waiter.BeginSession();
            BeginSession(settings, t0);
            Report();
            _endReason = Execute(request);
            detail = DetailFor(_endReason);
        }
        catch (Exception ex)
        {
            HostLog.Error("Click engine failed", ex);
            _endReason = StopReason.Manual;
        }
        finally
        {
            try
            {
                if (!_input.ReleaseAll()) HostLog.Warn("Not every button/key could be released at the end of the run.");
            }
            catch (Exception ex) { HostLog.Error("Release at the end of the run failed", ex); }
            _outputVk = 0;
            _waiter.EndSession();
        }

        var summary = new RunSummary(startUtc, Math.Max(0, (_clock.NowMs - t0) / 1000.0), _clicks, _endReason, detail);
        SetSnapshot(new EngineSnapshot(false, false, _clicks, _plan.ClicksPerSecond, (long)(summary.Seconds * 1000), null));
        Volatile.Write(ref _state, 0);
        try { RunEnded?.Invoke(summary); }
        catch (Exception ex) { HostLog.Error("RunEnded handler failed", ex); }
        RaiseState();
        return summary;
    }

    private void BeginSession(ClickerSettings settings, double t0)
    {
        _t0 = t0;
        _pausedMs = 0;
        _clicks = 0;
        _resumed = false;
        _ending = false;
        _finalPointClicked = false;
        _injectFailed = false;
        _blockedBy = null;
        _lastReportAt = double.NegativeInfinity;
        _sequencer = null;
        _appliedVersion = Volatile.Read(ref _configVersion);
        ApplyConfig(settings, first: true);
        _zones.ResetPause();
        PollEnvironment();
    }

    private void ApplyConfig(ClickerSettings s, bool first)
    {
        _s = s;
        _plan = CadencePlan.From(s);
        _outputVk = ClickerConflicts.OutputVk(s);

        var fresh = StopContext.From(s);
        if (_ctx is not null)
        {
            fresh.Clicks = _ctx.Clicks;
            fresh.ElapsedSeconds = _ctx.ElapsedSeconds;
            fresh.Cursor = _ctx.Cursor;
            fresh.Monitors = _ctx.Monitors;
            fresh.ForegroundClass = _ctx.ForegroundClass;
            fresh.ForegroundExe = _ctx.ForegroundExe;
        }
        fresh.Locked = _locked;
        _ctx = fresh;

        bool wantRoute = s.ClickPointsEnabled && s.ClickPoints.Count > 0;
        if (!wantRoute) _sequencer = null;
        else if (first || _sequencer is null || !SameRoute(_routePoints, s.ClickPoints) || _routeStop != s.StopWhenComplete)
        {
            _sequencer = new ClickPointSequencer(s.ClickPoints, s.StopWhenComplete, _rng);
        }
        _routePoints = wantRoute ? s.ClickPoints : null;
        _routeStop = s.StopWhenComplete;
    }

    private static bool SameRoute(IReadOnlyList<ClickPoint>? a, IReadOnlyList<ClickPoint> b)
    {
        if (a is null || a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].X != b[i].X || a[i].Y != b[i].Y || a[i].Clicks != b[i].Clicks || a[i].Radius != b[i].Radius) return false;
        return true;
    }

    private StopReason Execute(EngineStart request)
    {
        if (request.WaitForHotkeyRelease && _s.ActivationMode == ActivationMode.Toggle && !WaitHotkeyReleased())
            return _endReason;

        if (_s.DutyMode == DutyMode.Hold && _s.InputType == ClickerInput.Mouse) return MouseHoldLoop();
        if (KeyboardRepeat.IsHoldMode(_s)) return KeyboardHoldLoop();
        return ClickLoop();
    }

    // ---- start: no Ctrl+click, no hold cut short by the user's own release ----

    private bool WaitHotkeyReleased()
    {
        var vks = StartReleaseKeys(_s);
        if (vks.Length == 0) return true;
        while (true)
        {
            if (!Check()) return false;
            bool held = false;
            foreach (var vk in vks)
                if (_env.IsKeyDown(vk)) { held = true; break; }
            if (!held) return true;
            if (!WaitV(NowV() + 5)) return false;
        }
    }

    /// <summary>What a toggle-mode hotkey start waits for: <see cref="ModifierKeys"/>, plus the output key or button when the hotkey contains it.</summary>
    internal static int[] StartReleaseKeys(ClickerSettings s)
    {
        var vks = ModifierKeys(s.Hotkey);
        if (!ClickerConflicts.OutputInHotkey(s, s.Hotkey)) return vks;
        int output = ClickerConflicts.OutputVk(s);
        return Array.IndexOf(vks, output) >= 0 ? vks : [.. vks, output];
    }

    /// <summary>Both sides of every modifier family the hotkey demands (the user may release either hand).</summary>
    internal static int[] ModifierKeys(ClickerHotkey? hotkey)
    {
        var list = new List<int>();
        if (hotkey is null) return list.ToArray();
        foreach (var f in ModifierFamily.Families)
            if ((hotkey.Modifiers & f.All) != 0)
            {
                list.Add(f.VkLeft);
                list.Add(f.VkRight);
            }
        return list.ToArray();
    }

    // ---- click loop ----

    private StopReason ClickLoop()
    {
        double deadlineV = NowV();   // the first batch is due at once
        while (true)
        {
            if (!Check()) return _endReason;
            ApplyPendingConfig();

            double nowV = NowV();
            if (nowV - deadlineV > MaxCatchUpMs) deadlineV = nowV;   // suspend-sized gap: drop the missed clicks, do not burst

            if (!RunBatch(deadlineV)) return _endReason;
            if (_finalPointClicked) return StopReason.AllPointsDone;
            if (_ctx.ClickLimitEnabled && _clicks >= _ctx.ClickLimit) return StopReason.ClickLimit;

            double span = _s.SpeedRandomizationEnabled
                ? _rng.NextDuration(_plan.BatchMs, _s.SpeedRandomizationPercent)
                : _plan.BatchMs;
            deadlineV += span;
            if (!WaitV(deadlineV)) return _endReason;
        }
    }

    private void ApplyPendingConfig()
    {
        int version = Volatile.Read(ref _configVersion);
        if (version == _appliedVersion) return;
        _appliedVersion = version;
        ApplyConfig(_config, first: false);
    }

    /// <summary>One batch of <see cref="CadencePlan.CyclesPerBatch"/> cycles that starts at <paramref name="batchStartV"/>. false = the run must end.</summary>
    private bool RunBatch(double batchStartV)
    {
        bool burst = _plan.HoldMs < MinTimedHoldMs && !_plan.DoubleClick && _sequencer is null;
        if (burst)
        {
            int n = _plan.CyclesPerBatch;
            if (_ctx.ClickLimitEnabled) n = (int)Math.Min(n, Math.Max(1, _ctx.ClickLimit - _clicks));
            bool ok = _s.InputType == ClickerInput.Mouse
                ? _input.MouseClicks(_s.MouseButton, n)
                : _input.KeyClicks(_s.KeyboardVk, NeedsShift(), n);
            Injected(ok, n);
            return true;
        }

        for (int i = 0; i < _plan.CyclesPerBatch; i++)
        {
            if (i > 0)
            {
                if (_ctx.ClickLimitEnabled && _clicks >= _ctx.ClickLimit) break;
                if (!WaitV(batchStartV + i * _plan.IntervalMs)) return false;
            }
            if (!OneCycle()) return false;
            if (_finalPointClicked) break;
        }
        return true;
    }

    /// <summary>One click cycle: pick the click point, move there, press once or twice. false = the run must end.</summary>
    private bool OneCycle()
    {
        int wanted = _plan.DoubleClick ? 2 : 1;
        if (_ctx.ClickLimitEnabled) wanted = (int)Math.Min(wanted, Math.Max(1, _ctx.ClickLimit - _clicks));

        if (_sequencer is { HasPoints: true, IsComplete: false } seq)
        {
            if (wanted == 2 && seq.RemainingAtPoint == 1) wanted = 1;   // the point gets exactly the count it asked for
            if (seq.TryNext(wanted, out var target))
            {
                if (!MoveTo(target)) return false;
                if (target.Final) _finalPointClicked = true;
            }
        }

        return wanted == 2 ? DoublePress() : SinglePress(_plan.HoldMs);
    }

    private bool MoveTo(ClickTarget target)
    {
        if (CursorPath.ShouldJump(_plan.ClicksPerSecond) || !target.FirstOfVisit)
        {
            Injected(_input.MoveTo(target.X, target.Y), 0);
            return true;
        }

        double duration = CursorPath.MoveDurationMs(_plan.IntervalMs, _rng);
        CursorPath.Bezier(_env.CursorPosition, new PixelPoint(target.X, target.Y), duration, _rng, _path);
        double start = _clock.NowMs;
        double nextCheck = start + SliceMs;
        for (int i = 0; i < _path.Count; i++)
        {
            var step = _path[i];
            if (!_waiter.WaitUntil(start + step.AtMs))
            {
                // Woken: a stop was requested (Check ends the run), or the wake was spurious: finish the move instead of clicking mid-path.
                if (!Check()) return false;
                _waiter.ClearWake();
                Injected(_input.MoveTo(target.X, target.Y), 0);
                return true;
            }
            if (_clock.NowMs >= nextCheck)
            {
                double pausedBefore = _pausedMs;
                if (!Check()) return false;   // failsafes, stop zones and pause zones at least every SliceMs, glide or not
                nextCheck = _clock.NowMs + SliceMs;
                if (_pausedMs != pausedBefore)
                {
                    Injected(_input.MoveTo(target.X, target.Y), 0);   // after a pause zone the path is stale: jump to the target
                    return true;
                }
            }
            Injected(_input.MoveTo(step.X, step.Y), 0);
        }
        return true;
    }

    private bool SinglePress(double holdMs)
    {
        bool shift = _s.InputType == ClickerInput.Keyboard && NeedsShift();
        bool down = _s.InputType == ClickerInput.Mouse ? _input.MouseDown(_s.MouseButton) : _input.KeyDown(_s.KeyboardVk, shift);
        if (!down)
        {
            Injected(false, 1);
            return true;
        }

        bool alive = true;
        if (holdMs >= MinTimedHoldMs) alive = WaitV(NowV() + holdMs);
        bool up = Release();
        Injected(up, 1);
        return alive;
    }

    private bool DoublePress()
    {
        var pair = DoubleClickTiming.Plan(_plan.IntervalMs, _plan.HoldMs, _env.DoubleClickTimeMs);
        double firstAt = NowV();
        bool shift = _s.InputType == ClickerInput.Keyboard && NeedsShift();

        if (!PressDown(shift)) { Injected(false, 2); return true; }
        bool alive = pair.Hold1Ms < MinTimedHoldMs || WaitV(firstAt + pair.Hold1Ms);
        Injected(Release(), 1);
        if (!alive) return false;

        if (!WaitV(firstAt + pair.SecondPressOffsetMs)) return false;
        if (!PressDown(shift)) { Injected(false, 1); return true; }
        alive = pair.Hold2Ms < MinTimedHoldMs || WaitV(NowV() + pair.Hold2Ms);
        Injected(Release(), 1);
        return alive;
    }

    private bool PressDown(bool shift)
        => _s.InputType == ClickerInput.Mouse ? _input.MouseDown(_s.MouseButton) : _input.KeyDown(_s.KeyboardVk, shift);

    private bool Release()
        => _s.InputType == ClickerInput.Mouse ? _input.MouseUp(_s.MouseButton) : _input.KeyUp(_s.KeyboardVk);

    /// <summary>A letter key is typed in the wanted case: Shift goes around it when CapsLock disagrees with the wanted case.</summary>
    private bool NeedsShift()
    {
        int vk = _s.KeyboardVk;
        if (vk is < 0x41 or > 0x5A) return false;
        return (_s.KeyCase == KeyCase.Upper) != _env.CapsLockOn;
    }

    // ---- hold modes ----

    private StopReason MouseHoldLoop()
    {
        bool pressed = false, everPressed = false;
        while (true)
        {
            if (!Check()) return _endReason;
            if (_resumed)
            {
                _resumed = false;
                pressed = false;   // the pause zone released it
            }
            if (!pressed)
            {
                if (_input.MouseDown(_s.MouseButton))
                {
                    pressed = true;
                    Injected(true, everPressed ? 0 : 1);   // pressing again after a pause zone is still the same hold
                    everPressed = true;
                }
                else Injected(false, 1);
            }
            if (!WaitV(NowV() + SliceMs)) return _endReason;
        }
    }

    private StopReason KeyboardHoldLoop()
    {
        var repeat = KeyboardRepeat.Plan(_env.KeyboardDelayIndex, _env.KeyboardSpeed);
        bool pressed = false, everPressed = false;
        double firstPressV = 0;
        int repeats = 0;
        while (true)
        {
            if (!Check()) return _endReason;
            if (_resumed)
            {
                _resumed = false;
                pressed = false;
            }
            if (!pressed)
            {
                if (_input.KeyDown(_s.KeyboardVk, NeedsShift()))
                {
                    pressed = true;
                    firstPressV = NowV();
                    repeats = 0;
                    Injected(true, everPressed ? 0 : 1);
                    everPressed = true;
                }
                else
                {
                    Injected(false, 1);
                    if (!WaitV(NowV() + SliceMs)) return _endReason;
                    continue;
                }
            }

            if (!WaitV(firstPressV + KeyboardRepeat.RepeatTimeMs(repeats, repeat))) return _endReason;
            if (_resumed) continue;   // handled at the top
            Injected(_input.KeyRepeat(_s.KeyboardVk), 1);
            repeats++;
        }
    }

    // ---- waiting and the failsafes ----

    /// <summary>The clock of the schedule: real time minus the time spent in a pause zone.</summary>
    private double NowV() => _clock.NowMs - _pausedMs;

    /// <summary>
    /// Waits until the schedule clock reaches <paramref name="deadlineV"/>, looking at the failsafes every <see cref="SliceMs"/>.
    /// false = the run must end (<see cref="_endReason"/> is set). The last stretch of a wait is not followed by a check: the caller's
    /// loop starts with one.
    /// </summary>
    private bool WaitV(double deadlineV)
    {
        while (true)
        {
            double nowV = NowV();
            double remaining = deadlineV - nowV;
            if (remaining <= 0) return !_ending;

            bool last = remaining <= SliceMs;
            double target = last ? deadlineV : nowV + SliceMs;
            if (!_waiter.WaitUntil(target + _pausedMs))
            {
                // Woken: a stop request. Check() ends the run; with no reason pending the wake was spurious.
                if (!Check()) return false;
                _waiter.ClearWake();
                continue;
            }
            if (last) return !_ending;
            if (!Check()) return false;
        }
    }

    /// <summary>
    /// One look at everything that can end the run or hold it: a stop request, the failsafes, a pause zone. Holds the clicker (releasing
    /// what it pressed) while the cursor is inside a pause zone. false = the run must end. Also pushes the state report.
    /// </summary>
    private bool Check()
    {
        if (_ending) return false;

        PollEnvironment();
        int request = Volatile.Read(ref _stopRequest);
        if (request != 0) return End((StopReason)request);

        var rule = StopRules.Evaluate(_ctx);
        if (rule != StopReason.None) return End(rule);

        if (_s.ZonesEnabled && _zones.PollPause(_ctx.Cursor) == PauseChange.Paused)
        {
            if (!HoldWhilePaused()) return false;
        }

        MaybeReport();
        return true;
    }

    private bool End(StopReason reason)
    {
        _ending = true;
        _endReason = reason;
        return false;
    }

    /// <summary>The cursor entered a pause zone: let go of everything and wait (10 ms polls) until it leaves. false = the run ended meanwhile.</summary>
    private bool HoldWhilePaused()
    {
        _pauseStartedAt = _clock.NowMs;
        _input.ReleaseAll();
        MaybeReport();
        while (true)
        {
            _waiter.WaitUntil(_clock.NowMs + SliceMs);   // a stop request wakes it; the check below ends the run
            PollEnvironment();
            int request = Volatile.Read(ref _stopRequest);
            if (request != 0) return End((StopReason)request);
            var rule = StopRules.Evaluate(_ctx);
            if (rule != StopReason.None) return End(rule);
            if (_zones.PollPause(_ctx.Cursor) == PauseChange.Resumed) break;
            MaybeReport();   // the paused state reaches the clipper within one report interval
        }
        _pausedMs += _clock.NowMs - _pauseStartedAt;
        _resumed = true;
        MaybeReport();
        return true;
    }

    private void PollEnvironment()
    {
        var c = _ctx;
        var cursor = _env.CursorPosition;
        c.Cursor = cursor;
        c.Monitors = _env.Monitors;
        var fg = _env.Foreground;
        c.ForegroundClass = fg.Class;
        c.ForegroundExe = fg.Exe;
        c.Locked = _locked;
        c.Clicks = _clicks;
        c.ElapsedSeconds = (_clock.NowMs - _t0) / 1000.0;

        // An elevated window in front swallows injected input (UIPI) unless the host is elevated too; a rejected SendInput says the same.
        _blockedBy = fg.Elevated && !_env.HostElevated ? fg.ExeName ?? "?"
            : _injectFailed ? fg.ExeName ?? "?"
            : null;
    }

    private void Injected(bool ok, int clicks)
    {
        if (!ok)
        {
            _injectFailed = true;
            return;
        }
        _injectFailed = false;
        _clicks += clicks;
    }

    private string? DetailFor(StopReason reason) => reason switch
    {
        StopReason.ClickLimit => _ctx.ClickLimit.ToString(CultureInfo.InvariantCulture),
        StopReason.TimeLimit => TimeLimitText(),
        StopReason.NotOnWhitelist => ExeDetail(_ctx.ForegroundExe),
        _ => null
    };

    private string TimeLimitText()
    {
        string unit = _s.TimeLimitUnit switch { TimeLimitUnit.Minutes => "min", TimeLimitUnit.Hours => "h", _ => "s" };
        return _s.TimeLimit.ToString("0.##", CultureInfo.InvariantCulture) + " " + unit;
    }

    private static string? ExeDetail(string? exe) => string.IsNullOrEmpty(exe) ? null : System.IO.Path.GetFileName(exe);

    // ---- reporting ----

    private void MaybeReport()
    {
        // Changes wait for the next due report as well: Report() reads the blocked/paused state fresh, so a flapping value cannot flood the pipe.
        if (_clock.NowMs - _lastReportAt >= ReportIntervalMs) Report();
    }

    private void Report()
    {
        double now = _clock.NowMs;
        _lastReportAt = now;
        bool paused = _zones.IsPaused;
        SetSnapshot(new EngineSnapshot(true, paused, _clicks, _plan.ClicksPerSecond, (long)Math.Max(0, now - _t0), _blockedBy));
        RaiseState();
    }

    private void SetSnapshot(EngineSnapshot snapshot)
    {
        lock (_snapshotLock) _snapshot = snapshot;
    }

    // A listener that throws must not take the click loop down with it.
    private void RaiseState()
    {
        try { StateChanged?.Invoke(Snapshot); }
        catch (Exception ex) { HostLog.Error("StateChanged handler failed", ex); }
    }
}
