using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>One fake helper process: its end of an in-memory pipe, what the clipper sent it, and switches to crash or hang.</summary>
internal sealed class FakeHostSession : IClickerHostSession
{
    private readonly Stream _serverEnd;
    private readonly Stream _hostStream;
    private readonly TaskCompletionSource _connect = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _exited = new();
    private readonly List<ClickerMessage> _received = new();

    /// <summary>The helper's side of the pipe: write here what the helper says.</summary>
    public ClickerChannel HostEnd { get; }
    public string PipeName { get; }
    public bool Killed { get; private set; }

    /// <summary>true = a shutdown message ends the fake process (like the real helper); false = it hangs.</summary>
    public bool ExitOnShutdown = true;

    public event Action? Exited;

    public FakeHostSession(string pipeName, bool autoConnect)
    {
        PipeName = pipeName;
        var (server, host) = InMemoryPipe.Create();
        _serverEnd = server;
        _hostStream = host;
        HostEnd = new ClickerChannel(host);
        if (autoConnect) _connect.TrySetResult();
        _ = Task.Run(PumpAsync);
    }

    public bool HasExited => _exited.IsSet;

    public async Task<Stream> ConnectAsync(CancellationToken ct)
    {
        await _connect.Task.WaitAsync(ct);
        return _serverEnd;
    }

    public void Connect() => _connect.TrySetResult();

    public bool WaitForExit(int milliseconds) => _exited.Wait(milliseconds);

    public void Kill()
    {
        Killed = true;
        Exit();
    }

    /// <summary>The process ends (crash, or a clean exit): the pipe closes, Exited is raised.</summary>
    public void Exit()
    {
        if (_exited.IsSet) return;
        try { _hostStream.Dispose(); } catch { }
        _exited.Set();
        Exited?.Invoke();
    }

    public void Dispose() { }

    private async Task PumpAsync()
    {
        try
        {
            while (await HostEnd.ReadAsync() is { } decoded)
            {
                if (decoded.Status != DecodeStatus.Ok) continue;
                lock (_received) _received.Add(decoded.Message!);
                if (decoded.Message is ShutdownMessage && ExitOnShutdown) Exit();
            }
        }
        catch (Exception) { /* the pipe is gone */ }
    }

    public IReadOnlyList<ClickerMessage> Received
    {
        get { lock (_received) return _received.ToArray(); }
    }

    /// <summary>Says something; a helper that was ended meanwhile (killed by the controller) cannot hear it, which a test does not care about.</summary>
    public async Task Say(ClickerMessage message)
    {
        try { await HostEnd.WriteAsync(message); }
        catch (IOException) when (HasExited) { }
    }

    /// <summary>Raw bytes on the wire: lines no codec would write.</summary>
    public async Task SayRaw(string text) => await _hostStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text));

    public async Task<T> WaitFor<T>(Func<T, bool>? predicate = null, int timeoutMs = 10000) where T : ClickerMessage
    {
        T? hit = null;
        await Wait.Until(() => (hit = Received.OfType<T>().FirstOrDefault(m => predicate?.Invoke(m) ?? true)) is not null, timeoutMs,
            () => $"No {typeof(T).Name} arrived. Got: " + string.Join(", ", Received.Select(m => m.GetType().Name)));
        return hit!;
    }

    /// <summary>The helper's half of the handshake: hello first, then the clipper's hello and configuration are awaited.</summary>
    public async Task Handshake(StateMessage? firstState = null)
    {
        await Say(new HelloMessage(ClickerProtocol.Version, 4242));
        await WaitFor<HelloMessage>();
        await WaitFor<ConfigMessage>();
        await Say(firstState ?? Idle());
    }

    /// <summary>Hello and the first state back to back, without waiting for the clipper in between: a lock confirmation that must beat a timer.</summary>
    public async Task HandshakeEager(StateMessage state)
    {
        await Say(new HelloMessage(ClickerProtocol.Version, 4242));
        await Say(state);
    }

    public static StateMessage Idle(string? reason = null, bool locked = false)
        => new(Running: false, Armed: true, Locked: locked, reason, Clicks: 0, Cps: 0, ElapsedMs: 0, BlockedByElevated: null);
}

/// <summary>
/// The samples of the mouse patterns in memory. <see cref="FailSave"/> makes every write fail like a full or locked disk;
/// <see cref="FailLoads"/> makes the next reads of one pattern fail like a file another program holds for a moment.
/// </summary>
internal sealed class FakePatternStore : IMousePatternStore
{
    private readonly Dictionary<string, PatternData> _data = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _failLoads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _loadsOf = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _deleted = new();
    private int _saves, _loads;

    public volatile bool FailSave;

    /// <summary>The next <paramref name="times"/> reads of <paramref name="id"/> report <see cref="PatternLoadStatus.Unavailable"/>.</summary>
    public void FailLoads(string id, int times) { lock (_data) _failLoads[id] = times; }

    public int Saves { get { lock (_data) return _saves; } }
    public int Loads { get { lock (_data) return _loads; } }
    public int LoadsOf(string id) { lock (_data) return _loadsOf.GetValueOrDefault(id); }
    public IReadOnlyList<string> Deleted { get { lock (_data) return _deleted.ToArray(); } }

    /// <summary>Puts samples in place without counting a save (the state before the test).</summary>
    public void Put(string id, PatternData data) { lock (_data) _data[id] = data; }

    public PatternData? Get(string id) { lock (_data) return _data.TryGetValue(id, out var d) ? d : null; }

    public void Save(string id, PatternData data)
    {
        lock (_data)
        {
            if (FailSave) throw new IOException("The disk is full.");
            _data[id] = data;
            _saves++;
        }
    }

    public bool TryLoad(string id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PatternData? data)
    {
        if (Load(id, out var loaded) == PatternLoadStatus.Ok && loaded is not null)
        {
            data = loaded;
            return true;
        }
        data = null;
        return false;
    }

    public PatternLoadStatus Load(string id, out PatternData? data)
    {
        lock (_data)
        {
            _loads++;
            _loadsOf[id] = _loadsOf.GetValueOrDefault(id) + 1;
            data = null;
            if (_failLoads.TryGetValue(id, out int fail) && fail > 0)
            {
                _failLoads[id] = fail - 1;
                return PatternLoadStatus.Unavailable;
            }
            return _data.TryGetValue(id, out data) ? PatternLoadStatus.Ok : PatternLoadStatus.Missing;
        }
    }

    public void Delete(string id)
    {
        lock (_data)
        {
            _data.Remove(id);
            _deleted.Add(id);
        }
    }
}

internal sealed class FakeHostLauncher : IClickerHostLauncher, IDisposable
{
    private readonly List<FakeHostSession> _sessions = new();
    public bool AutoConnect = true;
    public bool ExitOnShutdown = true;
    public Exception? FailWith;
    public bool Disposed;
    private readonly List<ClickerRelease> _releases = new();

    public IReadOnlyList<ClickerRelease> Releases
    {
        get { lock (_releases) return _releases.ToArray(); }
    }

    public void LaunchRelease(ClickerRelease release)
    {
        lock (_releases) _releases.Add(release);
    }

    public IReadOnlyList<FakeHostSession> Sessions
    {
        get { lock (_sessions) return _sessions.ToArray(); }
    }

    public IClickerHostSession Launch(string pipeName, int parentPid)
    {
        if (FailWith is not null) throw FailWith;
        var session = new FakeHostSession(pipeName, AutoConnect) { ExitOnShutdown = ExitOnShutdown };
        lock (_sessions) _sessions.Add(session);
        return session;
    }

    public async Task<FakeHostSession> WaitForLaunch(int count, int timeoutMs = 10000)
    {
        await Wait.Until(() => Sessions.Count >= count, timeoutMs, () => $"Launch #{count} never happened (got {Sessions.Count}).");
        return Sessions[count - 1];
    }

    public void Dispose() => Disposed = true;
}

public sealed class ClickerControllerTests
{
    // The handshake, debounce, lock and goodbye timers run on a ManualTime: they fire only when a test advances it, never because a
    // loaded machine (the whole suite runs in parallel) held a thread up. Only Dispose waits in real time.
    private static readonly ClickerControllerTiming Timing = ClickerControllerTiming.Default;
    private static readonly ClickerControllerTiming ShortShutdown = Timing with { ShutdownWait = TimeSpan.FromMilliseconds(400) };

    private sealed class Rig : IDisposable
    {
        public readonly ClickerSettings Settings = new();
        public readonly FakeHostLauncher Launcher = new();
        public readonly FakePatternStore Store = new();
        public readonly ManualTime Time = new();
        public readonly ClickerController Controller;
        public readonly List<ClickerState> States = new();
        public readonly List<ClickerRunInfo> Runs = new();
        private readonly List<PatternRecordedInfo> _recorded = new();
        public int Saves;

        public Rig(bool armed = true, ClickerControllerTiming? timing = null, Action<FakeHostLauncher>? configure = null, bool patternsArmed = false)
        {
            Settings.Armed = armed;
            Settings.MousePatternsArmed = patternsArmed;
            configure?.Invoke(Launcher);
            Controller = new ClickerController(() => Settings, () => Interlocked.Increment(ref Saves), Launcher, Store, timing: timing ?? Timing,
                parentPid: 1234, time: Time);
            Controller.StateChanged += s => { lock (States) States.Add(s); };
            Controller.RunFinished += Runs.Add;
            Controller.PatternRecorded += r => { lock (_recorded) _recorded.Add(r); };
        }

        public IReadOnlyList<PatternRecordedInfo> Recorded
        {
            get { lock (_recorded) return _recorded.ToArray(); }
        }

        /// <summary>Settings were saved (the edit already happened on <see cref="Settings"/>).</summary>
        public void Saved() => Controller.OnSettingsChanged();

        public async Task<FakeHostSession> StartReady()
        {
            Controller.Initialize();
            var host = await Launcher.WaitForLaunch(1);
            await host.Handshake();
            await WaitState(s => s.Phase == ClickerPhase.Ready);
            return host;
        }

        public async Task<ClickerState> WaitState(Func<ClickerState, bool> predicate, int timeoutMs = 10000)
        {
            ClickerState? hit = null;
            await Wait.Until(() => predicate(hit = Controller.State), timeoutMs, () => "State never matched; last: " + Controller.State);
            return hit!;
        }

        public void Dispose() => Controller.Dispose();
    }

    // ---- lifecycle ----

    [Fact]
    public void Nothing_is_started_while_the_switch_is_off()
    {
        using var rig = new Rig(armed: false);

        rig.Controller.Initialize();

        Assert.Empty(rig.Launcher.Sessions);
        Assert.Equal(ClickerState.Off, rig.Controller.State);
    }

    [Fact]
    public void Nothing_is_started_before_initialize()
    {
        using var rig = new Rig();

        rig.Saved();

        Assert.Empty(rig.Launcher.Sessions);
    }

    [Fact]
    public async Task An_armed_clicker_starts_the_helper_on_a_valid_pipe_name_and_does_the_handshake()
    {
        using var rig = new Rig();
        rig.Settings.ClickSpeed = 77;

        rig.Controller.Initialize();
        var host = await rig.Launcher.WaitForLaunch(1);
        Assert.True(ClickerProtocol.IsValidPipeName(host.PipeName));
        Assert.StartsWith("WKI_Clipper.Clicker.1234.", host.PipeName);
        Assert.Equal(ClickerPhase.Starting, (await rig.WaitState(s => s.Phase == ClickerPhase.Starting)).Phase);

        await host.Handshake();

        var hello = await host.WaitFor<HelloMessage>();
        Assert.Equal(ClickerProtocol.Version, hello.V);
        var config = await host.WaitFor<ConfigMessage>();
        Assert.Equal(77, config.Settings.ClickSpeed);
        // hello answers before the configuration
        var order = host.Received.Select(m => m.GetType()).ToList();
        Assert.True(order.IndexOf(typeof(HelloMessage)) < order.IndexOf(typeof(ConfigMessage)));
        await rig.WaitState(s => s.Phase == ClickerPhase.Ready);
    }

    [Fact]
    public async Task Switching_on_later_starts_the_helper_and_switching_off_shuts_it_down()
    {
        using var rig = new Rig(armed: false);
        rig.Controller.Initialize();
        var armedChanges = 0;
        rig.Controller.ArmedChanged += () => Interlocked.Increment(ref armedChanges);

        rig.Settings.Armed = true;
        rig.Saved();
        var host = await rig.Launcher.WaitForLaunch(1);
        await host.Handshake();
        await rig.WaitState(s => s.Phase == ClickerPhase.Ready);

        rig.Settings.Armed = false;
        rig.Saved();
        await host.WaitFor<ShutdownMessage>();
        await rig.WaitState(s => s.Phase == ClickerPhase.Off);

        await WaitUntil(() => Volatile.Read(ref armedChanges) == 2);
        Assert.False(host.Killed);   // it left on its own after the goodbye
        Assert.Single(rig.Launcher.Sessions);
    }

    [Fact]
    public async Task A_helper_that_never_connects_is_ended_after_the_handshake_timeout_and_tried_once_more_then_the_state_is_an_error()
    {
        using var rig = new Rig(configure: l => l.AutoConnect = false);

        rig.Controller.Initialize();
        var first = await rig.Launcher.WaitForLaunch(1);
        await rig.Time.AdvanceWhenArmed(Timing.HandshakeTimeout);
        var second = await rig.Launcher.WaitForLaunch(2);
        await rig.Time.AdvanceWhenArmed(Timing.HandshakeTimeout);
        var state = await rig.WaitState(s => s.Phase == ClickerPhase.Error);

        Assert.True(first.Killed);
        Assert.True(second.Killed);
        Assert.Equal(ClickerFault.HandshakeFailed, state.Fault);
        Assert.Equal(2, rig.Launcher.Sessions.Count);   // restarted once, then it gives up
    }

    [Fact]
    public async Task A_helper_with_another_protocol_version_is_refused()
    {
        using var rig = new Rig();
        rig.Controller.Initialize();
        var host = await rig.Launcher.WaitForLaunch(1);

        await host.Say(new HelloMessage(ClickerProtocol.Version + 1, 4242));
        await rig.Launcher.WaitForLaunch(2);   // refused: killed and tried once more

        Assert.True(host.Killed);
        Assert.DoesNotContain(host.Received, m => m is ConfigMessage);
    }

    [Fact]
    public async Task A_helper_that_does_not_start_with_hello_is_refused()
    {
        using var rig = new Rig();
        rig.Controller.Initialize();
        var host = await rig.Launcher.WaitForLaunch(1);

        await host.Say(FakeHostSession.Idle());
        await rig.Launcher.WaitForLaunch(2);

        Assert.True(host.Killed);
    }

    [Fact]
    public async Task A_crash_is_answered_with_one_restart_then_an_error_until_retry()
    {
        using var rig = new Rig();
        var first = await rig.StartReady();

        first.Exit();
        var second = await rig.Launcher.WaitForLaunch(2);
        await second.Handshake();
        await rig.WaitState(s => s.Phase == ClickerPhase.Ready);

        second.Exit();
        var state = await rig.WaitState(s => s.Phase == ClickerPhase.Error);
        Assert.Equal(ClickerFault.Crashed, state.Fault);
        await Task.Delay(100);
        Assert.Equal(2, rig.Launcher.Sessions.Count);

        rig.Controller.Retry();
        var third = await rig.Launcher.WaitForLaunch(3);
        await third.Handshake();
        await rig.WaitState(s => s.Phase == ClickerPhase.Ready);
    }

    [Fact]
    public async Task Switching_the_clicker_off_and_on_clears_an_error_and_gives_the_restart_back()
    {
        using var rig = new Rig();
        var first = await rig.StartReady();
        first.Exit();
        (await rig.Launcher.WaitForLaunch(2)).Exit();
        await rig.WaitState(s => s.Phase == ClickerPhase.Error);

        rig.Settings.Armed = false;
        rig.Saved();
        await rig.WaitState(s => s.Phase == ClickerPhase.Off);
        rig.Settings.Armed = true;
        rig.Saved();

        await rig.Launcher.WaitForLaunch(3);
    }

    [Fact]
    public async Task A_launcher_that_throws_is_retried_once_then_reported()
    {
        using var rig = new Rig(configure: l => l.FailWith = new InvalidOperationException("no exe"));

        rig.Controller.Initialize();

        var state = await rig.WaitState(s => s.Phase == ClickerPhase.Error);
        Assert.Equal(ClickerFault.StartFailed, state.Fault);
    }

    [Fact]
    public async Task Unknown_and_malformed_helper_lines_are_skipped()
    {
        using var rig = new Rig();
        var host = await rig.StartReady();

        await host.SayRaw("{\"t\":\"fromTheFuture\",\"x\":1}\n");
        await host.SayRaw("this is not json\n");
        await host.SayRaw("\n");
        await host.Say(new LogMessage("info", "hi"));
        await host.Say(new StateMessage(true, true, false, null, 5, 10, 500, null));

        var state = await rig.WaitState(s => s.Phase == ClickerPhase.Running);
        Assert.Equal(5, state.Clicks);
    }

    // ---- state mirroring ----

    [Fact]
    public async Task The_helper_state_is_mirrored_and_the_last_stop_reason_survives_until_the_next_run()
    {
        using var rig = new Rig();
        var host = await rig.StartReady();

        await host.Say(new StateMessage(true, true, false, null, 120, 24.5, 5000, "game.exe"));
        var running = await rig.WaitState(s => s.Phase == ClickerPhase.Running);
        Assert.Equal(120, running.Clicks);
        Assert.Equal(24.5, running.Cps);
        Assert.Equal(5000, running.ElapsedMs);
        Assert.Equal("game.exe", running.BlockedByElevated);
        Assert.Null(running.LastStop);

        var end = new RunEndedMessage(DateTime.UtcNow, 5, 120, nameof(StopReason.TimeLimit), "60 s");
        await host.Say(end);
        await host.Say(FakeHostSession.Idle(nameof(StopReason.TimeLimit)));
        var stopped = await rig.WaitState(s => s.Phase == ClickerPhase.Ready && s.LastStop == StopReason.TimeLimit);
        Assert.Equal("60 s", stopped.LastStopDetail);
    }

    [Fact]
    public async Task State_changes_are_raised_only_when_something_changed()
    {
        using var rig = new Rig();
        var host = await rig.StartReady();
        int before;
        lock (rig.States) before = rig.States.Count;

        await host.Say(FakeHostSession.Idle());
        await host.Say(FakeHostSession.Idle());
        await host.Say(FakeHostSession.Idle(nameof(StopReason.Hotkey)));   // the pipe keeps the order: once this shows, the two before were handled
        await rig.WaitState(s => s.LastStop == StopReason.Hotkey);

        lock (rig.States) Assert.Equal(before + 1, rig.States.Count);
    }

    [Fact]
    public async Task The_master_switch_of_the_helper_is_mirrored()
    {
        using var rig = new Rig();
        rig.Controller.Initialize();
        var host = await rig.Launcher.WaitForLaunch(1);

        await host.Handshake(FakeHostSession.Idle() with { MasterAllowed = false });

        var state = await rig.WaitState(s => s.Phase == ClickerPhase.Ready);
        Assert.False(state.MasterAllowed);
    }

    [Fact]
    public async Task A_log_line_ahead_of_the_hello_does_not_break_the_handshake()
    {
        using var rig = new Rig();
        rig.Controller.Initialize();
        var host = await rig.Launcher.WaitForLaunch(1);

        await host.Say(new LogMessage("error", "The hotkey hooks are not running"));
        await host.Handshake();

        await rig.WaitState(s => s.Phase == ClickerPhase.Ready);
        Assert.Single(rig.Launcher.Sessions);
        Assert.False(host.Killed);
    }

    // ---- commands ----

    [Fact]
    public async Task Start_stop_and_picker_go_to_the_helper_and_start_needs_a_ready_helper()
    {
        using var rig = new Rig();
        Assert.False(rig.Controller.Start());   // nothing running yet
        var host = await rig.StartReady();

        Assert.True(rig.Controller.Start());
        Assert.True(rig.Controller.Stop());
        rig.Controller.SetPicker(true);
        rig.Controller.SetPicker(true);   // no repeat
        rig.Controller.SetPicker(false);

        await host.WaitFor<StartMessage>();
        await host.WaitFor<StopMessage>();
        await host.WaitFor<PickerMessage>(p => p.Active);
        await host.WaitFor<PickerMessage>(p => !p.Active);
        Assert.Equal(2, host.Received.OfType<PickerMessage>().Count());
    }

    [Fact]
    public async Task A_hotkey_capture_suppresses_hotkeys_like_a_picker_and_the_two_share_one_switch()
    {
        using var rig = new Rig();
        var host = await rig.StartReady();

        rig.Controller.SetHotkeyCapture(true);
        await host.WaitFor<PickerMessage>(p => p.Active);
        rig.Controller.SetPicker(true);          // already suppressed: nothing new
        rig.Controller.SetHotkeyCapture(false);  // the picker is still open: hotkeys stay off
        rig.Controller.SetPicker(false);         // both gone: now the helper hears hotkeys again

        await host.WaitFor<PickerMessage>(p => !p.Active);
        var pickers = host.Received.OfType<PickerMessage>().ToList();
        Assert.Equal(new[] { true, false }, pickers.Select(p => p.Active));
    }

    [Fact]
    public async Task A_running_hotkey_capture_is_told_to_a_helper_that_starts_afterwards()
    {
        using var rig = new Rig();
        rig.Controller.SetHotkeyCapture(true);

        rig.Controller.Initialize();
        var host = await rig.Launcher.WaitForLaunch(1);
        await host.Handshake();

        await host.WaitFor<PickerMessage>(p => p.Active);
        var order = host.Received.Select(m => m.GetType()).ToList();
        Assert.True(order.IndexOf(typeof(PickerMessage)) < order.IndexOf(typeof(ConfigMessage)));
    }

    [Fact]
    public async Task An_open_picker_is_told_to_a_helper_that_starts_afterwards()
    {
        using var rig = new Rig();
        rig.Controller.SetPicker(true);

        rig.Controller.Initialize();
        var host = await rig.Launcher.WaitForLaunch(1);
        await host.Handshake();

        await host.WaitFor<PickerMessage>(p => p.Active);
        var order = host.Received.Select(m => m.GetType()).ToList();
        Assert.True(order.IndexOf(typeof(PickerMessage)) < order.IndexOf(typeof(ConfigMessage)));   // before the config can arm any hotkey
    }

    // ---- configuration ----

    [Fact]
    public async Task Several_quick_edits_make_one_configuration_push_with_the_final_values()
    {
        using var rig = new Rig();
        var host = await rig.StartReady();
        Assert.Single(host.Received.OfType<ConfigMessage>());

        for (int i = 1; i <= 5; i++)
        {
            rig.Settings.ClickSpeed = 100 + i;
            rig.Saved();
        }
        Assert.Single(host.Received.OfType<ConfigMessage>());   // nothing before the debounce time is up

        await rig.Time.AdvanceWhenArmed(Timing.ConfigDebounce);
        var second = await host.WaitFor<ConfigMessage>(c => c.Settings.ClickSpeed == 105);
        rig.Time.Advance(Timing.ConfigDebounce);   // a late save of the same values pushes nothing more
        rig.Controller.Stop();
        await host.WaitFor<StopMessage>();       // everything queued before the stop has arrived

        Assert.Equal(105, second.Settings.ClickSpeed);
        Assert.Equal(2, host.Received.OfType<ConfigMessage>().Count());
    }

    [Fact]
    public async Task A_save_that_changes_nothing_the_helper_needs_pushes_nothing()
    {
        using var rig = new Rig();
        var host = await rig.StartReady();

        rig.Settings.Stats.AddRun(new RunRecord { Clicks = 1 });   // statistics and presets never go over the wire
        rig.Settings.Presets.Add(new ClickerPreset { Name = "x" });
        rig.Saved();
        await rig.Time.AdvanceWhenArmed(Timing.ConfigDebounce);
        rig.Controller.Stop();
        await host.WaitFor<StopMessage>();

        Assert.Single(host.Received.OfType<ConfigMessage>());
    }

    [Fact]
    public async Task The_pushed_configuration_is_a_copy_so_a_later_edit_cannot_change_a_queued_message()
    {
        using var rig = new Rig();
        var host = await rig.StartReady();

        rig.Settings.ClickSpeed = 11;
        rig.Saved();
        await rig.Time.AdvanceWhenArmed(Timing.ConfigDebounce);
        var pushed = await host.WaitFor<ConfigMessage>(c => c.Settings.ClickSpeed == 11);
        rig.Settings.ClickSpeed = 12;

        Assert.Equal(11, pushed.Settings.ClickSpeed);
    }

    // ---- statistics ----

    [Fact]
    public async Task A_finished_run_goes_into_the_statistics_newest_first_and_the_settings_are_saved()
    {
        using var rig = new Rig();
        var host = await rig.StartReady();
        var start = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

        await host.Say(new RunEndedMessage(start, 12.5, 300, nameof(StopReason.ClickLimit), "300"));
        await host.Say(new RunEndedMessage(start.AddMinutes(1), 3, 40, nameof(StopReason.Hotkey)));
        await WaitUntil(() => rig.Settings.Stats.Sessions == 2);

        var stats = rig.Settings.Stats;
        Assert.Equal(340, stats.TotalClicks);
        Assert.Equal(15.5, stats.TotalSeconds);
        Assert.Equal(nameof(StopReason.Hotkey), stats.LastRuns[0].StopReason);
        Assert.Equal(40, stats.LastRuns[0].Clicks);
        Assert.Equal(start, stats.LastRuns[1].StartUtc);
        Assert.Equal(2, rig.Saves);
        Assert.Equal(new[] { StopReason.ClickLimit, StopReason.Hotkey }, rig.Runs.Select(r => r.Reason).ToArray());
        Assert.Equal("300", rig.Runs[0].Detail);
    }

    [Fact]
    public async Task The_run_list_is_capped_at_100()
    {
        using var rig = new Rig();
        var host = await rig.StartReady();

        for (int i = 0; i < 105; i++) await host.Say(new RunEndedMessage(DateTime.UtcNow, 1, i, nameof(StopReason.Hotkey)));
        await WaitUntil(() => rig.Settings.Stats.Sessions == 105);

        Assert.Equal(100, rig.Settings.Stats.LastRuns.Count);
        Assert.Equal(104, rig.Settings.Stats.LastRuns[0].Clicks);
    }

    [Fact]
    public async Task An_unknown_stop_reason_is_recorded_as_none_not_dropped()
    {
        using var rig = new Rig();
        var host = await rig.StartReady();

        await host.Say(new RunEndedMessage(DateTime.UtcNow, 1, 1, "FromTheFuture"));
        await WaitUntil(() => rig.Settings.Stats.Sessions == 1);

        Assert.Equal(nameof(StopReason.None), rig.Settings.Stats.LastRuns[0].StopReason);
    }

    [Fact]
    public async Task Statistics_are_written_through_the_settings_thread_seam()
    {
        var posted = new List<Action>();
        var settings = new ClickerSettings { Armed = true };
        var launcher = new FakeHostLauncher();
        using var controller = new ClickerController(() => settings, () => { }, launcher, new FakePatternStore(), post: posted.Add, time: new ManualTime());
        controller.Initialize();
        var host = await launcher.WaitForLaunch(1);
        await host.Handshake();

        await host.Say(new RunEndedMessage(DateTime.UtcNow, 1, 5, nameof(StopReason.Hotkey)));
        await WaitUntil(() => { lock (posted) return posted.Count == 1; });

        Assert.Equal(0, settings.Stats.Sessions);   // not touched from the pipe thread
        posted[0]();
        Assert.Equal(1, settings.Stats.Sessions);
    }

    // ---- lock ----

    [Fact]
    public async Task A_lock_goes_to_the_helper_and_a_confirmation_keeps_it_alive()
    {
        using var rig = new Rig();
        var host = await rig.StartReady();

        rig.Controller.Lock(new BlacklistEntry { ExeName = "arma.exe", DisplayName = "Arma" });
        await host.WaitFor<LockMessage>();
        await host.Say(FakeHostSession.Idle(nameof(StopReason.Locked), locked: true));
        await rig.WaitState(s => s.LastStop == StopReason.Locked);   // the confirmation was handled
        rig.Time.Advance(Timing.LockConfirm * 3);                    // well beyond the confirmation time

        Assert.False(host.Killed);
        Assert.Single(rig.Launcher.Sessions);
        var state = rig.Controller.State;
        Assert.True(state.Locked);
        Assert.Equal("Arma", state.LockedBy);
    }

    [Fact]
    public async Task Locking_twice_sends_one_lock_message()
    {
        using var rig = new Rig();
        var host = await rig.StartReady();
        var entry = new BlacklistEntry { ExeName = "arma.exe", DisplayName = "Arma" };

        rig.Controller.Lock(entry);
        rig.Controller.Lock(entry);
        rig.Controller.Stop();
        await host.WaitFor<StopMessage>();

        Assert.Single(host.Received.OfType<LockMessage>());
    }

    [Fact]
    public async Task A_lock_the_helper_does_not_confirm_in_time_kills_it_and_the_new_one_starts_locked()
    {
        using var rig = new Rig();
        var host = await rig.StartReady();

        rig.Controller.Lock(new BlacklistEntry { ExeName = "arma.exe", DisplayName = "Arma" });
        rig.Time.Advance(Timing.LockConfirm);   // no confirmation

        var replacement = await rig.Launcher.WaitForLaunch(2);
        Assert.True(host.Killed);
        Assert.Equal(new[] { new ClickerRelease(ClickerMouseButton.Left, 0, false) }, rig.Launcher.Releases);   // what it held is let go
        await replacement.HandshakeEager(FakeHostSession.Idle(locked: true));
        await replacement.WaitFor<ConfigMessage>();
        var order = replacement.Received.Select(m => m.GetType()).ToList();
        int lockAt = order.IndexOf(typeof(LockMessage));
        Assert.True(lockAt >= 0 && lockAt < order.IndexOf(typeof(ConfigMessage)));   // locked before it can listen to a hotkey
        await rig.WaitState(s => s.Phase == ClickerPhase.Ready && s.Locked);
    }

    [Fact]
    public async Task Killing_for_an_unconfirmed_lock_does_not_use_up_the_crash_restart()
    {
        using var rig = new Rig();
        await rig.StartReady();
        rig.Controller.Lock(new BlacklistEntry { ExeName = "arma.exe", DisplayName = "Arma" });
        rig.Time.Advance(Timing.LockConfirm);
        var replacement = await rig.Launcher.WaitForLaunch(2);
        await replacement.HandshakeEager(FakeHostSession.Idle(locked: true));
        await rig.WaitState(s => s.Phase == ClickerPhase.Ready);

        replacement.Exit();   // a real crash: its one restart is still there

        await rig.Launcher.WaitForLaunch(3);
    }

    [Fact]
    public async Task A_helper_that_never_confirms_a_lock_is_given_up_on()
    {
        using var rig = new Rig();
        rig.Controller.Lock(new BlacklistEntry { ExeName = "arma.exe", DisplayName = "Arma" });
        rig.Controller.Initialize();

        // Every helper says hello and never reports Locked; the controller ends each one when the lock time is up.
        for (int n = 1; n <= 4; n++)
        {
            var host = await rig.Launcher.WaitForLaunch(n);
            await host.Say(new HelloMessage(ClickerProtocol.Version, 100 + n));
            await rig.Time.AdvanceWhenArmed(Timing.LockConfirm);
        }

        var state = await rig.WaitState(s => s.Phase == ClickerPhase.Error);
        Assert.Equal(ClickerFault.LockNotConfirmed, state.Fault);
        Assert.Equal(4, rig.Launcher.Sessions.Count);
    }

    [Fact]
    public async Task A_lock_set_before_the_helper_exists_is_part_of_its_handshake()
    {
        using var rig = new Rig();
        rig.Controller.Lock(new BlacklistEntry { ExeName = "arma.exe", DisplayName = "Arma" });

        rig.Controller.Initialize();
        var host = await rig.Launcher.WaitForLaunch(1);
        await host.Handshake(FakeHostSession.Idle(locked: true));

        await host.WaitFor<LockMessage>();
        await rig.WaitState(s => s.Phase == ClickerPhase.Ready && s.Locked);
    }

    [Fact]
    public async Task Unlock_goes_to_the_helper_and_cancels_the_confirmation_wait()
    {
        using var rig = new Rig();
        var host = await rig.StartReady();
        rig.Controller.Lock(new BlacklistEntry { ExeName = "arma.exe", DisplayName = "Arma" });
        rig.Controller.Unlock();

        await host.WaitFor<UnlockMessage>();
        rig.Time.Advance(Timing.LockConfirm * 3);

        Assert.False(host.Killed);   // nothing to confirm any more
        Assert.False(rig.Controller.State.Locked);
        Assert.Single(rig.Launcher.Sessions);
    }

    [Fact]
    public async Task Unlock_without_a_lock_says_nothing()
    {
        using var rig = new Rig();
        var host = await rig.StartReady();

        rig.Controller.Unlock();
        rig.Controller.Stop();
        await host.WaitFor<StopMessage>();

        Assert.DoesNotContain(host.Received, m => m is UnlockMessage);
    }

    [Fact]
    public async Task A_run_ended_by_the_lock_reports_who_locked()
    {
        using var rig = new Rig();
        var host = await rig.StartReady();
        rig.Controller.Lock(new BlacklistEntry { ExeName = "arma.exe", DisplayName = "Arma Reforger" });

        await host.Say(new RunEndedMessage(DateTime.UtcNow, 2, 10, nameof(StopReason.Locked)));
        await WaitUntil(() => rig.Runs.Count == 1);

        Assert.Equal("Arma Reforger", rig.Runs[0].LockedBy);
        Assert.Equal(StopReason.Locked, rig.Runs[0].Reason);
    }

    // ---- dispose ----

    [Fact]
    public async Task Dispose_says_goodbye_waits_for_the_helper_and_keeps_the_last_run()
    {
        var rig = new Rig();
        var host = await rig.StartReady();
        host.ExitOnShutdown = false;   // the helper reports its last run first, then leaves

        var dispose = Task.Run(rig.Controller.Dispose);
        await host.WaitFor<ShutdownMessage>();
        await host.Say(new RunEndedMessage(DateTime.UtcNow, 4, 77, nameof(StopReason.ClipperClosed)));
        host.Exit();
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(host.Killed);
        Assert.Equal(1, rig.Settings.Stats.Sessions);
        Assert.Equal(77, rig.Settings.Stats.TotalClicks);
        Assert.True(rig.Launcher.Disposed);
    }

    [Fact]
    public async Task Dispose_ends_a_helper_that_does_not_leave()
    {
        var rig = new Rig(timing: ShortShutdown);
        var host = await rig.StartReady();
        host.ExitOnShutdown = false;

        rig.Settings.InputType = ClickerInput.Keyboard;
        rig.Settings.KeyboardVk = 0x57;

        rig.Controller.Dispose();

        Assert.True(host.HasExited);
        Assert.True(host.Killed);
        Assert.Equal(new[] { new ClickerRelease(null, 0x57, true) }, rig.Launcher.Releases);   // once, although two paths kill it
    }

    [Fact]
    public async Task A_helper_that_leaves_by_itself_needs_no_release()
    {
        var rig = new Rig();
        await rig.StartReady();

        rig.Controller.Dispose();

        Assert.Empty(rig.Launcher.Releases);
    }

    [Fact]
    public async Task Abort_kills_a_ready_helper_and_releases_without_a_restart()
    {
        using var rig = new Rig();
        var host = await rig.StartReady();

        rig.Controller.Abort();

        Assert.True(host.Killed);
        Assert.Single(rig.Launcher.Releases);
        await Task.Delay(100);
        Assert.Single(rig.Launcher.Sessions);   // a deliberate end: no restart
    }

    [Fact]
    public async Task Dispose_ends_a_helper_that_is_still_connecting_at_once()
    {
        var rig = new Rig(configure: l => l.AutoConnect = false);
        rig.Controller.Initialize();
        var host = await rig.Launcher.WaitForLaunch(1);

        rig.Controller.Dispose();

        Assert.True(host.Killed);
        Assert.Empty(rig.Launcher.Releases);   // it never got a configuration, so it never pressed anything
    }

    [Fact]
    public async Task After_dispose_nothing_starts_and_calls_are_harmless()
    {
        var rig = new Rig(armed: false);
        rig.Controller.Initialize();
        rig.Controller.Dispose();

        rig.Settings.Armed = true;
        rig.Saved();
        rig.Controller.Lock(new BlacklistEntry { ExeName = "x.exe", DisplayName = "x" });
        rig.Controller.Unlock();
        rig.Controller.Dispose();
        await Task.Delay(50);

        Assert.Empty(rig.Launcher.Sessions);
        Assert.False(rig.Controller.Start());
    }

    // ---- mouse patterns ----

    private static readonly string IdA = new('a', 32);
    private static readonly string IdB = new('b', 32);

    /// <summary>Valid samples whose first raw step is <paramref name="dx"/>: different numbers make different samples.</summary>
    private static PatternData Samples(int dx, double durationMs = 2400)
        => new(new PatternTrack(new[] { 1, 2 }, new[] { dx, 1 }, new[] { 0, 2 }), new PatternTrack(new[] { 1 }, new[] { dx }, new[] { 0 }), durationMs);

    private static MousePattern Pattern(string id, int revision, string name = "Kreis")
        => new() { Id = id, Name = name, Revision = revision };

    private static string Text(PatternData data) => PatternCodec.Encode(data);

    [Fact]
    public async Task Helper_runs_when_only_patterns_armed()
    {
        using var rig = new Rig(armed: false, patternsArmed: true);

        var host = await rig.StartReady();

        Assert.True(rig.Controller.Armed);   // the guard watches the blacklist for the patterns, too
        var state = rig.Controller.State;
        Assert.True(state.PatternsArmed);
        Assert.False((await host.WaitFor<ConfigMessage>()).Settings.Armed);   // the helper itself keeps the clicker side dead
    }

    [Fact]
    public async Task Helper_stops_when_both_switches_off()
    {
        using var rig = new Rig(armed: true, patternsArmed: true);
        var host = await rig.StartReady();

        rig.Settings.Armed = false;   // the patterns still want the helper
        rig.Saved();
        await rig.Time.AdvanceWhenArmed(Timing.ConfigDebounce);
        await host.WaitFor<ConfigMessage>(c => !c.Settings.Armed);
        Assert.DoesNotContain(host.Received, m => m is ShutdownMessage);
        Assert.True(rig.Controller.Armed);
        Assert.Equal(ClickerPhase.Ready, rig.Controller.State.Phase);

        rig.Settings.MousePatternsArmed = false;
        rig.Saved();

        await host.WaitFor<ShutdownMessage>();
        await rig.WaitState(s => s.Phase == ClickerPhase.Off);
        Assert.False(rig.Controller.Armed);
        Assert.Single(rig.Launcher.Sessions);
    }

    [Fact]
    public async Task Pattern_data_follows_config_on_handshake()
    {
        using var rig = new Rig(armed: false, patternsArmed: true);
        rig.Settings.MousePatterns.Add(Pattern(IdA, 3));
        rig.Settings.MousePatterns.Add(Pattern(IdB, 1, "Welle"));   // no samples on disk: nothing to send, nothing thrown
        rig.Store.Put(IdA, Samples(7));

        var host = await rig.StartReady();

        var data = await host.WaitFor<PatternDataMessage>();
        Assert.Equal(IdA, data.Id);
        Assert.Equal(3, data.Revision);
        Assert.Equal(Text(Samples(7)), data.Data);
        var order = host.Received.Select(m => m.GetType()).ToList();
        Assert.True(order.IndexOf(typeof(ConfigMessage)) < order.IndexOf(typeof(PatternDataMessage)));   // the config names the revision first
        rig.Controller.Stop();
        await host.WaitFor<StopMessage>();
        Assert.Single(host.Received.OfType<PatternDataMessage>());
    }

    [Fact]
    public async Task Changed_revision_is_resent_once()
    {
        using var rig = new Rig(armed: false, patternsArmed: true);
        rig.Settings.MousePatterns.Add(Pattern(IdA, 1));
        rig.Settings.MousePatterns.Add(Pattern(IdB, 1, "Welle"));
        rig.Store.Put(IdA, Samples(1));
        rig.Store.Put(IdB, Samples(2));
        var host = await rig.StartReady();
        await host.WaitFor<PatternDataMessage>(d => d.Id == IdB);

        rig.Store.Put(IdA, Samples(5));
        rig.Settings.MousePatterns[0].Revision = 2;
        rig.Saved();
        await rig.Time.AdvanceWhenArmed(Timing.ConfigDebounce);
        var resent = await host.WaitFor<PatternDataMessage>(d => d.Revision == 2);
        rig.Settings.MousePatterns[1].Speed = 2;   // a change that is no new recording
        rig.Saved();
        await rig.Time.AdvanceWhenArmed(Timing.ConfigDebounce);
        await host.WaitFor<ConfigMessage>(c => c.Settings.MousePatterns.Count == 2 && c.Settings.MousePatterns[1].Speed == 2);
        rig.Controller.Stop();
        await host.WaitFor<StopMessage>();

        Assert.Equal(IdA, resent.Id);
        Assert.Equal(Text(Samples(5)), resent.Data);
        var sent = host.Received.OfType<PatternDataMessage>().Select(d => (d.Id, d.Revision)).ToList();
        Assert.Equal(new[] { (IdA, 1), (IdB, 1), (IdA, 2) }, sent);
    }

    [Fact]
    public async Task Samples_that_could_not_be_read_for_a_moment_are_sent_on_a_retry()
    {
        using var rig = new Rig(armed: false, patternsArmed: true);
        rig.Settings.MousePatterns.Add(Pattern(IdA, 3));
        rig.Store.Put(IdA, Samples(7));
        rig.Store.FailLoads(IdA, 2);   // the handshake's read ahead and its push both find the file held by another program

        var host = await rig.StartReady();
        rig.Controller.Stop();
        await host.WaitFor<StopMessage>();
        Assert.Empty(host.Received.OfType<PatternDataMessage>());

        await rig.Time.AdvanceWhenArmed(Timing.PatternLoadRetry);   // the config is unchanged: only the samples follow
        var data = await host.WaitFor<PatternDataMessage>();
        Assert.Equal((IdA, 3), (data.Id, data.Revision));
        Assert.Equal(Text(Samples(7)), data.Data);
        Assert.Single(host.Received.OfType<ConfigMessage>());
    }

    [Fact]
    public async Task A_read_error_that_does_not_pass_is_given_up_and_missing_samples_are_not_retried()
    {
        using var rig = new Rig(armed: false, patternsArmed: true);
        rig.Settings.MousePatterns.Add(Pattern(IdA, 3));
        rig.Settings.MousePatterns.Add(Pattern(IdB, 1, "Welle"));   // no samples at all: never tried again
        rig.Store.Put(IdA, Samples(7));
        rig.Store.FailLoads(IdA, int.MaxValue);

        await rig.StartReady();
        int missingReads = rig.Store.LoadsOf(IdB);         // the handshake's read ahead and its push
        for (int i = 0; i < 20; i++) rig.Time.Advance(Timing.PatternLoadRetry);
        int loads = rig.Store.LoadsOf(IdA);
        rig.Time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(loads, rig.Store.LoadsOf(IdA));       // given up: nothing reads it any more
        Assert.InRange(loads, 3, 20);                      // but it was tried again a few times
        Assert.Equal(missingReads, rig.Store.LoadsOf(IdB));
    }

    [Fact]
    public async Task Recording_creates_new_pattern_with_defaults()
    {
        using var rig = new Rig(armed: false, patternsArmed: true);
        rig.Settings.MousePatterns.Add(Pattern(IdA, 1, "Muster 1"));
        var host = await rig.StartReady();

        await host.Say(new PatternRecordedMessage("Ok", Text(Samples(4)), @"C:\Games\Skate.EXE", 2400));
        await WaitUntil(() => rig.Recorded.Count == 1);

        var p = Assert.Single(rig.Settings.MousePatterns, x => x.Id != IdA);
        Assert.True(MousePatternRules.IsValidId(p.Id));
        Assert.Equal("Muster 2", p.Name);
        Assert.True(p.Active);
        Assert.Null(p.Hotkey);
        Assert.Equal(ActivationMode.Hold, p.Mode);
        Assert.False(p.Repeat);
        Assert.Equal(1.0, p.Speed);
        Assert.Equal(PatternReplay.Auto, p.Replay);
        Assert.False(p.OnlyInProgram);
        Assert.Equal("Skate.exe", p.Program);
        Assert.Equal(2400, p.DurationMs);
        Assert.Equal(Text(Samples(4)), Text(rig.Store.Get(p.Id)!));
        Assert.Equal(1, rig.Saves);
        Assert.Equal(new PatternRecordedInfo("Ok", "Muster 2", 2.4), rig.Recorded[0]);

        // The settings save (SettingsChanged in the app) pushes the config naming it, and its samples follow.
        rig.Saved();
        await rig.Time.AdvanceWhenArmed(Timing.ConfigDebounce);
        var data = await host.WaitFor<PatternDataMessage>(d => d.Id == p.Id);
        Assert.Equal(p.Revision, data.Revision);
        Assert.Equal(Text(Samples(4)), data.Data);
        Assert.Contains(host.Received.OfType<ConfigMessage>(), c => c.Settings.MousePatterns.Any(x => x.Id == p.Id && x.Revision == p.Revision));
    }

    [Fact]
    public async Task Recording_replaces_target_and_keeps_options()
    {
        using var rig = new Rig(armed: false, patternsArmed: true);
        var hotkey = new ClickerHotkey { Keys = { 0x46 } };
        rig.Settings.MousePatterns.Add(new MousePattern
        {
            Id = IdA, Name = "Kreis", Hotkey = hotkey, Active = false, Mode = ActivationMode.Toggle, Repeat = true, Speed = 2,
            Replay = PatternReplay.Raw, Program = "old.exe", OnlyInProgram = true, DurationMs = 1000, Revision = 4
        });
        rig.Store.Put(IdA, Samples(1));
        var host = await rig.StartReady();
        rig.Controller.SetRecordTarget(IdA);
        Assert.Equal(IdA, rig.Controller.RecordTarget);

        await host.Say(new PatternRecordedMessage("Cut", Text(Samples(9, 60_000)), "new.exe", 60_000));
        await WaitUntil(() => rig.Recorded.Count == 1);

        var p = Assert.Single(rig.Settings.MousePatterns);
        Assert.Equal(IdA, p.Id);
        Assert.Equal("Kreis", p.Name);
        Assert.Same(hotkey, p.Hotkey);
        Assert.False(p.Active);
        Assert.Equal(ActivationMode.Toggle, p.Mode);
        Assert.True(p.Repeat);
        Assert.Equal(2, p.Speed);
        Assert.Equal(PatternReplay.Raw, p.Replay);
        Assert.True(p.OnlyInProgram);   // the checkbox stays, the program follows the new recording
        Assert.Equal("new.exe", p.Program);
        Assert.Equal(60_000, p.DurationMs);
        Assert.Equal(5, p.Revision);
        Assert.Equal(Text(Samples(9, 60_000)), Text(rig.Store.Get(IdA)!));
        Assert.Null(rig.Controller.RecordTarget);
        Assert.Equal(new PatternRecordedInfo("Cut", "Kreis", 60), rig.Recorded[0]);
    }

    [Fact]
    public async Task Empty_or_cancelled_recording_saves_nothing()
    {
        using var rig = new Rig(armed: false, patternsArmed: true);
        rig.Settings.MousePatterns.Add(Pattern(IdA, 1));
        var host = await rig.StartReady();
        rig.Controller.SetRecordTarget(IdA);

        await host.Say(new PatternRecordedMessage("Empty", null, "game.exe", 0));
        await host.Say(new PatternRecordedMessage("Cancelled", null, "game.exe", 0));
        await host.Say(new PatternRecordedMessage("Ok", "p1:garbage", "game.exe", 100));   // unreadable samples are no pattern either
        await WaitUntil(() => rig.Recorded.Count == 3);

        Assert.Equal(new[] { "Empty", "Cancelled", PatternRecordedInfo.Failed }, rig.Recorded.Select(r => r.Outcome));
        Assert.Equal(1, Assert.Single(rig.Settings.MousePatterns).Revision);
        Assert.Equal(0, rig.Store.Saves);
        Assert.Equal(0, rig.Saves);
        Assert.Equal(IdA, rig.Controller.RecordTarget);   // the next try still replaces it
    }

    [Fact]
    public async Task Session_end_during_recording_creates_nothing()
    {
        using var rig = new Rig(armed: false, patternsArmed: true);
        var host = await rig.StartReady();
        await host.Say(FakeHostSession.Idle() with { Recording = true });
        await rig.WaitState(s => s.Recording);

        host.Exit();   // killed mid-recording: no result ever comes

        await WaitUntil(() => rig.Recorded.Count == 1);
        Assert.Equal(new PatternRecordedInfo("Cancelled", null, 0), rig.Recorded[0]);
        Assert.Empty(rig.Settings.MousePatterns);
        Assert.Equal(0, rig.Store.Saves);
        Assert.Equal(0, rig.Saves);
        Assert.False(rig.Controller.State.Recording);
    }

    [Fact]
    public async Task A_recording_that_ended_with_a_result_is_not_reported_again_when_the_helper_ends()
    {
        using var rig = new Rig(armed: false, patternsArmed: true);
        var host = await rig.StartReady();
        await host.Say(FakeHostSession.Idle() with { Recording = true });
        await rig.WaitState(s => s.Recording);

        await host.Say(new PatternRecordedMessage("Empty", null, null, 0));
        await WaitUntil(() => rig.Recorded.Count == 1);
        host.Exit();
        await rig.Launcher.WaitForLaunch(2);   // the session's end was handled
        await Task.Delay(50);

        Assert.Single(rig.Recorded);
    }

    [Fact]
    public async Task A_recording_whose_samples_cannot_be_written_changes_nothing()
    {
        using var rig = new Rig(armed: false, patternsArmed: true);
        rig.Settings.MousePatterns.Add(Pattern(IdA, 1));
        var host = await rig.StartReady();
        rig.Store.FailSave = true;

        rig.Controller.SetRecordTarget(IdA);
        await host.Say(new PatternRecordedMessage("Ok", Text(Samples(3)), "game.exe", 2400));
        rig.Controller.SetRecordTarget(null);
        await host.Say(new PatternRecordedMessage("Ok", Text(Samples(3)), "game.exe", 2400));
        await WaitUntil(() => rig.Recorded.Count == 2);

        Assert.All(rig.Recorded, r => Assert.Equal(PatternRecordedInfo.Failed, r.Outcome));
        var p = Assert.Single(rig.Settings.MousePatterns);
        Assert.Equal(1, p.Revision);   // no new revision for samples that are not on disk
        Assert.Null(p.Program);
        Assert.Equal(0, rig.Saves);
    }

    [Fact]
    public async Task A_full_pattern_list_takes_no_new_recording_but_a_target_still_gets_one()
    {
        using var rig = new Rig(armed: false, patternsArmed: true);
        for (int i = 0; i < MousePatternRules.MaxPatterns; i++)
            rig.Settings.MousePatterns.Add(Pattern(i.ToString("x32"), 1, "P" + i));
        var host = await rig.StartReady();

        await host.Say(new PatternRecordedMessage("Ok", Text(Samples(3)), "game.exe", 2400));
        await WaitUntil(() => rig.Recorded.Count == 1);
        rig.Controller.SetRecordTarget(5.ToString("x32"));
        await host.Say(new PatternRecordedMessage("Ok", Text(Samples(3)), "game.exe", 2400));
        await WaitUntil(() => rig.Recorded.Count == 2);

        Assert.Equal(PatternRecordedInfo.Full, rig.Recorded[0].Outcome);
        Assert.Equal(new PatternRecordedInfo("Ok", "P5", 2.4), rig.Recorded[1]);
        Assert.Equal(MousePatternRules.MaxPatterns, rig.Settings.MousePatterns.Count);
        Assert.Equal(1, rig.Store.Saves);
    }

    [Fact]
    public async Task The_pattern_state_of_the_helper_is_mirrored()
    {
        using var rig = new Rig(armed: true, patternsArmed: true);
        var host = await rig.StartReady();

        await host.Say(FakeHostSession.Idle() with { Recording = true, Playing = IdA + "," + IdB, PatternsBlockedBy = "taskmgr.exe" });

        var state = await rig.WaitState(s => s.Recording);
        Assert.True(state.PatternsArmed);
        Assert.Equal(IdA + "," + IdB, state.Playing);
        Assert.Equal("taskmgr.exe", state.PatternsBlockedBy);
    }

    [Fact]
    public void Delete_pattern_removes_file_and_entry()
    {
        using var rig = new Rig(armed: false, patternsArmed: true);
        rig.Settings.MousePatterns.Add(Pattern(IdA, 1));
        rig.Settings.MousePatterns.Add(Pattern(IdB, 1, "Welle"));
        rig.Store.Put(IdA, Samples(1));
        rig.Store.Put(IdB, Samples(2));
        rig.Controller.SetRecordTarget(IdA);

        rig.Controller.DeletePattern(IdA.ToUpperInvariant());   // ids are file names: case does not matter

        Assert.Equal(IdB, Assert.Single(rig.Settings.MousePatterns).Id);
        Assert.Null(rig.Store.Get(IdA));
        Assert.NotNull(rig.Store.Get(IdB));
        Assert.Equal(1, rig.Saves);
        Assert.Null(rig.Controller.RecordTarget);   // the target is gone, the next recording is a new pattern
    }

    private static Task WaitUntil(Func<bool> condition) => Wait.Until(condition);
}
