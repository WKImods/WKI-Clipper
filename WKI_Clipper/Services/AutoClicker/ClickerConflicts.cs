using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services.AutoClicker;

public enum ClickerConflictKind
{
    /// <summary>Keyboard output without a key chosen.</summary>
    NoOutputKey,
    /// <summary>The output key or mouse button is one of the start hotkey's keys.</summary>
    OutputKeyIsHotkey,
    /// <summary>The output key or mouse button is one of the master hotkey's keys.</summary>
    OutputKeyIsMasterHotkey,
    /// <summary>Whitelist mode with an empty list: nothing may be clicked.</summary>
    EmptyWhitelist,
    /// <summary>The start hotkey is also one of the clipper's own RegisterHotKey bindings.</summary>
    HotkeyIsClipperHotkey,
    /// <summary>The master hotkey is also one of the clipper's own RegisterHotKey bindings.</summary>
    MasterIsClipperHotkey,
    /// <summary>Start and master hotkey overlap: one physical press can satisfy both.</summary>
    HotkeyIsMasterHotkey,
    /// <summary>The start hotkey is the left or right mouse button alone: every such click triggers it.</summary>
    HotkeyIsBareMouseButton,
    /// <summary>The master hotkey is the left or right mouse button alone.</summary>
    MasterIsBareMouseButton,
    /// <summary>The start hotkey has no main key: every shortcut with its modifiers triggers it.</summary>
    HotkeyIsModifierOnly,
    /// <summary>The master hotkey has no main key.</summary>
    MasterIsModifierOnly,
    /// <summary>The start hotkey contains Esc, which also closes menus and dialogs.</summary>
    HotkeyHasEscape,
    /// <summary>The master hotkey contains Esc.</summary>
    MasterHasEscape,
    /// <summary>The start hotkey contains a wheel direction: every scroll triggers it.</summary>
    HotkeyHasWheel,
    /// <summary>The master hotkey contains a wheel direction.</summary>
    MasterHasWheel
}

/// <summary>The kinds of allowed-but-risky hotkey that <see cref="ClickerConflicts.HotkeyRisks"/> reports.</summary>
internal enum HotkeyRisk { BareMouseButton, ModifierOnly, Escape, Wheel }

/// <summary>A problem with the current clicker setup. <see cref="BlocksStart"/> conflicts keep the clicker from starting; the rest are warnings.</summary>
public sealed record ClickerConflict(ClickerConflictKind Kind, bool BlocksStart, string Message);

/// <summary>
/// Pure checks for the situations design section 5 and the free-hotkeys design (section 3) name. Only a missing output key and an empty
/// whitelist block the start; every hotkey the user can record is allowed, and the risky ones get a warning. The UI shows
/// <see cref="ClickerConflict.Message"/> inline.
/// </summary>
public static class ClickerConflicts
{
    public static IReadOnlyList<ClickerConflict> Evaluate(AppSettings settings)
    {
        var c = settings.AutoClicker?.Clicker;
        var result = new List<ClickerConflict>();
        if (c is null) return result;

        if (c.InputType == ClickerInput.Keyboard && c.KeyboardVk <= 0)
            result.Add(new(ClickerConflictKind.NoOutputKey, true,
                L.T("Keine Taste zum Senden gewählt.", "No key to send chosen.")));

        if (OutputInHotkey(c, c.Hotkey))
            result.Add(new(ClickerConflictKind.OutputKeyIsHotkey, false, OutputMessage(c, master: false)));
        if (OutputInHotkey(c, c.MasterHotkey))
            result.Add(new(ClickerConflictKind.OutputKeyIsMasterHotkey, false, OutputMessage(c, master: true)));

        if (c.WhitelistEnabled && (c.Whitelist is null || c.Whitelist.Count == 0))
            result.Add(new(ClickerConflictKind.EmptyWhitelist, true,
                L.T("Whitelist ist aktiv, aber leer: es wird nichts geklickt. Füge ein Programm hinzu oder schalte die Whitelist aus.",
                    "The whitelist is on but empty, so nothing would be clicked. Add a program or turn the whitelist off.")));

        AddHotkeyRisks(result, c.Hotkey, master: false);
        AddHotkeyRisks(result, c.MasterHotkey, master: true);

        if (ClipperOwner(settings, c.Hotkey, c.StrictModifiers) is { } owner)
            result.Add(new(ClickerConflictKind.HotkeyIsClipperHotkey, false, ClipperClashText(owner, master: false)));
        if (ClipperOwner(settings, c.MasterHotkey, c.StrictModifiers) is { } masterOwner)
            result.Add(new(ClickerConflictKind.MasterIsClipperHotkey, false, ClipperClashText(masterOwner, master: true)));

        if (ClickerHotkey.Overlaps(c.Hotkey, c.MasterHotkey, c.StrictModifiers))
            result.Add(new(ClickerConflictKind.HotkeyIsMasterHotkey, false,
                L.T("Hotkey und Master-Hotkey überschneiden sich: ein Tastendruck löst beide aus.",
                    "The hotkey and the master hotkey overlap: one press triggers both.")));

        return result;
    }

    /// <summary>True when none of the conflicts blocks starting.</summary>
    public static bool CanStart(IEnumerable<ClickerConflict> conflicts) => !conflicts.Any(x => x.BlocksStart);

    /// <summary>The conflicts shown under one hotkey field: that hotkey's own (output key, clipper binding, risks) and the overlap of both.</summary>
    public static IEnumerable<ClickerConflict> ForHotkey(IEnumerable<ClickerConflict> conflicts, bool master)
        => conflicts.Where(x => x.Kind switch
        {
            ClickerConflictKind.HotkeyIsMasterHotkey => true,
            ClickerConflictKind.OutputKeyIsHotkey or ClickerConflictKind.HotkeyIsClipperHotkey or ClickerConflictKind.HotkeyIsBareMouseButton
                or ClickerConflictKind.HotkeyIsModifierOnly or ClickerConflictKind.HotkeyHasEscape or ClickerConflictKind.HotkeyHasWheel => !master,
            ClickerConflictKind.OutputKeyIsMasterHotkey or ClickerConflictKind.MasterIsClipperHotkey or ClickerConflictKind.MasterIsBareMouseButton
                or ClickerConflictKind.MasterIsModifierOnly or ClickerConflictKind.MasterHasEscape or ClickerConflictKind.MasterHasWheel => master,
            _ => false
        });

    /// <summary>
    /// The output - the keyboard key, or the mouse button with mouse output - is one of the hotkey's keys. The clicker does not trigger
    /// itself (the host ignores its own tagged input), but the user's presses and the clicker's mix for every other program.
    /// </summary>
    internal static bool OutputInHotkey(ClickerSettings c, ClickerHotkey? hotkey)
    {
        if (hotkey?.Keys is not { Count: > 0 } keys) return false;
        int vk = OutputVk(c);
        return vk > 0 && keys.Contains(vk);
    }

    /// <summary>The virtual key the clicker sends: the keyboard key (0 = none chosen), or the mouse button as VK_LBUTTON ... with mouse output.</summary>
    internal static int OutputVk(ClickerSettings c) => c.InputType == ClickerInput.Keyboard ? c.KeyboardVk : ButtonVk(c.MouseButton);

    private static int ButtonVk(ClickerMouseButton button) => button switch
    {
        ClickerMouseButton.Right => ClickerVk.RButton,
        ClickerMouseButton.Middle => ClickerVk.MButton,
        _ => ClickerVk.LButton
    };

    private static string OutputMessage(ClickerSettings c, bool master)
    {
        if (c.InputType == ClickerInput.Keyboard)
            return master
                ? L.T("Die zu sendende Taste gehört zum Master-Hotkey. Das geht, aber Spiele und Programme sehen deine Tastendrücke und die des Clickers vermischt.",
                    "The key to send is part of the master hotkey. This works, but games and programs see your presses and the clicker's mixed together.")
                : L.T("Die zu sendende Taste gehört zum Hotkey. Das geht, aber Spiele und Programme sehen deine Tastendrücke und die des Clickers vermischt.",
                    "The key to send is part of the hotkey. This works, but games and programs see your presses and the clicker's mixed together.");
        return master
            ? L.T("Die geklickte Maustaste gehört zum Master-Hotkey: deine echten Klicks schalten auch den Master-Schalter.",
                "The clicked mouse button is part of the master hotkey: your real clicks also switch the master switch.")
            : L.T("Die geklickte Maustaste gehört zum Hotkey. Halten-Modus: Dauerfeuer, solange du sie hältst. Umschalten: dein nächster echter Klick stoppt.",
                "The clicked mouse button is part of the hotkey. Hold mode: rapid fire while you hold it. Toggle: your next real click stops.");
    }

    /// <summary>Warnings for hotkeys that are allowed but fire more often than the user may expect.</summary>
    private static void AddHotkeyRisks(List<ClickerConflict> result, ClickerHotkey? hotkey, bool master)
    {
        var risks = master ? HotkeyRisks(hotkey, "Der Master-Hotkey", "The master hotkey") : HotkeyRisks(hotkey, "Der Hotkey", "The hotkey");
        foreach (var (risk, message) in risks)
        {
            var kind = risk switch
            {
                HotkeyRisk.BareMouseButton => master ? ClickerConflictKind.MasterIsBareMouseButton : ClickerConflictKind.HotkeyIsBareMouseButton,
                HotkeyRisk.ModifierOnly => master ? ClickerConflictKind.MasterIsModifierOnly : ClickerConflictKind.HotkeyIsModifierOnly,
                HotkeyRisk.Escape => master ? ClickerConflictKind.MasterHasEscape : ClickerConflictKind.HotkeyHasEscape,
                _ => master ? ClickerConflictKind.MasterHasWheel : ClickerConflictKind.HotkeyHasWheel
            };
            result.Add(new(kind, false, message));
        }
    }

    /// <summary>
    /// The risk texts of one hotkey (bare left/right mouse button, modifiers only, Esc, wheel), in that order; <paramref name="de"/> and
    /// <paramref name="en"/> name the hotkey as the subject of the sentence ("Der Hotkey"). Shared by the clicker and the mouse patterns.
    /// </summary>
    internal static List<(HotkeyRisk Risk, string Message)> HotkeyRisks(ClickerHotkey? hotkey, string de, string en)
    {
        var result = new List<(HotkeyRisk, string)>();
        if (hotkey is null || hotkey.IsEmpty) return result;
        var keys = hotkey.Keys ?? new List<int>();

        if (hotkey.Modifiers == ClickerModifiers.None && keys is [ClickerVk.LButton or ClickerVk.RButton])
        {
            bool left = keys[0] == ClickerVk.LButton;
            result.Add((HotkeyRisk.BareMouseButton, left
                ? L.T($"{de} ist die linke Maustaste allein: jeder Linksklick löst ihn aus, auch in Menüs und im Spiel (nicht über dem Clipper).",
                    $"{en} is the left mouse button alone: every left click triggers it, in menus and games too (not over the clipper).")
                : L.T($"{de} ist die rechte Maustaste allein: jeder Rechtsklick löst ihn aus, auch in Menüs und im Spiel (nicht über dem Clipper).",
                    $"{en} is the right mouse button alone: every right click triggers it, in menus and games too (not over the clipper).")));
        }
        if (hotkey.IsModifierOnly)
        {
            var combo = hotkey.ToDisplayString();
            result.Add((HotkeyRisk.ModifierOnly,
                L.T($"{de} besteht nur aus Modifikatortasten ({combo}): jede Tastenkombination damit, z. B. Strg+C, löst ihn mit aus.",
                    $"{en} is modifiers only ({combo}): every shortcut that uses them, e.g. Ctrl+C, triggers it too.")));
        }
        if (keys.Contains(ClickerVk.Escape))
            result.Add((HotkeyRisk.Escape,
                L.T($"{de} enthält Esc: Esc schließt auch Menüs und Dialoge, und jeder solche Druck löst ihn mit aus.",
                    $"{en} contains Esc: Esc also closes menus and dialogs, and every such press triggers it too.")));
        if (keys.Any(ClickerVk.IsWheel))
            result.Add((HotkeyRisk.Wheel,
                L.T($"{de} nutzt das Mausrad: jedes Scrollen löst ihn aus. Eine Scroll-Bewegung zählt als ein Druck, gehalten solange du weiterscrollst.",
                    $"{en} uses the mouse wheel: every scroll triggers it. One scroll gesture counts as one press, held while you keep scrolling.")));
        return result;
    }

    /// <summary>The warning for a hotkey that a clipper binding (<see cref="ClipperOwner"/>) fires together with.</summary>
    internal static string ClipperClashText(string owner, bool master) => master
        ? L.T($"Der Master-Hotkey ist schon belegt: {owner}. Beide Aktionen würden gleichzeitig ausgelöst.",
            $"The master hotkey is already used by {owner}. Both actions would fire together.")
        : L.T($"Der Hotkey ist schon belegt: {owner}. Beide Aktionen würden gleichzeitig ausgelöst.",
            $"The hotkey is already used by {owner}. Both actions would fire together.");

    /// <summary>
    /// The clipper binding (action hotkeys, stream-deck tiles, shortcut-bar entries - everything <c>HotkeyService.RegisterAll</c>
    /// registers) that fires together with this hotkey, as a label; null when none. RegisterHotKey fires on the WM_KEYDOWN of one
    /// key with exactly its modifiers, whatever other non-modifier keys are held, so any non-mouse key of the hotkey can clash - only
    /// mouse buttons and wheel directions cannot (a modifier-only hotkey has no key to clash with). Modifiers compare by family (Ctrl/Alt/Shift/Win, either side). Strict: the families must be equal.
    /// Not strict: the matcher ignores extra held modifiers, so a binding with the hotkey's families plus more fires both too.
    /// </summary>
    internal static string? ClipperOwner(AppSettings settings, ClickerHotkey? hotkey, bool strict)
    {
        if (hotkey?.Keys is not { Count: > 0 } keys) return null;

        uint families = 0;
        foreach (var f in ModifierFamily.Families)
        {
            if ((hotkey.Modifiers & f.All) == 0) continue;
            families |= f.Generic switch
            {
                ClickerModifiers.Ctrl => (uint)HotkeyModifier.Control,
                ClickerModifiers.Alt => (uint)HotkeyModifier.Alt,
                ClickerModifiers.Shift => (uint)HotkeyModifier.Shift,
                _ => (uint)HotkeyModifier.Win
            };
        }

        foreach (var key in keys)
        {
            if (ClickerVk.IsMouseInput(key)) continue;
            foreach (var (action, b) in Bindings(settings))
            {
                if (b.Key != (uint)key) continue;
                uint mods = (uint)b.Modifiers & ~(uint)HotkeyModifier.NoRepeat;
                if (strict ? mods == families : (mods & families) == families)
                    return Label(settings, action);
            }
        }
        return null;
    }

    private static IEnumerable<(string Action, HotkeyBinding Binding)> Bindings(AppSettings settings)
    {
        if (settings.Hotkeys is { } hotkeys)
            foreach (var (action, b) in hotkeys)
                if (b is { Key: not 0 }) yield return (action, b);

        if (settings.Streaming?.Buttons is { } buttons)
            foreach (var btn in buttons)
                if (btn?.Hotkey is { Key: not 0 } hk) yield return (HotkeyService.StreamButtonPrefix + btn.Slot, hk);

        foreach (var pair in ShortcutHotkeys.Bindings(settings))
            yield return pair;
    }

    private static string Label(AppSettings settings, string action)
    {
        var described = HotkeyService.DescribeAction(settings, action);
        return described == action ? L.T($"Clipper-Aktion \"{action}\"", $"clipper action \"{action}\"") : described;
    }
}
