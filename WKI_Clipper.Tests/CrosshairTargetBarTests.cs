using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.Views;
using Xunit;
using Application = System.Windows.Application;

namespace WKI_Clipper.Tests;

/// <summary>
/// The "Gilt für" bar of the crosshair widget on a real STA thread with the app theme: what it offers, that
/// choosing a game only moves the editor's target (nothing is written), and that it lays out and renders.
/// </summary>
[Collection("WpfApplication")]   // only one System.Windows.Application per process
public sealed class CrosshairTargetBarTests
{
    private const string Arma = "ArmaReforgerSteam.exe";
    private static readonly LayoutRect Mon = new(0, 0, 2560, 1440);

    private static GameInfo ArmaGame() => new(777, Arma, "Arma Reforger") { Monitor = Mon };

    [Fact]
    public void The_bar_offers_all_games_the_detected_game_and_existing_profiles()
    {
        var result = OnSta(() =>
        {
            var s = new AppSettings();
            s.GameProfiles["cs2.exe"] = new GameProfile { DisplayName = "CS2", ClipSeconds = 300 };
            var bar = new CrosshairTargetBar(s, new CrosshairProfileEditor(s), ArmaGame);
            return (keys: bar.Entries.Select(e => e.Key).ToArray(), hint: bar.HintText, resetVisible: bar.ResetVisible);
        });

        Assert.Equal(new string?[] { null, "armareforgersteam.exe", "cs2.exe" }, result.keys);
        Assert.Contains("Arma Reforger", result.hint);   // "Gerade erkannt: ..." names the game in front
        Assert.False(result.resetVisible);               // nothing to reset for "Alle Spiele"
    }

    [Fact]
    public void Choosing_a_game_moves_the_editor_target_and_writes_nothing()
    {
        var r = OnSta(() =>
        {
            var s = new AppSettings();
            var editor = new CrosshairProfileEditor(s);
            var bar = new CrosshairTargetBar(s, editor, ArmaGame);
            int changed = 0;
            bar.TargetChanged += () => changed++;

            bool found = bar.Select("armareforgersteam.exe");

            return (found, key: editor.TargetKey, changed, profiles: s.GameProfiles.Count,
                    hint: bar.HintText, resetVisible: bar.ResetVisible, resetEnabled: bar.ResetEnabled);
        });

        Assert.True(r.found);
        Assert.Equal("armareforgersteam.exe", r.key);
        Assert.Equal(1, r.changed);
        Assert.Equal(0, r.profiles);
        Assert.Contains("Arma Reforger", r.hint);
        Assert.True(r.resetVisible);
        Assert.False(r.resetEnabled);   // no own values yet
    }

    [Fact]
    public void An_edited_profile_enables_back_to_standard_and_the_button_removes_the_values()
    {
        var r = OnSta(() =>
        {
            var s = new AppSettings();
            var editor = new CrosshairProfileEditor(s);
            var bar = new CrosshairTargetBar(s, editor, ArmaGame);
            bar.Select("armareforgersteam.exe");
            editor.Set(CrosshairField.Scale, 0.5);
            bar.Refresh();
            bool enabledAfterEdit = bar.ResetEnabled;

            int reset = 0;
            bar.ProfileReset += () => reset++;
            bar.ResetSelected();

            return (enabledAfterEdit, reset, profiles: s.GameProfiles.Count, stillSelected: editor.TargetKey, enabledAfter: bar.ResetEnabled);
        });

        Assert.True(r.enabledAfterEdit);
        Assert.Equal(1, r.reset);
        Assert.Equal(0, r.profiles);                          // the emptied profile is gone
        Assert.Equal("armareforgersteam.exe", r.stillSelected);   // the game stays selected
        Assert.False(r.enabledAfter);
    }

    [Fact]
    public void Refreshing_keeps_the_selected_game_even_after_it_left_and_never_fires_a_change()
    {
        var r = OnSta(() =>
        {
            var s = new AppSettings();
            var editor = new CrosshairProfileEditor(s);
            GameInfo? current = ArmaGame();
            var bar = new CrosshairTargetBar(s, editor, () => current);
            bar.Select("armareforgersteam.exe");
            int changed = 0;
            bar.TargetChanged += () => changed++;

            current = null;          // the game exited; no profile was ever created
            bar.Refresh();

            return (key: editor.TargetKey, entries: bar.Entries.Select(e => e.Key).ToArray(), changed);
        });

        Assert.Equal("armareforgersteam.exe", r.key);
        Assert.Contains("armareforgersteam.exe", r.entries);
        Assert.Equal(0, r.changed);
    }

    [Fact]
    public void Choosing_all_games_again_switches_back()
    {
        var key = OnSta(() =>
        {
            var s = new AppSettings();
            var editor = new CrosshairProfileEditor(s);
            var bar = new CrosshairTargetBar(s, editor, ArmaGame);
            bar.Select("armareforgersteam.exe");
            bar.Select(null);
            return editor.TargetKey;
        });
        Assert.Null(key);
    }

    [Fact]
    public void The_bar_lays_out_with_the_theme_and_renders_to_a_picture()
    {
        var size = OnSta(() =>
        {
            var s = new AppSettings();
            s.GameProfiles["cs2.exe"] = new GameProfile { DisplayName = "Counter-Strike 2", Crosshair = new CrosshairLook { Scale = 2 } };
            var editor = new CrosshairProfileEditor(s);
            var bar = new CrosshairTargetBar(s, editor, ArmaGame);
            bar.Select("cs2.exe");

            var host = new System.Windows.Controls.Border
            {
                Width = 380, Padding = new Thickness(12),
                Background = (System.Windows.Media.Brush)Application.Current.FindResource("BgBrush"),
                Child = bar
            };
            host.Measure(new System.Windows.Size(380, 600));
            host.Arrange(new Rect(0, 0, 380, host.DesiredSize.Height));
            host.UpdateLayout();

            var bmp = new RenderTargetBitmap(380, (int)Math.Ceiling(host.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
            bmp.Render(host);
            var dir = Path.Combine(Path.GetTempPath(), "wki_clipper_ui");
            Directory.CreateDirectory(dir);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var f = File.Create(Path.Combine(dir, "crosshair_target_bar.png"))) enc.Save(f);
            return (w: host.ActualWidth, h: host.DesiredSize.Height);
        });

        Assert.Equal(380, size.w);
        Assert.InRange(size.h, 60, 400);
    }

    private static T OnSta<T>(Func<T> body)
        => WpfTestSupport.OnSta(() => { WpfTestSupport.EnsureTheme(); return body(); });
}
