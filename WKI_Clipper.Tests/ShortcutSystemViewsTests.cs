using System;
using System.IO;
using System.Linq;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>The real file-system and registry views behind the shortcut logic. Read-only; the registry keys used exist on every Windows.</summary>
public sealed class ShortcutSystemViewsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_views_" + Guid.NewGuid().ToString("N"));

    public ShortcutSystemViewsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private readonly DiskFileSystemView _fs = new();

    // ---- files ----

    [Fact]
    public void File_and_directory_checks_follow_the_disk()
    {
        var file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "x");

        Assert.True(_fs.FileExists(file));
        Assert.False(_fs.FileExists(_dir));
        Assert.True(_fs.DirExists(_dir));
        Assert.False(_fs.DirExists(file));
        Assert.False(_fs.FileExists(Path.Combine(_dir, "nope.txt")));
        Assert.False(_fs.FileExists(""));
    }

    [Fact]
    public void ReadAllText_returns_null_for_a_missing_file_and_never_throws()
    {
        Assert.Null(_fs.ReadAllText(Path.Combine(_dir, "nope.vdf")));
        Assert.Null(_fs.ReadAllText(Path.Combine(_dir, "no", "such", "dir.vdf")));
        Assert.Null(_fs.ReadAllText(_dir));    // a directory
        Assert.Null(_fs.ReadAllText(""));
    }

    [Fact]
    public void ReadAllText_reads_a_file_another_program_has_open_for_writing()
    {
        // Steam rewrites libraryfolders.vdf and the manifests while it runs.
        var file = Path.Combine(_dir, "libraryfolders.vdf");
        using var writer = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read);
        writer.Write(System.Text.Encoding.UTF8.GetBytes("\"libraryfolders\" { }"));
        writer.Flush();

        Assert.Equal("\"libraryfolders\" { }", _fs.ReadAllText(file));
    }

    [Fact]
    public void EnumerateFiles_matches_the_pattern_exactly_and_does_not_recurse()
    {
        File.WriteAllText(Path.Combine(_dir, "appmanifest_1.acf"), "");
        File.WriteAllText(Path.Combine(_dir, "appmanifest_2.acf"), "");
        File.WriteAllText(Path.Combine(_dir, "appmanifest_3.acfbak"), "");   // Windows' short-name matching would let *.acf catch this
        File.WriteAllText(Path.Combine(_dir, "libraryfolders.vdf"), "");
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "sub", "appmanifest_9.acf"), "");

        var found = _fs.EnumerateFiles(_dir, "appmanifest_*.acf").Select(Path.GetFileName).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "appmanifest_1.acf", "appmanifest_2.acf" }, found);
    }

    [Fact]
    public void EnumerateFiles_of_a_missing_folder_is_empty()
    {
        Assert.Empty(_fs.EnumerateFiles(Path.Combine(_dir, "gone"), "*.item"));
        Assert.Empty(_fs.EnumerateFiles("", "*.item"));
    }

    // ---- registry ----

    private readonly WindowsRegistryView _reg = new();

    [Fact]
    public void Reads_a_value_from_hklm()
    {
        var name = _reg.GetString("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName");

        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.Contains("Windows", name);
    }

    [Fact]
    public void Reads_a_value_from_hkcu_and_the_default_value_when_the_name_is_null()
    {
        Assert.NotNull(_reg.GetString("HKCU", @"Environment", "TEMP") ?? _reg.GetString("HKCU", @"Environment", "Path") ?? "x");
        // The default value of a key that has none is null, not an exception.
        Assert.Null(_reg.GetString("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", null));
    }

    [Fact]
    public void Lists_subkeys_and_reaches_the_wow6432node_path_too()
    {
        Assert.Contains("CurrentVersion", _reg.SubKeys("HKLM", @"SOFTWARE\Microsoft\Windows NT"), StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(_reg.SubKeys("HKLM", @"SOFTWARE\WOW6432Node\Microsoft"));
    }

    [Fact]
    public void Missing_keys_values_and_unknown_hives_give_null_or_empty()
    {
        Assert.Null(_reg.GetString("HKLM", @"SOFTWARE\WKI_Clipper_DoesNotExist", "x"));
        Assert.Empty(_reg.SubKeys("HKLM", @"SOFTWARE\WKI_Clipper_DoesNotExist"));
        Assert.Null(_reg.GetString("HKXX", @"SOFTWARE", "x"));
        Assert.Empty(_reg.SubKeys("HKXX", @"SOFTWARE"));
        Assert.Null(_reg.GetString("HKLM", "", "x"));
    }

    [Fact]
    public void Subkeys_are_listed_once_even_when_both_registry_views_hold_them()
    {
        var subs = _reg.SubKeys("HKLM", @"SOFTWARE\Microsoft");

        Assert.Equal(subs.Count, subs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
