using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;

namespace WKI_Clipper.Views;

/// <summary>
/// The clip editor's timeline: track, selection, keyframe ticks, the two marks and the playhead. Drawn by hand
/// (OnRender) — a clip has hundreds of keyframes. All positions are seconds on the timeline (the first frame is 0).
/// A click or drag on the track scrubs; a drag on a mark moves it. The control only reports where the mouse is; the
/// window decides what that means (frame snapping, limits).
/// </summary>
internal sealed class EditorTimeline : FrameworkElement
{
    private double _total = 1, _in, _out = 1, _playhead;
    private double? _snapFrom;
    private double[] _keyframes = Array.Empty<double>();
    private bool _exact;
    private TimelineHit _drag = TimelineHit.None;

    public EditorTimeline()
    {
        Height = 56;
        Focusable = false;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
    }

    /// <summary>The user wants this time shown (clicking or dragging on the track).</summary>
    public event Action<double>? Scrub;
    /// <summary>The start mark was dragged to this time.</summary>
    public event Action<double>? InDragged;
    /// <summary>The end mark was dragged to this time.</summary>
    public event Action<double>? OutDragged;

    /// <summary>False while a cut runs: the marks are locked, scrubbing is allowed.</summary>
    public bool MarksEnabled { get; set; } = true;

    public void SetState(double total, double inSeconds, double outSeconds, double playhead, double? snapFrom,
                         IReadOnlyList<double> keyframes, bool exact)
    {
        _total = total;
        _in = inSeconds;
        _out = outSeconds;
        _playhead = playhead;
        _snapFrom = snapFrom;
        _keyframes = keyframes as double[] ?? keyframes.ToArray();
        _exact = exact;
        InvalidateVisual();
    }

    public void SetPlayhead(double playhead)
    {
        _playhead = playhead;
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- drawing

    private Brush Res(string key, Brush fallback)
        => TryFindResource(key) as Brush ?? Application.Current?.TryFindResource(key) as Brush ?? fallback;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var panel = Res("PanelBrush", Brushes.DimGray);
        var accent = Res("AccentBrush", Brushes.Orange);
        var muted = Res("MutedBrush", Brushes.Gray);
        var text = Res("TextBrush", Brushes.White);

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));   // the whole control takes mouse hits, not just what is painted

        double top = 6, bottom = h - 6;
        double x0 = TimelineGeometry.XForSeconds(0, _total, w), x1 = TimelineGeometry.XForSeconds(_total, _total, w);
        dc.DrawRoundedRectangle(panel, null, new Rect(x0, top, Math.Max(1, x1 - x0), bottom - top), 3, 3);

        double xi = TimelineGeometry.XForSeconds(_in, _total, w), xo = TimelineGeometry.XForSeconds(_out, _total, w);
        if (xo > xi)
        {
            var fill = accent.Clone();
            fill.Opacity = 0.35;
            dc.DrawRectangle(fill, null, new Rect(xi, top, xo - xi, bottom - top));
        }

        // keyframes: thinned to at most one tick per 3 px; fainter when an exact cut makes them irrelevant
        var tick = new Pen(muted, 1) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        dc.PushOpacity(_exact ? 0.35 : 0.9);
        foreach (var x in TimelineGeometry.ThinTicks(_keyframes.Select(k => TimelineGeometry.XForSeconds(k, _total, w)), 3))
            dc.DrawLine(tick, new Point(Math.Floor(x) + 0.5, top), new Point(Math.Floor(x) + 0.5, top + 10));
        dc.Pop();

        // where the user asked to start, when a fast cut moves it back to a keyframe
        if (_snapFrom is { } wanted)
        {
            double xs = TimelineGeometry.XForSeconds(wanted, _total, w);
            var dash = new Pen(text, 1) { DashStyle = new DashStyle(new double[] { 2, 2 }, 0) };
            dc.DrawLine(dash, new Point(xs, top - 2), new Point(xs, bottom + 2));
        }

        DrawHandle(dc, accent, xi, top, bottom, pointRight: true);
        DrawHandle(dc, accent, xo, top, bottom, pointRight: false);

        double xp = TimelineGeometry.XForSeconds(_playhead, _total, w);
        dc.DrawRectangle(text, null, new Rect(xp - 1, 1, 2, h - 2));
    }

    private static void DrawHandle(DrawingContext dc, Brush brush, double x, double top, double bottom, bool pointRight)
    {
        dc.DrawRectangle(brush, null, new Rect(x - 1.5, top - 3, 3, bottom - top + 6));
        double dir = pointRight ? 1 : -1;
        var triangle = new StreamGeometry();
        using (var ctx = triangle.Open())
        {
            ctx.BeginFigure(new Point(x, top - 3), true, true);
            ctx.LineTo(new Point(x + dir * 8, top - 3), true, false);
            ctx.LineTo(new Point(x, top + 5), true, false);
        }
        triangle.Freeze();
        dc.DrawGeometry(brush, null, triangle);
    }

    // ---------------------------------------------------------------- mouse

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        double x = e.GetPosition(this).X, w = ActualWidth;
        double xi = TimelineGeometry.XForSeconds(_in, _total, w), xo = TimelineGeometry.XForSeconds(_out, _total, w);

        _drag = MarksEnabled ? TimelineGeometry.HitTest(x, xi, xo) : TimelineHit.Track;
        if (_drag == TimelineHit.None) _drag = TimelineHit.Track;
        CaptureMouse();
        Report(x);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag == TimelineHit.None || !IsMouseCaptured) return;
        Report(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _drag = TimelineHit.None;
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _drag = TimelineHit.None;
    }

    private void Report(double x)
    {
        double seconds = TimelineGeometry.SecondsForX(x, _total, ActualWidth);
        switch (_drag)
        {
            case TimelineHit.InHandle: InDragged?.Invoke(seconds); break;
            case TimelineHit.OutHandle: OutDragged?.Invoke(seconds); break;
            default: Scrub?.Invoke(seconds); break;
        }
    }
}
