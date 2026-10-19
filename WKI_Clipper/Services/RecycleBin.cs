using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using WKI_Clipper.Native;

namespace WKI_Clipper.Services;

/// <param name="DeletedPermanently">The file is gone but did not land in the Recycle Bin (the user confirmed Windows' "delete permanently?").</param>
public enum RecycleOutcome { Recycled, InUse, Declined, NotSupported, Failed, DeletedPermanently }

/// <summary>
/// Moves a file into the Windows Recycle Bin — and nothing else. A clip can be evidence; "delete" here always
/// means "restorable". Never File.Delete. Where Windows could not recycle (a drive without a bin, the bin switched
/// off for it, a file bigger than the bin) the move is refused up front, so nothing is deleted for good. What cannot
/// be predicted still makes Windows ask (FOF_WANTNUKEWARNING): "no" keeps the file, and a "yes" is reported as
/// <see cref="RecycleOutcome.DeletedPermanently"/> — found by counting the bin before and after, never assumed.
/// </summary>
public static class RecycleBin
{
    private const string VolumeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\";
    private const string PolicyKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";

    /// <summary>What the shell returned for the delete: the return code and whether the user said no.</summary>
    internal readonly record struct ShellDeleteResult(int ReturnCode, bool Aborted);

    /// <summary>The settings of one drive's bin: its size limit in MB (null = none known) and "delete at once" mode.</summary>
    internal readonly record struct VolumeSettings(long? MaxCapacityMegabytes, bool NukeOnDelete);

    /// <summary>Removable drives (USB sticks), optical drives and RAM disks have no bin; network drives are not recycled either.</summary>
    internal static bool DriveHasRecycleBin(DriveType type) => type == DriveType.Fixed;

    /// <summary>A path on a local drive that has a Recycle Bin, not a network path (UNC, mapped network drive).</summary>
    internal static bool IsLocalDrivePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false;   // UNC, "\\?\UNC\...", "\\?\C:\..." (no recycling there)
        try
        {
            if (!Path.IsPathFullyQualified(path)) return false;
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':') return false;
            return DriveHasRecycleBin(new DriveInfo(root).DriveType);
        }
        catch { return false; }
    }

    /// <summary>A local drive path to a file that exists.</summary>
    public static bool IsSupportedPath(string path)
    {
        try { return IsLocalDrivePath(path) && File.Exists(path); }
        catch { return false; }
    }

    /// <summary>
    /// False when Windows would delete the file for good instead of recycling it: the bin is switched off for the drive
    /// ("Don't move files to the Recycle Bin") or the file is bigger than the bin may hold. A limit that is not known
    /// (or 0) does not refuse anything — the outcome check after the move still tells the truth.
    /// </summary>
    internal static bool FitsInBin(long fileBytes, long? maxCapacityMegabytes, bool nukeOnDelete)
    {
        if (nukeOnDelete) return false;
        if (maxCapacityMegabytes is not > 0) return true;
        return fileBytes <= maxCapacityMegabytes.Value * 1024 * 1024;
    }

    /// <summary>"{guid}" out of a volume name like "\\?\Volume{guid}\"; null when there is none.</summary>
    internal static string? VolumeGuidOf(string? volumeName)
    {
        if (string.IsNullOrEmpty(volumeName)) return null;
        int open = volumeName.IndexOf("Volume{", StringComparison.OrdinalIgnoreCase);
        if (open < 0) return null;
        int start = open + "Volume".Length;
        int close = volumeName.IndexOf('}', start);
        return close < 0 ? null : volumeName.Substring(start, close - start + 1);
    }

    /// <summary>The bin settings of a drive root like "C:\" (per volume under HKCU, plus the "do not recycle" policy). Never throws.</summary>
    internal static VolumeSettings ReadVolumeSettings(string root)
    {
        try
        {
            bool nuke = PolicyForbidsRecycling();
            long? max = null;

            var name = new StringBuilder(64);
            if (Kernel32.GetVolumeNameForVolumeMountPointW(root.EndsWith('\\') ? root : root + @"\", name, (uint)name.Capacity)
                && VolumeGuidOf(name.ToString()) is { } guid)
            {
                using var key = Registry.CurrentUser.OpenSubKey(VolumeKeyPath + guid);
                if (key?.GetValue("NukeOnDelete") is int n && n != 0) nuke = true;
                if (key?.GetValue("MaxCapacity") is int m && m > 0) max = m;
            }
            return new VolumeSettings(max, nuke);
        }
        catch { return new VolumeSettings(null, false); }
    }

    private static bool PolicyForbidsRecycling()
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(PolicyKeyPath);
            if (key?.GetValue("NoRecycleFiles") is int v && v != 0) return true;
        }
        return false;
    }

    /// <summary>The number of items in the current user's bin on that drive; null when the drive has no bin or it cannot be read.</summary>
    internal static long? BinItemCount(string root)
    {
        var info = new Shell32.SHQUERYRBINFO { cbSize = Marshal.SizeOf<Shell32.SHQUERYRBINFO>() };
        return Shell32.SHQueryRecycleBinW(root, ref info) == 0 ? info.i64NumItems : null;
    }

    private static ShellDeleteResult ShellRecycle(string full)
    {
        var op = new Shell32.SHFILEOPSTRUCTW
        {
            hwnd = IntPtr.Zero,
            wFunc = Shell32.FO_DELETE,
            pFrom = full + "\0",
            pTo = null,
            fFlags = (ushort)(Shell32.FOF_ALLOWUNDO | Shell32.FOF_NOCONFIRMATION | Shell32.FOF_SILENT
                              | Shell32.FOF_NOERRORUI | Shell32.FOF_WANTNUKEWARNING)
        };
        int rc = Shell32.SHFileOperationW(ref op);
        return new ShellDeleteResult(rc, op.fAnyOperationsAborted != 0);
    }

    /// <summary>
    /// Sends the file to the Recycle Bin without a progress or confirmation dialog (Windows may still show its
    /// "cannot be recycled — delete permanently?" question). Returns instead of throwing.
    /// </summary>
    public static RecycleOutcome Send(string path) => Send(path, BinItemCount, ShellRecycle, ReadVolumeSettings);

    /// <summary>The steps of <see cref="Send(string)"/> with the shell and the registry replaceable (tests).</summary>
    internal static RecycleOutcome Send(string path, Func<string, long?> binItemCount,
        Func<string, ShellDeleteResult> shellDelete, Func<string, VolumeSettings> volumeSettings)
    {
        try
        {
            if (!IsSupportedPath(path)) return RecycleOutcome.NotSupported;
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full)!;

            // Refuse what Windows would delete for good: it would only ask, and a "Yes" cannot be taken back.
            var settings = volumeSettings(root);
            if (!FitsInBin(new FileInfo(full).Length, settings.MaxCapacityMegabytes, settings.NukeOnDelete))
            {
                Logger.Info($"Recycle bin: '{full}' not moved - the bin of {root} is off or too small for it");
                return RecycleOutcome.NotSupported;
            }
            if (binItemCount(root) is not { } before)
            {
                Logger.Info($"Recycle bin: '{full}' not moved - {root} has no readable Recycle Bin");
                return RecycleOutcome.NotSupported;
            }

            var shell = shellDelete(full);
            bool gone = !File.Exists(full);

            // Recycled or deleted for good both leave rc 0 and no file: only the bin tells them apart.
            bool? binGrew = null;
            if (shell.ReturnCode == 0 && !shell.Aborted && gone)
                binGrew = binItemCount(root) is { } after ? after > before : null;

            var outcome = Interpret(shell.ReturnCode, shell.Aborted, !gone, binGrew);
            Logger.Info($"Recycle bin: '{full}' -> {outcome} (rc={shell.ReturnCode})");
            return outcome;
        }
        catch (Exception ex)
        {
            Logger.Warn("Recycle bin failed: " + ex.Message);
            return RecycleOutcome.Failed;
        }
    }

    /// <param name="binGrew">Whether the bin holds more items than before; null = could not be counted. Only a file that is
    /// gone with the bin grown counts as recycled — gone with the bin the same (or unreadable) was deleted for good.</param>
    internal static RecycleOutcome Interpret(int rc, bool aborted, bool existsAfter, bool? binGrew)
    {
        if (rc == Shell32.ERROR_SHARING_VIOLATION) return RecycleOutcome.InUse;
        if (rc == 0 && aborted) return RecycleOutcome.Declined;           // the user said no to "delete permanently?"
        if (rc == 0 && !existsAfter) return binGrew == true ? RecycleOutcome.Recycled : RecycleOutcome.DeletedPermanently;
        return RecycleOutcome.Failed;                                      // includes rc == 0 with the file still there
    }
}
