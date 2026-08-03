using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WKI_Clipper.Services;

namespace WKI_Clipper.Views;

/// <summary>
/// Mini audio mixer for OBS: one row per audio input with a fader, dB readout and a
/// mute button. Stays in sync with OBS both ways — moving a fader in OBS updates this
/// widget, and vice versa. No settings of its own; the inputs come from OBS live.
/// </summary>
public partial class MixerView : UserControl
{
    private sealed class Row
    {
        public required FrameworkElement Root;
        public required Slider Fader;
        public required TextBlock Db;
        public required Button Mute;
        public bool Dragging;
    }

    private readonly Dictionary<string, Row> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _sendDebounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly Dictionary<string, float> _pending = new(StringComparer.OrdinalIgnoreCase);
    private bool _subscribed;

    public MixerView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _sendDebounce.Tick += (_, _) => FlushPending();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var host = App.Host;
        if (host is null) return;
        if (!_subscribed) { host.Obs.StatusChanged += OnStatus; _subscribed = true; }
        Rebuild();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var host = App.Host;
        if (host != null && _subscribed) { host.Obs.StatusChanged -= OnStatus; _subscribed = false; }
        _sendDebounce.Stop();
        FlushPending();   // don't lose the last fader move when the widget closes
    }

    // Service events arrive on socket worker threads.
    private void OnStatus() => Dispatcher.BeginInvoke(new Action(() => { if (IsLoaded) Rebuild(); }));

    private void Rebuild()
    {
        var host = App.Host;
        if (host is null) return;
        bool connected = host.Obs.IsConnected;
        var st = host.Obs.Status;
        var names = host.Obs.ListAudioInputNames();

        if (!connected || names.Count == 0)
        {
            Rows.Children.Clear();
            _rows.Clear();
            StatusLine.Text = connected
                ? L.T("Keine Audio-Eingänge gemeldet.", "No audio inputs reported.")
                : L.T("OBS ist nicht verbunden — Regler sind inaktiv.", "OBS is not connected — faders are inactive.");
            return;
        }
        StatusLine.Text = "";

        // Drop rows for inputs that vanished (scene collection switched).
        foreach (var gone in _rows.Keys.Where(k => !names.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            Rows.Children.Remove(_rows[gone].Root);
            _rows.Remove(gone);
        }

        foreach (var name in names)
        {
            if (!_rows.TryGetValue(name, out var row))
            {
                row = BuildRow(name);
                _rows[name] = row;
                Rows.Children.Add(row.Root);
            }
            // Never fight the user's finger: skip updates while they drag this fader.
            if (!row.Dragging && st.InputVolume.TryGetValue(name, out float mul))
            {
                row.Fader.ValueChanged -= OnFaderChanged;
                row.Fader.Value = Math.Clamp(mul, 0, 1);
                row.Fader.ValueChanged += OnFaderChanged;
                row.Db.Text = ObsVolume.FormatDb(mul);
            }
            bool muted = st.InputMuted.TryGetValue(name, out bool m) && m;
            row.Mute.Content = muted ? "🔇" : "🔊";
            row.Mute.Foreground = muted
                ? (Brush)FindResource("DangerBrush")
                : (Brush)FindResource("TextBrush");
        }
    }

    private Row BuildRow(string name)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var db = new TextBlock { Foreground = (Brush)FindResource("MutedBrush"), MinWidth = 60, TextAlignment = TextAlignment.Right };
        Grid.SetColumn(db, 1);
        header.Children.Add(title);
        header.Children.Add(db);
        stack.Children.Add(header);

        var faderRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        faderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        faderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var fader = new Slider { Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, Tag = name };
        fader.ValueChanged += OnFaderChanged;
        // Track drag state so incoming OBS echoes don't yank the knob mid-move.
        fader.AddHandler(System.Windows.Controls.Primitives.Thumb.DragStartedEvent,
            new System.Windows.Controls.Primitives.DragStartedEventHandler((_, _) => SetDragging(name, true)));
        fader.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
            new System.Windows.Controls.Primitives.DragCompletedEventHandler((_, _) => { SetDragging(name, false); FlushPending(); }));

        var mute = new Button
        {
            Content = "🔊", Width = 34, Margin = new Thickness(8, 0, 0, 0),
            Background = (Brush)FindResource("PanelHoverBrush"), Tag = name,
            ToolTip = L.T("Stumm schalten", "Toggle mute")
        };
        mute.Click += (s, args) =>
        {
            if (App.Host is { } h && s is Button b && (string?)b.Tag is { } n)
                _ = h.Obs.ToggleInputMuteAsync(n);
        };
        Grid.SetColumn(mute, 1);
        faderRow.Children.Add(fader);
        faderRow.Children.Add(mute);
        stack.Children.Add(faderRow);

        var card = new Border
        {
            Background = (Brush)FindResource("PanelBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 10),
            Margin = new Thickness(0, 0, 0, 6),
            Child = stack
        };
        return new Row { Root = card, Fader = fader, Db = db, Mute = mute };
    }

    private void SetDragging(string name, bool value)
    {
        if (_rows.TryGetValue(name, out var r)) r.Dragging = value;
    }

    private void OnFaderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is not Slider s || (string?)s.Tag is not { } name) return;
        float mul = (float)Math.Clamp(e.NewValue, 0, 1);
        if (_rows.TryGetValue(name, out var row)) row.Db.Text = ObsVolume.FormatDb(mul);
        // Coalesce: a drag fires per pixel, OBS only needs the latest value.
        _pending[name] = mul;
        _sendDebounce.Stop();
        _sendDebounce.Start();
    }

    private void FlushPending()
    {
        _sendDebounce.Stop();
        if (_pending.Count == 0) return;
        var host = App.Host;
        var snapshot = _pending.ToList();
        _pending.Clear();
        if (host is null) return;
        foreach (var (name, mul) in snapshot) _ = host.Obs.SetInputVolumeMulAsync(name, mul);
    }
}
