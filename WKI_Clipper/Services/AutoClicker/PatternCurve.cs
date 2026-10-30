using System;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>
/// One track of a pattern as a continuous path over time (mouse-patterns design 4.4): <see cref="At"/> is the cumulative offset from the
/// start at a playback time, each bin's motion spread linearly over its millisecond (the bin at <c>AtMs = k</c> moves over (k-1, k], the
/// interval <see cref="PatternRecording"/> summed it from), so the first bin is out at once at 0 ms. Immutable and shared by every
/// <see cref="PatternCursor"/> that plays the track.
/// </summary>
public sealed class PatternCurve
{
    private readonly int[] _at;
    private readonly int[] _dx;
    private readonly int[] _dy;
    private readonly long[] _sumX;   // motion of bins 0 .. i
    private readonly long[] _sumY;

    /// <summary>
    /// <paramref name="durationMs"/> is the recording's length (<see cref="PatternData.DurationMs"/>, shared by both tracks so either one
    /// passes in the same time); it is clamped to 0 .. <see cref="MousePatternRules.MaxDurationMs"/> and never ends before the last bin.
    /// Throws <see cref="ArgumentException"/> for arrays of different lengths or times that are not strictly increasing.
    /// </summary>
    public PatternCurve(PatternTrack track, double durationMs)
    {
        int n = track.AtMs.Length;
        if (track.Dx.Length != n || track.Dy.Length != n) throw new ArgumentException("The track's arrays differ in length.", nameof(track));

        _at = (int[])track.AtMs.Clone();
        _dx = (int[])track.Dx.Clone();
        _dy = (int[])track.Dy.Clone();
        _sumX = new long[n];
        _sumY = new long[n];
        long x = 0, y = 0;
        for (int i = 0; i < n; i++)
        {
            if (i > 0 && _at[i] <= _at[i - 1]) throw new ArgumentException("The track's times are not strictly increasing.", nameof(track));
            _sumX[i] = x += _dx[i];
            _sumY[i] = y += _dy[i];
        }

        Total = (x, y);
        double duration = double.IsFinite(durationMs) ? Math.Clamp(durationMs, 0, MousePatternRules.MaxDurationMs) : 0;
        DurationMs = n > 0 ? Math.Max(duration, _at[^1]) : duration;
    }

    /// <summary>The length of one pass; at and after it <see cref="At"/> is <see cref="Total"/>.</summary>
    public double DurationMs { get; }

    public (long X, long Y) Total { get; }

    /// <summary>The cumulative offset at <paramref name="tMs"/> into the pass: (0, 0) before the first bin and for NaN,
    /// <see cref="Total"/> after the last. Allocation-free (binary search), cheap enough for every player tick.</summary>
    public (double X, double Y) At(double tMs)
    {
        if (double.IsNaN(tMs) || _at.Length == 0) return (0, 0);

        // The bins that are complete by tMs: AtMs <= tMs.
        int lo = 0, hi = _at.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (_at[mid] <= tMs) lo = mid + 1;
            else hi = mid;
        }

        double x = lo > 0 ? _sumX[lo - 1] : 0, y = lo > 0 ? _sumY[lo - 1] : 0;
        if (lo < _at.Length)
        {
            double part = tMs - (_at[lo] - 1);   // the next bin is under way when tMs is inside (AtMs - 1, AtMs)
            if (part > 0)
            {
                x += part * _dx[lo];
                y += part * _dy[lo];
            }
        }
        return (x, y);
    }
}

/// <summary>
/// The playback position of one running pattern (mouse-patterns design 4.4): <see cref="Advance"/> turns the time since the start into the
/// integer move to send now, as the difference between the rounded cumulative target and what it already returned, so rounding never
/// accumulates - at any speed and over any number of passes. With repeat, pass n continues from n x <see cref="PatternCurve.Total"/>.
/// Not thread-safe: owned by the player thread.
/// </summary>
public sealed class PatternCursor
{
    /// <summary>A repeating pass is at least one bin long, so an instant pattern (one bin at 0 ms) cannot divide by zero.</summary>
    private const double MinPassMs = MousePatternRules.BinMs;

    private readonly PatternCurve _curve;
    private readonly double _speed;
    private readonly bool _repeat;
    private long _x, _y;
    private bool _done;

    /// <param name="speed">Clamped like the settings (<see cref="MousePatternRules.MinSpeed"/> .. <see cref="MousePatternRules.MaxSpeed"/>;
    /// NaN, infinite or negative = 1x).</param>
    public PatternCursor(PatternCurve curve, double speed, bool repeat)
    {
        _curve = curve;
        _speed = double.IsFinite(speed) && speed >= 0
            ? Math.Clamp(speed, MousePatternRules.MinSpeed, MousePatternRules.MaxSpeed)
            : MousePatternRules.DefaultSpeed;
        _repeat = repeat;
    }

    /// <summary>Everything returned so far.</summary>
    public (long X, long Y) Emitted => (_x, _y);

    /// <summary>
    /// The move from the last call to <paramref name="elapsedMs"/> since the start (negative, NaN or infinite count as 0). The move is
    /// valid whatever it returns: false means a non-repeating pass is complete with this call (by time, so a shape that returns to its start
    /// still plays to the end), and every later call returns false with 0/0. A repeating cursor always returns true.
    /// </summary>
    public bool Advance(double elapsedMs, out int dx, out int dy)
    {
        dx = dy = 0;
        if (_done) return false;

        double t = elapsedMs > 0 && double.IsFinite(elapsedMs) ? elapsedMs * _speed : 0;
        long tx, ty;
        bool passComplete = false;
        if (_repeat)
        {
            double pass = Math.Max(_curve.DurationMs, MinPassMs);
            double passes = Math.Floor(t / pass);
            double into = t - passes * pass;
            if (into >= pass) { passes++; into -= pass; }   // floating-point noise at a pass boundary
            if (into < 0) into = 0;
            var (ax, ay) = _curve.At(into);
            var total = _curve.Total;
            tx = (long)Math.Round(ax) + total.X * (long)passes;
            ty = (long)Math.Round(ay) + total.Y * (long)passes;
        }
        else
        {
            var (ax, ay) = _curve.At(t);
            tx = (long)Math.Round(ax);
            ty = (long)Math.Round(ay);
            passComplete = t >= _curve.DurationMs;
        }

        // One move cannot carry more than an int; anything beyond follows with the next call.
        dx = (int)Math.Clamp(tx - _x, int.MinValue, int.MaxValue);
        dy = (int)Math.Clamp(ty - _y, int.MinValue, int.MaxValue);
        _x += dx;
        _y += dy;
        _done = passComplete && _x == tx && _y == ty;
        return !_done;
    }
}
