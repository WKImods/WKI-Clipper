using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Service-level rules of the RAM buffer that need no ffmpeg: the warm-restart storage decision, the per-press length
/// seam, the ring sizing source, the reader's failure path, the buffer-folder bookkeeping and the orphan sweep.
/// </summary>
public sealed class MemoryBufferServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_rams_" + Guid.NewGuid().ToString("N"));
    private readonly string _rootPath = @"Software\WKI_Clipper_Tests_RamSvc_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;
    private readonly SettingsService _settings;
    private readonly ReplayBufferService _buffer;

    public MemoryBufferServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _root = Registry.CurrentUser.CreateSubKey(_rootPath);
        _settings = new SettingsService(_dir);
        var s = _settings.Current;
        s.Output.BufferFolder = Path.Combine(_dir, "buffer");
        s.Output.ClipsFolder = Path.Combine(_dir, "clips");
        s.Output.ScreenshotsFolder = Path.Combine(_dir, "shots");
        s.Video.Codec = "h264_amf";
        s.Video.Resolution = WKI_Clipper.Models.ResolutionPreset.FullHD;
        s.ReplayBuffer.SegmentDurationSeconds = 5;
        _buffer = new ReplayBufferService(_settings, new BufferFolderRecord(_root));
    }

    public void Dispose()
    {
        _buffer.Dispose();
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_rootPath, throwOnMissingSubKey: false);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static FieldInfo Field(string name)
        => typeof(ReplayBufferService).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
           ?? throw new MissingFieldException(nameof(ReplayBufferService), name);

    private StorageDecision DecideStorage(bool coldStart)
        => (StorageDecision)typeof(ReplayBufferService).GetMethod("DecideStorage", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_buffer, new object[] { coldStart })!;

    private RingPlan CurrentRingPlan()
        => (RingPlan)typeof(ReplayBufferService).GetMethod("CurrentRingPlan", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_buffer, null)!;

    [Fact]
    public void A_warm_restart_with_the_same_settings_keeps_the_ram_ring_exactly_as_it_is()
    {
        _settings.Current.ReplayBuffer.StorageMode = BufferStorageMode.Memory;
        _settings.Current.ReplayBuffer.DurationSeconds = 60;
        var cold = DecideStorage(coldStart: true);
        if (cold.Kind != BufferStorageKind.Memory) return;   // a machine without the RAM for even 15 s: nothing to test
        var store = Field("_memory").GetValue(_buffer);
        Assert.NotNull(store);
        Field("_memoryCapSeconds").SetValue(_buffer, 30);   // the low-RAM guard cut the ring meanwhile

        var warm = DecideStorage(coldStart: false);   // Alt-Tab: same settings

        Assert.Same(cold, warm);
        Assert.Same(store, Field("_memory").GetValue(_buffer));
        Assert.Equal(30, (int)Field("_memoryCapSeconds").GetValue(_buffer)!);   // neither undone nor cut further

        _settings.Current.ReplayBuffer.DurationSeconds = 45;   // a real settings change decides anew
        var changed = DecideStorage(coldStart: false);
        Assert.NotSame(cold, changed);
    }

    [Fact]
    public void The_press_length_is_capped_by_the_ring_and_never_sizes_it()
    {
        _settings.Current.ReplayBuffer.DurationSeconds = 60;
        Assert.Equal(60, _buffer.RingLookbackSeconds);
        Assert.Equal(60, _buffer.RequestedSecondsAtPress());

        _buffer.ClipSecondsAtPress = () => 300;
        Assert.Equal(60, _buffer.RequestedSecondsAtPress());
        _buffer.ClipSecondsAtPress = () => 10;
        Assert.Equal(10, _buffer.RequestedSecondsAtPress());
        _buffer.ClipSecondsAtPress = () => 0;
        Assert.Equal(60, _buffer.RequestedSecondsAtPress());
        _buffer.ClipSecondsAtPress = () => throw new InvalidOperationException("profile lookup broke");
        Assert.Equal(60, _buffer.RequestedSecondsAtPress());

        // The ring follows RingLookbackSeconds, whatever a press asks for.
        _buffer.ClipSecondsAtPress = () => 10;
        Assert.Equal(RingPlan.Create(60, 5), CurrentRingPlan());

        // A disk-limited ring caps the press as well.
        Field("_diskCapSeconds").SetValue(_buffer, 30);
        _buffer.ClipSecondsAtPress = () => 45;
        Assert.Equal(30, _buffer.RequestedSecondsAtPress());
        Assert.Equal(RingPlan.Create(30, 5), CurrentRingPlan());

        _settings.Current.ReplayBuffer.DurationSeconds = 99_999;
        Field("_diskCapSeconds").SetValue(_buffer, 0);
        Assert.Equal(DurationText.MaxSeconds, _buffer.RingLookbackSeconds);
    }

    private sealed class BrokenPipe : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("pipe broke");
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    [Fact]
    public void A_reader_that_fails_unasked_reports_it_once_and_a_requested_stop_does_not()
    {
        using var store = new MemorySegmentStore(64L << 20);
        var causes = new List<string>();
        var feed = new MemoryBufferFeed(new BrokenPipe(), store.OpenWriter(0), 5, null, () => false, c => causes.Add(c));
        feed.Start();
        Assert.True(feed.WaitForEnd(TimeSpan.FromSeconds(5)));
        Assert.Single(causes);
        Assert.Contains("pipe broke", causes[0]);

        var quiet = new List<string>();
        var stopped = new MemoryBufferFeed(new BrokenPipe(), store.OpenWriter(1), 5, null, () => true, c => quiet.Add(c));
        stopped.Start();
        Assert.True(stopped.WaitForEnd(TimeSpan.FromSeconds(5)));
        Assert.Empty(quiet);
    }

    [Fact]
    public void The_record_knows_the_folders_it_created_and_the_last_low_ram_cut()
    {
        var record = new BufferFolderRecord(_root);
        var a = Path.Combine(_dir, "a");
        Assert.False(record.WasCreated(a));
        record.AddCreated(a);
        record.AddCreated(a + Path.DirectorySeparatorChar);   // the same folder
        Assert.True(record.WasCreated(a.ToUpperInvariant()));
        Assert.False(record.WasCreated(Path.Combine(_dir, "b")));

        Assert.Null(record.ReadLowMemory());
        var t = new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);
        record.WriteLowMemory(t);
        Assert.Equal(t, record.ReadLowMemory());
        _buffer.ClearLowMemoryHistory();
        Assert.Null(record.ReadLowMemory());
    }

    [Fact]
    public void The_first_sweep_removes_orphaned_ram_save_files_of_any_age_and_nothing_else()
    {
        var clips = Path.Combine(_dir, "clips");
        Directory.CreateDirectory(Path.Combine(clips, "Arma Reforger"));
        var orphan = Path.Combine(clips, "Arma Reforger", "Clip_2026-10-19_08-00-00.mp4.wkitmp.0.ts");
        var list = Path.Combine(clips, "Clip_2026-10-19_08-00-00.mp4.wkitmp.list.txt");
        var clip = Path.Combine(clips, "Clip_2026-10-19_07-00-00.mp4");
        var foreign = Path.Combine(clips, "holiday.ts");
        foreach (var f in new[] { orphan, list, clip, foreign }) File.WriteAllBytes(f, new byte[16]);
        File.SetAttributes(orphan, FileAttributes.Hidden);

        Assert.Equal(0, ClipOutputFiles.DeleteStaleParts(clips, DateTime.UtcNow));   // fresh: a save may still need them
        Assert.Equal(2, ClipOutputFiles.DeleteStaleParts(clips, DateTime.UtcNow, tempsOfAnyAge: true));

        Assert.False(File.Exists(orphan));
        Assert.False(File.Exists(list));
        Assert.True(File.Exists(clip));
        Assert.True(File.Exists(foreign));
    }
}
