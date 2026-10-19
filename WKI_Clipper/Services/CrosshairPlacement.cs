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
    /// The monitor of the game's window: the one it is on RIGHT NOW (<paramref name="live"/>, looked up from the
    /// window handle), else the snapshot taken when the game was detected, else the primary monitor. An empty
    /// rectangle counts as missing.
    /// </summary>
    public static LayoutRect ResolveMonitor(LayoutRect? live, LayoutRect? snapshot, LayoutRect primary)
    {
        static bool Usable(LayoutRect? r) => r is { W: > 0, H: > 0 };
        if (Usable(live)) return live!.Value;
        if (Usable(snapshot)) return snapshot!.Value;
        return primary;
    }

    /// <summary>
    /// Is the monitor a profile's offset is measured from the game's monitor? Only when the profile belongs to the
    /// game in front. A profile edited from the board while another game (or none) runs has no known game monitor:
    /// its offset is measured from the monitor the crosshair is on - for the preview AND for the drag, so both agree.
    /// </summary>
    public static bool OffsetUsesGameMonitor(string? gameExe, string? profileKey)
        => profileKey is not null && string.Equals(EffectiveSettings.ProfileKey(gameExe), profileKey, StringComparison.Ordinal);

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
