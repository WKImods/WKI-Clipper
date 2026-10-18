using System.Linq;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// The crosshair widget's "Gilt für" selector edits either the global look or one game's profile. These
/// tests pin which object a slider move lands in, and that a profile only ever stores what was changed.
/// </summary>
public sealed class CrosshairProfileEditorTests
{
    private const string Arma = "ArmaReforgerSteam.exe";
    private const string ArmaKey = "armareforgersteam.exe";

    private static (AppSettings S, CrosshairProfileEditor E) Make()
    {
        var s = new AppSettings();
        s.Crosshair.Scale = 2.0;
        s.Crosshair.Opacity = 0.9;
        return (s, new CrosshairProfileEditor(s));
    }

    [Fact]
    public void The_editor_starts_on_all_games()
    {
        var (_, e) = Make();
        Assert.Null(e.TargetKey);
        Assert.False(e.IsProfile);
    }

    [Fact]
    public void Editing_all_games_writes_the_global_look()
    {
        var (s, e) = Make();
        e.Set(CrosshairField.Scale, 3.0);
        e.Set(CrosshairField.GainG, 0.5);
        Assert.Equal(3.0, s.Crosshair.Scale);
        Assert.Equal(0.5, s.Crosshair.GreenGain);
        Assert.Empty(s.GameProfiles);
    }

    [Fact]
    public void Editing_a_game_creates_a_profile_that_holds_only_the_changed_field()
    {
        var (s, e) = Make();
        e.SetTarget(Arma, "Arma Reforger");

        e.Set(CrosshairField.Opacity, 0.4);

        var p = Assert.Single(s.GameProfiles).Value;
        Assert.Equal(ArmaKey, s.GameProfiles.Keys.Single());
        Assert.Equal("Arma Reforger", p.DisplayName);
        Assert.Equal(0.4, p.Crosshair!.Opacity);
        Assert.Null(p.Crosshair.Scale);
        Assert.Null(p.Crosshair.Brightness);
        Assert.Null(p.Crosshair.ActiveId);
        Assert.Null(p.ClipSeconds);
        Assert.Equal(0.9, s.Crosshair.Opacity);   // the standard is untouched
        Assert.Equal(2.0, s.Crosshair.Scale);
    }

    [Fact]
    public void Just_selecting_a_game_creates_nothing()
    {
        var (s, e) = Make();
        e.SetTarget(Arma, "Arma Reforger");
        Assert.Empty(s.GameProfiles);
        Assert.True(e.IsProfile);
        Assert.Equal(ArmaKey, e.TargetKey);
    }

    [Fact]
    public void A_profile_target_reads_its_own_value_or_the_standard_where_it_has_none()
    {
        var (_, e) = Make();
        e.SetTarget(Arma, "Arma");
        Assert.Equal(2.0, e.Get(CrosshairField.Scale));     // standard shows through
        Assert.False(e.IsOverridden(CrosshairField.Scale));

        e.Set(CrosshairField.Scale, 0.5);
        Assert.Equal(0.5, e.Get(CrosshairField.Scale));
        Assert.True(e.IsOverridden(CrosshairField.Scale));
        Assert.Equal(0.9, e.Get(CrosshairField.Opacity));   // still the standard

        e.SetTarget(null, null);
        Assert.Equal(2.0, e.Get(CrosshairField.Scale));     // all games: unchanged
    }

    [Fact]
    public void A_changed_standard_still_shows_through_fields_the_profile_did_not_set()
    {
        var (s, e) = Make();
        e.SetTarget(Arma, "Arma");
        e.Set(CrosshairField.Scale, 0.5);

        s.Crosshair.Opacity = 0.3;

        Assert.Equal(0.3, e.Get(CrosshairField.Opacity));
        Assert.Equal(0.5, e.Get(CrosshairField.Scale));
    }

    [Fact]
    public void Every_field_round_trips_through_the_editor_for_global_and_profile()
    {
        foreach (var target in new string?[] { null, Arma })
        {
            var (_, e) = Make();
            e.SetTarget(target, target);
            foreach (var f in System.Enum.GetValues<CrosshairField>())
            {
                double v = 0.37 + (int)f * 0.01;
                e.Set(f, v);
                Assert.Equal(v, e.Get(f));
            }
        }
    }

    [Fact]
    public void The_image_choice_goes_to_the_selected_target()
    {
        var (s, e) = Make();
        s.Crosshair.ActiveId = "cross";

        e.SetTarget(Arma, "Arma");
        Assert.Equal("cross", e.ActiveId);
        e.SetActiveId("dot");
        Assert.Equal("dot", e.ActiveId);
        Assert.Equal("cross", s.Crosshair.ActiveId);
        Assert.Equal("dot", s.GameProfiles[ArmaKey].Crosshair!.ActiveId);

        e.SetTarget(null, null);
        Assert.Equal("cross", e.ActiveId);
        e.SetActiveId("ring");
        Assert.Equal("ring", s.Crosshair.ActiveId);
        Assert.Equal("dot", s.GameProfiles[ArmaKey].Crosshair!.ActiveId);
    }

    [Fact]
    public void Back_to_standard_removes_the_whole_look_of_a_profile_only()
    {
        var (s, e) = Make();
        e.SetTarget(Arma, "Arma");
        e.Set(CrosshairField.Scale, 0.5);
        e.SetActiveId("dot");
        e.CenterExactly();
        Assert.True(e.HasOverrides);

        e.ResetToStandard();

        Assert.False(e.HasOverrides);
        Assert.Empty(s.GameProfiles);                       // nothing left in it, so the profile goes too
        Assert.Equal(2.0, e.Get(CrosshairField.Scale));
        Assert.Equal(2.0, s.Crosshair.Scale);               // the global values stay
    }

    [Fact]
    public void Back_to_standard_keeps_the_f9_length_of_the_same_profile()
    {
        var (s, e) = Make();
        GameProfiles.SetClipSeconds(s, Arma, "Arma", 900);
        e.SetTarget(Arma, "Arma");
        e.Set(CrosshairField.Scale, 0.5);

        e.ResetToStandard();

        var p = Assert.Single(s.GameProfiles).Value;
        Assert.Equal(900, p.ClipSeconds);
        Assert.Null(p.Crosshair);
    }

    [Fact]
    public void Back_to_standard_does_nothing_for_all_games()
    {
        var (s, e) = Make();
        e.ResetToStandard();
        Assert.Equal(2.0, s.Crosshair.Scale);
        Assert.False(e.HasOverrides);
    }

    [Fact]
    public void Center_exactly_clears_the_global_position_but_pins_a_profile_to_the_monitor_centre()
    {
        var (s, e) = Make();
        s.Crosshair.CenterX = 1000; s.Crosshair.CenterY = 400;

        e.CenterExactly();
        Assert.Null(s.Crosshair.CenterX);
        Assert.Null(s.Crosshair.CenterY);

        e.SetTarget(Arma, "Arma");
        e.CenterExactly();
        var look = s.GameProfiles[ArmaKey].Crosshair!;
        Assert.Equal(0.0, look.OffsetX);
        Assert.Equal(0.0, look.OffsetY);
        Assert.Null(s.Crosshair.CenterX);
    }

    [Fact]
    public void The_editor_never_touches_enabled()
    {
        var (s, e) = Make();
        s.Crosshair.Enabled = true;
        e.SetTarget(Arma, "Arma");
        e.Set(CrosshairField.Scale, 1);
        e.SetActiveId("dot");
        e.CenterExactly();
        e.ResetToStandard();
        Assert.True(s.Crosshair.Enabled);
    }

    [Fact]
    public void The_target_for_the_desktop_or_nothing_is_all_games()
    {
        var (_, e) = Make();
        e.SetTarget("Desktop", "Desktop");
        Assert.Null(e.TargetKey);
        Assert.False(e.IsProfile);
    }

    // ---- selector entries ----

    private static AppSettings WithProfiles()
    {
        var s = new AppSettings();
        s.GameProfiles["zeta.exe"] = new GameProfile { DisplayName = "Zeta", Crosshair = new CrosshairLook { Scale = 2 } };
        s.GameProfiles["alpha.exe"] = new GameProfile { DisplayName = "Alpha", ClipSeconds = 300 };
        s.GameProfiles["nameless.exe"] = new GameProfile { DisplayName = "" };
        return s;
    }

    [Fact]
    public void The_selector_lists_all_games_first_then_the_profiles_sorted_by_name()
    {
        var entries = CrosshairTargetList.Build(WithProfiles(), currentExe: null, currentName: null);

        Assert.Equal(new string?[] { null, "alpha.exe", "nameless.exe", "zeta.exe" }, entries.Select(x => x.Key).ToArray());
        Assert.Equal("Alpha", entries[1].DisplayName);
        Assert.Equal("nameless.exe", entries[2].DisplayName);   // no stored name: the exe stands in
        Assert.All(entries, x => Assert.False(x.IsCurrent));
    }

    [Fact]
    public void The_detected_game_is_offered_even_without_a_profile_and_right_after_all_games()
    {
        var entries = CrosshairTargetList.Build(WithProfiles(), "ArmaReforgerSteam.exe", "Arma Reforger");

        Assert.Equal(new string?[] { null, ArmaKey, "alpha.exe", "nameless.exe", "zeta.exe" }, entries.Select(x => x.Key).ToArray());
        Assert.True(entries[1].IsCurrent);
        Assert.Equal("Arma Reforger", entries[1].DisplayName);
        Assert.False(entries[0].IsCurrent);
    }

    [Fact]
    public void A_detected_game_that_has_a_profile_appears_once_and_is_marked_current()
    {
        var entries = CrosshairTargetList.Build(WithProfiles(), "ALPHA.exe", "Alpha");

        Assert.Equal(1, entries.Count(x => x.Key == "alpha.exe"));
        Assert.True(entries.Single(x => x.Key == "alpha.exe").IsCurrent);
        Assert.Equal(4, entries.Count);
    }

    [Fact]
    public void The_game_being_edited_stays_in_the_list_even_without_a_profile()
    {
        var entries = CrosshairTargetList.Build(new AppSettings(), currentExe: null, currentName: null, "cs2.exe", "CS2");

        Assert.Equal(new string?[] { null, "cs2.exe" }, entries.Select(x => x.Key).ToArray());
        Assert.Equal("CS2", entries[1].DisplayName);
        Assert.False(entries[1].IsCurrent);
    }

    [Fact]
    public void The_desktop_is_never_offered_as_a_game()
    {
        var entries = CrosshairTargetList.Build(new AppSettings(), "Desktop", "Desktop");
        Assert.Single(entries);
        Assert.Null(entries[0].Key);
    }
}
