using System;
using System.Diagnostics;
using System.IO;
using WKI_Clipper.Native;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>The read counter the stall watchdog uses as a sign of life (a +faststart rewrite reads, a hung encoder does not).</summary>
public sealed class ProcessIoTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "wki_io_" + Guid.NewGuid().ToString("N") + ".bin");

    public void Dispose()
    {
        try { File.Delete(_file); } catch { }
    }

    [Fact]
    public void Reading_a_file_raises_the_read_counter_of_the_process_by_at_least_its_size()
    {
        File.WriteAllBytes(_file, new byte[4 * 1024 * 1024]);
        using var me = Process.GetCurrentProcess();

        var before = ProcessIo.TryGetReadBytes(me);
        _ = File.ReadAllBytes(_file);
        var after = ProcessIo.TryGetReadBytes(me);

        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.True(after - before >= 4UL * 1024 * 1024, $"read counter went from {before} to {after}");
    }

    [Fact]
    public void Writing_does_not_count_as_reading()
    {
        using var me = Process.GetCurrentProcess();

        var before = ProcessIo.TryGetReadBytes(me);
        File.WriteAllBytes(_file, new byte[8 * 1024 * 1024]);   // ffmpeg's own progress lines are writes, too
        var after = ProcessIo.TryGetReadBytes(me);

        Assert.True(after - before < 1024 * 1024, $"read counter went from {before} to {after}");
    }

    [Fact]
    public void A_process_that_cannot_be_asked_gives_null_instead_of_throwing()
    {
        var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        p.Dispose();

        Assert.Null(ProcessIo.TryGetReadBytes(p));   // disposed: no handle any more
    }
}
