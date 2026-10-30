using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.Services.AutoClicker;

namespace WKI_Clipper.Views.AutoClicker;

/// <summary>
/// The click-point picker (design section 8): a click adds a point, Backspace removes the last one, right-click, Esc or Enter finish.
/// Existing points are shown as numbered markers (with their jitter radius) and can be removed the same way. The list goes to
/// <c>Clicker.ClickPoints</c> when the picker closes.
/// </summary>
public sealed class ClickPointPicker : PickerSession
{
    private readonly List<ClickPoint> _points;

    private ClickPointPicker(AppHost host, Action? closed) : base(host, closed)
        => _points = PickerEdits.Copy(host.Settings.Current.AutoClicker.Clicker.ClickPoints);

    /// <summary>Opens the picker on every monitor. False when a picker is already open. <paramref name="closed"/> runs after it closed.</summary>
    public static bool Open(AppHost host, Action? closed = null) => new ClickPointPicker(host, closed).Open();

    internal IReadOnlyList<ClickPoint> Points => _points;

    protected override PickerOverlayWindow CreateWindow(PickerMonitor monitor) => new ClickPointPickerWindow(this, monitor);

    protected override void Commit()
    {
        var result = PickerEdits.Copy(_points);
        Host.Settings.Update(s => s.AutoClicker.Clicker.ClickPoints = result);
    }

    internal void Add(PixelPoint p)
    {
        if (!PickerEdits.TryAddPoint(_points, p)) return;
        Dirty = true;
        RedrawAll();
    }

    internal void RemoveLast()
    {
        if (!PickerEdits.RemoveLastPoint(_points)) return;
        Dirty = true;
        RedrawAll();
    }
}

/// <summary>One monitor's part of the <see cref="ClickPointPicker"/>.</summary>
public sealed class ClickPointPickerWindow : PickerOverlayWindow
{
    private const double MarkerSize = 22;
    private readonly ClickPointPicker _session;

    internal ClickPointPickerWindow(ClickPointPicker session, PickerMonitor monitor)
        : base(monitor, L.T("Klicken: Klickpunkt setzen · Rücktaste: letzten entfernen · Rechtsklick oder Esc: fertig",
                            "Click: add a click point · Backspace: remove the last one · Right-click or Esc: done"))
    {
        _session = session;
        MouseLeftButtonDown += (_, e) => _session.Guard(() =>
        {
            _session.Add(CursorPixel(e));
            e.Handled = true;
        });
        MouseRightButtonUp += (_, e) => _session.Guard(() =>
        {
            e.Handled = true;
            _session.Finish();
        });
        PreviewKeyDown += (_, e) => _session.Guard(() =>
        {
            switch (e.Key)
            {
                case Key.Escape:
                case Key.Enter:
                    e.Handled = true;
                    _session.Finish();
                    break;
                case Key.Back:
                case Key.Delete:
                    e.Handled = true;
                    _session.RemoveLast();
                    break;
            }
        });
    }

    public override void Redraw()
    {
        Layer.Children.Clear();
        var points = _session.Points;
        StatusText = L.T($"{points.Count} von {ClickerSettingsRules.MaxClickPoints} Klickpunkten",
                         $"{points.Count} of {ClickerSettingsRules.MaxClickPoints} click points");

        // The route first, so the markers sit on top. The canvas clips segments that leave this monitor.
        for (int i = 0; i + 1 < points.Count; i++)
        {
            var a = PickerGeometry.ToDip(Monitor, new PixelPoint(points[i].X, points[i].Y));
            var b = PickerGeometry.ToDip(Monitor, new PixelPoint(points[i + 1].X, points[i + 1].Y));
            var line = new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, StrokeThickness = 1.5, Opacity = 0.6, StrokeDashArray = new DoubleCollection { 4, 3 } };
            line.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
            Layer.Children.Add(line);
        }

        double scale = PickerGeometry.SafeScale(Monitor.Scale);
        for (int i = 0; i < points.Count; i++)
        {
            var p = new PixelPoint(points[i].X, points[i].Y);
            if (!Monitor.Bounds.Contains(p)) continue;
            var c = PickerGeometry.ToDip(Monitor, p);

            if (points[i].Radius > 0)
            {
                double r = points[i].Radius / scale;
                var ring = new Ellipse { Width = 2 * r, Height = 2 * r, StrokeThickness = 1, Opacity = 0.7 };
                ring.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
                Put(ring, c.X - r, c.Y - r);
                Layer.Children.Add(ring);
            }

            var dot = new Ellipse { Width = MarkerSize, Height = MarkerSize, StrokeThickness = 1.5 };
            dot.SetResourceReference(Shape.FillProperty, "AccentBrush");
            dot.SetResourceReference(Shape.StrokeProperty, "OnAccentBrush");
            Put(dot, c.X - MarkerSize / 2, c.Y - MarkerSize / 2);
            Layer.Children.Add(dot);

            var number = new TextBlock
            {
                Text = (i + 1).ToString(System.Globalization.CultureInfo.CurrentCulture),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Width = MarkerSize * 2,
                TextAlignment = TextAlignment.Center
            };
            number.SetResourceReference(TextBlock.ForegroundProperty, "OnAccentBrush");
            number.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Put(number, c.X - MarkerSize, c.Y - number.DesiredSize.Height / 2);
            Layer.Children.Add(number);
        }
    }
}
