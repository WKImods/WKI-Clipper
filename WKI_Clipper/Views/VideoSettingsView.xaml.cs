using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Media;
using WKI_Clipper.Models;
using WKI_Clipper.Services;

namespace WKI_Clipper.Views;

public partial class VideoSettingsView : UserControl
{
    private TextBlock? _resolutionHint;
    private TextBlock? _bitrateHint;
    private System.Windows.Controls.ComboBox? _codecBox;
    private System.Windows.Controls.ComboBox? _resolutionBox;
    private System.Windows.Controls.ComboBox? _qualityBox;
    private TextBox? _customBitrateBox;
    // True while RefreshCodecBox rebuilds the list programmatically, so the
    // SelectionChanged handler ignores the induced selection changes (which
    // otherwise fire Save + buffer restart on every codec-detection tick).
    private bool _suppressCodecChange;

    public VideoSettingsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (VideoRows.Children.Count > 0) return;
        var host = App.Host;
        if (host is null) return;

        // Capture source
        var captureBox = new System.Windows.Controls.ComboBox { MinWidth = 360 };
        captureBox.Items.Add("Gesamter Bildschirm  (volle Performance, immer das was du siehst)");
        captureBox.Items.Add("Aktives Fenster beim Aufnahme-Start  (folgt dem Fenster wenn es bewegt wird)");
        captureBox.SelectedIndex = host.Settings.Current.Video.CaptureSource == CaptureSource.ActiveWindow ? 1 : 0;
        captureBox.SelectionChanged += (_, _) =>
        {
            host.Settings.Current.Video.CaptureSource =
                captureBox.SelectedIndex == 1 ? CaptureSource.ActiveWindow : CaptureSource.Display;
            host.Settings.Save();
            // Replay buffer always uses Display source per design, so no restart needed here.
        };
        VideoRows.Children.Add(LabeledRow("Aufnahme-Quelle", captureBox,
            new TextBlock
            {
                Text = "Hinweis: Bei 'Aktives Fenster' wird das Fenster gecapturet, das beim Hotkey-Druck im Vordergrund war. Solange das Fenster sichtbar ist, läuft die Aufnahme stabil. Wenn ein anderes Fenster es komplett verdeckt, capturt ffmpeg den darunter liegenden Bildschirmbereich (Windows-GDI-Limit — echte Hintergrund-Aufnahme via WGC kommt in V2). Replay-Buffer läuft immer auf dem Gesamtbildschirm.",
                Style = (Style)FindResource("MutedStyle"),
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            }));

        // Resolution
        _resolutionBox = new System.Windows.Controls.ComboBox { MinWidth = 220 };
        foreach (var preset in new[] { ResolutionPreset.Native, ResolutionPreset.FullHD, ResolutionPreset.WQHD, ResolutionPreset.UHD })
            _resolutionBox.Items.Add(LabelForResolution(preset));
        _resolutionBox.SelectedIndex = IndexFromResolution(host.Settings.Current.Video.Resolution);
        _resolutionBox.SelectionChanged += (_, _) =>
        {
            var newRes = ResolutionFromIndex(_resolutionBox.SelectedIndex);
            if (newRes == host.Settings.Current.Video.Resolution) return;
            host.Settings.Current.Video.Resolution = newRes;
            host.Settings.Save();
            UpdateBitrateHint(host);
            host.ReplayBuffer.RequestRestart();
        };
        _resolutionHint = new TextBlock { Style = (Style)FindResource("MutedStyle"), Margin = new Thickness(0, 4, 0, 0) };
        UpdateResolutionHint();
        VideoRows.Children.Add(LabeledRow("Auflösung", _resolutionBox, _resolutionHint));

        // Framerate
        var fpsBox = new System.Windows.Controls.ComboBox { MinWidth = 220 };
        foreach (var fps in new[] { 30, 60, 120 }) fpsBox.Items.Add($"{fps} fps");
        fpsBox.SelectedIndex = host.Settings.Current.Video.Framerate switch
        {
            30 => 0,
            60 => 1,
            120 => 2,
            _ => 1
        };
        fpsBox.SelectionChanged += (_, _) =>
        {
            var newFps = fpsBox.SelectedIndex switch { 0 => 30, 2 => 120, _ => 60 };
            if (newFps == host.Settings.Current.Video.Framerate) return;
            host.Settings.Current.Video.Framerate = newFps;
            host.Settings.Save();
            host.ReplayBuffer.RequestRestart();
        };
        VideoRows.Children.Add(LabeledRow("Framerate", fpsBox));

        // Codec
        _codecBox = new System.Windows.Controls.ComboBox { MinWidth = 280 };
        RefreshCodecBox(host);
        _codecBox.SelectionChanged += (_, _) =>
        {
            if (_suppressCodecChange) return;
            if (_codecBox.SelectedItem is CodecInfo c)
            {
                // Only persist + restart on a real change, not a programmatic
                // re-selection of the same codec during list refresh.
                if (c.FFmpegName == host.Settings.Current.Video.Codec) return;
                host.Settings.Current.Video.Codec = c.FFmpegName;
                host.Settings.Save();
                host.ReplayBuffer.RequestRestart();
            }
        };
        VideoRows.Children.Add(LabeledRow("Codec", _codecBox,
            new TextBlock
            {
                Text = "Wenn keine AMD-GPU-Encoder gelistet sind, läuft Codec-Detection noch — kurz warten und Settings-Tab wechseln.",
                Style = (Style)FindResource("MutedStyle"),
                Margin = new Thickness(0, 4, 0, 0)
            }));

        // Quality preset
        _qualityBox = new System.Windows.Controls.ComboBox { MinWidth = 220 };
        _qualityBox.Items.Add("Niedrig");
        _qualityBox.Items.Add("Mittel  (empfohlen)");
        _qualityBox.Items.Add("Hoch");
        _qualityBox.Items.Add("Sehr hoch");
        _qualityBox.Items.Add("Manuell (Bitrate-Eingabe)");
        _qualityBox.SelectedIndex = (int)host.Settings.Current.Video.Quality;
        _qualityBox.SelectionChanged += (_, _) =>
        {
            var newQuality = (QualityPreset)_qualityBox.SelectedIndex;
            if (newQuality == host.Settings.Current.Video.Quality) return;
            host.Settings.Current.Video.Quality = newQuality;
            host.Settings.Save();
            UpdateBitrateHint(host);
            if (_customBitrateBox != null)
                _customBitrateBox.IsEnabled = host.Settings.Current.Video.Quality == QualityPreset.Custom;
            host.ReplayBuffer.RequestRestart();
        };
        _bitrateHint = new TextBlock { Style = (Style)FindResource("MutedStyle"), Margin = new Thickness(0, 4, 0, 0) };
        VideoRows.Children.Add(LabeledRow("Qualität", _qualityBox, _bitrateHint));

        // Custom bitrate (only enabled when Quality == Custom)
        _customBitrateBox = new TextBox
        {
            Text = host.Settings.Current.Video.Bitrate.ToString(),
            MinWidth = 220,
            IsEnabled = host.Settings.Current.Video.Quality == QualityPreset.Custom
        };
        _customBitrateBox.LostFocus += (_, _) =>
        {
            if (int.TryParse(_customBitrateBox.Text.Replace("_", "").Replace(" ", ""), out var bps))
            {
                host.Settings.Current.Video.Bitrate = bps;
                host.Settings.Save();
                host.ReplayBuffer.RequestRestart();
            }
            UpdateBitrateHint(host);
        };
        VideoRows.Children.Add(LabeledRow("Bitrate (bps)", _customBitrateBox));

        UpdateBitrateHint(host);

        // Periodically refresh codec list while detection runs in background.
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(800)
        };
        int attempts = 0;
        timer.Tick += (_, _) =>
        {
            attempts++;
            RefreshCodecBox(host);
            if (host.AvailableCodecs.Count > 1 || attempts > 10) timer.Stop();
        };
        timer.Start();

        // Buffer rows
        var bufferEnabled = new System.Windows.Controls.CheckBox
        {
            Content = "Buffer aktiv (auto-Start beim App-Start)",
            IsChecked = host.Settings.Current.ReplayBuffer.Enabled,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 8)
        };
        bufferEnabled.Checked += (_, _) =>
        {
            host.Settings.Current.ReplayBuffer.Enabled = true;
            host.Settings.Save();
            if (!host.ReplayBuffer.IsRunning) host.ReplayBuffer.Start();
        };
        bufferEnabled.Unchecked += (_, _) =>
        {
            host.Settings.Current.ReplayBuffer.Enabled = false;
            host.Settings.Save();
            _ = host.ReplayBuffer.StopAsync();
        };
        BufferRows.Children.Add(bufferEnabled);

        var bufferValues = new[] { 15, 30, 45, 60, 90, 120, 180 };
        var bufferDur = new System.Windows.Controls.ComboBox { MinWidth = 220 };
        foreach (var s in bufferValues) bufferDur.Items.Add($"{s} Sekunden");
        var initialIdx = Array.IndexOf(bufferValues, host.Settings.Current.ReplayBuffer.DurationSeconds);
        bufferDur.SelectedIndex = initialIdx >= 0 ? initialIdx : 3; // default 60s
        bufferDur.SelectionChanged += (_, _) =>
        {
            if (bufferDur.SelectedIndex >= 0 && bufferDur.SelectedIndex < bufferValues.Length)
            {
                host.Settings.Current.ReplayBuffer.DurationSeconds = bufferValues[bufferDur.SelectedIndex];
                host.Settings.Save();
                host.ReplayBuffer.RequestRestart();
            }
        };
        BufferRows.Children.Add(LabeledRow("Buffer-Länge", bufferDur,
            new TextBlock
            {
                Text = "Bestimmt wie viele Sekunden der Replay-Hotkey speichert.",
                Style = (Style)FindResource("MutedStyle"),
                Margin = new Thickness(0, 4, 0, 0)
            }));
    }

    private void RefreshCodecBox(AppHost host)
    {
        if (_codecBox is null) return;
        var current = host.Settings.Current.Video.Codec;
        _suppressCodecChange = true;
        try
        {
            _codecBox.Items.Clear();
            var codecs = host.AvailableCodecs.Count > 0
                ? host.AvailableCodecs
                : new[] { new CodecInfo(current, "(läuft Detection…) " + current) };
            int select = 0;
            for (int i = 0; i < codecs.Count; i++)
            {
                _codecBox.Items.Add(codecs[i]);
                if (codecs[i].FFmpegName == current) select = i;
            }
            _codecBox.DisplayMemberPath = nameof(CodecInfo.Label);
            _codecBox.SelectedIndex = select;
        }
        finally { _suppressCodecChange = false; }
    }

    private void UpdateResolutionHint()
    {
        if (_resolutionHint is null) return;
        try
        {
            var s = Screen.PrimaryScreen;
            if (s != null)
                _resolutionHint.Text = $"Display: {s.Bounds.Width} × {s.Bounds.Height}  ({Screen.AllScreens.Length} Monitor(e) erkannt)";
        }
        catch { _resolutionHint.Text = ""; }
    }

    private void UpdateBitrateHint(AppHost host)
    {
        if (_bitrateHint is null) return;
        var v = host.Settings.Current.Video;
        if (v.Quality == QualityPreset.Custom)
        {
            _bitrateHint.Text = $"Manuell: {v.Bitrate / 1_000_000} Mbps";
        }
        else
        {
            var bps = QualityPresets.ComputeBitrate(v.Quality, v.Resolution);
            _bitrateHint.Text = $"Effektive Bitrate: {bps / 1_000_000} Mbps (errechnet aus Qualität × Auflösung)";
        }
    }

    private FrameworkElement LabeledRow(string label, FrameworkElement control, FrameworkElement? hint = null)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var labelBlock = new TextBlock
        {
            Text = label,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 6)
        };
        stack.Children.Add(labelBlock);
        stack.Children.Add(control);
        if (hint != null) stack.Children.Add(hint);
        return stack;
    }

    private static string LabelForResolution(ResolutionPreset r) => r switch
    {
        ResolutionPreset.Native => "Native (automatisch)",
        ResolutionPreset.FullHD => "Full HD  (1920 × 1080)",
        ResolutionPreset.WQHD   => "WQHD  (2560 × 1440)",
        ResolutionPreset.UHD    => "4K UHD  (3840 × 2160)",
        _                       => r.ToString()
    };

    private static int IndexFromResolution(ResolutionPreset r) => r switch
    {
        ResolutionPreset.Native => 0,
        ResolutionPreset.FullHD => 1,
        ResolutionPreset.WQHD   => 2,
        ResolutionPreset.UHD    => 3,
        _                       => 0
    };

    private static ResolutionPreset ResolutionFromIndex(int i) => i switch
    {
        0 => ResolutionPreset.Native,
        1 => ResolutionPreset.FullHD,
        2 => ResolutionPreset.WQHD,
        3 => ResolutionPreset.UHD,
        _ => ResolutionPreset.Native
    };
}
