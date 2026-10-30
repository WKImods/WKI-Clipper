using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>
/// When the small "AutoClicker · 25 cps" label may be on screen and what it says (design section 7). The label is excluded from every
/// capture with WDA_EXCLUDEFROMCAPTURE, which AMD's own capture (vsrc_amf) ignores. The label must neither end up in a clip nor make the
/// replay buffer restart when it appears, so it asks the capture path instead of steering it: with AMF's capture in use it stays hidden.
/// It is deliberately NOT part of <see cref="FFmpegCommandBuilder.MustHonorCaptureExclusion"/>.
/// <para>
/// The mouse patterns share the label (mouse-patterns design 4.6): "Mausbewegung · Kreis" while a pattern plays (like the clicker's part
/// only with <see cref="ClickerSettings.ShowActiveIndicator"/>), "Aufnahme läuft · Strg+Alt+R beendet" while recording - always, or the
/// user could not tell that a recording is on. The parts that apply are joined.
/// </para>
/// </summary>
public static class ClickerIndicatorRules
{
    /// <summary>Between the clicker's, the playing patterns' and the recording's part of the label.</summary>
    public const string PartSeparator = "  ·  ";

    /// <summary>
    /// true when the capture path in use honours display affinity. False while AMD's own capture is configured and nothing else (the
    /// crosshair, a capture-excluded widget, the performance overlay) already forces the buffer onto ddagrab. Conservative for window
    /// capture, where the label could never be in the footage anyway.
    /// </summary>
    public static bool CanShow(AppSettings settings)
        => !FFmpegCommandBuilder.UsesAmfNativeCapture(settings, rawInput: false);

    /// <summary>
    /// The label is up while the capture path can hide it and one of its parts applies: the clicker runs or a pattern plays (both only
    /// when the user wants the label), or a pattern is being recorded (regardless of that option).
    /// </summary>
    public static bool ShouldShow(ClickerState state, AppSettings settings)
        => CanShow(settings) && Parts(state, settings).Count > 0;

    /// <summary>"AutoClicker · 25 cps". Rates below one click a second read better per minute or hour.</summary>
    public static string Text(double cps) => "AutoClicker · " + FormatCps(cps);

    /// <summary>"AutoClicker · 25 cps  ·  Mausbewegung · Kreis +1", "Aufnahme läuft · Strg+Alt+R beendet": the parts that apply, joined.</summary>
    public static string Text(ClickerState state, AppSettings settings) => string.Join(PartSeparator, Parts(state, settings));

    private static List<string> Parts(ClickerState state, AppSettings settings)
    {
        var clicker = settings.AutoClicker.Clicker;
        bool wanted = clicker.ShowActiveIndicator;
        var parts = new List<string>(3);
        if (wanted && state.Phase == ClickerPhase.Running) parts.Add(Text(state.Cps));
        if (wanted && PlayingText(state.Playing, clicker) is { } playing) parts.Add(playing);
        if (state.Recording)
        {
            string hotkey = ClickerHotkey.Describe(clicker.PatternRecordHotkey);
            parts.Add(L.T($"Aufnahme läuft · {hotkey} beendet", $"Recording · {hotkey} stops it"));
        }
        return parts;
    }

    /// <summary>"Mausbewegung · Kreis", "Mausbewegung · Kreis +2" (the first name the settings know, then how many more); null when none plays.</summary>
    private static string? PlayingText(string? playing, ClickerSettings clicker)
    {
        if (string.IsNullOrWhiteSpace(playing)) return null;
        var ids = playing.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0) return null;

        string label = L.T("Mausbewegung", "Mouse pattern");
        var patterns = clicker.MousePatterns ?? new List<MousePattern>();
        string? first = ids
            .Select(id => patterns.FirstOrDefault(p => p is not null && string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))?.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        if (first is null) return label;   // deleted meanwhile: still say that something moves the mouse
        return ids.Length > 1 ? $"{label} · {first} +{ids.Length - 1}" : $"{label} · {first}";
    }

    /// <summary>"25 cps", "2,5 cps" / "2.5 cps", "30/min", "5/h": at most two decimals, no trailing zeros, the decimal mark of the UI language.</summary>
    public static string FormatCps(double cps)
    {
        var culture = CultureInfo.GetCultureInfo(L.English ? "en-US" : "de-DE");
        if (double.IsNaN(cps) || double.IsInfinity(cps) || cps < 0) cps = 0;
        if (cps >= 1 || cps == 0) return Trim(cps, culture) + " cps";
        double perMinute = cps * 60;
        if (perMinute >= 1) return Trim(perMinute, culture) + "/min";
        return Trim(perMinute * 60, culture) + "/h";
    }

    private static string Trim(double value, CultureInfo culture)
        => (value >= 100 ? Math.Round(value) : Math.Round(value, 2)).ToString("0.##", culture);
}
