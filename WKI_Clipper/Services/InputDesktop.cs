using System;
using WKI_Clipper.Native;

namespace WKI_Clipper.Services;

/// <summary>
/// Whether this session's own desktop has the input. The lock screen, Ctrl+Alt+Del and a UAC prompt run on the secure
/// desktop, a screen saver or a password manager's "secure desktop" on desktops of their own - Desktop Duplication cannot
/// capture any of them: ddagrab loses its output there (ACCESS_LOST mid-run) and a start fails ("Desktop duplication access
/// denied") until the user is back on the desktop the clipper runs on.
/// </summary>
public static class InputDesktop
{
    public static bool IsAvailable()
    {
        try
        {
            // The secure desktop cannot even be opened by a user process.
            var input = User32.OpenInputDesktop(0, false, User32.DESKTOP_READOBJECTS);
            if (input == IntPtr.Zero) return false;
            try
            {
                // One that can (screen saver, a password manager's own desktop) still is not ours.
                return IsSameDesktop(NameOf(input), OwnDesktopName());
            }
            finally { User32.CloseDesktop(input); }
        }
        catch { return true; }   // cannot tell: never hold a restart back on it
    }

    /// <summary>The input desktop is ours when both names are known and equal; an unknown name never blocks.</summary>
    public static bool IsSameDesktop(string? inputName, string? ownName)
        => string.IsNullOrEmpty(inputName) || string.IsNullOrEmpty(ownName)
           || string.Equals(inputName, ownName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The name of the desktop the clipper runs on ("Default" in a normal session); null when it cannot be read.</summary>
    internal static string? OwnDesktopName()
    {
        try { return NameOf(User32.GetThreadDesktop(Kernel32.GetCurrentThreadId())); }
        catch { return null; }
    }

    private static string? NameOf(IntPtr desktop)
    {
        if (desktop == IntPtr.Zero) return null;
        var buffer = new char[256];
        if (!User32.GetUserObjectInformation(desktop, User32.UOI_NAME, buffer, (uint)(buffer.Length * sizeof(char)), out _))
            return null;
        int end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? buffer.Length : end);
    }
}
