using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// The notes are admin evidence: the tests here are about NOT losing them — atomic writes,
/// corrupt and locked files, the import from the old file-name format, rolling back to an older
/// clipper and forward again, and the no-pruning rule.
/// </summary>
public sealed class GalleryMetaSafetyTests : IDisposable
{
    private readonly string _dir;
    private readonly string _metaDir;
    private readonly string _path;     // the OLD file (import source only)
    private readonly string _store;    // the v2 store
    private readonly string _clips;
    private readonly string _shots;

    public GalleryMetaSafetyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wki_metasafe_" + Guid.NewGuid().ToString("N"));
        _metaDir = Path.Combine(_dir, "meta");
        _path = Path.Combine(_metaDir, "gallery-meta.json");
        _store = Path.Combine(_metaDir, "gallery-meta.v2.json");
        _clips = Path.Combine(_dir, "Clips");
        _shots = Path.Combine(_dir, "Shots");
        Directory.CreateDirectory(_metaDir);
        Directory.CreateDirectory(_clips);
        Directory.CreateDirectory(_shots);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Touch(string folder, params string[] parts)
    {
        var p = Path.Combine(new[] { folder }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "x");
        return p;
    }

    private string[] Siblings(string pattern) => Directory.GetFiles(_metaDir, pattern);

    private void WriteOld(string json) => File.WriteAllText(_path, json);

    private const string OldStar = "{ \"a.mp4\": { \"Favorite\": true, \"Tags\": [\"x\"] } }";

    // ---------------------------------------------------------------- format and atomic writes

    [Fact]
    public void The_store_is_its_own_v2_file_and_the_old_file_is_never_written()
    {
        WriteOld(OldStar);
        var before = File.ReadAllBytes(_path);

        var svc = new GalleryMetaService(_path);
        Assert.Equal(_store, svc.StorePath);
        svc.EnsureMigrated(_clips, _shots);
        svc.SetNote("clips|a.mp4", "n");
        svc.ToggleFavorite("clips|b.mp4");
        svc.SetTags("clips|b.mp4", new[] { "t" });

        Assert.True(File.Exists(_store));
        Assert.Equal(before, File.ReadAllBytes(_path));   // byte for byte untouched
    }

    [Fact]
    public void Saved_file_is_the_v2_envelope()
    {
        var svc = new GalleryMetaService(_path);
        svc.SetNote("clips|Game/a.mp4", "why");
        svc.SetFavorite("clips|Game/a.mp4", true);
        svc.SetTags("clips|Game/a.mp4", new[] { "t1" });

        using var doc = JsonDocument.Parse(File.ReadAllText(_store));
        Assert.Equal(2, doc.RootElement.GetProperty("version").GetInt32());
        var entry = doc.RootElement.GetProperty("entries").GetProperty("clips|Game/a.mp4");
        Assert.True(entry.GetProperty("favorite").GetBoolean());
        Assert.Equal("t1", entry.GetProperty("tags")[0].GetString());
        Assert.Equal("why", entry.GetProperty("note").GetString());
    }

    [Fact]
    public void Save_leaves_a_valid_file_and_no_temp_file()
    {
        var svc = new GalleryMetaService(_path);
        svc.SetNote("clips|a.mp4", "one");
        svc.SetNote("clips|b.mp4", "two");

        Assert.Empty(Siblings("*.tmp"));
        using var doc = JsonDocument.Parse(File.ReadAllText(_store));   // throws if not valid JSON
        Assert.Equal(2, doc.RootElement.GetProperty("entries").EnumerateObject().Count());
    }

    [Fact]
    public void Every_save_keeps_the_previous_version_as_bak()
    {
        var svc = new GalleryMetaService(_path);
        svc.SetNote("clips|a.mp4", "first");
        var afterFirst = File.ReadAllText(_store);

        svc.SetNote("clips|a.mp4", "second");

        Assert.Equal(afterFirst, File.ReadAllText(_store + ".bak"));
        Assert.Equal("second", new GalleryMetaService(_path).GetNote("clips|a.mp4"));
    }

    // ---------------------------------------------------------------- corrupt files

    [Fact]
    public void Corrupt_store_is_copied_aside_before_anything_is_written()
    {
        const string garbage = "{ \"entries\": { \"clips|a.mp4\": { \"note\": \"half-written";
        File.WriteAllText(_store, garbage);

        var svc = new GalleryMetaService(_path);

        var copies = Siblings("gallery-meta.v2.corrupt-*.json");
        Assert.Single(copies);
        Assert.Equal(garbage, File.ReadAllText(copies[0]));
        Assert.Equal(garbage, File.ReadAllText(_store));   // loading alone changes nothing

        svc.SetNote("clips|b.mp4", "new");                  // first write after the corruption
        Assert.Equal(garbage, File.ReadAllText(copies[0])); // the preserved copy is still the original
        Assert.Equal("new", new GalleryMetaService(_path).GetNote("clips|b.mp4"));
    }

    [Fact]
    public void Restarting_with_the_same_corrupt_store_does_not_pile_up_copies()
    {
        File.WriteAllText(_store, "][ broken");
        _ = new GalleryMetaService(_path);
        _ = new GalleryMetaService(_path);
        _ = new GalleryMetaService(_path);
        Assert.Single(Siblings("gallery-meta.v2.corrupt-*.json"));
    }

    [Fact]
    public void Corrupt_store_falls_back_to_the_bak()
    {
        var good = new GalleryMetaService(_path);
        good.SetNote("clips|a.mp4", "evidence");
        good.SetNote("clips|b.mp4", "more");   // 2nd save: .bak now holds the first version
        File.WriteAllText(_store, "not json at all");

        var svc = new GalleryMetaService(_path);

        Assert.Equal("evidence", svc.GetNote("clips|a.mp4"));   // came back from the .bak
        Assert.Single(Siblings("gallery-meta.v2.corrupt-*.json"));
    }

    [Fact]
    public void Corrupt_store_without_any_bak_starts_empty_but_keeps_the_copy()
    {
        File.WriteAllText(_store, "][");
        var svc = new GalleryMetaService(_path);

        Assert.Equal("", svc.GetNote("clips|a.mp4"));
        Assert.Single(Siblings("gallery-meta.v2.corrupt-*.json"));
    }

    [Fact]
    public void Missing_store_with_a_bak_is_restored_from_the_bak()
    {
        var good = new GalleryMetaService(_path);
        good.SetNote("clips|a.mp4", "evidence");
        good.SetNote("clips|b.mp4", "more");
        File.Delete(_store);

        var svc = new GalleryMetaService(_path);
        Assert.Equal("evidence", svc.GetNote("clips|a.mp4"));
        svc.SetNote("clips|c.mp4", "c");   // saving with no main file present must work
        Assert.Equal("c", new GalleryMetaService(_path).GetNote("clips|c.mp4"));
    }

    [Fact]
    public void Empty_store_is_not_treated_as_corruption()
    {
        File.WriteAllText(_store, "");
        var svc = new GalleryMetaService(_path);
        Assert.Empty(Siblings("gallery-meta.v2.corrupt-*.json"));
        svc.SetNote("clips|a.mp4", "x");
        Assert.Equal("x", new GalleryMetaService(_path).GetNote("clips|a.mp4"));
    }

    // ---- an emptied main file and the backup next to it ----

    private string Bak => _store + ".bak";

    /// <summary>A store with two saves, so the .bak holds the first version (evidence + more) and the main file the second.</summary>
    private void WriteGoodStoreWithBak()
    {
        var good = new GalleryMetaService(_path);
        good.SetNote("clips|a.mp4", "evidence");
        good.SetNote("clips|b.mp4", "more");
        File.WriteAllText(Bak, File.ReadAllText(_store));   // the .bak is the complete notes file
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n  ")]
    public void An_emptied_store_with_a_good_bak_is_restored_from_the_bak_instead_of_replacing_it(string emptied)
    {
        WriteGoodStoreWithBak();
        var goodBak = File.ReadAllText(Bak);
        File.WriteAllText(_store, emptied);               // a sync tool, a restore or a profile reset left this behind

        var svc = new GalleryMetaService(_path);

        Assert.False(svc.HasProblem);
        Assert.Equal("evidence", svc.GetNote("clips|a.mp4"));
        Assert.Equal("more", svc.GetNote("clips|b.mp4"));
        Assert.Equal(emptied, File.ReadAllText(_store));  // loading alone changes nothing

        Assert.True(svc.SetNote("clips|c.mp4", "c"));     // the first save must not end the only good copy
        var re = new GalleryMetaService(_path);
        Assert.Equal("evidence", re.GetNote("clips|a.mp4"));
        Assert.Equal("c", re.GetNote("clips|c.mp4"));
        Assert.Contains(Siblings("gallery-meta.v2.restored-*.json"), p => File.ReadAllText(p) == goodBak);   // and a copy of it stays
    }

    [Fact]
    public void An_emptied_store_without_any_bak_is_still_just_an_empty_store()
    {
        File.WriteAllText(_store, "");

        var svc = new GalleryMetaService(_path);

        Assert.False(svc.HasProblem);
        Assert.Empty(Siblings("gallery-meta.v2.*-*.json"));
        Assert.True(svc.SetNote("clips|a.mp4", "x"));
    }

    [Fact]
    public void An_emptied_store_with_an_empty_bak_starts_empty_without_copies()
    {
        File.WriteAllText(_store, "");
        File.WriteAllText(Bak, "  ");

        var svc = new GalleryMetaService(_path);

        Assert.False(svc.HasProblem);
        Assert.Empty(Siblings("gallery-meta.v2.*-*.json"));
    }

    [Fact]
    public void An_emptied_store_with_an_unusable_bak_keeps_a_copy_of_that_bak_before_anything_is_written()
    {
        const string garbage = "{ \"entries\": { \"clips|a.mp4\": { \"note\": \"half";
        File.WriteAllText(_store, "");
        File.WriteAllText(Bak, garbage);

        var svc = new GalleryMetaService(_path);

        Assert.False(svc.HasProblem);
        var copy = Assert.Single(Siblings("gallery-meta.v2.bak-corrupt-*.json"));
        Assert.Equal(garbage, File.ReadAllText(copy));
        svc.SetNote("clips|b.mp4", "new");                // overwrites the .bak with the empty main file
        Assert.Equal(garbage, File.ReadAllText(copy));    // the copy is still the original
    }

    [Fact]
    public void A_locked_bak_is_incomplete_not_missing_and_nothing_is_written_until_it_can_be_read()
    {
        WriteGoodStoreWithBak();
        File.WriteAllText(_store, "");
        var mainBefore = File.ReadAllText(_store);
        var bakBefore = File.ReadAllText(Bak);

        GalleryMetaService svc;
        using (new FileStream(Bak, FileMode.Open, FileAccess.Read, FileShare.None))   // a scanner holds the backup
        {
            svc = new GalleryMetaService(_path);
            Assert.True(svc.HasProblem);
            Assert.False(svc.SetNote("clips|c.mp4", "typed while the backup was locked"));
            Assert.Equal(mainBefore, File.ReadAllText(_store));
        }
        Assert.Equal(bakBefore, File.ReadAllText(Bak));

        Assert.True(svc.Flush());                         // readable now: restore, overlay, save
        Assert.False(svc.HasProblem);
        var re = new GalleryMetaService(_path);
        Assert.Equal("evidence", re.GetNote("clips|a.mp4"));
        Assert.Equal("typed while the backup was locked", re.GetNote("clips|c.mp4"));
    }

    [Fact]
    public void A_corrupt_store_with_a_locked_bak_is_incomplete_too()
    {
        WriteGoodStoreWithBak();
        File.WriteAllText(_store, "not json at all");

        using (new FileStream(Bak, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var svc = new GalleryMetaService(_path);
            Assert.True(svc.HasProblem);
            Assert.False(svc.SetNote("clips|c.mp4", "x"));
            Assert.Equal("not json at all", File.ReadAllText(_store));
            Assert.Single(Siblings("gallery-meta.v2.corrupt-*.json"));   // the corrupt main file was still kept
        }
    }

    [Fact]
    public void A_missing_store_with_a_locked_bak_is_incomplete_too()
    {
        WriteGoodStoreWithBak();
        File.Delete(_store);

        using (new FileStream(Bak, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var svc = new GalleryMetaService(_path);
            Assert.True(svc.HasProblem);
            Assert.False(svc.SetNote("clips|c.mp4", "x"));
            Assert.False(File.Exists(_store));            // no half-empty store appears next to the backup
        }
    }

    [Fact]
    public void Restoring_from_the_bak_keeps_a_copy_of_it_once()
    {
        WriteGoodStoreWithBak();
        File.WriteAllText(_store, "garbage");

        _ = new GalleryMetaService(_path);
        _ = new GalleryMetaService(_path);

        Assert.Single(Siblings("gallery-meta.v2.restored-*.json"));
    }

    [Fact]
    public void Unparsable_old_file_is_kept_aside_once_and_ignored()
    {
        WriteOld("{ broken old file");
        var svc = new GalleryMetaService(_path);
        _ = new GalleryMetaService(_path);

        Assert.Single(Siblings("gallery-meta.corrupt-*.json"));
        Assert.Equal("{ broken old file", File.ReadAllText(_path));   // not touched either
        Assert.True(svc.SetNote("clips|a.mp4", "still works"));
    }

    // ---------------------------------------------------------------- a locked store (issue: edits silently lost)

    [Fact]
    public void A_locked_store_is_never_overwritten_and_the_edit_reports_failure()
    {
        var first = new GalleryMetaService(_path);
        first.SetNote("clips|a.mp4", "evidence");
        var original = File.ReadAllText(_store);

        using (new FileStream(_store, FileMode.Open, FileAccess.Read, FileShare.None))   // e.g. a scanner holds it
        {
            var locked = new GalleryMetaService(_path);
            Assert.True(locked.HasProblem);
            Assert.False(locked.SetNote("clips|b.mp4", "typed while the file was locked"));
            Assert.True(locked.HasProblem);
        }

        Assert.Equal(original, File.ReadAllText(_store));
    }

    [Fact]
    public void Edits_made_while_locked_are_laid_over_the_file_once_it_is_readable()
    {
        var first = new GalleryMetaService(_path);
        first.SetTags("clips|a.mp4", new[] { "t" });
        first.SetNote("clips|a.mp4", "old note");
        first.SetNote("clips|z.mp4", "untouched");

        GalleryMetaService svc;
        using (new FileStream(_store, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            svc = new GalleryMetaService(_path);
            Assert.Equal("", svc.GetNote("clips|a.mp4"));   // cannot see what is in the locked file
            Assert.False(svc.SetNote("clips|b.mp4", "typed while locked"));
            Assert.False(svc.ToggleFavorite("clips|a.mp4"));
        }

        Assert.True(svc.Flush());   // the next attempt: file readable → re-read, overlay, save
        Assert.False(svc.HasProblem);

        foreach (var s in new[] { svc, new GalleryMetaService(_path) })
        {
            Assert.Equal("typed while locked", s.GetNote("clips|b.mp4"));
            Assert.Equal("old note", s.GetNote("clips|a.mp4"));          // not wiped
            Assert.Contains("t", s.GetTags("clips|a.mp4"));              // not wiped
            Assert.True(s.IsFavorite("clips|a.mp4"));                    // the star typed meanwhile
            Assert.Equal("untouched", s.GetNote("clips|z.mp4"));
        }
    }

    // ---- edits made blind (the gallery showed an empty store while the real file was locked) ----

    private GalleryMetaService LockedWithEvidence(out IDisposable fileLock)
    {
        var first = new GalleryMetaService(_path);
        first.SetNote("clips|a.mp4", "Cheater XYZ 12:30");
        first.SetTags("clips|a.mp4", new[] { "ban" });
        first.SetFavorite("clips|a.mp4", true);
        fileLock = new FileStream(_store, FileMode.Open, FileAccess.Read, FileShare.None);
        var svc = new GalleryMetaService(_path);
        Assert.Equal("", svc.GetNote("clips|a.mp4"));   // what the gallery shows: nothing
        return svc;
    }

    [Fact]
    public void A_note_typed_blind_is_added_to_the_note_the_file_has_never_replacing_it()
    {
        var svc = LockedWithEvidence(out var fileLock);
        Assert.False(svc.SetNote("clips|a.mp4", "typed over a note nobody saw"));
        fileLock.Dispose();

        Assert.True(svc.Flush());

        foreach (var s in new[] { svc, new GalleryMetaService(_path) })
            Assert.Equal("Cheater XYZ 12:30\ntyped over a note nobody saw", s.GetNote("clips|a.mp4"));
    }

    [Fact]
    public void Clearing_a_note_blind_cannot_delete_the_real_one()
    {
        var svc = LockedWithEvidence(out var fileLock);
        svc.SetNote("clips|a.mp4", "oops");
        svc.SetNote("clips|a.mp4", "");                // what Esc used to write: the displayed (empty) note
        fileLock.Dispose();

        Assert.True(svc.Flush());

        Assert.Equal("Cheater XYZ 12:30", new GalleryMetaService(_path).GetNote("clips|a.mp4"));
    }

    [Fact]
    public void Tags_set_blind_are_added_to_the_tags_the_file_has()
    {
        var svc = LockedWithEvidence(out var fileLock);
        svc.SetTags("clips|a.mp4", new[] { "proof", "BAN" });   // "BAN" is the file's "ban" again
        fileLock.Dispose();

        Assert.True(svc.Flush());

        Assert.Equal(new[] { "ban", "proof" }, new GalleryMetaService(_path).GetTags("clips|a.mp4").OrderBy(t => t));
    }

    [Fact]
    public void A_star_toggled_off_blind_cannot_remove_the_star_the_file_has()
    {
        var svc = LockedWithEvidence(out var fileLock);
        svc.ToggleFavorite("clips|a.mp4");   // the row showed no star: this "adds" one
        svc.ToggleFavorite("clips|a.mp4");   // and this "removes" it again
        fileLock.Dispose();

        Assert.True(svc.Flush());

        Assert.True(new GalleryMetaService(_path).IsFavorite("clips|a.mp4"));
    }

    [Fact]
    public void A_blind_edit_of_a_file_the_store_does_not_know_still_just_lands()
    {
        var svc = LockedWithEvidence(out var fileLock);
        svc.SetNote("clips|new.mp4", "new note");
        fileLock.Dispose();

        Assert.True(svc.Flush());

        var re = new GalleryMetaService(_path);
        Assert.Equal("new note", re.GetNote("clips|new.mp4"));
        Assert.Equal("Cheater XYZ 12:30", re.GetNote("clips|a.mp4"));
    }

    // ---- cancelling an edit whose save failed ----

    [Fact]
    public void A_discarded_blind_note_edit_leaves_the_note_of_the_file_alone_and_writes_nothing_of_its_own()
    {
        var svc = LockedWithEvidence(out var fileLock);
        Assert.False(svc.SetNote("clips|a.mp4", "typed, then Esc"));

        svc.DiscardNoteEdit("clips|a.mp4");
        fileLock.Dispose();

        Assert.True(svc.Flush());
        Assert.Equal("Cheater XYZ 12:30", svc.GetNote("clips|a.mp4"));
        Assert.Equal("Cheater XYZ 12:30", new GalleryMetaService(_path).GetNote("clips|a.mp4"));
    }

    [Fact]
    public void Discarding_after_the_lock_ended_but_the_write_failed_goes_back_to_the_note_of_the_file()
    {
        var svc = LockedWithEvidence(out var fileLock);
        fileLock.Dispose();
        Directory.CreateDirectory(_store + ".tmp");                       // the file reads again, but the write fails
        Assert.False(svc.SetNote("clips|a.mp4", "typed blind"));          // re-read and merged in memory, not on disk

        svc.DiscardNoteEdit("clips|a.mp4");

        Assert.Equal("Cheater XYZ 12:30", svc.GetNote("clips|a.mp4"));   // not "", which a later write would make permanent
        Directory.Delete(_store + ".tmp");
        Assert.True(svc.Flush());
        Assert.Equal("Cheater XYZ 12:30", new GalleryMetaService(_path).GetNote("clips|a.mp4"));
    }

    [Fact]
    public void Discarding_after_a_failed_write_puts_the_previous_note_back_in_memory()
    {
        var svc = new GalleryMetaService(_path);
        svc.SetNote("clips|a.mp4", "first");
        Directory.CreateDirectory(_store + ".tmp");   // the write fails
        Assert.False(svc.SetNote("clips|a.mp4", "second"));
        Assert.Equal("second", svc.GetNote("clips|a.mp4"));

        svc.DiscardNoteEdit("clips|a.mp4");

        Assert.Equal("first", svc.GetNote("clips|a.mp4"));
        Directory.Delete(_store + ".tmp");
        Assert.True(svc.Flush());
        Assert.False(svc.HasProblem);
        Assert.Equal("first", new GalleryMetaService(_path).GetNote("clips|a.mp4"));
    }

    [Fact]
    public void Discarding_a_note_of_a_file_that_had_none_removes_the_entry_again()
    {
        var svc = new GalleryMetaService(_path);
        svc.SetNote("clips|other.mp4", "keep");
        Directory.CreateDirectory(_store + ".tmp");
        Assert.False(svc.SetNote("clips|b.mp4", "never saved"));

        svc.DiscardNoteEdit("clips|b.mp4");
        Directory.Delete(_store + ".tmp");
        Assert.True(svc.Flush());

        var re = new GalleryMetaService(_path);
        Assert.Equal("", re.GetNote("clips|b.mp4"));
        Assert.Equal("keep", re.GetNote("clips|other.mp4"));
    }

    [Fact]
    public void Discarding_leaves_the_other_pending_edits_of_the_file_in_place()
    {
        var svc = LockedWithEvidence(out var fileLock);
        svc.ToggleFavorite("clips|b.mp4");
        svc.SetNote("clips|b.mp4", "discard me");

        svc.DiscardNoteEdit("clips|b.mp4");
        fileLock.Dispose();

        Assert.True(svc.Flush());
        var re = new GalleryMetaService(_path);
        Assert.True(re.IsFavorite("clips|b.mp4"));
        Assert.Equal("", re.GetNote("clips|b.mp4"));
    }

    [Fact]
    public void Discarding_after_the_edit_reached_the_disk_changes_nothing()
    {
        var svc = new GalleryMetaService(_path);
        Assert.True(svc.SetNote("clips|a.mp4", "saved"));

        svc.DiscardNoteEdit("clips|a.mp4");

        Assert.Equal("saved", svc.GetNote("clips|a.mp4"));
        svc.DiscardNoteEdit("clips|never-edited.mp4");   // and an unknown key is fine
    }

    [Fact]
    public void The_next_edit_after_the_lock_ends_recovers_by_itself()
    {
        var first = new GalleryMetaService(_path);
        first.SetNote("clips|a.mp4", "old note");

        GalleryMetaService svc;
        using (new FileStream(_store, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            svc = new GalleryMetaService(_path);
            svc.SetNote("clips|b.mp4", "while locked");
        }

        Assert.True(svc.SetNote("clips|c.mp4", "after"));   // no explicit Flush needed

        var re = new GalleryMetaService(_path);
        Assert.Equal("old note", re.GetNote("clips|a.mp4"));
        Assert.Equal("while locked", re.GetNote("clips|b.mp4"));
        Assert.Equal("after", re.GetNote("clips|c.mp4"));
    }

    [Fact]
    public void Flush_keeps_failing_while_locked_and_does_not_touch_the_file()
    {
        var first = new GalleryMetaService(_path);
        first.SetNote("clips|a.mp4", "evidence");
        var original = File.ReadAllText(_store);

        using (new FileStream(_store, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var svc = new GalleryMetaService(_path);
            svc.SetNote("clips|b.mp4", "x");
            Assert.False(svc.Flush());
            Assert.False(svc.Flush());
        }
        Assert.Equal(original, File.ReadAllText(_store));
    }

    [Fact]
    public void A_failed_write_is_reported_kept_in_memory_and_retried()
    {
        var svc = new GalleryMetaService(_path);
        svc.SetNote("clips|a.mp4", "first");

        Directory.CreateDirectory(_store + ".tmp");   // the temp file cannot be created → the write fails
        Assert.False(svc.SetNote("clips|b.mp4", "second"));
        Assert.True(svc.HasProblem);
        Assert.Equal("second", svc.GetNote("clips|b.mp4"));   // still there in memory

        Directory.Delete(_store + ".tmp");
        Assert.True(svc.Flush());
        Assert.False(svc.HasProblem);
        Assert.Equal("second", new GalleryMetaService(_path).GetNote("clips|b.mp4"));
        Assert.Equal("first", new GalleryMetaService(_path).GetNote("clips|a.mp4"));
    }

    [Fact]
    public void Flush_without_pending_changes_is_a_cheap_success()
    {
        var svc = new GalleryMetaService(_path);
        Assert.True(svc.Flush());
        Assert.False(File.Exists(_store));   // nothing to write, nothing written
    }

    // ---------------------------------------------------------------- import of the old format

    [Fact]
    public void The_old_file_gets_a_timestamped_backup_when_imported()
    {
        WriteOld(OldStar);

        var svc = new GalleryMetaService(_path);

        Assert.True(svc.MigrationPending);
        var backups = Siblings("gallery-meta.v1.bak-*.json");
        Assert.Single(backups);
        Assert.Equal(OldStar, File.ReadAllText(backups[0]));
        Assert.Equal(OldStar, File.ReadAllText(_path));   // untouched
    }

    [Fact]
    public void Restarting_without_a_change_does_not_pile_up_backups_but_a_changed_old_file_gets_another()
    {
        WriteOld(OldStar);
        _ = new GalleryMetaService(_path);
        _ = new GalleryMetaService(_path);
        Assert.Single(Siblings("gallery-meta.v1.bak-*.json"));

        WriteOld("{ \"a.mp4\": { \"Favorite\": true, \"Tags\": [] }, \"c.mp4\": { \"Favorite\": true, \"Tags\": [] } }");
        _ = new GalleryMetaService(_path);
        Assert.Equal(2, Siblings("gallery-meta.v1.bak-*.json").Length);
    }

    [Fact]
    public void Old_entries_resolve_by_file_name_even_before_migration_ran()
    {
        WriteOld("{ \"a.mp4\": { \"Favorite\": true, \"Tags\": [] } }");
        var svc = new GalleryMetaService(_path);
        Assert.True(svc.IsFavorite("clips|Game/a.mp4"));
    }

    [Fact]
    public void Migration_single_match_becomes_one_path_entry()
    {
        var file = Touch(_clips, "Arma", "a.mp4");
        WriteOld("{ \"a.mp4\": { \"Favorite\": true, \"Tags\": [\"clutch\"] } }");

        var svc = new GalleryMetaService(_path);
        svc.EnsureMigrated(_clips, _shots);

        Assert.False(svc.MigrationPending);
        Assert.True(svc.IsFavorite(GalleryMetaKey.For(GalleryBase.Clips, _clips, file)));
        Assert.Contains("clutch", svc.GetTags("clips|Arma/a.mp4"));

        using var doc = JsonDocument.Parse(File.ReadAllText(_store));
        Assert.Equal(2, doc.RootElement.GetProperty("version").GetInt32());
        var keys = doc.RootElement.GetProperty("entries").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "clips|Arma/a.mp4" }, keys);
    }

    [Fact]
    public void Migration_same_name_in_two_folders_gives_each_file_its_own_entry()
    {
        Touch(_clips, "Arma", "a.mp4");
        Touch(_clips, "Squad", "a.mp4");
        Touch(_clips, "a.mp4");             // and one in the base folder itself
        WriteOld("{ \"a.mp4\": { \"Favorite\": true, \"Tags\": [\"t\"] } }");

        var svc = new GalleryMetaService(_path);
        svc.EnsureMigrated(_clips, _shots);

        Assert.True(svc.IsFavorite("clips|Arma/a.mp4"));
        Assert.True(svc.IsFavorite("clips|Squad/a.mp4"));
        Assert.True(svc.IsFavorite("clips|a.mp4"));

        // ...and from now on they are independent
        svc.SetFavorite("clips|Squad/a.mp4", false);
        svc.SetNote("clips|Arma/a.mp4", "this one");
        Assert.True(svc.IsFavorite("clips|Arma/a.mp4"));
        Assert.False(svc.IsFavorite("clips|Squad/a.mp4"));
        Assert.Equal("", svc.GetNote("clips|Squad/a.mp4"));
    }

    [Fact]
    public void Migration_finds_screenshots_under_the_screenshots_base()
    {
        Touch(_shots, "Arma", "s.png");
        WriteOld("{ \"s.png\": { \"Favorite\": true, \"Tags\": [] } }");

        var svc = new GalleryMetaService(_path);
        svc.EnsureMigrated(_clips, _shots);

        Assert.True(svc.IsFavorite("shots|Arma/s.png"));
        Assert.DoesNotContain("legacy|", File.ReadAllText(_store));
    }

    [Fact]
    public void Migration_scans_only_one_level_of_game_folders()
    {
        Touch(_clips, "Arma", "deep", "a.mp4");   // two levels down: not part of the gallery
        WriteOld("{ \"a.mp4\": { \"Favorite\": true, \"Tags\": [] } }");

        var svc = new GalleryMetaService(_path);
        svc.EnsureMigrated(_clips, _shots);

        Assert.Contains("legacy|a.mp4", File.ReadAllText(_store));
    }

    [Fact]
    public void Migration_without_a_matching_file_keeps_the_entry_as_legacy()
    {
        WriteOld("{ \"gone.mp4\": { \"Favorite\": true, \"Tags\": [\"old\"] } }");

        var svc = new GalleryMetaService(_path);
        svc.EnsureMigrated(_clips, _shots);

        Assert.Contains("legacy|gone.mp4", File.ReadAllText(_store));
        Assert.True(svc.IsFavorite("legacy|gone.mp4"));
    }

    [Fact]
    public void Legacy_entry_is_found_by_file_name_when_a_file_appears_later()
    {
        WriteOld("{ \"later.mp4\": { \"Favorite\": true, \"Tags\": [\"old\"] } }");
        var svc = new GalleryMetaService(_path);
        svc.EnsureMigrated(_clips, _shots);                 // file does not exist yet → legacy

        var reloaded = new GalleryMetaService(_path);
        Assert.False(reloaded.MigrationPending);
        Assert.True(reloaded.IsFavorite("clips|Arma/later.mp4"));   // appears in a game folder: found by name
        Assert.Contains("old", reloaded.GetTags("shots|later.mp4"));
    }

    [Fact]
    public void Editing_a_legacy_resolved_file_copies_it_and_leaves_the_legacy_entry_alone()
    {
        WriteOld("{ \"a.mp4\": { \"Favorite\": true, \"Tags\": [\"t\"] } }");
        var svc = new GalleryMetaService(_path);
        svc.EnsureMigrated(_clips, _shots);   // no file → legacy

        svc.SetNote("clips|Arma/a.mp4", "note for this one");

        Assert.True(svc.IsFavorite("clips|Arma/a.mp4"));              // star carried over
        Assert.Contains("t", svc.GetTags("clips|Arma/a.mp4"));
        Assert.Equal("", svc.GetNote("clips|Squad/a.mp4"));            // a same-named file elsewhere is not touched
        Assert.True(svc.IsFavorite("clips|Squad/a.mp4"));              // ...and still resolves via legacy
    }

    [Fact]
    public void Unstarring_a_legacy_resolved_file_sticks()
    {
        WriteOld("{ \"a.mp4\": { \"Favorite\": true, \"Tags\": [] } }");
        var svc = new GalleryMetaService(_path);
        svc.EnsureMigrated(_clips, _shots);

        Assert.True(svc.ToggleFavorite("clips|Arma/a.mp4"));    // star off
        Assert.False(svc.IsFavorite("clips|Arma/a.mp4"));       // must not fall back to the legacy star
        Assert.False(new GalleryMetaService(_path).IsFavorite("clips|Arma/a.mp4"));
    }

    [Fact]
    public void Migration_runs_once_and_a_second_call_changes_nothing()
    {
        Touch(_clips, "a.mp4");
        WriteOld("{ \"a.mp4\": { \"Favorite\": true, \"Tags\": [] } }");
        var svc = new GalleryMetaService(_path);
        svc.EnsureMigrated(_clips, _shots);
        var after = File.ReadAllText(_store);

        svc.EnsureMigrated(_clips, _shots);
        Assert.Equal(after, File.ReadAllText(_store));

        var reloaded = new GalleryMetaService(_path);
        Assert.False(reloaded.MigrationPending);
        reloaded.EnsureMigrated(_clips, _shots);
        Assert.Equal(after, File.ReadAllText(_store));
    }

    [Fact]
    public void Old_keys_that_differ_only_by_case_are_merged_not_dropped()
    {
        WriteOld("{ \"A.mp4\": { \"Favorite\": true, \"Tags\": [\"one\"] }, \"a.MP4\": { \"Favorite\": false, \"Tags\": [\"two\"] } }");
        var svc = new GalleryMetaService(_path);

        Assert.True(svc.IsFavorite("clips|a.mp4"));
        var tags = svc.GetTags("clips|a.mp4");
        Assert.Contains("one", tags);
        Assert.Contains("two", tags);
    }

    // ---------------------------------------------------------------- rolling back to an older clipper and forward again

    [Fact]
    public void Rolling_back_and_forward_keeps_every_note_and_picks_up_what_the_old_version_added()
    {
        Touch(_clips, "Arma", "a.mp4");
        WriteOld("{ \"a.mp4\": { \"Favorite\": true, \"Tags\": [] } }");

        // new version: import, then the owner writes evidence
        var v2 = new GalleryMetaService(_path);
        v2.EnsureMigrated(_clips, _shots);
        v2.SetNote("clips|Arma/a.mp4", "evidence: teamkill at 12:03");
        var storeAfterV2 = File.ReadAllText(_store);

        // rollback: the older clipper only knows gallery-meta.json — it stars another file and rewrites it
        WriteOld("{ \"a.mp4\": { \"Favorite\": true, \"Tags\": [] }, \"b.mp4\": { \"Favorite\": true, \"Tags\": [\"later\"] } }");
        Assert.Equal(storeAfterV2, File.ReadAllText(_store));   // the older version cannot even see the notes

        // upgrade again
        var again = new GalleryMetaService(_path);
        Assert.Equal("evidence: teamkill at 12:03", again.GetNote("clips|Arma/a.mp4"));
        Assert.True(again.IsFavorite("clips|Arma/a.mp4"));
        Assert.True(again.IsFavorite("clips|b.mp4"));                 // merged in from the old file
        Assert.Contains("later", again.GetTags("clips|b.mp4"));
        Assert.True(again.MigrationPending);

        Touch(_clips, "b.mp4");
        again.EnsureMigrated(_clips, _shots);
        Assert.Contains("clips|b.mp4", File.ReadAllText(_store));
        Assert.DoesNotContain("legacy|b.mp4", File.ReadAllText(_store));
        Assert.Equal("evidence: teamkill at 12:03", new GalleryMetaService(_path).GetNote("clips|Arma/a.mp4"));
    }

    [Fact]
    public void A_star_removed_in_the_new_version_does_not_come_back_from_the_old_file_on_later_starts()
    {
        Touch(_clips, "a.mp4");
        WriteOld("{ \"a.mp4\": { \"Favorite\": true, \"Tags\": [] } }");
        var svc = new GalleryMetaService(_path);
        svc.EnsureMigrated(_clips, _shots);
        Assert.True(svc.IsFavorite("clips|a.mp4"));

        svc.SetFavorite("clips|a.mp4", false);

        var restarted = new GalleryMetaService(_path);   // the old file still says "starred"
        Assert.False(restarted.IsFavorite("clips|a.mp4"));
        Assert.False(restarted.MigrationPending);
        Assert.False(new GalleryMetaService(_path).IsFavorite("clips|a.mp4"));
    }

    [Fact]
    public void Merging_from_the_old_file_never_replaces_what_the_store_already_has()
    {
        Touch(_clips, "a.mp4");
        WriteOld("{ \"a.mp4\": { \"Favorite\": false, \"Tags\": [\"oldtag\"] } }");
        var svc = new GalleryMetaService(_path);
        svc.EnsureMigrated(_clips, _shots);
        svc.SetNote("clips|a.mp4", "my note");
        svc.SetTags("clips|a.mp4", new[] { "mine" });

        WriteOld("{ \"a.mp4\": { \"Favorite\": true, \"Tags\": [\"somethingelse\"] } }");   // an old version rewrote it
        var again = new GalleryMetaService(_path);

        Assert.Equal("my note", again.GetNote("clips|a.mp4"));
        Assert.Equal(new[] { "mine" }, again.GetTags("clips|a.mp4").ToArray());
        Assert.False(again.IsFavorite("clips|a.mp4"));
    }

    // ---------------------------------------------------------------- no automatic pruning

    [Fact]
    public void Entries_of_files_that_no_longer_exist_are_kept_across_restarts_and_migration()
    {
        var svc = new GalleryMetaService(_path);
        svc.SetNote("clips|Offline/drive.mp4", "the drive is unplugged");

        var again = new GalleryMetaService(_path);
        again.EnsureMigrated(_clips, _shots);

        Assert.Equal("the drive is unplugged", again.GetNote("clips|Offline/drive.mp4"));
        Assert.Equal("the drive is unplugged", new GalleryMetaService(_path).GetNote("clips|Offline/drive.mp4"));
    }

    [Fact]
    public void Remove_is_the_only_way_an_entry_goes_away()
    {
        var svc = new GalleryMetaService(_path);
        svc.SetNote("clips|a.mp4", "n");
        svc.SetNote("clips|b.mp4", "m");

        Assert.True(svc.Remove("clips|a.mp4"));

        var reloaded = new GalleryMetaService(_path);
        Assert.Equal("", reloaded.GetNote("clips|a.mp4"));
        Assert.Equal("m", reloaded.GetNote("clips|b.mp4"));
    }

    [Fact]
    public void A_removal_requested_while_locked_is_applied_once_the_file_is_readable()
    {
        var first = new GalleryMetaService(_path);
        first.SetNote("clips|a.mp4", "n");
        first.SetNote("clips|b.mp4", "m");

        GalleryMetaService svc;
        using (new FileStream(_store, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            svc = new GalleryMetaService(_path);
            Assert.False(svc.Remove("clips|a.mp4"));
        }
        Assert.True(svc.Flush());

        var re = new GalleryMetaService(_path);
        Assert.Equal("", re.GetNote("clips|a.mp4"));
        Assert.Equal("m", re.GetNote("clips|b.mp4"));
    }
}
