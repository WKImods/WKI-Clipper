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

    /// <summary>What the sliders and the image choice edit: all games or one game's profile ("Gilt für").</summary>
    private CrosshairProfileEditor? _editor;
    private CrosshairTargetBar? _targetBar;

    public CrosshairView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => OnVisibleChanged();
    }

    /// <summary>
    /// A user's pick in "Gilt für" lasts while the widget is on screen; when it opens again the editor follows the
    /// detected game (see <see cref="CrosshairProfileEditor.FollowDetectedGame"/>). Hiding this view itself (the tab switch
    /// of the container) keeps the pick; the container releases it when the whole widget leaves the screen.
    /// </summary>
    private void OnVisibleChanged()
    {
        if (_editor is not null)
        {
            if (!IsVisible) { if (Visibility == Visibility.Visible) _editor.ReleaseUserPick(); }
            else { SyncToDetectedGame(); _targetBar?.Refresh(); _shownOverrides = _editor.HasOverrides; }
        }
        PublishEditTarget();
    }

    /// <summary>The widget left the screen: the next time it opens the editor follows the detected game again.</summary>
    public void ReleaseUserPick() => _editor?.ReleaseUserPick();

    /// <summary>
    /// Until the user picks a target, the editor shows the detected game's profile when it has crosshair values of
    /// its own (so the board opened over a game previews and drags THAT look), else all games. Rebuilds the sliders
    /// and the library marker when the target moved.
    /// </summary>
    private void SyncToDetectedGame()
    {
        if (_editor is null || App.Host is not { } host) return;
        var game = host.CurrentGame;
        if (!_editor.FollowDetectedGame(game?.ExeName, game?.DisplayName)) return;
        RebuildSliders();
        RefreshLibrary();
    }

    /// <summary>The detected game changed (the host calls this): the editor may follow it; list, hint and sliders follow.</summary>
    public void OnGameChanged()
    {
        if (_targetBar is null) return;
        SyncToDetectedGame();
        RefreshTargets();
    }

    /// <summary>
    /// Tells the host which game this widget is editing, so a drag of the crosshair on screen can create that
    /// game's profile. Only while the widget is actually on screen - a hidden widget must not capture drags.
    /// </summary>
    private void PublishEditTarget()
    {
        var host = App.Host;
        if (host is null) return;
        var next = IsLoaded && IsVisible && _editor is { } e ? new CrosshairEditTarget(e.TargetKey, e.TargetName) : null;
        if (Equals(host.CrosshairEditing, next)) return;
        host.CrosshairEditing = next;
        host.RefreshCrosshair();   // the overlay previews the look being edited (or goes back to the game's)
    }

    /// <summary>A game came to the front or a profile appeared (the host calls this): list, hint and sliders follow.</summary>
    public void RefreshTargets()
    {
        if (_targetBar is null) return;
        _targetBar.Refresh();
        _shownOverrides = _editor?.HasOverrides ?? false;
        PublishEditTarget();
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
        PublishEditTarget();
    }

    /// <summary>The STANDARD crosshair settings (enable switch, snapping, grid and the all-games look).</summary>
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

        if (SlidersPanel.Children.Count > 0)
        {
            SyncToDetectedGame();
            _targetBar?.Refresh();
            _shownOverrides = _editor?.HasOverrides ?? false;
            PublishEditTarget();
            RefreshLibrary();
            return;
        }

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

        _editor = new CrosshairProfileEditor(host.Settings.Current);
        // Start on the detected game's profile when it has crosshair values (before the bar and sliders are built).
        _editor.FollowDetectedGame(host.CurrentGame?.ExeName, host.CurrentGame?.DisplayName);
        _shownOverrides = _editor.HasOverrides;
        _targetBar = new CrosshairTargetBar(host.Settings.Current, _editor, () => host.CurrentGame);
        _targetBar.TargetChanged += OnTargetChanged;
        _targetBar.ProfileReset += () => { SaveAndRefresh(); RebuildSliders(); RefreshLibrary(); RefreshTargets(); };
        TargetHost.Content = _targetBar;

        BuildSliders();
        RefreshLibrary();
        PublishEditTarget();
    }

    /// <summary>Another "Gilt für" entry was picked: sliders and the active image show that target's values.</summary>
    private void OnTargetChanged()
    {
        _shownOverrides = _editor?.HasOverrides ?? false;
        RebuildSliders();
        RefreshLibrary();
        PublishEditTarget();
    }

    private void RebuildSliders()
    {
        SlidersPanel.Children.Clear();
        BuildSliders();
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
        bool isActive = _editor!.ActiveId == entry.Id;

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
            // First import becomes the active (standard) crosshair right away - whatever "Gilt für" shows.
            if (string.IsNullOrEmpty(S!.ActiveId))
            {
                S.ActiveId = last.Id;
                host.Settings.Save();
                host.RefreshCrosshair();
            }
            RefreshLibrary();
        }
        if (failed > 0)
            ToastService.Show(ToastKind.Warning, L.T("Import", "Import"),
                L.T($"{failed} Datei(en) konnten nicht importiert werden.", $"{failed} file(s) could not be imported."),
                durationSeconds: 4);
    }

    private void SelectEntry(CrosshairEntry entry)
    {
        var host = App.Host; if (host is null) return;
        _editor!.SetActiveId(entry.Id);   // all games or the selected game's profile
        host.Settings.Save();
        RefreshLibrary();
        host.RefreshCrosshair();
        RefreshTargets();
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
        // A game profile must not keep pointing at the deleted image (it would fall back to the standard anyway).
        GameProfiles.ForgetCrosshairId(host.Settings.Current, entry.Id);
        host.Settings.Save();
        RefreshLibrary();
        host.RefreshCrosshair();
        RefreshTargets();   // a profile that only had this image is gone: list, hint and "Wie Standard" follow
    }

    // ---- sliders ----

    private void BuildSliders()
    {
        _building = true;

        // --- Positioning: snap-to-grid + a hard "dead center" reset ---
        // Snapping and the grid are one global setting, not part of a game's profile: with a game selected the
        // labels say so, so they do not look like they would create a profile.
        bool forProfile = _editor!.IsProfile;
        var snapBox = new System.Windows.Controls.CheckBox
        {
            Content = forProfile
                ? L.T("Nach Raster verschieben (gilt für alle Spiele)", "Snap to grid (applies to all games)")
                : L.T("Nach Raster verschieben", "Snap to grid"),
            IsChecked = S!.SnapToGrid,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 2)
        };
        var gridRow = MakeSlider(forProfile ? L.T("Raster (alle)", "Grid (all)") : L.T("Raster", "Grid"), 5, 100, S.GridSize,
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
            // All games: null = dead center of the primary screen. A game: pinned to the centre of ITS monitor.
            _editor!.CenterExactly();
            SaveAndRefresh();
        };
        SlidersPanel.Children.Add(centerBtn);

        // All values below belong to the "Gilt für" target: the standard or one game's profile.
        var e = _editor!;
        SlidersPanel.Children.Add(MakeSlider(L.T("Größe", "Size"), 0.1, 5.0, e.Get(CrosshairField.Scale), v => e.Set(CrosshairField.Scale, v), "0.00×"));
        SlidersPanel.Children.Add(MakeSlider(L.T("Deckkraft", "Opacity"), 0.05, 1.0, e.Get(CrosshairField.Opacity), v => e.Set(CrosshairField.Opacity, v), "0%"));
        SlidersPanel.Children.Add(MakeSlider(L.T("Helligkeit", "Brightness"), -1.0, 1.0, e.Get(CrosshairField.Brightness), v => e.Set(CrosshairField.Brightness, v), "+0.00;-0.00;0.00"));
        SlidersPanel.Children.Add(MakeSlider(L.T("Kontrast", "Contrast"), 0.2, 3.0, e.Get(CrosshairField.Contrast), v => e.Set(CrosshairField.Contrast, v), "0.00"));
        SlidersPanel.Children.Add(MakeSlider(L.T("Sättigung", "Saturation"), 0.0, 2.0, e.Get(CrosshairField.Saturation), v => e.Set(CrosshairField.Saturation, v), "0.00"));
        SlidersPanel.Children.Add(MakeSlider(L.T("Rot", "Red"), 0.0, 2.0, e.Get(CrosshairField.GainR), v => e.Set(CrosshairField.GainR, v), "0.00"));
        SlidersPanel.Children.Add(MakeSlider(L.T("Grün", "Green"), 0.0, 2.0, e.Get(CrosshairField.GainG), v => e.Set(CrosshairField.GainG, v), "0.00"));
        SlidersPanel.Children.Add(MakeSlider(L.T("Blau", "Blue"), 0.0, 2.0, e.Get(CrosshairField.GainB), v => e.Set(CrosshairField.GainB, v), "0.00"));

        // A game's profile has its own "Wie Standard" in the selector bar; this button is for the standard look.
        if (!e.IsProfile)
        {
            var reset = new Button
            {
                Content = L.T("Bildwerte zurücksetzen", "Reset image values"),
                Margin = new Thickness(0, 8, 0, 0)
            };
            reset.Click += (_, _) => ResetImageValues();
            SlidersPanel.Children.Add(reset);
        }
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
            SaveAndRefresh();
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
        // The first change of a game's profile (or its last reset) flips "has own values": hint and button follow.
        if (_editor is { IsProfile: true } e && e.HasOverrides != _shownOverrides) RefreshTargets();
    }

    /// <summary>What the target bar last showed for "this game has own crosshair values".</summary>
    private bool _shownOverrides;

    private void ResetImageValues()
    {
        var host = App.Host; if (host is null) return;
        var s = S!;   // only reachable for the standard look; a game's profile has "Wie Standard" in the bar
        // Image values only — position, snapping and grid stay as the user set them.
        s.Brightness = 0; s.Contrast = 1; s.Saturation = 1;
        s.RedGain = 1; s.GreenGain = 1; s.BlueGain = 1; s.Opacity = 1; s.Scale = 1;
        host.Settings.Save();
        host.RefreshCrosshair();

        // Rebuild the slider row so the thumbs jump back to their defaults.
        RebuildSliders();
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
