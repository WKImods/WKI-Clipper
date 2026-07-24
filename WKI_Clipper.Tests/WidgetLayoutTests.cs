using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class WidgetLayoutTests
{
    // A 1920x1080 work area at origin.
    private const double L = 0, T = 0, R = 1920, B = 1080;

    [Fact]
    public void On_screen_position_is_unchanged()
    {
        var (x, y) = WidgetLayout.Clamp(100, 100, 400, 300, L, T, R, B);
        Assert.Equal(100, x);
        Assert.Equal(100, y);
    }

    [Fact]
    public void Off_screen_right_and_bottom_is_pulled_back()
    {
        var (x, y) = WidgetLayout.Clamp(1900, 1050, 400, 300, L, T, R, B);
        Assert.True(x + 400 <= R);
        Assert.True(y + 300 <= B);
    }

    [Fact]
    public void Off_screen_negative_is_pulled_to_margin()
    {
        var (x, y) = WidgetLayout.Clamp(-500, -500, 400, 300, L, T, R, B, margin: 8);
        Assert.Equal(8, x);
        Assert.Equal(8, y);
    }

    [Fact]
    public void Widget_larger_than_area_pins_to_top_left_margin()
    {
        var (x, y) = WidgetLayout.Clamp(500, 500, 5000, 5000, L, T, R, B, margin: 8);
        Assert.Equal(8, x);
        Assert.Equal(8, y);
    }

    [Fact]
    public void Position_on_secondary_monitor_offset_is_respected()
    {
        // Second monitor to the right: 1920..3840
        var (x, y) = WidgetLayout.Clamp(2000, 100, 400, 300, 1920, 0, 3840, 1080);
        Assert.Equal(2000, x);
        Assert.Equal(100, y);
    }

    // --- crosshair snap-to-grid (anchored at the monitor center) ---

    [Fact]
    public void Snap_keeps_the_exact_monitor_center_reachable()
    {
        // 3440x1440 → center 1720,720. A point a few px off must land exactly on it.
        var (x, y) = WidgetLayout.SnapToGrid(1728, 713, 1720, 720, 25);
        Assert.Equal(1720, x);
        Assert.Equal(720, y);
    }

    [Fact]
    public void Snap_moves_to_the_nearest_grid_step_from_the_center()
    {
        var (x, y) = WidgetLayout.SnapToGrid(1758, 668, 1720, 720, 25);
        Assert.Equal(1770, x);   // +38px → 1.52 steps → rounds to 2 → 1720 + 2*25
        Assert.Equal(670, y);    // -52px → -2.08 steps → rounds to -2 → 720 - 2*25
    }

    [Fact]
    public void Snap_offsets_stay_symmetric_around_the_center()
    {
        var (left, _)  = WidgetLayout.SnapToGrid(1720 - 30, 720, 1720, 720, 25);
        var (right, _) = WidgetLayout.SnapToGrid(1720 + 30, 720, 1720, 720, 25);
        Assert.Equal(1720 - 25, left);
        Assert.Equal(1720 + 25, right);
    }

    [Fact]
    public void Snap_with_zero_or_negative_grid_is_a_noop()
    {
        Assert.Equal((1733.0, 707.0), WidgetLayout.SnapToGrid(1733, 707, 1720, 720, 0));
        Assert.Equal((1733.0, 707.0), WidgetLayout.SnapToGrid(1733, 707, 1720, 720, -5));
    }

    [Fact]
    public void Snap_works_on_a_secondary_monitor_anchor()
    {
        // Second monitor 1920..3840 → center 2880,540
        var (x, y) = WidgetLayout.SnapToGrid(2903, 517, 2880, 540, 25);
        Assert.Equal(2905, x);
        Assert.Equal(515, y);
    }

    [Theory]
    [InlineData(400, 300, true)]
    [InlineData(120, 80, true)]
    [InlineData(50, 300, false)]
    [InlineData(400, 40, false)]
    public void HasValidGeometry_checks_minimum_size(double w, double h, bool expected)
        => Assert.Equal(expected, WidgetLayout.HasValidGeometry(w, h));
}
