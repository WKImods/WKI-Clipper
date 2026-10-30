using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>The pure parts of the AutoClicker tab: hotkey recording and its window-message decoding, number fields, the Start/Stop button and the run list texts.</summary>
public sealed class ClickerUiRulesTests
{
    private const int A = 0x41, Y = 0x59, F8 = 0x77, Esc = 0x1B, Back = 0x08;

    private static HotkeyRecorder Rec(bool sided = false) => new(sided);

    // ---- hotkey recording ----

    private static readonly System.Func<int, bool> NothingDown = _ => false;

    [Fact]
    public void Ctrl_alt_y_is_recorded_when_everything_is_released()
    {
        var r = Rec();
        Assert.Equal(RecorderResult.Pending, r.Down(ClickerVk.LControl));
        Assert.Equal(RecorderResult.Pending, r.Down(ClickerVk.LMenu));
        Assert.Equal(RecorderResult.Pending, r.Down(Y));
        Assert.Equal(RecorderResult.Pending, r.Up(Y));
        Assert.Equal(RecorderResult.Pending, r.Up(ClickerVk.LMenu));
        Assert.Equal(RecorderResult.Done, r.Up(ClickerVk.LControl));

        Assert.True(ClickerHotkey.SameCombo(ClickerHotkey.Default(), r.Result));
    }

    [Fact]
    public void Sided_recording_keeps_the_side_of_each_modifier()
    {
        var r = Rec(sided: true);
        r.Down(ClickerVk.RControl);
        r.Down(ClickerVk.LShift);
        r.Down(F8);
        r.Up(F8);
        r.Up(ClickerVk.LShift);
        Assert.Equal(RecorderResult.Done, r.Up(ClickerVk.RControl));

        Assert.Equal(ClickerModifiers.RCtrl | ClickerModifiers.LShift, r.Result!.Modifiers);
        Assert.Equal(new[] { F8 }, r.Result.Keys);
    }

    [Fact]
    public void Generic_modifier_vks_count_as_their_family()
    {
        var r = Rec();
        r.Down(ClickerVk.Control);
        r.Down(A);
        r.Up(A);
        Assert.Equal(RecorderResult.Done, r.Up(ClickerVk.Control));
        Assert.Equal(ClickerModifiers.Ctrl, r.Result!.Modifiers);
    }

    [Fact]
    public void At_most_five_keys_are_kept_in_press_order_and_repeats_are_ignored()
    {
        var r = Rec();
        int[] keys = { 0x31, 0x32, 0x33, 0x34, 0x35, 0x36 };
        foreach (var k in keys) { r.Down(k); r.Down(k, repeat: true); }
        var results = keys.Select(k => r.Up(k)).ToList();

        // The sixth key was never part of the combo: the release of the fifth completes it.
        Assert.Equal(RecorderResult.Done, results[4]);
        Assert.Equal(new[] { 0x31, 0x32, 0x33, 0x34, 0x35 }, r.Result!.Keys);
    }

    [Fact]
    public void A_key_released_before_the_others_stays_in_the_combo()
    {
        var r = Rec();
        r.Down(ClickerVk.LControl);
        r.Down(A);
        r.Up(A);
        r.Down(Y);
        r.Up(Y);
        Assert.Equal(RecorderResult.Done, r.Up(ClickerVk.LControl));
        Assert.Equal(new[] { A, Y }, r.Result!.Keys);
    }

    [Theory]
    [InlineData(Esc)]
    [InlineData(Back)]
    [InlineData(0x0D)]   // Enter
    [InlineData(0x20)]   // Space
    [InlineData(0x09)]   // Tab
    public void Esc_backspace_and_the_other_control_keys_are_ordinary_keys(int vk)
    {
        var r = Rec();
        Assert.Equal(RecorderResult.Pending, r.Down(vk));
        Assert.Equal(RecorderResult.Done, r.Up(vk));
        Assert.Equal(new[] { vk }, r.Result!.Keys);
        Assert.Equal(ClickerModifiers.None, r.Result.Modifiers);
    }

    [Fact]
    public void Esc_with_a_modifier_is_recorded_too()
    {
        var r = Rec();
        r.Down(ClickerVk.LShift);
        r.Down(Esc);
        r.Up(Esc);
        Assert.Equal(RecorderResult.Done, r.Up(ClickerVk.LShift));
        Assert.Equal(ClickerModifiers.Shift, r.Result!.Modifiers);
        Assert.Equal(new[] { Esc }, r.Result.Keys);
    }

    [Theory]
    [InlineData(ClickerVk.LButton)]
    [InlineData(ClickerVk.RButton)]
    [InlineData(ClickerVk.MButton)]
    [InlineData(ClickerVk.XButton1)]
    [InlineData(ClickerVk.XButton2)]
    public void Every_mouse_button_alone_is_a_hotkey(int button)
    {
        var r = Rec();
        Assert.Equal(RecorderResult.Pending, r.Down(button));
        Assert.Equal(RecorderResult.Done, r.Up(button));
        Assert.Equal(new[] { button }, r.Result!.Keys);
        Assert.True(r.Result.IsValid());
    }

    [Fact]
    public void Mouse_buttons_combine_with_keys_and_modifiers()
    {
        var r = Rec();
        r.Down(ClickerVk.LControl);
        r.Down(ClickerVk.LButton);
        r.Down(ClickerVk.XButton1);
        r.Up(ClickerVk.LButton);
        r.Up(ClickerVk.XButton1);
        Assert.Equal(RecorderResult.Done, r.Up(ClickerVk.LControl));
        Assert.Equal(ClickerModifiers.Ctrl, r.Result!.Modifiers);
        Assert.Equal(new[] { ClickerVk.LButton, ClickerVk.XButton1 }, r.Result.Keys);
    }

    [Fact]
    public void Modifiers_alone_finish_on_release()
    {
        var r = Rec();
        r.Down(ClickerVk.LControl);
        r.Down(ClickerVk.RShift);
        Assert.Equal(RecorderResult.Pending, r.Up(ClickerVk.LControl));
        Assert.Equal(RecorderResult.Done, r.Up(ClickerVk.RShift));
        Assert.Equal(ClickerModifiers.Ctrl | ClickerModifiers.Shift, r.Result!.Modifiers);
        Assert.Empty(r.Result.Keys);
        Assert.True(r.Result.IsModifierOnly);
    }

    [Theory]
    [InlineData(ClickerVk.LShift, ClickerModifiers.Shift)]
    [InlineData(ClickerVk.RMenu, ClickerModifiers.Alt)]
    [InlineData(ClickerVk.LWin, ClickerModifiers.Win)]
    [InlineData(ClickerVk.RControl, ClickerModifiers.Ctrl)]
    public void A_single_modifier_is_a_hotkey(int vk, ClickerModifiers expected)
    {
        var r = Rec();
        r.Down(vk);
        Assert.Equal(RecorderResult.Done, r.Up(vk));
        Assert.Equal(expected, r.Result!.Modifiers);
    }

    [Fact]
    public void AltGr_is_left_ctrl_plus_right_alt_sided_and_ctrl_alt_generic()
    {
        // Windows reports AltGr as LCtrl down, RAlt down ... RAlt up, LCtrl up.
        static ClickerHotkey AltGr(bool sided)
        {
            var r = new HotkeyRecorder(sided);
            r.Down(ClickerVk.LControl);
            r.Down(ClickerVk.RMenu);
            r.Up(ClickerVk.RMenu);
            Assert.Equal(RecorderResult.Done, r.Up(ClickerVk.LControl));
            return r.Result!;
        }

        Assert.Equal(ClickerModifiers.LCtrl | ClickerModifiers.RAlt, AltGr(sided: true).Modifiers);
        Assert.Equal(ClickerModifiers.Ctrl | ClickerModifiers.Alt, AltGr(sided: false).Modifiers);
    }

    [Theory]
    [InlineData(ClickerVk.WheelUp)]
    [InlineData(ClickerVk.WheelDown)]
    [InlineData(ClickerVk.WheelLeft)]
    [InlineData(ClickerVk.WheelRight)]
    public void A_wheel_notch_finishes_at_once(int wheel)
    {
        var r = Rec();
        Assert.Equal(RecorderResult.Done, r.Down(wheel));
        Assert.Equal(new[] { wheel }, r.Result!.Keys);
    }

    [Fact]
    public void A_wheel_notch_finishes_with_what_is_held()
    {
        var r = Rec();
        r.Down(ClickerVk.LControl);
        r.Down(ClickerVk.XButton2);
        Assert.Equal(RecorderResult.Done, r.Down(ClickerVk.WheelDown));
        Assert.Equal(ClickerModifiers.Ctrl, r.Result!.Modifiers);
        Assert.Equal(new[] { ClickerVk.XButton2, ClickerVk.WheelDown }, r.Result.Keys);
    }

    [Fact]
    public void After_the_combo_is_done_further_input_changes_nothing()
    {
        var r = Rec();
        r.Down(ClickerVk.WheelUp);
        Assert.Equal(RecorderResult.Done, r.Down(ClickerVk.WheelUp));
        Assert.Equal(RecorderResult.Done, r.Down(A));
        Assert.Equal(RecorderResult.Done, r.Up(ClickerVk.LControl));
        Assert.Equal(new[] { ClickerVk.WheelUp }, r.Result!.Keys);
    }

    [Fact]
    public void Print_screen_arrives_as_a_key_up_only_and_is_a_full_press()
    {
        const int PrintScreen = 0x2C;
        var alone = Rec();
        Assert.Equal(RecorderResult.Done, alone.Up(PrintScreen));
        Assert.Equal(new[] { PrintScreen }, alone.Result!.Keys);

        var withAlt = Rec();
        withAlt.Down(ClickerVk.LMenu);
        Assert.Equal(RecorderResult.Pending, withAlt.Up(PrintScreen));
        Assert.Equal(RecorderResult.Done, withAlt.Up(ClickerVk.LMenu));
        Assert.Equal(ClickerModifiers.Alt, withAlt.Result!.Modifiers);
        Assert.Equal(new[] { PrintScreen }, withAlt.Result.Keys);
    }

    [Fact]
    public void A_release_of_a_key_pressed_before_the_capture_is_ignored()
    {
        // The click or Enter that started the capture: its release must not count.
        var live = new HashSet<int> { 0x0D };
        var r = new HotkeyRecorder(false, live.Contains);
        live.Clear();
        Assert.Equal(RecorderResult.Pending, r.Up(0x0D));
        Assert.Equal(RecorderResult.Pending, r.Up(ClickerVk.LButton));
        r.Down(A);
        Assert.Equal(RecorderResult.Done, r.Up(A));
        Assert.Equal(new[] { A }, r.Result!.Keys);
    }

    [Fact]
    public void A_key_held_since_before_the_capture_does_not_hold_up_the_finish()
    {
        var live = new HashSet<int> { 0x0D };
        var r = new HotkeyRecorder(false, live.Contains);
        r.Down(A);
        Assert.Equal(RecorderResult.Done, r.Up(A));
        Assert.Equal(new[] { A }, r.Result!.Keys);
    }

    [Fact]
    public void A_key_whose_press_a_registered_hotkey_took_counts_from_its_release()
    {
        // F10 registered by another program: the window gets only F10's key-up.
        const int F10 = 0x79;
        var alone = Rec();
        Assert.Equal(RecorderResult.Done, alone.Up(F10));
        Assert.Equal(new[] { F10 }, alone.Result!.Keys);

        var withCtrl = Rec();
        withCtrl.Down(ClickerVk.LControl);
        Assert.Equal(RecorderResult.Pending, withCtrl.Up(F10));
        Assert.Equal(RecorderResult.Done, withCtrl.Up(ClickerVk.LControl));
        Assert.Equal(ClickerModifiers.Ctrl, withCtrl.Result!.Modifiers);
        Assert.Equal(new[] { F10 }, withCtrl.Result.Keys);
    }

    [Fact]
    public void The_combo_waits_for_a_key_whose_press_the_window_never_saw()
    {
        // Win+R, Win released first: the shell took R's key-down, R is still physically down.
        const int R = 0x52;
        var live = new HashSet<int>();
        var r = new HotkeyRecorder(false, live.Contains);
        live.Add(ClickerVk.LWin);
        r.Down(ClickerVk.LWin);
        live.Add(R);
        live.Remove(ClickerVk.LWin);
        Assert.Equal(RecorderResult.Pending, r.Up(ClickerVk.LWin));

        live.Remove(R);
        Assert.Equal(RecorderResult.Done, r.Up(R));
        Assert.Equal(ClickerModifiers.Win, r.Result!.Modifiers);
        Assert.Equal(new[] { R }, r.Result.Keys);
    }

    [Fact]
    public void Focus_lost_to_a_win_shortcut_whose_key_the_window_never_saw_cancels()
    {
        const int R = 0x52;
        var live = new HashSet<int> { };
        var waited = new HotkeyRecorder(false, live.Contains);
        waited.Down(ClickerVk.LWin);
        live.Add(R);
        Assert.Equal(RecorderResult.Pending, waited.Up(ClickerVk.LWin));
        live.Clear();   // R's key-up went to the Run dialog
        Assert.Equal(RecorderResult.Cancelled, waited.FocusLost(live.Contains));

        var stillDown = new HotkeyRecorder(false, live.Contains);
        stillDown.Down(ClickerVk.LWin);
        Assert.Equal(RecorderResult.Cancelled, stillDown.FocusLost(vk => vk == R));
        Assert.Null(stillDown.Result);
    }

    [Fact]
    public void An_auto_repeat_of_a_key_held_since_before_the_capture_is_ignored()
    {
        var live = new HashSet<int> { 0x0D };
        var r = new HotkeyRecorder(false, live.Contains);
        Assert.Equal(RecorderResult.Pending, r.Down(0x0D, repeat: true));
        Assert.True(r.Preview.IsEmpty);
        r.Down(A);
        live.Remove(0x0D);
        Assert.Equal(RecorderResult.Pending, r.Up(0x0D));
        Assert.Equal(RecorderResult.Done, r.Up(A));
        Assert.Equal(new[] { A }, r.Result!.Keys);
    }

    [Fact]
    public void Codes_outside_the_key_range_are_ignored()
    {
        var r = Rec();
        Assert.Equal(RecorderResult.Pending, r.Down(0));
        Assert.Equal(RecorderResult.Pending, r.Down(0x200));
        Assert.True(r.Preview.IsEmpty);
    }

    [Fact]
    public void A_swallowed_key_up_is_reconciled_with_the_key_state()
    {
        // Both Shift keys held: Windows can report only one release.
        var r = Rec(sided: true);
        r.Down(ClickerVk.LShift);
        r.Down(ClickerVk.RShift);
        Assert.Equal(RecorderResult.Pending, r.Up(ClickerVk.RShift));
        Assert.Equal(RecorderResult.Done, r.Reconcile(NothingDown));
        Assert.Equal(ClickerModifiers.LShift | ClickerModifiers.RShift, r.Result!.Modifiers);
    }

    [Fact]
    public void Reconcile_keeps_keys_that_are_still_down_and_mouse_buttons()
    {
        var r = Rec();
        r.Down(ClickerVk.LControl);
        r.Down(ClickerVk.LButton);
        Assert.Equal(RecorderResult.Pending, r.Reconcile(vk => vk == ClickerVk.LControl));
        Assert.Equal(RecorderResult.Pending, r.Reconcile(NothingDown));   // LButton is not asked: WPF reports its release
        Assert.Equal(RecorderResult.Done, r.Up(ClickerVk.LButton));
        Assert.Equal(new[] { ClickerVk.LButton }, r.Result!.Keys);

        Assert.Equal(RecorderResult.Pending, Rec().Reconcile(NothingDown));   // nothing recorded: nothing to finish
    }

    [Fact]
    public void Focus_loss_cancels()
    {
        var r = Rec();
        r.Down(ClickerVk.LControl);
        r.Down(A);
        Assert.Equal(RecorderResult.Cancelled, r.FocusLost(NothingDown));
        Assert.Equal(RecorderResult.Cancelled, Rec().FocusLost(NothingDown));
    }

    [Fact]
    public void Focus_taken_by_the_start_menu_after_releasing_win_finishes_the_combo()
    {
        var r = Rec(sided: true);
        r.Down(ClickerVk.LWin);
        r.Down(ClickerVk.LShift);
        Assert.Equal(RecorderResult.Done, r.FocusLost(NothingDown));
        Assert.Equal(ClickerModifiers.LWin | ClickerModifiers.LShift, r.Result!.Modifiers);
    }

    [Fact]
    public void Focus_taken_by_a_win_shortcut_while_win_is_held_cancels()
    {
        var r = Rec();
        r.Down(ClickerVk.LWin);
        Assert.Equal(RecorderResult.Cancelled, r.FocusLost(vk => vk == ClickerVk.LWin));
        Assert.Null(r.Result);
    }

    [Fact]
    public void Sided_recording_follows_the_stored_choice_else_the_saved_hotkeys()
    {
        var c = new WKI_Clipper.Models.ClickerSettings();
        Assert.False(HotkeyRecorder.RecordsSided(c));   // the default Ctrl+Alt+Y is generic

        c.MasterHotkey = new ClickerHotkey { Modifiers = ClickerModifiers.LCtrl };
        Assert.True(HotkeyRecorder.RecordsSided(c));   // a sided master hotkey from before the choice was stored

        c.SidedModifiers = false;
        Assert.False(HotkeyRecorder.RecordsSided(c));
        c.MasterHotkey = null;
        c.SidedModifiers = true;
        Assert.True(HotkeyRecorder.RecordsSided(c));
    }

    [Fact]
    public void The_preview_shows_the_combo_held_so_far()
    {
        var r = Rec();
        r.Down(ClickerVk.LControl);
        r.Down(Y);
        Assert.Equal(ClickerModifiers.Ctrl, r.Preview.Modifiers);
        Assert.Equal(new[] { Y }, r.Preview.Keys);
    }

    // ---- window messages -> hook codes ----

    private static long KeyLParam(int scan, bool extended = false, bool repeat = false, bool up = false)
        => 1 | ((long)scan << 16) | (extended ? 1L << 24 : 0) | (repeat || up ? 1L << 30 : 0) | (up ? 1L << 31 : 0);

    [Fact]
    public void Generic_modifier_messages_become_the_sided_keys_the_hook_reports()
    {
        Assert.Equal(ClickerVk.LShift, RecorderKeys.FromKeyMessage(ClickerVk.Shift, KeyLParam(0x2A)));
        Assert.Equal(ClickerVk.RShift, RecorderKeys.FromKeyMessage(ClickerVk.Shift, KeyLParam(0x36)));
        Assert.Equal(ClickerVk.LControl, RecorderKeys.FromKeyMessage(ClickerVk.Control, KeyLParam(0x1D)));
        Assert.Equal(ClickerVk.RControl, RecorderKeys.FromKeyMessage(ClickerVk.Control, KeyLParam(0x1D, extended: true)));
        Assert.Equal(ClickerVk.LMenu, RecorderKeys.FromKeyMessage(ClickerVk.Menu, KeyLParam(0x38)));
        Assert.Equal(ClickerVk.RMenu, RecorderKeys.FromKeyMessage(ClickerVk.Menu, KeyLParam(0x38, extended: true, up: true)));
    }

    [Theory]
    [InlineData(0x2C)]   // Print Screen
    [InlineData(0x79)]   // F10 (arrives as WM_SYSKEYDOWN)
    [InlineData(0x13)]   // Pause
    [InlineData(0x90)]   // Num Lock
    [InlineData(0x91)]   // Scroll Lock
    [InlineData(0xAF)]   // Volume up
    [InlineData(0xB3)]   // Play/Pause
    [InlineData(0x87)]   // F24
    [InlineData(ClickerVk.LWin)]
    [InlineData(0x5D)]   // Menu key
    public void Other_keys_keep_their_vk(int vk)
        => Assert.Equal(vk, RecorderKeys.FromKeyMessage(vk, KeyLParam(0x45, extended: true)));

    [Fact]
    public void An_ime_processed_key_is_unwrapped_to_the_real_key()
    {
        const int Process = RecorderKeys.VkProcessKey;
        long a = KeyLParam(0x1E);
        System.Func<uint, int> noScan = _ => 0;

        Assert.Equal(A, RecorderKeys.UnwrapImeKey(Process, a, () => A, noScan));
        // The IME does not tell: the scan code does, with 0xE0 for an extended key.
        uint asked = 0;
        Assert.Equal(0x2E, RecorderKeys.UnwrapImeKey(Process, KeyLParam(0x53, extended: true), () => Process, s => { asked = s; return 0x2E; }));
        Assert.Equal(0xE053u, asked);
        Assert.Equal(0, RecorderKeys.UnwrapImeKey(Process, a, () => 0, noScan));
        // Any other key is left alone, and nothing is asked for it.
        Assert.Equal(A, RecorderKeys.UnwrapImeKey(A, a, () => throw new System.InvalidOperationException(), noScan));
    }

    [Fact]
    public void The_repeat_bit_marks_auto_repeat()
    {
        Assert.False(RecorderKeys.IsRepeat(KeyLParam(0x1E)));
        Assert.True(RecorderKeys.IsRepeat(KeyLParam(0x1E, repeat: true)));
    }

    [Theory]
    [InlineData(false, 120, ClickerVk.WheelUp)]
    [InlineData(false, -120, ClickerVk.WheelDown)]
    [InlineData(false, 30, ClickerVk.WheelUp)]      // smooth scrolling sends partial deltas
    [InlineData(true, 120, ClickerVk.WheelRight)]
    [InlineData(true, -240, ClickerVk.WheelLeft)]
    public void Wheel_messages_become_the_hooks_wheel_codes(bool horizontal, int delta, int expected)
    {
        long wParam = ((long)(ushort)(short)delta << 16) | 0x0008;   // low word: held buttons (MK_CONTROL here)
        Assert.Equal(expected, RecorderKeys.FromWheel(horizontal, wParam));
    }

    [Fact]
    public void A_zero_wheel_delta_is_no_input()
        => Assert.Null(RecorderKeys.FromWheel(false, 0x0004));

    [Theory]
    [InlineData(1, 0xA6)]    // APPCOMMAND_BROWSER_BACKWARD -> VK_BROWSER_BACK
    [InlineData(8, 0xAD)]    // VOLUME_MUTE
    [InlineData(10, 0xAF)]   // VOLUME_UP
    [InlineData(14, 0xB3)]   // MEDIA_PLAY_PAUSE
    [InlineData(18, 0xB7)]   // LAUNCH_APP2
    public void Keyboard_app_commands_map_to_their_key(int command, int vk)
        => Assert.Equal(vk, RecorderKeys.FromAppCommand((long)command << 16));

    [Theory]
    [InlineData(1 | 0x8000)]    // browser back from the mouse (XButton1): recorded as the button itself
    [InlineData(10 | 0x1000)]   // OEM device
    [InlineData(46)]            // APPCOMMAND_MEDIA_PLAY: no key the hook would see
    [InlineData(0)]
    public void Other_app_commands_are_not_keys(int high)
        => Assert.Null(RecorderKeys.FromAppCommand((long)high << 16));

    // ---- numbers ----

    [Theory]
    [InlineData("1.05", 1.05)]
    [InlineData("1,05", 1.05)]
    [InlineData(" 25 ", 25)]
    [InlineData("500", 500)]
    public void Numbers_accept_comma_and_point(string text, double expected)
    {
        Assert.True(ClickerNumber.TryParse(text, out var value));
        Assert.Equal(expected, value, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1,2,3")]
    [InlineData("NaN")]
    [InlineData("∞")]
    public void Garbage_is_no_number(string text)
        => Assert.False(ClickerNumber.TryParse(text, out _));

    [Fact]
    public void Numbers_show_the_language_decimal_separator_without_grouping()
    {
        Assert.Equal("1,5", ClickerNumber.Format(1.5, english: false));
        Assert.Equal("1.5", ClickerNumber.Format(1.5, english: true));
        Assert.Equal("100000", ClickerNumber.Format(100000, english: false));
        Assert.Equal("0,33", ClickerNumber.Format(1.0 / 3, english: false));
    }

    // ---- start/stop button ----

    private static ClickerState State(ClickerPhase phase) => ClickerState.Off with { Phase = phase };

    [Fact]
    public void Start_is_offered_only_when_ready_unlocked_and_free_of_blocking_conflicts()
    {
        Assert.Equal(new ClickerStartButton(true, false), ClickerStatusRules.StartButton(State(ClickerPhase.Ready), canStart: true));
        Assert.Equal(new ClickerStartButton(false, false), ClickerStatusRules.StartButton(State(ClickerPhase.Ready), canStart: false));
        Assert.Equal(new ClickerStartButton(false, false), ClickerStatusRules.StartButton(State(ClickerPhase.Ready) with { Locked = true }, true));
        Assert.Equal(new ClickerStartButton(false, false), ClickerStatusRules.StartButton(State(ClickerPhase.Ready) with { MasterAllowed = false }, true));
        Assert.Equal(new ClickerStartButton(false, false), ClickerStatusRules.StartButton(State(ClickerPhase.Off), true));
        Assert.Equal(new ClickerStartButton(false, false), ClickerStatusRules.StartButton(State(ClickerPhase.Starting), true));
        Assert.Equal(new ClickerStartButton(false, false), ClickerStatusRules.StartButton(State(ClickerPhase.Error), true));
    }

    [Fact]
    public void A_running_clicker_can_always_be_stopped()
        => Assert.Equal(new ClickerStartButton(true, true), ClickerStatusRules.StartButton(State(ClickerPhase.Running), canStart: false));

    [Fact]
    public void The_pill_tone_follows_the_state()
    {
        Assert.Equal(ClickerPillTone.Muted, ClickerStatusRules.Tone(State(ClickerPhase.Off)));
        Assert.Equal(ClickerPillTone.Normal, ClickerStatusRules.Tone(State(ClickerPhase.Ready)));
        Assert.Equal(ClickerPillTone.Active, ClickerStatusRules.Tone(State(ClickerPhase.Running)));
        Assert.Equal(ClickerPillTone.Danger, ClickerStatusRules.Tone(State(ClickerPhase.Running) with { BlockedByElevated = "game.exe" }));
        Assert.Equal(ClickerPillTone.Danger, ClickerStatusRules.Tone(State(ClickerPhase.Ready) with { Locked = true }));
        Assert.Equal(ClickerPillTone.Danger, ClickerStatusRules.Tone(State(ClickerPhase.Error)));
    }

    // ---- run list ----

    [Fact]
    public void A_stored_reason_code_is_described_and_unknown_text_is_kept()
    {
        Assert.Equal(StopReasonText.Describe(StopReason.Hotkey), ClickerStatusRules.RunReason("Hotkey"));
        Assert.Equal("irgendwas", ClickerStatusRules.RunReason("irgendwas"));
        Assert.Equal("", ClickerStatusRules.RunReason(null));
    }

    [Theory]
    [InlineData(0.4, "0,4 s")]
    [InlineData(42, "42 s")]
    [InlineData(125, "2:05 min")]
    [InlineData(3725, "1:02:05 h")]
    public void Run_lengths_read_naturally(double seconds, string expected)
        => Assert.Equal(expected, ClickerStatusRules.Length(seconds, english: false));
}
