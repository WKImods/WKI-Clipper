using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WKI_Clipper.Services;

/// <summary>Per-file gallery metadata: favorite flag, tags and a free-text note.</summary>
public sealed class GalleryMetaEntry
{
    public bool Favorite { get; set; }
    public List<string> Tags { get; set; } = new();
    public string Note { get; set; } = "";

    internal bool IsEmpty => !Favorite && Tags.Count == 0 && Note.Length == 0;

    internal GalleryMetaEntry Clone() => new() { Favorite = Favorite, Tags = new List<string>(Tags), Note = Note };
}

/// <summary>Which media folder a gallery file lives under — clips and screenshots can be different folders.</summary>
public enum GalleryBase { Clips, Shots }

/// <summary>
/// Identity of a gallery file in <see cref="GalleryMetaService"/>: "&lt;base&gt;|&lt;path relative to
/// that base, forward slashes&gt;". The path (not just the file name) is the identity, so the same
/// name in two game folders is two files with two notes.
/// </summary>
public static class GalleryMetaKey
{
    public const string LegacyPrefix = "legacy|";

    public static string For(GalleryBase kind, string baseDir, string filePath)
    {
        var prefix = kind == GalleryBase.Clips ? "clips|" : "shots|";
        string relative;
        try { relative = Path.GetRelativePath(baseDir, filePath); }
        catch { relative = Path.GetFileName(filePath); }

        // Outside the base (or on another drive): the name is all we can say.
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            relative = Path.GetFileName(filePath);

        return prefix + relative.Replace('\\', '/');
    }

    /// <summary>Key of an entry carried over from the old file-name-only format that matched no file.</summary>
    public static string Legacy(string fileName) => LegacyPrefix + fileName;

    /// <summary>The file name a key points at ("clips|Arma/x.mp4" → "x.mp4").</summary>
    public static string FileNameOf(string key)
    {
        var path = key[(key.IndexOf('|') + 1)..];
        return path[(path.LastIndexOf('/') + 1)..];
    }
}

/// <summary>
/// Side-car store for gallery favorites, tags and notes — clips and screenshots carry no
/// metadata of their own. The notes can be the only record of why a clip matters (admin
/// evidence), so losing them is the one thing this class must never do:
///
/// * The notes live in their own file ("gallery-meta.v2.json"). The old "gallery-meta.json"
///   (file-name keyed, written by earlier versions) is an import source only and is never
///   written: rolling back to an older clipper and forward again cannot overwrite the notes.
///   Entries an older version added to it in the meantime are merged in, never replacing.
/// * Writes are atomic (temp file + File.Replace, previous version kept as ".bak").
/// * An unparsable file is copied aside before anything is written. An emptied (0-byte) file is
///   no better than an unparsable one: the backup is tried, and a backup that is itself unusable
///   is copied aside too. A good backup that is restored is copied aside once as well, since the
///   next save replaces the ".bak".
/// * A file that cannot be read (virus scanner, backup tool) is never overwritten - and neither
///   is a store whose backup cannot be read when it is needed. Edits made meanwhile are kept in
///   memory and laid over the file once it is readable again, merged into what the file has
///   (they were typed over an empty-looking store); every edit method reports whether the change
///   reached the disk. A cancelled editor takes its failed edit back with <see cref="DiscardNoteEdit"/>.
///
/// Pure enough to unit-test: the path is injectable, no WPF/AppHost dependency.
/// </summary>
public sealed class GalleryMetaService
{
    public const int CurrentVersion = 2;
    public const int MaxNoteLength = 500;

    private readonly string _v1Path;
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, GalleryMetaEntry> _map = NewMap();
    /// <summary>Fingerprint of the old-format file as last imported; a different one means it changed since.</summary>
    private string? _v1Fingerprint;
    /// <summary>File names imported from the old format that still wait to be matched to real files.</summary>
    private HashSet<string> _pendingNames = NewNames();
    /// <summary>The store exists but could not be read/preserved: <see cref="_map"/> is NOT the whole truth, so nothing may be written.</summary>
    private bool _loadIncomplete;
    private bool _lastSaveFailed;
    /// <summary>What was edited since the last successful write — laid over the file if it had to be re-read.</summary>
    private readonly Dictionary<string, Fields> _dirty = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The note each unsaved note edit started from, so <see cref="DiscardNoteEdit"/> can take the edit back without writing.</summary>
    private readonly Dictionary<string, string> _noteBeforeEdit = new(StringComparer.OrdinalIgnoreCase);

    [Flags]
    private enum Fields { None = 0, Favorite = 1, Tags = 2, Note = 4, Removed = 8 }

    private sealed record LoadResult(
        Dictionary<string, GalleryMetaEntry> Map, string? Fingerprint, HashSet<string> Pending, bool Incomplete);

    private sealed record StoreFile(
        int Version, string? V1Fingerprint, Dictionary<string, GalleryMetaEntry> Entries);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,   // reads the old PascalCase file too
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static Dictionary<string, GalleryMetaEntry> NewMap() => new(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string> NewNames() => new(StringComparer.OrdinalIgnoreCase);

    /// <param name="filePath">
    /// The OLD file ("gallery-meta.json"; import only). The store itself is the sibling
    /// "gallery-meta.v2.json".
    /// </param>
    public GalleryMetaService(string? filePath = null)
    {
        _v1Path = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WKI_Clipper", "gallery-meta.json");
        _path = SiblingPath(".v2.json");

        lock (_gate)
        {
            var loaded = LoadCore();
            _map = loaded.Map;
            _v1Fingerprint = loaded.Fingerprint;
            _pendingNames = loaded.Pending;
            _loadIncomplete = loaded.Incomplete;
        }
    }

    /// <summary>The file the notes are stored in.</summary>
    public string StorePath => _path;

    /// <summary>True while entries imported from the old format wait to be matched to real files.</summary>
    public bool MigrationPending { get { lock (_gate) return _pendingNames.Count > 0; } }

    /// <summary>
    /// The store could not be read, or the last write failed: what is shown may be incomplete and
    /// recent edits may exist only in memory. The UI warns while this is true.
    /// </summary>
    public bool HasProblem { get { lock (_gate) return _loadIncomplete || _lastSaveFailed; } }

    private string SiblingPath(string suffix)
        => Path.Combine(Path.GetDirectoryName(_v1Path) ?? "", Path.GetFileNameWithoutExtension(_v1Path) + suffix);

    // ---------------------------------------------------------------- load

    private LoadResult LoadCore()
    {
        var map = NewMap();
        string? fingerprint = null;
        var pending = NewNames();

        // The main file holds no usable notes (missing, emptied, unparsable): the backup next to it may.
        bool tryBackup;
        if (File.Exists(_path))
        {
            if (!TryReadAllText(_path, out var text))
            {
                // Present but unreadable: an empty start followed by a save would replace the notes.
                Logger.Error("GalleryMeta: the notes file exists but cannot be read — nothing is written until it can.");
                return new LoadResult(map, null, pending, Incomplete: true);
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                // A sync tool, a restore or a profile reset can leave a 0-byte file. It holds no notes, but the next
                // save would move it over the good backup - so the backup is looked at first (nothing to copy aside).
                Logger.Warn("GalleryMeta: the notes file is empty — trying the backup.");
                tryBackup = true;
            }
            else if (TryParseStore(text, out var parsed, out var fp))
            {
                map = parsed;
                fingerprint = fp;
                tryBackup = false;
            }
            else
            {
                Logger.Warn("GalleryMeta: the notes file is unparsable — keeping a copy and trying the backup.");
                if (!KeepCopy(_path, ".v2.corrupt"))
                    return new LoadResult(map, null, pending, Incomplete: true);
                tryBackup = true;
            }
        }
        else tryBackup = true;

        if (tryBackup)
        {
            var backupPath = _path + ".bak";
            switch (TryLoadBackup(out var backup, out var backupFp))
            {
                case BakState.Loaded:
                    map = backup;
                    fingerprint = backupFp;
                    KeepCopy(backupPath, ".v2.restored");   // the next save replaces the .bak: the good version stays in a copy (best effort)
                    break;
                case BakState.Locked:
                    // Not "missing": a save now would rotate the main file over a backup nobody has looked at.
                    Logger.Error("GalleryMeta: the backup exists but cannot be read — nothing is written until it can.");
                    return new LoadResult(NewMap(), null, pending, Incomplete: true);
                case BakState.Unusable:
                    if (!KeepCopy(backupPath, ".v2.bak-corrupt"))
                        return new LoadResult(NewMap(), null, pending, Incomplete: true);
                    break;
                // Missing / Empty: nothing to restore
            }
        }

        ImportOldFile(map, ref fingerprint, pending);
        return new LoadResult(map, fingerprint, pending, Incomplete: false);
    }

    private enum BakState { Missing, Empty, Loaded, Locked, Unusable }

    private BakState TryLoadBackup(out Dictionary<string, GalleryMetaEntry> map, out string? fingerprint)
    {
        map = NewMap();
        fingerprint = null;
        var backup = _path + ".bak";
        if (!File.Exists(backup)) return BakState.Missing;
        if (!TryReadAllText(backup, out var text)) return BakState.Locked;
        if (string.IsNullOrWhiteSpace(text)) return BakState.Empty;
        if (!TryParseStore(text, out map, out fingerprint)) return BakState.Unusable;
        Logger.Warn("GalleryMeta: restored from " + backup);
        return BakState.Loaded;
    }

    /// <summary>
    /// Merges the old file into the store: entries whose file name the store does not know yet
    /// come in as legacy entries (matched to real files by <see cref="EnsureMigrated"/>). Runs
    /// only when the old file differs from what was imported last — otherwise a star removed
    /// since would come back on every start. The old file is never modified.
    /// </summary>
    private void ImportOldFile(Dictionary<string, GalleryMetaEntry> map, ref string? fingerprint, HashSet<string> pending)
    {
        if (!File.Exists(_v1Path)) return;
        if (!TryReadAllText(_v1Path, out var text) || string.IsNullOrWhiteSpace(text)) return;   // locked: next start; nothing is at risk

        var current = Fingerprint(text);
        if (current == fingerprint) return;

        if (!TryParseOld(text, out var old))
        {
            KeepCopy(_v1Path, ".corrupt");   // best effort — the old file is never written, so nothing hinges on it
            return;
        }

        KeepCopy(_v1Path, ".v1.bak");
        var known = new HashSet<string>(map.Keys.Select(GalleryMetaKey.FileNameOf), StringComparer.OrdinalIgnoreCase);
        foreach (var (name, entry) in old)
        {
            if (known.Contains(name)) continue;
            AddMerged(map, GalleryMetaKey.Legacy(name), entry);
            pending.Add(name);
        }
        fingerprint = current;
    }

    /// <summary>The v2 store: {"version": n, "v1Fingerprint": "...", "entries": {...}}.</summary>
    private static bool TryParseStore(string json, out Dictionary<string, GalleryMetaEntry> map, out string? fingerprint)
    {
        map = NewMap();
        fingerprint = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number) return false;
            if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Object) return false;

            if (root.TryGetProperty("v1Fingerprint", out var fp) && fp.ValueKind == JsonValueKind.String)
                fingerprint = fp.GetString();

            var raw = JsonSerializer.Deserialize<Dictionary<string, GalleryMetaEntry?>>(entries.GetRawText(), JsonOptions);
            if (raw is null) return false;
            foreach (var (key, entry) in raw)
                if (Sanitized(entry) is { } clean) AddMerged(map, key, clean);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    /// <summary>The old file: a bare {"fileName": {"Favorite":…, "Tags":[…]}} dictionary.</summary>
    private static bool TryParseOld(string json, out Dictionary<string, GalleryMetaEntry> byName)
    {
        byName = NewMap();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number) return false;   // that is the new format

            var raw = JsonSerializer.Deserialize<Dictionary<string, GalleryMetaEntry?>>(json, JsonOptions);
            if (raw is null) return false;
            foreach (var (name, entry) in raw)
                if (Sanitized(entry) is { } clean) AddMerged(byName, name, clean);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static GalleryMetaEntry? Sanitized(GalleryMetaEntry? entry)
    {
        if (entry is null) return null;
        entry.Tags ??= new List<string>();
        entry.Note ??= "";
        return entry;
    }

    /// <summary>Two keys that differ only by case are one file on Windows; a hand-edited file could hold both — keep both entries' data.</summary>
    private static void AddMerged(Dictionary<string, GalleryMetaEntry> map, string key, GalleryMetaEntry entry)
    {
        if (!map.TryGetValue(key, out var existing)) { map[key] = entry; return; }

        existing.Favorite |= entry.Favorite;
        foreach (var tag in entry.Tags)
            if (!existing.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) existing.Tags.Add(tag);
        if (entry.Note.Length > 0 && entry.Note != existing.Note)
            existing.Note = existing.Note.Length == 0 ? entry.Note : existing.Note + "\n" + entry.Note;
    }

    private static string Fingerprint(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static bool TryReadAllText(string path, out string text)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                text = File.ReadAllText(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A virus scanner or sync client may hold the file for a moment.
                if (attempt < 2) Thread.Sleep(120);
            }
        }
        text = "";
        return false;
    }

    /// <summary>
    /// Copies a file to "&lt;stem&gt;&lt;tag&gt;-yyyyMMdd-HHmmss.json" next to it (first free name) —
    /// unless an identical copy is already there, so repeated starts do not pile up duplicates.
    /// </summary>
    private bool KeepCopy(string source, string tag)
    {
        try
        {
            var dir = Path.GetDirectoryName(_v1Path) ?? "";
            var stem = Path.GetFileNameWithoutExtension(_v1Path);
            var bytes = File.ReadAllBytes(source);

            foreach (var existing in Directory.GetFiles(string.IsNullOrEmpty(dir) ? "." : dir, $"{stem}{tag}-*.json"))
            {
                try
                {
                    if (new FileInfo(existing).Length == bytes.Length && File.ReadAllBytes(existing).AsSpan().SequenceEqual(bytes))
                        return true;
                }
                catch { /* unreadable candidate: keep looking */ }
            }

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var target = Path.Combine(dir, $"{stem}{tag}-{stamp}.json");
            for (int n = 2; File.Exists(target); n++) target = Path.Combine(dir, $"{stem}{tag}-{stamp}-{n}.json");
            File.WriteAllBytes(target, bytes);
            Logger.Warn($"GalleryMeta: kept a copy of '{source}' as {target}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"GalleryMeta: could not keep a copy of '{source}'", ex);
            return false;
        }
    }

    // ---------------------------------------------------------------- save

    /// <summary>
    /// Writes the store. If the store could not be read at startup it is re-read first and the
    /// edits made in the meantime are laid over it. False = the change is NOT on disk.
    /// </summary>
    public bool Save()
    {
        lock (_gate) return SaveCore();
    }

    /// <summary>Retries whatever is still unsaved (called when the gallery refreshes and on exit). True when nothing is left unsaved.</summary>
    public bool Flush()
    {
        lock (_gate)
        {
            if (!_loadIncomplete && !_lastSaveFailed && _dirty.Count == 0) return true;
            return SaveCore();
        }
    }

    private bool SaveCore()
    {
        if (_loadIncomplete && !Reconcile())
        {
            _lastSaveFailed = true;
            return false;
        }

        bool ok = WriteFile();
        _lastSaveFailed = !ok;
        if (ok)
        {
            _dirty.Clear();
            _noteBeforeEdit.Clear();   // on disk now: nothing left to take back
        }
        return ok;
    }

    /// <summary>The store was unreadable at startup: read it again now and put the edits made since on top.</summary>
    private bool Reconcile()
    {
        var fresh = LoadCore();
        if (fresh.Incomplete) return false;

        // What a cancelled edit goes back to is now what the file says, not what the empty-looking store showed.
        foreach (var key in _noteBeforeEdit.Keys.ToList())
            _noteBeforeEdit[key] = FindIn(fresh.Map, key)?.Note ?? "";
        ApplyOverlay(fresh.Map, _map, _dirty);
        _map = fresh.Map;
        _v1Fingerprint = fresh.Fingerprint;
        _pendingNames = fresh.Pending;
        _loadIncomplete = false;
        Logger.Info("GalleryMeta: the notes file is readable again — edits made meanwhile were merged in.");
        return true;
    }

    /// <summary>
    /// Lays the edits made while the file could not be read over what the file says now. Only the fields the user
    /// touched are considered, and they are MERGED, never replaced: those edits were typed over an empty-looking
    /// store, so replacing would silently drop what the file has that nobody saw. A note is appended to the file's
    /// note (an empty one changes nothing - that is what cancelling an edit writes), tags are added, a star is only
    /// ever set. Removing something works again once the gallery shows the real values.
    /// </summary>
    private static void ApplyOverlay(
        Dictionary<string, GalleryMetaEntry> target,
        Dictionary<string, GalleryMetaEntry> edited,
        Dictionary<string, Fields> dirty)
    {
        foreach (var (key, fields) in dirty)
        {
            if (fields.HasFlag(Fields.Removed)) target.Remove(key);

            var touched = fields & ~Fields.Removed;
            if (touched == Fields.None) continue;

            edited.TryGetValue(key, out var source);
            source ??= new GalleryMetaEntry();
            if (!target.TryGetValue(key, out var entry)) target[key] = entry = new GalleryMetaEntry();

            if (touched.HasFlag(Fields.Favorite)) entry.Favorite |= source.Favorite;
            if (touched.HasFlag(Fields.Tags))
                foreach (var tag in source.Tags)
                    if (!entry.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) entry.Tags.Add(tag);
            if (touched.HasFlag(Fields.Note) && source.Note.Length > 0 && source.Note != entry.Note)
                entry.Note = entry.Note.Length == 0 ? source.Note : entry.Note + "\n" + source.Note;
            if (entry.IsEmpty && !ShadowsLegacy(target, key)) target.Remove(key);
        }
    }

    /// <summary>Atomic: a crash leaves the old file or the new one, never half of either.</summary>
    private bool WriteFile()
    {
        var tmp = _path + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var ordered = _map.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                              .ToDictionary(kv => kv.Key, kv => kv.Value);
            var json = JsonSerializer.Serialize(new StoreFile(CurrentVersion, _v1Fingerprint, ordered), JsonOptions);

            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                fs.Flush(flushToDisk: true);   // on disk before it replaces the good file
            }

            if (File.Exists(_path)) File.Replace(tmp, _path, _path + ".bak", ignoreMetadataErrors: true);
            else File.Move(tmp, _path);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("GalleryMeta save failed: " + ex.Message);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
    }

    // ---------------------------------------------------------------- migration

    /// <summary>
    /// Turns the entries imported from the old file-name format into path entries: every file with
    /// a matching name under the clips and screenshots folders (base + one level of game folders)
    /// gets its own copy; names that match nothing stay as "legacy|name". Existing path entries are
    /// never replaced. Does nothing unless an import is waiting.
    /// </summary>
    public void EnsureMigrated(string clipsDir, string screenshotsDir)
    {
        lock (_gate)
        {
            if (_pendingNames.Count == 0) return;

            try
            {
                var names = new HashSet<string>(_pendingNames, StringComparer.OrdinalIgnoreCase);
                var matches = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                void Scan(GalleryBase kind, string dir)
                {
                    foreach (var (file, _) in GameFolderNaming.EnumerateMedia(dir, f => names.Contains(f.Name)))
                    {
                        if (!matches.TryGetValue(file.Name, out var keys)) matches[file.Name] = keys = new List<string>();
                        keys.Add(GalleryMetaKey.For(kind, dir, file.FullName));
                    }
                }
                Scan(GalleryBase.Clips, clipsDir);
                Scan(GalleryBase.Shots, screenshotsDir);

                foreach (var name in names)
                {
                    var legacyKey = GalleryMetaKey.Legacy(name);
                    if (!matches.TryGetValue(name, out var targets) || !_map.TryGetValue(legacyKey, out var entry)) continue;

                    foreach (var target in targets)
                        if (!_map.ContainsKey(target)) _map[target] = entry.Clone();
                    _map.Remove(legacyKey);   // nothing is lost: every match has its own copy now
                }

                _pendingNames.Clear();
                SaveCore();
            }
            catch (Exception ex)
            {
                // Stays pending: nothing was written, the next refresh tries again.
                Logger.Error("GalleryMeta migration failed", ex);
            }
        }
    }

    // ---------------------------------------------------------------- queries and edits

    /// <summary>The entry for a key, falling back to the old file-name entry while no path entry exists.</summary>
    private GalleryMetaEntry? Find(string key) => FindIn(_map, key);

    private static GalleryMetaEntry? FindIn(Dictionary<string, GalleryMetaEntry> map, string key)
    {
        if (map.TryGetValue(key, out var e)) return e;
        return map.TryGetValue(GalleryMetaKey.Legacy(GalleryMetaKey.FileNameOf(key)), out var legacy) ? legacy : null;
    }

    /// <summary>
    /// Entry to edit. A file that so far only resolves through a legacy entry gets its own copy of it
    /// (the legacy entry stays for other files of that name).
    /// </summary>
    private GalleryMetaEntry GetOrCreate(string key)
    {
        if (_map.TryGetValue(key, out var e)) return e;
        e = Find(key)?.Clone() ?? new GalleryMetaEntry();
        _map[key] = e;
        return e;
    }

    private static bool ShadowsLegacy(Dictionary<string, GalleryMetaEntry> map, string key)
        => !key.StartsWith(GalleryMetaKey.LegacyPrefix, StringComparison.OrdinalIgnoreCase)
           && map.ContainsKey(GalleryMetaKey.Legacy(GalleryMetaKey.FileNameOf(key)));

    /// <summary>
    /// Drops an entry that carries nothing — but not while it shadows a legacy entry: removing it
    /// would bring the legacy values back to life.
    /// </summary>
    private void PruneIfEmpty(string key, GalleryMetaEntry e)
    {
        if (e.IsEmpty && !ShadowsLegacy(_map, key)) _map.Remove(key);
    }

    private void MarkDirty(string key, Fields touched)
    {
        _dirty.TryGetValue(key, out var current);
        _dirty[key] = touched == Fields.Removed ? Fields.Removed : current | touched;
    }

    /// <summary>Common tail of every edit. Returns whether the change reached the disk.</summary>
    private bool Commit(string key, GalleryMetaEntry e, Fields touched)
    {
        MarkDirty(key, touched);
        PruneIfEmpty(key, e);
        return SaveCore();
    }

    public bool IsFavorite(string key)
    {
        lock (_gate) return Find(key)?.Favorite == true;
    }

    /// <summary>Flips the favorite flag (read the new state with <see cref="IsFavorite"/>). True = saved to disk.</summary>
    public bool ToggleFavorite(string key)
    {
        lock (_gate)
        {
            var e = GetOrCreate(key);
            e.Favorite = !e.Favorite;
            return Commit(key, e, Fields.Favorite);
        }
    }

    /// <summary>True = saved to disk.</summary>
    public bool SetFavorite(string key, bool value)
    {
        lock (_gate)
        {
            var e = GetOrCreate(key);
            e.Favorite = value;
            return Commit(key, e, Fields.Favorite);
        }
    }

    public IReadOnlyList<string> GetTags(string key)
    {
        lock (_gate) return Find(key)?.Tags.ToList() ?? new List<string>();
    }

    /// <summary>True = saved to disk.</summary>
    public bool SetTags(string key, IEnumerable<string> tags)
    {
        lock (_gate)
        {
            var e = GetOrCreate(key);
            e.Tags = tags.Select(t => t.Trim())
                         .Where(t => t.Length > 0)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .ToList();
            return Commit(key, e, Fields.Tags);
        }
    }

    /// <summary>All distinct tags across every file, sorted.</summary>
    public IReadOnlyList<string> AllTags()
    {
        lock (_gate)
            return _map.Values.SelectMany(e => e.Tags)
                              .Distinct(StringComparer.OrdinalIgnoreCase)
                              .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                              .ToList();
    }

    public string GetNote(string key)
    {
        lock (_gate) return Find(key)?.Note ?? "";
    }

    /// <summary>
    /// Stores the note (trimmed, at most <see cref="MaxNoteLength"/> characters; read it back with
    /// <see cref="GetNote"/>). True = saved to disk; false = kept in memory and retried, the UI must say so.
    /// </summary>
    public bool SetNote(string key, string? note)
    {
        var clean = NormalizeNote(note);
        lock (_gate)
        {
            var e = GetOrCreate(key);
            _noteBeforeEdit.TryAdd(key, e.Note);   // the first unsaved edit counts: a retry must not become the "before"
            e.Note = clean;
            return Commit(key, e, Fields.Note);
        }
    }

    /// <summary>
    /// Takes back a note edit whose save failed (the user cancelled the editor): the note in memory goes back to
    /// what it was before that edit and the edit is no longer pending, so it is neither saved nor laid over the file
    /// later. Writes nothing - in particular not the value the row happens to show, which after an unreadable file
    /// is not the stored note. No-op when no unsaved note edit of the file exists.
    /// </summary>
    public void DiscardNoteEdit(string key)
    {
        lock (_gate)
        {
            if (!_noteBeforeEdit.Remove(key, out var before)) return;

            if (_dirty.TryGetValue(key, out var fields))
            {
                fields &= ~Fields.Note;
                if (fields == Fields.None) _dirty.Remove(key); else _dirty[key] = fields;
            }
            if (_map.TryGetValue(key, out var e))
            {
                e.Note = before;
                PruneIfEmpty(key, e);
            }
        }
    }

    public static string NormalizeNote(string? note)
    {
        var s = (note ?? "").Trim();
        if (s.Length <= MaxNoteLength) return s;
        int cut = MaxNoteLength;
        if (char.IsHighSurrogate(s[cut - 1])) cut--;   // never split an emoji in half
        return s[..cut].TrimEnd();
    }

    /// <summary>
    /// The only way an entry goes away: the user deleted the file through the gallery. A file that
    /// merely vanished (drive offline, folder moved) keeps its entry. True = saved to disk.
    /// </summary>
    public bool Remove(string key)
    {
        lock (_gate)
        {
            bool had = _map.Remove(key);
            if (!had && !_loadIncomplete) return true;
            MarkDirty(key, Fields.Removed);
            return SaveCore();
        }
    }
}
