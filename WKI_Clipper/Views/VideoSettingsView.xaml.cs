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
    private TextBlock? _bufferHint;
    private TextBlock? _bufferDiskWarn;
    private TextBlock? _bufferLongNote;
    // Re-evaluates the buffer-length hint when the bitrate changes (the byte estimate follows it).
    private Action? _refreshBufferHint;
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

        BufferHeading.Text = L.T("Replay-Buffer", "Replay buffer");

        // Capture target (which monitor/window + audio) now lives in the Aufnahme
        // tab; this tab only covers how it is encoded.
        VideoRows.Children.Add(new TextBlock
        {
            Text = L.T("Was aufgenommen wird (Monitor/Fenster) legst du im Tab „Aufnahme“ fest. Hier geht es nur um die Encoding-Qualität.",
                       "What gets captured (monitor/window) is set in the Capture tab. This tab only covers encoding quality."),
            Style = (Style)FindResource("MutedStyle"),
            Margin = new Thickness(0, 0, 0, 14),
            TextWrapping = TextWrapping.Wrap
        });

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
        VideoRows.Children.Add(LabeledRow(L.T("Auflösung", "Resolution"), _resolutionBox, _resolutionHint));

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
        VideoRows.Children.Add(LabeledRow(L.T("Framerate", "Frame rate"), fpsBox));

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
                Text = L.T("Wenn keine AMD-GPU-Encoder gelistet sind, läuft Codec-Detection noch — kurz warten und Settings-Tab wechseln.",
                           "If no AMD GPU encoders are listed, codec detection is still running — wait a moment and switch tabs."),
                Style = (Style)FindResource("MutedStyle"),
                Margin = new Thickness(0, 4, 0, 0)
            }));

        // Quality preset
        _qualityBox = new System.Windows.Controls.ComboBox { MinWidth = 220 };
        _qualityBox.Items.Add(L.T("Niedrig", "Low"));
        _qualityBox.Items.Add(L.T("Mittel  (empfohlen)", "Medium  (recommended)"));
        _qualityBox.Items.Add(L.T("Hoch", "High"));
        _qualityBox.Items.Add(L.T("Sehr hoch", "Very high"));
        _qualityBox.Items.Add(L.T("Manuell (Bitrate-Eingabe)", "Manual (bitrate input)"));
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
        VideoRows.Children.Add(LabeledRow(L.T("Qualität", "Quality"), _qualityBox, _bitrateHint));

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
        VideoRows.Children.Add(LabeledRow(L.T("Bitrate (bps)", "Bitrate (bps)"), _customBitrateBox));

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
            Content = L.T("Buffer aktiv (auto-Start beim App-Start)", "Buffer active (auto-start with the app)"),
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

        var bufferValues = DurationText.Choices;
        var bufferDur = new System.Windows.Controls.ComboBox { MinWidth = 220 };
        foreach (var s in bufferValues) bufferDur.Items.Add(DurationText.Format(s));
        bufferDur.SelectedIndex = InitialBufferIndex(bufferValues, host.Settings.Current.ReplayBuffer.DurationSeconds);

        // What the chosen length costs on the buffer drive; a 30-minute lookback is 6-7 GB (up to ~25 GB at "very high").
        _bufferHint = new TextBlock { Style = (Style)FindResource("MutedStyle"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        _bufferDiskWarn = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xA8, 0x40)),
            Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed
        };
        _bufferLongNote = new TextBlock
        {
            Style = (Style)FindResource("MutedStyle"), Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Text = L.T("Lange Rückblicke: Für lückenlose Beweise das Aufnahme-Ziel „Ganzer Monitor“ oder „Bestimmtes Fenster“ wählen. In „Automatik“ fehlt die Zeit in anderen Fenstern (Alt-Tab) — sie wird im Clip als Kapitel und Notiz markiert. Strg+F9-Aufnahmen pausieren den Buffer. Fenstergröße/Vollbild, Codec oder FPS ändern beendet den bisherigen Rückblick. Ein 30-min-Clip braucht je nach Platte 20–90 s zum Speichern; F8/F9 sind solange gesperrt.",
                       "Long lookbacks: for gap-free evidence choose the capture target “Whole monitor” or “Specific window”. In “Automatic” the time spent in other windows (Alt-Tab) is missing — it is marked in the clip as a chapter and a note. Ctrl+F9 recordings pause the buffer. Changing the window size/fullscreen, codec or FPS ends the previous lookback. A 30 min clip takes 20–90 s to save depending on the disk; F8/F9 are blocked meanwhile.")
        };
        void RefreshBufferHint()
        {
            if (_bufferHint is null || _bufferDiskWarn is null || _bufferLongNote is null) return;
            int idx = bufferDur.SelectedIndex;
            if (idx < 0 || idx >= bufferValues.Length) return;
            int chosen = bufferValues[idx];
            DiskForecast f;
            try { f = host.ReplayBuffer.Forecast(chosen); }
            catch (Exception ex)
            {
                Logger.Warn("Buffer forecast failed: " + ex.Message);
                return;
            }

            _bufferHint.Text = f.Known
                ? L.T($"Bestimmt, wie viel F9 speichert. Braucht ca. {DiskPlan.FormatBytes(f.RequiredBytes)} im Buffer-Ordner ({f.Drive} frei: {DiskPlan.FormatBytes(f.FreeBytes)}).",
                      $"Determines how much F9 saves. Needs about {DiskPlan.FormatBytes(f.RequiredBytes)} in the buffer folder ({f.Drive} free: {DiskPlan.FormatBytes(f.FreeBytes)}).")
                : L.T($"Bestimmt, wie viel F9 speichert. Braucht ca. {DiskPlan.FormatBytes(f.RequiredBytes)} im Buffer-Ordner.",
                      $"Determines how much F9 saves. Needs about {DiskPlan.FormatBytes(f.RequiredBytes)} in the buffer folder.");

            var d = f.Decision;
            if (f.Known && d.CannotStart)
            {
                _bufferDiskWarn.Text = L.T($"Zu wenig Platz auf {f.Drive} — der Buffer kann so nicht starten. Buffer-Ordner unter Einstellungen → Pfade auf ein größeres Laufwerk legen.",
                                           $"Not enough space on {f.Drive} — the buffer cannot start like this. Move the buffer folder to a bigger drive under Settings → Paths.");
                _bufferDiskWarn.Visibility = Visibility.Visible;
            }
            else if (f.Known && d.Shortened)
            {
                _bufferDiskWarn.Text = L.T($"Zu wenig Platz — der Buffer läuft nur mit {DurationText.Format(d.EffectiveSeconds)}. Buffer-Ordner unter Einstellungen → Pfade auf ein größeres Laufwerk legen.",
                                           $"Not enough space — the buffer only runs with {DurationText.Format(d.EffectiveSeconds)}. Move the buffer folder to a bigger drive under Settings → Paths.");
                _bufferDiskWarn.Visibility = Visibility.Visible;
            }
            else _bufferDiskWarn.Visibility = Visibility.Collapsed;

            _bufferLongNote.Visibility = chosen >= DurationText.LongClipSeconds ? Visibility.Visible : Visibility.Collapsed;
        }
        _refreshBufferHint = RefreshBufferHint;

        bufferDur.SelectionChanged += (_, _) =>
        {
            if (bufferDur.SelectedIndex >= 0 && bufferDur.SelectedIndex < bufferValues.Length)
            {
                int chosen = bufferValues[bufferDur.SelectedIndex];
                // The initial selection fires this too: nothing to save or restart then.
                if (chosen != host.Settings.Current.ReplayBuffer.DurationSeconds)
                {
                    host.Settings.Current.ReplayBuffer.DurationSeconds = chosen;
                    host.Settings.Save();
                    host.ReplayBuffer.RequestRestart();
                }
                RefreshBufferHint();
            }
        };

        var hintStack = new StackPanel();
        hintStack.Children.Add(_bufferHint);
        hintStack.Children.Add(_bufferDiskWarn);
        hintStack.Children.Add(_bufferLongNote);
        BufferRows.Children.Add(LabeledRow(L.T("Buffer-Länge", "Buffer length"), bufferDur, hintStack));
        RefreshBufferHint();
    }

    /// <summary>
    /// The list entry for the current setting: the exact length, otherwise the largest offered length below it
    /// (a hand-edited 100 s shows 90 s), otherwise 60 s.
    /// </summary>
    internal static int InitialBufferIndex(int[] choices, int current)
    {
        int clamped = DurationText.Clamp(current);
        int exact = Array.IndexOf(choices, clamped);
        if (exact >= 0) return exact;
        for (int i = choices.Length - 1; i >= 0; i--)
            if (choices[i] <= clamped) return i;
        return Math.Max(0, Array.IndexOf(choices, 60));
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
                : new[] { new CodecInfo(current, L.T("(läuft Detection…) ", "(detecting…) ") + current) };
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
                _resolutionHint.Text = L.T(
                    $"Display: {s.Bounds.Width} × {s.Bounds.Height}  ({Screen.AllScreens.Length} Monitor(e) erkannt)",
                    $"Display: {s.Bounds.Width} × {s.Bounds.Height}  ({Screen.AllScreens.Length} monitor(s) detected)");
        }
        catch { _resolutionHint.Text = ""; }
    }

    private void UpdateBitrateHint(AppHost host)
    {
        if (_bitrateHint is null) return;
        var v = host.Settings.Current.Video;
        if (v.Quality == QualityPreset.Custom)
        {
            _bitrateHint.Text = L.T($"Manuell: {v.Bitrate / 1_000_000} Mbps", $"Manual: {v.Bitrate / 1_000_000} Mbps");
        }
        else
        {
            var bps = QualityPresets.ComputeBitrate(v.Quality, v.Resolution);
            _bitrateHint.Text = L.T($"Effektive Bitrate: {bps / 1_000_000} Mbps (errechnet aus Qualität × Auflösung)",
                                    $"Effective bitrate: {bps / 1_000_000} Mbps (derived from quality × resolution)");
        }
        _refreshBufferHint?.Invoke();
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
        ResolutionPreset.Native => L.T("Native (automatisch)", "Native (automatic)"),
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
