using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WKI_Clipper.Native;
using WKI_Clipper.Services;
using WinForms = System.Windows.Forms;

namespace WKI_Clipper.Views;

/// <summary>
/// Places overlay windows in PHYSICAL pixels. The app is per-monitor DPI aware, so
/// monitor bounds (WinForms.Screen) are physical, while WPF's Left/Top/Width/Height are
/// DIPs. Mixing the two only worked at 100 % scaling — at 125/150 % the board, widgets,
/// toasts and the crosshair landed in the wrong place.
///
/// Rule used everywhere: SIZES stay WPF DIPs (content scales with Windows), POSITIONS
/// are set here via SetWindowPos in physical pixels of the target monitor.
/// </summary>
internal static class DisplayGeometry
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;

    /// <summary>Scale factor (1.0 = 100 %) of the monitor containing the physical point.</summary>
    public static double ScaleAt(double x, double y)
    {
        try
        {
            var mon = MonitorFromPoint(new POINT { X = (int)x, Y = (int)y }, MONITOR_DEFAULTTONEAREST);
            if (mon != IntPtr.Zero && GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out uint dpi, out _) == 0 && dpi > 0)
                return dpi / 96.0;
        }
        catch { /* pre-8.1 shcore: fall through */ }
        return 1.0;
    }

    public static double ScaleOf(WinForms.Screen s)
        => ScaleAt(s.Bounds.Left + s.Bounds.Width / 2.0, s.Bounds.Top + s.Bounds.Height / 2.0);

    public static LayoutRect WorkArea(WinForms.Screen s)
        => new(s.WorkingArea.Left, s.WorkingArea.Top, s.WorkingArea.Width, s.WorkingArea.Height);

    public static LayoutRect Bounds(WinForms.Screen s)
        => new(s.Bounds.Left, s.Bounds.Top, s.Bounds.Width, s.Bounds.Height);

    public static WinForms.Screen ScreenAt(double x, double y)
    {
        try { return WinForms.Screen.FromPoint(new System.Drawing.Point((int)x, (int)y)); }
        catch { return WinForms.Screen.PrimaryScreen ?? WinForms.Screen.AllScreens[0]; }
    }

    public static WinForms.Screen ScreenOf(LayoutRect r) => ScreenAt(r.X + r.W / 2, r.Y + r.H / 2);

    /// <summary>The window's real on-screen rectangle in physical pixels (null before it has a handle).</summary>
    public static LayoutRect? BoundsOf(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero || !User32.GetWindowRect(hwnd, out var r)) return null;
        return new LayoutRect(r.Left, r.Top, r.Width, r.Height);
    }

    /// <summary>
    /// Moves the window's top-left to a physical point. Creates the handle if needed, so
    /// it works before the first Show (no flash at the old place). If the move crosses
    /// onto a monitor with another scale, WPF rescales the window on the way and Windows
    /// may shift it — a second call puts it exactly where asked.
    /// </summary>
    public static void MoveTo(Window w, double x, double y)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        int ix = (int)Math.Round(x), iy = (int)Math.Round(y);
        const uint flags = SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
        SetWindowPos(hwnd, IntPtr.Zero, ix, iy, 0, 0, flags);
        if (User32.GetWindowRect(hwnd, out var r) && (r.Left != ix || r.Top != iy))
            SetWindowPos(hwnd, IntPtr.Zero, ix, iy, 0, 0, flags);
    }
}
