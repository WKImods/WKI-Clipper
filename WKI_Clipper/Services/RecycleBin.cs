using System;
using System.IO;
using WKI_Clipper.Native;

namespace WKI_Clipper.Services;

public enum RecycleOutcome { Recycled, InUse, Declined, NotSupported, Failed }

/// <summary>
/// Moves a file into the Windows Recycle Bin — and nothing else. A clip can be evidence; "delete" here always
/// means "restorable". Never File.Delete: when Windows cannot recycle (file too big for the bin, a drive without
/// one) it asks the user (FOF_WANTNUKEWARNING) instead of deleting for good, and "no" keeps the file.
/// </summary>
public static class RecycleBin
{
    /// <summary>A path on a local drive letter, not a network path (UNC, mapped network drive).</summary>
    internal static bool IsLocalDrivePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false;   // UNC, "\\?\UNC\...", "\\?\C:\..." (no recycling there)
        try
        {
            if (!Path.IsPathFullyQualified(path)) return false;
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':') return false;
            return new DriveInfo(root).DriveType is not (DriveType.Network or DriveType.NoRootDirectory or DriveType.Unknown);
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
    /// Sends the file to the Recycle Bin without a progress or confirmation dialog (Windows may still show its
    /// "cannot be recycled — delete permanently?" question). Returns instead of throwing.
    /// </summary>
    public static RecycleOutcome Send(string path)
    {
        try
        {
            if (!IsSupportedPath(path)) return RecycleOutcome.NotSupported;
            var full = Path.GetFullPath(path);

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
            var outcome = Interpret(rc, op.fAnyOperationsAborted != 0, File.Exists(full));
            Logger.Info($"Recycle bin: '{full}' -> {outcome} (rc={rc})");
            return outcome;
        }
        catch (Exception ex)
        {
            Logger.Warn("Recycle bin failed: " + ex.Message);
            return RecycleOutcome.Failed;
        }
    }

    internal static RecycleOutcome Interpret(int rc, bool aborted, bool existsAfter)
    {
        if (rc == Shell32.ERROR_SHARING_VIOLATION) return RecycleOutcome.InUse;
        if (rc == 0 && aborted) return RecycleOutcome.Declined;           // the user said no to "delete permanently?"
        if (rc == 0 && !existsAfter) return RecycleOutcome.Recycled;
        return RecycleOutcome.Failed;                                      // includes rc == 0 with the file still there
    }
}
