using System;
using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>A line under one pattern's card. <see cref="Info"/> lines are neutral notes; nothing here ever blocks a start.</summary>
public sealed record PatternWarning(string PatternId, string Message, bool Info);

/// <summary>
/// Pure checks of mouse-patterns design 4.8, per pattern. Hotkey interactions (another pattern, the record hotkey, a clipper binding,
/// the clicker) only count for active patterns - inactive ones are not tracked by the helper. The risky-hotkey hints are the clicker's
/// texts; missing samples are reported for every pattern. Overlap uses <see cref="ClickerHotkey.Overlaps"/> with the clicker's strictness,
/// the rule the helper's hotkey tracker applies to all bindings.
/// </summary>
public static class MousePatternConflicts
{
    /// <param name="hasData">Whether the samples of a pattern id can be loaded.</param>
    public static IReadOnlyList<PatternWarning> Evaluate(AppSettings s, Func<string, bool> hasData)
    {
        var result = new List<PatternWarning>();
        var c = s.AutoClicker?.Clicker;
        if (c?.MousePatterns is not { } patterns) return result;

        bool strict = c.StrictModifiers;
        bool clickerMovesPointer = c.ClickPointsEnabled && c.ClickPoints is { Count: > 0 };

        foreach (var p in patterns)
        {
            if (p is null) continue;
            void Warn(string message) => result.Add(new(p.Id, message, Info: false));
            var hotkey = p.Hotkey is { IsEmpty: false } h ? h : null;
            bool tracked = hotkey is not null && p.Active;
            bool sharesClicker = tracked && ClickerHotkey.Overlaps(hotkey, c.Hotkey, strict);

            if (tracked)
            {
                var partners = patterns
                    .Where(o => o is not null && !ReferenceEquals(o, p) && o.Active && MayRunTogether(p, o) && ClickerHotkey.Overlaps(hotkey, o.Hotkey, strict))
                    .Select(o => $"'{o.Name}'")
                    .ToList();
                if (partners.Count > 0)
                {
                    var names = string.Join(", ", partners);
                    Warn(L.T($"Startet zusammen mit {names}: die Hotkeys überschneiden sich.", $"Starts together with {names}: the hotkeys overlap."));
                }
                if (ClickerHotkey.Overlaps(hotkey, c.PatternRecordHotkey, strict))
                    Warn(L.T("Der Hotkey überschneidet sich mit dem Aufnahme-Hotkey: ein Druck löst das Muster und die Aufnahme aus.",
                        "The hotkey overlaps the record hotkey: one press triggers the pattern and the recording."));
                if (ClickerConflicts.ClipperOwner(s, hotkey, strict) is { } owner)
                    Warn(ClickerConflicts.ClipperClashText(owner, master: false));
            }

            foreach (var (risk, message) in ClickerConflicts.HotkeyRisks(hotkey, "Der Hotkey", "The hotkey"))
                Warn(risk == HotkeyRisk.BareMouseButton && !p.OnlyInProgram ? message + " " + Everywhere(p.Program) : message);

            if (sharesClicker && clickerMovesPointer)
                Warn(L.T("Der Clicker startet mit demselben Hotkey und fährt Klickpunkte an: er setzt den Zeiger auf seine Punkte und arbeitet gegen das Muster.",
                    "The clicker starts on the same hotkey and moves to its click points: it puts the pointer on its points and works against the pattern."));

            if (!hasData(p.Id))
                Warn(L.T("Aufnahme fehlt oder ist unlesbar - neu aufnehmen. So kann das Muster nicht abgespielt werden.",
                    "Recording missing or unreadable - record it again. The pattern cannot play like this."));

            if (sharesClicker && !clickerMovesPointer)
                result.Add(new(p.Id, L.T("Startet zusammen mit dem Clicker (gleicher Hotkey).", "Starts together with the clicker (same hotkey)."), Info: true));
        }
        return result;
    }

    /// <summary>Two patterns bound to different programs never play at the same time, whatever their hotkeys.</summary>
    private static bool MayRunTogether(MousePattern a, MousePattern b)
        => !(a.OnlyInProgram && b.OnlyInProgram && a.Program is not null && b.Program is not null
             && !string.Equals(a.Program, b.Program, StringComparison.OrdinalIgnoreCase));

    private static string Everywhere(string? program) => program is null
        ? L.T("Wirkt überall.", "Works everywhere.")
        : L.T($"Wirkt überall - 'Nur in {program}' anhaken?", $"Works everywhere - tick 'Only in {program}'?");
}
