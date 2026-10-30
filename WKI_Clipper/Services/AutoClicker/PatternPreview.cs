using System;
using System.Collections.Generic;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>The small path drawing on a pattern card (mouse-patterns design 4.7), as points for a WPF <c>Polyline</c>.</summary>
public static class PatternPreview
{
    /// <summary>A card's drawing is a few dozen DIPs wide; more points than this only cost layout time.</summary>
    public const int MaxPoints = 200;

    /// <summary>
    /// The cumulative path of <paramref name="track"/> from (0, 0) - the start is the first point, y grows downwards like on the screen -
    /// thinned evenly over time to at most <see cref="MaxPoints"/> (start and end always kept), then scaled with its aspect kept to fit
    /// <paramref name="width"/> x <paramref name="height"/> minus <paramref name="padding"/> on every side and centred. An empty track gives
    /// no points; sizes that leave no room (or are not numbers) put every point on the centre.
    /// </summary>
    public static IReadOnlyList<DipPoint> Fit(PatternTrack track, double width, double height, double padding)
    {
        int bins = Math.Min(track.AtMs.Length, Math.Min(track.Dx.Length, track.Dy.Length));
        if (bins == 0) return Array.Empty<DipPoint>();

        int all = bins + 1;   // the start plus the position after each bin
        int keep = Math.Min(all, MaxPoints);
        var xs = new double[keep];
        var ys = new double[keep];
        long x = 0, y = 0;
        for (int i = 0, k = 0; i < all && k < keep; i++)
        {
            // Rounding half up keeps the picks strictly increasing (the step is >= 1), so every slot is filled.
            int wanted = k == keep - 1 ? all - 1 : (int)Math.Floor((double)k * (all - 1) / (keep - 1) + 0.5);
            if (i == wanted)
            {
                xs[k] = x;
                ys[k] = y;
                k++;
            }
            if (i < bins)
            {
                x += track.Dx[i];
                y += track.Dy[i];
            }
        }

        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        for (int k = 0; k < keep; k++)
        {
            minX = Math.Min(minX, xs[k]); maxX = Math.Max(maxX, xs[k]);
            minY = Math.Min(minY, ys[k]); maxY = Math.Max(maxY, ys[k]);
        }

        double w = Size(width), h = Size(height), pad = Size(padding);
        double roomW = Math.Max(0, w - 2 * pad), roomH = Math.Max(0, h - 2 * pad);
        double scale = double.PositiveInfinity;
        if (maxX > minX) scale = Math.Min(scale, roomW / (maxX - minX));
        if (maxY > minY) scale = Math.Min(scale, roomH / (maxY - minY));
        if (double.IsInfinity(scale)) scale = 0;   // a single point

        double offsetX = w / 2 - (minX + maxX) / 2 * scale, offsetY = h / 2 - (minY + maxY) / 2 * scale;
        var points = new DipPoint[keep];
        for (int k = 0; k < keep; k++) points[k] = new DipPoint(offsetX + xs[k] * scale, offsetY + ys[k] * scale);
        return points;
    }

    private static double Size(double v) => double.IsFinite(v) && v > 0 ? v : 0;
}
