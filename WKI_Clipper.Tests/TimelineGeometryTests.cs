using System.Linq;
using WKI_Clipper.Views;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class TimelineGeometryTests
{
    private const double Width = 620;      // 600 px of track between the 10 px margins
    private const double Total = 120;      // seconds

    [Fact]
    public void The_ends_of_the_clip_sit_on_the_margins()
    {
        Assert.Equal(10, TimelineGeometry.XForSeconds(0, Total, Width), 9);
        Assert.Equal(610, TimelineGeometry.XForSeconds(Total, Total, Width), 9);
        Assert.Equal(310, TimelineGeometry.XForSeconds(60, Total, Width), 9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17.25)]
    [InlineData(60)]
    [InlineData(119.99)]
    [InlineData(120)]
    public void Time_to_x_and_back_is_the_identity(double seconds)
    {
        var x = TimelineGeometry.XForSeconds(seconds, Total, Width);

        Assert.Equal(seconds, TimelineGeometry.SecondsForX(x, Total, Width), 9);
    }

    [Fact]
    public void Times_and_positions_outside_the_clip_are_clamped()
    {
        Assert.Equal(10, TimelineGeometry.XForSeconds(-5, Total, Width), 9);
        Assert.Equal(610, TimelineGeometry.XForSeconds(500, Total, Width), 9);
        Assert.Equal(0, TimelineGeometry.SecondsForX(-100, Total, Width), 9);
        Assert.Equal(0, TimelineGeometry.SecondsForX(3, Total, Width), 9);          // inside the left margin
        Assert.Equal(Total, TimelineGeometry.SecondsForX(10_000, Total, Width), 9);
    }

    [Theory]
    [InlineData(0, 120)]       // no width yet (before the first layout)
    [InlineData(15, 120)]      // narrower than the two margins
    [InlineData(620, 0)]       // unknown length
    [InlineData(620, -1)]
    public void A_degenerate_control_or_clip_never_divides_by_zero(double width, double total)
    {
        Assert.Equal(10, TimelineGeometry.XForSeconds(30, total, width), 9);
        Assert.Equal(0, TimelineGeometry.SecondsForX(300, total, width), 9);
    }

    [Fact]
    public void A_click_near_a_handle_grabs_it_and_anywhere_else_is_the_track()
    {
        Assert.Equal(TimelineHit.InHandle, TimelineGeometry.HitTest(103, 100, 400));
        Assert.Equal(TimelineHit.InHandle, TimelineGeometry.HitTest(93, 100, 400));       // exactly on the radius
        Assert.Equal(TimelineHit.OutHandle, TimelineGeometry.HitTest(405, 100, 400));
        Assert.Equal(TimelineHit.Track, TimelineGeometry.HitTest(250, 100, 400));
        Assert.Equal(TimelineHit.Track, TimelineGeometry.HitTest(92.9, 100, 400));         // just outside
        Assert.Equal(TimelineHit.None, TimelineGeometry.HitTest(double.NaN, 100, 400));
    }

    [Fact]
    public void With_overlapping_handles_the_nearer_one_wins_and_a_tie_depends_on_the_side()
    {
        Assert.Equal(TimelineHit.InHandle, TimelineGeometry.HitTest(100, 100, 106));        // 0 vs 6
        Assert.Equal(TimelineHit.OutHandle, TimelineGeometry.HitTest(105, 100, 106));       // 5 vs 1
        Assert.Equal(TimelineHit.InHandle, TimelineGeometry.HitTest(98, 100, 100));         // both on the same spot: left → start
        Assert.Equal(TimelineHit.OutHandle, TimelineGeometry.HitTest(102, 100, 100));       // right → end
        Assert.Equal(TimelineHit.OutHandle, TimelineGeometry.HitTest(100, 100, 100));
    }

    [Fact]
    public void Four_hundred_fifty_keyframes_on_600_pixels_are_thinned_to_at_least_three_pixels_apart()
    {
        var xs = Enumerable.Range(0, 450).Select(i => TimelineGeometry.XForSeconds(i * 4.0, 1800, 620)).ToList();

        var thinned = TimelineGeometry.ThinTicks(xs, 3).ToList();

        Assert.True(thinned.Count < xs.Count);
        Assert.InRange(thinned.Count, 140, 200);   // 600 px / 3 px at most; ticks 1.33 px apart keep every third
        for (int i = 1; i < thinned.Count; i++)
            Assert.True(thinned[i] - thinned[i - 1] >= 3, $"{thinned[i - 1]} → {thinned[i]}");
        Assert.Equal(xs[0], thinned[0]);
    }

    [Fact]
    public void Ticks_that_are_far_enough_apart_all_survive()
    {
        var xs = new[] { 10.0, 50, 90, 130 };

        Assert.Equal(xs, TimelineGeometry.ThinTicks(xs, 3));
        Assert.Empty(TimelineGeometry.ThinTicks(System.Array.Empty<double>(), 3));
    }
}
