using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using WKI_Clipper.Models;
using WKI_Clipper.Native;
using WKI_Clipper.Services;

namespace WKI_Clipper.Views;

/// <summary>
/// The on-screen performance readout, Adrenalin-style: metric names and values (and small history
/// graphs) in a screen corner, in 1-3 columns, optionally on a translucent panel, in colours of your
/// choice. Without a panel there is no frame or background, only a soft shadow for legibility over any
/// game. Click-through and never focusable - except in drag mode, which the host switches on only while the board is
/// open: then the whole overlay can be grabbed (a dashed outline shows the area) and a drop is reported. Optionally
/// hidden from every capture (WDA).
/// All layout decisions (column flow, colours, panel fill) come from the pure <see cref="PerfOverlayLayout"/>.
/// </summary>
public sealed class PerfOverlayWindow : Window
{
    private const double GraphWidth = 64, GraphHeight = 14;

    private readonly Border _panel = new();
    private readonly StackPanel _columnsHost = new() { Orientation = System.Windows.Controls.Orientation.Horizontal };
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly List<Row> _rows = new();
    private int[] _shape = Array.Empty<int>();   // lines per column of the current visual tree
    private readonly System.Windows.Shapes.Rectangle _dragOutline;
    private IntPtr _hwnd;
    private bool _excluded;
    private bool _dragMode;

    private Brush _nameBrush = Brushes.White;
    private Brush _valueBrush = Brushes.White;
    private OverlayColor? _nameColor, _valueColor;
    private double _graphScale = 1;

    private static readonly FontFamily ValueFont = new("Cascadia Mono, Consolas");
    private static readonly Effect Shadow = MakeShadow();
    // A faint scrim in drag mode: it makes the whole area grabbable (the gaps between the numbers are fully transparent
    // and would let the click through) and shows where it is.
    private static readonly Brush DragScrim = MakeScrim();

    private static Brush MakeScrim()
    {
        var b = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0));
        b.Freeze();
        return b;
    }

    private static Effect MakeShadow()
    {
        // Readable over bright and dark game scenes alike, without a visible box.
        var e = new DropShadowEffect { Color = Colors.Black, BlurRadius = 5, ShadowDepth = 0, Opacity = 0.95 };
        e.Freeze();
        return e;
    }

    public PerfOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        IsHitTestVisible = false;
        Focusable = false;
        Title = "WKI Performance Overlay";

        _panel.Child = _columnsHost;
        _panel.LayoutTransform = _scale;

        // Sibling of the panel, never its parent: it must not change the size the window is measured to.
        _dragOutline = new System.Windows.Shapes.Rectangle
        {
            Fill = DragScrim,
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            RadiusX = 6,
            RadiusY = 6,
            Visibility = Visibility.Collapsed
        };
        _dragOutline.SetResourceReference(Shape.StrokeProperty, "AccentBrush");   // DynamicResource: follows a theme switch

        var root = new Grid();
        root.Children.Add(_panel);
        root.Children.Add(_dragOutline);
        Content = root;

        MouseLeftButtonDown += OnGrab;
    }

    /// <summary>Raised after the user dropped the overlay, with its rectangle in physical pixels. Only in drag mode.</summary>
    public event Action<LayoutRect>? Dropped;

    /// <summary>True while the overlay can be grabbed (the board is open).</summary>
    public bool DragMode => _dragMode;

    /// <summary>True from grabbing until the drop. The host must not move the window meanwhile - it would fight the user's hand.</summary>
    public bool IsDragging { get; private set; }

    internal Shape DragOutline => _dragOutline;
    internal bool DragOutlineShown => _dragOutline.Visibility == Visibility.Visible;

    /// <summary>
    /// Board open -> grabbable with a dashed outline. Board closed -> click-through again, so the overlay never
    /// intercepts a click meant for the game. Safe before the window has a handle: it is applied when it gets one.
    /// </summary>
    public void SetDragMode(bool on)
    {
        _dragMode = on;
        IsHitTestVisible = on;
        _dragOutline.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Cursor = on ? Cursors.SizeAll : null;
        if (_hwnd != IntPtr.Zero) ApplyClickThrough();
    }

    private void ApplyClickThrough()
    {
        int ex = User32.GetWindowLong(_hwnd, User32.GWL_EXSTYLE);
        // Never takes focus, never in Alt-Tab. TRANSPARENT (clicks go to the game underneath) except while dragging.
        ex |= User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW;
        if (_dragMode) ex &= ~User32.WS_EX_TRANSPARENT;
        else ex |= User32.WS_EX_TRANSPARENT;
        User32.SetWindowLong(_hwnd, User32.GWL_EXSTYLE, ex);
    }

    private void OnGrab(object sender, MouseButtonEventArgs e)
    {
        if (!_dragMode || IsDragging) return;
        IsDragging = true;
        try { DragMove(); }
        catch (InvalidOperationException) { return; }   // the button was already up again
        finally { IsDragging = false; }
        if (DisplayGeometry.BoundsOf(this) is { } bounds) ReportDrop(bounds);
    }

    /// <summary>A finished drag. Ignored unless the overlay is in drag mode: a stray event with the board closed must not move it.</summary>
    internal void ReportDrop(LayoutRect dropped)
    {
        if (_dragMode) Dropped?.Invoke(dropped);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyClickThrough();   // clicks go to the game underneath, unless the board is open and the overlay is being placed
        ApplyAffinity();
    }

    /// <summary>true = invisible to stream, clips and screenshots (still visible to the user).</summary>
    public void SetExcludedFromCapture(bool excluded)
    {
        _excluded = excluded;
        ApplyAffinity();
    }

    private void ApplyAffinity()
    {
        if (_hwnd == IntPtr.Zero) return;
        User32.SetWindowDisplayAffinity(_hwnd, _excluded ? User32.WDA_EXCLUDEFROMCAPTURE : User32.WDA_NONE);
    }

    /// <summary>
    /// Redraws the lines in the style of <paramref name="s"/> (size, columns, colours, panel, graph size).
    /// The visual tree is reused while the column shape stays the same.
    /// </summary>
    public void Render(IReadOnlyList<PerfOverlayLine> lines, PerfOverlaySettings s)
    {
        _scale.ScaleX = _scale.ScaleY = PerfOverlayLayout.ClampScale(s.Scale);
        ApplyPanel(s);
        ApplyColors(s);

        double graphScale = PerfOverlayLayout.ClampGraphScale(s.GraphScale);
        var columns = PerfOverlayLayout.Columns(lines, s.Columns);
        var shape = columns.Select(c => c.Count).ToArray();
        if (!shape.SequenceEqual(_shape) || graphScale != _graphScale)
        {
            _graphScale = graphScale;
            Rebuild(shape);
        }

        int i = 0;
        foreach (var column in columns)
            foreach (var line in column)
                _rows[i++].Update(line, _nameBrush, _valueBrush);
    }

    private void ApplyPanel(PerfOverlaySettings s)
    {
        var fill = PerfOverlayLayout.BackdropFill(s.Background, s.BackgroundTransparency);
        double pad = PerfOverlayLayout.BackdropPadding(s.Background);
        _panel.Padding = new Thickness(pad);
        if (fill is { } f)
        {
            var brush = new SolidColorBrush(Color.FromArgb(f.A, f.R, f.G, f.B));
            brush.Freeze();
            _panel.Background = brush;
            _panel.CornerRadius = new CornerRadius(8);
            _panel.Effect = null;   // the panel is the contrast; a shadow around it would only smudge
        }
        else
        {
            _panel.Background = null;
            _panel.CornerRadius = new CornerRadius(0);
            _panel.Effect = Shadow;
        }
    }

    private void ApplyColors(PerfOverlaySettings s)
    {
        if (_nameColor != s.NameColor) { _nameColor = s.NameColor; _nameBrush = BrushOf(s.NameColor); }
        if (_valueColor != s.ValueColor) { _valueColor = s.ValueColor; _valueBrush = BrushOf(s.ValueColor); }
    }

    private static Brush BrushOf(OverlayColor color)
    {
        var c = PerfOverlayLayout.ColorOf(color);
        var b = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    private void Rebuild(int[] shape)
    {
        _columnsHost.Children.Clear();
        _rows.Clear();
        _shape = shape;

        for (int col = 0; col < shape.Length; col++)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, col < shape.Length - 1 ? 16 : 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // label
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // value
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // graph

            for (int i = 0; i < shape[col]; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var row = new Row(_graphScale);
                Grid.SetRow(row.Label, i);
                Grid.SetRow(row.Value, i);
                Grid.SetRow(row.Graph, i);
                Grid.SetColumn(row.Value, 1);
                Grid.SetColumn(row.Graph, 2);
                grid.Children.Add(row.Label);
                grid.Children.Add(row.Value);
                grid.Children.Add(row.Graph);
                _rows.Add(row);
            }
            _columnsHost.Children.Add(grid);
        }
    }

    private sealed class Row
    {
        public readonly TextBlock Label = new()
        {
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Margin = new Thickness(0, 0, 10, 1)
        };
        public readonly TextBlock Value = new()
        {
            FontFamily = ValueFont,
            FontSize = 13,
            Margin = new Thickness(0, 0, 8, 1),
            TextAlignment = TextAlignment.Right,
            MinWidth = 64
        };
        public readonly Canvas Graph;
        private readonly Polyline _line = new()
        {
            StrokeThickness = 1.3,
            StrokeLineJoin = PenLineJoin.Round
        };
        private readonly double _w, _h;

        public Row(double graphScale)
        {
            _w = GraphWidth * graphScale;
            _h = GraphHeight * graphScale;
            Graph = new Canvas { Width = _w, Height = _h, VerticalAlignment = VerticalAlignment.Center };
            Graph.Children.Add(_line);
        }

        public void Update(PerfOverlayLine l, Brush nameBrush, Brush valueBrush)
        {
            Label.Text = l.Label;
            Label.Foreground = nameBrush;
            Value.Text = l.Value;
            Value.Foreground = l.Critical ? (Brush)Application.Current.FindResource("DangerBrush") : valueBrush;
            _line.Stroke = nameBrush;

            if (l.Graph is not { Count: > 1 } g)
            {
                Graph.Visibility = l.Graph is null ? Visibility.Collapsed : Visibility.Hidden;
                return;
            }
            Graph.Visibility = Visibility.Visible;
            // Fixed time axis: the newest value sits at the right edge, history scrolls left.
            double step = _w / (PerformanceMonitorService.HistoryLength - 1);
            var pts = new PointCollection(g.Count);
            for (int i = 0; i < g.Count; i++)
                pts.Add(new Point(_w - (g.Count - 1 - i) * step, _h - g[i] * _h));
            _line.Points = pts;
        }
    }
}
