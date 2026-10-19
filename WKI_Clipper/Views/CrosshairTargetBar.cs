using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using WKI_Clipper.Models;
using WKI_Clipper.Services;

namespace WKI_Clipper.Views;

/// <summary>
/// The "Gilt für" bar at the top of the crosshair widget: which look is being edited - the standard ("Alle
/// Spiele") or one game's profile - plus a plain-language line about what applies when, and "Wie Standard"
/// for a profile. Selecting only changes the editor's target; nothing is written until a value changes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CrosshairTargetBar : UserControl
{
    private readonly AppSettings _settings;
    private readonly CrosshairProfileEditor _editor;
    private readonly Func<GameInfo?> _currentGame;
    private readonly System.Windows.Controls.ComboBox _box;
    private readonly TextBlock _hint;
    private readonly Button _reset;
    private IReadOnlyList<CrosshairTarget> _entries = Array.Empty<CrosshairTarget>();
    private bool _building;

    /// <summary>The user picked another target (the sliders have to show its values).</summary>
    public event Action? TargetChanged;

    /// <summary>"Wie Standard" was pressed and the profile's crosshair values are gone.</summary>
    public event Action? ProfileReset;

    public CrosshairTargetBar(AppSettings settings, CrosshairProfileEditor editor, Func<GameInfo?> currentGame)
    {
        _settings = settings;
        _editor = editor;
        _currentGame = currentGame;

        var label = new TextBlock
        {
            Text = L.T("Gilt für", "Applies to"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush")
        };
        _box = new System.Windows.Controls.ComboBox { MinWidth = 180 };
        _box.SelectionChanged += OnSelectionChanged;

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(label, Dock.Left);
        row.Children.Add(label);
        row.Children.Add(_box);

        _hint = new TextBlock
        {
            Style = (Style)FindResource("MutedStyle"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };

        _reset = new Button
        {
            Content = L.T("Wie Standard", "Same as default"),
            ToolTip = L.T("Löscht alle eigenen Crosshair-Werte dieses Spiels — es gelten wieder die Werte von „Alle Spiele“. Die F9-Länge bleibt.",
                          "Removes all of this game's own crosshair values - the \"All games\" values apply again. The F9 length stays."),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed
        };
        _reset.Click += (_, _) => ResetSelected();

        var stack = new StackPanel();
        stack.Children.Add(row);
        stack.Children.Add(_hint);
        stack.Children.Add(_reset);
        Content = stack;

        Refresh();
    }

    /// <summary>The entries currently offered (tests and callers look at what the list holds).</summary>
    public IReadOnlyList<CrosshairTarget> Entries => _entries;

    /// <summary>The text of the line under the selector.</summary>
    public string HintText => _hint.Text;

    public bool ResetVisible => _reset.Visibility == Visibility.Visible;
    public bool ResetEnabled => _reset.IsEnabled;

    /// <summary>"Wie Standard": the selected game loses its crosshair values. Nothing for all games.</summary>
    public void ResetSelected()
    {
        if (!_editor.IsProfile) return;
        _editor.ResetToStandard();
        Refresh();
        ProfileReset?.Invoke();
    }

    /// <summary>Selects an entry by its key as if the user had picked it. False when the list does not offer it.</summary>
    public bool Select(string? key)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (!string.Equals(_entries[i].Key, key, StringComparison.Ordinal)) continue;
            _box.SelectedIndex = i;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Rebuilds the list (a game came to the front, a profile appeared or went) and the hint. The selection
    /// stays on the same game - without firing <see cref="TargetChanged"/> - and falls back to all games when it is gone.
    /// </summary>
    public void Refresh()
    {
        var game = _currentGame();
        var selected = _editor.TargetKey;
        _entries = CrosshairTargetList.Build(_settings, game?.ExeName, game?.DisplayName, selected, _editor.TargetName);

        int index = 0;
        _building = true;
        try
        {
            _box.Items.Clear();
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                _box.Items.Add(new ComboBoxItem { Content = Label(e) });
                if (string.Equals(e.Key, selected, StringComparison.Ordinal)) index = i;
            }
            _box.SelectedIndex = index;
        }
        finally { _building = false; }

        if (!string.Equals(_entries[index].Key, selected, StringComparison.Ordinal))
            _editor.SetTarget(_entries[index].Key, _entries[index].DisplayName);   // cannot happen with the selected key kept in the list; defensive

        UpdateHint(game);
    }

    private static string Label(CrosshairTarget e)
    {
        if (e.Key is null) return L.T("Alle Spiele (Standard)", "All games (default)");
        return e.IsCurrent ? L.T($"{e.DisplayName} (läuft gerade)", $"{e.DisplayName} (running now)") : e.DisplayName;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building || _box.SelectedIndex < 0 || _box.SelectedIndex >= _entries.Count) return;
        var entry = _entries[_box.SelectedIndex];
        if (string.Equals(entry.Key, _editor.TargetKey, StringComparison.Ordinal)) return;
        _editor.SetTarget(entry.Key, entry.DisplayName);
        UpdateHint(_currentGame());
        TargetChanged?.Invoke();
    }

    private void UpdateHint(GameInfo? game)
    {
        string text;
        if (!_editor.IsProfile)
        {
            text = L.T("Diese Werte gelten, wenn kein Spiel erkannt wird oder das Spiel kein eigenes Profil hat.",
                       "These values apply when no game is detected or the game has no profile of its own.");
        }
        else
        {
            string name = _editor.TargetName;
            text = _editor.HasOverrides
                ? L.T($"Eigene Werte für {name}: Sie gelten, solange {name} im Vordergrund ist. Alles, was hier nicht geändert wurde, kommt aus „Alle Spiele“.",
                      $"Own values for {name}: they apply while {name} is in front. Anything not changed here comes from \"All games\".")
                : L.T($"{name} hat noch keine eigenen Werte — jede Änderung unten (außer Raster) legt ein Profil an. Bis dahin gelten die Werte von „Alle Spiele“.",
                      $"{name} has no values of its own yet - any change below (except the grid) creates a profile. Until then the \"All games\" values apply.");
        }

        string now = game is null
            ? L.T("Gerade erkannt: kein Spiel — es gelten die Standardwerte.", "Detected right now: no game - the default values apply.")
            : EffectiveSettings.ProfileFor(_settings, game.ExeName)?.Crosshair is { } look && GameProfiles.HasOverrides(look)
                ? L.T($"Gerade erkannt: {game.DisplayName} — Profil aktiv.", $"Detected right now: {game.DisplayName} - its profile is active.")
                : L.T($"Gerade erkannt: {game.DisplayName} — kein eigenes Profil, Standardwerte.", $"Detected right now: {game.DisplayName} - no profile of its own, default values.");

        _hint.Text = text + "\n" + now;
        _reset.Visibility = _editor.IsProfile ? Visibility.Visible : Visibility.Collapsed;
        _reset.IsEnabled = _editor.HasOverrides;
    }
}
