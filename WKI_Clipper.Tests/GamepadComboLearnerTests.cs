using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// "Kombi anlernen": the user presses and holds the buttons for one second; the captured mask must have
/// at least two buttons and must not already belong to another action. All of it is a pure function of
/// the (time, pad, buttons) sequence.
/// </summary>
public sealed class GamepadComboLearnerTests
{
    private const ushort VM = GamepadButtons.View | GamepadButtons.Menu;
    private const ushort LBRB = GamepadButtons.LB | GamepadButtons.RB;

    private static readonly IReadOnlyDictionary<string, ushort> Bindings = new Dictionary<string, ushort>
    {
        [HotkeyActions.SaveReplay] = VM,
    };

    /// <summary>A steady press: the same mask at every 16 ms poll in [from, to].</summary>
    private static IEnumerable<(uint Time, int Slot, ushort Buttons)> Hold(ushort mask, uint from, uint to, int slot = 0)
    {
        for (uint t = from; t <= to; t += 16) yield return (t, slot, mask);
    }

    private static LearnOutcome Run(string action, IEnumerable<(uint, int, ushort)> samples,
        IReadOnlyDictionary<string, ushort>? bindings = null)
        => GamepadComboLearner.Run(action, bindings ?? Bindings, samples);

    // ---- capture ----

    [Fact]
    public void A_two_button_combo_held_for_a_second_is_captured()
    {
        var r = Run(HotkeyActions.Screenshot, Hold(LBRB, 0, 1100));
        Assert.Equal(LearnStatus.Captured, r.Status);
        Assert.Equal(LBRB, r.Mask);
        Assert.True(r.IsFinal);
    }

    [Fact]
    public void A_hold_of_less_than_a_second_is_still_pending()
    {
        var r = Run(HotkeyActions.Screenshot, Hold(LBRB, 0, 990));
        Assert.Equal(LearnStatus.Pending, r.Status);
        Assert.False(r.IsFinal);
    }

    [Fact]
    public void The_hold_boundary_is_exactly_one_second()
    {
        var l = new GamepadComboLearner(HotkeyActions.Screenshot, Bindings, 0);
        Assert.Equal(LearnStatus.Pending, l.Feed(0, 0, LBRB).Status);
        Assert.Equal(LearnStatus.Pending, l.Feed(999, 0, LBRB).Status);
        Assert.Equal(LearnStatus.Captured, l.Feed(1000, 0, LBRB).Status);
    }

    [Fact]
    public void The_second_button_pressed_late_only_starts_the_clock_at_that_moment()
    {
        var samples = Hold(GamepadButtons.LB, 0, 300).Concat(Hold(LBRB, 316, 1200));
        Assert.Equal(LearnStatus.Pending, Run(HotkeyActions.Screenshot, samples).Status);   // 316 + 1000 is not reached yet

        var longer = Hold(GamepadButtons.LB, 0, 300).Concat(Hold(LBRB, 316, 1400));
        var r = Run(HotkeyActions.Screenshot, longer);
        Assert.Equal(LearnStatus.Captured, r.Status);
        Assert.Equal(LBRB, r.Mask);
    }

    [Fact]
    public void Changing_the_buttons_restarts_the_hold()
    {
        var samples = Hold(LBRB, 0, 900).Concat(Hold(LBRB | GamepadButtons.A, 916, 1800));
        var r = Run(HotkeyActions.Screenshot, samples);
        Assert.Equal(LearnStatus.Pending, r.Status);                                        // 916 + 1000 is not reached yet

        var done = Run(HotkeyActions.Screenshot, samples.Concat(Hold(LBRB | GamepadButtons.A, 1816, 2000)));
        Assert.Equal(LearnStatus.Captured, done.Status);
        Assert.Equal(LBRB | GamepadButtons.A, done.Mask);
    }

    [Fact]
    public void Releasing_before_the_second_is_up_discards_the_attempt_and_waits_for_the_next_one()
    {
        var samples = Hold(LBRB, 0, 800)
            .Concat(Hold(0, 816, 1000))
            .Concat(Hold(GamepadButtons.X | GamepadButtons.Y, 1016, 2100));
        var r = Run(HotkeyActions.Screenshot, samples);
        Assert.Equal(LearnStatus.Captured, r.Status);
        Assert.Equal(GamepadButtons.X | GamepadButtons.Y, r.Mask);
    }

    [Fact]
    public void Any_connected_pad_can_provide_the_combo()
    {
        var r = Run(HotkeyActions.Screenshot, Hold(LBRB, 0, 1100, slot: 2));
        Assert.Equal(LearnStatus.Captured, r.Status);
    }

    [Fact]
    public void Buttons_on_two_different_pads_are_not_one_combo()
    {
        var samples = Hold(GamepadButtons.LB, 0, 1500, slot: 0)
            .Zip(Hold(GamepadButtons.RB, 0, 1500, slot: 1), (a, b) => new[] { a, b })
            .SelectMany(x => x);
        Assert.NotEqual(LearnStatus.Captured, Run(HotkeyActions.Screenshot, samples).Status);
    }

    // ---- rejections ----

    [Fact]
    public void A_single_button_is_rejected_when_the_time_runs_out()
    {
        var r = Run(HotkeyActions.Screenshot, Hold(GamepadButtons.A, 0, 5100));
        Assert.Equal(LearnStatus.TooFewButtons, r.Status);
        Assert.Equal(0, r.Mask);
    }

    [Fact]
    public void A_single_button_does_not_end_the_attempt_early_so_a_second_button_can_follow()
    {
        var samples = Hold(GamepadButtons.A, 0, 1500).Concat(Hold(GamepadButtons.A | GamepadButtons.B, 1516, 2600));
        var r = Run(HotkeyActions.Screenshot, samples);
        Assert.Equal(LearnStatus.Captured, r.Status);
        Assert.Equal(GamepadButtons.A | GamepadButtons.B, r.Mask);
    }

    [Fact]
    public void A_combo_that_belongs_to_another_action_is_rejected_with_its_owner()
    {
        var r = Run(HotkeyActions.Screenshot, Hold(VM, 0, 1100));
        Assert.Equal(LearnStatus.AlreadyUsed, r.Status);
        Assert.Equal(HotkeyActions.SaveReplay, r.UsedBy);
        Assert.Equal(VM, r.Mask);
    }

    [Fact]
    public void The_actions_own_current_combo_can_be_learned_again()
    {
        var r = Run(HotkeyActions.SaveReplay, Hold(VM, 0, 1100));
        Assert.Equal(LearnStatus.Captured, r.Status);
        Assert.Equal(VM, r.Mask);
    }

    [Fact]
    public void A_superset_or_subset_of_another_combo_is_not_a_duplicate()
    {
        var r = Run(HotkeyActions.Screenshot, Hold(VM | GamepadButtons.A, 0, 1100));
        Assert.Equal(LearnStatus.Captured, r.Status);
    }

    [Fact]
    public void Nothing_pressed_times_out_after_five_seconds()
    {
        var l = new GamepadComboLearner(HotkeyActions.Screenshot, Bindings, 1000);
        Assert.Equal(LearnStatus.Pending, l.Tick(5999).Status);
        Assert.Equal(LearnStatus.TimedOut, l.Tick(6000).Status);
    }

    [Fact]
    public void A_hold_that_completes_at_the_same_moment_as_the_timeout_still_counts()
    {
        var l = new GamepadComboLearner(HotkeyActions.Screenshot, Bindings, 0, timeoutMs: 5000, holdMs: 1000);
        l.Feed(4000, 0, LBRB);
        Assert.Equal(LearnStatus.Captured, l.Feed(5000, 0, LBRB).Status);
    }

    [Fact]
    public void A_hold_that_cannot_complete_in_time_times_out()
    {
        var r = Run(HotkeyActions.Screenshot, Hold(0, 0, 4099).Concat(Hold(LBRB, 4100, 5200)));
        Assert.Equal(LearnStatus.TimedOut, r.Status);
    }

    [Fact]
    public void The_outcome_is_final_and_does_not_change_afterwards()
    {
        var l = new GamepadComboLearner(HotkeyActions.Screenshot, Bindings, 0);
        l.Feed(0, 0, LBRB);
        var first = l.Feed(1000, 0, LBRB);
        Assert.Equal(LearnStatus.Captured, first.Status);

        Assert.Equal(first, l.Feed(1100, 0, GamepadButtons.X | GamepadButtons.Y));
        Assert.Equal(first, l.Tick(60_000));
    }

    // ---- time arithmetic ----

    [Fact]
    public void Timestamps_wrap_around_during_the_hold()
    {
        uint start = uint.MaxValue - 300;
        var l = new GamepadComboLearner(HotkeyActions.Screenshot, Bindings, start);
        l.Feed(start, 0, LBRB);
        Assert.Equal(LearnStatus.Pending, l.Feed(unchecked(start + 999), 0, LBRB).Status);
        Assert.Equal(LearnStatus.Captured, l.Feed(unchecked(start + 1000), 0, LBRB).Status);
    }

    [Fact]
    public void Timestamps_wrap_around_during_the_timeout()
    {
        uint start = uint.MaxValue - 2000;
        var l = new GamepadComboLearner(HotkeyActions.Screenshot, Bindings, start);
        Assert.Equal(LearnStatus.Pending, l.Tick(unchecked(start + 4999)).Status);
        Assert.Equal(LearnStatus.TimedOut, l.Tick(unchecked(start + 5000)).Status);
    }

    // ---- the pure helper ----

    [Fact]
    public void An_empty_sequence_is_still_pending()
    {
        Assert.Equal(LearnStatus.Pending, Run(HotkeyActions.Screenshot, Enumerable.Empty<(uint, int, ushort)>()).Status);
    }
}
