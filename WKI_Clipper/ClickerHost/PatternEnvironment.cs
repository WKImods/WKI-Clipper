using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using WKI_Clipper.Native;
using WKI_Clipper.Services.AutoClicker;

namespace WKI_Clipper.ClickerHost;

/// <summary>What the pattern player reads from Windows (mouse-patterns design 4.3). Tests answer from a fake.</summary>
internal interface IPatternEnvironment
{
    /// <summary>The pointer is shown right now. A game that hides it reads raw input, and an absolute move would jerk its camera.</summary>
    bool PointerVisible { get; }

    /// <summary>Windows' pointer ballistics are non-linear ("Zeigerbeschleunigung verbessern" on).</summary>
    bool AccelerationOn { get; }

    /// <summary>The pointer in virtual-screen pixels: the anchor of an absolute replay.</summary>
    PixelPoint CursorPosition { get; }

    /// <summary>The monitors' rectangles: the pointer is always on one of them, so an absolute replay keeps its target there too.</summary>
    IReadOnlyList<PixelRect> Monitors { get; }
}

/// <summary>
/// The Windows answers for the pattern player's thread: GetCursorInfo, SPI_GETMOUSE and GetCursorPos. It shares no state with
/// <see cref="WindowsHostEnvironment"/>, whose foreground cache is not thread-safe; the monitors come from a thread-safe source.
/// </summary>
internal sealed class WindowsPatternEnvironment : IPatternEnvironment
{
    private static readonly uint CursorInfoSize = (uint)Marshal.SizeOf<User32.CURSORINFO>();

    private readonly Func<IReadOnlyList<PixelRect>> _monitors;
    private PixelPoint _lastCursor;   // the player's thread only

    /// <param name="monitors">Called at every tick of an absolute replay, so it must be cheap and thread-safe
    /// (<see cref="WindowsHostEnvironment.Monitors"/> caches and locks).</param>
    public WindowsPatternEnvironment(Func<IReadOnlyList<PixelRect>> monitors) => _monitors = monitors;

    /// <summary>Showing and with a cursor handle. When Windows does not answer: hidden, so Auto sends no absolute move into a game.</summary>
    public bool PointerVisible
    {
        get
        {
            var info = new User32.CURSORINFO { cbSize = CursorInfoSize };
            return User32.GetCursorInfo(ref info) && (info.flags & User32.CURSOR_SHOWING) != 0 && info.hCursor != IntPtr.Zero;
        }
    }

    /// <summary>When Windows does not answer: on, its default (Auto then still plays the raw counts while the pointer is hidden).</summary>
    public bool AccelerationOn => User32.IsMouseAccelerationOn() ?? true;

    /// <summary>The last position read when GetCursorPos fails (another desktop in front - SendInput is refused then anyway).</summary>
    public PixelPoint CursorPosition
    {
        get
        {
            if (User32.GetCursorPos(out var p)) _lastCursor = new PixelPoint(p.X, p.Y);
            return _lastCursor;
        }
    }

    public IReadOnlyList<PixelRect> Monitors => _monitors();
}
