using System;
using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;
using WKI_Clipper.ViewModels;

namespace WKI_Clipper.Services;

/// <summary>
/// Which keys are refused while a clip or GIF is being written, named the way the user set them: the keyboard
/// bindings of "Save replay" and "Save GIF" (formatted like the hotkeys page does) and their controller combos.
/// The defaults are F9 and F8 - but both can be rebound, and the controller combo is refused as well, so a fixed
/// "F8/F9" would name keys that may not be the blocked ones. A binding that is not set is left out.
/// </summary>
public static class SaveKeyHints
{
    /// <summary>The refused inputs in display order: replay key, replay combo, GIF key, GIF combo; no duplicates.</summary>
    public static IReadOnlyList<string> Bindings(AppSettings settings)
    {
        var combos = settings.Gamepad.Enabled
            ? GamepadButtons.Sanitize(settings.Gamepad.Bindings)
            : new Dictionary<string, ushort>();

        var list = new List<string>();
        foreach (var action in new[] { HotkeyActions.SaveReplay, HotkeyActions.SaveGif })
        {
            if (settings.Hotkeys.TryGetValue(action, out var binding) && binding is { Key: not 0 })
                list.Add(HotkeyEntryViewModel.Describe(binding));
            if (combos.TryGetValue(action, out var mask))
                list.Add(GamepadButtons.Describe(mask));
        }
        return list.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>"F9 / View + Menu / F8 sind bis zum Ende gesperrt." - the body of the long-clip progress toast.</summary>
    public static string BlockedUntilFinished(AppSettings settings)
    {
        var keys = Bindings(settings);
        var subject = string.Join(" / ", keys);
        return keys.Count switch
        {
            0 => L.T("Clip-/GIF-Speichern ist bis zum Ende gesperrt.", "Clip/GIF saving is blocked until it finishes."),
            1 => L.T($"{subject} ist bis zum Ende gesperrt.", $"{subject} is blocked until it finishes."),
            _ => L.T($"{subject} sind bis zum Ende gesperrt.", $"{subject} are blocked until it finishes.")
        };
    }

    /// <summary>The same for a help text that explains how long a save takes: "... sind solange gesperrt."</summary>
    public static string BlockedMeanwhile(AppSettings settings)
    {
        var keys = Bindings(settings);
        var subject = string.Join(" / ", keys);
        return keys.Count switch
        {
            0 => L.T("Clip-/GIF-Speichern ist solange gesperrt.", "Clip/GIF saving is blocked meanwhile."),
            1 => L.T($"{subject} ist solange gesperrt.", $"{subject} is blocked meanwhile."),
            _ => L.T($"{subject} sind solange gesperrt.", $"{subject} are blocked meanwhile.")
        };
    }
}
