using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WKI_Clipper.Models;
using WKI_Clipper.Services;

namespace WKI_Clipper.Views;

public partial class StatusView : UserControl
{
    private DispatcherTimer? _refreshTimer;
    private TextBlock? _recordingText;
    private System.Windows.Shapes.Ellipse? _recordingDot;
    private TextBlock? _bufferText;
    private System.Windows.Shapes.Ellipse? _bufferDot;
    private TextBlock? _audioText;
    private TextBlock? _videoText;
    private TextBlock? _captureSrcText;
    private TextBlock? _ffmpegText;
    private TextBlock? _clipsPathText;
    private TextBlock? _diskSpaceText;

    public StatusView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (InfoContainer.Children.Count == 0)
        {
            BuildSections();
        }
        Refresh();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _refreshTimer?.Stop();
        _refreshTimer = null;
    }

    private void BuildSections()
    {
        // === Recording row ===
        _recordingDot = MakeDot();
        _recordingText = MakeRowText();
        InfoContainer.Children.Add(MakeCard("Aufnahme-Status",
            "Zeigt ob gerade manuell aufgenommen wird (Strg+F9).",
            MakeIconRow(_recordingDot, _recordingText)));

        // === Buffer row ===
        _bufferDot = MakeDot();
        _bufferText = MakeRowText();
        InfoContainer.Children.Add(MakeCard("Replay-Buffer",
            "Läuft im Hintergrund mit, F9 speichert die letzten N Sekunden.",
            MakeIconRow(_bufferDot, _bufferText)));

        // === Audio + Video config ===
        _audioText = MakeRowText();
        _videoText = MakeRowText();
        _captureSrcText = MakeRowText();
        var cfgStack = new StackPanel();
        cfgStack.Children.Add(MakeLabeledRow("Audio",          _audioText));
        cfgStack.Children.Add(MakeLabeledRow("Video",          _videoText));
        cfgStack.Children.Add(MakeLabeledRow("Aufnahme-Quelle", _captureSrcText));
        InfoContainer.Children.Add(MakeCard("Aktive Konfiguration",
            "Was Buffer + Recording gerade benutzen würden.",
            cfgStack));

        // === System info ===
        _ffmpegText = MakeRowText();
        _clipsPathText = MakeRowText();
        _diskSpaceText = MakeRowText();
        var sysStack = new StackPanel();
        sysStack.Children.Add(MakeLabeledRow("FFmpeg",          _ffmpegText));
        sysStack.Children.Add(MakeLabeledRow("Clips-Ordner",    _clipsPathText));
        sysStack.Children.Add(MakeLabeledRow("Freier Speicher", _diskSpaceText));
        InfoContainer.Children.Add(MakeCard("System",
            "Wo Dateien landen und wie viel Platz noch da ist.",
            sysStack));
    }

    private void Refresh()
    {
        if (!IsVisible) return;
        var host = App.Host;
        if (host is null) return;
        var s = host.Settings.Current;

        // Recording state
        if (host.ManualRecording.IsRecording)
        {
            _recordingDot!.Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0x4E, 0x4E));
            var dur = host.ManualRecording.StartedAt is { } start
                ? (DateTime.Now - start).ToString(@"mm\:ss")
                : "00:00";
            var name = host.ManualRecording.CurrentOutputPath != null
                ? System.IO.Path.GetFileName(host.ManualRecording.CurrentOutputPath)
                : "";
            _recordingText!.Text = $"Aufnahme läuft  ·  {dur}  ·  {name}";
        }
        else
        {
            _recordingDot!.Fill = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            _recordingText!.Text = "Bereit. Strg+F9 zum Starten.";
        }

        // Buffer state
        if (host.ReplayBuffer.IsRunning)
        {
            _bufferDot!.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x6A, 0x2C));
            _bufferText!.Text = $"Buffer aktiv  ·  letzte {s.ReplayBuffer.DurationSeconds} s im Speicher  ·  F9 speichert";
        }
        else
        {
            _bufferDot!.Fill = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            _bufferText!.Text = "Buffer aus. Strg+F10 schaltet ihn ein.";
        }

        // Effective capture plan (same resolver F9/Ctrl+F9 use). Buffer's pinned
        // plan if running, else a fresh preview of the current profile.
        var plan = host.ReplayBuffer.CurrentPlan
                   ?? CaptureTargetResolver.Resolve(s.Capture, s);

        // Audio (effective route, not just the checkboxes)
        _audioText!.Text = $"{plan.AudioLabel}  ·  Offset {s.Audio.OffsetMilliseconds} ms";

        // Video
        var resName = s.Video.Resolution switch
        {
            ResolutionPreset.Native => "Native",
            ResolutionPreset.FullHD => "Full HD",
            ResolutionPreset.WQHD   => "WQHD",
            ResolutionPreset.UHD    => "4K UHD",
            _                       => s.Video.Resolution.ToString()
        };
        int bitrate = s.Video.Quality == QualityPreset.Custom
            ? s.Video.Bitrate
            : QualityPresets.ComputeBitrate(s.Video.Quality, s.Video.Resolution);
        var codecLabel = host.AvailableCodecs.FirstOrDefault(c => c.FFmpegName == s.Video.Codec)?.Label ?? s.Video.Codec;
        _videoText!.Text = $"{resName}  ·  {s.Video.Framerate} fps  ·  {codecLabel}  ·  {bitrate / 1_000_000} Mbps";

        // Capture target (effective, from the resolver)
        _captureSrcText!.Text = plan.VideoLabel;

        // FFmpeg
        _ffmpegText!.Text = host.FFmpeg.IsAvailable()
            ? System.IO.Path.GetFileName(host.FFmpeg.FFmpegPath) + "  ·  " + host.AvailableCodecs.Count + " Encoder verfügbar"
            : "NICHT GEFUNDEN — winget install Gyan.FFmpeg";

        // Clips path + disk space
        var clipsDir = SettingsService.ExpandPath(s.Output.ClipsFolder);
        _clipsPathText!.Text = clipsDir;
        try
        {
            var root = System.IO.Path.GetPathRoot(clipsDir);
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    _diskSpaceText!.Text = FormatBytes(drive.AvailableFreeSpace)
                        + "  /  " + FormatBytes(drive.TotalSize) + " auf " + drive.Name;
                    return;
                }
            }
            _diskSpaceText!.Text = "—";
        }
        catch { _diskSpaceText!.Text = "—"; }
    }

    private FrameworkElement MakeCard(string title, string description, FrameworkElement content)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = System.Windows.FontWeights.SemiBold,
            FontSize = 14,
            Foreground = (Brush)FindResource("TextBrush")
        });
        stack.Children.Add(new TextBlock
        {
            Text = description,
            Style = (Style)FindResource("MutedStyle"),
            Margin = new Thickness(0, 2, 0, 10)
        });
        stack.Children.Add(content);

        return new Border
        {
            Background = (Brush)FindResource("PanelBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 12, 16, 14),
            Margin = new Thickness(0, 0, 0, 10),
            Child = stack
        };
    }

    private FrameworkElement MakeIconRow(System.Windows.Shapes.Ellipse dot, TextBlock text)
    {
        var sp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        sp.Children.Add(dot);
        sp.Children.Add(text);
        return sp;
    }

    private FrameworkElement MakeLabeledRow(string label, TextBlock value)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var l = new TextBlock
        {
            Text = label,
            Foreground = (Brush)FindResource("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumn(l, 0);
        grid.Children.Add(l);

        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }

    private System.Windows.Shapes.Ellipse MakeDot() => new System.Windows.Shapes.Ellipse
    {
        Width = 12, Height = 12,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 10, 0),
        Fill = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55))
    };

    private TextBlock MakeRowText() => new TextBlock
    {
        Foreground = (Brush)FindResource("TextBrush"),
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap
    };

    private static string FormatBytes(long bytes)
    {
        const double GB = 1024.0 * 1024 * 1024;
        const double TB = GB * 1024;
        if (bytes >= TB) return $"{bytes / TB:F2} TB";
        if (bytes >= GB) return $"{bytes / GB:F1} GB";
        return $"{bytes / (1024.0 * 1024):F0} MB";
    }
}
