using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class GameLibrariesTests
{
    [Fact]
    public void Steam_libraries_are_root_plus_vdf_paths_deduped()
    {
        var fs = PhillipsPc.Steam();

        var libs = GameLibraries.SteamLibraries(PhillipsPc.SteamRoot, fs);

        // The root is also listed as library "0" in the file: it must appear once.
        Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, libs);
    }

    [Fact]
    public void Steam_libraries_dedupe_case_and_trailing_slash_variants()
    {
        var fs = new FakeFileSystem().Add(PhillipsPc.SteamRoot + @"\steamapps\libraryfolders.vdf", """
            "libraryfolders"
            {
                "0" { "path" "c:\\program files (x86)\\steam\\" }
                "1" { "path" "D:\\SteamLibrary" }
                "2" { "path" "d:\\steamlibrary\\" }
                "3" { "path" "D:/SteamLibrary" }
            }
            """);

        var libs = GameLibraries.SteamLibraries(PhillipsPc.SteamRoot, fs);

        Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, libs);
    }

    [Fact]
    public void Steam_libraries_without_vdf_are_just_the_root()
    {
        var libs = GameLibraries.SteamLibraries(PhillipsPc.SteamRoot, new FakeFileSystem());
        Assert.Equal(new[] { PhillipsPc.SteamRoot }, libs);
    }

    [Fact]
    public void Steam_libraries_understand_the_old_flat_format()
    {
        var fs = new FakeFileSystem().Add(PhillipsPc.SteamRoot + @"\steamapps\libraryfolders.vdf",
            "\"LibraryFolders\"\n{\n\t\"TimeNextStatsReport\"\t\t\"1456760000\"\n\t\"ContentStatsID\"\t\t\"-72\"\n\t\"1\"\t\t\"D:\\SteamLibrary\"\n}\n");

        var libs = GameLibraries.SteamLibraries(PhillipsPc.SteamRoot, fs);

        Assert.Equal(new[] { PhillipsPc.SteamRoot, @"D:\SteamLibrary" }, libs);
    }

    [Fact]
    public void Steam_apps_exclude_redistributables_228980()
    {
        var fs = PhillipsPc.Steam();
        var libs = GameLibraries.SteamLibraries(PhillipsPc.SteamRoot, fs);

        var apps = GameLibraries.SteamApps(libs, fs);

        Assert.DoesNotContain(apps, a => a.AppId == 228980);
        Assert.Equal(3, apps.Count);
        var arma = Assert.Single(apps, a => a.AppId == 1874880);
        Assert.Equal("Arma Reforger", arma.Name);
        Assert.Equal("Arma Reforger", arma.InstallDir);
        Assert.Equal(@"D:\SteamLibrary", arma.Library);
        var wardogs = Assert.Single(apps, a => a.AppId == 1867240);
        Assert.Equal("WARDOGS", wardogs.Name);
        Assert.Equal("Wardogs", wardogs.InstallDir);   // installdir differs from the display name
        Assert.Contains(apps, a => a.AppId == 221100 && a.Name == "DayZ");
    }

    [Fact]
    public void Steam_apps_skip_unreadable_manifests()
    {
        var fs = PhillipsPc.Steam()
            .Add(PhillipsPc.SteamLibraryD + @"\steamapps\appmanifest_1.acf", "")
            .Add(PhillipsPc.SteamLibraryD + @"\steamapps\appmanifest_2.acf", "\"AppState\" { \"appid\" \"2\" }")           // no installdir
            .Add(PhillipsPc.SteamLibraryD + @"\steamapps\appmanifest_3.acf", "\"AppState\" { \"appid\" \"x\" \"installdir\" \"Y\" }")  // appid not a number
            .Add(PhillipsPc.SteamLibraryD + @"\steamapps\libraryfolders.vdf", "not a manifest");                          // wrong pattern

        var apps = GameLibraries.SteamApps(new[] { PhillipsPc.SteamRoot, PhillipsPc.SteamLibraryD }, fs);

        Assert.Equal(3, apps.Count);
    }

    [Fact]
    public void Epic_apps_read_the_real_manifests()
    {
        var apps = GameLibraries.EpicApps(PhillipsPc.EpicManifests, PhillipsPc.Epic());

        Assert.Equal(2, apps.Count);
        var fortnite = Assert.Single(apps, a => a.AppName == "Fortnite");
        Assert.Equal("Fortnite", fortnite.DisplayName);
        Assert.Equal(@"D:\Program Files\Epic Games\Fortnite", fortnite.InstallLocation);
        Assert.Equal("FortniteGame/Binaries/Win64/FortniteLauncher.exe", fortnite.LaunchExecutable);
        var spider = Assert.Single(apps, a => a.AppName == "be23672deb69402781cd47cc2919caf4");
        Assert.Equal("Marvel's Spider-Man Remastered", spider.DisplayName);
        Assert.Equal("Spider-Man.exe", spider.LaunchExecutable);
    }

    [Fact]
    public void Epic_manifest_with_bad_json_is_skipped()
    {
        var fs = PhillipsPc.Epic()
            .Add(PhillipsPc.EpicManifests + @"\BROKEN.item", "{ \"AppName\": \"Half")
            .Add(PhillipsPc.EpicManifests + @"\EMPTY.item", "")
            .Add(PhillipsPc.EpicManifests + @"\ARRAY.item", "[1,2,3]")
            .Add(PhillipsPc.EpicManifests + @"\NOLOC.item", "{ \"AppName\": \"X\", \"DisplayName\": \"X\" }")
            .Add(PhillipsPc.EpicManifests + @"\notes.txt", "{}");

        var apps = GameLibraries.EpicApps(PhillipsPc.EpicManifests, fs);

        // Only the two good manifests survive; nothing throws.
        Assert.Equal(2, apps.Count);
        Assert.Contains(apps, a => a.AppName == "Fortnite");
        Assert.Contains(apps, a => a.AppName == "be23672deb69402781cd47cc2919caf4");
    }

    [Fact]
    public void Epic_dlc_items_are_not_games()
    {
        // A DLC .item names its base game in MainGameAppName and shares the install folder: it must
        // never take over the base game's launch target.
        var fs = PhillipsPc.Epic().Add(PhillipsPc.EpicManifests + @"\DLC.item", """
            { "AppName": "FortniteDlcPack", "MainGameAppName": "Fortnite", "DisplayName": "Pack",
              "InstallLocation": "D:\\Program Files\\Epic Games\\Fortnite" }
            """);

        var apps = GameLibraries.EpicApps(PhillipsPc.EpicManifests, fs);

        Assert.DoesNotContain(apps, a => a.AppName == "FortniteDlcPack");
        Assert.Equal(2, apps.Count);
    }

    [Fact]
    public void Epic_apps_without_manifest_folder_are_empty()
        => Assert.Empty(GameLibraries.EpicApps(PhillipsPc.EpicManifests, new FakeFileSystem()));
}
