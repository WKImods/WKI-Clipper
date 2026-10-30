using System;
using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.ClickerHost;
using WKI_Clipper.Models;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Messages, hotkeys, locks, pickers and the start-zone monitor through the activation logic to the engine (fake) and back to the clipper.</summary>
public sealed class HostCoordinatorTests
{
    private const int Y = 0x59, F8 = 0x77;

    private sealed class Rig
    {
        public readonly FakeEngine Engine = new();
        public readonly HotkeyTracker Tracker = new();
        public readonly ZoneTracker Zones = new();
        public readonly List<ClickerMessage> Sent = new();
        public PixelPoint Cursor = new(0, 0);
        public bool OverClipper;
        public readonly HostCoordinator Coordinator;
        public ClickerSettings Settings = new() { Armed = true };   // the helper honours "Clicker bereit" since the mouse patterns

        public Rig(Action<ClickerSettings>? configure = null)
        {
            configure?.Invoke(Settings);
            Coordinator = new HostCoordinator(Engine, Tracker, Zones, () => Cursor, Sent.Add, () => OverClipper);
            Push();
        }

        public void Push() => Coordinator.Handle(new ConfigMessage(Settings));

        public void HotkeyDown() { Tracker.OnKey(ClickerVk.LControl, true); Tracker.OnKey(ClickerVk.LMenu, true); Tracker.OnKey(Y, true); }
        public void HotkeyUp() { Tracker.OnKey(Y, false); Tracker.OnKey(ClickerVk.LControl, false); Tracker.OnKey(ClickerVk.LMenu, false); }
        public void Press(int vk) => Tracker.OnKey(vk, true);
        public void Release(int vk) => Tracker.OnKey(vk, false);

        public IEnumerable<StateMessage> States => Sent.OfType<StateMessage>();
    }

    private static ClickerZone StartZone() => new() { Id = "s", X = 100, Y = 100, W = 100, H = 100, Action = ZoneAction.Start };

    // ---- hotkeys ----

    [Fact]
    public void In_toggle_mode_the_hotkey_starts_and_stops_and_the_start_waits_for_the_modifiers()
    {
        var r = new Rig();

        r.HotkeyDown();
        Assert.Single(r.Engine.Starts);
        Assert.True(r.Engine.Starts[0].WaitForHotkeyRelease);
        r.HotkeyUp();
        r.HotkeyDown();

        Assert.Equal(new[] { StopReason.Hotkey }, r.Engine.Stops.ToArray());
    }

    [Fact]
    public void In_hold_mode_pressing_starts_releasing_stops_and_nothing_waits()
    {
        var r = new Rig(s => s.ActivationMode = ActivationMode.Hold);

        r.HotkeyDown();
        Assert.False(r.Engine.Starts.Single().WaitForHotkeyRelease);
        r.Release(Y);

        Assert.Equal(new[] { StopReason.HoldReleased }, r.Engine.Stops.ToArray());
    }

    [Fact]
    public void A_configuration_push_never_starts_a_hotkey_that_is_already_held()
    {
        var r = new Rig();
        r.HotkeyDown();
        r.Engine.End(StopReason.Manual);
        r.Engine.Starts.Clear();

        r.Settings.ClickSpeed = 40;
        r.Push();

        Assert.Empty(r.Engine.Starts);
        Assert.Equal(2, r.Engine.Configs.Count);
    }

    [Fact]
    public void An_engine_that_ended_on_its_own_does_not_leave_the_next_press_as_a_stop()
    {
        var r = new Rig();
        r.HotkeyDown();
        r.HotkeyUp();
        r.Engine.End(StopReason.ClickLimit);

        r.HotkeyDown();

        Assert.Equal(2, r.Engine.Starts.Count);
        Assert.Empty(r.Engine.Stops);
    }

    [Fact]
    public void A_refused_start_leaves_the_machine_idle_so_the_next_press_tries_again()
    {
        var r = new Rig();
        r.Engine.StartStatus = EngineStartStatus.NoOutputKey;
        r.HotkeyDown();
        r.HotkeyUp();

        r.Engine.StartStatus = EngineStartStatus.Started;
        r.HotkeyDown();

        Assert.Equal(2, r.Engine.Starts.Count);
        Assert.True(r.Engine.Running);
        Assert.Empty(r.Engine.Stops);
    }

    // ---- mouse hotkeys over the clipper ----

    private static ClickerHotkey ShiftLeftClick() => new() { Modifiers = ClickerModifiers.Shift, Keys = { ClickerVk.LButton } };

    private static void ShiftClickDown(Rig r) { r.Press(ClickerVk.LShift); r.Press(ClickerVk.LButton); }

    private static void ShiftClickUp(Rig r) { r.Release(ClickerVk.LButton); r.Release(ClickerVk.LShift); }

    [Fact]
    public void A_mouse_hotkey_pressed_over_the_clipper_does_not_start()
    {
        var r = new Rig(s => s.Hotkey = ShiftLeftClick());
        r.OverClipper = true;

        ShiftClickDown(r);

        Assert.Empty(r.Engine.Starts);
    }

    [Fact]
    public void A_mouse_hotkey_pressed_elsewhere_starts()
    {
        var r = new Rig(s => s.Hotkey = ShiftLeftClick());

        ShiftClickDown(r);

        Assert.Single(r.Engine.Starts);
    }

    [Fact]
    public void A_keyboard_hotkey_starts_over_the_clipper_too()
    {
        var r = new Rig();
        r.OverClipper = true;

        r.HotkeyDown();

        Assert.Single(r.Engine.Starts);
    }

    [Fact]
    public void Releasing_a_held_mouse_hotkey_over_the_clipper_still_stops()
    {
        var r = new Rig(s => { s.Hotkey = ShiftLeftClick(); s.ActivationMode = ActivationMode.Hold; });
        ShiftClickDown(r);
        Assert.Single(r.Engine.Starts);

        r.OverClipper = true;
        ShiftClickUp(r);

        Assert.Single(r.Engine.Stops);
    }

    [Fact]
    public void A_mouse_master_pressed_over_the_clipper_is_ignored()
    {
        var r = new Rig(s => s.MasterHotkey = new ClickerHotkey { Keys = { ClickerVk.XButton1 } });
        r.OverClipper = true;

        r.Press(ClickerVk.XButton1);
        r.Release(ClickerVk.XButton1);

        Assert.False(r.States.Last().MasterAllowed);
    }

    [Fact]
    public void A_wheel_hotkey_scrolled_over_the_clipper_does_not_start()
    {
        var r = new Rig(s => s.Hotkey = new ClickerHotkey { Keys = { ClickerVk.WheelDown } });
        r.OverClipper = true;
        r.Press(ClickerVk.WheelDown);
        Assert.Empty(r.Engine.Starts);

        r.Release(ClickerVk.WheelDown);
        r.OverClipper = false;
        r.Press(ClickerVk.WheelDown);
        Assert.Single(r.Engine.Starts);
    }

    [Fact]
    public void A_bare_left_button_hotkey_over_the_clipper_does_not_start()
    {
        var r = new Rig(s => s.Hotkey = new ClickerHotkey { Keys = { ClickerVk.LButton } });
        r.OverClipper = true;
        r.Press(ClickerVk.LButton);
        Assert.Empty(r.Engine.Starts);
    }

    [Fact]
    public void A_bare_left_button_toggles_on_one_click_and_off_on_the_next()
    {
        var r = new Rig(s => s.Hotkey = new ClickerHotkey { Keys = { ClickerVk.LButton } });

        r.Press(ClickerVk.LButton);
        r.Release(ClickerVk.LButton);
        Assert.Single(r.Engine.Starts);
        Assert.Empty(r.Engine.Stops);

        r.Press(ClickerVk.LButton);
        Assert.Equal(new[] { StopReason.Hotkey }, r.Engine.Stops.ToArray());
    }

    [Fact]
    public void A_modifier_only_hotkey_starts_on_its_modifiers_and_waits_for_their_release()
    {
        var r = new Rig(s => s.Hotkey = new ClickerHotkey { Modifiers = ClickerModifiers.LCtrl | ClickerModifiers.RAlt });

        r.Press(ClickerVk.LControl);
        Assert.Empty(r.Engine.Starts);
        r.Press(ClickerVk.RMenu);   // AltGr

        Assert.True(Assert.Single(r.Engine.Starts).WaitForHotkeyRelease);
    }

    [Fact]
    public void A_wheel_hold_hotkey_clicks_while_scrolling_and_stops_after_the_quiet_period()
    {
        var r = new Rig(s => { s.Hotkey = new ClickerHotkey { Keys = { ClickerVk.WheelUp } }; s.ActivationMode = ActivationMode.Hold; });
        var pump = new HotkeyPump(r.Tracker, _ => false);

        for (long t = 0; t <= 600; t += 120)
        {
            pump.Post(HookInput.Wheel(ClickerVk.WheelUp, t));
            pump.Drain(t);
        }
        Assert.Single(r.Engine.Starts);
        Assert.Empty(r.Engine.Stops);

        pump.Drain(600 + WheelHold.QuietMs);
        Assert.Equal(new[] { StopReason.HoldReleased }, r.Engine.Stops.ToArray());
    }

    [Fact]
    public void A_wheel_toggle_hotkey_flips_once_per_scroll_gesture()
    {
        var r = new Rig(s => s.Hotkey = new ClickerHotkey { Keys = { ClickerVk.WheelDown } });
        var pump = new HotkeyPump(r.Tracker, _ => false);
        void Gesture(long start)
        {
            for (long t = start; t < start + 500; t += 50)
            {
                pump.Post(HookInput.Wheel(ClickerVk.WheelDown, t));
                pump.Drain(t);
            }
            pump.Drain(start + 500 + WheelHold.QuietMs);
        }

        Gesture(0);
        Assert.Single(r.Engine.Starts);
        Assert.Empty(r.Engine.Stops);

        Gesture(2000);
        Assert.Equal(new[] { StopReason.Hotkey }, r.Engine.Stops.ToArray());
    }

    // ---- master switch ----

    [Fact]
    public void A_toggle_master_starts_off_and_switching_it_off_again_stops_the_run()
    {
        var r = new Rig(s => s.MasterHotkey = new ClickerHotkey { Keys = { F8 } });

        r.HotkeyDown();
        r.HotkeyUp();
        Assert.Empty(r.Engine.Starts);   // master off

        r.Press(F8);
        r.Release(F8);
        r.HotkeyDown();
        Assert.Single(r.Engine.Starts);

        r.Press(F8);   // master off again
        Assert.Equal(new[] { StopReason.MasterSwitch }, r.Engine.Stops.ToArray());
    }

    [Fact]
    public void The_state_tells_the_clipper_whether_the_master_switch_allows_starts()
    {
        var r = new Rig(s => s.MasterHotkey = new ClickerHotkey { Keys = { F8 } });
        Assert.False(r.States.Last().MasterAllowed);   // a toggle master starts off

        r.Press(F8);
        Assert.True(r.States.Last().MasterAllowed);
        r.Release(F8);
        r.Press(F8);
        Assert.False(r.States.Last().MasterAllowed);

        r.Settings.MasterHotkey = null;   // no master: always allowed
        r.Push();
        Assert.True(r.States.Last().MasterAllowed);
    }

    // ---- lock and picker ----

    [Fact]
    public void A_lock_stops_a_run_refuses_starts_and_is_confirmed_in_the_state()
    {
        var r = new Rig();
        r.HotkeyDown();
        r.HotkeyUp();
        r.Sent.Clear();

        r.Coordinator.Handle(new LockMessage("game.exe"));

        Assert.Equal(new[] { StopReason.Locked }, r.Engine.Stops.ToArray());
        Assert.True(r.Engine.Locked);
        Assert.True(r.States.Last().Locked);

        r.Engine.End(StopReason.Locked);
        r.Engine.Starts.Clear();
        r.HotkeyDown();
        Assert.Empty(r.Engine.Starts);

        r.HotkeyUp();
        r.Coordinator.Handle(new UnlockMessage());
        Assert.False(r.States.Last().Locked);
        r.HotkeyDown();
        Assert.Single(r.Engine.Starts);
    }

    [Fact]
    public void An_open_picker_stops_the_run_and_blocks_starts_until_it_closes()
    {
        var r = new Rig();
        r.HotkeyDown();
        r.HotkeyUp();

        r.Coordinator.Handle(new PickerMessage(true));
        Assert.Equal(new[] { StopReason.PickerOpened }, r.Engine.Stops.ToArray());

        r.Engine.End(StopReason.PickerOpened);
        r.Engine.Starts.Clear();
        r.HotkeyDown();
        r.HotkeyUp();
        Assert.Empty(r.Engine.Starts);

        r.Coordinator.Handle(new PickerMessage(false));
        r.HotkeyDown();
        Assert.Single(r.Engine.Starts);
    }

    // ---- messages from the clipper ----

    [Fact]
    public void Start_and_stop_from_the_ui_use_the_same_rules_without_waiting_for_modifiers()
    {
        var r = new Rig();

        r.Coordinator.Handle(new StartMessage());
        Assert.False(r.Engine.Starts.Single().WaitForHotkeyRelease);

        r.Coordinator.Handle(new StopMessage());
        Assert.Equal(new[] { StopReason.Manual }, r.Engine.Stops.ToArray());
    }

    [Fact]
    public void A_start_from_the_ui_is_refused_while_locked()
    {
        var r = new Rig();
        r.Coordinator.Handle(new LockMessage("x"));
        r.Coordinator.Handle(new StartMessage());
        Assert.Empty(r.Engine.Starts);
    }

    [Fact]
    public void The_configuration_is_normalised_before_it_reaches_the_engine()
    {
        var r = new Rig();
        r.Settings.ClickSpeed = 99999;
        r.Push();
        Assert.Equal(500, r.Engine.Configs.Last().ClickSpeed);
    }

    [Fact]
    public void A_hello_from_the_clipper_is_answered_with_the_state()
    {
        var r = new Rig();
        r.Sent.Clear();

        r.Coordinator.Handle(new HelloMessage(1));

        var state = Assert.IsType<StateMessage>(Assert.Single(r.Sent));
        Assert.True(state.Armed);
        Assert.False(state.Running);
        Assert.False(state.Locked);
    }

    // ---- reports ----

    [Fact]
    public void A_finished_run_is_reported_with_its_reason_and_the_state_names_it()
    {
        var r = new Rig();
        r.HotkeyDown();
        r.Sent.Clear();

        r.Engine.End(StopReason.ClickLimit, clicks: 321);

        var ended = r.Sent.OfType<RunEndedMessage>().Single();
        Assert.Equal("ClickLimit", ended.Reason);
        Assert.Equal(321, ended.Clicks);
        var last = r.States.Last();
        Assert.False(last.Running);
        Assert.Equal("ClickLimit", last.Reason);
        Assert.Equal(321, last.Clicks);
    }

    [Fact]
    public void An_unchanged_idle_state_is_not_sent_again()
    {
        var r = new Rig();
        r.Sent.Clear();
        r.Push();
        r.Push();
        Assert.Empty(r.States);
    }

    [Fact]
    public void While_running_the_state_has_no_stop_reason()
    {
        var r = new Rig();
        r.HotkeyDown();
        r.HotkeyUp();
        r.Engine.End(StopReason.Hotkey);
        r.HotkeyDown();   // a new run

        Assert.True(r.States.Last().Running);
        Assert.Null(r.States.Last().Reason);
    }

    // ---- start zone ----

    [Fact]
    public void A_start_zone_starts_on_entering_and_stops_a_zone_started_run_on_leaving()
    {
        var r = new Rig(s =>
        {
            s.ZonesEnabled = true;
            s.Zones = new() { StartZone() };
        });

        r.Coordinator.PollStartZone();                     // baseline outside
        r.Cursor = new PixelPoint(150, 150);
        r.Coordinator.PollStartZone();                     // enters
        Assert.False(r.Engine.Starts.Single().WaitForHotkeyRelease);
        Assert.True(r.Engine.Running);

        r.Cursor = new PixelPoint(0, 0);
        r.Coordinator.PollStartZone();                     // leaves
        Assert.Equal(new[] { StopReason.LeftStartZone }, r.Engine.Stops.ToArray());
    }

    [Fact]
    public void A_run_started_by_hotkey_is_not_stopped_by_leaving_a_start_zone()
    {
        var r = new Rig(s =>
        {
            s.ZonesEnabled = true;
            s.Zones = new() { StartZone() };
        });
        r.Coordinator.PollStartZone();
        r.Cursor = new PixelPoint(150, 150);
        r.HotkeyDown();                                    // started by hotkey while the cursor sits in the zone
        r.Coordinator.PollStartZone();
        r.Cursor = new PixelPoint(0, 0);
        r.Coordinator.PollStartZone();

        Assert.Empty(r.Engine.Stops);
    }

    [Fact]
    public void A_start_zone_respects_the_lock_and_does_not_retry_while_the_cursor_stays_inside()
    {
        var r = new Rig(s =>
        {
            s.ZonesEnabled = true;
            s.Zones = new() { StartZone() };
        });
        r.Coordinator.Handle(new LockMessage("x"));
        r.Coordinator.PollStartZone();
        r.Cursor = new PixelPoint(150, 150);
        r.Coordinator.PollStartZone();
        r.Coordinator.Handle(new UnlockMessage());
        r.Coordinator.PollStartZone();                     // still inside, the entry was used up

        Assert.Empty(r.Engine.Starts);
    }

    [Fact]
    public void A_start_the_engine_refuses_is_handed_back_to_the_zone_tracker()
    {
        var r = new Rig(s =>
        {
            s.ZonesEnabled = true;
            s.Zones = new() { StartZone() };
        });
        r.Engine.StartStatus = EngineStartStatus.EmptyWhitelist;
        r.Coordinator.PollStartZone();
        r.Cursor = new PixelPoint(150, 150);
        r.Coordinator.PollStartZone();
        Assert.False(r.Zones.StartedByZone);

        r.Cursor = new PixelPoint(0, 0);
        r.Coordinator.PollStartZone();
        Assert.Empty(r.Engine.Stops);
    }

    // ---- shutdown ----

    [Fact]
    public void Shutdown_locks_and_stops_a_running_engine()
    {
        var r = new Rig();
        r.Coordinator.Handle(new StartMessage());

        r.Coordinator.Shutdown(System.TimeSpan.FromMilliseconds(20));

        Assert.True(r.Engine.Locked);
        Assert.Equal(new[] { StopReason.ClipperClosed }, r.Engine.Stops.ToArray());
    }

    [Fact]
    public async System.Threading.Tasks.Task Shutdown_waits_for_the_run_ended_message_not_just_for_the_engine_to_be_idle()
    {
        var r = new Rig();
        r.Coordinator.Handle(new StartMessage());
        r.Engine.Running = false;   // IsRunning drops before the run-ended handler has queued its message
        var late = System.Threading.Tasks.Task.Run(() =>
        {
            System.Threading.Thread.Sleep(150);
            r.Engine.End(StopReason.ClipperClosed);
        });

        r.Coordinator.Shutdown(System.TimeSpan.FromSeconds(5));

        Assert.Single(r.Sent.OfType<RunEndedMessage>());
        await late.WaitAsync(System.TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Shutdown_without_a_run_returns_at_once()
    {
        var r = new Rig();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        r.Coordinator.Shutdown(System.TimeSpan.FromSeconds(5));

        Assert.True(watch.ElapsedMilliseconds < 1000);
    }

    [Fact]
    public void A_start_the_engine_refuses_does_not_make_shutdown_wait()
    {
        var r = new Rig();
        r.Engine.StartStatus = EngineStartStatus.NoOutputKey;
        r.Coordinator.Handle(new StartMessage());
        var watch = System.Diagnostics.Stopwatch.StartNew();

        r.Coordinator.Shutdown(System.TimeSpan.FromSeconds(5));

        Assert.True(watch.ElapsedMilliseconds < 1000);
    }

    // ---- start guard ----

    [Fact]
    public void A_start_refused_for_an_empty_whitelist_leaves_the_machine_idle()
    {
        var r = new Rig();
        r.Engine.StartStatus = EngineStartStatus.EmptyWhitelist;
        r.HotkeyDown();
        r.HotkeyUp();
        Assert.False(r.Engine.Running);

        r.Engine.StartStatus = EngineStartStatus.Started;
        r.HotkeyDown();

        Assert.Equal(2, r.Engine.Starts.Count);
        Assert.True(r.Engine.Running);
        Assert.Empty(r.Engine.Stops);
    }

    // ---- the clicker's own switch and the mouse patterns (mouse-patterns design 4.5, 4.6) ----

    [Fact]
    public void Clicker_hotkey_dead_when_only_patterns_armed()
    {
        var r = new Rig(s =>
        {
            s.Armed = false;
            s.MousePatternsArmed = true;
            s.ZonesEnabled = true;
            s.Zones = new() { StartZone() };
        });

        r.HotkeyDown();
        r.Coordinator.Handle(new StartMessage());
        r.Coordinator.PollStartZone();
        r.Cursor = new PixelPoint(150, 150);
        r.Coordinator.PollStartZone();

        Assert.Empty(r.Engine.Starts);
        Assert.False(r.States.Last().Armed);

        r.HotkeyUp();
        r.Settings.Armed = true;
        r.Push();
        r.HotkeyDown();
        Assert.Single(r.Engine.Starts);

        r.Settings.Armed = false;         // switched off while it runs (the helper stays for the patterns)
        r.Push();
        Assert.Equal(new[] { StopReason.Manual }, r.Engine.Stops.ToArray());
    }

    private const string PatternId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>A coordinator with real mouse patterns behind it; every message is logged with what played and recorded at that moment.</summary>
    private sealed class PatternRig : IDisposable
    {
        public readonly FakeClock Clock = new() { Now = 1000 };
        public readonly FakeEnvironment Host = new();
        public readonly FakeEngine Engine = new();
        public readonly HotkeyTracker Tracker = new();
        public readonly PatternPlayer Player;
        public readonly PatternRecorder Recorder;
        public readonly PatternCoordinator Patterns;
        public readonly HostCoordinator Coordinator;
        public readonly List<(ClickerMessage Message, int Playing, bool Recording)> Sent = new();
        public readonly ClickerSettings Settings = new() { Armed = true, MousePatternsArmed = true };

        public PatternRig()
        {
            Player = new PatternPlayer(new FakeInput(Clock, Host), new FakeWaiter(Clock), Clock, new FakePatternEnvironment(Host), ownThread: false);
            Recorder = new PatternRecorder(Clock, new ManualTime());
            Patterns = new PatternCoordinator(Player, Recorder, () => Host.Fg, () => false, Send);
            Coordinator = new HostCoordinator(Engine, Tracker, new ZoneTracker(), () => Host.Cursor, Send, () => false, Patterns);
            Settings.MousePatterns.Add(new MousePattern { Id = PatternId, Name = "Kreis", Hotkey = new ClickerHotkey { Keys = { F8 } }, Repeat = true });
            Coordinator.Handle(new ConfigMessage(Settings));
        }

        private void Send(ClickerMessage message)
        {
            lock (Sent) Sent.Add((message, Player.Playing.Count, Recorder.IsRecording));
        }

        public static string Samples()
        {
            var track = new PatternTrack(Enumerable.Range(0, 50).ToArray(), Enumerable.Repeat(1, 50).ToArray(), new int[50]);
            return PatternCodec.Encode(new PatternData(track, track, 49));
        }

        public void RecordPress()
        {
            foreach (var vk in new[] { ClickerVk.LControl, ClickerVk.LMenu, 0x52 }) Tracker.OnKey(vk, true);
            foreach (var vk in new[] { 0x52, ClickerVk.LMenu, ClickerVk.LControl }) Tracker.OnKey(vk, false);
        }

        public IEnumerable<StateMessage> States => Sent.Select(s => s.Message).OfType<StateMessage>();

        public void Dispose()
        {
            Recorder.Dispose();
            Player.Dispose();
        }
    }

    [Fact]
    public void Lock_confirmation_after_patterns_stopped()
    {
        using var r = new PatternRig();
        r.Coordinator.Handle(new PatternDataMessage(PatternId, 0, PatternRig.Samples()));
        r.Tracker.OnKey(F8, true);
        Assert.True(r.Player.IsPlaying(PatternId));
        r.Sent.Clear();

        r.Coordinator.Handle(new LockMessage("game.exe"));

        var confirmation = r.Sent.First(s => s.Message is StateMessage { Locked: true });
        Assert.Equal(0, confirmation.Playing);   // stopped before the clipper hears "locked"
        Assert.Null(((StateMessage)confirmation.Message).Playing);
        r.Tracker.OnKey(F8, false);

        r.Coordinator.Handle(new UnlockMessage());
        r.RecordPress();
        Assert.True(r.Recorder.IsRecording);
        r.Sent.Clear();
        r.Coordinator.Handle(new LockMessage("game.exe"));

        int cancelled = r.Sent.FindIndex(s => s.Message is PatternRecordedMessage { Outcome: "Cancelled" });
        int locked = r.Sent.FindIndex(s => s.Message is StateMessage { Locked: true });
        Assert.InRange(cancelled, 0, locked - 1);
        Assert.False(r.Sent[locked].Recording);
        Assert.False(((StateMessage)r.Sent[locked].Message).Recording);
    }

    [Fact]
    public void Pattern_samples_from_the_clipper_reach_the_patterns_and_garbage_is_ignored()
    {
        using var r = new PatternRig();

        r.Coordinator.Handle(new PatternDataMessage(PatternId, 0, "p1:not base64!"));
        r.Tracker.OnKey(F8, true);
        Assert.False(r.Player.IsPlaying(PatternId));
        r.Tracker.OnKey(F8, false);

        r.Coordinator.Handle(new PatternDataMessage(PatternId, 0, PatternRig.Samples()));
        r.Tracker.OnKey(F8, true);
        Assert.True(r.Player.IsPlaying(PatternId));
        Assert.Equal(PatternId, r.States.Last().Playing);

        r.Tracker.OnKey(F8, false);
        Assert.Null(r.States.Last().Playing);
        r.RecordPress();
        Assert.True(r.States.Last().Recording);
    }

    [Fact]
    public void The_clicker_and_a_pattern_on_the_same_hotkey_start_together()
    {
        using var r = new PatternRig();
        r.Settings.Hotkey = new ClickerHotkey { Keys = { F8 } };
        r.Settings.ActivationMode = ActivationMode.Hold;
        r.Coordinator.Handle(new ConfigMessage(r.Settings));
        r.Coordinator.Handle(new PatternDataMessage(PatternId, 0, PatternRig.Samples()));

        r.Tracker.OnKey(F8, true);
        Assert.Single(r.Engine.Starts);
        Assert.True(r.Player.IsPlaying(PatternId));
        Assert.True(r.States.Last().Running);   // the pattern does not change the clicker's state

        r.Tracker.OnKey(F8, false);
        Assert.Equal(new[] { StopReason.HoldReleased }, r.Engine.Stops.ToArray());
        Assert.False(r.Player.IsPlaying(PatternId));
    }

    // ---- ordering of what goes to the clipper ----

    [Fact]
    public async System.Threading.Tasks.Task States_reach_the_clipper_in_the_order_they_were_read_even_when_two_threads_publish()
    {
        var engine = new FakeEngine();
        var sent = new List<ClickerMessage>();
        int active = 0, maxActive = 0;
        System.Threading.Tasks.Task? other = null;
        bool first = true;
        HostCoordinator? coordinator = null;
        void Send(ClickerMessage m)
        {
            int now = System.Threading.Interlocked.Increment(ref active);
            lock (sent)
            {
                maxActive = Math.Max(maxActive, now);
                sent.Add(m);
            }
            if (m is StateMessage { Running: true } && first)
            {
                first = false;
                // The engine thread ends the run while this (stale, running) state is on its way out.
                other = System.Threading.Tasks.Task.Run(() => engine.End(StopReason.Manual));
                System.Threading.Thread.Sleep(150);
            }
            System.Threading.Interlocked.Decrement(ref active);
        }
        coordinator = new HostCoordinator(engine, new HotkeyTracker(), new ZoneTracker(), () => new PixelPoint(0, 0), Send);
        engine.Running = true;
        engine.Current = new EngineSnapshot(true, false, 3, 25, 100, null);

        coordinator.Handle(new HelloMessage(ClickerProtocol.Version, 1));
        await other!.WaitAsync(System.TimeSpan.FromSeconds(5));

        var states = sent.OfType<StateMessage>().ToList();
        Assert.Equal(1, maxActive);
        Assert.False(states[^1].Running);
        Assert.True(states[0].Running);
    }
}
