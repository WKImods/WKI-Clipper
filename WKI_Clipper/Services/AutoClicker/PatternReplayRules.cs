using WKI_Clipper.Models;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>
/// How one playback is sent: <see cref="RelativeRaw"/> = the raw counts as relative moves ("Spiel"), <see cref="RelativePointer"/> = the
/// pointer offsets as relative moves (no usable raw track), <see cref="AbsolutePointer"/> = anchor + pointer offsets as absolute moves ("Zeiger").
/// </summary>
public enum ResolvedReplay { RelativeRaw, RelativePointer, AbsolutePointer }

/// <summary>The replay-type decision of mouse-patterns design 4.3, made once at each playback start.</summary>
public static class PatternReplayRules
{
    /// <summary>
    /// <list type="bullet">
    /// <item>Raw: the raw counts; without a raw track the pointer track, still relative.</item>
    /// <item>Pointer: always anchor + pointer offsets.</item>
    /// <item>Auto (also for an unknown value): without a raw track (pen tablet) Zeiger while the pointer is visible, otherwise the pointer
    /// track as relative moves. With one: linear ballistics (<paramref name="accelerationOn"/> false) always the raw counts - right for
    /// games and the desktop alike, never an absolute move; acceleration on: Zeiger while the pointer is visible (pixel-exact), otherwise
    /// the raw counts, since an absolute move into a game that hides the pointer would jerk its camera.</item>
    /// </list>
    /// </summary>
    public static ResolvedReplay Resolve(PatternReplay setting, bool accelerationOn, bool pointerVisible, bool hasRaw)
    {
        var relative = hasRaw ? ResolvedReplay.RelativeRaw : ResolvedReplay.RelativePointer;
        return setting switch
        {
            PatternReplay.Raw => relative,
            PatternReplay.Pointer => ResolvedReplay.AbsolutePointer,
            _ when !hasRaw => pointerVisible ? ResolvedReplay.AbsolutePointer : ResolvedReplay.RelativePointer,
            _ => accelerationOn && pointerVisible ? ResolvedReplay.AbsolutePointer : ResolvedReplay.RelativeRaw,
        };
    }
}
