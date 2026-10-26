using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Where the shortcut bar sits and which widgets it pushes down (pure maths, like the sidebar's).</summary>
public sealed class BarLayoutTests
{
    private static readonly LayoutRect Primary = new(0, 0, 1920, 1040);          // 1080p work area (taskbar excluded), 100 %
    private static readonly LayoutRect Second = new(1920, 0, 2560, 1400);        // a 1440p monitor to the right at 150 %

    [Fact]
    public void Bar_is_centred_on_work_area_with_top_margin()
    {
        var bar = BarLayout.BarRect(Primary, barWidth: 640, scale: 1.0);

        Assert.Equal(640, bar.X);                       // (1920 - 640) / 2
        Assert.Equal(8, bar.Y);                         // top margin 8 DIP
        Assert.Equal(640, bar.W);
        Assert.Equal(56, bar.H);                        // height 56 DIP
        Assert.Equal(Primary.X + Primary.W / 2, bar.X + bar.W / 2);
    }

    [Fact]
    public void Bar_uses_the_scale_of_its_monitor_for_everything_but_the_centre()
    {
        var bar = BarLayout.BarRect(Second, barWidth: 640, scale: 1.5);

        Assert.Equal(960, bar.W);                       // 640 DIP at 150 %
        Assert.Equal(84, bar.H);                        // 56 DIP at 150 %
        Assert.Equal(12, bar.Y);                        // 8 DIP at 150 %
        Assert.Equal(Second.X + Second.W / 2, bar.X + bar.W / 2);   // centred on THIS monitor
        Assert.Equal(1920 + 800, bar.X);
    }

    [Fact]
    public void A_bar_wider_than_the_screen_is_cut_to_the_work_area()
    {
        var bar = BarLayout.BarRect(Primary, barWidth: 5000, scale: 1.0);

        Assert.True(bar.X >= Primary.X);
        Assert.True(bar.Right <= Primary.Right);
        Assert.Equal(Primary.W - 2 * BarLayout.SideMarginDip, bar.W);
    }

    [Fact]
    public void Bar_rect_has_whole_pixels_at_odd_scales()
    {
        var bar = BarLayout.BarRect(new LayoutRect(0, 0, 1707, 1000), barWidth: 333, scale: 1.25);

        Assert.Equal(System.Math.Round(bar.X), bar.X);
        Assert.Equal(System.Math.Round(bar.Y), bar.Y);
        Assert.Equal(System.Math.Round(bar.W), bar.W);
        Assert.Equal(System.Math.Round(bar.H), bar.H);
    }

    [Fact]
    public void A_zero_or_missing_scale_counts_as_100_percent()
        => Assert.Equal(56, BarLayout.BarRect(Primary, 400, 0).H);

    // ---- the sidebar's edge ----

    // The expanded sidebar: 16 DIP from the edge, 280 wide -> right edge 296; the bar keeps one side margin (8) of air to it.
    private const double SidebarLimit = 16 + 280 + BarLayout.SideMarginDip;

    [Fact]
    public void A_bar_that_fits_beside_the_sidebar_stays_centred()
    {
        var plain = BarLayout.BarRect(Primary, 640, 1.0);
        var limited = BarLayout.BarRect(Primary, 640, 1.0, SidebarLimit);

        Assert.Equal(plain, limited);
    }

    [Fact]
    public void A_bar_that_would_cover_the_sidebar_slides_right_of_it()
    {
        // 1500 DIP wide, centred at x = 210: its left 94 px would sit on the sidebar's header and fold button.
        var bar = BarLayout.BarRect(Primary, barWidth: 1500, scale: 1.0, SidebarLimit);

        Assert.Equal(SidebarLimit, bar.X);
        Assert.Equal(1500, bar.W);                      // still whole: it fits to the right
        Assert.True(bar.Right <= Primary.Right - BarLayout.SideMarginDip);
    }

    [Fact]
    public void A_bar_too_wide_even_when_slid_is_cut_at_the_right_margin()
    {
        var bar = BarLayout.BarRect(Primary, barWidth: 1700, scale: 1.0, SidebarLimit);

        Assert.Equal(SidebarLimit, bar.X);
        Assert.Equal(Primary.Right - BarLayout.SideMarginDip, bar.Right);   // 8 DIP from the right edge, like any cut bar
        Assert.True(bar.W < 1700);                                          // the scroller takes over
    }

    [Fact]
    public void The_limit_is_in_pixels_on_a_scaled_second_monitor()
    {
        // 150 %: sidebar right edge = 1920 + (16 + 280) * 1.5; the gap is 8 DIP * 1.5.
        double limit = Second.X + (16 + 280 + BarLayout.SideMarginDip) * 1.5;
        var bar = BarLayout.BarRect(Second, barWidth: 1600, scale: 1.5, limit);   // 2400 px wide: centred x = 1920 + 80

        Assert.Equal(limit, bar.X);
        Assert.Equal(Second.Right - BarLayout.SideMarginDip * 1.5, bar.Right);
        Assert.Equal(84, bar.H);
        Assert.Equal(12, bar.Y);
    }

    [Fact]
    public void A_limit_left_of_the_centred_bar_changes_nothing_and_a_hopeless_limit_never_gives_a_negative_width()
    {
        Assert.Equal(BarLayout.BarRect(Primary, 640, 1.0), BarLayout.BarRect(Primary, 640, 1.0, 10));

        var hopeless = BarLayout.BarRect(Primary, 640, 1.0, leftLimitPx: Primary.Right + 500);
        Assert.True(hopeless.W >= 0);
    }

    // ---- obstacle ----

    private static readonly LayoutRect TheBar = BarLayout.BarRect(Primary, 640, 1.0);   // 640,8 .. 1280,64
    private const double Gap = 8;

    private static IReadOnlyList<(WidgetId Id, LayoutRect Rect)> Widgets(params (WidgetId, LayoutRect)[] w) => w;

    [Fact]
    public void Widget_under_bar_moves_once_and_stays()
    {
        var widget = new LayoutRect(700, 20, 360, 400);     // its title bar reaches up into the bar

        var first = BarLayout.PushBelow(TheBar, Widgets((WidgetId.Capture, widget)), Gap);

        var moved = Assert.Single(first);
        Assert.Equal(WidgetId.Capture, moved.Id);
        Assert.Equal(TheBar.Bottom + Gap, moved.NewRect.Y);   // one gap below the bar
        Assert.Equal(widget.X, moved.NewRect.X);              // only the height changes ...
        Assert.Equal(widget.W, moved.NewRect.W);              // ... and the size stays
        Assert.Equal(widget.H, moved.NewRect.H);

        // Next time the board opens the widget sits where it was put: nothing moves.
        var second = BarLayout.PushBelow(TheBar, Widgets((WidgetId.Capture, moved.NewRect)), Gap);
        Assert.Empty(second);
    }

    [Fact]
    public void Widget_not_under_bar_is_untouched()
    {
        var below = new LayoutRect(700, 200, 360, 400);
        var leftOf = new LayoutRect(100, 8, 400, 300);       // same height as the bar, but beside it
        var rightOf = new LayoutRect(1400, 8, 400, 300);

        var moves = BarLayout.PushBelow(TheBar, Widgets((WidgetId.Audio, below), (WidgetId.Gallery, leftOf), (WidgetId.Mixer, rightOf)), Gap);

        Assert.Empty(moves);
    }

    [Fact]
    public void Widget_touching_the_bar_edge_does_not_count_as_under_it()
    {
        var touching = new LayoutRect(700, TheBar.Bottom, 360, 400);

        Assert.Empty(BarLayout.PushBelow(TheBar, Widgets((WidgetId.Audio, touching)), Gap));
    }

    [Fact]
    public void Only_the_widgets_that_really_intersect_are_returned()
    {
        var under = new LayoutRect(600, 0, 300, 300);
        var clear = new LayoutRect(600, 300, 300, 300);
        var corner = new LayoutRect(1270, 50, 300, 300);     // overlaps by a few pixels at the bar's bottom-right corner

        var moves = BarLayout.PushBelow(TheBar,
            Widgets((WidgetId.Capture, under), (WidgetId.Audio, clear), (WidgetId.Gallery, corner)), Gap);

        Assert.Equal(new[] { WidgetId.Capture, WidgetId.Gallery }, moves.Select(m => m.Id).ToArray());
        Assert.All(moves, m => Assert.Equal(TheBar.Bottom + Gap, m.NewRect.Y));
    }

    [Fact]
    public void No_widgets_means_no_moves()
        => Assert.Empty(BarLayout.PushBelow(TheBar, Widgets(), Gap));
}
