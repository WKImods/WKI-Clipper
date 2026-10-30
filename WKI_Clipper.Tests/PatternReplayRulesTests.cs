using WKI_Clipper.Models;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Mouse-patterns design 4.3: which track a playback uses and how it is sent.</summary>
public sealed class PatternReplayRulesTests
{
    private const ResolvedReplay Raw = ResolvedReplay.RelativeRaw;
    private const ResolvedReplay Rel = ResolvedReplay.RelativePointer;
    private const ResolvedReplay Abs = ResolvedReplay.AbsolutePointer;

    [Theory]
    //          setting               accel  visible hasRaw  expected
    // Spiel (roh): the raw counts; without a raw track the pointer track, still as relative moves.
    [InlineData(PatternReplay.Raw,     false, false, true,  Raw)]
    [InlineData(PatternReplay.Raw,     false, true,  true,  Raw)]
    [InlineData(PatternReplay.Raw,     true,  false, true,  Raw)]
    [InlineData(PatternReplay.Raw,     true,  true,  true,  Raw)]
    [InlineData(PatternReplay.Raw,     false, false, false, Rel)]
    [InlineData(PatternReplay.Raw,     false, true,  false, Rel)]
    [InlineData(PatternReplay.Raw,     true,  false, false, Rel)]
    [InlineData(PatternReplay.Raw,     true,  true,  false, Rel)]
    // Zeiger: always anchor + pointer offsets.
    [InlineData(PatternReplay.Pointer, false, false, true,  Abs)]
    [InlineData(PatternReplay.Pointer, false, true,  true,  Abs)]
    [InlineData(PatternReplay.Pointer, true,  false, true,  Abs)]
    [InlineData(PatternReplay.Pointer, true,  true,  true,  Abs)]
    [InlineData(PatternReplay.Pointer, false, false, false, Abs)]
    [InlineData(PatternReplay.Pointer, false, true,  false, Abs)]
    [InlineData(PatternReplay.Pointer, true,  false, false, Abs)]
    [InlineData(PatternReplay.Pointer, true,  true,  false, Abs)]
    // Automatisch: linear ballistics -> the raw counts, whether a pointer is visible or not.
    [InlineData(PatternReplay.Auto,    false, false, true,  Raw)]
    [InlineData(PatternReplay.Auto,    false, true,  true,  Raw)]
    // Acceleration on: Zeiger while the pointer is visible (pixel-exact), otherwise the raw counts (no absolute move into a game).
    [InlineData(PatternReplay.Auto,    true,  true,  true,  Abs)]
    [InlineData(PatternReplay.Auto,    true,  false, true,  Raw)]
    // No usable raw track (pen tablet): Zeiger while the pointer is visible, otherwise the pointer track as relative moves.
    [InlineData(PatternReplay.Auto,    false, true,  false, Abs)]
    [InlineData(PatternReplay.Auto,    true,  true,  false, Abs)]
    [InlineData(PatternReplay.Auto,    false, false, false, Rel)]
    [InlineData(PatternReplay.Auto,    true,  false, false, Rel)]
    public void Resolve_matches_spec_table(PatternReplay setting, bool accelerationOn, bool pointerVisible, bool hasRaw, ResolvedReplay expected)
        => Assert.Equal(expected, PatternReplayRules.Resolve(setting, accelerationOn, pointerVisible, hasRaw));

    [Fact]
    public void An_unknown_setting_is_treated_as_Auto()
        => Assert.Equal(PatternReplayRules.Resolve(PatternReplay.Auto, true, true, true),
                        PatternReplayRules.Resolve((PatternReplay)99, true, true, true));
}
