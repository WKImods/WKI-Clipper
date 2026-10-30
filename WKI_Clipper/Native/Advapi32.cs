using System;
using System.Runtime.InteropServices;

namespace WKI_Clipper.Native;

internal static class Advapi32
{
    public const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, out uint tokenInformation,
        uint tokenInformationLength, out uint returnLength);

    /// <summary>
    /// true when the process behind <paramref name="processHandle"/> (opened with PROCESS_QUERY_LIMITED_INFORMATION) runs elevated.
    /// A token that a non-elevated caller cannot even open is an elevated one (access denied), so that counts as elevated too -
    /// but only if the caller made sure the handle really has the query right (anti-cheat drivers strip rights from handles of
    /// processes that are not elevated); any other failure counts as "not elevated" (unknown).
    /// </summary>
    public static bool IsElevated(IntPtr processHandle)
    {
        if (!OpenProcessToken(processHandle, TOKEN_QUERY, out var token))
            return Marshal.GetLastWin32Error() == Kernel32.ERROR_ACCESS_DENIED;
        try
        {
            return GetTokenInformation(token, TokenElevation, out uint elevated, sizeof(uint), out _) && elevated != 0;
        }
        finally { Kernel32.CloseHandle(token); }
    }
}
