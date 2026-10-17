using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.ViewModels;

namespace WKI_Clipper.Views;

public partial class HotkeysView : UserControl
{
    private static (string action, string label)[] Actions =>
    new[]
    {
        (HotkeyActions.SaveReplay,      L.T("Letzte Sekunden speichern", "Save last seconds")),
        (HotkeyActions.Screenshot,      L.T("Screenshot vom aktiven Fenster", "Screenshot of the active window")),
        (HotkeyActions.ToggleRecording, L.T("Recording Start/Stop", "Recording start/stop")),
        (HotkeyActions.ToggleOverlay,   L.T("Overlay öffnen/schließen", "Open/close overlay")),
        (HotkeyActions.ToggleBuffer,    L.T("Replay-Buffer pause/resume", "Replay buffer pause/resume")),
        (HotkeyActions.ToggleCrosshair, L.T("Crosshair ein-/ausblenden", "Show/hide crosshair")),
        (HotkeyActions.SaveGif,         L.T("Sofort-GIF speichern", "Save instant GIF")),
        (HotkeyActions.SpotifyPlayPause, L.T("Spotify: Abspielen/Pause", "Spotify: play/pause")),
        (HotkeyActions.SpotifyNext,      L.T("Spotify: Nächster Titel", "Spotify: next track")),
        (HotkeyActions.SpotifyPrevious,  L.T("Spotify: Vorheriger Titel", "Spotify: previous track")),
        (HotkeyActions.TogglePerfOverlay, L.T("Leistungs-Overlay ein/aus", "Performance overlay on/off")),
    };

    public HotkeysView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachGamepad(App.Host);   // on every load: Unloaded detaches again
        if (RowsContainer.Children.Count > 0) return;
        var host = App.Host;
        if (host is null)
        {
            Logger.Error("HotkeysView loaded but App.Host is null");
            return;
        }

        SubheadingText.Text = L.T("Klicke auf einen Button, drücke die neue Tastenkombi. Esc bricht ab.",
                                  "Click a button, then press the new key combo. Esc cancels.");

        foreach (var (action, label) in Actions)
        {
            RowsContainer.Children.Add(BuildRow(host, action, label));
        }
        BuildControllerSection(host);
        Logger.Info($"HotkeysView built {Actions.Length} rows");
    }

    private FrameworkElement BuildRow(AppHost host, string action, string label)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Label
        var labelBlock = new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("TextBrush")
        };
        Grid.SetColumn(labelBlock, 0);
        grid.Children.Add(labelBlock);

        // Capture button (click to rebind)
        var captureBtn = new Button
        {
            Content = DescribeBinding(host, action),
            Padding = new Thickness(10, 8, 10, 8),
            MinHeight = 36,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Tag = action,
            Background = (Brush)FindResource("PanelBrush"),
            Foreground = (Brush)FindResource("TextBrush"),
            FontSize = 13,
            Cursor = Cursors.Hand
        };
        Grid.SetColumn(captureBtn, 1);
        grid.Children.Add(captureBtn);

        // Clear button
        var clearBtn = new Button
        {
            Content = L.T("Löschen", "Clear"),
            Padding = new Thickness(10, 6, 10, 6),
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)FindResource("PanelBrush"),
            Foreground = (Brush)FindResource("TextBrush"),
            FontSize = 13,
            Cursor = Cursors.Hand
        };
        Grid.SetColumn(clearBtn, 2);
        grid.Children.Add(clearBtn);

        // Wrap in a styled panel-background border
        var rowBorder = new Border
        {
            Background = (Brush)FindResource("PanelBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 3, 0, 3),
            Child = grid
        };

        // Wire events
        captureBtn.Click += (_, _) => StartCapture(captureBtn, host, action);
        clearBtn.Click += (_, _) =>
        {
            host.Settings.Current.Hotkeys[action] = new HotkeyBinding
            {
                Modifiers = HotkeyModifier.None,
                Key = 0
            };
            host.Settings.Save();
            host.Hotkeys.RegisterAll();
            captureBtn.Content = L.T("— (nicht gebunden)", "— (not bound)");
            Logger.Info($"Hotkey cleared: {action}");
        };

        return rowBorder;
    }

    private void StartCapture(Button btn, AppHost host, string action)
    {
        var defaultBg = (Brush)FindResource("PanelBrush");
        var accentBg  = (Brush)FindResource("AccentBrush");
        var prevContent = btn.Content;

        btn.Content = L.T("Drücke Tastenkombi…  (Esc abbrechen)", "Press a key combo…  (Esc to cancel)");
        btn.Background = accentBg;
        btn.Focus();
        Keyboard.Focus(btn);

        KeyEventHandler? keyHandler = null;
        KeyboardFocusChangedEventHandler? lostFocusHandler = null;

        void Cleanup()
        {
            if (keyHandler != null) btn.PreviewKeyDown -= keyHandler;
            if (lostFocusHandler != null) btn.LostKeyboardFocus -= lostFocusHandler;
        }

        keyHandler = (s, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.Escape)
            {
                btn.Content = prevContent;
                btn.Background = defaultBg;
                Cleanup();
                e.Handled = true;
                return;
            }

            if (IsModifierKey(key))
            {
                e.Handled = true;
                return;
            }

            var modifiers = HotkeyModifier.None;
            var km = Keyboard.Modifiers;
            if ((km & ModifierKeys.Control) != 0) modifiers |= HotkeyModifier.Control;
            if ((km & ModifierKeys.Alt) != 0)     modifiers |= HotkeyModifier.Alt;
            if ((km & ModifierKeys.Shift) != 0)   modifiers |= HotkeyModifier.Shift;
            if ((km & ModifierKeys.Windows) != 0) modifiers |= HotkeyModifier.Win;

            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0)
            {
                e.Handled = true;
                return;
            }

            // Symmetric collision guard: also refuses combos taken by stream-deck
            // tiles — otherwise the clipper registers first and silently "steals"
            // the tile's key while the tile still looks bound.
            var candidate = new HotkeyBinding { Modifiers = modifiers, Key = (uint)vk };
            var clash = HotkeyService.FindCollision(host.Settings.Current, candidate, excludeAction: action);
            if (clash != null)
            {
                btn.Content = L.T($"Belegt durch {clash}", $"Taken by {clash}");
                btn.Background = defaultBg;
                Cleanup();
                e.Handled = true;
                return;
            }

            host.Settings.Current.Hotkeys[action] = candidate;
            host.Settings.Save();
            host.Hotkeys.RegisterAll();

            btn.Content = DescribeBinding(host, action);
            btn.Background = defaultBg;
            Cleanup();
            Logger.Info($"Hotkey rebound: {action} = {btn.Content}");
            e.Handled = true;
        };

        lostFocusHandler = (s, e) =>
        {
            // Aborted by clicking elsewhere
            btn.Content = DescribeBinding(host, action);
            btn.Background = defaultBg;
            Cleanup();
        };

        btn.PreviewKeyDown += keyHandler;
        btn.LostKeyboardFocus += lostFocusHandler;
    }

    // ---- Controller combos ----

    /// <summary>The actions that can get a controller combo (the three that matter while playing).</summary>
    private static readonly string[] PadActions =
    {
        HotkeyActions.SaveReplay, HotkeyActions.Screenshot, HotkeyActions.ToggleRecording
    };

    private sealed record PadRow(TextBlock Combo, Button Learn, Button Remove);

    private readonly Dictionary<string, PadRow> _padRows = new();
    private TextBlock? _padStatus;
    private CheckBox? _padEnabled;
    private string? _learningAction;
    private AppHost? _padHost;
    private DispatcherTimer? _padMessageTimer;
    private DispatcherTimer? _padSaveTimer;

    private static string ActionLabel(string action)
    {
        foreach (var (a, label) in Actions)
            if (a == action) return label;
        return action;
    }

    private void BuildControllerSection(AppHost host)
    {
        var cfg = host.Settings.Current.Gamepad;

        RowsContainer.Children.Add(new TextBlock
        {
            Text = L.T("Controller", "Controller"),
            FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 18, 0, 2)
        });
        RowsContainer.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("MutedStyle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Text = L.T("Halte eine Tastenkombination auf dem Xbox-Controller gedrückt, um dieselbe Aktion wie per Hotkey auszulösen — auch im Spiel.",
                       "Hold a button combination on the Xbox controller to run the same action as the hotkey — also in game.")
        });

        _padEnabled = new CheckBox
        {
            Content = L.T("Controller-Kombis aktivieren", "Enable controller combos"),
            IsChecked = cfg.Enabled, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 2)
        };
        _padEnabled.Checked += (_, _) => SetPadEnabled(host, true);
        _padEnabled.Unchecked += (_, _) => SetPadEnabled(host, false);
        RowsContainer.Children.Add(_padEnabled);

        _padStatus = new TextBlock
        {
            Style = (Style)FindResource("MutedStyle"), Margin = new Thickness(0, 0, 0, 8)
        };
        RowsContainer.Children.Add(_padStatus);

        // Hold time
        var holdText = new TextBlock
        {
            Style = (Style)FindResource("MutedStyle"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0), MinWidth = 56
        };
        var hold = new Slider
        {
            Minimum = GamepadComboDetector.MinHoldMs, Maximum = GamepadComboDetector.MaxHoldMs,
            TickFrequency = 50, IsSnapToTickEnabled = true, SmallChange = 50, LargeChange = 100,
            Value = GamepadComboDetector.ClampHoldMs(cfg.HoldMs), Width = 220, VerticalAlignment = VerticalAlignment.Center
        };
        holdText.Text = $"{(int)hold.Value} ms";
        hold.ValueChanged += (_, ev) =>
        {
            cfg.HoldMs = (int)Math.Round(ev.NewValue);
            holdText.Text = $"{cfg.HoldMs} ms";
            SaveSoon(host);
        };
        var holdRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        holdRow.Children.Add(new TextBlock
        {
            Text = L.T("Haltezeit", "Hold time"), VerticalAlignment = VerticalAlignment.Center, Width = 80
        });
        holdRow.Children.Add(hold);
        holdRow.Children.Add(holdText);
        holdRow.ToolTip = L.T("So lange muss die Kombi gehalten werden (300–1500 ms). Länger = weniger versehentliche Auslösungen.",
                              "How long the combo has to be held (300–1500 ms). Longer = fewer accidental triggers.");
        RowsContainer.Children.Add(holdRow);

        foreach (var action in PadActions)
            RowsContainer.Children.Add(BuildPadRow(host, action));

        RowsContainer.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("MutedStyle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4),
            Text = L.T("Hinweis: Das Spiel sieht die Tasten ebenfalls — View/Menu öffnen dort evtl. Karte oder Pause. Wähle eine Kombi, die in deinen Spielen harmlos ist. " +
                       "Steam Input und DS4Windows können Controller verstecken oder doppeln; PlayStation-Controller werden noch nicht unterstützt. " +
                       "Share- und Guide-Taste sind über XInput nicht verfügbar.",
                       "Note: the game sees the buttons too — View/Menu may open the map or pause there. Pick a combo that is harmless in your games. " +
                       "Steam Input and DS4Windows can hide or duplicate controllers; PlayStation controllers are not supported yet. " +
                       "Share and Guide are not available through XInput.")
        });

        RefreshPadRows(host);
        RefreshPadStatus();
    }

    private FrameworkElement BuildPadRow(AppHost host, string action)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = ActionLabel(action), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("TextBrush"), TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var combo = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0), FontSize = 13
        };
        Grid.SetColumn(combo, 1);
        grid.Children.Add(combo);

        var learn = new Button
        {
            Content = L.T("Kombi anlernen", "Learn combo"),
            Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)FindResource("PanelBrush"), Foreground = (Brush)FindResource("TextBrush"),
            FontSize = 13, Cursor = Cursors.Hand,
            ToolTip = L.T("Drücke die Tasten und halte sie 1 Sekunde gedrückt (mindestens 2 Tasten).",
                          "Press the buttons and hold them for 1 second (at least 2 buttons).")
        };
        Grid.SetColumn(learn, 2);
        grid.Children.Add(learn);

        var remove = new Button
        {
            Content = L.T("Entfernen", "Remove"),
            Padding = new Thickness(10, 6, 10, 6), VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)FindResource("PanelBrush"), Foreground = (Brush)FindResource("TextBrush"),
            FontSize = 13, Cursor = Cursors.Hand
        };
        Grid.SetColumn(remove, 3);
        grid.Children.Add(remove);

        _padRows[action] = new PadRow(combo, learn, remove);

        learn.Click += (_, _) =>
        {
            if (_learningAction == action) { host.Gamepad.CancelLearn(); return; }
            if (_learningAction != null) return;
            StartLearning(host, action);
        };
        remove.Click += (_, _) =>
        {
            if (_learningAction == action) host.Gamepad.CancelLearn();
            if (host.Settings.Current.Gamepad.Bindings.Remove(action))
            {
                host.Settings.Save();
                Logger.Info($"Controller combo removed: {action}");
            }
            RefreshPadRows(host);
        };

        return new Border
        {
            Background = (Brush)FindResource("PanelBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 3, 0, 3),
            Child = grid
        };
    }

    private void StartLearning(AppHost host, string action)
    {
        bool started = host.Gamepad.BeginLearn(action,
            outcome => Dispatcher.BeginInvoke(new Action(() => OnLearned(host, action, outcome))));
        if (!started)
        {
            ShowPadMessage(host, action, L.T("Kein Controller verbunden.", "No controller connected."));
            return;
        }

        _learningAction = action;
        var row = _padRows[action];
        row.Combo.Text = L.T("Halte 2+ Tasten 1 s gedrückt …", "Hold 2+ buttons for 1 s …");
        row.Combo.Foreground = (Brush)FindResource("AccentBrush");
        row.Learn.Content = L.T("Abbrechen", "Cancel");
        RefreshPadStatus();
    }

    private void OnLearned(AppHost host, string action, LearnOutcome outcome)
    {
        if (_learningAction == action) _learningAction = null;
        if (_padRows.TryGetValue(action, out var row))
            row.Learn.Content = L.T("Kombi anlernen", "Learn combo");

        if (outcome.Status == LearnStatus.Captured)
        {
            host.Settings.Current.Gamepad.Bindings[action] = outcome.Mask;
            host.Settings.Save();
            Logger.Info($"Controller combo learned: {action} = {GamepadButtons.Describe(outcome.Mask)}");
            RefreshPadRows(host);
        }
        else if (outcome.Status == LearnStatus.Cancelled)
        {
            RefreshPadRows(host);
        }
        else
        {
            ShowPadMessage(host, action, GamepadStatusText.DescribeLearn(outcome, ActionLabel));
        }
        RefreshPadStatus();
    }

    private void SetPadEnabled(AppHost host, bool enabled)
    {
        var cfg = host.Settings.Current.Gamepad;
        if (cfg.Enabled == enabled) return;
        cfg.Enabled = enabled;
        host.Settings.Save();   // the service follows the setting: starts or stops its thread
        RefreshPadStatus();
    }

    /// <summary>Combo texts of all rows, from the settings.</summary>
    private void RefreshPadRows(AppHost host)
    {
        var bindings = host.Settings.Current.Gamepad.Bindings;
        foreach (var (action, row) in _padRows)
        {
            if (_learningAction == action) continue;
            bool has = bindings.TryGetValue(action, out var mask) && GamepadButtons.IsValidCombo(mask);
            row.Combo.Text = has ? GamepadButtons.Describe(mask) : L.T("— (nicht belegt)", "— (not set)");
            row.Combo.Foreground = (Brush)FindResource(has ? "TextBrush" : "MutedBrush");
            row.Remove.IsEnabled = bindings.ContainsKey(action);
        }
    }

    /// <summary>Shows a short message in place of the combo text, then goes back to the stored combo.</summary>
    private void ShowPadMessage(AppHost host, string action, string text)
    {
        if (!_padRows.TryGetValue(action, out var row)) return;
        row.Combo.Text = text;
        row.Combo.Foreground = (Brush)FindResource("DangerBrush");

        _padMessageTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _padMessageTimer.Stop();
        _padMessageTimer.Tick -= OnPadMessageTimer;
        _padMessageTimer.Tick += OnPadMessageTimer;
        _padMessageTimer.Start();
    }

    private void OnPadMessageTimer(object? sender, EventArgs e)
    {
        _padMessageTimer?.Stop();
        if (App.Host is { } host) RefreshPadRows(host);
    }

    private void RefreshPadStatus()
    {
        var host = App.Host;
        if (host is null || _padStatus is null) return;
        var cfg = host.Settings.Current.Gamepad;
        var slots = host.Gamepad.ConnectedSlots;
        _padStatus.Text = GamepadStatusText.Format(cfg.Enabled, host.Gamepad.IsAvailable, slots);

        if (_padEnabled != null && _padEnabled.IsChecked != cfg.Enabled) _padEnabled.IsChecked = cfg.Enabled;
        foreach (var (action, row) in _padRows)
            row.Learn.IsEnabled = _learningAction == action || (_learningAction == null && cfg.Enabled && slots.Count > 0);
    }

    /// <summary>Slider drags save debounced, like the other widgets.</summary>
    private void SaveSoon(AppHost host)
    {
        if (_padSaveTimer is null)
        {
            _padSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _padSaveTimer.Tick += (_, _) => { _padSaveTimer.Stop(); App.Host?.Settings.Save(); };
        }
        _padSaveTimer.Stop();
        _padSaveTimer.Start();
    }

    private void AttachGamepad(AppHost? host)
    {
        if (host is null || _padHost == host) return;
        DetachGamepad();
        _padHost = host;
        host.Gamepad.StatusChanged += OnPadStatusChanged;
        RefreshPadStatus();
    }

    private void DetachGamepad()
    {
        if (_padHost is null) return;
        _padHost.Gamepad.StatusChanged -= OnPadStatusChanged;
        _padHost = null;
    }

    // Raised on the poll thread.
    private void OnPadStatusChanged() => Dispatcher.BeginInvoke(new Action(RefreshPadStatus));

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Leaving the view ends a half-done learn attempt and writes a pending hold-time change.
        if (_padHost != null && _learningAction != null) _padHost.Gamepad.CancelLearn();
        if (_padSaveTimer is { IsEnabled: true }) { _padSaveTimer.Stop(); _padHost?.Settings.Save(); }
        _padMessageTimer?.Stop();
        DetachGamepad();
    }

    private static string DescribeBinding(AppHost host, string action)
    {
        if (host.Settings.Current.Hotkeys.TryGetValue(action, out var b) && b.Key != 0)
            return HotkeyEntryViewModel.Describe(b);
        return L.T("— (nicht gebunden)", "— (not bound)");
    }

    private static bool IsModifierKey(Key k) => k is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or
        Key.LWin or Key.RWin or
        Key.System or Key.None or
        Key.Capital or Key.NumLock or Key.Scroll;
}
