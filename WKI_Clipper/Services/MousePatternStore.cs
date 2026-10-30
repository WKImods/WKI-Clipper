using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using WKI_Clipper.Services.AutoClicker;

namespace WKI_Clipper.Services;

/// <summary>Where the samples of the mouse patterns live; the settings only hold their metadata (mouse-patterns design 5.2).</summary>
public interface IMousePatternStore
{
    /// <summary>Writes the samples of a pattern, replacing earlier ones. Throws when the id is invalid or the file cannot be written.</summary>
    void Save(string id, PatternData data);

    /// <summary>False when the id is invalid or the file is missing, unreadable or corrupt; never throws.</summary>
    bool TryLoad(string id, [NotNullWhen(true)] out PatternData? data);

    /// <summary>Removes the samples of a pattern; nothing happens for an invalid id or a missing file.</summary>
    void Delete(string id);
}

/// <summary>
/// One file per pattern: "&lt;appData&gt;\mouse-patterns\&lt;id&gt;.wkipattern" holding the <see cref="PatternCodec"/> text. Only ids of
/// 32 hex characters (<see cref="MousePatternRules.IsValidId"/>) become file names, so a hand-edited id never reaches another path.
/// Writes go to a ".tmp" file first and then replace the old file, so a crash leaves the old samples or the new ones.
/// </summary>
public sealed class MousePatternStore : IMousePatternStore
{
    public const string FolderName = "mouse-patterns";
    public const string Extension = ".wkipattern";

    private readonly string _dir;

    public MousePatternStore(string appDataDir) => _dir = Path.Combine(appDataDir, FolderName);

    public string Directory => _dir;

    public void Save(string id, PatternData data)
    {
        if (!MousePatternRules.IsValidId(id)) throw new ArgumentException("Not a pattern id.", nameof(id));
        var text = PatternCodec.Encode(data);
        var path = PathOf(id);
        var tmp = path + ".tmp";
        try
        {
            System.IO.Directory.CreateDirectory(_dir);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                writer.Write(text);
                writer.Flush();
                fs.Flush(flushToDisk: true);   // on disk before it replaces the old samples
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    public bool TryLoad(string id, [NotNullWhen(true)] out PatternData? data)
    {
        data = null;
        if (!MousePatternRules.IsValidId(id)) return false;
        var path = PathOf(id);
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > PatternCodec.MaxEncodedLength) return false;
            if (PatternCodec.TryDecode(File.ReadAllText(path), out data)) return true;
            Logger.Warn($"MousePatterns: the samples of {id} are unreadable - the pattern needs a new recording.");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"MousePatterns: could not read the samples of {id}: {ex.Message}");
            return false;
        }
    }

    public void Delete(string id)
    {
        if (!MousePatternRules.IsValidId(id)) return;
        var path = PathOf(id);
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"MousePatterns: could not delete the samples of {id}: {ex.Message}");
        }
    }

    private string PathOf(string id) => Path.Combine(_dir, id + Extension);
}
