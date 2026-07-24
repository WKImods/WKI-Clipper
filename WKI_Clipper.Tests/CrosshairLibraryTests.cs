using System;
using System.IO;
using System.Linq;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class CrosshairLibraryTests : IDisposable
{
    private readonly string _dir;

    public CrosshairLibraryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wki_ch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Minimal valid-enough PNG payload — the library only needs a readable file.</summary>
    private string DropPng(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 });
        return path;
    }

    [Fact]
    public void Manually_dropped_png_is_picked_up()
    {
        DropPng("Green_Round.png");
        var svc = new CrosshairLibraryService(_dir);   // ctor scans

        Assert.Single(svc.Entries);
        Assert.Equal("Green_Round", svc.Entries[0].Name);
        Assert.Equal("Green_Round.png", svc.Entries[0].FileName);
    }

    [Fact]
    public void Scanning_twice_does_not_duplicate()
    {
        DropPng("dot.png");
        var svc = new CrosshairLibraryService(_dir);
        svc.ScanFolder();
        svc.ScanFolder();

        Assert.Single(svc.Entries);
    }

    [Fact]
    public void New_file_after_construction_is_found_by_scan()
    {
        var svc = new CrosshairLibraryService(_dir);
        Assert.Empty(svc.Entries);

        DropPng("later.png");
        int added = svc.ScanFolder();

        Assert.Equal(1, added);
        Assert.Contains(svc.Entries, e => e.FileName == "later.png");
    }

    [Fact]
    public void Deleted_file_is_dropped_from_the_index()
    {
        var path = DropPng("gone.png");
        var svc = new CrosshairLibraryService(_dir);
        Assert.Single(svc.Entries);

        File.Delete(path);
        svc.ScanFolder();

        Assert.Empty(svc.Entries);
    }

    [Fact]
    public void Non_png_files_are_ignored()
    {
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "hello");
        File.WriteAllText(Path.Combine(_dir, "cross.jpg"), "x");
        var svc = new CrosshairLibraryService(_dir);

        Assert.Empty(svc.Entries);
    }

    [Fact]
    public void Index_survives_a_restart_of_the_service()
    {
        DropPng("keep.png");
        var first = new CrosshairLibraryService(_dir);
        Assert.Single(first.Entries);
        first.Dispose();

        var second = new CrosshairLibraryService(_dir);
        Assert.Single(second.Entries);
        Assert.Equal("keep", second.Entries[0].Id);
    }

    [Fact]
    public void Import_copies_the_file_into_the_library()
    {
        var src = Path.Combine(Path.GetTempPath(), "wki_src_" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(src, new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2 });
        try
        {
            var svc = new CrosshairLibraryService(_dir);
            var entry = svc.Import(src);

            Assert.NotNull(entry);
            Assert.True(File.Exists(svc.FullPath(entry!)));
            Assert.Single(svc.Entries);

            // A later scan must not add the imported copy a second time.
            svc.ScanFolder();
            Assert.Single(svc.Entries);
        }
        finally { try { File.Delete(src); } catch { } }
    }

    [Fact]
    public void Remove_deletes_the_file_and_the_entry()
    {
        DropPng("bye.png");
        var svc = new CrosshairLibraryService(_dir);
        var id = svc.Entries[0].Id;

        svc.Remove(id);

        Assert.Empty(svc.Entries);
        Assert.False(File.Exists(Path.Combine(_dir, "bye.png")));
    }
}
