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
/// Widgets stay VISIBLE to external screen capture (Snipping Tool, OBS) unless
/// <see cref="ExcludeFromCapture"/> is set (WhatsApp); the app's own screenshots hide
/// the remaining overlay centrally via WidgetHost.HideDuringCapture().
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
    /// <summary>A title-bar drag ended (host: resolve overlaps when the grid is on).</summary>
    public event Action<WidgetWindow>? DragFinished;
    /// <summary>A resize-grip drag ended (host: make room for the grown window).</summary>
    public event Action<WidgetWindow>? ResizeFinished;

    /// <summary>
    /// Live snapping while dragged: gets the proposed rectangle (physical pixels), returns
    /// the snapped one. Set by the host while the grid switch is on, null otherwise.
    /// </summary>
    public Func<WidgetWindow, Services.LayoutRect, Services.LayoutRect>? MoveSnapper { get; set; }
    /// <summary>Like <see cref="MoveSnapper"/> for the resize grip; returns the snapped physical size.</summary>
    public Func<WidgetWindow, Services.LayoutRect, (double W, double H)>? ResizeSnapper { get; set; }

    private IntPtr _hwnd;
    private bool _boardOpen = true;
    private double _configuredOpacity = 1.0;
    private bool _hoverBoost;

    /// <summary>The hosted widget UserControl (lets the host talk to it directly).</summary>
    public FrameworkElement WidgetContent { get; }

    /// <summary>
    /// Direct rendering: a normal (not per-pixel transparent) window whose alpha comes from
    /// WS_EX_LAYERED + SetLayeredWindowAttributes. Needed for content that is a real child
    /// window — the classic WebView2 of the WhatsApp widget. The composition WebView used
    /// elsewhere fetches its picture through Windows' screen capture, and a window excluded
    /// from capture blocks exactly that, so the user saw an empty widget.
    /// </summary>
    private readonly bool _direct;
    private System.Windows.Threading.DispatcherTimer? _hoverTimer;

    public WidgetWindow(WidgetId id, string title, FrameworkElement content, bool directRendering = false)
    {
        Id = id;
        InitializeComponent();
        _direct = directRendering;
        if (_direct)
        {
            // Must happen before the window handle exists.
            AllowsTransparency = false;
            ResizeMode = ResizeMode.NoResize;   // no system frame; the grip resizes
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x1E, 0x24));
        }
        TitleText.Text = title;
        WidgetContent = content;
        ContentHost.Child = content;
        // ValueChanged is attached AFTER InitializeComponent: setting Minimum during
        // the XAML parse coerces the value and would fire into a half-built window.
        OpacitySlider.Value = _configuredOpacity;
        OpacitySlider.ValueChanged += OnOpacitySliderChanged;
        UpdateOpacityTooltip();
    }

    public bool IsPinned
    {
        get => PinButton.IsChecked == true;
        set => PinButton.IsChecked = value;
    }

    /// <summary>Re-labels the window after a language switch (web widgets are not rebuilt).</summary>
    public void SetTitle(string title) => TitleText.Text = title;

    /// <summary>Inner padding around the hosted view (web pages want the room).</summary>
    public Thickness ContentPadding
    {
        get => ContentHost.Padding;
        set => ContentHost.Padding = value;
    }

    private bool _excludeFromCapture;

    /// <summary>
    /// Hidden from all screen capture (ddagrab, WGC, OBS, Snipping Tool) but still
    /// visible on the user's screen. Applies immediately, also at runtime.
    /// Note: AMD's own vsrc_amf capture ignores this — FFmpegCommandBuilder falls back
    /// to ddagrab while such a widget is in use.
    /// </summary>
    public bool ExcludeFromCapture
    {
        get => _excludeFromCapture;
        set
        {
            _excludeFromCapture = value;
            ApplyDisplayAffinity();
        }
    }

    private void ApplyDisplayAffinity()
    {
        if (_hwnd == IntPtr.Zero) return;
        uint affinity = _excludeFromCapture ? User32.WDA_EXCLUDEFROMCAPTURE : User32.WDA_NONE;
        if (!User32.SetWindowDisplayAffinity(_hwnd, affinity))
            Services.Logger.Warn($"Widget {Id}: SetWindowDisplayAffinity({affinity:X}) failed.");
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
        UpdateOpacityTooltip();
        if (!_hoverBoost) ShowOpacity(_configuredOpacity, animate: false);
    }

    private void OnOpacitySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _configuredOpacity = Math.Clamp(e.NewValue, 0.3, 1.0);
        UpdateOpacityTooltip();
        // While dragging the cursor is over the window, so the hover boost would mask
        // the change — show the real value during adjustment.
        ShowOpacity(_configuredOpacity, animate: false);
        OpacityChanged?.Invoke(this);
    }

    /// <summary>WPF opacity normally; window-level alpha in direct-rendering mode.</summary>
    private void ShowOpacity(double value, bool animate)
    {
        if (_direct)
        {
            if (_hwnd != IntPtr.Zero)
                User32.SetLayeredWindowAttributes(_hwnd, 0, (byte)Math.Round(Math.Clamp(value, 0, 1) * 255), User32.LWA_ALPHA);
            return;
        }
        if (animate) AnimateOpacityTo(value);
        else Opacity = value;
    }

    private void UpdateOpacityTooltip()
    {
        if (OpacitySlider != null)
            OpacitySlider.ToolTip = Services.L.T("Transparenz: ", "Transparency: ")
                                    + $"{(int)Math.Round(_configuredOpacity * 100)} %";
    }

    // Hover = temporarily fully visible; leaving fades back to the chosen value.
    // Only active when the user actually dialed transparency in.
    protected override void OnMouseEnter(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (_configuredOpacity >= 0.999) return;
        _hoverBoost = true;
        ShowOpacity(1.0, animate: true);
        if (_direct) StartHoverWatch();
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_hoverBoost) return;
        // Direct mode: moving onto the web page (a child window) looks like "leaving" to
        // WPF — keep the boost while the cursor is still inside the window.
        if (_direct && CursorInsideWindow()) return;
        EndHoverBoost();
    }

    private void EndHoverBoost()
    {
        _hoverBoost = false;
        _hoverTimer?.Stop();
        ShowOpacity(_configuredOpacity, animate: true);
    }

    /// <summary>The child web window swallows mouse messages, so leaving is detected by polling.</summary>
    private void StartHoverWatch()
    {
        if (_hoverTimer is null)
        {
            _hoverTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _hoverTimer.Tick += (_, _) => { if (!CursorInsideWindow()) EndHoverBoost(); };
        }
        _hoverTimer.Start();
    }

    private bool CursorInsideWindow()
        => _hwnd != IntPtr.Zero && User32.GetCursorPos(out var c) && User32.GetWindowRect(_hwnd, out var r)
           && c.X >= r.Left && c.X < r.Right && c.Y >= r.Top && c.Y < r.Bottom;

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
        // Visible to external capture unless excluded. Own screenshots hide the
        // overlay centrally via WidgetHost.HideDuringCapture().
        // The hook first: in direct mode it has to protect WS_EX_LAYERED from the very first change.
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        ApplyDisplayAffinity();
        ApplyActivationStyle();
        if (_direct) ShowOpacity(_hoverBoost ? 1.0 : _configuredOpacity, animate: false);
    }

    private const int WM_MOVING = 0x0216, WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232;

    // Where the window and the cursor were when the move began.
    private User32.RECT _moveStartRect;
    private User32.POINT _moveStartCursor;
    private bool _moveTracking;

    /// <summary>
    /// WM_MOVING carries the rectangle Windows is about to move the window to; rewriting
    /// it is how live snapping works.
    ///
    /// The proposed rectangle must NOT be trusted while snapping: Windows builds the next
    /// one on top of the rectangle we returned last time. Snapping a small mouse step back
    /// onto the same grid point then swallows every following step too — the window sticks
    /// and cannot be moved at all. So the unsnapped position is rebuilt from the cursor's
    /// total travel since the move began, and only that is snapped.
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Direct mode: WPF strips WS_EX_LAYERED from a window without AllowsTransparency
        // (verified: the style never sticks). Answering WM_STYLECHANGING ourselves, with the
        // bit kept, stops that — and with it alpha and click-through keep working.
        if (msg == User32.WM_STYLECHANGING && _direct && wParam.ToInt64() == User32.GWL_EXSTYLE && lParam != IntPtr.Zero)
        {
            int newStyle = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 4);   // STYLESTRUCT.styleNew
            System.Runtime.InteropServices.Marshal.WriteInt32(lParam, 4, newStyle | User32.WS_EX_LAYERED);
            handled = true;
            return IntPtr.Zero;
        }
        if (msg == WM_ENTERSIZEMOVE)
        {
            _moveTracking = User32.GetWindowRect(hwnd, out _moveStartRect) && User32.GetCursorPos(out _moveStartCursor);
        }
        else if (msg == WM_EXITSIZEMOVE)
        {
            _moveTracking = false;
        }
        else if (msg == WM_MOVING && lParam != IntPtr.Zero && MoveSnapper is { } snap)
        {
            var r = System.Runtime.InteropServices.Marshal.PtrToStructure<User32.RECT>(lParam);
            int w = r.Width, h = r.Height;
            double x = r.Left, y = r.Top;
            if (_moveTracking && User32.GetCursorPos(out var c))
            {
                x = _moveStartRect.Left + (c.X - _moveStartCursor.X);
                y = _moveStartRect.Top + (c.Y - _moveStartCursor.Y);
            }
            var s = snap(this, new Services.LayoutRect(x, y, w, h));
            r.Left = (int)Math.Round(s.X);
            r.Top = (int)Math.Round(s.Y);
            r.Right = r.Left + w;
            r.Bottom = r.Top + h;
            System.Runtime.InteropServices.Marshal.StructureToPtr(r, lParam, false);
            handled = true;
            return new IntPtr(1);
        }
        return IntPtr.Zero;
    }

    /// <summary>Board open = interactive/activatable; board closed = no-activate (no focus steal).</summary>
    public void SetBoardOpen(bool open)
    {
        _boardOpen = open;
        if (_hwnd != IntPtr.Zero) ApplyActivationStyle();
    }

    /// <summary>
    /// Click-through while pinned with the board closed (read-only overlays like chat):
    /// clicks land on the game underneath instead of the widget.
    /// </summary>
    public bool ClickThroughWhenPinned { get; set; }

    private void ApplyActivationStyle()
    {
        // When the board is closed, the widget must not steal focus from the game.
        // When open, allow normal interaction (sliders, text boxes need focus).
        int ex = User32.GetWindowLong(_hwnd, User32.GWL_EXSTYLE);
        if (_boardOpen)
            ex &= ~(User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW | User32.WS_EX_TRANSPARENT);
        else
        {
            ex |= User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW;
            if (ClickThroughWhenPinned) ex |= User32.WS_EX_TRANSPARENT;
            else ex &= ~User32.WS_EX_TRANSPARENT;
        }
        if (_direct) ex |= User32.WS_EX_LAYERED;   // window alpha + click-through need it
        User32.SetWindowLong(_hwnd, User32.GWL_EXSTYLE, ex);
    }

    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { /* DragMove throws if the button was already released */ }
        GeometryChanged?.Invoke(this);
        DragFinished?.Invoke(this);
    }

    private void OnResize(object sender, DragDeltaEventArgs e)
    {
        // The thumb reports the mouse offset from its CURRENT position, so snapping the
        // size here never drifts away from the cursor.
        double w = Math.Max(MinWidth, Width + e.HorizontalChange);
        double h = Math.Max(MinHeight, Height + e.VerticalChange);
        if (ResizeSnapper is { } snap && DisplayGeometry.BoundsOf(this) is { } b)
        {
            double s = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
            var (pw, ph) = snap(this, new Services.LayoutRect(b.X, b.Y, w * s, h * s));
            w = pw / s;
            h = ph / s;
        }
        Width = Math.Max(MinWidth, w);
        Height = Math.Max(MinHeight, h);
    }

    private void OnResizeCompleted(object sender, DragCompletedEventArgs e)
    {
        GeometryChanged?.Invoke(this);
        ResizeFinished?.Invoke(this);
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
