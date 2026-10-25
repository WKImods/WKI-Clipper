using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WKI_Clipper.Models;
using WKI_Clipper.Services;

namespace WKI_Clipper.Views;

/// <summary>
/// CPU / GPU / GPU temperature / RAM / VRAM live bars, the GPU clock / voltage / VRAM clock, the FPS
/// values (while ticked), plus the settings of the on-screen overlay (corner, size, columns, colours,
/// background, which values, graphs, visibility in captures). Reference-counts the shared
/// <see cref="PerformanceMonitorService"/> so polling only runs while this widget or the overlay is on
/// screen, and holds an FPS lease only while an FPS value is ticked and this widget is shown.
/// </summary>
public partial class PerformanceView : UserControl
{
    private MetricRow? _cpu, _gpu, _gpuTemp, _ram, _vram;
    private ValueRow? _gpuClock, _gpuVoltage, _vramClock;
    private FpsCard? _fps;
    private bool _subscribed;
    private IDisposable? _fpsLease;
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
            _fps     = new FpsCard();
            _cpu     = new MetricRow("CPU");
            _gpu     = new MetricRow("GPU");
            _gpuTemp = new MetricRow(L.T("GPU-Temperatur", "GPU temperature"));
            _ram     = new MetricRow("RAM");
            _vram    = new MetricRow("VRAM");
            Rows.Children.Add(_fps.Root);
            Rows.Children.Add(_cpu.Root);
            Rows.Children.Add(_gpu.Root);
            Rows.Children.Add(_gpuTemp.Root);
            Rows.Children.Add(BuildGpuDetails());
            Rows.Children.Add(_ram.Root);
            Rows.Children.Add(_vram.Root);
            BuildOverlaySettings();
        }

        var host = App.Host;
        if (host is null || _subscribed) return;

        host.Performance.Sampled += OnSample;
        host.Performance.AddViewer();
        _subscribed = true;
        SyncFpsLease();
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
        ReleaseFpsLease();
    }

    // ---- FPS measurement (opt-in) ----

    /// <summary>Takes or gives back the FPS measurement: only for a ticked FPS value while this widget is shown.</summary>
    private void SyncFpsLease()
    {
        var host = App.Host;
        if (host is null) return;
        bool want = _subscribed && PerfOverlayLayout.ShouldMeasureFps(Cfg, overlayShown: false, widgetShown: true);
        if (want && _fpsLease is null)
        {
            try { _fpsLease = host.FrameTimes.Subscribe(); }
            catch (ObjectDisposedException) { /* shutting down */ }
        }
        else if (!want) ReleaseFpsLease();
    }

    private void ReleaseFpsLease()
    {
        _fpsLease?.Dispose();
        _fpsLease = null;
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

        string na = L.T("k. A.", "n/a");
        _gpuClock!.Set(s.GpuClockMhz is double mhz ? $"{mhz:F0} MHz" : na);
        _gpuVoltage!.Set(s.GpuVoltageMv is uint mv ? $"{mv} mV" : na);
        _vramClock!.Set(s.VramClockMhz is double vmhz ? $"{vmhz:F0} MHz" : na);

        _ram!.Update(s.RamPercent, $"{Gb(s.RamUsedBytes):F1} / {Gb(s.RamTotalBytes):F1} GB");

        if (s.VramPercent is double vp)
            _vram!.Update(vp, $"{Gb(s.VramUsedBytes):F1} / {Gb(s.VramTotalBytes):F1} GB");
        else
            // No size reported: bar against a 24 GB reference, without claiming a total.
            _vram!.Update(Math.Min(100, Gb(s.VramUsedBytes) / 24.0 * 100), $"{Gb(s.VramUsedBytes):F1} GB", critical: false);

        UpdateFps();
    }

    private void UpdateFps()
    {
        var host = App.Host;
        bool any = PerfOverlayLayout.AnyFpsValue(Cfg);
        _fps!.Root.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (!any || host is null) return;

        var frame = host.FrameTimes.Current;
        _fps.Update(Cfg, frame, host.CurrentGame?.DisplayName);
    }

    private static double Gb(ulong bytes) => bytes / (1024.0 * 1024 * 1024);

    private UIElement BuildGpuDetails()
    {
        _gpuClock   = new ValueRow(L.T("GPU-Takt", "GPU clock"));
        _gpuVoltage = new ValueRow(L.T("GPU-Spannung", "GPU voltage"));
        _vramClock  = new ValueRow(L.T("VRAM-Takt", "VRAM clock"));

        var stack = new StackPanel();
        stack.Children.Add(_gpuClock.Root);
        stack.Children.Add(_gpuVoltage.Root);
        stack.Children.Add(_vramClock.Root);
        stack.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("MutedStyle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            Text = L.T("Board-Leistung, Speicher-Temperatur und CPU-Temperatur/-Spannung/-Leistung gibt es nicht: Sie bräuchten ein Hersteller-SDK oder einen Kernel-Treiber — den melden Anti-Cheat-Systeme, deshalb bewusst nicht genutzt.",
                       "Board power, memory temperature and CPU temperature / voltage / power are not available: they would need a vendor SDK or a kernel driver, which anti-cheat systems flag — deliberately not used.")
        });
        return Card(stack);
    }

    private static Border Card(UIElement child) => new()
    {
        Background = (Brush)Application.Current.FindResource("PanelBrush"),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(14, 10, 14, 10),
        Margin = new Thickness(0, 0, 0, 8),
        Child = child
    };

    // ---- overlay settings ----

    private void BuildOverlaySettings()
    {
        OverlayPanel.Children.Add(new TextBlock
        {
            Text = L.T("Bildschirm-Overlay", "On-screen overlay"),
            FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 4, 0, 2)
        });
        OverlayPanel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("MutedStyle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Text = L.T("Die Zahlen in einer Bildschirmecke, ohne Fenster — auch im Spiel, Klicks gehen durch. Per Hotkey schaltbar (im Hotkey-Tab belegen).",
                       "The numbers in a screen corner, no window — also in game, clicks pass through. Toggle by hotkey (bind it in the hotkey tab).")
        });

        OverlayPanel.Children.Add(Check(L.T("Overlay anzeigen", "Show overlay"), Cfg.Enabled, v => Cfg.Enabled = v, bold: true));
        var onlyGame = Check(L.T("Nur bei erkanntem Spiel zeigen", "Only show while a game is detected"), Cfg.OnlyWhenGame, v => Cfg.OnlyWhenGame = v);
        onlyGame.ToolTip = L.T("Das Overlay erscheint, sobald ein Spiel im Vordergrund ist (Vollbild oder dein Fenster-Ziel), und verschwindet danach wieder.",
                               "The overlay appears while a game is in front (fullscreen, or your window target) and goes away afterwards.");
        OverlayPanel.Children.Add(onlyGame);

        // Corner (the full position list with "Free" follows with the drag mode)
        (OverlayAnchor Value, string Text)[] corners =
        {
            (OverlayAnchor.TopLeft, L.T("Oben links", "Top left")),
            (OverlayAnchor.TopRight, L.T("Oben rechts", "Top right")),
            (OverlayAnchor.BottomLeft, L.T("Unten links", "Bottom left")),
            (OverlayAnchor.BottomRight, L.T("Unten rechts", "Bottom right")),
        };
        OverlayPanel.Children.Add(Labeled(L.T("Ecke", "Corner"),
            Combo(corners, Cfg.Anchor, v => Cfg.Anchor = v)));

        OverlayPanel.Children.Add(Labeled(L.T("Größe", "Size"),
            SliderRow(0.75, 2.0, PerfOverlayLayout.ClampScale(Cfg.Scale), v => $"{v * 100:F0} %",
                      v => Cfg.Scale = Math.Round(v, 2))));
        OverlayPanel.Children.Add(Labeled(L.T("Grafikgröße", "Graph size"),
            SliderRow(0.5, 2.0, PerfOverlayLayout.ClampGraphScale(Cfg.GraphScale), v => $"{v * 100:F0} %",
                      v => Cfg.GraphScale = Math.Round(v, 2))));

        (int Value, string Text)[] columns = { (1, "1"), (2, "2"), (3, "3") };
        OverlayPanel.Children.Add(Labeled(L.T("Spalten", "Columns"),
            Combo(columns, PerfOverlayLayout.ClampColumns(Cfg.Columns), v => Cfg.Columns = v)));

        (OverlayBackground Value, string Text)[] backgrounds =
        {
            (OverlayBackground.Off, L.T("Aus", "Off")),
            (OverlayBackground.Black, L.T("Schwarz", "Black")),
            (OverlayBackground.Violet, L.T("Violett", "Violet")),
        };
        OverlayPanel.Children.Add(Labeled(L.T("Hintergrund", "Background"),
            Combo(backgrounds, Cfg.Background, v => Cfg.Background = v)));
        OverlayPanel.Children.Add(Labeled(L.T("Transparenz", "Transparency"),
            SliderRow(0, 100, PerfOverlayLayout.ClampTransparency(Cfg.BackgroundTransparency), v => $"{v:F0} %",
                      v => Cfg.BackgroundTransparency = (int)Math.Round(v))));

        (OverlayColor Value, string Text)[] colors =
        {
            (OverlayColor.White, L.T("Weiß", "White")),
            (OverlayColor.Gray, L.T("Grau", "Gray")),
            (OverlayColor.Orange, L.T("WKI-Orange", "WKI orange")),
            (OverlayColor.Green, L.T("Grün", "Green")),
            (OverlayColor.Cyan, "Cyan"),
            (OverlayColor.Yellow, L.T("Gelb", "Yellow")),
            (OverlayColor.Red, L.T("Rot", "Red")),
        };
        OverlayPanel.Children.Add(Labeled(L.T("Farbe Name", "Name colour"),
            Combo(colors, Cfg.NameColor, v => Cfg.NameColor = v, swatch: PerfOverlayLayout.ColorOf)));
        OverlayPanel.Children.Add(Labeled(L.T("Farbe Wert", "Value colour"),
            Combo(colors, Cfg.ValueColor, v => Cfg.ValueColor = v, swatch: PerfOverlayLayout.ColorOf)));

        // Values
        OverlayPanel.Children.Add(new TextBlock { Text = L.T("Werte", "Values"), Style = (Style)FindResource("MutedStyle"), Margin = new Thickness(0, 8, 0, 2) });
        var values = new WrapPanel();
        values.Children.Add(Check("FPS", Cfg.ShowFps, v => Cfg.ShowFps = v));
        values.Children.Add(Check(L.T("Framezeit", "Frame time"), Cfg.ShowFrameTime, v => Cfg.ShowFrameTime = v));
        values.Children.Add(Check("1 % Low", Cfg.ShowLow1, v => Cfg.ShowLow1 = v));
        values.Children.Add(Check(L.T("0,1 % Low", "0.1 % low"), Cfg.ShowLow01, v => Cfg.ShowLow01 = v));
        values.Children.Add(Check(L.T("Mikro-Stutter", "Micro-stutter"), Cfg.ShowStutter, v => Cfg.ShowStutter = v));
        values.Children.Add(Check("CPU", Cfg.ShowCpu, v => Cfg.ShowCpu = v));
        values.Children.Add(Check("GPU", Cfg.ShowGpu, v => Cfg.ShowGpu = v));
        values.Children.Add(Check(L.T("GPU-Takt", "GPU clock"), Cfg.ShowGpuClock, v => Cfg.ShowGpuClock = v));
        values.Children.Add(Check(L.T("GPU-Spannung", "GPU voltage"), Cfg.ShowGpuVoltage, v => Cfg.ShowGpuVoltage = v));
        values.Children.Add(Check(L.T("GPU-Temp", "GPU temp"), Cfg.ShowGpuTemp, v => Cfg.ShowGpuTemp = v));
        values.Children.Add(Check(L.T("GPU-Lüfter", "GPU fan"), Cfg.ShowGpuFan, v => Cfg.ShowGpuFan = v));
        values.Children.Add(Check("RAM", Cfg.ShowRam, v => Cfg.ShowRam = v));
        values.Children.Add(Check("VRAM", Cfg.ShowVram, v => Cfg.ShowVram = v));
        values.Children.Add(Check(L.T("VRAM-Takt", "VRAM clock"), Cfg.ShowVramClock, v => Cfg.ShowVramClock = v));
        values.Children.Add(Check(L.T("Uhrzeit", "Clock"), Cfg.ShowClock, v => Cfg.ShowClock = v));
        OverlayPanel.Children.Add(values);
        OverlayPanel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("MutedStyle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4),
            Text = L.T("FPS-Werte starten eine passive Messung (wie Adrenalin und die Xbox Game Bar) — nur solange einer angehakt und sichtbar ist. Nichts wird in das Spiel eingeschleust.",
                       "FPS values start a passive measurement (like Adrenalin and the Xbox Game Bar) — only while one is ticked and visible. Nothing is injected into the game.")
        });

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

    /// <summary>A drop-down over (value, text) pairs; <paramref name="swatch"/> adds a colour dot before the text.</summary>
    private ComboBox Combo<T>((T Value, string Text)[] items, T selected, Action<T> set, Func<T, Rgba>? swatch = null) where T : notnull
    {
        var combo = new ComboBox { MinWidth = 160, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        foreach (var (value, text) in items)
        {
            object content = text;
            if (swatch != null)
            {
                var c = swatch(value);
                var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                row.Children.Add(new Ellipse
                {
                    Width = 12, Height = 12, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                    Fill = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B)),
                    Stroke = (Brush)FindResource("BorderBrush"), StrokeThickness = 1
                });
                row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
                content = row;
            }
            combo.Items.Add(new ComboBoxItem { Content = content, Tag = value });
        }
        combo.SelectedIndex = Math.Max(0, Array.FindIndex(items, i => EqualityComparer<T>.Default.Equals(i.Value, selected)));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: T v }) { set(v); Apply(); }
        };
        return combo;
    }

    /// <summary>A slider with its value written next to it; drags apply live and save debounced.</summary>
    private FrameworkElement SliderRow(double min, double max, double value, Func<double, string> format, Action<double> set)
    {
        var text = new TextBlock { Style = (Style)FindResource("MutedStyle"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), MinWidth = 40 };
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, Width = 160, VerticalAlignment = VerticalAlignment.Center };
        text.Text = format(slider.Value);
        slider.ValueChanged += (_, ev) =>
        {
            set(ev.NewValue);
            text.Text = format(ev.NewValue);
            Apply(debounceSave: true);
        };
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        row.Children.Add(slider);
        row.Children.Add(text);
        return row;
    }

    private static FrameworkElement Labeled(string label, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
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
        SyncFpsLease();   // a freshly ticked / unticked FPS value starts / stops the measurement
        if (!debounceSave) { host.Settings.Save(); return; }
        if (_saveTimer is null)
        {
            _saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); App.Host.Settings.Save(); };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Label on the left, value on the right — one line of a card.</summary>
    private sealed class ValueRow
    {
        public readonly Grid Root;
        private readonly TextBlock _value;

        public ValueRow(string label)
        {
            Root = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var lbl = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold };
            _value = new TextBlock { Foreground = (Brush)Application.Current.FindResource("MutedBrush") };
            Grid.SetColumn(_value, 1);
            Root.Children.Add(lbl);
            Root.Children.Add(_value);
        }

        public void Set(string text) => _value.Text = text;
    }

    /// <summary>
    /// The FPS values of the game in front: a line per ticked value, the game's name, and — when the
    /// measurement is refused — how to get the permission. Collapsed while no FPS value is ticked.
    /// </summary>
    private sealed class FpsCard
    {
        public readonly Border Root;
        private readonly TextBlock _game;
        private readonly TextBlock _hint;
        private readonly Dictionary<FpsValue, ValueRow> _rows = new();

        public FpsCard()
        {
            var stack = new StackPanel();

            var header = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock { Text = "FPS", FontWeight = FontWeights.SemiBold, FontSize = 14 });
            _game = new TextBlock { Foreground = (Brush)Application.Current.FindResource("MutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220 };
            Grid.SetColumn(_game, 1);
            header.Children.Add(_game);
            stack.Children.Add(header);

            foreach (var kind in Enum.GetValues<FpsValue>())
            {
                var row = new ValueRow(PerfOverlayLayout.FrameLabel(kind));
                _rows[kind] = row;
                stack.Children.Add(row.Root);
            }

            _hint = new TextBlock
            {
                Style = (Style)Application.Current.FindResource("MutedStyle"), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed,
                Text = L.T("Für FPS braucht dein Windows-Konto die Gruppe „Leistungsprotokollbenutzer“ (Computerverwaltung → Lokale Benutzer und Gruppen → Gruppen → Leistungsprotokollbenutzer → Hinzufügen). Danach einmal ab- und wieder anmelden.",
                           "FPS needs your Windows account to be a member of “Performance Log Users” (Computer Management → Local Users and Groups → Groups → Performance Log Users → Add). Then sign out and back in.")
            };
            stack.Children.Add(_hint);

            Root = Card(stack);
            Root.Visibility = Visibility.Collapsed;
        }

        public void Update(PerfOverlaySettings s, FrameMetricsSnapshot? frame, string? gameName)
        {
            _game.Text = gameName ?? L.T("Kein Spiel erkannt", "No game detected");
            foreach (var (kind, row) in _rows)
            {
                bool ticked = kind switch
                {
                    FpsValue.Fps => s.ShowFps,
                    FpsValue.FrameTime => s.ShowFrameTime,
                    FpsValue.Low1 => s.ShowLow1,
                    FpsValue.Low01 => s.ShowLow01,
                    _ => s.ShowStutter,
                };
                row.Root.Visibility = ticked ? Visibility.Visible : Visibility.Collapsed;
                if (ticked) row.Set(PerfOverlayLayout.FrameValueText(kind, frame));
            }
            _hint.Visibility = frame?.Status == FrameMetricsStatus.NoPermission ? Visibility.Visible : Visibility.Collapsed;
        }
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
