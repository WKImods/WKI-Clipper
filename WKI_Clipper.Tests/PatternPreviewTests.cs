using System;
using System.Linq;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Mouse-patterns design 4.7: the small path drawing on each pattern card.</summary>
public sealed class PatternPreviewTests
{
    private static PatternTrack Track(params (int At, int Dx, int Dy)[] bins)
    {
        var at = new int[bins.Length]; var dx = new int[bins.Length]; var dy = new int[bins.Length];
        for (int i = 0; i < bins.Length; i++) (at[i], dx[i], dy[i]) = bins[i];
        return new PatternTrack(at, dx, dy);
    }

    private static void Near(double x, double y, DipPoint actual)
    {
        Assert.Equal(x, actual.X, 9);
        Assert.Equal(y, actual.Y, 9);
    }

    [Fact]
    public void Preview_fits_keeps_aspect_and_caps_points()
    {
        // 1000 bins: 1000 counts to the right, then 500 down - a 2:1 "L".
        var bins = Enumerable.Range(0, 1000).Select(i => (i, i < 500 ? 2 : 0, i < 500 ? 0 : 1)).ToArray();
        var points = PatternPreview.Fit(Track(bins), 100, 100, 10);

        Assert.Equal(PatternPreview.MaxPoints, points.Count);
        Near(10, 30, points[0]);                            // the start is kept ...
        Near(90, 70, points[^1]);                           // ... and so is the end
        double minX = points.Min(p => p.X), maxX = points.Max(p => p.X), minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        Assert.Equal(80, maxX - minX, 9);                   // the wide side fills the area inside the padding
        Assert.Equal(40, maxY - minY, 9);                   // 2:1 kept
        Assert.Equal(50, (minX + maxX) / 2, 9);             // centred
        Assert.Equal(50, (minY + maxY) / 2, 9);
    }

    [Fact]
    public void A_short_track_keeps_every_point_from_the_start()
    {
        var points = PatternPreview.Fit(Track((0, 10, 0), (3, 0, -10), (9, -10, 0)), 60, 40, 5);

        Assert.Equal(4, points.Count);                       // start + one point per bin
        Near(15, 35, points[0]);                             // y grows downwards, like on the screen
        Near(45, 35, points[1]);
        Near(45, 5, points[2]);
        Near(15, 5, points[3]);
    }

    [Fact]
    public void A_straight_line_is_scaled_by_its_length_and_centred_across()
    {
        var points = PatternPreview.Fit(Track((0, 5, 0), (1, 5, 0)), 100, 50, 0);

        Near(0, 25, points[0]);
        Near(50, 25, points[1]);
        Near(100, 25, points[2]);
    }

    [Fact]
    public void Empty_track_gives_no_points()
        => Assert.Empty(PatternPreview.Fit(PatternTrack.Empty, 100, 100, 4));

    [Fact]
    public void Odd_sizes_never_give_unusable_points()
    {
        var track = Track((0, 3, 4), (1, -1, 2));
        foreach (var (w, h, pad) in new[] { (0.0, 0.0, 0.0), (double.NaN, 20, 2), (20, 20, 50), (-5, 10, double.PositiveInfinity) })
        {
            var points = PatternPreview.Fit(track, w, h, pad);
            Assert.Equal(3, points.Count);
            Assert.All(points, p => Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y)));
        }
    }
}
