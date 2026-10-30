using System;
using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Click-point order, counts and jitter, and the cursor path between points (design section 4).</summary>
public sealed class ClickPointTests
{
    private static ClickPoint P(int x, int y, int clicks = 1, int radius = 0) => new() { X = x, Y = y, Clicks = clicks, Radius = radius };

    private static ClickPointSequencer Seq(IReadOnlyList<ClickPoint> points, bool stopWhenComplete = false, int seed = 1)
        => new(points, stopWhenComplete, new Randomizer(seed));

    private static List<ClickTarget> Take(ClickPointSequencer s, int n, int clicksPerCycle = 1)
    {
        var list = new List<ClickTarget>();
        for (int i = 0; i < n && s.TryNext(clicksPerCycle, out var t); i++) list.Add(t);
        return list;
    }

    // ---- sequencer: order and counts ----

    [Fact]
    public void Points_are_visited_in_order_with_their_click_counts_and_the_route_loops()
    {
        var s = Seq(new[] { P(100, 100, clicks: 2), P(300, 200, clicks: 3) });

        var targets = Take(s, 12);

        Assert.Equal(new[] { 0, 0, 1, 1, 1, 0, 0, 1, 1, 1, 0, 0 }, targets.Select(t => t.PointIndex).ToArray());
        // Even at radius 0 the extra offset moves a click by up to 2 px.
        Assert.All(targets, t =>
        {
            var (x, y) = t.PointIndex == 0 ? (100, 100) : (300, 200);
            Assert.InRange(t.X - x, -2, 2);
            Assert.InRange(t.Y - y, -2, 2);
        });
        Assert.False(s.IsComplete);
    }

    [Fact]
    public void The_first_click_of_a_visit_is_marked()
    {
        var s = Seq(new[] { P(1, 1, clicks: 3), P(2, 2, clicks: 1) });
        var flags = Take(s, 5).Select(t => t.FirstOfVisit).ToArray();
        Assert.Equal(new[] { true, false, false, true, true }, flags);   // 3 clicks at A, 1 at B, then A again
    }

    [Fact]
    public void Stop_when_complete_marks_the_final_click_and_then_refuses()
    {
        var s = Seq(new[] { P(100, 100, clicks: 2), P(300, 200, clicks: 1) }, stopWhenComplete: true);

        var got = Take(s, 10);

        Assert.Equal(3, got.Count);
        Assert.Equal(new[] { false, false, true }, got.Select(t => t.Final).ToArray());
        Assert.True(s.IsComplete);
        Assert.False(s.TryNext(1, out _));
    }

    [Fact]
    public void Without_stop_when_complete_no_target_is_ever_final()
    {
        var s = Seq(new[] { P(1, 1), P(2, 2) });
        Assert.DoesNotContain(Take(s, 50), t => t.Final);
    }

    [Fact]
    public void A_single_point_with_one_click_is_final_at_once_when_stopping_on_completion()
    {
        var s = Seq(new[] { P(5, 6) }, stopWhenComplete: true);
        Assert.True(s.TryNext(1, out var t));
        Assert.True(t.Final);
        Assert.False(s.TryNext(1, out _));
    }

    [Fact]
    public void A_double_click_cycle_counts_two_clicks_toward_the_point()
    {
        var s = Seq(new[] { P(1, 1, clicks: 4), P(2, 2, clicks: 2) });

        var got = Take(s, 6, clicksPerCycle: 2).Select(t => t.PointIndex).ToArray();

        Assert.Equal(new[] { 0, 0, 1, 0, 0, 1 }, got);
    }

    [Fact]
    public void The_remaining_clicks_at_the_point_let_the_engine_split_the_last_double_click()
    {
        var s = Seq(new[] { P(1, 1, clicks: 3), P(2, 2, clicks: 1) });

        Assert.Equal(3, s.RemainingAtPoint);
        s.TryNext(2, out _);
        Assert.Equal(1, s.RemainingAtPoint);       // one click left: the engine sends a single click
        s.TryNext(1, out var t);
        Assert.Equal(0, t.PointIndex);
        Assert.Equal(1, s.CurrentIndex);
        Assert.Equal(1, s.RemainingAtPoint);
    }

    [Fact]
    public void A_cycle_that_overshoots_the_count_moves_on_to_the_next_point()
    {
        var s = Seq(new[] { P(1, 1, clicks: 1), P(2, 2, clicks: 1) });
        Assert.Equal(new[] { 0, 1, 0 }, Take(s, 3, clicksPerCycle: 2).Select(t => t.PointIndex).ToArray());
    }

    [Fact]
    public void No_points_means_no_targets()
    {
        var s = Seq(Array.Empty<ClickPoint>());
        Assert.False(s.HasPoints);
        Assert.False(s.TryNext(1, out _));
        Assert.False(s.IsComplete);
    }

    [Fact]
    public void A_count_below_one_is_treated_as_one()
    {
        var s = Seq(new[] { P(1, 1, clicks: 0), P(2, 2, clicks: -4) });
        Assert.Equal(new[] { 0, 1, 0, 1 }, Take(s, 4).Select(t => t.PointIndex).ToArray());
    }

    [Fact]
    public void Reset_starts_the_route_again()
    {
        var s = Seq(new[] { P(1, 1), P(2, 2) }, stopWhenComplete: true);
        Take(s, 5);
        Assert.True(s.IsComplete);
        s.Reset();
        Assert.False(s.IsComplete);
        Assert.Equal(0, s.CurrentIndex);
        Assert.Equal(2, Take(s, 5).Count);
    }

    [Fact]
    public void Points_with_negative_coordinates_stay_within_the_extra_offset_without_a_radius()
    {
        var s = Seq(new[] { P(-1500, -300) });
        foreach (var t in Take(s, 500))
        {
            Assert.InRange(t.X, -1502, -1498);
            Assert.InRange(t.Y, -302, -298);
        }
    }

    // ---- jitter ----

    private static double Distance(ClickTarget t, int cx, int cy) => Math.Sqrt((t.X - cx) * (double)(t.X - cx) + (t.Y - cy) * (double)(t.Y - cy));

    [Fact]
    public void Jitter_stays_within_the_radius_plus_the_extra_offset()
    {
        var s = Seq(new[] { P(-800, 600, clicks: 1, radius: 10) }, seed: 21);
        foreach (var t in Take(s, 30_000))
            Assert.True(Distance(t, -800, 600) <= 10 + ClickPointJitter.ExtraMaxPx + 0.75, $"{t.X},{t.Y}");
    }

    [Fact]
    public void Jitter_without_extra_would_be_uniform_in_the_disc()
    {
        // Mean distance of a uniform disc is 2R/3; the extra offsets and the pixel rounding move it only slightly.
        var s = Seq(new[] { P(0, 0, clicks: 1, radius: 30) }, seed: 5);
        var d = Take(s, 50_000).Select(t => Distance(t, 0, 0)).Average();
        Assert.InRange(d, 19.5, 21.5);
    }

    [Fact]
    public void Jitter_is_centred_on_the_point()
    {
        var s = Seq(new[] { P(1000, 1000, clicks: 1, radius: 20) }, seed: 8);
        var targets = Take(s, 50_000);
        Assert.InRange(targets.Average(t => t.X), 999.5, 1000.5);
        Assert.InRange(targets.Average(t => t.Y), 999.5, 1000.5);
    }

    [Fact]
    public void A_radius_of_zero_still_gets_the_extra_offset_of_at_most_two_pixels()
    {
        // Design section 4: the 21.6 % extra offset comes on top of the radius jitter, whatever the radius.
        var s = Seq(new[] { P(640, 480, clicks: 1, radius: 0) }, seed: 3);
        var got = Take(s, 20_000);

        Assert.All(got, t =>
        {
            Assert.InRange(t.X - 640, -2, 2);
            Assert.InRange(t.Y - 480, -2, 2);
            Assert.True(Math.Sqrt((t.X - 640.0) * (t.X - 640.0) + (t.Y - 480.0) * (t.Y - 480.0)) <= ClickPointJitter.ExtraMaxPx + 1.0);
        });
        // Small offsets round to the pixel itself, so fewer than 21.6 % of the clicks end up displaced, but clearly some do.
        double displaced = got.Count(t => t.X != 640 || t.Y != 480) / (double)got.Count;
        Assert.InRange(displaced, 0.05, 0.216);
    }

    [Fact]
    public void The_extra_offset_happens_in_about_216_per_mille_of_the_clicks_and_is_at_most_two_pixels()
    {
        var rng = new Randomizer(77);
        int hits = 0;
        const int n = 200_000;
        for (int i = 0; i < n; i++)
        {
            if (!ClickPointJitter.TryExtraOffset(rng, out double dx, out double dy)) continue;
            hits++;
            Assert.True(Math.Sqrt(dx * dx + dy * dy) <= ClickPointJitter.ExtraMaxPx + 1e-9);
        }
        Assert.InRange(hits / (double)n, 0.206, 0.226);
        Assert.Equal(0.216, ClickPointJitter.ExtraChance);
    }

    [Fact]
    public void The_same_seed_gives_the_same_route()
    {
        var pts = new[] { P(10, 10, 2, 15), P(500, 300, 3, 40) };
        var a = Take(Seq(pts, seed: 99), 100).Select(t => (t.X, t.Y)).ToList();
        var b = Take(Seq(pts, seed: 99), 100).Select(t => (t.X, t.Y)).ToList();
        Assert.Equal(a, b);
        var c = Take(Seq(pts, seed: 100), 100).Select(t => (t.X, t.Y)).ToList();
        Assert.NotEqual(a, c);
    }

    // ---- CursorPath: when and how long ----

    [Theory]
    [InlineData(49.9, false)]
    [InlineData(50.0, true)]
    [InlineData(100.0, true)]
    [InlineData(1.0, false)]
    public void The_cursor_jumps_from_50_clicks_per_second(double cps, bool jumps)
        => Assert.Equal(jumps, CursorPath.ShouldJump(cps));

    [Fact]
    public void The_move_lasts_20_to_60_percent_of_the_interval()
    {
        var rng = new Randomizer(4);
        double min = double.MaxValue, max = 0;
        for (int i = 0; i < 20_000; i++)
        {
            double d = CursorPath.MoveDurationMs(100, rng);
            min = Math.Min(min, d);
            max = Math.Max(max, d);
        }
        Assert.InRange(min, 20, 21);
        Assert.InRange(max, 59, 60);
    }

    [Fact]
    public void The_move_never_lasts_longer_than_200_ms()
    {
        var rng = new Randomizer(4);
        for (int i = 0; i < 5_000; i++)
        {
            Assert.True(CursorPath.MoveDurationMs(10_000, rng) <= 200);
            Assert.True(CursorPath.MoveDurationMs(600, rng) <= 200);
        }
        Assert.Equal(200, CursorPath.MoveDurationMs(10_000, new Randomizer(1)));   // 20 % of 10 s is already above the cap
    }

    // ---- CursorPath: Bezier ----

    [Fact]
    public void Ease_is_a_quad_in_out_curve()
    {
        Assert.Equal(0, CursorPath.Ease(0), 12);
        Assert.Equal(0.5, CursorPath.Ease(0.5), 12);
        Assert.Equal(1, CursorPath.Ease(1), 12);
        Assert.Equal(0.02, CursorPath.Ease(0.1), 12);
        Assert.Equal(0.98, CursorPath.Ease(0.9), 12);
        double prev = 0;
        for (double t = 0.01; t <= 1.0; t += 0.01)
        {
            double e = CursorPath.Ease(t);
            Assert.True(e >= prev);
            prev = e;
        }
        Assert.Equal(0, CursorPath.Ease(-1));
        Assert.Equal(1, CursorPath.Ease(2));
    }

    [Fact]
    public void The_path_ends_exactly_on_the_target_at_the_full_duration()
    {
        var path = CursorPath.Bezier(new PixelPoint(100, 100), new PixelPoint(900, 450), 80, new Randomizer(2));

        Assert.True(path.Count >= 2);
        Assert.Equal(new PixelPoint(900, 450), new PixelPoint(path[^1].X, path[^1].Y));
        Assert.Equal(80, path[^1].AtMs, 9);
    }

    [Fact]
    public void The_path_times_increase_and_stay_within_the_duration()
    {
        var path = CursorPath.Bezier(new PixelPoint(0, 0), new PixelPoint(800, 600), 150, new Randomizer(2));
        double prev = 0;
        foreach (var p in path)
        {
            Assert.True(p.AtMs > prev, "times must increase");
            Assert.True(p.AtMs <= 150 + 1e-9);
            prev = p.AtMs;
        }
    }

    [Fact]
    public void Without_a_random_source_the_path_is_a_straight_eased_line()
    {
        var path = CursorPath.Bezier(new PixelPoint(0, 0), new PixelPoint(1000, 0), 100);

        Assert.All(path, p => Assert.Equal(0, p.Y));
        for (int i = 1; i < path.Count; i++) Assert.True(path[i].X >= path[i - 1].X);

        // Slow start and slow end: the first and last steps are shorter than a step through the middle.
        int mid = path.Count / 2;
        int first = path[0].X;
        int middle = path[mid].X - path[mid - 1].X;
        int last = path[^1].X - path[^2].X;
        Assert.True(first < middle, $"first {first} vs middle {middle}");
        Assert.True(last < middle, $"last {last} vs middle {middle}");

        // Halfway in time the cursor is halfway there.
        var half = path.OrderBy(p => Math.Abs(p.AtMs - 50)).First();
        Assert.InRange(half.X, 1000 * CursorPath.Ease(half.AtMs / 100) - 1, 1000 * CursorPath.Ease(half.AtMs / 100) + 1);
        Assert.InRange(half.X, 400, 600);
    }

    [Fact]
    public void A_curved_path_leaves_the_straight_line_but_only_a_little()
    {
        var path = CursorPath.Bezier(new PixelPoint(0, 0), new PixelPoint(1000, 0), 100, new Randomizer(13));
        int maxY = path.Max(p => Math.Abs(p.Y));
        Assert.True(maxY > 0, "an unseeded curve should bend");
        Assert.True(maxY <= CursorPath.MaxCurve * 1000 * 0.5 + 1, $"bent by {maxY}");
    }

    [Fact]
    public void The_same_seed_gives_the_same_path()
    {
        var a = CursorPath.Bezier(new PixelPoint(5, 5), new PixelPoint(700, 300), 90, new Randomizer(8));
        var b = CursorPath.Bezier(new PixelPoint(5, 5), new PixelPoint(700, 300), 90, new Randomizer(8));
        Assert.Equal(a, b);
    }

    [Fact]
    public void A_path_with_negative_coordinates_ends_on_target()
    {
        var path = CursorPath.Bezier(new PixelPoint(-100, -100), new PixelPoint(-1800, 700), 120, new Randomizer(6));
        Assert.Equal(new PixelPoint(-1800, 700), new PixelPoint(path[^1].X, path[^1].Y));
    }

    [Fact]
    public void No_distance_or_no_time_gives_the_target_at_once()
    {
        var same = CursorPath.Bezier(new PixelPoint(10, 10), new PixelPoint(10, 10), 100, new Randomizer(1));
        Assert.Single(same);
        Assert.Equal(new PathPoint(10, 10, 0), same[0]);

        var instant = CursorPath.Bezier(new PixelPoint(0, 0), new PixelPoint(50, 50), 0, new Randomizer(1));
        Assert.Single(instant);
        Assert.Equal(new PathPoint(50, 50, 0), instant[0]);

        var negative = CursorPath.Bezier(new PixelPoint(0, 0), new PixelPoint(50, 50), -5);
        Assert.Single(negative);
    }

    [Fact]
    public void Intermediate_points_do_not_repeat_the_previous_position()
    {
        // A 3 px move over 100 ms would otherwise emit dozens of identical positions.
        var path = CursorPath.Bezier(new PixelPoint(0, 0), new PixelPoint(3, 0), 100);
        for (int i = 1; i < path.Count; i++)
            Assert.NotEqual((path[i - 1].X, path[i - 1].Y), (path[i].X, path[i].Y));
        Assert.True(path.Count <= 4);
    }

    [Fact]
    public void The_step_count_is_bounded()
    {
        var path = CursorPath.Bezier(new PixelPoint(0, 0), new PixelPoint(4000, 3000), 200);
        Assert.InRange(path.Count, 10, CursorPath.MaxSteps);
    }

    [Fact]
    public void The_fill_overload_reuses_the_caller_list()
    {
        var list = new List<PathPoint> { new(1, 1, 1), new(2, 2, 2) };
        CursorPath.Bezier(new PixelPoint(0, 0), new PixelPoint(100, 100), 40, null, list);
        Assert.NotEmpty(list);
        Assert.Equal(new PathPoint(100, 100, 40), list[^1]);
        Assert.DoesNotContain(new PathPoint(1, 1, 1), list);
        Assert.DoesNotContain(new PathPoint(2, 2, 2), list);
    }
}
