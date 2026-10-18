using System;
using System.Collections.Generic;

namespace WKI_Clipper.Services;

public enum LearnStatus
{
    /// <summary>Still listening.</summary>
    Pending,
    /// <summary>A usable combo was held; <see cref="LearnOutcome.Mask"/> holds it.</summary>
    Captured,
    /// <summary>Only single buttons were held - a combo needs at least two.</summary>
    TooFewButtons,
    /// <summary>The combo belongs to another action (<see cref="LearnOutcome.UsedBy"/>).</summary>
    AlreadyUsed,
    /// <summary>Nothing usable was held within the time limit.</summary>
    TimedOut,
    /// <summary>The attempt was aborted (cancelled by the user, or the service stopped).</summary>
    Cancelled
}

public readonly record struct LearnOutcome(LearnStatus Status, ushort Mask = 0, string? UsedBy = null)
{
    public static readonly LearnOutcome Pending = new(LearnStatus.Pending);

    public bool IsFinal => Status != LearnStatus.Pending;
}

/// <summary>
/// "Kombi anlernen": the user presses and HOLDS the buttons of the new combo. The capture is the exact mask
/// that stayed unchanged on one pad for the hold time (1 s). It needs at least two buttons and must not
/// already belong to another action. Pure like the detector: samples in, a <see cref="LearnOutcome"/> out.
/// </summary>
public sealed class GamepadComboLearner
{
    public const int DefaultTimeoutMs = 5000;
    public const int DefaultHoldMs = 1000;

    private readonly (string Action, ushort Mask)[] _others;
    private readonly uint _start;
    private readonly int _timeoutMs;
    private readonly int _holdMs;
    private readonly Dictionary<int, (ushort Mask, uint Since)> _slots = new();
    private bool _sawSingleButton;
    private LearnOutcome _final = LearnOutcome.Pending;

    /// <param name="action">The action being learned; its own current combo may be learned again.</param>
    /// <param name="bindings">The current combos of all actions.</param>
    /// <param name="startMs">Time of the start, for the timeout.</param>
    public GamepadComboLearner(string action, IReadOnlyDictionary<string, ushort> bindings, uint startMs,
        int timeoutMs = DefaultTimeoutMs, int holdMs = DefaultHoldMs)
    {
        var others = new List<(string, ushort)>();
        foreach (var kv in bindings)
            if (kv.Key != action && kv.Value != 0) others.Add((kv.Key, kv.Value));
        others.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        _others = others.ToArray();
        _start = startMs;
        _timeoutMs = timeoutMs;
        _holdMs = holdMs;
    }

    /// <summary>Feeds the current buttons of one pad (call on every poll while any button is down).</summary>
    public LearnOutcome Feed(uint nowMs, int slot, ushort buttons)
    {
        if (_final.IsFinal) return _final;

        if (!_slots.TryGetValue(slot, out var s)) s = (0, _start);
        if (buttons != s.Mask) s = (buttons, nowMs);
        _slots[slot] = s;

        return Tick(nowMs);
    }

    /// <summary>Re-evaluates the holds and the timeout without new input.</summary>
    public LearnOutcome Tick(uint nowMs)
    {
        if (_final.IsFinal) return _final;

        foreach (var kv in _slots)
        {
            var (mask, since) = kv.Value;
            if (mask == 0 || GamepadComboDetector.Elapsed(nowMs, since) < (uint)_holdMs) continue;

            if (GamepadButtons.Count(mask) < GamepadButtons.MinComboButtons)
            {
                _sawSingleButton = true;   // keep waiting: the second button may still follow
                continue;
            }

            foreach (var (action, other) in _others)
                if (other == mask)
                    return _final = new LearnOutcome(LearnStatus.AlreadyUsed, mask, action);

            return _final = new LearnOutcome(LearnStatus.Captured, mask);
        }

        if (GamepadComboDetector.Elapsed(nowMs, _start) >= (uint)_timeoutMs)
            return _final = new LearnOutcome(_sawSingleButton ? LearnStatus.TooFewButtons : LearnStatus.TimedOut);

        return LearnOutcome.Pending;
    }

    /// <summary>
    /// The whole capture as a pure function of a recorded sequence; the learner starts at the first sample.
    /// Returns the final outcome, or <see cref="LearnOutcome.Pending"/> when the sequence ends undecided.
    /// </summary>
    public static LearnOutcome Run(string action, IReadOnlyDictionary<string, ushort> bindings,
        IEnumerable<(uint TimeMs, int Slot, ushort Buttons)> samples,
        int timeoutMs = DefaultTimeoutMs, int holdMs = DefaultHoldMs)
    {
        GamepadComboLearner? learner = null;
        var last = LearnOutcome.Pending;
        foreach (var (time, slot, buttons) in samples)
        {
            learner ??= new GamepadComboLearner(action, bindings, time, timeoutMs, holdMs);
            last = learner.Feed(time, slot, buttons);
            if (last.IsFinal) return last;
        }
        return last;
    }
}
