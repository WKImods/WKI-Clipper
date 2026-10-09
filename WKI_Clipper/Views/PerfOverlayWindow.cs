using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using WKI_Clipper.Native;
using WKI_Clipper.Services;

namespace WKI_Clipper.Views;

/// <summary>
/// The on-screen performance readout: bare numbers (and small history graphs) in a screen
/// corner — no frame, no background, a soft shadow for legibility over any game. Always
/// click-through and never focusable; optionally hidden from every capture (WDA).
/// </summary>
public sealed class PerfOverlayWindow : Window
{
    private const double GraphWidth = 64, GraphHeight = 14;

    private readonly Grid _grid = new();
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly List<Row> _rows = new();
    private IntPtr _hwnd;
    private bool _excluded;

    private static readonly FontFamily ValueFont = new("Cascadia Mono, Consolas");

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

        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // label
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // value
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // graph

        Content = new Border
        {
            Padding = new Thickness(6),
            Child = _grid,
            LayoutTransform = _scale,
            // Readable over bright and dark game scenes alike, without a visible box.
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 5, ShadowDepth = 0, Opacity = 0.95 }
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        int ex = User32.GetWindowLong(_hwnd, User32.GWL_EXSTYLE);
        // Never takes focus, never in Alt-Tab, clicks always go to the game underneath.
        User32.SetWindowLong(_hwnd, User32.GWL_EXSTYLE,
            ex | User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW | User32.WS_EX_TRANSPARENT);
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

    /// <summary>Redraws the lines; rows are reused while their count stays the same.</summary>
    public void Render(IReadOnlyList<PerfOverlayLine> lines, double scale)
    {
        _scale.ScaleX = _scale.ScaleY = scale;
        if (_rows.Count != lines.Count) Rebuild(lines.Count);
        for (int i = 0; i < lines.Count; i++) _rows[i].Update(lines[i]);
    }

    private void Rebuild(int count)
    {
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        _rows.Clear();
        for (int i = 0; i < count; i++)
        {
            _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = new Row();
            Grid.SetRow(row.Label, i);
            Grid.SetRow(row.Value, i);
            Grid.SetRow(row.Graph, i);
            Grid.SetColumn(row.Value, 1);
            Grid.SetColumn(row.Graph, 2);
            _grid.Children.Add(row.Label);
            _grid.Children.Add(row.Value);
            _grid.Children.Add(row.Graph);
            _rows.Add(row);
        }
    }

    private sealed class Row
    {
        public readonly TextBlock Label = new()
        {
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Margin = new Thickness(0, 0, 10, 1),
            Foreground = (Brush)Application.Current.FindResource("AccentBrush")
        };
        public readonly TextBlock Value = new()
        {
            FontFamily = ValueFont,
            FontSize = 13,
            Margin = new Thickness(0, 0, 8, 1),
            TextAlignment = TextAlignment.Right,
            MinWidth = 64
        };
        public readonly Canvas Graph = new() { Width = GraphWidth, Height = GraphHeight, VerticalAlignment = VerticalAlignment.Center };
        private readonly Polyline _line = new()
        {
            Stroke = (Brush)Application.Current.FindResource("AccentBrush"),
            StrokeThickness = 1.3,
            StrokeLineJoin = PenLineJoin.Round
        };

        public Row() => Graph.Children.Add(_line);

        public void Update(PerfOverlayLine l)
        {
            Label.Text = l.Label;
            Value.Text = l.Value;
            Value.Foreground = l.Critical
                ? (Brush)Application.Current.FindResource("DangerBrush")
                : Brushes.White;

            if (l.Graph is not { Count: > 1 } g)
            {
                Graph.Visibility = l.Graph is null ? Visibility.Collapsed : Visibility.Hidden;
                return;
            }
            Graph.Visibility = Visibility.Visible;
            // Fixed time axis: the newest value sits at the right edge, history scrolls left.
            double step = GraphWidth / (PerformanceMonitorService.HistoryLength - 1);
            var pts = new PointCollection(g.Count);
            for (int i = 0; i < g.Count; i++)
                pts.Add(new Point(GraphWidth - (g.Count - 1 - i) * step, GraphHeight - g[i] * GraphHeight));
            _line.Points = pts;
        }
    }
}
