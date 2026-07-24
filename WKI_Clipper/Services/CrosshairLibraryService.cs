using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WKI_Clipper.Services;

/// <summary>One crosshair image in the library.</summary>
public sealed class CrosshairEntry
{
    /// <summary>Stable id, also the stored file's base name.</summary>
    public string Id { get; set; } = "";
    /// <summary>User-facing name (defaults to the imported file name).</summary>
    public string Name { get; set; } = "";
    /// <summary>File name inside the library folder (not a full path — folder may move).</summary>
    public string FileName { get; set; } = "";
    public DateTime AddedUtc { get; set; }
}

/// <summary>
/// The crosshair PNG library: imported images are COPIED into
/// %APPDATA%\WKI_Clipper\crosshairs so the overlay keeps working when the original
/// file is moved or deleted. The index lives in crosshairs.json next to them.
/// Pure file/JSON logic — unit-testable via the injectable root path.
/// </summary>
public sealed class CrosshairLibraryService
{
    private readonly string _dir;
    private readonly string _indexPath;
    private List<CrosshairEntry> _entries = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public CrosshairLibraryService(string? directory = null)
    {
        _dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WKI_Clipper", "crosshairs");
        _indexPath = Path.Combine(_dir, "crosshairs.json");
        Load();
    }

    public string Directory => _dir;

    public IReadOnlyList<CrosshairEntry> Entries => _entries;

    public void Load()
    {
        try
        {
            System.IO.Directory.CreateDirectory(_dir);
            if (!File.Exists(_indexPath)) { _entries = new(); return; }
            var json = File.ReadAllText(_indexPath);
            _entries = JsonSerializer.Deserialize<List<CrosshairEntry>>(json, JsonOptions) ?? new();
            // Drop index entries whose file vanished (manual delete in Explorer).
            _entries.RemoveAll(e => !File.Exists(FullPath(e)));
        }
        catch (Exception ex)
        {
            Logger.Warn("Crosshair library load failed: " + ex.Message);
            _entries = new();
        }
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(_dir);
            File.WriteAllText(_indexPath, JsonSerializer.Serialize(_entries, JsonOptions));
        }
        catch (Exception ex) { Logger.Warn("Crosshair library save failed: " + ex.Message); }
    }

    /// <summary>Absolute path of an entry's image file.</summary>
    public string FullPath(CrosshairEntry e) => Path.Combine(_dir, e.FileName);

    public CrosshairEntry? GetById(string? id)
        => string.IsNullOrEmpty(id) ? null : _entries.FirstOrDefault(e => e.Id == id);

    /// <summary>
    /// Copies a PNG into the library and indexes it. Returns the new entry, or null
    /// when the source is unreadable / not a supported image.
    /// </summary>
    public CrosshairEntry? Import(string sourcePath)
    {
        try
        {
            if (!File.Exists(sourcePath)) return null;
            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (ext != ".png") return null;   // PNG only — alpha is what makes a crosshair usable

            System.IO.Directory.CreateDirectory(_dir);
            var id = Guid.NewGuid().ToString("N")[..12];
            var fileName = id + ".png";
            File.Copy(sourcePath, Path.Combine(_dir, fileName), overwrite: false);

            var entry = new CrosshairEntry
            {
                Id = id,
                Name = Path.GetFileNameWithoutExtension(sourcePath),
                FileName = fileName,
                AddedUtc = DateTime.UtcNow
            };
            _entries.Add(entry);
            Save();
            Logger.Info($"Crosshair imported: {entry.Name} ({fileName})");
            return entry;
        }
        catch (Exception ex)
        {
            Logger.Warn("Crosshair import failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>Removes an entry and its copied file.</summary>
    public void Remove(string id)
    {
        var e = GetById(id);
        if (e is null) return;
        try { File.Delete(FullPath(e)); } catch { /* file may already be gone */ }
        _entries.Remove(e);
        Save();
        Logger.Info($"Crosshair removed: {e.Name}");
    }

    public void Rename(string id, string newName)
    {
        var e = GetById(id);
        if (e is null || string.IsNullOrWhiteSpace(newName)) return;
        e.Name = newName.Trim();
        Save();
    }
}
