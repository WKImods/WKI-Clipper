using System;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Mouse-patterns design 4.4: the cumulative playback curve and the drift-free cursor (speed, repeat, one pass).</summary>
public sealed class PatternCurveTests
{
    private static PatternTrack Track(params (int At, int Dx, int Dy)[] bins)
    {
        var at = new int[bins.Length]; var dx = new int[bins.Length]; var dy = new int[bins.Length];
        for (int i = 0; i < bins.Length; i++) (at[i], dx[i], dy[i]) = bins[i];
        return new PatternTrack(at, dx, dy);
    }

    /// <summary>A track with uneven values, gaps and both directions, so rounding has every chance to drift.</summary>
    private static PatternTrack Wobbly(int bins, int firstAt = 0)
    {
        var at = new int[bins]; var dx = new int[bins]; var dy = new int[bins];
        int t = firstAt;
        for (int i = 0; i < bins; i++)
        {
            at[i] = t;
            t += 1 + i % 4;                 // pauses of 0-3 ms between bins
            dx[i] = i % 7 - 2;              // never 0 together with dy
            dy[i] = i % 7 == 2 ? 3 : -(i % 3) - 1;
        }
        return new PatternTrack(at, dx, dy);
    }

    private static (long X, long Y) Rounded((double X, double Y) p) => ((long)Math.Round(p.X), (long)Math.Round(p.Y));

    [Fact]
    public void Curve_interpolates_inside_a_bin()
    {
        var c = new PatternCurve(Track((0, 4, -2), (5, 10, 20)), 5);

        Assert.Equal((0.0, 0.0), c.At(-1));        // bin 0 moves over (-1, 0]
        Assert.Equal((4.0, -2.0), c.At(0));        // ... so the first motion is out at once
        Assert.Equal((4.0, -2.0), c.At(4));        // bin 5 moves over (4, 5]
        Assert.Equal((6.5, 3.0), c.At(4.25));
        Assert.Equal((9.0, 8.0), c.At(4.5));
        Assert.Equal((14.0, 18.0), c.At(5));
        Assert.Equal((14.0, 18.0), c.At(1_000));   // clamped to the total after the end
        Assert.Equal((0.0, 0.0), c.At(double.NaN));
        Assert.Equal(5, c.DurationMs);
        Assert.Equal((14L, 18L), c.Total);
    }

    [Fact]
    public void A_curve_lasts_at_least_until_its_last_bin()
    {
        var track = Track((1, 1, 0), (7, 1, 0));
        Assert.Equal(7, new PatternCurve(track, 3).DurationMs);              // a duration that ends before the last bin
        Assert.Equal(7, new PatternCurve(track, double.NaN).DurationMs);
        Assert.Equal(10, new PatternCurve(track, 10).DurationMs);            // the other track ended later
        Assert.Equal(0, new PatternCurve(PatternTrack.Empty, -5).DurationMs);
        Assert.Equal((0.0, 0.0), new PatternCurve(PatternTrack.Empty, 0).At(3));
    }

    [Fact]
    public void A_malformed_track_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new PatternCurve(new PatternTrack(new[] { 0, 1 }, new[] { 1 }, new[] { 1, 1 }), 1));
        Assert.Throws<ArgumentException>(() => new PatternCurve(Track((3, 1, 0), (3, 1, 0)), 3));
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(1.0)]
    [InlineData(4.0)]
    public void Cursor_is_drift_free_at_quarter_one_and_four_times_speed(double speed)
    {
        var curve = new PatternCurve(Wobbly(300), 0);
        var cursor = new PatternCursor(curve, speed, repeat: false);
        double end = curve.DurationMs / speed;

        long sx = 0, sy = 0;
        bool running = true;
        int ticks = 0;
        for (double e = 0; running; e += end / 1000.3)   // ~1000 small ticks that never land on a bin edge
        {
            running = cursor.Advance(e, out int dx, out int dy);
            sx += dx;
            sy += dy;
            ticks++;
            Assert.Equal((sx, sy), cursor.Emitted);
            if (running) Assert.Equal(Rounded(curve.At(e * speed)), cursor.Emitted);   // the cumulative target, nothing accumulated
            else Assert.True(e >= end - 1e-9);
        }

        Assert.InRange(ticks, 1000, 1002);
        Assert.Equal(curve.Total, (sx, sy));
        Assert.Equal(curve.Total, cursor.Emitted);
    }

    [Fact]
    public void Repeat_continues_from_the_end_without_jump()
    {
        var curve = new PatternCurve(Wobbly(120, firstAt: 1), 0);   // starts at 0 ms: a pass boundary carries no motion of its own
        var cursor = new PatternCursor(curve, 2.0, repeat: true);
        double pass = curve.DurationMs / 2.0;

        long sx = 0, sy = 0;
        for (int i = 1; i <= 3000; i++)
        {
            double e = 3 * pass * i / 3000;
            Assert.True(cursor.Advance(e, out int dx, out int dy));
            Assert.InRange(dx, -4, 4);   // the biggest bin is 4 counts; a tick covers less than one bin
            Assert.InRange(dy, -4, 4);
            sx += dx;
            sy += dy;
        }

        Assert.Equal((3 * curve.Total.X, 3 * curve.Total.Y), (sx, sy));
        Assert.Equal((sx, sy), cursor.Emitted);

        Assert.True(cursor.Advance(3.5 * pass, out _, out _));      // and on into the fourth pass, from where the third ended
        var half = Rounded(curve.At(0.5 * curve.DurationMs));
        Assert.Equal((3 * curve.Total.X + half.X, 3 * curve.Total.Y + half.Y), cursor.Emitted);
    }

    [Fact]
    public void Non_repeating_cursor_stops_after_one_pass()
    {
        // A closed shape: its total is 0/0, so the pass has to end by time, not by "total delivered".
        var curve = new PatternCurve(Track((0, 3, 0), (4, 0, 5), (10, -3, -5)), 10);
        var cursor = new PatternCursor(curve, 2.0, repeat: false);

        Assert.True(cursor.Advance(0, out int dx, out int dy));
        Assert.Equal((3, 0), (dx, dy));
        Assert.True(cursor.Advance(4.9, out _, out _));
        Assert.True(cursor.Advance(4.75, out _, out _));            // a clock that steps back moves back along the path
        Assert.False(cursor.Advance(5, out dx, out dy));            // 10 ms at 2x: done, the last bit still delivered
        Assert.Equal((0L, 0L), cursor.Emitted);
        Assert.False(cursor.Advance(6, out dx, out dy));
        Assert.Equal((0, 0), (dx, dy));
        Assert.False(cursor.Advance(1, out dx, out dy));            // finished stays finished
        Assert.Equal((0, 0), (dx, dy));
    }

    [Fact]
    public void A_single_instant_pattern_plays_at_once_and_repeats_once_per_ms()
    {
        var curve = new PatternCurve(Track((0, 7, -1)), 0);

        var once = new PatternCursor(curve, 1.0, repeat: false);
        Assert.False(once.Advance(0, out int dx, out int dy));
        Assert.Equal((7, -1), (dx, dy));

        var looping = new PatternCursor(curve, 1.0, repeat: true);
        Assert.True(looping.Advance(0, out _, out _));
        Assert.True(looping.Advance(9.5, out _, out _));
        Assert.Equal((70L, -10L), looping.Emitted);                 // passes 0 .. 9, never a division by a zero-length pass
    }

    [Fact]
    public void Odd_elapsed_values_and_speeds_are_safe()
    {
        var curve = new PatternCurve(Track((0, 2, 2), (3, 2, 2)), 3);

        var c = new PatternCursor(curve, double.NaN, repeat: false);   // an unreadable speed plays at 1x
        Assert.True(c.Advance(double.NaN, out int dx, out int dy));
        Assert.Equal((2, 2), (dx, dy));
        Assert.True(c.Advance(-5, out dx, out dy));
        Assert.Equal((0, 0), (dx, dy));
        Assert.True(c.Advance(2.5, out _, out _));
        Assert.False(c.Advance(3, out _, out _));

        var slow = new PatternCursor(curve, 0, repeat: false);         // clamped to the slowest speed
        Assert.True(slow.Advance(11.9, out _, out _));
        Assert.False(slow.Advance(12, out _, out _));
    }
}
