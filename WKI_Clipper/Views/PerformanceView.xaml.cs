using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WKI_Clipper.Models;
using WKI_Clipper.Services;

namespace WKI_Clipper.Views;

/// <summary>
/// CPU / GPU / GPU temperature / RAM / VRAM live bars, plus the settings of the on-screen
/// overlay (corner, size, which values, graphs, visibility in captures). Reference-counts
/// the shared <see cref="PerformanceMonitorService"/> so polling only runs while this
/// widget or the overlay is on screen. No FPS (deliberately out of scope).
/// </summary>
public partial class PerformanceView : UserControl
{
    private MetricRow? _cpu, _gpu, _gpuTemp, _ram, _vram;
    private bool _subscribed;
    private System.Windows.Threading.DispatcherTimer? _saveTimer;

    private static PerfOverlaySettings Cfg => App.Host.Settings.Current.PerfOverlay;

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
            _cpu     = new MetricRow("CPU");
            _gpu     = new MetricRow("GPU");
            _gpuTemp = new MetricRow(L.T("GPU-Temperatur", "GPU temperature"));
            _ram     = new MetricRow("RAM");
            _vram    = new MetricRow("VRAM");
            Rows.Children.Add(_cpu.Root);
            Rows.Children.Add(_gpu.Root);
            Rows.Children.Add(_gpuTemp.Root);
            Rows.Children.Add(_ram.Root);
            Rows.Children.Add(_vram.Root);
            BuildOverlaySettings();
        }

        var host = App.Host;
        if (host is null || _subscribed) return;

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

        if (s.GpuTempC is double t)
        {
            _gpuTemp!.Root.Visibility = Visibility.Visible;
            string fan = s.GpuFanRpm is uint rpm ? L.T($" · Lüfter {rpm} U/min", $" · fan {rpm} rpm") : "";
            // Bar on a 30–100 °C scale; red from 85 °C.
            _gpuTemp.Update(PerfOverlayLayout.TempFraction(t) * 100, $"{t:F0} °C{fan}", critical: t >= 85);
        }
        else _gpuTemp!.Root.Visibility = Visibility.Collapsed;

        _ram!.Update(s.RamPercent, $"{Gb(s.RamUsedBytes):F1} / {Gb(s.RamTotalBytes):F1} GB");

        if (s.VramPercent is double vp)
            _vram!.Update(vp, $"{Gb(s.VramUsedBytes):F1} / {Gb(s.VramTotalBytes):F1} GB");
        else
            // No size reported: bar against a 24 GB reference, without claiming a total.
            _vram!.Update(Math.Min(100, Gb(s.VramUsedBytes) / 24.0 * 100), $"{Gb(s.VramUsedBytes):F1} GB", critical: false);
    }

    private static double Gb(ulong bytes) => bytes / (1024.0 * 1024 * 1024);

    // ---- overlay settings ----

    private void BuildOverlaySettings()
    {
        var host = App.Host;
        OverlayPanel.Children.Add(new TextBlock
        {
            Text = L.T("Bildschirm-Overlay", "On-screen overlay"),
            FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 4, 0, 2)
        });
        OverlayPanel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("MutedStyle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Text = L.T("Nur die Zahlen, ohne Fenster, in einer Bildschirmecke — auch im Spiel, Klicks gehen durch. Per Hotkey schaltbar (im Hotkey-Tab belegen).",
                       "Just the numbers, no window, in a screen corner — also in game, clicks pass through. Toggle by hotkey (bind it in the hotkey tab).")
        });

        OverlayPanel.Children.Add(Check(L.T("Overlay anzeigen", "Show overlay"), Cfg.Enabled, v => Cfg.Enabled = v, bold: true));

        // Corner
        var corner = new ComboBox { MinWidth = 160, Margin = new Thickness(0, 2, 0, 0) };
        (ScreenCorner Value, string Text)[] corners =
        {
            (ScreenCorner.TopLeft, L.T("Oben links", "Top left")),
            (ScreenCorner.TopRight, L.T("Oben rechts", "Top right")),
            (ScreenCorner.BottomLeft, L.T("Unten links", "Bottom left")),
            (ScreenCorner.BottomRight, L.T("Unten rechts", "Bottom right")),
        };
        foreach (var c in corners) corner.Items.Add(new ComboBoxItem { Content = c.Text, Tag = c.Value });
        corner.SelectedIndex = Array.FindIndex(corners, c => c.Value == Cfg.Corner);
        corner.SelectionChanged += (_, _) =>
        {
            if (corner.SelectedItem is ComboBoxItem { Tag: ScreenCorner sc }) { Cfg.Corner = sc; Apply(); }
        };
        OverlayPanel.Children.Add(Labeled(L.T("Ecke", "Corner"), corner));

        // Size
        var sizeText = new TextBlock { Style = (Style)FindResource("MutedStyle"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), MinWidth = 40 };
        var size = new Slider { Minimum = 0.75, Maximum = 2.0, Value = PerfOverlayLayout.ClampScale(Cfg.Scale), Width = 160, VerticalAlignment = VerticalAlignment.Center };
        sizeText.Text = $"{size.Value * 100:F0} %";
        size.ValueChanged += (_, ev) =>
        {
            Cfg.Scale = Math.Round(ev.NewValue, 2);
            sizeText.Text = $"{Cfg.Scale * 100:F0} %";
            Apply(debounceSave: true);
        };
        var sizeRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        sizeRow.Children.Add(size);
        sizeRow.Children.Add(sizeText);
        OverlayPanel.Children.Add(Labeled(L.T("Größe", "Size"), sizeRow));

        // Values
        OverlayPanel.Children.Add(new TextBlock { Text = L.T("Werte", "Values"), Style = (Style)FindResource("MutedStyle"), Margin = new Thickness(0, 8, 0, 2) });
        var values = new WrapPanel();
        values.Children.Add(Check("CPU", Cfg.ShowCpu, v => Cfg.ShowCpu = v));
        values.Children.Add(Check("GPU", Cfg.ShowGpu, v => Cfg.ShowGpu = v));
        values.Children.Add(Check(L.T("GPU-Temp", "GPU temp"), Cfg.ShowGpuTemp, v => Cfg.ShowGpuTemp = v));
        values.Children.Add(Check(L.T("GPU-Lüfter", "GPU fan"), Cfg.ShowGpuFan, v => Cfg.ShowGpuFan = v));
        values.Children.Add(Check("RAM", Cfg.ShowRam, v => Cfg.ShowRam = v));
        values.Children.Add(Check("VRAM", Cfg.ShowVram, v => Cfg.ShowVram = v));
        values.Children.Add(Check(L.T("Uhrzeit", "Clock"), Cfg.ShowClock, v => Cfg.ShowClock = v));
        OverlayPanel.Children.Add(values);

        OverlayPanel.Children.Add(Check(L.T("Verlaufsgraphen (letzte 60 s)", "History graphs (last 60 s)"), Cfg.ShowGraphs, v => Cfg.ShowGraphs = v));
        var inCapture = Check(L.T("In Stream & Clips sichtbar", "Visible in stream & clips"), !Cfg.HideFromCapture, v => Cfg.HideFromCapture = !v);
        inCapture.ToolTip = L.T("Aus (Standard): nur du siehst das Overlay — nicht im Stream, in Clips, Aufnahmen oder Screenshots.",
                                "Off (default): only you see the overlay — not on stream, in clips, recordings or screenshots.");
        OverlayPanel.Children.Add(inCapture);
    }

    private CheckBox Check(string text, bool initial, Action<bool> set, bool bold = false)
    {
        var cb = new CheckBox
        {
            Content = text, IsChecked = initial, Margin = new Thickness(0, 3, 14, 3),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
        };
        cb.Checked += (_, _) => { set(true); Apply(); };
        cb.Unchecked += (_, _) => { set(false); Apply(); };
        return cb;
    }

    private static FrameworkElement Labeled(string label, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(control, 1);
        grid.Children.Add(l);
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>Applies the overlay right away; slider drags save the file debounced.</summary>
    private void Apply(bool debounceSave = false)
    {
        var host = App.Host;
        host.RefreshPerfOverlay();
        if (!debounceSave) { host.Settings.Save(); return; }
        if (_saveTimer is null)
        {
            _saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); App.Host.Settings.Save(); };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

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

        /// <summary><paramref name="critical"/> null = red from 90 %.</summary>
        public void Update(double percent, string valueText, bool? critical = null)
        {
            percent = Math.Min(100, Math.Max(0, percent));
            _fillCol.Width = new GridLength(percent, GridUnitType.Star);
            _restCol.Width = new GridLength(100 - percent, GridUnitType.Star);
            _value.Text = valueText;
            _fill.Background = (critical ?? percent >= 90)
                ? (Brush)Application.Current.FindResource("DangerBrush")
                : (Brush)Application.Current.FindResource("AccentBrush");
        }
    }
}
