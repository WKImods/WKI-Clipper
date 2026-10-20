using System;
using System.Drawing;
using System.IO;
using System.Linq;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;
using Xunit.Abstractions;

namespace WKI_Clipper.Tests;

/// <summary>
/// The REAL shell on this PC (Phillip's, with Steam, the Xbox app and Comet). Opt-in with WKI_LIVE=1; every test returns at
/// once without it. Read-only: nothing here starts a program - <c>Start</c> is only tried with an exe that does not exist.
/// Icons go into a temp folder, not into the real cache.
/// </summary>
public sealed class ShortcutNativeLiveTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _iconDir = Path.Combine(Path.GetTempPath(), "wki_icons_" + Guid.NewGuid().ToString("N"));

    public ShortcutNativeLiveTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        try { Directory.Delete(_iconDir, recursive: true); } catch { }
    }

    private static bool Enabled => Environment.GetEnvironmentVariable("WKI_LIVE") == "1";

    private Win32ShortcutShell NewShell() => new(_iconDir);

    [Fact]
    public void Reads_steam_start_menu_link_target()
    {
        if (!Enabled) return;

        var links = NewShell().StartMenuAndDesktopLinks();
        _out.WriteLine($"{links.Count} links");

        Assert.NotEmpty(links);
        var steam = links.FirstOrDefault(l => l.Name.Equals("Steam", StringComparison.OrdinalIgnoreCase)
                                              && l.Target != null && l.Target.EndsWith("steam.exe", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(steam);
        Assert.True(File.Exists(steam!.Target));

        // The single-link read gives the same answer as the scan.
        var again = NewShell().ReadLink(steam.Path);
        Assert.NotNull(again);
        Assert.Equal(steam.Target, again!.Target);
    }

    [Fact]
    public void Scan_sees_the_world_of_tanks_start_and_uninstall_shortcuts_with_their_arguments()
    {
        if (!Enabled) return;

        var links = NewShell().StartMenuAndDesktopLinks();

        var start = links.FirstOrDefault(l => l.Name == "World of Tanks EU");
        Assert.NotNull(start);
        Assert.Equal("--open", start!.Arguments);
        Assert.EndsWith("wgc_api.exe", start.Target, StringComparison.OrdinalIgnoreCase);
        var uninstall = links.FirstOrDefault(l => l.Name.StartsWith("World of Tanks EU", StringComparison.Ordinal) && l.Name != "World of Tanks EU");
        Assert.NotNull(uninstall);
        Assert.True(GameLaunchResolver.IsUninstallShortcut(uninstall!));
    }

    [Fact]
    public void Notepad_icon_is_256_with_alpha()
    {
        if (!Enabled) return;

        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var png = NewShell().IconPng(notepad, "live_notepad");

        Assert.NotNull(png);
        Assert.True(File.Exists(png));
        using var bmp = new Bitmap(png!);
        Assert.Equal(256, bmp.Width);
        Assert.Equal(256, bmp.Height);
        Assert.True(Image.IsAlphaPixelFormat(bmp.PixelFormat));
        // A real icon has transparent pixels (corners) - the alpha channel survived.
        Assert.True(bmp.GetPixel(0, 0).A < 255, "the corner of the icon should be transparent");

        // The second ask is served from the cache: same file.
        Assert.Equal(png, NewShell().IconPng(notepad, "live_notepad"));
    }

    [Fact]
    public void Icon_of_a_start_menu_shortcut_is_read()
    {
        if (!Enabled) return;

        var shell = NewShell();
        var steam = shell.StartMenuAndDesktopLinks().First(l => l.Name.Equals("Steam", StringComparison.OrdinalIgnoreCase) && l.Target != null);

        var png = shell.IconPng(steam.Path, "live_steam_lnk");

        Assert.NotNull(png);
        using var bmp = new Bitmap(png!);
        Assert.True(bmp.Width >= 48);
        _out.WriteLine(png);
    }

    [Fact]
    public void Icon_of_a_missing_file_is_null()
    {
        if (!Enabled) return;

        Assert.Null(NewShell().IconPng(@"C:\definitely\missing\x.exe", "live_missing"));
    }

    [Fact]
    public void Windows_default_browser_exe_exists()
    {
        if (!Enabled) return;

        var exe = NewShell().WindowsDefaultBrowserExe();
        _out.WriteLine(exe ?? "(null)");

        Assert.False(string.IsNullOrWhiteSpace(exe));
        Assert.True(File.Exists(exe));
    }

    [Fact]
    public void Xbox_store_app_is_installed_here()
    {
        if (!Enabled) return;

        var shell = NewShell();
        Assert.True(shell.StoreAppInstalled(LauncherCatalog.XboxAumid));
        Assert.False(shell.StoreAppInstalled("WKI.Does.Not.Exist_0000000000000!App"));
    }

    [Fact]
    public void Start_rejects_missing_exe_with_error()
    {
        if (!Enabled) return;

        var ok = NewShell().Start(new LaunchTarget(ShortcutKind.Exe, @"C:\definitely\missing\wki_no_such_game.exe", null), out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
        _out.WriteLine(error);
    }

    [Fact]
    public void Start_rejects_a_missing_shortcut_and_a_bad_steam_id_with_an_error()
    {
        if (!Enabled) return;

        var shell = NewShell();
        Assert.False(shell.Start(new LaunchTarget(ShortcutKind.ShellLink, @"C:\definitely\missing\game.lnk", null), out var e1));
        Assert.False(string.IsNullOrWhiteSpace(e1));
        Assert.False(shell.Start(new LaunchTarget(ShortcutKind.SteamApp, "not-a-number", null), out var e2));
        Assert.False(string.IsNullOrWhiteSpace(e2));
    }

    [Fact]
    public void Real_scan_finds_the_launchers_and_browsers_of_this_pc()
    {
        if (!Enabled) return;

        var shell = NewShell();
        var fs = new DiskFileSystemView();
        var reg = new WindowsRegistryView();

        var launchers = LauncherCatalog.Detect(shell.StartMenuAndDesktopLinks(), reg, fs, shell.StoreAppInstalled);
        var browsers = BrowserCatalog.Detect(reg, fs);
        _out.WriteLine("launchers: " + string.Join(", ", launchers.Select(l => l.Id)));
        _out.WriteLine("browsers:  " + string.Join(", ", browsers.Select(b => $"{b.Id}{(b.Installed ? "" : "(-)")}")));

        Assert.Contains(launchers, l => l.Id == "steam");
        Assert.Contains(launchers, l => l.Id == "xbox");
        Assert.Contains(browsers, b => b.Id == "comet" && b.Installed);
        Assert.Contains(browsers, b => b.Id == "chrome" && b.Installed);
        Assert.DoesNotContain(browsers, b => b.Name.Contains("Explorer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Real_steam_and_epic_libraries_resolve_arma_and_spiderman()
    {
        if (!Enabled) return;

        var fs = new DiskFileSystemView();
        var libs = GameLibraries.SteamLibraries(@"C:\Program Files (x86)\Steam", fs);
        var steam = GameLibraries.SteamApps(libs, fs);
        var epic = GameLibraries.EpicApps(@"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests", fs);
        _out.WriteLine($"steam {steam.Count} apps in {libs.Count} libraries; epic {epic.Count} apps");

        var arma = steam.FirstOrDefault(a => a.AppId == 1874880);
        Assert.NotNull(arma);
        var exe = Path.Combine(arma!.Library, "steamapps", "common", arma.InstallDir, "ArmaReforgerSteam.exe");
        var t = GameLaunchResolver.Resolve(exe, steam, epic, new ShellLinkInfo[0], LauncherCatalog.LauncherExeNames);
        Assert.Equal(new LaunchTarget(ShortcutKind.SteamApp, "1874880", null), t);

        var spider = epic.FirstOrDefault(a => a.DisplayName.Contains("Spider-Man", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(spider);
        var t2 = GameLaunchResolver.Resolve(Path.Combine(spider!.InstallLocation, "Spider-Man.exe"), steam, epic, new ShellLinkInfo[0], LauncherCatalog.LauncherExeNames);
        Assert.Equal(ShortcutKind.EpicApp, t2.Kind);
    }

    [Fact]
    public void Wot_exe_resolves_to_its_start_shortcut_on_this_pc()
    {
        if (!Enabled) return;

        var links = NewShell().StartMenuAndDesktopLinks();
        var steam = new SteamApp[0];

        var t = GameLaunchResolver.Resolve(@"C:\Games\World_of_Tanks_EU\WorldOfTanks.exe", steam, new EpicApp[0], links, LauncherCatalog.LauncherExeNames);

        Assert.Equal(ShortcutKind.ShellLink, t.Kind);
        Assert.EndsWith("World of Tanks EU.lnk", t.Target, StringComparison.OrdinalIgnoreCase);
    }
}
