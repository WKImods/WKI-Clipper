using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WKI_Clipper.Services;

/// <summary>
/// The buffer folder the app actually wrote into last, kept in HKCU (not in settings.json: the settings file
/// can be reset or edited while segments are still on disk). Two jobs: after the user moves the buffer folder
/// the old folder's segments are deleted (a 30-minute ring is gigabytes), and the uninstaller reads it to
/// clean up a folder that is not the default one.
/// </summary>
public sealed class BufferFolderRecord
{
    internal const string KeyPath = @"Software\WKI_Clipper";
    internal const string ValueName = "BufferFolder";

    private readonly RegistryKey _root;

    public BufferFolderRecord() : this(Registry.CurrentUser) { }

    /// <summary>Test seam: a throw-away registry root (pattern of <see cref="AutoStartService"/>).</summary>
    internal BufferFolderRecord(RegistryKey root) => _root = root;

    /// <summary>The recorded folder, or null when missing or on any error.</summary>
    public string? Read()
    {
        try
        {
            using var key = _root.OpenSubKey(KeyPath, writable: false);
            var value = key?.GetValue(ValueName) as string;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex)
        {
            Logger.Warn("BufferFolderRecord.Read failed: " + ex.Message);
            return null;
        }
    }

    public void Write(string fullPath)
    {
        try
        {
            using var key = _root.CreateSubKey(KeyPath);
            key.SetValue(ValueName, fullPath, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            Logger.Warn("BufferFolderRecord.Write failed: " + ex.Message);
        }
    }

    // ---- low-RAM history of the RAM ring ----

    internal const string LowMemoryValueName = "RamBufferShortenedUtc";

    /// <summary>When the low-RAM guard last had to shorten the RAM ring (UTC), null when never / cleared.</summary>
    public DateTime? ReadLowMemory()
    {
        try
        {
            using var key = _root.OpenSubKey(KeyPath, writable: false);
            return key?.GetValue(LowMemoryValueName) is string s
                   && long.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long ticks)
                   && ticks > 0 && ticks <= DateTime.MaxValue.Ticks
                ? new DateTime(ticks, DateTimeKind.Utc)
                : null;
        }
        catch (Exception ex)
        {
            Logger.Warn("BufferFolderRecord.ReadLowMemory failed: " + ex.Message);
            return null;
        }
    }

    public void WriteLowMemory(DateTime? utc)
    {
        try
        {
            using var key = _root.CreateSubKey(KeyPath);
            if (utc is { } t) key.SetValue(LowMemoryValueName, t.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), RegistryValueKind.String);
            else key.DeleteValue(LowMemoryValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Logger.Warn("BufferFolderRecord.WriteLowMemory failed: " + ex.Message);
        }
    }

    // ---- folders this app created (only those, and the default folder, are ever hidden) ----

    internal const string CreatedValueName = "CreatedFolders";

    public bool WasCreated(string fullPath)
        => !string.IsNullOrWhiteSpace(fullPath)
           && ReadList(CreatedValueName).Any(p => !BufferFolderRules.IsDifferentFolder(p, fullPath));

    public void AddCreated(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || fullPath.Contains(PendingSeparator) || WasCreated(fullPath)) return;
        var list = ReadList(CreatedValueName).ToList();
        list.Add(fullPath);
        WriteList(CreatedValueName, list.TakeLast(16).ToList());   // a handful of moves at most; never grows unbounded
    }

    private IReadOnlyList<string> ReadList(string valueName)
    {
        try
        {
            using var key = _root.OpenSubKey(KeyPath, writable: false);
            var raw = key?.GetValue(valueName) as string;
            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
            return raw.Split(PendingSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception ex)
        {
            Logger.Warn($"BufferFolderRecord.ReadList({valueName}) failed: " + ex.Message);
            return Array.Empty<string>();
        }
    }

    private void WriteList(string valueName, List<string> list)
    {
        try
        {
            using var key = _root.CreateSubKey(KeyPath);
            if (list.Count == 0) key.DeleteValue(valueName, throwOnMissingValue: false);
            else key.SetValue(valueName, string.Join(PendingSeparator, list), RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            Logger.Warn($"BufferFolderRecord.WriteList({valueName}) failed: " + ex.Message);
        }
    }

    // ---- folders still to be emptied ----

    /// <summary>
    /// Old buffer folders whose segments have not been deleted yet. One plain string, '|' between the entries ('|'
    /// cannot occur in a Windows path, and the uninstaller reads it without any array handling): a folder stays
    /// listed until it is empty, so closing the app (or a long save) before the cleanup ran does not leave gigabytes
    /// that neither the app nor the uninstaller know about.
    /// </summary>
    internal const string PendingValueName = "PendingCleanup";
    private const char PendingSeparator = '|';

    public IReadOnlyList<string> ReadPending()
    {
        try
        {
            using var key = _root.OpenSubKey(KeyPath, writable: false);
            var raw = key?.GetValue(PendingValueName) as string;
            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
            return raw.Split(PendingSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception ex)
        {
            Logger.Warn("BufferFolderRecord.ReadPending failed: " + ex.Message);
            return Array.Empty<string>();
        }
    }

    public void AddPending(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || fullPath.Contains(PendingSeparator)) return;
        var list = ReadPending().ToList();
        if (list.Any(p => !BufferFolderRules.IsDifferentFolder(p, fullPath))) return;
        list.Add(fullPath);
        WritePending(list);
    }

    public void RemovePending(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return;   // IsDifferentFolder reads "blank" as "same" - would empty the list
        var list = ReadPending().ToList();
        int before = list.Count;
        list.RemoveAll(p => !BufferFolderRules.IsDifferentFolder(p, fullPath));
        if (list.Count != before) WritePending(list);
    }

    private void WritePending(List<string> list)
    {
        try
        {
            using var key = _root.CreateSubKey(KeyPath);
            if (list.Count == 0) key.DeleteValue(PendingValueName, throwOnMissingValue: false);
            else key.SetValue(PendingValueName, string.Join(PendingSeparator, list), RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            Logger.Warn("BufferFolderRecord.WritePending failed: " + ex.Message);
        }
    }
}

/// <summary>
/// What counts as "ours" in a buffer folder. The folder is user-chosen, so cleanup must never touch anything
/// else in it - and never a directory.
/// </summary>
public static class BufferFolderRules
{
    // [0-9] and \z on purpose (see PathRules).
    private static readonly Regex SegmentRx = new(@"^seg_[0-9]+_[0-9]+\.mp4\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] TempNames = { "_concat_list.txt", "_chapters.txt", "_gif_src.mp4", "_gif_list.txt" };

    /// <summary>seg_{gen}_{NN}.mp4 plus the helper files a save leaves in the folder.</summary>
    public static bool IsOwnBufferFile(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        foreach (var t in TempNames)
            if (string.Equals(fileName, t, StringComparison.OrdinalIgnoreCase)) return true;
        return SegmentRx.IsMatch(fileName);
    }

    /// <summary>Full-path compare, trailing separators trimmed, case-insensitive; a blank recording is "not different".</summary>
    public static bool IsDifferentFolder(string? recorded, string? current)
    {
        if (string.IsNullOrWhiteSpace(recorded) || string.IsNullOrWhiteSpace(current)) return false;
        try
        {
            return !string.Equals(Normalize(recorded), Normalize(current), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length
            ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : full;
    }

    /// <summary>True while at least one of our own files is left in the folder (top level only).</summary>
    public static bool HasOwnFiles(string? dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
                .Any(p => IsOwnBufferFile(Path.GetFileName(p)));
        }
        catch { return false; }
    }

    /// <summary>Top level only, own files only, best effort; never deletes directories. Returns how many files went.</summary>
    public static int DeleteOwnFiles(string? dir)
    {
        int deleted = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return 0;
            foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
            {
                if (!IsOwnBufferFile(Path.GetFileName(path))) continue;
                try { File.Delete(path); deleted++; }
                catch { /* locked by a running ffmpeg/concat - the next pass gets it */ }
            }
        }
        catch { }
        return deleted;
    }
}
