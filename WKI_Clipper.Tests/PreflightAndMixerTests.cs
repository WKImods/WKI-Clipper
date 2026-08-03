using System.Linq;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;
// The test project references WinForms too, which has its own CheckState.
using CheckState = WKI_Clipper.Services.CheckState;

namespace WKI_Clipper.Tests;

public sealed class PreflightAndMixerTests
{
    // A fully green baseline; individual tests knock out one thing at a time.
    private static PreflightInput Good() => new(
        ObsConnected: true, CurrentScene: "Start", Streaming: false, ReplayBufferActive: true,
        MicInputKnown: true, MicMuted: false, MicInputName: "Mikrofon",
        ClipperBufferRunning: true, FreeDiskGb: 500);

    private static CheckResult Row(PreflightInput s, string titlePart)
        => PreflightChecks.Evaluate(s).First(c => c.Title.Contains(titlePart));

    [Fact]
    public void All_good_allows_going_live()
    {
        var checks = PreflightChecks.Evaluate(Good());
        Assert.True(PreflightChecks.CanGoLive(checks));
        Assert.DoesNotContain(checks, c => c.State == CheckState.Fail);
    }

    [Fact]
    public void Muted_mic_blocks_going_live()
    {
        var s = Good() with { MicMuted = true };
        Assert.Equal(CheckState.Fail, Row(s, "Mik").State);
        Assert.False(PreflightChecks.CanGoLive(PreflightChecks.Evaluate(s)));
    }

    [Fact]
    public void Disconnected_obs_blocks_and_marks_obs_checks_unknown()
    {
        var s = Good() with { ObsConnected = false };
        var checks = PreflightChecks.Evaluate(s);
        Assert.False(PreflightChecks.CanGoLive(checks));
        // The three OBS-dependent rows cannot be judged without a connection.
        Assert.Equal(3, checks.Count(c => c.State == CheckState.Unknown));
    }

    [Fact]
    public void Unknown_mic_input_warns_but_does_not_block()
    {
        var s = Good() with { MicInputKnown = false, MicMuted = false };
        Assert.Equal(CheckState.Warn, Row(s, "Mik").State);
        Assert.True(PreflightChecks.CanGoLive(PreflightChecks.Evaluate(s)));
    }

    [Fact]
    public void Off_replay_buffers_only_warn()
    {
        var s = Good() with { ReplayBufferActive = false, ClipperBufferRunning = false };
        var checks = PreflightChecks.Evaluate(s);
        Assert.True(PreflightChecks.CanGoLive(checks));            // warnings never block
        Assert.Equal(2, checks.Count(c => c.State == CheckState.Warn));
    }

    [Fact]
    public void Low_disk_warns_and_unknown_disk_is_unknown()
    {
        Assert.Equal(CheckState.Warn, Row(Good() with { FreeDiskGb = 3 }, "Speicher").State);
        Assert.Equal(CheckState.Unknown, Row(Good() with { FreeDiskGb = 0 }, "Speicher").State);
    }

    [Fact]
    public void Already_streaming_is_surfaced_as_a_row()
    {
        var checks = PreflightChecks.Evaluate(Good() with { Streaming = true });
        Assert.Contains(checks, c => c.Title == "Stream" && c.State == CheckState.Ok);
    }

    // ---- mixer dB maths ----

    [Theory]
    [InlineData(1.0, 0.0)]
    [InlineData(0.5, -6.0)]      // half amplitude ≈ -6 dB
    [InlineData(0.1, -20.0)]
    public void MulToDb_matches_the_standard_curve(double mul, double expectedDb)
        => Assert.Equal(expectedDb, ObsVolume.MulToDb(mul), precision: 1);

    [Fact]
    public void Silence_is_minus_infinity_and_formats_as_such()
    {
        Assert.True(double.IsNegativeInfinity(ObsVolume.MulToDb(0)));
        Assert.Equal("-∞", ObsVolume.FormatDb(0));
        Assert.Equal("0.0 dB", ObsVolume.FormatDb(1.0).Replace(",", "."));
    }

    // ---- migration v6 ----

    [Fact]
    public void V5_gains_mixer_and_preflight_widgets()
    {
        var s = new AppSettings { SchemaVersion = 5 };
        s.Widgets.Widgets.RemoveAll(w => w.Id is WidgetId.Mixer or WidgetId.Preflight);

        Assert.True(SettingsService.MigrateIfNeeded(s));
        Assert.Equal(6, s.SchemaVersion);
        Assert.Contains(s.Widgets.Widgets, w => w.Id == WidgetId.Mixer);
        Assert.Contains(s.Widgets.Widgets, w => w.Id == WidgetId.Preflight);
        Assert.NotNull(s.Streaming.Preflight);
        Assert.Equal("Arma Reforger", s.Streaming.Preflight.TargetScene);
    }
}
