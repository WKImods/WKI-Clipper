using System.Linq;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// F9 length per game: the pure helpers behind it - the clip length of ONE press (never mixed up with the length the
/// ring is sized for), the rows and the "add current game" state of the settings list, the ring driver and the
/// status note.
/// </summary>
public sealed class PressClipLengthTests
{
    private const string Arma = "ArmaReforgerSteam.exe";
    private const string ArmaKey = "armareforgersteam.exe";

    private static AppSettings Settings(int global = 60)
    {
        var s = new AppSettings();
        s.ReplayBuffer.DurationSeconds = global;
        return s;
    }

    private static void Length(AppSettings s, string exe, int? seconds, string? name = null)
        => GameProfiles.SetClipSeconds(s, exe, name ?? exe, seconds);

    // ---- the clip length of one press ----

    [Fact]
    public void Without_a_profile_a_press_saves_the_global_length()
    {
        var clip = PressClipLength.Resolve(ringSeconds: 60, Settings(60), Arma);
        Assert.Equal(60, clip.Seconds);
        Assert.Equal(ClipLengthSource.Global, clip.Source);
        Assert.Null(clip.ProfileKey);
        Assert.False(clip.CappedByRing);
    }

    [Fact]
    public void A_shorter_profile_length_wins_in_its_game_and_the_other_games_keep_the_global_one()
    {
        var s = Settings(600);
        Length(s, Arma, 90);

        var inGame = PressClipLength.Resolve(600, s, Arma);
        Assert.Equal(90, inGame.Seconds);
        Assert.Equal(ClipLengthSource.Profile, inGame.Source);
        Assert.Equal(ArmaKey, inGame.ProfileKey);

        Assert.Equal(600, PressClipLength.Resolve(600, s, "cs2.exe").Seconds);
    }

    [Fact]
    public void A_profile_longer_than_what_the_ring_holds_is_capped_and_says_so()
    {
        var s = Settings(60);
        Length(s, Arma, 1800);

        var clip = PressClipLength.Resolve(ringSeconds: 300, s, Arma);   // the ring only holds 300 s right now (RAM/disk cap)

        Assert.Equal(300, clip.Seconds);
        Assert.Equal(1800, clip.WantedSeconds);
        Assert.True(clip.CappedByRing);
        Assert.Equal(ClipLengthSource.Profile, clip.Source);
    }

    [Fact]
    public void A_long_profile_in_a_ring_that_holds_it_saves_all_of_it()
    {
        var s = Settings(60);
        Length(s, Arma, 1800);
        var clip = PressClipLength.Resolve(1800, s, Arma);
        Assert.Equal(1800, clip.Seconds);
        Assert.False(clip.CappedByRing);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("Desktop")] [InlineData("Unknown")] [InlineData("not-profiled.exe")]
    public void No_game_the_desktop_or_an_unknown_exe_uses_the_global_length_as_the_backup(string? exe)
    {
        var s = Settings(75);
        Length(s, Arma, 1800);
        var clip = PressClipLength.Resolve(1800, s, exe);
        Assert.Equal(75, clip.Seconds);
        Assert.Equal(ClipLengthSource.Global, clip.Source);
    }

    [Fact]
    public void A_profile_without_a_length_is_the_global_length()
    {
        var s = Settings(90);
        s.GameProfiles[ArmaKey] = new GameProfile { Crosshair = new CrosshairLook { Scale = 2 } };
        var clip = PressClipLength.Resolve(90, s, Arma);
        Assert.Equal(90, clip.Seconds);
        Assert.Equal(ClipLengthSource.Global, clip.Source);
    }

    [Fact]
    public void A_disk_or_ram_capped_ring_caps_the_global_length_too()
        => Assert.Equal(30, PressClipLength.Resolve(30, Settings(60), null).Seconds);

    [Fact]
    public void The_log_text_names_the_source_the_length_and_a_cap()
    {
        var s = Settings(60);
        Length(s, Arma, 1800);

        Assert.Equal("press length 1 min (standard)", PressClipLength.Describe(PressClipLength.Resolve(1800, s, null)));
        Assert.Equal("press length 30 min from profile 'armareforgersteam.exe'",
                     PressClipLength.Describe(PressClipLength.Resolve(1800, s, Arma)));
        var capped = PressClipLength.Describe(PressClipLength.Resolve(300, s, Arma));
        Assert.Equal("press length 5 min from profile 'armareforgersteam.exe' (the ring holds 5 min, the profile asks for 30 min)", capped);
    }

    [Fact]
    public void An_override_from_the_hook_keeps_the_source_of_the_game_but_is_capped_by_the_ring()
    {
        var s = Settings(60);
        Length(s, Arma, 120);
        var baseClip = PressClipLength.Resolve(600, s, Arma);

        var over = PressClipLength.WithRequested(baseClip, ringSeconds: 600, requested: 300);
        Assert.Equal(300, over.Seconds);
        Assert.Equal(ClipLengthSource.Profile, over.Source);

        var capped = PressClipLength.WithRequested(baseClip, ringSeconds: 100, requested: 300);
        Assert.Equal(100, capped.Seconds);
        Assert.True(capped.CappedByRing);
    }

    // ---- the settings list ----

    [Fact]
    public void The_rows_are_the_profiles_that_have_a_length_sorted_by_name()
    {
        var s = Settings();
        Length(s, "zeta.exe", 300, "Zeta");
        Length(s, "alpha.exe", 1800, "Alpha");
        s.GameProfiles["looks.exe"] = new GameProfile { DisplayName = "Only looks", Crosshair = new CrosshairLook { Scale = 2 } };
        s.GameProfiles["nameless.exe"] = new GameProfile { ClipSeconds = 45 };
        s.GameProfiles["crazy.exe"] = new GameProfile { DisplayName = "Crazy", ClipSeconds = 99_999 };

        var rows = ProfileLengths.Rows(s);

        Assert.Equal(new[] { "alpha.exe", "crazy.exe", "nameless.exe", "zeta.exe" }, rows.Select(r => r.Key).ToArray());
        Assert.Equal("Alpha", rows[0].DisplayName);
        Assert.Equal(1800, rows[1].Seconds);                  // hand-edited length: clamped like everywhere
        Assert.Equal("nameless.exe", rows[2].DisplayName);    // no stored name: the exe stands in
    }

    [Fact]
    public void There_are_no_rows_without_lengths()
        => Assert.Empty(ProfileLengths.Rows(Settings()));

    [Fact]
    public void Adding_the_current_game_is_possible_when_it_has_no_length_yet()
    {
        var s = Settings();
        var state = ProfileLengths.AddCurrent(s, Arma, "Arma Reforger");
        Assert.True(state.CanAdd);
        Assert.Equal(ArmaKey, state.Key);
        Assert.Equal("Arma Reforger", state.Name);
        Assert.Equal(AddCurrentReason.None, state.Reason);

        s.GameProfiles[ArmaKey] = new GameProfile { Crosshair = new CrosshairLook { Scale = 2 } };   // only a crosshair look
        Assert.True(ProfileLengths.AddCurrent(s, Arma, "Arma Reforger").CanAdd);
    }

    [Fact]
    public void Adding_is_refused_without_a_game_or_when_the_game_already_has_a_length()
    {
        var s = Settings();
        Assert.Equal(AddCurrentReason.NoGame, ProfileLengths.AddCurrent(s, null, null).Reason);
        Assert.Equal(AddCurrentReason.NoGame, ProfileLengths.AddCurrent(s, "Desktop", "Desktop").Reason);
        Assert.False(ProfileLengths.AddCurrent(s, null, null).CanAdd);

        Length(s, Arma, 300, "Arma Reforger");
        var has = ProfileLengths.AddCurrent(s, "ARMAREFORGERSTEAM.EXE", "Arma Reforger");
        Assert.False(has.CanAdd);
        Assert.Equal(AddCurrentReason.AlreadyHasLength, has.Reason);
        Assert.Equal(ArmaKey, has.Key);
    }

    [Fact]
    public void Adding_uses_the_exe_as_the_name_when_the_game_has_no_label()
        => Assert.Equal("cs2.exe", ProfileLengths.AddCurrent(Settings(), "cs2.exe", " ").Name);

    // ---- what sizes the ring ----

    [Fact]
    public void The_ring_seconds_are_the_longest_of_the_chosen_global_length_and_every_profile()
    {
        var s = Settings(60);
        Length(s, "a.exe", 120);
        Length(s, "b.exe", 1800);

        Assert.Equal(1800, ProfileLengths.RingSeconds(s, 60));
        Assert.Equal(1800, ProfileLengths.RingSeconds(s, 300));
        Assert.Equal(1800, ProfileLengths.RingSeconds(s, 1800));
        Assert.Equal(300, ProfileLengths.RingSeconds(Settings(60), 300));   // no profiles: the chosen length
    }

    [Fact]
    public void The_ring_driver_is_the_longest_profile_but_only_when_it_is_longer_than_the_global_length()
    {
        var s = Settings(60);
        Assert.Null(ProfileLengths.DriverOf(s, 60));

        Length(s, "short.exe", 30, "Short");
        Length(s, "same.exe", 60, "Same");
        Assert.Null(ProfileLengths.DriverOf(s, 60));

        Length(s, "arma.exe", 1800, "Arma Reforger");
        Length(s, "mid.exe", 300, "Mid");
        var driver = ProfileLengths.DriverOf(s, 60)!;
        Assert.Equal("arma.exe", driver.Key);
        Assert.Equal("Arma Reforger", driver.DisplayName);
        Assert.Equal(1800, driver.Seconds);

        Assert.Null(ProfileLengths.DriverOf(s, 1800));   // the global length was raised to the same: no profile drives the ring
    }

    [Fact]
    public void Two_profiles_with_the_same_longest_length_name_the_first_by_name()
    {
        var s = Settings(60);
        Length(s, "b.exe", 600, "Beta");
        Length(s, "a.exe", 600, "Alpha");
        Assert.Equal("a.exe", ProfileLengths.DriverOf(s, 60)!.Key);
    }

    // ---- the status note ----

    [Fact]
    public void The_status_note_names_the_game_when_its_profile_length_differs_from_the_ring()
    {
        var s = Settings(60);
        Length(s, Arma, 120);
        Length(s, "cs2.exe", 1800);

        var note = PressClipLength.StatusNote(s, Arma, "Arma Reforger", ringSeconds: 1800)!.Value;
        Assert.Equal("Arma Reforger", note.Game);
        Assert.Equal(120, note.Seconds);
    }

    [Fact]
    public void No_note_when_the_press_length_is_the_ring()
    {
        var s = Settings(60);
        Length(s, Arma, 1800);
        Assert.Null(PressClipLength.StatusNote(s, Arma, "Arma Reforger", 1800));
        Assert.Null(PressClipLength.StatusNote(Settings(60), null, null, 60));
    }

    [Fact]
    public void The_status_text_names_the_game_and_the_length()
    {
        var game = PressClipLength.StatusText(("Arma Reforger", 120));
        Assert.Contains("Arma Reforger", game);
        Assert.Contains("2 min", game);

        var plain = PressClipLength.StatusText((null, 60));
        Assert.Contains("1 min", plain);
        Assert.DoesNotContain("null", plain);
    }

    [Fact]
    public void Outside_a_profiled_game_a_ring_longer_than_the_global_length_still_gets_an_honest_note()
    {
        var s = Settings(60);
        Length(s, Arma, 1800);

        var none = PressClipLength.StatusNote(s, null, null, 1800)!.Value;     // desktop: F9 saves the global 60 s
        Assert.Null(none.Game);
        Assert.Equal(60, none.Seconds);

        var other = PressClipLength.StatusNote(s, "cs2.exe", "CS2", 1800)!.Value;
        Assert.Null(other.Game);
        Assert.Equal(60, other.Seconds);
    }
}
