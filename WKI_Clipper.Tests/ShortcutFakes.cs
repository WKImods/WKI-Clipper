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

    /// <summary>The real catalog set (it contains wgc_api.exe on purpose, so the resolver tests run against the same combination as the app).</summary>
    public static IReadOnlySet<string> LauncherExeNames => LauncherCatalog.LauncherExeNames;

    // ---- the rest of Phillip's Start menu and desktop (read from the PC on 2026-10-20) ----

    private const string UserStartMenu = @"C:\Users\cr4zy\AppData\Roaming\Microsoft\Windows\Start Menu\Programs";
    private const string UserDesktop = @"C:\Users\cr4zy\Desktop";

    public const string SteamExePath = @"C:\Program Files (x86)\Steam\steam.exe";
    public const string EaLauncherPath = @"C:\Program Files\Electronic Arts\EA Desktop\EA Desktop\EALauncher.exe";
    public const string EpicLauncherPath = @"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win32\EpicGamesLauncher.exe";
    public const string GogGalaxyPath = @"C:\Program Files (x86)\GOG Galaxy\GalaxyClient.exe";
    public const string UbisoftConnectPath = @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\UbisoftConnect.exe";
    public const string BattleNetPath = @"C:\Program Files (x86)\Battle.net\Battle.net Launcher.exe";
    public const string RockstarPath = @"C:\Program Files\Rockstar Games\Launcher\LauncherPatcher.exe";
    public const string WargamingPath = @"C:\ProgramData\Wargaming.net\GameCenter\wgc.exe";
    public const string CometPath = @"C:\Users\cr4zy\AppData\Local\Perplexity\Comet\Application\comet.exe";
    public const string ChromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    public const string BravePath = @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe";
    public const string IePath = @"C:\Program Files\Internet Explorer\iexplore.exe";
    public const string XboxAumid = "Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App";

    public static readonly ShellLinkInfo RockstarLink = Link(
        StartMenu + @"\Rockstar Games\Rockstar Games Launcher.lnk", RockstarPath, null, @"C:\Program Files\Rockstar Games\Launcher\");
    public static readonly ShellLinkInfo RockstarDesktopLink = Link(
        Desktop + @"\Rockstar Games Launcher.lnk", RockstarPath, null, @"C:\Program Files\Rockstar Games\Launcher\");
    public static readonly ShellLinkInfo EaLink = Link(
        StartMenu + @"\EA\EA.lnk", EaLauncherPath, null, @"C:\Program Files\Electronic Arts\EA Desktop\EA Desktop\");
    public static readonly ShellLinkInfo EaAppRecovery = Link(
        StartMenu + @"\EA\App Recovery.lnk", EaLauncherPath, "-recovery", @"C:\Program Files\Electronic Arts\EA Desktop\EA Desktop");
    public static readonly ShellLinkInfo EaAppUpdater = Link(
        StartMenu + @"\EA\EA app-Updater.lnk", @"C:\Program Files\Electronic Arts\EA Desktop\EA Desktop\EAUpdater.exe");
    public static readonly ShellLinkInfo EaUpdater = Link(
        StartMenu + @"\EA\EA Updater.lnk", @"C:\Program Files\Electronic Arts\EA Desktop\EA Desktop\EAUpdater.exe");
    public static readonly ShellLinkInfo EaErrorReporter = Link(
        StartMenu + @"\EA\EA Error Reporter.lnk", @"C:\Program Files\Electronic Arts\EA Desktop\EA Desktop\ErrorReporter.exe");
    public static readonly ShellLinkInfo EpicLink = Link(
        StartMenu + @"\Epic Games Launcher.lnk", EpicLauncherPath, null, @"C:\Program Files (x86)\Epic Games\");
    public static readonly ShellLinkInfo GogLink = Link(
        StartMenu + @"\GOG.com\GOG GALAXY\GOG GALAXY.lnk", GogGalaxyPath, null, @"C:\Program Files (x86)\GOG Galaxy");
    /// <summary>A GOG GAME shortcut: it targets the launcher exe, too, but with arguments that start the game.</summary>
    public static readonly ShellLinkInfo GogCyberpunk = Link(
        StartMenu + @"\Cyberpunk 2077 [GOG.com]\Cyberpunk 2077.lnk", GogGalaxyPath,
        "/command=runGame /gameId=1423049311 /path=\"D:\\Program Files\\GOG Galaxy\\Cyberpunk 2077\"", @"C:\Program Files (x86)\GOG Galaxy");
    public static readonly ShellLinkInfo UbisoftLink = Link(
        StartMenu + @"\Ubisoft\Ubisoft Connect\Ubisoft Connect.lnk", UbisoftConnectPath, null, @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher");
    public static readonly ShellLinkInfo UbisoftDesktopLink = Link(
        Desktop + @"\Ubisoft Connect.lnk", UbisoftConnectPath, null, @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher");
    public static readonly ShellLinkInfo BattleNetLink = Link(
        StartMenu + @"\Battle.net\Battle.net.lnk", BattleNetPath, null, @"C:\Program Files (x86)\Battle.net");
    public static readonly ShellLinkInfo SteamStartMenuLink = Link(
        UserStartMenu + @"\Steam\Steam.lnk", SteamExePath, null, @"C:\Program Files (x86)\Steam");
    public static readonly ShellLinkInfo SteamDesktopLink = Link(
        UserDesktop + @"\Steam.lnk", @"C:\Program Files (x86)\Steam\Steam.exe", null, @"C:\Program Files (x86)\Steam");

    /// <summary>Everything the Start menu and the desktops hold, in the order a scan lists it (updaters and helpers before the launchers on purpose).</summary>
    public static IReadOnlyList<ShellLinkInfo> StartMenuLinks() => new[]
    {
        EaAppRecovery, EaAppUpdater, EaErrorReporter, EaUpdater, EaLink,
        GogCyberpunk, GogLink,
        WotUninstall, WotStart, WargamingGameCenter, AslainLogsArchiver,
        RockstarDesktopLink, RockstarLink,
        UbisoftDesktopLink, UbisoftLink,
        SteamDesktopLink, SteamStartMenuLink,
        EpicLink, BattleNetLink,
    };

    /// <summary>The files the links above point at (an installed launcher has its exe on disk).</summary>
    public static FakeFileSystem Programs()
    {
        return new FakeFileSystem()
            .Add(SteamExePath).Add(EaLauncherPath)
            .Add(@"C:\Program Files\Electronic Arts\EA Desktop\EA Desktop\EAUpdater.exe")
            .Add(@"C:\Program Files\Electronic Arts\EA Desktop\EA Desktop\ErrorReporter.exe")
            .Add(EpicLauncherPath).Add(GogGalaxyPath).Add(UbisoftConnectPath).Add(BattleNetPath)
            .Add(RockstarPath).Add(WargamingPath)
            .Add(CometPath).Add(ChromePath).Add(BravePath).Add(IePath);
    }

    private const string StartMenuInternet = @"SOFTWARE\Clients\StartMenuInternet";
    private const string StartMenuInternet32 = @"SOFTWARE\WOW6432Node\Clients\StartMenuInternet";

    private static void AddBrowser(FakeRegistry reg, string hive, string root, string key, string displayName, string command)
    {
        reg.Set(hive, root + "\\" + key, null, displayName);
        reg.Set(hive, root + "\\" + key + @"\shell\open\command", null, command);
    }

    /// <summary>The StartMenuInternet registry as it is on the PC: Brave and Chrome in HKLM (and mirrored in WOW6432Node), Comet in HKCU, IE registered.</summary>
    public static FakeRegistry BrowserRegistry()
    {
        var reg = new FakeRegistry();
        foreach (var root in new[] { StartMenuInternet, StartMenuInternet32 })
        {
            AddBrowser(reg, "HKLM", root, "Brave", "Brave", "\"" + BravePath + "\"");
            AddBrowser(reg, "HKLM", root, "Google Chrome", "Google Chrome", "\"" + ChromePath + "\"");
            AddBrowser(reg, "HKLM", root, "IEXPLORE.EXE", "Internet Explorer", IePath);
        }
        AddBrowser(reg, "HKCU", StartMenuInternet, "Comet.LMIUB2OZQHSTWDG23CICJZPPGU", "Comet", "\"" + CometPath + "\"");
        return reg;
    }
}

/// <summary>
/// A shell that starts nothing: it records what it was asked (in <see cref="Log"/>, shared with the test's closeBoard lambda
/// so the ORDER of "close" and "start" is visible) and answers from fixtures. <see cref="ScanGate"/> lets a test hold a scan open.
/// </summary>
internal sealed class FakeShell : IShortcutShell
{
    public List<string> Log { get; } = new();
    public IReadOnlyList<ShellLinkInfo> Links { get; set; } = PhillipsPc.StartMenuLinks();
    public string? DefaultBrowserExe { get; set; } = PhillipsPc.CometPath;
    public HashSet<string> StoreApps { get; } = new(StringComparer.Ordinal);
    public List<LaunchTarget> Started { get; } = new();
    public bool StartSucceeds { get; set; } = true;
    public string? StartError { get; set; } = "the shell said no";
    public int ScanCount;
    /// <summary>When set, a scan blocks here until the test lets it go (after signalling <see cref="ScanEntered"/>).</summary>
    public System.Threading.ManualResetEventSlim? ScanGate { get; set; }
    public System.Threading.ManualResetEventSlim ScanEntered { get; } = new(false);
    /// <summary>Like <see cref="ScanGate"/>, but for the icon step, which runs AFTER the scan took its snapshot of the list. Only the FIRST icon read waits.</summary>
    public System.Threading.ManualResetEventSlim? IconGate { get => _iconGate; set { _iconGate = value; _iconGateTaken = 0; } }
    private System.Threading.ManualResetEventSlim? _iconGate;
    private int _iconGateTaken;
    public System.Threading.ManualResetEventSlim IconEntered { get; } = new(false);

    public IReadOnlyList<ShellLinkInfo> StartMenuAndDesktopLinks()
    {
        System.Threading.Interlocked.Increment(ref ScanCount);
        lock (Log) Log.Add("scan");
        ScanEntered.Set();
        ScanGate?.Wait(TimeSpan.FromSeconds(10));
        return Links;
    }

    public ShellLinkInfo? ReadLink(string path) => Links.FirstOrDefault(l => ShortcutPaths.SamePath(l.Path, path));
    public string? WindowsDefaultBrowserExe() => DefaultBrowserExe;
    public bool StoreAppInstalled(string aumid) => StoreApps.Contains(aumid);

    public string? IconPng(string sourcePath, string cacheKey)
    {
        IconEntered.Set();
        var gate = _iconGate;
        if (gate != null && System.Threading.Interlocked.Exchange(ref _iconGateTaken, 1) == 0) gate.Wait(TimeSpan.FromSeconds(10));
        return string.IsNullOrWhiteSpace(sourcePath) ? null : @"C:\icons\" + cacheKey.Replace(':', '_') + ".png";
    }

    public bool Start(LaunchTarget target, out string? error)
    {
        lock (Log) Log.Add($"start:{target.Kind}:{target.Target}");
        Started.Add(target);
        error = StartSucceeds ? null : StartError;
        return StartSucceeds;
    }
}

/// <summary>In-memory registry: case-insensitive keys, "HKLM"/"HKCU" hives, null value name = the key's default value.</summary>
internal sealed class FakeRegistry : IRegistryView
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);

    public FakeRegistry Set(string hive, string key, string? value, string data)
    {
        var k = key.Trim('\\');
        _values[hive + "|" + k + "|" + (value ?? "")] = data;
        for (var p = k; p.Length > 0; p = p.Contains('\\') ? p[..p.LastIndexOf('\\')] : "")
            _keys.Add(hive + "|" + p);
        return this;
    }

    public string? GetString(string hive, string key, string? value)
        => _values.TryGetValue(hive + "|" + key.Trim('\\') + "|" + (value ?? ""), out var v) ? v : null;

    public IReadOnlyList<string> SubKeys(string hive, string key)
    {
        var prefix = hive + "|" + key.Trim('\\') + "\\";
        return _keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && k.IndexOf('\\', prefix.Length) < 0)
            .Select(k => k[prefix.Length..])
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
