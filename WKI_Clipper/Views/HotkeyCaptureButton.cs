using System;
using System.Windows;
using System.Windows.Input;
using WKI_Clipper.Models;
using WKI_Clipper.ViewModels;
using WKI_Clipper.Services;

namespace WKI_Clipper.Views;

/// <summary>
/// A button that shows a global hotkey and, when clicked, captures the next key combination (Esc cancels, clicking elsewhere
/// aborts) - the same press-to-bind pattern as the hotkey page and the stream-deck tile dialog, in one reusable control.
/// A combination is only accepted when <c>collision</c> finds no owner for it (the clipper's own hotkeys, the tiles, other shortcuts).
/// Unbound is the default and shows as "-".
/// </summary>
public sealed class HotkeyCaptureButton : Button
{
    private readonly Func<HotkeyBinding, string?> _collision;

    /// <summary>The bound combination, or null.</summary>
    public HotkeyBinding? Binding { get; private set; }

    /// <summary>The user bound a new combination or cleared it (null).</summary>
    public event Action<HotkeyBinding?>? BindingChanged;

    /// <summary>Something to tell the user ("taken by ...").</summary>
    public event Action<string>? Notice;

    /// <summary>True from the click until the combination, Esc or a click elsewhere ended the capture.</summary>
    public bool IsCapturing { get; private set; }

    /// <summary>The capture is over (bound, cancelled or aborted). The owner may rebuild around the button now - not before: removing a capturing button from the tree aborts it.</summary>
    public event Action? CaptureEnded;

    /// <param name="collision">The owner of a combination that is taken, or null when it is free.</param>
    public HotkeyCaptureButton(HotkeyBinding? binding, Func<HotkeyBinding, string?> collision)
    {
        _collision = collision;
        Binding = binding is { Key: not 0 } ? binding : null;
        // The theme styles exactly "Button" (an implicit style does not reach a subclass): take that look.
        Style = (Style)Application.Current.FindResource(typeof(Button));
        MinWidth = 140;
        MinHeight = 28;
        Padding = new Thickness(10, 3, 10, 3);
        ShowBinding();
        Click += (_, _) => StartCapture();
    }

    private void ShowBinding()
        => Content = Binding is { } b ? HotkeyEntryViewModel.Describe(b) : L.T("— (kein Hotkey)", "— (no hotkey)");

    /// <summary>Binds <paramref name="candidate"/> unless it is taken (then the user is told who has it). True when bound.</summary>
    internal bool Accept(HotkeyBinding candidate)
    {
        if (candidate.Key == 0) return false;
        var clash = _collision(candidate);
        if (clash != null)
        {
            Notice?.Invoke(L.T($"Kombination ist schon belegt: {clash}. Eine andere wählen.", $"That combination is already taken: {clash}. Pick another."));
            ShowBinding();
            return false;
        }
        Binding = candidate;
        ShowBinding();
        BindingChanged?.Invoke(candidate);
        return true;
    }

    /// <summary>Unbinds. Nothing happens (and nothing is raised) when there was no hotkey.</summary>
    public void Clear()
    {
        if (Binding == null) return;
        Binding = null;
        ShowBinding();
        BindingChanged?.Invoke(null);
    }

    private void StartCapture()
    {
        // A second click (or a double click) must not hang a second handler pair on the button: its Done would never run
        // (the first handler marks the key handled), and the next plain key press would be bound as a global hotkey unseen.
        if (IsCapturing) return;
        IsCapturing = true;

        Content = L.T("Drücke Tastenkombi…  (Esc abbrechen)", "Press a key combo…  (Esc to cancel)");
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("AccentBrush");
        Focus();
        Keyboard.Focus(this);

        KeyEventHandler? keys = null;
        KeyboardFocusChangedEventHandler? lost = null;
        void Done()
        {
            if (!IsCapturing) return;
            IsCapturing = false;
            if (keys != null) PreviewKeyDown -= keys;
            if (lost != null) LostKeyboardFocus -= lost;
            ClearValue(BackgroundProperty);   // back to the theme's button look
            ShowBinding();
            CaptureEnded?.Invoke();
        }

        keys = (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            e.Handled = true;
            if (key == Key.Escape) { Done(); return; }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
                return;   // wait for the real key

            var mods = HotkeyModifier.None;
            var km = Keyboard.Modifiers;
            if ((km & ModifierKeys.Control) != 0) mods |= HotkeyModifier.Control;
            if ((km & ModifierKeys.Alt) != 0) mods |= HotkeyModifier.Alt;
            if ((km & ModifierKeys.Shift) != 0) mods |= HotkeyModifier.Shift;
            if ((km & ModifierKeys.Windows) != 0) mods |= HotkeyModifier.Win;

            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0) return;
            Accept(new HotkeyBinding { Modifiers = mods, Key = (uint)vk });
            Done();
        };
        lost = (_, _) => Done();   // clicked elsewhere: abort

        PreviewKeyDown += keys;
        LostKeyboardFocus += lost;
    }
}
