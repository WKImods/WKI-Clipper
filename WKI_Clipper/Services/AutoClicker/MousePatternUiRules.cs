using System;
using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>
/// The words of the "Mausbewegung" sub-tab and the patterns' share of the AutoClicker status row (mouse-patterns design 4.5-4.8), made
/// from the settings and a <see cref="ClickerState"/> at display time so a language switch needs no new state. Pure, so the wording is tested.
/// </summary>
public static class MousePatternUiRules
{
    /// <summary>"Mausbewegung bereit (Aufnahme: Strg+Alt+R)": the second switch of the status row.</summary>
    public static string ArmedLabel(ClickerSettings c)
    {
        string hotkey = ClickerHotkey.Describe(c.PatternRecordHotkey);
        return L.T($"Mausbewegung bereit (Aufnahme: {hotkey})", $"Mouse patterns ready (recording: {hotkey})");
    }

    /// <summary>
    /// The state the clicker's pill and Start button show. <see cref="ClickerState.Phase"/> is the helper's, which also runs for the mouse
    /// patterns alone; with "Clicker bereit" off the helper ignores the clicker hotkey and Start, so the clicker side reads "Aus". An error
    /// concerns both switches and stays visible (with "Erneut versuchen"); a run that is still stopping can still be stopped.
    /// </summary>
    public static ClickerState ClickerSide(ClickerState state, ClickerSettings c)
        => c.Armed || state.Phase is ClickerPhase.Error or ClickerPhase.Running ? state : ClickerState.Off;

    /// <summary>
    /// The line on top of the sub-tab: "Mausbewegung ist aus - …", "Startet …", the error, the lock, "Aufnahme läuft · Strg+Alt+R beendet",
    /// "Spielt: Kreis", "Gestoppt: game.exe läuft als Administrator …" or "Bereit · 2 Muster aktiv" - the first that applies.
    /// </summary>
    public static string StatusLine(ClickerState state, ClickerSettings c)
    {
        if (!c.MousePatternsArmed)
            return L.T("Mausbewegung ist aus - oben 'Mausbewegung bereit' einschalten", "Mouse patterns are off - switch on 'Mouse patterns ready' above");

        switch (state.Phase)
        {
            case ClickerPhase.Error:
                return ClickerStateText.Fault(state.Fault);
            case ClickerPhase.Off or ClickerPhase.Starting:
                return L.T("Startet …", "Starting …");   // switched on a moment ago: the helper is on its way
        }

        if (state.Locked) return ClickerStateText.Pill(state);   // "Gesperrt: Arma auf der Blacklist" (the lock holds the patterns too)
        if (state.Recording) return RecordingText(c);

        var ids = PlayingIds(state.Playing);
        if (ids.Count > 0)
        {
            var names = NamesOf(ids, c);
            return names.Count == 0 ? L.T("Ein Muster spielt", "A pattern is playing") : L.T("Spielt: ", "Playing: ") + string.Join(", ", names);
        }

        if (!string.IsNullOrWhiteSpace(state.PatternsBlockedBy)) return BlockedText(state.PatternsBlockedBy!);

        var patterns = c.MousePatterns ?? new List<MousePattern>();
        int total = patterns.Count(p => p is not null);
        int active = patterns.Count(p => p is { Active: true });
        if (total == 0) return L.T("Bereit · noch keine Muster", "Ready · no patterns yet");
        if (active == 0) return L.T("Bereit · kein Muster aktiv", "Ready · no pattern active");
        return L.T($"Bereit · {active} Muster aktiv", active == 1 ? "Ready · 1 pattern active" : $"Ready · {active} patterns active");
    }

    /// <summary>"Aufnahme läuft · Strg+Alt+R beendet" (the same words as the on-screen label).</summary>
    public static string RecordingText(ClickerSettings c)
    {
        string hotkey = ClickerHotkey.Describe(c.PatternRecordHotkey);
        return L.T($"Aufnahme läuft · {hotkey} beendet", $"Recording · {hotkey} stops it");
    }

    /// <summary>An elevated window in front refused the moves (Windows rule); "?" when its program could not be read.</summary>
    public static string BlockedText(string exe) => exe == "?"
        ? L.T("Gestoppt: ein Administrator-Fenster nimmt keine Bewegung an", "Stopped: an administrator window does not take the movement")
        : L.T($"Gestoppt: {exe} läuft als Administrator und nimmt keine Bewegung an", $"Stopped: {exe} runs as administrator and does not take the movement");

    /// <summary>The non-info lines of <see cref="MousePatternConflicts.Evaluate"/>: what the status row counts.</summary>
    public static int WarningCount(IEnumerable<PatternWarning> warnings) => warnings.Count(w => !w.Info);

    /// <summary>"3 Muster-Hinweise - siehe Mausbewegung" for the status row; null when there is nothing to say.</summary>
    public static string? WarningSummary(int count) => count switch
    {
        <= 0 => null,
        1 => L.T("1 Muster-Hinweis - siehe Mausbewegung", "1 pattern warning - see Mouse patterns"),
        _ => L.T($"{count} Muster-Hinweise - siehe Mausbewegung", $"{count} pattern warnings - see Mouse patterns")
    };

    /// <summary>"Noch keine Muster. Strg+Alt+R drücken, Bewegung machen, nochmal drücken."</summary>
    public static string EmptyText(ClickerSettings c)
    {
        string hotkey = ClickerHotkey.Describe(c.PatternRecordHotkey);
        return L.T($"Noch keine Muster. {hotkey} drücken, Bewegung machen, nochmal drücken.",
                   $"No patterns yet. Press {hotkey}, make the movement, press it again.");
    }

    /// <summary>"Nächste Aufnahme ersetzt 'Kreis' - Strg+Alt+R im Spiel drücken" ("Neu aufnehmen" marked that pattern).</summary>
    public static string TargetLine(string name, ClickerSettings c)
    {
        string hotkey = ClickerHotkey.Describe(c.PatternRecordHotkey);
        return L.T($"Nächste Aufnahme ersetzt '{name}' - {hotkey} im Spiel drücken", $"The next recording replaces '{name}' - press {hotkey} in the game");
    }

    /// <summary>The length of the recorded motion: "2,4 s".</summary>
    public static string Duration(double ms) => ClickerStatusRules.Length(double.IsFinite(ms) ? ms / 1000.0 : 0);

    /// <summary>"Nur in game.exe": the program binding of a pattern.</summary>
    public static string OnlyIn(string program) => L.T($"Nur in {program}", $"Only in {program}");

    public static string ReplayName(PatternReplay replay) => replay switch
    {
        PatternReplay.Pointer => L.T("Zeiger", "Pointer"),
        PatternReplay.Raw => L.T("Spiel (roh)", "Game (raw)"),
        _ => L.T("Automatisch", "Automatic")
    };

    public static string ModeName(ActivationMode mode)
        => mode == ActivationMode.Toggle ? L.T("Umschalten", "Toggle") : L.T("Halten", "Hold");

    private static List<string> PlayingIds(string? playing)
        => string.IsNullOrWhiteSpace(playing)
            ? new List<string>()
            : playing.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>The names the settings know for the ids, in the helper's order; ids of patterns deleted meanwhile are left out.</summary>
    private static List<string> NamesOf(IEnumerable<string> ids, ClickerSettings c)
    {
        var patterns = c.MousePatterns ?? new List<MousePattern>();
        return ids
            .Select(id => patterns.FirstOrDefault(p => p is not null && string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToList();
    }
}
