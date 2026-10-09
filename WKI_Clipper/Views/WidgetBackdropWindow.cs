using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WKI_Clipper.Native;
using WinForms = System.Windows.Forms;

namespace WKI_Clipper.Views;

/// <summary>
/// Full-monitor dim behind the widgets while the board is open. Clicking it or
/// pressing Esc dismisses the board (Xbox behaviour). The board is only up while the
/// user interacts, and own screenshots hide it via WidgetHost.HideDuringCapture().
/// </summary>
public sealed class WidgetBackdropWindow : Window
{
    public event Action? Dismissed;

    public WidgetBackdropWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Focusable = true;
    }

    /// <summary>Covers the given monitor and takes the keyboard (Esc closes the board).</summary>
    public void ShowOn(WinForms.Screen screen)
    {
        Cover(screen);
        Show();
        Activate();
        Focus();
    }

    /// <summary>
    /// Sizes and places the dim over the whole monitor at any display scaling: size in
    /// DIPs of that monitor, position in physical pixels (see DisplayGeometry).
    /// </summary>
    public void Cover(WinForms.Screen screen)
    {
        var b = DisplayGeometry.Bounds(screen);
        double s = DisplayGeometry.ScaleOf(screen);
        Width = b.W / s;
        Height = b.H / s;
        DisplayGeometry.MoveTo(this, b.X, b.Y);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Dismissed?.Invoke();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Dismissed?.Invoke();
        base.OnKeyDown(e);
    }
}
