using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WKI_Clipper.Services;

/// <summary>Per-file gallery metadata (favorite flag + tags), keyed by file name.</summary>
public sealed class GalleryMetaEntry
{
    public bool Favorite { get; set; }
    public List<string> Tags { get; set; } = new();
}

/// <summary>
/// Side-car store for gallery favorites/tags — clips and screenshots carry no metadata
/// of their own. Persists to a small JSON file. Pure enough to unit-test: the file path
/// is injectable and no WPF/AppHost dependency is involved.
/// </summary>
public sealed class GalleryMetaService
{
    private readonly string _path;
    private Dictionary<string, GalleryMetaEntry> _map = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public GalleryMetaService(string? filePath = null)
    {
        _path = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WKI_Clipper", "gallery-meta.json");
        Load();
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_path)) { _map = new(StringComparer.OrdinalIgnoreCase); return; }
            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, GalleryMetaEntry>>(json, JsonOptions);
            _map = loaded != null
                ? new Dictionary<string, GalleryMetaEntry>(loaded, StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // Corrupt/unreadable meta is non-critical — start empty rather than crash the gallery.
            _map = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(_map, JsonOptions));
        }
        catch (Exception ex) { Logger.Warn("GalleryMeta save failed: " + ex.Message); }
    }

    public bool IsFavorite(string fileName)
        => _map.TryGetValue(fileName, out var e) && e.Favorite;

    /// <summary>Flips the favorite flag and persists. Returns the new state.</summary>
    public bool ToggleFavorite(string fileName)
    {
        var e = GetOrCreate(fileName);
        e.Favorite = !e.Favorite;
        Prune(fileName, e);
        Save();
        return e.Favorite;
    }

    public void SetFavorite(string fileName, bool value)
    {
        var e = GetOrCreate(fileName);
        e.Favorite = value;
        Prune(fileName, e);
        Save();
    }

    public IReadOnlyList<string> GetTags(string fileName)
        => _map.TryGetValue(fileName, out var e) ? e.Tags.ToList() : new List<string>();

    public void SetTags(string fileName, IEnumerable<string> tags)
    {
        var e = GetOrCreate(fileName);
        e.Tags = tags.Select(t => t.Trim())
                     .Where(t => t.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .ToList();
        Prune(fileName, e);
        Save();
    }

    /// <summary>All distinct tags across every file, sorted.</summary>
    public IReadOnlyList<string> AllTags()
        => _map.Values.SelectMany(e => e.Tags)
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                      .ToList();

    private GalleryMetaEntry GetOrCreate(string fileName)
    {
        if (!_map.TryGetValue(fileName, out var e))
        {
            e = new GalleryMetaEntry();
            _map[fileName] = e;
        }
        return e;
    }

    /// <summary>Drop empty entries so the file doesn't accumulate dead keys.</summary>
    private void Prune(string fileName, GalleryMetaEntry e)
    {
        if (!e.Favorite && e.Tags.Count == 0) _map.Remove(fileName);
    }
}
