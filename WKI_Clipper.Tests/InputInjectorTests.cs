using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using WKI_Clipper.ClickerHost;
using WKI_Clipper.Models;
using WKI_Clipper.Native;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>SendInput for the clicker, against a recorder: flags, our tag, absolute coordinates, scan codes, Shift wrapping, return counts, release retries.</summary>
public sealed class InputInjectorTests
{
    private sealed class Recorder : ISendInput
    {
        public readonly List<User32.INPUT> Sent = new();
        public readonly List<int> Calls = new();
        /// <summary>How many events of each call to accept; null = all. Consumed per call.</summary>
        public readonly Queue<int> Accept = new();

        public uint Send(User32.INPUT[] inputs, int count)
        {
            int take = Accept.Count > 0 ? Math.Min(Accept.Dequeue(), count) : count;
            Calls.Add(count);
            for (int i = 0; i < take; i++) Sent.Add(inputs[i]);
            return (uint)take;
        }
    }

    private static InputInjector Make(Recorder r, PixelRect? screen = null, Func<uint, uint>? scan = null)
        => new(r, () => screen ?? new PixelRect(0, 0, 1920, 1080), scan ?? (vk => vk));   // the virtual key doubles as a non-zero scan code without extended bits

    private static string Describe(User32.INPUT i)
    {
        if (i.type == User32.INPUT_MOUSE) return "M" + i.u.mi.dwFlags.ToString("X");
        var ki = i.u.ki;
        string dir = (ki.dwFlags & User32.KEYEVENTF_KEYUP) != 0 ? "u" : "d";
        return "K" + dir + (ki.wScan != 0 ? ki.wScan.ToString("X") : "vk" + ki.wVk.ToString("X"));
    }

    // ---- structure ----

    [Fact]
    public void The_input_struct_has_the_size_windows_expects()
    {
        Assert.Equal(40, Marshal.SizeOf<User32.INPUT>());   // x64
    }

    [Fact]
    public void Our_tag_is_wkic()
    {
        Assert.Equal(0x574B4943UL, InputInjector.Tag);
        Assert.Equal("WKIC", System.Text.Encoding.ASCII.GetString(new byte[] { 0x57, 0x4B, 0x49, 0x43 }));
    }

    // ---- blind release (the release process) ----

    [Fact]
    public void A_blind_release_sends_ups_for_the_button_the_key_and_shift_without_having_pressed_them()
    {
        var r = new Recorder();

        Assert.True(Make(r).ReleaseBlind(ClickerMouseButton.Right, 0x57, shift: true));

        Assert.Equal(new[] { "M" + User32.MOUSEEVENTF_RIGHTUP.ToString("X"), "Ku57", "KuA0" }, r.Sent.Select(Describe).ToArray());
        Assert.All(r.Sent, i => Assert.Equal((UIntPtr)InputInjector.Tag, i.type == User32.INPUT_MOUSE ? i.u.mi.dwExtraInfo : i.u.ki.dwExtraInfo));
    }

    [Fact]
    public void A_blind_release_with_nothing_given_sends_nothing()
    {
        var r = new Recorder();

        Assert.True(Make(r).ReleaseBlind(null, 0, shift: false));

        Assert.Empty(r.Calls);
    }

    // ---- mouse ----

    [Theory]
    [InlineData(ClickerMouseButton.Left, User32.MOUSEEVENTF_LEFTDOWN, User32.MOUSEEVENTF_LEFTUP)]
    [InlineData(ClickerMouseButton.Right, User32.MOUSEEVENTF_RIGHTDOWN, User32.MOUSEEVENTF_RIGHTUP)]
    [InlineData(ClickerMouseButton.Middle, User32.MOUSEEVENTF_MIDDLEDOWN, User32.MOUSEEVENTF_MIDDLEUP)]
    public void A_button_press_and_release_use_the_buttons_flags_and_carry_the_tag(ClickerMouseButton button, uint down, uint up)
    {
        var r = new Recorder();
        var inj = Make(r);

        Assert.True(inj.MouseDown(button));
        Assert.True(inj.MouseUp(button));

        Assert.Equal(new[] { down, up }, r.Sent.Select(i => i.u.mi.dwFlags).ToArray());
        Assert.All(r.Sent, i =>
        {
            Assert.Equal(User32.INPUT_MOUSE, i.type);
            Assert.Equal((UIntPtr)InputInjector.Tag, i.u.mi.dwExtraInfo);
        });
    }

    [Fact]
    public void A_burst_is_one_call_with_all_pairs_in_order()
    {
        var r = new Recorder();
        var inj = Make(r);

        Assert.True(inj.MouseClicks(ClickerMouseButton.Left, 3));

        Assert.Equal(new[] { 6 }, r.Calls.ToArray());
        Assert.Equal(new[] { "M2", "M4", "M2", "M4", "M2", "M4" }, r.Sent.Select(Describe).ToArray());
    }

    [Fact]
    public void A_release_of_a_button_we_did_not_press_sends_nothing()
    {
        var r = new Recorder();
        Assert.True(Make(r).MouseUp(ClickerMouseButton.Left));
        Assert.Empty(r.Sent);
    }

    [Fact]
    public void A_rejected_press_returns_false_and_is_not_tracked()
    {
        var r = new Recorder();
        r.Accept.Enqueue(0);
        var inj = Make(r);

        Assert.False(inj.MouseDown(ClickerMouseButton.Left));
        Assert.True(inj.ReleaseAll());
        Assert.Empty(r.Sent);   // nothing was pressed, so nothing is released
    }

    [Fact]
    public void A_failed_release_is_retried_and_a_burst_cut_in_the_middle_of_a_pair_releases_the_button()
    {
        var r = new Recorder();
        var inj = Make(r);
        Assert.True(inj.MouseDown(ClickerMouseButton.Left));

        r.Accept.Enqueue(0);   // the first release attempt is rejected
        Assert.True(inj.MouseUp(ClickerMouseButton.Left));
        Assert.Equal(new[] { "M2", "M4" }, r.Sent.Select(Describe).ToArray());

        r.Sent.Clear();
        r.Accept.Enqueue(3);   // down up down | up (cut)
        Assert.False(inj.MouseClicks(ClickerMouseButton.Left, 2));
        Assert.Equal(new[] { "M2", "M4", "M2", "M4" }, r.Sent.Select(Describe).ToArray());   // the missing release was sent
    }

    [Fact]
    public void A_release_that_keeps_failing_stays_tracked_and_release_all_tries_again()
    {
        var r = new Recorder();
        var inj = Make(r);
        inj.MouseDown(ClickerMouseButton.Right);
        r.Sent.Clear();
        r.Accept.Enqueue(0);
        r.Accept.Enqueue(0);
        r.Accept.Enqueue(0);

        Assert.False(inj.MouseUp(ClickerMouseButton.Right));
        Assert.Empty(r.Sent);

        Assert.True(inj.ReleaseAll());
        Assert.Equal(new[] { "M10" }, r.Sent.Select(Describe).ToArray());
        Assert.True(inj.ReleaseAll());
        Assert.Single(r.Sent);   // released once, not twice
    }

    [Fact]
    public void Release_all_lets_go_of_every_button_and_key_and_the_shift_we_added()
    {
        var r = new Recorder();
        var inj = Make(r);
        inj.MouseDown(ClickerMouseButton.Left);
        inj.MouseDown(ClickerMouseButton.Middle);
        inj.KeyDown(0x41, shift: true);
        r.Sent.Clear();

        Assert.True(inj.ReleaseAll());

        var sent = r.Sent.Select(Describe).ToList();
        Assert.Contains("M4", sent);
        Assert.Contains("M40", sent);
        Assert.Contains("Ku41", sent);
        Assert.Contains("KuA0", sent);   // the shift
        Assert.Equal(4, sent.Count);
    }

    // ---- moves ----

    [Fact]
    public void A_move_is_absolute_over_the_virtual_desktop()
    {
        var r = new Recorder();
        var inj = Make(r);

        Assert.True(inj.MoveTo(960, 540));

        var mi = r.Sent.Single().u.mi;
        Assert.Equal(User32.MOUSEEVENTF_MOVE | User32.MOUSEEVENTF_ABSOLUTE | User32.MOUSEEVENTF_VIRTUALDESK, mi.dwFlags);
        Assert.InRange(mi.dx, 32768, 32768 + 40);   // the middle of the screen
        Assert.InRange(mi.dy, 32768, 32768 + 40);
    }

    [Theory]
    [InlineData(1920)]
    [InlineData(3840)]
    [InlineData(7680)]
    [InlineData(5760)]
    public void Every_pixel_maps_to_a_distinct_value_that_lands_back_inside_the_same_pixel(int extent)
    {
        int previous = -1;
        for (int px = 0; px < extent; px++)
        {
            int n = InputInjector.ToAbsolute(px, 0, extent);
            Assert.InRange(n, 0, 65535);
            Assert.True(n > previous, $"pixel {px} must map above the previous one");
            previous = n;
            Assert.Equal(px, (int)((long)n * extent / 65536));   // the usual absolute-to-pixel conversion
        }
    }

    [Fact]
    public void A_virtual_desktop_with_a_negative_origin_is_normalised_from_its_own_left_edge()
    {
        var screen = new PixelRect(-1920, -200, 3840, 1280);
        Assert.InRange(InputInjector.ToAbsolute(-1920, screen.X, screen.W), 0, 20);
        Assert.InRange(InputInjector.ToAbsolute(1919, screen.X, screen.W), 65515, 65535);
        Assert.InRange(InputInjector.ToAbsolute(-200, screen.Y, screen.H), 0, 60);
        Assert.Equal(InputInjector.ToAbsolute(0, 0, 3840), InputInjector.ToAbsolute(1920 - 1920, -1920 + 1920, 3840));

        var r = new Recorder();
        Make(r, screen).MoveTo(-1920, -200);
        Assert.InRange(r.Sent.Single().u.mi.dx, 0, 20);
    }

    [Fact]
    public void Coordinates_outside_the_desktop_are_clamped_and_a_degenerate_screen_gives_zero()
    {
        Assert.Equal(InputInjector.ToAbsolute(1919, 0, 1920), InputInjector.ToAbsolute(99999, 0, 1920));
        Assert.Equal(InputInjector.ToAbsolute(0, 0, 1920), InputInjector.ToAbsolute(-99999, 0, 1920));
        Assert.Equal(0, InputInjector.ToAbsolute(5, 0, 0));
    }

    // ---- keyboard ----

    [Fact]
    public void A_key_goes_out_by_scan_code_with_the_extended_flag_where_the_scan_code_has_one()
    {
        var r = new Recorder();
        var inj = Make(r, scan: vk => vk == 0x27 ? 0xE04Du : 0x1Eu);   // right arrow is an extended key

        inj.KeyClicks(0x27, shift: false, pairs: 1);
        inj.KeyClicks(0x41, shift: false, pairs: 1);

        var arrow = r.Sent.Take(2).Select(i => i.u.ki).ToArray();
        Assert.All(arrow, k =>
        {
            Assert.Equal(0x4D, k.wScan);
            Assert.NotEqual(0u, k.dwFlags & User32.KEYEVENTF_SCANCODE);
            Assert.NotEqual(0u, k.dwFlags & User32.KEYEVENTF_EXTENDEDKEY);
            Assert.Equal((UIntPtr)InputInjector.Tag, k.dwExtraInfo);
        });
        Assert.NotEqual(0u, arrow[1].dwFlags & User32.KEYEVENTF_KEYUP);
        var plain = r.Sent.Skip(2).Select(i => i.u.ki).ToArray();
        Assert.All(plain, k => Assert.Equal(0u, k.dwFlags & User32.KEYEVENTF_EXTENDEDKEY));
    }

    [Fact]
    public void A_key_without_a_scan_code_falls_back_to_the_virtual_key()
    {
        var r = new Recorder();
        var inj = Make(r, scan: _ => 0);

        inj.KeyClicks(0x41, shift: false, pairs: 1);

        Assert.Equal(new[] { "Kdvk41", "Kuvk41" }, r.Sent.Select(Describe).ToArray());
        Assert.All(r.Sent, i => Assert.Equal(0u, i.u.ki.dwFlags & User32.KEYEVENTF_SCANCODE));
    }

    [Fact]
    public void Shift_goes_down_before_the_key_and_up_after_it_in_one_call()
    {
        var r = new Recorder();
        var inj = Make(r);

        Assert.True(inj.KeyClicks(0x41, shift: true, pairs: 2));

        Assert.Equal(new[] { 6 }, r.Calls.ToArray());
        Assert.Equal(new[] { "KdA0", "Kd41", "Ku41", "Kd41", "Ku41", "KuA0" }, r.Sent.Select(Describe).ToArray());
    }

    [Fact]
    public void A_held_key_keeps_the_shift_of_its_press_until_it_is_released()
    {
        var r = new Recorder();
        var inj = Make(r);

        Assert.True(inj.KeyDown(0x41, shift: true));
        Assert.True(inj.KeyRepeat(0x41));
        Assert.Equal(3, r.Sent.Count);   // shift down, key down, key down (the repeat)
        Assert.Equal(new[] { 2, 1 }, r.Calls.ToArray());

        r.Sent.Clear();
        Assert.True(inj.KeyUp(0x41));
        Assert.Equal(2, r.Sent.Count);   // key up, then shift up
        Assert.NotEqual(0u, r.Sent[0].u.ki.dwFlags & User32.KEYEVENTF_KEYUP);
        Assert.Equal(0xA0 & 0xFF, r.Sent[1].u.ki.wScan & 0xFF);
    }

    [Fact]
    public void A_key_press_cut_after_its_shift_releases_the_shift_again()
    {
        var r = new Recorder();
        var inj = Make(r);
        r.Accept.Enqueue(1);   // shift down goes in, the key does not

        Assert.False(inj.KeyDown(0x41, shift: true));

        Assert.Equal(2, r.Sent.Count);
        Assert.Equal(0u, r.Sent[0].u.ki.dwFlags & User32.KEYEVENTF_KEYUP);
        Assert.NotEqual(0u, r.Sent[1].u.ki.dwFlags & User32.KEYEVENTF_KEYUP);   // shift up
        Assert.Equal(r.Sent[0].u.ki.wScan, r.Sent[1].u.ki.wScan);

        r.Sent.Clear();
        Assert.True(inj.ReleaseAll());
        Assert.Empty(r.Sent);   // nothing left held
    }

    [Fact]
    public void A_cut_key_burst_releases_the_key_and_the_shift_it_may_have_pressed()
    {
        var r = new Recorder();
        var inj = Make(r);
        r.Accept.Enqueue(2);   // shift down, key down | rest lost

        Assert.False(inj.KeyClicks(0x41, shift: true, pairs: 1));

        var tail = r.Sent.Skip(2).Select(i => (i.u.ki.wScan & 0xFF, (i.u.ki.dwFlags & User32.KEYEVENTF_KEYUP) != 0)).ToArray();
        Assert.Equal(new[] { (0x41, true), (0xA0 & 0xFF, true) }, tail);
    }

    // ---- clean-up releases that fail too must stay tracked ----

    [Fact]
    public void A_burst_cut_after_a_press_whose_cleanup_release_fails_leaves_the_button_to_release_all()
    {
        var r = new Recorder();
        var inj = Make(r);
        r.Accept.Enqueue(1);   // down goes in, its up does not
        r.Accept.Enqueue(0);   // ... and the three clean-up attempts fail as well (secure desktop)
        r.Accept.Enqueue(0);
        r.Accept.Enqueue(0);

        Assert.False(inj.MouseClicks(ClickerMouseButton.Left, 1));
        r.Sent.Clear();

        Assert.True(inj.ReleaseAll());
        Assert.Equal(new[] { "M4" }, r.Sent.Select(Describe).ToArray());   // left up
    }

    [Fact]
    public void A_key_press_cut_after_its_shift_whose_cleanup_release_fails_leaves_the_shift_to_release_all()
    {
        var r = new Recorder();
        var inj = Make(r);
        r.Accept.Enqueue(1);   // shift down goes in, the key does not
        r.Accept.Enqueue(0);
        r.Accept.Enqueue(0);
        r.Accept.Enqueue(0);

        Assert.False(inj.KeyDown(0x41, shift: true));
        r.Sent.Clear();

        Assert.True(inj.ReleaseAll());
        Assert.Equal(new[] { "KuA0" }, r.Sent.Select(Describe).ToArray());   // shift up
    }

    [Fact]
    public void A_key_burst_cut_whose_cleanup_release_fails_leaves_key_and_shift_to_release_all()
    {
        var r = new Recorder();
        var inj = Make(r);
        r.Accept.Enqueue(3);   // shift down, key down, key up | the rest is lost
        r.Accept.Enqueue(0);
        r.Accept.Enqueue(0);
        r.Accept.Enqueue(0);

        Assert.False(inj.KeyClicks(0x41, shift: true, pairs: 2));
        r.Sent.Clear();

        Assert.True(inj.ReleaseAll());
        var sent = r.Sent.Select(Describe).ToList();
        Assert.Contains("KuA0", sent);   // shift up
        Assert.Contains("Ku41", sent);
    }

    [Fact]
    public void A_burst_that_was_rejected_completely_leaves_nothing_to_release()
    {
        var r = new Recorder();
        var inj = Make(r);
        r.Accept.Enqueue(0);   // nothing went in, so there is nothing to clean up
        r.Accept.Enqueue(0);
        r.Accept.Enqueue(0);
        r.Accept.Enqueue(0);

        Assert.False(inj.KeyClicks(0x41, shift: true, pairs: 1));
        r.Sent.Clear();

        Assert.True(inj.ReleaseAll());
        Assert.Empty(r.Sent);
    }
}
