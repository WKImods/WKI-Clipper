using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WKI_Clipper.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> whose time only moves when a test calls <see cref="Advance"/>. Due timers fire on the advancing
/// thread, in due order. Timers of <see cref="CancellationTokenSource"/> and <see cref="Task.Delay(TimeSpan, TimeProvider)"/> created
/// with it run here too, so a timeout can never fire because the machine was slow.
/// </summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate) return _now.UtcTicks;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the time on and fires every timer that falls due on the way (a periodic one as often as it is due).</summary>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_gate) target = _now + by;
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due is { } d && d <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }
                _now = next.Due!.Value;
                next.Due = next.Period > TimeSpan.Zero && next.Period != Timeout.InfiniteTimeSpan ? _now + next.Period : null;
            }
            next.Fire();
        }
    }

    /// <summary>Waits (in real time) until a timer is armed that falls due within <paramref name="by"/>, then advances by that much.</summary>
    public async Task AdvanceWhenArmed(TimeSpan by, int timeoutMs = 10000)
    {
        await Wait.Until(() =>
        {
            lock (_gate)
            {
                var target = _now + by;
                return _timers.Any(t => t.Due is { } d && d <= target);
            }
        }, timeoutMs);
        Advance(by);
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTime _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private bool _disposed;

        public ManualTimer(ManualTime owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        /// <summary>Guarded by the owner's gate.</summary>
        public DateTimeOffset? Due;
        public TimeSpan Period;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._gate)
            {
                if (_disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : _owner._now + dueTime;
                Period = period;
                return true;
            }
        }

        public void Fire()
        {
            lock (_owner._gate)
            {
                if (_disposed) return;
            }
            _callback(_state);
        }

        public void Dispose()
        {
            lock (_owner._gate)
            {
                _disposed = true;
                Due = null;
                _owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Polling waits for the async tests. The condition is checked once more after the deadline, so a test thread that was held up past it still sees a state that did arrive.</summary>
internal static class Wait
{
    public static async Task Until(Func<bool> condition, int timeoutMs = 10000, Func<string>? what = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                if (condition()) return;
                throw new TimeoutException(what?.Invoke() ?? "Condition never became true.");
            }
            await Task.Delay(5);
        }
    }
}
