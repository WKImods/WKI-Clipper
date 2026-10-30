using System.Runtime.InteropServices;

namespace WKI_Clipper.Native;

internal static class WinMm
{
    /// <summary>Raises the system timer resolution; every call needs a matching <see cref="timeEndPeriod"/>.</summary>
    [DllImport("winmm.dll")]
    public static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    public static extern uint timeEndPeriod(uint uPeriod);
}
