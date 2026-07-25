using System;
using System.IO;
using System.Linq;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class CrosshairSeedingTests : IDisposable
{
    private readonly string _lib;
    private readonly string _defaults;

    public CrosshairSeedingTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "wki_seed_" + Guid.NewGuid().ToString("N"));
        _lib = Path.Combine(root, "library");
        _defaults = Path.Combine(root, "defaults");
        Directory.CreateDirectory(_lib);
        Directory.CreateDirectory(_defaults);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_lib)!, recursive: true); } catch { }
    }

    private void MakeDefault(string name, byte marker = 1)
        => File.WriteAllBytes(Path.Combine(_defaults, name),
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, marker });

    [Fact]
    public void First_run_seeds_the_bundled_crosshairs()
    {
        MakeDefault("Kreuz.png");
        MakeDefault("Punkt.png");

        var svc = new CrosshairLibraryService(_lib, _defaults);

        Assert.Equal(2, svc.Entries.Count);
        Assert.Contains(svc.Entries, e => e.FileName == "Kreuz.png");
        Assert.True(File.Exists(Path.Combine(_lib, ".defaults-seeded")));
    }

    [Fact]
    public void Seeding_runs_only_once_deleted_defaults_do_not_return()
    {
        MakeDefault("a.png");
        MakeDefault("b.png");

        var first = new CrosshairLibraryService(_lib, _defaults);
        Assert.Equal(2, first.Entries.Count);
        first.Dispose();

        // User deletes one in Explorer.
        File.Delete(Path.Combine(_lib, "a.png"));

        var second = new CrosshairLibraryService(_lib, _defaults);
        Assert.Single(second.Entries);                       // a.png must NOT be re-seeded
        Assert.False(File.Exists(Path.Combine(_lib, "a.png")));
    }

    [Fact]
    public void Seeding_never_overwrites_a_users_own_file()
    {
        // User already has Kreuz.png with their own content.
        File.WriteAllBytes(Path.Combine(_lib, "Kreuz.png"),
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 99 });
        MakeDefault("Kreuz.png", marker: 7);   // different content in the bundle

        var svc = new CrosshairLibraryService(_lib, _defaults);

        var bytes = File.ReadAllBytes(Path.Combine(_lib, "Kreuz.png"));
        Assert.Equal(99, bytes[^1]);           // user's content survived
        Assert.Single(svc.Entries);
    }

    [Fact]
    public void Missing_defaults_folder_is_not_fatal_and_still_marks_seeded()
    {
        Directory.Delete(_defaults, recursive: true);

        var svc = new CrosshairLibraryService(_lib, _defaults);

        Assert.Empty(svc.Entries);
        Assert.True(File.Exists(Path.Combine(_lib, ".defaults-seeded")));
    }

    [Fact]
    public void Marker_file_is_not_indexed_as_a_crosshair()
    {
        MakeDefault("only.png");
        var svc = new CrosshairLibraryService(_lib, _defaults);

        Assert.Single(svc.Entries);            // just the png, not the marker
        Assert.DoesNotContain(svc.Entries, e => e.FileName.Contains("seeded"));
    }
}
