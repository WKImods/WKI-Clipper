using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class RecycleBinTests
{
    // ---- the result table ----

    [Theory]
    [InlineData(0, false, false, true, RecycleOutcome.Recycled)]
    [InlineData(0, true, false, true, RecycleOutcome.Declined)]    // the user declined "delete permanently?"
    [InlineData(0, true, true, true, RecycleOutcome.Declined)]
    [InlineData(0, false, true, true, RecycleOutcome.Failed)]      // "success" but the file is still there
    [InlineData(32, false, true, true, RecycleOutcome.InUse)]
    [InlineData(32, true, true, true, RecycleOutcome.InUse)]
    [InlineData(1, false, true, true, RecycleOutcome.Failed)]
    [InlineData(0x71, false, true, true, RecycleOutcome.Failed)]
    [InlineData(-1, false, false, true, RecycleOutcome.Failed)]
    // the file is gone but the bin did not grow: the user confirmed "delete permanently?" - never "in the bin"
    [InlineData(0, false, false, false, RecycleOutcome.DeletedPermanently)]
    // the bin could not be counted afterwards: gone, but not provably in the bin
    [InlineData(0, false, false, null, RecycleOutcome.DeletedPermanently)]
    // the bin count only matters for a file that is gone
    [InlineData(0, false, true, false, RecycleOutcome.Failed)]
    [InlineData(0, true, true, false, RecycleOutcome.Declined)]
    [InlineData(32, false, true, false, RecycleOutcome.InUse)]
    public void The_return_code_the_abort_flag_the_file_and_the_bin_decide_the_outcome(int rc, bool aborted, bool existsAfter, bool? binGrew, RecycleOutcome expected)
        => Assert.Equal(expected, RecycleBin.Interpret(rc, aborted, existsAfter, binGrew));

    // ---- the retry while the file is in use (editor and gallery) ----

    [Fact]
    public void The_retry_is_short_and_backs_off()
    {
        var delays = RecycleBin.RetryDelays;

        Assert.NotEmpty(delays);
        Assert.Equal(delays.OrderBy(d => d), delays);
        Assert.True(delays.Aggregate(TimeSpan.Zero, (sum, d) => sum + d) <= TimeSpan.FromSeconds(5));
    }

    // ---- which drives have a Recycle Bin ----

    [Theory]
    [InlineData(DriveType.Fixed, true)]
    [InlineData(DriveType.Removable, false)]      // USB stick: Windows would ask "delete permanently?"
    [InlineData(DriveType.CDRom, false)]
    [InlineData(DriveType.Ram, false)]
    [InlineData(DriveType.Network, false)]
    [InlineData(DriveType.NoRootDirectory, false)]
    [InlineData(DriveType.Unknown, false)]
    public void Only_a_fixed_drive_has_a_recycle_bin(DriveType type, bool expected)
        => Assert.Equal(expected, RecycleBin.DriveHasRecycleBin(type));

    // ---- the bin's own settings: refuse what Windows would delete for good ----

    [Theory]
    [InlineData(1_000_000L, 100_000L, false, true)]               // 1 MB into a 100 000 MB bin
    [InlineData(5_000_000_000L, 100_000L, false, true)]
    [InlineData(5_000_000_000L, 4_000L, false, false)]            // 4.77 GB into a 4 000 MB bin: too big
    [InlineData(4_000L * 1024 * 1024, 4_000L, false, true)]       // exactly the capacity still fits
    [InlineData(4_000L * 1024 * 1024 + 1, 4_000L, false, false)]
    [InlineData(1_000_000L, 100_000L, true, false)]               // "Don't move files to the Recycle Bin"
    [InlineData(1_000_000L, null, true, false)]
    [InlineData(5_000_000_000L, null, false, true)]               // no limit known: leave it to the outcome check
    [InlineData(5_000_000_000L, 0L, false, true)]                 // 0 = not a real limit
    public void A_file_is_only_recycled_when_the_bin_is_on_and_big_enough(long fileBytes, long? maxMegabytes, bool nukeOnDelete, bool expected)
        => Assert.Equal(expected, RecycleBin.FitsInBin(fileBytes, maxMegabytes, nukeOnDelete));

    [Fact]
    public void The_volume_guid_is_read_from_the_mount_point_name()
    {
        Assert.Equal("{8fd34892-c82b-4acf-b629-f53bc4893109}",
                     RecycleBin.VolumeGuidOf(@"\\?\Volume{8fd34892-c82b-4acf-b629-f53bc4893109}\"));
        Assert.Null(RecycleBin.VolumeGuidOf(@"C:\"));
        Assert.Null(RecycleBin.VolumeGuidOf(""));
        Assert.Null(RecycleBin.VolumeGuidOf(null));
    }

    [Fact]
    public void The_bin_of_the_system_drive_can_be_counted_and_a_missing_drive_cannot()
    {
        // guards the SHQUERYRBINFO layout: with a wrong cbSize the shell answers E_INVALIDARG, which reads as "no bin"
        Assert.NotNull(RecycleBin.BinItemCount(Path.GetPathRoot(Environment.SystemDirectory)!));

        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        char free = "ZYXWVUTSRQPONMLKJIHGFED".First(c => !used.Contains(c));
        Assert.Null(RecycleBin.BinItemCount(free + @":\"));
    }

    [Fact]
    public void The_settings_of_the_system_drive_can_be_read_without_throwing()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory)!;

        var ex = Record.Exception(() => RecycleBin.ReadVolumeSettings(root));

        Assert.Null(ex);
    }

    // ---- Send: the order of the steps, with the shell replaced ----

    private sealed class TempFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wki_recycle_send_" + Guid.NewGuid().ToString("N") + ".tmp");
        public TempFile() => File.WriteAllText(Path, "x");
        public void Dispose() { try { File.Delete(Path); } catch { } }
    }

    private sealed class FakeShell
    {
        public long Items = 10;
        public int Deletes;
        public bool CountFails, CountFailsAfter;
        public int ReturnCode;
        public bool Aborted, Recycles = true;

        public long? Count(string root)
        {
            if (CountFails || (CountFailsAfter && Deletes > 0)) return null;
            return Items;
        }

        public RecycleBin.ShellDeleteResult Delete(string path)
        {
            Deletes++;
            if (ReturnCode == 0 && !Aborted)
            {
                File.Delete(path);
                if (Recycles) Items++;
            }
            return new RecycleBin.ShellDeleteResult(ReturnCode, Aborted);
        }
    }

    private static RecycleOutcome Send(TempFile file, FakeShell shell, RecycleBin.VolumeSettings? settings = null)
        => RecycleBin.Send(file.Path, shell.Count, shell.Delete, _ => settings ?? new RecycleBin.VolumeSettings(null, false));

    [Fact]
    public void A_file_that_lands_in_the_bin_is_recycled()
    {
        using var file = new TempFile();
        var shell = new FakeShell();

        Assert.Equal(RecycleOutcome.Recycled, Send(file, shell));
        Assert.False(File.Exists(file.Path));
        Assert.Equal(1, shell.Deletes);
    }

    [Fact]
    public void A_file_that_vanishes_without_the_bin_growing_was_deleted_for_good()
    {
        using var file = new TempFile();
        var shell = new FakeShell { Recycles = false };      // the user answered "Yes" to "delete permanently?"

        Assert.Equal(RecycleOutcome.DeletedPermanently, Send(file, shell));
        Assert.False(File.Exists(file.Path));
    }

    [Fact]
    public void If_the_bin_cannot_be_counted_afterwards_the_file_is_not_claimed_to_be_in_it()
    {
        using var file = new TempFile();
        var shell = new FakeShell { CountFailsAfter = true };

        Assert.Equal(RecycleOutcome.DeletedPermanently, Send(file, shell));
    }

    [Fact]
    public void If_the_bin_cannot_be_counted_beforehand_the_file_is_not_touched()
    {
        using var file = new TempFile();
        var shell = new FakeShell { CountFails = true };

        Assert.Equal(RecycleOutcome.NotSupported, Send(file, shell));
        Assert.True(File.Exists(file.Path));
        Assert.Equal(0, shell.Deletes);
    }

    [Theory]
    [InlineData(true, null)]        // the bin is switched off for that drive
    [InlineData(false, 1L)]         // the bin holds 1 MB at most and the file is 2 MB
    public void A_bin_that_would_delete_for_good_is_refused_before_the_shell_is_asked(bool nukeOnDelete, long? maxMegabytes)
    {
        using var file = new TempFile();
        File.WriteAllBytes(file.Path, new byte[2 * 1024 * 1024]);   // 2 MB
        var shell = new FakeShell();

        var outcome = Send(file, shell, new RecycleBin.VolumeSettings(maxMegabytes, nukeOnDelete));

        Assert.Equal(RecycleOutcome.NotSupported, outcome);
        Assert.True(File.Exists(file.Path));
        Assert.Equal(0, shell.Deletes);
    }

    [Fact]
    public void A_user_who_declines_the_permanent_delete_keeps_the_file()
    {
        using var file = new TempFile();
        var shell = new FakeShell { Aborted = true };

        Assert.Equal(RecycleOutcome.Declined, Send(file, shell));
        Assert.True(File.Exists(file.Path));
    }

    [Fact]
    public void A_sharing_violation_is_in_use_and_the_file_stays()
    {
        using var file = new TempFile();
        var shell = new FakeShell { ReturnCode = 32 };

        Assert.Equal(RecycleOutcome.InUse, Send(file, shell));
        Assert.True(File.Exists(file.Path));
    }

    [Fact]
    public void A_shell_that_throws_is_a_failed_outcome_not_an_exception()
    {
        using var file = new TempFile();

        var outcome = RecycleBin.Send(file.Path, _ => 5L, _ => throw new InvalidOperationException("boom"),
                                      _ => new RecycleBin.VolumeSettings(null, false));

        Assert.Equal(RecycleOutcome.Failed, outcome);
        Assert.True(File.Exists(file.Path));
    }

    // ---- Send: two deletes at once (the gallery's own threads, the editor's UI thread) ----

    private static readonly Func<string, RecycleBin.VolumeSettings> NoLimits = _ => new RecycleBin.VolumeSettings(null, false);

    [Fact]
    public void Another_file_recycled_meanwhile_does_not_make_a_permanent_delete_look_recycled()
    {
        // A waits in Windows' "delete permanently?" and the user says yes; B, on the same drive, goes to the bin meanwhile.
        // The count around A's shell call must not take B's item for A's.
        using var a = new TempFile();
        using var b = new TempFile();
        long items = 10;
        long? Count(string _) => Interlocked.Read(ref items);
        using var aAsks = new ManualResetEventSlim();
        using var userSaysYes = new ManualResetEventSlim();
        using var bDone = new ManualResetEventSlim();
        RecycleOutcome aOutcome = RecycleOutcome.Failed, bOutcome = RecycleOutcome.Failed;

        var aThread = new Thread(() => aOutcome = RecycleBin.Send(a.Path, Count, full =>
        {
            aAsks.Set();
            userSaysYes.Wait(TimeSpan.FromSeconds(10));
            File.Delete(full);                                      // gone for good, the bin does not grow
            return new RecycleBin.ShellDeleteResult(0, false);
        }, NoLimits));
        var bThread = new Thread(() =>
        {
            bOutcome = RecycleBin.Send(b.Path, Count, full =>
            {
                File.Delete(full);
                Interlocked.Increment(ref items);                   // into the bin
                return new RecycleBin.ShellDeleteResult(0, false);
            }, NoLimits);
            bDone.Set();
        });

        aThread.Start();
        Assert.True(aAsks.Wait(TimeSpan.FromSeconds(10)));
        bThread.Start();
        bDone.Wait(TimeSpan.FromMilliseconds(300));                 // B's whole try fits in here unless it has to wait for A
        userSaysYes.Set();
        Assert.True(aThread.Join(TimeSpan.FromSeconds(10)));
        Assert.True(bThread.Join(TimeSpan.FromSeconds(10)));

        Assert.Equal(RecycleOutcome.DeletedPermanently, aOutcome);
        Assert.Equal(RecycleOutcome.Recycled, bOutcome);
        Assert.Equal(11, Interlocked.Read(ref items));
    }

    [Fact]
    public async Task A_file_in_use_does_not_hold_up_another_delete_while_it_waits_for_its_next_try()
    {
        using var a = new TempFile();
        using var b = new TempFile();
        var shell = new FakeShell();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = RecycleBin.RetryWhileInUseAsync(
            () => Task.Run(() => RecycleBin.Send(a.Path, shell.Count, _ => new RecycleBin.ShellDeleteResult(32, false), NoLimits)),
            _ => { waiting.TrySetResult(); return release.Task; });
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var other = await Task.Run(() => Send(b, shell)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(RecycleOutcome.Recycled, other);
        Assert.False(File.Exists(b.Path));
        release.SetResult();
        Assert.Equal(RecycleOutcome.InUse, await first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(File.Exists(a.Path));
    }

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
