using System;
using System.IO;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// F9 used to write straight to Clip_*.mp4, so the gallery could list a half-written file
/// that was still growing (or one left behind by a timeout). Output now goes to a .part
/// file and only appears under its real name once the whole thing succeeded.
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

    // ---- which leftovers are stale ----

    [Theory]
    [InlineData("Clip_2026-10-17_11-00-00.mp4.part", 11 * 60, true)]
    [InlineData("Gif_2026-10-17_11-00-00.gif.part", 11 * 60, true)]
    [InlineData("Clip_2026-10-17_11-00-00.mp4.part", 10 * 60, false)]   // exactly ten minutes: not yet
    [InlineData("Clip_2026-10-17_11-00-00.mp4.part", 9 * 60 + 59, false)]
    [InlineData("Clip_2026-10-17_11-00-00.mp4.part", 30, false)]        // a save in flight
    [InlineData("Clip_2026-10-17_11-00-00.mp4", 3600, false)]           // a real clip is never touched
    [InlineData("Gif_2026-10-17_11-00-00.gif", 3600, false)]
    [InlineData("holiday.part", 3600, false)]                          // not ours: must end in .mp4.part / .gif.part
    [InlineData("Rec_2026.mkv.part", 3600, false)]
    [InlineData("Clip_1.mp4.part.bak", 3600, false)]
    [InlineData("notes.mp4.partial", 3600, false)]
    [InlineData("Clip_1.MP4.PART", 3600, false)]                       // exact suffix only
    public void Stale_part_rule(string fileName, int ageSeconds, bool expected)
        => Assert.Equal(expected, ClipOutputFiles.IsStalePart(fileName, Now.AddSeconds(-ageSeconds), Now));

    [Fact]
    public void Stale_cleanup_covers_the_base_folder_and_one_level_of_game_folders_only()
    {
        var old = Now.AddMinutes(-30);
        var fresh = Now.AddSeconds(-20);

        var baseOld = Touch("Clip_a.mp4.part", old);
        var gameOld = Touch(Path.Combine("Arma Reforger", "Gif_b.gif.part"), old);
        var baseFresh = Touch("Clip_c.mp4.part", fresh);
        var gameFresh = Touch(Path.Combine("Arma Reforger", "Clip_d.mp4.part"), fresh);
        var realClip = Touch("Clip_e.mp4", old);
        var realGameClip = Touch(Path.Combine("Arma Reforger", "Clip_f.mp4"), old);
        var foreign = Touch("something.part", old);
        var tooDeep = Touch(Path.Combine("Arma Reforger", "nested", "Clip_g.mp4.part"), old);

        int deleted = ClipOutputFiles.DeleteStaleParts(_root, Now);

        Assert.Equal(2, deleted);
        Assert.False(File.Exists(baseOld));
        Assert.False(File.Exists(gameOld));
        Assert.True(File.Exists(baseFresh));
        Assert.True(File.Exists(gameFresh));
        Assert.True(File.Exists(realClip));
        Assert.True(File.Exists(realGameClip));
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(tooDeep));
    }

    [Fact]
    public void Stale_cleanup_of_a_missing_or_empty_folder_is_harmless()
    {
        Assert.Equal(0, ClipOutputFiles.DeleteStaleParts(Path.Combine(_root, "nope"), Now));
        Assert.Equal(0, ClipOutputFiles.DeleteStaleParts("", Now));
        Assert.Equal(0, ClipOutputFiles.DeleteStaleParts(_root, Now));
    }

    // ---- finishing ----

    [Fact]
    public void Successful_output_is_renamed_to_its_real_name()
    {
        var fin = Path.Combine(_root, "Clip_1.mp4");
        var part = Touch("Clip_1.mp4.part", Now, "data");

        Assert.True(ClipOutputFiles.Finish(part, fin, exitCode: 0));

        Assert.True(File.Exists(fin));
        Assert.False(File.Exists(part));
        Assert.Equal("data", File.ReadAllText(fin));
    }

    [Fact]
    public void A_failed_exit_code_removes_the_part_and_publishes_nothing()
    {
        var fin = Path.Combine(_root, "Clip_1.mp4");
        var part = Touch("Clip_1.mp4.part", Now, "half");

        Assert.False(ClipOutputFiles.Finish(part, fin, exitCode: 1));

        Assert.False(File.Exists(fin));
        Assert.False(File.Exists(part));
    }

    [Fact]
    public void Exit_zero_without_an_output_file_is_a_failure()
    {
        var fin = Path.Combine(_root, "Clip_1.mp4");
        var part = Path.Combine(_root, "Clip_1.mp4.part");

        Assert.False(ClipOutputFiles.Finish(part, fin, exitCode: 0));
        Assert.False(File.Exists(fin));
    }

    [Fact]
    public void Finish_replaces_a_same_second_leftover_instead_of_failing()
    {
        var fin = Touch("Clip_1.mp4", Now, "old");
        var part = Touch("Clip_1.mp4.part", Now, "new");

        Assert.True(ClipOutputFiles.Finish(part, fin, 0));
        Assert.Equal("new", File.ReadAllText(fin));
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
