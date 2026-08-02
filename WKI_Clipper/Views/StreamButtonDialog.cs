using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.ViewModels;

namespace WKI_Clipper.Views;

/// <summary>
/// Modal config dialog for one stream-deck tile: label, action, dynamic parameters
/// (scenes/inputs loaded live from OBS; editable so names can be typed while OBS is
/// offline), tile color, and an optional global hotkey (press-to-bind, with collision
/// checks against the clipper's own hotkeys and every other tile).
/// </summary>
public sealed class StreamButtonDialog : Window
{
    private readonly AppHost _host;
    private readonly int _slot;

    private readonly TextBox _labelBox = new() { MinWidth = 220 };
    private readonly ComboBox _actionBox = new() { MinWidth = 220 };
    private readonly ComboBox _paramBox = new() { MinWidth = 220, IsEditable = true };
    private readonly ComboBox _param2Box = new() { MinWidth = 220, IsEditable = true };
    private readonly TextBlock _paramLabel = new();
    private readonly TextBlock _param2Label = new();
    private readonly StackPanel _paramPanel = new();
    private readonly StackPanel _param2Panel = new();
    private readonly Button _hotkeyBtn = new() { MinWidth = 200, MinHeight = 34 };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly List<Border> _swatches = new();

    private HotkeyBinding? _hotkey;
    private string _color;

    /// <summary>Set when OK was pressed — the configured (or edited) button.</summary>
    public StreamButtonConfig? Result { get; private set; }

    private static readonly string[] Palette =
        { "#3A3A44", "#2E7D32", "#C62828", "#1565C0", "#6A1B9A", "#E65100", "#00838F", "#FF6A2C" };

    private static (StreamAction action, string label)[] Actions =>
    new[]
    {
        (StreamAction.SetScene,            L.T("Szene wechseln", "Switch scene")),
        (StreamAction.ToggleStream,        L.T("Stream Start/Stop", "Stream start/stop")),
        (StreamAction.StartStream,         L.T("Stream starten", "Start stream")),
        (StreamAction.StopStream,          L.T("Stream beenden", "Stop stream")),
        (StreamAction.ToggleRecord,        L.T("OBS-Aufnahme Start/Stop", "OBS record start/stop")),
        (StreamAction.PauseResumeRecord,   L.T("OBS-Aufnahme Pause/Weiter", "OBS record pause/resume")),
        (StreamAction.SaveReplayBuffer,    L.T("OBS: Replay speichern", "OBS: save replay")),
        (StreamAction.ToggleReplayBuffer,  L.T("OBS: Replay-Buffer an/aus", "OBS: replay buffer on/off")),
        (StreamAction.ToggleInputMute,     L.T("Eingang stummschalten", "Toggle input mute")),
        (StreamAction.ToggleSceneItem,     L.T("Szenen-Element ein/aus", "Toggle scene item")),
        (StreamAction.ToggleVirtualCam,    L.T("Virtuelle Kamera an/aus", "Toggle virtual camera")),
        (StreamAction.StudioModeTransition,L.T("Studio-Übergang auslösen", "Trigger studio transition")),
    };

    public StreamButtonDialog(AppHost host, int slot, StreamButtonConfig? existing, Window owner)
    {
        _host = host;
        _slot = slot;
        _color = existing?.Color ?? Palette[0];
        _hotkey = existing?.Hotkey;

        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Title = L.T("Streaming-Button", "Streaming button");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");

        var root = new StackPanel { Margin = new Thickness(18) };

        root.Children.Add(Row(L.T("Beschriftung", "Label"), _labelBox));
        _labelBox.Text = existing?.Label ?? "";

        foreach (var (a, lbl) in Actions) _actionBox.Items.Add(new ActionChoice(a, lbl));
        _actionBox.SelectedIndex = Math.Max(0, Array.FindIndex(Actions, x => x.action == (existing?.Action ?? StreamAction.SetScene)));
        _actionBox.SelectionChanged += (_, _) => UpdateParamArea();
        root.Children.Add(Row(L.T("Aktion", "Action"), _actionBox));

        _paramPanel.Children.Add(_paramLabel);
        _paramPanel.Children.Add(_paramBox);
        _paramPanel.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(_paramPanel);

        _param2Panel.Children.Add(_param2Label);
        _param2Panel.Children.Add(_param2Box);
        _param2Panel.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(_param2Panel);

        _paramBox.Text = existing?.Param ?? "";
        _param2Box.Text = existing?.Param2 ?? "";
        // Loading scene items needs the chosen scene → refresh when it changes.
        _paramBox.SelectionChanged += (_, _) => { if (CurrentAction == StreamAction.ToggleSceneItem) _ = LoadSceneItemsAsync(); };

        // --- color swatches ---
        var colorRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var hex in Palette)
        {
            var b = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand,
                Background = BrushFromHex(hex), Tag = hex,
                BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent
            };
            b.MouseLeftButtonUp += (_, _) => { _color = hex; UpdateSwatchSelection(); };
            _swatches.Add(b);
            colorRow.Children.Add(b);
        }
        root.Children.Add(Row(L.T("Farbe", "Color"), colorRow));
        UpdateSwatchSelection();

        // --- hotkey ---
        var hotkeyRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        _hotkeyBtn.Content = DescribeHotkey();
        _hotkeyBtn.Click += (_, _) => StartHotkeyCapture();
        var clearHk = new Button { Content = L.T("Löschen", "Clear"), Margin = new Thickness(8, 0, 0, 0) };
        clearHk.Click += (_, _) => { _hotkey = null; _hotkeyBtn.Content = DescribeHotkey(); _hint.Text = ""; };
        hotkeyRow.Children.Add(_hotkeyBtn);
        hotkeyRow.Children.Add(clearHk);
        root.Children.Add(Row(L.T("Globaler Hotkey", "Global hotkey"), hotkeyRow));

        root.Children.Add(_hint);
        _hint.Foreground = (Brush)Application.Current.FindResource("MutedBrush");

        // --- buttons ---
        var btnRow = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var ok = new Button { Content = "OK", MinWidth = 90, Style = (Style)Application.Current.FindResource("AccentButton") };
        ok.Click += (_, _) => OnOk();
        var cancel = new Button { Content = L.T("Abbrechen", "Cancel"), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        btnRow.Children.Add(ok);
        btnRow.Children.Add(cancel);
        root.Children.Add(btnRow);

        Content = root;
        UpdateParamArea();
    }

    private StreamAction CurrentAction => ((ActionChoice)_actionBox.SelectedItem!).Action;

    private void UpdateParamArea()
    {
        var a = CurrentAction;
        bool needsParam = a is StreamAction.SetScene or StreamAction.ToggleInputMute or StreamAction.ToggleSceneItem;
        bool needsParam2 = a is StreamAction.ToggleSceneItem;

        _paramPanel.Visibility = needsParam ? Visibility.Visible : Visibility.Collapsed;
        _param2Panel.Visibility = needsParam2 ? Visibility.Visible : Visibility.Collapsed;

        _paramLabel.Text = a switch
        {
            StreamAction.SetScene => L.T("Szene", "Scene"),
            StreamAction.ToggleInputMute => L.T("Audio-Eingang", "Audio input"),
            StreamAction.ToggleSceneItem => L.T("Szene", "Scene"),
            _ => ""
        };
        _param2Label.Text = L.T("Element", "Item");

        if (needsParam) _ = LoadParamChoicesAsync(a);
        if (needsParam2) _ = LoadSceneItemsAsync();
    }

    /// <summary>Fills the parameter dropdown from live OBS data (keeps typed text intact).</summary>
    private async Task LoadParamChoicesAsync(StreamAction a)
    {
        var typed = _paramBox.Text;
        List<string> items = a switch
        {
            StreamAction.ToggleInputMute => await Task.Run(_host.Obs.ListInputNames),
            _ => await Task.Run(_host.Obs.ListScenes),
        };
        _paramBox.Items.Clear();
        foreach (var s in items) _paramBox.Items.Add(s);
        _paramBox.Text = typed;
        _hint.Text = items.Count == 0 && !_host.Obs.IsConnected
            ? L.T("OBS ist nicht verbunden — Namen können auch von Hand eingetragen werden.",
                  "OBS is not connected — names can also be typed manually.")
            : "";
    }

    private async Task LoadSceneItemsAsync()
    {
        var scene = _paramBox.Text;
        if (string.IsNullOrWhiteSpace(scene)) return;
        var typed = _param2Box.Text;
        var items = await Task.Run(() => _host.Obs.ListSceneItems(scene));
        _param2Box.Items.Clear();
        foreach (var s in items) _param2Box.Items.Add(s);
        _param2Box.Text = typed;
    }

    // ---- hotkey capture (same pattern as HotkeysView press-to-bind) ----

    private void StartHotkeyCapture()
    {
        var prev = _hotkeyBtn.Content;
        _hotkeyBtn.Content = L.T("Drücke Tastenkombi…  (Esc abbrechen)", "Press a key combo…  (Esc to cancel)");
        _hotkeyBtn.Background = (Brush)Application.Current.FindResource("AccentBrush");
        _hotkeyBtn.Focus();
        Keyboard.Focus(_hotkeyBtn);

        KeyEventHandler? handler = null;
        KeyboardFocusChangedEventHandler? lost = null;
        void Cleanup()
        {
            if (handler != null) _hotkeyBtn.PreviewKeyDown -= handler;
            if (lost != null) _hotkeyBtn.LostKeyboardFocus -= lost;
            _hotkeyBtn.Background = (Brush)Application.Current.FindResource("PanelBrush");
        }

        handler = (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { _hotkeyBtn.Content = prev; Cleanup(); e.Handled = true; return; }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                    or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            { e.Handled = true; return; }

            var mods = HotkeyModifier.None;
            var km = Keyboard.Modifiers;
            if ((km & ModifierKeys.Control) != 0) mods |= HotkeyModifier.Control;
            if ((km & ModifierKeys.Alt) != 0) mods |= HotkeyModifier.Alt;
            if ((km & ModifierKeys.Shift) != 0) mods |= HotkeyModifier.Shift;
            if ((km & ModifierKeys.Windows) != 0) mods |= HotkeyModifier.Win;

            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0) { e.Handled = true; return; }

            var candidate = new HotkeyBinding { Modifiers = mods, Key = (uint)vk };
            var clash = FindCollision(candidate);
            if (clash != null)
            {
                _hint.Text = L.T($"Kombination ist schon belegt: {clash}. Andere wählen.",
                                 $"That combo is already taken: {clash}. Pick another.");
                _hotkeyBtn.Content = prev;
            }
            else
            {
                _hotkey = candidate;
                _hotkeyBtn.Content = DescribeHotkey();
                _hint.Text = "";
            }
            Cleanup();
            e.Handled = true;
        };
        lost = (_, _) => { _hotkeyBtn.Content = DescribeHotkey(); Cleanup(); };

        _hotkeyBtn.PreviewKeyDown += handler;
        _hotkeyBtn.LostKeyboardFocus += lost;
    }

    /// <summary>Human-readable owner of a colliding binding, or null when free (shared logic).</summary>
    private string? FindCollision(HotkeyBinding candidate)
        => HotkeyService.FindCollision(_host.Settings.Current, candidate, excludeSlot: _slot);

    private string DescribeHotkey()
        => _hotkey is { Key: not 0 } hk ? HotkeyEntryViewModel.Describe(hk) : L.T("— (kein Hotkey)", "— (no hotkey)");

    private void OnOk()
    {
        var a = CurrentAction;
        bool needsParam = a is StreamAction.SetScene or StreamAction.ToggleInputMute or StreamAction.ToggleSceneItem;
        if (needsParam && string.IsNullOrWhiteSpace(_paramBox.Text))
        {
            _hint.Text = L.T("Bitte einen Parameter wählen/eintragen.", "Please choose or type a parameter.");
            return;
        }
        if (a == StreamAction.ToggleSceneItem && string.IsNullOrWhiteSpace(_param2Box.Text))
        {
            _hint.Text = L.T("Bitte das Szenen-Element angeben.", "Please specify the scene item.");
            return;
        }

        string label = _labelBox.Text.Trim();
        if (label.Length == 0)
            label = needsParam ? _paramBox.Text.Trim() : ((ActionChoice)_actionBox.SelectedItem!).Label;

        Result = new StreamButtonConfig
        {
            Slot = _slot,
            Label = label,
            Color = _color,
            Action = a,
            Param = needsParam ? _paramBox.Text.Trim() : null,
            Param2 = a == StreamAction.ToggleSceneItem ? _param2Box.Text.Trim() : null,
            Hotkey = _hotkey
        };
        DialogResult = true;
        Close();
    }

    // ---- small helpers ----

    private void UpdateSwatchSelection()
    {
        foreach (var s in _swatches)
            s.BorderBrush = (string)s.Tag == _color
                ? (Brush)Application.Current.FindResource("TextBrush")
                : Brushes.Transparent;
    }

    private static FrameworkElement Row(string label, FrameworkElement content)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = (Brush)Application.Current.FindResource("MutedBrush"),
            Margin = new Thickness(0, 0, 0, 3)
        });
        stack.Children.Add(content);
        return stack;
    }

    internal static SolidColorBrush BrushFromHex(string hex)
    {
        try { return new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)); }
        catch { return new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x44)); }
    }

    private sealed record ActionChoice(StreamAction Action, string Label)
    {
        public override string ToString() => Label;
    }
}
