using System;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>
/// Where a game profile puts the crosshair: an offset in DIPs from the centre of the monitor the game is on,
/// so the same profile works on another resolution, monitor or Windows scaling. Pure maths; the overlay
/// itself keeps working in physical pixels.
/// </summary>
public static class CrosshairPlacement
{
    private static double SafeScale(double scale) => scale > 0 && !double.IsNaN(scale) ? scale : 1.0;

    /// <summary>Physical centre = monitor centre (physical pixels) + offset (DIPs) x the monitor's scale.</summary>
    public static (double X, double Y) Center(LayoutRect monitor, double scale, double offsetX, double offsetY)
    {
        scale = SafeScale(scale);
        return (monitor.X + monitor.W / 2 + offsetX * scale, monitor.Y + monitor.H / 2 + offsetY * scale);
    }

    /// <summary>The inverse of <see cref="Center"/>: a dragged physical centre as an offset, rounded to 0.01 DIP.</summary>
    public static (double X, double Y) OffsetFrom(LayoutRect monitor, double scale, double centerX, double centerY)
    {
        scale = SafeScale(scale);
        return (Math.Round((centerX - (monitor.X + monitor.W / 2)) / scale, 2),
                Math.Round((centerY - (monitor.Y + monitor.H / 2)) / scale, 2));
    }

    /// <summary>
    /// Which profile a drag of the crosshair belongs to - its key - or null for the standard position.
    /// The rule is "what you drag is what you see":
    ///  - while the crosshair widget is being edited (<paramref name="editing"/>) the overlay shows the look of the
    ///    edited target, so a drag moves that target: a game's profile (its first drag creates the profile) or,
    ///    for "Alle Spiele", the standard position;
    ///  - otherwise the overlay shows the game in front, so a drag moves that game's profile when it sets crosshair
    ///    values. A profile that only sets the F9 length shows the standard crosshair, so a drag there stays standard.
    /// </summary>
    public static string? DragProfileKey(AppSettings settings, string? gameExe, CrosshairEditTarget? editing)
    {
        if (editing is not null) return EffectiveSettings.ProfileKey(editing.Key);
        if (EffectiveSettings.ProfileKey(gameExe) is not { } key) return null;
        return GameProfiles.HasOverrides(EffectiveSettings.ProfileFor(settings, gameExe)?.Crosshair) ? key : null;
    }

    /// <summary>The exe whose look the overlay shows: the edited target while editing (null = standard), else the game in front.</summary>
    public static string? LookExe(string? gameExe, CrosshairEditTarget? editing)
        => editing is not null ? editing.Key : gameExe;
}
