using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Where the performance overlay sits: eight fixed anchors and a free, dragged-to place. All maths in one unit (the
/// host passes physical pixels); nothing here touches WPF.
/// </summary>
public sealed class PerfOverlayPlacementTests : IDisposable
{
    private static readonly MonitorBox Main = new(@"\\.\DISPLAY1", new LayoutRect(0, 0, 2560, 1440), Primary: true);
    private static readonly MonitorBox Side = new(@"\\.\DISPLAY2", new LayoutRect(2560, 0, 1920, 1080), Primary: false);
    private static readonly MonitorBox[] Both = { Side, Main };   // the primary is not first on purpose

    private const double W = 200, H = 100, Margin = 10;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_overlay_" + Guid.NewGuid().ToString("N"));

    public PerfOverlayPlacementTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static LayoutRect PlaceOn(MonitorBox[] monitors, OverlayAnchor a, OverlayFreePlace? free = null, double w = W, double h = H)
        => PerfOverlayPlacement.Place(a, free, w, h, monitors, preferredMonitor: null, Margin);

    private static void AssertInside(LayoutRect r, LayoutRect area)
    {
        Assert.True(r.X >= area.X - 0.001, $"left {r.X} < {area.X}");
        Assert.True(r.Y >= area.Y - 0.001, $"top {r.Y} < {area.Y}");
        Assert.True(r.Right <= area.Right + 0.001, $"right {r.Right} > {area.Right}");
        Assert.True(r.Bottom <= area.Bottom + 0.001, $"bottom {r.Bottom} > {area.Bottom}");
    }

    // ---- the four old corners keep meaning the same place ----

    [Theory]
    [InlineData("TopLeft", OverlayAnchor.TopLeft)]
    [InlineData("TopRight", OverlayAnchor.TopRight)]
    [InlineData("BottomLeft", OverlayAnchor.BottomLeft)]
    [InlineData("BottomRight", OverlayAnchor.BottomRight)]
    public void Old_corner_values_migrate_to_same_anchor(string oldCorner, OverlayAnchor expected)
    {
        WriteOldSettingsFile(oldCorner);

        var loaded = new SettingsService(_dir).Load();

        Assert.Equal(expected, loaded.PerfOverlay.Anchor);
        Assert.Null(loaded.PerfOverlay.Free);
        // the other overlay settings of that file are untouched
        Assert.Equal(1.5, loaded.PerfOverlay.Scale);
        Assert.True(loaded.PerfOverlay.Enabled);
    }

    [Fact]
    public void A_migrated_file_is_written_back_with_the_anchor_and_without_the_old_corner()
    {
        WriteOldSettingsFile("BottomLeft");
        var service = new SettingsService(_dir);
        service.Load();

        service.Save();

        var overlay = JsonNode.Parse(File.ReadAllText(service.SettingsFilePath))!["PerfOverlay"]!.AsObject();
        Assert.Equal("BottomLeft", (string?)overlay["Anchor"]);
        Assert.False(overlay.ContainsKey("Corner"));
        Assert.Equal(OverlayAnchor.BottomLeft, new SettingsService(_dir).Load().PerfOverlay.Anchor);
    }

    [Fact]
    public void A_new_file_without_any_overlay_position_starts_top_right_like_before()
    {
        var s = new PerfOverlaySettings();
        Assert.Equal(OverlayAnchor.TopRight, s.Anchor);
        Assert.Null(s.Free);
    }

    /// <summary>A settings.json exactly as 0.16 wrote it: the overlay position is the "Corner" key, no "Anchor", no "Free".</summary>
    private void WriteOldSettingsFile(string corner)
    {
        var seed = new SettingsService(_dir);
        seed.Current.SchemaVersion = SettingsService.CurrentSchemaVersion;
        seed.Current.Output.ClipsFolder = Path.Combine(_dir, "clips");
        seed.Current.Output.ScreenshotsFolder = Path.Combine(_dir, "shots");
        seed.Current.Output.BufferFolder = Path.Combine(_dir, "buffer");
        seed.Current.PerfOverlay.Enabled = true;
        seed.Current.PerfOverlay.Scale = 1.5;
        seed.Save();

        var root = JsonNode.Parse(File.ReadAllText(seed.SettingsFilePath))!.AsObject();
        var overlay = root["PerfOverlay"]!.AsObject();
        overlay.Remove("Anchor");
        overlay.Remove("Free");
        overlay["Corner"] = corner;
        File.WriteAllText(seed.SettingsFilePath, root.ToJsonString());
        if (File.Exists(seed.BackupFilePath)) File.Delete(seed.BackupFilePath);
    }

    // ---- the eight fixed anchors ----

    [Theory]
    [InlineData(OverlayAnchor.TopLeft, 10, 10)]
    [InlineData(OverlayAnchor.TopCenter, 1180, 10)]
    [InlineData(OverlayAnchor.TopRight, 2560 - 10 - 200, 10)]
    [InlineData(OverlayAnchor.MiddleLeft, 10, 670)]
    [InlineData(OverlayAnchor.MiddleRight, 2560 - 10 - 200, 670)]
    [InlineData(OverlayAnchor.BottomLeft, 10, 1440 - 10 - 100)]
    [InlineData(OverlayAnchor.BottomCenter, 1180, 1440 - 10 - 100)]
    [InlineData(OverlayAnchor.BottomRight, 2560 - 10 - 200, 1440 - 10 - 100)]
    public void Each_fixed_anchor_places_with_margin(OverlayAnchor anchor, double x, double y)
        => Assert.Equal(new LayoutRect(x, y, W, H), PlaceOn(Both, anchor));

    [Fact]
    public void A_fixed_anchor_sits_on_the_primary_monitor_unless_another_is_preferred()
    {
        Assert.Equal(new LayoutRect(2350, 10, W, H), PlaceOn(Both, OverlayAnchor.TopRight));

        var onSide = PerfOverlayPlacement.Place(OverlayAnchor.TopRight, null, W, H, Both, Side.Device, Margin);
        Assert.Equal(new LayoutRect(2560 + 1920 - 10 - 200, 10, W, H), onSide);

        var gone = PerfOverlayPlacement.Place(OverlayAnchor.TopRight, null, W, H, Both, @"\\.\DISPLAY9", Margin);
        Assert.Equal(new LayoutRect(2350, 10, W, H), gone);   // the preferred monitor is unplugged: primary
    }

    [Fact]
    public void An_overlay_larger_than_the_monitor_keeps_its_top_left_visible()
    {
        var r = PlaceOn(new[] { Main }, OverlayAnchor.BottomRight, w: 3000, h: 2000);
        Assert.Equal((0.0, 0.0), (r.X, r.Y));
    }

    [Fact]
    public void An_unknown_anchor_value_and_a_free_anchor_without_a_place_fall_back_to_top_right()
    {
        var topRight = PlaceOn(Both, OverlayAnchor.TopRight);
        Assert.Equal(topRight, PlaceOn(Both, (OverlayAnchor)99));
        Assert.Equal(topRight, PlaceOn(Both, OverlayAnchor.Free, free: null));
    }

    // ---- dropping the overlay ----

    [Fact]
    public void Drop_near_top_center_snaps_to_TopCenter()
    {
        // TopCenter is at (1180, 10); this is 18 px away
        var (anchor, free) = PerfOverlayPlacement.FromDrop(new LayoutRect(1190, 25, W, H), Main, snap: 24, Margin);

        Assert.Equal(OverlayAnchor.TopCenter, anchor);
        Assert.Null(free);
    }

    [Fact]
    public void Drop_just_outside_the_snap_distance_stays_free()
    {
        var (anchor, free) = PerfOverlayPlacement.FromDrop(new LayoutRect(1180, 10 + 25, W, H), Main, snap: 24, Margin);

        Assert.Equal(OverlayAnchor.Free, anchor);
        Assert.NotNull(free);
    }

    [Fact]
    public void Drop_snaps_to_the_nearest_of_several_anchors_in_reach()
    {
        // A tiny monitor with a huge snap distance: TopCenter (150,10) and TopRight (290,10) are both in reach.
        var tiny = new MonitorBox("T", new LayoutRect(0, 0, 400, 300), true);
        var (anchor, _) = PerfOverlayPlacement.FromDrop(new LayoutRect(170, 10, 100, 50), tiny, snap: 200, Margin);
        Assert.Equal(OverlayAnchor.TopCenter, anchor);
    }

    [Fact]
    public void Drop_in_open_area_becomes_free_with_nearest_third_edges()
    {
        // centre (500, 1150): left third of 2560, bottom third of 1440
        var (anchor, free) = PerfOverlayPlacement.FromDrop(new LayoutRect(400, 1100, W, H), Main, snap: 24, Margin);

        Assert.Equal(OverlayAnchor.Free, anchor);
        Assert.NotNull(free);
        Assert.Equal(Main.Device, free!.Monitor);
        Assert.Equal(0, free.HEdge);                                      // left
        Assert.Equal(2, free.VEdge);                                      // bottom
        Assert.Equal(400.0 / 2560, free.RelDX, 6);                        // left edge to the monitor's left edge
        Assert.Equal((1440.0 - 1200) / 1440, free.RelDY, 6);              // bottom edge to the monitor's bottom edge

        // centre (1500, 600): middle third / middle third; right third in x for the next one
        var (_, mid) = PerfOverlayPlacement.FromDrop(new LayoutRect(1400, 550, W, H), Main, snap: 24, Margin);
        Assert.Equal((1, 1), (mid!.HEdge, mid.VEdge));
        Assert.Equal((1500.0 - 1280) / 2560, mid.RelDX, 6);               // centre to the monitor's centre
        Assert.Equal((600.0 - 720) / 1440, mid.RelDY, 6);

        var (_, right) = PerfOverlayPlacement.FromDrop(new LayoutRect(2000, 300, W, H), Main, snap: 24, Margin);
        Assert.Equal((2, 0), (right!.HEdge, right.VEdge));
        Assert.Equal((2560.0 - 2200) / 2560, right.RelDX, 6);             // right edge to the monitor's right edge
        Assert.Equal(300.0 / 1440, right.RelDY, 6);
    }

    [Fact]
    public void Drop_on_the_second_monitor_stores_that_monitor_and_measures_from_it()
    {
        var (anchor, free) = PerfOverlayPlacement.FromDrop(new LayoutRect(2560 + 300, 400, W, H), Side, snap: 24, Margin);

        Assert.Equal(OverlayAnchor.Free, anchor);
        Assert.Equal(Side.Device, free!.Monitor);
        Assert.Equal(300.0 / 1920, free.RelDX, 6);
    }

    [Theory]
    [InlineData(400, 100)]      // left / top
    [InlineData(1300, 500)]     // middle / middle
    [InlineData(2300, 1250)]    // right / bottom
    [InlineData(1000, 1300)]
    public void A_dropped_free_place_comes_back_at_the_dropped_position(double x, double y)
    {
        var dropped = new LayoutRect(x, y, W, H);
        var (anchor, free) = PerfOverlayPlacement.FromDrop(dropped, Main, snap: 24, Margin);
        Assert.Equal(OverlayAnchor.Free, anchor);

        var back = PlaceOn(Both, OverlayAnchor.Free, free);

        Assert.Equal(dropped.X, back.X, 3);
        Assert.Equal(dropped.Y, back.Y, 3);
        Assert.Equal((W, H), (back.W, back.H));
    }

    [Fact]
    public void A_drop_half_off_the_monitor_is_stored_inside_it()
    {
        var (_, free) = PerfOverlayPlacement.FromDrop(new LayoutRect(2500, 1400, W, H), Main, snap: 24, Margin);
        var back = PlaceOn(Both, OverlayAnchor.Free, free);
        AssertInside(back, Main.WorkArea);
        Assert.True(free!.RelDX >= 0 && free.RelDY >= 0, "no negative distance from an edge");
    }

    // ---- free place: edges, growth, vanished monitors, resolution changes ----

    [Fact]
    public void Free_place_keeps_right_edge_when_overlay_grows()
    {
        var free = new OverlayFreePlace { Monitor = Main.Device, HEdge = 2, VEdge = 0, RelDX = 0.05, RelDY = 0.1 };

        var small = PlaceOn(Both, OverlayAnchor.Free, free, w: 200, h: 100);
        var grown = PlaceOn(Both, OverlayAnchor.Free, free, w: 320, h: 100);

        Assert.Equal(small.Right, grown.Right, 3);                   // the anchored edge stays
        Assert.Equal(small.X - 120, grown.X, 3);                     // growth goes to the left
        Assert.Equal(small.Y, grown.Y, 3);
        Assert.Equal(2560 - 0.05 * 2560, small.Right, 3);
    }

    [Fact]
    public void Free_place_keeps_bottom_edge_and_grows_a_centred_overlay_both_ways()
    {
        var bottom = new OverlayFreePlace { Monitor = Main.Device, HEdge = 0, VEdge = 2, RelDX = 0.1, RelDY = 0.05 };
        var a = PlaceOn(Both, OverlayAnchor.Free, bottom, w: 200, h: 100);
        var b = PlaceOn(Both, OverlayAnchor.Free, bottom, w: 200, h: 180);
        Assert.Equal(a.Bottom, b.Bottom, 3);
        Assert.Equal(a.X, b.X, 3);

        var centre = new OverlayFreePlace { Monitor = Main.Device, HEdge = 1, VEdge = 1, RelDX = 0.1, RelDY = -0.1 };
        var c = PlaceOn(Both, OverlayAnchor.Free, centre, w: 200, h: 100);
        var d = PlaceOn(Both, OverlayAnchor.Free, centre, w: 300, h: 140);
        Assert.Equal(c.X + c.W / 2, d.X + d.W / 2, 3);
        Assert.Equal(c.Y + c.H / 2, d.Y + d.H / 2, 3);
        Assert.Equal(1280 + 0.1 * 2560, c.X + c.W / 2, 3);
    }

    [Fact]
    public void Free_overlay_on_missing_monitor_lands_on_primary_visible()
    {
        // saved on the right-hand monitor, near its bottom-right; that monitor is unplugged now
        var free = new OverlayFreePlace { Monitor = @"\\.\DISPLAY7", HEdge = 2, VEdge = 2, RelDX = 0.02, RelDY = 0.03 };

        var r = PlaceOn(new[] { Main }, OverlayAnchor.Free, free);

        AssertInside(r, Main.WorkArea);
        Assert.Equal(2560 - 0.02 * 2560, r.Right, 3);              // same proportions, measured on the primary

        // and with a second monitor still present, the primary is the fallback - not the other one
        var withSide = PlaceOn(Both, OverlayAnchor.Free, free);
        AssertInside(withSide, Main.WorkArea);
    }

    [Fact]
    public void Free_overlay_is_clamped_after_resolution_change()
    {
        // left edge at 95 % of the width: fine on the old 3440 px monitor, but the overlay is wider than the 5 % left
        var free = new OverlayFreePlace { Monitor = Main.Device, HEdge = 0, VEdge = 0, RelDX = 0.95, RelDY = 0.95 };
        var smaller = new MonitorBox(Main.Device, new LayoutRect(0, 0, 1280, 720), true);

        var r = PlaceOn(new[] { smaller }, OverlayAnchor.Free, free);

        AssertInside(r, smaller.WorkArea);
        Assert.Equal(1280 - W, r.X, 3);
        Assert.Equal(720 - H, r.Y, 3);

        // a monitor now so small that the overlay does not fit: its top-left (the first line) stays visible
        var tiny = new MonitorBox(Main.Device, new LayoutRect(100, 50, 150, 80), true);
        var big = PlaceOn(new[] { tiny }, OverlayAnchor.Free, free);
        Assert.Equal((100.0, 50.0), (big.X, big.Y));
    }

    [Fact]
    public void Garbage_in_a_free_place_cannot_push_the_overlay_off_screen()
    {
        var free = new OverlayFreePlace { Monitor = Main.Device, HEdge = 7, VEdge = -3, RelDX = double.NaN, RelDY = double.PositiveInfinity };
        AssertInside(PlaceOn(Both, OverlayAnchor.Free, free), Main.WorkArea);
    }

    [Fact]
    public void No_monitor_at_all_still_gives_a_rectangle()
    {
        var r = PerfOverlayPlacement.Place(OverlayAnchor.TopRight, null, W, H, Array.Empty<MonitorBox>(), null, Margin);
        Assert.Equal((W, H), (r.W, r.H));
    }

    [Fact]
    public void The_monitor_under_a_rectangle_is_the_one_holding_its_centre()
    {
        Assert.Equal(Side, PerfOverlayPlacement.MonitorAt(new LayoutRect(2600, 100, W, H), Both));
        Assert.Equal(Main, PerfOverlayPlacement.MonitorAt(new LayoutRect(100, 100, W, H), Both));
        // in no monitor (the gap of a diagonal layout, or a stale rectangle): the nearest one
        Assert.Equal(Side, PerfOverlayPlacement.MonitorAt(new LayoutRect(4700, 100, W, H), Both));
        Assert.Null(PerfOverlayPlacement.MonitorAt(new LayoutRect(0, 0, W, H), Array.Empty<MonitorBox>()));
    }

    [Fact]
    public void The_old_four_corner_function_still_gives_the_same_corners()
    {
        var area = new LayoutRect(0, 0, 3440, 1440);
        Assert.Equal((10.0, 10.0), PerfOverlayLayout.Place(OverlayAnchor.TopLeft, 200, 100, area, 10));
        Assert.Equal((3230.0, 1330.0), PerfOverlayLayout.Place(OverlayAnchor.BottomRight, 200, 100, area, 10));
    }

    [Fact]
    public void Every_anchor_but_free_has_a_place()
    {
        foreach (var a in Enum.GetValues<OverlayAnchor>().Where(a => a != OverlayAnchor.Free))
            AssertInside(PlaceOn(Both, a), Main.WorkArea);
    }
}
