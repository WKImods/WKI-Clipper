using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// F9 used to write straight to Clip_*.mp4, so the gallery could list a half-written file
/// that was still growing (or one left behind by a timeout). Output now goes to a .part
/// file and only appears under its real name once the whole thing succeeded - and a finished
/// clip is never deleted just because the rename was blocked for a moment.
/// </summary>
public sealed class ClipOutputFilesTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wki_clipout_" + Guid.NewGuid().ToString("N"));

    public ClipOutputFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Touch(string relative, DateTime lastWriteUtc, string content = "x")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    // ---- naming ----

    [Theory]
    [InlineData(@"C:\clips\Clip_1.mp4", @"C:\clips\Clip_1.mp4.part")]
    [InlineData(@"C:\clips\Gif_1.gif", @"C:\clips\Gif_1.gif.part")]
    public void Part_path_appends_part_to_the_final_name(string final, string part)
        => Assert.Equal(part, ClipOutputFiles.PartPathFor(final));

    // ---- ffmpeg arguments: the extension no longer tells ffmpeg the container ----

    [Fact]
    public void Concat_names_the_container_explicitly_before_the_output()
    {
        var args = FFmpegCommandBuilder.BuildConcat("list.txt", @"C:\c\Clip_1.mp4.part");
        Assert.EndsWith("-f mp4 \"C:\\c\\Clip_1.mp4.part\"", args);
        Assert.Contains("-map 0", args);
    }

    [Fact]
    public void Gif_names_the_container_explicitly_before_the_output()
    {
        var args = FFmpegCommandBuilder.BuildGif("src.mp4", @"C:\c\Gif_1.gif.part", 5, 15, 640);
        Assert.Contains("-f gif", args);
        Assert.EndsWith("-y \"C:\\c\\Gif_1.gif.part\"", args);
        Assert.Contains("-loop 0", args);
    }

    // ---- which leftovers are stale: only our own names, and only after a day ----

    private const int Hour = 3600;

    [Theory]
    [InlineData("Clip_2026-10-17_11-00-00.mp4.part", 25 * Hour, true)]
    [InlineData("Gif_2026-10-17_11-00-00.gif.part", 25 * Hour, true)]
    [InlineData("Clip_2026-10-17_11-00-00 (1).mp4.part", 25 * Hour, true)]     // collision suffix
    [InlineData("Gif_2026-10-17_11-00-00 (12).gif.part", 25 * Hour, true)]
    [InlineData("Clip_2026-10-17_11-00-00.mp4.part", 24 * Hour, false)]        // exactly 24 h: not yet
    [InlineData("Clip_2026-10-17_11-00-00.mp4.part", 24 * Hour - 1, false)]
    [InlineData("Clip_2026-10-17_11-00-00.mp4.part", 11 * 60, false)]          // the old 10 min rule is gone
    [InlineData("Clip_2026-10-17_11-00-00.mp4.part", 30, false)]               // a save in flight
    // Not ours - the clips folder may be D:\ and other programs keep .part files too:
    [InlineData("Some.Movie.2024.mp4.part", 99 * Hour, false)]                 // Firefox / browser downloads
    [InlineData("holiday.part", 99 * Hour, false)]
    [InlineData("Rec_2026-10-17_11-00-00.mp4.part", 99 * Hour, false)]
    [InlineData("Clip_1.mp4.part", 99 * Hour, false)]                          // not our timestamp
    [InlineData("Clip_2026-10-17_11-00-00.mp4", 99 * Hour, false)]             // a real clip is never touched
    [InlineData("Gif_2026-10-17_11-00-00.gif", 99 * Hour, false)]
    [InlineData("Clip_2026-10-17_11-00-00.mp4.part.bak", 99 * Hour, false)]
    [InlineData("Clip_2026-10-17_11-00-00.mp4.partial", 99 * Hour, false)]
    [InlineData("My_Clip_2026-10-17_11-00-00.mp4.part", 99 * Hour, false)]     // anchored at the start
    [InlineData("Clip_2026-10-17_11-00-00 (x).mp4.part", 99 * Hour, false)]
    [InlineData("Clip_2026-10-17_11-00-00.gif.part", 99 * Hour, false)]        // Clip is always mp4
    [InlineData("Gif_2026-10-17_11-00-00.mp4.part", 99 * Hour, false)]         // Gif is always gif
    [InlineData("CLIP_2026-10-17_11-00-00.MP4.PART", 99 * Hour, false)]        // exact names only
    public void Stale_part_rule(string fileName, int ageSeconds, bool expected)
        => Assert.Equal(expected, ClipOutputFiles.IsStalePart(fileName, Now.AddSeconds(-ageSeconds), Now));

    [Fact]
    public void Stale_cleanup_covers_the_base_folder_and_one_level_of_game_folders_only()
    {
        var old = Now.AddHours(-30);
        var fresh = Now.AddHours(-2);

        var baseOld = Touch("Clip_2026-10-16_10-00-00.mp4.part", old);
        var gameOld = Touch(Path.Combine("Arma Reforger", "Gif_2026-10-16_10-00-01.gif.part"), old);
        var baseFresh = Touch("Clip_2026-10-17_10-00-00.mp4.part", fresh);
        var gameFresh = Touch(Path.Combine("Arma Reforger", "Clip_2026-10-17_10-00-01.mp4.part"), fresh);
        var realClip = Touch("Clip_2026-10-16_10-00-02.mp4", old);
        var realGameClip = Touch(Path.Combine("Arma Reforger", "Clip_2026-10-16_10-00-03.mp4"), old);
        var foreign = Touch("something.part", old);
        var download = Touch("Some.Movie.2024.mp4.part", old);
        var gameDownload = Touch(Path.Combine("Arma Reforger", "tutorial.mp4.part"), old);
        var tooDeep = Touch(Path.Combine("Arma Reforger", "nested", "Clip_2026-10-16_10-00-04.mp4.part"), old);

        int deleted = ClipOutputFiles.DeleteStaleParts(_root, Now);

        Assert.Equal(2, deleted);
        Assert.False(File.Exists(baseOld));
        Assert.False(File.Exists(gameOld));
        foreach (var kept in new[] { baseFresh, gameFresh, realClip, realGameClip, foreign, download, gameDownload, tooDeep })
            Assert.True(File.Exists(kept), kept);
    }

    [Fact]
    public void Stale_cleanup_of_a_missing_or_empty_folder_is_harmless()
    {
        Assert.Equal(0, ClipOutputFiles.DeleteStaleParts(Path.Combine(_root, "nope"), Now));
        Assert.Equal(0, ClipOutputFiles.DeleteStaleParts("", Now));
        Assert.Equal(0, ClipOutputFiles.DeleteStaleParts(_root, Now));
    }

    // ---- finishing ----

    private sealed class Recorder
    {
        public readonly List<TimeSpan> Delays = new();
        public int MoveCalls;
        public Task Delay(TimeSpan t) { Delays.Add(t); return Task.CompletedTask; }
    }

    [Fact]
    public async Task Successful_output_is_renamed_to_its_real_name()
    {
        var fin = Path.Combine(_root, "Clip_1.mp4");
        var part = Touch("Clip_1.mp4.part", Now, "data");

        Assert.Equal(FinishOutcome.Published, await ClipOutputFiles.FinishAsync(part, fin, exitCode: 0));

        Assert.Equal("data", File.ReadAllText(fin));
        Assert.False(File.Exists(part));
    }

    [Fact]
    public async Task A_failed_exit_code_removes_the_part_and_publishes_nothing()
    {
        var fin = Path.Combine(_root, "Clip_1.mp4");
        var part = Touch("Clip_1.mp4.part", Now, "half");

        Assert.Equal(FinishOutcome.Failed, await ClipOutputFiles.FinishAsync(part, fin, exitCode: 1));

        Assert.False(File.Exists(fin));
        Assert.False(File.Exists(part));
    }

    [Fact]
    public async Task Exit_zero_without_an_output_file_is_a_failure()
    {
        var fin = Path.Combine(_root, "Clip_1.mp4");
        var part = Path.Combine(_root, "Clip_1.mp4.part");

        Assert.Equal(FinishOutcome.Failed, await ClipOutputFiles.FinishAsync(part, fin, exitCode: 0));
        Assert.False(File.Exists(fin));
    }

    [Fact]
    public async Task Finish_replaces_a_same_second_leftover_instead_of_failing()
    {
        var fin = Touch("Clip_1.mp4", Now, "old");
        var part = Touch("Clip_1.mp4.part", Now, "new");

        Assert.Equal(FinishOutcome.Published, await ClipOutputFiles.FinishAsync(part, fin, 0));
        Assert.Equal("new", File.ReadAllText(fin));
    }

    [Fact]
    public async Task A_rename_blocked_by_a_scanner_is_retried_for_both_exception_types_and_then_succeeds()
    {
        // Defender / the indexer / OneDrive hold a just-closed file: IOException or, typically,
        // UnauthorizedAccessException. The old code retried only the first and then deleted the clip.
        var fin = Path.Combine(_root, "Clip_1.mp4");
        var part = Touch("Clip_1.mp4.part", Now, "clip");
        var rec = new Recorder();
        var ops = new OutputFileOps
        {
            Move = (from, to) =>
            {
                rec.MoveCalls++;
                if (rec.MoveCalls == 1) throw new UnauthorizedAccessException("scanner");
                if (rec.MoveCalls == 2) throw new IOException("sharing violation");
                File.Move(from, to, overwrite: true);
            },
            Delay = rec.Delay,
        };

        var outcome = await ClipOutputFiles.FinishAsync(part, fin, 0, ops);

        Assert.Equal(FinishOutcome.Published, outcome);
        Assert.Equal(3, rec.MoveCalls);
        Assert.Equal(2, rec.Delays.Count);
        Assert.True(rec.Delays[1] > rec.Delays[0]);                  // backoff grows
        Assert.Equal("clip", File.ReadAllText(fin));
        Assert.False(File.Exists(part));
    }

    [Fact]
    public void The_rename_retries_span_about_ten_seconds_with_non_decreasing_backoff()
    {
        var total = ClipOutputFiles.RenameBackoff.Aggregate(TimeSpan.Zero, (a, b) => a + b);

        Assert.InRange(total.TotalSeconds, 9.5, 10.5);
        for (int i = 1; i < ClipOutputFiles.RenameBackoff.Length; i++)
            Assert.True(ClipOutputFiles.RenameBackoff[i] >= ClipOutputFiles.RenameBackoff[i - 1]);
    }

    [Fact]
    public async Task When_the_rename_never_works_the_clip_is_copied_and_published()
    {
        var fin = Path.Combine(_root, "Clip_1.mp4");
        var part = Touch("Clip_1.mp4.part", Now, "clip");
        var rec = new Recorder();
        var ops = new OutputFileOps
        {
            Move = (_, _) => { rec.MoveCalls++; throw new UnauthorizedAccessException("locked"); },
            Delay = rec.Delay,
        };

        var outcome = await ClipOutputFiles.FinishAsync(part, fin, 0, ops);

        Assert.Equal(FinishOutcome.Published, outcome);
        Assert.Equal(ClipOutputFiles.RenameBackoff.Length + 1, rec.MoveCalls);     // first try + every retry
        Assert.Equal(ClipOutputFiles.RenameBackoff.ToList(), rec.Delays);
        Assert.Equal("clip", File.ReadAllText(fin));
        Assert.False(File.Exists(part));
    }

    [Fact]
    public async Task When_rename_and_copy_both_fail_the_finished_clip_is_kept_and_never_deleted()
    {
        var fin = Path.Combine(_root, "Clip_1.mp4");
        var part = Touch("Clip_1.mp4.part", Now, "the only copy of the clip");
        var rec = new Recorder();
        var ops = new OutputFileOps
        {
            Move = (_, _) => throw new UnauthorizedAccessException("locked"),
            // a copy that dies midway leaves a half file under the real name
            Copy = (_, to) => { File.WriteAllText(to, "half"); throw new IOException("disk full"); },
            Delay = rec.Delay,
        };

        var outcome = await ClipOutputFiles.FinishAsync(part, fin, 0, ops);

        Assert.Equal(FinishOutcome.RenameFailed, outcome);
        Assert.Equal("the only copy of the clip", File.ReadAllText(part));         // kept for the user
        Assert.False(File.Exists(fin));                                            // no half clip in the gallery
    }

    [Fact]
    public async Task A_failed_fallback_leaves_an_older_clip_with_the_same_name_alone()
    {
        var fin = Touch("Clip_1.mp4", Now, "older clip");
        var part = Touch("Clip_1.mp4.part", Now, "new clip");
        var ops = new OutputFileOps
        {
            Move = (_, _) => throw new IOException("locked"),
            Copy = (_, _) => throw new IOException("locked"),
            Delay = _ => Task.CompletedTask,
        };

        Assert.Equal(FinishOutcome.RenameFailed, await ClipOutputFiles.FinishAsync(part, fin, 0, ops));

        Assert.Equal("older clip", File.ReadAllText(fin));
        Assert.Equal("new clip", File.ReadAllText(part));
    }

    [Fact]
    public async Task A_non_io_failure_skips_the_retries_and_goes_straight_to_the_fallback()
    {
        var fin = Path.Combine(_root, "Clip_1.mp4");
        var part = Touch("Clip_1.mp4.part", Now, "clip");
        var rec = new Recorder();
        var ops = new OutputFileOps
        {
            Move = (_, _) => { rec.MoveCalls++; throw new NotSupportedException("weird path"); },
            Delay = rec.Delay,
        };

        Assert.Equal(FinishOutcome.Published, await ClipOutputFiles.FinishAsync(part, fin, 0, ops));
        Assert.Equal(1, rec.MoveCalls);
        Assert.Empty(rec.Delays);
    }

    [Fact]
    public void Discard_removes_the_part_and_tolerates_a_missing_one()
    {
        var part = Touch("Clip_1.mp4.part", Now);
        ClipOutputFiles.Discard(part);
        Assert.False(File.Exists(part));

        ClipOutputFiles.Discard(part);   // already gone: no throw
    }
}
