using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using WKI_Clipper.Services;

namespace WKI_Clipper.Views;

public partial class ClipsView : UserControl
{
    private enum Filter { All, Clips, Recordings, Screenshots }
    private Filter _filter = Filter.All;
    private readonly Dictionary<Filter, System.Windows.Controls.Button> _filterButtons = new();
    private string _search = "";
    private bool _favOnly;
    /// <summary>Selected game folder; null = all games.</summary>
    private string? _game;
    /// <summary>Label for files saved before per-game sorting existed (they sit in the base folder).</summary>
    private static string UnsortedLabel => L.T("Ohne Spiel (älter)", "No game (older)");
    private bool _rebuildingGameFilter;

    public ClipsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var host = App.Host;
        if (host is null) return;

        if (FilterRow.Children.Count == 0)
        {
            RefreshBtn.Content = L.T("Aktualisieren", "Refresh");
            OpenClipsFolderBtn.Content = L.T("Clips-Ordner", "Clips folder");
            OpenShotsFolderBtn.Content = L.T("Screenshots-Ordner", "Screenshots folder");
            EmptyHint.Text = L.T("Keine Dateien gefunden. Drück F9 für nen Clip oder F10 für nen Screenshot.",
                                 "No files found. Press F9 for a clip or F10 for a screenshot.");
            SearchBox.ToolTip = L.T("Nach Dateiname oder Notiz suchen…", "Search by file name or note…");
            FavOnly.Content = L.T("★ Favoriten", "★ Favorites");
            BuildFilterButtons();
            RefreshBtn.Click += (_, _) => Reload(host);
            OpenClipsFolderBtn.Click += (_, _) => OpenFolder(SettingsService.ExpandPath(host.Settings.Current.Output.ClipsFolder));
            OpenShotsFolderBtn.Click += (_, _) => OpenFolder(SettingsService.ExpandPath(host.Settings.Current.Output.ScreenshotsFolder));
        }

        Reload(host);
    }

    private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _search = SearchBox.Text ?? "";
        if (App.Host != null && ItemsContainer != null) Reload(App.Host);
    }

    private void OnFavOnlyChanged(object sender, RoutedEventArgs e)
    {
        _favOnly = FavOnly.IsChecked == true;
        if (App.Host != null && ItemsContainer != null) Reload(App.Host);
    }

    private void OnGameFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuildingGameFilter) return;
        _game = GameFilter.SelectedItem is ComboBoxItem { Tag: string g } ? g : null;
        if (App.Host != null && ItemsContainer != null) Reload(App.Host);
    }

    /// <summary>
    /// Refills the game dropdown from what is actually on disk, keeping the current choice
    /// if that game still exists. Tag "" means "files without a game folder".
    /// </summary>
    private void RebuildGameFilter(IEnumerable<string?> games)
    {
        _rebuildingGameFilter = true;
        try
        {
            GameFilter.Items.Clear();
            GameFilter.Items.Add(new ComboBoxItem { Content = L.T("Alle Spiele", "All games"), Tag = null });

            var distinct = games.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var g in distinct.Where(g => g != null).OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase))
                GameFilter.Items.Add(new ComboBoxItem { Content = g, Tag = g });
            if (distinct.Contains(null))
                GameFilter.Items.Add(new ComboBoxItem { Content = UnsortedLabel, Tag = "" });

            var keep = GameFilter.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Tag as string, _game, StringComparison.OrdinalIgnoreCase));
            if (keep is null) _game = null;   // selected game vanished (folder deleted/renamed)
            GameFilter.SelectedItem = keep ?? GameFilter.Items[0];
        }
        finally { _rebuildingGameFilter = false; }
    }

    private void BuildFilterButtons()
    {
        foreach (var f in new[] { Filter.All, Filter.Clips, Filter.Recordings, Filter.Screenshots })
        {
            var label = f switch
            {
                Filter.All         => L.T("Alle", "All"),
                Filter.Clips       => "Clips (F9)",
                Filter.Recordings  => "Recordings",
                Filter.Screenshots => "Screenshots",
                _                  => f.ToString()
            };
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 6, 0)
            };
            btn.Click += (_, _) =>
            {
                _filter = f;
                UpdateFilterButtonStates();
                if (App.Host != null) Reload(App.Host);
            };
            _filterButtons[f] = btn;
            FilterRow.Children.Add(btn);
        }
        UpdateFilterButtonStates();
    }

    private void UpdateFilterButtonStates()
    {
        foreach (var kv in _filterButtons)
        {
            kv.Value.Background = kv.Key == _filter
                ? (Brush)FindResource("AccentBrush")
                : (Brush)FindResource("PanelBrush");
        }
    }

    private void Reload(AppHost host)
    {
        ItemsContainer.Children.Clear();

        var items = new List<MediaEntry>();

        var clipsDir = SettingsService.ExpandPath(host.Settings.Current.Output.ClipsFolder);
        var shotsDir = SettingsService.ExpandPath(host.Settings.Current.Output.ScreenshotsFolder);
        var meta = host.GalleryMeta;
        // A locked notes file is retried on every refresh, and edits typed meanwhile are saved then.
        meta.Flush();
        // Old file-name-keyed notes/stars need the real folders to find their files (no-op once done).
        meta.EnsureMigrated(clipsDir, shotsDir);

        // Base folder AND one level of game subfolders: since per-game sorting, new captures
        // live in Clips\<Game>\ — a flat scan would silently hide every one of them.
        // Replay-buffer segments are never clips, even if the buffer folder overlaps this one.
        foreach (var (f, game) in GameFolderNaming.EnumerateMedia(clipsDir, f =>
                 f.Extension.ToLowerInvariant() is ".gif" or ".mp4" or ".mkv" or ".mov"
                 && !PathRules.IsBufferSegment(f.Name)))
        {
            var kind = f.Extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ? MediaKind.Gif
                     : f.Name.StartsWith("Rec_", StringComparison.OrdinalIgnoreCase) ? MediaKind.Recording
                     : MediaKind.Clip;
            items.Add(new MediaEntry(f.FullName, f.Name, f.LastWriteTime, f.Length, kind, game,
                                     GalleryMetaKey.For(GalleryBase.Clips, clipsDir, f.FullName)));
        }

        foreach (var (f, game) in GameFolderNaming.EnumerateMedia(shotsDir, f =>
                 f.Extension.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg"))
        {
            items.Add(new MediaEntry(f.FullName, f.Name, f.LastWriteTime, f.Length, MediaKind.Screenshot, game,
                                     GalleryMetaKey.For(GalleryBase.Shots, shotsDir, f.FullName)));
        }

        RebuildGameFilter(items.Select(i => i.Game));

        var search = _search.Trim();
        var filtered = items
            .Where(it => _game is null
                         || (_game.Length == 0 ? it.Game is null
                                               : string.Equals(it.Game, _game, StringComparison.OrdinalIgnoreCase)))
            .Where(it => _filter switch
            {
                Filter.All         => true,
                Filter.Clips       => it.Kind == MediaKind.Clip,
                Filter.Recordings  => it.Kind == MediaKind.Recording,
                Filter.Screenshots => it.Kind == MediaKind.Screenshot,
                _                  => true
            })
            .Where(it => search.Length == 0
                         || it.FileName.Contains(search, StringComparison.OrdinalIgnoreCase)
                         || meta.GetNote(it.Key).Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(it => !_favOnly || meta.IsFavorite(it.Key))
            .OrderByDescending(it => it.CreatedAt)
            .Take(200)
            .ToList();

        EmptyHint.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var it in filtered)
        {
            ItemsContainer.Children.Add(BuildRow(it));
        }
        UpdateMetaWarning();
    }

    /// <summary>
    /// Notes can be evidence, so "not saved" must never go unnoticed: while the notes file cannot be
    /// read or written, a banner says so (edits stay in memory and are retried on the next refresh).
    /// </summary>
    private void UpdateMetaWarning()
    {
        var problem = App.Host?.GalleryMeta.HasProblem == true;
        MetaWarning.Visibility = problem ? Visibility.Visible : Visibility.Collapsed;
        if (problem)
            MetaWarningText.Text = L.T(
                "Notizen konnten nicht gespeichert oder gelesen werden — die Datei ist gesperrt oder defekt. Neue Eingaben bleiben im Speicher und werden beim nächsten Aktualisieren erneut gespeichert. Nicht beenden, bevor diese Meldung weg ist.",
                "Notes could not be saved or read — the file is locked or damaged. New entries stay in memory and are saved again on the next refresh. Do not quit before this message is gone.");
    }

    private FrameworkElement BuildRow(MediaEntry it)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // star
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // edit note
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // open
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // "…" menu

        // Icon / Kind badge
        var iconBg = new Border
        {
            Width = 32, Height = 32,
            CornerRadius = new CornerRadius(4),
            Background = (Brush)FindResource(it.Kind switch
            {
                MediaKind.Clip       => "AccentBrush",
                MediaKind.Recording  => "DangerBrush",
                MediaKind.Screenshot => "PanelHoverBrush",
                MediaKind.Gif        => "AccentDimBrush",
                _                    => "PanelBrush"
            }),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        iconBg.Child = new TextBlock
        {
            Text = it.Kind == MediaKind.Screenshot ? "PNG" : it.Kind == MediaKind.Gif ? "GIF" : "MP4",
            FontSize = 10,
            FontWeight = System.Windows.FontWeights.Bold,
            Foreground = (Brush)System.Windows.Media.Brushes.White,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(iconBg, 0);
        grid.Children.Add(iconBg);

        // Note (the row title, when there is one) + file name + meta
        var meta = App.Host?.GalleryMeta;
        var title = new RowNoteTitle(meta?.GetNote(it.Key) ?? "", text =>
        {
            var saved = App.Host?.GalleryMeta.SetNote(it.Key, text) ?? false;
            UpdateMetaWarning();
            return saved;
        });
        var fileName = new TextBlock
        {
            Text = it.FileName,
            Foreground = (Brush)FindResource("TextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        // With a note the note is the title; the file name steps back to a plain line below it.
        void StyleFileName(bool hasNote)
        {
            fileName.FontWeight = hasNote ? System.Windows.FontWeights.Normal : System.Windows.FontWeights.SemiBold;
            fileName.FontSize = hasNote ? 12 : 13;
        }
        StyleFileName(title.Note.Length > 0);
        title.NoteChanged += note => StyleFileName(note.Length > 0);

        var textStack = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(title);
        textStack.Children.Add(fileName);
        textStack.Children.Add(new TextBlock
        {
            Text = $"{(it.Game is { } g ? g + "  ·  " : "")}{it.CreatedAt:dd.MM.yyyy HH:mm:ss}  ·  {FormatSize(it.SizeBytes)}",
            Style = (Style)FindResource("MutedStyle")
        });
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        // Kind label
        var kindLabel = new TextBlock
        {
            Text = it.Kind switch
            {
                MediaKind.Clip       => "Clip",
                MediaKind.Recording  => "Recording",
                MediaKind.Screenshot => "Screenshot",
                MediaKind.Gif        => "GIF",
                _                    => ""
            },
            Style = (Style)FindResource("MutedStyle"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0)
        };
        Grid.SetColumn(kindLabel, 2);
        grid.Children.Add(kindLabel);

        // Favorite star toggle
        var favBtn = new System.Windows.Controls.Button
        {
            Content = "★",
            FontSize = 15,
            Background = Brushes.Transparent,
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 2, 0),
            Foreground = (Brush)FindResource(meta != null && meta.IsFavorite(it.Key) ? "AccentBrush" : "MutedBrush"),
            ToolTip = L.T("Favorit", "Favorite")
        };
        favBtn.Click += (_, _) =>
        {
            if (App.Host is null) return;
            App.Host.GalleryMeta.ToggleFavorite(it.Key);   // a failed save is kept in memory; the banner says so
            bool now = App.Host.GalleryMeta.IsFavorite(it.Key);
            favBtn.Foreground = (Brush)FindResource(now ? "AccentBrush" : "MutedBrush");
            UpdateMetaWarning();
            if (_favOnly) Reload(App.Host);
        };
        Grid.SetColumn(favBtn, 3);
        grid.Children.Add(favBtn);

        // Edit note: the title turns into a text box
        var noteBtn = IconButton(IconGlyph.Edit, L.T("Notiz bearbeiten", "Edit note"));
        noteBtn.Click += (_, _) => title.BeginEdit();
        Grid.SetColumn(noteBtn, 4);
        grid.Children.Add(noteBtn);

        // Open button
        var openBtn = new System.Windows.Controls.Button
        {
            Content = L.T("Öffnen", "Open"),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 4, 0)
        };
        openBtn.Click += (_, _) => OpenFile(it.FilePath);
        Grid.SetColumn(openBtn, 5);
        grid.Children.Add(openBtn);

        // Everything used less often lives behind "…" (also on right-click anywhere on the row)
        var menu = BuildRowMenu(it, title);
        var moreBtn = IconButton(IconGlyph.More, L.T("Weitere Aktionen", "More actions"));
        moreBtn.Click += (_, _) =>
        {
            menu.PlacementTarget = moreBtn;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        };
        Grid.SetColumn(moreBtn, 6);
        grid.Children.Add(moreBtn);

        var card = new Border
        {
            Background = (Brush)FindResource("PanelBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 3, 0, 3),
            Cursor = Cursors.Hand,
            ContextMenu = menu,
            Child = grid
        };
        card.MouseLeftButtonUp += (_, e) =>
        {
            // A click on a control inside the row (button, the note box) is not a click on the row.
            // Events from the context menu bubble here through the logical tree even though its
            // popup is not part of the row's visuals — those are not row clicks either.
            if (e.OriginalSource is not DependencyObject source) return;
            for (DependencyObject? d = source; d != null; d = ParentOf(d))
            {
                if (d == card) { OpenFile(it.FilePath); return; }
                if (d is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.TextBoxBase) return;
            }
        };
        return card;
    }

    /// <summary>The per-row "…" menu. Phase 5 adds the Discord compress entries here.</summary>
    private ContextMenu BuildRowMenu(MediaEntry it, RowNoteTitle title)
    {
        var menu = new ContextMenu();

        var editNote = new MenuItem { Header = L.T("Notiz bearbeiten", "Edit note") };
        editNote.Click += (_, _) => title.BeginEdit();
        menu.Items.Add(editNote);

        var showInFolder = new MenuItem { Header = L.T("Im Ordner zeigen", "Show in folder") };
        showInFolder.Click += (_, _) => ShowInExplorer(it.FilePath);
        menu.Items.Add(showInFolder);

        return menu;
    }

    /// <summary>A small icon-only button in the row's muted look.</summary>
    private System.Windows.Controls.Button IconButton(string glyph, string tooltip)
    {
        var icon = IconGlyph.Make(glyph, 14);
        icon.Foreground = (Brush)FindResource("MutedBrush");
        return new System.Windows.Controls.Button
        {
            Content = icon,
            Background = Brushes.Transparent,
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 0, 2, 0),
            ToolTip = tooltip
        };
    }

    /// <summary>Visual parent where there is one, else the logical one (text runs, popup roots).</summary>
    private static DependencyObject? ParentOf(DependencyObject d)
    {
        if (d is Visual or System.Windows.Media.Media3D.Visual3D) return VisualTreeHelper.GetParent(d);
        return d is ContentElement ce ? ContentOperations.GetParent(ce) : LogicalTreeHelper.GetParent(d);
    }

    private static void OpenFile(string path)
    {
        if (!File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Error("OpenFile failed: " + path, ex); }
    }

    private static void ShowInExplorer(string path)
    {
        if (!File.Exists(path))
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                try { Process.Start("explorer.exe", dir); } catch { }
            return;
        }
        try { Process.Start("explorer.exe", "/select,\"" + path + "\""); }
        catch (Exception ex) { Logger.Error("ShowInExplorer failed: " + path, ex); }
    }

    private static void OpenFolder(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch { }
        try { Process.Start("explorer.exe", dir); } catch { }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    private enum MediaKind { Clip, Recording, Screenshot, Gif }
    /// <param name="Game">Game subfolder name, or null for files directly in the base folder.</param>
    /// <param name="Key">Identity in GalleryMetaService (base + relative path).</param>
    private sealed record MediaEntry(string FilePath, string FileName, DateTime CreatedAt, long SizeBytes, MediaKind Kind, string? Game, string Key);
}
