using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    };

    public HotkeysView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
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

            host.Settings.Current.Hotkeys[action] = new HotkeyBinding
            {
                Modifiers = modifiers,
                Key = (uint)vk
            };
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
