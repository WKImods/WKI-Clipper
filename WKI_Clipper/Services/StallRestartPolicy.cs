using System;
using System.Linq;

namespace WKI_Clipper.Services;

/// <summary>What the stall watchdog does about a ring that stopped turning.</summary>
public enum StallAction
{
    /// <summary>Not yet: a start that never delivered a picture waits out its backoff.</summary>
    Wait,
    /// <summary>Restart with the "no new data" toast: a ring that ran and stopped, or the first start that never delivered.</summary>
    Restart,
    /// <summary>Restart without a toast: another start in a row that delivered nothing, or the recorded monitor sleeps.</summary>
    RestartQuietly
}

/// <summary>
/// A buffer started while its monitor sleeps deeply (DisplayPort standby: Windows drops the monitor's device, its CCD path is
/// gone) never gets a first picture from ddagrab: ffmpeg waits in the input. Restarting that every 30 s with a toast helped
/// nobody (222 restarts overnight). Starts that deliver nothing back off instead - 30 s, then 30 s, 1, 2, 4, 8 and at most
/// 10 min more - and stay quiet after the first, and quiet from the start while the monitor sleeps. Waking the monitor ends
/// the waiting ffmpeg with ACCESS_LOST (887a0026), which the display auto-restart picks up at once. A ring that ran and then
/// stalled is restarted with its toast right away, as before.
/// </summary>
public static class StallRestartPolicy
{
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);

    /// <summary>The extra wait for a start that comes after <paramref name="streak"/> starts in a row that delivered nothing.</summary>
    public static TimeSpan NoPictureBackoff(int streak)
        => streak <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, 30 * Math.Pow(2, Math.Min(streak - 1, 10))));

    /// <param name="deliveredAny">The run completed at least one segment.</param>
    /// <param name="monitorAsleep">The monitor the run records sleeps (<see cref="IsAsleep"/>).</param>
    /// <param name="noPictureStreak">Earlier starts in a row that delivered nothing and were restarted.</param>
    /// <param name="sinceStart">How long the run has been going.</param>
    /// <param name="grace">What any run gets before a missing segment counts (the watchdog's own grace).</param>
    public static StallAction Decide(bool deliveredAny, bool monitorAsleep, int noPictureStreak, TimeSpan sinceStart, TimeSpan grace)
    {
        if (deliveredAny) return StallAction.Restart;
        if (sinceStart < grace + NoPictureBackoff(noPictureStreak)) return StallAction.Wait;
        return noPictureStreak == 0 && !monitorAsleep ? StallAction.Restart : StallAction.RestartQuietly;
    }

    /// <summary>
    /// The monitor <paramref name="gdiName"/> sleeps deeply: Windows lists it without its device path, or not at all any more.
    /// False without a name (window capture, nothing resolved).
    /// </summary>
    public static bool IsAsleep(DisplayTopology topology, string? gdiName)
    {
        if (string.IsNullOrEmpty(gdiName)) return false;
        var display = topology.Displays.FirstOrDefault(d => string.Equals(d.GdiName, gdiName, StringComparison.OrdinalIgnoreCase));
        return display is null || string.IsNullOrEmpty(display.DevicePath);
    }
}
