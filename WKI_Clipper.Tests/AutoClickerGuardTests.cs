using System;
using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

internal sealed class FakeProcessProbe : IProcessProbe
{
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    public int ListCalls { get; private set; }

    /// <summary>Runs right after <see cref="RunningNames"/> handed out its snapshot: a change that lands while a check is in progress.</summary>
    public Action? AfterRunningNames { get; set; }

    public FakeProcessProbe Running(params string[] names)
    {
        lock (_names) foreach (var n in names) _names.Add(n);
        return this;
    }

    public FakeProcessProbe Stopped(string name)
    {
        lock (_names) _names.Remove(name);
        return this;
    }

    public IReadOnlyCollection<string> RunningNames()
    {
        List<string> snapshot;
        lock (_names) snapshot = _names.ToList();
        ListCalls++;
        AfterRunningNames?.Invoke();
        return snapshot;
    }
}

/// <summary>How a blacklist entry is matched against the running programs, and the parts of the guard that are not about one lock.</summary>
public sealed class AutoClickerGuardTests
{
    private static List<BlacklistEntry> Arma() => new() { new BlacklistEntry { ExeName = "ArmaReforgerSteam.exe", DisplayName = "Arma Reforger" } };

    // ---- matching ----

    [Theory]
    [InlineData("armareforgersteam")]            // case
    [InlineData("ARMAREFORGERSTEAM")]
    [InlineData("ArmaReforgerSteam.exe")]        // a process list that keeps the extension
    public void The_match_ignores_case_and_a_present_or_missing_extension(string running)
        => Assert.Equal(GuardAction.LockClicker, AutoClickerGuard.DecideNative(Arma(), new[] { running }, null, false).Action);

    [Fact]
    public void The_blacklist_may_be_written_without_extension_or_with_a_full_path()
    {
        var list = new List<BlacklistEntry>
        {
            new() { ExeName = "ArmaReforgerSteam", DisplayName = "no ext" },
            new() { ExeName = @"D:\Games\cs2.exe", DisplayName = "path" },
        };

        Assert.Equal(GuardAction.LockClicker, AutoClickerGuard.DecideNative(list, new[] { "ArmaReforgerSteam" }, null, false).Action);
        Assert.Equal(GuardAction.LockClicker, AutoClickerGuard.DecideNative(list, new[] { "cs2" }, null, false).Action);
    }

    [Fact]
    public void A_foreground_exe_given_as_a_full_path_matches()
        => Assert.Equal(GuardAction.LockClicker,
            AutoClickerGuard.DecideNative(Arma(), Array.Empty<string>(), @"C:\x\ArmaReforgerSteam.exe", false).Action);

    [Fact]
    public void Nothing_running_or_an_empty_blacklist_matches_nothing()
    {
        Assert.Null(AutoClickerGuard.FindBlocker(Arma(), Array.Empty<string>(), null));
        Assert.Null(AutoClickerGuard.FindBlocker(new List<BlacklistEntry>(), new[] { "ArmaReforgerSteam" }, "ArmaReforgerSteam.exe"));
        Assert.Null(AutoClickerGuard.FindBlocker(null, new[] { "ArmaReforgerSteam" }, null));
        Assert.Null(AutoClickerGuard.FindBlocker(Arma(), null, null));
    }

    [Fact]
    public void A_name_that_only_starts_like_the_entry_does_not_match()
        => Assert.Null(AutoClickerGuard.FindBlocker(
            new List<BlacklistEntry> { new() { ExeName = "arma.exe", DisplayName = "x" } }, new[] { "arma2", "xarma", "arma.exe.bak" }, null));

    [Fact]
    public void Broken_entries_are_skipped_and_the_first_real_match_is_reported()
    {
        var list = new List<BlacklistEntry>
        {
            null!,
            new() { ExeName = "", DisplayName = "blank" },
            new() { ExeName = "cs2.exe", DisplayName = "CS2" },
            new() { ExeName = "arma.exe", DisplayName = "Arma" },
        };

        Assert.Equal("CS2", AutoClickerGuard.FindBlocker(list, new[] { "arma", "cs2" }, null)!.DisplayName);
    }

    [Fact]
    public void FindBlocker_does_not_care_about_the_ignore_flag()
        => Assert.Equal("Arma Reforger", AutoClickerGuard.FindBlocker(Arma(), new[] { "ArmaReforgerSteam" }, null)!.DisplayName);

    // ---- the service ----

    private sealed class Rig : IDisposable
    {
        public readonly FakeProcessProbe Processes = new();
        public readonly FakeClickerLock Clicker = new();
        public readonly AutoClickerSettings Settings = new() { Blacklist = Arma() };
        public readonly AutoClickerGuard Guard;

        public Rig()
        {
            // A long poll interval and a synchronous "background": the test drives every check itself.
            Guard = new AutoClickerGuard(() => Settings, Processes, Clicker, pollMs: 600_000, background: work => work());
        }

        public void Dispose() => Guard.Dispose();
    }

    [Fact]
    public void Ignore_is_session_only_and_off_for_a_new_guard()
    {
        using var first = new Rig();
        first.Guard.IgnoreBlacklist = true;

        using var second = new Rig();   // "after a restart"

        Assert.False(second.Guard.IgnoreBlacklist);
    }

    [Fact]
    public void IgnoreChanged_fires_only_on_a_real_change()
    {
        using var rig = new Rig();
        int changes = 0;
        rig.Guard.IgnoreChanged += () => changes++;

        rig.Guard.IgnoreBlacklist = true;
        rig.Guard.IgnoreBlacklist = true;
        rig.Guard.IgnoreBlacklist = false;

        Assert.Equal(2, changes);
    }

    [Fact]
    public void A_game_that_ended_is_forgotten()
    {
        using var rig = new Rig();
        rig.Clicker.Armed = false;   // only the game in front counts
        rig.Guard.OnGameChanged(new GameInfo(10, "ArmaReforgerSteam.exe", "Arma Reforger"));
        Assert.True(rig.Clicker.IsLocked);

        rig.Guard.OnGameChanged(null);

        Assert.False(rig.Clicker.IsLocked);
    }

    [Fact]
    public void A_trigger_that_arrives_during_a_check_is_not_lost()
    {
        using var rig = new Rig();
        bool fired = false;
        rig.Processes.AfterRunningNames = () =>
        {
            if (fired) return;
            fired = true;
            // The running check already holds its process list (without the game). The game starts now and a window switch reports it
            // while that check is still going: a call that is dropped here would leave the clicker unlocked next to the game.
            rig.Processes.Running("ArmaReforgerSteam");
            rig.Guard.OnForegroundChanged();
        };

        rig.Guard.Evaluate();

        Assert.True(rig.Clicker.IsLocked);
    }

    [Fact]
    public void Ignoring_the_blacklist_stops_the_poll_until_it_counts_again()
    {
        using var rig = new Rig();
        rig.Guard.Evaluate();
        Assert.True(rig.Guard.IsPolling);

        rig.Guard.IgnoreBlacklist = true;
        Assert.False(rig.Guard.IsPolling);

        rig.Guard.Evaluate();   // a later check or window switch does not arm anything again
        rig.Guard.OnForegroundChanged();
        Assert.False(rig.Guard.IsPolling);

        rig.Guard.IgnoreBlacklist = false;
        Assert.True(rig.Guard.IsPolling);
    }

    [Fact]
    public void An_empty_blacklist_stops_the_poll_and_a_new_entry_starts_it_again()
    {
        using var rig = new Rig();
        rig.Guard.Evaluate();
        Assert.True(rig.Guard.IsPolling);

        rig.Settings.Blacklist = new List<BlacklistEntry>();
        rig.Guard.Evaluate();
        Assert.False(rig.Guard.IsPolling);

        AutoClickerBlacklist.Add(rig.Settings.Blacklist, "cs2", "CS2");
        rig.Guard.Evaluate();
        Assert.True(rig.Guard.IsPolling);
    }

    [Fact]
    public void Dispose_stops_everything()
    {
        var rig = new Rig();
        rig.Processes.Running("ArmaReforgerSteam");
        rig.Guard.Dispose();

        rig.Guard.Evaluate();

        Assert.False(rig.Guard.IsPolling);
        Assert.False(rig.Clicker.IsLocked);
        Assert.Equal(0, rig.Processes.ListCalls);
    }
}
