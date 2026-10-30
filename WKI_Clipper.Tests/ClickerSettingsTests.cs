using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Schema v12 (the native clicker section) and the clamps of <see cref="ClickerSettingsRules.Normalize"/>.</summary>
public sealed class ClickerSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_clicker_" + Guid.NewGuid().ToString("N"));

    public ClickerSettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static ClickerSettings Norm(Action<ClickerSettings> edit)
    {
        var c = new ClickerSettings();
        edit(c);
        return ClickerSettingsRules.Normalize(c);
    }

    // ---- migration v11 -> v12 ----

    [Fact]
    public void V11_settings_migrate_to_v12_with_default_clicker_settings_not_armed()
    {
        // A real v11 file has an AutoClicker section without a "Clicker" property.
        var s = JsonSerializer.Deserialize<AppSettings>(
            "{\"SchemaVersion\":11,\"AutoClicker\":{\"WarningAcknowledged\":true,\"Blacklist\":[{\"ExeName\":\"cs2.exe\",\"DisplayName\":\"CS2\"}]}}")!;

        bool changed = SettingsService.MigrateIfNeeded(s);

        Assert.True(changed);
        Assert.Equal(12, s.SchemaVersion);
        Assert.NotNull(s.AutoClicker.Clicker);
        Assert.False(s.AutoClicker.Clicker.Armed);
        Assert.Equal(25, s.AutoClicker.Clicker.ClickSpeed);
        Assert.Equal("simple", s.AutoClicker.Clicker.SubTab);
        // v11 data survives
        Assert.True(s.AutoClicker.WarningAcknowledged);
        Assert.Equal("cs2.exe", Assert.Single(s.AutoClicker.Blacklist).ExeName);
    }

    [Fact]
    public void The_migration_never_leaves_the_clicker_armed()
    {
        var s = new AppSettings { SchemaVersion = 11 };
        s.AutoClicker.Clicker.Armed = true;   // e.g. a pre-release build wrote it

        SettingsService.MigrateIfNeeded(s);

        Assert.False(s.AutoClicker.Clicker.Armed);
    }

    [Fact]
    public void An_explicit_null_clicker_section_is_replaced_on_load()
    {
        var s = JsonSerializer.Deserialize<AppSettings>("{\"SchemaVersion\":12,\"AutoClicker\":{\"Clicker\":null}}")!;
        Assert.Null(s.AutoClicker.Clicker);

        SettingsService.EnsureSections(s);

        Assert.NotNull(s.AutoClicker.Clicker);
        Assert.Equal(25, s.AutoClicker.Clicker.ClickSpeed);
    }

    [Fact]
    public void A_hand_edited_clicker_section_is_clamped_on_load()
    {
        var s = JsonSerializer.Deserialize<AppSettings>(
            "{\"SchemaVersion\":12,\"AutoClicker\":{\"Clicker\":{\"ClickSpeed\":99999,\"CornerSizePx\":0,\"Zones\":null,\"Hotkey\":null,\"SubTab\":\"nonsense\"}}}")!;

        SettingsService.EnsureSections(s);

        var c = s.AutoClicker.Clicker;
        Assert.Equal(500, c.ClickSpeed);
        Assert.Equal(1, c.CornerSizePx);
        Assert.Empty(c.Zones);
        Assert.True(ClickerHotkey.SameCombo(ClickerHotkey.Default(), c.Hotkey));
        Assert.Equal("simple", c.SubTab);
    }

    [Fact]
    public void A_pre_release_settings_file_on_a_removed_sub_tab_loads_on_the_first_sub_tab()
    {
        // Written by a pre-release build of 0.18.0 that still had a sub-tab that no longer exists; everything else in the section must survive.
        var s = JsonSerializer.Deserialize<AppSettings>(
            "{\"SchemaVersion\":12,\"AutoClicker\":{\"WarningAcknowledged\":true,\"GamingUtilitiesTab\":\"AutoClicker\"," +
            "\"Blacklist\":[{\"ExeName\":\"cs2.exe\",\"DisplayName\":\"CS2\"}],\"Clicker\":{\"ClickSpeed\":40,\"SubTab\":\"legacy\"}}}")!;

        bool migrated = SettingsService.MigrateIfNeeded(s);
        SettingsService.EnsureSections(s);

        Assert.False(migrated);   // no schema bump for a removed sub-tab
        var c = s.AutoClicker.Clicker;
        Assert.Equal("simple", c.SubTab);
        Assert.Equal(40, c.ClickSpeed);
        Assert.True(s.AutoClicker.WarningAcknowledged);
        Assert.Equal("AutoClicker", s.AutoClicker.GamingUtilitiesTab);
        Assert.Equal("cs2.exe", Assert.Single(s.AutoClicker.Blacklist).ExeName);
    }

    [Fact]
    public void A_pre_release_settings_file_on_a_removed_sub_tab_loads_from_disk_and_saves_normalized()
    {
        // Output folders point into the temp dir so Load()'s EnsureOutputDirs never touches the real Videos folder.
        var json = "{\"SchemaVersion\":12," +
                   "\"Output\":{\"ClipsFolder\":" + JsonSerializer.Serialize(Path.Combine(_dir, "clips")) +
                   ",\"ScreenshotsFolder\":" + JsonSerializer.Serialize(Path.Combine(_dir, "shots")) +
                   ",\"BufferFolder\":" + JsonSerializer.Serialize(Path.Combine(_dir, "buffer")) + "}," +
                   "\"AutoClicker\":{\"Clicker\":{\"SubTab\":\"legacy\"}}}";
        File.WriteAllText(Path.Combine(_dir, "settings.json"), json);

        var service = new SettingsService(_dir);
        service.Load();
        Assert.Equal("simple", service.Current.AutoClicker.Clicker.SubTab);

        service.Save();
        Assert.DoesNotContain("\"legacy\"", File.ReadAllText(Path.Combine(_dir, "settings.json")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Clicker_settings_round_trip_through_the_settings_file()
    {
        var first = new SettingsService(_dir);
        first.Current.SchemaVersion = SettingsService.CurrentSchemaVersion;
        // Output folders point into the temp dir so Load()'s EnsureOutputDirs never touches the real Videos folder.
        first.Current.Output.ClipsFolder = Path.Combine(_dir, "clips");
        first.Current.Output.ScreenshotsFolder = Path.Combine(_dir, "shots");
        first.Current.Output.BufferFolder = Path.Combine(_dir, "buffer");
        var c = first.Current.AutoClicker.Clicker;
        c.Armed = true;
        c.ClickSpeed = 120.5;
        c.ClickUnit = RateUnit.Minute;
        c.InputType = ClickerInput.Keyboard;
        c.KeyboardVk = 0x41;
        c.KeyCase = KeyCase.Upper;
        c.Hotkey = new ClickerHotkey { Modifiers = ClickerModifiers.LCtrl | ClickerModifiers.Shift, Keys = { 0x46, ClickerVk.XButton1 } };
        c.MasterHotkey = new ClickerHotkey { Modifiers = ClickerModifiers.None, Keys = { 0x7B } };
        c.MasterMode = ActivationMode.Hold;
        c.Zones.Add(new ClickerZone { Id = "z1", Name = "Menu", X = -1920, Y = 10, W = 300, H = 200, Action = ZoneAction.Pause });
        c.ClickPoints.Add(new ClickPoint { X = 100, Y = -50, Clicks = 3, Radius = 12 });
        c.Whitelist.Add(new BlacklistEntry { ExeName = "notepad.exe", DisplayName = "Notepad" });
        c.Presets.Add(new ClickerPreset { Name = "mine", ClickSpeed = 7 });
        c.Stats.AddRun(new RunRecord { StartUtc = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc), Seconds = 5.5, Clicks = 100, StopReason = "Hotkey" });
        first.Save();

        var loaded = new SettingsService(_dir).Load().AutoClicker.Clicker;

        Assert.True(loaded.Armed);
        Assert.Equal(120.5, loaded.ClickSpeed);
        Assert.Equal(RateUnit.Minute, loaded.ClickUnit);
        Assert.Equal(ClickerInput.Keyboard, loaded.InputType);
        Assert.Equal(0x41, loaded.KeyboardVk);
        Assert.Equal(KeyCase.Upper, loaded.KeyCase);
        Assert.Equal(ClickerModifiers.LCtrl | ClickerModifiers.Shift, loaded.Hotkey.Modifiers);
        Assert.Equal(new[] { 0x46, ClickerVk.XButton1 }, loaded.Hotkey.Keys);
        Assert.Equal(new[] { 0x7B }, loaded.MasterHotkey!.Keys);
        Assert.Equal(ActivationMode.Hold, loaded.MasterMode);
        var z = Assert.Single(loaded.Zones);
        Assert.Equal((-1920, 10, 300, 200, ZoneAction.Pause), (z.X, z.Y, z.W, z.H, z.Action));
        var p = Assert.Single(loaded.ClickPoints);
        Assert.Equal((100, -50, 3, 12), (p.X, p.Y, p.Clicks, p.Radius));
        Assert.Equal("notepad.exe", Assert.Single(loaded.Whitelist).ExeName);
        Assert.Equal("mine", Assert.Single(loaded.Presets).Name);
        Assert.Equal(1, loaded.Stats.Sessions);
        Assert.Equal(100, loaded.Stats.TotalClicks);
        Assert.Equal("Hotkey", Assert.Single(loaded.Stats.LastRuns).StopReason);
    }

    [Fact]
    public void Enums_and_the_hotkey_are_written_as_text()
    {
        var json = JsonSerializer.Serialize(new ClickerSettings());
        Assert.Contains("\"Second\"", json);
        Assert.Contains("\"Ctrl, Alt\"", json);
    }

    [Fact]
    public void An_existing_v12_hotkey_section_loads_and_saves_unchanged()
    {
        const string hotkey = "\"Hotkey\":{\"Modifiers\":\"LCtrl, Shift\",\"Keys\":[70,5]}";
        const string master = "\"MasterHotkey\":{\"Modifiers\":\"None\",\"Keys\":[123]}";
        var s = JsonSerializer.Deserialize<AppSettings>("{\"SchemaVersion\":12,\"AutoClicker\":{\"Clicker\":{" + hotkey + "," + master + "}}}")!;
        var c = ClickerSettingsRules.Normalize(s.AutoClicker.Clicker);

        Assert.Equal(ClickerModifiers.LCtrl | ClickerModifiers.Shift, c.Hotkey.Modifiers);
        Assert.Equal(new[] { 0x46, ClickerVk.XButton1 }, c.Hotkey.Keys);
        Assert.Equal(new[] { 0x7B }, c.MasterHotkey!.Keys);
        var json = JsonSerializer.Serialize(c);
        Assert.Contains(hotkey, json);   // same shape: { Modifiers, Keys } and nothing else
        Assert.Contains(master, json);
    }

    [Fact]
    public void Modifier_only_and_wheel_hotkeys_round_trip_in_the_v12_shape()
    {
        var c = new ClickerSettings
        {
            Hotkey = new ClickerHotkey { Modifiers = ClickerModifiers.LCtrl | ClickerModifiers.RAlt },
            MasterHotkey = new ClickerHotkey { Keys = { ClickerVk.WheelDown } }
        };
        var json = JsonSerializer.Serialize(ClickerSettingsRules.Normalize(c));
        Assert.Contains("\"Hotkey\":{\"Modifiers\":\"LCtrl, RAlt\",\"Keys\":[]}", json);
        Assert.Contains("\"MasterHotkey\":{\"Modifiers\":\"None\",\"Keys\":[258]}", json);

        var loaded = ClickerSettingsRules.Normalize(JsonSerializer.Deserialize<ClickerSettings>(json)!);
        Assert.True(ClickerHotkey.SameCombo(c.Hotkey, loaded.Hotkey));
        Assert.True(ClickerHotkey.SameCombo(c.MasterHotkey, loaded.MasterHotkey));
    }

    // ---- defaults of design section 3 ----

    [Fact]
    public void The_defaults_match_the_design()
    {
        var c = new ClickerSettings();
        Assert.False(c.Armed);
        Assert.Equal(RateInputMode.Rate, c.RateInputMode);
        Assert.Equal(25, c.ClickSpeed);
        Assert.Equal(RateUnit.Second, c.ClickUnit);
        Assert.Equal(40, c.DurationMs);
        Assert.False(c.ExtendedSpeedLimit);
        Assert.True(c.SpeedRandomizationEnabled);
        Assert.Equal(35, c.SpeedRandomizationPercent);
        Assert.True(c.DutyCycleEnabled);
        Assert.Equal(45, c.DutyCyclePercent);
        Assert.Equal(DutyMode.Click, c.DutyMode);
        Assert.Equal(ClickerInput.Mouse, c.InputType);
        Assert.Equal(ClickerMouseButton.Left, c.MouseButton);
        Assert.Equal(0, c.KeyboardVk);
        Assert.Equal(KeyCase.Lower, c.KeyCase);
        Assert.False(c.DoubleClick);
        Assert.Equal(ClickerModifiers.Ctrl | ClickerModifiers.Alt, c.Hotkey.Modifiers);
        Assert.Equal(new[] { 0x59 }, c.Hotkey.Keys);
        Assert.Equal(ActivationMode.Toggle, c.ActivationMode);
        Assert.False(c.StrictModifiers);
        Assert.Null(c.MasterHotkey);
        Assert.Equal(ActivationMode.Toggle, c.MasterMode);
        Assert.False(c.ClickLimitEnabled);
        Assert.Equal(1000, c.ClickLimit);
        Assert.False(c.TimeLimitEnabled);
        Assert.Equal(60, c.TimeLimit);
        Assert.Equal(TimeLimitUnit.Seconds, c.TimeLimitUnit);
        Assert.True(c.CornerStopEnabled);
        Assert.Equal(50, c.CornerSizePx);
        Assert.True(c.EdgeStopEnabled);
        Assert.Equal(40, c.EdgeSizePx);
        Assert.True(c.TaskSwitcherStopEnabled);
        Assert.False(c.ZonesEnabled);
        Assert.Empty(c.Zones);
        Assert.False(c.ClickPointsEnabled);
        Assert.Empty(c.ClickPoints);
        Assert.False(c.StopWhenComplete);
        Assert.False(c.WhitelistEnabled);
        Assert.Empty(c.Whitelist);
        Assert.Empty(c.Presets);
        Assert.Equal(0, c.Stats.Sessions);
        Assert.True(c.ShowActiveIndicator);
        Assert.Equal("simple", c.SubTab);
    }

    [Fact]
    public void Defaults_are_already_normal()
    {
        var c = new ClickerSettings();
        var before = JsonSerializer.Serialize(c);
        ClickerSettingsRules.Normalize(c);
        Assert.Equal(before, JsonSerializer.Serialize(c));
    }

    // ---- speed ----

    [Theory]
    [InlineData(0.0, 1)]
    [InlineData(0.5, 1)]
    [InlineData(1.0, 1)]
    [InlineData(1.05, 1.05)]
    [InlineData(500.0, 500)]
    [InlineData(501.0, 500)]
    [InlineData(100000.0, 500)]
    public void Speed_is_clamped_to_1_to_500(double input, double expected)
        => Assert.Equal(expected, Norm(c => c.ClickSpeed = input).ClickSpeed);

    [Theory]
    [InlineData(750.0, 750)]
    [InlineData(1000.0, 1000)]
    [InlineData(1001.0, 1000)]
    public void The_extended_limit_raises_the_cap_to_1000(double input, double expected)
        => Assert.Equal(expected, Norm(c => { c.ExtendedSpeedLimit = true; c.ClickSpeed = input; }).ClickSpeed);

    [Fact]
    public void Switching_the_extended_limit_off_pulls_a_fast_speed_back()
        => Assert.Equal(500, Norm(c => { c.ExtendedSpeedLimit = false; c.ClickSpeed = 900; }).ClickSpeed);

    [Fact]
    public void Double_click_caps_the_rate_at_10_per_second()
    {
        Assert.Equal(10, Norm(c => { c.DoubleClick = true; c.ClickSpeed = 25; }).ClickSpeed);
        Assert.Equal(10, Norm(c => { c.DoubleClick = true; c.ExtendedSpeedLimit = true; c.ClickSpeed = 800; }).ClickSpeed);
        Assert.Equal(7, Norm(c => { c.DoubleClick = true; c.ClickSpeed = 7; }).ClickSpeed);
    }

    [Fact]
    public void The_double_click_cap_follows_the_unit()
    {
        // 10 per second = 600 per minute: the plain 500 cap is the tighter one there.
        Assert.Equal(500, Norm(c => { c.DoubleClick = true; c.ClickUnit = RateUnit.Minute; c.ClickSpeed = 5000; }).ClickSpeed);
        Assert.Equal(500, Norm(c => { c.DoubleClick = true; c.ClickUnit = RateUnit.Hour; c.ClickSpeed = 5000; }).ClickSpeed);
        Assert.Equal(600, Norm(c => { c.DoubleClick = true; c.ExtendedSpeedLimit = true; c.ClickUnit = RateUnit.Minute; c.ClickSpeed = 5000; }).ClickSpeed);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-3.0)]
    public void A_broken_speed_falls_back_to_the_default(double input)
        => Assert.Equal(25, Norm(c => c.ClickSpeed = input).ClickSpeed);

    [Fact]
    public void A_broken_speed_under_double_click_falls_back_to_the_capped_default()
        => Assert.Equal(10, Norm(c => { c.DoubleClick = true; c.ClickSpeed = double.NaN; }).ClickSpeed);

    [Fact]
    public void An_unknown_unit_or_mode_falls_back()
    {
        var c = Norm(x => { x.ClickUnit = (RateUnit)99; x.RateInputMode = (RateInputMode)7; x.DutyMode = (DutyMode)9; x.InputType = (ClickerInput)5; });
        Assert.Equal(RateUnit.Second, c.ClickUnit);
        Assert.Equal(RateInputMode.Rate, c.RateInputMode);
        Assert.Equal(DutyMode.Click, c.DutyMode);
        Assert.Equal(ClickerInput.Mouse, c.InputType);
    }

    // ---- duration ----

    [Theory]
    [InlineData(0L, 1L)]
    [InlineData(1L, 1L)]
    [InlineData(40L, 40L)]
    [InlineData(3_599_999_999L, 3_599_999_999L)]
    [InlineData(3_600_000_000L, 3_599_999_999L)]
    [InlineData(long.MaxValue, 3_599_999_999L)]
    [InlineData(-5L, 40L)]
    public void Duration_is_clamped_to_1_ms_to_999h_59m_59s_999ms(long input, long expected)
        => Assert.Equal(expected, Norm(c => c.DurationMs = input).DurationMs);

    [Fact]
    public void Double_click_needs_a_cycle_of_at_least_100_ms()
    {
        Assert.Equal(100, Norm(c => { c.DoubleClick = true; c.DurationMs = 40; }).DurationMs);
        Assert.Equal(250, Norm(c => { c.DoubleClick = true; c.DurationMs = 250; }).DurationMs);
    }

    // ---- percentages ----

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(200.0, 200)]
    [InlineData(201.0, 200)]
    [InlineData(-1.0, 35)]
    [InlineData(double.NaN, 35)]
    public void Randomization_is_0_to_200(double input, double expected)
        => Assert.Equal(expected, Norm(c => c.SpeedRandomizationPercent = input).SpeedRandomizationPercent);

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(100.0, 100)]
    [InlineData(150.0, 100)]
    [InlineData(-10.0, 45)]
    [InlineData(double.NaN, 45)]
    public void Duty_cycle_is_0_to_100(double input, double expected)
        => Assert.Equal(expected, Norm(c => c.DutyCyclePercent = input).DutyCyclePercent);

    // ---- keyboard key ----

    [Theory]
    [InlineData(0x41, 0x41)]
    [InlineData(255, 255)]
    [InlineData(256, 0)]
    [InlineData(-4, 0)]
    [InlineData(ClickerVk.LButton, 0)]
    [InlineData(ClickerVk.XButton2, 0)]
    public void The_output_key_is_a_keyboard_vk_or_none(int input, int expected)
        => Assert.Equal(expected, Norm(c => c.KeyboardVk = input).KeyboardVk);

    // ---- limits ----

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(100_000_000, 100_000_000)]
    [InlineData(100_000_001, 100_000_000)]
    [InlineData(int.MaxValue, 100_000_000)]
    [InlineData(-1, 1000)]
    public void The_click_limit_is_1_to_100_million(int input, int expected)
        => Assert.Equal(expected, Norm(c => c.ClickLimit = input).ClickLimit);

    [Theory]
    [InlineData(0.0, 0.1)]
    [InlineData(1.5, 1.5)]
    [InlineData(1e9, 100_000.0)]
    [InlineData(-2.0, 60.0)]
    [InlineData(double.NaN, 60.0)]
    public void The_time_limit_is_clamped_per_unit_value(double input, double expected)
        => Assert.Equal(expected, Norm(c => c.TimeLimit = input).TimeLimit);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(500, 500)]
    [InlineData(501, 500)]
    [InlineData(-1, 50)]
    public void The_corner_size_is_1_to_500(int input, int expected)
        => Assert.Equal(expected, Norm(c => c.CornerSizePx = input).CornerSizePx);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(500, 500)]
    [InlineData(9999, 500)]
    [InlineData(-1, 40)]
    public void The_edge_size_is_1_to_500(int input, int expected)
        => Assert.Equal(expected, Norm(c => c.EdgeSizePx = input).EdgeSizePx);

    // ---- zones ----

    [Fact]
    public void Zones_are_capped_at_64_and_invalid_ones_dropped()
    {
        var c = Norm(x =>
        {
            for (int i = 0; i < 80; i++) x.Zones.Add(new ClickerZone { Id = "z" + i, X = i, Y = 0, W = 10, H = 10 });
        });
        Assert.Equal(64, c.Zones.Count);
        Assert.Equal("z0", c.Zones[0].Id);

        var d = Norm(x =>
        {
            x.Zones.Add(new ClickerZone { Id = "a", W = 0, H = 10 });
            x.Zones.Add(new ClickerZone { Id = "b", W = 10, H = -3 });
            x.Zones.Add(null!);
            x.Zones.Add(new ClickerZone { Id = "c", W = 10, H = 10 });
        });
        Assert.Equal("c", Assert.Single(d.Zones).Id);
    }

    [Fact]
    public void A_dropped_zone_does_not_use_up_a_slot()
    {
        var c = Norm(x =>
        {
            for (int i = 0; i < 10; i++) x.Zones.Add(new ClickerZone { W = 0, H = 0 });
            for (int i = 0; i < 70; i++) x.Zones.Add(new ClickerZone { W = 5, H = 5 });
        });
        Assert.Equal(64, c.Zones.Count);
    }

    [Fact]
    public void Zone_ids_are_filled_in_and_unique_and_the_rest_is_clamped()
    {
        var c = Norm(x =>
        {
            x.Zones.Add(new ClickerZone { Id = "", X = -1920, Y = -1080, W = 100, H = 100, Name = "  " + new string('n', 80) + "  " });
            x.Zones.Add(new ClickerZone { Id = "dup", W = 5, H = 5 });
            x.Zones.Add(new ClickerZone { Id = "dup", X = int.MaxValue, Y = int.MinValue, W = int.MaxValue, H = 5, Action = (ZoneAction)42 });
        });

        Assert.Equal(3, c.Zones.Count);
        Assert.All(c.Zones, z => Assert.False(string.IsNullOrWhiteSpace(z.Id)));
        Assert.Equal(3, c.Zones.Select(z => z.Id).Distinct().Count());
        Assert.Equal(-1920, c.Zones[0].X);                        // negative monitor coordinates are legal
        Assert.Equal(40, c.Zones[0].Name.Length);
        Assert.Equal(ClickerSettingsRules.CoordinateLimit, c.Zones[2].X);
        Assert.Equal(-ClickerSettingsRules.CoordinateLimit, c.Zones[2].Y);
        Assert.Equal(2 * ClickerSettingsRules.CoordinateLimit, c.Zones[2].W);
        Assert.Equal(ZoneAction.Stop, c.Zones[2].Action);
    }

    // ---- click points ----

    [Fact]
    public void Click_points_are_capped_at_500_and_clamped()
    {
        var c = Norm(x =>
        {
            for (int i = 0; i < 600; i++) x.ClickPoints.Add(new ClickPoint { X = i, Y = -i, Clicks = 1 });
            x.ClickPoints.Add(null!);
        });
        Assert.Equal(500, c.ClickPoints.Count);
        Assert.Equal(-499, c.ClickPoints[499].Y);

        var d = Norm(x =>
        {
            x.ClickPoints.Add(new ClickPoint { Clicks = 0, Radius = 20000 });
            x.ClickPoints.Add(new ClickPoint { Clicks = 5_000_000, Radius = -4 });
            x.ClickPoints.Add(new ClickPoint { Clicks = -1, Radius = 9999, X = 5_000_000 });
        });
        Assert.Equal((1, 9999), (d.ClickPoints[0].Clicks, d.ClickPoints[0].Radius));
        Assert.Equal((999_999, 0), (d.ClickPoints[1].Clicks, d.ClickPoints[1].Radius));
        Assert.Equal((1, 9999, ClickerSettingsRules.CoordinateLimit), (d.ClickPoints[2].Clicks, d.ClickPoints[2].Radius, d.ClickPoints[2].X));
    }

    // ---- whitelist ----

    [Fact]
    public void The_whitelist_is_cleaned_like_the_blacklist()
    {
        var c = Norm(x =>
        {
            x.Whitelist.Add(new BlacklistEntry { ExeName = @"C:\Apps\Tool.exe", DisplayName = "" });
            x.Whitelist.Add(new BlacklistEntry { ExeName = "tool", DisplayName = "dup" });
            x.Whitelist.Add(new BlacklistEntry { ExeName = "", DisplayName = "none" });
            x.Whitelist.Add(null!);
        });
        var e = Assert.Single(c.Whitelist);
        Assert.Equal("Tool.exe", e.ExeName);
        Assert.Equal("Tool", e.DisplayName);
    }

    // ---- presets list ----

    [Fact]
    public void Presets_are_capped_at_100_and_names_trimmed_and_unique()
    {
        var c = Norm(x =>
        {
            for (int i = 0; i < 120; i++) x.Presets.Add(new ClickerPreset { Name = "p" + i });
        });
        Assert.Equal(100, c.Presets.Count);

        var d = Norm(x =>
        {
            x.Presets.Add(new ClickerPreset { Name = "  Fast  " });
            x.Presets.Add(new ClickerPreset { Name = "FAST" });
            x.Presets.Add(new ClickerPreset { Name = "fast" });
            x.Presets.Add(new ClickerPreset { Name = new string('x', 60) });
            x.Presets.Add(new ClickerPreset { Name = "   " });
            x.Presets.Add(null!);
        });
        Assert.Equal(new[] { "Fast", "FAST (2)", "fast (3)", new string('x', 40), "Preset" }, d.Presets.Select(p => p.Name).ToArray());
        Assert.Equal(d.Presets.Count, d.Presets.Select(p => p.Name.ToLowerInvariant()).Distinct().Count());
    }

    [Fact]
    public void A_name_made_unique_stays_within_40_characters()
    {
        var c = Norm(x =>
        {
            x.Presets.Add(new ClickerPreset { Name = new string('y', 40) });
            x.Presets.Add(new ClickerPreset { Name = new string('y', 40) });
        });
        Assert.All(c.Presets, p => Assert.True(p.Name.Length <= 40));
        Assert.Equal(new string('y', 36) + " (2)", c.Presets[1].Name);
    }

    [Fact]
    public void Preset_contents_are_clamped_too()
    {
        var c = Norm(x => x.Presets.Add(new ClickerPreset
        {
            Name = "wild", ClickSpeed = 99999, DutyCyclePercent = 500, ClickLimit = 0, CornerSizePx = 9999,
            Zones = { new ClickerZone { W = 0, H = 0 } },
            ClickPoints = { new ClickPoint { Clicks = 0 } }
        }));
        var p = Assert.Single(c.Presets);
        Assert.Equal(500, p.ClickSpeed);
        Assert.Equal(100, p.DutyCyclePercent);
        Assert.Equal(1, p.ClickLimit);
        Assert.Equal(500, p.CornerSizePx);
        Assert.Empty(p.Zones);
        Assert.Equal(1, Assert.Single(p.ClickPoints).Clicks);
    }

    // ---- stats ----

    [Fact]
    public void Stats_are_repaired_and_the_run_list_is_capped_at_100()
    {
        var c = Norm(x =>
        {
            x.Stats.TotalClicks = -5;
            x.Stats.TotalSeconds = double.NaN;
            x.Stats.Sessions = -1;
            for (int i = 0; i < 130; i++) x.Stats.LastRuns.Add(new RunRecord { Clicks = i, Seconds = -1, StopReason = new string('r', 500) });
            x.Stats.LastRuns.Add(null!);
        });
        Assert.Equal((0L, 0.0, 0L), (c.Stats.TotalClicks, c.Stats.TotalSeconds, c.Stats.Sessions));
        Assert.Equal(100, c.Stats.LastRuns.Count);
        Assert.All(c.Stats.LastRuns, r => Assert.Equal(0, r.Seconds));
        Assert.Equal(200, c.Stats.LastRuns[0].StopReason.Length);
    }

    [Fact]
    public void A_null_stats_object_and_null_lists_are_replaced()
    {
        var c = new ClickerSettings { Stats = null!, Zones = null!, ClickPoints = null!, Whitelist = null!, Presets = null! };
        ClickerSettingsRules.Normalize(c);
        Assert.NotNull(c.Stats);
        Assert.Empty(c.Stats.LastRuns);
        Assert.Empty(c.Zones);
        Assert.Empty(c.ClickPoints);
        Assert.Empty(c.Whitelist);
        Assert.Empty(c.Presets);
    }

    [Fact]
    public void AddRun_counts_the_run_and_keeps_the_newest_100_first()
    {
        var stats = new ClickerStats();
        for (int i = 1; i <= 105; i++)
            stats.AddRun(new RunRecord { Clicks = i, Seconds = 2, StopReason = "r" + i });

        Assert.Equal(105, stats.Sessions);
        Assert.Equal(105 * 106 / 2, stats.TotalClicks);
        Assert.Equal(210, stats.TotalSeconds);
        Assert.Equal(100, stats.LastRuns.Count);
        Assert.Equal("r105", stats.LastRuns[0].StopReason);
        Assert.Equal("r6", stats.LastRuns[99].StopReason);
    }

    // ---- sub-tab, enums ----

    [Theory]
    [InlineData("advanced", "advanced")]
    [InlineData("  ZONES ", "zones")]
    [InlineData("presets", "presets")]
    [InlineData("legacy", "simple")]        // a sub-tab that no longer exists
    [InlineData("LEGACY", "simple")]
    [InlineData("", "simple")]
    [InlineData(null, "simple")]
    [InlineData("crosshair", "simple")]
    public void The_sub_tab_must_be_a_known_key(string? input, string expected)
        => Assert.Equal(expected, Norm(c => c.SubTab = input!).SubTab);

    [Fact]
    public void Normalize_returns_the_same_instance()
    {
        var c = new ClickerSettings();
        Assert.Same(c, ClickerSettingsRules.Normalize(c));
    }

    [Fact]
    public void Normalize_is_idempotent()
    {
        var c = Norm(x =>
        {
            x.ClickSpeed = 9999; x.Zones.Add(new ClickerZone { W = 4, H = 4 }); x.Presets.Add(new ClickerPreset { Name = "a" });
            x.Presets.Add(new ClickerPreset { Name = "A" });
            x.Hotkey = new ClickerHotkey { Modifiers = ClickerModifiers.Ctrl | ClickerModifiers.LCtrl, Keys = { 0x41, 0x41, 0xA2 } };
        });
        var once = JsonSerializer.Serialize(c);
        ClickerSettingsRules.Normalize(c);
        Assert.Equal(once, JsonSerializer.Serialize(c));
    }

    // ---- helpers the UI uses ----

    [Fact]
    public void MaxSpeed_and_MinDurationMs_describe_the_caps()
    {
        Assert.Equal(500, ClickerSettingsRules.MaxSpeed(false, false, RateUnit.Second));
        Assert.Equal(1000, ClickerSettingsRules.MaxSpeed(true, false, RateUnit.Hour));
        Assert.Equal(10, ClickerSettingsRules.MaxSpeed(true, true, RateUnit.Second));
        Assert.Equal(1, ClickerSettingsRules.MinDurationMs(false));
        Assert.Equal(100, ClickerSettingsRules.MinDurationMs(true));
    }
}
