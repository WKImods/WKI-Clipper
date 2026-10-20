using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using WKI_Clipper.Services;

namespace WKI_Clipper.Tests;

/// <summary>
/// In-memory file system for the shortcut tests: Windows semantics (case-insensitive, either slash,
/// trailing slashes ignored), direct-children-only enumeration like Directory.EnumerateFiles.
/// </summary>
internal sealed class FakeFileSystem : IFileSystemView
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);

    public FakeFileSystem Add(string path, string content = "")
    {
        var p = ShortcutPaths.Normalize(path);
        _files[p] = content;
        for (var d = System.IO.Path.GetDirectoryName(p); !string.IsNullOrEmpty(d); d = System.IO.Path.GetDirectoryName(d))
            _dirs.Add(d);
        return this;
    }

    public FakeFileSystem AddDir(string path)
    {
        _dirs.Add(ShortcutPaths.Normalize(path));
        return this;
    }

    public bool FileExists(string p) => _files.ContainsKey(ShortcutPaths.Normalize(p));

    public bool DirExists(string p) => _dirs.Contains(ShortcutPaths.Normalize(p));

    public string? ReadAllText(string p) => _files.TryGetValue(ShortcutPaths.Normalize(p), out var t) ? t : null;

    public IEnumerable<string> EnumerateFiles(string dir, string pattern)
    {
        var d = ShortcutPaths.Normalize(dir);
        var rx = new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return _files.Keys
            .Where(f => string.Equals(System.IO.Path.GetDirectoryName(f), d, StringComparison.OrdinalIgnoreCase)
                        && rx.IsMatch(System.IO.Path.GetFileName(f)))
            .ToList();
    }
}

/// <summary>
/// The facts of Phillip's PC (shortcut bar spec section 3), as test fixtures. Read from the real machine on 2026-10-20.
/// </summary>
internal static class PhillipsPc
{
    public const string SteamRoot = @"C:\Program Files (x86)\Steam";
    public const string SteamLibraryD = @"D:\SteamLibrary";
    public const string EpicManifests = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";

    public const string LibraryFoldersVdf = """
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"C:\\Program Files (x86)\\Steam"
        		"label"		""
        		"contentid"		"167806308797938513"
        		"totalsize"		"0"
        		"apps"
        		{
        			"228980"		"274526052"
        		}
        	}
        	"1"
        	{
        		"path"		"D:\\SteamLibrary"
        		"label"		""
        		"contentid"		"8574334404160642639"
        		"totalsize"		"2048390066176"
        		"apps"
        		{
        			"221100"		"33181025746"
        			"1867240"		"48905446067"
        			"1874880"		"31384776198"
        		}
        	}
        }
        """;

    public static string Acf(int appId, string name, string installDir) => $$"""
        "AppState"
        {
        	"appid"		"{{appId}}"
        	"Universe"		"1"
        	"name"		"{{name}}"
        	"StateFlags"		"4"
        	"installdir"		"{{installDir}}"
        	"SizeOnDisk"		"274526052"
        }
        """;

    /// <summary>Steam root + D:\SteamLibrary with the real manifests (redistributables, Arma Reforger, DayZ, WARDOGS).</summary>
    public static FakeFileSystem Steam()
    {
        return new FakeFileSystem()
            .Add(SteamRoot + @"\steamapps\libraryfolders.vdf", LibraryFoldersVdf)
            .Add(SteamRoot + @"\steamapps\appmanifest_228980.acf", Acf(228980, "Steamworks Common Redistributables", "Steamworks Shared"))
            .Add(SteamLibraryD + @"\steamapps\appmanifest_1874880.acf", Acf(1874880, "Arma Reforger", "Arma Reforger"))
            .Add(SteamLibraryD + @"\steamapps\appmanifest_221100.acf", Acf(221100, "DayZ", "DayZ"))
            .Add(SteamLibraryD + @"\steamapps\appmanifest_1867240.acf", Acf(1867240, "WARDOGS", "Wardogs"));
    }

    public const string FortniteItem = """
        {
          "FormatVersion": 0,
          "AppName": "Fortnite",
          "MainGameAppName": "Fortnite",
          "DisplayName": "Fortnite",
          "InstallLocation": "D:\\Program Files\\Epic Games\\Fortnite",
          "LaunchExecutable": "FortniteGame/Binaries/Win64/FortniteLauncher.exe",
          "bIsApplication": true
        }
        """;

    public const string SpiderManItem = """
        {
          "FormatVersion": 0,
          "AppName": "be23672deb69402781cd47cc2919caf4",
          "MainGameAppName": "be23672deb69402781cd47cc2919caf4",
          "DisplayName": "Marvel's Spider-Man Remastered",
          "InstallLocation": "D:\\Program Files\\Epic Games\\MarvelsSpiderManRemaster",
          "LaunchExecutable": "Spider-Man.exe",
          "bIsApplication": true
        }
        """;

    public static FakeFileSystem Epic()
    {
        return new FakeFileSystem()
            .Add(EpicManifests + @"\04DE3AA7479EAD771A19AD998B5D28EE.item", FortniteItem)
            .Add(EpicManifests + @"\AC9D089C45ED89C04E726F882127FBD6.item", SpiderManItem);
    }

    // ---- shortcuts (Start menu + Desktop) ----

    public static ShellLinkInfo Link(string path, string target, string? args = null, string? workDir = null)
        => new(path, System.IO.Path.GetFileNameWithoutExtension(path), target, args, workDir, null);

    private const string StartMenu = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs";
    private const string Desktop = @"C:\Users\Public\Desktop";

    public static readonly ShellLinkInfo WotStart = Link(
        StartMenu + @"\Wargaming.net\World_of_Tanks_EU\World of Tanks EU.lnk",
        @"C:\Games\World_of_Tanks_EU\wgc_api.exe", "--open");

    public static readonly ShellLinkInfo WotUninstall = Link(
        StartMenu + @"\Wargaming.net\World_of_Tanks_EU\World of Tanks EU deinstallieren.lnk",
        @"C:\Games\World_of_Tanks_EU\wgc_api.exe", "--uninstall", @"C:\Games\World_of_Tanks_EU");

    /// <summary>A real helper shortcut that sits in the same game folder (its target is in the WoT folder, too).</summary>
    public static readonly ShellLinkInfo AslainLogsArchiver = Link(
        Desktop + @"\Aslains Logs Archiver.lnk",
        @"C:\Games\World_of_Tanks_EU\Aslains_Logs_Archiver.exe", null, @"C:\Games\World_of_Tanks_EU");

    public static readonly ShellLinkInfo WargamingGameCenter = Link(
        StartMenu + @"\Wargaming.net\Wargaming.net Game Center.lnk",
        @"C:\ProgramData\Wargaming.net\GameCenter\wgc.exe", null, @"C:\ProgramData\Wargaming.net\GameCenter");

    public static readonly ShellLinkInfo SteamLink = Link(
        StartMenu + @"\Steam\Steam.lnk", @"C:\Program Files (x86)\Steam\Steam.exe", null, @"C:\Program Files (x86)\Steam");

    /// <summary>Launcher exe names as the catalog will hand them to the resolver (superset of what the tests here need).</summary>
    public static readonly IReadOnlySet<string> LauncherExeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "steam.exe", "EALauncher.exe", "EpicGamesLauncher.exe", "GalaxyClient.exe", "UbisoftConnect.exe",
        "Battle.net Launcher.exe", "LauncherPatcher.exe", "wgc.exe",
        "EADesktop.exe", "Battle.net.exe", "Launcher.exe", "wgc_api.exe",
    };
}
