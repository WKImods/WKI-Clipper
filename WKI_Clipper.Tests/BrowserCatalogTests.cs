using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class BrowserCatalogTests
{
    private static IReadOnlyList<BrowserInfo> Detect(FakeRegistry? reg = null, FakeFileSystem? fs = null)
        => BrowserCatalog.Detect(reg ?? PhillipsPc.BrowserRegistry(), fs ?? PhillipsPc.Programs());

    private static BrowserInfo One(IReadOnlyList<BrowserInfo> all, string id) => Assert.Single(all, b => b.Id == id);

    [Fact]
    public void Comet_detected_from_hkcu_and_iexplore_ignored()
    {
        var all = Detect();

        var comet = One(all, "comet");
        Assert.True(comet.Installed);
        Assert.Equal("Comet", comet.Name);
        Assert.Equal(PhillipsPc.CometPath, comet.ExePath);

        // Internet Explorer is registered (and its exe exists), but it is never offered.
        Assert.DoesNotContain(all, b => b.ExePath != null && b.ExePath.EndsWith("iexplore.exe", System.StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(all, b => b.Id.Contains("iexplore", System.StringComparison.OrdinalIgnoreCase) || b.Name.Contains("Explorer", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Chrome_in_hklm_and_wow6432_listed_once()
    {
        var all = Detect();

        var chrome = One(all, "chrome");   // Assert.Single: exactly one row
        Assert.True(chrome.Installed);
        Assert.Equal("Google Chrome", chrome.Name);
        Assert.Equal(PhillipsPc.ChromePath, chrome.ExePath);
        Assert.Equal(1, all.Count(b => b.ExePath == PhillipsPc.BravePath));   // same for the other doubled entry
    }

    [Fact]
    public void Brave_is_an_additional_detected_browser()
    {
        var brave = One(Detect(), "brave");

        Assert.True(brave.Installed);
        Assert.Equal("Brave", brave.Name);
        Assert.Equal(PhillipsPc.BravePath, brave.ExePath);
        Assert.Null(brave.DownloadUrl);   // only the four named candidates have a download page
    }

    [Fact]
    public void Edge_without_exe_not_installed()
    {
        // A registry entry whose exe is gone (the Appx stub on a machine where Edge was removed).
        var reg = PhillipsPc.BrowserRegistry();
        reg.Set("HKLM", @"SOFTWARE\Clients\StartMenuInternet\Microsoft Edge", null, "Microsoft Edge");
        reg.Set("HKLM", @"SOFTWARE\Clients\StartMenuInternet\Microsoft Edge\shell\open\command", null,
            "\"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe\" --single-argument %1");

        var edge = One(Detect(reg), "edge");

        Assert.False(edge.Installed);
        Assert.Null(edge.ExePath);
        Assert.Equal("Microsoft Edge", edge.Name);
    }

    [Fact]
    public void Edge_with_exe_is_installed_and_the_command_arguments_are_cut_off()
    {
        var reg = PhillipsPc.BrowserRegistry();
        reg.Set("HKLM", @"SOFTWARE\Clients\StartMenuInternet\Microsoft Edge", null, "Microsoft Edge");
        reg.Set("HKLM", @"SOFTWARE\Clients\StartMenuInternet\Microsoft Edge\shell\open\command", null,
            "\"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe\" --single-argument %1");
        var fs = PhillipsPc.Programs().Add(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe");

        var edge = One(Detect(reg, fs), "edge");

        Assert.True(edge.Installed);
        Assert.Equal(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", edge.ExePath);
    }

    [Fact]
    public void Opera_not_installed_has_download_url()
    {
        var opera = One(Detect(), "opera");

        Assert.False(opera.Installed);
        Assert.Null(opera.ExePath);
        Assert.Equal("Opera", opera.Name);
        Assert.Equal("https://www.opera.com/download", opera.DownloadUrl);
    }

    [Fact]
    public void The_four_named_candidates_are_always_listed_first_in_a_fixed_order()
    {
        var all = Detect(new FakeRegistry(), new FakeFileSystem());

        Assert.Equal(new[] { "opera", "chrome", "comet", "firefox" }, all.Select(b => b.Id).ToArray());
        Assert.All(all, b => Assert.False(b.Installed));
        Assert.Equal("https://www.google.com/chrome/", One(all, "chrome").DownloadUrl);
        Assert.Equal("https://www.firefox.com/", One(all, "firefox").DownloadUrl);
        Assert.Equal("https://www.perplexity.ai/comet", One(all, "comet").DownloadUrl);
        Assert.Equal("Mozilla Firefox", One(all, "firefox").Name);
    }

    // Opera and Opera GX register their entry point, ...\Programs\Opera[ GX]\launcher.exe - opera.exe lives in a version folder.
    private const string OperaLauncher = @"C:\Users\cr4zy\AppData\Local\Programs\Opera\launcher.exe";
    private const string OperaGxLauncher = @"C:\Users\cr4zy\AppData\Local\Programs\Opera GX\launcher.exe";

    private static void AddOpera(FakeRegistry reg, string subKey, string name, string exe)
    {
        reg.Set("HKCU", @"Software\Clients\StartMenuInternet\" + subKey, null, name);
        reg.Set("HKCU", @"Software\Clients\StartMenuInternet\" + subKey + @"\shell\open\command", null, "\"" + exe + "\"");
    }

    [Fact]
    public void Opera_gx_is_not_opera()
    {
        var reg = new FakeRegistry();
        AddOpera(reg, "Opera GXStable", "Opera GX Internet Browser", OperaGxLauncher);
        var fs = new FakeFileSystem().Add(OperaGxLauncher);

        var all = Detect(reg, fs);

        Assert.False(One(all, "opera").Installed);
        var gx = Assert.Single(all, b => b.Installed);
        Assert.Equal("operagx", gx.Id);
        Assert.Equal("Opera GX", gx.Name);
        Assert.Equal(OperaGxLauncher, gx.ExePath);
    }

    [Fact]
    public void Opera_registered_through_launcher_exe_is_installed_opera()
    {
        // Before: the leaf "launcher.exe" matched nothing, so Opera showed as "not installed" with a download link.
        var reg = new FakeRegistry();
        AddOpera(reg, "OperaStable", "Opera Internet Browser", OperaLauncher);
        var fs = new FakeFileSystem().Add(OperaLauncher);

        var opera = One(Detect(reg, fs), "opera");

        Assert.True(opera.Installed);
        Assert.Equal("Opera", opera.Name);
        Assert.Equal(OperaLauncher, opera.ExePath);
        Assert.DoesNotContain(Detect(reg, fs), b => b.Id == "launcher");
    }

    [Fact]
    public void Opera_and_opera_gx_installed_together_are_two_rows_in_either_registry_order()
    {
        // "Opera GXStable" is enumerated before "OperaStable": the first one must not swallow the second.
        var reg = new FakeRegistry();
        AddOpera(reg, "Opera GXStable", "Opera GX Internet Browser", OperaGxLauncher);
        AddOpera(reg, "OperaStable", "Opera Internet Browser", OperaLauncher);
        var fs = new FakeFileSystem().Add(OperaGxLauncher).Add(OperaLauncher);

        var all = Detect(reg, fs);

        var opera = One(all, "opera");
        var gx = One(all, "operagx");
        Assert.True(opera.Installed);
        Assert.True(gx.Installed);
        Assert.Equal(OperaLauncher, opera.ExePath);
        Assert.Equal(OperaGxLauncher, gx.ExePath);
        Assert.Equal(2, all.Count(b => b.Installed));
        Assert.Contains("Opera GX", gx.Name);
    }

    [Fact]
    public void Opera_registered_through_the_classic_opera_exe_still_counts()
    {
        var reg = new FakeRegistry();
        AddOpera(reg, "OperaStable", "Opera Internet Browser", @"C:\Program Files\Opera\opera.exe");
        AddOpera(reg, "Opera GXStable", "Opera GX Internet Browser", @"C:\Program Files\Opera GX\opera.exe");
        var fs = new FakeFileSystem().Add(@"C:\Program Files\Opera\opera.exe").Add(@"C:\Program Files\Opera GX\opera.exe");

        var all = Detect(reg, fs);

        Assert.True(One(all, "opera").Installed);
        Assert.True(One(all, "operagx").Installed);
    }

    [Fact]
    public void A_launcher_exe_that_is_not_opera_does_not_become_opera_because_of_the_user_name()
    {
        // The word "opera" in the PATH (a user called "operator") must not decide - only the registry's own name does.
        var reg = new FakeRegistry();
        AddOpera(reg, "SomeBrowser", "Some Browser", @"C:\Users\operator\AppData\Local\Programs\Some Browser\launcher.exe");
        var fs = new FakeFileSystem().Add(@"C:\Users\operator\AppData\Local\Programs\Some Browser\launcher.exe");

        var all = Detect(reg, fs);

        Assert.False(One(all, "opera").Installed);
        Assert.DoesNotContain(all, b => b.Id == "operagx");
        var other = Assert.Single(all, b => b.Installed);
        Assert.Equal("Some Browser", other.Name);
    }

    [Fact]
    public void The_generic_launcher_exe_is_not_a_browser_exe_name()
    {
        // The game detection reads this set: "launcher.exe" there would hide every game that is started through a launcher.exe.
        Assert.DoesNotContain("launcher.exe", BrowserCatalog.BrowserExeNames);
    }

    [Fact]
    public void Command_without_quotes_keeps_a_path_with_spaces()
    {
        var reg = new FakeRegistry();
        reg.Set("HKLM", @"SOFTWARE\Clients\StartMenuInternet\FIREFOX.EXE", null, "Firefox");
        reg.Set("HKLM", @"SOFTWARE\Clients\StartMenuInternet\FIREFOX.EXE\shell\open\command", null,
            @"C:\Program Files\Mozilla Firefox\firefox.exe -osint");
        var fs = new FakeFileSystem().Add(@"C:\Program Files\Mozilla Firefox\firefox.exe");

        var ff = One(Detect(reg, fs), "firefox");

        Assert.True(ff.Installed);
        Assert.Equal(@"C:\Program Files\Mozilla Firefox\firefox.exe", ff.ExePath);
    }

    [Fact]
    public void Known_browser_exe_names_cover_the_candidates_and_ie()
    {
        foreach (var n in new[] { "chrome.exe", "firefox.exe", "opera.exe", "comet.exe", "brave.exe", "msedge.exe", "iexplore.exe" })
            Assert.Contains(n, BrowserCatalog.BrowserExeNames);
    }

    // ---- default browser ----

    private static readonly string WindowsDefault = PhillipsPc.CometPath;

    [Fact]
    public void Chosen_default_uninstalled_falls_back_to_windows_default()
    {
        var all = Detect();   // opera is NOT installed

        var exe = BrowserCatalog.ResolveDefault("opera", all, WindowsDefault, out var fellBack);

        Assert.Equal(WindowsDefault, exe);
        Assert.True(fellBack);
    }

    [Fact]
    public void Chosen_default_installed_is_used()
    {
        var exe = BrowserCatalog.ResolveDefault("chrome", Detect(), WindowsDefault, out var fellBack);

        Assert.Equal(PhillipsPc.ChromePath, exe);
        Assert.False(fellBack);
    }

    [Fact]
    public void No_choice_means_the_windows_default_without_a_notice()
    {
        var exe = BrowserCatalog.ResolveDefault(null, Detect(), WindowsDefault, out var fellBack);

        Assert.Equal(WindowsDefault, exe);
        Assert.False(fellBack);
    }

    [Fact]
    public void Unknown_chosen_id_falls_back()
    {
        var exe = BrowserCatalog.ResolveDefault("netscape", Detect(), WindowsDefault, out var fellBack);

        Assert.Equal(WindowsDefault, exe);
        Assert.True(fellBack);
    }

    [Fact]
    public void Internet_explorer_as_windows_default_is_never_started()
    {
        var exe = BrowserCatalog.ResolveDefault(null, Detect(), PhillipsPc.IePath, out _);

        Assert.Null(exe);
    }

    [Fact]
    public void No_windows_default_and_no_choice_gives_null()
    {
        var exe = BrowserCatalog.ResolveDefault(null, Detect(), null, out var fellBack);

        Assert.Null(exe);
        Assert.False(fellBack);
    }
}
