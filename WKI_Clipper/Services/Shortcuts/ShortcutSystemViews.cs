using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Security;
using Microsoft.Win32;

namespace WKI_Clipper.Services;

/// <summary>
/// The real disk behind <see cref="IFileSystemView"/>. Read-only and forgiving: a missing folder, a locked or half-written
/// file or an odd path is "not there", never an exception (Steam rewrites its files while the clipper reads them).
/// </summary>
public sealed class DiskFileSystemView : IFileSystemView
{
    public bool FileExists(string p)
    {
        try { return !string.IsNullOrWhiteSpace(p) && File.Exists(p); }
        catch (Exception) { return false; }
    }

    public bool DirExists(string p)
    {
        try { return !string.IsNullOrWhiteSpace(p) && Directory.Exists(p); }
        catch (Exception) { return false; }
    }

    public string? ReadAllText(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        try
        {
            using var stream = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception) { return null; }
    }

    public IEnumerable<string> EnumerateFiles(string dir, string pattern)
    {
        if (string.IsNullOrWhiteSpace(dir)) return Array.Empty<string>();
        try
        {
            // The Win32 pattern matcher also compares 8.3 short names, so "*.acf" would let "x.acfbak" through;
            // the exact matcher drops those.
            return Directory.EnumerateFiles(dir, pattern)
                .Where(f => FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(f), ignoreCase: true))
                .ToList();
        }
        catch (Exception) { return Array.Empty<string>(); }
    }
}

/// <summary>
/// The real registry behind <see cref="IRegistryView"/>. HKLM is read in the 64-bit view and in the 32-bit view (the
/// WOW6432Node), HKCU in the default view: a 32-bit installer (EA, an old browser) leaves its keys in the other half.
/// Missing keys, denied keys and unknown hives are "not there".
/// </summary>
public sealed class WindowsRegistryView : IRegistryView
{
    public string? GetString(string hive, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        foreach (var view in Views(hive))
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(view.Hive, view.View);
                using var sub = baseKey.OpenSubKey(key);
                if (sub?.GetValue(value ?? "") is string s && s.Length > 0) return s;   // REG_EXPAND_SZ arrives expanded
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
            {
                // unreadable = not there
            }
        }
        return null;
    }

    public IReadOnlyList<string> SubKeys(string hive, string key)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(key)) return names;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in Views(hive))
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(view.Hive, view.View);
                using var sub = baseKey.OpenSubKey(key);
                if (sub == null) continue;
                foreach (var name in sub.GetSubKeyNames())
                    if (seen.Add(name)) names.Add(name);
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
            {
                // unreadable = not there
            }
        }
        return names;
    }

    private static IEnumerable<(RegistryHive Hive, RegistryView View)> Views(string hive)
    {
        if (string.Equals(hive, "HKLM", StringComparison.OrdinalIgnoreCase))
        {
            yield return (RegistryHive.LocalMachine, RegistryView.Registry64);
            yield return (RegistryHive.LocalMachine, RegistryView.Registry32);
        }
        else if (string.Equals(hive, "HKCU", StringComparison.OrdinalIgnoreCase))
        {
            yield return (RegistryHive.CurrentUser, RegistryView.Default);
        }
    }
}
