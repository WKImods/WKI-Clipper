using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WKI_Clipper.Native;

/// <summary>
/// A Windows Job Object that kills every process assigned to it the moment its last
/// handle closes. The clipper holds the only handle, so when the clipper dies (exit,
/// crash, killed from the task manager) Windows takes every ffmpeg it started down with
/// it instead of leaving an orphan encoding into a half-written file.
/// </summary>
internal sealed class KillOnCloseJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    public sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(true) { }
        protected override bool ReleaseHandle() => Kernel32.CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeJobHandle hJob, int infoClass, IntPtr lpInfo, uint cbInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle hJob, SafeHandle hProcess);

    private readonly SafeJobHandle _handle;

    private KillOnCloseJob(SafeJobHandle handle) => _handle = handle;

    /// <summary>Creates the job, or returns null if Windows refuses (then jobs simply run unguarded).</summary>
    public static KillOnCloseJob? TryCreate()
    {
        SafeJobHandle? handle = null;
        try
        {
            handle = CreateJobObjectW(IntPtr.Zero, null);
            if (handle.IsInvalid) { handle.Dispose(); return null; }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

            int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
                {
                    handle.Dispose();
                    return null;
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }

            return new KillOnCloseJob(handle);
        }
        catch
        {
            handle?.Dispose();
            return null;
        }
    }

    /// <summary>Puts a running process into the job. False if it already exited or Windows refused.</summary>
    public bool TryAssign(Process process)
    {
        try { return AssignProcessToJobObject(_handle, process.SafeHandle); }
        catch { return false; }
    }

    /// <summary>Closing the handle is what kills the remaining members.</summary>
    public void Dispose() => _handle.Dispose();
}
