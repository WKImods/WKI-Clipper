using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WKI_Clipper.Services;

namespace WKI_Clipper.Views;

/// <summary>
/// CPU / GPU / RAM / VRAM live bars. Reference-counts the shared
/// <see cref="PerformanceMonitorService"/> so polling only runs while this widget is
/// actually on screen. No FPS (deliberately out of scope).
/// </summary>
public partial class PerformanceView : UserControl
{
    private MetricRow? _cpu, _gpu, _ram, _vram;
    private bool _subscribed;

    public PerformanceView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Rows.Children.Count == 0)
        {
            _cpu  = new MetricRow("CPU");
            _gpu  = new MetricRow("GPU");
            _ram  = new MetricRow("RAM");
            _vram = new MetricRow("VRAM");
            Rows.Children.Add(_cpu.Root);
            Rows.Children.Add(_gpu.Root);
            Rows.Children.Add(_ram.Root);
            Rows.Children.Add(_vram.Root);
        }

        var host = App.Host;
        if (host is null) return;

        host.Performance.Sampled += OnSample;
        host.Performance.AddViewer();
        _subscribed = true;
        OnSample(host.Performance.Last); // paint the primed value at once
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var host = App.Host;
        if (host != null && _subscribed)
        {
            host.Performance.Sampled -= OnSample;
            host.Performance.RemoveViewer();
            _subscribed = false;
        }
    }

    private void OnSample(PerfSample s)
    {
        // Marshal to the UI thread — the service raises Sampled from a timer thread.
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => OnSample(s))); return; }
        if (!IsLoaded) return;

        _cpu!.Update(s.CpuPercent, $"{s.CpuPercent:F0} %");
        _gpu!.Update(s.GpuPercent, $"{s.GpuPercent:F0} %");

        double ramPct = s.RamTotalBytes > 0 ? (double)s.RamUsedBytes / s.RamTotalBytes * 100 : 0;
        _ram!.Update(ramPct, $"{Gb(s.RamUsedBytes):F1} / {Gb(s.RamTotalBytes):F1} GB");

        // VRAM has no reliable total from counters — show absolute usage, bar scaled
        // to a 24 GB reference so it stays meaningful across cards without lying about a total.
        double vramRefPct = Math.Min(100, Gb(s.VramUsedBytes) / 24.0 * 100);
        _vram!.Update(vramRefPct, $"{Gb(s.VramUsedBytes):F1} GB", showBarOnly: true);
    }

    private static double Gb(ulong bytes) => bytes / (1024.0 * 1024 * 1024);

    /// <summary>Label + track/fill bar + value text. Bar uses star-sized columns so it scales with width.</summary>
    private sealed class MetricRow
    {
        public readonly Border Root;
        private readonly ColumnDefinition _fillCol;
        private readonly ColumnDefinition _restCol;
        private readonly Border _fill;
        private readonly TextBlock _value;

        public MetricRow(string label)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var lbl = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold };
            _value = new TextBlock { Foreground = (Brush)Application.Current.FindResource("MutedBrush") };
            Grid.SetColumn(_value, 1);
            header.Children.Add(lbl);
            header.Children.Add(_value);
            stack.Children.Add(header);

            var track = new Border
            {
                Height = 8,
                Margin = new Thickness(0, 5, 0, 0),
                CornerRadius = new CornerRadius(4),
                Background = (Brush)Application.Current.FindResource("PanelHoverBrush"),
                ClipToBounds = true
            };
            var barGrid = new Grid();
            _fillCol = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
            _restCol = new ColumnDefinition { Width = new GridLength(100, GridUnitType.Star) };
            barGrid.ColumnDefinitions.Add(_fillCol);
            barGrid.ColumnDefinitions.Add(_restCol);
            _fill = new Border { CornerRadius = new CornerRadius(4), Background = (Brush)Application.Current.FindResource("AccentBrush") };
            Grid.SetColumn(_fill, 0);
            barGrid.Children.Add(_fill);
            track.Child = barGrid;
            stack.Children.Add(track);

            Root = new Border
            {
                Background = (Brush)Application.Current.FindResource("PanelBrush"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Child = stack
            };
        }

        public void Update(double percent, string valueText, bool showBarOnly = false)
        {
            percent = Math.Min(100, Math.Max(0, percent));
            _fillCol.Width = new GridLength(percent, GridUnitType.Star);
            _restCol.Width = new GridLength(100 - percent, GridUnitType.Star);
            _value.Text = valueText;
            // Turn red when a real load metric is critical (not for the VRAM reference bar).
            _fill.Background = (!showBarOnly && percent >= 90)
                ? (Brush)Application.Current.FindResource("DangerBrush")
                : (Brush)Application.Current.FindResource("AccentBrush");
        }
    }
}
