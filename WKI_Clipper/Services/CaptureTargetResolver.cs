using System;
using System.Diagnostics;
using System.Linq;
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
/// Every target resolves to a MONITOR as Windows/DXGI name it (<see cref="DisplayTopology"/>): Monitor mode
/// finds the saved monitor by its identity (<see cref="MonitorTargetRules"/>), Auto/Window take the monitor
/// the window is on (HMONITOR → topology). The capture index is always that monitor's DXGI output on the
/// default adapter - never a position in a list. Window targets are recorded occlusion-proof through WGC
/// when possible; the monitor is then only the fallback and the size estimate.
/// </summary>
public static class CaptureTargetResolver
{
    /// <param name="Source">The monitor ddagrab records: its DXGI output on the default adapter, size, label and how it
    /// was found. With WGC it is the monitor the window is on.</param>
    /// <param name="Warning">User text when the monitor that should be recorded cannot be and the primary monitor is
    /// recorded instead (the chosen monitor is missing or hangs on another GPU); null otherwise.</param>
    public readonly record struct CapturePlan(
        CaptureSource Source,
        SystemAudioMode SysMode,
        int? AudioPid,
        string? TargetProcess,
        IntPtr Hwnd,
        string VideoLabel,
        string AudioLabel,
        bool UseWgc,
        string? Warning);

    /// <summary>
    /// The chosen monitor was found by an older or incomplete identity (a 0.19 GDI name, a new port, a new GDI slot): the
    /// settings should store this one instead. Raised on the resolving thread, by every resolve that finds it so, until the
    /// settings carry it. The app writes it without restarting the buffer - what is recorded stays the same.
    /// </summary>
    public static event Action<MonitorRef>? MonitorRefUpgrade;

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

    /// <summary>
    /// The plan for <paramref name="profile"/> on the current display topology (<paramref name="topology"/>, default
    /// <see cref="DisplayTopologyCache.Shared"/>: cheap while nothing changed, so the views may call this every second).
    /// </summary>
    /// <exception cref="InvalidOperationException">No monitor the capture can reach (the message says why).</exception>
    public static CapturePlan Resolve(CaptureProfile profile, AppSettings settings, DisplayTopologyCache? topology = null)
    {
        var topo = (topology ?? DisplayTopologyCache.Shared).Get();
        var (hwnd, videoPid, appName) = ResolveApp(profile);

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

        var video = ResolveVideo(profile, appName, hwnd, MonitorOf(hwnd), useWgc, topo);
        string audioLabel = BuildAudioLabel(sysMode, audioName, settings);

        return new CapturePlan(video.Source, sysMode, audioPid, appName, hwnd, video.Label, audioLabel, useWgc, video.Warning);
    }

    /// <summary>
    /// The video half of a plan - source, label, warning - with the window's monitor passed in as a handle
    /// (<paramref name="windowMonitor"/>, 0 = no window). Raises <see cref="MonitorRefUpgrade"/> when the saved monitor
    /// asks for it.
    /// </summary>
    internal static (CaptureSource Source, string Label, string? Warning) ResolveVideo(CaptureProfile profile, string? appName,
        IntPtr hwnd, nint windowMonitor, bool useWgc, DisplayTopology topo)
    {
        MonitorRef? saved = null;
        MonitorResolution resolution;
        if (profile.Mode == CaptureMode.Monitor)
        {
            saved = MonitorRef.FromProfile(profile);
            resolution = MonitorTargetRules.Resolve(saved, topo);
        }
        else
        {
            resolution = FollowWindow(windowMonitor, topo);
        }
        var source = SourceOf(resolution, topo);

        if (resolution.UpgradeSavedRef)
            RaiseUpgrade(MonitorRef.Of(resolution.Display!));

        string monitor = !resolution.IsFallback ? source.Label
            : saved is not null ? MonitorLabels.Fallback(source.Label, saved)
            : L.T($"Ersatz: {source.Label}", $"Fallback: {source.Label}");
        // With WGC the window itself is recorded; the fallback monitor would only be the size estimate.
        string? warning = resolution.IsFallback && !useWgc ? FallbackWarning(profile.Mode, resolution) : null;
        return (source, BuildVideoLabel(profile, monitor, appName, hwnd, useWgc), warning);
    }

    private static MonitorResolution FollowWindow(nint windowMonitor, DisplayTopology topo)
    {
        var display = topo.ByHMonitor(windowMonitor);
        string reason = windowMonitor == 0 ? L.T("kein Fenster", "no window")
            : display is null ? L.T("Monitor des Fensters gerade unbekannt", "the window's monitor is not known right now")
            : L.T("Monitor des Fensters", "monitor of the window");
        return MonitorTargetRules.Follow(display, topo, reason);
    }

    /// <summary>The source of a resolved monitor; throws for <see cref="MonitorResolutionStatus.NoDisplays"/>.</summary>
    private static CaptureSource SourceOf(MonitorResolution resolution, DisplayTopology topo)
    {
        string noMonitor = L.T($"Kein Monitor, den die Aufnahme erreicht: {resolution.Reason}",
                               $"No monitor the capture can reach: {resolution.Reason}");
        if (resolution.Display is null) throw new InvalidOperationException(noMonitor);
        // A resolved display always has an output on the default adapter (MonitorResolution's contract).
        return CaptureSourceRules.ForDisplay(resolution.Display, topo, resolution) ?? throw new InvalidOperationException(noMonitor);
    }

    /// <summary>"Gewählter Monitor „X“ ist nicht verbunden – nehme Hauptmonitor „Y“" (the reason names X and why).</summary>
    private static string FallbackWarning(CaptureMode mode, MonitorResolution resolution)
    {
        string name = MonitorLabels.NameOf(resolution.Display!.FriendlyName);
        return mode == CaptureMode.Monitor
            ? L.T($"Gewählter Monitor {resolution.Reason} – nehme Hauptmonitor „{name}“",
                  $"Chosen monitor {resolution.Reason} – recording the primary monitor “{name}” instead")
            : L.T($"Fenster-Monitor {resolution.Reason} – nehme Hauptmonitor „{name}“",
                  $"The window's monitor {resolution.Reason} – recording the primary monitor “{name}” instead");
    }

    private static void RaiseUpgrade(MonitorRef next)
    {
        try { MonitorRefUpgrade?.Invoke(next); }
        catch (Exception ex) { Logger.Warn("Monitor identity upgrade handler threw: " + ex.Message); }
    }

    /// <summary>The HMONITOR <paramref name="hwnd"/> is on; 0 without a window.</summary>
    private static nint MonitorOf(IntPtr hwnd)
        => hwnd == IntPtr.Zero ? 0 : DisplayConfig.MonitorFromWindow(hwnd, DisplayConfig.MONITOR_DEFAULTTONEAREST);

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

    /// <summary>
    /// The monitor the user is currently looking at, INDEPENDENT of the capture
    /// profile. Screenshots use this so they are ALWAYS a whole monitor regardless of
    /// Auto/Window/Monitor mode. Prefers the foreground window's monitor; if that is
    /// our own overlay (or there is none), falls back to the monitor under the cursor.
    /// A monitor the capture cannot reach falls back to the primary (the resolution says so).
    /// </summary>
    /// <exception cref="InvalidOperationException">No monitor the capture can reach.</exception>
    public static CaptureSource ResolveActiveMonitor(DisplayTopologyCache? topology = null)
    {
        var topo = (topology ?? DisplayTopologyCache.Shared).Get();
        nint monitor = 0;
        string reason = "";
        var hwnd = User32.GetForegroundWindow();
        if (hwnd != IntPtr.Zero)
        {
            User32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != 0 && (int)pid != SelfPid)
            {
                monitor = MonitorOf(hwnd);
                reason = L.T("Monitor des Vordergrundfensters", "monitor of the foreground window");
            }
        }
        if (monitor == 0 && User32.GetCursorPos(out var cursor))
        {
            monitor = DisplayConfig.MonitorFromPoint(cursor, DisplayConfig.MONITOR_DEFAULTTONEAREST);
            reason = L.T("Monitor unter dem Mauszeiger", "monitor under the cursor");
        }

        var display = topo.ByHMonitor(monitor);
        if (display is null) reason = L.T("aktiver Monitor gerade unbekannt", "the active monitor is not known right now");
        return SourceOf(MonitorTargetRules.Follow(display, topo, reason), topo);
    }

    // -------- labels for honest UI --------

    /// <param name="mon">The monitor part: its label (<see cref="MonitorLabels"/>), or the fallback text.</param>
    private static string BuildVideoLabel(CaptureProfile profile, string mon, string? appName, IntPtr hwnd, bool useWgc)
    {
        string auto = L.T("Automatik: ", "Automatic: ");
        if (useWgc && appName != null)
        {
            // Per-window WGC: the window itself is captured, occlusion-proof.
            string wgcPart = L.T(" — Fenster (verdeckungssicher)", " — window (occlusion-proof)");
            return profile.Mode == CaptureMode.Auto
                ? $"{auto}{appName}{wgcPart}"
                : $"{appName}{wgcPart}";
        }
        return profile.Mode switch
        {
            CaptureMode.Monitor => mon,
            CaptureMode.Window => appName != null && hwnd != IntPtr.Zero
                ? $"{appName} → {mon}"
                : appName != null
                    ? $"{appName} {L.T("(kein Fenster)", "(no window)")} → {mon}"
                    : $"{L.T("Kein Fenster gewählt", "No window chosen")} → {mon}",
            _ => appName != null
                ? $"{auto}{appName} → {mon}"
                : $"{auto}{mon}"
        };
    }

    private static string BuildAudioLabel(SystemAudioMode sysMode, string? audioName, AppSettings settings)
    {
        bool mic = settings.Audio.RecordMicrophone;
        string micPart = mic ? L.T(" + Mikro", " + mic") : "";
        return sysMode switch
        {
            SystemAudioMode.Process => L.T($"Nur {audioName ?? "Spiel"}", $"Only {audioName ?? "game"}") + micPart,
            SystemAudioMode.AllAudio => L.T("Alle Sounds", "All sounds") + micPart,
            _ => mic ? L.T("Nur Mikro", "Mic only") : L.T("Kein Ton", "No audio")
        };
    }

    // Shell / system processes we must never couple audio to (a clip would be
    // silent). Anything else is fair game as a coupled audio source.
    private static readonly System.Collections.Generic.HashSet<string> NonCouplable =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "applicationframehost", "shellexperiencehost", "searchhost",
            "searchapp", "startmenuexperiencehost", "textinputhost", "sihost", "dwm",
            "lockapp", "wki_clipper",
            // The browser behind our own web widgets (its file and sign-in dialogs would
            // otherwise become the Auto-mode capture target and the "game" folder).
            "msedgewebview2"
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
