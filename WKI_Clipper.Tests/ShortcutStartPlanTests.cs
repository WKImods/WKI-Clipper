using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// How each kind of entry is started (spec section 4.2), decided without starting anything. The plan carries no verb on
/// purpose: Win32ShortcutShell always uses ShellExecute without "runas".
/// </summary>
public sealed class ShortcutStartPlanTests
{
    private static readonly FakeFileSystem Disk = new FakeFileSystem()
        .Add(@"C:\Games\Foo\foo.exe")
        .Add(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Wargaming.net\World_of_Tanks_EU\World of Tanks EU.lnk")
        .Add(@"C:\Users\Public\Desktop\Steam.url")
        .Add(PhillipsPc.CometPath)
        .Add(PhillipsPc.RockstarPath);

    private static StartPlan Plan(ShortcutKind kind, string target, string? args = null)
        => ShortcutStartPlan.For(new LaunchTarget(kind, target, args), Disk);

    [Fact]
    public void Steam_game_starts_the_rungameid_link()
    {
        var p = Plan(ShortcutKind.SteamApp, "1874880");

        Assert.Null(p.Error);
        Assert.Equal("steam://rungameid/1874880", p.FileName);
        Assert.Null(p.Arguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12 34")]
    [InlineData("1874880/../x")]
    [InlineData("1&calc")]
    public void Steam_appid_must_be_digits_only(string id)
    {
        var p = Plan(ShortcutKind.SteamApp, id);

        Assert.NotNull(p.Error);
        Assert.Null(p.FileName);
    }

    [Fact]
    public void Epic_game_starts_the_launch_link_with_the_app_name_escaped()
    {
        var p = Plan(ShortcutKind.EpicApp, "be23672deb69402781cd47cc2919caf4");
        Assert.Equal("com.epicgames.launcher://apps/be23672deb69402781cd47cc2919caf4?action=launch&silent=true", p.FileName);

        var odd = Plan(ShortcutKind.EpicApp, "Fort nite&x=1");
        Assert.Equal("com.epicgames.launcher://apps/Fort%20nite%26x%3D1?action=launch&silent=true", odd.FileName);
    }

    [Fact]
    public void Epic_without_app_name_is_an_error()
        => Assert.NotNull(Plan(ShortcutKind.EpicApp, " ").Error);

    [Fact]
    public void Shell_link_starts_the_link_file_itself()
    {
        var lnk = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Wargaming.net\World_of_Tanks_EU\World of Tanks EU.lnk";

        var p = Plan(ShortcutKind.ShellLink, lnk);

        Assert.Null(p.Error);
        Assert.Equal(lnk, p.FileName);       // NOT wgc_api.exe: the shortcut carries its own arguments and folder
        Assert.Null(p.Arguments);
        Assert.Null(p.WorkingDirectory);
    }

    [Fact]
    public void Url_file_starts_the_file_itself()
        => Assert.Equal(@"C:\Users\Public\Desktop\Steam.url", Plan(ShortcutKind.ShellLink, @"C:\Users\Public\Desktop\Steam.url").FileName);

    [Fact]
    public void Missing_link_file_is_an_error_not_a_start()
    {
        var p = Plan(ShortcutKind.ShellLink, @"C:\Gone\Game.lnk");

        Assert.NotNull(p.Error);
        Assert.Null(p.FileName);
    }

    [Fact]
    public void Store_app_starts_through_explorer_with_the_apps_folder_path()
    {
        var p = Plan(ShortcutKind.StoreApp, PhillipsPc.XboxAumid);

        Assert.Null(p.Error);
        Assert.Equal("explorer.exe", p.FileName);
        Assert.Equal(@"shell:AppsFolder\" + PhillipsPc.XboxAumid, p.Arguments);
    }

    [Fact]
    public void Store_app_without_aumid_is_an_error()
        => Assert.NotNull(Plan(ShortcutKind.StoreApp, "").Error);

    [Fact]
    public void Exe_starts_in_its_own_folder_with_its_arguments()
    {
        var p = Plan(ShortcutKind.Exe, @"C:\Games\Foo\foo.exe", "-windowed -nosplash");

        Assert.Null(p.Error);
        Assert.Equal(@"C:\Games\Foo\foo.exe", p.FileName);
        Assert.Equal("-windowed -nosplash", p.Arguments);
        Assert.Equal(@"C:\Games\Foo", p.WorkingDirectory);
    }

    [Fact]
    public void Missing_exe_is_an_error_not_a_start()
    {
        var p = Plan(ShortcutKind.Exe, @"C:\Gone\Game.exe");

        Assert.NotNull(p.Error);
        Assert.Null(p.FileName);
    }

    [Theory]
    [InlineData(ShortcutKind.Browser)]
    [InlineData(ShortcutKind.DefaultBrowser)]
    public void Browsers_start_the_exe_without_arguments(ShortcutKind kind)
    {
        // Even if an entry carries arguments, a browser is started bare.
        var p = Plan(kind, PhillipsPc.CometPath, "--incognito");

        Assert.Null(p.Error);
        Assert.Equal(PhillipsPc.CometPath, p.FileName);
        Assert.Null(p.Arguments);
        Assert.Equal(@"C:\Users\cr4zy\AppData\Local\Perplexity\Comet\Application", p.WorkingDirectory);
    }

    [Fact]
    public void Catalog_launcher_with_a_shortcut_starts_the_shortcut()
    {
        var lnk = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Wargaming.net\World_of_Tanks_EU\World of Tanks EU.lnk";

        var p = Plan(ShortcutKind.CatalogLauncher, lnk);

        Assert.Equal(lnk, p.FileName);
        Assert.Null(p.WorkingDirectory);
    }

    [Fact]
    public void Catalog_launcher_with_an_exe_starts_it_in_its_folder()
    {
        var p = Plan(ShortcutKind.CatalogLauncher, PhillipsPc.RockstarPath);

        Assert.Equal(PhillipsPc.RockstarPath, p.FileName);
        Assert.Equal(@"C:\Program Files\Rockstar Games\Launcher", p.WorkingDirectory);
    }

    [Fact]
    public void Blank_target_is_an_error_for_every_path_kind()
    {
        foreach (var kind in new[] { ShortcutKind.Exe, ShortcutKind.ShellLink, ShortcutKind.Browser, ShortcutKind.DefaultBrowser, ShortcutKind.CatalogLauncher })
            Assert.NotNull(Plan(kind, "  ").Error);
    }

    [Fact]
    public void Url_file_text_is_read_from_the_internet_shortcut_section()
    {
        var text = "\uFEFF[InternetShortcut]\r\nURL=steam://rungameid/1874880\r\nIconFile=D:\\x.ico\r\nIconIndex=0\r\n";

        Assert.Equal("steam://rungameid/1874880", Win32ShortcutShell.ParseInternetShortcut(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[Other]\r\nURL=x\r\n")]
    [InlineData("[InternetShortcut]\r\nIconFile=x\r\n")]
    [InlineData("URL=http://no-section")]
    public void Url_file_without_a_url_in_its_section_gives_null(string text)
        => Assert.Null(Win32ShortcutShell.ParseInternetShortcut(text));

    [Fact]
    public void Url_file_keys_are_case_insensitive()
        => Assert.Equal("https://example.org/", Win32ShortcutShell.ParseInternetShortcut("[internetshortcut]\nurl = https://example.org/\n"));
}
