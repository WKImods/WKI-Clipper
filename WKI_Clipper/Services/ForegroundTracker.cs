using System;
using System.Diagnostics;
using WKI_Clipper.Models;
using WKI_Clipper.Native;
using Screen = System.Windows.Forms.Screen;

namespace WKI_Clipper.Services;

/// <summary>
/// Event-based foreground tracking (SetWinEventHook, no polling) for the AUTO
/// capture mode. Decides when the pinned auto-target is stale and requests a
/// re-pin:
///  - nothing real is pinned yet (or the pinned process died) and a real app
///    comes to the foreground, or
///  - a NEWLY LAUNCHED app (started after the current pin) takes the foreground
///    AND is (near-)fullscreen — i.e. "the game just started".
/// Plain alt-tab between long-running apps (e.g. game → Discord) never re-pins:
/// the clip stays on the game (explicit user requirement). Launching a small
/// helper app mid-game doesn't steal the pin either (fullscreen check).
///
/// Must be started on a thread with a message pump (the WPF UI thread) —
/// WINEVENT_OUTOFCONTEXT delivers callbacks via that thread's queue.
/// </summary>
public sealed class ForegroundTracker : IDisposable
{
    private readonly SettingsService _settings;
    private IntPtr _hook;
    // Field keeps the delegate alive — the native hook holds no GC reference.
    private User32.WinEventDelegate? _callback;
    private int? _pinnedPid;
    private DateTime _pinnedAtUtc = DateTime.UtcNow;

    /// <summary>A better auto-target appeared — consumer should re-pin (restart the buffer). Arg: process name.</summary>
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
            DateTime startedUtc;
            try
            {
                using var p = Process.GetProcessById(pid);
                name = p.ProcessName;
                startedUtc = SafeStartUtc(p);
            }
            catch { return; }

            if (!CaptureTargetResolver.IsCouplableApp(name)) return; // shell/system
            if (_pinnedPid == pid) return;                           // already the target

            bool pinnedAlive = false;
            if (_pinnedPid is int pp)
            {
                try { using var cur = Process.GetProcessById(pp); pinnedAlive = !cur.HasExited; }
                catch { /* exited */ }
            }

            bool newlyLaunched = startedUtc > _pinnedAtUtc;
            bool shouldRepin = !pinnedAlive || (newlyLaunched && IsNearFullscreen(hwnd));
            if (!shouldRepin) return;

            _pinnedPid = pid;
            _pinnedAtUtc = DateTime.UtcNow;
            Logger.Info($"ForegroundTracker: re-pin auto target → {name} (PID {pid}, newlyLaunched={newlyLaunched}, pinnedAlive={pinnedAlive})");
            RetargetRequested?.Invoke(name!);
        }
        catch (Exception ex)
        {
            Logger.Error("ForegroundTracker callback failed", ex);
        }
    }

    /// <summary>Window covers ≥90% of its monitor (fullscreen/borderless/maximized).</summary>
    private static bool IsNearFullscreen(IntPtr hwnd)
    {
        try
        {
            if (!User32.GetWindowRect(hwnd, out var r)) return false;
            var screen = Screen.FromHandle(hwnd);
            long monitorArea = (long)screen.Bounds.Width * screen.Bounds.Height;
            long windowArea = (long)r.Width * r.Height;
            return monitorArea > 0 && windowArea >= monitorArea * 9 / 10;
        }
        catch { return false; }
    }

    private static DateTime SafeStartUtc(Process p)
    {
        // Process.StartTime is LOCAL time — normalize before comparing with UtcNow.
        try { return p.StartTime.ToUniversalTime(); }
        catch { return DateTime.MinValue; }
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
