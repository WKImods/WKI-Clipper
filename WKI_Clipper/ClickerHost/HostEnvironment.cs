using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using WKI_Clipper.Native;
using WKI_Clipper.Services.AutoClicker;

namespace WKI_Clipper.ClickerHost;

/// <summary>The window in front: its class, the program behind it and whether that program runs elevated.</summary>
internal sealed class ForegroundInfo
{
    public static readonly ForegroundInfo None = new(IntPtr.Zero, 0, null, null, false);

    public ForegroundInfo(IntPtr hwnd, int pid, string? windowClass, string? exe, bool elevated)
    {
        Hwnd = hwnd;
        Pid = pid;
        Class = windowClass;
        Exe = exe;
        Elevated = elevated;
    }

    public IntPtr Hwnd { get; }
    public int Pid { get; }
    public string? Class { get; }

    /// <summary>Full image path (or just a name in tests); null when it could not be read.</summary>
    public string? Exe { get; }

    public bool Elevated { get; }

    /// <summary>The file name of <see cref="Exe"/> for messages.</summary>
    public string? ExeName => Exe is null ? null : Path.GetFileName(Exe);
}

/// <summary>Everything the engine reads from Windows. Tests answer from a fake.</summary>
internal interface IHostEnvironment
{
    /// <summary>Cursor in virtual-screen pixels.</summary>
    PixelPoint CursorPosition { get; }

    /// <summary>The monitors' rectangles (cached for a moment; the engine reads this every iteration).</summary>
    IReadOnlyList<PixelRect> Monitors { get; }

    PixelRect VirtualScreen { get; }

    /// <summary>The foreground window. The same instance while it stays the same window, so the engine's loop allocates nothing.</summary>
    ForegroundInfo Foreground { get; }

    /// <summary>The host itself runs elevated (then no window can be out of reach for it).</summary>
    bool HostElevated { get; }

    /// <summary>SPI_GETKEYBOARDDELAY, 0..3.</summary>
    int KeyboardDelayIndex { get; }

    /// <summary>SPI_GETKEYBOARDSPEED, 0..31.</summary>
    int KeyboardSpeed { get; }

    int DoubleClickTimeMs { get; }

    bool CapsLockOn { get; }

    /// <summary>The key is physically (or by anyone's injection) down right now.</summary>
    bool IsKeyDown(int vk);
}

/// <summary>The Windows answers: GetCursorPos, EnumDisplayMonitors, GetForegroundWindow and friends.</summary>
internal sealed class WindowsHostEnvironment : IHostEnvironment
{
    private const long MonitorCacheMs = 2000;
    private const int FallbackDoubleClickMs = 500;

    private readonly object _monitorLock = new();
    private IReadOnlyList<PixelRect> _monitors = Array.Empty<PixelRect>();
    private long _monitorsAt = long.MinValue;
    private readonly char[] _classBuffer = new char[256];
    private ForegroundInfo _foreground = ForegroundInfo.None;
    private readonly Lazy<bool> _hostElevated = new(IsCurrentProcessElevated);

    public PixelPoint CursorPosition
        => User32.GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : default;

    public PixelRect VirtualScreen => new(
        User32.GetSystemMetrics(User32.SM_XVIRTUALSCREEN), User32.GetSystemMetrics(User32.SM_YVIRTUALSCREEN),
        User32.GetSystemMetrics(User32.SM_CXVIRTUALSCREEN), User32.GetSystemMetrics(User32.SM_CYVIRTUALSCREEN));

    public IReadOnlyList<PixelRect> Monitors
    {
        get
        {
            long now = Environment.TickCount64;
            lock (_monitorLock)
            {
                if (now - _monitorsAt >= MonitorCacheMs || _monitors.Count == 0)
                {
                    _monitors = EnumerateMonitors();
                    _monitorsAt = now;
                }
                return _monitors;
            }
        }
    }

    private IReadOnlyList<PixelRect> EnumerateMonitors()
    {
        var list = new List<PixelRect>();
        User32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr _, IntPtr _, ref User32.RECT r, IntPtr _) =>
        {
            list.Add(new PixelRect(r.Left, r.Top, r.Width, r.Height));
            return true;
        }, IntPtr.Zero);
        if (list.Count == 0) list.Add(VirtualScreen);   // enumeration failed: the whole desktop is one monitor
        return list;
    }

    public ForegroundInfo Foreground
    {
        get
        {
            var hwnd = User32.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return _foreground = ForegroundInfo.None;
            User32.GetWindowThreadProcessId(hwnd, out uint pid);
            var cached = _foreground;
            if (cached.Hwnd == hwnd && cached.Pid == (int)pid) return cached;

            string? cls = null;
            int n = User32.GetClassNameW(hwnd, _classBuffer, _classBuffer.Length);
            if (n > 0) cls = new string(_classBuffer, 0, n);

            var (exe, elevated) = ReadProcess((int)pid);
            return _foreground = new ForegroundInfo(hwnd, (int)pid, cls, exe, elevated);
        }
    }

    /// <summary>
    /// The foreground window read afresh, without the cache and the shared buffer of <see cref="Foreground"/>: safe on any thread (the
    /// pattern coordinator reads it on the hotkey worker, its poll timer and the pattern player's thread). Allocates a little per call.
    /// </summary>
    public static ForegroundInfo ReadForeground()
    {
        var hwnd = User32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return ForegroundInfo.None;
        User32.GetWindowThreadProcessId(hwnd, out uint pid);
        var buffer = new char[256];
        int n = User32.GetClassNameW(hwnd, buffer, buffer.Length);
        var (exe, elevated) = ReadProcess((int)pid);
        return new ForegroundInfo(hwnd, (int)pid, n > 0 ? new string(buffer, 0, n) : null, exe, elevated);
    }

    /// <summary>
    /// Whether the cursor is over a window of process <paramref name="pid"/> (the clipper): the topmost visible, uncloaked, not
    /// click-through top-level window under it belongs to that process. Walks the z-order with EnumWindows instead of asking
    /// WindowFromPoint, which sends WM_NCHITTEST to the window's thread - a busy clipper UI must never hold up the hotkey worker this
    /// runs on. No shared state, like <see cref="IsForegroundWindowElevated"/>.
    /// </summary>
    public static bool IsCursorOverProcess(int pid)
    {
        if (pid <= 0 || !User32.GetCursorPos(out var p)) return false;
        bool over = false;
        try
        {
            User32.EnumWindows((hwnd, _) =>
            {
                if (!User32.IsWindowVisible(hwnd) || User32.IsIconic(hwnd)) return true;
                if ((User32.GetWindowLong(hwnd, User32.GWL_EXSTYLE) & User32.WS_EX_TRANSPARENT) != 0) return true;   // click-through
                if (User32.DwmGetWindowAttributeInt(hwnd, User32.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                if (User32.DwmGetWindowAttribute(hwnd, User32.DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<User32.RECT>()) != 0
                    && !User32.GetWindowRect(hwnd, out r)) return true;
                if (p.X < r.Left || p.X >= r.Right || p.Y < r.Top || p.Y >= r.Bottom) return true;
                User32.GetWindowThreadProcessId(hwnd, out uint owner);
                over = owner == (uint)pid;
                return false;   // the topmost window under the cursor decides
            }, IntPtr.Zero);
        }
        catch { return false; }
        return over;
    }

    /// <summary>
    /// Whether the window in front belongs to an elevated program. Uses no shared state, so the hotkey worker can call it while
    /// the engine thread reads <see cref="Foreground"/> (which uses a shared buffer and cache and is not thread-safe).
    /// </summary>
    public static bool IsForegroundWindowElevated()
    {
        var hwnd = User32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        User32.GetWindowThreadProcessId(hwnd, out uint pid);
        return ReadProcess((int)pid).Elevated;
    }

    private static (string? Exe, bool Elevated) ReadProcess(int pid)
    {
        if (pid <= 0) return (null, false);
        // Limited rights only (see Kernel32.PROCESS_QUERY_LIMITED_INFORMATION): the foreground may be an anti-cheat protected game.
        var handle = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return (null, false);
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            string? exe = Kernel32.QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString(0, (int)size) : null;
            // Image name unreadable => the handle's query right was stripped (e.g. by an anti-cheat ObCallback), so a denied token is "unknown", not elevated.
            return (exe, exe != null && Advapi32.IsElevated(handle));
        }
        finally { Kernel32.CloseHandle(handle); }
    }

    public bool HostElevated => _hostElevated.Value;

    private static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public int KeyboardDelayIndex
        => User32.SystemParametersInfoW(User32.SPI_GETKEYBOARDDELAY, 0, out int v, 0) ? v : 1;

    public int KeyboardSpeed
        => User32.SystemParametersInfoW(User32.SPI_GETKEYBOARDSPEED, 0, out int v, 0) ? v : 31;

    public int DoubleClickTimeMs
    {
        get
        {
            uint t = User32.GetDoubleClickTime();
            return t == 0 ? FallbackDoubleClickMs : (int)t;
        }
    }

    public bool CapsLockOn => (User32.GetKeyState(User32.VK_CAPITAL) & 1) != 0;

    public bool IsKeyDown(int vk) => (User32.GetAsyncKeyState(vk) & 0x8000) != 0;
}
