using System;
using System.IO;
using Microsoft.Win32;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>The recorded buffer folder, against a throw-away registry root (never the real HKCU\Software\WKI_Clipper).</summary>
public sealed class BufferFolderRecordTests : IDisposable
{
    private readonly string _rootPath = @"Software\WKI_Clipper_Tests_BufferFolder_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;
    private readonly BufferFolderRecord _record;

    public BufferFolderRecordTests()
    {
        _root = Registry.CurrentUser.CreateSubKey(_rootPath);
        _record = new BufferFolderRecord(_root);
    }

    public void Dispose()
    {
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_rootPath, throwOnMissingSubKey: false);
    }

    [Fact]
    public void Missing_record_reads_as_null()
        => Assert.Null(_record.Read());

    [Fact]
    public void Write_then_read_round_trips()
    {
        _record.Write(@"D:\Arma\buffer");
        Assert.Equal(@"D:\Arma\buffer", _record.Read());
    }

    [Fact]
    public void Write_overwrites_the_previous_folder()
    {
        _record.Write(@"C:\a");
        _record.Write(@"D:\b");
        Assert.Equal(@"D:\b", _record.Read());
    }

    [Fact]
    public void A_blank_value_reads_as_null()
    {
        using (var k = _root.CreateSubKey(BufferFolderRecord.KeyPath))
            k.SetValue(BufferFolderRecord.ValueName, "   ");
        Assert.Null(_record.Read());
    }

    [Fact]
    public void The_record_lives_where_the_uninstaller_looks()
    {
        Assert.Equal(@"Software\WKI_Clipper", BufferFolderRecord.KeyPath);
        Assert.Equal("BufferFolder", BufferFolderRecord.ValueName);
        Assert.Equal("PendingCleanup", BufferFolderRecord.PendingValueName);
    }

    // ---- folders whose cleanup has not finished (the app was closed, a save outlasted the wait) ----

    [Fact]
    public void No_pending_folders_by_default()
        => Assert.Empty(_record.ReadPending());

    [Fact]
    public void Pending_folders_round_trip_in_order_without_duplicates()
    {
        _record.AddPending(@"C:\old1");
        _record.AddPending(@"D:\old2");
        _record.AddPending(@"c:\OLD1");   // same folder, other spelling

        Assert.Equal(new[] { @"C:\old1", @"D:\old2" }, _record.ReadPending());
    }

    [Fact]
    public void A_pending_folder_is_removed_by_any_spelling_and_the_rest_stays()
    {
        _record.AddPending(@"C:\old1");
        _record.AddPending(@"D:\old2");

        _record.RemovePending(@"c:\old1\");

        Assert.Equal(new[] { @"D:\old2" }, _record.ReadPending());
    }

    [Fact]
    public void Removing_the_last_pending_folder_leaves_no_value_behind()
    {
        _record.AddPending(@"C:\old1");
        _record.RemovePending(@"C:\old1");

        using var k = _root.OpenSubKey(BufferFolderRecord.KeyPath);
        Assert.Null(k?.GetValue(BufferFolderRecord.PendingValueName));
        Assert.Empty(_record.ReadPending());
    }

    [Fact]
    public void The_pending_list_does_not_disturb_the_recorded_folder()
    {
        _record.Write(@"D:\current");
        _record.AddPending(@"C:\old1");
        Assert.Equal(@"D:\current", _record.Read());
    }

    [Fact]
    public void Pending_folders_are_stored_as_one_pipe_separated_string_for_the_uninstaller()
    {
        // '|' cannot occur in a Windows path, and Inno Setup reads a plain string value easily.
        _record.AddPending(@"C:\old1");
        _record.AddPending(@"D:\old2");

        using var k = _root.OpenSubKey(BufferFolderRecord.KeyPath);
        Assert.Equal(@"C:\old1|D:\old2", k!.GetValue(BufferFolderRecord.PendingValueName));
    }
}

public sealed class BufferFolderRulesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_bufrules_" + Guid.NewGuid().ToString("N"));

    public BufferFolderRulesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(@"C:\buf\", @"C:\buf", false)]
    [InlineData(@"C:\BUF", @"c:\buf", false)]
    [InlineData(@"C:\buf", @"D:\buf", true)]
    [InlineData(@"C:\buf", @"C:\buf2", true)]
    [InlineData(@"C:\buf\sub", @"C:\buf", true)]
    [InlineData(null, @"C:\buf", false)]
    [InlineData("", @"C:\buf", false)]
    [InlineData("   ", @"C:\buf", false)]
    public void Folder_comparison_ignores_case_and_trailing_separators(string? recorded, string current, bool different)
        => Assert.Equal(different, BufferFolderRules.IsDifferentFolder(recorded, current));

    [Theory]
    [InlineData("seg_0_00.mp4", true)]
    [InlineData("seg_12_374.mp4", true)]
    [InlineData("SEG_3_01.MP4", true)]
    [InlineData("_concat_list.txt", true)]
    [InlineData("_chapters.txt", true)]
    [InlineData("_gif_src.mp4", true)]
    [InlineData("_gif_list.txt", true)]
    [InlineData("seg_a_1.mp4", false)]
    [InlineData("seg_1.mp4", false)]
    [InlineData("seg_1_2.mp4.part", false)]
    [InlineData("holiday.mp4", false)]
    [InlineData("Clip_2026-10-17_11-00-00.mp4", false)]
    [InlineData("notes.txt", false)]
    [InlineData("", false)]
    public void Only_our_own_names_are_ours(string name, bool own)
        => Assert.Equal(own, BufferFolderRules.IsOwnBufferFile(name));

    [Fact]
    public void Delete_removes_only_own_files_and_leaves_directories_alone()
    {
        File.WriteAllText(Path.Combine(_dir, "seg_1_00.mp4"), "x");
        File.WriteAllText(Path.Combine(_dir, "seg_1_01.mp4"), "x");
        File.WriteAllText(Path.Combine(_dir, "_concat_list.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "holiday.mp4"), "x");
        File.WriteAllText(Path.Combine(_dir, "seg_a_1.mp4"), "x");
        var sub = Directory.CreateDirectory(Path.Combine(_dir, "seg_9_99.mp4")).FullName;   // a DIRECTORY with a segment name
        File.WriteAllText(Path.Combine(sub, "seg_1_00.mp4"), "x");

        int n = BufferFolderRules.DeleteOwnFiles(_dir);

        Assert.Equal(3, n);
        Assert.True(File.Exists(Path.Combine(_dir, "holiday.mp4")));
        Assert.True(File.Exists(Path.Combine(_dir, "seg_a_1.mp4")));
        Assert.True(Directory.Exists(sub));
        Assert.True(File.Exists(Path.Combine(sub, "seg_1_00.mp4")));   // no recursion
        Assert.False(File.Exists(Path.Combine(_dir, "seg_1_00.mp4")));
    }

    [Fact]
    public void Own_files_are_found_in_a_folder_even_when_only_a_helper_file_is_left()
    {
        Assert.False(BufferFolderRules.HasOwnFiles(_dir));
        File.WriteAllText(Path.Combine(_dir, "holiday.mp4"), "x");
        Assert.False(BufferFolderRules.HasOwnFiles(_dir));
        File.WriteAllText(Path.Combine(_dir, "_chapters.txt"), "x");
        Assert.True(BufferFolderRules.HasOwnFiles(_dir));
        Assert.False(BufferFolderRules.HasOwnFiles(Path.Combine(_dir, "nope")));
        Assert.False(BufferFolderRules.HasOwnFiles(null));
    }

    [Fact]
    public void Delete_in_a_missing_folder_is_a_no_op()
        => Assert.Equal(0, BufferFolderRules.DeleteOwnFiles(Path.Combine(_dir, "nope")));

    [Fact]
    public void Delete_with_a_blank_path_is_a_no_op()
    {
        Assert.Equal(0, BufferFolderRules.DeleteOwnFiles(null));
        Assert.Equal(0, BufferFolderRules.DeleteOwnFiles("  "));
    }

    [Fact]
    public void A_locked_file_does_not_stop_the_rest()
    {
        var locked = Path.Combine(_dir, "seg_1_00.mp4");
        File.WriteAllText(locked, "x");
        File.WriteAllText(Path.Combine(_dir, "seg_1_01.mp4"), "x");
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            int n = BufferFolderRules.DeleteOwnFiles(_dir);
            Assert.Equal(1, n);
            Assert.True(File.Exists(locked));
        }
    }
}
