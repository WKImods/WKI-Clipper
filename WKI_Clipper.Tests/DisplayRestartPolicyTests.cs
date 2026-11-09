using System;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class DisplayRestartPolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 9, 20, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime[] NoRestarts = Array.Empty<DateTime>();

    private const string GenericExit = "[out#0/segment @ 0000020b] Conversion failed!";

    [Theory]
    [InlineData("[Parsed_ddagrab_0 @ 0000020b1c3e4f40] Output parameters changed!")]
    [InlineData("[Parsed_ddagrab_0 @ 0000020b1c3e4f40] AcquireNextFrame failed: 887a0026")]
    [InlineData("[Parsed_ddagrab_0 @ 0000020b1c3e4f40] DuplicateOutput failed: 887a0022")]
    [InlineData("[Parsed_ddagrab_0 @ 0000020b1c3e4f40] Failed to create device: 887a0005")]
    [InlineData("AcquireNextFrame failed: 887A0026")]          // hex in upper case
    [InlineData("output PARAMETERS changed")]
    public void Marker_in_stderr_allows_a_restart(string line)
    {
        string stderr = "frame= 1234 fps= 60\n" + line + "\n" + GenericExit;

        Assert.True(DisplayRestartPolicy.ShouldAutoRestart(stderr, lastDisplayChangeUtc: null, Now, NoRestarts));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(10)]
    public void Exit_soon_after_display_change_allows_a_restart(int secondsAgo)
        => Assert.True(DisplayRestartPolicy.ShouldAutoRestart(GenericExit, Now.AddSeconds(-secondsAgo), Now, NoRestarts));

    [Fact]
    public void Exit_without_marker_or_display_change_does_not_restart()
    {
        Assert.False(DisplayRestartPolicy.ShouldAutoRestart(GenericExit, null, Now, NoRestarts));
        Assert.False(DisplayRestartPolicy.ShouldAutoRestart(null, null, Now, NoRestarts));
        Assert.False(DisplayRestartPolicy.ShouldAutoRestart("", null, Now, NoRestarts));
        // A display change long before the exit has nothing to do with it.
        Assert.False(DisplayRestartPolicy.ShouldAutoRestart(GenericExit, Now.AddSeconds(-11), Now, NoRestarts));
    }

    [Fact]
    public void Fourth_restart_within_a_minute_is_refused()
    {
        string stderr = "AcquireNextFrame failed: 887a0026";
        var three = new[] { Now.AddSeconds(-50), Now.AddSeconds(-30), Now.AddSeconds(-5) };
        Assert.False(DisplayRestartPolicy.ShouldAutoRestart(stderr, Now.AddSeconds(-2), Now, three));

        var two = new[] { Now.AddSeconds(-30), Now.AddSeconds(-5) };
        Assert.True(DisplayRestartPolicy.ShouldAutoRestart(stderr, Now.AddSeconds(-2), Now, two));

        // Restarts older than a minute no longer count.
        var oneOld = new[] { Now.AddSeconds(-61), Now.AddSeconds(-30), Now.AddSeconds(-5) };
        Assert.True(DisplayRestartPolicy.ShouldAutoRestart(stderr, null, Now, oneOld));
    }

    /// <summary>The mode-change errors, plus the start refused while the lock screen or a UAC prompt has the input (0.19.2).</summary>
    [Fact]
    public void Markers_are_the_ddagrab_mode_change_errors()
        => Assert.Equal(new[] { "Output parameters changed", "887a0026", "887a0022", "887a0005", "Desktop duplication access denied" },
                        DisplayRestartPolicy.ModeChangeMarkers);
}
