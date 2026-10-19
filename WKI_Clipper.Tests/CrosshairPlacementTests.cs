using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// A profile stores the crosshair position as an offset in DIPs from the centre of the monitor the game is on
/// (absolute pixels would break across resolutions and monitors). These tests pin the maths and the rule that
/// decides whether a drag lands in a profile or in the global position.
/// </summary>
public sealed class CrosshairPlacementTests
{
    private const string Arma = "ArmaReforgerSteam.exe";
    private static readonly LayoutRect Main = new(0, 0, 2560, 1440);
    private static readonly LayoutRect Right = new(2560, 0, 1920, 1080);     // second monitor, 125 %
    private static readonly LayoutRect Left = new(-1920, 0, 1920, 1080);     // a monitor left of the primary

    // ---- offset -> physical centre ----

    [Fact]
    public void A_zero_offset_is_the_centre_of_the_monitor()
        => Assert.Equal((1280.0, 720.0), CrosshairPlacement.Center(Main, 1.0, 0, 0));

    [Fact]
    public void The_offset_is_added_in_dips_scaled_to_physical_pixels()
    {
        Assert.Equal((1290.0, 700.0), CrosshairPlacement.Center(Main, 1.0, 10, -20));
        Assert.Equal((1295.0, 705.0), CrosshairPlacement.Center(Main, 1.5, 10, -10));   // 15 px / -15 px
    }

    [Fact]
    public void The_same_offset_follows_the_game_to_a_monitor_with_another_resolution_and_scale()
    {
        // 2560x1440 at 100 %: centre (1280,720) + (10,0)  ->  (1290,720)
        Assert.Equal((1290.0, 720.0), CrosshairPlacement.Center(Main, 1.0, 10, 0));
        // 1920x1080 at 125 % to the right: centre (3520,540) + 10 dip * 1.25
        Assert.Equal((3532.5, 540.0), CrosshairPlacement.Center(Right, 1.25, 10, 0));
    }

    [Fact]
    public void A_monitor_with_negative_coordinates_works_like_any_other()
        => Assert.Equal((-950.0, 560.0), CrosshairPlacement.Center(Left, 1.0, 10, 20));

    [Fact]
    public void A_nonsense_scale_counts_as_one()
    {
        Assert.Equal((1290.0, 720.0), CrosshairPlacement.Center(Main, 0, 10, 0));
        Assert.Equal((1290.0, 720.0), CrosshairPlacement.Center(Main, -2, 10, 0));
    }

    // ---- physical centre -> offset ----

    [Theory]
    [InlineData(1.0, 0, 0)]
    [InlineData(1.0, 12.5, -7)]
    [InlineData(1.25, -33, 18)]
    [InlineData(1.5, 100, 100)]
    [InlineData(2.0, -0.5, 0.25)]
    public void A_drag_converted_to_an_offset_lands_on_the_same_spot_again(double scale, double ox, double oy)
    {
        foreach (var monitor in new[] { Main, Right, Left })
        {
            var (px, py) = CrosshairPlacement.Center(monitor, scale, ox, oy);
            var (bx, by) = CrosshairPlacement.OffsetFrom(monitor, scale, px, py);
            Assert.Equal(ox, bx, 6);
            Assert.Equal(oy, by, 6);
        }
    }

    [Fact]
    public void An_offset_is_measured_from_the_centre_of_the_game_monitor()
    {
        // dropped 150 px right of and 75 px above the centre of a 125 % monitor
        var (x, y) = CrosshairPlacement.OffsetFrom(Right, 1.25, 3520 + 150, 540 - 75);
        Assert.Equal(120.0, x, 6);
        Assert.Equal(-60.0, y, 6);
    }

    [Fact]
    public void Stored_offsets_are_rounded_so_float_noise_does_not_end_up_in_the_file()
    {
        var (x, y) = CrosshairPlacement.OffsetFrom(Main, 1.25, 1280 + 10, 720 + 3);
        Assert.Equal(8.0, x);          // 10 / 1.25
        Assert.Equal(2.4, y);          // 3 / 1.25
        var (nx, _) = CrosshairPlacement.OffsetFrom(Main, 3.0, 1280 + 1, 720);
        Assert.Equal(0.33, nx);        // 0.3333... rounded to two decimals
    }

    // ---- which monitor ----

    [Fact]
    public void The_monitor_the_window_is_on_now_beats_the_snapshot_and_the_primary()
        => Assert.Equal(Right, CrosshairPlacement.ResolveMonitor(live: Right, snapshot: Main, primary: Left));

    [Fact]
    public void Without_a_live_monitor_the_snapshot_is_used_and_then_the_primary()
    {
        Assert.Equal(Main, CrosshairPlacement.ResolveMonitor(null, Main, Left));
        Assert.Equal(Left, CrosshairPlacement.ResolveMonitor(null, null, Left));
    }

    [Fact]
    public void An_empty_rectangle_counts_as_missing()
    {
        var empty = new LayoutRect(0, 0, 0, 0);
        Assert.Equal(Main, CrosshairPlacement.ResolveMonitor(empty, Main, Left));
        Assert.Equal(Left, CrosshairPlacement.ResolveMonitor(empty, empty, Left));
    }

    [Fact]
    public void After_a_resolution_change_the_centre_follows_the_live_monitor_not_the_stale_one()
    {
        // detected on a 2560x1440 monitor, then switched to 1920x1080 (the snapshot still says 2560x1440)
        var live = new LayoutRect(0, 0, 1920, 1080);
        var monitor = CrosshairPlacement.ResolveMonitor(live, Main, Left);
        Assert.Equal((960.0, 540.0), CrosshairPlacement.Center(monitor, 1.0, 0, 0));
    }

    [Fact]
    public void The_game_monitor_is_the_reference_only_for_the_profile_of_the_game_in_front()
    {
        Assert.True(CrosshairPlacement.OffsetUsesGameMonitor(Arma, "armareforgersteam.exe"));
        Assert.True(CrosshairPlacement.OffsetUsesGameMonitor("ARMAREFORGERSTEAM.EXE", "armareforgersteam.exe"));
        Assert.False(CrosshairPlacement.OffsetUsesGameMonitor(Arma, "cs2.exe"));      // another game's profile
        Assert.False(CrosshairPlacement.OffsetUsesGameMonitor(null, "cs2.exe"));      // no game in front
        Assert.False(CrosshairPlacement.OffsetUsesGameMonitor(Arma, null));           // the standard has no offset
    }

    [Fact]
    public void A_profile_dragged_to_the_second_monitor_while_its_game_is_not_running_stores_a_small_offset()
    {
        // Measured from the monitor the crosshair is dropped on (not the primary), the offset is 0/0 - so the
        // crosshair later lands in the middle of the game's monitor instead of 2240 px beyond it.
        var (ox, oy) = CrosshairPlacement.OffsetFrom(Right, 1.0, 3520, 540);
        Assert.Equal((0.0, 0.0), (ox, oy));
        var (wrongX, _) = CrosshairPlacement.OffsetFrom(Main, 1.0, 3520, 540);
        Assert.Equal(2240.0, wrongX);   // what measuring from the primary used to store
    }

    // ---- where does a drag go ----

    private static AppSettings Settings() => new();

    [Fact]
    public void Without_a_game_a_drag_moves_the_global_position()
        => Assert.Null(CrosshairPlacement.DragProfileKey(Settings(), null, null));

    [Fact]
    public void A_game_without_a_profile_drags_the_global_position()
        => Assert.Null(CrosshairPlacement.DragProfileKey(Settings(), Arma, null));

    [Fact]
    public void A_game_whose_profile_has_a_look_drags_into_the_profile()
    {
        var s = Settings();
        s.GameProfiles["armareforgersteam.exe"] = new GameProfile { Crosshair = new CrosshairLook { Scale = 2 } };
        Assert.Equal("armareforgersteam.exe", CrosshairPlacement.DragProfileKey(s, "ARMAREFORGERSTEAM.EXE", null));
    }

    [Fact]
    public void A_profile_that_only_sets_the_f9_length_does_not_capture_the_drag()
    {
        // The crosshair on screen is the global one there, so that is what the user moves.
        var s = Settings();
        s.GameProfiles["armareforgersteam.exe"] = new GameProfile { ClipSeconds = 900 };
        Assert.Null(CrosshairPlacement.DragProfileKey(s, Arma, null));
    }

    private static CrosshairEditTarget Editing(string? key, string name = "") => new(key, name);

    [Fact]
    public void While_editing_a_game_in_the_widget_the_drag_goes_into_its_profile_even_before_it_exists()
    {
        Assert.Equal("armareforgersteam.exe", CrosshairPlacement.DragProfileKey(Settings(), Arma, Editing("ArmaReforgerSteam.exe")));
        Assert.Equal("cs2.exe", CrosshairPlacement.DragProfileKey(Settings(), Arma, Editing("CS2.exe")));   // not the game in front
    }

    [Fact]
    public void While_editing_all_games_the_drag_moves_the_standard_even_if_the_game_in_front_has_a_profile()
    {
        // The overlay previews the standard look while "Alle Spiele" is edited, so that is what the user drags.
        var s = Settings();
        s.GameProfiles["armareforgersteam.exe"] = new GameProfile { Crosshair = new CrosshairLook { OffsetX = 4 } };
        Assert.Null(CrosshairPlacement.DragProfileKey(s, Arma, Editing(null)));
    }

    [Fact]
    public void The_overlay_shows_the_edited_look_while_editing_and_the_game_in_front_otherwise()
    {
        Assert.Equal(Arma, CrosshairPlacement.LookExe(Arma, null));
        Assert.Null(CrosshairPlacement.LookExe(null, null));
        Assert.Equal("cs2.exe", CrosshairPlacement.LookExe(Arma, Editing("cs2.exe")));
        Assert.Null(CrosshairPlacement.LookExe(Arma, Editing(null)));   // all games: the standard look
    }

    [Fact]
    public void Writing_a_drag_into_a_profile_stores_the_offset_and_leaves_the_global_position_alone()
    {
        var s = Settings();
        s.Crosshair.CenterX = 500; s.Crosshair.CenterY = 400;

        GameProfiles.SetCrosshairOffset(s, Arma, "Arma Reforger", 12.5, -3);

        var look = s.GameProfiles["armareforgersteam.exe"].Crosshair!;
        Assert.Equal(12.5, look.OffsetX);
        Assert.Equal(-3, look.OffsetY);
        Assert.Null(look.Scale);                       // nothing else is claimed
        Assert.Equal(500, s.Crosshair.CenterX);
        Assert.Equal(400, s.Crosshair.CenterY);
        Assert.Equal("Arma Reforger", s.GameProfiles["armareforgersteam.exe"].DisplayName);
    }

    [Fact]
    public void A_dragged_profile_position_comes_back_through_the_resolver()
    {
        var s = Settings();
        GameProfiles.SetCrosshairOffset(s, Arma, "Arma Reforger", 20, 10);

        var (ox, oy) = EffectiveSettings.CrosshairOffsetFor(s, Arma)!.Value;
        var (x, y) = CrosshairPlacement.Center(Main, 1.0, ox, oy);

        Assert.Equal((1300.0, 730.0), (x, y));
    }
}
