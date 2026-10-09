using System.Collections.Generic;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>v0.13: grid/snapping and scale-independent placement of the widget board.</summary>
public sealed class WidgetGridTests
{
    // A 3440x1440 monitor with the taskbar: work area 3440x1392, all in physical pixels.
    private static readonly LayoutRect Area = new(0, 0, 3440, 1392);
    private const double Grid = 16, Gap = 8, Snap = 14;

    [Fact]
    public void Touching_at_exactly_one_gap_is_not_overlapping()
    {
        var a = new LayoutRect(100, 100, 200, 100);
        Assert.False(a.Overlaps(new LayoutRect(308, 100, 50, 50), Gap));   // 8 px apart
        Assert.True(a.Overlaps(new LayoutRect(305, 100, 50, 50), Gap));    // only 5 px apart
        Assert.False(a.Overlaps(new LayoutRect(300, 100, 50, 50)));        // edge to edge, no gap asked
    }

    // ---- scale-independent placement ----

    [Fact]
    public void A_top_right_widget_stays_top_right_when_scaling_goes_from_100_to_150_percent()
    {
        const double margin = 8;
        var at100 = new LayoutRect(Area.Right - margin - 400, margin, 400, 300);
        var (rx, ry) = WidgetLayout.ToRelative(at100, Area);

        // Same DIP size, now 1.5 times the pixels; margin scales too.
        var (x, y) = WidgetLayout.FromRelative(rx, ry, 600, 450, Area, margin * 1.5);

        Assert.Equal(Area.Right - 12 - 600, x);   // still flush right
        Assert.Equal(12, y);                      // still at the top
    }

    [Fact]
    public void A_centered_widget_stays_centered_at_any_size()
    {
        var at100 = new LayoutRect(1520, 546, 400, 300);   // center 1720/696 = middle of the area
        var (rx, ry) = WidgetLayout.ToRelative(at100, Area);
        var (x, y) = WidgetLayout.FromRelative(rx, ry, 600, 450, Area, 12);
        Assert.Equal(1720, x + 300, 3);
        Assert.Equal(696, y + 225, 3);
    }

    [Fact]
    public void A_window_larger_than_the_screen_keeps_its_title_bar_reachable()
    {
        var (x, y) = WidgetLayout.FromRelative(0.9, 0.9, 5000, 3000, Area, 8);
        Assert.Equal(8, x);
        Assert.Equal(8, y);
    }

    // ---- live snapping ----

    [Fact]
    public void Dragging_near_a_neighbour_snaps_one_gap_beside_it()
    {
        var neighbour = new LayoutRect(100, 100, 300, 200);
        var dragged = new LayoutRect(400 + Gap + 5, 103, 200, 100);   // 5 px too far right, 3 px low

        var (x, y) = WidgetLayout.SnapMove(dragged, new[] { neighbour }, Area, Grid, Snap, Gap);

        Assert.Equal(408, x);   // neighbour.Right + gap
        Assert.Equal(100, y);   // tops aligned
    }

    [Fact]
    public void Dragging_near_the_screen_edge_snaps_to_the_margin()
    {
        var dragged = new LayoutRect(Area.Right - Gap - 200 - 6, 500, 200, 100);
        var (x, _) = WidgetLayout.SnapMove(dragged, new List<LayoutRect>(), Area, Grid, Snap, Gap);
        Assert.Equal(Area.Right - Gap - 200, x);
    }

    [Fact]
    public void Far_from_everything_the_window_moves_in_grid_steps()
    {
        var dragged = new LayoutRect(173, 501, 200, 100);
        var (x, y) = WidgetLayout.SnapMove(dragged, new List<LayoutRect>(), Area, Grid, Snap, Gap);
        Assert.Equal(168, x);   // 8 + 10·16
        Assert.Equal(504, y);   // 8 + 31·16
    }

    [Fact]
    public void Resizing_towards_a_neighbour_stops_one_gap_before_it()
    {
        var neighbour = new LayoutRect(600, 100, 300, 300);
        var growing = new LayoutRect(100, 100, 600 - Gap - 100 - 4, 200);   // right edge 4 px short

        var (w, _) = WidgetLayout.SnapResize(growing, new[] { neighbour }, Area, Grid, Snap, Gap, 220, 140);

        Assert.Equal(600 - Gap, 100 + w);
    }

    [Fact]
    public void Resizing_never_goes_below_the_minimum()
    {
        var r = new LayoutRect(100, 100, 50, 30);
        var (w, h) = WidgetLayout.SnapResize(r, new List<LayoutRect>(), Area, Grid, Snap, Gap, 220, 140);
        Assert.Equal(220, w);
        Assert.Equal(140, h);
    }

    [Fact]
    public void Grid_lengths_end_on_the_gutter()
    {
        Assert.Equal(392, WidgetLayout.SnapLength(395, Grid, Gap, 100));   // 25·16 − 8
        Assert.Equal(220, WidgetLayout.SnapLength(150, Grid, Gap, 220));   // minimum wins
    }

    // ---- free spot / arrange ----

    [Fact]
    public void A_free_spot_is_kept_as_it_is()
    {
        var desired = new LayoutRect(1000, 500, 300, 200);
        var spot = WidgetLayout.FindFreeSpot(desired, new[] { new LayoutRect(100, 100, 300, 200) }, Area, Gap);
        Assert.Equal((1000.0, 500.0), spot);
    }

    [Fact]
    public void An_overlapping_drop_moves_to_the_nearest_free_spot()
    {
        var obstacle = new LayoutRect(1000, 500, 400, 300);
        var desired = new LayoutRect(1350, 550, 300, 200);   // overlaps the obstacle's right part

        var spot = WidgetLayout.FindFreeSpot(desired, new[] { obstacle }, Area, Gap);

        Assert.NotNull(spot);
        var placed = desired.At(spot!.Value.X, spot.Value.Y);
        Assert.False(placed.Overlaps(obstacle, Gap));
        Assert.Equal(1408, placed.X);   // shortest way out: to the right, one gap beside it
        Assert.Equal(550, placed.Y);
    }

    [Fact]
    public void A_full_screen_has_no_free_spot()
    {
        var everything = new LayoutRect(0, 0, 3440, 1392);
        Assert.Null(WidgetLayout.FindFreeSpot(new LayoutRect(100, 100, 300, 200), new[] { everything }, Area, Gap));
        Assert.Null(WidgetLayout.FindFreeSpot(new LayoutRect(0, 0, 5000, 200), new List<LayoutRect>(), Area, Gap));
    }

    [Fact]
    public void Arranging_removes_every_overlap_and_keeps_everything_on_screen()
    {
        var windows = new[]
        {
            new LayoutRect(40, 40, 360, 430),
            new LayoutRect(84, 84, 380, 500),     // the old stagger: heavy overlap
            new LayoutRect(128, 128, 520, 560),
            new LayoutRect(172, 172, 320, 300),
            new LayoutRect(3300, 1300, 400, 300), // partly off-screen
        };

        var result = WidgetLayout.Arrange(windows, Area, Grid, Gap, 220, 140, snapToGrid: false);

        Assert.Equal(windows[0], result[0]);   // the top-left one keeps its place
        for (int i = 0; i < result.Length; i++)
        {
            Assert.True(result[i].X >= Area.X + Gap - 0.01 && result[i].Right <= Area.Right - Gap + 0.01);
            Assert.True(result[i].Y >= Area.Y + Gap - 0.01 && result[i].Bottom <= Area.Bottom - Gap + 0.01);
            for (int j = i + 1; j < result.Length; j++)
                Assert.False(result[i].Overlaps(result[j], Gap), $"{i} overlaps {j}");
        }
    }

    [Fact]
    public void Arranging_on_the_grid_aligns_starts_and_sizes()
    {
        var windows = new[] { new LayoutRect(45, 47, 359, 431), new LayoutRect(600, 60, 300, 200) };

        var result = WidgetLayout.Arrange(windows, Area, Grid, Gap, 220, 140, snapToGrid: true);

        foreach (var r in result)
        {
            Assert.Equal(0, (r.X - Gap) % Grid);
            Assert.Equal(0, (r.Y - Gap) % Grid);
            Assert.Equal(0, (r.W + Gap) % Grid);
            Assert.Equal(0, (r.H + Gap) % Grid);
        }
    }

    [Fact]
    public void With_stacking_allowed_overlaps_stay_and_only_the_grid_applies()
    {
        var bottom = new LayoutRect(104, 104, 392, 424);
        var onTop = new LayoutRect(105, 103, 392, 424);   // deliberately stacked on it

        var result = WidgetLayout.Arrange(new[] { bottom, onTop }, Area, Grid, Gap, 220, 140,
                                          snapToGrid: true, resolveOverlaps: false);

        Assert.True(result[0].Overlaps(result[1], Gap));   // still stacked
        Assert.Equal(result[0].X, result[1].X);             // and now exactly on top of each other
        Assert.Equal(result[0].Y, result[1].Y);
    }

    [Fact]
    public void Fixed_obstacles_are_avoided_but_never_moved()
    {
        var launcher = new LayoutRect(1400, 12, 640, 48);
        var under = new LayoutRect(1500, 20, 300, 200);   // sits under the launcher pill

        var result = WidgetLayout.Arrange(new[] { under }, Area, Grid, Gap, 220, 140, snapToGrid: false,
                                          fixedObstacles: new[] { launcher });

        Assert.False(result[0].Overlaps(launcher, Gap));
    }
}
