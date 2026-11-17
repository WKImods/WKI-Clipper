using System;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>
/// The way back of one pass to where it began (pattern return design 1). <see cref="PatternReturn.Jump"/> undoes everything the pass
/// sent in one move; <see cref="PatternReturn.Reverse"/> plays the driven part of the curve backwards at the pass's tempo and ends exactly
/// at the start. Like <see cref="PatternCursor"/>, each move is the difference between a rounded cumulative target and what it already
/// returned, so rounding never drifts. Not thread-safe: owned by the player thread.
/// </summary>
public sealed class PatternReturnPath
{
    private readonly PatternCurve _curve;
    private readonly double _speed;
    private readonly double _fromMs;
    private readonly long _fromX, _fromY;       // the rounded curve position the way back starts at
    private readonly long _offsetX, _offsetY;   // what the way out sent
    private readonly PatternReturn _mode;
    private long _x, _y;
    private bool _done;

    /// <param name="speed">Clamped like <see cref="PatternCursor"/>.</param>
    /// <param name="fromCurveMs">Where the way out ended on the curve (<see cref="PatternCursor.CurveMs"/>); only Reverse uses it.</param>
    /// <param name="offset">Everything the way out sent: the way back sends exactly minus this.</param>
    /// <exception cref="ArgumentException"><paramref name="mode"/> is not Jump or Reverse (Off has no way back).</exception>
    public PatternReturnPath(PatternCurve curve, double speed, double fromCurveMs, (long X, long Y) offset, PatternReturn mode)
    {
        ArgumentNullException.ThrowIfNull(curve);
        if (mode is not (PatternReturn.Jump or PatternReturn.Reverse))
            throw new ArgumentException("Only Jump and Reverse have a way back.", nameof(mode));

        _curve = curve;
        _speed = PatternCursor.ClampSpeed(speed);
        _fromMs = fromCurveMs > 0 && double.IsFinite(fromCurveMs) ? Math.Min(fromCurveMs, curve.DurationMs) : 0;
        var (ax, ay) = curve.At(_fromMs);
        (_fromX, _fromY) = ((long)Math.Round(ax), (long)Math.Round(ay));
        (_offsetX, _offsetY) = offset;
        _mode = mode;
    }

    /// <summary>Everything the way back sent so far.</summary>
    public (long X, long Y) Emitted => (_x, _y);

    /// <summary>
    /// The move from the last call to <paramref name="elapsedMs"/> since the way back began (negative, NaN or infinite count as 0). The
    /// move is valid whatever it returns: false means it is back at the start with this call, and every later call returns false with 0/0.
    /// Jump is back with its first call (a move beyond an int follows with the next calls).
    /// </summary>
    public bool Advance(double elapsedMs, out int dx, out int dy)
    {
        dx = dy = 0;
        if (_done) return false;

        // The way back's cumulative target: -offset at the start; on the way, the curve's position at r minus where it turned.
        long tx = -_offsetX, ty = -_offsetY;
        bool home = true;
        if (_mode == PatternReturn.Reverse)
        {
            double r = _fromMs - (elapsedMs > 0 && double.IsFinite(elapsedMs) ? elapsedMs * _speed : 0);
            if (r > 0)
            {
                var (ax, ay) = _curve.At(r);
                tx = (long)Math.Round(ax) - _fromX;
                ty = (long)Math.Round(ay) - _fromY;
                home = false;
            }
        }

        // One move cannot carry more than an int; anything beyond follows with the next call.
        dx = (int)Math.Clamp(tx - _x, int.MinValue, int.MaxValue);
        dy = (int)Math.Clamp(ty - _y, int.MinValue, int.MaxValue);
        _x += dx;
        _y += dy;
        _done = home && _x == tx && _y == ty;
        return !_done;
    }
}
