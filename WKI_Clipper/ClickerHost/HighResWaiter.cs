using System;
using System.Diagnostics;
using System.Threading;
using WKI_Clipper.Native;

namespace WKI_Clipper.ClickerHost;

/// <summary>A monotonic millisecond clock. The engine never reads the real time itself, so tests drive a fake one.</summary>
internal interface IClock
{
    double NowMs { get; }
    DateTime UtcNow { get; }
}

internal sealed class StopwatchClock : IClock
{
    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

    public double NowMs => Stopwatch.GetTimestamp() * MsPerTick;
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>Waits for a point in time of an <see cref="IClock"/>; the engine thread's only way of sleeping.</summary>
internal interface IWaiter : IDisposable
{
    /// <summary>Blocks until the clock reaches <paramref name="deadlineMs"/>. false = <see cref="Wake"/> cut the wait short (a stop was requested).</summary>
    bool WaitUntil(double deadlineMs);

    /// <summary>Makes the current wait, and every following one, return false until <see cref="ClearWake"/>. Callable from any thread.</summary>
    void Wake();

    void ClearWake();

    /// <summary>A run starts / ends. The fallback waiter raises the system timer resolution only in between.</summary>
    void BeginSession();
    void EndSession();
}

/// <summary>
/// The engine's sleeper (design section 4): a high-resolution waitable timer is armed for ~1 ms before the deadline, the rest is a short
/// spin (a waiter made without spin sleeps it too: the pattern player's), and the stop signal is a second wait object so a stop wakes a
/// long wait at once. Long waits go in chunks of at most
/// <see cref="MaxChunkMs"/>; the timer is armed with a relative due time, so a system clock step cannot park the thread. No global timer-resolution change: without the
/// high-resolution timer (older Windows) a normal timer is used together with timeBeginPeriod(1), only while a run is active.
/// </summary>
internal sealed class HighResWaiter : IWaiter
{
    /// <summary>The timer fires this long before the deadline; the spin covers the rest.</summary>
    public const double EarlyMs = 1.0;

    /// <summary>Wake margin when only the normal timer exists (its accuracy is about 1-2 ms even at timeBeginPeriod(1)).</summary>
    public const double FallbackEarlyMs = 2.5;

    public const double MaxChunkMs = 250;

    /// <summary>The shortest sleep handed to the timer (a waiter without spin sleeps even the last few microseconds).</summary>
    private const double MinSleepMs = 0.05;

    private readonly IClock _clock;
    private readonly IntPtr _timer;
    private readonly IntPtr _wakeEvent;
    private readonly bool _highResolution;
    private readonly bool _spin;
    private readonly double _earlyMs;
    private readonly IntPtr[] _handles;
    private int _wake;
    private int _sessions;
    private bool _disposed;

    /// <param name="spin">
    /// false: never spins - the timer alone wakes the thread, up to its resolution (about a millisecond) late. For the pattern player,
    /// which ticks every millisecond while it plays and is cumulative (a late tick costs no accuracy): with the spin it would keep a core
    /// busy for as long as a pattern plays.
    /// </param>
    public HighResWaiter(IClock clock, bool spin = true)
    {
        _clock = clock;
        _spin = spin;
        _wakeEvent = Kernel32.CreateEventW(IntPtr.Zero, bManualReset: true, bInitialState: false, null);
        _timer = Kernel32.CreateWaitableTimerExW(IntPtr.Zero, null, Kernel32.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Kernel32.TIMER_ALL_ACCESS);
        _highResolution = _timer != IntPtr.Zero;
        if (!_highResolution)
            _timer = Kernel32.CreateWaitableTimerExW(IntPtr.Zero, null, 0, Kernel32.TIMER_ALL_ACCESS);
        _earlyMs = !spin ? 0 : _highResolution ? EarlyMs : FallbackEarlyMs;
        _handles = new[] { _timer, _wakeEvent };
        HostLog.Info(_highResolution ? "Waiter: high-resolution waitable timer."
            : _timer != IntPtr.Zero ? "Waiter: no high-resolution timer, using a normal timer with timeBeginPeriod(1) while running."
            : "Waiter: no waitable timer at all, using the stop event as the sleep.");
    }

    public bool WaitUntil(double deadlineMs)
    {
        while (true)
        {
            if (Volatile.Read(ref _wake) != 0) return false;
            double remaining = deadlineMs - _clock.NowMs;
            if (remaining <= 0) return true;

            if (!_spin || remaining > _earlyMs + MinSleepMs)
            {
                double sleepMs = Math.Min(Math.Max(remaining - _earlyMs, MinSleepMs), MaxChunkMs);
                if (SleepChunk(sleepMs)) return false;
                continue;
            }

            // Last stretch: spin to the deadline, still watching the stop flag.
            while (_clock.NowMs < deadlineMs)
            {
                if (Volatile.Read(ref _wake) != 0) return false;
                Thread.SpinWait(20);
            }
            return true;
        }
    }

    /// <summary>Sleeps up to <paramref name="ms"/>; true when the stop event ended the sleep.</summary>
    private bool SleepChunk(double ms)
    {
        if (_timer == IntPtr.Zero)
        {
            uint wait = (uint)Math.Max(1, Math.Round(ms));
            return Kernel32.WaitForSingleObject(_wakeEvent, wait) == Kernel32.WAIT_OBJECT_0;
        }

        long due = -(long)(ms * 10_000);   // 100 ns units; negative = relative (interrupt time, immune to system clock steps); ms >= 0.05, so never 0
        if (!Kernel32.SetWaitableTimerEx(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
        {
            Thread.Sleep(1);
            return Volatile.Read(ref _wake) != 0;
        }
        uint result = Kernel32.WaitForMultipleObjects(2, _handles, false, Kernel32.INFINITE);
        return result == Kernel32.WAIT_OBJECT_0 + 1;
    }

    public void Wake()
    {
        Volatile.Write(ref _wake, 1);
        if (_wakeEvent != IntPtr.Zero) Kernel32.SetEvent(_wakeEvent);
    }

    public void ClearWake()
    {
        Volatile.Write(ref _wake, 0);
        if (_wakeEvent != IntPtr.Zero) Kernel32.ResetEvent(_wakeEvent);
    }

    public void BeginSession()
    {
        if (!_highResolution && Interlocked.Increment(ref _sessions) == 1) WinMm.timeBeginPeriod(1);
    }

    public void EndSession()
    {
        if (!_highResolution && Interlocked.Decrement(ref _sessions) == 0) WinMm.timeEndPeriod(1);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_highResolution && Interlocked.Exchange(ref _sessions, 0) > 0) WinMm.timeEndPeriod(1);
        if (_timer != IntPtr.Zero) Kernel32.CloseHandle(_timer);
        if (_wakeEvent != IntPtr.Zero) Kernel32.CloseHandle(_wakeEvent);
    }
}
