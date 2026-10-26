using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using WinForms = System.Windows.Forms;

namespace WKI_Clipper.Views;

/// <summary>
/// The content of the shortcut edit dialog: three lists (Spiele / Launcher / Browser) with, per row, a tick for "show on the bar", the
/// icon, the name, an optional hotkey (unbound by default), move up/down and - for games and picked browsers - remove; the standard-browser
/// selector; "Spiel hinzufügen", "Browser hinzufügen" and "Neu suchen". Every change goes through <see cref="ShortcutEditorModel"/> at
/// once (there is no OK/Cancel: the bar shows it live). Whatever needs a window of its own (pickers, files, dialogs) is only requested by
/// an event; <see cref="ShortcutEditorWindow"/> answers it. Rendered without a window in tests.
/// </summary>
public sealed class ShortcutEditorView : Border
{
    private sealed class Row
    {
        public required EditorRow Data;
        public required CheckBox Shown;
        public required Button Up, Down, Remove, ClearHotkey;
        public required HotkeyCaptureButton Hotkey;
    }

    private readonly ShortcutEditorModel _model;
    private readonly Dictionary<ShortcutGroup, StackPanel> _rowPanels = new();
    private readonly Dictionary<ShortcutGroup, TextBlock> _emptyHints = new();
    private readonly Dictionary<string, Row> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ShortcutGroup> _sectionOrder = new();
    private readonly ComboBox _defaultBrowser = new() { MinWidth = 240, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _rescan, _addGame, _addBrowser, _done;
    private IReadOnlyList<BrowserOption> _browserOptions = Array.Empty<BrowserOption>();
    private bool _updating;
    private bool _rebuildPending;

    /// <summary>"Neu suchen": scan again and remove launchers and browsers that are still missing.</summary>
    public event Action? RescanRequested;
    /// <summary>"Spiel hinzufügen…": the button to put the source menu under.</summary>
    public event Action<FrameworkElement>? AddGameRequested;
    /// <summary>"Browser hinzufügen…": the button to put the browser menu under.</summary>
    public event Action<FrameworkElement>? AddBrowserRequested;
    public event Action? DoneRequested;
    /// <summary>A hotkey was bound or cleared: the global registration has to follow.</summary>
    public event Action? HotkeysChanged;

    public ShortcutEditorView(ShortcutEditorModel model)
    {
        _model = model;
        Padding = new Thickness(18);

        _status.Foreground = (Brush)Application.Current.FindResource("MutedBrush");
        _rescan = new Button { Content = L.T("Neu suchen", "Rescan"), ToolTip = L.T("Den PC erneut absuchen und nicht mehr vorhandene Launcher und Browser entfernen.", "Scan the PC again and remove launchers and browsers that are gone.") };
        _rescan.Click += (_, _) => RescanRequested?.Invoke();
        _addGame = new Button { Content = L.T("Spiel hinzufügen…", "Add game…") };
        _addGame.Click += (_, _) => AddGameRequested?.Invoke(_addGame);
        _addBrowser = new Button { Content = L.T("Browser hinzufügen…", "Add browser…") };
        _addBrowser.Click += (_, _) => AddBrowserRequested?.Invoke(_addBrowser);
        _done = new Button { Content = L.T("Fertig", "Done"), MinWidth = 100, Style = (Style)Application.Current.FindResource("AccentButton") };
        _done.Click += (_, _) => DoneRequested?.Invoke();

        // --- header ---
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(_rescan, Dock.Right);
        header.Children.Add(_rescan);
        header.Children.Add(new TextBlock
        {
            Text = L.T("Verknüpfungen bearbeiten", "Edit shortcuts"),
            Style = (Style)Application.Current.FindResource("HeadingStyle"), Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
        });
        var hint = new TextBlock
        {
            Text = L.T("Haken = auf der Leiste zeigen. Ein Hotkey ist freiwillig und startet das Programm, auch bei geschlossenem Board.",
                       "Tick = show on the bar. A hotkey is optional and starts the program even while the board is closed."),
            Style = (Style)Application.Current.FindResource("MutedStyle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        };

        // --- the three lists ---
        var sections = new StackPanel();
        sections.Children.Add(Section(ShortcutGroup.Game, L.T("Spiele", "Games"), _addGame,
            L.T("Noch keine Spiele. \"Spiel hinzufügen…\" nennt Vorschläge, Steam, Epic oder eine eigene Datei.", "No games yet. \"Add game…\" offers suggestions, Steam, Epic or a file of your own.")));
        sections.Children.Add(Section(ShortcutGroup.Launcher, L.T("Launcher", "Launchers"), null,
            L.T("Keine Launcher gefunden. \"Neu suchen\" sucht noch einmal.", "No launchers found. \"Rescan\" looks again.")));
        sections.Children.Add(Section(ShortcutGroup.Browser, L.T("Browser", "Browsers"), _addBrowser, "", withDefaultBrowser: true));

        var scroll = new ScrollViewer
        {
            Content = sections, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };

        // --- footer ---
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(_done, Dock.Right);
        footer.Children.Add(_done);
        footer.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(hint);
        root.Children.Add(footer);
        root.Children.Add(scroll);
        Child = root;

        _defaultBrowser.SelectionChanged += (_, _) =>
        {
            if (_updating || _defaultBrowser.SelectedItem is not BrowserOption option) return;
            _model.SetDefaultBrowser(option.Id);
        };
        Rebuild();
    }

    private Border Section(ShortcutGroup group, string title, Button? addButton, string emptyHint, bool withDefaultBrowser = false)
    {
        var rows = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var empty = new TextBlock
        {
            Text = emptyHint, Style = (Style)Application.Current.FindResource("MutedStyle"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 6, 0, 2), Visibility = Visibility.Collapsed,
        };
        _rowPanels[group] = rows;
        _emptyHints[group] = empty;
        _sectionOrder.Add(group);

        var head = new DockPanel();
        if (addButton != null)
        {
            DockPanel.SetDock(addButton, Dock.Right);
            head.Children.Add(addButton);
        }
        head.Children.Add(new TextBlock
        {
            Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        });

        var body = new StackPanel();
        body.Children.Add(head);
        if (withDefaultBrowser)
        {
            var pick = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 2) };
            pick.Children.Add(new TextBlock { Text = L.T("Standard-Browser:", "Default browser:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            _defaultBrowser.DisplayMemberPath = nameof(BrowserOption.Label);
            _defaultBrowser.ToolTip = L.T("Welcher Browser startet, wenn der Standard-Browser-Knopf der Leiste geklickt wird.", "Which browser starts when the bar's default-browser button is clicked.");
            pick.Children.Add(_defaultBrowser);
            body.Children.Add(pick);
        }
        body.Children.Add(rows);
        body.Children.Add(empty);

        return new Border
        {
            Background = (Brush)Application.Current.FindResource("SoftFillBrush"),
            BorderBrush = (Brush)Application.Current.FindResource("LineBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 12),
            Child = body,
        };
    }

    // ---- rows ----

    /// <summary>
    /// Reads everything from the model again. Cheap; called after every change and when the service reports one. While a hotkey button
    /// is capturing, the rebuild waits until it is over: replacing the rows would remove the capturing button from the tree, which
    /// silently aborts the capture (a scan that ends just then is enough) and throws the pressed combination away.
    /// </summary>
    public void Rebuild()
    {
        if (IsCapturing)
        {
            _rebuildPending = true;
            return;
        }
        _rebuildPending = false;

        _rows.Clear();
        foreach (var group in _sectionOrder)
        {
            var panel = _rowPanels[group];
            panel.Children.Clear();
            var rows = _model.Rows(group);
            foreach (var data in rows) panel.Children.Add(BuildRow(data));
            _emptyHints[group].Visibility = rows.Count == 0 && _emptyHints[group].Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        _updating = true;
        try
        {
            _browserOptions = _model.DefaultBrowserOptions();
            _defaultBrowser.ItemsSource = _browserOptions;
            var selected = _model.SelectedDefaultBrowserId;
            _defaultBrowser.SelectedItem = _browserOptions.FirstOrDefault(o => string.Equals(o.Id, selected, StringComparison.OrdinalIgnoreCase)) ?? _browserOptions.FirstOrDefault();
        }
        finally { _updating = false; }
    }

    private FrameworkElement BuildRow(EditorRow data)
    {
        var muted = (Brush)Application.Current.FindResource("MutedBrush");
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2), MinHeight = 34 };
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });

        var shown = new CheckBox
        {
            IsChecked = !data.Hidden, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0),
            ToolTip = L.T("Auf der Leiste zeigen", "Show on the bar"),
        };
        Place(grid, shown, 0);

        var icon = BarIcons.Tile(data.Name, data.IconPng, 28);
        icon.Margin = new Thickness(0, 0, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Opacity = data.Missing ? 0.4 : 1.0;
        Place(grid, icon, 1);

        var name = new TextBlock
        {
            Text = data.Missing ? $"{data.Name}  ({L.T("nicht gefunden", "not found")})" : data.Name,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = data.Missing || data.Hidden ? 0.55 : 1.0, Margin = new Thickness(0, 0, 10, 0),
        };
        Place(grid, name, 2);

        var hotkey = new HotkeyCaptureButton(data.Hotkey, candidate => _model.HotkeyCollision(data.Id, candidate))
        {
            ToolTip = L.T("Globaler Hotkey (freiwillig): klicken, dann die Tasten drücken", "Global hotkey (optional): click, then press the keys"),
        };
        Place(grid, hotkey, 3);
        var clear = SmallButton(IconGlyph.Cancel, L.T("Hotkey löschen", "Clear hotkey"), 11);
        clear.Visibility = data.Hotkey is { Key: not 0 } ? Visibility.Visible : Visibility.Hidden;
        Place(grid, clear, 4);

        var up = SmallButton(IconGlyph.ChevronUp, L.T("Nach oben", "Move up"));
        up.IsEnabled = data.CanMoveUp;
        up.Margin = new Thickness(10, 0, 0, 0);
        Place(grid, up, 5);
        var down = SmallButton(IconGlyph.ChevronDown, L.T("Nach unten", "Move down"));
        down.IsEnabled = data.CanMoveDown;
        Place(grid, down, 6);
        var remove = SmallButton(IconGlyph.Cancel, data.Group == ShortcutGroup.Game ? L.T("Spiel entfernen", "Remove game") : L.T("Browser entfernen", "Remove browser"));
        remove.Visibility = data.CanRemove ? Visibility.Visible : Visibility.Hidden;   // hidden, not collapsed: the columns stay aligned
        remove.Margin = new Thickness(6, 0, 0, 0);
        Place(grid, remove, 7);

        var row = new Row { Data = data, Shown = shown, Up = up, Down = down, Remove = remove, ClearHotkey = clear, Hotkey = hotkey };
        _rows[data.Id] = row;

        shown.Click += (_, _) => { _model.SetHidden(data.Id, shown.IsChecked != true); Rebuild(); };
        up.Click += (_, _) => { _model.Move(data.Id, -1); Rebuild(); };
        down.Click += (_, _) => { _model.Move(data.Id, +1); Rebuild(); };
        remove.Click += (_, _) => { _model.Remove(data.Id); Rebuild(); };
        clear.Click += (_, _) => hotkey.Clear();
        hotkey.Notice += message => ShowStatus(message);
        hotkey.CaptureEnded += OnCaptureEnded;
        hotkey.BindingChanged += binding =>
        {
            _model.SetHotkey(data.Id, binding);
            ShowStatus("");
            clear.Visibility = binding is { Key: not 0 } ? Visibility.Visible : Visibility.Hidden;
            HotkeysChanged?.Invoke();
        };
        return grid;
    }

    /// <summary>A capture ended: a rebuild that was held back runs now - after the key handler that ended the capture has finished with its button.</summary>
    private void OnCaptureEnded()
    {
        if (!_rebuildPending) return;
        try { Dispatcher.BeginInvoke(new Action(Rebuild)); }
        catch (InvalidOperationException) { /* shutting down */ }
    }

    private static void Place(Grid grid, UIElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static Button SmallButton(string glyph, string tooltip, double glyphSize = 13)
    {
        var g = IconGlyph.Make(glyph, glyphSize);
        g.Foreground = (Brush)Application.Current.FindResource("MutedBrush");
        return new Button
        {
            Content = g, ToolTip = tooltip, Width = 28, Height = 28, Padding = new Thickness(0),
            Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center,
        };
    }

    // ---- status ----

    public void ShowStatus(string text) => _status.Text = text;

    /// <summary>A rescan is running: the button waits and the status says so.</summary>
    public void SetBusy(bool busy)
    {
        _rescan.IsEnabled = !busy;
        if (busy) _status.Text = L.T("Suche läuft …", "Scanning …");
        else if (_status.Text == L.T("Suche läuft …", "Scanning …")) _status.Text = "";
    }

    // ---- test seams ----

    internal int GroupHeaderCount => _sectionOrder.Count;
    internal IReadOnlyList<string> RowIds(ShortcutGroup group)
        => _rowPanels[group].Children.OfType<Grid>().Select(g => _rows.Values.First(r => g.Children.Contains(r.Shown)).Data.Id).ToList();
    internal string EmptyHint(ShortcutGroup group) => _emptyHints[group].Text;
    internal bool IsShown(string id) => _rows[id].Shown.IsChecked == true;
    internal bool CanRemove(string id) => _rows[id].Remove.Visibility == Visibility.Visible;
    internal HotkeyCaptureButton HotkeyButton(string id) => _rows[id].Hotkey;
    internal HotkeyBinding? HotkeyOf(string id) => _rows[id].Hotkey.Binding;
    internal string StatusText => _status.Text;
    internal bool RescanEnabled => _rescan.IsEnabled;
    internal bool IsCapturing => _rows.Values.Any(r => r.Hotkey.IsCapturing);
    internal bool RebuildPending => _rebuildPending;

    /// <summary>What a click on the tick does - the box is flipped first, as the mouse would.</summary>
    internal void ClickShown(string id)
    {
        var r = _rows[id];
        r.Shown.IsChecked = r.Shown.IsChecked != true;
        r.Shown.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }
    internal void ClickUp(string id) => _rows[id].Up.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    internal void ClickDown(string id) => _rows[id].Down.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    internal void ClickRemove(string id) => _rows[id].Remove.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    internal void ClearHotkey(string id) => _rows[id].Hotkey.Clear();
    internal void ClickRescan() => _rescan.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    internal void ClickAddGame() => _addGame.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    internal void ClickAddBrowser() => _addBrowser.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    internal void ClickDone() => _done.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    internal IReadOnlyList<string> DefaultBrowserLabels() => _browserOptions.Select(o => o.Label).ToList();
    internal string? SelectedDefaultBrowser => (_defaultBrowser.SelectedItem as BrowserOption)?.Id;
    internal void ChooseDefaultBrowser(string? id)
        => _defaultBrowser.SelectedItem = _browserOptions.First(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The shortcut edit dialog (the bar's gear): <see cref="ShortcutEditorView"/> in a modal window above the board. It answers the view's
/// requests - the source menu for games (suggestions, Steam, Epic, a picked file), the browser menu with the missing-browser dialog, and
/// "Neu suchen" (which prunes) - and keeps the lists current while the service reports changes from scans or the bar.
/// </summary>
public sealed class ShortcutEditorWindow : Window
{
    private readonly ShortcutService _service;
    private readonly ShortcutEditorModel _model;
    private readonly Action _hotkeysChanged;

    public ShortcutEditorView View { get; }

    /// <summary>The last "Neu suchen" (for tests).</summary>
    internal Task? LastRescan { get; private set; }

    /// <param name="hotkeysChanged">A hotkey was bound or cleared: the host re-registers the global hotkeys.</param>
    public ShortcutEditorWindow(ShortcutService service, ShortcutEditorModel model, Action hotkeysChanged, Window? owner = null)
    {
        _service = service;
        _model = model;
        _hotkeysChanged = hotkeysChanged;

        if (owner != null) Owner = owner;
        Title = L.T("Verknüpfungen bearbeiten", "Edit shortcuts");
        Width = 800;
        Height = 760;
        MinWidth = 640;
        MinHeight = 420;
        Topmost = true;   // above the board's topmost windows
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");

        View = new ShortcutEditorView(model);
        View.DoneRequested += Close;
        View.HotkeysChanged += () => _hotkeysChanged();
        View.RescanRequested += () => LastRescan = RescanAsync();
        View.AddGameRequested += ShowAddGameMenu;
        View.AddBrowserRequested += ShowAddBrowserMenu;
        Content = View;

        _service.ItemsChanged += OnItemsChanged;
        Closed += (_, _) => _service.ItemsChanged -= OnItemsChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Centred on the monitor the board is on - the cursor is on the gear that opened this.
        try
        {
            var screen = WinForms.Screen.FromPoint(WinForms.Cursor.Position);
            var area = DisplayGeometry.WorkArea(screen);
            double s = DisplayGeometry.ScaleOf(screen);
            Height = Math.Min(Height, Math.Max(MinHeight, area.H / s - 80));
            Width = Math.Min(Width, Math.Max(MinWidth, area.W / s - 40));
            DisplayGeometry.MoveTo(this, area.X + (area.W - Width * s) / 2, area.Y + (area.H - Height * s) / 2);
        }
        catch (Exception ex) { Logger.Warn("Placing the shortcut editor failed: " + ex.Message); }
    }

    // The service raises this on whatever thread changed something (a scan, the game detector).
    private void OnItemsChanged(object? sender, EventArgs e)
    {
        try { Dispatcher.BeginInvoke(new Action(View.Rebuild)); }
        catch (InvalidOperationException) { /* shutting down */ }
    }

    // ---- Neu suchen ----

    private async Task RescanAsync()
    {
        View.SetBusy(true);
        int before = CountScanned();
        try { await _service.RescanAsync(pruneMissing: true); }
        catch (Exception ex) { Logger.Warn("Rescan from the shortcut editor failed: " + ex.Message); }

        void Finish()
        {
            View.Rebuild();
            View.SetBusy(false);
            int removed = Math.Max(0, before - CountScanned());
            View.ShowStatus(removed > 0
                ? L.T($"{removed} nicht mehr vorhandene Einträge entfernt.", $"{removed} entries that are gone were removed.")
                : L.T("Nichts mehr zu entfernen - die Liste ist aktuell.", "Nothing left to remove - the list is up to date."));
        }
        // Back on the UI thread, whatever the await continued on (a click inside a running dispatcher keeps it; this makes it certain).
        if (Dispatcher.CheckAccess()) Finish();
        else await Dispatcher.InvokeAsync(Finish);
    }

    /// <summary>Launchers and browsers: the entries a scan can take away.</summary>
    private int CountScanned() => _model.Rows(ShortcutGroup.Launcher).Count + _model.Rows(ShortcutGroup.Browser).Count;

    // ---- games ----

    private void ShowAddGameMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        AddItem(menu, L.T("Vorschläge (zuletzt erkannte Spiele)", "Suggestions (recently detected games)"),
            () => _ = ChooseGamesAsync(L.T("Vorschläge", "Suggestions"), _model.Suggestions));
        AddItem(menu, L.T("Steam-Bibliothek", "Steam library"),
            () => _ = ChooseGamesAsync(L.T("Steam-Bibliothek", "Steam library"), _model.SteamGames));
        AddItem(menu, "Epic Games",
            () => _ = ChooseGamesAsync("Epic Games", _model.EpicGames));
        menu.Items.Add(new Separator());
        AddItem(menu, L.T("Verknüpfung oder Exe wählen…", "Pick a shortcut or exe…"), () => _ = PickFileAsync());
        menu.IsOpen = true;
    }

    private static void AddItem(ContextMenu menu, string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();
        menu.Items.Add(item);
    }

    private async Task ChooseGamesAsync(string title, Func<IReadOnlyList<GameChoice>> load)
    {
        View.ShowStatus(L.T("Lade …", "Loading …"));
        IReadOnlyList<GameChoice> choices;
        try { choices = await Task.Run(load); }
        catch (Exception ex)
        {
            Logger.Warn($"Listing '{title}' failed: {ex.Message}");
            View.ShowStatus(L.T("Die Liste konnte nicht gelesen werden.", "The list could not be read."));
            return;
        }

        if (choices.Count == 0)
        {
            View.ShowStatus(L.T($"{title}: nichts gefunden.", $"{title}: nothing found."));
            return;
        }
        View.ShowStatus("");
        var dialog = new GameChoiceDialog(title, choices, this);
        dialog.ShowDialog();
        var picked = dialog.Selected.ToList();
        if (picked.Count > 0) await AddGamesAsync(picked);
        View.Rebuild();
        if (picked.Count > 0)
            View.ShowStatus(L.T($"{picked.Count} Spiel(e) hinzugefügt.", $"{picked.Count} game(s) added."));
    }

    /// <summary>
    /// Adds the picked games off the UI thread: every add reads the game's icon from the shell (a new STA thread, a cold disk, a shell
    /// extension) - a long pick would freeze the board, the bar and every pinned overlay, which all share this dispatcher.
    /// The service is safe to call from any thread, and the editor and the bar refresh themselves from its ItemsChanged.
    /// </summary>
    private async Task AddGamesAsync(IReadOnlyList<GameChoice> picked)
    {
        View.ShowStatus(L.T("Füge hinzu …", "Adding …"));
        try { await Task.Run(() => { foreach (var choice in picked) _model.AddGame(choice); }); }
        catch (Exception ex) { Logger.Warn("Adding games failed: " + ex.Message); }
    }

    private async Task PickFileAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("Verknüpfung oder Programm wählen", "Pick a shortcut or program"),
            Filter = L.T("Programme und Verknüpfungen (*.lnk, *.url, *.exe)|*.lnk;*.url;*.exe|Alle Dateien (*.*)|*.*",
                         "Programs and shortcuts (*.lnk, *.url, *.exe)|*.lnk;*.url;*.exe|All files (*.*)|*.*"),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        AddFileResult result;
        try { result = await Task.Run(() => _model.AddGameFromFile(dialog.FileName)); }
        catch (Exception ex)
        {
            Logger.Warn("Adding a picked file failed: " + ex.Message);
            View.ShowStatus(L.T("Die Datei konnte nicht hinzugefügt werden.", "The file could not be added."));
            return;
        }
        View.Rebuild();
        View.ShowStatus(result switch
        {
            AddFileResult.Added => L.T("Hinzugefügt.", "Added."),
            AddFileResult.Uninstaller => L.T("Das ist eine Deinstallations-Verknüpfung - sie wird nicht hinzugefügt.", "That is an uninstall shortcut - it is not added."),
            AddFileResult.Unsupported => L.T("Nur Verknüpfungen (.lnk, .url) und Programme (.exe) gehen.", "Only shortcuts (.lnk, .url) and programs (.exe) work."),
            _ => L.T("Die Datei wurde nicht gefunden.", "The file was not found."),
        });
    }

    // ---- browsers ----

    private void ShowAddBrowserMenu(FrameworkElement anchor)
    {
        var browsers = _model.AddableBrowsers();
        if (browsers.Count == 0)
        {
            View.ShowStatus(L.T("Alle bekannten Browser sind schon auf der Leiste.", "All known browsers are on the bar already."));
            return;
        }
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var browser in browsers)
        {
            var info = browser;
            AddItem(menu, info.Installed ? info.Name : $"{info.Name}  ({L.T("nicht installiert", "not installed")})", () => _ = PickBrowserAsync(info));
        }
        menu.IsOpen = true;
    }

    private async Task PickBrowserAsync(BrowserInfo browser)
    {
        // Off the UI thread: adding a browser reads its icon from the shell (see AddGamesAsync).
        BrowserPick pick;
        try { pick = await Task.Run(() => _model.PickBrowser(browser)); }
        catch (Exception ex)
        {
            Logger.Warn("Adding a browser failed: " + ex.Message);
            View.ShowStatus(L.T("Der Browser konnte nicht hinzugefügt werden.", "The browser could not be added."));
            return;
        }
        if (pick.Added)
        {
            View.Rebuild();
            return;
        }

        var dialog = new BrowserMissingDialog(pick, this);
        dialog.ShowDialog();
        switch (dialog.Outcome)
        {
            case BrowserMissingOutcome.OpenDownload:
                OpenDownloadPage(dialog.DownloadUrl);
                View.ShowStatus(L.T($"Nach der Installation von {pick.Missing!.Name} \"Neu suchen\" drücken.", $"After installing {pick.Missing!.Name}, press \"Rescan\"."));
                break;
            case BrowserMissingOutcome.UseOther when dialog.Other != null:
                var other = dialog.Other;
                try { await Task.Run(() => _model.AddBrowser(other)); }
                catch (Exception ex) { Logger.Warn("Adding the other browser failed: " + ex.Message); }
                View.Rebuild();
                break;
        }
    }

    /// <summary>The download page in the Windows default browser - never anything but a web address.</summary>
    private static void OpenDownloadPage(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return;
        try { using var _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Warn("Opening the download page failed: " + ex.Message); }
    }
}

/// <summary>Pick games from a list (suggestions, a library): several at once; those already on the bar are shown but cannot be ticked.</summary>
internal sealed class GameChoiceDialog : Window
{
    private readonly ListBox _list = new() { SelectionMode = System.Windows.Controls.SelectionMode.Extended, MinHeight = 240, MaxHeight = 460, MinWidth = 440 };
    private readonly IReadOnlyList<GameChoice> _choices;

    /// <summary>What the user ticked and confirmed (empty after cancel).</summary>
    public IReadOnlyList<GameChoice> Selected { get; private set; } = Array.Empty<GameChoice>();

    internal ListBox ChoiceList => _list;

    public GameChoiceDialog(string title, IReadOnlyList<GameChoice> choices, Window owner)
    {
        _choices = choices;
        if (owner.IsVisible) Owner = owner;   // a window that was never shown cannot own one
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");

        foreach (var c in choices)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2) };
            var icon = BarIcons.Tile(c.Name, null, 24);   // a letter tile: the shell reads a game's real icon only once it is added (dozens of games would stall the list)
            icon.Margin = new Thickness(0, 0, 10, 0);
            row.Children.Add(icon);
            row.Children.Add(new TextBlock
            {
                Text = c.AlreadyAdded ? $"{c.Name}  ({L.T("schon auf der Leiste", "already on the bar")})" : c.Name,
                VerticalAlignment = VerticalAlignment.Center,
            });
            _list.Items.Add(new ListBoxItem { Content = row, IsEnabled = !c.AlreadyAdded, Opacity = c.AlreadyAdded ? 0.5 : 1.0, Tag = c });
        }

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            Text = L.T("Mehrere mit Strg oder Umschalt auswählen.", "Select several with Ctrl or Shift."),
            Style = (Style)Application.Current.FindResource("MutedStyle"), Margin = new Thickness(0, 0, 0, 8),
        });
        root.Children.Add(_list);

        var ok = new Button { Content = L.T("Hinzufügen", "Add"), MinWidth = 100, Style = (Style)Application.Current.FindResource("AccentButton") };
        ok.Click += (_, _) => Confirm();
        var cancel = new Button { Content = L.T("Abbrechen", "Cancel"), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => Close();
        root.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0),
            Children = { ok, cancel },
        });
        Content = root;
    }

    private void Confirm()
    {
        Selected = _list.SelectedItems.OfType<ListBoxItem>().Select(i => (GameChoice)i.Tag).Where(c => !c.AlreadyAdded).ToList();
        Close();
    }
}
