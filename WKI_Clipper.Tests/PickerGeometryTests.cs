using System.Collections.Generic;
using WKI_Clipper.Models;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Picker overlays: physical virtual-screen pixels versus each monitor window's DIPs, drag rectangles, edits (design section 8).</summary>
public sealed class PickerGeometryTests
{
    // A 150 % monitor left of a 100 % primary, both 1080 p physical.
    private static readonly PickerMonitor Left150 = new(new PixelRect(-1920, 0, 1920, 1080), 1.5);
    private static readonly PickerMonitor Primary = new(new PixelRect(0, 0, 2560, 1440), 1.0);

    // ---- DIP <-> physical ----

    [Fact]
    public void Window_size_is_the_physical_monitor_size_in_the_monitors_dips()
    {
        Assert.Equal(new DipSize(1280, 720), PickerGeometry.WindowSize(Left150));
        Assert.Equal(new DipSize(2560, 1440), PickerGeometry.WindowSize(Primary));
    }

    [Fact]
    public void A_physical_point_maps_to_the_centre_of_its_pixel_in_local_dips()
    {
        var d = PickerGeometry.ToDip(Left150, new PixelPoint(-1920, 0));
        Assert.Equal(0.5 / 1.5, d.X, 9);
        Assert.Equal(0.5 / 1.5, d.Y, 9);

        var e = PickerGeometry.ToDip(Left150, new PixelPoint(-1, 1079));
        Assert.Equal(1919.5 / 1.5, e.X, 9);
        Assert.Equal(1079.5 / 1.5, e.Y, 9);
    }

    [Fact]
    public void Local_dips_map_back_to_the_same_physical_pixel()
    {
        foreach (var m in new[] { Left150, Primary, new PickerMonitor(new PixelRect(2560, -300, 3840, 2160), 1.75) })
            for (int x = m.Bounds.X; x < m.Bounds.Right; x += 37)
                for (int y = m.Bounds.Y; y < m.Bounds.Bottom; y += 41)
                {
                    var p = new PixelPoint(x, y);
                    Assert.Equal(p, PickerGeometry.ToPhysical(m, PickerGeometry.ToDip(m, p)));
                }
    }

    [Fact]
    public void A_dip_position_outside_the_window_still_maps_linearly()
    {
        // Mouse capture during a drag reports positions beyond the window.
        Assert.Equal(new PixelPoint(-1920 + 3000, -150), PickerGeometry.ToPhysical(Left150, new DipPoint(2000, -100)));
    }

    [Fact]
    public void A_physical_rect_maps_to_local_dips_with_its_size_scaled()
    {
        var r = PickerGeometry.ToDip(Left150, new PixelRect(-1620, 150, 300, 600));
        Assert.Equal(new DipRect(200, 100, 200, 400), r);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_broken_scale_counts_as_100_percent(double scale)
    {
        var m = new PickerMonitor(new PixelRect(100, 100, 800, 600), scale);
        Assert.Equal(new DipSize(800, 600), PickerGeometry.WindowSize(m));
        Assert.Equal(new PixelPoint(150, 120), PickerGeometry.ToPhysical(m, new DipPoint(50.2, 20.7)));
    }

    // ---- monitors ----

    [Fact]
    public void Monitor_lookup_uses_exclusive_far_edges()
    {
        var monitors = new List<PickerMonitor> { Left150, Primary };
        Assert.Equal(0, PickerGeometry.MonitorIndexAt(monitors, new PixelPoint(-1, 500)));
        Assert.Equal(1, PickerGeometry.MonitorIndexAt(monitors, new PixelPoint(0, 500)));
        Assert.Equal(-1, PickerGeometry.MonitorIndexAt(monitors, new PixelPoint(-100, 1200)));   // below the shorter left monitor
    }

    [Fact]
    public void Intersect_clips_a_rect_to_a_monitor_and_is_null_when_they_do_not_overlap()
    {
        var spanning = new PixelRect(-100, 50, 300, 100);
        Assert.Equal(new PixelRect(-100, 50, 100, 100), PickerGeometry.Intersect(spanning, Left150.Bounds));
        Assert.Equal(new PixelRect(0, 50, 200, 100), PickerGeometry.Intersect(spanning, Primary.Bounds));
        Assert.Null(PickerGeometry.Intersect(new PixelRect(-100, 1100, 50, 50), Left150.Bounds));
        Assert.Null(PickerGeometry.Intersect(new PixelRect(-50, 0, 50, 10), Primary.Bounds));   // touching edge only
    }

    // ---- drag rectangles ----

    [Fact]
    public void A_drag_covers_both_end_pixels_in_any_direction()
    {
        var expected = new PixelRect(-20, 10, 121, 51);
        Assert.Equal(expected, PickerGeometry.ZoneFromDrag(new PixelPoint(-20, 10), new PixelPoint(100, 60)));
        Assert.Equal(expected, PickerGeometry.ZoneFromDrag(new PixelPoint(100, 60), new PixelPoint(-20, 10)));
        Assert.Equal(expected, PickerGeometry.ZoneFromDrag(new PixelPoint(100, 10), new PixelPoint(-20, 60)));
    }

    [Fact]
    public void A_drag_smaller_than_the_minimum_is_a_click_not_a_zone()
    {
        int min = PickerGeometry.MinZoneSizePx;
        Assert.Null(PickerGeometry.ZoneFromDrag(new PixelPoint(5, 5), new PixelPoint(5, 5)));
        Assert.Null(PickerGeometry.ZoneFromDrag(new PixelPoint(0, 0), new PixelPoint(min - 2, 300)));
        Assert.Null(PickerGeometry.ZoneFromDrag(new PixelPoint(0, 0), new PixelPoint(300, min - 2)));
        Assert.Equal(new PixelRect(0, 0, min, min), PickerGeometry.ZoneFromDrag(new PixelPoint(0, 0), new PixelPoint(min - 1, min - 1)));
    }

    [Fact]
    public void A_drag_preview_never_vanishes_for_tiny_drags()
    {
        Assert.Equal(new PixelRect(5, 5, 1, 1), PickerGeometry.DragRect(new PixelPoint(5, 5), new PixelPoint(5, 5)));
        Assert.Equal(new PixelRect(3, 5, 3, 1), PickerGeometry.DragRect(new PixelPoint(5, 5), new PixelPoint(3, 5)));
    }

    [Fact]
    public void Drag_coordinates_are_clamped_to_the_settings_limit()
    {
        int lim = ClickerSettingsRules.CoordinateLimit;
        var r = PickerGeometry.ZoneFromDrag(new PixelPoint(-lim - 500, 0), new PixelPoint(lim + 500, 10))!.Value;
        Assert.Equal(-lim, r.X);
        Assert.Equal(lim, r.Right - 1);
    }

    // ---- chooser placement ----

    [Fact]
    public void The_chooser_sits_below_the_zone_and_stays_inside_the_window()
    {
        var win = new DipSize(1280, 720);
        var size = new DipSize(240, 40);
        Assert.Equal(new DipPoint(100, 308), PickerGeometry.ChooserPosition(new DipRect(100, 100, 300, 200), size, win));
        // No room below: above the zone.
        Assert.Equal(new DipPoint(100, 552), PickerGeometry.ChooserPosition(new DipRect(100, 600, 300, 110), size, win));
        // Right edge and a zone that fills the whole window: clamped inside.
        Assert.Equal(new DipPoint(1032, 8), PickerGeometry.ChooserPosition(new DipRect(1200, 0, 80, 720), size, win));
    }

    // ---- edits ----

    [Fact]
    public void Adding_points_uses_the_defaults_and_stops_at_the_cap()
    {
        var points = new List<ClickPoint>();
        Assert.True(PickerEdits.TryAddPoint(points, new PixelPoint(-5, 7)));
        Assert.Equal(-5, points[0].X);
        Assert.Equal(7, points[0].Y);
        Assert.Equal(1, points[0].Clicks);
        Assert.Equal(0, points[0].Radius);

        while (points.Count < ClickerSettingsRules.MaxClickPoints) points.Add(new ClickPoint());
        Assert.False(PickerEdits.TryAddPoint(points, new PixelPoint(1, 1)));
        Assert.Equal(ClickerSettingsRules.MaxClickPoints, points.Count);
    }

    [Fact]
    public void Removing_the_last_point_reports_whether_there_was_one()
    {
        var points = new List<ClickPoint> { new() { X = 1 }, new() { X = 2 } };
        Assert.True(PickerEdits.RemoveLastPoint(points));
        Assert.Single(points);
        Assert.Equal(1, points[0].X);
        Assert.True(PickerEdits.RemoveLastPoint(points));
        Assert.False(PickerEdits.RemoveLastPoint(points));
    }

    [Fact]
    public void A_new_zone_is_a_named_enabled_stop_zone_with_its_own_id()
    {
        var zones = new List<ClickerZone> { new() { Id = "a", Name = "Zone 1" }, new() { Id = "b", Name = "Zone 3" } };
        var z = PickerEdits.TryAddZone(zones, new PixelRect(-10, 20, 30, 40));
        Assert.NotNull(z);
        Assert.Same(z, zones[2]);
        Assert.Equal("Zone 2", z!.Name);
        Assert.Equal(ZoneAction.Stop, z.Action);
        Assert.True(z.Enabled);
        Assert.Equal((-10, 20, 30, 40), (z.X, z.Y, z.W, z.H));
        Assert.False(string.IsNullOrEmpty(z.Id));
        Assert.NotEqual(z.Id, PickerEdits.TryAddZone(zones, new PixelRect(0, 0, 10, 10))!.Id);
        Assert.Equal("Zone 4", zones[3].Name);
    }

    [Fact]
    public void Zones_stop_at_the_cap()
    {
        var zones = new List<ClickerZone>();
        while (zones.Count < ClickerSettingsRules.MaxZones) zones.Add(new ClickerZone { Name = "x" });
        Assert.Null(PickerEdits.TryAddZone(zones, new PixelRect(0, 0, 10, 10)));
    }

    [Fact]
    public void Copies_are_deep_so_a_cancelled_edit_cannot_touch_the_settings()
    {
        var points = new List<ClickPoint> { new() { X = 1, Y = 2, Clicks = 3, Radius = 4 } };
        var copy = PickerEdits.Copy(points);
        copy[0].X = 99;
        Assert.Equal(1, points[0].X);
        Assert.Equal((1, 2, 3, 4), (PickerEdits.Copy(points)[0].X, copy[0].Y, copy[0].Clicks, copy[0].Radius));

        var zones = new List<ClickerZone> { new() { Id = "z", Name = "Z", X = 1, Y = 2, W = 3, H = 4, Action = ZoneAction.Pause, Enabled = false } };
        var zc = PickerEdits.Copy(zones);
        zc[0].Name = "changed";
        Assert.Equal("Z", zones[0].Name);
        Assert.Equal(("z", ZoneAction.Pause, false, 4), (zc[0].Id, zc[0].Action, zc[0].Enabled, zc[0].H));
    }
}
