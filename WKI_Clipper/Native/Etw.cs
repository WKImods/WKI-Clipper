using System;
using System.Runtime.InteropServices;

namespace WKI_Clipper.Native;

/// <summary>
/// The few advapi32 ETW calls a passive real-time consumer needs (StartTrace / EnableTraceEx2 /
/// OpenTrace / ProcessTrace / ControlTrace / CloseTrace). Nothing here touches another process.
/// Struct layouts are written by offset into unmanaged buffers (x64 only — the app is x64):
/// EVENT_TRACE_PROPERTIES (120 bytes + names) and EVENT_TRACE_LOGFILEW (448 bytes).
/// </summary>
internal static class Etw
{
    public const uint ERROR_SUCCESS = 0;
    public const uint ERROR_ACCESS_DENIED = 5;
    public const uint ERROR_INVALID_PARAMETER = 87;
    public const uint ERROR_ALREADY_EXISTS = 183;
    public const uint ERROR_CANCELLED = 1223;
    public const uint ERROR_WMI_INSTANCE_NOT_FOUND = 4201;

    public const uint WNODE_FLAG_TRACED_GUID = 0x0002_0000;
    public const uint EVENT_TRACE_REAL_TIME_MODE = 0x0000_0100;
    /// <summary>One common buffer instead of per-CPU buffers: a real-time consumer then sees the
    /// events in the order they were written, so consecutive presents never arrive swapped.</summary>
    public const uint EVENT_TRACE_NO_PER_PROCESSOR_BUFFERING = 0x1000_0000;

    public const uint EVENT_TRACE_CONTROL_QUERY = 0;
    public const uint EVENT_TRACE_CONTROL_STOP = 1;

    public const uint PROCESS_TRACE_MODE_REAL_TIME = 0x0000_0100;
    public const uint PROCESS_TRACE_MODE_RAW_TIMESTAMP = 0x0000_1000;
    public const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x1000_0000;

    public const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
    public const byte TRACE_LEVEL_INFORMATION = 4;
    public const uint ENABLE_TRACE_PARAMETERS_VERSION_2 = 2;
    public const uint EVENT_FILTER_TYPE_EVENT_ID = 0x8000_0200;

    public const ulong INVALID_PROCESSTRACE_HANDLE = ulong.MaxValue;

    // --- EVENT_TRACE_PROPERTIES (x64) ---
    private const int MaxNameChars = 1024;
    public const int PropertiesStructSize = 120;
    public const int PropertiesBufferSize = PropertiesStructSize + 2 * MaxNameChars * 2;
    private const int P_WnodeBufferSize = 0;
    private const int P_WnodeClientContext = 40;
    private const int P_WnodeFlags = 44;
    private const int P_BufferSize = 48;
    private const int P_MinimumBuffers = 52;
    private const int P_MaximumBuffers = 56;
    private const int P_LogFileMode = 64;
    private const int P_FlushTimer = 68;
    private const int P_EventsLost = 88;
    private const int P_LogBuffersLost = 96;
    private const int P_RealTimeBuffersLost = 100;
    private const int P_LogFileNameOffset = 112;
    private const int P_LoggerNameOffset = 116;

    // --- EVENT_TRACE_LOGFILEW (x64) ---
    public const int LogfileSize = 448;
    private const int L_LoggerName = 8;
    private const int L_ProcessTraceMode = 28;
    private const int L_PerfFreq = 120 + 256;   // LogfileHeader.PerfFreq
    private const int L_EventRecordCallback = 424;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void EventRecordCallback(IntPtr eventRecord);

    [StructLayout(LayoutKind.Sequential)]
    public struct ENABLE_TRACE_PARAMETERS
    {
        public uint Version;
        public uint EnableProperty;
        public uint ControlFlags;
        public Guid SourceId;
        public IntPtr EnableFilterDesc;
        public uint FilterDescCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EVENT_FILTER_DESCRIPTOR
    {
        public ulong Ptr;
        public uint Size;
        public uint Type;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "StartTraceW")]
    public static extern uint StartTrace(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "ControlTraceW")]
    public static extern uint ControlTrace(ulong sessionHandle, string? sessionName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll")]
    public static extern uint EnableTraceEx2(ulong traceHandle, ref Guid providerId, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, ref ENABLE_TRACE_PARAMETERS enableParameters);

    [DllImport("advapi32.dll")]
    public static extern uint EnableTraceEx2(ulong traceHandle, ref Guid providerId, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "OpenTraceW")]
    public static extern ulong OpenTrace(IntPtr logfile);

    [DllImport("advapi32.dll")]
    public static extern uint ProcessTrace(ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll")]
    public static extern uint CloseTrace(ulong traceHandle);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    public static IntPtr AllocZeroed(int size)
    {
        IntPtr p = Marshal.AllocHGlobal(size);
        Marshal.Copy(new byte[size], 0, p, size);
        return p;
    }

    /// <summary>Properties for StartTrace: real-time only (no log file), QPC clock.</summary>
    public static IntPtr AllocSessionProperties(uint bufferKb, uint minBuffers, uint maxBuffers, uint logFileMode, uint flushTimerSec)
    {
        IntPtr p = AllocZeroed(PropertiesBufferSize);
        Marshal.WriteInt32(p, P_WnodeBufferSize, PropertiesBufferSize);
        Marshal.WriteInt32(p, P_WnodeFlags, unchecked((int)WNODE_FLAG_TRACED_GUID));
        Marshal.WriteInt32(p, P_WnodeClientContext, 1);   // 1 = QPC timestamps
        Marshal.WriteInt32(p, P_BufferSize, (int)bufferKb);
        Marshal.WriteInt32(p, P_MinimumBuffers, (int)minBuffers);
        Marshal.WriteInt32(p, P_MaximumBuffers, (int)maxBuffers);
        Marshal.WriteInt32(p, P_LogFileMode, unchecked((int)logFileMode));
        Marshal.WriteInt32(p, P_FlushTimer, (int)flushTimerSec);
        Marshal.WriteInt32(p, P_LoggerNameOffset, PropertiesStructSize);
        Marshal.WriteInt32(p, P_LogFileNameOffset, 0);
        return p;
    }

    /// <summary>Properties buffer for ControlTrace (query/stop): room for both names to be returned.</summary>
    public static IntPtr AllocControlProperties()
    {
        IntPtr p = AllocZeroed(PropertiesBufferSize);
        Marshal.WriteInt32(p, P_WnodeBufferSize, PropertiesBufferSize);
        Marshal.WriteInt32(p, P_LoggerNameOffset, PropertiesStructSize);
        Marshal.WriteInt32(p, P_LogFileNameOffset, PropertiesStructSize + MaxNameChars * 2);
        return p;
    }

    /// <summary>EventsLost + LogBuffersLost + RealTimeBuffersLost of a queried properties buffer.</summary>
    public static long ReadLostCounters(IntPtr properties)
        => (uint)Marshal.ReadInt32(properties, P_EventsLost)
         + (long)(uint)Marshal.ReadInt32(properties, P_LogBuffersLost)
         + (uint)Marshal.ReadInt32(properties, P_RealTimeBuffersLost);

    /// <summary>EVENT_TRACE_LOGFILEW for a real-time EVENT_RECORD consumer with raw (QPC) timestamps.</summary>
    public static IntPtr AllocRealTimeLogfile(IntPtr loggerName, IntPtr callback)
    {
        IntPtr p = AllocZeroed(LogfileSize);
        Marshal.WriteIntPtr(p, L_LoggerName, loggerName);
        Marshal.WriteInt32(p, L_ProcessTraceMode, unchecked((int)(PROCESS_TRACE_MODE_REAL_TIME
            | PROCESS_TRACE_MODE_EVENT_RECORD | PROCESS_TRACE_MODE_RAW_TIMESTAMP)));
        Marshal.WriteIntPtr(p, L_EventRecordCallback, callback);
        return p;
    }

    /// <summary>LogfileHeader.PerfFreq after OpenTrace — the tick rate of the raw timestamps.</summary>
    public static long ReadPerfFreq(IntPtr logfile) => Marshal.ReadInt64(logfile, L_PerfFreq);
}
