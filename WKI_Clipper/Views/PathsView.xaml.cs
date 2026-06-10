using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WinForms = System.Windows.Forms;

namespace WKI_Clipper.Views;

public partial class PathsView : UserControl
{
    private enum PathKind { Clips, Screenshots, Buffer }

    private static readonly (PathKind kind, string label, string description)[] Definitions =
    {
        (PathKind.Clips,       "Clips-Ordner",
            "Hier landen alle gespeicherten Replays und manuellen Aufnahmen."),
        (PathKind.Screenshots, "Screenshots-Ordner",
            "Hier landen PNG-Screenshots vom aktiven Fenster."),
        (PathKind.Buffer,      "Buffer-Ordner (temporär)",
            "Live-Segmente des Replay-Buffers. Wird beim App-Start geleert."),
    };

    public PathsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (RowsContainer.Children.Count > 0) return;
        var host = App.Host;
        if (host is null) return;

        foreach (var def in Definitions)
        {
            RowsContainer.Children.Add(BuildRow(host, def.kind, def.label, def.description));
        }

        OpenSettingsJsonBtn.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("notepad.exe",
                    "\"" + host.Settings.SettingsFilePath + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Logger.Error("open settings.json failed", ex); }
        };
        OpenSettingsDirBtn.Click += (_, _) =>
        {
            try { Process.Start("explorer.exe", host.Settings.AppDataDir); } catch { }
        };
        ResetAllBtn.Click += (_, _) =>
        {
            var result = MessageBox.Show(
                "Wirklich alle Einstellungen auf Standard zurücksetzen?\n\nDie App muss danach neu gestartet werden.",
                "Einstellungen zurücksetzen",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            try
            {
                if (File.Exists(host.Settings.SettingsFilePath))
                    File.Delete(host.Settings.SettingsFilePath);
                MessageBox.Show("Settings gelöscht. App startet neu.", "OK", MessageBoxButton.OK);
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (exe != null) Process.Start(exe);
                Application.Current.Shutdown();
            }
            catch (Exception ex) { Logger.Error("reset all failed", ex); }
        };
    }

    private FrameworkElement BuildRow(AppHost host, PathKind kind, string label, string description)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Status dot (live validation)
        var statusDot = new System.Windows.Shapes.Ellipse
        {
            Width = 12, Height = 12,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        Grid.SetColumn(statusDot, 0);
        grid.Children.Add(statusDot);

        // TextBox
        var box = new System.Windows.Controls.TextBox
        {
            Text = GetPath(host, kind),
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(4, 0, 8, 0),
            FontSize = 13
        };
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);

        var browseBtn = MakeButton("Durchsuchen…", host);
        Grid.SetColumn(browseBtn, 2);
        grid.Children.Add(browseBtn);

        var openBtn = MakeButton("Öffnen", host);
        openBtn.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(openBtn, 3);
        grid.Children.Add(openBtn);

        var resetBtn = MakeButton("↺", host);
        resetBtn.Margin = new Thickness(6, 0, 0, 0);
        resetBtn.MinWidth = 36;
        resetBtn.ToolTip = "Auf Standardpfad zurücksetzen";
        Grid.SetColumn(resetBtn, 4);
        grid.Children.Add(resetBtn);

        // Wire events
        box.TextChanged += (_, _) =>
        {
            SetPath(host, kind, box.Text);
            host.Settings.Save();
            UpdateStatusDot(statusDot, box.Text);
        };

        browseBtn.Click += (_, _) =>
        {
            using var dlg = new WinForms.FolderBrowserDialog
            {
                Description = label + " wählen",
                UseDescriptionForTitle = true,
                SelectedPath = Expand(box.Text),
                ShowNewFolderButton = true
            };
            if (dlg.ShowDialog() == WinForms.DialogResult.OK)
            {
                box.Text = dlg.SelectedPath;
            }
        };

        openBtn.Click += (_, _) =>
        {
            var p = Expand(box.Text);
            try { Directory.CreateDirectory(p); } catch { }
            try { Process.Start("explorer.exe", p); } catch { }
        };

        resetBtn.Click += (_, _) =>
        {
            box.Text = DefaultFor(kind);
        };

        // Header card
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        header.Children.Add(new TextBlock { Text = label, FontWeight = System.Windows.FontWeights.SemiBold });
        header.Children.Add(new TextBlock
        {
            Text = description,
            Style = (Style)FindResource("MutedStyle")
        });

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(grid);

        var card = new Border
        {
            Background = (Brush)FindResource("PanelBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14, 10, 14, 12),
            Margin = new Thickness(0, 3, 0, 3),
            Child = stack
        };

        UpdateStatusDot(statusDot, box.Text);
        return card;
    }

    private static System.Windows.Controls.Button MakeButton(string content, AppHost host)
    {
        var b = new System.Windows.Controls.Button
        {
            Content = content,
            Padding = new Thickness(10, 6, 10, 6),
            MinWidth = 100,
            Cursor = Cursors.Hand
        };
        return b;
    }

    private static void UpdateStatusDot(System.Windows.Shapes.Ellipse dot, string path)
    {
        try
        {
            var expanded = Expand(path);
            if (Directory.Exists(expanded))
            {
                dot.Fill = new SolidColorBrush(Color.FromRgb(0x4A, 0xD8, 0x6A));
                dot.ToolTip = "OK — Ordner existiert";
            }
            else if (PathLikelyValid(expanded))
            {
                dot.Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0xA8, 0x40));
                dot.ToolTip = "Ordner existiert noch nicht — wird beim Speichern erstellt";
            }
            else
            {
                dot.Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0x4E, 0x4E));
                dot.ToolTip = "Pfad ungültig";
            }
        }
        catch
        {
            dot.Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0x4E, 0x4E));
            dot.ToolTip = "Pfad ungültig";
        }
    }

    private static bool PathLikelyValid(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            return !string.IsNullOrWhiteSpace(Path.GetPathRoot(full));
        }
        catch { return false; }
    }

    private static string Expand(string raw) => Environment.ExpandEnvironmentVariables(raw ?? "");

    private static string GetPath(AppHost host, PathKind kind) => kind switch
    {
        PathKind.Clips       => host.Settings.Current.Output.ClipsFolder,
        PathKind.Screenshots => host.Settings.Current.Output.ScreenshotsFolder,
        PathKind.Buffer      => host.Settings.Current.Output.BufferFolder,
        _                    => ""
    };

    private static void SetPath(AppHost host, PathKind kind, string path)
    {
        switch (kind)
        {
            case PathKind.Clips:       host.Settings.Current.Output.ClipsFolder = path;       break;
            case PathKind.Screenshots: host.Settings.Current.Output.ScreenshotsFolder = path; break;
            case PathKind.Buffer:      host.Settings.Current.Output.BufferFolder = path;      break;
        }
    }

    private static string DefaultFor(PathKind kind) => kind switch
    {
        PathKind.Clips       => @"%USERPROFILE%\Videos\WKI_Clipper\Clips",
        PathKind.Screenshots => @"%USERPROFILE%\Videos\WKI_Clipper\Screenshots",
        PathKind.Buffer      => @"%LOCALAPPDATA%\WKI_Clipper\buffer",
        _                    => ""
    };
}
