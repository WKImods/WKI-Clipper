using System;
using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// The controller combo state machine. It is pure (timestamps and button masks in, an action out), so
/// every rule of "hold View+Menu for half a second" is proven here without a controller.
/// </summary>
public sealed class GamepadComboDetectorTests
{
    private const ushort VM = GamepadButtons.View | GamepadButtons.Menu;
    private const ushort VMA = GamepadButtons.View | GamepadButtons.Menu | GamepadButtons.A;
    private const ushort LBRB = GamepadButtons.LB | GamepadButtons.RB;

    private static GamepadComboDetector NewDetector(int holdMs = 500, params (string Action, ushort Mask)[] bindings)
    {
        var d = new GamepadComboDetector { HoldMs = holdMs };
        if (bindings.Length == 0) bindings = new[] { (HotkeyActions.SaveReplay, VM) };
        d.SetBindings(bindings.Select(b => new KeyValuePair<string, ushort>(b.Action, b.Mask)));
        return d;
    }

    /// <summary>Polls one pad at a steady rate and returns every fire as (time, action).</summary>
    private static List<(uint Time, string Action)> Poll(GamepadComboDetector d, int slot, ushort mask,
        uint from, uint to, uint step = 16)
    {
        var fired = new List<(uint, string)>();
        for (uint t = from; t <= to; t += step)
        {
            var a = d.Update(t, slot, mask);
            if (a != null) fired.Add((t, a));
        }
        return fired;
    }

    // ---- hold threshold ----

    [Fact]
    public void Holding_below_the_threshold_does_not_fire()
    {
        var d = NewDetector();
        Assert.Null(d.Update(0, 0, VM));
        Assert.Null(d.Update(250, 0, VM));
        Assert.Null(d.Update(499, 0, VM));
    }

    [Fact]
    public void Holding_exactly_the_threshold_fires_the_bound_action()
    {
        var d = NewDetector();
        Assert.Null(d.Update(0, 0, VM));
        Assert.Equal(HotkeyActions.SaveReplay, d.Update(500, 0, VM));
    }

    [Fact]
    public void The_hold_is_measured_from_the_last_button_press_not_the_first()
    {
        var d = NewDetector();
        Assert.Null(d.Update(0, 0, GamepadButtons.View));
        Assert.Null(d.Update(300, 0, VM));           // Menu arrives 300 ms later: the hold starts here
        Assert.Null(d.Update(799, 0, VM));
        Assert.Equal(HotkeyActions.SaveReplay, d.Update(800, 0, VM));
    }

    [Fact]
    public void A_held_combo_fires_once_not_on_every_poll()
    {
        var d = NewDetector();
        var fired = Poll(d, 0, VM, 0, 5000);
        var one = Assert.Single(fired);
        Assert.Equal(HotkeyActions.SaveReplay, one.Action);
        Assert.InRange(one.Time, 500u, 520u);
    }

    [Fact]
    public void The_hold_time_is_configurable()
    {
        var d = NewDetector(holdMs: 1000);
        d.Update(0, 0, VM);
        Assert.Null(d.Update(999, 0, VM));
        Assert.Equal(HotkeyActions.SaveReplay, d.Update(1000, 0, VM));
    }

    [Theory]
    [InlineData(0, 300)]
    [InlineData(-50, 300)]
    [InlineData(299, 300)]
    [InlineData(300, 300)]
    [InlineData(800, 800)]
    [InlineData(1500, 1500)]
    [InlineData(1501, 1500)]
    [InlineData(int.MaxValue, 1500)]
    public void The_hold_time_stays_inside_its_safe_range(int set, int expected)
    {
        // A hand-edited 0 would otherwise make every two-button press a clip.
        var d = new GamepadComboDetector { HoldMs = set };
        Assert.Equal(expected, d.HoldMs);
        Assert.Equal(expected, GamepadComboDetector.ClampHoldMs(set));
    }

    // ---- exactness ----

    [Fact]
    public void An_extra_button_cancels_the_combo_and_the_hold_restarts_when_it_is_gone()
    {
        var d = NewDetector();
        Assert.Null(d.Update(0, 0, VM));
        Assert.Null(d.Update(300, 0, VMA));           // an extra button: not the combo any more
        Assert.Null(d.Update(900, 0, VMA));
        Assert.Null(d.Update(1000, 0, VM));           // back to the exact combo: the clock restarts
        Assert.Null(d.Update(1499, 0, VM));
        Assert.Equal(HotkeyActions.SaveReplay, d.Update(1500, 0, VM));
    }

    [Fact]
    public void Only_a_part_of_the_combo_never_fires()
    {
        var d = NewDetector();
        Assert.Empty(Poll(d, 0, GamepadButtons.View, 0, 5000));
        Assert.Empty(Poll(d, 0, GamepadButtons.Menu, 5000, 10000));
    }

    [Fact]
    public void Buttons_on_two_different_pads_do_not_add_up_to_a_combo()
    {
        var d = NewDetector();
        for (uint t = 0; t <= 3000; t += 16)
        {
            Assert.Null(d.Update(t, 0, GamepadButtons.View));
            Assert.Null(d.Update(t, 1, GamepadButtons.Menu));
        }
    }

    [Fact]
    public void Releasing_early_cancels_and_a_new_press_needs_its_own_full_hold()
    {
        var d = NewDetector();
        d.Update(0, 0, VM);
        Assert.Null(d.Update(400, 0, VM));
        Assert.Null(d.Update(410, 0, 0));             // released after 400 ms
        Assert.Null(d.Update(420, 0, VM));
        Assert.Null(d.Update(919, 0, VM));
        Assert.Equal(HotkeyActions.SaveReplay, d.Update(920, 0, VM));
    }

    [Fact]
    public void Flapping_input_never_fires()
    {
        var d = NewDetector();
        // Combo on/off every 100 ms for ten seconds.
        for (uint t = 0; t < 10_000; t += 16)
            Assert.Null(d.Update(t, 0, (t / 100) % 2 == 0 ? VM : (ushort)0));
        // Combo / combo plus a stray button every 300 ms.
        for (uint t = 10_000; t < 20_000; t += 16)
            Assert.Null(d.Update(t, 0, (t / 300) % 2 == 0 ? VM : VMA));
    }

    // ---- re-arming ----

    [Fact]
    public void Releasing_every_button_of_the_combo_rearms_it()
    {
        var d = NewDetector();
        Assert.Single(Poll(d, 0, VM, 0, 600));
        Assert.Null(d.Update(700, 0, 0));
        Assert.Empty(Poll(d, 0, 0, 716, 2400));
        var again = Poll(d, 0, VM, 2500, 3200);
        Assert.Equal(HotkeyActions.SaveReplay, Assert.Single(again).Action);
    }

    [Fact]
    public void Releasing_only_one_button_does_not_rearm()
    {
        var d = NewDetector();
        Assert.Single(Poll(d, 0, VM, 0, 600));
        Poll(d, 0, VM, 616, 2600);                            // keeps holding past the cooldown
        Assert.Null(d.Update(2700, 0, GamepadButtons.View));  // Menu let go, View still down
        // Menu pressed again and held well past the hold time and the cooldown: still the same press.
        Assert.Empty(Poll(d, 0, VM, 2716, 5000));
        // Everything up, then a fresh press fires again.
        Assert.Null(d.Update(5100, 0, 0));
        Assert.Single(Poll(d, 0, VM, 5200, 5900));
    }

    // ---- cooldown ----

    [Fact]
    public void Two_pads_reporting_the_same_press_fire_once()
    {
        var d = NewDetector();
        int fires = 0;
        for (uint t = 0; t <= 6000; t += 16)
        {
            if (d.Update(t, 0, VM) != null) fires++;
            if (d.Update(t, 1, VM) != null) fires++;      // a virtual pad echoing the physical one
        }
        Assert.Equal(1, fires);
    }

    [Fact]
    public void The_echo_pad_cannot_fire_late_even_if_it_starts_after_the_first_pad()
    {
        var d = NewDetector();
        int fires = 0;
        for (uint t = 0; t <= 6000; t += 16)
        {
            if (d.Update(t, 0, VM) != null) fires++;
            if (d.Update(t, 1, t >= 120 ? VM : (ushort)0) != null) fires++;
        }
        Assert.Equal(1, fires);
    }

    [Fact]
    public void A_press_whose_hold_completes_inside_the_cooldown_is_dropped_not_deferred()
    {
        var d = NewDetector();
        Assert.Single(Poll(d, 0, VM, 0, 600));            // fires at ~500
        d.Update(610, 0, 0);
        // Pressed again 100 ms later and held for ages: the hold completes inside the cooldown.
        Assert.Empty(Poll(d, 0, VM, 700, 6000));
        // A deliberate new press after everything was let go works.
        d.Update(6100, 0, 0);
        Assert.Single(Poll(d, 0, VM, 6200, 6900));
    }

    [Fact]
    public void A_press_that_starts_inside_the_cooldown_but_completes_after_it_still_fires()
    {
        var d = NewDetector();
        d.Update(0, 0, VM);
        Assert.Equal(HotkeyActions.SaveReplay, d.Update(500, 0, VM));   // cooldown runs until 2500
        d.Update(600, 0, 0);
        d.Update(2100, 0, VM);                                           // pressed inside the cooldown
        Assert.Null(d.Update(2499, 0, VM));
        Assert.Equal(HotkeyActions.SaveReplay, d.Update(2600, 0, VM));   // hold complete, cooldown over
    }

    [Fact]
    public void The_cooldown_is_exactly_two_seconds()
    {
        // Pad 0 fires at t=500. Pad 1 completes its hold at t=2499 (1999 ms later) and is dropped ...
        var d = NewDetector();
        d.Update(0, 0, VM);
        Assert.Equal(HotkeyActions.SaveReplay, d.Update(500, 0, VM));
        d.Update(1999, 1, VM);
        Assert.Null(d.Update(2499, 1, VM));

        // ... while a second pad completing at t=2500 (2000 ms later) fires.
        d.Update(2000, 2, VM);
        Assert.Equal(HotkeyActions.SaveReplay, d.Update(2500, 2, VM));
    }

    [Fact]
    public void A_fresh_press_after_the_cooldown_fires()
    {
        var d = NewDetector();
        d.Update(0, 0, VM);
        Assert.Equal(HotkeyActions.SaveReplay, d.Update(500, 0, VM));
        d.Update(600, 0, 0);
        d.Update(2100, 0, VM);
        Assert.Null(d.Update(2599, 0, VM));
        Assert.Equal(HotkeyActions.SaveReplay, d.Update(2600, 0, VM));
    }

    // ---- several bindings ----

    [Fact]
    public void Each_binding_fires_its_own_action()
    {
        var d = NewDetector(500,
            (HotkeyActions.SaveReplay, VM),
            (HotkeyActions.Screenshot, LBRB),
            (HotkeyActions.ToggleRecording, GamepadButtons.LS | GamepadButtons.RS));

        Assert.Equal(HotkeyActions.Screenshot, Assert.Single(Poll(d, 0, LBRB, 0, 700)).Action);
        d.Update(800, 0, 0);
        Assert.Equal(HotkeyActions.SaveReplay, Assert.Single(Poll(d, 0, VM, 3000, 3700)).Action);
        d.Update(3800, 0, 0);
        Assert.Equal(HotkeyActions.ToggleRecording,
            Assert.Single(Poll(d, 0, GamepadButtons.LS | GamepadButtons.RS, 6000, 6700)).Action);
    }

    [Fact]
    public void A_combo_that_contains_another_one_fires_only_when_it_is_the_exact_mask()
    {
        var d = NewDetector(500, (HotkeyActions.SaveReplay, VM), (HotkeyActions.Screenshot, VMA));

        // View+Menu+A pressed together (A 100 ms after Menu): the smaller combo never reaches its hold.
        d.Update(0, 0, VM);
        Assert.Null(d.Update(100, 0, VMA));
        Assert.Null(d.Update(599, 0, VMA));
        Assert.Equal(HotkeyActions.Screenshot, d.Update(600, 0, VMA));
    }

    [Fact]
    public void The_smaller_combo_is_not_triggered_by_a_longer_press_that_passes_through_it_quickly()
    {
        var d = NewDetector(500, (HotkeyActions.SaveReplay, VM), (HotkeyActions.Screenshot, VMA));
        d.Update(0, 0, GamepadButtons.View);
        d.Update(40, 0, VM);
        d.Update(120, 0, VMA);
        var fires = Poll(d, 0, VMA, 136, 1000);
        Assert.DoesNotContain(fires, f => f.Action == HotkeyActions.SaveReplay);
        Assert.Equal(HotkeyActions.Screenshot, Assert.Single(fires).Action);
    }

    [Fact]
    public void No_bindings_and_empty_masks_never_fire()
    {
        var d = new GamepadComboDetector();
        Assert.Empty(Poll(d, 0, VM, 0, 5000));

        d.SetBindings(new[] { new KeyValuePair<string, ushort>(HotkeyActions.SaveReplay, 0) });
        Assert.Empty(Poll(d, 0, 0, 0, 5000));
        Assert.Empty(Poll(d, 0, VM, 5000, 10000));
    }

    [Fact]
    public void Replacing_the_bindings_takes_effect_immediately()
    {
        var d = NewDetector();
        d.SetBindings(new[] { new KeyValuePair<string, ushort>(HotkeyActions.ToggleRecording, LBRB) });

        Assert.Empty(Poll(d, 0, VM, 0, 2000));            // the old combo is gone
        d.Update(2100, 0, 0);
        Assert.Equal(HotkeyActions.ToggleRecording, Assert.Single(Poll(d, 0, LBRB, 5000, 5700)).Action);
    }

    // ---- time arithmetic ----

    [Fact]
    public void Timestamps_wrap_around_without_breaking_the_hold()
    {
        var d = NewDetector();
        uint start = uint.MaxValue - 200;                  // 49.7 days of uptime: the clock is about to wrap
        d.Update(start, 0, VM);
        Assert.Null(d.Update(unchecked(start + 499), 0, VM));
        Assert.Equal(HotkeyActions.SaveReplay, d.Update(unchecked(start + 500), 0, VM));
    }

    [Fact]
    public void Timestamps_wrap_around_without_breaking_the_cooldown()
    {
        var d = NewDetector();
        uint t0 = uint.MaxValue - 600;
        d.Update(t0, 0, VM);
        Assert.Equal(HotkeyActions.SaveReplay, d.Update(unchecked(t0 + 500), 0, VM));   // fires at MaxValue-100

        // Echo pad completes 549 ms after the fire, across the wrap: dropped.
        uint t1 = unchecked(t0 + 550);
        d.Update(t1, 1, VM);
        Assert.Null(d.Update(unchecked(t1 + 500), 1, VM));

        // A press completing 2600 ms after the fire fires.
        uint t2 = unchecked(t0 + 2600);
        d.Update(t2, 2, VM);
        Assert.Equal(HotkeyActions.SaveReplay, d.Update(unchecked(t2 + 500), 2, VM));
    }

    // ---- lifecycle ----

    [Fact]
    public void A_disconnected_pad_forgets_its_half_finished_press()
    {
        var d = NewDetector();
        d.Update(0, 0, VM);
        d.Update(400, 0, VM);
        d.ResetSlot(0);                                    // unplugged
        Assert.Null(d.Update(450, 0, VM));                 // plugged back in, button still down: clock restarts
        Assert.Null(d.Update(949, 0, VM));
        Assert.Equal(HotkeyActions.SaveReplay, d.Update(950, 0, VM));
    }

    [Fact]
    public void RequireRelease_blocks_the_pad_until_every_button_is_up()
    {
        var d = NewDetector();
        d.Update(0, 0, VM);
        d.RequireRelease(0);
        Assert.Empty(Poll(d, 0, VM, 16, 5000));            // still holding what was held before
        Assert.Null(d.Update(5100, 0, 0));                 // released ...
        Assert.Equal(HotkeyActions.SaveReplay, Assert.Single(Poll(d, 0, VM, 5200, 5900)).Action);   // ... works again
    }

    [Fact]
    public void RequireRelease_on_an_idle_pad_does_not_swallow_the_next_press()
    {
        var d = NewDetector();
        d.RequireRelease(0);
        d.Update(0, 0, 0);
        Assert.Single(Poll(d, 0, VM, 16, 700));
    }

    [Fact]
    public void Reset_forgets_everything_including_the_cooldown()
    {
        var d = NewDetector();
        Assert.Single(Poll(d, 0, VM, 0, 600));
        d.Reset();
        d.Update(700, 0, 0);
        Assert.Single(Poll(d, 0, VM, 716, 1400));          // would be inside the old cooldown
    }
}
