using System;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Display scaling 100 % → 175 % → 100 %: the widgets of the board must come back exactly where
/// they were. A place is stored as the window centre relative to the work area; the only way to
/// break the round trip is to save a rectangle the APP chose (sidebar clearance, clamping) as if
/// the user had chosen it. Real layout of the reported board, 3440x1440 ultrawide.
/// </summary>
public sealed class ScalingRoundTripTests
{
    // Work area = monitor minus taskbar; the taskbar is taller at 175 %.
    private static readonly LayoutRect Area100 = new(0, 0, 3440, 1392);
    private static readonly LayoutRect Area175 = new(0, 0, 3440, 1356);

    // Same constants the host uses.
    private const double EdgeMarginDip = WidgetPlacementPlanner.EdgeMarginDip;

    /// <summary>
    /// What WidgetHost does when it puts a widget on a monitor, through the planner it runs:
    /// the stored centre fraction, the size in DIPs scaled to pixels, clamped onto the screen,
    /// then moved clear of the sidebar.
    /// </summary>
    private static LayoutRect Place(double relX, double relY, double dipW, double dipH, double scale, LayoutRect area)
    {
        var planner = new WidgetPlacementPlanner();
        var st = new WidgetState { Id = WidgetId.Audio, RelX = relX, RelY = relY, Width = dipW, Height = dipH };
        var rect = planner.Restore(st, dipW, dipH, index: 0, legacy: false, area, scale);

        var sidebar = WidgetLayout.SidebarRect(area, scale, collapsed: false);
        foreach (var (_, moved) in planner.ClearOfSidebar(new[] { (st.Id, rect) }, sidebar, area, scale))
            rect = moved;
        return rect;
    }

    // The reported layout at 100 %: physical pixels, sizes in DIPs are the same numbers.
    public static TheoryData<WidgetId, double, double, double, double> Layout => new()
    {
        { WidgetId.Audio,       304,   72, 432, 552 },
        { WidgetId.Gallery,     744,   72, 872, 552 },
        { WidgetId.Performance, 304,  632, 432, 424 },
        { WidgetId.Spotify,     304, 1176, 416, 200 },
        { WidgetId.Crosshair,   744,  632, 360, 744 },
        { WidgetId.Music,      1112,  632, 504, 408 },
        { WidgetId.Capture,    1624,   72, 472, 552 },
        { WidgetId.WhatsApp,   2104,   72, 952, 824 },
    };

    public static TheoryData<WidgetId, double, double, double, double> OverlappingTheSidebarAt175 => new()
    {
        { WidgetId.Audio,       304,   72, 432, 552 },
        { WidgetId.Gallery,     744,   72, 872, 552 },
        { WidgetId.Performance, 304,  632, 432, 424 },
        { WidgetId.Spotify,     304, 1176, 416, 200 },
    };

    // ---- sidebar math ----

    [Fact]
    public void The_sidebar_ends_at_296_px_at_100_percent_and_518_px_at_175_percent()
    {
        Assert.Equal(296, WidgetLayout.SidebarRect(Area100, 1.0, collapsed: false).Right, 6);
        Assert.Equal(518, WidgetLayout.SidebarRect(Area175, 1.75, collapsed: false).Right, 6);
    }

    [Fact]
    public void The_collapsed_sidebar_is_a_narrow_rail()
    {
        Assert.Equal(88, WidgetLayout.SidebarRect(Area100, 1.0, collapsed: true).Right, 6);
        Assert.Equal(154, WidgetLayout.SidebarRect(Area175, 1.75, collapsed: true).Right, 6);
    }

    [Fact]
    public void The_sidebar_fills_the_work_area_height_minus_the_margin()
    {
        var at100 = WidgetLayout.SidebarRect(Area100, 1.0, collapsed: false);
        Assert.Equal((16.0, 16.0, 1360.0), (at100.X, at100.Y, at100.H));

        var at175 = WidgetLayout.SidebarRect(Area175, 1.75, collapsed: false);
        Assert.Equal(28, at175.X, 6);
        Assert.Equal(28, at175.Y, 6);
        Assert.Equal(1300, at175.H, 6);
    }

    [Theory]
    [MemberData(nameof(OverlappingTheSidebarAt175))]
    public void These_widgets_end_up_under_the_sidebar_at_175_percent(WidgetId id, double x, double y, double w, double h)
    {
        var (rx, ry) = WidgetLayout.ToRelative(new LayoutRect(x, y, w, h), Area100);
        var (px, py) = WidgetLayout.FromRelative(rx, ry, w * 1.75, h * 1.75, Area175, EdgeMarginDip * 1.75);
        var sidebar = WidgetLayout.SidebarRect(Area175, 1.75, collapsed: false);

        Assert.True(new LayoutRect(px, py, w * 1.75, h * 1.75).Overlaps(sidebar), $"{id} should overlap the sidebar at 175 %");
    }

    [Fact]
    public void The_other_widgets_stay_clear_of_the_sidebar_at_175_percent()
    {
        var sidebar = WidgetLayout.SidebarRect(Area175, 1.75, collapsed: false);
        foreach (var (rect, dip) in new[]
                 {
                     (new LayoutRect(744, 632, 360, 744), (W: 360.0, H: 744.0)),     // Crosshair
                     (new LayoutRect(1112, 632, 504, 408), (W: 504.0, H: 408.0)),    // Music
                     (new LayoutRect(1624, 72, 472, 552), (W: 472.0, H: 552.0)),     // Capture
                     (new LayoutRect(2104, 72, 952, 824), (W: 952.0, H: 824.0)),     // WhatsApp
                 })
        {
            var (rx, ry) = WidgetLayout.ToRelative(rect, Area100);
            var (px, py) = WidgetLayout.FromRelative(rx, ry, dip.W * 1.75, dip.H * 1.75, Area175, EdgeMarginDip * 1.75);
            Assert.False(new LayoutRect(px, py, dip.W * 1.75, dip.H * 1.75).Overlaps(sidebar));
        }
    }

    [Fact]
    public void ClearOf_moves_an_overlapping_window_to_the_right_of_the_obstacle()
    {
        var sidebar = new LayoutRect(16, 16, 280, 1360);
        var rect = new LayoutRect(100, 200, 400, 300);

        var (x, y) = WidgetLayout.ClearOf(rect, sidebar, Area100, gap: 8, margin: 8);

        Assert.Equal(304, x);   // sidebar edge 296 + gap
        Assert.Equal(200, y);   // only the horizontal axis moves
    }

    [Fact]
    public void ClearOf_leaves_a_window_alone_that_does_not_overlap()
    {
        var sidebar = new LayoutRect(16, 16, 280, 1360);
        var rect = new LayoutRect(304, 200, 400, 300);   // exactly one gap away

        Assert.Equal((304.0, 200.0), WidgetLayout.ClearOf(rect, sidebar, Area100, gap: 8, margin: 8));
    }

    [Fact]
    public void ClearOf_keeps_the_pushed_window_on_the_screen()
    {
        var sidebar = new LayoutRect(16, 16, 280, 1360);

        // Too wide to sit right of the sidebar: pushed as far as the screen allows.
        var (x, _) = WidgetLayout.ClearOf(new LayoutRect(100, 200, 3300, 300), sidebar, Area100, gap: 8, margin: 8);
        Assert.Equal(Area100.Right - 3300 - 8, x);

        // Above the top margin: pulled down.
        var (_, y) = WidgetLayout.ClearOf(new LayoutRect(100, -50, 400, 300), sidebar, Area100, gap: 8, margin: 8);
        Assert.Equal(8, y);
    }

    // ---- the round trip ----

    [Theory]
    [MemberData(nameof(Layout))]
    public void A_widget_comes_back_to_its_exact_place_after_175_percent(WidgetId id, double x, double y, double w, double h)
    {
        var original = new LayoutRect(x, y, w, h);
        // What is stored. System moves never write it, so it is the same on the way back.
        var (relX, relY) = WidgetLayout.ToRelative(original, Area100);

        var at175 = Place(relX, relY, w, h, 1.75, Area175);

        // The app's own placement at 175 % (clamp, sidebar clearance) must not be saveable...
        var ledger = new PlacementLedger();
        ledger.MarkSystem(id, at175);
        Assert.False(ledger.ShouldSave(id, at175));

        // ...so the unchanged stored place lands the widget exactly where it was.
        var back = Place(relX, relY, w, h, 1.0, Area100);
        Assert.Equal(original, back);
    }

    [Theory]
    [MemberData(nameof(OverlappingTheSidebarAt175))]
    public void The_old_behaviour_saved_the_pushed_175_percent_spot_and_displaced_the_widget(WidgetId id, double x, double y, double w, double h)
    {
        var original = new LayoutRect(x, y, w, h);
        var (relX, relY) = WidgetLayout.ToRelative(original, Area100);
        var at175 = Place(relX, relY, w, h, 1.75, Area175);

        // Bug: KeepWidgetsClearOfSidebar saved that emergency spot as the user's place.
        var (badRelX, badRelY) = WidgetLayout.ToRelative(at175, Area175);
        var buggyBack = Place(badRelX, badRelY, w, h, 1.0, Area100);

        Assert.True(original != buggyBack, $"{id} would have come back at {buggyBack}, not at {original}");
        Assert.NotEqual(original, buggyBack);
        Assert.NotEqual(at175, original);   // and it really was pushed
    }

    [Fact]
    public void Audio_was_pushed_to_532_and_lands_about_390_px_to_the_right_with_the_old_behaviour()
    {
        var original = new LayoutRect(304, 72, 432, 552);
        var (relX, relY) = WidgetLayout.ToRelative(original, Area100);
        var at175 = Place(relX, relY, 432, 552, 1.75, Area175);
        Assert.Equal(532, at175.X);   // sidebar edge 518 + 14 px gap, as in the report
        Assert.Equal(14, at175.Y);    // clamped at the top

        var (badRelX, badRelY) = WidgetLayout.ToRelative(at175, Area175);
        var buggyBack = Place(badRelX, badRelY, 432, 552, 1.0, Area100);
        Assert.Equal(390, buggyBack.X - original.X, 0);
        Assert.Equal(162, buggyBack.Y - original.Y, 0);
    }
}
