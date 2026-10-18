using System;
using System.Collections.Generic;

namespace WKI_Clipper.Services;

/// <summary>
/// Turns a stream of (time, pad, buttons) samples into "action X was triggered". Pure: no clock, no
/// threads, no I/O, so every rule is unit-tested. Not thread-safe; the caller serializes access.
///
/// Rules:
///  - an action fires when EXACTLY its mask (no other button) has been held for the hold time on ONE pad;
///    any change of the held buttons restarts the hold;
///  - after a fire the combo is spent until EVERY button of it has been released (letting go of just one
///    button and pressing it again does not re-arm);
///  - a global cooldown after any fire stops a second pad (or a virtual pad echoing the physical one) from
///    firing again. A hold that completes inside the cooldown is dropped, not deferred, so an echo cannot
///    fire later while the buttons are still down.
/// Timestamps are uint milliseconds and every difference is taken with unchecked wrap-around arithmetic.
/// </summary>
public sealed class GamepadComboDetector
{
    public const int DefaultHoldMs = 500;
    public const int MinHoldMs = 300;
    public const int MaxHoldMs = 1500;
    public const int DefaultCooldownMs = 2000;

    private sealed class Slot
    {
        public ushort Mask;
        public uint Since;
        public bool NeedRelease;
        /// <summary>Actions that fired during the current press, with the mask they fired for.</summary>
        public readonly Dictionary<string, ushort> Spent = new();
    }

    private readonly Dictionary<int, Slot> _slots = new();
    private (string Action, ushort Mask)[] _bindings = Array.Empty<(string, ushort)>();
    private readonly int _cooldownMs;
    private int _holdMs = DefaultHoldMs;
    private bool _everFired;
    private uint _lastFire;

    public GamepadComboDetector(int cooldownMs = DefaultCooldownMs) => _cooldownMs = Math.Max(0, cooldownMs);

    /// <summary>Hold time in ms; always inside <see cref="MinHoldMs"/>..<see cref="MaxHoldMs"/>.</summary>
    public int HoldMs
    {
        get => _holdMs;
        set => _holdMs = ClampHoldMs(value);
    }

    /// <summary>A hand-edited 0 would turn every two-button press into a clip, so the range is enforced.</summary>
    public static int ClampHoldMs(int ms) => Math.Clamp(ms, MinHoldMs, MaxHoldMs);

    /// <summary>Replaces the combos. Empty masks are ignored; the actions are ordered ordinally for determinism.</summary>
    public void SetBindings(IEnumerable<KeyValuePair<string, ushort>> bindings)
    {
        var list = new List<(string Action, ushort Mask)>();
        foreach (var kv in bindings)
            if (kv.Value != 0) list.Add((kv.Key, kv.Value));
        list.Sort((a, b) => string.CompareOrdinal(a.Action, b.Action));
        _bindings = list.ToArray();
    }

    /// <summary>
    /// Feeds the current buttons of one pad. Returns the action to run, or null. Call it on every poll, also
    /// when nothing changed: the hold time elapses between changes.
    /// </summary>
    public string? Update(uint nowMs, int slot, ushort buttons)
    {
        if (!_slots.TryGetValue(slot, out var s))
        {
            s = new Slot { Since = nowMs };
            _slots[slot] = s;
        }

        if (s.NeedRelease)
        {
            if (buttons != 0)
            {
                s.Mask = 0;
                return null;
            }
            s.NeedRelease = false;
            s.Mask = 0;
            s.Since = nowMs;
            s.Spent.Clear();
        }

        if (buttons != s.Mask)
        {
            s.Mask = buttons;
            s.Since = nowMs;
        }

        // A combo comes back only once every one of its buttons has been let go.
        if (s.Spent.Count > 0)
        {
            List<string>? rearmed = null;
            foreach (var kv in s.Spent)
                if ((buttons & kv.Value) == 0) (rearmed ??= new()).Add(kv.Key);
            if (rearmed != null)
                foreach (var a in rearmed) s.Spent.Remove(a);
        }

        if (buttons == 0) return null;

        foreach (var (action, mask) in _bindings)
        {
            if (mask != buttons) continue;
            if (s.Spent.ContainsKey(action)) return null;
            if (Elapsed(nowMs, s.Since) < (uint)_holdMs) return null;

            s.Spent[action] = mask;
            if (_everFired && Elapsed(nowMs, _lastFire) < (uint)_cooldownMs)
                return null;   // an echo of a press that already fired, or a double press: dropped for good

            _everFired = true;
            _lastFire = nowMs;
            return action;
        }
        return null;
    }

    /// <summary>The pad went away: forget its press.</summary>
    public void ResetSlot(int slot) => _slots.Remove(slot);

    /// <summary>
    /// Ignore the pad until all of its buttons are up. Used after learn mode, when the buttons that were just
    /// held must not fire anything.
    /// </summary>
    public void RequireRelease(int slot)
    {
        if (!_slots.TryGetValue(slot, out var s))
        {
            s = new Slot();
            _slots[slot] = s;
        }
        s.NeedRelease = true;
    }

    /// <summary>Forget every press and the cooldown.</summary>
    public void Reset()
    {
        _slots.Clear();
        _everFired = false;
    }

    /// <summary>Milliseconds from <paramref name="since"/> to <paramref name="now"/>, correct across the uint wrap.</summary>
    internal static uint Elapsed(uint now, uint since) => unchecked(now - since);
}
