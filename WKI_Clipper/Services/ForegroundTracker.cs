using System;
using System.Diagnostics;
using WKI_Clipper.Models;
using WKI_Clipper.Native;

namespace WKI_Clipper.Services;

/// <summary>
/// Event-based foreground tracking (SetWinEventHook, no polling) for the AUTO
/// capture mode — the "freecam": whatever real app window the user focuses
/// becomes the capture target. A short dwell (~1.5 s) confirms the focus is
/// deliberate so rapid alt-tab cycling doesn't churn the pipeline. Shell/system
/// windows and our own overlay never become targets.
///
/// Running captures are unaffected by design: a Ctrl+F9 recording keeps its own
/// pinned WGC session, and F9 clips only ever contain the currently pinned
/// window (per-generation identity filtering in the buffer).
///
/// Must be started on a thread with a message pump (the WPF UI thread) —
/// WINEVENT_OUTOFCONTEXT delivers callbacks via that thread's queue.
/// </summary>
public sealed class ForegroundTracker : IDisposable
{
    private const int DwellMs = 1500;

    private readonly SettingsService _settings;
    private IntPtr _hook;
    // Field keeps the delegate alive — the native hook holds no GC reference.
    private User32.WinEventDelegate? _callback;
    private int? _pinnedPid;
    private int _candidateSeq;

    /// <summary>The auto-target changed — consumer should re-pin the buffer. Arg: process name.</summary>
    public event Action<string>? RetargetRequested;

    public ForegroundTracker(SettingsService settings)
    {
        _settings = settings;
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _callback = OnWinEvent;
        _hook = User32.SetWinEventHook(
            User32.EVENT_SYSTEM_FOREGROUND, User32.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _callback, 0, 0,
            User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS);
        Logger.Info(_hook != IntPtr.Zero
            ? "ForegroundTracker: WinEvent hook installed"
            : "ForegroundTracker: SetWinEventHook FAILED — auto re-pin disabled");
    }

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (_settings.Current.Capture.Mode != CaptureMode.Auto) return;
            if (hwnd == IntPtr.Zero) return;

            User32.GetWindowThreadProcessId(hwnd, out uint upid);
            int pid = (int)upid;
            if (pid == 0 || pid == Environment.ProcessId) return;
            if (User32.GetWindowTextLength(hwnd) == 0) return;

            string? name;
            try
            {
                using var p = Process.GetProcessById(pid);
                name = p.ProcessName;
            }
            catch { return; }

            if (!CaptureTargetResolver.IsCouplableApp(name)) return; // shell/system
            if (_pinnedPid == pid) return;                           // already the target

            // Freecam with dwell: confirm the focus survives DwellMs before
            // re-pinning, so alt-tab cycling doesn't churn the capture pipeline.
            int seq = ++_candidateSeq;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(DwellMs).ConfigureAwait(false);
                if (seq != _candidateSeq) return;                         // newer candidate took over
                if (_settings.Current.Capture.Mode != CaptureMode.Auto) return;
                if (User32.GetForegroundWindow() != hwnd) return;         // focus moved on
                if (!User32.IsWindow(hwnd)) return;
                if (_pinnedPid == pid) return;

                _pinnedPid = pid;
                Logger.Info($"ForegroundTracker: Freecam re-pin → {name} (PID {pid})");
                RetargetRequested?.Invoke(name!);
            });
        }
        catch (Exception ex)
        {
            Logger.Error("ForegroundTracker callback failed", ex);
        }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            try { User32.UnhookWinEvent(_hook); } catch { }
            _hook = IntPtr.Zero;
        }
        _callback = null;
    }
}
