using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using WKI_Clipper.Models;
using WKI_Clipper.Native;

namespace WKI_Clipper.Views;

/// <summary>
/// A single floating, draggable, pinnable widget window. Borderless + topmost.
/// Widgets stay VISIBLE to external screen capture (Snipping Tool, OBS); the app's
/// own screenshots hide the overlay centrally via WidgetHost.HideDuringCapture().
///
/// Focus model: the window never activates on show (ShowActivated=false). When the
/// board is closed and the widget is pinned, it also gets WS_EX_NOACTIVATE so a
/// click on it can't pull focus away from the game.
/// </summary>
public partial class WidgetWindow : Window
{
    public WidgetId Id { get; }

    /// <summary>Raised when the user toggles the pin.</summary>
    public event Action<WidgetWindow>? PinToggled;
    /// <summary>Raised when the user closes the widget (hide + persist Visible=false).</summary>
    public event Action<WidgetWindow>? CloseRequested;
    /// <summary>Raised after a drag/resize settles, so the host can persist geometry.</summary>
    public event Action<WidgetWindow>? GeometryChanged;

    private IntPtr _hwnd;
    private bool _boardOpen = true;

    /// <summary>The hosted widget UserControl (lets the host talk to it directly).</summary>
    public FrameworkElement WidgetContent { get; }

    public WidgetWindow(WidgetId id, string title, FrameworkElement content)
    {
        Id = id;
        InitializeComponent();
        TitleText.Text = title;
        WidgetContent = content;
        ContentHost.Child = content;
    }

    public bool IsPinned
    {
        get => PinButton.IsChecked == true;
        set => PinButton.IsChecked = value;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        // Widgets stay visible to external capture (Snipping Tool, OBS). Own
        // screenshots hide the overlay centrally via WidgetHost.HideDuringCapture().
        ApplyActivationStyle();
    }

    /// <summary>Board open = interactive/activatable; board closed = no-activate (no focus steal).</summary>
    public void SetBoardOpen(bool open)
    {
        _boardOpen = open;
        if (_hwnd != IntPtr.Zero) ApplyActivationStyle();
    }

    private void ApplyActivationStyle()
    {
        // When the board is closed, the widget must not steal focus from the game.
        // When open, allow normal interaction (sliders, text boxes need focus).
        int ex = User32.GetWindowLong(_hwnd, User32.GWL_EXSTYLE);
        if (_boardOpen)
            ex &= ~(User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW);
        else
            ex |= User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW;
        User32.SetWindowLong(_hwnd, User32.GWL_EXSTYLE, ex);
    }

    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { /* DragMove throws if the button was already released */ }
        GeometryChanged?.Invoke(this);
    }

    private void OnResize(object sender, DragDeltaEventArgs e)
    {
        var w = Width + e.HorizontalChange;
        var h = Height + e.VerticalChange;
        Width = Math.Max(MinWidth, w);
        Height = Math.Max(MinHeight, h);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        // Persist size after a resize grip drag ends (deactivation is a cheap settle point).
        GeometryChanged?.Invoke(this);
    }

    private void OnPinChanged(object sender, RoutedEventArgs e) => PinToggled?.Invoke(this);

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this);
}
