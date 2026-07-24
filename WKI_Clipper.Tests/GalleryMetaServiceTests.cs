using System;
using System.IO;
using System.Linq;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class GalleryMetaServiceTests : IDisposable
{
    private readonly string _path;

    public GalleryMetaServiceTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "wki_meta_" + Guid.NewGuid().ToString("N") + ".json");
    }

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    [Fact]
    public void ToggleFavorite_flips_and_reports_new_state()
    {
        var svc = new GalleryMetaService(_path);
        Assert.False(svc.IsFavorite("clip.mp4"));
        Assert.True(svc.ToggleFavorite("clip.mp4"));
        Assert.True(svc.IsFavorite("clip.mp4"));
        Assert.False(svc.ToggleFavorite("clip.mp4"));
        Assert.False(svc.IsFavorite("clip.mp4"));
    }

    [Fact]
    public void Tags_are_set_deduped_and_returned()
    {
        var svc = new GalleryMetaService(_path);
        svc.SetTags("clip.mp4", new[] { "Arma", "Funny", "arma", "  " });
        var tags = svc.GetTags("clip.mp4");
        Assert.Equal(2, tags.Count);
        Assert.Contains("Arma", tags);
        Assert.Contains("Funny", tags);
    }

    [Fact]
    public void AllTags_aggregates_across_files_sorted()
    {
        var svc = new GalleryMetaService(_path);
        svc.SetTags("a.mp4", new[] { "Zebra", "Alpha" });
        svc.SetTags("b.png", new[] { "Beta", "alpha" });
        var all = svc.AllTags();
        Assert.Equal(new[] { "Alpha", "Beta", "Zebra" }, all.ToArray());
    }

    [Fact]
    public void Persists_across_instances()
    {
        var a = new GalleryMetaService(_path);
        a.ToggleFavorite("keep.mp4");
        a.SetTags("keep.mp4", new[] { "clutch" });

        var b = new GalleryMetaService(_path);
        Assert.True(b.IsFavorite("keep.mp4"));
        Assert.Contains("clutch", b.GetTags("keep.mp4"));
    }

    [Fact]
    public void Corrupt_json_loads_empty_without_throwing()
    {
        File.WriteAllText(_path, "{ this is not valid json ]");
        var svc = new GalleryMetaService(_path);
        Assert.False(svc.IsFavorite("anything.mp4"));
        Assert.Empty(svc.AllTags());
    }

    [Fact]
    public void Clearing_favorite_and_tags_prunes_entry()
    {
        var svc = new GalleryMetaService(_path);
        svc.ToggleFavorite("x.mp4");
        svc.ToggleFavorite("x.mp4");      // back to false → entry pruned
        var reloaded = new GalleryMetaService(_path);
        Assert.False(reloaded.IsFavorite("x.mp4"));
    }
}
