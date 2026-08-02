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
    /// <summary>Raised when the user picks a new opacity, so the host can persist it.</summary>
    public event Action<WidgetWindow>? OpacityChanged;

    private IntPtr _hwnd;
    private bool _boardOpen = true;
    private double _configuredOpacity = 1.0;
    private bool _hoverBoost;

    /// <summary>The hosted widget UserControl (lets the host talk to it directly).</summary>
    public FrameworkElement WidgetContent { get; }

    public WidgetWindow(WidgetId id, string title, FrameworkElement content)
    {
        Id = id;
        InitializeComponent();
        TitleText.Text = title;
        WidgetContent = content;
        ContentHost.Child = content;
        OpacityButton.ToolTip = Services.L.T("Transparenz", "Transparency");
        OpacitySlider.Value = _configuredOpacity;
        OpacitySlider.ValueChanged += OnOpacitySliderChanged;
        UpdateOpacityLabel();
    }

    public bool IsPinned
    {
        get => PinButton.IsChecked == true;
        set => PinButton.IsChecked = value;
    }

    /// <summary>The persisted opacity choice (0.3–1.0), independent of the hover boost.</summary>
    public double ConfiguredOpacity => _configuredOpacity;

    /// <summary>Applies a stored opacity without raising OpacityChanged (host restore path).</summary>
    public void SetConfiguredOpacity(double value)
    {
        _configuredOpacity = Math.Clamp(value, 0.3, 1.0);
        OpacitySlider.ValueChanged -= OnOpacitySliderChanged;
        OpacitySlider.Value = _configuredOpacity;
        OpacitySlider.ValueChanged += OnOpacitySliderChanged;
        UpdateOpacityLabel();
        if (!_hoverBoost) Opacity = _configuredOpacity;
    }

    private void OnOpacityClick(object sender, RoutedEventArgs e)
        => OpacityPopup.IsOpen = !OpacityPopup.IsOpen;

    private void OnOpacitySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _configuredOpacity = Math.Clamp(e.NewValue, 0.3, 1.0);
        UpdateOpacityLabel();
        // While the slider is being dragged the cursor is over the window, so the
        // hover boost would mask the change — show the real value during adjustment.
        Opacity = _configuredOpacity;
        OpacityChanged?.Invoke(this);
    }

    private void UpdateOpacityLabel()
    {
        // Defensive: never let a XAML-parse-order event kill the constructor again.
        if (OpacityValueText != null)
            OpacityValueText.Text = $"{(int)Math.Round(_configuredOpacity * 100)} %";
    }

    // Hover = temporarily fully visible; leaving fades back to the chosen value.
    // Only active when the user actually dialed transparency in.
    protected override void OnMouseEnter(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (_configuredOpacity >= 0.999) return;
        _hoverBoost = true;
        AnimateOpacityTo(1.0);
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_hoverBoost) return;
        _hoverBoost = false;
        if (OpacityPopup.IsOpen) OpacityPopup.IsOpen = false;
        AnimateOpacityTo(_configuredOpacity);
    }

    private void AnimateOpacityTo(double target)
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(150),
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
        };
        anim.Completed += (_, _) => Opacity = target;
        BeginAnimation(OpacityProperty, anim);
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
