using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Per-game profiles: the settings section (defaults, missing/null section, round trip, key clean-up)
/// and the pure resolver that merges a profile over the standard settings.
/// </summary>
public sealed class GameProfileTests : IDisposable
{
    private const string Arma = "ArmaReforgerSteam.exe";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_profiles_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static AppSettings Settings(int globalSeconds = 60)
    {
        var s = new AppSettings();
        s.ReplayBuffer.DurationSeconds = globalSeconds;
        return s;
    }

    private static GameProfile Profile(AppSettings s, string exe, int? seconds = null, CrosshairLook? look = null)
    {
        var p = new GameProfile { DisplayName = exe, ClipSeconds = seconds, Crosshair = look };
        s.GameProfiles[EffectiveSettings.ProfileKey(exe)!] = p;
        return p;
    }

    // ---- the settings section ----

    [Fact]
    public void A_fresh_settings_object_has_no_profiles()
    {
        var s = new AppSettings();
        Assert.NotNull(s.GameProfiles);
        Assert.Empty(s.GameProfiles);
    }

    [Fact]
    public void A_settings_file_from_before_profiles_existed_loads_without_any()
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>("{ \"SchemaVersion\": 9 }")!;
        SettingsService.EnsureSections(loaded);
        Assert.NotNull(loaded.GameProfiles);
        Assert.Empty(loaded.GameProfiles);
    }

    [Fact]
    public void An_explicit_null_section_is_replaced_by_an_empty_one()
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>("{ \"GameProfiles\": null }")!;
        SettingsService.EnsureSections(loaded);
        Assert.NotNull(loaded.GameProfiles);
        Assert.Empty(loaded.GameProfiles);
    }

    [Fact]
    public void Profiles_survive_a_json_round_trip_field_for_field()
    {
        var s = new AppSettings();
        s.GameProfiles["arma.exe"] = new GameProfile
        {
            DisplayName = "Arma Reforger",
            ClipSeconds = 1800,
            Crosshair = new CrosshairLook
            {
                ActiveId = "dot", Scale = 1.5, Opacity = 0.8, Brightness = 0.25, Contrast = 1.2, Saturation = 0.5,
                GainR = 1.1, GainG = 0.9, GainB = 0.7, OffsetX = -12.5, OffsetY = 30,
            },
        };
        s.GameProfiles["cs2.exe"] = new GameProfile { DisplayName = "CS2" };   // nothing but a name

        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s))!;

        Assert.Equal(2, back.GameProfiles.Count);
        var arma = back.GameProfiles["arma.exe"];
        Assert.Equal("Arma Reforger", arma.DisplayName);
        Assert.Equal(1800, arma.ClipSeconds);
        var look = arma.Crosshair!;
        Assert.Equal("dot", look.ActiveId);
        Assert.Equal(1.5, look.Scale);
        Assert.Equal(0.8, look.Opacity);
        Assert.Equal(0.25, look.Brightness);
        Assert.Equal(1.2, look.Contrast);
        Assert.Equal(0.5, look.Saturation);
        Assert.Equal(1.1, look.GainR);
        Assert.Equal(0.9, look.GainG);
        Assert.Equal(0.7, look.GainB);
        Assert.Equal(-12.5, look.OffsetX);
        Assert.Equal(30, look.OffsetY);
        Assert.Null(back.GameProfiles["cs2.exe"].ClipSeconds);
        Assert.Null(back.GameProfiles["cs2.exe"].Crosshair);
    }

    [Fact]
    public void Profiles_survive_a_save_and_load_through_the_settings_service()
    {
        Directory.CreateDirectory(_dir);
        var svc = new SettingsService(_dir);
        svc.Current.SchemaVersion = SettingsService.CurrentSchemaVersion;
        svc.Current.Output.ClipsFolder = Path.Combine(_dir, "clips");
        svc.Current.Output.ScreenshotsFolder = Path.Combine(_dir, "shots");
        svc.Current.Output.BufferFolder = Path.Combine(_dir, "buffer");
        svc.Current.GameProfiles["arma.exe"] = new GameProfile { DisplayName = "Arma", ClipSeconds = 600 };
        svc.Save();

        var again = new SettingsService(_dir);
        var loaded = again.Load();

        Assert.Equal(600, loaded.GameProfiles["arma.exe"].ClipSeconds);
        Assert.Equal("Arma", loaded.GameProfiles["arma.exe"].DisplayName);
    }

    [Fact]
    public void Hand_edited_keys_are_cleaned_up_when_the_sections_are_ensured()
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>(@"{ ""GameProfiles"": {
            ""ArmaReforgerSteam.EXE"": { ""DisplayName"": ""Arma"", ""ClipSeconds"": 300 },
            ""  "": { ""DisplayName"": ""blank key"" },
            ""cs2"": { ""DisplayName"": ""CS2"" },
            ""nothing.exe"": null } }")!;

        SettingsService.EnsureSections(loaded);

        Assert.Equal(2, loaded.GameProfiles.Count);
        Assert.Equal(300, loaded.GameProfiles["armareforgersteam.exe"].ClipSeconds);
        Assert.True(loaded.GameProfiles.ContainsKey("cs2.exe"));
        Assert.Equal(300, EffectiveSettings.ClipSecondsFor(loaded, Arma));
    }

    [Fact]
    public void Normalize_keeps_the_first_of_two_keys_that_mean_the_same_exe()
    {
        var raw = new Dictionary<string, GameProfile>
        {
            ["Game.exe"] = new GameProfile { DisplayName = "first" },
            ["game.EXE"] = new GameProfile { DisplayName = "second" },
        };
        var clean = GameProfiles.Normalize(raw);
        var only = Assert.Single(clean);
        Assert.Equal("game.exe", only.Key);
        Assert.Equal("first", only.Value.DisplayName);
    }

    // ---- exe key ----

    [Theory]
    [InlineData("ArmaReforgerSteam.exe", "armareforgersteam.exe")]
    [InlineData("  Cs2.EXE ", "cs2.exe")]
    [InlineData(@"C:\Games\Foo\Foo.exe", "foo.exe")]
    [InlineData("arma", "arma.exe")]
    [InlineData("VALORANT-Win64-Shipping.exe", "valorant-win64-shipping.exe")]
    public void The_key_is_the_lower_case_exe_file_name(string exe, string key)
        => Assert.Equal(key, EffectiveSettings.ProfileKey(exe));

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("   ")]
    [InlineData("Desktop")] [InlineData("desktop")] [InlineData("Unknown")] [InlineData(@"C:\")]
    public void No_exe_the_desktop_or_unknown_has_no_key(string? exe)
        => Assert.Null(EffectiveSettings.ProfileKey(exe));

    [Fact]
    public void Looking_a_profile_up_ignores_case_and_path()
    {
        var s = Settings();
        var p = Profile(s, Arma, seconds: 120);
        Assert.Same(p, EffectiveSettings.ProfileFor(s, "armareforgersteam.EXE"));
        Assert.Same(p, EffectiveSettings.ProfileFor(s, @"D:\Steam\" + Arma));
        Assert.Null(EffectiveSettings.ProfileFor(s, "cs2.exe"));
        Assert.Null(EffectiveSettings.ProfileFor(s, null));
    }

    // ---- F9 length ----

    [Fact]
    public void Without_a_profile_the_global_length_is_used()
        => Assert.Equal(90, EffectiveSettings.ClipSecondsFor(Settings(90), Arma));

    [Fact]
    public void A_profile_length_wins_over_the_global_one()
    {
        var s = Settings(60);
        Profile(s, Arma, seconds: 300);
        Assert.Equal(300, EffectiveSettings.ClipSecondsFor(s, Arma));
        Assert.Equal(300, EffectiveSettings.ClipSecondsFor(s, "ARMAREFORGERSTEAM.exe"));
    }

    [Fact]
    public void A_profile_may_be_shorter_than_the_global_length()
    {
        var s = Settings(600);
        Profile(s, "cs2.exe", seconds: 30);
        Assert.Equal(30, EffectiveSettings.ClipSecondsFor(s, "cs2.exe"));
        Assert.Equal(600, EffectiveSettings.ClipSecondsFor(s, "other.exe"));
    }

    [Fact]
    public void A_profile_without_a_length_falls_back_to_the_global_one()
    {
        var s = Settings(90);
        Profile(s, Arma, seconds: null, look: new CrosshairLook { Scale = 2 });
        Assert.Equal(90, EffectiveSettings.ClipSecondsFor(s, Arma));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("Desktop")] [InlineData("Unknown")] [InlineData("not-profiled.exe")]
    public void No_game_a_desktop_capture_or_an_unknown_exe_uses_the_global_length(string? exe)
    {
        var s = Settings(75);
        Profile(s, Arma, seconds: 1800);
        Assert.Equal(75, EffectiveSettings.ClipSecondsFor(s, exe));
    }

    [Theory]
    [InlineData(5, 15)]
    [InlineData(99_999, 1800)]
    [InlineData(0, 60)]        // nonsense: the global length applies
    [InlineData(-30, 60)]
    public void A_hand_edited_profile_length_is_clamped_or_ignored(int stored, int effective)
    {
        var s = Settings(60);
        Profile(s, Arma, seconds: stored);
        Assert.Equal(effective, EffectiveSettings.ClipSecondsFor(s, Arma));
    }

    [Fact]
    public void A_hand_edited_global_length_is_clamped_too()
    {
        Assert.Equal(15, EffectiveSettings.ClipSecondsFor(Settings(1), null));
        Assert.Equal(1800, EffectiveSettings.ClipSecondsFor(Settings(86_400), null));
    }

    // ---- longest length (the buffer has to hold it) ----

    [Fact]
    public void The_longest_length_without_profiles_is_the_global_one()
        => Assert.Equal(60, EffectiveSettings.LongestClipSeconds(Settings(60)));

    [Fact]
    public void A_longer_profile_makes_the_buffer_plan_for_its_length()
    {
        var s = Settings(60);
        Profile(s, "a.exe", seconds: 120);
        Profile(s, "b.exe", seconds: 1800);
        Profile(s, "c.exe", seconds: 30);
        Assert.Equal(1800, EffectiveSettings.LongestClipSeconds(s));
    }

    [Fact]
    public void Shorter_profiles_never_shrink_the_plan_below_the_global_length()
    {
        var s = Settings(600);
        Profile(s, "a.exe", seconds: 30);
        Profile(s, "b.exe", seconds: null, look: new CrosshairLook());
        Assert.Equal(600, EffectiveSettings.LongestClipSeconds(s));
    }

    [Fact]
    public void The_longest_length_is_clamped_like_every_other_length()
    {
        var s = Settings(60);
        Profile(s, "a.exe", seconds: 500_000);
        Profile(s, "b.exe", seconds: -1);
        Assert.Equal(1800, EffectiveSettings.LongestClipSeconds(s));
    }

    [Fact]
    public void The_pure_helper_takes_plain_numbers_and_ignores_missing_ones()
    {
        Assert.Equal(60, EffectiveSettings.LongestClipSeconds(60, null));
        Assert.Equal(60, EffectiveSettings.LongestClipSeconds(60, new int?[] { null, 45 }));
        Assert.Equal(900, EffectiveSettings.LongestClipSeconds(60, new int?[] { 300, null, 900, 120 }));
        Assert.Equal(900, EffectiveSettings.LongestClipSeconds(900, new int?[] { 300 }));
    }

    // ---- crosshair look ----

    [Fact]
    public void Without_a_profile_the_look_is_the_global_one()
    {
        var s = Settings();
        s.Crosshair.Enabled = true;
        s.Crosshair.ActiveId = "cross";
        s.Crosshair.Scale = 2; s.Crosshair.Opacity = 0.5; s.Crosshair.Brightness = 0.1;
        s.Crosshair.Contrast = 1.5; s.Crosshair.Saturation = 0.7;
        s.Crosshair.RedGain = 1.2; s.Crosshair.GreenGain = 0.8; s.Crosshair.BlueGain = 0.6;
        s.Crosshair.CenterX = 1000; s.Crosshair.CenterY = 500;
        s.Crosshair.SnapToGrid = false; s.Crosshair.GridSize = 40;

        var look = EffectiveSettings.CrosshairLookFor(s, Arma);

        Assert.True(look.Enabled);
        Assert.Equal("cross", look.ActiveId);
        Assert.Equal(2, look.Scale);
        Assert.Equal(0.5, look.Opacity);
        Assert.Equal(0.1, look.Brightness);
        Assert.Equal(1.5, look.Contrast);
        Assert.Equal(0.7, look.Saturation);
        Assert.Equal(1.2, look.RedGain);
        Assert.Equal(0.8, look.GreenGain);
        Assert.Equal(0.6, look.BlueGain);
        Assert.Equal(1000, look.CenterX);
        Assert.Equal(500, look.CenterY);
        Assert.False(look.SnapToGrid);
        Assert.Equal(40, look.GridSize);
    }

    [Fact]
    public void The_resolved_look_is_a_copy_so_editing_it_never_touches_the_settings()
    {
        var s = Settings();
        var look = EffectiveSettings.CrosshairLookFor(s, null);
        look.Scale = 9; look.Enabled = true; look.CenterX = 1;
        Assert.Equal(1.0, s.Crosshair.Scale);
        Assert.False(s.Crosshair.Enabled);
        Assert.Null(s.Crosshair.CenterX);
    }

    [Fact]
    public void Only_the_fields_a_profile_sets_replace_the_global_values()
    {
        var s = Settings();
        s.Crosshair.ActiveId = "cross"; s.Crosshair.Scale = 2; s.Crosshair.Opacity = 0.9; s.Crosshair.Contrast = 1.4;
        Profile(s, Arma, look: new CrosshairLook { Scale = 0.5, GainB = 0.25 });

        var look = EffectiveSettings.CrosshairLookFor(s, Arma);

        Assert.Equal(0.5, look.Scale);             // overridden
        Assert.Equal(0.25, look.BlueGain);         // overridden
        Assert.Equal("cross", look.ActiveId);      // standard
        Assert.Equal(0.9, look.Opacity);           // standard
        Assert.Equal(1.4, look.Contrast);          // standard
        Assert.Equal(1.0, look.RedGain);           // standard default
    }

    [Fact]
    public void A_profile_can_pick_another_crosshair_image_and_an_empty_id_means_standard()
    {
        var s = Settings();
        s.Crosshair.ActiveId = "cross";
        Profile(s, "a.exe", look: new CrosshairLook { ActiveId = "dot" });
        Profile(s, "b.exe", look: new CrosshairLook { ActiveId = "" });

        Assert.Equal("dot", EffectiveSettings.CrosshairLookFor(s, "a.exe").ActiveId);
        Assert.Equal("cross", EffectiveSettings.CrosshairLookFor(s, "b.exe").ActiveId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Enabled_always_comes_from_the_global_setting_never_from_a_profile(bool globalEnabled)
    {
        // A profile that flipped Enabled would switch the capture path (MustHonorCaptureExclusion) and
        // restart the buffer exactly when a game starts, losing the lookback.
        var s = Settings();
        s.Crosshair.Enabled = globalEnabled;
        Profile(s, Arma, look: new CrosshairLook { Scale = 3, ActiveId = "dot", OffsetX = 5 });

        Assert.Equal(globalEnabled, EffectiveSettings.CrosshairLookFor(s, Arma).Enabled);
        Assert.Equal(globalEnabled, EffectiveSettings.CrosshairLookFor(s, null).Enabled);
        Assert.Equal(globalEnabled, EffectiveSettings.CrosshairLookFor(s, "Desktop").Enabled);
    }

    [Fact]
    public void A_profile_never_changes_what_the_capture_path_decision_sees()
    {
        var s = Settings();
        s.Crosshair.Enabled = false;
        bool before = FFmpegCommandBuilder.MustHonorCaptureExclusion(s);
        Profile(s, Arma, seconds: 1800, look: new CrosshairLook { Scale = 3 });
        Assert.Equal(before, FFmpegCommandBuilder.MustHonorCaptureExclusion(s));
    }

    [Theory]
    [InlineData(null)] [InlineData("Desktop")] [InlineData("Unknown")] [InlineData("not-profiled.exe")]
    public void No_game_or_an_unknown_one_gets_the_global_look(string? exe)
    {
        var s = Settings();
        s.Crosshair.Scale = 2;
        Profile(s, Arma, look: new CrosshairLook { Scale = 0.5 });
        Assert.Equal(2, EffectiveSettings.CrosshairLookFor(s, exe).Scale);
    }

    [Fact]
    public void The_position_stays_the_global_one_until_the_profile_has_an_offset()
    {
        var s = Settings();
        s.Crosshair.CenterX = 1280; s.Crosshair.CenterY = 720;
        Profile(s, Arma, look: new CrosshairLook { Scale = 2 });

        var look = EffectiveSettings.CrosshairLookFor(s, Arma);
        Assert.Equal(1280, look.CenterX);
        Assert.Equal(720, look.CenterY);
        Assert.Null(EffectiveSettings.CrosshairOffsetFor(s, Arma));
    }

    [Fact]
    public void A_profile_offset_is_reported_with_a_missing_axis_as_zero()
    {
        var s = Settings();
        Profile(s, "a.exe", look: new CrosshairLook { OffsetX = 12 });
        Profile(s, "b.exe", look: new CrosshairLook { OffsetY = -8 });
        Profile(s, "c.exe", look: new CrosshairLook { OffsetX = 3, OffsetY = 4 });
        Profile(s, "d.exe", look: new CrosshairLook());

        Assert.Equal((12.0, 0.0), EffectiveSettings.CrosshairOffsetFor(s, "a.exe"));
        Assert.Equal((0.0, -8.0), EffectiveSettings.CrosshairOffsetFor(s, "b.exe"));
        Assert.Equal((3.0, 4.0), EffectiveSettings.CrosshairOffsetFor(s, "c.exe"));
        Assert.Null(EffectiveSettings.CrosshairOffsetFor(s, "d.exe"));
        Assert.Null(EffectiveSettings.CrosshairOffsetFor(s, null));
    }

    // ---- profile editing helpers ----

    [Fact]
    public void Describe_names_what_a_profile_changes_for_the_log()
    {
        var s = Settings();
        Assert.Contains("standard", GameProfiles.Describe(s, Arma), StringComparison.OrdinalIgnoreCase);

        Profile(s, Arma, seconds: 300, look: new CrosshairLook { Scale = 2, OffsetX = 1 });
        var text = GameProfiles.Describe(s, Arma);
        Assert.Contains("300", text);
        Assert.Contains("Scale", text);
        Assert.Contains("OffsetX", text);
        Assert.DoesNotContain("Opacity", text);
    }

    [Fact]
    public void The_active_profile_key_names_the_profile_that_applies_or_nothing()
    {
        var s = Settings();
        Assert.Null(GameProfiles.ActiveKey(s, Arma));
        Profile(s, Arma, seconds: 300);
        Assert.Equal("armareforgersteam.exe", GameProfiles.ActiveKey(s, "ARMAREFORGERSTEAM.exe"));
        Assert.Null(GameProfiles.ActiveKey(s, "cs2.exe"));
        Assert.Null(GameProfiles.ActiveKey(s, null));
    }

    [Fact]
    public void Setting_a_length_creates_the_profile_and_clearing_it_prunes_the_empty_profile()
    {
        var s = Settings();
        GameProfiles.SetClipSeconds(s, Arma, "Arma Reforger", 900);
        Assert.Equal(900, EffectiveSettings.ClipSecondsFor(s, Arma));
        Assert.Equal("Arma Reforger", s.GameProfiles["armareforgersteam.exe"].DisplayName);

        GameProfiles.SetClipSeconds(s, Arma, "Arma Reforger", null);
        Assert.Empty(s.GameProfiles);
        Assert.Equal(60, EffectiveSettings.ClipSecondsFor(s, Arma));
    }

    [Fact]
    public void Clearing_the_length_keeps_a_profile_that_still_has_a_crosshair_look()
    {
        var s = Settings();
        Profile(s, Arma, seconds: 900, look: new CrosshairLook { Scale = 2 });
        GameProfiles.SetClipSeconds(s, Arma, null, null);
        Assert.Single(s.GameProfiles);
        Assert.Null(s.GameProfiles["armareforgersteam.exe"].ClipSeconds);
        Assert.Equal(2, s.GameProfiles["armareforgersteam.exe"].Crosshair!.Scale);
    }

    [Fact]
    public void Setting_a_length_for_the_desktop_or_no_game_is_refused()
    {
        var s = Settings();
        Assert.False(GameProfiles.SetClipSeconds(s, "Desktop", "Desktop", 900));
        Assert.False(GameProfiles.SetClipSeconds(s, null, null, 900));
        Assert.Empty(s.GameProfiles);
    }

    [Fact]
    public void Removing_a_profile_takes_both_the_length_and_the_look_with_it()
    {
        var s = Settings();
        Profile(s, Arma, seconds: 900, look: new CrosshairLook { Scale = 2 });
        Assert.True(GameProfiles.Remove(s, "ARMAREFORGERSTEAM.EXE"));
        Assert.False(GameProfiles.Remove(s, Arma));
        Assert.Equal(60, EffectiveSettings.ClipSecondsFor(s, Arma));
        Assert.Equal(1.0, EffectiveSettings.CrosshairLookFor(s, Arma).Scale);
    }

    [Fact]
    public void Forgetting_a_deleted_library_image_clears_it_from_every_profile()
    {
        var s = Settings();
        Profile(s, "a.exe", look: new CrosshairLook { ActiveId = "gone", Scale = 2 });
        Profile(s, "b.exe", look: new CrosshairLook { ActiveId = "kept" });

        GameProfiles.ForgetCrosshairId(s, "gone");

        Assert.Null(s.GameProfiles["a.exe"].Crosshair!.ActiveId);
        Assert.Equal(2, s.GameProfiles["a.exe"].Crosshair!.Scale);
        Assert.Equal("kept", s.GameProfiles["b.exe"].Crosshair!.ActiveId);
    }
}
