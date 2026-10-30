using System;
using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

internal sealed class FakeClickerLock : IClickerLock
{
    public bool Armed { get; set; } = true;
    public List<BlacklistEntry> Locks { get; } = new();
    public int Unlocks { get; private set; }
    public bool IsLocked { get; private set; }

    public void Lock(BlacklistEntry entry)
    {
        Locks.Add(entry);
        IsLocked = true;
    }

    public void Unlock()
    {
        Unlocks++;
        IsLocked = false;
    }
}

/// <summary>The guard locks the native clicker while a blacklisted program runs or is in front.</summary>
public sealed class AutoClickerGuardClickerTests
{
    private static List<BlacklistEntry> Arma() => new() { new BlacklistEntry { ExeName = "ArmaReforgerSteam.exe", DisplayName = "Arma Reforger" } };

    // ---- the pure decision ----

    [Fact]
    public void A_blacklisted_program_locks_a_clicker_that_is_switched_on()
    {
        var d = AutoClickerGuard.DecideNative(Arma(), new[] { "explorer", "ArmaReforgerSteam" }, null, ignore: false);

        Assert.Equal(GuardAction.LockClicker, d.Action);
        Assert.Equal("ArmaReforgerSteam.exe", d.Match!.ExeName);
    }

    [Fact]
    public void The_foreground_game_locks_even_before_the_process_list_shows_it()
        => Assert.Equal(GuardAction.LockClicker,
            AutoClickerGuard.DecideNative(Arma(), Array.Empty<string>(), "ArmaReforgerSteam.exe", false).Action);

    [Fact]
    public void An_ignored_blacklist_never_locks()
    {
        var d = AutoClickerGuard.DecideNative(Arma(), new[] { "ArmaReforgerSteam" }, "ArmaReforgerSteam.exe", ignore: true);

        Assert.Equal(GuardAction.UnlockClicker, d.Action);
        Assert.Null(d.Match);
    }

    [Fact]
    public void Nothing_blacklisted_running_unlocks()
    {
        Assert.Equal(GuardAction.UnlockClicker, AutoClickerGuard.DecideNative(Arma(), new[] { "chrome" }, "chrome.exe", false).Action);
        Assert.Equal(GuardAction.UnlockClicker, AutoClickerGuard.DecideNative(new List<BlacklistEntry>(), new[] { "ArmaReforgerSteam" }, null, false).Action);
        Assert.Equal(GuardAction.UnlockClicker, AutoClickerGuard.DecideNative(null, null, null, false).Action);
    }

    // ---- the service ----

    private sealed class Rig : IDisposable
    {
        public readonly FakeProcessProbe Processes = new();
        public readonly FakeClickerLock Clicker = new();
        public readonly AutoClickerSettings Settings = new() { Blacklist = Arma() };
        public readonly AutoClickerGuard Guard;

        public Rig()
        {
            Guard = new AutoClickerGuard(() => Settings, Processes, Clicker, pollMs: 600_000, background: work => work());
        }

        public void Dispose() => Guard.Dispose();
    }

    [Fact]
    public void A_game_change_to_a_blacklisted_game_locks_the_clicker()
    {
        using var rig = new Rig();
        rig.Processes.Running("ArmaReforgerSteam");

        rig.Guard.OnGameChanged(new GameInfo(10, "ArmaReforgerSteam.exe", "Arma Reforger"));

        Assert.True(rig.Clicker.IsLocked);
        Assert.Equal("Arma Reforger", rig.Clicker.Locks.Last().DisplayName);
    }

    [Fact]
    public void The_clicker_is_unlocked_again_when_the_game_is_gone()
    {
        using var rig = new Rig();
        rig.Processes.Running("ArmaReforgerSteam");
        rig.Guard.Evaluate();
        Assert.True(rig.Clicker.IsLocked);

        rig.Processes.Stopped("ArmaReforgerSteam");
        rig.Guard.Evaluate();

        Assert.False(rig.Clicker.IsLocked);
    }

    [Fact]
    public void A_harmless_program_leaves_the_clicker_unlocked()
    {
        using var rig = new Rig();
        rig.Processes.Running("SomeOtherGame");

        rig.Guard.OnGameChanged(new GameInfo(10, "SomeOtherGame.exe", "Other"));

        Assert.False(rig.Clicker.IsLocked);
        Assert.Empty(rig.Clicker.Locks);
    }

    [Fact]
    public void While_the_clicker_is_switched_on_the_poll_runs()
    {
        using var rig = new Rig();

        rig.Guard.Evaluate();

        Assert.True(rig.Guard.IsPolling);       // a game may start in the background
    }

    [Fact]
    public void The_poll_catches_a_game_that_starts_in_the_background_and_locks_the_clicker()
    {
        using var rig = new Rig();
        rig.Guard.Evaluate();
        Assert.False(rig.Clicker.IsLocked);

        rig.Processes.Running("ArmaReforgerSteam");
        rig.Guard.Evaluate();   // the next tick

        Assert.True(rig.Clicker.IsLocked);
    }

    [Fact]
    public void A_switched_off_clicker_is_not_polled_for_and_reads_no_process_list()
    {
        using var rig = new Rig();
        rig.Clicker.Armed = false;
        rig.Processes.Running("ArmaReforgerSteam");

        rig.Guard.Evaluate();

        Assert.False(rig.Clicker.IsLocked);
        Assert.False(rig.Guard.IsPolling);
        Assert.Equal(0, rig.Processes.ListCalls);
    }

    [Fact]
    public void A_blacklisted_game_in_front_locks_a_switched_off_clicker_so_switching_on_starts_locked()
    {
        using var rig = new Rig();
        rig.Clicker.Armed = false;
        rig.Processes.Running("ArmaReforgerSteam");

        // The lock is in place before the switch flips: no window in which the fresh helper is unlocked while a slow check runs.
        rig.Guard.OnGameChanged(new GameInfo(10, "ArmaReforgerSteam.exe", "Arma Reforger"));

        Assert.True(rig.Clicker.IsLocked);
        Assert.Equal(0, rig.Processes.ListCalls);   // still no process list for a clicker that is off
    }

    [Fact]
    public void Switching_the_clicker_on_while_a_blacklisted_game_runs_locks_it_at_the_next_look()
    {
        using var rig = new Rig();
        rig.Clicker.Armed = false;
        rig.Processes.Running("ArmaReforgerSteam");
        rig.Guard.Evaluate();
        Assert.False(rig.Clicker.IsLocked);

        rig.Clicker.Armed = true;
        rig.Guard.OnForegroundChanged();   // what the app does when the switch flips

        Assert.True(rig.Clicker.IsLocked);
    }

    [Fact]
    public void Ignoring_the_blacklist_lifts_the_lock_at_once_and_switching_it_back_locks_again()
    {
        using var rig = new Rig();
        rig.Processes.Running("ArmaReforgerSteam");
        rig.Guard.Evaluate();
        Assert.True(rig.Clicker.IsLocked);

        rig.Guard.IgnoreBlacklist = true;
        Assert.False(rig.Clicker.IsLocked);
        Assert.False(rig.Guard.IsPolling);

        rig.Guard.IgnoreBlacklist = false;
        Assert.True(rig.Clicker.IsLocked);
    }

    [Fact]
    public void Emptying_the_blacklist_lifts_the_lock()
    {
        using var rig = new Rig();
        rig.Processes.Running("ArmaReforgerSteam");
        rig.Guard.Evaluate();
        Assert.True(rig.Clicker.IsLocked);

        rig.Settings.Blacklist = new List<BlacklistEntry>();
        rig.Guard.Evaluate();

        Assert.False(rig.Clicker.IsLocked);
        Assert.False(rig.Guard.IsPolling);
    }

    [Fact]
    public void A_blacklist_edit_locks_at_once()
    {
        using var rig = new Rig();
        rig.Processes.Running("cs2");
        rig.Guard.Evaluate();
        Assert.False(rig.Clicker.IsLocked);

        AutoClickerBlacklist.Add(rig.Settings.Blacklist, "cs2", "CS2");
        rig.Guard.Evaluate();

        Assert.True(rig.Clicker.IsLocked);
    }

    [Fact]
    public void One_check_reads_the_process_list_once()
    {
        using var rig = new Rig();
        rig.Processes.Running("ArmaReforgerSteam");

        rig.Guard.Evaluate();

        Assert.True(rig.Clicker.IsLocked);
        Assert.Equal(1, rig.Processes.ListCalls);
    }

    [Fact]
    public void A_clicker_that_throws_does_not_break_the_guard()
    {
        var processes = new FakeProcessProbe().Running("ArmaReforgerSteam");
        var settings = new AutoClickerSettings { Blacklist = Arma() };
        using var guard = new AutoClickerGuard(() => settings, processes, new ThrowingLock(), pollMs: 600_000, background: w => w());

        guard.Evaluate();
        guard.Evaluate();

        Assert.True(guard.IsPolling);   // still watching
        Assert.Equal(2, processes.ListCalls);
    }

    private sealed class ThrowingLock : IClickerLock
    {
        public bool Armed => true;
        public void Lock(BlacklistEntry entry) => throw new InvalidOperationException("lock failed");
        public void Unlock() => throw new InvalidOperationException("unlock failed");
    }
}
