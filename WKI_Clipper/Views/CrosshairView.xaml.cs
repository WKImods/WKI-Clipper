using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WKI_Clipper.Models;
using WKI_Clipper.Services;

namespace WKI_Clipper.Views;

/// <summary>
/// Config panel for the crosshair overlay: the PNG library (import / pick / delete)
/// plus the image sliders. Every change writes settings and asks the host to re-render
/// the live overlay, so adjustments are visible immediately.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class CrosshairView : UserControl
{
    private bool _building;

    private bool _watching;

    public CrosshairView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Folder changed on disk (files dropped in via Explorer) → refresh the list.</summary>
    private void OnLibraryChanged()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsLoaded) return;
            var host = App.Host;
            if (host is null) return;

            // If nothing is selected yet, adopt the first available crosshair so a
            // dropped-in file is usable right away.
            if (string.IsNullOrEmpty(S!.ActiveId) && host.Crosshairs.Entries.Count > 0)
            {
                S.ActiveId = host.Crosshairs.Entries[0].Id;
                host.Settings.Save();
                host.RefreshCrosshair();
            }
            RefreshLibrary();
        }));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var host = App.Host;
        if (host != null && _watching)
        {
            host.Crosshairs.LibraryChanged -= OnLibraryChanged;
            _watching = false;
        }
    }

    private CrosshairSettings? S => App.Host?.Settings.Current.Crosshair;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var host = App.Host;
        if (host is null) return;

        if (!_watching)
        {
            host.Crosshairs.LibraryChanged += OnLibraryChanged;
            _watching = true;
        }
        // Catch anything dropped in while this view wasn't alive.
        host.Crosshairs.ScanFolder();

        if (SlidersPanel.Children.Count > 0) { RefreshLibrary(); return; }

        EnabledBox.Content = L.T("Crosshair anzeigen", "Show crosshair");
        HotkeyHint.Text = L.T("Ein-/Ausschalten mit Strg+Alt+C. Bei geöffnetem Overlay lässt sich das Crosshair mit der Maus verschieben — beim Spielen sind Klicks durchlässig.",
                              "Toggle with Ctrl+Alt+C. While the overlay board is open you can drag the crosshair; during gameplay clicks pass through it.");
        LibHeading.Text = L.T("Bibliothek", "Library");
        EmptyHint.Text = L.T("Noch keine PNGs. Füge unten welche hinzu — sie werden in den Clipper kopiert und bleiben dauerhaft verfügbar.",
                             "No PNGs yet. Add some below — they are copied into the clipper and stay available.");
        ImportBtn.Content = L.T("PNG hinzufügen…", "Add PNG…");
        FolderBtn.Content = L.T("Ordner öffnen", "Open folder");

        EnabledBox.IsChecked = S!.Enabled;
        EnabledBox.Checked += (_, _) => SetEnabled(true);
        EnabledBox.Unchecked += (_, _) => SetEnabled(false);

        ImportBtn.Click += (_, _) => ImportPng();
        FolderBtn.Click += (_, _) =>
        {
            // Create first: the folder only exists once something was imported, and
            // Explorer errors out on a missing path.
            try { System.IO.Directory.CreateDirectory(host.Crosshairs.Directory); } catch { }
            try { Process.Start("explorer.exe", host.Crosshairs.Directory); }
            catch (Exception ex) { Logger.Warn("Open crosshair folder failed: " + ex.Message); }
        };

        BuildSliders();
        RefreshLibrary();
    }

    // ---- library ----

    private void RefreshLibrary()
    {
        var host = App.Host;
        if (host is null) return;

        LibraryContainer.Children.Clear();
        var entries = host.Crosshairs.Entries;
        EmptyHint.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var entry in entries)
            LibraryContainer.Children.Add(BuildEntryRow(entry));
    }

    private FrameworkElement BuildEntryRow(CrosshairEntry entry)
    {
        var host = App.Host!;
        bool isActive = S!.ActiveId == entry.Id;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Thumbnail on a checkerboard-ish backdrop so white crosshairs stay visible.
        var thumbHost = new Border
        {
            Width = 38, Height = 38,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x3B)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new System.Windows.Controls.Image
            {
                Source = CrosshairImage.LoadRaw(host.Crosshairs.FullPath(entry)),
                Stretch = Stretch.Uniform,
                Margin = new Thickness(3)
            }
        };
        Grid.SetColumn(thumbHost, 0);
        grid.Children.Add(thumbHost);

        var nameBlock = new TextBlock
        {
            Text = entry.Name,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = (Brush)FindResource(isActive ? "AccentBrush" : "TextBrush")
        };
        Grid.SetColumn(nameBlock, 1);
        grid.Children.Add(nameBlock);

        var btns = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        var useBtn = new Button
        {
            Content = isActive ? L.T("Aktiv", "Active") : L.T("Wählen", "Use"),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 0),
            IsEnabled = !isActive
        };
        if (isActive) useBtn.Style = (Style)FindResource("AccentButton");
        useBtn.Click += (_, _) => SelectEntry(entry);
        btns.Children.Add(useBtn);

        var delBtn = new Button
        {
            Content = "✕",
            Padding = new Thickness(8, 4, 8, 4),
            ToolTip = L.T("Aus der Bibliothek entfernen", "Remove from library")
        };
        delBtn.Click += (_, _) => DeleteEntry(entry);
        btns.Children.Add(delBtn);

        Grid.SetColumn(btns, 2);
        grid.Children.Add(btns);

        return new Border
        {
            Background = (Brush)FindResource(isActive ? "PanelHoverBrush" : "PanelBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 2, 0, 2),
            Child = grid
        };
    }

    private void ImportPng()
    {
        var host = App.Host;
        if (host is null) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("Crosshair-PNG auswählen", "Choose crosshair PNG"),
            Filter = "PNG (*.png)|*.png",
            Multiselect = true
        };
        if (dlg.ShowDialog() != true) return;

        CrosshairEntry? last = null;
        int failed = 0;
        foreach (var file in dlg.FileNames)
        {
            var added = host.Crosshairs.Import(file);
            if (added != null) last = added; else failed++;
        }

        if (last != null)
        {
            // First import becomes the active crosshair right away.
            if (string.IsNullOrEmpty(S!.ActiveId)) SelectEntry(last);
            else RefreshLibrary();
        }
        if (failed > 0)
            ToastService.Show(ToastKind.Warning, L.T("Import", "Import"),
                L.T($"{failed} Datei(en) konnten nicht importiert werden.", $"{failed} file(s) could not be imported."),
                durationSeconds: 4);
    }

    private void SelectEntry(CrosshairEntry entry)
    {
        var host = App.Host; if (host is null) return;
        S!.ActiveId = entry.Id;
        host.Settings.Save();
        RefreshLibrary();
        host.RefreshCrosshair();
    }

    private void DeleteEntry(CrosshairEntry entry)
    {
        var host = App.Host; if (host is null) return;
        var answer = MessageBox.Show(
            L.T($"'{entry.Name}' aus der Bibliothek löschen?", $"Delete '{entry.Name}' from the library?"),
            "WKI Clipper", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        bool wasActive = S!.ActiveId == entry.Id;
        host.Crosshairs.Remove(entry.Id);
        if (wasActive)
        {
            // Fall back to whatever is left, else nothing.
            S.ActiveId = host.Crosshairs.Entries.Count > 0 ? host.Crosshairs.Entries[0].Id : null;
        }
        host.Settings.Save();
        RefreshLibrary();
        host.RefreshCrosshair();
    }

    // ---- sliders ----

    private void BuildSliders()
    {
        _building = true;

        // --- Positioning: snap-to-grid + a hard "dead center" reset ---
        var snapBox = new System.Windows.Controls.CheckBox
        {
            Content = L.T("Nach Raster verschieben", "Snap to grid"),
            IsChecked = S!.SnapToGrid,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 2)
        };
        var gridRow = MakeSlider(L.T("Raster", "Grid"), 5, 100, S.GridSize,
            v => S.GridSize = (int)Math.Round(v), "0 px");
        gridRow.IsEnabled = S.SnapToGrid;
        snapBox.Checked += (_, _) => { S.SnapToGrid = true; gridRow.IsEnabled = true; SaveAndRefresh(); };
        snapBox.Unchecked += (_, _) => { S.SnapToGrid = false; gridRow.IsEnabled = false; SaveAndRefresh(); };
        SlidersPanel.Children.Add(snapBox);
        SlidersPanel.Children.Add(gridRow);

        var centerBtn = new Button
        {
            Content = L.T("Exakt zentrieren", "Center exactly"),
            Margin = new Thickness(0, 2, 0, 8)
        };
        centerBtn.Click += (_, _) =>
        {
            S.CenterX = null; S.CenterY = null;   // null = dead center of the primary screen
            SaveAndRefresh();
        };
        SlidersPanel.Children.Add(centerBtn);

        SlidersPanel.Children.Add(MakeSlider(L.T("Größe", "Size"), 0.1, 5.0, S!.Scale, v => S.Scale = v, "0.00×"));
        SlidersPanel.Children.Add(MakeSlider(L.T("Deckkraft", "Opacity"), 0.05, 1.0, S.Opacity, v => S.Opacity = v, "0%"));
        SlidersPanel.Children.Add(MakeSlider(L.T("Helligkeit", "Brightness"), -1.0, 1.0, S.Brightness, v => S.Brightness = v, "+0.00;-0.00;0.00"));
        SlidersPanel.Children.Add(MakeSlider(L.T("Kontrast", "Contrast"), 0.2, 3.0, S.Contrast, v => S.Contrast = v, "0.00"));
        SlidersPanel.Children.Add(MakeSlider(L.T("Sättigung", "Saturation"), 0.0, 2.0, S.Saturation, v => S.Saturation = v, "0.00"));
        SlidersPanel.Children.Add(MakeSlider(L.T("Rot", "Red"), 0.0, 2.0, S.RedGain, v => S.RedGain = v, "0.00"));
        SlidersPanel.Children.Add(MakeSlider(L.T("Grün", "Green"), 0.0, 2.0, S.GreenGain, v => S.GreenGain = v, "0.00"));
        SlidersPanel.Children.Add(MakeSlider(L.T("Blau", "Blue"), 0.0, 2.0, S.BlueGain, v => S.BlueGain = v, "0.00"));

        var reset = new Button
        {
            Content = L.T("Bildwerte zurücksetzen", "Reset image values"),
            Margin = new Thickness(0, 8, 0, 0)
        };
        reset.Click += (_, _) => ResetImageValues();
        SlidersPanel.Children.Add(reset);
        _building = false;
    }

    private FrameworkElement MakeSlider(string label, double min, double max, double value,
        Action<double> apply, string format)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });

        var lbl = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("MutedBrush")
        };
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        var slider = new Slider
        {
            Minimum = min, Maximum = max,
            Value = Math.Clamp(value, min, max),
            VerticalAlignment = VerticalAlignment.Center,
            IsMoveToPointEnabled = true
        };
        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);

        var val = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Foreground = (Brush)FindResource("TextBrush"),
            FontSize = 12,
            Text = Fmt(slider.Value, format)
        };
        Grid.SetColumn(val, 2);
        grid.Children.Add(val);

        slider.ValueChanged += (_, e) =>
        {
            val.Text = Fmt(e.NewValue, format);
            if (_building) return;
            apply(e.NewValue);
            var host = App.Host;
            if (host is null) return;
            host.Settings.Save();
            host.RefreshCrosshair();
        };
        return grid;
    }

    private static string Fmt(double v, string format)
        => format == "0%" ? (v * 100).ToString("0") + "%"
         : format == "0.00×" ? v.ToString("0.00") + "×"
         : format == "0 px" ? v.ToString("0") + " px"
         : v.ToString(format);

    private void SaveAndRefresh()
    {
        var host = App.Host; if (host is null) return;
        host.Settings.Save();
        host.RefreshCrosshair();
    }

    private void ResetImageValues()
    {
        var host = App.Host; if (host is null) return;
        var s = S!;
        // Image values only — position, snapping and grid stay as the user set them.
        s.Brightness = 0; s.Contrast = 1; s.Saturation = 1;
        s.RedGain = 1; s.GreenGain = 1; s.BlueGain = 1; s.Opacity = 1; s.Scale = 1;
        host.Settings.Save();
        host.RefreshCrosshair();

        // Rebuild the slider row so the thumbs jump back to their defaults.
        SlidersPanel.Children.Clear();
        BuildSliders();
    }

    private void SetEnabled(bool on)
    {
        var host = App.Host; if (host is null) return;
        if (S!.Enabled == on) return;
        S.Enabled = on;
        host.Settings.Save();
        host.RefreshCrosshair();
    }

    /// <summary>Lets the host push external changes (hotkey toggle) back into the checkbox.</summary>
    public void SyncEnabled(bool on)
    {
        if (EnabledBox.IsChecked == on) return;
        EnabledBox.IsChecked = on;
    }
}
