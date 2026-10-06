using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using WKI_Clipper.Native;

namespace WKI_Clipper.Services;

/// <summary>
/// Decides which game a capture belongs to, at the moment it is taken.
///
/// Source of truth, in order:
///  1. A window pinned by the capture plan (WGC window capture) — the footage shows that
///     window no matter what is in front, so it names the folder.
///  2. The foreground window at hotkey time. Hotkeys never take focus, so while playing
///     this is the game. When the foreground is the clipper itself (clicking in the widget
///     board), the last EXTERNAL foreground window is used instead.
///  3. Shell, desktop and system processes go to "Desktop".
///
/// The exe path is read with PROCESS_QUERY_LIMITED_INFORMATION only — no handle with
/// memory-read rights is ever opened on a game process (anti-cheat), and the version
/// metadata comes from the file on disk, not from the running process.
/// </summary>
public sealed class GameContext
{
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private readonly Func<int?> _lastExternalForegroundPid;
    /// <summary>exe path → folder name. Version resources do not change while the app runs.</summary>
    private readonly ConcurrentDictionary<string, string> _byExe = new(StringComparer.OrdinalIgnoreCase);

    public GameContext(Func<int?> lastExternalForegroundPid)
    {
        _lastExternalForegroundPid = lastExternalForegroundPid;
    }

    /// <summary>Folder name for what a capture taken right now shows. Never throws.</summary>
    public string ResolveFolder(CaptureTargetResolver.CapturePlan? plan)
    {
        try
        {
            int? pid = PidOfPinnedWindow(plan) ?? ForegroundPid();
            string folder = pid is { } p ? FolderForPid(p) : GameFolderNaming.DesktopFolder;
            Logger.Info($"Game folder resolved: '{folder}' (pid={pid?.ToString() ?? "none"})");
            return folder;
        }
        catch (Exception ex)
        {
            Logger.Warn("Game folder resolution failed: " + ex.Message);
            return GameFolderNaming.UnknownFolder;
        }
    }

    private static int? PidOfPinnedWindow(CaptureTargetResolver.CapturePlan? plan)
    {
        if (plan is not { UseWgc: true } p || p.Hwnd == IntPtr.Zero || !User32.IsWindow(p.Hwnd)) return null;
        User32.GetWindowThreadProcessId(p.Hwnd, out uint pid);
        return pid == 0 ? null : (int)pid;
    }

    private int? ForegroundPid()
    {
        var hwnd = User32.GetForegroundWindow();
        if (hwnd != IntPtr.Zero)
        {
            User32.GetWindowThreadProcessId(hwnd, out uint upid);
            int pid = (int)upid;
            if (pid != 0 && pid != Environment.ProcessId) return pid;
        }
        // Our own window is in front (widget board) — the game is the last one before it.
        return _lastExternalForegroundPid();
    }

    private string FolderForPid(int pid)
    {
        string processName;
        try
        {
            using var p = Process.GetProcessById(pid);
            processName = p.ProcessName;
        }
        catch { return GameFolderNaming.DesktopFolder; }   // process already gone

        // Shell / desktop / system UI: not a game, one shared folder.
        if (!CaptureTargetResolver.IsCouplableApp(processName)) return GameFolderNaming.DesktopFolder;

        var exe = ImagePath(pid);
        if (exe is null) return GameFolderNaming.FromMetadata(null, null, processName);

        // Settings, input hosts, frame hosts … — Windows' own UI, wherever it is in front.
        if (GameFolderNaming.IsWindowsSystemImage(exe, WindowsDir)) return GameFolderNaming.DesktopFolder;

        return _byExe.GetOrAdd(exe, path =>
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                return GameFolderNaming.FromMetadata(info.ProductName, info.FileDescription, processName);
            }
            catch { return GameFolderNaming.FromMetadata(null, null, processName); }
        });
    }

    private static string? ImagePath(int pid)
    {
        var h = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return Kernel32.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, (int)size) : null;
        }
        finally { Kernel32.CloseHandle(h); }
    }
}
