using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.Services.AutoClicker;

namespace WKI_Clipper.Views.AutoClicker;

/// <summary>
/// "Zonen & Schutz": the failsafes (corners, edges, Alt+Tab), the zones, the game blacklist (it locks the clicker) with
/// "Blacklist ignorieren", and the whitelist.
/// </summary>
public sealed class ZonesSafetyPanel : ClickerPanel
{
    private readonly StackPanel _zoneRows = new();
    private readonly TextBlock _zonesEmpty;
    private readonly Button _pickZones;
    private readonly StackPanel _blacklistRows = new();
    private readonly TextBlock _blacklistEmpty;
    private readonly StackPanel _whitelistRows = new();
    private readonly TextBlock _whitelistEmpty;
    private string? _zonesShown, _blacklistShown, _whitelistShown;
    private bool _subscribed;

    internal Button AddGameBtn { get; }
    internal Button IgnoreBtn { get; }
    internal TextBlock IgnoreText { get; }

    public ZonesSafetyPanel()
    {
        // ---- failsafes ----
        var safety = Section(L.T("Schutz", "Failsafes"),
            L.T("Fährt die Maus in eine Ecke oder an einen Bildschirmrand, stoppt der Clicker sofort (pro Monitor).",
                "Moving the mouse into a corner or to a screen edge stops the clicker at once (per monitor)."));
        var corner = Number(c => c.CornerSizePx, (c, v) => c.CornerSizePx = (int)Math.Round(v), width: 60);
        safety.Children.Add(Row(L.T("Ecken", "Corners"),
            Check(L.T("an", "on"), c => c.CornerStopEnabled, (c, v) => c.CornerStopEnabled = v), corner, Label("px  (1-500)")));
        EnabledWhen(corner, c => c.CornerStopEnabled);
        var edge = Number(c => c.EdgeSizePx, (c, v) => c.EdgeSizePx = (int)Math.Round(v), width: 60);
        safety.Children.Add(Row(L.T("Ränder", "Edges"),
            Check(L.T("an", "on"), c => c.EdgeStopEnabled, (c, v) => c.EdgeStopEnabled = v), edge, Label("px  (1-500)")));
        EnabledWhen(edge, c => c.EdgeStopEnabled);
        safety.Children.Add(Check(L.T("Stoppen, wenn Alt+Tab oder die Aufgabenansicht offen ist", "Stop while Alt+Tab or Task View is open"),
            c => c.TaskSwitcherStopEnabled, (c, v) => c.TaskSwitcherStopEnabled = v));

        // ---- zones ----
        var zones = Section(L.T("Zonen", "Zones"),
            L.T("Stopp-Zone: beendet das Klicken. Pause-Zone: pausiert, solange die Maus darin ist. Start-Zone: startet beim Hineinfahren und stoppt beim Verlassen.",
                "Stop zone: ends clicking. Pause zone: pauses while the mouse is inside. Start zone: starts on entering and stops on leaving."));
        zones.Children.Add(Check(L.T("Zonen verwenden", "Use zones"), c => c.ZonesEnabled, (c, v) => c.ZonesEnabled = v));
        _zoneRows.Margin = new Thickness(0, 8, 0, 0);
        zones.Children.Add(_zoneRows);
        _zonesEmpty = Muted(L.T("Noch keine Zone.", "No zone yet."), new Thickness(0, 0, 0, 4));
        zones.Children.Add(_zonesEmpty);
        _pickZones = Btn(L.T("Zonen zeichnen", "Draw zones"), OpenZonePicker, accent: true,
            tooltip: L.T("Rechteck aufziehen, dann Stopp/Pause/Start wählen. Esc beendet.", "Drag a rectangle, then pick stop/pause/start. Esc finishes."));
        zones.Children.Add(Buttons(_pickZones));

        // ---- blacklist ----
        var blacklist = Section("Blacklist", L.T(
            "Solange eines dieser Spiele läuft oder im Vordergrund ist, ist der AutoClicker gesperrt.",
            "While one of these games runs or is in front, the AutoClicker is locked."));
        blacklist.Children.Add(_blacklistRows);
        _blacklistEmpty = Muted(L.T("Noch kein Spiel auf der Blacklist.", "No game on the blacklist yet."), new Thickness(0, 0, 0, 8));
        blacklist.Children.Add(_blacklistEmpty);
        AddGameBtn = Btn(L.T("Spiel hinzufügen", "Add game"), OnAddGame, accent: true);
        IgnoreBtn = Btn(L.T("Blacklist ignorieren", "Ignore blacklist"), () =>
        {
            if (Host is { } host) host.AutoClickerGuard.IgnoreBlacklist = !host.AutoClickerGuard.IgnoreBlacklist;
        }, tooltip: L.T("Nur bis zum Neustart des Clippers - wird nicht gespeichert.", "Only until the clipper restarts - not saved."));
        blacklist.Children.Add(Buttons(AddGameBtn, IgnoreBtn));
        IgnoreText = new TextBlock
        {
            Text = L.T("Blacklist wird ignoriert (bis zum Neustart des Clippers)", "Blacklist is ignored (until the clipper restarts)"),
            Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Visibility = Visibility.Collapsed,
        };
        IgnoreText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        blacklist.Children.Add(IgnoreText);

        // ---- whitelist ----
        var whitelist = Section("Whitelist", L.T(
            "Wenn an, klickt der AutoClicker nur, solange eines dieser Programme im Vordergrund ist.",
            "When on, the AutoClicker only clicks while one of these programs is in front."));
        whitelist.Children.Add(Check(L.T("Nur in diesen Programmen klicken", "Click only in these programs"),
            c => c.WhitelistEnabled, (c, v) => c.WhitelistEnabled = v));
        _whitelistRows.Margin = new Thickness(0, 8, 0, 0);
        whitelist.Children.Add(_whitelistRows);
        _whitelistEmpty = Muted(L.T("Noch kein Programm auf der Whitelist.", "No program on the whitelist yet."), new Thickness(0, 0, 0, 4));
        whitelist.Children.Add(_whitelistEmpty);
        whitelist.Children.Add(Buttons(Btn(L.T("Programm hinzufügen", "Add program"), OnAddWhitelist, accent: true)));

        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
    }

    private void Subscribe()
    {
        if (_subscribed || Host is not { } host) return;
        host.AutoClickerGuard.IgnoreChanged += OnIgnoreChanged;
        PickerSession.OpenChanged += OnPickerOpenChanged;
        _subscribed = true;
        UpdateIgnoreUi();
        Refresh();
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        if (Host is { } host) host.AutoClickerGuard.IgnoreChanged -= OnIgnoreChanged;
        PickerSession.OpenChanged -= OnPickerOpenChanged;
        _subscribed = false;
    }

    protected override void OnRefresh(ClickerSettings c)
    {
        _pickZones.IsEnabled = !PickerSession.IsOpen && c.Zones.Count < ClickerSettingsRules.MaxZones;
        RenderZones(c);
        RenderBlacklist();
        RenderWhitelist(c);
    }

    private void OnPickerOpenChanged() => Refresh();

    // ---------------- zones ----------------

    private static string ZoneKey(ClickerSettings c)
        => string.Join("|", c.Zones.Select(z => $"{z.Id};{z.Name};{z.X};{z.Y};{z.W};{z.H};{z.Action};{z.Enabled}"));

    private void RenderZones(ClickerSettings c)
    {
        var key = ZoneKey(c);
        if (key == _zonesShown) return;
        _zonesShown = key;
        _zoneRows.Children.Clear();
        _zonesEmpty.Visibility = c.Zones.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var zone in c.Zones)
        {
            var id = zone.Id;
            var action = new ComboBox { MinWidth = 90 };
            foreach (var a in new[] { ZoneAction.Stop, ZoneAction.Pause, ZoneAction.Start })
                action.Items.Add(new ComboBoxItem { Content = ZonePickerWindow.ActionName(a), Tag = a, IsSelected = a == zone.Action });
            action.SelectionChanged += (_, _) =>
            {
                if (action.SelectedItem is ComboBoxItem { Tag: ZoneAction a }) EditZone(id, z => z.Action = a);
            };
            var enabled = new CheckBox { Content = L.T("aktiv", "active"), IsChecked = zone.Enabled, VerticalAlignment = VerticalAlignment.Center };
            enabled.Checked += (_, _) => EditZone(id, z => z.Enabled = true);
            enabled.Unchecked += (_, _) => EditZone(id, z => z.Enabled = false);
            var delete = new Button { Content = L.T("Löschen", "Delete") };
            delete.Click += (_, _) => CommitList(s => s.Zones, (s, l) => s.Zones = l, l => l.RemoveAll(z => z.Id == id) > 0);

            _zoneRows.Children.Add(ListRow(TwoLines(zone.Name, $"{zone.W} × {zone.H} px  @ {zone.X}, {zone.Y}"), action, enabled, delete));
        }
    }

    /// <summary>Edits one zone on a copy of the list (the helper's config push reads it on a pool thread).</summary>
    private void EditZone(string id, Action<ClickerZone> edit)
        => CommitList(s => s.Zones, (s, l) => s.Zones = l, list =>
        {
            int i = list.FindIndex(z => z.Id == id);
            if (i < 0) return false;
            var o = list[i];
            var copy = new ClickerZone { Id = o.Id, Name = o.Name, X = o.X, Y = o.Y, W = o.W, H = o.H, Action = o.Action, Enabled = o.Enabled };
            edit(copy);
            list[i] = copy;
            return true;
        });

    private void OpenZonePicker()
    {
        if (Host is not { } host) return;
        host.CloseBoard?.Invoke();   // the board's topmost windows would cover the screen to draw on
        ZonePicker.Open(host, () => { if (IsLoaded) Refresh(); });
    }

    // ---------------- blacklist ----------------

    private void RenderBlacklist()
    {
        var entries = Host?.Settings.Current.AutoClicker?.Blacklist ?? new List<BlacklistEntry>();
        var key = string.Join("|", entries.Select(e => e.ExeName + ";" + e.DisplayName));
        if (key == _blacklistShown) return;
        _blacklistShown = key;
        _blacklistRows.Children.Clear();
        _blacklistEmpty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var entry in entries)
        {
            var remove = new Button
            {
                Content = L.T("Entfernen", "Remove"),
                ToolTip = L.T($"{entry.DisplayName} von der Blacklist nehmen", $"Take {entry.DisplayName} off the blacklist"),
            };
            var exe = entry.ExeName;
            remove.Click += (_, _) => RemoveBlacklisted(exe);
            _blacklistRows.Children.Add(ListRow(TwoLines(entry.DisplayName, entry.ExeName), remove));
        }
    }

    private void OnAddGame()
    {
        var host = Host;
        if (host is null) return;
        var settings = host.Settings.Current;
        var game = host.CurrentGame;

        var dialog = new BlacklistGameDialog(AutoClickerBlacklist.Candidates(settings, game?.ExeName, game?.DisplayName), Window.GetWindow(this));
        dialog.ShowDialog();

        // A copy, then swapped in: the guard reads the list on pool threads while it is being edited.
        var next = settings.AutoClicker.Blacklist.ToList();
        bool changed = false;
        foreach (var c in dialog.Selected) changed |= AutoClickerBlacklist.Add(next, c.ExeName, c.DisplayName);
        if (dialog.PickedExe is { } picked)
        {
            if (AutoClickerBlacklist.Add(next, picked, null)) changed = true;
            else if (AutoClickerBlacklist.Key(picked) is not null)
                ToastService.Show(ToastKind.Info, "Blacklist",
                    L.T("Dieses Programm steht schon auf der Blacklist.", "That program is already on the blacklist."), durationSeconds: 4);
        }
        if (!changed) return;

        CommitBlacklist(host, next);
    }

    private void RemoveBlacklisted(string exe)
    {
        var host = Host;
        if (host is null) return;
        var next = host.Settings.Current.AutoClicker.Blacklist.ToList();
        if (AutoClickerBlacklist.Remove(next, exe)) CommitBlacklist(host, next);
    }

    private void CommitBlacklist(AppHost host, List<BlacklistEntry> next)
    {
        host.Settings.Current.AutoClicker.Blacklist = next;
        host.Settings.Save();
        RenderBlacklist();
        // An entry that is running right now must act at once (the clicker locks); a removed one lifts it.
        _ = Task.Run(host.AutoClickerGuard.Evaluate);
    }

    private void OnIgnoreChanged()
        => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsLoaded) UpdateIgnoreUi();
        }));

    private void UpdateIgnoreUi()
    {
        bool ignoring = Host?.AutoClickerGuard.IgnoreBlacklist == true;
        // The toggle looks "on" (accent) while the blacklist is ignored.
        if (ignoring) IgnoreBtn.Style = (Style)FindResource("AccentButton");
        else IgnoreBtn.ClearValue(StyleProperty);
        IgnoreText.Visibility = ignoring ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- whitelist ----------------

    private void RenderWhitelist(ClickerSettings c)
    {
        var key = string.Join("|", c.Whitelist.Select(e => e.ExeName + ";" + e.DisplayName));
        if (key == _whitelistShown) return;
        _whitelistShown = key;
        _whitelistRows.Children.Clear();
        _whitelistEmpty.Visibility = c.Whitelist.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var entry in c.Whitelist)
        {
            var exe = entry.ExeName;
            var remove = new Button
            {
                Content = L.T("Entfernen", "Remove"),
                ToolTip = L.T($"{entry.DisplayName} von der Whitelist nehmen", $"Take {entry.DisplayName} off the whitelist"),
            };
            remove.Click += (_, _) => CommitList(s => s.Whitelist, (s, l) => s.Whitelist = l, l => AutoClickerBlacklist.Remove(l, exe));
            _whitelistRows.Children.Add(ListRow(TwoLines(entry.DisplayName, entry.ExeName), remove));
        }
    }

    private void OnAddWhitelist()
    {
        var host = Host;
        if (host is null || Clicker is not { } clicker) return;
        var game = host.CurrentGame;

        var dialog = new BlacklistGameDialog(
            AutoClickerBlacklist.Candidates(host.Settings.Current, game?.ExeName, game?.DisplayName, listed: clicker.Whitelist),
            Window.GetWindow(this), whitelist: true);
        dialog.ShowDialog();

        CommitList(s => s.Whitelist, (s, l) => s.Whitelist = l, list =>
        {
            bool changed = false;
            foreach (var c in dialog.Selected) changed |= AutoClickerBlacklist.Add(list, c.ExeName, c.DisplayName);
            if (dialog.PickedExe is { } picked) changed |= AutoClickerBlacklist.Add(list, picked, null);
            return changed;
        });
    }
}
