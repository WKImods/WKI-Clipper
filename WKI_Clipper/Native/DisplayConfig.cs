using System;
using System.Runtime.InteropServices;

namespace WKI_Clipper.Native;

/// <summary>
/// Display topology interop: the CCD API (QueryDisplayConfig / DisplayConfigGetDeviceInfo) for the monitor's
/// name, device path and EDID ids, GetMonitorInfo for the GDI name of an HMONITOR, MonitorFromWindow /
/// MonitorFromPoint for the HMONITOR of a window or point, and the thread DPI context
/// the topology reader runs under. Read-only: nothing here changes a display setting.
/// </summary>
internal static class DisplayConfig
{
    public const int ERROR_SUCCESS = 0;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    /// <summary>QueryDisplayConfig: only the paths that drive a monitor right now.</summary>
    public const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;

    public const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    public const int DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    /// <summary>DISPLAYCONFIG_TARGET_DEVICE_NAME.flags: edidManufactureId / edidProductCodeId are filled in.</summary>
    public const uint TARGET_NAME_FLAG_EDID_IDS_VALID = 0x00000004;

    /// <summary>MONITORINFO.dwFlags: the primary monitor.</summary>
    public const uint MONITORINFOF_PRIMARY = 0x00000001;

    /// <summary>MonitorFromWindow / MonitorFromPoint: the nearest monitor when the window or point is on none.</summary>
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    /// <summary>DPI_AWARENESS_CONTEXT values (pseudo handles).</summary>
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_UNAWARE = new(-1);
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    /// <summary>Only the header of the mode union is needed; the size (64 bytes) is what QueryDisplayConfig writes.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public int type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(uint flags,
        ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    /// <summary>
    /// The active paths, retried when the topology changes between the size query and the query itself
    /// (ERROR_INSUFFICIENT_BUFFER). Returns the last Win32 error code; <paramref name="paths"/> is empty on failure.
    /// </summary>
    public static int QueryActivePaths(out DISPLAYCONFIG_PATH_INFO[] paths)
    {
        int rc = ERROR_INSUFFICIENT_BUFFER;
        for (int attempt = 0; attempt < 3 && rc == ERROR_INSUFFICIENT_BUFFER; attempt++)
        {
            rc = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint numPaths, out uint numModes);
            if (rc != ERROR_SUCCESS) break;
            var pathArray = new DISPLAYCONFIG_PATH_INFO[numPaths];
            var modeArray = new DISPLAYCONFIG_MODE_INFO[numModes];
            rc = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, pathArray, ref numModes, modeArray, IntPtr.Zero);
            if (rc == ERROR_SUCCESS)
            {
                Array.Resize(ref pathArray, (int)numPaths);
                paths = pathArray;
                return rc;
            }
        }
        paths = Array.Empty<DISPLAYCONFIG_PATH_INFO>();
        return rc;
    }

    // --- Monitor info and thread DPI context ---

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public User32.RECT rcMonitor;
        public User32.RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    /// <summary>
    /// With a GDI name as <paramref name="lpDevice"/>: the monitor devices on that source. A monitor that sleeps
    /// (DisplayPort) or is gone has no entry here, while its CCD path can still be active.
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    /// <summary>Sets the calling thread's DPI awareness; returns the previous context (zero on failure).</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    /// <summary>The HMONITOR a window is on (most of its area); with <see cref="MONITOR_DEFAULTTONEAREST"/> never zero for a real window.</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    /// <summary>The HMONITOR a point in screen coordinates (the calling thread's DPI context) is on.</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(User32.POINT pt, uint dwFlags);
}
