using WKI_Clipper.Models;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class ActivationStateMachineTests
{
    private static readonly ActivationOutput None = ActivationOutput.None;

    private static ActivationOutput Start(ActivationReason r = ActivationReason.Hotkey) => ActivationOutput.Start(r);
    private static ActivationOutput Stop(ActivationReason r) => ActivationOutput.Stop(r);

    private static ActivationStateMachine Machine(ActivationMode mode = ActivationMode.Toggle)
    {
        var m = new ActivationStateMachine();
        m.Configure(mode, hasMaster: false, ActivationMode.Toggle);
        return m;
    }

    /// <summary>A full press and release of the start hotkey; returns the output of the press and of the release.</summary>
    private static (ActivationOutput Press, ActivationOutput Release) Tap(ActivationStateMachine m)
        => (m.OnMatchChanged(true), m.OnMatchChanged(false));

    // ---- Toggle ----

    [Fact]
    public void Toggle_starts_on_the_first_press_and_stops_on_the_second()
    {
        var m = Machine();

        Assert.Equal((Start(), None), Tap(m));
        Assert.True(m.IsRunning);
        Assert.Equal((Stop(ActivationReason.Hotkey), None), Tap(m));
        Assert.False(m.IsRunning);
        Assert.Equal((Start(), None), Tap(m));
    }

    [Fact]
    public void Toggle_acts_on_the_rising_edge_only()
    {
        var m = Machine();
        Assert.Equal(Start(), m.OnMatchChanged(true));
        Assert.Equal(None, m.OnMatchChanged(true));    // no change, no second action
        Assert.Equal(None, m.OnMatchChanged(false));   // releasing does nothing in Toggle
        Assert.True(m.IsRunning);
    }

    [Fact]
    public void Toggle_keeps_holding_the_key_from_flipping_again()
    {
        var m = Machine();
        m.OnMatchChanged(true);
        for (int i = 0; i < 5; i++) Assert.Equal(None, m.OnMatchChanged(true));
        Assert.True(m.IsRunning);
    }

    [Fact]
    public void A_toggle_start_survives_the_engine_ending_the_run_itself()
    {
        var m = Machine();
        Tap(m);
        m.OnStoppedByEngine();                 // click limit reached
        Assert.False(m.IsRunning);
        Assert.Equal((Start(), None), Tap(m)); // the next press starts again (it does NOT send a stop first)
    }

    // ---- Hold ----

    [Fact]
    public void Hold_runs_only_while_the_hotkey_is_held()
    {
        var m = Machine(ActivationMode.Hold);

        Assert.Equal(Start(), m.OnMatchChanged(true));
        Assert.True(m.IsRunning);
        Assert.Equal(Stop(ActivationReason.HoldReleased), m.OnMatchChanged(false));
        Assert.False(m.IsRunning);
    }

    [Fact]
    public void Hold_does_not_restart_after_the_engine_ended_the_run_while_the_key_is_still_down()
    {
        var m = Machine(ActivationMode.Hold);
        m.OnMatchChanged(true);
        m.OnStoppedByEngine();

        Assert.Equal(None, m.OnMatchChanged(true));
        Assert.False(m.IsRunning);
        Assert.Equal(None, m.OnMatchChanged(false));         // releasing a stopped run sends no stop
        Assert.Equal(Start(), m.OnMatchChanged(true));        // a fresh press starts again
    }

    [Fact]
    public void Hold_release_without_a_run_does_nothing()
        => Assert.Equal(None, Machine(ActivationMode.Hold).OnMatchChanged(false));

    // ---- Lock ----

    [Fact]
    public void Lock_stops_a_run_and_refuses_starts_until_unlocked()
    {
        var m = Machine();
        Tap(m);

        Assert.Equal(Stop(ActivationReason.Locked), m.OnLock());
        Assert.False(m.IsRunning);
        Assert.True(m.IsLocked);
        Assert.Equal((None, None), Tap(m));

        m.OnUnlock();
        Assert.False(m.IsLocked);
        Assert.Equal((Start(), None), Tap(m));
    }

    [Fact]
    public void Locking_an_idle_clicker_sends_no_stop()
        => Assert.Equal(None, Machine().OnLock());

    [Fact]
    public void A_hotkey_held_while_the_lock_lifts_starts_nothing()
    {
        var m = Machine(ActivationMode.Hold);
        m.OnLock();
        m.OnMatchChanged(true);          // refused
        m.OnUnlock();
        Assert.False(m.IsRunning);
        m.OnMatchChanged(false);
        Assert.Equal(Start(), m.OnMatchChanged(true));
    }

    // ---- Picker ----

    [Fact]
    public void Opening_a_picker_stops_a_run_and_blocks_hotkeys()
    {
        var m = Machine();
        Tap(m);

        Assert.Equal(Stop(ActivationReason.PickerOpened), m.OnPicker(true));
        Assert.False(m.IsRunning);
        Assert.Equal((None, None), Tap(m));
        Assert.Equal(None, m.OnPicker(false));
        Assert.Equal((Start(), None), Tap(m));
    }

    [Fact]
    public void A_hotkey_held_through_the_picker_does_not_start_when_it_closes()
    {
        var m = Machine();
        m.OnPicker(true);
        m.OnMatchChanged(true);
        m.OnPicker(false);
        Assert.False(m.IsRunning);
        m.OnMatchChanged(false);
        Assert.Equal(Start(), m.OnMatchChanged(true));
    }

    [Fact]
    public void Opening_a_picker_on_an_idle_clicker_sends_no_stop()
        => Assert.Equal(None, Machine().OnPicker(true));

    [Fact]
    public void In_hold_mode_releasing_during_the_picker_sends_nothing()
    {
        var m = Machine(ActivationMode.Hold);
        m.OnMatchChanged(true);
        m.OnPicker(true);                                    // stops the run
        Assert.Equal(None, m.OnMatchChanged(false));
    }

    // ---- Master switch ----

    private static ActivationStateMachine WithMaster(ActivationMode mode, ActivationMode masterMode)
    {
        var m = new ActivationStateMachine();
        m.Configure(mode, hasMaster: true, masterMode);
        return m;
    }

    [Fact]
    public void Without_a_master_everything_is_allowed()
        => Assert.True(Machine().MasterAllowed);

    [Fact]
    public void A_toggle_master_starts_off_and_blocks_the_hotkey()
    {
        var m = WithMaster(ActivationMode.Toggle, ActivationMode.Toggle);
        Assert.False(m.MasterAllowed);
        Assert.Equal((None, None), Tap(m));
        Assert.False(m.IsRunning);
    }

    [Fact]
    public void A_toggle_master_flips_on_each_press()
    {
        var m = WithMaster(ActivationMode.Toggle, ActivationMode.Toggle);

        m.OnMasterChanged(true); m.OnMasterChanged(false);
        Assert.True(m.MasterAllowed);
        Assert.Equal((Start(), None), Tap(m));

        // switching it off stops the run
        Assert.Equal(Stop(ActivationReason.MasterSwitch), m.OnMasterChanged(true));
        m.OnMasterChanged(false);
        Assert.False(m.MasterAllowed);
        Assert.False(m.IsRunning);
        Assert.Equal((None, None), Tap(m));

        m.OnMasterChanged(true); m.OnMasterChanged(false);
        Assert.Equal((Start(), None), Tap(m));
    }

    [Fact]
    public void Holding_a_toggle_master_flips_it_only_once()
    {
        var m = WithMaster(ActivationMode.Toggle, ActivationMode.Toggle);
        m.OnMasterChanged(true);
        m.OnMasterChanged(true);
        m.OnMasterChanged(true);
        Assert.True(m.MasterAllowed);
    }

    [Fact]
    public void A_hold_master_allows_only_while_held()
    {
        var m = WithMaster(ActivationMode.Toggle, ActivationMode.Hold);
        Assert.False(m.MasterAllowed);
        Assert.Equal((None, None), Tap(m));

        m.OnMasterChanged(true);
        Assert.True(m.MasterAllowed);
        Assert.Equal(Start(), m.OnMatchChanged(true));
        m.OnMatchChanged(false);
        Assert.True(m.IsRunning);

        Assert.Equal(Stop(ActivationReason.MasterSwitch), m.OnMasterChanged(false));
        Assert.False(m.IsRunning);
        Assert.False(m.MasterAllowed);
    }

    [Fact]
    public void Hold_hotkey_with_hold_master_stops_with_the_master_reason_when_the_master_goes_first()
    {
        var m = WithMaster(ActivationMode.Hold, ActivationMode.Hold);
        m.OnMasterChanged(true);
        Assert.Equal(Start(), m.OnMatchChanged(true));

        Assert.Equal(Stop(ActivationReason.MasterSwitch), m.OnMasterChanged(false));
        Assert.Equal(None, m.OnMatchChanged(false));             // the run is gone already
    }

    [Fact]
    public void A_hotkey_pressed_before_the_hold_master_starts_nothing()
    {
        var m = WithMaster(ActivationMode.Hold, ActivationMode.Hold);
        Assert.Equal(None, m.OnMatchChanged(true));              // ignored: master not held
        Assert.Equal(None, m.OnMasterChanged(true));              // master arrives later: still no start without a fresh press
        Assert.False(m.IsRunning);
    }

    [Fact]
    public void The_master_does_not_flip_while_a_picker_is_open()
    {
        var m = WithMaster(ActivationMode.Toggle, ActivationMode.Toggle);
        m.OnPicker(true);
        m.OnMasterChanged(true); m.OnMasterChanged(false);
        m.OnPicker(false);
        Assert.False(m.MasterAllowed);
    }

    [Fact]
    public void Removing_the_master_allows_everything_again_and_a_new_master_starts_from_scratch()
    {
        var m = WithMaster(ActivationMode.Toggle, ActivationMode.Toggle);
        m.OnMasterChanged(true); m.OnMasterChanged(false);
        Assert.True(m.MasterAllowed);

        Assert.Equal(None, m.Configure(ActivationMode.Toggle, hasMaster: false, ActivationMode.Toggle));
        Assert.True(m.MasterAllowed);

        m.Configure(ActivationMode.Toggle, hasMaster: true, ActivationMode.Toggle);
        Assert.False(m.MasterAllowed);                           // the old latch is gone
    }

    [Fact]
    public void Adding_a_master_to_a_running_clicker_stops_it_when_the_master_is_off()
    {
        var m = Machine();
        Tap(m);
        Assert.Equal(Stop(ActivationReason.MasterSwitch), m.Configure(ActivationMode.Toggle, hasMaster: true, ActivationMode.Toggle));
        Assert.False(m.IsRunning);
    }

    [Fact]
    public void A_hold_master_that_is_already_held_when_configured_counts()
    {
        var m = new ActivationStateMachine();
        m.OnMasterChanged(true);                                  // pressed before the master was configured
        m.Configure(ActivationMode.Toggle, hasMaster: true, ActivationMode.Hold);
        Assert.True(m.MasterAllowed);
    }

    [Fact]
    public void Changing_the_master_mode_resets_the_toggle_latch()
    {
        var m = WithMaster(ActivationMode.Toggle, ActivationMode.Toggle);
        m.OnMasterChanged(true); m.OnMasterChanged(false);
        Assert.True(m.MasterAllowed);

        m.Configure(ActivationMode.Toggle, hasMaster: true, ActivationMode.Hold);
        m.Configure(ActivationMode.Toggle, hasMaster: true, ActivationMode.Toggle);
        Assert.False(m.MasterAllowed);
    }

    // ---- start from outside (start zone) ----

    [Fact]
    public void An_external_start_honours_lock_picker_master_and_a_running_clicker()
    {
        var m = Machine();
        Assert.Equal(Start(ActivationReason.StartZone), m.OnStartRequested(ActivationReason.StartZone));
        Assert.Equal(None, m.OnStartRequested(ActivationReason.StartZone));   // already running
        m.OnStoppedByEngine();

        m.OnLock();
        Assert.Equal(None, m.OnStartRequested(ActivationReason.StartZone));
        m.OnUnlock();

        m.OnPicker(true);
        Assert.Equal(None, m.OnStartRequested(ActivationReason.StartZone));
        m.OnPicker(false);

        m.Configure(ActivationMode.Toggle, hasMaster: true, ActivationMode.Hold);
        Assert.Equal(None, m.OnStartRequested(ActivationReason.StartZone));
        m.OnMasterChanged(true);
        Assert.Equal(Start(ActivationReason.StartZone), m.OnStartRequested(ActivationReason.StartZone));
    }

    [Fact]
    public void After_an_external_start_the_toggle_hotkey_stops_it()
    {
        var m = Machine();
        m.OnStartRequested(ActivationReason.StartZone);
        Assert.Equal(Stop(ActivationReason.Hotkey), m.OnMatchChanged(true));
    }

    // ---- reconfiguration ----

    [Fact]
    public void Switching_between_toggle_and_hold_applies_from_the_next_edge()
    {
        var m = Machine();
        Tap(m);                                                   // running (toggle)
        m.Configure(ActivationMode.Hold, hasMaster: false, ActivationMode.Toggle);
        Assert.True(m.IsRunning);
        Assert.Equal(None, m.OnMatchChanged(false));              // already up: no falling edge
        Assert.Equal(None, m.OnMatchChanged(true));               // rising while running in Hold: nothing to start
        Assert.Equal(Stop(ActivationReason.HoldReleased), m.OnMatchChanged(false));
    }
}
