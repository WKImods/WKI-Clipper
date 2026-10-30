using System;
using System.Collections.Generic;
using WKI_Clipper.Models;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Pause zones (suspend while inside) and start zones (start on entering, stop on leaving) - design section 4.</summary>
public sealed class ZoneTrackerTests
{
    private static ClickerZone Zone(int x, int y, int w, int h, ZoneAction a, bool enabled = true)
        => new() { Id = Guid.NewGuid().ToString("N"), X = x, Y = y, W = w, H = h, Action = a, Enabled = enabled };

    private static readonly PixelPoint Outside = new(0, 0);
    private static readonly PixelPoint Inside = new(150, 150);

    private static ZoneTracker Tracker(params ClickerZone[] zones)
    {
        var t = new ZoneTracker();
        t.Configure(true, zones);
        return t;
    }

    // ---- hit test ----

    [Fact]
    public void Hit_uses_the_action_the_enabled_flag_and_exclusive_far_edges()
    {
        var zones = new[]
        {
            Zone(100, 100, 100, 100, ZoneAction.Pause),
            Zone(-500, -500, 100, 100, ZoneAction.Stop),
            Zone(300, 300, 50, 50, ZoneAction.Start, enabled: false)
        };
        Assert.True(ZoneTracker.Hit(zones, ZoneAction.Pause, 100, 100));
        Assert.True(ZoneTracker.Hit(zones, ZoneAction.Pause, 199, 199));
        Assert.False(ZoneTracker.Hit(zones, ZoneAction.Pause, 200, 150));
        Assert.False(ZoneTracker.Hit(zones, ZoneAction.Stop, 150, 150));
        Assert.True(ZoneTracker.Hit(zones, ZoneAction.Stop, -450, -450));
        Assert.False(ZoneTracker.Hit(zones, ZoneAction.Start, 310, 310));   // disabled
    }

    // ---- pause zones ----

    [Fact]
    public void Entering_a_pause_zone_pauses_and_leaving_resumes()
    {
        var t = Tracker(Zone(100, 100, 100, 100, ZoneAction.Pause));

        Assert.Equal(PauseChange.None, t.PollPause(Outside));
        Assert.False(t.IsPaused);
        Assert.Equal(PauseChange.Paused, t.PollPause(Inside));
        Assert.True(t.IsPaused);
        Assert.Equal(PauseChange.None, t.PollPause(new PixelPoint(160, 160)));   // still inside
        Assert.True(t.IsPaused);
        Assert.Equal(PauseChange.Resumed, t.PollPause(Outside));
        Assert.False(t.IsPaused);
        Assert.Equal(PauseChange.None, t.PollPause(Outside));
    }

    [Fact]
    public void Moving_between_overlapping_pause_zones_stays_paused()
    {
        var t = Tracker(Zone(100, 100, 100, 100, ZoneAction.Pause), Zone(150, 100, 100, 100, ZoneAction.Pause));
        Assert.Equal(PauseChange.Paused, t.PollPause(new PixelPoint(120, 120)));
        Assert.Equal(PauseChange.None, t.PollPause(new PixelPoint(170, 120)));    // in both
        Assert.Equal(PauseChange.None, t.PollPause(new PixelPoint(230, 120)));    // only in the second
        Assert.True(t.IsPaused);
        Assert.Equal(PauseChange.Resumed, t.PollPause(new PixelPoint(300, 120)));
    }

    [Fact]
    public void Stop_and_start_zones_do_not_pause()
    {
        var t = Tracker(Zone(100, 100, 100, 100, ZoneAction.Stop), Zone(100, 100, 100, 100, ZoneAction.Start));
        Assert.Equal(PauseChange.None, t.PollPause(Inside));
        Assert.False(t.IsPaused);
    }

    [Fact]
    public void Disabled_pause_zones_and_switched_off_zones_do_not_pause()
    {
        var t = Tracker(Zone(100, 100, 100, 100, ZoneAction.Pause, enabled: false));
        Assert.Equal(PauseChange.None, t.PollPause(Inside));

        t.Configure(false, new[] { Zone(100, 100, 100, 100, ZoneAction.Pause) });
        Assert.Equal(PauseChange.None, t.PollPause(Inside));
        Assert.False(t.IsPaused);
    }

    [Fact]
    public void Switching_the_zones_off_while_paused_resumes()
    {
        var zones = new[] { Zone(100, 100, 100, 100, ZoneAction.Pause) };
        var t = new ZoneTracker();
        t.Configure(true, zones);
        Assert.Equal(PauseChange.Paused, t.PollPause(Inside));
        t.Configure(false, zones);
        Assert.Equal(PauseChange.Resumed, t.PollPause(Inside));
    }

    [Fact]
    public void A_new_run_does_not_inherit_a_pause()
    {
        var t = Tracker(Zone(100, 100, 100, 100, ZoneAction.Pause));
        t.PollPause(Inside);
        t.ResetPause();
        Assert.False(t.IsPaused);
        Assert.Equal(PauseChange.Paused, t.PollPause(Inside));   // reported again for the new run
    }

    [Fact]
    public void A_pause_zone_on_a_negative_monitor_works()
    {
        var t = Tracker(Zone(-1920, 0, 400, 400, ZoneAction.Pause));
        Assert.Equal(PauseChange.Paused, t.PollPause(new PixelPoint(-1800, 100)));
        Assert.Equal(PauseChange.Resumed, t.PollPause(new PixelPoint(-1000, 100)));
    }

    // ---- start zones ----

    [Fact]
    public void Entering_a_start_zone_while_idle_starts_and_leaving_stops()
    {
        var t = Tracker(Zone(100, 100, 100, 100, ZoneAction.Start));

        Assert.Equal(StartZoneAction.None, t.PollStart(Outside, running: false, canStart: true));
        Assert.Equal(StartZoneAction.Start, t.PollStart(Inside, running: false, canStart: true));
        Assert.True(t.StartedByZone);
        Assert.Equal(StartZoneAction.None, t.PollStart(Inside, running: true, canStart: false));
        Assert.Equal(StartZoneAction.Stop, t.PollStart(Outside, running: true, canStart: false));
        Assert.False(t.StartedByZone);
        Assert.Equal(StartZoneAction.None, t.PollStart(Outside, running: false, canStart: true));
    }

    [Fact]
    public void A_start_only_happens_on_the_entering_edge()
    {
        var t = Tracker(Zone(100, 100, 100, 100, ZoneAction.Start));
        t.PollStart(Outside, false, true);
        Assert.Equal(StartZoneAction.Start, t.PollStart(Inside, false, true));
        t.OnRunEnded();    // the user stopped it with the hotkey while the cursor stays inside
        Assert.Equal(StartZoneAction.None, t.PollStart(Inside, false, true));
        Assert.Equal(StartZoneAction.None, t.PollStart(Inside, false, true));
        Assert.Equal(StartZoneAction.None, t.PollStart(Outside, false, true));   // leaving does not stop a run that is over
        Assert.Equal(StartZoneAction.Start, t.PollStart(Inside, false, true));   // entering again starts again
    }

    [Fact]
    public void A_fresh_tracker_with_the_cursor_already_inside_does_not_start()
    {
        // Arming the host with the cursor parked in a start zone is not an entry.
        var t = Tracker(Zone(100, 100, 100, 100, ZoneAction.Start));
        Assert.Equal(StartZoneAction.None, t.PollStart(Inside, false, true));
        Assert.Equal(StartZoneAction.None, t.PollStart(Inside, false, true));
        Assert.False(t.StartedByZone);
        Assert.Equal(StartZoneAction.None, t.PollStart(Outside, false, true));
        Assert.Equal(StartZoneAction.Start, t.PollStart(Inside, false, true));
    }

    [Fact]
    public void Switching_zones_off_and_on_with_the_cursor_inside_does_not_start()
    {
        var zones = new[] { Zone(100, 100, 100, 100, ZoneAction.Start) };
        var t = new ZoneTracker();
        t.Configure(true, zones);
        t.PollStart(Outside, false, true);
        Assert.Equal(StartZoneAction.Start, t.PollStart(Inside, false, true));
        t.OnRunEnded();

        t.Configure(false, zones);
        Assert.Equal(StartZoneAction.None, t.PollStart(Inside, false, true));
        t.Configure(true, zones);
        Assert.Equal(StartZoneAction.None, t.PollStart(Inside, false, true));   // the baseline, not an entry
        Assert.Equal(StartZoneAction.None, t.PollStart(Outside, false, true));
        Assert.Equal(StartZoneAction.Start, t.PollStart(Inside, false, true));
    }

    [Fact]
    public void A_run_not_started_by_the_zone_is_not_stopped_when_the_cursor_leaves()
    {
        var t = Tracker(Zone(100, 100, 100, 100, ZoneAction.Start));
        Assert.Equal(StartZoneAction.None, t.PollStart(Inside, running: true, canStart: false));
        Assert.False(t.StartedByZone);
        Assert.Equal(StartZoneAction.None, t.PollStart(Outside, running: true, canStart: false));
    }

    [Fact]
    public void Entering_while_the_clicker_cannot_start_consumes_the_edge()
    {
        // Locked, no armed host, picker open ...: no start now, and none later while the cursor stays inside.
        var t = Tracker(Zone(100, 100, 100, 100, ZoneAction.Start));
        t.PollStart(Outside, running: false, canStart: true);
        Assert.Equal(StartZoneAction.None, t.PollStart(Inside, running: false, canStart: false));
        Assert.Equal(StartZoneAction.None, t.PollStart(Inside, running: false, canStart: true));
        Assert.False(t.StartedByZone);
    }

    [Fact]
    public void A_refused_start_leaves_nothing_to_stop()
    {
        var t = Tracker(Zone(100, 100, 100, 100, ZoneAction.Start));
        t.PollStart(Outside, false, true);
        Assert.Equal(StartZoneAction.Start, t.PollStart(Inside, false, true));
        t.OnStartRefused();    // the activation machine said no
        Assert.False(t.StartedByZone);
        Assert.Equal(StartZoneAction.None, t.PollStart(Outside, running: false, canStart: true));
    }

    [Fact]
    public void Moving_between_overlapping_start_zones_keeps_the_run()
    {
        var t = Tracker(Zone(100, 100, 100, 100, ZoneAction.Start), Zone(150, 100, 100, 100, ZoneAction.Start));
        t.PollStart(Outside, false, true);
        Assert.Equal(StartZoneAction.Start, t.PollStart(new PixelPoint(120, 120), false, true));
        Assert.Equal(StartZoneAction.None, t.PollStart(new PixelPoint(170, 120), true, false));
        Assert.Equal(StartZoneAction.None, t.PollStart(new PixelPoint(230, 120), true, false));
        Assert.Equal(StartZoneAction.Stop, t.PollStart(new PixelPoint(300, 120), true, false));
    }

    [Fact]
    public void A_run_that_ended_on_its_own_is_not_stopped_again_on_leaving()
    {
        var t = Tracker(Zone(100, 100, 100, 100, ZoneAction.Start));
        t.PollStart(Outside, false, true);
        t.PollStart(Inside, false, true);
        // The engine hit its click limit; a leave now finds nothing running.
        Assert.Equal(StartZoneAction.None, t.PollStart(Outside, running: false, canStart: true));
        Assert.False(t.StartedByZone);
    }

    [Fact]
    public void Pause_and_stop_zones_do_not_start()
    {
        var t = Tracker(Zone(100, 100, 100, 100, ZoneAction.Pause), Zone(100, 100, 100, 100, ZoneAction.Stop));
        Assert.Equal(StartZoneAction.None, t.PollStart(Inside, false, true));
    }

    [Fact]
    public void Disabled_start_zones_and_switched_off_zones_do_not_start()
    {
        var t = Tracker(Zone(100, 100, 100, 100, ZoneAction.Start, enabled: false));
        Assert.Equal(StartZoneAction.None, t.PollStart(Inside, false, true));

        t.Configure(false, new[] { Zone(100, 100, 100, 100, ZoneAction.Start) });
        Assert.Equal(StartZoneAction.None, t.PollStart(Outside, false, true));
        Assert.Equal(StartZoneAction.None, t.PollStart(Inside, false, true));
    }

    [Fact]
    public void Re_pushing_the_same_configuration_does_not_restart_a_clicker_stopped_inside_the_zone()
    {
        var zones = new[] { Zone(100, 100, 100, 100, ZoneAction.Start) };
        var t = new ZoneTracker();
        t.Configure(true, zones);
        t.PollStart(Outside, false, true);
        t.PollStart(Inside, false, true);
        t.OnRunEnded();

        t.Configure(true, zones);    // the main app pushes the settings again after any edit
        Assert.Equal(StartZoneAction.None, t.PollStart(Inside, false, true));
    }

    [Fact]
    public void Switching_the_zones_off_during_a_zone_run_forgets_the_zone_ownership()
    {
        var zones = new[] { Zone(100, 100, 100, 100, ZoneAction.Start) };
        var t = new ZoneTracker();
        t.Configure(true, zones);
        t.PollStart(Outside, false, true);
        t.PollStart(Inside, false, true);
        t.Configure(false, zones);
        Assert.Equal(StartZoneAction.None, t.PollStart(Outside, running: true, canStart: false));
        Assert.False(t.StartedByZone);
    }

    [Fact]
    public void A_start_zone_on_a_negative_monitor_works()
    {
        var t = Tracker(Zone(-1920, -1080, 500, 500, ZoneAction.Start));
        t.PollStart(new PixelPoint(0, 0), false, true);
        Assert.Equal(StartZoneAction.Start, t.PollStart(new PixelPoint(-1800, -1000), false, true));
        Assert.Equal(StartZoneAction.Stop, t.PollStart(new PixelPoint(0, 0), true, false));
    }

    [Fact]
    public void Null_zones_are_an_empty_list()
    {
        var t = new ZoneTracker();
        t.Configure(true, null);
        Assert.Equal(PauseChange.None, t.PollPause(Inside));
        Assert.Equal(StartZoneAction.None, t.PollStart(Inside, false, true));
    }
}
