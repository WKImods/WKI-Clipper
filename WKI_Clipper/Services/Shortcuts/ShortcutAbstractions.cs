using System.Collections.Generic;

namespace WKI_Clipper.Services;

/// <summary>
/// Read-only view of the file system. The shortcut logic talks to this instead of System.IO, so every
/// rule is tested against a fake that holds the facts of a real PC (see the shortcut bar spec, section 3).
/// </summary>
public interface IFileSystemView
{
    bool FileExists(string p);
    bool DirExists(string p);

    /// <summary>The file's text, or null when it does not exist or cannot be read.</summary>
    string? ReadAllText(string p);

    /// <summary>Full paths of the files directly inside <paramref name="dir"/> that match <paramref name="pattern"/> (no recursion).</summary>
    IEnumerable<string> EnumerateFiles(string dir, string pattern);
}

/// <summary>
/// A .lnk or .url file read from the Start menu or the desktop. A .lnk has <see cref="Target"/> (and arguments and
/// working folder); a .url has <see cref="Url"/> only. <see cref="Name"/> is the file name without extension.
/// </summary>
public sealed record ShellLinkInfo(string Path, string Name, string? Target, string? Arguments, string? WorkingDir, string? Url);

/// <summary>Read-only view of the registry. <c>hive</c> is "HKLM" or "HKCU"; a null <c>value</c> reads the key's default value.</summary>
public interface IRegistryView
{
    string? GetString(string hive, string key, string? value);
    IReadOnlyList<string> SubKeys(string hive, string key);
}
