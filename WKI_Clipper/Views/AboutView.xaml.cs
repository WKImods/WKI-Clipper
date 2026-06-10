using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Media;
using WKI_Clipper.Services;

namespace WKI_Clipper.Views;

[SupportedOSPlatform("windows")]
public partial class AboutView : UserControl
{
    private readonly AutoStartService _autoStart = new();
    private System.Windows.Controls.CheckBox? _autoStartBox;
    private TextBlock? _ffmpegStatus;

    public AboutView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (InfoContainer.Children.Count > 0) return;
        var host = App.Host;
        if (host is null) return;

        // App info
        InfoContainer.Children.Add(Card(
            "App",
            new (string, string)[]
            {
                ("Version",    GetVersion()),
                ("Settings",   host.Settings.SettingsFilePath),
                ("Log",        Logger.Path),
                ("Buffer",     SettingsService.ExpandPath(host.Settings.Current.Output.BufferFolder)),
            }));

        // System info
        var primary = Screen.PrimaryScreen;
        var screenInfo = primary != null
            ? $"{primary.Bounds.Width} × {primary.Bounds.Height} (primär), insgesamt {Screen.AllScreens.Length} Monitor(e)"
            : "(unbekannt)";

        InfoContainer.Children.Add(Card(
            "System",
            new (string, string)[]
            {
                ("Windows",     Environment.OSVersion.VersionString),
                ("Bildschirme", screenInfo),
                ("CPU",         Environment.ProcessorCount + " logische Kerne"),
            }));

        // FFmpeg status
        var ffmpegStack = new StackPanel();
        _ffmpegStatus = new TextBlock
        {
            Foreground = (Brush)FindResource("TextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        ffmpegStack.Children.Add(_ffmpegStatus);
        UpdateFFmpegStatus(host);

        var ffmpegPathBtn = new System.Windows.Controls.Button
        {
            Content = "FFmpeg-Ordner öffnen",
            Padding = new Thickness(10, 4, 10, 4),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        ffmpegPathBtn.Click += (_, _) =>
        {
            var dir = Path.GetDirectoryName(host.FFmpeg.FFmpegPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                try { Process.Start("explorer.exe", dir); } catch { }
        };
        ffmpegStack.Children.Add(ffmpegPathBtn);

        InfoContainer.Children.Add(SectionCard("FFmpeg", ffmpegStack));

        // Actions
        var actionsStack = new StackPanel();
        _autoStartBox = new System.Windows.Controls.CheckBox
        {
            Content = "Mit Windows starten (Tray-Modus)",
            IsChecked = _autoStart.IsEnabled(),
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        _autoStartBox.Checked += (_, _) => _autoStart.SetEnabled(true);
        _autoStartBox.Unchecked += (_, _) => _autoStart.SetEnabled(false);
        actionsStack.Children.Add(_autoStartBox);

        var toastBox = new System.Windows.Controls.CheckBox
        {
            Content = "Tray-Benachrichtigungen anzeigen",
            IsChecked = host.Settings.Current.Behavior.ShowToastNotifications,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        toastBox.Checked += (_, _) =>
        {
            host.Settings.Current.Behavior.ShowToastNotifications = true;
            host.Settings.Save();
        };
        toastBox.Unchecked += (_, _) =>
        {
            host.Settings.Current.Behavior.ShowToastNotifications = false;
            host.Settings.Save();
        };
        actionsStack.Children.Add(toastBox);

        var bufBox = new System.Windows.Controls.CheckBox
        {
            Content = "Replay-Buffer beim App-Start automatisch starten",
            IsChecked = host.Settings.Current.Behavior.StartBufferOnLaunch,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        bufBox.Checked += (_, _) =>
        {
            host.Settings.Current.Behavior.StartBufferOnLaunch = true;
            host.Settings.Save();
        };
        bufBox.Unchecked += (_, _) =>
        {
            host.Settings.Current.Behavior.StartBufferOnLaunch = false;
            host.Settings.Save();
        };
        actionsStack.Children.Add(bufBox);

        var btnRow = new WrapPanel();
        var openLogBtn = new System.Windows.Controls.Button
        {
            Content = "Log öffnen", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0)
        };
        openLogBtn.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("notepad.exe", "\"" + Logger.Path + "\"") { UseShellExecute = true }); } catch { }
        };
        btnRow.Children.Add(openLogBtn);

        var openSettingsDirBtn = new System.Windows.Controls.Button
        {
            Content = "Settings-Ordner öffnen", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0)
        };
        openSettingsDirBtn.Click += (_, _) =>
        {
            try { Process.Start("explorer.exe", host.Settings.AppDataDir); } catch { }
        };
        btnRow.Children.Add(openSettingsDirBtn);

        var exitBtn = new System.Windows.Controls.Button
        {
            Content = "App beenden",
            Padding = new Thickness(10, 4, 10, 4),
            Background = (Brush)FindResource("DangerBrush"),
            Foreground = (Brush)System.Windows.Media.Brushes.White
        };
        exitBtn.Click += (_, _) => Application.Current.Shutdown();
        btnRow.Children.Add(exitBtn);

        actionsStack.Children.Add(btnRow);

        InfoContainer.Children.Add(SectionCard("Einstellungen & Aktionen", actionsStack));
    }

    private void UpdateFFmpegStatus(AppHost host)
    {
        if (_ffmpegStatus is null) return;
        if (!host.FFmpeg.IsAvailable())
        {
            _ffmpegStatus.Text = "FFmpeg nicht gefunden! Installiere via:  winget install Gyan.FFmpeg";
            _ffmpegStatus.Foreground = (Brush)FindResource("DangerBrush");
            return;
        }
        _ffmpegStatus.Text = $"FFmpeg: {host.FFmpeg.FFmpegPath}\n\nVerfügbare Codecs:\n  " +
            string.Join("\n  ", host.AvailableCodecs.Select(c => "• " + c.Label + "  ›  " + c.FFmpegName));
    }

    private FrameworkElement Card(string title, (string label, string value)[] rows)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = System.Windows.FontWeights.SemiBold,
            FontSize = 14,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 8)
        });
        foreach (var (label, value) in rows)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var lblTb = new TextBlock
            {
                Text = label,
                Foreground = (Brush)FindResource("MutedBrush"),
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetColumn(lblTb, 0); grid.Children.Add(lblTb);

            var valTb = new TextBlock
            {
                Text = value,
                Foreground = (Brush)FindResource("TextBrush"),
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, monospace"),
                FontSize = 12
            };
            Grid.SetColumn(valTb, 1); grid.Children.Add(valTb);
            stack.Children.Add(grid);
        }
        return SectionCard(null, stack);
    }

    private FrameworkElement SectionCard(string? title, FrameworkElement content)
    {
        var stack = new StackPanel();
        if (title != null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = System.Windows.FontWeights.SemiBold,
                FontSize = 14,
                Foreground = (Brush)FindResource("TextBrush"),
                Margin = new Thickness(0, 0, 0, 8)
            });
        }
        stack.Children.Add(content);
        return new Border
        {
            Background = (Brush)FindResource("PanelBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14, 12, 14, 14),
            Margin = new Thickness(0, 4, 0, 8),
            Child = stack
        };
    }

    private static string GetVersion()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var ver = asm.GetName().Version;
            return ver?.ToString(3) ?? "0.0.0";
        }
        catch { return "0.0.0"; }
    }
}
