using System;
using System.IO;
using System.Text.Json;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Save() used to be File.WriteAllText on settings.json itself, once per slider move. A write
/// interrupted halfway (crash, kill, power) left a truncated file, Load() threw it away as
/// corrupt and every setting went back to default. Now: write a temp file, swap it in
/// atomically, keep the previous version as settings.json.bak and fall back to that.
/// </summary>
public sealed class SettingsPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_settings_" + Guid.NewGuid().ToString("N"));

    public SettingsPersistenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Main => Path.Combine(_dir, "settings.json");
    private string Bak => Path.Combine(_dir, "settings.json.bak");
    private string Tmp => Path.Combine(_dir, "settings.json.tmp");

    // Output folders point into the temp dir so Load()'s EnsureOutputDirs never touches the
    // real Videos folder; the current schema version keeps Load() from re-saving a migration.
    private SettingsService NewService(int bufferSeconds = 60)
    {
        var s = new SettingsService(_dir);
        s.Current.SchemaVersion = SettingsService.CurrentSchemaVersion;
        s.Current.Output.ClipsFolder = Path.Combine(_dir, "clips");
        s.Current.Output.ScreenshotsFolder = Path.Combine(_dir, "shots");
        s.Current.Output.BufferFolder = Path.Combine(_dir, "buffer");
        s.Current.ReplayBuffer.DurationSeconds = bufferSeconds;
        return s;
    }

    private static int ReadBufferSeconds(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("ReplayBuffer").GetProperty("DurationSeconds").GetInt32();
    }

    // ---- Save ----

    [Fact]
    public void The_settings_path_lives_in_the_directory_it_was_given()
    {
        var s = new SettingsService(_dir);
        Assert.Equal(_dir, s.AppDataDir);
        Assert.Equal(Main, s.SettingsFilePath);
    }

    [Fact]
    public void First_save_creates_the_file_and_leaves_no_temp_or_backup()
    {
        var s = NewService(111);
        s.Save();

        Assert.Equal(111, ReadBufferSeconds(Main));
        Assert.False(File.Exists(Tmp));
        Assert.False(File.Exists(Bak));
    }

    [Fact]
    public void Second_save_keeps_the_previous_version_as_bak()
    {
        var s = NewService(111);
        s.Save();
        s.Current.ReplayBuffer.DurationSeconds = 222;
        s.Save();

        Assert.Equal(222, ReadBufferSeconds(Main));
        Assert.Equal(111, ReadBufferSeconds(Bak));
        Assert.False(File.Exists(Tmp));
    }

    [Fact]
    public void Save_raises_SettingsChanged_even_though_it_swaps_files()
    {
        var s = NewService();
        int raised = 0;
        s.SettingsChanged += (_, _) => raised++;
        s.Save();
        s.Save();
        Assert.Equal(2, raised);
    }

    [Fact]
    public void A_leftover_temp_file_from_an_interrupted_save_does_not_block_the_next_save()
    {
        var s = NewService(111);
        s.Save();
        File.WriteAllText(Tmp, "{ \"Replay");            // killed mid-write on a previous run

        s.Current.ReplayBuffer.DurationSeconds = 333;
        s.Save();

        Assert.Equal(333, ReadBufferSeconds(Main));
        Assert.False(File.Exists(Tmp));
    }

    // ---- Load ----

    [Fact]
    public void Load_ignores_a_half_written_temp_file_next_to_a_good_settings_file()
    {
        NewService(111).Save();
        File.WriteAllText(Tmp, "{ \"Replay");

        var loaded = new SettingsService(_dir).Load();

        Assert.Equal(111, loaded.ReplayBuffer.DurationSeconds);
    }

    [Fact]
    public void Load_prefers_the_main_file_over_the_backup()
    {
        var s = NewService(111);
        s.Save();
        s.Current.ReplayBuffer.DurationSeconds = 222;
        s.Save();

        Assert.Equal(222, new SettingsService(_dir).Load().ReplayBuffer.DurationSeconds);
    }

    [Fact]
    public void Truncated_settings_file_is_restored_from_the_backup()
    {
        var s = NewService(111);
        s.Save();
        s.Current.ReplayBuffer.DurationSeconds = 222;
        s.Save();
        var full = File.ReadAllText(Main);
        File.WriteAllText(Main, full[..(full.Length / 2)]);     // the interrupted write

        var loaded = new SettingsService(_dir).Load();

        Assert.Equal(111, loaded.ReplayBuffer.DurationSeconds);          // previous version, not defaults (60)
        Assert.Equal(111, ReadBufferSeconds(Main));                      // settings.json is whole again
        Assert.Equal(111, ReadBufferSeconds(Bak));                       // and the good backup survived
        Assert.Single(Directory.GetFiles(_dir, "settings.json.broken-*"));
    }

    [Fact]
    public void Empty_settings_file_is_restored_from_the_backup()
    {
        var s = NewService(111);
        s.Save();
        s.Current.ReplayBuffer.DurationSeconds = 222;
        s.Save();
        File.WriteAllBytes(Main, Array.Empty<byte>());           // zero-length: what a power cut leaves behind

        Assert.Equal(111, new SettingsService(_dir).Load().ReplayBuffer.DurationSeconds);
    }

    [Fact]
    public void Missing_settings_file_is_restored_from_the_backup()
    {
        var s = NewService(111);
        s.Save();
        s.Current.ReplayBuffer.DurationSeconds = 222;
        s.Save();
        File.Delete(Main);

        var loaded = new SettingsService(_dir).Load();

        Assert.Equal(111, loaded.ReplayBuffer.DurationSeconds);
        Assert.Equal(111, ReadBufferSeconds(Main));
    }

    [Fact]
    public void With_no_usable_file_at_all_load_falls_back_to_defaults_and_keeps_the_broken_copy()
    {
        // The defaults path creates the default output folders, same as every fresh install.
        File.WriteAllText(Main, "not json at all");
        File.WriteAllText(Bak, "{ also broken");

        var loaded = new SettingsService(_dir).Load();

        Assert.Equal(SettingsService.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(new ReplayBufferSettings().DurationSeconds, loaded.ReplayBuffer.DurationSeconds);
        Assert.Single(Directory.GetFiles(_dir, "settings.json.broken-*"));
        Assert.True(File.Exists(Main));
        using var doc = JsonDocument.Parse(File.ReadAllText(Main));   // a valid file was written again
    }

    // ---- reset ----

    [Fact]
    public void Deleting_the_settings_removes_the_backup_too_or_it_would_come_back()
    {
        var s = NewService(111);
        s.Save();
        s.Save();
        File.WriteAllText(Tmp, "x");
        Assert.True(File.Exists(Bak));

        s.DeleteSettingsFiles();

        Assert.False(File.Exists(Main));
        Assert.False(File.Exists(Bak));
        Assert.False(File.Exists(Tmp));
    }
}
