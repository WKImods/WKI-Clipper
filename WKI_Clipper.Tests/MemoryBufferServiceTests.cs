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

    [Fact]
    public void The_ring_is_sized_for_the_longest_length_while_a_press_saves_its_own()
    {
        var s = _settings.Current;
        s.ReplayBuffer.DurationSeconds = 60;
        GameProfiles.SetClipSeconds(s, "arma.exe", "Arma", 1800);
        GameProfiles.SetClipSeconds(s, "cs2.exe", "CS2", 30);

        // Every sizing site reads RingLookbackSeconds: effective lookback, ring plan, press cap.
        Assert.Equal(1800, _buffer.RingLookbackSeconds);
        Assert.Equal(1800, _buffer.EffectiveDurationSeconds);
        Assert.Equal(RingPlan.Create(1800, 5), CurrentRingPlan());

        // Nothing detected / unknown / desktop: the global length is the backup, not the ring.
        Assert.Equal(60, _buffer.RequestedSecondsAtPress());
        string? exe = null;
        _buffer.GameExeAtPress = () => exe;
        foreach (var (game, expected) in new (string?, int)[] { (null, 60), ("Desktop", 60), ("unknown.exe", 60), ("cs2.exe", 30), ("ARMA.EXE", 1800) })
        {
            exe = game;
            Assert.Equal(expected, _buffer.RequestedSecondsAtPress());
        }

        // A lookup that throws never breaks F9: the global length applies.
        _buffer.GameExeAtPress = () => throw new InvalidOperationException("detector broke");
        Assert.Equal(60, _buffer.RequestedSecondsAtPress());
    }

    [Fact]
    public void The_press_length_wired_like_the_app_does_follows_the_game_and_is_capped_by_the_running_ring()
    {
        var s = _settings.Current;
        s.ReplayBuffer.DurationSeconds = 60;
        GameProfiles.SetClipSeconds(s, "arma.exe", "Arma", 1800);
        string? exe = "arma.exe";
        _buffer.GameExeAtPress = () => exe;
        _buffer.ClipSecondsAtPress = () => EffectiveSettings.ClipSecondsFor(_settings.Current, exe);

        Assert.Equal(1800, _buffer.RequestedSecondsAtPress());
        var clip = _buffer.ResolvePressClip();
        Assert.Equal(ClipLengthSource.Profile, clip.Source);
        Assert.Equal("arma.exe", clip.ProfileKey);

        exe = null;                                                    // the game is not recognised: the backup
        Assert.Equal(60, _buffer.RequestedSecondsAtPress());
        Assert.Equal(ClipLengthSource.Global, _buffer.ResolvePressClip().Source);

        exe = "arma.exe";
        Field("_memoryCapSeconds").SetValue(_buffer, 300);             // the low-RAM guard cut the running ring
        Assert.Equal(300, _buffer.RequestedSecondsAtPress());
        Assert.True(_buffer.ResolvePressClip().CappedByRing);
    }

    [Fact]
    public void A_profile_length_changes_the_storage_key_so_the_storage_is_decided_anew_for_the_longer_ring()
    {
        var s = _settings.Current;
        s.ReplayBuffer.StorageMode = BufferStorageMode.Disk;   // deterministic on every machine
        s.ReplayBuffer.DurationSeconds = 60;

        var before = DecideStorage(coldStart: true);
        Assert.Equal(60, before.LookbackSeconds);
        Assert.Same(before, DecideStorage(coldStart: false));   // unchanged settings: a warm restart keeps it

        GameProfiles.SetClipSeconds(s, "arma.exe", "Arma", 1800);
        var after = DecideStorage(coldStart: false);
        Assert.NotSame(before, after);
        Assert.Equal(1800, after.LookbackSeconds);              // keyed AND decided for the longest length
        Assert.True(after.EstimatedBytes > before.EstimatedBytes * 10);

        GameProfiles.SetClipSeconds(s, "arma.exe", null, null);
        Assert.Equal(60, DecideStorage(coldStart: false).LookbackSeconds);
    }

    [Fact]
    public void A_profile_that_makes_the_ring_longer_can_flip_auto_from_ram_to_disk()
    {
        // The planner is fed the RING length: 60 s fits a 2 GB RAM budget, 30 min does not.
        var s = new WKI_Clipper.Models.AppSettings();
        s.ReplayBuffer.DurationSeconds = 60;
        long budget = 2L << 30;
        StorageDecision Decide(int ring) => BufferStoragePlanner.Decide(BufferStorageMode.Auto, true, ring, 5, 25_000_000, 1, budget, null);

        Assert.Equal(BufferStorageKind.Memory, Decide(ProfileLengths.RingSeconds(s, 60)).Kind);
        GameProfiles.SetClipSeconds(s, "arma.exe", "Arma", 1800);
        var flipped = Decide(ProfileLengths.RingSeconds(s, 60));
        Assert.Equal(BufferStorageKind.Disk, flipped.Kind);
        Assert.Equal(StorageReason.OverBudget, flipped.Reason);
    }

    [Fact]
    public void The_disk_forecast_for_the_ring_length_counts_the_longest_profile()
    {
        var s = _settings.Current;
        s.ReplayBuffer.DurationSeconds = 60;
        long globalOnly = _buffer.Forecast(_buffer.RingLookbackSeconds).RequiredBytes;
        GameProfiles.SetClipSeconds(s, "arma.exe", "Arma", 1800);
        long withProfile = _buffer.Forecast(_buffer.RingLookbackSeconds).RequiredBytes;
        Assert.True(withProfile > globalOnly * 10);
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
