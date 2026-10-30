using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using WKI_Clipper.Models;
using WKI_Clipper.Native;
using WKI_Clipper.Services.AutoClicker;

namespace WKI_Clipper.ClickerHost;

/// <summary>Where <see cref="InputInjector"/> sends its events: the real SendInput, or a recorder in tests.</summary>
internal interface ISendInput
{
    /// <summary>Injects the first <paramref name="count"/> entries; returns how many went in.</summary>
    uint Send(User32.INPUT[] inputs, int count);
}

internal sealed class Win32SendInput : ISendInput
{
    private static readonly int Size = Marshal.SizeOf<User32.INPUT>();

    public uint Send(User32.INPUT[] inputs, int count) => User32.SendInput((uint)count, inputs, Size);
}

/// <summary>
/// What the click engine and the pattern player send. Every method returns true when Windows took ALL events of the call; false (UIPI
/// blocking an elevated window, a locked desktop) leaves the caller to flag it. A press stays tracked until its release went out, so
/// <see cref="ReleaseAll"/> can always undo what this sink did.
/// </summary>
internal interface IInputSink
{
    /// <summary>Moves the cursor to a virtual-screen pixel (absolute, multi-monitor, negative coordinates ok).</summary>
    bool MoveTo(int x, int y);

    /// <summary>
    /// A relative move by mouse counts (MOUSEEVENTF_MOVE without ABSOLUTE): other programs' raw input gets the counts 1:1, the pointer
    /// moves through Windows' pointer ballistics. Presses nothing, so nothing to track; 0/0 sends nothing and returns true.
    /// </summary>
    bool MoveBy(int dx, int dy);

    bool MouseDown(ClickerMouseButton button);
    bool MouseUp(ClickerMouseButton button);

    /// <summary><paramref name="pairs"/> presses and releases in ONE call (the engine's zero-hold fast path).</summary>
    bool MouseClicks(ClickerMouseButton button, int pairs);

    /// <summary>First press of a key; with <paramref name="shift"/> a Shift press goes first and stays down until <see cref="KeyUp"/>.</summary>
    bool KeyDown(int vk, bool shift);

    /// <summary>A further key-down of a key that is already down (the Windows auto-repeat the hold mode imitates).</summary>
    bool KeyRepeat(int vk);

    /// <summary>Releases the key and the Shift its press added. Does nothing for a key this sink did not press.</summary>
    bool KeyUp(int vk);

    /// <summary><paramref name="pairs"/> key taps in one call (Shift wrapped around them when asked).</summary>
    bool KeyClicks(int vk, bool shift, int pairs);

    /// <summary>Releases every button and key this sink holds. true when everything is up.</summary>
    bool ReleaseAll();
}

/// <summary>
/// SendInput for the clicker (design section 4) and the pattern player: mouse buttons, absolute virtual-desktop moves normalised to
/// 0..65535 over the virtual screen, relative moves, keyboard by scan code (extended flag where needed). Every event carries <see cref="Tag"/> in dwExtraInfo so the host's own
/// hooks skip it. Return counts are checked, a failed release is retried, and a Shift that went down for a failed key is released again.
/// </summary>
internal sealed class InputInjector : IInputSink
{
    /// <summary>"WKIC": dwExtraInfo of every event this host injects.</summary>
    public const ulong Tag = 0x574B4943;

    /// <summary>Longest burst of one call.</summary>
    public const int MaxPairs = 32;

    private const int ReleaseAttempts = 3;
    private const int VkLShift = ClickerVk.LShift;

    private readonly ISendInput _sender;
    private readonly Func<PixelRect> _virtualScreen;
    private readonly Func<uint, uint> _scanCode;
    private readonly User32.INPUT[] _inputs = new User32.INPUT[2 * MaxPairs + 2];
    private readonly Dictionary<int, bool> _keysDown = new();   // vk -> this press added the Shift
    private readonly object _lock = new();
    private uint _mouseDown;   // bit mask of MouseBit
    private bool _shiftHeld;

    /// <param name="scanCode">Virtual key to scan code with the extended bits in the high byte (MapVirtualKey MAPVK_VK_TO_VSC_EX); 0 = unknown.</param>
    public InputInjector(ISendInput sender, Func<PixelRect> virtualScreen, Func<uint, uint> scanCode)
    {
        _sender = sender;
        _virtualScreen = virtualScreen;
        _scanCode = scanCode;
    }

    /// <summary>The real thing: SendInput, the system's virtual screen and MapVirtualKey.</summary>
    public static InputInjector CreateNative(Func<PixelRect> virtualScreen)
        => new(new Win32SendInput(), virtualScreen, vk => User32.MapVirtualKeyW(vk, User32.MAPVK_VK_TO_VSC_EX));

    /// <summary>
    /// A pixel to the 0..65535 range of absolute mouse input over <paramref name="extent"/> pixels starting at <paramref name="origin"/>.
    /// The middle of the pixel is targeted, so either rounding convention of Windows lands inside it.
    /// </summary>
    public static int ToAbsolute(int value, int origin, int extent)
    {
        if (extent <= 0) return 0;
        long rel = Math.Clamp((long)value - origin, 0, extent - 1);
        long n = ((rel * 2 + 1) * 65536) / (2L * extent);
        return (int)Math.Clamp(n, 0, 65535);
    }

    public bool MoveTo(int x, int y)
    {
        lock (_lock)
        {
            var vs = _virtualScreen();
            _inputs[0] = Mouse(User32.MOUSEEVENTF_MOVE | User32.MOUSEEVENTF_ABSOLUTE | User32.MOUSEEVENTF_VIRTUALDESK,
                ToAbsolute(x, vs.X, vs.W), ToAbsolute(y, vs.Y, vs.H));
            return Send(1) == 1;
        }
    }

    public bool MoveBy(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return true;
        lock (_lock)
        {
            _inputs[0] = Mouse(User32.MOUSEEVENTF_MOVE, dx, dy);
            return Send(1) == 1;
        }
    }

    public bool MouseDown(ClickerMouseButton button)
    {
        lock (_lock)
        {
            _inputs[0] = Mouse(DownFlag(button));
            if (Send(1) != 1) return false;
            _mouseDown |= Bit(button);
            return true;
        }
    }

    public bool MouseUp(ClickerMouseButton button)
    {
        lock (_lock)
        {
            if ((_mouseDown & Bit(button)) == 0) return true;   // not held by us (already released)
            _inputs[0] = Mouse(UpFlag(button));
            if (!SendWithRetry(1)) return false;
            _mouseDown &= ~Bit(button);
            return true;
        }
    }

    public bool MouseClicks(ClickerMouseButton button, int pairs)
    {
        lock (_lock)
        {
            pairs = Math.Clamp(pairs, 1, MaxPairs);
            for (int i = 0; i < pairs; i++)
            {
                _inputs[2 * i] = Mouse(DownFlag(button));
                _inputs[2 * i + 1] = Mouse(UpFlag(button));
            }
            int count = 2 * pairs;
            uint sent = Send(count);
            if (sent == count) return true;

            // An odd count means the last press went in without its release.
            if (sent % 2 == 1)
            {
                _inputs[0] = Mouse(UpFlag(button));
                if (!SendWithRetry(1)) _mouseDown |= Bit(button);   // still down for Windows: ReleaseAll must try again
            }
            return false;
        }
    }

    public bool KeyDown(int vk, bool shift)
    {
        lock (_lock)
        {
            if (_keysDown.ContainsKey(vk)) return KeyRepeatLocked(vk);
            bool addShift = shift && !_shiftHeld;
            int n = 0;
            if (addShift) _inputs[n++] = Key(VkLShift, up: false);
            _inputs[n++] = Key(vk, up: false);
            uint sent = Send(n);
            if (sent == n)
            {
                if (addShift) _shiftHeld = true;
                _keysDown[vk] = addShift;
                return true;
            }
            // Shift went down but the key did not: do not leave Shift pressed.
            if (addShift && sent >= 1)
            {
                _inputs[0] = Key(VkLShift, up: true);
                if (!SendWithRetry(1)) _shiftHeld = true;   // ReleaseAll must try again
            }
            return false;
        }
    }

    public bool KeyRepeat(int vk)
    {
        lock (_lock) return KeyRepeatLocked(vk);
    }

    private bool KeyRepeatLocked(int vk)
    {
        _inputs[0] = Key(vk, up: false);
        return Send(1) == 1;
    }

    public bool KeyUp(int vk)
    {
        lock (_lock)
        {
            if (!_keysDown.TryGetValue(vk, out bool ownsShift)) return true;
            int n = 0;
            _inputs[n++] = Key(vk, up: true);
            if (ownsShift) _inputs[n++] = Key(VkLShift, up: true);
            if (!SendWithRetry(n)) return false;
            _keysDown.Remove(vk);
            if (ownsShift) _shiftHeld = false;
            return true;
        }
    }

    public bool KeyClicks(int vk, bool shift, int pairs)
    {
        lock (_lock)
        {
            pairs = Math.Clamp(pairs, 1, MaxPairs);
            bool wrap = shift && !_shiftHeld;
            int n = 0;
            if (wrap) _inputs[n++] = Key(VkLShift, up: false);
            for (int i = 0; i < pairs; i++)
            {
                _inputs[n++] = Key(vk, up: false);
                _inputs[n++] = Key(vk, up: true);
            }
            if (wrap) _inputs[n++] = Key(VkLShift, up: true);

            uint sent = Send(n);
            if (sent == n) return true;

            // Something was cut off: make sure neither the key nor the Shift we may have pressed stay down.
            int m = 0;
            _inputs[m++] = Key(vk, up: true);
            if (wrap) _inputs[m++] = Key(VkLShift, up: true);
            if (!SendWithRetry(m))
            {
                // Unknown which part went out: keep key and Shift tracked so ReleaseAll retries them (an extra release is harmless).
                if (sent > 0) _keysDown[vk] = wrap;
                if (wrap && sent > 0) _shiftHeld = true;
            }
            return false;
        }
    }

    /// <summary>
    /// Releases without knowing what was pressed (the release process after a helper was killed): an up for the mouse button, the key
    /// and Shift, each only when given. An up for something that is not down is harmless.
    /// </summary>
    public bool ReleaseBlind(ClickerMouseButton? button, int vk, bool shift)
    {
        lock (_lock)
        {
            int n = 0;
            if (button is { } b) _inputs[n++] = Mouse(UpFlag(b));
            if (vk > 0) _inputs[n++] = Key(vk, up: true);
            if (shift) _inputs[n++] = Key(VkLShift, up: true);
            return n == 0 || SendWithRetry(n);
        }
    }

    public bool ReleaseAll()
    {
        lock (_lock)
        {
            bool ok = true;
            foreach (var button in new[] { ClickerMouseButton.Left, ClickerMouseButton.Right, ClickerMouseButton.Middle })
            {
                if ((_mouseDown & Bit(button)) == 0) continue;
                _inputs[0] = Mouse(UpFlag(button));
                if (SendWithRetry(1)) _mouseDown &= ~Bit(button); else ok = false;
            }

            if (_keysDown.Count > 0)
            {
                var keys = new List<KeyValuePair<int, bool>>(_keysDown);
                foreach (var (vk, ownsShift) in keys)
                {
                    int n = 0;
                    _inputs[n++] = Key(vk, up: true);
                    if (ownsShift) _inputs[n++] = Key(VkLShift, up: true);
                    if (SendWithRetry(n))
                    {
                        _keysDown.Remove(vk);
                        if (ownsShift) _shiftHeld = false;
                    }
                    else ok = false;
                }
            }
            if (_shiftHeld)
            {
                _inputs[0] = Key(VkLShift, up: true);
                if (SendWithRetry(1)) _shiftHeld = false; else ok = false;
            }
            return ok;
        }
    }

    // ---- helpers ----

    private uint Send(int count) => _sender.Send(_inputs, count);

    /// <summary>Sends the first <paramref name="count"/> entries (releases); what a short call left out is sent again, up to a few times.</summary>
    private bool SendWithRetry(int count)
    {
        for (int attempt = 0; attempt < ReleaseAttempts; attempt++)
        {
            uint sent = Send(count);
            if (sent >= count) return true;
            if (sent > 0)
            {
                int left = count - (int)sent;
                Array.Copy(_inputs, (int)sent, _inputs, 0, left);
                count = left;
            }
        }
        return false;
    }

    private static uint Bit(ClickerMouseButton b) => b switch { ClickerMouseButton.Right => 2u, ClickerMouseButton.Middle => 4u, _ => 1u };

    private static uint DownFlag(ClickerMouseButton b) => b switch
    {
        ClickerMouseButton.Right => User32.MOUSEEVENTF_RIGHTDOWN,
        ClickerMouseButton.Middle => User32.MOUSEEVENTF_MIDDLEDOWN,
        _ => User32.MOUSEEVENTF_LEFTDOWN
    };

    private static uint UpFlag(ClickerMouseButton b) => b switch
    {
        ClickerMouseButton.Right => User32.MOUSEEVENTF_RIGHTUP,
        ClickerMouseButton.Middle => User32.MOUSEEVENTF_MIDDLEUP,
        _ => User32.MOUSEEVENTF_LEFTUP
    };

    private static User32.INPUT Mouse(uint flags, int dx = 0, int dy = 0) => new()
    {
        type = User32.INPUT_MOUSE,
        u = new User32.INPUTUNION
        {
            mi = new User32.MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags, dwExtraInfo = (UIntPtr)Tag }
        }
    };

    private User32.INPUT Key(int vk, bool up)
    {
        uint scan = _scanCode((uint)vk);
        var ki = new User32.KEYBDINPUT { dwExtraInfo = (UIntPtr)Tag };
        if (scan == 0)
        {
            ki.wVk = (ushort)vk;
            ki.dwFlags = up ? User32.KEYEVENTF_KEYUP : 0;
        }
        else
        {
            ki.wScan = (ushort)(scan & 0xFF);
            ki.dwFlags = User32.KEYEVENTF_SCANCODE | ((scan & 0xFF00) != 0 ? User32.KEYEVENTF_EXTENDEDKEY : 0)
                | (up ? User32.KEYEVENTF_KEYUP : 0);
        }
        return new User32.INPUT { type = User32.INPUT_KEYBOARD, u = new User32.INPUTUNION { ki = ki } };
    }
}
