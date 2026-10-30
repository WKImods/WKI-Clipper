using System.Windows;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.Services.AutoClicker;

namespace WKI_Clipper.Views.AutoClicker;

/// <summary>"Erweitert": duration mode, extended limit, randomization, duty cycle, double click, limits, strict modifiers, master hotkey, indicator.</summary>
public sealed class AdvancedClickerPanel : ClickerPanel
{
    public AdvancedClickerPanel()
    {
        var timing = Section(L.T("Takt", "Timing"));
        timing.Children.Add(Row(L.T("Angabe als", "Given as"), Choice(new[]
        {
            (RateInputMode.Rate, L.T("Klicks pro Zeiteinheit", "Clicks per time unit")),
            (RateInputMode.Duration, L.T("Dauer eines Takts", "Length of one cycle")),
        }, c => c.RateInputMode, (c, v) => c.RateInputMode = v, minWidth: 220)));

        var duration = Number(c => c.DurationMs, (c, v) => c.DurationMs = (long)System.Math.Round(v), width: 110,
            tooltip: L.T("1 ms bis 999 h 59 min 59,999 s", "1 ms up to 999 h 59 min 59.999 s"));
        var durationRow = Row(L.T("Taktdauer", "Cycle time"), duration, Label("ms"));
        timing.Children.Add(durationRow);
        VisibleWhen(durationRow, c => c.RateInputMode == RateInputMode.Duration);

        timing.Children.Add(Check(L.T("Erweitertes Limit (bis 1000 pro Sekunde)", "Extended limit (up to 1000 per second)"),
            c => c.ExtendedSpeedLimit, (c, v) => c.ExtendedSpeedLimit = v));

        var rand = Number(c => c.SpeedRandomizationPercent, (c, v) => c.SpeedRandomizationPercent = v, width: 60);
        timing.Children.Add(Row(L.T("Zufällige Abweichung", "Random variation"),
            Check(L.T("an", "on"), c => c.SpeedRandomizationEnabled, (c, v) => c.SpeedRandomizationEnabled = v), rand, Label("%  (0-200)")));
        EnabledWhen(rand, c => c.SpeedRandomizationEnabled);

        var duty = Number(c => c.DutyCyclePercent, (c, v) => c.DutyCyclePercent = v, width: 60,
            tooltip: L.T("Wie lange die Taste pro Takt gedrückt bleibt. Bei hohen Raten automatisch begrenzt.", "How long the button stays down per cycle. Capped automatically at high rates."));
        timing.Children.Add(Row(L.T("Haltedauer (Duty)", "Hold time (duty)"),
            Check(L.T("an", "on"), c => c.DutyCycleEnabled, (c, v) => c.DutyCycleEnabled = v), duty, Label("%  (0-100)")));
        EnabledWhen(duty, c => c.DutyCycleEnabled);

        timing.Children.Add(Row(L.T("Art", "Kind"), Choice(new[]
        {
            (DutyMode.Click, L.T("Klicken", "Click")),
            (DutyMode.Hold, L.T("Gedrückt halten bis Stopp", "Hold down until stopped")),
        }, c => c.DutyMode, (c, v) => c.DutyMode = v, minWidth: 220)));

        timing.Children.Add(Check(L.T("Doppelklick (höchstens 10 pro Sekunde)", "Double click (at most 10 per second)"),
            c => c.DoubleClick, (c, v) => c.DoubleClick = v));

        var limits = Section(L.T("Limits", "Limits"),
            L.T("Der Clicker stoppt von selbst, wenn ein Limit erreicht ist.", "The clicker stops by itself when a limit is reached."));
        var clickLimit = Number(c => c.ClickLimit, (c, v) => c.ClickLimit = (int)System.Math.Min(int.MaxValue, System.Math.Round(v)), width: 110);
        limits.Children.Add(Row(L.T("Klick-Limit", "Click limit"),
            Check(L.T("an", "on"), c => c.ClickLimitEnabled, (c, v) => c.ClickLimitEnabled = v), clickLimit, Label(L.T("Klicks", "clicks"))));
        EnabledWhen(clickLimit, c => c.ClickLimitEnabled);

        var timeLimit = Number(c => c.TimeLimit, (c, v) => c.TimeLimit = v, width: 80);
        var timeUnit = Choice(new[]
        {
            (TimeLimitUnit.Seconds, L.T("Sekunden", "seconds")),
            (TimeLimitUnit.Minutes, L.T("Minuten", "minutes")),
            (TimeLimitUnit.Hours, L.T("Stunden", "hours")),
        }, c => c.TimeLimitUnit, (c, v) => c.TimeLimitUnit = v, minWidth: 110);
        limits.Children.Add(Row(L.T("Zeitlimit", "Time limit"),
            Check(L.T("an", "on"), c => c.TimeLimitEnabled, (c, v) => c.TimeLimitEnabled = v), timeLimit, timeUnit));
        EnabledWhen(timeLimit, c => c.TimeLimitEnabled);
        EnabledWhen(timeUnit, c => c.TimeLimitEnabled);

        var hotkeys = Section(L.T("Hotkeys", "Hotkeys"));
        hotkeys.Children.Add(Check(L.T("Strenge Modifier (zusätzlich gehaltenes Strg/Alt/Umschalt/Win verhindert den Hotkey)",
                                       "Strict modifiers (an extra held Ctrl/Alt/Shift/Win blocks the hotkey)"),
            c => c.StrictModifiers, (c, v) => c.StrictModifiers = v));

        var master = new HotkeyCaptureBox(required: false)
        {
            ToolTip = L.T("Klicken, dann die Kombination drücken - jede Taste, Maustaste oder das Mausrad. \"Leeren\" entfernt den Master-Schalter.",
                          "Click, then press the combination - any key, mouse button or the wheel. \"Clear\" removes the master switch."),
        };
        master.HotkeyChanged += h => Commit(c => c.MasterHotkey = h);
        OnSync(c => master.Sided = HotkeyRecorder.RecordsSided(c));   // the "left/right" choice next to the main hotkey
        OnSync(c => master.SetHotkey(c.MasterHotkey));
        hotkeys.Children.Add(Row(L.T("Master-Schalter", "Master switch"), master, master.CancelButton, master.ClearButton, Choice(new[]
        {
            (ActivationMode.Toggle, L.T("Umschalten", "Toggle")),
            (ActivationMode.Hold, L.T("Halten", "Hold")),
        }, c => c.MasterMode, (c, v) => c.MasterMode = v, minWidth: 120)));
        hotkeys.Children.Add(HotkeyWarnings(master: true));
        hotkeys.Children.Add(Muted(L.T(
            "Mit Master-Schalter startet der Clicker nur, solange der Schalter es erlaubt (Umschalten: beginnt aus). Ohne: immer erlaubt.",
            "With a master switch the clicker only starts while the switch allows it (toggle: starts off). Without one: always allowed."),
            new Thickness(0, 4, 0, 0)));

        var display = Section(L.T("Anzeige", "Display"));
        display.Children.Add(Check(L.T("\"AutoClicker · cps\" einblenden, solange er klickt", "Show \"AutoClicker · cps\" while clicking"),
            c => c.ShowActiveIndicator, (c, v) => c.ShowActiveIndicator = v));
        var noIndicator = Muted(L.T(
            "Mit der aktuellen Aufnahme-Methode lässt sich die Anzeige nicht aus Clips heraushalten - sie bleibt deshalb aus.",
            "The current capture method cannot keep the label out of clips, so it stays hidden."), new Thickness(0, 4, 0, 0));
        display.Children.Add(noIndicator);
        OnSync(c => noIndicator.Visibility = Host is { } h && !ClickerIndicatorRules.CanShow(h.Settings.Current) ? Visibility.Visible : Visibility.Collapsed);
    }
}
