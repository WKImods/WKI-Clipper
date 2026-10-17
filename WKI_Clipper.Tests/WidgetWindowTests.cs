using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using WKI_Clipper.Models;
using WKI_Clipper.Native;
using WKI_Clipper.Views;
using Xunit;
using Application = System.Windows.Application;

namespace WKI_Clipper.Tests;

/// <summary>
/// Real widget windows (STA thread, app theme loaded). Covers what only the Win32 window
/// itself can tell: which system behaviours Windows applies to it.
/// </summary>
[Collection("WpfApplication")]   // only one System.Windows.Application per process: never run alongside the other WPF tests
public class WidgetWindowTests
{
    private const int GWL_STYLE = -16;
    private const int WS_MAXIMIZEBOX = 0x00010000;
    private const int WS_THICKFRAME = 0x00040000;

    /// <summary>
    /// Dragging a widget to the top screen edge must not let Windows Snap maximize it:
    /// a maximized widget can no longer be moved (DragMove only works in the Normal
    /// state) or sized by the grip, so it stuck until the clipper was restarted.
    /// Snap needs a sizable frame, drag-to-top a maximize box — neither may be set.
    /// </summary>
    [Fact]
    public void Widget_windows_offer_windows_snap_nothing_to_maximize()
    {
        var styles = OnSta(() =>
        {
            EnsureTheme();
            int Style(bool direct)
            {
                var w = new WidgetWindow(WidgetId.Capture, "Test", new Border(), direct);
                try { return User32.GetWindowLong(new WindowInteropHelper(w).EnsureHandle(), GWL_STYLE); }
                finally { w.Close(); }
            }
            return (normal: Style(false), direct: Style(true));
        });

        Assert.Equal(0, styles.normal & WS_MAXIMIZEBOX);
        Assert.Equal(0, styles.normal & WS_THICKFRAME);
        Assert.Equal(0, styles.direct & WS_MAXIMIZEBOX);
        Assert.Equal(0, styles.direct & WS_THICKFRAME);
    }

    /// <summary>Should anything maximize a widget anyway, it snaps straight back to Normal.</summary>
    [Fact]
    public void Maximized_widget_returns_to_normal()
    {
        var state = OnSta(() =>
        {
            EnsureTheme();
            var w = new WidgetWindow(WidgetId.Capture, "Test", new Border()) { Left = -20000, Top = -20000, Width = 300, Height = 200 };
            try
            {
                w.Show();
                w.WindowState = WindowState.Maximized;
                return w.WindowState;
            }
            finally { w.Close(); }
        });

        Assert.Equal(WindowState.Normal, state);
    }

    private static void EnsureTheme()
    {
        if (Application.Current != null) return;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
            new Uri("/WKI_Clipper;component/Views/Theme.xaml", UriKind.Relative)));
    }

    private static T OnSta<T>(Func<T> body)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw new Exception("STA body failed", error);
        return result;
    }
}
