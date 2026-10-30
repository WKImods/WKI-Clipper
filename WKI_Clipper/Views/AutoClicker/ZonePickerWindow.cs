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
/// The zone picker (design section 8): drag a rectangle (it may span monitors); on release the zone is added as a Stop zone and a
/// small chooser next to it switches it to Pause or Start. Backspace takes back the last zone drawn in this picker, right-click, Esc
/// or Enter finish (Esc during a drag only drops the drag). Existing zones are drawn, disabled ones dimmed. The list goes to
/// <c>Clicker.Zones</c> when the picker closes.
/// </summary>
public sealed class ZonePicker : PickerSession
{
    private readonly List<ClickerZone> _zones;
    private readonly List<ClickerZone> _added = new();
    private PixelPoint? _dragStart;
    private PixelPoint _dragNow;

    private ZonePicker(AppHost host, Action? closed) : base(host, closed)
        => _zones = PickerEdits.Copy(host.Settings.Current.AutoClicker.Clicker.Zones);

    /// <summary>Opens the picker on every monitor. False when a picker is already open. <paramref name="closed"/> runs after it closed.</summary>
    public static bool Open(AppHost host, Action? closed = null) => new ZonePicker(host, closed).Open();

    internal IReadOnlyList<ClickerZone> Zones => _zones;

    /// <summary>The zone whose action chooser is showing, and the window showing it.</summary>
    internal ClickerZone? Pending { get; private set; }
    internal ZonePickerWindow? PendingWindow { get; private set; }

    /// <summary>The rectangle of the drag in progress, or null.</summary>
    internal PixelRect? Drag => _dragStart is PixelPoint s ? PickerGeometry.DragRect(s, _dragNow) : null;

    protected override PickerOverlayWindow CreateWindow(PickerMonitor monitor) => new ZonePickerWindow(this, monitor);

    protected override void Commit()
    {
        var result = PickerEdits.Copy(_zones);
        Host.Settings.Update(s => s.AutoClicker.Clicker.Zones = result);
    }

    internal void BeginDrag(PixelPoint p)
    {
        Pending = null;
        PendingWindow = null;
        _dragStart = p;
        _dragNow = p;
        RedrawAll();
    }

    internal void UpdateDrag(PixelPoint p)
    {
        if (_dragStart is null || p == _dragNow) return;
        _dragNow = p;
        RedrawAll();
    }

    internal void EndDrag(PixelPoint p, ZonePickerWindow window)
    {
        if (_dragStart is not PixelPoint start) return;
        _dragStart = null;
        var rect = PickerGeometry.ZoneFromDrag(start, p);
        var zone = rect is PixelRect r ? PickerEdits.TryAddZone(_zones, r) : null;
        if (zone is not null)
        {
            _added.Add(zone);
            Dirty = true;
            Pending = zone;
            PendingWindow = window;
        }
        RedrawAll();
    }

    internal void CancelDrag()
    {
        if (_dragStart is null) return;
        _dragStart = null;
        RedrawAll();
    }

    internal bool IsDragging => _dragStart is not null;

    internal void Choose(ZoneAction action)
    {
        if (Pending is null) return;
        Pending.Action = action;
        Pending = null;
        PendingWindow = null;
        RedrawAll();
    }

    internal void RemoveLastAdded()
    {
        if (_added.Count == 0) return;
        var zone = _added[^1];
        _added.RemoveAt(_added.Count - 1);
        _zones.Remove(zone);
        if (ReferenceEquals(Pending, zone))
        {
            Pending = null;
            PendingWindow = null;
        }
        Dirty = true;
        RedrawAll();
    }
}

/// <summary>One monitor's part of the <see cref="ZonePicker"/>.</summary>
public sealed class ZonePickerWindow : PickerOverlayWindow
{
    private readonly ZonePicker _session;

    internal ZonePickerWindow(ZonePicker session, PickerMonitor monitor)
        : base(monitor, L.T("Rechteck ziehen: Zone anlegen · Rücktaste: letzte neue Zone entfernen · Rechtsklick oder Esc: fertig",
                            "Drag a rectangle: add a zone · Backspace: remove the last new zone · Right-click or Esc: done"))
    {
        _session = session;
        // Button clicks inside the chooser are handled by the buttons and never reach these handlers.
        MouseLeftButtonDown += (_, e) => _session.Guard(() =>
        {
            e.Handled = true;
            _session.BeginDrag(CursorPixel(e));
            CaptureMouse();   // keeps the drag alive across onto other monitors' windows
        });
        MouseMove += (_, e) => _session.Guard(() =>
        {
            if (_session.IsDragging && IsMouseCaptured) _session.UpdateDrag(CursorPixel(e));
        });
        MouseLeftButtonUp += (_, e) => _session.Guard(() =>
        {
            if (!_session.IsDragging) return;
            e.Handled = true;
            // End the drag before releasing the capture: LostMouseCapture would otherwise see a drag in progress and drop it.
            _session.EndDrag(CursorPixel(e), this);
            if (IsMouseCaptured) ReleaseMouseCapture();
        });
        LostMouseCapture += (_, _) => _session.Guard(() =>
        {
            // Capture taken away mid-drag (another window, a system dialog): drop the drag rather than finishing it somewhere odd.
            if (_session.IsDragging) _session.CancelDrag();
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
                case Key.Escape when _session.IsDragging:
                    e.Handled = true;
                    _session.CancelDrag();
                    if (IsMouseCaptured) ReleaseMouseCapture();
                    break;
                case Key.Escape:
                case Key.Enter:
                    e.Handled = true;
                    _session.Finish();
                    break;
                case Key.Back:
                case Key.Delete:
                    e.Handled = true;
                    _session.RemoveLastAdded();
                    break;
            }
        });
    }

    public override void Redraw()
    {
        Layer.Children.Clear();
        int enabled = 0;
        foreach (var z in _session.Zones)
        {
            if (z.Enabled) enabled++;
            DrawZone(z);
        }
        StatusText = L.T($"{_session.Zones.Count} von {ClickerSettingsRules.MaxZones} Zonen ({enabled} aktiv)",
                         $"{_session.Zones.Count} of {ClickerSettingsRules.MaxZones} zones ({enabled} enabled)");

        if (_session.Drag is PixelRect drag && PickerGeometry.Intersect(drag, Monitor.Bounds) is not null)
        {
            var d = PickerGeometry.ToDip(Monitor, drag);
            var frame = new System.Windows.Shapes.Rectangle { Width = d.W, Height = d.H, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 2 } };
            frame.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
            Put(frame, d.X, d.Y);
            Layer.Children.Add(frame);
        }

        if (_session.Pending is ClickerZone pending && ReferenceEquals(_session.PendingWindow, this))
            DrawChooser(pending);
    }

    private static string BrushFor(ZoneAction action) => action switch
    {
        ZoneAction.Pause => "AccentBrush",
        ZoneAction.Start => "SuccessBrush",
        _ => "DangerBrush"
    };

    public static string ActionName(ZoneAction action) => action switch
    {
        ZoneAction.Pause => L.T("Pause", "Pause"),
        ZoneAction.Start => L.T("Start", "Start"),
        _ => L.T("Stopp", "Stop")
    };

    private void DrawZone(ClickerZone z)
    {
        var rect = new PixelRect(z.X, z.Y, z.W, z.H);
        if (PickerGeometry.Intersect(rect, Monitor.Bounds) is null) return;
        var d = PickerGeometry.ToDip(Monitor, rect);
        string brush = z.Enabled ? BrushFor(z.Action) : "FaintBrush";
        double opacity = z.Enabled ? 1.0 : 0.5;

        var fill = new System.Windows.Shapes.Rectangle { Width = d.W, Height = d.H, Opacity = 0.18 * opacity };
        fill.SetResourceReference(Shape.FillProperty, brush);
        Put(fill, d.X, d.Y);
        Layer.Children.Add(fill);

        var frame = new System.Windows.Shapes.Rectangle { Width = d.W, Height = d.H, StrokeThickness = 2, Opacity = opacity };
        if (!z.Enabled) frame.StrokeDashArray = new DoubleCollection { 3, 3 };
        frame.SetResourceReference(Shape.StrokeProperty, brush);
        Put(frame, d.X, d.Y);
        Layer.Children.Add(frame);

        string label = z.Name + " · " + ActionName(z.Action) + (z.Enabled ? "" : L.T(" (aus)", " (off)"));
        var text = new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, Opacity = opacity, Margin = new Thickness(6, 4, 6, 4) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        Put(text, d.X, d.Y);
        Layer.Children.Add(text);
    }

    private void DrawChooser(ClickerZone zone)
    {
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        var caption = new TextBlock { Text = L.T("Aktion:", "Action:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        row.Children.Add(caption);

        foreach (var action in new[] { ZoneAction.Stop, ZoneAction.Pause, ZoneAction.Start })
        {
            var b = new Button { Content = ActionName(action), Margin = new Thickness(0, 0, 6, 0), MinWidth = 64, Cursor = Cursors.Hand };
            if (action == zone.Action) b.SetResourceReference(StyleProperty, "AccentButton");
            b.Click += (_, _) => _session.Guard(() => _session.Choose(action));
            row.Children.Add(b);
        }

        var box = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6, 4, 6),
            Child = row,
            Cursor = Cursors.Arrow
        };
        box.SetResourceReference(Border.BackgroundProperty, "PopupBrush");
        box.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        // A press on the chooser's background must not start a new drag underneath it.
        box.MouseLeftButtonDown += (_, e) => e.Handled = true;

        box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var local = PickerGeometry.ToDip(Monitor, new PixelRect(zone.X, zone.Y, zone.W, zone.H));
        var at = PickerGeometry.ChooserPosition(local, new DipSize(box.DesiredSize.Width, box.DesiredSize.Height), LocalSize);
        Put(box, at.X, at.Y);
        Layer.Children.Add(box);
    }
}
