using System;
using System.Runtime.InteropServices;
using WKI_Clipper.ClickerHost;
using WKI_Clipper.Native;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Which raw input records count as the hook watchdog's reference: only those Windows turns into a hook event.</summary>
public sealed class RawInputEventsTests
{
    private static readonly IntPtr Pen = new(0x2A), Tablet = new(0x3B);

    private static User32.RAWMOUSE Mouse(int x = 0, int y = 0, ushort flags = 0, ushort buttons = 0, ushort data = 0)
        => new() { usFlags = flags, usButtonFlags = buttons, usButtonData = data, lLastX = x, lLastY = y };

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
}
