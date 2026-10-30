using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.Services.AutoClicker;

namespace WKI_Clipper.ClickerHost;

/// <summary>What the command line of a helper process says.</summary>
internal sealed record HostArgs(string PipeName, int ParentPid);

/// <summary>
/// Entry of the AutoClicker helper process (design section 2): <c>WKI_Clipper.exe --clicker-host &lt;pipeName&gt; &lt;parentPid&gt;</c>.
/// It has no window, no tray and no single-instance mutex, logs to its own file and runs only as long as the clipper wants it: it ends
/// when the pipe closes, <c>shutdown</c> arrives or the parent process exits - and always lets go of every key it pressed.
/// </summary>
internal static class HostMain
{
    public const string Switch = "--clicker-host";

    private const int ConnectTimeoutMs = 5000;
    private const int StartZonePollMs = 100;
    private const int PatternPollMs = 50;

    /// <summary>Exit codes of the helper process.</summary>
    public const int ExitOk = 0, ExitBadArguments = 2, ExitNoPipe = 3, ExitVersion = 4, ExitFailure = 5;

    /// <summary>
    /// null unless the arguments are exactly the host switch, a pipe name the clipper could have made and a positive process id. The
    /// check is strict because this decides whether the whole program starts as the host.
    /// </summary>
    public static HostArgs? TryParseArgs(string[]? args)
    {
        if (args is null || args.Length != 3 || !string.Equals(args[0], Switch, StringComparison.Ordinal)) return null;
        if (!ClickerProtocol.IsValidPipeName(args[1])) return null;
        if (!int.TryParse(args[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int pid) || pid <= 0) return null;
        return new HostArgs(args[1], pid);
    }

    /// <summary>true when the command line asks for host mode at all (even with broken arguments, so a bad start never opens the full app).</summary>
    public static bool IsHostInvocation(string[]? args)
        => args is { Length: > 0 } && string.Equals(args[0], Switch, StringComparison.Ordinal);

    /// <summary>
    /// The release mode (<see cref="ClickerRelease"/>): blind ups for what a killed helper may have held, then exit. No pipe, no hook,
    /// no log file - it runs a few milliseconds.
    /// </summary>
    public static int RunRelease(string[] args)
    {
        if (ClickerRelease.TryParse(args) is not { } release) return ExitBadArguments;
        try
        {
            var injector = InputInjector.CreateNative(() => new PixelRect(0, 0, 1, 1));   // ups only: the screen does not matter
            return injector.ReleaseBlind(release.Button, release.Vk, release.Shift) ? ExitOk : ExitFailure;
        }
        catch { return ExitFailure; }
    }

    /// <summary>Runs the helper to its end and returns the process exit code.</summary>
    public static int Run(string[] args)
    {
        HostLog.Rotate();
        HostLog.Info("==================== clicker host start ====================");
        var parsed = TryParseArgs(args);
        if (parsed is null)
        {
            HostLog.Error("Bad arguments: " + string.Join(' ', args));
            return ExitBadArguments;
        }

        InputInjector? injector = null;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            HostLog.Error("Unhandled exception in the host", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
            try { injector?.ReleaseAll(); } catch { }
        };

        try
        {
            // Pause-free latency for the click loop: no blocking gen2 collections while it runs.
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        }
        catch (Exception ex) { HostLog.Warn("GC latency mode not set: " + ex.Message); }

        try { return RunConnected(parsed, ref injector); }
        catch (Exception ex)
        {
            HostLog.Error("Host failed", ex);
            try { injector?.ReleaseAll(); } catch { }
            return ExitFailure;
        }
    }

    private static int RunConnected(HostArgs args, ref InputInjector? injector)
    {
        using var pipe = new NamedPipeClientStream(".", args.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        try { pipe.Connect(ConnectTimeoutMs); }
        catch (Exception ex)
        {
            HostLog.Error("Could not connect to the clipper's pipe: " + ex.Message);
            return ExitNoPipe;
        }
        HostLog.Info("Connected to the clipper (parent pid " + args.ParentPid + ").");

        using var cts = new CancellationTokenSource();
        using var parentWatch = WatchParent(args.ParentPid, cts);
        if (parentWatch is null && !ParentAlive(args.ParentPid))
        {
            HostLog.Warn("The parent process is already gone.");
            return ExitOk;
        }

        var clock = new StopwatchClock();
        var env = new WindowsHostEnvironment();
        injector = InputInjector.CreateNative(() => env.VirtualScreen);
        using var waiter = new HighResWaiter(clock);
        var zones = new ZoneTracker();
        var engine = new ClickEngine(injector, waiter, clock, env, zones, new Randomizer());
        var tracker = new HotkeyTracker();

        // Mouse patterns (mouse-patterns design 5.1): the player has its own thread and its own waiter (the engine's wake is sticky),
        // one that sleeps instead of spinning; declared after the waiter, so on the way out the player ends before its waiter is disposed.
        using var recorder = new PatternRecorder(clock);
        using var patternWaiter = new HighResWaiter(clock, spin: false);
        using var player = new PatternPlayer(injector, patternWaiter, clock, new WindowsPatternEnvironment(() => env.Monitors));
        Func<bool> overClipper = () => WindowsHostEnvironment.IsCursorOverProcess(args.ParentPid);

        HostCoordinator? coordinator = null;
        using var server = new HostServer(new ClickerChannel(pipe), Environment.ProcessId, message => coordinator?.Handle(message));
        var patterns = new PatternCoordinator(player, recorder, WindowsHostEnvironment.ReadForeground, overClipper, server.Send);
        coordinator = new HostCoordinator(engine, tracker, zones, () => env.CursorPosition, server.Send, overClipper, patterns);
        HostLog.Forward = (level, text) => server.Send(new LogMessage(level, text));

        // The hook watchdog's reference and the source of a pattern recording's raw track; the hook starts and disposes it. Both sinks
        // stay set: the recorder ignores motion while it does not record, and the hook then skips the cursor query.
        var rawInput = new RawInputMonitor(clock) { MouseSink = recorder };
        // UIPI: with an elevated window in front, a non-elevated host's low-level hooks see nothing - the watchdog must not take that for a dead hook.
        using var hook = new HotkeyHook(tracker, rawInput, env.IsKeyDown,
            () => !env.HostElevated && WindowsHostEnvironment.IsForegroundWindowElevated(), isInjected: vk => vk == engine.OutputVk)
        {
            PointerSink = recorder
        };
        if (!hook.Start()) HostLog.Error("The hotkey hooks are not running (yet): hotkeys do not work until they are.");
        using var zoneTimer = new System.Threading.Timer(_ =>
        {
            try { coordinator?.PollStartZone(); }
            catch (Exception ex) { HostLog.Error("Start-zone poll failed", ex); }
        }, null, StartZonePollMs, StartZonePollMs);
        using var patternTimer = new System.Threading.Timer(_ =>
        {
            try { patterns.Poll(); }
            catch (Exception ex) { HostLog.Error("Pattern poll failed", ex); }
        }, null, PatternPollMs, PatternPollMs);

        HostEnd end;
        try { end = server.RunAsync(cts.Token).GetAwaiter().GetResult(); }
        finally { HostLog.Forward = null; }
        HostLog.Info("Conversation ended: " + end);

        zoneTimer.Dispose();
        patternTimer.Dispose();
        coordinator.Shutdown(TimeSpan.FromMilliseconds(500));   // stops every playback and a recording first
        player.Dispose();                                        // nothing moves any more once this returns
        try { injector.ReleaseAll(); } catch { }
        try { server.FlushAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult(); } catch { }
        hook.Dispose();
        return end == HostEnd.VersionMismatch ? ExitVersion : ExitOk;
    }

    /// <summary>Cancels <paramref name="cts"/> when the parent process exits. null when the parent cannot be watched (already gone).</summary>
    private static IDisposable? WatchParent(int pid, CancellationTokenSource cts)
    {
        try
        {
            var parent = Process.GetProcessById(pid);
            parent.EnableRaisingEvents = true;
            parent.Exited += (_, _) =>
            {
                HostLog.Info("The parent process exited.");
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            };
            if (parent.HasExited) cts.Cancel();
            return parent;
        }
        catch (Exception ex)
        {
            HostLog.Warn("Parent process not watchable: " + ex.Message);
            return null;
        }
    }

    private static bool ParentAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }
}
