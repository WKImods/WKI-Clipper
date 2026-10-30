using System;
using System.Globalization;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>
/// When the small "AutoClicker · 25 cps" label may be on screen and what it says (design section 7). The label is excluded from every
/// capture with WDA_EXCLUDEFROMCAPTURE, which AMD's own capture (vsrc_amf) ignores. The label must neither end up in a clip nor make the
/// replay buffer restart when it appears, so it asks the capture path instead of steering it: with AMF's capture in use it stays hidden.
/// It is deliberately NOT part of <see cref="FFmpegCommandBuilder.MustHonorCaptureExclusion"/>.
/// </summary>
public static class ClickerIndicatorRules
{
    /// <summary>
    /// true when the capture path in use honours display affinity. False while AMD's own capture is configured and nothing else (the
    /// crosshair, a capture-excluded widget, the performance overlay) already forces the buffer onto ddagrab. Conservative for window
    /// capture, where the label could never be in the footage anyway.
    /// </summary>
    public static bool CanShow(AppSettings settings)
        => !FFmpegCommandBuilder.UsesAmfNativeCapture(settings, rawInput: false);

    /// <summary>The label is up while the clicker runs, the user wants it and the capture path can hide it.</summary>
    public static bool ShouldShow(ClickerState state, AppSettings settings)
        => state.Phase == ClickerPhase.Running && settings.AutoClicker.Clicker.ShowActiveIndicator && CanShow(settings);

    /// <summary>"AutoClicker · 25 cps". Rates below one click a second read better per minute or hour.</summary>
    public static string Text(double cps) => "AutoClicker · " + FormatCps(cps);

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
