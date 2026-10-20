using System;
using System.IO;
using System.Linq;
using System.Text;

namespace WKI_Clipper.Services;

/// <summary>
/// Windows path comparison without touching the disk: either slash, any case, trailing and doubled
/// separators ignored. Steam writes "D:\\SteamLibrary", a process reports "d:\steamlibrary\", Epic
/// uses "/" in launch executables - all of them must meet in the same place.
/// </summary>
internal static class ShortcutPaths
{
    /// <summary>"D:/Steam//Lib\" becomes "D:\Steam\Lib". Roots keep their separator ("C:\"). Empty for null/blank.</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var p = path.Trim().Trim('"').Replace('/', '\\');

        // Collapse doubled separators, but keep the leading "\\" of a UNC path.
        bool unc = p.StartsWith(@"\\", StringComparison.Ordinal);
        var sb = new StringBuilder(p.Length);
        char prev = '\0';
        foreach (char c in p)
        {
            if (c == '\\' && prev == '\\') continue;
            sb.Append(c);
            prev = c;
        }
        p = sb.ToString();
        if (unc) p = @"\" + p;

        while (p.Length > 3 && p[^1] == '\\') p = p[..^1];
        if (p.Length == 2 && p[1] == ':') p += "\\";
        return p;
    }

    public static bool SamePath(string? a, string? b)
        => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is <paramref name="dir"/> itself or lies anywhere below it.</summary>
    public static bool IsSameOrUnder(string? path, string? dir)
    {
        var p = Normalize(path);
        var d = Normalize(dir);
        if (p.Length == 0 || d.Length == 0) return false;
        if (string.Equals(p, d, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = d.EndsWith('\\') ? d : d + "\\";
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Joins segments with single backslashes, ignoring empty ones. The first segment may be a drive or UNC root.</summary>
    public static string Combine(params string?[] parts)
    {
        var used = parts.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        if (used.Length == 0) return "";
        var sb = new StringBuilder(used[0]!.TrimEnd('\\', '/'));
        for (int i = 1; i < used.Length; i++)
            sb.Append('\\').Append(used[i]!.Trim('\\', '/'));
        return Normalize(sb.ToString());
    }

    public static string? DirectoryOf(string? path)
    {
        var p = Normalize(path);
        return p.Length == 0 ? null : Path.GetDirectoryName(p);
    }

    public static string FileName(string? path)
    {
        var p = Normalize(path);
        return p.Length == 0 ? "" : Path.GetFileName(p);
    }

    /// <summary>Number of path segments including the drive ("C:\Games\Foo" is 3).</summary>
    public static int SegmentCount(string? path)
        => Normalize(path).Split('\\', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Lower-case letters and digits only: "World_of_Tanks EU" and "WorldOfTanks" become comparable.</summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
