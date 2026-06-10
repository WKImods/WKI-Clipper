using System.Text;
using WKI_Clipper.Models;

namespace WKI_Clipper.ViewModels;

/// <summary>
/// Static utility for rendering hotkey bindings as human-friendly strings.
/// (Was a full ViewModel earlier — collapsed to a util after the MVVM-based
/// hotkey rebinding UI proved fragile and was replaced with procedural code-behind
/// in HotkeysView.xaml.cs.)
/// </summary>
public static class HotkeyEntryViewModel
{
    public static string Describe(HotkeyBinding b)
    {
        if (b.Key == 0) return "—";

        var sb = new StringBuilder();
        if ((b.Modifiers & HotkeyModifier.Control) != 0) sb.Append("Strg + ");
        if ((b.Modifiers & HotkeyModifier.Alt) != 0)     sb.Append("Alt + ");
        if ((b.Modifiers & HotkeyModifier.Shift) != 0)   sb.Append("Shift + ");
        if ((b.Modifiers & HotkeyModifier.Win) != 0)     sb.Append("Win + ");
        sb.Append(KeyName(b.Key));
        return sb.ToString();
    }

    private static string KeyName(uint vk) => vk switch
    {
        >= 0x70 and <= 0x7B => "F" + (vk - 0x6F),     // F1..F12
        >= 0x30 and <= 0x39 => ((char)vk).ToString(), // 0..9
        >= 0x41 and <= 0x5A => ((char)vk).ToString(), // A..Z
        0x20 => "Leertaste",
        0x0D => "Enter",
        0x08 => "Backspace",
        0x09 => "Tab",
        0x21 => "Bild auf",
        0x22 => "Bild ab",
        0x23 => "Ende",
        0x24 => "Pos1",
        0x25 => "←",
        0x26 => "↑",
        0x27 => "→",
        0x28 => "↓",
        0x2D => "Einfg",
        0x2E => "Entf",
        _ => $"VK_0x{vk:X}"
    };
}
