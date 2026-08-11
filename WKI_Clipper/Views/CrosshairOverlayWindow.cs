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

        _image.Width = image.PixelWidth * Math.Clamp(s.Scale, 0.1, 10);
        _image.Height = image.PixelHeight * Math.Clamp(s.Scale, 0.1, 10);
        Opacity = Math.Clamp(s.Opacity, 0.05, 1.0);

        // Size is content-driven; wait for layout before centering on the target point.
        UpdateLayout();
        CenterOn(s);
    }

    /// <summary>Positions the window so the image's center sits on the configured point.</summary>
    public void CenterOn(CrosshairSettings s)
    {
        _settings = s;
        double cx, cy;
        if (s.CenterX is double sx && s.CenterY is double sy)
        {
            cx = sx; cy = sy;
        }
        else
        {
            // Never placed yet → dead center of the primary screen.
            var scr = WinForms.Screen.PrimaryScreen ?? WinForms.Screen.AllScreens[0];
            cx = scr.Bounds.Left + scr.Bounds.Width / 2.0;
            cy = scr.Bounds.Top + scr.Bounds.Height / 2.0;
        }
        double w = ActualWidth > 0 ? ActualWidth : _image.Width;
        double h = ActualHeight > 0 ? ActualHeight : _image.Height;
        Left = cx - w / 2;
        Top = cy - h / 2;
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

        double cx = Left + ActualWidth / 2;
        double cy = Top + ActualHeight / 2;

        // Snap into a grid anchored at the center of the monitor the crosshair
        // landed on, then re-seat the window on the snapped point.
        if (_settings is { SnapToGrid: true, GridSize: > 0 })
        {
            var (ax, ay) = MonitorCenter(cx, cy);
            (cx, cy) = Services.WidgetLayout.SnapToGrid(cx, cy, ax, ay, _settings.GridSize);
            Left = cx - ActualWidth / 2;
            Top = cy - ActualHeight / 2;
        }

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
