using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using WKI_Clipper.Models;
using WKI_Clipper.Services;

namespace WKI_Clipper.Views;

/// <summary>
/// Stream music player widget: transport, the two independent levels (what listeners
/// hear vs. what you hear), output device pickers and the track list.
/// </summary>
public partial class MusicView : UserControl
{
    private Button? _playBtn;
    private ToggleButton? _shuffleBtn, _repeatBtn, _monitorBtn;
    private Slider? _mainSlider, _monitorSlider;
    private FrameworkElement? _monitorRow;
    private bool _subscribed;

    public MusicView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static MusicSettings Cfg => App.Host!.Settings.Current.Music;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var host = App.Host;
        if (host is null) return;
        if (Transport.Children.Count == 0) BuildUi(host);
        if (!_subscribed) { host.Music.StateChanged += OnState; _subscribed = true; }
        RefreshAll();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var host = App.Host;
        if (host != null && _subscribed) { host.Music.StateChanged -= OnState; _subscribed = false; }
    }

    private void OnState() => Dispatcher.BeginInvoke(new Action(() => { if (IsLoaded) RefreshAll(); }));

    private void BuildUi(AppHost host)
    {
        // --- transport ---
        _playBtn = TransportButton("▶", () => host.Music.TogglePlayPause());
        Transport.Children.Add(TransportButton("⏮", () => host.Music.Prev()));
        Transport.Children.Add(_playBtn);
        Transport.Children.Add(TransportButton("⏭", () => host.Music.Next()));
        Transport.Children.Add(TransportButton("⏹", () => host.Music.Stop()));

        _shuffleBtn = ToggleChip("🔀", L.T("Zufall", "Shuffle"), Cfg.Shuffle, on => host.Music.SetShuffle(on));
        _repeatBtn = ToggleChip("🔁", L.T("Wiederholen", "Repeat"), Cfg.Repeat, on => host.Music.SetRepeat(on));
        Transport.Children.Add(_shuffleBtn);
        Transport.Children.Add(_repeatBtn);

        // --- stream level ---
        _mainSlider = new Slider { Minimum = 0, Maximum = 1, Value = Cfg.Volume, VerticalAlignment = VerticalAlignment.Center };
        _mainSlider.ValueChanged += (_, ev) => host.Music.SetMainVolume((float)ev.NewValue);
        LevelsPanel.Children.Add(LabeledSlider(
            L.T("Stream-Lautstärke", "Stream volume"),
            L.T("Was die Zuschauer hören", "What viewers hear"), _mainSlider));
        LevelsPanel.Children.Add(DeviceRow(host, L.T("Ausgabe (Stream)", "Output (stream)"),
            () => Cfg.OutputDeviceId, v => { Cfg.OutputDeviceId = v; host.Settings.Save(); }));

        // --- monitor ---
        _monitorBtn = ToggleChip("🎧", L.T("Mithören", "Monitor"), Cfg.MonitorEnabled, on =>
        {
            host.Music.SetMonitorEnabled(on);
            if (_monitorRow != null) _monitorRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        });
        _monitorBtn.Margin = new Thickness(0, 10, 0, 0);
        LevelsPanel.Children.Add(_monitorBtn);

        _monitorSlider = new Slider { Minimum = 0, Maximum = 1, Value = Cfg.MonitorVolume, VerticalAlignment = VerticalAlignment.Center };
        _monitorSlider.ValueChanged += (_, ev) => host.Music.SetMonitorVolume((float)ev.NewValue);
        var monStack = new StackPanel();
        monStack.Children.Add(LabeledSlider(
            L.T("Monitor-Lautstärke", "Monitor volume"),
            L.T("Was DU hörst — unabhängig vom Stream", "What YOU hear — independent of the stream"), _monitorSlider));
        monStack.Children.Add(DeviceRow(host, L.T("Ausgabe (Monitor)", "Output (monitor)"),
            () => Cfg.MonitorDeviceId, v => { Cfg.MonitorDeviceId = v; host.Settings.Save(); }));
        _monitorRow = monStack;
        _monitorRow.Visibility = Cfg.MonitorEnabled ? Visibility.Visible : Visibility.Collapsed;
        LevelsPanel.Children.Add(_monitorRow);

        // --- footer: folder + reload ---
        var folderRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        var folderBtn = new Button { Content = L.T("Ordner…", "Folder…"), Padding = new Thickness(10, 5, 10, 5) };
        folderBtn.Click += (_, _) => PickFolder(host);
        var reloadBtn = new Button { Content = L.T("Neu einlesen", "Rescan"), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(6, 0, 0, 0) };
        reloadBtn.Click += (_, _) => host.Music.ReloadPlaylist();
        folderRow.Children.Add(folderBtn);
        folderRow.Children.Add(reloadBtn);
        FooterPanel.Children.Add(folderRow);
        FooterPanel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("MutedStyle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            Text = L.T($"„Jetzt läuft\" wird geschrieben nach: {Cfg.NowPlayingFile}",
                       $"\"Now playing\" is written to: {Cfg.NowPlayingFile}")
        });
    }

    // ---- refresh ----

    private void RefreshAll()
    {
        var host = App.Host;
        if (host is null) return;

        var track = host.Music.CurrentTrack;
        if (track is null)
        {
            TitleText.Text = L.T("Nichts ausgewählt", "Nothing selected");
            ArtistText.Text = host.Music.Tracks.Count == 0
                ? L.T("Keine Titel im Ordner gefunden.", "No tracks found in the folder.")
                : L.T("Titel anklicken oder ▶ drücken.", "Click a track or press ▶.");
        }
        else
        {
            var (artist, title) = TrackName.Parse(track);
            TitleText.Text = title;
            ArtistText.Text = artist.Length > 0 ? artist : L.T("(kein Interpret im Dateinamen)", "(no artist in file name)");
        }

        if (_playBtn != null) _playBtn.Content = host.Music.IsPlaying ? "⏸" : "▶";
        if (_shuffleBtn != null) _shuffleBtn.IsChecked = host.Music.Shuffle;
        if (_repeatBtn != null) _repeatBtn.IsChecked = host.Music.Repeat;

        BuildTrackList(host);
    }

    private void BuildTrackList(AppHost host)
    {
        TrackList.Children.Clear();
        int current = host.Music.CurrentIndex;
        for (int i = 0; i < host.Music.Tracks.Count; i++)
        {
            int idx = i;
            var (artist, title) = TrackName.Parse(host.Music.Tracks[i]);
            var text = artist.Length > 0 ? $"{artist} — {title}" : title;
            var item = new Border
            {
                Background = idx == current ? (Brush)FindResource("AccentDimBrush") : Brushes.Transparent,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 0, 2),
                Cursor = Cursors.Hand,
                Child = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis }
            };
            item.MouseLeftButtonUp += (_, _) => host.Music.PlayIndex(idx);
            TrackList.Children.Add(item);
        }
    }

    // ---- small builders ----

    private Button TransportButton(string glyph, Action action)
    {
        var b = new Button { Content = glyph, Width = 40, Height = 32, Margin = new Thickness(0, 0, 6, 0), FontSize = 14 };
        b.Click += (_, _) => action();
        return b;
    }

    private ToggleButton ToggleChip(string glyph, string tip, bool initial, Action<bool> onChange)
    {
        var t = new ToggleButton
        {
            Content = glyph, Width = 40, Height = 32, Margin = new Thickness(0, 0, 6, 0), FontSize = 14,
            IsChecked = initial, ToolTip = tip, Cursor = Cursors.Hand,
            Foreground = (Brush)FindResource("TextBrush"),
            Template = (ControlTemplate)Application.Current.FindResource("LauncherToggleTemplate")
        };
        t.Checked += (_, _) => onChange(true);
        t.Unchecked += (_, _) => onChange(false);
        return t;
    }

    private FrameworkElement LabeledSlider(string label, string hint, Slider slider)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        sp.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold });
        sp.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("MutedStyle") });
        sp.Children.Add(slider);
        return sp;
    }

    private FrameworkElement DeviceRow(AppHost host, string label, Func<string?> get, Action<string?> set)
    {
        var box = new ComboBox { MinWidth = 200 };
        var devices = host.AudioDevices.ListRenderDevices().ToList();
        box.Items.Add(new DeviceChoice(null, L.T("(Standardgerät)", "(default device)")));
        foreach (var d in devices) box.Items.Add(new DeviceChoice(d.Id, d.Name));

        string? cur = get();
        box.SelectedIndex = 0;
        for (int i = 0; i < box.Items.Count; i++)
            if (((DeviceChoice)box.Items[i]!).Id == cur) { box.SelectedIndex = i; break; }

        box.SelectionChanged += (_, _) => { if (box.SelectedItem is DeviceChoice c) set(c.Id); };

        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        sp.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("MutedStyle") });
        sp.Children.Add(box);
        return sp;
    }

    private void PickFolder(AppHost host)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            SelectedPath = SettingsService.ExpandPath(Cfg.Folder),
            Description = L.T("Musik-Ordner wählen", "Choose the music folder")
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        Cfg.Folder = dlg.SelectedPath;
        host.Settings.Save();
        host.Music.ReloadPlaylist();
    }

    private sealed record DeviceChoice(string? Id, string Name)
    {
        public override string ToString() => Name;
    }
}
