using System;
using WKI_Clipper.Native;

namespace WKI_Clipper.Services;

/// <summary>
/// Whether this session's own desktop has the input. The lock screen, Ctrl+Alt+Del and a UAC prompt run on the secure
/// desktop, which Desktop Duplication cannot capture: ddagrab loses its output there (ACCESS_LOST mid-run) and a start
/// fails with "Desktop duplication access denied" until the user is back.
/// </summary>
public static class InputDesktop
{
    public static bool IsAvailable()
    {
        try
        {
            var desktop = User32.OpenInputDesktop(0, false, User32.DESKTOP_READOBJECTS);
            if (desktop == IntPtr.Zero) return false;
            User32.CloseDesktop(desktop);
            return true;
        }
        catch { return true; }   // cannot tell: never hold a restart back on it
    }
}
