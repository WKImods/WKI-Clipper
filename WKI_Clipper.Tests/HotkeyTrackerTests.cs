using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.ClickerHost;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>The physical key state behind the hotkeys (everything of the hook thread that does not need Windows).</summary>
public sealed class HotkeyTrackerTests
{
    private const int Y = 0x59, F8 = 0x77;

    private sealed class Recorder
    {
        public readonly List<bool> Hotkey = new();
        public readonly List<bool> Master = new();
        public readonly HotkeyTracker Tracker = new();

        public Recorder()
        {
            Tracker.HotkeyChanged = Hotkey.Add;
            Tracker.MasterChanged = Master.Add;
        }

        public void Press(params int[] vks) { foreach (var v in vks) Tracker.OnKey(v, true); }
        public void Release(params int[] vks) { foreach (var v in vks) Tracker.OnKey(v, false); }
    }

    private static ClickerHotkey CtrlAltY() => ClickerHotkey.Default();

    [Fact]
    public void The_start_hotkey_reports_when_it_starts_and_stops_matching()
    {
        var r = new Recorder();
        r.Tracker.Configure(CtrlAltY(), null, strict: false);

        r.Press(ClickerVk.RControl, ClickerVk.LMenu);
        Assert.Empty(r.Hotkey);
        r.Press(Y);
        Assert.Equal(new[] { true }, r.Hotkey);
        r.Release(Y);
        Assert.Equal(new[] { true, false }, r.Hotkey);
    }

    [Fact]
    public void Key_auto_repeat_and_unrelated_keys_change_nothing()
    {
        var r = new Recorder();
        r.Tracker.Configure(CtrlAltY(), null, strict: false);
        r.Press(ClickerVk.LControl, ClickerVk.LMenu, Y);

        r.Press(Y, Y, Y);   // auto-repeat
        r.Press(0x41);
        r.Release(0x41);

        Assert.Equal(new[] { true }, r.Hotkey);
    }

    [Fact]
    public void Strict_mode_lets_an_extra_modifier_break_the_match()
    {
        var r = new Recorder();
        r.Tracker.Configure(CtrlAltY(), null, strict: true);
        r.Press(ClickerVk.LControl, ClickerVk.LMenu, Y);
        Assert.Equal(new[] { true }, r.Hotkey);
        r.Press(ClickerVk.LShift);
        Assert.Equal(new[] { true, false }, r.Hotkey);
    }

    [Fact]
    public void The_master_hotkey_is_tracked_next_to_the_start_hotkey()
    {
        var r = new Recorder();
        r.Tracker.Configure(CtrlAltY(), new ClickerHotkey { Keys = { F8 } }, strict: false);

        r.Press(F8);
        r.Release(F8);

        Assert.Equal(new[] { true, false }, r.Master);
        Assert.Empty(r.Hotkey);
    }

    [Fact]
    public void A_mouse_button_is_a_key_like_any_other()
    {
        var r = new Recorder();
        r.Tracker.Configure(new ClickerHotkey { Modifiers = ClickerModifiers.Ctrl, Keys = { ClickerVk.XButton1 } }, null, strict: false);

        r.Press(ClickerVk.LControl, ClickerVk.XButton1);
        r.Release(ClickerVk.XButton1);

        Assert.Equal(new[] { true, false }, r.Hotkey);
    }

    [Fact]
    public void A_new_configuration_never_reports_an_already_held_hotkey_as_pressed()
    {
        var r = new Recorder();
        r.Press(ClickerVk.LControl, ClickerVk.LMenu, Y);   // held before any hotkey is configured

        r.Tracker.Configure(CtrlAltY(), null, strict: false);
        Assert.Empty(r.Hotkey);

        r.Release(Y);   // but letting go is reported, so the activation logic does not keep a "held"
        Assert.Equal(new[] { false }, r.Hotkey);
    }

    [Fact]
    public void A_new_configuration_that_no_longer_matches_reports_the_release()
    {
        var r = new Recorder();
        r.Tracker.Configure(CtrlAltY(), null, strict: false);
        r.Press(ClickerVk.LControl, ClickerVk.LMenu, Y);

        r.Tracker.Configure(new ClickerHotkey { Keys = { F8 } }, null, strict: false);

        Assert.Equal(new[] { true, false }, r.Hotkey);
    }

    [Fact]
    public void Resync_forgets_keys_whose_release_was_missed()
    {
        var r = new Recorder();
        r.Tracker.Configure(CtrlAltY(), null, strict: false);
        r.Press(ClickerVk.LControl, ClickerVk.LMenu, Y);

        r.Tracker.Resync(vk => vk == ClickerVk.LControl);   // Windows says only Ctrl is really down

        Assert.Equal(new[] { true, false }, r.Hotkey);
        Assert.Equal(new[] { ClickerVk.LControl }, r.Tracker.DownKeys.ToArray());

        r.Tracker.Resync(vk => true);   // never adds keys
        Assert.Single(r.Tracker.DownKeys);
    }

    [Fact]
    public void Without_a_configured_hotkey_nothing_is_reported()
    {
        var r = new Recorder();
        r.Press(ClickerVk.LControl, Y);
        r.Release(Y);
        Assert.Empty(r.Hotkey);
        Assert.Empty(r.Master);
    }

    // ---- hook helpers ----

    [Theory]
    [InlineData(0x00010000u, ClickerVk.XButton1)]
    [InlineData(0x00020000u, ClickerVk.XButton2)]
    [InlineData(0x00020001u, ClickerVk.XButton2)]
    public void The_high_word_of_the_mouse_data_names_the_extra_button(uint mouseData, int expected)
        => Assert.Equal(expected, HotkeyHook.XButton(mouseData));

    [Theory]
    [InlineData(5000u, 1000u, true)]        // input long after the last hook call
    [InlineData(1200u, 1000u, false)]       // within the grace
    [InlineData(1000u, 5000u, false)]       // the hook ran after the last input
    [InlineData(5u, 4294967290u, false)]    // tick counter wrapped: 11 ms apart
    [InlineData(3000u, 4294966000u, true)]  // wrapped, 4 s apart
    public void A_hook_that_saw_nothing_while_input_arrived_is_stale(uint lastInput, uint lastHook, bool stale)
        => Assert.Equal(stale, HookHealth.IsStale(lastInput, lastHook, 1500));

    [Fact]
    public void The_warning_numbers_name_each_hook_against_its_own_input()
    {
        var ticks = new HookTicks(KeyboardInput: 10000, KeyboardHook: 4000, MouseInput: 7600, MouseHook: 7500);
        var check = new HookCheck(HookVerdict.Stale, HookVerdict.Stale, HookVerdict.Healthy);
        Assert.Equal("keyboard stale: input tick 10000, hook tick 4000 (6000 ms before the input); "
                     + "mouse healthy: input tick 7600, hook tick 7500 (100 ms before the input)", HookHealth.Describe(ticks, check));
    }

    [Fact]
    public void The_warning_says_when_a_hook_is_not_watched()
    {
        var ticks = new HookTicks(KeyboardInput: null, KeyboardHook: 4000, MouseInput: 9000, MouseHook: 1000);
        var check = new HookCheck(HookVerdict.StaleAgain, HookVerdict.Healthy, HookVerdict.StaleAgain);
        Assert.Equal("keyboard healthy: not watched, hook tick 4000; mouse stale: input tick 9000, hook tick 1000 (8000 ms before the input)",
            HookHealth.Describe(ticks, check));
    }

    [Fact]
    public void The_warning_names_the_device_behind_each_input_tick()
    {
        var ticks = new HookTicks(KeyboardInput: 10000, KeyboardHook: 9900, MouseInput: 9000, MouseHook: 1000,
                                  KeyboardDevice: System.IntPtr.Zero, MouseDevice: new System.IntPtr(0x1A2B));
        var check = new HookCheck(HookVerdict.Stale, HookVerdict.Healthy, HookVerdict.Stale);
        Assert.Equal("keyboard healthy: input tick 10000 from no device (injected), hook tick 9900 (100 ms before the input); "
                     + @"mouse stale: input tick 9000 from device 0x1A2B (\\?\HID#VID_1B1C&PID_1BAC), hook tick 1000 (8000 ms before the input)",
            HookHealth.Describe(ticks, check, handle => handle == new System.IntPtr(0x1A2B) ? @"\\?\HID#VID_1B1C&PID_1BAC" : null));
        Assert.Contains("mouse stale: input tick 9000 from device 0x1A2B, hook tick 1000", HookHealth.Describe(ticks, check, _ => null));
    }

    // ---- watchdog decisions ----

    // Both hooks with the same numbers: the per-hook rule then decides like a single hook would.
    private static HookVerdict Check(HookWatchdog dog, uint input, uint hook, bool blind = false)
        => dog.Check(new HookTicks(input, hook, input, hook), blind, graceMs: 1500).Verdict;

    private static HookCheck Check(HookWatchdog dog, uint? keyboardInput, uint keyboardHook, uint? mouseInput, uint mouseHook)
        => dog.Check(new HookTicks(keyboardInput, keyboardHook, mouseInput, mouseHook), blind: false, graceMs: 1500);

    [Fact]
    public void An_elevated_foreground_makes_the_hook_blind_not_dead()
    {
        var dog = new HookWatchdog();
        Assert.Equal(HookVerdict.Blind, Check(dog, 9000, 1000, blind: true));
        Assert.Equal(HookVerdict.Blind, Check(dog, 11000, 1000, blind: true));
    }

    [Fact]
    public void One_input_the_hooks_never_saw_is_only_suspect()
    {
        var dog = new HookWatchdog();
        Assert.Equal(HookVerdict.Suspect, Check(dog, 9000, 1000));
        Assert.Equal(HookVerdict.Suspect, Check(dog, 9000, 1000));   // nothing new arrived: still no proof
        Assert.Equal(HookVerdict.Healthy, Check(dog, 9000, 9500));   // the hooks ran again
    }

    [Fact]
    public void More_input_without_a_hook_call_is_stale_and_reported_once_per_episode()
    {
        var dog = new HookWatchdog();

        Assert.Equal(HookVerdict.Suspect, Check(dog, 9000, 1000));
        Assert.Equal(HookVerdict.Stale, Check(dog, 11000, 1000));
        Assert.Equal(HookVerdict.StaleAgain, Check(dog, 13000, 1000));
        Assert.Equal(HookVerdict.StaleAgain, Check(dog, 15000, 1000));

        Assert.Equal(HookVerdict.Healthy, Check(dog, 15000, 15000));
        Assert.Equal(HookVerdict.Suspect, Check(dog, 20000, 15000));
        Assert.Equal(HookVerdict.Stale, Check(dog, 22000, 15000));
    }

    [Fact]
    public void A_hook_is_judged_only_by_input_of_its_own_kind()
    {
        var dog = new HookWatchdog();
        // Only the mouse was used: the keyboard hook has been silent for long, but no keyboard input arrived either.
        var check = Check(dog, keyboardInput: 900, keyboardHook: 1000, mouseInput: 60000, mouseHook: 59990);
        Assert.Equal(new HookCheck(HookVerdict.Healthy, HookVerdict.Healthy, HookVerdict.Healthy), check);
    }

    [Fact]
    public void A_single_dead_hook_is_found_while_the_other_keeps_running()
    {
        var dog = new HookWatchdog();
        // Keyboard input keeps arriving without the keyboard hook; the mouse hook sees every move. A shared reference missed this.
        Assert.Equal(new HookCheck(HookVerdict.Suspect, HookVerdict.Suspect, HookVerdict.Healthy),
            Check(dog, keyboardInput: 9000, keyboardHook: 1000, mouseInput: 9000, mouseHook: 9000));
        Assert.Equal(new HookCheck(HookVerdict.Stale, HookVerdict.Stale, HookVerdict.Healthy),
            Check(dog, keyboardInput: 11000, keyboardHook: 1000, mouseInput: 11000, mouseHook: 11000));

        var mouseDog = new HookWatchdog();
        Assert.Equal(HookVerdict.Suspect, Check(mouseDog, keyboardInput: 9000, keyboardHook: 9000, mouseInput: 9000, mouseHook: 1000).Mouse);
        Assert.Equal(new HookCheck(HookVerdict.Stale, HookVerdict.Healthy, HookVerdict.Stale),
            Check(mouseDog, keyboardInput: 11000, keyboardHook: 11000, mouseInput: 11000, mouseHook: 1000));
    }

    [Fact]
    public void A_hook_going_stale_is_reported_while_the_other_ones_episode_goes_on()
    {
        var dog = new HookWatchdog();
        Check(dog, keyboardInput: 9000, keyboardHook: 1000, mouseInput: 9000, mouseHook: 9000);
        Check(dog, keyboardInput: 11000, keyboardHook: 1000, mouseInput: 11000, mouseHook: 11000);   // keyboard: Stale
        Assert.Equal(HookVerdict.Suspect, Check(dog, keyboardInput: 13000, keyboardHook: 1000, mouseInput: 13000, mouseHook: 11000).Mouse);

        var check = Check(dog, keyboardInput: 15000, keyboardHook: 1000, mouseInput: 15000, mouseHook: 11000);
        Assert.Equal(new HookCheck(HookVerdict.Stale, HookVerdict.StaleAgain, HookVerdict.Stale), check);
    }

    [Fact]
    public void A_hook_without_a_reference_is_never_stale()
    {
        var dog = new HookWatchdog();
        for (uint t = 10000; t <= 20000; t += 2000)
            Assert.Equal(HookVerdict.Healthy, Check(dog, keyboardInput: null, keyboardHook: 1000, mouseInput: t, mouseHook: t).Verdict);
    }

    [Fact]
    public void The_per_hook_rule_holds_across_a_tick_wrap()
    {
        var dog = new HookWatchdog();
        Assert.Equal(HookVerdict.Suspect, Check(dog, keyboardInput: 3000, keyboardHook: 4294966000u, mouseInput: 5, mouseHook: 4294967290u).Verdict);
        Assert.Equal(HookVerdict.Healthy, Check(dog, keyboardInput: 3000, keyboardHook: 2900, mouseInput: 5, mouseHook: 4294967290u).Keyboard);
    }

    [Fact]
    public void Being_blind_ends_a_stale_episode_and_a_suspicion()
    {
        var dog = new HookWatchdog();
        Check(dog, 9000, 1000);
        Assert.Equal(HookVerdict.Stale, Check(dog, 10000, 1000));
        Assert.Equal(HookVerdict.Blind, Check(dog, 11000, 1000, blind: true));
        Assert.Equal(HookVerdict.Suspect, Check(dog, 13000, 1000));
    }

    // ---- the mouse reference without raw input ----

    [Fact]
    public void A_cursor_that_moved_while_the_mouse_hook_ran_is_no_evidence()
    {
        var cursor = new CursorMoveReference();
        Assert.Null(cursor.Observe(100, 100, now: 2000, mouseHookTick: 0));   // first sight: a baseline, not a move
        Assert.Null(cursor.Observe(300, 100, now: 4000, mouseHookTick: 3500));
        Assert.Null(cursor.Observe(300, 100, now: 6000, mouseHookTick: 3500));   // the cursor stood still
    }

    [Fact]
    public void A_cursor_that_moved_without_a_mouse_hook_event_is_the_reference()
    {
        var cursor = new CursorMoveReference();
        cursor.Observe(100, 100, now: 2000, mouseHookTick: 1000);
        Assert.Equal(4000u, cursor.Observe(150, 100, now: 4000, mouseHookTick: 1000));
        Assert.Equal(4000u, cursor.Observe(150, 100, now: 6000, mouseHookTick: 1000));   // kept while nothing new happens
        Assert.Equal(8000u, cursor.Observe(150, 120, now: 8000, mouseHookTick: 1000));
    }

    [Fact]
    public void Two_cursor_moves_without_the_mouse_hook_make_it_stale()
    {
        var cursor = new CursorMoveReference();
        var dog = new HookWatchdog();
        HookVerdict Step(int x, uint now) => dog.Check(new HookTicks(null, 1000, cursor.Observe(x, 0, now, 1000), 1000), false, 1500).Verdict;

        Assert.Equal(HookVerdict.Healthy, Step(0, 2000));
        Assert.Equal(HookVerdict.Suspect, Step(10, 4000));
        Assert.Equal(HookVerdict.Suspect, Step(10, 6000));
        Assert.Equal(HookVerdict.Stale, Step(20, 8000));
    }

    [Fact]
    public void An_unreadable_cursor_starts_a_new_baseline()
    {
        var cursor = new CursorMoveReference();
        cursor.Observe(100, 100, now: 2000, mouseHookTick: 1000);
        cursor.Forget();
        Assert.Null(cursor.Observe(500, 500, now: 4000, mouseHookTick: 1000));
    }

    [Fact]
    public void The_cursor_reference_holds_across_a_tick_wrap()
    {
        var cursor = new CursorMoveReference();
        cursor.Observe(0, 0, now: 4294967000u, mouseHookTick: 4294966000u);
        Assert.Null(cursor.Observe(5, 0, now: 1704, mouseHookTick: 100));   // the hook ran after the previous check, past the wrap
        Assert.Equal(3704u, cursor.Observe(9, 0, now: 3704, mouseHookTick: 100));
    }

    // ---- what the mouse hook records ----

    [Theory]
    [InlineData(0x0201, ClickerVk.LButton, true)]
    [InlineData(0x0202, ClickerVk.LButton, false)]
    [InlineData(0x0204, ClickerVk.RButton, true)]
    [InlineData(0x0208, ClickerVk.MButton, false)]
    public void Mouse_buttons_become_keys(int message, int vk, bool down)
    {
        var input = HotkeyHook.MouseInput(message, 0, 42);
        Assert.Equal(HookInput.Key(vk, down, 42), input);
    }

    [Theory]
    [InlineData(0x020A, 120, ClickerVk.WheelUp)]
    [InlineData(0x020A, -120, ClickerVk.WheelDown)]
    [InlineData(0x020A, 15, ClickerVk.WheelUp)]       // smooth-scrolling fraction of a notch
    [InlineData(0x020E, 120, ClickerVk.WheelRight)]
    [InlineData(0x020E, -240, ClickerVk.WheelLeft)]
    public void Wheel_messages_become_notches_of_their_direction(int message, int delta, int wheel)
    {
        uint mouseData = unchecked((uint)(delta << 16));
        Assert.Equal(HookInput.Wheel(wheel, 7), HotkeyHook.MouseInput(message, mouseData, 7));
    }

    [Fact]
    public void A_zero_wheel_delta_and_other_messages_record_nothing()
    {
        Assert.Null(HotkeyHook.MouseInput(0x020A, 0, 1));
        Assert.Null(HotkeyHook.MouseInput(0x0200, 0, 1));   // WM_MOUSEMOVE
    }

    // ---- delivery order ----

    [Fact]
    public async System.Threading.Tasks.Task Events_of_concurrent_callers_are_delivered_in_the_order_they_were_computed()
    {
        const int F8Key = F8;
        var tracker = new HotkeyTracker();
        var log = new List<string>();
        var entered = new System.Threading.ManualResetEventSlim();
        var release = new System.Threading.ManualResetEventSlim();
        bool block = false;
        tracker.HotkeyChanged = on =>
        {
            lock (log) log.Add("hotkey:" + on);
            if (!on && block)
            {
                entered.Set();
                release.Wait(5000);
            }
        };
        tracker.MasterChanged = on => { lock (log) log.Add("master:" + on); };
        tracker.Configure(new ClickerHotkey { Keys = { Y } }, new ClickerHotkey { Keys = { F8Key } }, strict: false);
        tracker.OnKey(Y, true);

        block = true;
        var configure = System.Threading.Tasks.Task.Run(() => tracker.Configure(null, new ClickerHotkey { Keys = { F8Key } }, strict: false));
        Assert.True(entered.Wait(5000), "the configure call should be inside its callback");
        var press = System.Threading.Tasks.Task.Run(() => tracker.OnKey(F8Key, true));
        System.Threading.Thread.Sleep(150);   // long enough for the press to overtake if nothing serialized the two
        release.Set();
        await System.Threading.Tasks.Task.WhenAll(configure, press).WaitAsync(System.TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "hotkey:True", "hotkey:False", "master:True" }, log.ToArray());
    }
}
