using System.Collections.Generic;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>
/// Decides whether the physically pressed virtual keys satisfy a <see cref="ClickerHotkey"/>. Pure: the host's hook thread keeps the
/// down-set and asks after every change.
/// </summary>
public static class HotkeyMatcher
{
    /// <summary>
    /// True when every key of the hotkey is down and every demanded modifier is down: a generic modifier is satisfied by either side
    /// (VK_LCONTROL or VK_RCONTROL), a sided one only by that side. With <paramref name="strict"/> a modifier that the hotkey does not
    /// demand must not be down either; for a family demanded as one side only, the other side counts as extra.
    /// Extra non-modifier keys never block a match. A modifier-only hotkey matches on its modifiers alone (AltGr arrives as
    /// LCtrl+RAlt, so it satisfies LCtrl+RAlt as well as Ctrl+Alt).
    /// </summary>
    public static bool IsMatch(ClickerHotkey? hotkey, IReadOnlySet<int> downVks, bool strict)
    {
        if (hotkey is null || hotkey.IsEmpty || hotkey.Keys is not { } keys) return false;

        foreach (var f in ModifierFamily.Families)
        {
            bool left = downVks.Contains(f.VkLeft);
            bool right = downVks.Contains(f.VkRight);
            // The generic VK (VK_CONTROL ...) never comes out of the low-level hooks, but a GetAsyncKeyState-fed set may carry it.
            bool generic = f.VkGeneric != 0 && downVks.Contains(f.VkGeneric);
            bool anyDown = left || right || generic;

            bool wantGeneric = (hotkey.Modifiers & f.Generic) != 0;
            bool wantLeft = (hotkey.Modifiers & f.Left) != 0;
            bool wantRight = (hotkey.Modifiers & f.Right) != 0;

            if (wantLeft && !left) return false;
            if (wantRight && !right) return false;
            if (wantGeneric && !anyDown) return false;

            if (!strict) continue;
            if (!wantGeneric && !wantLeft && !wantRight)
            {
                if (anyDown) return false;
            }
            else if (!wantGeneric)
            {
                if (wantLeft && !wantRight && right) return false;
                if (wantRight && !wantLeft && left) return false;
            }
        }

        foreach (var vk in keys)
            if (!downVks.Contains(vk)) return false;
        return true;
    }
}
