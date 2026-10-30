using System;
using System.Runtime.InteropServices;
using WKI_Clipper.ClickerHost;
using WKI_Clipper.Native;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Which raw input records count as the hook watchdog's reference: only those Windows turns into a hook event.</summary>
public sealed class RawInputEventsTests
{
    private static readonly IntPtr Pen = new(0x2A), Tablet = new(0x3B), Sensor = new(0x4C);

    private static User32.RAWMOUSE Mouse(int x = 0, int y = 0, ushort flags = 0, ushort buttons = 0, ushort data = 0, uint extra = 0)
        => new() { usFlags = flags, usButtonFlags = buttons, usButtonData = data, lLastX = x, lLastY = y, ulExtraInformation = extra };

    private static User32.RAWKEYBOARD Key(ushort makeCode, ushort vKey) => new() { MakeCode = makeCode, VKey = vKey };

    [Theory]
    [InlineData(3, 0, true)]
    [InlineData(0, -1, true)]
    [InlineData(0, 0, false)]   // an empty relative report: no move, so no hook event
    public void A_relative_report_counts_only_when_it_moves(int x, int y, bool counts)
        => Assert.Equal(counts, new RawInputEvents().IsMouseEvent(Pen, Mouse(x, y)));

    [Theory]
    [InlineData((ushort)0x0001)]   // left down
    [InlineData((ushort)0x0020)]   // middle up
    [InlineData((ushort)0x0040)]   // XButton1 down
    public void A_button_counts_without_any_move(ushort buttons)
        => Assert.True(new RawInputEvents().IsMouseEvent(Pen, Mouse(buttons: buttons)));

    [Theory]
    [InlineData(User32.RI_MOUSE_WHEEL, (ushort)120, true)]
    [InlineData(User32.RI_MOUSE_HWHEEL, (ushort)0xFF88, true)]   // -120
    [InlineData(User32.RI_MOUSE_WHEEL, (ushort)0, false)]          // a wheel turn of nothing
    public void A_wheel_counts_only_with_a_delta(ushort wheel, ushort delta, bool counts)
        => Assert.Equal(counts, new RawInputEvents().IsMouseEvent(Pen, Mouse(buttons: wheel, data: delta)));

    [Fact]
    public void An_absolute_device_counts_only_when_its_position_changes()
    {
        var events = new RawInputEvents();
        Assert.True(events.IsMouseEvent(Tablet, Mouse(30000, 20000, User32.MOUSE_MOVE_ABSOLUTE)));
        Assert.False(events.IsMouseEvent(Tablet, Mouse(30000, 20000, User32.MOUSE_MOVE_ABSOLUTE)));
        Assert.False(events.IsMouseEvent(Tablet, Mouse(30000, 20000, User32.MOUSE_MOVE_ABSOLUTE)));
        Assert.True(events.IsMouseEvent(Tablet, Mouse(30001, 20000, User32.MOUSE_MOVE_ABSOLUTE)));
    }

    [Fact]
    public void An_absolute_report_at_rest_still_counts_its_button()
        => Assert.True(Rested().IsMouseEvent(Tablet, Mouse(100, 200, User32.MOUSE_MOVE_ABSOLUTE, buttons: 0x0002)));

    [Fact]
    public void Absolute_positions_are_kept_per_device()
    {
        var events = Rested();
        Assert.True(events.IsMouseEvent(Pen, Mouse(100, 200, User32.MOUSE_MOVE_ABSOLUTE)));   // another device's first report
        Assert.False(events.IsMouseEvent(Tablet, Mouse(100, 200, User32.MOUSE_MOVE_ABSOLUTE)));
        Assert.False(events.IsMouseEvent(Pen, Mouse(100, 200, User32.MOUSE_MOVE_ABSOLUTE)));
    }

    [Fact]
    public void An_attributes_change_is_no_event()
        => Assert.False(new RawInputEvents().IsMouseEvent(Pen, Mouse(5, 5, User32.MOUSE_ATTRIBUTES_CHANGED, buttons: 0x0001)));

    // The tablet's position is known and unchanged since its last report.
    private static RawInputEvents Rested()
    {
        var events = new RawInputEvents();
        events.IsMouseEvent(Tablet, Mouse(100, 200, User32.MOUSE_MOVE_ABSOLUTE));
        return events;
    }

    [Theory]
    [InlineData((ushort)0x1E, (ushort)0x41, true)]    // A
    [InlineData((ushort)0x1D, (ushort)0xFF, false)]   // the fake key of an escape sequence (Pause's 0xE1 prefix)
    [InlineData((ushort)0xFF, (ushort)0xFF, false)]   // keyboard overrun
    [InlineData((ushort)0xFF, (ushort)0x41, false)]
    public void A_keyboard_record_counts_unless_it_is_a_fake_key_or_an_overrun(ushort makeCode, ushort vKey, bool counts)
        => Assert.Equal(counts, RawInputEvents.IsKeyboardEvent(Key(makeCode, vKey)));

    [Fact]
    public void The_raw_input_record_matches_the_x64_layout()
    {
        Assert.Equal(24, Marshal.SizeOf<User32.RAWMOUSE>());
        Assert.Equal(12, (int)Marshal.OffsetOf<User32.RAWMOUSE>(nameof(User32.RAWMOUSE.lLastX)));
        Assert.Equal(16, Marshal.SizeOf<User32.RAWKEYBOARD>());
        Assert.Equal(24, (int)Marshal.OffsetOf<User32.RAWINPUT>(nameof(User32.RAWINPUT.data)));
        Assert.Equal(48, Marshal.SizeOf<User32.RAWINPUT>());
    }

    // RAWMOUSE has a USHORT followed by a ULONG-aligned union: two bytes of padding sit after usFlags.
    [Fact]
    public void Rawmouse_layout_matches_native()
    {
        static int Offset(string field) => (int)Marshal.OffsetOf<User32.RAWMOUSE>(field);

        Assert.Equal(0, Offset(nameof(User32.RAWMOUSE.usFlags)));
        Assert.Equal(4, Offset(nameof(User32.RAWMOUSE.usButtonFlags)));
        Assert.Equal(6, Offset(nameof(User32.RAWMOUSE.usButtonData)));
        Assert.Equal(8, Offset(nameof(User32.RAWMOUSE.ulRawButtons)));
        Assert.Equal(12, Offset(nameof(User32.RAWMOUSE.lLastX)));
        Assert.Equal(16, Offset(nameof(User32.RAWMOUSE.lLastY)));
        Assert.Equal(20, Offset(nameof(User32.RAWMOUSE.ulExtraInformation)));
        Assert.Equal(24, Marshal.SizeOf<User32.RAWMOUSE>());
    }

    [Fact]
    public void Cursor_info_and_mouse_parameters_match_native()
    {
        Assert.Equal(24, Marshal.SizeOf<User32.CURSORINFO>());   // x64: cbSize, flags, HCURSOR, POINT
        Assert.Equal(8, (int)Marshal.OffsetOf<User32.CURSORINFO>(nameof(User32.CURSORINFO.hCursor)));
        Assert.Equal(12, Marshal.SizeOf<User32.MOUSEPARAMS>());   // SPI_GETMOUSE: int[3]
        Assert.Equal(8, (int)Marshal.OffsetOf<User32.MOUSEPARAMS>(nameof(User32.MOUSEPARAMS.Acceleration)));
    }

    // ---- the pattern recorder's raw track: physical relative motion only ----

    [Fact]
    public void Raw_sink_skips_absolute_injected_and_deviceless_records()
    {
        Assert.True(RawInputEvents.IsRecordableMotion(User32.RIM_TYPEMOUSE, Sensor, Mouse(3, -2)));
        Assert.True(RawInputEvents.IsRecordableMotion(User32.RIM_TYPEMOUSE, Sensor, Mouse(0, 1, extra: 0x1234)));   // someone else's extra info

        Assert.False(RawInputEvents.IsRecordableMotion(User32.RIM_TYPEKEYBOARD, Sensor, Mouse(3, -2)));
        Assert.False(RawInputEvents.IsRecordableMotion(User32.RIM_TYPEMOUSE, Tablet, Mouse(30000, 20000, User32.MOUSE_MOVE_ABSOLUTE)));
        Assert.False(RawInputEvents.IsRecordableMotion(User32.RIM_TYPEMOUSE, IntPtr.Zero, Mouse(3, -2)));   // injected (by anyone)
        Assert.False(RawInputEvents.IsRecordableMotion(User32.RIM_TYPEMOUSE, Sensor, Mouse(3, -2, extra: (uint)InputInjector.Tag)));
        Assert.False(RawInputEvents.IsRecordableMotion(User32.RIM_TYPEMOUSE, Sensor, Mouse(buttons: 0x0001)));   // a click, no motion
        Assert.False(RawInputEvents.IsRecordableMotion(User32.RIM_TYPEMOUSE, Sensor, Mouse(5, 5, User32.MOUSE_ATTRIBUTES_CHANGED)));
    }
}
