using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Pure parts of the RAM replay buffer: storage decision, ring eviction and pins, run grouping, TS helpers.</summary>
public sealed class MemoryBufferUnitTests
{
    private const long MB = 1024L * 1024;
    private const long GB = 1024 * MB;

    // ---- storage decision ----

    private static StorageDecision Decide(BufferStorageMode mode, int lookback, long videoBps = 25_000_000,
        long budget = 2048 * MB, RamStatus? ram = null, bool codecOk = true)
        => BufferStoragePlanner.Decide(mode, codecOk, lookback, 5, videoBps, 2, budget,
            ram ?? new RamStatus(48 * GB, 30 * GB));

    [Fact]
    public void Auto_keeps_five_minutes_at_25_mbit_in_ram_and_ten_minutes_on_disk()
    {
        var five = Decide(BufferStorageMode.Auto, 300);
        Assert.Equal(BufferStorageKind.Memory, five.Kind);
        Assert.Equal(StorageReason.FitsMemory, five.Reason);
        Assert.InRange(five.EstimatedBytes, 1100 * MB, 1400 * MB);   // (300 + 45) s x 3.5 MB/s
        Assert.False(five.Shortened);

        var ten = Decide(BufferStorageMode.Auto, 600);
        Assert.Equal(BufferStorageKind.Disk, ten.Kind);
        Assert.Equal(StorageReason.OverBudget, ten.Reason);
    }

    [Fact]
    public void Auto_goes_to_disk_when_free_ram_minus_the_floor_cannot_hold_the_ring()
    {
        // 16 GB machine, 2.5 GB free: floor 1.6 GB leaves ~0.9 GB, a 5 min ring needs ~1.2 GB.
        var d = Decide(BufferStorageMode.Auto, 300, ram: new RamStatus(16 * GB, (long)(2.5 * GB)));
        Assert.Equal(BufferStorageKind.Disk, d.Kind);
        Assert.Equal(StorageReason.LowMemory, d.Reason);
    }

    [Fact]
    public void Memory_mode_shortens_a_lookback_that_does_not_fit_instead_of_using_the_disk()
    {
        var d = Decide(BufferStorageMode.Memory, 1800);
        Assert.Equal(BufferStorageKind.Memory, d.Kind);
        Assert.True(d.Shortened);
        Assert.Equal(300, d.LookbackSeconds);   // the longest offered length that fits 2 GB at 25 Mbit/s
        Assert.True(d.EstimatedBytes <= d.BudgetBytes);
    }

    [Fact]
    public void Memory_mode_falls_back_to_disk_when_not_even_the_shortest_lookback_fits()
    {
        var d = Decide(BufferStorageMode.Memory, 300, ram: new RamStatus(16 * GB, 1 * GB));
        Assert.Equal(BufferStorageKind.Disk, d.Kind);
        Assert.Equal(StorageReason.LowMemory, d.Reason);
    }

    [Fact]
    public void Disk_mode_and_av1_always_record_to_disk()
    {
        Assert.Equal(StorageReason.DiskChosen, Decide(BufferStorageMode.Disk, 60).Reason);
        var av1 = Decide(BufferStorageMode.Memory, 60, codecOk: false);
        Assert.Equal(BufferStorageKind.Disk, av1.Kind);
        Assert.Equal(StorageReason.CodecUnsupported, av1.Reason);
        Assert.False(MpegTs.SupportsCodec("av1_amf"));
        Assert.False(MpegTs.SupportsCodec("av1_nvenc"));
        Assert.True(MpegTs.SupportsCodec("h264_amf"));
        Assert.True(MpegTs.SupportsCodec("hevc_amf"));
        Assert.True(MpegTs.SupportsCodec("libx264"));
        Assert.True(MpegTs.SupportsCodec("hevc_nvenc"));
    }

    [Fact]
    public void Auto_keeps_an_8_gb_machine_on_the_disk_even_when_it_looks_free_at_app_start()
    {
        // 8 GB, 5 GB free before the game runs: room 3.5 GB holds a 60 s ring, but not the ring AND a game.
        var d = Decide(BufferStorageMode.Auto, 60, ram: new RamStatus(8 * GB, 5 * GB));
        Assert.Equal(BufferStorageKind.Disk, d.Kind);
        Assert.Equal(StorageReason.LowMemory, d.Reason);

        // 12 GB with 10 GB free: room 8.5 GB = ring + 4 GB game reserve -> RAM.
        Assert.Equal(BufferStorageKind.Memory, Decide(BufferStorageMode.Auto, 60, ram: new RamStatus(12 * GB, 10 * GB)).Kind);
        // A 16-GB-class machine (15.4 GiB usable) passes on its size alone.
        Assert.Equal(BufferStorageKind.Memory, Decide(BufferStorageMode.Auto, 60, ram: new RamStatus((long)(15.4 * GB), 4 * GB)).Kind);
        // Forced RAM on the 8 GB machine is still RAM: the user asked for it.
        Assert.Equal(BufferStorageKind.Memory, Decide(BufferStorageMode.Memory, 60, ram: new RamStatus(8 * GB, 5 * GB)).Kind);
    }

    [Fact]
    public void After_the_guard_had_to_shorten_auto_uses_the_disk_and_only_auto()
    {
        var d = BufferStoragePlanner.Decide(BufferStorageMode.Auto, true, 60, 5, 25_000_000, 2, 2048 * MB,
            new RamStatus(48 * GB, 30 * GB), recentLowMemory: true);
        Assert.Equal(BufferStorageKind.Disk, d.Kind);
        Assert.Equal(StorageReason.RecentLowMemory, d.Reason);
        Assert.Equal(BufferStorageKind.Memory, BufferStoragePlanner.Decide(BufferStorageMode.Memory, true, 60, 5, 25_000_000, 2,
            2048 * MB, new RamStatus(48 * GB, 30 * GB), recentLowMemory: true).Kind);
    }

    [Fact]
    public void The_own_ring_counts_as_available_ram()
    {
        // 16 GB, 2 GB free while our 1.2 GB ring runs: without it a warm restart saw 0.4 GB of room and cut the ring.
        var measured = new RamStatus(16 * GB, 2 * GB);
        var withRing = BufferStoragePlanner.WithOwnRing(measured, 1200 * MB);
        Assert.Equal(2 * GB + 1200 * MB, withRing!.Value.AvailableBytes);
        Assert.Equal(BufferStorageKind.Memory, Decide(BufferStorageMode.Memory, 300, ram: withRing).Kind);
        Assert.False(Decide(BufferStorageMode.Memory, 300, ram: withRing).Shortened);
        Assert.True(Decide(BufferStorageMode.Memory, 300, ram: measured).Kind == BufferStorageKind.Disk
                    || Decide(BufferStorageMode.Memory, 300, ram: measured).Shortened);
        Assert.Null(BufferStoragePlanner.WithOwnRing(null, 5));
    }

    [Fact]
    public void The_settings_page_shows_the_longest_lookback_auto_keeps_in_ram()
    {
        Assert.Equal(300, BufferStoragePlanner.LongestAutoMemoryLookback(true, 5, 25_000_000, 2, 2048 * MB, new RamStatus(48 * GB, 30 * GB)));
        Assert.Equal(120, BufferStoragePlanner.LongestAutoMemoryLookback(true, 5, 70_000_000, 2, 2048 * MB, new RamStatus(48 * GB, 30 * GB)));
        Assert.Equal(0, BufferStoragePlanner.LongestAutoMemoryLookback(true, 5, 25_000_000, 2, 2048 * MB, new RamStatus(8 * GB, 5 * GB)));
        Assert.Equal(0, BufferStoragePlanner.LongestAutoMemoryLookback(false, 5, 25_000_000, 2, 2048 * MB, null));
    }

    [Fact]
    public void Unknown_ram_only_applies_the_budget()
    {
        var d = BufferStoragePlanner.Decide(BufferStorageMode.Auto, true, 300, 5, 25_000_000, 2, 2048 * MB, null);
        Assert.Equal(BufferStorageKind.Memory, d.Kind);
        Assert.Equal(2048 * MB, d.BudgetBytes);
    }

    [Theory]
    [InlineData(0, 48L * 1024, 2048)]     // 15 % of 48 GB is 7.2 GB: capped at 2 GB
    [InlineData(0, 8L * 1024, 1228)]      // 15 % of 8 GB
    [InlineData(0, 1024, 256)]            // tiny machine: the minimum
    [InlineData(100, 48L * 1024, 256)]    // configured values are clamped
    [InlineData(99999, 48L * 1024, 16384)]
    [InlineData(4096, 48L * 1024, 4096)]
    public void The_ram_budget_defaults_to_15_percent_capped_at_2_gb(int configured, long totalMB, int expected)
        => Assert.Equal(expected, BufferStoragePlanner.EffectiveMaxMemoryMB(configured, totalMB * MB));

    [Fact]
    public void The_safety_floor_is_one_and_a_half_gigabytes_or_ten_percent()
    {
        Assert.Equal(1536 * MB, BufferStoragePlanner.SafetyFloorBytes(8 * GB));
        Assert.Equal((long)(48 * GB * 0.10), BufferStoragePlanner.SafetyFloorBytes(48 * GB));
    }

    [Fact]
    public void Low_ram_shrinks_the_budget_by_the_missing_amount_but_never_below_the_minimum()
    {
        var ram = new RamStatus(16 * GB, 1 * GB);   // floor 1.6 GB: 0.6 GB missing
        var shrunk = BufferStoragePlanner.ShrinkBudget(2048 * MB, 1200 * MB, ram, 60 * MB);
        Assert.NotNull(shrunk);
        Assert.InRange(shrunk!.Value, 1200 * MB - (long)(0.61 * GB), 1200 * MB - (long)(0.59 * GB));

        Assert.Equal(60 * MB, BufferStoragePlanner.ShrinkBudget(2048 * MB, 200 * MB, ram, 60 * MB));
        Assert.Null(BufferStoragePlanner.ShrinkBudget(2048 * MB, 1200 * MB, new RamStatus(16 * GB, 8 * GB), 60 * MB));
    }

    [Fact]
    public void The_lookback_for_a_shrunk_budget_is_an_offered_length()
    {
        long twoMin = BufferStoragePlanner.EstimateRingBytes(120, 5, 25_000_000, 2);
        Assert.Equal(120, BufferStoragePlanner.LookbackForBytes(twoMin + MB, 300, 5, 25_000_000, 2));
        Assert.Equal(15, BufferStoragePlanner.LookbackForBytes(1, 300, 5, 25_000_000, 2));
        Assert.Equal(60, BufferStoragePlanner.LookbackForBytes(10 * GB, 60, 5, 25_000_000, 2));
    }

    // ---- ring: eviction and pins ----

    private sealed class Clock
    {
        public DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>A complete segment of <paramref name="bytes"/> bytes and <paramref name="seconds"/> seconds, closed at the clock.</summary>
    private static bool AddSegment(MemoryGenerationWriter w, Clock clock, long startPts, int bytes, double seconds = 1)
    {
        w.SegmentStarted(startPts, Array.Empty<byte>(), true);
        w.Append(new byte[bytes]);
        int before = w.Completed;
        w.SegmentEnded(startPts + (long)(seconds * MpegTs.ClockRate));
        clock.Now = clock.Now.AddSeconds(seconds);
        return w.Completed > before;
    }

    private static MemorySegmentStore Store(long budget, Clock clock)
        => new(budget, new MemoryBlockPool(64 * 1024, 8), () => clock.Now);

    [Fact]
    public void Segments_older_than_the_age_limit_leave_the_ring()
    {
        var clock = new Clock();
        using var store = Store(64 * MB, clock);
        store.SetAgeLimit(TimeSpan.FromSeconds(10));
        var w = store.OpenWriter(0);
        for (int i = 0; i < 30; i++) AddSegment(w, clock, i * 90_000L, 100_000);

        var segs = store.Snapshot();
        Assert.InRange(segs.Count, 10, 11);
        Assert.True(segs[0].EndUtc >= clock.Now.AddSeconds(-11));
        Assert.Equal(segs.Count * 2 * 64 * 1024L, store.BytesInUse);   // 100 000 bytes = two 64 KiB blocks each
    }

    [Fact]
    public void The_ring_never_holds_more_than_its_byte_budget()
    {
        var clock = new Clock();
        using var store = Store(2 * MB, clock);
        var w = store.OpenWriter(0);
        for (int i = 0; i < 100; i++)
        {
            AddSegment(w, clock, i * 90_000L, 150_000);
            Assert.True(store.BytesInUse <= 2 * MB, $"{store.BytesInUse} > budget after segment {i}");
        }
        var segs = store.Snapshot();
        Assert.Equal(99, segs[^1].Sequence);   // the newest is always kept, the oldest go
        Assert.True(segs.Count >= 5);
    }

    [Fact]
    public void A_smaller_budget_evicts_the_oldest_at_once()
    {
        var clock = new Clock();
        using var store = Store(16 * MB, clock);
        var w = store.OpenWriter(0);
        for (int i = 0; i < 20; i++) AddSegment(w, clock, i * 90_000L, 500_000);
        long freed = store.SetBudget(4 * MB);
        Assert.True(freed > 0);
        Assert.True(store.BytesInUse <= 4 * MB);
        Assert.Equal(19, store.Snapshot()[^1].Sequence);
    }

    [Fact]
    public void Pinned_segments_survive_eviction_and_are_freed_when_the_save_lets_go()
    {
        var clock = new Clock();
        using var store = Store(4 * MB, clock);
        var w = store.OpenWriter(0);
        for (int i = 0; i < 5; i++) AddSegment(w, clock, i * 90_000L, 500_000);
        var pin = store.SnapshotPinned();
        var picked = store.Pin(pin.Segments.Take(3));
        pin.Dispose();
        var bytesOfFirst = picked.Segments[0].Chunks().Sum(c => c.Length);

        // Recording goes on: the unpinned ones make room, the pinned ones stay readable.
        for (int i = 5; i < 9; i++) AddSegment(w, clock, i * 90_000L, 500_000);
        Assert.Equal(500_000, bytesOfFirst);
        Assert.All(picked.Segments, s => Assert.Equal(500_000, s.Chunks().Sum(c => c.Length)));
        Assert.True(store.BytesInUse <= 4 * MB);

        picked.Dispose();
        store.SetBudget(2 * MB);
        Assert.DoesNotContain(store.Snapshot(), s => s.Sequence < 3);
        Assert.True(store.BytesInUse <= 2 * MB);
    }

    [Fact]
    public void With_everything_pinned_a_full_budget_drops_new_footage_rather_than_growing()
    {
        var clock = new Clock();
        using var store = Store(2 * MB, clock);
        var w = store.OpenWriter(0);
        for (int i = 0; i < 3; i++) AddSegment(w, clock, i * 90_000L, 600_000);
        using var pin = store.SnapshotPinned();

        bool kept = AddSegment(w, clock, 3 * 90_000L, 900_000);

        Assert.False(kept);
        Assert.Equal(1, w.Dropped);
        Assert.True(store.BytesInUse <= 2 * MB);
        Assert.Equal(3, store.Count);
    }

    [Fact]
    public void Clear_and_dispose_give_the_memory_back()
    {
        var clock = new Clock();
        var pool = new MemoryBlockPool(64 * 1024, 4);
        var store = new MemorySegmentStore(64 * MB, pool, () => clock.Now);
        var w = store.OpenWriter(0);
        for (int i = 0; i < 10; i++) AddSegment(w, clock, i * 90_000L, 300_000);
        Assert.True(store.BytesInUse > 0);
        store.Clear();
        Assert.Equal(0, store.BytesInUse);
        Assert.Equal(0, pool.FreeBlocks);
        store.Dispose();
        Assert.False(AddSegment(w, clock, 99 * 90_000L, 1000));   // a late writer after dispose adds nothing
        Assert.Equal(0, store.BytesInUse);
    }

    [Fact]
    public void A_graceful_end_keeps_the_open_segment_a_crash_drops_it()
    {
        var clock = new Clock();
        using var store = Store(16 * MB, clock);
        var w = store.OpenWriter(0);
        w.SegmentStarted(0, Array.Empty<byte>(), true);
        w.Append(new byte[1000]);
        w.Finish(graceful: true, estimatedEndPts: 45_000);
        var seg = Assert.Single(store.Snapshot());
        Assert.Equal(TimeSpan.FromSeconds(0.5), seg.Duration);

        var crash = store.OpenWriter(1);
        crash.SegmentStarted(0, Array.Empty<byte>(), true);
        crash.Append(new byte[1000]);
        crash.Finish(graceful: false, estimatedEndPts: 45_000);
        Assert.Single(store.Snapshot());
    }

    // ---- runs for materialisation ----

    [Fact]
    public void Runs_split_at_generation_changes_and_sequence_holes()
    {
        var clock = new Clock();
        using var store = Store(64 * MB, clock);
        var g0 = store.OpenWriter(0);
        for (int i = 0; i < 3; i++) AddSegment(g0, clock, i * 90_000L, 1000);
        store.NextSequence();   // a dropped segment leaves a hole
        AddSegment(g0, clock, 4 * 90_000L, 1000);
        var g1 = store.OpenWriter(1);
        for (int i = 0; i < 2; i++) AddSegment(g1, clock, i * 90_000L, 1000);

        var picked = store.Snapshot().Select(s => new SegmentCandidate("ram", s.Generation, s.EndUtc, s.Bytes, s.Duration) { Memory = s }).ToList();
        var runs = MemoryClipMaterializer.Runs(picked);

        Assert.Equal(new[] { 3, 1, 2 }, runs.Select(r => r.Count));
    }

    // ---- TS helpers ----

    [Fact]
    public void Timestamps_unwrap_across_the_33_bit_boundary()
    {
        long nearEnd = MpegTs.PtsModulus - 90_000;
        Assert.Equal(MpegTs.PtsModulus + 90_000, MpegTs.Unwrap(90_000, nearEnd));
        Assert.Equal(nearEnd - 1, MpegTs.Unwrap(nearEnd - 1, MpegTs.PtsModulus + 5));
        Assert.True(MpegTs.AtOrAfter(10, MpegTs.PtsModulus - 10));
        Assert.False(MpegTs.AtOrAfter(MpegTs.PtsModulus - 10, 10));
        Assert.True(MpegTs.AtOrAfter(500, 500));
    }

    // ---- disk ring visibility ----

    private static SegmentFileState F(string name, int ageSeconds, bool hidden = false)
        => new(name, new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc).AddSeconds(-ageSeconds), hidden);

    [Fact]
    public void Only_the_open_slot_and_the_next_ones_ffmpeg_reopens_stay_visible()
    {
        // wrap 6, open slot 3 (newest), protect 2 ahead: slots 3, 4, 5 visible; 0, 1, 2 hidden.
        var files = new[]
        {
            F("seg_4_00.mp4", 15), F("seg_4_01.mp4", 10), F("seg_4_02.mp4", 5), F("seg_4_03.mp4", 0),
            F("seg_4_04.mp4", 30, hidden: true), F("seg_4_05.mp4", 25, hidden: true),
            F("seg_3_07.mp4", 100), F("notes.txt", 1)
        };
        var (hide, show) = SegmentVisibility.Plan(files, 4, 6, 2);

        Assert.Equal(new[] { "seg_3_07.mp4", "seg_4_00.mp4", "seg_4_01.mp4", "seg_4_02.mp4" }, hide.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(new[] { "seg_4_04.mp4", "seg_4_05.mp4" }, show.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void The_protected_window_wraps_around_the_ring()
    {
        // open slot 9 of a 10-slot ring: 9, 0, 1, 2 must be writable.
        var files = new[]
        {
            F("seg_2_00.mp4", 50, hidden: true), F("seg_2_02.mp4", 45, hidden: true), F("seg_2_05.mp4", 20),
            F("seg_2_09.mp4", 0, hidden: true)
        };
        var (hide, show) = SegmentVisibility.Plan(files, 2, 10, 3);
        Assert.Equal(new[] { "seg_2_05.mp4" }, hide);
        Assert.Equal(new[] { "seg_2_00.mp4", "seg_2_02.mp4", "seg_2_09.mp4" }, show.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Before_the_first_segment_of_a_run_its_first_slots_stay_visible()
    {
        var files = new[] { F("seg_3_00.mp4", 50, hidden: true), F("seg_3_07.mp4", 40), F("seg_2_01.mp4", 10) };
        var (hide, show) = SegmentVisibility.Plan(files, 3, 10, 2);
        // seg_3_07 is the newest of generation 3: slots 7, 8, 9 visible, slot 0 may stay hidden; gen 2 is hidden.
        Assert.Equal(new[] { "seg_2_01.mp4" }, hide);
        Assert.Empty(show);
        var (hide2, show2) = SegmentVisibility.Plan(new[] { F("seg_4_00.mp4", 50, hidden: true) }, 5, 10, 2);
        Assert.Empty(hide2);
        Assert.Empty(show2);   // other generation, already hidden; generation 5 has no file yet
    }

    [Fact]
    public void Protect_ahead_covers_at_least_15_seconds_and_never_the_whole_ring()
    {
        Assert.Equal(3, SegmentVisibility.ProtectAhead(5, 20));
        Assert.Equal(15, SegmentVisibility.ProtectAhead(1, 400));
        Assert.Equal(2, SegmentVisibility.ProtectAhead(1, 4));
    }

    [Fact]
    public void Hidden_segments_are_shown_again_for_their_generation_and_the_folder_is_hidden_only_when_it_is_ours()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wki_vis_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "seg_1_00.mp4");
            var b = Path.Combine(dir, "seg_12_00.mp4");
            File.WriteAllBytes(a, new byte[10]);
            File.WriteAllBytes(b, new byte[10]);
            File.SetAttributes(a, FileAttributes.Hidden);
            File.SetAttributes(b, FileAttributes.Hidden);

            SegmentVisibility.ShowGeneration(dir, 1);
            Assert.False(File.GetAttributes(a).HasFlag(FileAttributes.Hidden));
            Assert.True(File.GetAttributes(b).HasFlag(FileAttributes.Hidden));

            // A folder that is not ours is never hidden, whatever is in it.
            Assert.False(SegmentVisibility.UpdateFolder(dir, ours: false));
            Assert.False(new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.Hidden));

            // Ours with only buffer files: hidden.
            Assert.True(SegmentVisibility.UpdateFolder(dir, ours: true));
            Assert.True(new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.Hidden));

            // The user puts something there: visible again.
            File.WriteAllBytes(Path.Combine(dir, "holiday.jpg"), new byte[10]);
            Assert.False(SegmentVisibility.UpdateFolder(dir, ours: true));
            Assert.False(new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.Hidden));
            File.Delete(Path.Combine(dir, "holiday.jpg"));

            // A folder the buffer leaves gets its attribute back.
            Assert.True(SegmentVisibility.UpdateFolder(dir, ours: true));
            SegmentVisibility.ShowFolder(dir);
            Assert.False(new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.Hidden));
        }
        finally
        {
            try { new DirectoryInfo(dir).Attributes = FileAttributes.Directory; } catch { }
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- helper files, commands, retention ----

    [Fact]
    public void Temp_files_of_a_ram_save_are_swept_after_six_hours_and_nothing_else()
    {
        var now = DateTime.UtcNow;
        var old = now.AddHours(-7);
        string clip = @"C:\c\Clip_2026-10-18_12-00-00.mp4";
        Assert.Equal(clip + ".wkitmp.0.ts", ClipOutputFiles.TempPathFor(clip, "0.ts"));
        Assert.True(ClipOutputFiles.IsStaleTemp("Clip_2026-10-18_12-00-00.mp4.wkitmp.0.ts", old, now));
        Assert.True(ClipOutputFiles.IsStaleTemp("Gif_2026-10-18_12-00-00.gif.wkitmp.src", old, now));
        Assert.True(ClipOutputFiles.IsStaleTemp("Clip_2026-10-18_12-00-00 (2).mp4.wkitmp.list.txt", old, now));
        Assert.False(ClipOutputFiles.IsStaleTemp("Clip_2026-10-18_12-00-00.mp4.wkitmp.0.ts", now.AddHours(-1), now));
        Assert.False(ClipOutputFiles.IsStaleTemp("movie.ts", old, now));
        Assert.False(ClipOutputFiles.IsStaleTemp("Clip_2026-10-18_12-00-00.mp4", old, now));
    }

    [Fact]
    public void The_ram_command_writes_mpegts_to_stdout_with_the_same_encoder_arguments()
    {
        var s = new WKI_Clipper.Models.AppSettings();
        s.Video.Codec = "h264_amf";
        s.Video.Resolution = WKI_Clipper.Models.ResolutionPreset.FullHD;
        var disk = FFmpegCommandBuilder.Build(s, @"C:\b\seg_0_%02d.mp4", segmentOutput: true, segmentDurationSec: 5,
            segmentWrap: 20, keyframes: KeyframeMode.ForcedBoundaryWithSegmentGop);
        var ram = FFmpegCommandBuilder.Build(s, @"C:\b\seg_0_%02d.mp4", segmentOutput: true, segmentDurationSec: 5,
            segmentWrap: 20, keyframes: KeyframeMode.ForcedBoundaryWithSegmentGop, mpegTsPipe: true);

        Assert.EndsWith("-f mpegts -pes_payload_size 0 pipe:1", ram);
        Assert.DoesNotContain("-f segment", ram);
        Assert.DoesNotContain("seg_0_", ram);
        int cut = disk.IndexOf("-f segment", StringComparison.Ordinal);
        Assert.Equal(disk[..cut], ram[..cut]);   // capture, filters, encoder and keyframes identical
    }

    [Fact]
    public void A_concat_of_ram_runs_maps_video_and_audio_never_everything()
    {
        var a = FFmpegCommandBuilder.BuildConcat("l.txt", "o.mp4.part", true, "c.txt", audioVideoOnly: true);
        Assert.Contains("-map 0:v -map 0:a? ", a);
        Assert.DoesNotContain("-map 0 ", a);
        Assert.Contains("-map_chapters 1", a);
        Assert.Contains("-map 0 ", FFmpegCommandBuilder.BuildConcat("l.txt", "o.mp4.part"));
    }

    [Fact]
    public void Ram_segments_follow_the_same_retention_rules_as_files()
    {
        var current = new SegmentIdentity("mon:0:1920x1080", "h264_amf|60|Native|a1|dda|EncoderDefault");
        var other = new SegmentIdentity("mon:0:1920x1080", "hevc_amf|60|Native|a1|dda|EncoderDefault");
        var now = DateTime.UtcNow;
        var gens = new Dictionary<int, GenerationInfo>
        {
            [0] = new(0, other, "x", now.AddMinutes(-10), now.AddMinutes(-5)),
            [1] = new(1, current, "x", now.AddMinutes(-5), null),
        };
        var ctx = new RetentionContext(now, now.AddHours(-1), 100, 1, current, gens, _ => true);
        Assert.True(SegmentRetention.ShouldDelete(0, now.AddSeconds(-5), ctx));     // other codec
        Assert.False(SegmentRetention.ShouldDelete(1, now.AddSeconds(-5), ctx));
        Assert.True(SegmentRetention.ShouldDelete(1, now.AddSeconds(-200), ctx));   // aged out
        Assert.True(SegmentRetention.ShouldDelete(7, now.AddHours(-2), ctx));       // unknown, before the session
        Assert.False(SegmentRetention.ShouldDelete(7, now, ctx));
    }
}
