using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// The planner's side of the bar obstacle: what is moved when the board opens and what is written back. Unlike the sidebar
/// (a system move, never saved) the push below the bar is saved - "pushed once, no jumping on every open" (Review Focus 4).
/// Runs the same sequence the host does: show, push, commit, close, open again.
/// </summary>
public sealed class BarClearanceTests
{
    private static readonly LayoutRect Area = new(0, 0, 1920, 1040);
    private static readonly LayoutRect Bar = BarLayout.BarRect(Area, 640, 1.0);
    private const string Monitor = @"\\.\DISPLAY1";

    private static WidgetState State(WidgetId id, LayoutRect r)
    {
        var (rx, ry) = WidgetLayout.ToRelative(r, Area);
        return new WidgetState { Id = id, Width = r.W, Height = r.H, X = r.X, Y = r.Y, RelX = rx, RelY = ry, MonitorDeviceName = Monitor };
    }

    private static IEnumerable<(WidgetId, LayoutRect)> Windows(params (WidgetId, LayoutRect)[] w) => w;

    [Fact]
    public void Widget_under_bar_moves_once_and_the_result_is_saved()
    {
        var planner = new WidgetPlacementPlanner();
        var placed = new LayoutRect(700, 20, 360, 400);
        var st = State(WidgetId.Capture, placed);
        planner.Placed(WidgetId.Capture, placed);                       // the board put it there from the stored place

        var moves = planner.ClearOfBar(Windows((WidgetId.Capture, placed)), Bar, Area, 1.0);

        var m = Assert.Single(moves);
        Assert.Equal(Bar.Bottom + WidgetPlacementPlanner.GapDip, m.Rect.Y);
        Assert.True(planner.Commit(st, m.Rect, 360, 400, Area, Monitor));   // saved, unlike a sidebar move
        Assert.Equal(m.Rect.Y, st.Y);
        Assert.NotNull(st.RelY);

        // Closed and opened again: the stored place is below the bar, nothing intersects, nothing moves, nothing is written.
        var restored = planner.Restore(st, 360, 400, 0, legacy: false, Area, 1.0);
        planner.Placed(WidgetId.Capture, restored);
        Assert.Empty(planner.ClearOfBar(Windows((WidgetId.Capture, restored)), Bar, Area, 1.0));
        Assert.False(planner.Commit(st, restored, 360, 400, Area, Monitor));
    }

    [Fact]
    public void Widget_not_under_bar_is_untouched_and_unsaved()
    {
        var planner = new WidgetPlacementPlanner();
        var placed = new LayoutRect(700, 300, 360, 400);
        var st = State(WidgetId.Audio, placed);
        planner.Placed(WidgetId.Audio, placed);

        Assert.Empty(planner.ClearOfBar(Windows((WidgetId.Audio, placed)), Bar, Area, 1.0));
        Assert.False(planner.Commit(st, placed, 360, 400, Area, Monitor));
    }

    [Fact]
    public void The_push_stays_inside_the_work_area()
    {
        // A tall widget that cannot sit below the bar and still fit: it ends at the bottom edge margin, not off the screen.
        var planner = new WidgetPlacementPlanner();
        var tall = new LayoutRect(700, 20, 360, 990);

        var m = Assert.Single(planner.ClearOfBar(Windows((WidgetId.Gallery, tall)), Bar, Area, 1.0));

        Assert.True(m.Rect.Bottom <= Area.Bottom);
        Assert.True(m.Rect.Y >= Area.Y);
        Assert.Equal(tall.H, m.Rect.H);
    }

    [Fact]
    public void The_gap_and_margin_scale_with_the_monitor()
    {
        var planner = new WidgetPlacementPlanner();
        var area = new LayoutRect(0, 0, 2560, 1400);
        var bar = BarLayout.BarRect(area, 640, 1.5);
        var widget = new LayoutRect(900, 20, 540, 600);

        var m = Assert.Single(planner.ClearOfBar(Windows((WidgetId.Capture, widget)), bar, area, 1.5));

        Assert.Equal(bar.Bottom + WidgetPlacementPlanner.GapDip * 1.5, m.Rect.Y);
    }

    [Fact]
    public void A_later_sidebar_move_does_not_become_part_of_the_saved_place()
    {
        // A small screen where one widget sits under BOTH obstacles: the bar push is the user's to keep, the sidebar's sideways push is not.
        var area = new LayoutRect(0, 0, 900, 800);
        var bar = BarLayout.BarRect(area, 640, 1.0);                                   // x 130..770
        var sidebar = WidgetLayout.SidebarRect(area, 1.0, collapsed: false);           // x 16..296
        var planner = new WidgetPlacementPlanner();
        var placed = new LayoutRect(150, 20, 300, 300);
        var st = State(WidgetId.Capture, placed);
        planner.Placed(WidgetId.Capture, placed);

        var pushed = Assert.Single(planner.ClearOfBar(Windows((WidgetId.Capture, placed)), bar, area, 1.0));
        Assert.True(planner.Commit(st, pushed.Rect, 300, 300, area, Monitor));         // saved right after the push, as the host does
        var savedX = st.X;

        var aside = Assert.Single(planner.ClearOfSidebar(Windows((WidgetId.Capture, pushed.Rect)), sidebar, area, 1.0));
        Assert.True(aside.Rect.X > pushed.Rect.X);                                     // the sidebar pushed it right ...
        Assert.False(planner.Commit(st, aside.Rect, 300, 300, area, Monitor));        // ... but that is never saved
        Assert.Equal(savedX, st.X);
    }

    [Fact]
    public void A_widget_that_cannot_move_further_is_not_reported_as_moved()
    {
        // Already at the clamp position (as low as it can go): the push would give the same rectangle back.
        var planner = new WidgetPlacementPlanner();
        var tall = new LayoutRect(700, 42, 360, 990);   // y 42 is the lowest a 990 px tall window can sit in this work area

        Assert.Empty(planner.ClearOfBar(Windows((WidgetId.Gallery, tall)), Bar, Area, 1.0));
    }
}