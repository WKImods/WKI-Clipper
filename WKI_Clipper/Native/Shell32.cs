using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WKI_Clipper.Native;

/// <summary>
/// SHFileOperationW: the only API that moves a file into the Recycle Bin (File.Delete never does), and
/// SHQueryRecycleBinW, which counts a drive's bin (the only way to tell "recycled" from "deleted for good").
/// Also the shell calls of the shortcut bar (.lnk files, icons, Store apps, default browser) at the end.
/// </summary>
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

    /// <summary>
    /// What <see cref="SHQueryRecycleBinW"/> reports for one drive: bytes and items of the current user's bin there.
    /// Natural alignment (24 bytes: the shell rejects any other cbSize with E_INVALIDARG; Pack = 4 gave 20).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    /// <summary>The Recycle Bin of one drive (a root like "C:\"). S_OK (0) when it has one and could be read.</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHQueryRecycleBinW(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    // ---- Shortcut bar: reading .lnk files, program icons, Store apps, the default browser ----

    /// <summary>CLSID_ShellLink: the object that reads and writes .lnk files.</summary>
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    public class ShellLinkObject { }

    /// <summary>IShellLinkW, declared by hand in vtable order. Read side only is used; the setters keep the slots in place.</summary>
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx, cy;
        public SIZE(int x, int y) { cx = x; cy = y; }
    }

    /// <summary>IShellItemImageFactory: the shell's own thumbnail/icon of a file, with alpha.</summary>
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    public static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    public static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    /// <summary>SIIGBF_BIGGERSIZEOK: a larger image than asked for is fine (the file's biggest icon).</summary>
    public const int SIIGBF_BIGGERSIZEOK = 0x1;
    /// <summary>SIIGBF_ICONONLY: the icon, never a thumbnail of the content.</summary>
    public const int SIIGBF_ICONONLY = 0x4;

    /// <summary>A shell item for a path or a "shell:AppsFolder\AUMID" name. S_OK (0) when it exists.</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, out IntPtr ppv);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr hObject);

    /// <summary>ASSOCSTR_EXECUTABLE: the exe a file type or protocol is opened with.</summary>
    public const int ASSOCSTR_EXECUTABLE = 2;

    /// <summary>"http" with ASSOCSTR_EXECUTABLE gives the Windows default browser's exe. S_OK (0) when found.</summary>
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int AssocQueryStringW(uint flags, int str, string pszAssoc, string? pszExtra, StringBuilder? pszOut, ref uint pcchOut);
}
