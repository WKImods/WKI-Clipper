using System;
using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>
/// The hotkey side of the shortcut bar. An entry's optional hotkey travels through the same machinery as the clipper's own
/// (RegisterHotKey, collision check, the one dispatch switch) under the action name <c>App:&lt;entryId&gt;</c> - the same pattern
/// as the stream-deck tiles' <c>StreamButton:&lt;slot&gt;</c>.
/// </summary>
public static class ShortcutHotkeys
{
    public const string Prefix = "App:";

    public static string ActionFor(string id) => Prefix + id;

    /// <summary>True for <c>App:&lt;id&gt;</c> (the id may itself contain colons: "launcher:steam"). <paramref name="id"/> is empty otherwise.</summary>
    public static bool TryParse(string action, out string id)
    {
        id = "";
        if (action is null || !action.StartsWith(Prefix, StringComparison.Ordinal) || action.Length == Prefix.Length) return false;
        id = action[Prefix.Length..];
        return true;
    }

    /// <summary>
    /// What <c>HotkeyService.RegisterAll</c> registers for the shortcut entries: only entries that have a key. A hidden or greyed-out
    /// entry keeps its hotkey - the user bound it; hiding it from the bar does not unbind it.
    /// </summary>
    internal static IEnumerable<(string Action, HotkeyBinding Binding)> Bindings(AppSettings settings)
    {
        var items = settings.Shortcuts?.Items;
        if (items == null) yield break;
        foreach (var e in items)
            if (e.Hotkey is { Key: not 0 } hk) yield return (ActionFor(e.Id), hk);
    }

    /// <summary>
    /// A string that changes exactly when the set of (entry, hotkey) pairs changes - names, order and icons do not matter.
    /// Lets the host re-register the global hotkeys only when one of them really changed (a rescan changes many other things).
    /// </summary>
    public static string Signature(IEnumerable<ShortcutEntry> items)
        => string.Join(";", items
            .Where(e => e.Hotkey is { Key: not 0 })
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .Select(e => $"{e.Id}={(uint)e.Hotkey!.Modifiers}:{e.Hotkey.Key}"));
}
