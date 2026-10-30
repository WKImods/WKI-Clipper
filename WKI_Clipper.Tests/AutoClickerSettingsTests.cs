using System;
using System.IO;
using System.Text.Json;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// The Gaming Utilities / AutoClicker section: schema v11 adds it (v12 adds the native clicker, see ClickerSettingsTests). Every field has an initializer, so older files load the
/// defaults; EnsureSections guards against explicit nulls and cleans a hand-edited blacklist.
/// </summary>
public sealed class AutoClickerSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_autoclicker_" + Guid.NewGuid().ToString("N"));

    public AutoClickerSettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Schema_version_is_12()
        => Assert.Equal(12, SettingsService.CurrentSchemaVersion);

    [Fact]
    public void V10_settings_migrate_with_the_default_autoclicker_section()
    {
        // A real v10 file has no "AutoClicker" property at all.
        var s = JsonSerializer.Deserialize<AppSettings>("{\"SchemaVersion\":10}")!;
        Assert.Equal(10, s.SchemaVersion);

        bool changed = SettingsService.MigrateIfNeeded(s);

        Assert.True(changed);
        Assert.Equal(SettingsService.CurrentSchemaVersion, s.SchemaVersion);
        Assert.NotNull(s.AutoClicker);
        Assert.Empty(s.AutoClicker.Blacklist);
        Assert.False(s.AutoClicker.WarningAcknowledged);
        Assert.Equal("Crosshair", s.AutoClicker.GamingUtilitiesTab);
    }

    [Fact]
    public void V10_migration_keeps_everything_else()
    {
        var s = JsonSerializer.Deserialize<AppSettings>("{\"SchemaVersion\":10,\"Shortcuts\":{\"Expanded\":false}}")!;
        s.Crosshair.Enabled = true;

        Assert.True(SettingsService.MigrateIfNeeded(s));

        Assert.False(s.Shortcuts.Expanded);
        Assert.True(s.Crosshair.Enabled);
    }

    [Fact]
    public void The_current_version_is_a_noop()
    {
        var s = new AppSettings { SchemaVersion = SettingsService.CurrentSchemaVersion };
        Assert.False(SettingsService.MigrateIfNeeded(s));
    }

    [Fact]
    public void A_fresh_install_starts_with_an_empty_blacklist_and_the_crosshair_tab()
    {
        var s = new AppSettings();
        Assert.Empty(s.AutoClicker.Blacklist);
        Assert.False(s.AutoClicker.WarningAcknowledged);
        Assert.Equal("Crosshair", s.AutoClicker.GamingUtilitiesTab);
    }

    [Fact]
    public void Null_section_list_and_tab_are_guarded()
    {
        var noSection = JsonSerializer.Deserialize<AppSettings>("{\"SchemaVersion\":11,\"AutoClicker\":null}")!;
        Assert.Null(noSection.AutoClicker);
        SettingsService.EnsureSections(noSection);
        Assert.NotNull(noSection.AutoClicker);
        Assert.Empty(noSection.AutoClicker.Blacklist);

        var nullList = JsonSerializer.Deserialize<AppSettings>(
            "{\"SchemaVersion\":11,\"AutoClicker\":{\"Blacklist\":null,\"GamingUtilitiesTab\":\"\",\"WarningAcknowledged\":true}}")!;
        Assert.Null(nullList.AutoClicker.Blacklist);
        SettingsService.EnsureSections(nullList);
        Assert.NotNull(nullList.AutoClicker.Blacklist);
        Assert.Equal("Crosshair", nullList.AutoClicker.GamingUtilitiesTab);
        Assert.True(nullList.AutoClicker.WarningAcknowledged);   // untouched
    }

    [Fact]
    public void A_hand_edited_blacklist_is_cleaned_on_load()
    {
        var s = JsonSerializer.Deserialize<AppSettings>(
            "{\"SchemaVersion\":11,\"AutoClicker\":{\"Blacklist\":[" +
            "{\"ExeName\":\"C:\\\\Games\\\\ArmaReforgerSteam.exe\",\"DisplayName\":\"Arma Reforger\"}," +
            "{\"ExeName\":\"armareforgersteam\",\"DisplayName\":\"duplicate\"}," +
            "{\"ExeName\":\"\",\"DisplayName\":\"nothing\"}," +
            "null," +
            "{\"ExeName\":\"cs2\",\"DisplayName\":\"\"}]}}")!;

        SettingsService.EnsureSections(s);

        Assert.Equal(2, s.AutoClicker.Blacklist.Count);
        Assert.Equal("ArmaReforgerSteam.exe", s.AutoClicker.Blacklist[0].ExeName);
        Assert.Equal("Arma Reforger", s.AutoClicker.Blacklist[0].DisplayName);
        Assert.Equal("cs2.exe", s.AutoClicker.Blacklist[1].ExeName);
        Assert.Equal("cs2", s.AutoClicker.Blacklist[1].DisplayName);
    }

    [Fact]
    public void Autoclicker_section_round_trips()
    {
        var first = new SettingsService(_dir);
        first.Current.SchemaVersion = SettingsService.CurrentSchemaVersion;
        // Output folders point into the temp dir so Load()'s EnsureOutputDirs never touches the real Videos folder.
        first.Current.Output.ClipsFolder = Path.Combine(_dir, "clips");
        first.Current.Output.ScreenshotsFolder = Path.Combine(_dir, "shots");
        first.Current.Output.BufferFolder = Path.Combine(_dir, "buffer");
        first.Current.AutoClicker.WarningAcknowledged = true;
        first.Current.AutoClicker.GamingUtilitiesTab = "AutoClicker";
        first.Current.AutoClicker.Blacklist.Add(new BlacklistEntry { ExeName = "ArmaReforgerSteam.exe", DisplayName = "Arma Reforger" });
        first.Save();

        var loaded = new SettingsService(_dir).Load();

        Assert.True(loaded.AutoClicker.WarningAcknowledged);
        Assert.Equal("AutoClicker", loaded.AutoClicker.GamingUtilitiesTab);
        var entry = Assert.Single(loaded.AutoClicker.Blacklist);
        Assert.Equal("ArmaReforgerSteam.exe", entry.ExeName);
        Assert.Equal("Arma Reforger", entry.DisplayName);
    }

    [Fact]
    public void The_ignore_flag_is_not_part_of_the_settings()
    {
        // "Blacklist ignorieren" is session only: nothing in the saved section may carry it.
        var json = JsonSerializer.Serialize(new AutoClickerSettings());
        Assert.DoesNotContain("Ignore", json, StringComparison.OrdinalIgnoreCase);
    }
}
