using System;
using System.Linq;
using System.Text.Json;
using System.Windows;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.Views;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>The "Gaming Utilities" widget: the old crosshair widget under a new name, with the AutoClicker tab next to it.</summary>
[Collection("WpfApplication")]   // only one System.Windows.Application per process
public sealed class GamingUtilitiesTests
{
    private static T OnSta<T>(Func<T> body) => WpfTestSupport.OnSta(() => { WpfTestSupport.EnsureTheme(); return body(); });

    // ---- the rename touches the label only ----

    [Fact]
    public void The_crosshair_widget_is_labelled_Gaming_Utilities()
        => Assert.Equal("Gaming Utilities", WidgetCatalog.Label(WidgetId.Crosshair));

    [Fact]
    public void The_widget_id_and_its_saved_name_stay_Crosshair()
    {
        Assert.Equal("Crosshair", WidgetId.Crosshair.ToString());

        // A saved layout names the widget by this id: an old settings.json must still find it.
        var state = JsonSerializer.Deserialize<WidgetState>("{\"Id\":\"Crosshair\",\"Visible\":true}",
            new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;
        Assert.Equal(WidgetId.Crosshair, state.Id);
    }

    [Fact]
    public void Only_the_widget_label_changed_the_crosshair_feature_keeps_its_name()
    {
        Assert.Equal("ToggleCrosshair", HotkeyActions.ToggleCrosshair);
        Assert.NotNull(new AppSettings().Crosshair);
    }

    // ---- the container ----

    [Fact]
    public void The_container_hosts_one_crosshair_view_and_one_autoclicker_view()
    {
        var r = OnSta(() =>
        {
            var view = new GamingUtilitiesView();
            return (view.Crosshair, view.AutoClicker, ReferenceEquals(view.Crosshair, view.Crosshair));
        });

        Assert.NotNull(r.Item1);
        Assert.NotNull(r.Item2);
        Assert.True(r.Item3);
    }

    [Fact]
    public void The_tabs_switch_between_the_two_views_and_start_on_the_crosshair()
    {
        var r = OnSta(() =>
        {
            var view = new GamingUtilitiesView();
            var start = (view.Crosshair.Visibility, view.AutoClicker.Visibility);

            view.TabAutoClicker.IsChecked = true;
            var auto = (view.Crosshair.Visibility, view.AutoClicker.Visibility);

            view.TabCrosshair.IsChecked = true;
            var back = (view.Crosshair.Visibility, view.AutoClicker.Visibility);
            return (start, auto, back);
        });

        Assert.Equal((Visibility.Visible, Visibility.Collapsed), r.start);
        Assert.Equal((Visibility.Collapsed, Visibility.Visible), r.auto);
        Assert.Equal((Visibility.Visible, Visibility.Collapsed), r.back);
    }

    [Fact]
    public void The_two_tabs_form_one_radio_group()
    {
        var group = OnSta(() =>
        {
            var view = new GamingUtilitiesView();
            return (view.TabCrosshair.GroupName, view.TabAutoClicker.GroupName);
        });

        Assert.Equal(group.Item1, group.Item2);
    }

    // ---- the AutoClicker tab builds ----

    [Fact]
    public void The_autoclicker_tab_builds_its_texts()
    {
        var r = OnSta(() =>
        {
            var view = new AutoClickerView();
            view.BuildTexts();
            var zones = view.ZonesPanel;
            return (view.WarningText.Text, view.AckBtn.Content?.ToString(), zones.IgnoreBtn.Content?.ToString(),
                    zones.IgnoreText.Text, zones.AddGameBtn.Content?.ToString());
        });

        Assert.Contains("Anti-Cheat", r.Item1);
        Assert.Contains("BattlEye", r.Item1);
        Assert.Equal("Verstanden", r.Item2);
        Assert.Equal("Blacklist ignorieren", r.Item3);
        Assert.Equal("Blacklist wird ignoriert (bis zum Neustart des Clippers)", r.Item4);
        Assert.Equal("Spiel hinzufügen", r.Item5);
    }

    [Fact]
    public void The_autoclicker_tab_offers_the_five_sub_tabs_in_the_design_order()
    {
        var r = OnSta(() =>
        {
            var view = new AutoClickerView();
            view.BuildTexts();
            var tabs = view.SubTabStrip.Children.OfType<System.Windows.Controls.RadioButton>()
                .Select(t => (Text: t.Content?.ToString(), Key: (string)t.Tag)).ToList();
            return (tabs, view.SubTabStrip.Children.Count);
        });

        Assert.Equal(new[] { "Einfach", "Erweitert", "Zonen & Schutz", "Klickpunkte", "Presets" }, r.tabs.Select(t => t.Text));
        // "patterns" is a known key already; its tab arrives with the mouse-patterns UI (plan task 7), which updates this test.
        Assert.Equal(WKI_Clipper.Services.AutoClicker.ClickerSettingsRules.SubTabs.Where(k => k != "patterns"), r.tabs.Select(t => t.Key));
        Assert.Equal(5, r.Count);
    }

    [Fact]
    public void The_hotkey_box_shows_the_combo_and_the_output_key_box_a_single_key()
    {
        var r = OnSta(() =>
        {
            var combo = new WKI_Clipper.Views.AutoClicker.HotkeyCaptureBox(required: true);
            combo.SetHotkey(WKI_Clipper.Services.AutoClicker.ClickerHotkey.Default());
            var key = new WKI_Clipper.Views.AutoClicker.HotkeyCaptureBox(required: false, singleKey: true);
            var empty = key.Content?.ToString();
            key.SetHotkey(new WKI_Clipper.Services.AutoClicker.ClickerHotkey { Keys = { 0x20 } });
            return (combo.Content?.ToString(), empty, key.Content?.ToString());
        });

        Assert.Equal("Strg+Alt+Y", r.Item1);
        Assert.Equal("— (keine Taste)", r.Item2);
        Assert.Equal("Leertaste", r.Item3);
    }

    [Fact]
    public void The_clear_button_restores_the_default_hotkey_and_empties_the_master()
    {
        var r = OnSta(() =>
        {
            var click = new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent);
            var main = new WKI_Clipper.Views.AutoClicker.HotkeyCaptureBox(required: true);
            main.SetHotkey(WKI_Clipper.Services.AutoClicker.ClickerHotkey.Default());
            bool defaultClearable = main.ClearButton.IsEnabled;
            main.SetHotkey(new WKI_Clipper.Services.AutoClicker.ClickerHotkey { Keys = { WKI_Clipper.Services.AutoClicker.ClickerVk.LButton } });
            WKI_Clipper.Services.AutoClicker.ClickerHotkey? mainChanged = null;
            main.HotkeyChanged += h => mainChanged = h;
            main.ClearButton.RaiseEvent(click);

            var master = new WKI_Clipper.Views.AutoClicker.HotkeyCaptureBox(required: false);
            bool emptyClearable = master.ClearButton.IsEnabled;
            master.SetHotkey(new WKI_Clipper.Services.AutoClicker.ClickerHotkey { Modifiers = WKI_Clipper.Services.AutoClicker.ClickerModifiers.Shift });
            bool masterRaised = false;
            WKI_Clipper.Services.AutoClicker.ClickerHotkey? masterChanged = new();
            master.HotkeyChanged += h => { masterRaised = true; masterChanged = h; };
            master.ClearButton.RaiseEvent(click);

            return (defaultClearable, mainChanged, main.Content?.ToString(), main.ClearButton.Content?.ToString(), main.CancelButton.Visibility,
                    emptyClearable, masterRaised, masterChanged, master.Content?.ToString(), master.ClearButton.Content?.ToString());
        });

        Assert.False(r.defaultClearable);
        Assert.True(WKI_Clipper.Services.AutoClicker.ClickerHotkey.SameCombo(WKI_Clipper.Services.AutoClicker.ClickerHotkey.Default(), r.mainChanged));
        Assert.Equal("Strg+Alt+Y", r.Item3);
        Assert.Equal("Standard", r.Item4);
        Assert.Equal(Visibility.Collapsed, r.Item5);   // cancel only shows while recording
        Assert.False(r.emptyClearable);
        Assert.True(r.masterRaised);
        Assert.Null(r.masterChanged);
        Assert.Equal("(keine)", r.Item9);
        Assert.Equal("Leeren", r.Item10);
    }

    [Fact]
    public void The_game_dialog_names_the_whitelist_and_marks_whitelisted_programs()
    {
        var settings = new AppSettings();
        settings.AutoClicker.Blacklist.Add(new BlacklistEntry { ExeName = "cs2.exe", DisplayName = "Counter-Strike 2" });
        var white = new System.Collections.Generic.List<BlacklistEntry> { new() { ExeName = "game.exe", DisplayName = "Game" } };

        var forWhitelist = AutoClickerBlacklist.Candidates(settings, "game.exe", "Game", listed: white);
        var forBlacklist = AutoClickerBlacklist.Candidates(settings, "game.exe", "Game");
        var title = OnSta(() => new BlacklistGameDialog(forWhitelist, owner: null, whitelist: true).Title);

        Assert.True(forWhitelist.Single(c => c.ExeName == "game.exe").AlreadyListed);
        Assert.False(forBlacklist.Single(c => c.ExeName == "game.exe").AlreadyListed);
        Assert.Equal("Programm zur Whitelist hinzufügen", title);
    }

    [Fact]
    public void The_game_dialog_lists_candidates_and_disables_those_already_listed()
    {
        var items = OnSta(() =>
        {
            var dlg = new BlacklistGameDialog(new[]
            {
                new BlacklistCandidate("cs2.exe", "Counter-Strike 2", BlacklistSource.Profile, AlreadyListed: false),
                new BlacklistCandidate("arma.exe", "Arma", BlacklistSource.Detected, AlreadyListed: true),
            }, owner: null);
            return dlg.ChoiceList.Items.Cast<System.Windows.Controls.ListBoxItem>().Select(i => i.IsEnabled).ToList();
        });

        Assert.Equal(new[] { true, false }, items);
    }
}
