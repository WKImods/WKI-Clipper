using System.Windows;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.Services.AutoClicker;

namespace WKI_Clipper.Views.AutoClicker;

/// <summary>"Einfach": rate and unit, mouse button or keyboard key (+ case), Toggle/Hold and the start hotkey.</summary>
public sealed class SimpleClickerPanel : ClickerPanel
{
    public SimpleClickerPanel()
    {
        var speed = Section(L.T("Geschwindigkeit", "Speed"));
        var rate = Number(c => c.ClickSpeed, (c, v) => c.ClickSpeed = v);
        var unit = Choice(new[]
        {
            (RateUnit.Second, L.T("pro Sekunde", "per second")),
            (RateUnit.Minute, L.T("pro Minute", "per minute")),
            (RateUnit.Hour, L.T("pro Stunde", "per hour")),
            (RateUnit.Day, L.T("pro Tag", "per day")),
        }, c => c.ClickUnit, (c, v) => c.ClickUnit = v);
        var rateRow = Row(L.T("Klicks", "Clicks"), rate, unit);
        speed.Children.Add(rateRow);
        VisibleWhen(rateRow, c => c.RateInputMode == RateInputMode.Rate);

        var max = Muted("", new Thickness(0, 4, 0, 0));
        speed.Children.Add(max);
        OnSync(c => max.Text = c.RateInputMode == RateInputMode.Rate
            ? L.T($"Höchstens {ClickerNumber.Format(ClickerSettingsRules.MaxSpeed(c.ExtendedSpeedLimit, c.DoubleClick, c.ClickUnit))} - mehr unter Erweitert.",
                  $"At most {ClickerNumber.Format(ClickerSettingsRules.MaxSpeed(c.ExtendedSpeedLimit, c.DoubleClick, c.ClickUnit))} - more under Advanced.")
            : L.T($"Taktdauer {c.DurationMs} ms (unter Erweitert eingestellt).", $"Cycle time {c.DurationMs} ms (set under Advanced)."));

        var output = Section(L.T("Was geklickt wird", "What is clicked"));
        output.Children.Add(Row(L.T("Eingabe", "Input"), Choice(new[]
        {
            (ClickerInput.Mouse, L.T("Maus", "Mouse")),
            (ClickerInput.Keyboard, L.T("Tastatur", "Keyboard")),
        }, c => c.InputType, (c, v) => c.InputType = v)));

        var buttonRow = Row(L.T("Maustaste", "Mouse button"), Choice(new[]
        {
            (ClickerMouseButton.Left, L.T("Links", "Left")),
            (ClickerMouseButton.Right, L.T("Rechts", "Right")),
            (ClickerMouseButton.Middle, L.T("Mitte", "Middle")),
        }, c => c.MouseButton, (c, v) => c.MouseButton = v));
        output.Children.Add(buttonRow);
        VisibleWhen(buttonRow, c => c.InputType == ClickerInput.Mouse);

        var key = new HotkeyCaptureBox(required: false, singleKey: true)
        {
            ToolTip = L.T("Klicken, dann die Taste drücken, die gesendet werden soll.", "Click, then press the key to send."),
        };
        key.HotkeyChanged += h => Commit(c => c.KeyboardVk = h is { Keys.Count: > 0 } ? h.Keys[0] : 0);
        OnSync(c => key.SetHotkey(c.KeyboardVk > 0 ? new ClickerHotkey { Keys = { c.KeyboardVk } } : null));
        var keyRow = Row(L.T("Taste", "Key"), key);
        output.Children.Add(keyRow);
        VisibleWhen(keyRow, c => c.InputType == ClickerInput.Keyboard);

        var caseRow = Row(L.T("Buchstaben", "Letters"), Choice(new[]
        {
            (KeyCase.Lower, L.T("klein (a)", "lower case (a)")),
            (KeyCase.Upper, L.T("groß (A)", "upper case (A)")),
        }, c => c.KeyCase, (c, v) => c.KeyCase = v));
        caseRow.ToolTip = L.T("Gilt nur für A-Z; die Feststelltaste wird berücksichtigt.", "Only for A-Z; Caps Lock is taken into account.");
        output.Children.Add(caseRow);
        VisibleWhen(caseRow, c => c.InputType == ClickerInput.Keyboard && c.KeyboardVk is >= 0x41 and <= 0x5A);

        var start = Section(L.T("Starten", "Starting"),
            L.T("Der Hotkey wirkt überall, solange \"Clicker bereit\" an ist.", "The hotkey works everywhere while \"Clicker ready\" is on."));
        start.Children.Add(Row(L.T("Modus", "Mode"), Choice(new[]
        {
            (ActivationMode.Toggle, L.T("Umschalten (drücken = an/aus)", "Toggle (press = on/off)")),
            (ActivationMode.Hold, L.T("Halten (klickt solange gedrückt)", "Hold (clicks while held)")),
        }, c => c.ActivationMode, (c, v) => c.ActivationMode = v, minWidth: 240)));

        var hotkey = new HotkeyCaptureBox(required: true)
        {
            ToolTip = L.T("Klicken, dann Tasten, Maustasten oder das Mausrad über dem Feld drücken - jede Kombination geht.",
                          "Click, then press keys, mouse buttons or the wheel over the field - any combination works."),
        };
        hotkey.HotkeyChanged += h => Commit(c => c.Hotkey = h ?? ClickerHotkey.Default());
        // Stored, and read by the master hotkey's box as well.
        var sided = Check(L.T("Links/rechts unterscheiden", "Tell left and right apart"),
            HotkeyRecorder.RecordsSided, (c, v) => c.SidedModifiers = v,
            L.T("Beim nächsten Aufnehmen (Hotkey und Master-Hotkey) z. B. LStrg statt Strg speichern.",
                "The next capture (hotkey and master hotkey) stores e.g. LCtrl instead of Ctrl."));
        sided.Margin = new Thickness(0);
        sided.VerticalAlignment = VerticalAlignment.Center;
        OnSync(c => hotkey.Sided = HotkeyRecorder.RecordsSided(c));
        OnSync(c => hotkey.SetHotkey(c.Hotkey));
        start.Children.Add(Row("Hotkey", hotkey, hotkey.CancelButton, hotkey.ClearButton, sided));
        start.Children.Add(HotkeyWarnings(master: false));
    }
}
