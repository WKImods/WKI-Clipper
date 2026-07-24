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
public sealed class CrosshairLibraryService : IDisposable
{
    private readonly string _dir;
    private readonly string _indexPath;
    private List<CrosshairEntry> _entries = new();

    private FileSystemWatcher? _watcher;
    private System.Timers.Timer? _debounce;

    /// <summary>Raised (off the UI thread) when the folder content changed and was re-indexed.</summary>
    public event Action? LibraryChanged;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public CrosshairLibraryService(string? directory = null)
    {
        _dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WKI_Clipper", "crosshairs");
        _indexPath = Path.Combine(_dir, "crosshairs.json");
        Load();
        ScanFolder();     // pick up PNGs that were dropped in manually while we were off
        StartWatching();
    }

    /// <summary>
    /// Indexes any PNG sitting in the library folder that isn't known yet — so simply
    /// dropping files into the folder works, no import dialog needed. Files that are
    /// still being copied are skipped and picked up by the next watcher event.
    /// Returns the number of newly indexed files.
    /// </summary>
    public int ScanFolder()
    {
        int added = 0;
        try
        {
            System.IO.Directory.CreateDirectory(_dir);
            foreach (var file in System.IO.Directory.EnumerateFiles(_dir, "*.png"))
            {
                var fileName = Path.GetFileName(file);
                if (_entries.Exists(e => string.Equals(e.FileName, fileName, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (!IsReadable(file)) continue;   // still being written → try again later

                _entries.Add(new CrosshairEntry
                {
                    // The file name IS the identity for hand-placed files; keeps the
                    // index readable and stable across restarts.
                    Id = Path.GetFileNameWithoutExtension(fileName),
                    Name = Path.GetFileNameWithoutExtension(fileName),
                    FileName = fileName,
                    AddedUtc = DateTime.UtcNow
                });
                added++;
                Logger.Info($"Crosshair auto-detected in folder: {fileName}");
            }

            // Drop entries whose file was deleted in Explorer.
            int removed = _entries.RemoveAll(e => !File.Exists(FullPath(e)));
            if (added > 0 || removed > 0) Save();
        }
        catch (Exception ex) { Logger.Warn("Crosshair folder scan failed: " + ex.Message); }
        return added;
    }

    private static bool IsReadable(string path)
    {
        try
        {
            using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return fs.Length > 0;
        }
        catch { return false; }
    }

    /// <summary>Watches the folder so files dropped in via Explorer show up live.</summary>
    private void StartWatching()
    {
        try
        {
            System.IO.Directory.CreateDirectory(_dir);

            // Copies fire several events; coalesce them into one rescan.
            _debounce = new System.Timers.Timer(500) { AutoReset = false };
            _debounce.Elapsed += (_, _) =>
            {
                if (ScanFolder() >= 0) LibraryChanged?.Invoke();
            };

            _watcher = new FileSystemWatcher(_dir, "*.png")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            void Bump(object? s, FileSystemEventArgs e) { _debounce.Stop(); _debounce.Start(); }
            _watcher.Created += Bump;
            _watcher.Deleted += Bump;
            _watcher.Changed += Bump;
            _watcher.Renamed += (s, e) => Bump(s, e);
        }
        catch (Exception ex) { Logger.Warn("Crosshair folder watcher failed: " + ex.Message); }
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

    public void Dispose()
    {
        try { if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); } } catch { }
        try { _debounce?.Dispose(); } catch { }
    }
}
