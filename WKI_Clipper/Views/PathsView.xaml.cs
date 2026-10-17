using System;
using System.Collections.Generic;
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

    private static (PathKind kind, string label, string description)[] Definitions =>
    new[]
    {
        (PathKind.Clips,       L.T("Clips-Ordner", "Clips folder"),
            L.T("Hier landen alle gespeicherten Replays und manuellen Aufnahmen.",
                "All saved replays and manual recordings end up here.")),
        (PathKind.Screenshots, L.T("Screenshots-Ordner", "Screenshots folder"),
            L.T("Hier landen PNG-Screenshots vom aktiven Fenster.",
                "PNG screenshots of the active window end up here.")),
        (PathKind.Buffer,      L.T("Buffer-Ordner (temporär)", "Buffer folder (temporary)"),
            L.T("Live-Segmente des Replay-Buffers. Wird beim App-Start geleert.",
                "Live segments of the replay buffer. Cleared at app start.")),
    };

    // One entry per row: re-checks a row whose last edit was refused, because a change in
    // another row (e.g. the clips folder moved away) may have lifted the conflict.
    private readonly List<Action> _revalidators = new();

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

        HeadingText.Text = L.T("Pfade", "Paths");
        SubheadingText.Text = L.T("Klick Durchsuchen für Folder-Picker. Klick Öffnen für Explorer. ↺ setzt auf Standard zurück.",
                                  "Click Browse for a folder picker. Click Open for Explorer. ↺ resets to the default.");
        EnvVarNote.Text = L.T("Umgebungsvariablen wie %USERPROFILE% oder %LOCALAPPDATA% werden automatisch expandiert.",
                              "Environment variables like %USERPROFILE% or %LOCALAPPDATA% are expanded automatically.");
        OpenSettingsJsonBtn.Content = L.T("settings.json in Notepad öffnen", "Open settings.json in Notepad");
        OpenSettingsDirBtn.Content = L.T("Settings-Ordner öffnen", "Open settings folder");
        ResetAllBtn.Content = L.T("Alle Einstellungen zurücksetzen", "Reset all settings");

        foreach (var def in Definitions)
        {
            RowsContainer.Children.Add(BuildRow(host, def.kind, def.label, def.description));
        }

        // Per-game subfolders for clips/recordings/GIFs and screenshots.
        var sortBox = new CheckBox
        {
            Content = L.T("Nach Spiel in Unterordner sortieren (z. B. Clips\\Arma Reforger\\)",
                          "Sort into one subfolder per game (e.g. Clips\\Arma Reforger\\)"),
            ToolTip = L.T("Gilt für neue Clips, Aufnahmen, GIFs und Screenshots. Bestehende Dateien bleiben, wo sie sind — die Galerie zeigt beides.",
                          "Applies to new clips, recordings, GIFs and screenshots. Existing files stay where they are — the gallery shows both."),
            IsChecked = host.Settings.Current.Output.SortByGame,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(28, 14, 0, 0)
        };
        sortBox.Checked += (_, _) => { host.Settings.Current.Output.SortByGame = true; host.Settings.Save(); };
        sortBox.Unchecked += (_, _) => { host.Settings.Current.Output.SortByGame = false; host.Settings.Save(); };
        RowsContainer.Children.Add(sortBox);

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
                L.T("Wirklich alle Einstellungen auf Standard zurücksetzen?\n\nDie App muss danach neu gestartet werden.",
                    "Really reset all settings to defaults?\n\nThe app will restart afterwards."),
                L.T("Einstellungen zurücksetzen", "Reset settings"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            try
            {
                // Includes settings.json.bak: Load() would otherwise restore the old settings from it.
                host.Settings.DeleteSettingsFiles();
                MessageBox.Show(L.T("Settings gelöscht. App startet neu.", "Settings deleted. App is restarting."), "OK", MessageBoxButton.OK);
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

        var browseBtn = MakeButton(L.T("Durchsuchen…", "Browse…"), host);
        Grid.SetColumn(browseBtn, 2);
        grid.Children.Add(browseBtn);

        var openBtn = MakeButton(L.T("Öffnen", "Open"), host);
        openBtn.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(openBtn, 3);
        grid.Children.Add(openBtn);

        var resetBtn = MakeButton("↺", host);
        resetBtn.Margin = new Thickness(6, 0, 0, 0);
        resetBtn.MinWidth = 36;
        resetBtn.ToolTip = L.T("Auf Standardpfad zurücksetzen", "Reset to default path");
        Grid.SetColumn(resetBtn, 4);
        grid.Children.Add(resetBtn);

        // Inline explanation for a refused change; hidden while the row is fine.
        var conflictText = new TextBlock
        {
            Style = (Style)FindResource("MutedStyle"),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x4E, 0x4E)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(32, 6, 0, 0),
            Visibility = Visibility.Collapsed
        };
        bool refused = false;

        // Saves the box text — unless it would let the replay buffer write its segments into the
        // clips/screenshots folder (the gallery would list one "clip" per segment). Then the last
        // saved value stays in effect and the row says why.
        void Apply()
        {
            var reason = ConflictReason(host, kind, box.Text);
            if (reason is null)
            {
                refused = false;
                conflictText.Visibility = Visibility.Collapsed;
                SetPath(host, kind, box.Text);
                host.Settings.Save();
                UpdateStatusDot(statusDot, box.Text);
                foreach (var revalidate in _revalidators) revalidate();
                return;
            }

            refused = true;
            conflictText.Text = reason.Replace("\n\n", " ");
            conflictText.Visibility = Visibility.Visible;
            statusDot.Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0x4E, 0x4E));
            statusDot.ToolTip = conflictText.Text;
        }
        _revalidators.Add(() => { if (refused) Apply(); });

        // Browse/reset are single deliberate picks: refuse them outright instead of leaving
        // an unsaved value in the box.
        void TrySet(string value)
        {
            var reason = ConflictReason(host, kind, value);
            if (reason is not null)
            {
                MessageBox.Show(reason, "WKI Clipper", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            box.Text = value;
        }

        // Wire events
        box.TextChanged += (_, _) => Apply();

        browseBtn.Click += (_, _) =>
        {
            using var dlg = new WinForms.FolderBrowserDialog
            {
                Description = L.T(label + " wählen", "Choose " + label),
                UseDescriptionForTitle = true,
                SelectedPath = Expand(box.Text),
                ShowNewFolderButton = true
            };
            if (dlg.ShowDialog() == WinForms.DialogResult.OK)
            {
                TrySet(dlg.SelectedPath);
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
            TrySet(DefaultFor(kind));
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
        stack.Children.Add(conflictText);

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
                dot.ToolTip = L.T("OK — Ordner existiert", "OK — folder exists");
            }
            else if (PathLikelyValid(expanded))
            {
                dot.Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0xA8, 0x40));
                dot.ToolTip = L.T("Ordner existiert noch nicht — wird beim Speichern erstellt", "Folder doesn't exist yet — created on save");
            }
            else
            {
                dot.Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0x4E, 0x4E));
                dot.ToolTip = L.T("Pfad ungültig", "Invalid path");
            }
        }
        catch
        {
            dot.Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0x4E, 0x4E));
            dot.ToolTip = L.T("Pfad ungültig", "Invalid path");
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

    private static string FolderName(PathKind kind) => kind == PathKind.Clips
        ? L.T("Clips-Ordner", "clips folder")
        : L.T("Screenshots-Ordner", "screenshots folder");

    /// <summary>
    /// Why <paramref name="candidate"/> cannot be saved for <paramref name="kind"/>, or null when
    /// it is fine. The replay buffer drops a segment file every few seconds; if that folder is the
    /// clips/screenshots folder (or inside it) the gallery lists every segment as a new clip.
    /// Checked against the SAVED values of the other rows.
    /// </summary>
    private static string? ConflictReason(AppHost host, PathKind kind, string candidate)
    {
        if (kind == PathKind.Buffer)
        {
            foreach (var other in new[] { PathKind.Clips, PathKind.Screenshots })
            {
                var otherPath = GetPath(host, other);
                if (!PathRules.IsSameOrInside(candidate, otherPath)) continue;

                var name = FolderName(other);
                var shown = Expand(otherPath);
                return L.T(
                    $"Der Buffer-Ordner darf nicht der {name} ({shown}) sein und nicht darin liegen.\n\nDie Replay-Segmente (alle paar Sekunden eine neue Datei) würden sonst als Clips in der Galerie erscheinen.\n\nDie Änderung wurde nicht übernommen.",
                    $"The buffer folder must not be the {name} ({shown}) or inside it.\n\nOtherwise the replay segments (a new file every few seconds) would show up as clips in the gallery.\n\nThe change was not applied.");
            }
            return null;
        }

        var buffer = GetPath(host, PathKind.Buffer);
        if (!PathRules.IsSameOrInside(buffer, candidate)) return null;

        var folder = FolderName(kind);
        var bufferShown = Expand(buffer);
        return L.T(
            $"Der aktuelle Buffer-Ordner ({bufferShown}) wäre dann der {folder} oder läge darin.\n\nDie Replay-Segmente (alle paar Sekunden eine neue Datei) würden sonst als Clips in der Galerie erscheinen. Ändere zuerst den Buffer-Ordner.\n\nDie Änderung wurde nicht übernommen.",
            $"The current buffer folder ({bufferShown}) would then be the {folder} or inside it.\n\nOtherwise the replay segments (a new file every few seconds) would show up as clips in the gallery. Change the buffer folder first.\n\nThe change was not applied.");
    }

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
