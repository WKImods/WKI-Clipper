namespace WKI_Clipper.Services.AutoClicker;

/// <summary>The localized text of a <see cref="StopReason"/>, as the status pill and the statistics show it.</summary>
public static class StopReasonText
{
    /// <summary>The reason as a sentence; <paramref name="detail"/> (the limit that was reached, a program name ...) is appended in parentheses.</summary>
    public static string Describe(StopReason reason, string? detail = null)
    {
        var text = reason switch
        {
            StopReason.ClickLimit => L.T("Klick-Limit erreicht", "Click limit reached"),
            StopReason.StopZone => L.T("Stopp-Zone", "Stop zone"),
            StopReason.CornerTopLeft => L.T("Ecke oben links", "Top-left corner"),
            StopReason.CornerTopRight => L.T("Ecke oben rechts", "Top-right corner"),
            StopReason.CornerBottomLeft => L.T("Ecke unten links", "Bottom-left corner"),
            StopReason.CornerBottomRight => L.T("Ecke unten rechts", "Bottom-right corner"),
            StopReason.EdgeTop => L.T("Oberer Rand", "Top edge"),
            StopReason.EdgeBottom => L.T("Unterer Rand", "Bottom edge"),
            StopReason.EdgeLeft => L.T("Linker Rand", "Left edge"),
            StopReason.EdgeRight => L.T("Rechter Rand", "Right edge"),
            StopReason.TaskSwitcher => L.T("Alt+Tab / Aufgabenansicht", "Alt+Tab / Task View"),
            StopReason.NotOnWhitelist => L.T("Programm nicht auf der Whitelist", "Program not on the whitelist"),
            StopReason.Locked => L.T("Gesperrt (Blacklist)", "Locked (blacklist)"),
            StopReason.TimeLimit => L.T("Zeitlimit erreicht", "Time limit reached"),
            StopReason.Hotkey => L.T("Hotkey", "Hotkey"),
            StopReason.HoldReleased => L.T("Hold-Hotkey losgelassen", "Hold hotkey released"),
            StopReason.MasterSwitch => L.T("Master-Schalter", "Master switch"),
            StopReason.AllPointsDone => L.T("Alle Klickpunkte erledigt", "All click points done"),
            StopReason.LeftStartZone => L.T("Start-Zone verlassen", "Left the start zone"),
            StopReason.PickerOpened => L.T("Auswahl geöffnet", "Picker opened"),
            StopReason.ClipperClosed => L.T("Clipper beendet", "Clipper closed"),
            StopReason.Manual => L.T("Manuell gestoppt", "Stopped manually"),
            _ => ""
        };
        return string.IsNullOrEmpty(detail) || text.Length == 0 ? text : $"{text} ({detail})";
    }

    /// <summary>The stop reason that belongs to a stop (or start) the activation machine asked for.</summary>
    public static StopReason From(ActivationReason reason) => reason switch
    {
        ActivationReason.Hotkey => StopReason.Hotkey,
        ActivationReason.HoldReleased => StopReason.HoldReleased,
        ActivationReason.MasterSwitch => StopReason.MasterSwitch,
        ActivationReason.Locked => StopReason.Locked,
        ActivationReason.PickerOpened => StopReason.PickerOpened,
        ActivationReason.StartZone => StopReason.LeftStartZone,
        _ => StopReason.None
    };
}
