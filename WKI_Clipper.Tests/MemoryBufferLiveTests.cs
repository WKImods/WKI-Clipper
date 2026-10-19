using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;
using Xunit.Abstractions;

namespace WKI_Clipper.Tests;

/// <summary>
/// The real buffer on this machine: monitor capture, encoder, RAM ring or hidden disk ring, F9. Opt-in (WKI_LIVE=1) - it
/// records the primary monitor for a few seconds (video only, the clip is deleted afterwards).
/// </summary>
public sealed class MemoryBufferLiveTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_live_" + Guid.NewGuid().ToString("N"));
    private readonly string _rootPath = @"Software\WKI_Clipper_Tests_Live_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;
    private readonly string? _ffmpeg = FFmpegTestSupport.Find();

    public MemoryBufferLiveTests(ITestOutputHelper output)
    {
        _out = output;
        _root = Registry.CurrentUser.CreateSubKey(_rootPath);
    }

    public void Dispose()
    {
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_rootPath, throwOnMissingSubKey: false);
        try
        {
            foreach (var f in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, recursive: true);
        }
        catch { }
    }

    private static bool Enabled => Environment.GetEnvironmentVariable("WKI_LIVE") == "1";

    private async Task<(ReplayBufferService Buffer, SettingsService Settings)> StartAsync(BufferStorageMode mode)
    {
        var settings = new SettingsService(_dir);
        var s = settings.Current;
        s.Output.BufferFolder = Path.Combine(_dir, "buffer");
        s.Output.ClipsFolder = Path.Combine(_dir, "clips");
        s.Output.ScreenshotsFolder = Path.Combine(_dir, "shots");
        s.Output.SortByGame = false;
        s.Audio.RecordMicrophone = false;
        s.Audio.RecordSystemSound = false;
        s.Capture.Mode = CaptureMode.Monitor;
        s.Video.Resolution = ResolutionPreset.FullHD;
        s.Video.Framerate = 30;
        s.Video.Codec = await new FFmpegService(_ffmpeg).HasEncoderAsync("h264_amf") ? "h264_amf" : "libx264";
        s.ReplayBuffer.DurationSeconds = 60;
        s.ReplayBuffer.SegmentDurationSeconds = 2;
        s.ReplayBuffer.StorageMode = mode;
        s.ReplayBuffer.KeyframeModeOverride = KeyframeMode.ForcedAtSegmentBoundary;
        var buffer = new ReplayBufferService(settings, new BufferFolderRecord(_root)) { FfmpegPathOverride = _ffmpeg };
        buffer.BufferError += (_, m) => _out.WriteLine("BufferError: " + m);
        buffer.BufferInfo += (_, m) => _out.WriteLine("BufferInfo: " + m);
        buffer.Start();
        for (int i = 0; i < 50 && !buffer.IsRunning; i++) await Task.Delay(100);
        Assert.True(buffer.IsRunning, "the buffer did not start");
        return (buffer, settings);
    }

    [Fact]
    public async Task Live_ram_buffer_records_saves_and_gives_the_memory_back()
    {
        if (!Enabled || _ffmpeg is null) return;
        var (buffer, _) = await StartAsync(BufferStorageMode.Auto);
        try
        {
            Assert.Equal(BufferStorageKind.Memory, buffer.StorageKind);
            await Task.Delay(11_000);
            _out.WriteLine($"{buffer.StorageLine}, available {buffer.AvailableSeconds()} s");
            Assert.True(buffer.StorageBytes > 0);
            Assert.InRange(buffer.AvailableSeconds(), 6, 12);
            Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "buffer")));   // RAM: nothing on the disk

            var clip = await buffer.SaveLastAsync();
            Assert.NotNull(clip);
            var probe = FfRun.Run(_ffmpeg, $"-hide_banner -i \"{clip}\"").StdErr;
            double seconds = FFmpegProbeParser.Parse(probe)?.Duration?.TotalSeconds ?? -1;
            _out.WriteLine($"clip {seconds:0.00} s, {new FileInfo(clip!).Length / 1024} KiB");
            Assert.InRange(seconds, 6, 12);
            var (dc, derr) = FfRun.Run(_ffmpeg, $"-hide_banner -v error -i \"{clip}\" -f null -");
            Assert.True(dc == 0 && string.IsNullOrWhiteSpace(derr), derr);
            Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "clips"), "*.wkitmp.*"));
        }
        finally
        {
            await buffer.StopAsync();
            Assert.Equal(0, buffer.StorageBytes);
            buffer.Dispose();
        }
    }

    [Fact]
    public async Task Live_disk_buffer_hides_its_segments_and_still_saves()
    {
        if (!Enabled || _ffmpeg is null) return;
        var (buffer, _) = await StartAsync(BufferStorageMode.Disk);
        try
        {
            Assert.Equal(BufferStorageKind.Disk, buffer.StorageKind);
            await Task.Delay(12_000);
            var dir = new DirectoryInfo(Path.Combine(_dir, "buffer"));
            Assert.True(dir.Attributes.HasFlag(FileAttributes.Hidden), "buffer folder not hidden");
            var segs = dir.GetFiles("seg_*.mp4");
            int hidden = segs.Count(f => f.Attributes.HasFlag(FileAttributes.Hidden));
            _out.WriteLine($"{buffer.StorageLine}; {segs.Length} segments, {hidden} hidden");
            var clip = await buffer.SaveLastAsync();
            Assert.NotNull(clip);
            Assert.True(buffer.IsRunning, "the ring died");
        }
        finally
        {
            await buffer.StopAsync();
            buffer.Dispose();
        }
    }
}
