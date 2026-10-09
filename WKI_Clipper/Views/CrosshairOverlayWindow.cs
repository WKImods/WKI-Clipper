using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WKI_Clipper.Models;
using WKI_Clipper.Native;
using WKI_Clipper.Services;
using WinForms = System.Windows.Forms;

namespace WKI_Clipper.Views;

/// <summary>
/// The actual crosshair on screen: a fully transparent, borderless, topmost window
/// showing nothing but the chosen PNG.
///
/// Two modes, driven by the board state:
///  - board OPEN  → draggable (a faint frame shows the hit area) so it can be placed
///  - board CLOSED → WS_EX_TRANSPARENT + NOACTIVATE: clicks pass straight through to
///    the game, so aiming still works with the crosshair on top.
///
/// Marked WDA_EXCLUDEFROMCAPTURE: an aiming aid belongs on the screen, not in the footage.
/// This also hides it from OBS and the Snipping Tool, which is the accepted trade-off —
/// there is no per-capture opt-out, since F9 saves seconds that are already recorded.
/// </summary>
public sealed class CrosshairOverlayWindow : Window
{
    private readonly System.Windows.Controls.Image _image;
    private readonly Border _frame;
    private IntPtr _hwnd;
    private bool _interactive;
    private CrosshairSettings? _settings;

    /// <summary>Raised after the user drags the crosshair, with the new CENTER position.</summary>
    public event Action<double, double>? Moved;

    public CrosshairOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        IsHitTestVisible = false;

        _image = new System.Windows.Controls.Image
        {
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true
        };
        // Crosshairs are small; keep edges clean when scaled up.
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

        // The drag frame is a SIBLING overlay, never the image's parent: collapsing a
        // parent Border would collapse the crosshair itself (and the window with it,
        // since SizeToContent follows the content).
        _frame = new Border
        {
            BorderBrush = (System.Windows.Media.Brush)Application.Current.FindResource("AccentBrush"),
            BorderThickness = new Thickness(1),
            Background = System.Windows.Media.Brushes.Transparent,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };

        var root = new Grid();
        root.Children.Add(_image);
        root.Children.Add(_frame);
        Content = root;

        MouseLeftButtonDown += OnDragStart;
    }

    /// <summary>Applies image + scale, then re-centers on the stored position.</summary>
    public void Apply(BitmapSource? image, CrosshairSettings s)
    {
        _settings = s;
        _image.Source = image;
        if (image == null) { Hide(); return; }

        var (cx, cy) = TargetCenter(s);
        SizeFor(DisplayGeometry.ScaleAt(cx, cy));
        Opacity = Math.Clamp(s.Opacity, 0.05, 1.0);

        // Size is content-driven; wait for layout before centering on the target point.
        UpdateLayout();
        CenterOn(s);
    }

    /// <summary>Display scale the image is currently sized for.</summary>
    private double _sizedForScale = 1.0;

    /// <summary>
    /// One image pixel = one SCREEN pixel at Scale 1, whatever Windows' display scaling
    /// is: an aiming mark must neither grow nor blur at 125/150 %. DIPs are divided by
    /// the scale of the monitor it sits on.
    /// </summary>
    private void SizeFor(double dpi)
    {
        if (_image.Source is not BitmapSource img || _settings is null) return;
        double scale = Math.Clamp(_settings.Scale, 0.1, 10);
        _image.Width = img.PixelWidth * scale / dpi;
        _image.Height = img.PixelHeight * scale / dpi;
        _sizedForScale = dpi;
    }

    /// <summary>The configured center in physical pixels, or the primary screen's middle.</summary>
    private static (double X, double Y) TargetCenter(CrosshairSettings s)
    {
        if (s.CenterX is double sx && s.CenterY is double sy) return (sx, sy);
        // Never placed yet → dead center of the primary screen.
        var scr = WinForms.Screen.PrimaryScreen ?? WinForms.Screen.AllScreens[0];
        return (scr.Bounds.Left + scr.Bounds.Width / 2.0, scr.Bounds.Top + scr.Bounds.Height / 2.0);
    }

    /// <summary>Positions the window so the image's center sits on the configured point (physical pixels).</summary>
    public void CenterOn(CrosshairSettings s)
    {
        _settings = s;
        var (cx, cy) = TargetCenter(s);
        double dpi = DisplayGeometry.ScaleAt(cx, cy);
        double w = (ActualWidth > 0 ? ActualWidth : _image.Width) * dpi;
        double h = (ActualHeight > 0 ? ActualHeight : _image.Height) * dpi;
        DisplayGeometry.MoveTo(this, cx - w / 2, cy - h / 2);
    }

    /// <summary>
    /// Board open → draggable and visible frame. Board closed → click-through, so the
    /// crosshair never intercepts a shot.
    /// </summary>
    public void SetInteractive(bool interactive)
    {
        _interactive = interactive;
        IsHitTestVisible = interactive;
        _frame.Visibility = interactive ? Visibility.Visible : Visibility.Collapsed;
        Cursor = interactive ? System.Windows.Input.Cursors.SizeAll : null;
        if (_hwnd != IntPtr.Zero) ApplyClickThrough();
    }

    private void ApplyClickThrough()
    {
        int ex = User32.GetWindowLong(_hwnd, User32.GWL_EXSTYLE);
        // NOACTIVATE/TOOLWINDOW always: the crosshair must never take focus or show
        // up in Alt-Tab. TRANSPARENT only while non-interactive (clicks pass through).
        ex |= User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW;
        if (_interactive) ex &= ~User32.WS_EX_TRANSPARENT;
        else ex |= User32.WS_EX_TRANSPARENT;
        User32.SetWindowLong(_hwnd, User32.GWL_EXSTYLE, ex);
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (!_interactive) return;
        try { DragMove(); } catch { return; }
        if (DisplayGeometry.BoundsOf(this) is not { } b) return;

        // Physical pixels throughout — the stored center must mean the same screen point
        // at every display scaling.
        double cx = b.X + b.W / 2;
        double cy = b.Y + b.H / 2;

        // Snap into a grid anchored at the center of the monitor the crosshair
        // landed on.
        if (_settings is { SnapToGrid: true, GridSize: > 0 })
        {
            var (ax, ay) = MonitorCenter(cx, cy);
            (cx, cy) = Services.WidgetLayout.SnapToGrid(cx, cy, ax, ay, _settings.GridSize);
        }

        // Dropped on a monitor with another display scale: WPF has rescaled the window,
        // so re-size the image back to 1:1 screen pixels for the new monitor.
        double dpi = DisplayGeometry.ScaleAt(cx, cy);
        if (Math.Abs(dpi - _sizedForScale) > 0.001)
        {
            SizeFor(dpi);
            UpdateLayout();
        }

        // Re-seat the window so the image center sits exactly on the (snapped) point.
        double w = ActualWidth * dpi, h = ActualHeight * dpi;
        DisplayGeometry.MoveTo(this, cx - w / 2, cy - h / 2);

        Moved?.Invoke(cx, cy);
    }

    private static (double X, double Y) MonitorCenter(double x, double y)
    {
        try
        {
            var b = WinForms.Screen.FromPoint(new System.Drawing.Point((int)x, (int)y)).Bounds;
            return (b.Left + b.Width / 2.0, b.Top + b.Height / 2.0);
        }
        catch
        {
            var b = (WinForms.Screen.PrimaryScreen ?? WinForms.Screen.AllScreens[0]).Bounds;
            return (b.Left + b.Width / 2.0, b.Top + b.Height / 2.0);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        // Keep the crosshair out of every screen capture. Hiding it just before a capture
        // would not work for F9: that saves the PAST 60 seconds, which already contain the
        // overlay. The exclusion therefore has to be permanent, not momentary.
        try { User32.SetWindowDisplayAffinity(_hwnd, User32.WDA_EXCLUDEFROMCAPTURE); }
        catch { /* pre-2004 Windows: the crosshair stays visible in clips, nothing breaks */ }

        ApplyClickThrough();
    }
}
