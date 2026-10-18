using System;
using System.Globalization;

namespace WKI_Clipper.Services;

/// <summary>
/// Buffer lengths reach 30 minutes, so "1800 s" is no longer a readable unit. One formatter keeps the
/// tray, toast, widgets, settings and status pages saying the same thing. The units are
/// language-neutral ("s", "min"), hence no <see cref="L"/> here.
/// </summary>
public static class DurationText
{
    public const int MinSeconds = 15;
    public const int MaxSeconds = 1800;

    /// <summary>The lookback lengths the settings page offers.</summary>
    public static readonly int[] Choices = { 15, 30, 45, 60, 90, 120, 180, 300, 600, 900, 1200, 1800 };

    /// <summary>From this length on a clip counts as "long": progress toast, busy text, hints.</summary>
    public const int LongClipSeconds = 300;

    /// <summary>A hand-edited settings.json must not be able to build a ring of hours.</summary>
    public static int Clamp(int seconds) => Math.Clamp(seconds, MinSeconds, MaxSeconds);

    /// <summary>A configured length: under a minute "45 s", whole minutes "30 min", otherwise "1:30 min".</summary>
    public static string Format(int seconds)
    {
        if (seconds < 60) return string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, seconds)} s");
        if (seconds % 60 == 0) return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60} min");
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00} min");
    }

    /// <summary>Footage that actually exists: whole seconds, "42 s" or "m:ss min" ("29:05 min", "30:00 min").</summary>
    public static string FormatActual(double seconds)
    {
        long whole = (long)Math.Round(Math.Max(0, double.IsNaN(seconds) ? 0 : seconds), MidpointRounding.AwayFromZero);
        if (whole < 60) return string.Create(CultureInfo.InvariantCulture, $"{whole} s");
        return string.Create(CultureInfo.InvariantCulture, $"{whole / 60}:{whole % 60:00} min");
    }
}
