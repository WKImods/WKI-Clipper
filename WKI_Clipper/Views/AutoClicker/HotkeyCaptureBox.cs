using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using WKI_Clipper.Native;
using WKI_Clipper.Services;
using WKI_Clipper.Services.AutoClicker;

namespace WKI_Clipper.Views.AutoClicker;

/// <summary>
/// The hotkey capture control of the AutoClicker (free-hotkeys design section 4): click, then press the combo - any key incl. Esc,
/// Backspace, Print Screen, F10, media keys and modifiers alone, any mouse button incl. the left one and the wheel while the cursor is
/// over the field. The combo is taken when everything is released (a wheel notch at once). <see cref="CancelButton"/>, a click beside
/// the field and focus loss cancel; <see cref="ClearButton"/> clears (a required hotkey goes back to the default Ctrl+Alt+Y).
/// In single-key mode (the keyboard output key) the first key press is taken, Esc cancels and mouse buttons end the capture.
/// Keys are read from the window's own messages, before WPF translates them (WPF hides Print Screen's key-down, turns F10 and Alt
/// combos into Key.System, reports generic modifiers): the codes match what the helper's low-level hook reports. No global hook in the
/// main app. While it records, the helper ignores its hotkeys (<see cref="ClickerController.SetHotkeyCapture"/>), the clipper's own
/// RegisterHotKey bindings are off (<see cref="HotkeyService.Suspend"/>), and input the clicker injected itself is never taken as a key.
/// </summary>
public sealed class HotkeyCaptureBox : Button
{
    private const int WM_APPCOMMAND = 0x0319;

    private readonly bool _required;
    private readonly bool _singleKey;
    private HotkeyRecorder? _recorder;
    private bool _singleCapture;
    private HashSet<int>? _singleDownAtStart;
    private HwndSource? _source;
    // Kept as fields: the very instances that were added are removed again.
    private readonly ThreadMessageEventHandler _threadFilter;
    private readonly HwndSourceHook _windowHook;

    /// <summary>The shown combo (null = none).</summary>
    public ClickerHotkey? Hotkey { get; private set; }

    /// <summary>Record left and right modifiers apart (LStrg instead of Strg). Read when a capture starts.</summary>
    public bool Sided { get; set; }

    /// <summary>Ends a running capture and keeps the old combo. Visible only while capturing; the panel places it next to the field.</summary>
    public Button CancelButton { get; }

    /// <summary>Removes the combo (the required hotkey: back to the default). The panel places it next to the field.</summary>
    public Button ClearButton { get; }

    /// <summary>The user captured a new combo or cleared it.</summary>
    public event Action<ClickerHotkey?>? HotkeyChanged;

    public bool IsCapturing => _recorder is not null || _singleCapture;

    /// <param name="required">Clearing restores the default instead of leaving no hotkey.</param>
    /// <param name="singleKey">One keyboard key, no modifiers, no mouse buttons.</param>
    public HotkeyCaptureBox(bool required, bool singleKey = false)
    {
        _required = required;
        _singleKey = singleKey;
        _threadFilter = OnThreadMessage;
        _windowHook = OnWindowMessage;
        // The theme styles exactly "Button" (an implicit style does not reach a subclass): take that look.
        Style = (Style)Application.Current.FindResource(typeof(Button));
        MinWidth = 160;
        MinHeight = 28;
        Padding = new Thickness(10, 3, 10, 3);

        CancelButton = new Button
        {
            Content = L.T("Abbrechen", "Cancel"),
            Visibility = Visibility.Collapsed,
            ToolTip = L.T("Aufnahme beenden, der bisherige Hotkey bleibt.", "Stop recording and keep the current hotkey."),
        };
        CancelButton.Click += (_, _) => EndCapture();
        ClearButton = new Button
        {
            Content = required ? L.T("Standard", "Default") : L.T("Leeren", "Clear"),
            ToolTip = required
                ? L.T($"Setzt den Hotkey auf {ClickerHotkey.Describe(ClickerHotkey.Default())} zurück.", $"Resets the hotkey to {ClickerHotkey.Describe(ClickerHotkey.Default())}.")
                : L.T("Entfernt den Hotkey.", "Removes the hotkey."),
        };
        ClearButton.Click += (_, _) => Finish(RecorderResult.Cleared, null);

        Click += (_, _) => StartCapture();
        PreviewKeyDown += OnWpfKey;
        PreviewKeyUp += OnWpfKey;
        PreviewMouseDown += OnMouseDown;
        PreviewMouseUp += OnMouseUp;
        LostKeyboardFocus += (_, _) => OnFocusLost();
        Unloaded += (_, _) => EndCapture();   // a panel that goes away mid-capture must not leave the helper's hotkeys switched off
        ShowHotkey();
    }

    /// <summary>Shows a combo without raising <see cref="HotkeyChanged"/> (settings refresh). Ignored while capturing.</summary>
    public void SetHotkey(ClickerHotkey? hotkey)
    {
        if (IsCapturing) return;
        Hotkey = hotkey?.Clone();
        ShowHotkey();
    }

    private void ShowHotkey()
    {
        if (_singleKey)
            Content = Hotkey is { Keys.Count: > 0 } h ? ClickerHotkey.KeyName(h.Keys[0]) : L.T("— (keine Taste)", "— (no key)");
        else
            Content = ClickerHotkey.Describe(Hotkey);
        ClearButton.IsEnabled = _required
            ? !ClickerHotkey.SameCombo(Hotkey, ClickerHotkey.Default())
            : Hotkey is { IsEmpty: false };
    }

    private string Prompt => _singleKey
        ? L.T("Taste drücken…  (Esc bricht ab)", "Press a key…  (Esc cancels)")
        : L.T("Taste, Maustaste oder Mausrad hier drücken…  (Abbrechen: Knopf oder daneben klicken)",
              "Press a key, mouse button or wheel here…  (cancel: button or click elsewhere)");

    private void StartCapture()
    {
        if (IsCapturing) return;
        _source = PresentationSource.FromVisual(this) as HwndSource;
        if (_source is null) return;   // not in a window: there are no messages to read
        // The clipper's own RegisterHotKey bindings (F9, F10, Ctrl+F10 ...) would take the key-down and fire their action: off meanwhile.
        SuspendClipperHotkeys(true);
        if (_singleKey)
        {
            _singleCapture = true;
            _singleDownAtStart = HotkeyRecorder.KeysDown(IsPhysicallyDown);
        }
        else _recorder = new HotkeyRecorder(Sided, IsPhysicallyDown);
        TellClicker(true);

        // The thread filter sees posted input before WPF (and before TranslateMessage), whichever order the handlers run in;
        // WM_APPCOMMAND is sent, not posted, so it needs the window hook.
        ComponentDispatcher.ThreadFilterMessage += _threadFilter;
        _source.AddHook(_windowHook);

        Content = Prompt;
        CancelButton.Visibility = _singleKey ? Visibility.Collapsed : Visibility.Visible;
        SetResourceReference(BackgroundProperty, "AccentBrush");
        SetResourceReference(ForegroundProperty, "OnAccentBrush");
        Focus();
        Keyboard.Focus(this);
        // Every mouse button in the window comes here while capturing (the starting click has been released already): over the
        // field it is recorded, elsewhere it cancels or hits the clear button.
        if (!_singleKey) Dispatcher.BeginInvoke(new Action(() => { if (IsCapturing) Mouse.Capture(this, CaptureMode.Element); }));
    }

    private void EndCapture()
    {
        if (!IsCapturing) return;
        _recorder = null;
        _singleCapture = false;
        ComponentDispatcher.ThreadFilterMessage -= _threadFilter;
        _source?.RemoveHook(_windowHook);
        _source = null;
        TellClicker(false);
        SuspendClipperHotkeys(false);
        if (IsMouseCaptured) ReleaseMouseCapture();
        CancelButton.Visibility = Visibility.Collapsed;
        ClearValue(BackgroundProperty);
        ClearValue(ForegroundProperty);
        ShowHotkey();
    }

    private static void TellClicker(bool capturing)
    {
        try { App.Host?.Clicker.SetHotkeyCapture(capturing); }
        catch (Exception ex) { Logger.Warn("AutoClicker: hotkey capture state not passed on: " + ex.Message); }
    }

    private static void SuspendClipperHotkeys(bool suspend)
    {
        try
        {
            if (App.Host?.Hotkeys is not { } hotkeys) return;
            if (suspend) hotkeys.Suspend();
            else hotkeys.Resume();
        }
        catch (Exception ex) { Logger.Warn("AutoClicker: clipper hotkeys not " + (suspend ? "suspended" : "resumed") + ": " + ex.Message); }
    }

    /// <summary>The lock screen (or a UAC prompt) has the input: the key state of this desktop says nothing about the user's keys.</summary>
    private static bool InputDesktopIsElsewhere()
    {
        var desktop = User32.OpenInputDesktop(0, false, User32.DESKTOP_READOBJECTS);
        if (desktop == IntPtr.Zero) return true;
        User32.CloseDesktop(desktop);
        return false;
    }

    /// <summary>Input the AutoClicker sent itself (e.g. still queued from a run that is just stopping): never a key of the combo.</summary>
    private static bool IsInjectedByClicker()
    {
        try { return (ulong)(long)User32.GetMessageExtraInfo() == ClickerHost.InputInjector.Tag; }
        catch { return false; }
    }

    private static bool IsDown(int vk) => (User32.GetKeyState(vk) & 0x8000) != 0;

    private static bool IsPhysicallyDown(int vk) => (User32.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool IsCursorOver(FrameworkElement element)
    {
        if (!element.IsVisible) return false;
        var p = Mouse.GetPosition(element);
        return p.X >= 0 && p.Y >= 0 && p.X < element.ActualWidth && p.Y < element.ActualHeight;
    }

    private void Finish(RecorderResult result, ClickerHotkey? captured)
    {
        EndCapture();
        switch (result)
        {
            case RecorderResult.Done:
                Hotkey = captured;
                break;
            case RecorderResult.Cleared:
                Hotkey = _required ? ClickerHotkey.Default() : null;
                break;
            default:
                return;
        }
        ShowHotkey();
        HotkeyChanged?.Invoke(Hotkey?.Clone());
    }

    private void Complete(RecorderResult result)
    {
        switch (result)
        {
            case RecorderResult.Done: Finish(result, _recorder?.Result); return;
            case RecorderResult.Cancelled: EndCapture(); return;
            case RecorderResult.Cleared: Finish(result, null); return;
        }
        if (_recorder is null) return;
        var preview = _recorder.Preview;
        Content = preview.IsEmpty ? Prompt : ClickerHotkey.Describe(preview);
    }

    // ---------------- keyboard and wheel: window messages ----------------

    private void OnThreadMessage(ref MSG msg, ref bool handled)
    {
        if (!IsCapturing || _source is null || msg.hwnd != _source.Handle) return;
        switch (msg.message)
        {
            case User32.WM_KEYDOWN or User32.WM_SYSKEYDOWN or User32.WM_KEYUP or User32.WM_SYSKEYUP:
                // Swallowed: no Alt/F10 menu, no Tab navigation, no access keys, no Space/Enter click while recording.
                handled = true;
                if (IsInjectedByClicker()) return;
                long lParam = (long)msg.lParam;
                var hwnd = msg.hwnd;
                int raw = RecorderKeys.UnwrapImeKey((int)((long)msg.wParam & 0xFF), lParam,
                    () => (int)Imm32.ImmGetVirtualKey(hwnd), scan => (int)User32.MapVirtualKeyW(scan, User32.MAPVK_VSC_TO_VK_EX));
                int vk = RecorderKeys.FromKeyMessage(raw, lParam);
                OnKey(vk, msg.message is User32.WM_KEYDOWN or User32.WM_SYSKEYDOWN, RecorderKeys.IsRepeat(lParam));
                break;
            case User32.WM_MOUSEWHEEL or User32.WM_MOUSEHWHEEL:
                handled = true;   // no scrolling of the tab while recording
                if (_recorder is null || IsInjectedByClicker() || !IsCursorOver(this)) return;
                if (RecorderKeys.FromWheel(msg.message == User32.WM_MOUSEHWHEEL, (long)msg.wParam) is { } wheel)
                    Complete(_recorder.Down(wheel));
                break;
        }
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_APPCOMMAND || !IsCapturing) return IntPtr.Zero;
        handled = true;   // no browser-back/volume command reaches WPF or the shell while recording
        // A key message already went through OnThreadMessage (and, swallowed, produced no command); a keyboard command without one
        // comes from a key whose message the window did not get: take its VK as a press and release.
        if (!IsInjectedByClicker() && RecorderKeys.FromAppCommand((long)lParam) is { } vk
            && !(_recorder?.Preview.Keys.Contains(vk) ?? false))
        {
            OnKey(vk, down: true, repeat: false);
            if (IsCapturing) OnKey(vk, down: false, repeat: false);
        }
        return (IntPtr)1;
    }

    private void OnKey(int vk, bool down, bool repeat)
    {
        if (vk == 0) return;
        if (_singleCapture)
        {
            // Any key is taken on its first key-down. A key-up whose key was not down at the start is a press whose key-down the window
            // never got (Print Screen, another program's RegisterHotKey): taken as well.
            if (down && repeat || !down && (_singleDownAtStart?.Remove(vk) ?? true)) return;
            if (vk == ClickerVk.Escape) { EndCapture(); return; }
            if (ClickerVk.IsModifier(vk)) return;   // a modifier is no output key
            Finish(RecorderResult.Done, new ClickerHotkey { Keys = { vk } });
            return;
        }
        if (_recorder is null) return;
        var result = down ? _recorder.Down(vk, repeat) : _recorder.Up(vk);
        if (result == RecorderResult.Pending && !down) result = _recorder.Reconcile(IsDown);
        Complete(result);
    }

    /// <summary>The window messages are read in <see cref="OnThreadMessage"/>; what still reaches WPF must not move focus or click.</summary>
    private void OnWpfKey(object sender, KeyEventArgs e)
    {
        if (IsCapturing) e.Handled = true;
    }

    private void OnFocusLost()
    {
        // Win+L: the lock screen took the focus, and on its desktop every key reads as released - that is no finished Win combo.
        if (_recorder is { } recorder && !InputDesktopIsElsewhere()) Complete(recorder.FocusLost(IsPhysicallyDown));
        else EndCapture();
    }

    // ---------------- mouse buttons: WPF events under mouse capture ----------------

    private static int MouseVk(MouseButton button) => button switch
    {
        MouseButton.Left => ClickerVk.LButton,
        MouseButton.Right => ClickerVk.RButton,
        MouseButton.Middle => ClickerVk.MButton,
        MouseButton.XButton1 => ClickerVk.XButton1,
        _ => ClickerVk.XButton2
    };

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsCapturing) return;
        e.Handled = true;   // no Click while capturing
        if (IsInjectedByClicker()) return;
        if (_recorder is null) { EndCapture(); return; }   // single-key mode: a click ends the capture
        if (IsCursorOver(this)) Complete(_recorder.Down(MouseVk(e.ChangedButton)));
        else if (IsCursorOver(ClearButton) && ClearButton.IsEnabled) Complete(RecorderResult.Cleared);
        else Complete(RecorderResult.Cancelled);   // the cancel button or anywhere else
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_recorder is null) return;
        e.Handled = true;
        if (IsInjectedByClicker()) return;
        Complete(_recorder.Up(MouseVk(e.ChangedButton)));
    }
}
