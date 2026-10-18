using System;
using System.Runtime.InteropServices;

namespace WKI_Clipper.Native;

/// <summary>SHFileOperationW: the only API that moves a file into the Recycle Bin (File.Delete never does).</summary>
internal static class Shell32
{
    public const uint FO_DELETE = 0x0003;

    public const ushort FOF_SILENT = 0x0004;
    public const ushort FOF_NOCONFIRMATION = 0x0010;
    /// <summary>Move to the Recycle Bin instead of deleting for good.</summary>
    public const ushort FOF_ALLOWUNDO = 0x0040;
    public const ushort FOF_NOERRORUI = 0x0400;
    /// <summary>Ask (instead of silently deleting for good) when the file cannot be recycled — too big, or no bin on that drive.</summary>
    public const ushort FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>Win32 ERROR_SHARING_VIOLATION: another process holds the file open.</summary>
    public const int ERROR_SHARING_VIOLATION = 32;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        /// <summary>Double-null terminated: the path, a NUL, and the marshaler's own terminator.</summary>
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);
}
