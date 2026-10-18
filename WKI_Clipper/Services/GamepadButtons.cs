using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace WKI_Clipper.Services;

/// <summary>
/// The XInput button bits (XINPUT_GAMEPAD_*) with the Xbox names, plus the rules for a usable combo.
/// Share and Guide do not exist in XInput and are not supported; triggers and sticks are analog and are
/// not part of a combo.
/// </summary>
public static class GamepadButtons
{
    public const ushort Up = 0x0001;
    public const ushort Down = 0x0002;
    public const ushort Left = 0x0004;
    public const ushort Right = 0x0008;
    /// <summary>XINPUT_GAMEPAD_START, the "hamburger" button.</summary>
    public const ushort Menu = 0x0010;
    /// <summary>XINPUT_GAMEPAD_BACK, the "two windows" button.</summary>
    public const ushort View = 0x0020;
    /// <summary>Left stick click.</summary>
    public const ushort LS = 0x0040;
    /// <summary>Right stick click.</summary>
    public const ushort RS = 0x0080;
    public const ushort LB = 0x0100;
    public const ushort RB = 0x0200;
    public const ushort A = 0x1000;
    public const ushort B = 0x2000;
    public const ushort X = 0x4000;
    public const ushort Y = 0x8000;

    /// <summary>Every bit that is a real button. The reserved bits 0x0400 and 0x0800 are masked away.</summary>
    public const ushort ValidMask = 0xF3FF;

    /// <summary>A combo needs at least this many buttons - one button is far too easy to hit in a game.</summary>
    public const int MinComboButtons = 2;

    // Display order: shoulders and sticks, the two middle buttons, the D-pad, the face buttons.
    private static readonly (ushort Bit, string Name)[] Names =
    {
        (LB, "LB"), (RB, "RB"), (LS, "LS"), (RS, "RS"),
        (View, "View"), (Menu, "Menu"),
        (Up, "D-Pad ↑"), (Down, "D-Pad ↓"), (Left, "D-Pad ←"), (Right, "D-Pad →"),
        (A, "A"), (B, "B"), (X, "X"), (Y, "Y"),
    };

    /// <summary>"View + Menu"; "—" when the mask holds no known button.</summary>
    public static string Describe(ushort mask)
    {
        var sb = new StringBuilder();
        foreach (var (bit, name) in Names)
        {
            if ((mask & bit) == 0) continue;
            if (sb.Length > 0) sb.Append(" + ");
            sb.Append(name);
        }
        return sb.Length == 0 ? "—" : sb.ToString();
    }

    public static int Count(ushort mask) => BitOperations.PopCount(mask);

    /// <summary>At least <see cref="MinComboButtons"/> buttons and nothing that is not a button.</summary>
    public static bool IsValidCombo(ushort mask)
        => (mask & ~ValidMask) == 0 && Count(mask) >= MinComboButtons;

    /// <summary>
    /// The stored bindings that can actually be used: valid combos only, and a mask that two actions share
    /// goes to the ordinally first action, so one press never maps to two actions.
    /// </summary>
    public static Dictionary<string, ushort> Sanitize(IReadOnlyDictionary<string, ushort>? raw)
    {
        var clean = new Dictionary<string, ushort>();
        if (raw is null) return clean;

        var used = new HashSet<ushort>();
        var actions = new List<string>(raw.Keys);
        actions.Sort(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            ushort mask = raw[action];
            if (!IsValidCombo(mask) || !used.Add(mask)) continue;
            clean[action] = mask;
        }
        return clean;
    }
}
