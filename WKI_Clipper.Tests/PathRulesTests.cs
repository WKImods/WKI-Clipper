using System;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// The replay buffer writes seg_N_NN.mp4 every few seconds. If its folder is (inside) the
/// clips folder the gallery shows a new "clip" every 5 s — these rules keep the two apart.
/// </summary>
public class PathRulesTests
{
    // ---- IsSameOrInside ----

    [Theory]
    [InlineData(@"C:\Clips", @"C:\Clips")]
    [InlineData(@"C:\Clips\", @"C:\Clips")]
    [InlineData(@"C:\Clips", @"C:\Clips\")]
    [InlineData(@"C:\Clips\\", @"C:\Clips")]
    [InlineData(@"c:\clips", @"C:\CLIPS")]
    [InlineData(@"C:/Clips", @"C:\Clips")]
    public void Same_folder_is_inside_itself(string path, string folder)
        => Assert.True(PathRules.IsSameOrInside(path, folder));

    [Theory]
    [InlineData(@"C:\Clips\buffer", @"C:\Clips")]
    [InlineData(@"C:\Clips\buffer\", @"C:\Clips")]
    [InlineData(@"C:\Clips\a\b\c", @"C:\Clips")]
    [InlineData(@"c:\CLIPS\Buffer", @"C:\clips\")]
    [InlineData(@"C:/Clips/buffer", @"C:\Clips")]
    [InlineData(@"C:\Clips\sub\..", @"C:\Clips")]
    [InlineData(@"C:\Clips\sub\..\buffer", @"C:\Clips")]
    public void Anything_below_the_folder_is_inside(string path, string folder)
        => Assert.True(PathRules.IsSameOrInside(path, folder));

    [Theory]
    [InlineData(@"C:\Clips2", @"C:\Clips")]
    [InlineData(@"C:\Clips2\buffer", @"C:\Clips")]
    [InlineData(@"C:\Clips_old", @"C:\Clips")]
    [InlineData(@"C:\ClipsX\Clips", @"C:\Clips")]
    public void Sibling_with_the_same_prefix_is_not_inside(string path, string folder)
        => Assert.False(PathRules.IsSameOrInside(path, folder));

    [Theory]
    [InlineData(@"C:\", @"C:\Clips")]                  // parent of the folder
    [InlineData(@"C:\Other", @"C:\Clips")]
    [InlineData(@"D:\Clips", @"C:\Clips")]             // other drive
    [InlineData(@"D:\Clips\buffer", @"C:\Clips")]
    [InlineData(@"C:\Clips\..\Other", @"C:\Clips")]    // dot-dot escapes the folder
    [InlineData(@"C:\Clips\..", @"C:\Clips")]
    public void Unrelated_or_parent_paths_are_not_inside(string path, string folder)
        => Assert.False(PathRules.IsSameOrInside(path, folder));

    [Theory]
    [InlineData(@"C:\anything", @"C:\", true)]
    [InlineData(@"C:\", @"C:\", true)]
    [InlineData(@"C:", @"C:\", true)]
    [InlineData(@"D:\anything", @"C:\", false)]
    public void Drive_root_as_folder_contains_everything_on_that_drive(string path, string folder, bool expected)
        => Assert.Equal(expected, PathRules.IsSameOrInside(path, folder));

    [Fact]
    public void Unc_paths_follow_the_same_rules()
    {
        Assert.True(PathRules.IsSameOrInside(@"\\srv\share\Clips\buffer", @"\\srv\share\Clips"));
        Assert.True(PathRules.IsSameOrInside(@"\\SRV\Share\clips", @"\\srv\share\Clips\"));
        Assert.False(PathRules.IsSameOrInside(@"\\srv\share\Clips2", @"\\srv\share\Clips"));
        Assert.False(PathRules.IsSameOrInside(@"\\other\share\Clips", @"\\srv\share\Clips"));
    }

    [Fact]
    public void Environment_variables_are_expanded_on_both_sides()
    {
        var name = "WKI_PATHRULES_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, @"C:\Users\Test");
        try
        {
            Assert.True(PathRules.IsSameOrInside($@"%{name}%\Videos\buffer", @"C:\Users\Test\Videos"));
            Assert.True(PathRules.IsSameOrInside(@"C:\Users\Test\Videos", $@"%{name}%\Videos"));
            Assert.True(PathRules.IsSameOrInside($@"%{name}%\Videos", $@"%{name}%\Videos\"));
            Assert.False(PathRules.IsSameOrInside($@"%{name}%\Other", @"C:\Users\Test\Videos"));
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Fact]
    public void Default_locations_do_not_overlap()
    {
        var o = new OutputSettings();
        Assert.False(PathRules.IsSameOrInside(o.BufferFolder, o.ClipsFolder));
        Assert.False(PathRules.IsSameOrInside(o.BufferFolder, o.ScreenshotsFolder));
        Assert.False(PathRules.IsSameOrInside(o.ClipsFolder, o.BufferFolder));
    }

    [Theory]
    [InlineData(null, @"C:\Clips")]
    [InlineData(@"C:\Clips", null)]
    [InlineData(null, null)]
    [InlineData("", @"C:\Clips")]
    [InlineData(@"C:\Clips", "")]
    [InlineData("   ", @"C:\Clips")]
    [InlineData(@"C:\Clips", "   ")]
    [InlineData("", "")]
    public void Empty_paths_are_never_inside(string? path, string? folder)
        => Assert.False(PathRules.IsSameOrInside(path, folder));

    [Theory]
    [InlineData("C:\\Clips\0bad", @"C:\Clips")]
    [InlineData(@"C:\Clips\buffer", "C:\\Cl\0ips")]
    [InlineData(@"C:\Clips\bu|ffer", @"C:\Clips")]
    [InlineData("\"C:\\Clips\\buffer\"", @"C:\Clips")]
    public void Invalid_paths_return_false_instead_of_throwing(string path, string folder)
        => Assert.False(PathRules.IsSameOrInside(path, folder));

    // ---- SafeBufferFolder ----

    private const string Fallback = @"%LOCALAPPDATA%\WKI_Clipper\buffer";

    [Fact]
    public void Separate_buffer_folder_is_kept_as_configured()
    {
        const string buffer = @"D:\Fast\buffer";
        Assert.Same(buffer, PathRules.SafeBufferFolder(buffer, @"C:\Clips", @"C:\Shots", Fallback));
    }

    [Theory]
    [InlineData(@"C:\Clips", @"C:\Clips", @"C:\Shots")]
    [InlineData(@"C:\Clips\", @"C:\Clips", @"C:\Shots")]
    [InlineData(@"C:\Clips\buffer", @"C:\Clips", @"C:\Shots")]
    [InlineData(@"C:\Clips\a\b", @"C:\Clips", @"C:\Shots")]
    [InlineData(@"C:\Shots", @"C:\Clips", @"C:\Shots")]
    [InlineData(@"C:\Shots\buffer", @"C:\Clips", @"C:\Shots")]
    [InlineData(@"c:\clips\BUFFER", @"C:\Clips", @"C:\Shots")]
    public void Buffer_inside_clips_or_screenshots_falls_back_to_the_default(string buffer, string clips, string shots)
        => Assert.Equal(Fallback, PathRules.SafeBufferFolder(buffer, clips, shots, Fallback));

    [Fact]
    public void Sibling_with_the_same_prefix_is_not_replaced()
    {
        const string buffer = @"C:\Clips2";
        Assert.Same(buffer, PathRules.SafeBufferFolder(buffer, @"C:\Clips", @"C:\Shots", Fallback));
    }

    [Fact]
    public void Buffer_above_the_clips_folder_is_allowed()
    {
        // Segments land in C:\Videos itself; the gallery only looks at Clips and one level
        // below it, so they can never show up there.
        const string buffer = @"C:\Videos";
        Assert.Same(buffer, PathRules.SafeBufferFolder(buffer, @"C:\Videos\Clips", @"C:\Videos\Shots", Fallback));
    }

    [Fact]
    public void Environment_variables_are_expanded_when_comparing_for_the_fallback()
    {
        var name = "WKI_PATHRULES_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, @"C:\Users\Test");
        try
        {
            Assert.Equal(Fallback, PathRules.SafeBufferFolder(
                $@"%{name}%\Videos\buffer", @"C:\Users\Test\Videos", @"C:\Shots", Fallback));
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Fact]
    public void Unset_clips_and_screenshots_never_trigger_the_fallback()
    {
        const string buffer = @"D:\buffer";
        Assert.Same(buffer, PathRules.SafeBufferFolder(buffer, "", null!, Fallback));
    }

    [Fact]
    public void Default_buffer_folder_matches_the_settings_default()
        => Assert.Equal(PathRules.DefaultBufferFolder, new OutputSettings().BufferFolder);

    // ---- IsBufferSegment ----

    [Theory]
    [InlineData("seg_3_07.mp4")]
    [InlineData("seg_0_00.mp4")]
    [InlineData("seg_12_345.mp4")]
    [InlineData("SEG_3_07.MP4")]
    [InlineData("Seg_3_07.Mp4")]
    public void Buffer_segment_names_are_recognized(string name)
        => Assert.True(PathRules.IsBufferSegment(name));

    [Theory]
    [InlineData("Clip_2026-10-14_22-33-42.mp4")]
    [InlineData("Rec_2026-10-14_22-33-42.mp4")]
    [InlineData("seg_abc.mp4")]
    [InlineData("myseg_1_2.mp4")]
    [InlineData("seg_1_2.mkv")]
    [InlineData("seg_1_2.gif")]
    [InlineData("seg_1_2.mp4.tmp")]
    [InlineData("seg_1_2_3.mp4")]
    [InlineData("seg_1.mp4")]
    [InlineData("seg__2.mp4")]
    [InlineData("seg_1_.mp4")]
    [InlineData("seg_a_1.mp4")]
    [InlineData("seg_1_b.mp4")]
    [InlineData("seg_-1_2.mp4")]
    [InlineData("seg_1_2.mp4\n")]
    [InlineData(" seg_1_2.mp4")]
    [InlineData("_gif_src.mp4")]
    [InlineData("")]
    public void Everything_else_is_not_a_buffer_segment(string name)
        => Assert.False(PathRules.IsBufferSegment(name));

    [Fact]
    public void Null_file_name_is_not_a_buffer_segment()
        => Assert.False(PathRules.IsBufferSegment(null));

    [Fact]
    public void Non_ascii_digits_are_not_segment_numbers()
        => Assert.False(PathRules.IsBufferSegment("seg_\u0663_\u0664.mp4")); // Arabic-Indic digits
}
