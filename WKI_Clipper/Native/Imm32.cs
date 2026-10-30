using System;
using System.Runtime.InteropServices;

namespace WKI_Clipper.Native;

internal static class Imm32
{
    /// <summary>The key behind a VK_PROCESSKEY message the IME of <paramref name="hWnd"/> processed; VK_PROCESSKEY or 0 when it does not tell.</summary>
    [DllImport("imm32.dll")]
    public static extern uint ImmGetVirtualKey(IntPtr hWnd);
}
