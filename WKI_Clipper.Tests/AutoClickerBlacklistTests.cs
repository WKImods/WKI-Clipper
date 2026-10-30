using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>The blacklist as list rules: one entry per exe file name, no matter how the exe was spelled.</summary>
public sealed class AutoClickerBlacklistTests
{
    [Theory]
    [InlineData("ArmaReforgerSteam.exe", "armareforgersteam.exe")]
    [InlineData("ARMAREFORGERSTEAM.EXE", "armareforgersteam.exe")]
    [InlineData("ArmaReforgerSteam", "armareforgersteam.exe")]                                   // ".exe" is optional
    [InlineData(@"D:\SteamLibrary\steamapps\common\Arma Reforger\ArmaReforgerSteam.exe", "armareforgersteam.exe")]   // a path reduces to the file name
    [InlineData("\"D:\\Games\\cs2.exe\"", "cs2.exe")]                                            // quoted path
    [InlineData("  cs2.exe  ", "cs2.exe")]
    [InlineData("Minecraft.Windows", "minecraft.windows.exe")]                                   // dotted process name
    public void Key_is_the_lower_case_file_name_with_exe(string input, string key)
        => Assert.Equal(key, AutoClickerBlacklist.Key(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Desktop")]
    [InlineData("Unknown")]
    [InlineData(@"C:\")]
    public void Key_is_null_for_nothing_and_for_non_programs(string? input)
    {
        Assert.Null(AutoClickerBlacklist.Key(input));
        Assert.Null(AutoClickerBlacklist.Canonical(input));
    }

    [Theory]
    [InlineData("ArmaReforgerSteam", "ArmaReforgerSteam.exe")]
    [InlineData("ArmaReforgerSteam.EXE", "ArmaReforgerSteam.exe")]
    [InlineData(@"D:\Games\Arma\ArmaReforgerSteam.exe", "ArmaReforgerSteam.exe")]
    public void Canonical_keeps_the_casing_and_normalises_the_extension(string input, string expected)
        => Assert.Equal(expected, AutoClickerBlacklist.Canonical(input));

    [Fact]
    public void Add_stores_the_canonical_exe_and_a_label()
    {
        var list = new List<BlacklistEntry>();

        Assert.True(AutoClickerBlacklist.Add(list, @"D:\Games\ArmaReforgerSteam.exe", "Arma Reforger"));
        Assert.True(AutoClickerBlacklist.Add(list, "cs2", null));

        Assert.Equal("ArmaReforgerSteam.exe", list[0].ExeName);
        Assert.Equal("Arma Reforger", list[0].DisplayName);
        Assert.Equal("cs2.exe", list[1].ExeName);
        Assert.Equal("cs2", list[1].DisplayName);   // no name given: the exe without extension
    }

    [Theory]
    [InlineData("arma.exe")]
    [InlineData("ARMA.EXE")]
    [InlineData("arma")]
    [InlineData(@"C:\Somewhere\Arma.exe")]
    public void Add_refuses_a_duplicate_however_it_is_spelled(string again)
    {
        var list = new List<BlacklistEntry>();
        Assert.True(AutoClickerBlacklist.Add(list, "Arma.exe", "Arma"));

        Assert.False(AutoClickerBlacklist.Add(list, again, "Other label"));

        var only = Assert.Single(list);
        Assert.Equal("Arma", only.DisplayName);   // the existing entry stays as it was
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Desktop")]
    public void Add_refuses_nothing(string? exe)
    {
        var list = new List<BlacklistEntry>();
        Assert.False(AutoClickerBlacklist.Add(list, exe, "x"));
        Assert.Empty(list);
    }

    [Fact]
    public void Remove_finds_the_entry_by_any_spelling()
    {
        var list = new List<BlacklistEntry>();
        AutoClickerBlacklist.Add(list, "Arma.exe", "Arma");
        AutoClickerBlacklist.Add(list, "cs2.exe", "CS2");

        Assert.True(AutoClickerBlacklist.Remove(list, @"X:\ARMA"));
        Assert.False(AutoClickerBlacklist.Remove(list, "arma.exe"));   // gone already
        Assert.Equal("cs2.exe", Assert.Single(list).ExeName);
    }

    [Fact]
    public void Contains_ignores_case_extension_and_folders()
    {
        var list = new List<BlacklistEntry> { new() { ExeName = "Arma.exe", DisplayName = "Arma" } };

        Assert.True(AutoClickerBlacklist.Contains(list, "arma"));
        Assert.True(AutoClickerBlacklist.Contains(list, @"C:\x\ARMA.EXE"));
        Assert.False(AutoClickerBlacklist.Contains(list, "arma2.exe"));
        Assert.False(AutoClickerBlacklist.Contains(list, null));
    }

    [Fact]
    public void Normalize_drops_nulls_and_non_programs_and_dedupes_with_the_first_winning()
    {
        var raw = new List<BlacklistEntry?>
        {
            new() { ExeName = "Arma", DisplayName = "First" },
            null,
            new() { ExeName = "", DisplayName = "Nothing" },
            new() { ExeName = "ARMA.EXE", DisplayName = "Second" },
            new() { ExeName = @"C:\g\cs2.exe", DisplayName = " " },
        };

        var clean = AutoClickerBlacklist.Normalize(raw);

        Assert.Equal(new[] { "Arma.exe", "cs2.exe" }, clean.Select(e => e.ExeName));
        Assert.Equal("First", clean[0].DisplayName);
        Assert.Equal("cs2", clean[1].DisplayName);
        Assert.Empty(AutoClickerBlacklist.Normalize(null));
    }

    // ---- the "Spiel hinzufügen" suggestions ----

    [Fact]
    public void Candidates_offer_the_detected_game_the_profiles_and_the_bars_games()
    {
        var s = new AppSettings();
        s.GameProfiles["cs2.exe"] = new GameProfile { DisplayName = "Counter-Strike 2" };
        s.Shortcuts.Items.Add(new ShortcutEntry
        {
            Id = "game:steam:1", Group = ShortcutGroup.Game, Kind = ShortcutKind.SteamApp, Target = "1",
            DisplayName = "Arma Reforger", IconSource = @"D:\Steam\Arma Reforger\ArmaReforgerSteam.exe",
        });
        s.Shortcuts.Items.Add(new ShortcutEntry
        {
            Id = "game:exe:x", Group = ShortcutGroup.Game, Kind = ShortcutKind.Exe, Target = @"C:\Games\Tarkov\EscapeFromTarkov.exe",
            DisplayName = "Tarkov",
        });
        s.Shortcuts.RecentGames.Add(new SeenGame { ExePath = @"C:\Games\WoT\WorldOfTanks.exe", DisplayName = "World of Tanks" });

        var list = AutoClickerBlacklist.Candidates(s, "Valorant-Win64-Shipping.exe", "Valorant");

        Assert.Equal(new[]
        {
            "Valorant-Win64-Shipping.exe", "cs2.exe", "ArmaReforgerSteam.exe", "EscapeFromTarkov.exe", "WorldOfTanks.exe",
        }, list.Select(c => c.ExeName));
        Assert.Equal(BlacklistSource.Detected, list[0].Source);
        Assert.Equal(BlacklistSource.Profile, list[1].Source);
        Assert.Equal("Counter-Strike 2", list[1].DisplayName);
        Assert.Equal(BlacklistSource.ShortcutBar, list[2].Source);
        Assert.Equal("Arma Reforger", list[2].DisplayName);
    }

    [Fact]
    public void Candidates_list_a_program_once_and_the_first_source_names_it()
    {
        var s = new AppSettings();
        s.GameProfiles["arma.exe"] = new GameProfile { DisplayName = "From profile" };
        s.Shortcuts.Items.Add(new ShortcutEntry { Group = ShortcutGroup.Game, Kind = ShortcutKind.Exe, Target = @"C:\x\ARMA.exe", DisplayName = "From bar" });

        var list = AutoClickerBlacklist.Candidates(s, "Arma.exe", "From detector");

        var only = Assert.Single(list);
        Assert.Equal("From detector", only.DisplayName);
        Assert.Equal(BlacklistSource.Detected, only.Source);
    }

    [Fact]
    public void Candidates_mark_programs_that_are_listed_already()
    {
        var s = new AppSettings();
        AutoClickerBlacklist.Add(s.AutoClicker.Blacklist, "arma.exe", "Arma");
        s.GameProfiles["arma.exe"] = new GameProfile { DisplayName = "Arma" };
        s.GameProfiles["cs2.exe"] = new GameProfile { DisplayName = "CS2" };

        var list = AutoClickerBlacklist.Candidates(s, null, null);

        Assert.True(list.Single(c => c.ExeName == "arma.exe").AlreadyListed);
        Assert.False(list.Single(c => c.ExeName == "cs2.exe").AlreadyListed);
    }

    [Fact]
    public void Candidates_skip_bar_entries_without_an_exe_and_non_games()
    {
        var s = new AppSettings();
        s.Shortcuts.Items.Add(new ShortcutEntry { Group = ShortcutGroup.Game, Kind = ShortcutKind.EpicApp, Target = "Fortnite", DisplayName = "Fortnite" });          // no exe known
        s.Shortcuts.Items.Add(new ShortcutEntry { Group = ShortcutGroup.Launcher, Kind = ShortcutKind.Exe, Target = @"C:\Steam\steam.exe", DisplayName = "Steam" });   // not a game
        s.Shortcuts.Items.Add(new ShortcutEntry { Group = ShortcutGroup.Game, Kind = ShortcutKind.ShellLink, Target = @"C:\Menu\Game.lnk", IconSource = "shell:AppsFolder\\X!App", DisplayName = "Store game" });

        Assert.Empty(AutoClickerBlacklist.Candidates(s, null, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Desktop")]
    public void Candidates_ignore_a_detected_game_that_is_no_program(string? detected)
        => Assert.Empty(AutoClickerBlacklist.Candidates(new AppSettings(), detected, "x"));
}
