using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using WKI_Clipper.Native;
using WKI_Clipper.Services.AutoClicker;

namespace WKI_Clipper.ClickerHost;

/// <summary>
/// The physical key state behind the clicker hotkeys, free of Windows: the hook feeds key and button events, the tracker keeps the
/// down-set and reports when the start hotkey or the master hotkey starts or stops matching (<see cref="HotkeyMatcher"/>). A new
/// configuration never reports a hotkey that is already held as pressed - only a press made after it counts - but does report a
/// hotkey that just stopped matching, so the activation logic does not keep a stale "held".
/// </summary>
internal sealed class HotkeyTracker
{
    private readonly object _lock = new();
    private readonly object _dispatch = new();   // serializes compute + delivery so events arrive in the order they were computed
    private readonly HashSet<int> _down = new();
    private ClickerHotkey? _hotkey;
    private ClickerHotkey? _master;
    private bool _strict;
    private bool _hotkeyMatched;
    private bool _masterMatched;

    /// <summary>
    /// The start hotkey's match changed. Called on the thread that fed the event, outside the tracker's data lock but under its dispatch
    /// lock, so events are delivered in the order they were computed; keep it short and do not call back into the tracker.
    /// </summary>
    public Action<bool>? HotkeyChanged { get; set; }

    /// <summary>The master hotkey's match changed. Same rules as <see cref="HotkeyChanged"/>.</summary>
    public Action<bool>? MasterChanged { get; set; }

    public void Configure(ClickerHotkey? hotkey, ClickerHotkey? master, bool strict)
    {
        lock (_dispatch)
        {
            bool? hotkeyEvent = null, masterEvent = null;
            lock (_lock)
            {
                _hotkey = hotkey?.Clone();
                _master = master?.Clone();
                _strict = strict;
                hotkeyEvent = Rebase(HotkeyMatcher.IsMatch(_hotkey, _down, strict), ref _hotkeyMatched);
                masterEvent = Rebase(HotkeyMatcher.IsMatch(_master, _down, strict), ref _masterMatched);
            }
            if (hotkeyEvent is { } h) HotkeyChanged?.Invoke(h);
            if (masterEvent is { } m) MasterChanged?.Invoke(m);
        }
    }

    // Reports a fall to "not matching"; a rise to "matching" is taken over silently (the key was already down).
    private static bool? Rebase(bool now, ref bool last)
    {
        if (now == last) return null;
        last = now;
        return now ? null : false;
    }

    /// <summary>A key or mouse button went down or up (virtual-key code; mouse buttons as VK_LBUTTON ...).</summary>
    public void OnKey(int vk, bool down)
    {
        lock (_dispatch)
        {
            bool? hotkeyEvent = null, masterEvent = null;
            lock (_lock)
            {
                if (!(down ? _down.Add(vk) : _down.Remove(vk))) return;   // auto-repeat or a release we never saw the press of
                hotkeyEvent = Update(_hotkey, ref _hotkeyMatched);
                masterEvent = Update(_master, ref _masterMatched);
            }
            if (hotkeyEvent is { } h) HotkeyChanged?.Invoke(h);
            if (masterEvent is { } m) MasterChanged?.Invoke(m);
        }
    }

    private bool? Update(ClickerHotkey? hotkey, ref bool last)
    {
        bool now = HotkeyMatcher.IsMatch(hotkey, _down, _strict);
        if (now == last) return null;
        last = now;
        return now;
    }

    /// <summary>
    /// Drops every key that <paramref name="isDown"/> says is up. A hook that Windows silently removed (or a session lock) loses
    /// key releases; without this a modifier would count as held forever.
    /// </summary>
    public void Resync(Func<int, bool> isDown)
    {
        lock (_dispatch)
        {
            bool? hotkeyEvent = null, masterEvent = null;
            lock (_lock)
            {
                if (_down.RemoveWhere(vk => !isDown(vk)) == 0) return;
                hotkeyEvent = Update(_hotkey, ref _hotkeyMatched);
                masterEvent = Update(_master, ref _masterMatched);
            }
            if (hotkeyEvent is { } h) HotkeyChanged?.Invoke(h);
            if (masterEvent is { } m) MasterChanged?.Invoke(m);
        }
    }

    /// <summary>Snapshot of the keys currently down (tests).</summary>
    internal IReadOnlyCollection<int> DownKeys
    {
        get { lock (_lock) return new List<int>(_down); }
    }
}

/// <summary>When a hook that saw no events while its kind of input kept arriving is dead.</summary>
internal static class HookHealth
{
    /// <summary>
    /// true when input of the hook's kind arrived clearly after the hook last ran. Both are GetTickCount-domain values and may wrap,
    /// so the difference is taken as a signed 32-bit number.
    /// </summary>
    public static bool IsStale(uint lastInputTick, uint lastHookTick, int graceMs)
        => unchecked((int)(lastInputTick - lastHookTick)) > graceMs;

    /// <summary>
    /// The numbers behind a verdict for the log: per hook, its reference input tick (and the device that sent it, so a false alarm
    /// names its source) against its own last call. <paramref name="deviceName"/> resolves a device handle to its path.
    /// </summary>
    public static string Describe(HookTicks ticks, HookCheck check, Func<IntPtr, string?>? deviceName = null)
        => Describe("keyboard", check.Keyboard, ticks.KeyboardInput, ticks.KeyboardDevice, ticks.KeyboardHook, deviceName) + "; "
         + Describe("mouse", check.Mouse, ticks.MouseInput, ticks.MouseDevice, ticks.MouseHook, deviceName);

    private static string Describe(string hook, HookVerdict verdict, uint? inputTick, IntPtr? device, uint hookTick,
        Func<IntPtr, string?>? deviceName)
    {
        string state = verdict is HookVerdict.Stale or HookVerdict.StaleAgain ? "stale" : verdict.ToString().ToLowerInvariant();
        return inputTick is { } input
            ? $"{hook} {state}: input tick {input}{Source(device, deviceName)}, hook tick {hookTick} ({unchecked((int)(input - hookTick))} ms before the input)"
            : $"{hook} {state}: not watched, hook tick {hookTick}";
    }

    private static string Source(IntPtr? device, Func<IntPtr, string?>? deviceName)
    {
        if (device is not { } handle) return "";
        if (handle == IntPtr.Zero) return " from no device (injected)";
        string? name = deviceName?.Invoke(handle);
        return $" from device 0x{handle.ToInt64():X}" + (string.IsNullOrEmpty(name) ? "" : $" ({name})");
    }
}

/// <summary>
/// The clock values the watchdog judges by. The input ticks are each hook's own reference - when input of its kind last arrived,
/// seen by something other than the hook - and null where there is none (that hook is not watched). The devices name the raw
/// input device behind an input tick (zero: injected), null where the reference has no device.
/// </summary>
internal readonly record struct HookTicks(uint? KeyboardInput, uint KeyboardHook, uint? MouseInput, uint MouseHook,
    IntPtr? KeyboardDevice = null, IntPtr? MouseDevice = null);

/// <summary>What the watchdog found at one tick.</summary>
internal enum HookVerdict
{
    /// <summary>The hook is alive (or there was no input to judge it by).</summary>
    Healthy,
    /// <summary>UIPI withholds the input from the hooks (an elevated window is in front): silence is expected.</summary>
    Blind,
    /// <summary>
    /// Input arrived after the hook last ran, but only once so far: one input the hook never saw (it may have been swallowed by a
    /// hook in front of ours) is no proof of a dead hook. Wait for more input.
    /// </summary>
    Suspect,
    /// <summary>More input arrived and the hook still did not run: re-install the hooks and say so.</summary>
    Stale,
    /// <summary>Still the same stale episode as the previous tick: re-install again, but do not repeat the warning.</summary>
    StaleAgain
}

/// <summary>The verdict for both hooks together (what to do) and for each of them (for the log).</summary>
internal readonly record struct HookCheck(HookVerdict Verdict, HookVerdict Keyboard, HookVerdict Mouse);

/// <summary>
/// The watchdog's decision logic, free of Windows (the worker feeds it the clock values and the UIPI answer). Each hook is judged
/// against the input of its own kind only, so one dead hook is found while the other keeps running.
/// </summary>
internal sealed class HookWatchdog
{
    private readonly Suspicion _keyboard = new();
    private readonly Suspicion _mouse = new();

    public HookCheck Check(HookTicks ticks, bool blind, int graceMs)
    {
        if (blind)
        {
            _keyboard.Clear();
            _mouse.Clear();
            return new HookCheck(HookVerdict.Blind, HookVerdict.Blind, HookVerdict.Blind);
        }
        var keyboard = _keyboard.Judge(ticks.KeyboardInput, ticks.KeyboardHook, graceMs);
        var mouse = _mouse.Judge(ticks.MouseInput, ticks.MouseHook, graceMs);
        return new HookCheck(Rank(keyboard) >= Rank(mouse) ? keyboard : mouse, keyboard, mouse);
    }

    // A hook that just went stale is reported even while the other one's episode goes on.
    private static int Rank(HookVerdict verdict) => verdict switch
    {
        HookVerdict.Stale => 3,
        HookVerdict.StaleAgain => 2,
        HookVerdict.Suspect => 1,
        _ => 0
    };

    private sealed class Suspicion
    {
        private bool _episode;
        private uint? _suspectInput;

        public HookVerdict Judge(uint? inputTick, uint hookTick, int graceMs)
        {
            if (inputTick is not { } input || !HookHealth.IsStale(input, hookTick, graceMs))
            {
                Clear();
                return HookVerdict.Healthy;
            }
            if (_episode) return HookVerdict.StaleAgain;
            if (_suspectInput is { } first && unchecked((int)(input - first)) > 0)
            {
                _episode = true;
                _suspectInput = null;
                return HookVerdict.Stale;
            }
            _suspectInput ??= input;
            return HookVerdict.Suspect;
        }

        public void Clear()
        {
            _episode = false;
            _suspectInput = null;
        }
    }
}

/// <summary>
/// The mouse hook's reference when raw input is unavailable: the cursor moved between two watchdog checks while the mouse hook did
/// not run once in that time. A program moving the cursor (SetCursorPos) produces no hook event either; the watchdog's Suspect stage
/// keeps a single such move from counting. The keyboard has no such fallback.
/// </summary>
internal sealed class CursorMoveReference
{
    private (int X, int Y)? _lastPosition;
    private uint _lastCheck;

    /// <summary>The tick of the latest check that found such a move; null until one did.</summary>
    public uint? Reference { get; private set; }

    /// <summary>One watchdog check: where the cursor is now, the check's tick and when the mouse hook last ran.</summary>
    public uint? Observe(int x, int y, uint now, uint mouseHookTick)
    {
        if (_lastPosition is { } last && (last.X != x || last.Y != y) && unchecked((int)(mouseHookTick - _lastCheck)) < 0)
            Reference = now;
        _lastPosition = (x, y);
        _lastCheck = now;
        return Reference;
    }

    /// <summary>The cursor could not be read (a secure desktop has the input): the next position is a new baseline, not a move.</summary>
    public void Forget() => _lastPosition = null;
}

/// <summary>
/// The low-level keyboard and mouse hooks (WH_KEYBOARD_LL + WH_MOUSE_LL) on their own thread with a message loop (design section 5).
/// They observe only: every event is passed on with CallNextHookEx, never swallowed. Events that carry the host's own
/// <see cref="InputInjector.Tag"/> are skipped, input injected by anyone else counts as physical.
/// <para>
/// Windows removes a low-level hook that answers too slowly (LowLevelHooksTimeout) without telling anybody. The callbacks therefore
/// only stamp their hook's tick, post the event to a <see cref="HotkeyPump"/> and return; a worker thread drains it into the tracker
/// (whose events run the coordinator, the engine start and the over-the-clipper window walk), releases quiet wheel keys and runs the
/// watchdog. The watchdog compares each hook's last call with when input of its kind last arrived (<see cref="RawInputMonitor"/>,
/// or cursor movement without it) and has the hook thread re-install the hooks (and re-sync the key state) when one went deaf.
/// </para>
/// </summary>
internal sealed class HotkeyHook : IDisposable
{
    private const int WatchdogMs = 2000;
    private const int StaleGraceMs = 1500;
    private const int StartTimeoutMs = 5000;
    private const uint WmReinstall = User32.WM_APP + 1;

    private static readonly int KeyboardExtraInfoOffset = (int)Marshal.OffsetOf<User32.KBDLLHOOKSTRUCT>(nameof(User32.KBDLLHOOKSTRUCT.dwExtraInfo));
    private static readonly int MouseDataOffset = (int)Marshal.OffsetOf<User32.MSLLHOOKSTRUCT>(nameof(User32.MSLLHOOKSTRUCT.mouseData));
    private static readonly int MouseExtraInfoOffset = (int)Marshal.OffsetOf<User32.MSLLHOOKSTRUCT>(nameof(User32.MSLLHOOKSTRUCT.dwExtraInfo));

    private readonly HotkeyPump _pump;
    private readonly Func<bool> _hookBlind;
    private readonly Func<long> _clock;
    private readonly HookWatchdog _watchdog = new();
    private readonly RawInputMonitor _rawInput = new();
    private readonly CursorMoveReference _cursorReference = new();   // worker only
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _ready = new(false);
    private bool _wasBlind;   // worker only
    private Thread? _thread;
    private Thread? _worker;
    private uint _threadId;
    private bool _installed;
    private volatile bool _disposed;
    private int _keyboardTick, _mouseTick;   // Environment.TickCount of each hook's last call
    private int _callbackErrors, _reportedCallbackErrors;

    // Kept in fields: the native hook holds no reference to the delegates.
    private User32.LowLevelHookProc? _keyboardProc;
    private User32.LowLevelHookProc? _mouseProc;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;

    /// <param name="hookBlind">
    /// true while the hooks cannot see input for a reason other than being dead: an elevated window in front, whose input UIPI does not
    /// deliver to a non-elevated low-level hook. Called on the worker thread, so it must not touch state shared with other threads.
    /// </param>
    /// <param name="clock">Milliseconds for the wheel hold timing (default <see cref="Environment.TickCount64"/>).</param>
    /// <param name="isInjected">The key or button the clicker sends right now (see <see cref="HotkeyPump"/>).</param>
    public HotkeyHook(HotkeyTracker tracker, Func<int, bool> isKeyDown, Func<bool>? hookBlind = null, Func<long>? clock = null,
        Func<int, bool>? isInjected = null)
    {
        _pump = new HotkeyPump(tracker, isKeyDown, isInjected);
        _hookBlind = hookBlind ?? (() => false);
        _clock = clock ?? (() => Environment.TickCount64);
    }

    /// <summary>
    /// Starts the hook thread, which starts the worker once both hooks are installed; true when that happened within the start timeout.
    /// A hook thread that is late still installs and starts the worker then: the hotkeys begin to work late instead of never.
    /// </summary>
    public bool Start()
    {
        if (_thread is not null) return _installed;
        _rawInput.Start();
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "WKI Clicker hotkey hooks", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        if (!_ready.Wait(StartTimeoutMs))
        {
            HostLog.Error("Hotkey hooks: the hook thread did not start in time; the hotkeys work once it does.");
            return false;
        }
        return _installed;
    }

    // ---- hook thread: install, pump messages, re-install on request ----

    private void ThreadMain()
    {
        _threadId = Kernel32.GetCurrentThreadId();
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
        try
        {
            _installed = Install();
            // Started here, not by Start(): the hooks record from now on, and only the worker drains what they record.
            if (_installed && !_disposed)
            {
                var worker = new Thread(WorkerMain) { IsBackground = true, Name = "WKI Clicker hotkey worker", Priority = ThreadPriority.AboveNormal };
                worker.Start();
                Volatile.Write(ref _worker, worker);
            }
            _ready.Set();
            if (!_installed) return;

            while (User32.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WmReinstall)
                {
                    Reinstall();
                    continue;
                }
                User32.TranslateMessage(ref msg);
                User32.DispatchMessageW(ref msg);
            }
        }
        catch (Exception ex) { HostLog.Error("Hotkey hook thread failed", ex); }
        finally
        {
            Uninstall();
            try { _ready.Set(); }
            catch (ObjectDisposedException) { }   // Dispose ran while this thread was still late
        }
    }

    private bool Install()
    {
        var module = Kernel32.GetModuleHandleW(null);
        int now = Environment.TickCount;
        Volatile.Write(ref _keyboardTick, now);
        Volatile.Write(ref _mouseTick, now);
        _keyboardHook = User32.SetWindowsHookExW(User32.WH_KEYBOARD_LL, _keyboardProc!, module, 0);
        if (_keyboardHook == IntPtr.Zero)
        {
            HostLog.Error("Hotkey hooks: keyboard hook failed, error " + Marshal.GetLastWin32Error());
            return false;
        }
        _mouseHook = User32.SetWindowsHookExW(User32.WH_MOUSE_LL, _mouseProc!, module, 0);
        if (_mouseHook == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            Uninstall();
            HostLog.Error("Hotkey hooks: mouse hook failed, error " + error);
            return false;
        }
        return true;
    }

    private void Uninstall()
    {
        if (_keyboardHook != IntPtr.Zero) User32.UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) User32.UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = _mouseHook = IntPtr.Zero;
    }

    private void Reinstall()
    {
        Uninstall();
        if (!Install())
        {
            HostLog.Error("Hotkey hooks: re-installing failed.");   // no hook is installed now, so logging here holds nothing up
            return;
        }
        Record(HookInput.Resync(_clock()));   // key-ups the dead hooks missed
    }

    // ---- hook callbacks: stamp, record, return ----

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == User32.HC_ACTION)
        {
            Volatile.Write(ref _keyboardTick, Environment.TickCount);
            try
            {
                int message = (int)wParam;
                bool down = message is User32.WM_KEYDOWN or User32.WM_SYSKEYDOWN;
                if ((down || message is User32.WM_KEYUP or User32.WM_SYSKEYUP)
                    && (ulong)(long)Marshal.ReadIntPtr(lParam, KeyboardExtraInfoOffset) != InputInjector.Tag)
                    Record(HookInput.Key(Marshal.ReadInt32(lParam), down, _clock()));
            }
            catch { Interlocked.Increment(ref _callbackErrors); }   // the worker logs it: no file I/O on the hook thread
        }
        return User32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == User32.HC_ACTION)
        {
            Volatile.Write(ref _mouseTick, Environment.TickCount);
            int message = (int)wParam;
            if (message != User32.WM_MOUSEMOVE)   // by far the most frequent event; nothing to do for it
            {
                try
                {
                    if ((ulong)(long)Marshal.ReadIntPtr(lParam, MouseExtraInfoOffset) != InputInjector.Tag
                        && MouseInput(message, unchecked((uint)Marshal.ReadInt32(lParam, MouseDataOffset)), _clock()) is { } input)
                        Record(input);
                }
                catch { Interlocked.Increment(ref _callbackErrors); }
            }
        }
        return User32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void Record(HookInput input)
    {
        _pump.Post(input);
        _wake.Set();
    }

    /// <summary>The input a low-level mouse message stands for: a button change or a wheel notch; null for anything else (and a zero wheel delta).</summary>
    internal static HookInput? MouseInput(int message, uint mouseData, long atMs)
    {
        switch (message)
        {
            case User32.WM_LBUTTONDOWN: return HookInput.Key(ClickerVk.LButton, true, atMs);
            case User32.WM_LBUTTONUP: return HookInput.Key(ClickerVk.LButton, false, atMs);
            case User32.WM_RBUTTONDOWN: return HookInput.Key(ClickerVk.RButton, true, atMs);
            case User32.WM_RBUTTONUP: return HookInput.Key(ClickerVk.RButton, false, atMs);
            case User32.WM_MBUTTONDOWN: return HookInput.Key(ClickerVk.MButton, true, atMs);
            case User32.WM_MBUTTONUP: return HookInput.Key(ClickerVk.MButton, false, atMs);
            case User32.WM_XBUTTONDOWN: return HookInput.Key(XButton(mouseData), true, atMs);
            case User32.WM_XBUTTONUP: return HookInput.Key(XButton(mouseData), false, atMs);
            case User32.WM_MOUSEWHEEL or User32.WM_MOUSEHWHEEL:
                short delta = unchecked((short)(mouseData >> 16));
                if (delta == 0) return null;
                // Vertical: positive = rotated forward (away from the user). Horizontal: positive = tilted right.
                int wheel = message == User32.WM_MOUSEWHEEL
                    ? delta > 0 ? ClickerVk.WheelUp : ClickerVk.WheelDown
                    : delta > 0 ? ClickerVk.WheelRight : ClickerVk.WheelLeft;
                return HookInput.Wheel(wheel, atMs);
        }
        return null;
    }

    /// <summary>The high word of mouseData says which extra button: 1 = XBUTTON1, 2 = XBUTTON2.</summary>
    internal static int XButton(uint mouseData) => (mouseData >> 16) == 2 ? ClickerVk.XButton2 : ClickerVk.XButton1;

    // ---- worker: drain, wheel release, watchdog ----

    private void WorkerMain()
    {
        long nextCheck = _clock() + WatchdogMs;
        while (!_disposed)
        {
            long now = _clock();
            long wait = nextCheck - now;
            if (_pump.NextWheelReleaseAt is { } release) wait = Math.Min(wait, release - now);
            try
            {
                if (wait > 0) _wake.WaitOne((int)Math.Min(wait, WatchdogMs));
            }
            catch (ObjectDisposedException) { break; }   // a worker the late hook thread started after Dispose
            if (_disposed) break;

            now = _clock();
            try { _pump.Drain(now); }
            catch (Exception ex) { HostLog.Error("Hotkey handling failed", ex); }
            ReportCallbackErrors();
            if (now - nextCheck >= 0)
            {
                try { CheckHealth(); }
                catch (Exception ex) { HostLog.Error("Hotkey hook watchdog failed", ex); }
                nextCheck = now + WatchdogMs;
            }
        }
    }

    private void ReportCallbackErrors()
    {
        int errors = Volatile.Read(ref _callbackErrors);
        if (errors == _reportedCallbackErrors) return;
        HostLog.Error($"Hotkey hooks: {errors - _reportedCallbackErrors} event(s) could not be read.");
        _reportedCallbackErrors = errors;
    }

    private void CheckHealth()
    {
        uint keyboard = unchecked((uint)Volatile.Read(ref _keyboardTick));
        uint mouse = unchecked((uint)Volatile.Read(ref _mouseTick));
        bool raw = _rawInput.State == RawInputState.Active;
        // Never GetLastInputInfo: game controllers, pens and virtual devices move it without any hook event.
        var ticks = raw
            ? new HookTicks(_rawInput.KeyboardTick, keyboard, _rawInput.MouseTick, mouse, _rawInput.KeyboardDevice, _rawInput.MouseDevice)
            : new HookTicks(null, keyboard, CursorReference(mouse), mouse);

        bool blind = SafeBlind();
        if (_wasBlind && !blind)
        {
            // Back from an elevated window: judge the hooks afresh, not by the silence of the time spent there, and drop the key-ups they never saw.
            _wasBlind = false;
            ForgiveSilence();
            Record(HookInput.Resync(_clock()));
            return;
        }

        var check = _watchdog.Check(ticks, blind, StaleGraceMs);
        switch (check.Verdict)
        {
            case HookVerdict.Blind:
                ForgiveSilence();
                _wasBlind = true;
                return;
            case HookVerdict.Stale:
                HostLog.Warn("Hotkey hooks: no events while input arrived - re-installing the hooks (reference "
                             + (raw ? "raw input" : "cursor movement") + "; " + HookHealth.Describe(ticks, check, RawInputMonitor.DeviceName) + ").");
                RequestReinstall();
                return;
            case HookVerdict.StaleAgain:
                RequestReinstall();
                return;
        }
    }

    private uint? CursorReference(uint mouseHookTick)
    {
        if (!User32.GetCursorPos(out var cursor))
        {
            _cursorReference.Forget();
            return _cursorReference.Reference;
        }
        return _cursorReference.Observe(cursor.X, cursor.Y, unchecked((uint)Environment.TickCount), mouseHookTick);
    }

    // Input that arrived until now no longer counts against the hooks.
    private void ForgiveSilence()
    {
        int now = Environment.TickCount;
        Volatile.Write(ref _keyboardTick, now);
        Volatile.Write(ref _mouseTick, now);
    }

    // Uninstall and install must happen on the thread that owns the hooks (and pumps their messages).
    private void RequestReinstall()
    {
        if (!User32.PostThreadMessageW(_threadId, WmReinstall, UIntPtr.Zero, IntPtr.Zero))
            HostLog.Error("Hotkey hooks: could not ask the hook thread to re-install, error " + Marshal.GetLastWin32Error());
    }

    private bool SafeBlind()
    {
        try { return _hookBlind(); }
        catch (Exception ex)
        {
            HostLog.Warn("Hotkey hooks: elevation probe failed: " + ex.Message);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_thread is { IsAlive: true } t)
        {
            User32.PostThreadMessageW(_threadId, User32.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
            t.Join(2000);
        }
        _wake.Set();
        if (Volatile.Read(ref _worker) is not { } worker || worker.Join(2000)) _wake.Dispose();
        // Last: a worker that is still running only reads its ticks, which stay valid after this.
        _rawInput.Dispose();
        _ready.Dispose();
    }
}
