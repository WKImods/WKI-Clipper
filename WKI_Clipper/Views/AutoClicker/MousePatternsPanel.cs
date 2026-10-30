using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.Services.AutoClicker;
using Orientation = System.Windows.Controls.Orientation;

namespace WKI_Clipper.Views.AutoClicker;

/// <summary>
/// "Mausbewegung" (mouse-patterns design 4.7): what patterns do and where they stand (<see cref="MousePatternUiRules.StatusLine"/>), the
/// record hotkey with the "Neu aufnehmen" target line, and one two-line card per pattern (the widget is narrow) with its warnings
/// (<see cref="MousePatternConflicts"/>) under it. The cards are rebuilt only when a shown value of a pattern changes; an edit in a card's
/// text box keeps them (and the focus of the next box), like the click points. Status, target line and warnings follow every refresh.
/// The drawing comes from the pattern's samples file, read once per <see cref="MousePattern.Revision"/> - only the thinned points are kept.
/// </summary>
public sealed class MousePatternsPanel : ClickerPanel
{
    private const double PreviewWidth = 64, PreviewHeight = 40, PreviewPadding = 5, StartDot = 5;

    /// <summary>The controls of one pattern's card that a refresh or a test reads.</summary>
    internal sealed record PatternCard(Border Root, CheckBox Active, TextBox NameBox, Polyline Preview, TextBlock Duration, CheckBox OnlyIn,
        HotkeyCaptureBox Hotkey, TextBox Speed, Button Rerecord, StackPanel Warnings);

    /// <summary>What a pattern's samples gave for one revision: whether they load, and the drawing's points.</summary>
    private sealed record PreviewEntry(int Revision, bool HasData, IReadOnlyList<DipPoint> Points);

    private readonly Dictionary<string, PreviewEntry> _previews = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PatternCard> _cards = new(StringComparer.OrdinalIgnoreCase);
    private IMousePatternStore? _store;
    private string? _shown;
    private bool _keepRows;

    internal TextBlock StatusText { get; }
    internal HotkeyCaptureBox RecordHotkey { get; }
    internal FrameworkElement TargetRow { get; }
    internal TextBlock TargetText { get; }
    internal StackPanel Cards { get; } = new();
    internal TextBlock EmptyText { get; }
    internal IReadOnlyDictionary<string, PatternCard> CardsById => _cards;

    /// <summary>Non-info warnings of the last refresh, for the status row's "n Muster-Hinweise".</summary>
    internal int WarningTotal { get; private set; }

    /// <summary>Where the drawings are read from: the clicker's store, unless a test sets one.</summary>
    internal IMousePatternStore? Store
    {
        get => _store ?? Host?.Clicker.PatternStore;
        set => _store = value;
    }

    public MousePatternsPanel()
    {
        var top = Section(L.T("Mausbewegung", "Mouse patterns"), L.T(
            "Nimmt eine Mausbewegung auf und spielt sie per Hotkey wieder ab - immer ab der Stelle, an der die Maus gerade ist, auch in Spielen ohne Mauszeiger. Muster bewegen nur, sie klicken nicht. Sie laufen unabhängig vom Clicker, auch gleichzeitig mit ihm.",
            "Records a mouse movement and replays it on a hotkey - always from where the mouse is, also in games without a pointer. Patterns only move, they never click. They run independently of the clicker, also together with it."));
        StatusText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        top.Children.Add(StatusText);

        var record = Section(L.T("Aufnahme", "Recording"), L.T(
            "Im Spiel oder Programm den Aufnahme-Hotkey drücken, die Bewegung machen, nochmal drücken. Aufgenommen wird nur echte Mausbewegung, keine Klicks und keine Tasten; die Ruhe am Anfang und am Ende fällt weg, nach 60 s endet die Aufnahme von selbst. Jede Aufnahme wird ein neues Muster - \"Neu aufnehmen\" ersetzt stattdessen die Bewegung eines vorhandenen.",
            "In the game or program press the record hotkey, make the movement, press it again. Only real mouse movement is recorded, no clicks and no keys; the stillness at the start and the end is cut off, after 60 s the recording ends by itself. Every recording becomes a new pattern - \"Record again\" replaces the movement of an existing one instead."));
        RecordHotkey = new HotkeyCaptureBox(required: true, defaultHotkey: MousePatternRules.DefaultRecordHotkey())
        {
            ToolTip = L.T("Klicken, dann Tasten, Maustasten oder das Mausrad über dem Feld drücken. Erster Druck startet die Aufnahme, der zweite beendet sie.",
                          "Click, then press keys, mouse buttons or the wheel over the field. The first press starts the recording, the second ends it."),
        };
        RecordHotkey.HotkeyChanged += h => Commit(c => c.PatternRecordHotkey = h ?? MousePatternRules.DefaultRecordHotkey());
        record.Children.Add(Row(L.T("Aufnahme-Hotkey", "Record hotkey"), RecordHotkey, RecordHotkey.CancelButton, RecordHotkey.ClearButton));

        TargetText = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        TargetText.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        var cancelTarget = Btn(L.T("Abbrechen", "Cancel"), () => SetTarget(null),
            tooltip: L.T("Die nächste Aufnahme wird wieder ein neues Muster.", "The next recording becomes a new pattern again."));
        var targetRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        targetRow.Children.Add(TargetText);
        targetRow.Children.Add(cancelTarget);
        TargetRow = targetRow;
        record.Children.Add(targetRow);

        var list = Section(L.T("Muster", "Patterns"), L.T(
            "Wiedergabe: \"Automatisch\" wählt bei jedem Start selbst. \"Zeiger\" führt den Mauszeiger pixelgenau ab dem Startpunkt - eigene Mausbewegung wird währenddessen überschrieben. \"Spiel (roh)\" sendet die Bewegung wie eine echte Maus - eigene Bewegung kommt dazu. Ist in Windows \"Zeigerbeschleunigung verbessern\" an, kann ein Spiel ohne Mauszeiger, das keine Rohdaten liest, die Bewegung größer oder kleiner sehen (aber jedes Mal gleich).",
            "Replay: \"Automatic\" decides at every start. \"Pointer\" moves the pointer pixel-exact from the start point - your own mouse movement is overridden meanwhile. \"Game (raw)\" sends the movement like a real mouse - your own movement adds on top. With \"Enhance pointer precision\" on in Windows, a game without a pointer that does not read raw input may see the movement larger or smaller (but the same every time)."));
        Cards.Margin = new Thickness(0, 8, 0, 0);
        list.Children.Add(Cards);
        EmptyText = Muted("", new Thickness(0, 4, 0, 4));
        list.Children.Add(EmptyText);

        Loaded += (_, _) => Refresh();
    }

    protected override void OnRefresh(ClickerSettings c)
    {
        if (Host is not { } host) return;
        Render(host.Settings.Current, host.Clicker.State, host.Clicker.RecordTarget);
    }

    /// <summary>Only the status line, for the helper's state messages (several a second while something runs).</summary>
    internal void ShowState(ClickerState state)
    {
        if (Clicker is { } c) StatusText.Text = MousePatternUiRules.StatusLine(state, c);
    }

    /// <summary>Fills the panel from <paramref name="settings"/> (the refresh, and tests without an app).</summary>
    internal void Render(AppSettings settings, ClickerState state, string? recordTarget)
    {
        var c = settings.AutoClicker.Clicker;
        var patterns = (c.MousePatterns ?? new List<MousePattern>()).Where(p => p is { Id: not null }).ToList();
        bool sided = HotkeyRecorder.RecordsSided(c);

        StatusText.Text = MousePatternUiRules.StatusLine(state, c);
        RecordHotkey.Sided = sided;
        RecordHotkey.SetHotkey(c.PatternRecordHotkey);

        var target = recordTarget is null ? null : patterns.FirstOrDefault(p => SameId(p.Id, recordTarget));
        TargetText.Text = target is null ? "" : MousePatternUiRules.TargetLine(target.Name, c);
        TargetRow.Visibility = target is null ? Visibility.Collapsed : Visibility.Visible;

        EmptyText.Text = MousePatternUiRules.EmptyText(c);
        EmptyText.Visibility = patterns.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var previews = new Dictionary<string, PreviewEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in patterns) previews[p.Id] = PreviewOf(p);
        foreach (var gone in _previews.Keys.Where(id => !previews.ContainsKey(id)).ToList()) _previews.Remove(gone);

        var key = RowsKey(patterns, sided);
        if (_keepRows) _shown = key;   // an edit inside a card: keep the cards (and the focus of the next box)
        // A card's hotkey box that is capturing would lose its capture: the cards follow at the next refresh.
        if (key != _shown && !_cards.Values.Any(card => card.Hotkey.IsCapturing))
        {
            _shown = key;
            Cards.Children.Clear();
            _cards.Clear();
            foreach (var p in patterns)
            {
                var card = BuildCard(p, previews[p.Id], sided);
                _cards[p.Id] = card;
                Cards.Children.Add(card.Root);
            }
        }

        var warnings = MousePatternConflicts.Evaluate(settings, id => previews.TryGetValue(id, out var e) && e.HasData);
        WarningTotal = MousePatternUiRules.WarningCount(warnings);
        foreach (var (id, card) in _cards)
        {
            card.Warnings.Children.Clear();
            foreach (var w in warnings.Where(w => SameId(w.PatternId, id)))
            {
                var line = new TextBlock { Text = w.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
                line.SetResourceReference(TextBlock.ForegroundProperty, w.Info ? "MutedBrush" : "DangerBrush");
                card.Warnings.Children.Add(line);
            }
            card.Warnings.Visibility = card.Warnings.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            // The pattern "Neu aufnehmen" marked looks pressed.
            if (target is not null && SameId(target.Id, id)) card.Rerecord.Style = (Style)Application.Current.FindResource("AccentButton");
            else card.Rerecord.ClearValue(StyleProperty);
        }
    }

    private static bool SameId(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string RowsKey(IEnumerable<MousePattern> patterns, bool sided)
        => sided + "#" + string.Join("|", patterns.Select(p => string.Join(";",
            p.Id, p.Name, p.Hotkey is { } h ? $"{(int)h.Modifiers}:{string.Join(",", h.Keys ?? new List<int>())}" : "-",
            p.Active, p.Mode, p.Repeat, p.Speed.ToString("R", CultureInfo.InvariantCulture), p.Replay, p.Program, p.OnlyInProgram,
            p.DurationMs.ToString("R", CultureInfo.InvariantCulture), p.Revision)));

    /// <summary>The drawing of a pattern, read from its samples once per revision (a missing store is not remembered as missing samples).</summary>
    private PreviewEntry PreviewOf(MousePattern p)
    {
        if (_previews.TryGetValue(p.Id, out var known) && known.Revision == p.Revision) return known;
        if (Store is not { } store) return new PreviewEntry(p.Revision, false, Array.Empty<DipPoint>());

        PreviewEntry entry;
        if (store.TryLoad(p.Id, out var data))
        {
            // The pointer track is what the user saw; a pen tablet leaves only the raw one.
            var track = data.Pointer.Count > 0 ? data.Pointer : data.Raw;
            entry = new PreviewEntry(p.Revision, true, PatternPreview.Fit(track, PreviewWidth, PreviewHeight, PreviewPadding));
        }
        else entry = new PreviewEntry(p.Revision, false, Array.Empty<DipPoint>());
        _previews[p.Id] = entry;
        return entry;
    }

    // ---------------- a card ----------------

    private PatternCard BuildCard(MousePattern p, PreviewEntry preview, bool sided)
    {
        var id = p.Id;

        // ---- line 1: [aktiv] name, drawing, length, [Nur in <Programm>] ----
        var active = new CheckBox
        {
            Content = L.T("aktiv", "active"), IsChecked = p.Active,
            ToolTip = L.T("Nur aktive Muster hören auf ihren Hotkey.", "Only active patterns listen to their hotkey."),
        };
        active.Checked += (_, _) => Edit(id, x => x.Active = true);
        active.Unchecked += (_, _) => Edit(id, x => x.Active = false);

        var name = new TextBox { Width = 150, MaxLength = ClickerSettingsRules.MaxNameLength, Text = p.Name, VerticalContentAlignment = VerticalAlignment.Center };
        void ApplyName()
        {
            if (Find(id) is not { } current) return;
            if (name.Text != current.Name) EditKeepingRows(id, x => x.Name = name.Text);
            if (Find(id) is { } after) name.Text = after.Name;   // the cleaned name (an empty one gets "Muster n")
        }
        name.LostKeyboardFocus += (_, _) => ApplyName();
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyName(); e.Handled = true; } };

        var (drawing, line) = Drawing(preview);

        var duration = new TextBlock { Text = MousePatternUiRules.Duration(p.DurationMs), Style = (Style)Application.Current.FindResource("MutedStyle") };

        var onlyIn = new CheckBox
        {
            Content = p.Program is { } program ? MousePatternUiRules.OnlyIn(program) : "",
            IsChecked = p.OnlyInProgram,
            Visibility = p.Program is null ? Visibility.Collapsed : Visibility.Visible,
            ToolTip = L.T("Spielt nur, solange das Programm der Aufnahme im Vordergrund ist.", "Plays only while the program of the recording is in front."),
        };
        onlyIn.Checked += (_, _) => Edit(id, x => x.OnlyInProgram = true);
        onlyIn.Unchecked += (_, _) => Edit(id, x => x.OnlyInProgram = false);

        // ---- line 2: hotkey, Halten/Umschalten, [Wiederholen], tempo, replay, "Neu aufnehmen", "Löschen" ----
        var hotkey = new HotkeyCaptureBox(required: false)
        {
            Sided = sided,
            ToolTip = L.T("Klicken, dann Tasten, Maustasten oder das Mausrad über dem Feld drücken. Ohne Hotkey spielt das Muster nie.",
                          "Click, then press keys, mouse buttons or the wheel over the field. Without a hotkey the pattern never plays."),
        };
        hotkey.SetHotkey(p.Hotkey);
        hotkey.HotkeyChanged += h => Edit(id, x => x.Hotkey = h);

        var mode = Combo(new[] { ActivationMode.Hold, ActivationMode.Toggle }, p.Mode, MousePatternUiRules.ModeName, v => Edit(id, x => x.Mode = v),
            L.T("Halten: spielt, solange der Hotkey gedrückt ist. Umschalten: ein Druck startet, der nächste stoppt.",
                "Hold: plays while the hotkey is held. Toggle: one press starts, the next one stops."));

        var repeat = new CheckBox
        {
            Content = L.T("Wiederholen", "Repeat"), IsChecked = p.Repeat,
            ToolTip = L.T("An: am Ende geht es nahtlos mit dem nächsten Durchgang weiter. Aus: ein Durchgang pro Druck.",
                          "On: at the end it continues seamlessly with the next pass. Off: one pass per press."),
        };
        repeat.Checked += (_, _) => Edit(id, x => x.Repeat = true);
        repeat.Unchecked += (_, _) => Edit(id, x => x.Repeat = false);

        var speed = new TextBox
        {
            Width = 50, Text = ClickerNumber.Format(p.Speed), VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = L.T($"{ClickerNumber.Format(MousePatternRules.MinSpeed)} bis {ClickerNumber.Format(MousePatternRules.MaxSpeed)} - die Form bleibt gleich, nur das Tempo ändert sich.",
                          $"{ClickerNumber.Format(MousePatternRules.MinSpeed)} to {ClickerNumber.Format(MousePatternRules.MaxSpeed)} - the shape stays the same, only the tempo changes."),
        };
        void ApplySpeed()
        {
            if (Find(id) is not { } current) return;
            if (ClickerNumber.TryParse(speed.Text, out var v) && v != current.Speed) EditKeepingRows(id, x => x.Speed = v);
            if (Find(id) is { } after) speed.Text = ClickerNumber.Format(after.Speed);   // the clamped value, also after a bad entry
        }
        speed.LostKeyboardFocus += (_, _) => ApplySpeed();
        speed.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplySpeed(); e.Handled = true; } };
        var tempo = new StackPanel { Orientation = Orientation.Horizontal };
        tempo.Children.Add(Spaced(Label(L.T("Tempo", "Tempo")), 4));
        tempo.Children.Add(Spaced(speed, 4));
        tempo.Children.Add(Label("x"));

        var replay = Combo(new[] { PatternReplay.Auto, PatternReplay.Pointer, PatternReplay.Raw }, p.Replay, MousePatternUiRules.ReplayName,
            v => Edit(id, x => x.Replay = v),
            L.T("Automatisch: Spiel (roh) ohne Windows-Zeigerbeschleunigung, sonst Zeiger, wenn ein Mauszeiger zu sehen ist. Zeiger: pixelgenau ab dem Startpunkt. Spiel (roh): wie eine echte Maus.",
                "Automatic: Game (raw) without Windows pointer acceleration, else Pointer while a pointer is visible. Pointer: pixel-exact from the start point. Game (raw): like a real mouse."));
        var playback = new StackPanel { Orientation = Orientation.Horizontal };
        playback.Children.Add(Spaced(Label(L.T("Wiedergabe", "Replay")), 4));
        playback.Children.Add(replay);

        var rerecord = new Button
        {
            Content = L.T("Neu aufnehmen", "Record again"),
            ToolTip = L.T("Die nächste Aufnahme ersetzt die Bewegung dieses Musters; Name, Hotkey und Einstellungen bleiben.",
                          "The next recording replaces this pattern's movement; name, hotkey and settings stay."),
        };
        rerecord.Click += (_, _) => SetTarget(id);
        var delete = new Button { Content = L.T("Löschen", "Delete") };
        delete.Click += (_, _) => Delete(id);

        var first = CardLine(new Thickness(0), active, name, drawing, duration, onlyIn);
        var second = CardLine(new Thickness(0, 6, 0, 0),hotkey, hotkey.CancelButton, hotkey.ClearButton, mode, repeat, tempo, playback, rerecord, delete);
        var warnings = new StackPanel { Visibility = Visibility.Collapsed };

        var stack = new StackPanel();
        stack.Children.Add(first);
        stack.Children.Add(second);
        stack.Children.Add(warnings);
        var root = new Border
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(1),
            Child = stack, Tag = id,
        };
        root.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        root.SetResourceReference(Border.BackgroundProperty, "SoftFillBrush");

        return new PatternCard(root, active, name, line, duration, onlyIn, hotkey, speed, rerecord, warnings);
    }

    /// <summary>The small path drawing: the motion scaled into a box, its start marked with a dot.</summary>
    private static (Border Box, Polyline Line) Drawing(PreviewEntry preview)
    {
        var canvas = new Canvas { Width = PreviewWidth, Height = PreviewHeight, ClipToBounds = true };
        var line = new Polyline
        {
            StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round,
            Points = new PointCollection(preview.Points.Select(point => new Point(point.X, point.Y))),
        };
        line.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        canvas.Children.Add(line);
        if (preview.Points.Count > 0)
        {
            var dot = new Ellipse { Width = StartDot, Height = StartDot };
            dot.SetResourceReference(Shape.FillProperty, "AccentBrush");
            Canvas.SetLeft(dot, preview.Points[0].X - StartDot / 2);
            Canvas.SetTop(dot, preview.Points[0].Y - StartDot / 2);
            canvas.Children.Add(dot);
        }

        var box = new Border
        {
            CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Child = canvas,
            ToolTip = preview.HasData ? L.T("Die aufgenommene Bewegung (Punkt = Start)", "The recorded movement (dot = start)") : L.T("Keine Aufnahme", "No recording"),
        };
        box.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        box.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        return (box, line);
    }

    private static WrapPanel CardLine(Thickness margin, params FrameworkElement[] children)
    {
        var panel = new WrapPanel { Margin = margin };
        foreach (var child in children)
        {
            child.VerticalAlignment = VerticalAlignment.Center;
            if (child.Margin == default) child.Margin = new Thickness(0, 2, 8, 2);
            panel.Children.Add(child);
        }
        return panel;
    }

    private static T Spaced<T>(T element, double right) where T : FrameworkElement
    {
        element.Margin = new Thickness(0, 0, right, 0);
        element.VerticalAlignment = VerticalAlignment.Center;
        return element;
    }

    /// <summary>A drop-down of a card; the selection is set before the handler, so building it commits nothing.</summary>
    private static ComboBox Combo<T>(IEnumerable<T> values, T current, Func<T, string> text, Action<T> set, string tooltip) where T : struct, Enum
    {
        var combo = new ComboBox { MinWidth = 110, ToolTip = tooltip };
        foreach (var value in values)
            combo.Items.Add(new ComboBoxItem { Content = text(value), Tag = value, IsSelected = EqualityComparer<T>.Default.Equals(value, current) });
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: T value }) set(value);
        };
        return combo;
    }

    // ---------------- edits ----------------

    private static MousePattern? Find(string id)
        => Clicker?.MousePatterns?.FirstOrDefault(p => p is not null && SameId(p.Id, id));

    /// <summary>Edits one pattern on a copy of the list (the controller reads the settings on pool threads), normalizes and saves.</summary>
    private void Edit(string id, Action<MousePattern> edit)
        => CommitList(s => s.MousePatterns, (s, l) => s.MousePatterns = l, list =>
        {
            int i = list.FindIndex(p => p is not null && SameId(p.Id, id));
            if (i < 0) return false;
            var copy = Copy(list[i]);
            edit(copy);
            list[i] = copy;
            return true;
        });

    private void EditKeepingRows(string id, Action<MousePattern> edit)
    {
        _keepRows = true;
        try { Edit(id, edit); }
        finally { _keepRows = false; }
    }

    private static MousePattern Copy(MousePattern o) => new()
    {
        Id = o.Id, Name = o.Name, Hotkey = o.Hotkey?.Clone(), Active = o.Active, Mode = o.Mode, Repeat = o.Repeat, Speed = o.Speed,
        Replay = o.Replay, Program = o.Program, OnlyInProgram = o.OnlyInProgram, DurationMs = o.DurationMs, Revision = o.Revision,
    };

    /// <summary>Marks the pattern the next recording replaces (null: a new one). Not a setting, so the panel redraws itself.</summary>
    private void SetTarget(string? id)
    {
        if (Host is not { } host) return;
        host.Clicker.SetRecordTarget(id);
        Refresh();
    }

    private void Delete(string id)
    {
        if (Host is not { } host || Clicker is not { } c || Find(id) is not { } pattern) return;
        var answer = MessageBox.Show(Window.GetWindow(this), L.T($"Muster '{pattern.Name}' löschen?", $"Delete pattern '{pattern.Name}'?"),
            "AutoClicker", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        // A copy first: the controller reads the list on pool threads, and DeletePattern removes in place.
        c.MousePatterns = new List<MousePattern>(c.MousePatterns);
        host.Clicker.DeletePattern(id);
        Refresh();
    }
}
