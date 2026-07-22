using System;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using WKI_Clipper.Models;
using WKI_Clipper.Native;

namespace WKI_Clipper.Services;

/// <summary>
/// The single source of truth for "what gets clipped". Given a <see cref="CaptureProfile"/>
/// and the audio settings it resolves ONE plan — which monitor to capture and which
/// audio source to use — that the replay buffer, manual recording, screenshots and
/// the status UI all consume identically. This is what keeps F9, Ctrl+F9 and F10 in
/// lock-step instead of each guessing its own target.
///
/// Phase 1 is monitor-based: even "window" targets resolve to the window's MONITOR
/// (ddagrab output index), which is alt-tab-stable and can capture exclusive
/// fullscreen. Occlusion-proof per-window capture (staying on the game while another
/// window covers it) is the Phase 2 WGC job.
/// </summary>
public static class CaptureTargetResolver
{
    public readonly record struct CapturePlan(
        int MonitorIndex,
        string MonitorDevice,
        int MonitorWidth,
        int MonitorHeight,
        SystemAudioMode SysMode,
        int? AudioPid,
        string? TargetProcess,
        IntPtr Hwnd,
        string VideoLabel,
        string AudioLabel,
        bool UseWgc);

    private static int SelfPid => Environment.ProcessId;

    /// <summary>A pickable window: its process name (the stable id) + current title.</summary>
    public readonly record struct WindowEntry(string ProcessName, string Title);

    /// <summary>
    /// Lists distinct top-level windowed processes for the shared window picker
    /// (excludes our own process and shell/system processes).
    /// </summary>
    public static System.Collections.Generic.List<WindowEntry> ListWindowedProcesses()
    {
        var result = new System.Collections.Generic.List<WindowEntry>();
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    if (p.Id == SelfPid) continue;
                    if (NonCouplable.Contains(p.ProcessName)) continue;
                    var title = p.MainWindowTitle;
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    if (seen.Add(p.ProcessName))
                        result.Add(new WindowEntry(p.ProcessName, title));
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return result.OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static CapturePlan Resolve(CaptureProfile profile, AppSettings settings)
    {
        var (hwnd, videoPid, appName) = ResolveApp(profile);

        int idx;
        Screen screen;
        if (profile.Mode == CaptureMode.Monitor)
            (idx, screen) = MonitorByDevice(profile.MonitorDeviceName);
        else if (hwnd != IntPtr.Zero)
            (idx, screen) = MonitorForHwnd(hwnd);
        else
            (idx, screen) = PrimaryMonitor();

        // --- Audio route ---
        SystemAudioMode sysMode;
        int? audioPid = null;
        string? audioName = null;

        if (profile.CoupleAudio && videoPid.HasValue && IsCouplableApp(appName))
        {
            // Ton folgt dem Video-Ziel (aber nie an Desktop/Shell koppeln, sonst
            // wäre ein Clip im Leerlauf stumm).
            sysMode = SystemAudioMode.Process;
            audioPid = videoPid;
            audioName = appName;
        }
        else if (settings.Audio.SystemCaptureMode == AudioCaptureMode.GameOnly)
        {
            // Legacy explicit game-only audio, independent of the video target and
            // — the #14 fix — independent of the "System-Sound" checkbox.
            var (gpid, gname) = ResolveGamePid(settings.Audio.GameProcessName);
            if (gpid.HasValue)
            {
                sysMode = SystemAudioMode.Process;
                audioPid = gpid;
                audioName = gname;
            }
            else
            {
                sysMode = settings.Audio.RecordSystemSound ? SystemAudioMode.AllAudio : SystemAudioMode.None;
            }
        }
        else
        {
            sysMode = settings.Audio.RecordSystemSound ? SystemAudioMode.AllAudio : SystemAudioMode.None;
        }

        // Occlusion-proof per-window capture: Auto/Window targets with a live
        // window use WGC (the clip stays on the game even when another window
        // covers it). Monitor mode and window-less targets stay on ddagrab.
        bool useWgc = profile.Mode != CaptureMode.Monitor
                      && hwnd != IntPtr.Zero
                      && WgcWindowCapture.IsSupported
                      && !WgcWindowCapture.IsBlocked(hwnd);

        string videoLabel = BuildVideoLabel(profile, idx, screen, appName, hwnd, useWgc);
        string audioLabel = BuildAudioLabel(sysMode, audioName, settings);

        return new CapturePlan(idx, screen.DeviceName, screen.Bounds.Width, screen.Bounds.Height,
            sysMode, audioPid, appName, hwnd, videoLabel, audioLabel, useWgc);
    }

    /// <summary>
    /// Plan for a MANUAL recording (Strg+F9). In Auto mode the recording is a
    /// freecam screen recording: it captures the pinned target's MONITOR — the
    /// video follows every window the user focuses there — and hears everything
    /// (a moving video with single-app audio would be inconsistent). Window and
    /// Monitor modes behave exactly like <see cref="Resolve"/> (Window = pinned,
    /// occlusion-proof WGC).
    /// </summary>
    public static CapturePlan ResolveForManualRecording(CaptureProfile profile, AppSettings settings)
    {
        var plan = Resolve(profile, settings);
        if (profile.Mode != CaptureMode.Auto) return plan;

        var sysMode = settings.Audio.RecordSystemSound ? SystemAudioMode.AllAudio : SystemAudioMode.None;
        string video = $"Freecam: Monitor {plan.MonitorIndex + 1} ({plan.MonitorWidth}×{plan.MonitorHeight}) — folgt deinen Fenstern";
        string audio = BuildAudioLabel(sysMode, null, settings);
        return plan with { UseWgc = false, SysMode = sysMode, AudioPid = null, VideoLabel = video, AudioLabel = audio };
    }

    // -------- app (window/pid) resolution --------

    private static (IntPtr hwnd, int? pid, string? name) ResolveApp(CaptureProfile profile) => profile.Mode switch
    {
        CaptureMode.Window => ResolveByProcessName(profile.TargetProcessName),
        CaptureMode.Auto => ResolveForeground(),
        _ => (IntPtr.Zero, null, null) // Monitor: no specific app
    };

    private static (IntPtr, int?, string?) ResolveForeground()
    {
        var hwnd = User32.GetForegroundWindow();
        if (hwnd != IntPtr.Zero)
        {
            User32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != 0 && (int)pid != SelfPid)
                return (hwnd, (int)pid, ProcName((int)pid));
        }

        // Foreground is our own overlay (or nothing) → take the topmost other window.
        var alt = FindTopNonSelfWindow();
        if (alt != IntPtr.Zero)
        {
            User32.GetWindowThreadProcessId(alt, out uint pid2);
            if (pid2 != 0)
                return (alt, (int)pid2, ProcName((int)pid2));
        }
        return (IntPtr.Zero, null, null);
    }

    private static IntPtr FindTopNonSelfWindow()
    {
        IntPtr found = IntPtr.Zero;
        try
        {
            User32.EnumWindows((h, _) =>
            {
                if (!User32.IsWindowVisible(h)) return true;
                if (User32.IsIconic(h)) return true;
                if (User32.GetWindowTextLength(h) == 0) return true;
                User32.GetWindowThreadProcessId(h, out uint pid);
                if (pid == 0 || (int)pid == SelfPid) return true;
                found = h;
                return false; // stop at the first (topmost in Z-order) match
            }, IntPtr.Zero);
        }
        catch { }
        return found;
    }

    private static (IntPtr, int?, string?) ResolveByProcessName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return ResolveForeground();
        try
        {
            var procs = Process.GetProcessesByName(name)
                .OrderBy(SafeStartTime)
                .ToList();
            try
            {
                var withWin = procs.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
                if (withWin != null)
                    return (withWin.MainWindowHandle, withWin.Id, name);
                if (procs.Count > 0)
                    return (IntPtr.Zero, procs[0].Id, name); // running but no window → audio still works
            }
            finally { foreach (var p in procs) p.Dispose(); }
        }
        catch { }
        return (IntPtr.Zero, null, name);
    }

    private static (int?, string?) ResolveGamePid(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            var (_, pid, pname) = ResolveForeground();
            return (pid, pname);
        }
        try
        {
            var procs = Process.GetProcessesByName(name).OrderBy(SafeStartTime).ToList();
            try
            {
                if (procs.Count > 0) return (procs[0].Id, name);
            }
            finally { foreach (var p in procs) p.Dispose(); }
        }
        catch { }
        return (null, name);
    }

    // -------- monitor mapping --------
    // NOTE: ddagrab output_idx is the DXGI output index. We map via WinForms
    // Screen order, which matches DXGI output order on typical single-GPU setups.
    // A precise DXGI enumeration is a Phase 2 refinement.

    private static (int, Screen) MonitorForHwnd(IntPtr hwnd)
    {
        var screen = Screen.FromHandle(hwnd);
        int i = IndexOfScreen(screen);
        return (i, screen);
    }

    private static (int, Screen) MonitorByDevice(string? device)
    {
        var screens = Screen.AllScreens;
        if (!string.IsNullOrEmpty(device))
        {
            int i = Array.FindIndex(screens, s => s.DeviceName == device);
            if (i >= 0) return (i, screens[i]);
        }
        return PrimaryMonitor();
    }

    private static (int, Screen) PrimaryMonitor()
    {
        var screens = Screen.AllScreens;
        var primary = Screen.PrimaryScreen ?? (screens.Length > 0 ? screens[0] : null);
        if (primary == null) return (0, screens.Length > 0 ? screens[0] : Screen.AllScreens[0]);
        int i = IndexOfScreen(primary);
        return (i, primary);
    }

    private static int IndexOfScreen(Screen screen)
    {
        var screens = Screen.AllScreens;
        int i = Array.FindIndex(screens, s => s.DeviceName == screen.DeviceName);
        return i < 0 ? 0 : i;
    }

    // -------- labels for honest UI --------

    private static string BuildVideoLabel(CaptureProfile profile, int idx, Screen screen, string? appName, IntPtr hwnd, bool useWgc)
    {
        string mon = $"Monitor {idx + 1} ({screen.Bounds.Width}×{screen.Bounds.Height})";
        if (useWgc && appName != null)
        {
            // Per-window WGC: the window itself is captured, occlusion-proof.
            return profile.Mode == CaptureMode.Auto
                ? $"Automatik: {appName} — Fenster (verdeckungssicher)"
                : $"{appName} — Fenster (verdeckungssicher)";
        }
        return profile.Mode switch
        {
            CaptureMode.Monitor => mon,
            CaptureMode.Window => appName != null && hwnd != IntPtr.Zero
                ? $"{appName} → {mon}"
                : appName != null
                    ? $"{appName} (kein Fenster) → {mon}"
                    : $"Kein Fenster gewählt → {mon}",
            _ => appName != null
                ? $"Automatik: {appName} → {mon}"
                : $"Automatik: {mon}"
        };
    }

    private static string BuildAudioLabel(SystemAudioMode sysMode, string? audioName, AppSettings settings)
    {
        bool mic = settings.Audio.RecordMicrophone;
        string micPart = mic ? " + Mikro" : "";
        return sysMode switch
        {
            SystemAudioMode.Process => $"Nur {audioName ?? "Spiel"}{micPart}",
            SystemAudioMode.AllAudio => $"Alle Sounds{micPart}",
            _ => mic ? "Nur Mikro" : "Kein Ton"
        };
    }

    // Shell / system processes we must never couple audio to (a clip would be
    // silent). Anything else is fair game as a coupled audio source.
    private static readonly System.Collections.Generic.HashSet<string> NonCouplable =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "applicationframehost", "shellexperiencehost", "searchhost",
            "searchapp", "startmenuexperiencehost", "textinputhost", "sihost", "dwm",
            "lockapp", "wki_clipper"
        };

    /// <summary>True for real apps; false for shell/system processes (and us).</summary>
    public static bool IsCouplableApp(string? name)
        => !string.IsNullOrEmpty(name) && !NonCouplable.Contains(name);

    private static DateTime SafeStartTime(Process p)
    {
        try { return p.StartTime; } catch { return DateTime.MaxValue; }
    }

    private static string? ProcName(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return null; }
    }
}
