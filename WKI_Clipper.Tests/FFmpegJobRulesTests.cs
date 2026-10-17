using System;
using System.IO;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class FFmpegJobRulesTests
{
    private const long MB = 1024L * 1024;

    // ---- disk check: free space must exceed estimate x 1.1 + 500 MB ----

    [Fact]
    public void Required_free_space_is_estimate_times_1_1_plus_500_MB()
    {
        Assert.Equal(1000 * MB * 11 / 10 + 500 * MB, FFmpegJobRules.RequiredFreeBytes(1000 * MB));
        Assert.Equal(500 * MB, FFmpegJobRules.RequiredFreeBytes(0));
        Assert.Equal(500 * MB, FFmpegJobRules.RequiredFreeBytes(-5));   // a bogus estimate never lowers the floor
    }

    [Fact]
    public void Free_space_must_strictly_exceed_the_requirement()
    {
        long required = FFmpegJobRules.RequiredFreeBytes(1000 * MB);
        Assert.False(FFmpegJobRules.HasEnoughDiskSpace(required, 1000 * MB));
        Assert.False(FFmpegJobRules.HasEnoughDiskSpace(required - 1, 1000 * MB));
        Assert.True(FFmpegJobRules.HasEnoughDiskSpace(required + 1, 1000 * MB));
    }

    // ---- collision-free final name ----

    [Fact]
    public void UniquePath_keeps_a_free_name()
    {
        Assert.Equal(@"C:\c\a.mp4", FFmpegJobRules.UniquePath(@"C:\c\a.mp4", _ => false));
    }

    [Fact]
    public void UniquePath_appends_2_then_3_when_the_name_is_taken()
    {
        var taken = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\c\a.mp4", @"C:\c\a (2).mp4"
        };
        Assert.Equal(@"C:\c\a (3).mp4", FFmpegJobRules.UniquePath(@"C:\c\a.mp4", taken.Contains));
    }

    [Fact]
    public void UniquePath_handles_names_without_extension_and_dots_in_the_name()
    {
        Assert.Equal(@"C:\c\clip.v2 (2).mp4", FFmpegJobRules.UniquePath(@"C:\c\clip.v2.mp4", p => p == @"C:\c\clip.v2.mp4"));
        Assert.Equal(@"C:\c\noext (2)", FFmpegJobRules.UniquePath(@"C:\c\noext", p => p == @"C:\c\noext"));
    }

    // ---- optional thread cap goes in front of the output file ----

    [Fact]
    public void Thread_cap_is_inserted_before_the_quoted_output_path()
    {
        var part = @"C:\c\out.mp4.part";
        var args = $"-i \"in.mp4\" -c:v libx264 -f mp4 \"{part}\"";
        Assert.Equal($"-i \"in.mp4\" -c:v libx264 -f mp4 -threads 2 \"{part}\"",
                     FFmpegJobRules.ApplyThreadCap(args, part, 2));
    }

    [Fact]
    public void Thread_cap_absent_or_zero_leaves_the_arguments_alone()
    {
        var part = @"C:\c\out.mp4.part";
        var args = $"-i in.mp4 \"{part}\"";
        Assert.Equal(args, FFmpegJobRules.ApplyThreadCap(args, part, null));
        Assert.Equal(args, FFmpegJobRules.ApplyThreadCap(args, part, 0));
    }

    [Fact]
    public void Thread_cap_falls_back_to_a_leading_option_when_the_output_is_not_found()
    {
        Assert.Equal("-threads 3 -i in.mp4 other.mp4", FFmpegJobRules.ApplyThreadCap("-i in.mp4 other.mp4", @"C:\c\x.part", 3));
    }

    // ---- -progress lines ----

    [Theory]
    [InlineData("out_time_us=5000000", 5.0)]
    [InlineData("out_time_us=1500000", 1.5)]
    [InlineData("out_time=00:00:05.000000", 5.0)]
    [InlineData("out_time=01:02:03.500000", 3723.5)]
    public void Progress_position_is_read_from_the_out_time_lines(string line, double seconds)
    {
        Assert.True(FFmpegProgressParser.TryParseOutTime(line, out var pos));
        Assert.Equal(seconds, pos.TotalSeconds, 3);
    }

    [Theory]
    [InlineData("out_time_us=N/A")]
    [InlineData("out_time=N/A")]
    [InlineData("out_time_us=-9223372036854775807")]
    [InlineData("out_time=-577014:32:22.77")]
    [InlineData("frame=150")]
    [InlineData("progress=continue")]
    [InlineData("")]
    public void Progress_ignores_unset_and_unrelated_lines(string line)
    {
        Assert.False(FFmpegProgressParser.TryParseOutTime(line, out _));
    }

    [Fact]
    public void Progress_end_marker_is_recognised()
    {
        Assert.True(FFmpegProgressParser.IsEnd("progress=end"));
        Assert.False(FFmpegProgressParser.IsEnd("progress=continue"));
    }

    [Fact]
    public void Fraction_is_position_over_expected_duration_clamped_to_0_1()
    {
        Assert.Equal(0.5, FFmpegProgressParser.Fraction(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)), 6);
        Assert.Equal(1.0, FFmpegProgressParser.Fraction(TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(10)), 6);
        Assert.Equal(0.0, FFmpegProgressParser.Fraction(TimeSpan.Zero, TimeSpan.FromSeconds(10)), 6);
        Assert.Equal(0.0, FFmpegProgressParser.Fraction(TimeSpan.FromSeconds(5), TimeSpan.Zero), 6);   // unknown length
    }

    // ---- stale .part cleanup ----

    [Fact]
    public void Only_old_part_files_count_as_stale()
    {
        var now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
        var age = TimeSpan.FromMinutes(10);
        Assert.True(FFmpegJobRules.IsStalePart("a.mp4.part", now.AddMinutes(-11), now, age));
        Assert.True(FFmpegJobRules.IsStalePart("A.MP4.PART", now.AddHours(-3), now, age));
        Assert.False(FFmpegJobRules.IsStalePart("a.mp4.part", now.AddMinutes(-9), now, age));   // maybe still being written
        Assert.False(FFmpegJobRules.IsStalePart("a.mp4", now.AddDays(-9), now, age));            // a finished clip is never touched
    }

    // ---- the runner owns -y/-n ----

    [Theory]
    [InlineData("-y -i in.mp4 -f mp4 \"x.part\"", "-i in.mp4 -f mp4 \"x.part\"")]
    [InlineData("-i in.mp4 -n \"x.part\"", "-i in.mp4 \"x.part\"")]
    [InlineData("-i in.mp4 -y", "-i in.mp4")]
    [InlineData("-y -n -i in.mp4", "-i in.mp4")]
    [InlineData("-i in.mp4  -y   -f mp4", "-i in.mp4  -f mp4")]
    [InlineData("-vf yadif -i in.mp4", "-vf yadif -i in.mp4")]          // -yadif / -y-prefixed words stay
    [InlineData("-ynot -nostdin -i a", "-ynot -nostdin -i a")]
    [InlineData("-i \"C:\\clips\\-y\\in.mp4\" -f mp4", "-i \"C:\\clips\\-y\\in.mp4\" -f mp4")]   // inside quotes
    [InlineData("-i \"a -y b.mp4\"", "-i \"a -y b.mp4\"")]
    [InlineData("", "")]
    public void Overwrite_flags_are_removed_only_as_whole_unquoted_tokens(string args, string expected)
    {
        Assert.Equal(expected, FFmpegJobRules.StripOverwriteFlags(args));
    }
}
