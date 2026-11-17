using System;
using WKI_Clipper.Models;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Pattern return design 1: the way back of one pass to where it began (Jump, Reverse) and the cursor's curve time.</summary>
public sealed class PatternReturnPathTests
{
    private static PatternTrack Bins(int n, Func<int, (int At, int Dx, int Dy)> bin)
    {
        var at = new int[n]; var dx = new int[n]; var dy = new int[n];
        for (int i = 0; i < n; i++) (at[i], dx[i], dy[i]) = bin(i);
        return new PatternTrack(at, dx, dy);
    }

    /// <summary>1 px to the right per ms from 0 to <paramref name="n"/> ms: the bin at k ms (k = 1 .. n) moves over (k-1, k], so At(t) = t.</summary>
    private static PatternCurve Ramp(int n) => new(Bins(n, i => (i + 1, 1, 0)), n);

    /// <summary>Uneven values, pauses and both directions, so rounding has every chance to drift.</summary>
    private static PatternCurve Wobbly(int n)
    {
        int t = 0;
        var track = Bins(n, i =>
        {
            int at = t;
            t += 1 + i % 4;
            return (at, i % 7 - 2, i % 7 == 2 ? 3 : -(i % 3) - 1);
        });
        return new PatternCurve(track, 0);
    }

    [Fact]
    public void Jump_undoes_the_offset_in_one_move()
    {
        var back = new PatternReturnPath(Ramp(10), 1, 10, (120, -40), PatternReturn.Jump);

        Assert.False(back.Advance(0, out int dx, out int dy));   // back at the start with this call
        Assert.Equal((-120, 40), (dx, dy));
        Assert.Equal((-120L, 40L), back.Emitted);

        Assert.False(back.Advance(5, out dx, out dy));            // later calls: nothing more
        Assert.Equal((0, 0), (dx, dy));
    }

    [Fact]
    public void A_jump_beyond_an_int_follows_with_the_next_call()
    {
        var back = new PatternReturnPath(Ramp(10), 1, 10, (3_000_000_000, 0), PatternReturn.Jump);

        Assert.True(back.Advance(0, out int dx, out _));
        Assert.Equal(int.MinValue, dx);
        Assert.False(back.Advance(0, out dx, out _));
        Assert.Equal(-3_000_000_000L - int.MinValue, dx);
        Assert.Equal((-3_000_000_000L, 0L), back.Emitted);
    }

    [Fact]
    public void Reverse_retraces_the_path_and_ends_at_the_start()
    {
        var back = new PatternReturnPath(Ramp(10), 1, 10, (10, 0), PatternReturn.Reverse);

        Assert.True(back.Advance(0, out int dx, out _));
        Assert.Equal(0, dx);                                      // still at the end of the pass
        for (int ms = 1; ms <= 3; ms++)
        {
            Assert.True(back.Advance(ms, out dx, out int dy));
            Assert.Equal((-1, 0), (dx, dy));                      // the same tempo as the way out
        }
        Assert.Equal((-3L, 0L), back.Emitted);

        Assert.True(back.Advance(9, out _, out _));
        Assert.Equal((-9L, 0L), back.Emitted);
        Assert.False(back.Advance(10, out dx, out _));            // back at the start at 10 ms
        Assert.Equal(-1, dx);
        Assert.Equal((-10L, 0L), back.Emitted);
        Assert.False(back.Advance(20, out dx, out _));
        Assert.Equal(0, dx);
    }

    [Fact]
    public void Reverse_at_double_speed_takes_half_the_time()
    {
        var back = new PatternReturnPath(Ramp(10), 2, 10, (10, 0), PatternReturn.Reverse);

        Assert.True(back.Advance(4, out _, out _));
        Assert.Equal((-8L, 0L), back.Emitted);
        Assert.False(back.Advance(5, out _, out _));
        Assert.Equal((-10L, 0L), back.Emitted);
    }

    [Fact]
    public void Reverse_after_an_early_stop_retraces_only_the_driven_part()
    {
        var back = new PatternReturnPath(Ramp(10), 1, 4, (4, 0), PatternReturn.Reverse);

        Assert.True(back.Advance(3, out _, out _));
        Assert.Equal((-3L, 0L), back.Emitted);
        Assert.False(back.Advance(4, out _, out _));
        Assert.Equal((-4L, 0L), back.Emitted);
    }

    [Fact]
    public void Reverse_ignores_times_that_are_no_time()
    {
        var back = new PatternReturnPath(Ramp(10), 1, 10, (10, 0), PatternReturn.Reverse);

        Assert.True(back.Advance(-5, out int dx, out _));
        Assert.True(back.Advance(double.NaN, out int dx2, out _));
        Assert.True(back.Advance(double.PositiveInfinity, out int dx3, out _));
        Assert.Equal((0, 0, 0), (dx, dx2, dx3));
    }

    [Theory]
    [InlineData(0.25, 0.29)]
    [InlineData(1.37, 0.37)]
    [InlineData(4.0, 0.83)]
    public void Reverse_never_drifts_by_rounding(double speed, double step)
    {
        var curve = Wobbly(300);
        var cursor = new PatternCursor(curve, speed, repeat: false);
        double stopAt = curve.DurationMs / speed * 0.61;   // released somewhere inside the pass
        for (double t = 0; t <= stopAt; t += step) cursor.Advance(t, out _, out _);

        var offset = cursor.Emitted;
        var back = new PatternReturnPath(curve, speed, cursor.CurveMs, offset, PatternReturn.Reverse);
        long sx = 0, sy = 0;
        bool running = true;
        int calls = 0;
        for (double t = 0; running; t += step)
        {
            running = back.Advance(t, out int dx, out int dy);
            sx += dx;
            sy += dy;
            Assert.True(++calls < 100_000, "the way back never ended");
        }

        Assert.NotEqual((0L, 0L), offset);
        Assert.Equal(-offset.X, sx);
        Assert.Equal(-offset.Y, sy);
        Assert.Equal((sx, sy), back.Emitted);
        // The same time as the way out (within one step).
        Assert.InRange((calls - 1) * step, cursor.CurveMs / speed - step, cursor.CurveMs / speed + step);
    }

    [Fact]
    public void Off_is_no_return_path()
    {
        Assert.Throws<ArgumentException>(() => new PatternReturnPath(Ramp(10), 1, 10, (10, 0), PatternReturn.Off));
        Assert.Throws<ArgumentException>(() => new PatternReturnPath(Ramp(10), 1, 10, (10, 0), (PatternReturn)7));
    }

    [Fact]
    public void Cursor_reports_the_curve_time_it_reached()
    {
        var cursor = new PatternCursor(Ramp(10), 2, repeat: false);
        Assert.Equal(0, cursor.CurveMs);

        cursor.Advance(3, out _, out _);
        Assert.Equal(6, cursor.CurveMs);

        cursor.Advance(100, out _, out _);   // past the end
        Assert.Equal(10, cursor.CurveMs);
        cursor.Advance(200, out _, out _);
        Assert.Equal(10, cursor.CurveMs);
    }

    [Fact]
    public void A_new_pattern_has_no_way_back()
        => Assert.Equal(PatternReturn.Off, new MousePattern().Return);
}
