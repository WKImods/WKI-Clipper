using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using WKI_Clipper.Models;
using WKI_Clipper.Native;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// The polling service against a fake pad source and a fake clock: connection tracking, probe rate, packet
/// skipping, firing, learn mode and the thread lifecycle. No controller is needed.
/// </summary>
public sealed class GamepadServiceTests : IDisposable
{
    private const ushort VM = GamepadButtons.View | GamepadButtons.Menu;
    private const ushort LBRB = GamepadButtons.LB | GamepadButtons.RB;

    private readonly List<Rig> _rigs = new();

    public void Dispose()
    {
        foreach (var r in _rigs) r.Dispose();
    }

    private sealed class FakePads : IPadSource
    {
        private readonly object _l = new();
        private readonly PadSnapshot?[] _slots = new PadSnapshot?[4];
        private readonly uint[] _packet = new uint[4];
        private readonly int[] _reads = new int[4];
        public bool Available = true;
        public Exception? Throw;

        public bool IsAvailable => Available;

        public void Connect(int slot, ushort buttons = 0) => Press(slot, buttons);
        public void Press(int slot, ushort buttons)
        {
            lock (_l) { _packet[slot]++; _slots[slot] = new PadSnapshot(_packet[slot], buttons); }
        }
        public void Disconnect(int slot) { lock (_l) _slots[slot] = null; }
        public int Reads(int slot) { lock (_l) return _reads[slot]; }

        public bool TryRead(int slot, out PadSnapshot state)
        {
            lock (_l)
            {
                _reads[slot]++;
                if (Throw != null) throw Throw;
                state = _slots[slot] ?? default;
                return _slots[slot].HasValue;
            }
        }
    }

    private sealed class Rig : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "wki_gamepad_svc_" + Guid.NewGuid().ToString("N"));
        public readonly SettingsService Settings;
        public readonly FakePads Pads = new();
        public readonly GamepadService Svc;
        public readonly List<string> Fired = new();
        public int StatusChanges;
        public uint Now;

        public Rig(bool realClock = false, Action<GamepadSettings>? configure = null)
        {
            Settings = new SettingsService(Dir);
            configure?.Invoke(Settings.Current.Gamepad);
            Func<uint> clock = realClock ? () => unchecked((uint)Environment.TickCount64) : () => Now;
            Svc = new GamepadService(Settings, Pads, clock, followSettings: false);
            Svc.ReloadConfig();
            Svc.ActionTriggered += a => { lock (Fired) Fired.Add(a); };
            Svc.StatusChanged += () => Interlocked.Increment(ref StatusChanges);
        }

        public int FiredCount() { lock (Fired) return Fired.Count; }

        /// <summary>One poll at the current fake time.</summary>
        public void Poll() => Svc.PollOnce(Now);

        /// <summary>Polls at 16 ms steps for the given duration (first poll at the current time).</summary>
        public void Run(uint ms)
        {
            uint end = Now + ms;
            Svc.PollOnce(Now);
            while (Now < end)
            {
                Now += 16;
                Svc.PollOnce(Now);
            }
        }

        public void Dispose()
        {
            Svc.Dispose();
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    private Rig NewRig(bool realClock = false, Action<GamepadSettings>? configure = null)
    {
        var r = new Rig(realClock, configure);
        _rigs.Add(r);
        return r;
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 4000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    // ---- connection tracking ----

    [Fact]
    public void A_pad_in_slot_zero_is_reported_as_connected_and_a_status_change_is_raised_once()
    {
        var rig = NewRig();
        rig.Pads.Connect(0);

        rig.Run(200);

        Assert.Equal(new[] { 0 }, rig.Svc.ConnectedSlots.ToArray());
        Assert.Equal(1, rig.StatusChanges);
    }

    [Fact]
    public void Unplugging_the_pad_is_noticed_and_raised()
    {
        var rig = NewRig();
        rig.Pads.Connect(0);
        rig.Run(100);

        rig.Pads.Disconnect(0);
        rig.Run(100);

        Assert.Empty(rig.Svc.ConnectedSlots);
        Assert.Equal(2, rig.StatusChanges);
    }

    [Fact]
    public void Several_pads_are_tracked_by_slot()
    {
        var rig = NewRig();
        rig.Pads.Connect(0);
        rig.Pads.Connect(2);
        rig.Run(100);
        Assert.Equal(new[] { 0, 2 }, rig.Svc.ConnectedSlots.ToArray());
    }

    [Fact]
    public void Nothing_connected_raises_no_status_change()
    {
        var rig = NewRig();
        rig.Run(3000);
        Assert.Empty(rig.Svc.ConnectedSlots);
        Assert.Equal(0, rig.StatusChanges);
    }

    // ---- poll cost ----

    [Fact]
    public void A_connected_pad_is_read_on_every_poll()
    {
        var rig = NewRig();
        rig.Pads.Connect(0);
        rig.Run(160);                       // 11 polls (t = 0, 16, ... 160)
        Assert.Equal(11, rig.Pads.Reads(0));
    }

    [Fact]
    public void Empty_slots_are_probed_once_every_two_seconds_not_every_poll()
    {
        var rig = NewRig();
        rig.Pads.Connect(0);

        rig.Run(1900);
        Assert.Equal(1, rig.Pads.Reads(1));      // the very first poll probes every slot once
        Assert.Equal(1, rig.Pads.Reads(2));
        Assert.Equal(1, rig.Pads.Reads(3));

        rig.Run(300);                             // now past t = 2000
        Assert.Equal(2, rig.Pads.Reads(1));
        Assert.True(rig.Pads.Reads(0) > 100);
    }

    [Fact]
    public void A_pad_plugged_in_later_is_found_at_the_next_probe()
    {
        var rig = NewRig();
        rig.Run(100);
        rig.Pads.Connect(1);

        rig.Run(1500);
        Assert.Empty(rig.Svc.ConnectedSlots);    // not probed yet: only every 2 s

        rig.Run(1000);
        Assert.Equal(new[] { 1 }, rig.Svc.ConnectedSlots.ToArray());
    }

    // ---- firing ----

    [Fact]
    public void Holding_the_default_combo_fires_save_replay_once()
    {
        var rig = NewRig();
        rig.Pads.Connect(0);
        rig.Run(100);

        rig.Pads.Press(0, VM);
        rig.Run(450);
        Assert.Equal(0, rig.FiredCount());

        rig.Run(2000);                           // keeps holding
        Assert.Equal(new[] { HotkeyActions.SaveReplay }, rig.Fired.ToArray());
    }

    [Fact]
    public void The_hold_keeps_counting_while_the_pad_reports_no_new_state()
    {
        // XInput's packet number only moves when something changed; a steady hold must still complete.
        var rig = NewRig();
        rig.Pads.Connect(0);
        rig.Run(100);
        rig.Pads.Press(0, VM);                   // ONE packet, then the pad is silent
        rig.Run(700);
        Assert.Equal(1, rig.FiredCount());
    }

    [Fact]
    public void Releasing_early_fires_nothing()
    {
        var rig = NewRig();
        rig.Pads.Connect(0);
        rig.Run(100);
        rig.Pads.Press(0, VM);
        rig.Run(300);
        rig.Pads.Press(0, 0);
        rig.Run(1000);
        Assert.Equal(0, rig.FiredCount());
    }

    [Fact]
    public void Triggers_sticks_and_unknown_bits_do_not_count_as_extra_buttons()
    {
        var rig = NewRig();
        rig.Pads.Connect(0);
        rig.Run(100);
        rig.Pads.Press(0, (ushort)(VM | 0x0400 | 0x0800));   // Guide/Share-like bits XInput does not define
        rig.Run(700);
        Assert.Equal(1, rig.FiredCount());
    }

    [Fact]
    public void The_hold_time_and_bindings_come_from_the_settings()
    {
        var rig = NewRig(configure: g =>
        {
            g.HoldMs = 1000;
            g.Bindings.Clear();
            g.Bindings[HotkeyActions.Screenshot] = LBRB;
        });
        rig.Pads.Connect(0);
        rig.Run(100);

        rig.Pads.Press(0, VM);                   // no longer bound
        rig.Run(1500);
        Assert.Equal(0, rig.FiredCount());
        rig.Pads.Press(0, 0);
        rig.Run(100);

        rig.Pads.Press(0, LBRB);
        rig.Run(900);
        Assert.Equal(0, rig.FiredCount());       // 1000 ms, not 500
        rig.Run(200);
        Assert.Equal(new[] { HotkeyActions.Screenshot }, rig.Fired.ToArray());
    }

    [Fact]
    public void A_settings_change_is_picked_up_by_ReloadConfig()
    {
        var rig = NewRig();
        rig.Pads.Connect(0);
        rig.Settings.Current.Gamepad.Bindings[HotkeyActions.ToggleRecording] = LBRB;
        rig.Svc.ReloadConfig();

        rig.Run(100);
        rig.Pads.Press(0, LBRB);
        rig.Run(700);
        Assert.Equal(new[] { HotkeyActions.ToggleRecording }, rig.Fired.ToArray());
    }

    [Fact]
    public void An_unusable_stored_binding_never_fires()
    {
        var rig = NewRig(configure: g =>
        {
            g.Bindings.Clear();
            g.Bindings[HotkeyActions.SaveReplay] = GamepadButtons.A;   // one button: ignored
        });
        rig.Pads.Connect(0);
        rig.Run(100);
        rig.Pads.Press(0, GamepadButtons.A);
        rig.Run(2000);
        Assert.Equal(0, rig.FiredCount());
    }

    [Fact]
    public void Two_pads_reporting_the_same_press_fire_once()
    {
        var rig = NewRig();
        rig.Pads.Connect(0);
        rig.Pads.Connect(1);
        rig.Run(2500);                            // second slot is only probed at t=0, so connect first
        rig.Pads.Press(0, VM);
        rig.Pads.Press(1, VM);
        rig.Run(5000);
        Assert.Equal(1, rig.FiredCount());
    }

    [Fact]
    public void A_pad_that_vanishes_mid_press_forgets_the_press()
    {
        var rig = NewRig();
        rig.Pads.Connect(0, VM);
        rig.Run(300);
        rig.Pads.Disconnect(0);
        rig.Run(100);
        rig.Pads.Connect(0, VM);                  // back, buttons still down
        rig.Run(2100);                            // found again at the next probe (every 2 s)
        Assert.Equal(new[] { 0 }, rig.Svc.ConnectedSlots.ToArray());
        Assert.Equal(0, rig.FiredCount());        // the 300 ms held before the unplug must not count
        rig.Run(700);
        Assert.Equal(1, rig.FiredCount());        // a fresh half second after it was seen again
    }

    [Fact]
    public void A_throwing_subscriber_does_not_break_polling()
    {
        var rig = NewRig();
        rig.Svc.ActionTriggered += _ => throw new InvalidOperationException("handler bug");
        rig.Pads.Connect(0);
        rig.Run(100);
        rig.Pads.Press(0, VM);
        var ex = Record.Exception(() => rig.Run(700));
        Assert.Null(ex);
        Assert.Equal(1, rig.FiredCount());
    }

    // ---- learn mode ----

    [Fact]
    public void Learning_needs_a_connected_pad()
    {
        var rig = NewRig();
        rig.Run(100);
        Assert.False(rig.Svc.BeginLearn(HotkeyActions.Screenshot, _ => { }));
    }

    [Fact]
    public void Learning_captures_the_held_combo_and_fires_no_action_meanwhile()
    {
        var rig = NewRig();
        rig.Pads.Connect(0);
        rig.Run(100);
        LearnOutcome? result = null;
        Assert.True(rig.Svc.BeginLearn(HotkeyActions.Screenshot, o => result = o));

        // No bound action may fire while a combo is being learned.
        rig.Pads.Press(0, LBRB);
        rig.Run(1200);

        Assert.NotNull(result);
        Assert.Equal(LearnStatus.Captured, result!.Value.Status);
        Assert.Equal(LBRB, result.Value.Mask);
        Assert.Equal(0, rig.FiredCount());
    }

    [Fact]
    public void Learning_a_combo_that_is_bound_elsewhere_is_rejected_and_does_not_fire_it()
    {
        var rig = NewRig();
        rig.Pads.Connect(0);
        rig.Run(100);
        LearnOutcome? result = null;
        rig.Svc.BeginLearn(HotkeyActions.Screenshot, o => result = o);

        rig.Pads.Press(0, VM);
        rig.Run(1200);

        Assert.Equal(LearnStatus.AlreadyUsed, result!.Value.Status);
        Assert.Equal(HotkeyActions.SaveReplay, result.Value.UsedBy);
        Assert.Equal(0, rig.FiredCount());
    }

    [Fact]
    public void After_learning_the_still_held_buttons_do_not_fire_until_they_are_released()
    {
        var rig = NewRig();
        rig.Pads.Connect(0);
        rig.Run(100);
        LearnOutcome? result = null;
        rig.Svc.BeginLearn(HotkeyActions.SaveReplay, o => result = o);

        rig.Pads.Press(0, VM);                    // re-learn the combo that is already bound
        rig.Run(1200);
        Assert.Equal(LearnStatus.Captured, result!.Value.Status);

        rig.Run(5000);                            // still holding: nothing fires
        Assert.Equal(0, rig.FiredCount());

        rig.Pads.Press(0, 0);
        rig.Run(100);
        rig.Pads.Press(0, VM);
        rig.Run(700);
        Assert.Equal(1, rig.FiredCount());
    }

    [Fact]
    public void Learning_times_out_after_five_seconds()
    {
        var rig = NewRig();
        rig.Pads.Connect(0);
        rig.Run(100);
        LearnOutcome? result = null;
        rig.Svc.BeginLearn(HotkeyActions.Screenshot, o => result = o);

        rig.Run(4900);
        Assert.Null(result);
        rig.Run(300);
        Assert.Equal(LearnStatus.TimedOut, result!.Value.Status);
    }

    [Fact]
    public void Only_one_learn_attempt_runs_at_a_time()
    {
        var rig = NewRig();
        rig.Pads.Connect(0);
        rig.Run(100);
        Assert.True(rig.Svc.BeginLearn(HotkeyActions.Screenshot, _ => { }));
        Assert.False(rig.Svc.BeginLearn(HotkeyActions.ToggleRecording, _ => { }));
    }

    [Fact]
    public void Cancelling_reports_cancelled_and_resumes_normal_detection_after_a_release()
    {
        var rig = NewRig();
        rig.Pads.Connect(0);
        rig.Run(100);
        LearnOutcome? result = null;
        rig.Svc.BeginLearn(HotkeyActions.Screenshot, o => result = o);
        rig.Pads.Press(0, VM);
        rig.Run(300);

        rig.Svc.CancelLearn();
        Assert.Equal(LearnStatus.Cancelled, result!.Value.Status);

        rig.Run(3000);                            // buttons still held from the learn attempt
        Assert.Equal(0, rig.FiredCount());

        rig.Pads.Press(0, 0);
        rig.Run(100);
        rig.Pads.Press(0, VM);
        rig.Run(700);
        Assert.Equal(1, rig.FiredCount());
    }

    [Fact]
    public void Cancelling_without_an_attempt_does_nothing()
    {
        var rig = NewRig();
        Assert.Null(Record.Exception(() => rig.Svc.CancelLearn()));
    }

    // ---- thread lifecycle ----

    [Fact]
    public void The_poll_thread_finds_a_pad_and_stops_cleanly()
    {
        var rig = NewRig(realClock: true);
        rig.Pads.Connect(0);

        rig.Svc.Start();
        Assert.True(rig.Svc.IsRunning);
        Assert.True(WaitFor(() => rig.Svc.ConnectedSlots.Count == 1));

        rig.Svc.Stop();
        Assert.False(rig.Svc.IsRunning);
        Assert.Empty(rig.Svc.ConnectedSlots);
        Assert.Null(Record.Exception(() => { rig.Svc.Stop(); rig.Svc.Dispose(); }));
    }

    [Fact]
    public void The_poll_thread_fires_the_combo_in_real_time()
    {
        var rig = NewRig(realClock: true);
        rig.Pads.Connect(0);
        rig.Svc.Start();
        Assert.True(WaitFor(() => rig.Svc.ConnectedSlots.Count == 1));

        rig.Pads.Press(0, VM);

        Assert.True(WaitFor(() => rig.FiredCount() >= 1, 5000));
        Thread.Sleep(300);
        Assert.Equal(1, rig.FiredCount());
    }

    [Fact]
    public void Start_twice_runs_one_thread()
    {
        var rig = NewRig(realClock: true);
        rig.Svc.Start();
        rig.Svc.Start();
        Assert.True(rig.Svc.IsRunning);
        rig.Svc.Stop();
        Assert.False(rig.Svc.IsRunning);
    }

    [Fact]
    public void A_failing_source_does_not_kill_the_thread_and_recovery_is_noticed()
    {
        var rig = NewRig(realClock: true);
        rig.Pads.Throw = new InvalidOperationException("driver hiccup");
        rig.Svc.Start();

        Thread.Sleep(300);
        Assert.True(rig.Svc.IsRunning);

        rig.Pads.Throw = null;
        rig.Pads.Connect(0);
        Assert.True(WaitFor(() => rig.Svc.ConnectedSlots.Count == 1));
    }

    [Fact]
    public void ApplySettings_starts_and_stops_with_the_enabled_switch()
    {
        var rig = NewRig(realClock: true);

        rig.Svc.ApplySettings();
        Assert.True(rig.Svc.IsRunning);

        rig.Settings.Current.Gamepad.Enabled = false;
        rig.Svc.ApplySettings();
        Assert.False(rig.Svc.IsRunning);

        rig.Settings.Current.Gamepad.Enabled = true;
        rig.Svc.ApplySettings();
        Assert.True(rig.Svc.IsRunning);
    }

    [Fact]
    public void Disabled_in_the_settings_never_starts()
    {
        var rig = NewRig(realClock: true, configure: g => g.Enabled = false);
        rig.Svc.ApplySettings();
        Assert.False(rig.Svc.IsRunning);
    }

    [Fact]
    public void Without_xinput_the_feature_reports_unavailable_and_stays_off()
    {
        var rig = NewRig(realClock: true);
        rig.Pads.Available = false;

        rig.Svc.ApplySettings();
        rig.Svc.Start();

        Assert.False(rig.Svc.IsAvailable);
        Assert.False(rig.Svc.IsRunning);
    }

    [Fact]
    public void Stopping_while_learning_reports_cancelled()
    {
        var rig = NewRig(realClock: true);
        rig.Pads.Connect(0);
        rig.Svc.Start();
        Assert.True(WaitFor(() => rig.Svc.ConnectedSlots.Count == 1));

        LearnOutcome? result = null;
        Assert.True(rig.Svc.BeginLearn(HotkeyActions.Screenshot, o => result = o));
        rig.Svc.Stop();

        Assert.NotNull(result);
        Assert.Equal(LearnStatus.Cancelled, result!.Value.Status);
    }

    // ---- native layer ----

    [Fact]
    public void The_native_state_struct_has_the_xinput_layout()
    {
        Assert.Equal(16, Marshal.SizeOf<XInput.XINPUT_STATE>());
        Assert.Equal(12, Marshal.SizeOf<XInput.XINPUT_GAMEPAD>());
        Assert.Equal(4, (int)Marshal.OffsetOf<XInput.XINPUT_STATE>(nameof(XInput.XINPUT_STATE.Gamepad)));
    }

    [Fact]
    public void The_library_search_order_is_1_4_then_1_3_then_9_1_0()
    {
        Assert.Equal(new[] { "xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll" }, XInput.LibraryNames);
    }

    [Fact]
    public void The_first_library_that_resolves_wins()
    {
        var asked = new List<string>();
        var found = XInput.Resolve(XInput.LibraryNames, n => { asked.Add(n); return (IntPtr)1234; });
        Assert.Equal(("xinput1_4.dll", (IntPtr)1234), found);
        Assert.Equal(new[] { "xinput1_4.dll" }, asked);
    }

    [Fact]
    public void A_missing_library_falls_back_to_the_next_one()
    {
        var found = XInput.Resolve(XInput.LibraryNames, n => n == "xinput1_3.dll" ? (IntPtr)77 : IntPtr.Zero);
        Assert.Equal(("xinput1_3.dll", (IntPtr)77), found);

        var last = XInput.Resolve(XInput.LibraryNames, n => n == "xinput9_1_0.dll" ? (IntPtr)5 : IntPtr.Zero);
        Assert.Equal(("xinput9_1_0.dll", (IntPtr)5), last);
    }

    [Fact]
    public void No_library_at_all_means_not_available()
    {
        Assert.Null(XInput.Resolve(XInput.LibraryNames, _ => IntPtr.Zero));
    }

    [Fact]
    public void A_library_that_throws_while_loading_is_skipped()
    {
        var found = XInput.Resolve(XInput.LibraryNames, n =>
            n == "xinput1_4.dll" ? throw new BadImageFormatException("broken") : (IntPtr)9);
        Assert.Equal(("xinput1_3.dll", (IntPtr)9), found);
    }

    [Fact]
    public void The_real_source_loads_and_reads_every_slot_without_throwing()
    {
        // Works with or without a controller plugged in: a missing pad is just "not connected".
        var source = new XInputPadSource();
        Assert.Null(Record.Exception(() =>
        {
            for (int slot = 0; slot < GamepadService.SlotCount; slot++)
                source.TryRead(slot, out _);
        }));
    }

    // ---- texts ----

    [Fact]
    public void Status_text_covers_off_unavailable_none_and_connected()
    {
        Assert.Equal("Controller-Kombis sind aus", GamepadStatusText.Format(false, true, new[] { 0 }));
        Assert.Contains("nicht verfügbar", GamepadStatusText.Format(true, false, Array.Empty<int>()));
        Assert.Equal("Kein Controller", GamepadStatusText.Format(true, true, Array.Empty<int>()));
        Assert.Equal("Xbox-Controller verbunden (Slot 1)", GamepadStatusText.Format(true, true, new[] { 0 }));
        Assert.Equal("Xbox-Controller verbunden (Slots 1, 3)", GamepadStatusText.Format(true, true, new[] { 0, 2 }));
    }

    [Fact]
    public void Learn_outcomes_read_as_a_short_message()
    {
        string Label(string a) => a == HotkeyActions.SaveReplay ? "Clip speichern" : a;

        Assert.Contains("2 Tasten", GamepadStatusText.DescribeLearn(new LearnOutcome(LearnStatus.TooFewButtons), Label));
        Assert.Contains("Clip speichern", GamepadStatusText.DescribeLearn(
            new LearnOutcome(LearnStatus.AlreadyUsed, VM, HotkeyActions.SaveReplay), Label));
        Assert.False(string.IsNullOrWhiteSpace(GamepadStatusText.DescribeLearn(new LearnOutcome(LearnStatus.TimedOut), Label)));
        Assert.Contains("View + Menu", GamepadStatusText.DescribeLearn(new LearnOutcome(LearnStatus.Captured, VM), Label));
        Assert.Equal("", GamepadStatusText.DescribeLearn(new LearnOutcome(LearnStatus.Cancelled), Label));
        Assert.Equal("", GamepadStatusText.DescribeLearn(LearnOutcome.Pending, Label));
    }
}
