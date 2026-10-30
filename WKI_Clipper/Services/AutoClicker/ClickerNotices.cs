using System;
using System.Globalization;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>A toast the clipper shows for a run or a pattern recording that ended. <paramref name="IsWarning"/> picks the warning look.</summary>
public sealed record ClickerNotice(string Title, string Body, bool IsWarning);

/// <summary>
/// The few moments the clipper speaks up about the clicker (design section 7: toasts only for a blacklist lock and for a click or time
/// limit end; every other stop reason shows in the state pill) and after every pattern recording. Pure, so the wording is tested.
/// </summary>
public static class ClickerNotices
{
    /// <summary>The toast for a finished run, or null when the end is not worth one.</summary>
    public static ClickerNotice? ForRunEnd(ClickerRunInfo run)
    {
        switch (run.Reason)
        {
            case StopReason.Locked:
                string game = string.IsNullOrWhiteSpace(run.LockedBy) ? L.T("einem Spiel", "a game") : run.LockedBy!;
                return new ClickerNotice("AutoClicker",
                    L.T($"AutoClicker gestoppt - {game} steht auf der Blacklist", $"AutoClicker stopped - {game} is on the blacklist"), IsWarning: true);

            case StopReason.ClickLimit:
            case StopReason.TimeLimit:
                return new ClickerNotice("AutoClicker",
                    L.T("AutoClicker gestoppt - ", "AutoClicker stopped - ") + StopReasonText.Describe(run.Reason, run.Detail), IsWarning: false);

            default:
                return null;
        }
    }

    /// <summary>
    /// The toast after a pattern recording (mouse-patterns design 4.2): "'Muster 3' aufgenommen (2,4 s)", with "- nach 60 s abgeschnitten"
    /// when the 60 s limit cut it; a warning when nothing was saved. Null for an outcome this build does not know.
    /// </summary>
    public static ClickerNotice? ForPatternRecorded(PatternRecordedInfo info)
    {
        string title = L.T("Mausbewegung", "Mouse patterns");
        switch (info.Outcome)
        {
            case nameof(PatternRecordingOutcome.Ok):
            case nameof(PatternRecordingOutcome.Cut):
                string name = string.IsNullOrWhiteSpace(info.Name) ? L.T("Muster", "Pattern") : info.Name!;
                double seconds = double.IsFinite(info.Seconds) && info.Seconds > 0 ? Math.Round(info.Seconds, 1) : 0;
                string length = ClickerNumber.Format(seconds) + " s";
                string body = L.T($"'{name}' aufgenommen ({length})", $"'{name}' recorded ({length})");
                if (info.Outcome == nameof(PatternRecordingOutcome.Cut))
                {
                    int limit = MousePatternRules.MaxDurationMs / 1000;
                    body += L.T($" - nach {limit} s abgeschnitten", $" - cut off after {limit} s");
                }
                return new ClickerNotice(title, body, IsWarning: false);

            case nameof(PatternRecordingOutcome.Empty):
                return new ClickerNotice(title, L.T("Keine Bewegung aufgenommen", "No movement recorded"), IsWarning: true);

            case nameof(PatternRecordingOutcome.Cancelled):
                return new ClickerNotice(title, L.T("Aufnahme verworfen", "Recording discarded"), IsWarning: true);

            case PatternRecordedInfo.Full:
                return new ClickerNotice(title,
                    L.T($"Aufnahme verworfen - es gibt schon {MousePatternRules.MaxPatterns} Muster",
                        $"Recording discarded - there are {MousePatternRules.MaxPatterns} patterns already"), IsWarning: true);

            case PatternRecordedInfo.Failed:
                return new ClickerNotice(title, L.T("Aufnahme konnte nicht gespeichert werden", "The recording could not be saved"), IsWarning: true);

            default:
                return null;
        }
    }
}

/// <summary>The words of the status row (design section 9), made from a <see cref="ClickerState"/> at display time so a language switch needs no new state.</summary>
public static class ClickerStateText
{
    /// <summary>"Ein Admin-Fenster (game.exe) blockiert die Klicks": an elevated window in front swallows the injected clicks.</summary>
    public static string ElevatedNotice(string exe)
        => L.T($"Ein Admin-Fenster ({exe}) blockiert die Klicks", $"An administrator window ({exe}) blocks the clicks");

    public static string Fault(ClickerFault fault) => fault switch
    {
        ClickerFault.StartFailed => L.T("Der AutoClicker-Helfer konnte nicht gestartet werden", "The AutoClicker helper could not be started"),
        ClickerFault.HandshakeFailed => L.T("Der AutoClicker-Helfer antwortet nicht", "The AutoClicker helper does not answer"),
        ClickerFault.Crashed => L.T("Der AutoClicker-Helfer wurde unerwartet beendet", "The AutoClicker helper ended unexpectedly"),
        ClickerFault.LockNotConfirmed => L.T("Der AutoClicker-Helfer bestätigt die Sperre nicht", "The AutoClicker helper does not confirm the lock"),
        _ => ""
    };

    /// <summary>"Aus", "Startet …", "Bereit", "Läuft · 25 cps · 1.234 Klicks", "Gesperrt: Arma auf der Blacklist", "Bereit · Master-Schalter aus", "Gestoppt: Hotkey" or the error.</summary>
    public static string Pill(ClickerState state)
    {
        switch (state.Phase)
        {
            case ClickerPhase.Off:
                return L.T("Aus", "Off");
            case ClickerPhase.Error:
                return Fault(state.Fault);
            case ClickerPhase.Starting:
                return L.T("Startet …", "Starting …");
        }

        if (state.Locked)
            return string.IsNullOrWhiteSpace(state.LockedBy)
                ? L.T("Gesperrt (Blacklist)", "Locked (blacklist)")
                : L.T($"Gesperrt: {state.LockedBy} auf der Blacklist", $"Locked: {state.LockedBy} is on the blacklist");

        if (state.Phase == ClickerPhase.Running)
        {
            if (!string.IsNullOrEmpty(state.BlockedByElevated)) return ElevatedNotice(state.BlockedByElevated!);
            var culture = CultureInfo.GetCultureInfo(L.English ? "en-US" : "de-DE");
            string clicks = state.Clicks.ToString("N0", culture);
            string cps = ClickerIndicatorRules.FormatCps(state.Cps);
            return L.T($"Läuft · {cps} · {clicks} Klicks", $"Running · {cps} · {clicks} clicks");
        }

        // Every hotkey press is ignored until the master switch is pressed: say so, or the clicker just looks broken.
        if (!state.MasterAllowed) return L.T("Bereit · Master-Schalter aus", "Ready · master switch off");

        return state.LastStop is { } stop
            ? L.T("Gestoppt: ", "Stopped: ") + StopReasonText.Describe(stop, state.LastStopDetail)
            : L.T("Bereit", "Ready");
    }
}
