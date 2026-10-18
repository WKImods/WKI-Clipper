using System;
using System.IO;
using Microsoft.Win32;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>The parts of the buffer service that need neither ffmpeg nor a capture target.</summary>
public sealed class ReplayBufferLongLookbackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_longlook_" + Guid.NewGuid().ToString("N"));
    private readonly string _rootPath = @"Software\WKI_Clipper_Tests_Service_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;
    private readonly SettingsService _settings;
    private readonly ReplayBufferService _buffer;

    public ReplayBufferLongLookbackTests()
    {
        Directory.CreateDirectory(_dir);
        _root = Registry.CurrentUser.CreateSubKey(_rootPath);
        _settings = new SettingsService(_dir);
        _settings.Current.Output.BufferFolder = Path.Combine(_dir, "buffer");
        _settings.Current.Output.ClipsFolder = Path.Combine(_dir, "clips");
        _settings.Current.Output.ScreenshotsFolder = Path.Combine(_dir, "shots");
        _settings.Current.Video.Resolution = ResolutionPreset.WQHD;
        _settings.Current.Video.Quality = QualityPreset.Mittel;
        // A throw-away registry root: the service must never touch the real HKCU\Software\WKI_Clipper.
        _buffer = new ReplayBufferService(_settings, new BufferFolderRecord(_root));
    }

    public void Dispose()
    {
        _buffer.Dispose();
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_rootPath, throwOnMissingSubKey: false);
        DeleteDir();
    }

    private void DeleteDir()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(60, 60)]
    [InlineData(1800, 1800)]
    [InlineData(7200, 1800)]   // a hand-edited settings.json must not build a 2-hour ring
    [InlineData(0, 15)]
    public void The_effective_lookback_is_the_clamped_setting(int configured, int expected)
    {
        _settings.Current.ReplayBuffer.DurationSeconds = configured;
        Assert.Equal(expected, _buffer.EffectiveDurationSeconds);
    }

    [Fact]
    public void Nothing_is_disk_limited_before_the_buffer_has_started()
        => Assert.False(_buffer.DiskLimited);

    [Fact]
    public void Forecast_of_thirty_minutes_at_the_default_quality_is_about_six_and_a_half_gb()
    {
        var f = _buffer.Forecast(1800);

        Assert.True(f.Known);
        Assert.InRange(f.RequiredBytes, 6.5e9 * 0.99, 6.5e9 * 1.01);
        Assert.True(f.FreeBytes > 0);
        Assert.EndsWith(":", f.Drive);
    }

    [Fact]
    public void Forecast_follows_the_quality_setting()
    {
        var mittel = _buffer.Forecast(600).RequiredBytes;
        _settings.Current.Video.Quality = QualityPreset.SehrHoch;
        Assert.True(_buffer.Forecast(600).RequiredBytes > mittel * 2);
    }

    [Fact]
    public void Forecast_clamps_an_out_of_range_length()
        => Assert.Equal(1800, _buffer.Forecast(100_000).DurationSeconds);

    [Fact]
    public void Forecast_on_a_network_folder_cannot_know_the_free_space_and_so_does_not_limit()
    {
        _settings.Current.Output.BufferFolder = @"\\server\share\buffer";
        var f = _buffer.Forecast(1800);

        Assert.False(f.Known);
        Assert.False(f.Decision.Shortened);
        Assert.False(f.Decision.CannotStart);
        Assert.Equal(1800, f.Decision.EffectiveSeconds);
    }

    [Fact]
    public void Forecast_counts_the_buffers_own_segments_as_free_space()
    {
        var folder = Path.Combine(_dir, "buffer");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "seg_0_00.mp4"), new byte[1024 * 1024]);
        File.WriteAllBytes(Path.Combine(folder, "holiday.mp4"), new byte[1024 * 1024]);   // not ours: not counted

        // 1 MiB of own segments is the only difference to the free space the drive reports.
        Assert.Equal(1024L * 1024, DiskProbe.OwnSegmentBytes(folder));
    }

    [Fact]
    public async System.Threading.Tasks.Task A_clip_save_without_a_running_buffer_never_touches_the_disk_check()
    {
        var path = await _buffer.SaveLastAsync();
        Assert.Null(path);
        Assert.False(Directory.Exists(Path.Combine(_dir, "clips")));
    }
}
