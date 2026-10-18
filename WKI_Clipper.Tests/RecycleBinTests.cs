using System;
using System.IO;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class RecycleBinTests
{
    // ---- the result table ----

    [Theory]
    [InlineData(0, false, false, RecycleOutcome.Recycled)]
    [InlineData(0, true, false, RecycleOutcome.Declined)]    // the user declined "delete permanently?"
    [InlineData(0, true, true, RecycleOutcome.Declined)]
    [InlineData(0, false, true, RecycleOutcome.Failed)]      // "success" but the file is still there
    [InlineData(32, false, true, RecycleOutcome.InUse)]
    [InlineData(32, true, true, RecycleOutcome.InUse)]
    [InlineData(1, false, true, RecycleOutcome.Failed)]
    [InlineData(0x71, false, true, RecycleOutcome.Failed)]
    [InlineData(-1, false, false, RecycleOutcome.Failed)]
    public void The_return_code_the_abort_flag_and_the_file_decide_the_outcome(int rc, bool aborted, bool existsAfter, RecycleOutcome expected)
        => Assert.Equal(expected, RecycleBin.Interpret(rc, aborted, existsAfter));

    // ---- which paths can be recycled ----

    [Theory]
    [InlineData(@"C:\Clips\a.mp4", true)]
    [InlineData(@"D:\Clips\Arma\a b.mp4", true)]
    [InlineData(@"\\server\share\a.mp4", false)]
    [InlineData(@"\\?\UNC\server\share\a.mp4", false)]
    [InlineData(@"\\?\C:\a.mp4", false)]
    [InlineData(@"Clips\a.mp4", false)]
    [InlineData(@"a.mp4", false)]
    [InlineData(@"C:a.mp4", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("   ", false)]
    public void Only_a_fully_qualified_local_drive_path_qualifies(string? path, bool expected)
    {
        // the drive letter itself may not exist on this machine; the rule under test is the shape of the path
        if (expected && path is not null && !Directory.Exists(Path.GetPathRoot(path))) return;
        Assert.Equal(expected, RecycleBin.IsLocalDrivePath(path));
    }

    [Fact]
    public void An_existing_local_file_is_supported_and_a_missing_one_is_not()
    {
        var path = Path.Combine(Path.GetTempPath(), "wki_recycle_support_" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Assert.False(RecycleBin.IsSupportedPath(path));
            File.WriteAllText(path, "x");
            Assert.True(RecycleBin.IsSupportedPath(path));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void A_path_that_cannot_be_recycled_is_not_touched()
    {
        Assert.Equal(RecycleOutcome.NotSupported, RecycleBin.Send(@"\\server\share\a.mp4"));
        Assert.Equal(RecycleOutcome.NotSupported, RecycleBin.Send(Path.Combine(Path.GetTempPath(), "wki_missing_" + Guid.NewGuid().ToString("N") + ".mp4")));
    }

    // ---- the real thing (needs a Recycle Bin on the temp drive; leaves one tiny file in it) ----

    [Fact]
    [Trait("Category", "Live")]
    public void A_real_file_leaves_its_folder_and_goes_to_the_recycle_bin()
    {
        if (Environment.GetEnvironmentVariable("WKI_LIVE") != "1") return;

        var path = Path.Combine(Path.GetTempPath(), "wki_test_recycle_" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(path, "recycle me");

        var outcome = RecycleBin.Send(path);

        Assert.Equal(RecycleOutcome.Recycled, outcome);
        Assert.False(File.Exists(path));
    }

    [Fact]
    [Trait("Category", "Live")]
    public void A_file_another_process_holds_open_without_delete_sharing_stays_where_it_is()
    {
        if (Environment.GetEnvironmentVariable("WKI_LIVE") != "1") return;

        var path = Path.Combine(Path.GetTempPath(), "wki_test_lock_" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(path, "locked");
        try
        {
            using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);   // no FileShare.Delete

            var outcome = RecycleBin.Send(path);

            Assert.Equal(RecycleOutcome.InUse, outcome);
            Assert.True(File.Exists(path));
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
