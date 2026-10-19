namespace WKI_Clipper.Models;

/// <summary>
/// What differs for ONE game from the standard settings: the length F9 saves and the crosshair look.
/// Everything is optional - a null field means "use the standard value". The section has an initializer in
/// <see cref="AppSettings"/>, so a settings file written before profiles existed loads with none - no schema
/// migration needed.
///
/// A profile is looked up by the game's exe file name in lower case (see
/// <see cref="Services.EffectiveSettings.ProfileKey"/>); whatever is not detected as a game uses the standard.
/// </summary>
public sealed class GameProfile
{
    /// <summary>Label for the lists (the name the game folders use); the key is the exe, this is only for people.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>F9 length for this game in seconds. null = the standard length.</summary>
    public int? ClipSeconds { get; set; }

    /// <summary>Crosshair look overrides. null = no overrides at all.</summary>
    public CrosshairLook? Crosshair { get; set; }
}

/// <summary>
/// The parts of the crosshair a game profile may change. Null = take the standard value. Deliberately
/// WITHOUT <c>Enabled</c>: the on/off switch decides how the buffer captures (the crosshair has to stay out of
/// the footage), so a profile that flipped it would restart the buffer exactly when a game starts and lose the
/// lookback.
/// </summary>
public sealed class CrosshairLook
{
    /// <summary>Id of the library image. null = the standard image.</summary>
    public string? ActiveId { get; set; }
    public double? Scale { get; set; }
    public double? Opacity { get; set; }
    public double? Brightness { get; set; }
    public double? Contrast { get; set; }
    public double? Saturation { get; set; }
    public double? GainR { get; set; }
    public double? GainG { get; set; }
    public double? GainB { get; set; }

    /// <summary>
    /// Position of the crosshair's centre in DIPs relative to the CENTRE OF THE MONITOR THE GAME IS ON.
    /// Absolute pixels would break as soon as the game runs on another resolution or monitor. Both null = use
    /// the standard position; one of them null counts as 0 on that axis.
    /// </summary>
    public double? OffsetX { get; set; }
    public double? OffsetY { get; set; }
}
