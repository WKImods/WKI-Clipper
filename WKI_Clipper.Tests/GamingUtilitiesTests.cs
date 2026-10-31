using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.Services.AutoClicker;
using WKI_Clipper.Views;
using WKI_Clipper.Views.AutoClicker;
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
    public void Subtabs_are_six_in_order()
    {
        var r = OnSta(() =>
        {
            var view = new AutoClickerView();
            view.BuildTexts();
            var tabs = view.SubTabStrip.Children.OfType<System.Windows.Controls.RadioButton>()
                .Select(t => (Text: t.Content?.ToString(), Key: (string)t.Tag)).ToList();
            return (tabs, view.SubTabStrip.Children.Count);
        });

        Assert.Equal(new[] { "Einfach", "Erweitert", "Zonen & Schutz", "Klickpunkte", "Mausbewegung", "Presets" }, r.tabs.Select(t => t.Text));
        Assert.Equal(ClickerSettingsRules.SubTabs, r.tabs.Select(t => t.Key));
        Assert.Equal(6, r.Count);
    }

    [Fact]
    public void The_status_row_has_the_second_switch_and_the_warning_names_movement_macros()
    {
        var r = OnSta(() =>
        {
            var view = new AutoClickerView();
            view.BuildTexts();
            return (view.WarningText.Text, view.PatternsArmedBox.ToolTip?.ToString(), view.ArmedBox.Parent == view.PatternsArmedBox.Parent);
        });

        Assert.Contains("Bewegungs-Makros", r.Item1);
        Assert.Contains("Anti-Cheat", r.Item1);
        Assert.Contains("Mausbewegung", r.Item2);
        Assert.True(r.Item3);   // next to "Clicker bereit"
    }

    // ---- the mouse patterns tab ----

    private const string PatternA = "0123456789abcdef0123456789abcdef";
    private const string PatternB = "fedcba9876543210fedcba9876543210";

    private static AppSettings TwoPatterns()
    {
        var settings = new AppSettings();
        var c = settings.AutoClicker.Clicker;
        c.MousePatternsArmed = true;
        c.MousePatterns = new List<MousePattern>
        {
            new() { Id = PatternA, Name = "Kreis", Hotkey = new ClickerHotkey { Keys = { 0x46 } }, Revision = 1, DurationMs = 2400, Program = "game.exe" },
            new() { Id = PatternB, Name = "Welle", Active = false, Revision = 1, DurationMs = 800 },
        };
        return settings;
    }

    private static ClickerState ReadyState => ClickerState.Off with { Phase = ClickerPhase.Ready, PatternsArmed = true };

    private static string Lines(System.Windows.Controls.Panel panel)
        => string.Join(Environment.NewLine, panel.Children.OfType<System.Windows.Controls.TextBlock>().Select(t => t.Text));

    [Fact]
    public void Panel_builds_with_two_patterns_and_no_data_file()
    {
        var store = new FakePatternStore();
        var r = OnSta(() =>
        {
            var panel = new MousePatternsPanel { Store = store };
            panel.Render(TwoPatterns(), ReadyState, recordTarget: PatternB);
            var a = panel.CardsById[PatternA];
            var b = panel.CardsById[PatternB];
            return (Cards: panel.Cards.Children.Count, Names: (a.NameBox.Text, b.NameBox.Text), Active: (a.Active.IsChecked, b.Active.IsChecked),
                    Points: a.Preview.Points.Count + b.Preview.Points.Count, Duration: a.Duration.Text,
                    OnlyIn: (a.OnlyIn.Visibility, a.OnlyIn.Content?.ToString(), b.OnlyIn.Visibility),
                    Hotkeys: (a.Hotkey.Content?.ToString(), b.Hotkey.Content?.ToString()), Speed: a.Speed.Text,
                    WarnA: Lines(a.Warnings), WarnB: Lines(b.Warnings),
                    Empty: panel.EmptyText.Visibility, Status: panel.StatusText.Text, Target: (panel.TargetRow.Visibility, panel.TargetText.Text),
                    Record: panel.RecordHotkey.Content?.ToString());
        });

        Assert.Equal(2, r.Cards);
        Assert.Equal(("Kreis", "Welle"), r.Names);
        Assert.Equal((true, false), r.Active);
        Assert.Equal(0, r.Points);   // no samples, no drawing
        Assert.Equal("2,4 s", r.Duration);
        Assert.Equal((Visibility.Visible, "Nur in game.exe", Visibility.Collapsed), r.OnlyIn);
        Assert.Equal(("F", "(keine)"), r.Hotkeys);
        Assert.Equal("1", r.Speed);
        Assert.Contains("Aufnahme fehlt", r.WarnA);
        Assert.Contains("Aufnahme fehlt", r.WarnB);
        Assert.Equal(Visibility.Collapsed, r.Empty);
        Assert.Equal("Bereit · 1 Muster aktiv", r.Status);
        Assert.Equal((Visibility.Visible, "Nächste Aufnahme ersetzt 'Welle' - Strg+Alt+R im Spiel drücken"), r.Target);
        Assert.Equal("Strg+Alt+R", r.Record);
    }

    [Fact]
    public void The_drawing_shows_the_samples_and_they_are_read_once_per_revision()
    {
        var store = new FakePatternStore();
        store.Put(PatternA, new PatternData(PatternTrack.Empty, new PatternTrack(new[] { 1, 2, 3 }, new[] { 10, 0, -10 }, new[] { 0, 10, 0 }), 3));
        var r = OnSta(() =>
        {
            var panel = new MousePatternsPanel { Store = store };
            var settings = TwoPatterns();
            panel.Render(settings, ReadyState, recordTarget: null);
            int loadsFirst = store.Loads;
            int points = panel.CardsById[PatternA].Preview.Points.Count;
            var warn = Lines(panel.CardsById[PatternA].Warnings);

            panel.Render(settings, ReadyState with { Recording = true }, recordTarget: null);
            int loadsSame = store.Loads;
            var status = panel.StatusText.Text;
            settings.AutoClicker.Clicker.MousePatterns[0].Revision = 2;
            panel.Render(settings, ReadyState, recordTarget: null);
            return (points, warn, loadsFirst, loadsSame, loadsAfter: store.Loads, status, target: panel.TargetRow.Visibility);
        });

        Assert.Equal(4, r.points);   // the start and one point per bin
        Assert.DoesNotContain("Aufnahme fehlt", r.warn);
        Assert.Equal(2, r.loadsFirst);   // both patterns once
        Assert.Equal(r.loadsFirst, r.loadsSame);
        Assert.Equal(r.loadsFirst + 1, r.loadsAfter);
        Assert.Equal("Aufnahme läuft · Strg+Alt+R beendet", r.status);
        Assert.Equal(Visibility.Collapsed, r.target);
    }

    [Fact]
    public void Cards_are_not_rebuilt_under_a_name_or_tempo_box_being_typed_in()
    {
        const string PatternC = "00112233445566778899aabbccddeeff";
        var r = OnSta(() =>
        {
            var panel = new MousePatternsPanel { Store = new FakePatternStore() };
            var settings = TwoPatterns();
            var patterns = settings.AutoClicker.Clicker.MousePatterns;
            panel.Render(settings, ReadyState, recordTarget: null);
            var name = panel.CardsById[PatternA].NameBox;
            name.Text = "Kreis schnell";                   // typed, not committed yet (that happens when the box is left)
            panel.HasKeyboardFocus = e => ReferenceEquals(e, name);

            patterns.Add(new MousePattern { Id = PatternC, Name = "Neu", Revision = 1 });   // a recording arrived meanwhile
            panel.Render(settings, ReadyState, recordTarget: null);
            var underName = (Same: ReferenceEquals(panel.CardsById[PatternA].NameBox, name), name.Text, panel.Cards.Children.Count);

            var speed = panel.CardsById[PatternB].Speed;
            panel.HasKeyboardFocus = e => ReferenceEquals(e, speed);
            patterns[1].Revision = 2;
            panel.Render(settings, ReadyState, recordTarget: null);
            var underSpeed = (Same: ReferenceEquals(panel.CardsById[PatternB].Speed, speed), panel.Cards.Children.Count);

            panel.HasKeyboardFocus = _ => false;           // the box was left: the cards follow
            panel.Render(settings, ReadyState, recordTarget: null);
            return (underName, underSpeed, Cards: panel.Cards.Children.Count, HasC: panel.CardsById.ContainsKey(PatternC),
                    Rebuilt: !ReferenceEquals(panel.CardsById[PatternA].NameBox, name));
        });

        Assert.True(r.underName.Same);
        Assert.Equal("Kreis schnell", r.underName.Text);
        Assert.Equal(2, r.underName.Item3);
        Assert.True(r.underSpeed.Same);
        Assert.Equal(2, r.underSpeed.Item2);
        Assert.Equal(3, r.Cards);
        Assert.True(r.HasC);
        Assert.True(r.Rebuilt);
    }

    [Fact]
    public void Without_patterns_the_tab_says_how_to_record_one()
    {
        var r = OnSta(() =>
        {
            var panel = new MousePatternsPanel { Store = new FakePatternStore() };
            panel.Render(new AppSettings(), ClickerState.Off, recordTarget: null);
            return (panel.Cards.Children.Count, panel.EmptyText.Visibility, panel.EmptyText.Text, panel.StatusText.Text);
        });

        Assert.Equal(0, r.Item1);
        Assert.Equal(Visibility.Visible, r.Item2);
        Assert.Equal("Noch keine Muster. Strg+Alt+R drücken, Bewegung machen, nochmal drücken.", r.Item3);
        Assert.Equal("Mausbewegung ist aus - oben 'Mausbewegung bereit' einschalten", r.Item4);
    }

    [Fact]
    public void The_record_hotkey_box_clears_to_its_own_default()
    {
        var r = OnSta(() =>
        {
            var click = new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent);
            var box = new HotkeyCaptureBox(required: true, defaultHotkey: MousePatternRules.DefaultRecordHotkey());
            box.SetHotkey(MousePatternRules.DefaultRecordHotkey());
            bool defaultClearable = box.ClearButton.IsEnabled;
            box.SetHotkey(new ClickerHotkey { Keys = { 0x46 } });
            ClickerHotkey? changed = null;
            box.HotkeyChanged += h => changed = h;
            box.ClearButton.RaiseEvent(click);
            return (defaultClearable, changed, box.Content?.ToString(), box.ClearButton.ToolTip?.ToString());
        });

        Assert.False(r.defaultClearable);
        Assert.True(ClickerHotkey.SameCombo(MousePatternRules.DefaultRecordHotkey(), r.changed));
        Assert.Equal("Strg+Alt+R", r.Item3);
        Assert.Contains("Strg+Alt+R", r.Item4);
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
