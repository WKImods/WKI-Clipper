using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>
/// Virtual-key codes the clicker's hotkey logic and display need. Mouse buttons are virtual keys too (VK_LBUTTON ...); the mouse wheel
/// gets pseudo codes above the VK range (a VK is a byte), so they can never collide with a real key.
/// </summary>
public static class ClickerVk
{
    public const int LButton = 0x01, RButton = 0x02, MButton = 0x04, XButton1 = 0x05, XButton2 = 0x06;
    public const int Shift = 0x10, Control = 0x11, Menu = 0x12;   // generic: either side
    public const int LShift = 0xA0, RShift = 0xA1, LControl = 0xA2, RControl = 0xA3, LMenu = 0xA4, RMenu = 0xA5;
    public const int LWin = 0x5B, RWin = 0x5C;
    public const int Escape = 0x1B;

    /// <summary>Wheel pseudo keys (WM_MOUSEWHEEL forward/backward, WM_MOUSEHWHEEL left/right). The host holds them while notches keep coming.</summary>
    public const int WheelUp = 0x101, WheelDown = 0x102, WheelLeft = 0x103, WheelRight = 0x104;

    /// <summary>A mouse button (not the wheel).</summary>
    public static bool IsMouse(int vk) => vk is LButton or RButton or MButton or XButton1 or XButton2;

    public static bool IsWheel(int vk) => vk is >= WheelUp and <= WheelRight;

    /// <summary>Anything that comes from the mouse: a button or a wheel direction.</summary>
    public static bool IsMouseInput(int vk) => IsMouse(vk) || IsWheel(vk);

    /// <summary>A code a hotkey may hold as one of its keys: a VK 1..255 or a wheel pseudo key. Modifier keys are excluded separately.</summary>
    public static bool IsKeyCode(int vk) => vk is >= 1 and <= 255 || IsWheel(vk);

    /// <summary>Any modifier key, generic or sided. These are never "main keys" of a hotkey.</summary>
    public static bool IsModifier(int vk) => vk is Shift or Control or Menu or >= LShift and <= RMenu or LWin or RWin;
}

/// <summary>
/// The modifiers of a clicker hotkey. The generic flag (<see cref="Ctrl"/>) means "either side"; the sided flags demand that very key.
/// Two sided flags of one family (LCtrl|RCtrl) demand both.
/// </summary>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClickerModifiers
{
    None = 0,
    Ctrl = 1, LCtrl = 2, RCtrl = 4,
    Alt = 8, LAlt = 16, RAlt = 32,
    Shift = 64, LShift = 128, RShift = 256,
    Win = 512, LWin = 1024, RWin = 2048
}

/// <summary>One modifier family (Ctrl, Alt, Shift, Win) with its flags, virtual keys and display names.</summary>
internal readonly record struct ModifierFamily(
    ClickerModifiers Generic, ClickerModifiers Left, ClickerModifiers Right,
    int VkGeneric, int VkLeft, int VkRight, string De, string En)
{
    public ClickerModifiers All => Generic | Left | Right;

    public static readonly ModifierFamily[] Families =
    {
        new(ClickerModifiers.Ctrl,  ClickerModifiers.LCtrl,  ClickerModifiers.RCtrl,  ClickerVk.Control, ClickerVk.LControl, ClickerVk.RControl, "Strg", "Ctrl"),
        new(ClickerModifiers.Alt,   ClickerModifiers.LAlt,   ClickerModifiers.RAlt,   ClickerVk.Menu,    ClickerVk.LMenu,    ClickerVk.RMenu,    "Alt", "Alt"),
        new(ClickerModifiers.Shift, ClickerModifiers.LShift, ClickerModifiers.RShift, ClickerVk.Shift,   ClickerVk.LShift,   ClickerVk.RShift,   "Umschalt", "Shift"),
        new(ClickerModifiers.Win,   ClickerModifiers.LWin,   ClickerModifiers.RWin,   0,                 ClickerVk.LWin,     ClickerVk.RWin,     "Win", "Win"),
    };

    /// <summary>Every defined modifier bit; anything else in a hand-edited value is dropped.</summary>
    public static ClickerModifiers Mask { get; } = Families.Aggregate(ClickerModifiers.None, (m, f) => m | f.All);
}

/// <summary>
/// A hotkey of the native clicker: modifiers plus 0..5 main keys (virtual keys, mouse buttons and wheel pseudo keys included); without
/// a main key at least one modifier ("modifier-only hotkey"). Detected by the host from low-level hooks (see <see cref="HotkeyMatcher"/>);
/// no RegisterHotKey, so it never fights the clipper's own hotkeys for ownership.
/// </summary>
public sealed class ClickerHotkey
{
    public const int MaxKeys = 5;

    public ClickerModifiers Modifiers { get; set; }
    public List<int> Keys { get; set; } = new();

    /// <summary>Ctrl+Alt+Y. A new instance every call: hotkeys are mutable.</summary>
    public static ClickerHotkey Default() => new() { Modifiers = ClickerModifiers.Ctrl | ClickerModifiers.Alt, Keys = { 0x59 } };

    public ClickerHotkey Clone() => new() { Modifiers = Modifiers, Keys = new List<int>(Keys ?? new()) };

    /// <summary>
    /// 0..5 distinct main keys (VKs 1..255 or wheel pseudo keys, no modifier key among them) and at least one modifier or one key.
    /// Every input is allowed, risky ones included (a bare left mouse button, Esc, the wheel): <see cref="ClickerConflicts"/> warns about
    /// those. Hand-edited values are repaired by <see cref="ClickerSettingsRules.Normalize"/>.
    /// </summary>
    public bool IsValid()
    {
        if (Keys is null || Keys.Count > MaxKeys) return false;
        if (Keys.Count == 0 && Modifiers == ClickerModifiers.None) return false;
        if (Keys.Distinct().Count() != Keys.Count) return false;
        if (Keys.Any(k => !ClickerVk.IsKeyCode(k) || ClickerVk.IsModifier(k))) return false;
        return (Modifiers & ~ModifierFamily.Mask) == 0;
    }

    /// <summary>Neither a modifier nor a key: "no hotkey".</summary>
    [JsonIgnore]
    public bool IsEmpty => Modifiers == ClickerModifiers.None && (Keys is null || Keys.Count == 0);

    /// <summary>Only modifiers, no main key (Strg+Umschalt, AltGr = LStrg+RAlt ...).</summary>
    [JsonIgnore]
    public bool IsModifierOnly => Modifiers != ClickerModifiers.None && (Keys is null || Keys.Count == 0);

    /// <summary>One of the keys is a mouse button or a wheel direction.</summary>
    [JsonIgnore]
    public bool HasMouseInput => Keys is not null && Keys.Any(ClickerVk.IsMouseInput);

    /// <summary>
    /// Same combo: same modifier flags and the same set of keys (order does not matter - a hotkey is "all of these are down").
    /// </summary>
    public static bool SameCombo(ClickerHotkey? a, ClickerHotkey? b)
    {
        if (a is null || b is null) return false;
        if (a.Modifiers != b.Modifiers) return false;
        var ka = a.Keys ?? new(); var kb = b.Keys ?? new();
        return ka.Count == kb.Count && new HashSet<int>(ka).SetEquals(kb);
    }

    /// <summary>
    /// One physical press can satisfy both hotkeys: the minimal input of one (its keys plus one VK per demanded modifier) also
    /// matches the other under <see cref="HotkeyMatcher.IsMatch"/>. Catches generic vs sided (Ctrl+Y / LCtrl+Y) and, when not strict,
    /// subset modifiers or keys (Ctrl+Y / Ctrl+Alt+Y).
    /// </summary>
    public static bool Overlaps(ClickerHotkey? a, ClickerHotkey? b, bool strict)
    {
        if (a is null || b is null || a.IsEmpty || b.IsEmpty) return false;
        return HotkeyMatcher.IsMatch(a, MinimalInput(b), strict) || HotkeyMatcher.IsMatch(b, MinimalInput(a), strict);
    }

    private static HashSet<int> MinimalInput(ClickerHotkey h)
    {
        var down = new HashSet<int>(h.Keys ?? new());
        foreach (var f in ModifierFamily.Families)
        {
            if ((h.Modifiers & (f.Generic | f.Left)) != 0) down.Add(f.VkLeft);   // generic: one side is enough
            if ((h.Modifiers & f.Right) != 0) down.Add(f.VkRight);
        }
        return down;
    }

    /// <summary>"Strg+Alt+Y", "LStrg+Maus 4", "Strg+Umschalt", "(keine)" for an empty/null hotkey. Follows the app language.</summary>
    public static string Describe(ClickerHotkey? hotkey)
        => hotkey is null || hotkey.IsEmpty ? L.T("(keine)", "(none)") : hotkey.ToDisplayString();

    /// <summary>Modifiers first (Strg, Alt, Umschalt, Win; sided ones as LStrg/RStrg ...), then the keys in the order they were pressed.</summary>
    public string ToDisplayString()
    {
        var parts = new List<string>();
        foreach (var f in ModifierFamily.Families)
        {
            var name = L.T(f.De, f.En);
            if (Modifiers.HasFlag(f.Generic)) parts.Add(name);
            if (Modifiers.HasFlag(f.Left)) parts.Add("L" + name);
            if (Modifiers.HasFlag(f.Right)) parts.Add("R" + name);
        }
        if (Keys is not null)
            foreach (var vk in Keys) parts.Add(KeyName(vk));
        return string.Join("+", parts);
    }

    /// <summary>Display name of one virtual key (or wheel pseudo key) for a US-layout-independent label; unknown codes show as "Taste 0xNN".</summary>
    public static string KeyName(int vk)
    {
        switch (vk)
        {
            case ClickerVk.LButton: return L.T("Maus links", "Left mouse");
            case ClickerVk.RButton: return L.T("Maus rechts", "Right mouse");
            case ClickerVk.MButton: return L.T("Maus Mitte", "Middle mouse");
            case ClickerVk.XButton1: return L.T("Maus 4", "Mouse 4");
            case ClickerVk.XButton2: return L.T("Maus 5", "Mouse 5");
            case ClickerVk.WheelUp: return L.T("Mausrad hoch", "Wheel up");
            case ClickerVk.WheelDown: return L.T("Mausrad runter", "Wheel down");
            case ClickerVk.WheelLeft: return L.T("Mausrad links", "Wheel left");
            case ClickerVk.WheelRight: return L.T("Mausrad rechts", "Wheel right");
            case 0x03: return L.T("Abbruch", "Break");
            case 0x08: return L.T("Rücktaste", "Backspace");
            case 0x09: return "Tab";
            case 0x0C: return L.T("Num 5 (ohne Num)", "Clear");
            case 0x0D: return L.T("Eingabe", "Enter");
            case 0x13: return L.T("Pause", "Pause");
            case 0x14: return L.T("Feststelltaste", "Caps Lock");
            case ClickerVk.Escape: return "Esc";
            case 0x20: return L.T("Leertaste", "Space");
            case 0x21: return L.T("Bild auf", "Page Up");
            case 0x22: return L.T("Bild ab", "Page Down");
            case 0x23: return L.T("Ende", "End");
            case 0x24: return L.T("Pos1", "Home");
            case 0x25: return L.T("Pfeil links", "Left");
            case 0x26: return L.T("Pfeil hoch", "Up");
            case 0x27: return L.T("Pfeil rechts", "Right");
            case 0x28: return L.T("Pfeil runter", "Down");
            case 0x2C: return L.T("Druck", "Print Screen");
            case 0x2D: return L.T("Einfg", "Insert");
            case 0x2E: return L.T("Entf", "Delete");
            case 0x5D: return L.T("Menütaste", "Menu key");
            case 0x5F: return L.T("Standby", "Sleep");
            case 0x6A: return "Num *";
            case 0x6B: return "Num +";
            case 0x6D: return "Num -";
            case 0x6E: return "Num ,";
            case 0x6F: return "Num /";
            case 0x90: return L.T("Num Lock", "Num Lock");
            case 0x91: return L.T("Rollen", "Scroll Lock");
            case 0xA6: return L.T("Browser zurück", "Browser back");
            case 0xA7: return L.T("Browser vor", "Browser forward");
            case 0xA8: return L.T("Browser neu laden", "Browser refresh");
            case 0xA9: return L.T("Browser stopp", "Browser stop");
            case 0xAA: return L.T("Browser suchen", "Browser search");
            case 0xAB: return L.T("Browser Favoriten", "Browser favorites");
            case 0xAC: return L.T("Browser Startseite", "Browser home");
            case 0xAD: return L.T("Stumm", "Mute");
            case 0xAE: return L.T("Leiser", "Volume down");
            case 0xAF: return L.T("Lauter", "Volume up");
            case 0xB0: return L.T("Nächster Titel", "Next track");
            case 0xB1: return L.T("Voriger Titel", "Previous track");
            case 0xB2: return L.T("Medien stopp", "Media stop");
            case 0xB3: return L.T("Wiedergabe/Pause", "Play/Pause");
            case 0xB4: return L.T("E-Mail", "Mail");
            case 0xB5: return L.T("Medien wählen", "Media select");
            case 0xB6: return "App 1";
            case 0xB7: return "App 2";
            case 0xBA: return ";";
            case 0xBB: return "=";
            case 0xBC: return ",";
            case 0xBD: return "-";
            case 0xBE: return ".";
            case 0xBF: return "/";
            case 0xC0: return "`";
            case 0xDB: return "[";
            case 0xDC: return "\\";
            case 0xDD: return "]";
            case 0xDE: return "'";
            case 0xE2: return "<";
        }
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x60 and <= 0x69) return "Num " + (vk - 0x60);
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        return L.T($"Taste 0x{vk:X2}", $"Key 0x{vk:X2}");
    }
}
