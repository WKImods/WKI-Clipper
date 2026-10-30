using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using WKI_Clipper.Native;

namespace WKI_Clipper.ClickerHost;

/// <summary>Whether the raw input reference of the hook watchdog is available.</summary>
internal enum RawInputState
{
    /// <summary>The raw input thread has not registered yet.</summary>
    Starting,
    /// <summary>Keyboard and mouse raw input are registered: <see cref="RawInputMonitor.KeyboardTick"/> and <see cref="RawInputMonitor.MouseTick"/> follow the input.</summary>
    Active,
    /// <summary>Registration failed (logged once); the ticks never move.</summary>
    Failed
}

/// <summary>
/// Which raw input records Windows turns into a keyboard or mouse event - the ones a low-level hook is called for. A device may send
/// reports that change nothing (a relative move of 0/0 without buttons, an unchanged absolute position, a wheel turn of 0, a keyboard
/// overrun); win32k makes no event of them, so counting them against the hooks would take a live hook for dead.
/// <para>
/// When unsure it says no: a missed event only delays the watchdog, a counted non-event makes it re-install live hooks forever.
/// Used on the raw input thread only.
/// </para>
/// </summary>
internal sealed class RawInputEvents
{
    private const ushort WheelFlags = User32.RI_MOUSE_WHEEL | User32.RI_MOUSE_HWHEEL;

    // Per device: an absolute device repeats its position in every report, and only a changed one is a move.
    private readonly Dictionary<IntPtr, (int X, int Y)> _absolute = new();

    public bool IsMouseEvent(IntPtr device, in User32.RAWMOUSE mouse)
    {
        if ((mouse.usFlags & User32.MOUSE_ATTRIBUTES_CHANGED) != 0) return false;

        bool moved;
        if ((mouse.usFlags & User32.MOUSE_MOVE_ABSOLUTE) != 0)
        {
            var position = (mouse.lLastX, mouse.lLastY);
            moved = !_absolute.TryGetValue(device, out var last) || last != position;
            _absolute[device] = position;
        }
        else moved = mouse.lLastX != 0 || mouse.lLastY != 0;

        bool buttons = (mouse.usButtonFlags & ~WheelFlags) != 0;
        bool wheel = (mouse.usButtonFlags & WheelFlags) != 0 && mouse.usButtonData != 0;
        return moved || buttons || wheel;
    }

    public static bool IsKeyboardEvent(in User32.RAWKEYBOARD keyboard)
        => keyboard.VKey != 0xFF && keyboard.MakeCode != User32.KEYBOARD_OVERRUN_MAKE_CODE;

    /// <summary>
    /// A record for the pattern recorder's raw track (mouse-patterns design 4.3, 5.1): relative motion of a physical mouse. Absolute
    /// devices (pens, tablets, remote desktops) report positions, not counts; input injected by anyone has no device; ours also carries
    /// <see cref="InputInjector.Tag"/>, so a running playback or glide is never recorded.
    /// </summary>
    public static bool IsRecordableMotion(uint type, IntPtr device, in User32.RAWMOUSE mouse)
        => type == User32.RIM_TYPEMOUSE
           && device != IntPtr.Zero
           && (mouse.usFlags & (User32.MOUSE_MOVE_ABSOLUTE | User32.MOUSE_ATTRIBUTES_CHANGED)) == 0
           && mouse.ulExtraInformation != (uint)InputInjector.Tag
           && (mouse.lLastX != 0 || mouse.lLastY != 0);
}

/// <summary>
/// Receives the raw track of a pattern recording. Called on the raw input thread for every physical relative mouse record while
/// <see cref="IsRecording"/> is true: it must not block, allocate or throw.
/// </summary>
internal interface IRawMouseSink
{
    /// <summary>false: the monitor skips the call (and the clock read) altogether.</summary>
    bool IsRecording { get; }

    /// <summary>Mouse counts of one record, stamped with the monitor's <see cref="IClock"/>.</summary>
    void OnRawMouse(double atMs, int dx, int dy);
}

/// <summary>
/// When the last physical keyboard and mouse input arrived, as the hook watchdog's reference. Raw input of the generic keyboard and
/// mouse usages comes from the same devices that feed the low-level hooks - unlike GetLastInputInfo, which game controllers, pens and
/// virtual devices move without a hook ever running - and only records that make an event count (<see cref="RawInputEvents"/>).
/// Input injected with SendInput arrives here too (with no device); the hooks see it as well and stamp their tick before they skip
/// the host's own events, so it counts on both sides.
/// <para>
/// It lives on its own thread with a message-only window: the hook thread must answer its callbacks quickly (a mouse reports 1000+
/// times a second), and a stalled hook thread - the very failure the watchdog looks for - must not stop this reference as well.
/// </para>
/// <para>
/// The same window feeds the pattern recorder's raw track (<see cref="MouseSink"/>): a process can register raw mouse input for one
/// window only, so a second registration would take this reference away.
/// </para>
/// </summary>
internal sealed class RawInputMonitor : IDisposable
{
    private const int StartTimeoutMs = 2000;
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<User32.RAWINPUTHEADER>();
    private static readonly uint InputSize = (uint)Marshal.SizeOf<User32.RAWINPUT>();
    private static int s_instances;

    private readonly string _className = "WKI_ClickerRawInput_" + Interlocked.Increment(ref s_instances);
    private readonly ManualResetEventSlim _ready = new(false);
    private IRawMouseSink? _mouseSink;
    private User32.WindowProc? _windowProc;   // kept: the window class holds no reference to the delegate
    private Thread? _thread;
    private int _threadId;
    private int _disposed;
    private int _state = (int)RawInputState.Starting;
    private int _keyboardTick, _mouseTick;   // Environment.TickCount of the last raw input event of each kind
    private IntPtr _keyboardDevice, _mouseDevice;   // the device that sent it
    private readonly RawInputEvents _events = new();

    /// <param name="clock">Stamps the raw track of a pattern recording (QPC; the watchdog ticks stay in the GetTickCount domain).</param>
    public RawInputMonitor(IClock clock) => Clock = clock;

    /// <summary>The time base of the recorded motion; the hook stamps the pointer track with it too, so both tracks share it.</summary>
    public IClock Clock { get; }

    /// <summary>Where physical relative mouse motion goes (<see cref="RawInputEvents.IsRecordableMotion"/>); null = nowhere. Any thread.</summary>
    public IRawMouseSink? MouseSink
    {
        get => Volatile.Read(ref _mouseSink);
        set => Volatile.Write(ref _mouseSink, value);
    }

    public RawInputState State => (RawInputState)Volatile.Read(ref _state);

    /// <summary>When the last raw keyboard input arrived (GetTickCount domain); the start time until then.</summary>
    public uint KeyboardTick => unchecked((uint)Volatile.Read(ref _keyboardTick));

    /// <summary>When the last raw mouse input arrived (GetTickCount domain); the start time until then.</summary>
    public uint MouseTick => unchecked((uint)Volatile.Read(ref _mouseTick));

    /// <summary>The raw input device handle behind <see cref="KeyboardTick"/>; zero for injected input or before any.</summary>
    public IntPtr KeyboardDevice => Volatile.Read(ref _keyboardDevice);

    /// <summary>The raw input device handle behind <see cref="MouseTick"/>; zero for injected input or before any.</summary>
    public IntPtr MouseDevice => Volatile.Read(ref _mouseDevice);

    /// <summary>The device's interface path (names the vendor and product), null when Windows does not know the handle (any more).</summary>
    public static string? DeviceName(IntPtr device)
    {
        if (device == IntPtr.Zero) return null;
        uint size = 0;
        if (User32.GetRawInputDeviceInfoW(device, User32.RIDI_DEVICENAME, null, ref size) != 0 || size == 0) return null;
        var buffer = new char[size];
        uint copied = User32.GetRawInputDeviceInfoW(device, User32.RIDI_DEVICENAME, buffer, ref size);
        return copied is 0 or uint.MaxValue ? null : new string(buffer, 0, (int)Math.Min(copied, (uint)buffer.Length)).TrimEnd('\0');
    }

    /// <summary>Starts the raw input thread and waits briefly for its registration; a late thread still registers then.</summary>
    public RawInputState Start()
    {
        if (_thread is not null) return State;
        int now = Environment.TickCount;
        Volatile.Write(ref _keyboardTick, now);
        Volatile.Write(ref _mouseTick, now);
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "WKI Clicker raw input", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        if (!_ready.Wait(StartTimeoutMs))
            HostLog.Warn("Raw input: the thread did not start in time; the hook watchdog judges the mouse hook by cursor movement until it does.");
        return State;
    }

    private void ThreadMain()
    {
        var module = Kernel32.GetModuleHandleW(null);
        IntPtr window = IntPtr.Zero;
        bool classRegistered = false, inputRegistered = false;
        try
        {
            _windowProc = WindowProc;
            var windowClass = new User32.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<User32.WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_windowProc),
                hInstance = module,
                lpszClassName = _className
            };
            classRegistered = User32.RegisterClassExW(ref windowClass) != 0;
            if (!classRegistered)
            {
                Fail("registering the window class", Marshal.GetLastWin32Error());
                return;
            }
            window = User32.CreateWindowExW(0, _className, null, 0, 0, 0, 0, 0, User32.HWND_MESSAGE, IntPtr.Zero, module, IntPtr.Zero);
            if (window == IntPtr.Zero)
            {
                Fail("creating the message window", Marshal.GetLastWin32Error());
                return;
            }

            // Published only now that the window gave this thread a message queue, so Dispose's WM_QUIT cannot get lost; the full
            // fences of this exchange and of Dispose's guarantee that one of the two sides sees the other.
            Interlocked.Exchange(ref _threadId, unchecked((int)Kernel32.GetCurrentThreadId()));
            if (Volatile.Read(ref _disposed) != 0) return;

            inputRegistered = User32.RegisterRawInputDevices(Devices(User32.RIDEV_INPUTSINK, window), 2, (uint)Marshal.SizeOf<User32.RAWINPUTDEVICE>());
            if (!inputRegistered)
            {
                Fail("registering keyboard and mouse", Marshal.GetLastWin32Error());
                return;
            }
            Volatile.Write(ref _state, (int)RawInputState.Active);
            HostLog.Info("Raw input: keyboard and mouse registered as the hook watchdog's reference.");
            _ready.Set();

            while (User32.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
                User32.DispatchMessageW(ref msg);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _state, (int)RawInputState.Failed);
            HostLog.Error("Raw input thread failed", ex);
        }
        finally
        {
            // Before the window goes, so no raw input is ever addressed to a destroyed window.
            if (inputRegistered) User32.RegisterRawInputDevices(Devices(User32.RIDEV_REMOVE, IntPtr.Zero), 2, (uint)Marshal.SizeOf<User32.RAWINPUTDEVICE>());
            if (window != IntPtr.Zero) User32.DestroyWindow(window);
            if (classRegistered) User32.UnregisterClassW(_className, module);
            try { _ready.Set(); }
            catch (ObjectDisposedException) { }   // Dispose ran while this thread was still late
        }
    }

    private static User32.RAWINPUTDEVICE[] Devices(uint flags, IntPtr target) => new[]
    {
        new User32.RAWINPUTDEVICE { usUsagePage = User32.HID_USAGE_PAGE_GENERIC, usUsage = User32.HID_USAGE_GENERIC_KEYBOARD, dwFlags = flags, hwndTarget = target },
        new User32.RAWINPUTDEVICE { usUsagePage = User32.HID_USAGE_PAGE_GENERIC, usUsage = User32.HID_USAGE_GENERIC_MOUSE, dwFlags = flags, hwndTarget = target }
    };

    private void Fail(string step, int error)
    {
        Volatile.Write(ref _state, (int)RawInputState.Failed);
        HostLog.Warn($"Raw input: {step} failed, error {error} - the hook watchdog judges the mouse hook by cursor movement and "
                     + "does not watch the keyboard hook.");
    }

    // Runs for every raw input record (1000+ a second): one fixed-size copy on the stack, no allocation.
    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == User32.WM_INPUT)
        {
            uint size = InputSize;
            uint copied = User32.GetRawInputData(lParam, User32.RID_INPUT, out var input, ref size, HeaderSize);
            if (copied != uint.MaxValue && copied >= HeaderSize)
            {
                var device = input.header.hDevice;
                if (input.header.dwType == User32.RIM_TYPEKEYBOARD && RawInputEvents.IsKeyboardEvent(input.data.keyboard))
                {
                    Volatile.Write(ref _keyboardDevice, device);
                    Volatile.Write(ref _keyboardTick, Environment.TickCount);
                }
                else if (input.header.dwType == User32.RIM_TYPEMOUSE && _events.IsMouseEvent(device, input.data.mouse))
                {
                    Volatile.Write(ref _mouseDevice, device);
                    Volatile.Write(ref _mouseTick, Environment.TickCount);
                }

                if (MouseSink is { IsRecording: true } sink && RawInputEvents.IsRecordableMotion(input.header.dwType, device, input.data.mouse))
                    sink.OnRawMouse(Clock.NowMs, input.data.mouse.lLastX, input.data.mouse.lLastY);
            }
        }
        return User32.DefWindowProcW(hWnd, msg, wParam, lParam);   // WM_INPUT needs it to free the raw input
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        int threadId = Volatile.Read(ref _threadId);
        if (threadId != 0 && _thread is { IsAlive: true } thread)
        {
            User32.PostThreadMessageW(unchecked((uint)threadId), User32.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
            thread.Join(2000);
        }
        _ready.Dispose();
    }
}
