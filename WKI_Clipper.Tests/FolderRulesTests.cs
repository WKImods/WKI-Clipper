using System;
using System.IO;
using System.Reflection;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// A user set the buffer folder to "D:". That is not a folder but "the current directory on D:" - and two programs
/// disagree about it: ffmpeg (started in its own directory) wrote the ring into one place, the clipper (which looked
/// into its own current directory) never saw it. The ring, the installed program and the clips ended up in the
/// install folder, nothing was pruned, the disk filled. These rules keep every working folder a real, fully
/// qualified folder that is neither a drive root nor the program's own folder.
/// </summary>
public sealed class FolderRulesTests
{
    private const string Install = @"D:\WKI Clipper\";

    // ---- buffer folder: shape ----

    [Theory]
    [InlineData("D:")]                  // the user's input: the current directory on D:
    [InlineData("d:")]
    [InlineData("D:foo")]               // drive-relative
    [InlineData("D:foo\\bar")]
    [InlineData("buffer")]              // relative
    [InlineData(@"buffer\segments")]
    [InlineData(@".\buffer")]
    [InlineData(@"..\buffer")]
    [InlineData(@"\buffer")]            // rooted on "the current drive"
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData(@"%WKI_SURELY_UNDEFINED_VARIABLE%\buffer")]   // stays a literal, relative path
    [InlineData(@"D:\bu|ffer")]         // invalid characters
    [InlineData("\"D:\\buffer\"")]
    [InlineData(@"\\server")]           // not a share
    public void A_buffer_folder_that_is_not_fully_qualified_is_refused(string? folder)
        => Assert.Equal(FolderProblem.NotFullyQualified, PathRules.BufferFolderProblem(folder, Install));

    [Theory]
    [InlineData(@"D:\")]
    [InlineData(@"d:\")]
    [InlineData("D:/")]
    [InlineData(@"D:\\")]
    [InlineData(@"\\server\share")]     // the root of a network share
    [InlineData(@"\\server\share\")]
    [InlineData(@"\\SERVER\Share\\")]
    public void A_buffer_folder_that_is_a_drive_root_is_refused(string folder)
        => Assert.Equal(FolderProblem.DriveRoot, PathRules.BufferFolderProblem(folder, Install));

    [Theory]
    [InlineData(@"D:\WKI_Clipper\buffer")]
    [InlineData(@"D:\WKI_Clipper\buffer\")]
    [InlineData("D:/WKI_Clipper/buffer")]
    [InlineData(@"E:\b")]
    [InlineData(@"\\server\share\buffer")]
    [InlineData(@"%LOCALAPPDATA%\WKI_Clipper\buffer")]
    [InlineData(@"D:\a\..\b")]          // fully qualified, just not normalized
    public void A_full_path_below_a_drive_is_accepted(string folder)
        => Assert.Equal(FolderProblem.None, PathRules.BufferFolderProblem(folder, Install));

    [Fact]
    public void The_unnormalized_dotdot_path_is_judged_by_what_it_resolves_to()
    {
        Assert.Equal(FolderProblem.DriveRoot, PathRules.BufferFolderProblem(@"D:\a\..", Install));
        Assert.Equal(FolderProblem.InstallDirectory, PathRules.BufferFolderProblem(@"D:\x\..\WKI Clipper\buffer", Install));
    }

    // ---- the program's own folder, for all three folders ----

    public static readonly TheoryData<string> InsideTheInstallFolder = new()
    {
        @"D:\WKI Clipper",
        @"D:\WKI Clipper\",
        @"d:\wki clipper",                  // case
        @"D:\WKI CLIPPER\\",                // trailing separators
        "D:/WKI Clipper",                   // forward slash
        @"D:\WKI Clipper\buffer",
        @"D:\WKI Clipper\Assets\ffmpeg",
        @"D:\WKI Clipper\Arma Reforger",    // a per-game clips folder, as in the incident
        @"D:\x\..\WKI Clipper\Clips",
    };

    public static readonly TheoryData<string> OutsideTheInstallFolder = new()
    {
        @"D:\WKI Clipper2",                 // sibling with the same prefix
        @"D:\WKI Clipper2\buffer",
        @"D:\WKI",
        @"D:\WKI_Clipper\Clips",
        @"E:\WKI Clipper",                  // same name, other drive
        @"D:\",
        @"D:\Other\WKI Clipper",
    };

    [Theory]
    [MemberData(nameof(InsideTheInstallFolder))]
    public void The_buffer_folder_must_not_be_the_install_folder_or_inside_it(string folder)
        => Assert.Equal(FolderProblem.InstallDirectory, PathRules.BufferFolderProblem(folder, Install));

    [Theory]
    [MemberData(nameof(InsideTheInstallFolder))]
    public void Clips_and_screenshots_folders_must_not_be_the_install_folder_or_inside_it_either(string folder)
        => Assert.Equal(FolderProblem.InstallDirectory, PathRules.OutputFolderProblem(folder, Install));

    [Theory]
    [MemberData(nameof(OutsideTheInstallFolder))]
    public void Siblings_and_other_drives_are_not_the_install_folder(string folder)
    {
        // The buffer additionally refuses a drive root; compare only the install-folder rule here.
        Assert.NotEqual(FolderProblem.InstallDirectory, PathRules.BufferFolderProblem(folder, Install));
        Assert.Equal(FolderProblem.None, PathRules.OutputFolderProblem(folder, Install));
    }

    [Theory]
    [InlineData("D:")]
    [InlineData("clips")]
    [InlineData("")]
    [InlineData(@"\\server")]
    public void Clips_and_screenshots_folders_must_be_fully_qualified_too(string folder)
        => Assert.Equal(FolderProblem.NotFullyQualified, PathRules.OutputFolderProblem(folder, Install));

    [Fact]
    public void Clips_may_sit_in_a_drive_root_unlike_the_buffer()
    {
        Assert.Equal(FolderProblem.None, PathRules.OutputFolderProblem(@"D:\", Install));
        Assert.Equal(FolderProblem.DriveRoot, PathRules.BufferFolderProblem(@"D:\", Install));
    }

    [Fact]
    public void Without_an_install_folder_only_the_shape_rules_apply()
    {
        Assert.Equal(FolderProblem.None, PathRules.BufferFolderProblem(@"D:\WKI Clipper\buffer", null));
        Assert.Equal(FolderProblem.None, PathRules.BufferFolderProblem(@"D:\WKI Clipper\buffer", ""));
        Assert.Equal(FolderProblem.NotFullyQualified, PathRules.BufferFolderProblem("D:", null));
    }

    [Fact]
    public void The_install_folder_is_the_folder_of_the_running_program()
        => Assert.Equal(AppContext.BaseDirectory, PathRules.InstallDirectory);

    [Fact]
    public void The_default_folders_are_outside_a_normal_install_folder()
    {
        var install = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? @"C:\Users\x\AppData\Local",
                                   "Programs", "WKI Clipper");
        var o = new OutputSettings();
        Assert.Equal(FolderProblem.None, PathRules.BufferFolderProblem(o.BufferFolder, install));
        Assert.Equal(FolderProblem.None, PathRules.OutputFolderProblem(o.ClipsFolder, install));
        Assert.Equal(FolderProblem.None, PathRules.OutputFolderProblem(o.ScreenshotsFolder, install));
    }

    // ---- the runtime fallback ----

    private const string Fallback = PathRules.DefaultBufferFolder;

    [Theory]
    [InlineData("D:")]
    [InlineData(@"D:\")]
    [InlineData("D:foo")]
    [InlineData("buffer")]
    [InlineData("")]
    [InlineData(@"\\server\share")]
    [InlineData(@"D:\WKI Clipper")]
    [InlineData(@"D:\WKI Clipper\buffer")]
    public void An_unusable_buffer_folder_falls_back_to_the_default_for_the_run(string buffer)
        => Assert.Equal(Fallback, PathRules.SafeBufferFolder(buffer, @"C:\Clips", @"C:\Shots", Fallback, Install));

    [Fact]
    public void A_usable_buffer_folder_is_kept_as_configured()
    {
        const string buffer = @"D:\WKI_Clipper\buffer";
        Assert.Same(buffer, PathRules.SafeBufferFolder(buffer, @"C:\Clips", @"C:\Shots", Fallback, Install));
    }

    [Fact]
    public void The_shape_rules_also_apply_to_the_old_four_argument_call()
        => Assert.Equal(Fallback, PathRules.SafeBufferFolder("D:", @"C:\Clips", @"C:\Shots", Fallback));

    // ---- what ffmpeg and the clipper both see ----

    [Fact]
    public void Expanding_a_folder_always_gives_a_fully_qualified_path()
    {
        foreach (var raw in new[] { "D:", @"D:foo", "buffer", @"..\x", @"%TEMP%\x", @"D:\a\..\b", "D:/x" })
        {
            var full = SettingsService.ExpandPath(raw);
            Assert.True(Path.IsPathFullyQualified(full), $"'{raw}' expanded to the relative '{full}'");
        }
    }

    [Fact]
    public void Expanding_leaves_an_absolute_path_alone()
        => Assert.Equal(@"D:\WKI_Clipper\buffer", SettingsService.ExpandPath(@"D:\WKI_Clipper\buffer"));

    [Fact]
    public void Expanding_expands_variables()
    {
        var name = "WKI_FOLDERRULES_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, @"E:\Base");
        try { Assert.Equal(@"E:\Base\buffer", SettingsService.ExpandPath($@"%{name}%\buffer")); }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Fact]
    public void Expanding_an_empty_path_does_not_throw()
        => Assert.Equal("", SettingsService.ExpandPath(""));

    [Theory]
    [InlineData("D:")]
    [InlineData(@"D:\")]
    [InlineData("buffer")]
    [InlineData("")]
    [InlineData("%WKI_SURELY_UNDEFINED_VARIABLE%")]
    public void The_service_never_hands_ffmpeg_an_unusable_or_relative_buffer_folder(string configured)
    {
        var dir = Path.Combine(Path.GetTempPath(), "wki_folderrules_" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new SettingsService(dir);
            settings.Current.Output.BufferFolder = configured;
            using var buffer = new ReplayBufferService(settings);

            var resolved = (string)typeof(ReplayBufferService)
                .GetMethod("ResolveBufferDir", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(buffer, new object[] { false })!;

            Assert.True(Path.IsPathFullyQualified(resolved), resolved);
            Assert.Equal(SettingsService.ExpandPath(PathRules.DefaultBufferFolder), resolved);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void A_buffer_folder_inside_the_running_programs_own_folder_is_replaced_by_the_default()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wki_folderrules_" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new SettingsService(dir);
            settings.Current.Output.BufferFolder = Path.Combine(AppContext.BaseDirectory, "buffer");
            using var buffer = new ReplayBufferService(settings);

            var resolved = (string)typeof(ReplayBufferService)
                .GetMethod("ResolveBufferDir", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(buffer, new object[] { false })!;

            Assert.Equal(SettingsService.ExpandPath(PathRules.DefaultBufferFolder), resolved);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void The_segment_pattern_is_always_fully_qualified_and_names_the_generation()
    {
        foreach (var dir in new[] { @"D:\WKI_Clipper\buffer", "buffer", "D:", @"..\buffer" })
        {
            var pattern = ReplayBufferService.SegmentPatternFor(dir, 3);
            Assert.True(Path.IsPathFullyQualified(pattern), pattern);
            Assert.EndsWith("seg_3_%02d.mp4", pattern);
        }
        Assert.Equal(@"D:\WKI_Clipper\buffer\seg_0_%02d.mp4", ReplayBufferService.SegmentPatternFor(@"D:\WKI_Clipper\buffer", 0));
    }

    // ---- suggestions and texts ----

    [Theory]
    [InlineData("buffer", "D:", @"D:\WKI_Clipper\buffer")]
    [InlineData("Clips", @"d:\WKI Clipper", @"D:\WKI_Clipper\Clips")]
    [InlineData("Screenshots", @"E:\WKI Clipper\Shots", @"E:\WKI_Clipper\Screenshots")]
    [InlineData("buffer", "", @"<drive>:\WKI_Clipper\buffer")]
    [InlineData("buffer", "buffer", @"<drive>:\WKI_Clipper\buffer")]
    [InlineData("buffer", null, @"<drive>:\WKI_Clipper\buffer")]
    public void The_suggestion_keeps_the_drive_the_user_typed(string leaf, string? typed, string expected)
        => Assert.Equal(expected, PathRules.SuggestFolder(leaf, typed, "<drive>"));

    [Fact]
    public void The_install_folder_refusal_says_why_and_suggests_another_folder()
    {
        var text = FolderMessages.Refusal(FolderProblem.InstallDirectory, "Clips", @"D:\WKI Clipper");
        Assert.Contains("Bitte nicht den Programmordner verwenden — Updates und Deinstallation arbeiten dort.", text);
        Assert.Contains(@"D:\WKI_Clipper\Clips", text);
    }

    [Fact]
    public void The_bare_drive_refusal_suggests_a_folder_on_that_drive()
    {
        var text = FolderMessages.Refusal(FolderProblem.NotFullyQualified, "buffer", "D:");
        Assert.Contains(@"D:\WKI_Clipper\buffer", text);
    }

    [Fact]
    public void The_drive_root_refusal_suggests_a_subfolder()
    {
        var text = FolderMessages.Refusal(FolderProblem.DriveRoot, "buffer", @"D:\");
        Assert.Contains(@"D:\WKI_Clipper\buffer", text);
    }

    [Fact]
    public void A_clips_folder_that_already_sits_in_the_install_folder_gets_a_standing_warning_that_moves_nothing()
    {
        var text = FolderMessages.StandingWarning(FolderProblem.InstallDirectory, "Clips", @"D:\WKI Clipper\Arma", Fallback)!;
        Assert.Contains("Programmordner", text);
        Assert.Contains(@"D:\WKI_Clipper\Clips", text);
        Assert.Contains("nichts verschoben", text);
    }

    [Fact]
    public void A_saved_buffer_folder_that_cannot_be_used_says_where_the_buffer_runs_instead()
    {
        var text = FolderMessages.StandingWarning(FolderProblem.NotFullyQualified, "buffer", "D:", Fallback)!;
        Assert.Contains("Standardordner", text);
        Assert.Contains(Fallback, text);
    }

    [Fact]
    public void A_fine_folder_has_no_warning()
        => Assert.Null(FolderMessages.StandingWarning(FolderProblem.None, "buffer", @"D:\ok", Fallback));
}
