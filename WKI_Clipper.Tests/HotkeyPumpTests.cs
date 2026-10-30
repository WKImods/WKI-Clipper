using System.Collections.Generic;
using WKI_Clipper.ClickerHost;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>What the hook callbacks hand to the hotkey worker, and the wheel's held/released timing (free-hotkeys design, section 3).</summary>
public sealed class HotkeyPumpTests
{
    private const int Y = 0x59;
    private const int Quiet = WheelHold.QuietMs;

    private sealed class Rig
    {
        public readonly List<bool> Hotkey = new();
        public readonly HotkeyTracker Tracker = new();
        public readonly HotkeyPump Pump;

        public Rig(ClickerHotkey hotkey, System.Func<int, bool>? isKeyDown = null, System.Func<int, bool>? isInjected = null)
        {
            Tracker.HotkeyChanged = Hotkey.Add;
            Tracker.Configure(hotkey, null, strict: false);
            Pump = new HotkeyPump(Tracker, isKeyDown ?? (_ => true), isInjected);
        }

        public void Notch(int wheel, long at)
        {
            Pump.Post(HookInput.Wheel(wheel, at));
            Pump.Drain(at);
        }
    }

    private static ClickerHotkey Wheel(int wheel, ClickerModifiers mods = ClickerModifiers.None) => new() { Modifiers = mods, Keys = { wheel } };

    [Fact]
    public void Keys_reach_the_tracker_in_the_order_they_were_recorded()
    {
        var r = new Rig(new ClickerHotkey { Keys = { Y } });
        r.Pump.Post(HookInput.Key(Y, true, 0));
        r.Pump.Post(HookInput.Key(Y, false, 5));
        Assert.Empty(r.Hotkey);   // nothing happens on the recording thread

        r.Pump.Drain(10);

        Assert.Equal(new[] { true, false }, r.Hotkey);
    }

    [Fact]
    public void A_notch_presses_the_wheel_key_and_the_quiet_period_releases_it()
    {
        var r = new Rig(Wheel(ClickerVk.WheelDown));

        r.Notch(ClickerVk.WheelDown, 1000);
        Assert.Equal(new[] { true }, r.Hotkey);
        Assert.Equal(1000 + Quiet, r.Pump.NextWheelReleaseAt);

        r.Pump.Drain(1000 + Quiet - 1);
        Assert.Equal(new[] { true }, r.Hotkey);
        r.Pump.Drain(1000 + Quiet);
        Assert.Equal(new[] { true, false }, r.Hotkey);
        Assert.Null(r.Pump.NextWheelReleaseAt);
    }

    [Fact]
    public void Notches_that_keep_coming_hold_the_key_as_one_press()
    {
        var r = new Rig(Wheel(ClickerVk.WheelUp));

        for (long t = 0; t <= 2000; t += 100) r.Notch(ClickerVk.WheelUp, t);
        Assert.Equal(new[] { true }, r.Hotkey);
        Assert.Equal(2000 + Quiet, r.Pump.NextWheelReleaseAt);

        r.Pump.Drain(2000 + Quiet);
        Assert.Equal(new[] { true, false }, r.Hotkey);
    }

    [Fact]
    public void A_notch_after_a_quiet_period_is_a_new_press_even_when_both_are_drained_together()
    {
        var r = new Rig(Wheel(ClickerVk.WheelUp));
        r.Pump.Post(HookInput.Wheel(ClickerVk.WheelUp, 0));
        r.Pump.Post(HookInput.Wheel(ClickerVk.WheelUp, Quiet + 50));

        r.Pump.Drain(Quiet + 60);

        Assert.Equal(new[] { true, false, true }, r.Hotkey);
    }

    [Fact]
    public void Scrolling_the_other_way_releases_at_once()
    {
        var r = new Rig(Wheel(ClickerVk.WheelUp));
        r.Notch(ClickerVk.WheelUp, 0);
        r.Notch(ClickerVk.WheelDown, 50);

        Assert.Equal(new[] { true, false }, r.Hotkey);
        Assert.Contains(ClickerVk.WheelDown, r.Tracker.DownKeys);
        Assert.DoesNotContain(ClickerVk.WheelUp, r.Tracker.DownKeys);
    }

    [Fact]
    public void The_two_wheel_axes_are_independent()
    {
        var r = new Rig(new ClickerHotkey { Keys = { ClickerVk.WheelUp, ClickerVk.WheelRight } });
        r.Notch(ClickerVk.WheelUp, 0);
        r.Notch(ClickerVk.WheelRight, 100);

        Assert.Equal(new[] { true }, r.Hotkey);
        r.Pump.Drain(Quiet);   // the vertical notch went quiet first
        Assert.Equal(new[] { true, false }, r.Hotkey);
    }

    [Fact]
    public void A_modifier_plus_wheel_hotkey_needs_both()
    {
        var r = new Rig(Wheel(ClickerVk.WheelDown, ClickerModifiers.Ctrl));
        r.Notch(ClickerVk.WheelDown, 0);
        Assert.Empty(r.Hotkey);

        r.Pump.Post(HookInput.Key(ClickerVk.LControl, true, 300));
        r.Notch(ClickerVk.WheelDown, 310);
        Assert.Equal(new[] { true }, r.Hotkey);
    }

    [Fact]
    public void A_resync_keeps_held_wheel_keys_that_windows_knows_nothing_about()
    {
        var r = new Rig(Wheel(ClickerVk.WheelDown, ClickerModifiers.Ctrl), isKeyDown: vk => false);
        r.Pump.Post(HookInput.Key(ClickerVk.LControl, true, 0));
        r.Notch(ClickerVk.WheelDown, 10);
        Assert.Equal(new[] { true }, r.Hotkey);

        r.Pump.Post(HookInput.Resync(20));
        r.Pump.Drain(20);

        Assert.Equal(new[] { true, false }, r.Hotkey);   // Ctrl was really up
        Assert.Equal(new[] { ClickerVk.WheelDown }, r.Tracker.DownKeys);
    }

    [Fact]
    public void A_resync_keeps_the_held_hotkey_key_the_clicker_is_sending()
    {
        // Hold + LMB hotkey + LMB output: between two injected clicks GetAsyncKeyState reads the held button as up.
        int sending = ClickerVk.LButton;
        var r = new Rig(new ClickerHotkey { Keys = { ClickerVk.LButton } }, isKeyDown: _ => false, isInjected: vk => vk == sending);
        r.Pump.Post(HookInput.Key(ClickerVk.LButton, true, 0));
        r.Pump.Post(HookInput.Resync(10));
        r.Pump.Drain(10);

        Assert.Equal(new[] { true }, r.Hotkey);
        Assert.Contains(ClickerVk.LButton, r.Tracker.DownKeys);

        sending = 0;   // the run is over: the key state can be trusted again
        r.Pump.Post(HookInput.Resync(20));
        r.Pump.Drain(20);
        Assert.Equal(new[] { true, false }, r.Hotkey);
    }

    [Fact]
    public void An_unknown_wheel_code_is_ignored()
    {
        var r = new Rig(Wheel(ClickerVk.WheelDown));
        r.Notch(0x41, 0);
        Assert.Empty(r.Hotkey);
        Assert.Null(r.Pump.NextWheelReleaseAt);
    }
}
