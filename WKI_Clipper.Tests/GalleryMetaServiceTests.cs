using System;
using System.IO;
using System.Linq;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class GalleryMetaServiceTests : IDisposable
{
    // A folder per test: the service writes siblings (.bak, .corrupt-*), which must not pile up in %TEMP%.
    private readonly string _dir;
    private readonly string _path;

    public GalleryMetaServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wki_meta_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "gallery-meta.json");   // the OLD file; the store is gallery-meta.v2.json
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void ToggleFavorite_flips_the_state_and_reports_that_it_was_saved()
    {
        var svc = new GalleryMetaService(_path);
        Assert.False(svc.IsFavorite("clip.mp4"));
        Assert.True(svc.ToggleFavorite("clip.mp4"));
        Assert.True(svc.IsFavorite("clip.mp4"));
        Assert.True(svc.ToggleFavorite("clip.mp4"));
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
        a.SetNote("keep.mp4", "cheater at the airfield");

        var b = new GalleryMetaService(_path);
        Assert.True(b.IsFavorite("keep.mp4"));
        Assert.Contains("clutch", b.GetTags("keep.mp4"));
        Assert.Equal("cheater at the airfield", b.GetNote("keep.mp4"));
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
        Assert.DoesNotContain("x.mp4", File.ReadAllText(svc.StorePath));
    }

    // ---- notes ----

    [Fact]
    public void Note_is_trimmed()
    {
        var svc = new GalleryMetaService(_path);
        Assert.True(svc.SetNote("clips|a.mp4", "  hello \r\n"));
        Assert.Equal("hello", svc.GetNote("clips|a.mp4"));
    }

    [Fact]
    public void Note_is_capped_at_500_characters()
    {
        var svc = new GalleryMetaService(_path);
        svc.SetNote("clips|a.mp4", new string('x', 800));
        Assert.Equal(500, svc.GetNote("clips|a.mp4").Length);
        Assert.Equal(500, new GalleryMetaService(_path).GetNote("clips|a.mp4").Length);
    }

    [Fact]
    public void Note_cap_does_not_cut_a_surrogate_pair_in_half()
    {
        // 499 letters + one emoji (2 UTF-16 units) straddles the limit.
        var text = new string('a', 499) + "\U0001F600";
        var stored = GalleryMetaService.NormalizeNote(text);
        Assert.Equal(new string('a', 499), stored);
        Assert.True(stored.All(c => !char.IsSurrogate(c)));
    }

    [Fact]
    public void Note_of_exactly_500_characters_is_kept_whole()
    {
        var text = new string('y', 500);
        Assert.Equal(text, GalleryMetaService.NormalizeNote(text));
    }

    [Fact]
    public void Empty_note_alone_prunes_the_entry_but_a_note_keeps_it()
    {
        var svc = new GalleryMetaService(_path);
        svc.SetNote("clips|a.mp4", "evidence");
        Assert.Contains("a.mp4", File.ReadAllText(svc.StorePath));

        svc.SetNote("clips|a.mp4", "   ");
        Assert.DoesNotContain("a.mp4", File.ReadAllText(svc.StorePath));
    }

    [Fact]
    public void Note_survives_the_other_fields_being_cleared()
    {
        var svc = new GalleryMetaService(_path);
        svc.SetNote("clips|a.mp4", "keep me");
        svc.ToggleFavorite("clips|a.mp4");
        svc.ToggleFavorite("clips|a.mp4");   // star off again — must not take the note with it
        Assert.Equal("keep me", new GalleryMetaService(_path).GetNote("clips|a.mp4"));
    }

    // ---- keys ----

    [Fact]
    public void Key_is_base_plus_relative_path_with_forward_slashes()
    {
        Assert.Equal("clips|Arma Reforger/2026-10-17 clip.mp4",
            GalleryMetaKey.For(GalleryBase.Clips, @"D:\Clips", @"D:\Clips\Arma Reforger\2026-10-17 clip.mp4"));
        Assert.Equal("shots|shot.png",
            GalleryMetaKey.For(GalleryBase.Shots, @"D:\Shots\", @"D:\Shots\shot.png"));
        Assert.Equal("clips|a/b/c.mp4",
            GalleryMetaKey.For(GalleryBase.Clips, @"D:\Clips", @"D:\Clips\a\b\c.mp4"));
    }

    [Fact]
    public void Key_ignores_the_case_of_the_base_folder_but_keeps_the_files_own_spelling()
    {
        Assert.Equal("clips|Game/Clip.mp4",
            GalleryMetaKey.For(GalleryBase.Clips, @"d:\clips", @"D:\CLIPS\Game\Clip.mp4"));
    }

    [Fact]
    public void Key_for_a_file_outside_the_base_falls_back_to_the_file_name()
    {
        Assert.Equal("clips|x.mp4", GalleryMetaKey.For(GalleryBase.Clips, @"D:\Clips", @"E:\Other\x.mp4"));
        Assert.Equal("clips|x.mp4", GalleryMetaKey.For(GalleryBase.Clips, @"D:\Clips\Sub", @"D:\Clips\x.mp4"));
    }

    [Fact]
    public void Lookup_is_case_insensitive_on_Windows()
    {
        var svc = new GalleryMetaService(_path);
        svc.SetNote("clips|Game/Clip.mp4", "n");
        Assert.Equal("n", svc.GetNote("CLIPS|game/clip.MP4"));
        Assert.Equal("n", new GalleryMetaService(_path).GetNote("clips|GAME/CLIP.mp4"));
    }

    [Fact]
    public void FileNameOf_reads_the_name_from_every_key_shape()
    {
        Assert.Equal("x.mp4", GalleryMetaKey.FileNameOf("clips|Game/x.mp4"));
        Assert.Equal("x.mp4", GalleryMetaKey.FileNameOf("shots|x.mp4"));
        Assert.Equal("x.mp4", GalleryMetaKey.FileNameOf("legacy|x.mp4"));
        Assert.Equal("x.mp4", GalleryMetaKey.FileNameOf("x.mp4"));
    }
}
