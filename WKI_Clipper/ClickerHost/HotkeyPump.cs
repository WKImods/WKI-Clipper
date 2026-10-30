using System;
using System.Collections.Concurrent;
using WKI_Clipper.Services.AutoClicker;

namespace WKI_Clipper.ClickerHost;

internal enum HookInputKind : byte { Key, Wheel, Resync }

/// <summary>
/// What a hook callback recorded: a key or mouse button going down or up, one wheel notch (<see cref="Vk"/> is the wheel pseudo
/// key), or a request to re-sync the key state. <see cref="AtMs"/> is the hotkey clock when it happened.
/// </summary>
internal readonly record struct HookInput(HookInputKind Kind, int Vk, bool Down, long AtMs)
{
    public static HookInput Key(int vk, bool down, long atMs) => new(HookInputKind.Key, vk, down, atMs);
    public static HookInput Wheel(int wheelVk, long atMs) => new(HookInputKind.Wheel, wheelVk, true, atMs);
    public static HookInput Resync(long atMs) => new(HookInputKind.Resync, 0, false, atMs);
}

/// <summary>
/// The wheel as keys (free-hotkeys design, section 3): a notch in one direction presses that wheel key, it stays held while notches in
/// the same direction keep coming and is released after <see cref="QuietMs"/> without one. One scroll gesture is therefore one press.
/// A notch in the opposite direction of the same axis releases the other direction at once. Pure: the caller passes the clock.
/// </summary>
internal sealed class WheelHold
{
    /// <summary>Quiet time after the last notch that ends a wheel "press".</summary>
    public const int QuietMs = 250;

    // Index = wheel code - WheelUp: 0 up, 1 down, 2 left, 3 right; i ^ 1 is the opposite direction of the same axis.
    private readonly long[] _lastNotch = new long[4];
    private readonly bool[] _held = new bool[4];

    public void Notch(int wheelVk, long atMs, Action<int, bool> emit)
    {
        if (!ClickerVk.IsWheel(wheelVk)) return;
        int i = wheelVk - ClickerVk.WheelUp;
        Expire(atMs, emit);
        if (_held[i ^ 1])
        {
            _held[i ^ 1] = false;
            emit(ClickerVk.WheelUp + (i ^ 1), false);
        }
        _lastNotch[i] = atMs;
        if (_held[i]) return;
        _held[i] = true;
        emit(wheelVk, true);
    }

    /// <summary>Releases every wheel key whose last notch is at least <see cref="QuietMs"/> old.</summary>
    public void Expire(long nowMs, Action<int, bool> emit)
    {
        for (int i = 0; i < _held.Length; i++)
        {
            if (!_held[i] || nowMs - _lastNotch[i] < QuietMs) continue;
            _held[i] = false;
            emit(ClickerVk.WheelUp + i, false);
        }
    }

    /// <summary>When the next held wheel key is due for release; null when none is held.</summary>
    public long? NextReleaseAt
    {
        get
        {
            long? next = null;
            for (int i = 0; i < _held.Length; i++)
                if (_held[i] && (next is null || _lastNotch[i] + QuietMs < next)) next = _lastNotch[i] + QuietMs;
            return next;
        }
    }
}

/// <summary>
/// Between the hook callbacks and everything that reacts to a hotkey. The callbacks only <see cref="Post"/> what they saw (a lock-free
/// queue write) and return at once; the hotkey worker thread <see cref="Drain"/>s the queue into the <see cref="HotkeyTracker"/>, whose
/// events run the coordinator, the activation logic, the engine start and the over-the-clipper check. Windows silently removes a
/// low-level hook that answers too slowly, so none of that work may run on the hook thread.
/// </summary>
internal sealed class HotkeyPump
{
    private readonly ConcurrentQueue<HookInput> _queue = new();
    private readonly HotkeyTracker _tracker;
    private readonly WheelHold _wheel = new();
    private readonly Action<int, bool> _emit;
    private readonly Func<int, bool> _isDownForResync;

    /// <param name="isKeyDown">The real key state for a re-sync (GetAsyncKeyState). Wheel keys are never asked: Windows has no state for them.</param>
    /// <param name="isInjected">
    /// true for the key or button the clicker is sending right now. Its GetAsyncKeyState follows the injected ups too, so between two
    /// clicks it reads "up" while the user still holds it (output = hotkey key): a re-sync keeps it instead of ending a Hold run.
    /// </param>
    public HotkeyPump(HotkeyTracker tracker, Func<int, bool> isKeyDown, Func<int, bool>? isInjected = null)
    {
        _tracker = tracker;
        _emit = tracker.OnKey;
        isInjected ??= _ => false;
        _isDownForResync = vk => ClickerVk.IsWheel(vk) || isInjected(vk) || isKeyDown(vk);
    }

    /// <summary>Queues one recorded input. Any thread; never blocks.</summary>
    public void Post(HookInput input) => _queue.Enqueue(input);

    /// <summary>Feeds everything queued to the tracker in order, then releases wheel keys that went quiet. The worker thread only.</summary>
    public void Drain(long nowMs)
    {
        while (_queue.TryDequeue(out var input))
        {
            // A wheel key that went quiet before this input was released before it, too.
            _wheel.Expire(input.AtMs, _emit);
            switch (input.Kind)
            {
                case HookInputKind.Key: _tracker.OnKey(input.Vk, input.Down); break;
                case HookInputKind.Wheel: _wheel.Notch(input.Vk, input.AtMs, _emit); break;
                case HookInputKind.Resync: _tracker.Resync(_isDownForResync); break;
            }
        }
        _wheel.Expire(nowMs, _emit);
    }

    /// <summary>When the worker must wake to release a held wheel key; null when none is held.</summary>
    public long? NextWheelReleaseAt => _wheel.NextReleaseAt;
}
