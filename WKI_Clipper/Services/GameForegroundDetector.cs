using System;
using System.Diagnostics;
using System.Windows.Forms;
using WKI_Clipper.Native;

namespace WKI_Clipper.Services;

/// <summary>
/// "Is a game in the foreground right now?" for the option that holds a Discord compress back while
/// playing. A game is a real (non-shell) app whose window covers its whole monitor - exclusive or
/// borderless fullscreen - or the capture target the user configured. When the clipper's own window is
/// in front (the widget board opened over the game) the app before it is judged instead, so opening
/// the gallery over a game does not look like "no game".
/// </summary>
internal static class GameForegroundDetector
{
    /// <summary>Drivers and borderless windows overshoot the monitor by a pixel or two.</summary>
    private const int Tolerance = 2;

    internal static bool CoversMonitor(int left, int top, int right, int bottom, System.Drawing.Rectangle monitor)
        => left <= monitor.Left + Tolerance
           && top <= monitor.Top + Tolerance
           && right >= monitor.Right - Tolerance
           && bottom >= monitor.Bottom - Tolerance;

    /// <param name="lastExternalPid">The last foreground app that was not ours (see ForegroundTracker).</param>
    /// <param name="configuredTargetProcess">Window-mode capture target (process name without .exe), if any.</param>
    public static bool IsGameInForeground(Func<int?> lastExternalPid, string? configuredTargetProcess)
    {
        try
        {
            var hwnd = User32.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            User32.GetWindowThreadProcessId(hwnd, out uint upid);

            int pid = (int)upid;
            if (pid == Environment.ProcessId)
            {
                // our own window is in front: judge the app that was there before
                if (lastExternalPid() is not { } external) return false;
                pid = external;
                hwnd = IntPtr.Zero;
            }
            if (pid == 0) return false;

            string name;
            IntPtr window = hwnd;
            using (var process = Process.GetProcessById(pid))
            {
                name = process.ProcessName;
                if (window == IntPtr.Zero) window = process.MainWindowHandle;
            }

            if (!CaptureTargetResolver.IsCouplableApp(name)) return false;
            if (!string.IsNullOrEmpty(configuredTargetProcess)
                && string.Equals(name, configuredTargetProcess, StringComparison.OrdinalIgnoreCase)) return true;

            if (window == IntPtr.Zero || User32.IsIconic(window)) return false;
            if (!User32.GetWindowRect(window, out var r)) return false;
            return CoversMonitor(r.Left, r.Top, r.Right, r.Bottom, Screen.FromHandle(window).Bounds);
        }
        catch (Exception ex)
        {
            Logger.Warn("Game-in-foreground check failed: " + ex.Message);
            return false;   // when in doubt do not hold the job back forever
        }
    }
}
