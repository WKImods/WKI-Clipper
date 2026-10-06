using System;
using System.IO;
using System.Linq;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Per-game folder sorting: which name a game gets, how it is made path-safe, where files
/// land, and that the gallery still finds everything (old flat files + new game folders).
/// </summary>
public class GameFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wki_gamefolder_" + Guid.NewGuid().ToString("N"));

    public GameFolderTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // ---- naming ----

    [Fact]
    public void Product_name_wins()
        => Assert.Equal("Arma Reforger",
            GameFolderNaming.FromMetadata("Arma Reforger", "Arma Reforger Client", "ArmaReforgerSteam"));

    [Theory]
    [InlineData("Unreal Engine")]
    [InlineData("UE4Game")]
    [InlineData("BootstrapPackagedGame")]
    [InlineData("Unity Player")]
    [InlineData("Microsoft® Windows® Operating System")]
    public void Engine_or_os_product_names_are_skipped(string generic)
    {
        // Otherwise every Unreal game would end up in one "Unreal Engine" folder.
        Assert.Equal("Wardogs", GameFolderNaming.FromMetadata(generic, "Wardogs", "WardogsClient-Win64-Shipping"));
    }

    /// <summary>Real version-resource values read from the installed games on the user's machine.</summary>
    [Theory]
    [InlineData("Arma Reforger", "Arma Reforger game", "ArmaReforgerSteam", "Arma Reforger")]
    [InlineData("Arma Reforger", "Arma Reforger Workbench", "ArmaReforgerWorkbenchSteamDiag", "Arma Reforger Workbench")]
    [InlineData("Wardogs", "Wardogs", "WardogsClient-Win64-Shipping", "Wardogs")]
    [InlineData("DayZ", "DayZ", "DayZ_x64", "DayZ")]
    [InlineData("Arma 3", "Arma 3", "arma3_x64", "Arma 3")]
    [InlineData("Avatar: Frontiers of Pandora™", "Avatar: Frontiers of Pandora™", "afop", "Avatar Frontiers of Pandora")]
    [InlineData("", "", "ItTakesTwo_Trial", "ItTakesTwo_Trial")]
    public void Real_installed_games_get_sensible_folders(string product, string desc, string process, string expected)
        => Assert.Equal(expected, GameFolderNaming.FromMetadata(product, desc, process));

    [Fact]
    public void Workbench_and_game_never_share_a_folder()
    {
        // The reason description refinement exists: same product name, different programs.
        var game = GameFolderNaming.FromMetadata("Arma Reforger", "Arma Reforger game", "ArmaReforgerSteam");
        var tools = GameFolderNaming.FromMetadata("Arma Reforger", "Arma Reforger Workbench", "ArmaReforgerWorkbenchSteamDiag");
        Assert.NotEqual(game, tools);
    }

    [Theory]
    [InlineData("Some Game Client")]
    [InlineData("Some Game (64-bit)")]
    [InlineData("Some Game game launcher")]
    public void Generic_description_extensions_keep_the_product_name(string desc)
        => Assert.Equal("Some Game", GameFolderNaming.FromMetadata("Some Game", desc, "x"));

    [Fact]
    public void The_german_os_product_name_is_generic_too()
    {
        // Found by the live test on a German Windows: SystemSettings got its own folder.
        Assert.Equal("Einstellungen",
            GameFolderNaming.FromMetadata("Betriebssystem Microsoft® Windows®", "Einstellungen", "SystemSettings"));
    }

    [Theory]
    [InlineData(@"C:\Windows\ImmersiveControlPanel\SystemSettings.exe", true)]
    [InlineData(@"C:\WINDOWS\System32\ApplicationFrameHost.exe", true)]
    [InlineData(@"C:\Windows.old\game.exe", false)]                 // prefix, not the folder
    [InlineData(@"D:\SteamLibrary\steamapps\common\Arma Reforger\ArmaReforgerSteam.exe", false)]
    [InlineData(null, false)]
    public void Windows_own_executables_are_recognised_language_independently(string? exe, bool expected)
        => Assert.Equal(expected, GameFolderNaming.IsWindowsSystemImage(exe, @"C:\Windows"));

    [Fact]
    public void Falls_back_to_the_cleaned_process_name()
        => Assert.Equal("WardogsClient",
            GameFolderNaming.FromMetadata(null, "  ", "WardogsClient-Win64-Shipping"));

    [Theory]
    [InlineData("WardogsClient-Win64-Shipping", "WardogsClient")]
    [InlineData("Game-WinGDK-Shipping", "Game")]
    [InlineData("SomeGame-Shipping.exe", "SomeGame")]
    [InlineData("DayZ_BE", "DayZ")]
    [InlineData("game_x64", "game")]
    [InlineData("ArmaReforgerSteam", "ArmaReforgerSteam")]
    public void Engine_build_suffixes_are_stripped(string process, string expected)
        => Assert.Equal(expected, GameFolderNaming.StripProcessSuffixes(process));

    [Fact]
    public void A_suffix_alone_is_never_stripped_to_nothing()
        => Assert.Equal("-Shipping", GameFolderNaming.StripProcessSuffixes("-Shipping"));

    [Theory]
    [InlineData("Tom Clancy's Rainbow Six® Siege™", "Tom Clancy's Rainbow Six Siege")]
    [InlineData("Half-Life: Alyx", "Half-Life Alyx")]        // ':' is not allowed in a path
    [InlineData("What/If\\Game?", "What If Game")]
    [InlineData("Trailing dots...", "Trailing dots")]         // Windows silently strips them
    [InlineData("  lots   of    space  ", "lots of space")]
    public void Names_are_made_path_safe(string raw, string expected)
        => Assert.Equal(expected, GameFolderNaming.Sanitize(raw));

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    public void Reserved_device_names_are_defused(string name)
        => Assert.Equal(name + "_", GameFolderNaming.Sanitize(name));

    [Fact]
    public void Very_long_names_are_capped()
        => Assert.True(GameFolderNaming.Sanitize(new string('x', 200)).Length <= 60);

    [Fact]
    public void Nothing_usable_gives_the_unknown_folder()
        => Assert.Equal(GameFolderNaming.UnknownFolder, GameFolderNaming.FromMetadata(null, null, "   "));

    // ---- where files land ----

    [Fact]
    public void Sorting_off_keeps_the_base_folder()
        => Assert.Equal(_root, GameFolderNaming.TargetDirectory(_root, "Arma Reforger", sortByGame: false));

    [Fact]
    public void No_game_keeps_the_base_folder()
        => Assert.Equal(_root, GameFolderNaming.TargetDirectory(_root, null, sortByGame: true));

    [Fact]
    public void A_game_gets_its_own_subfolder()
        => Assert.Equal(Path.Combine(_root, "Arma Reforger"),
            GameFolderNaming.TargetDirectory(_root, "Arma Reforger", sortByGame: true));

    [Fact]
    public void Prepare_creates_the_game_folder()
    {
        var dir = GameFolderNaming.PrepareDirectory(_root, sortByGame: true, () => "Arma Reforger");
        Assert.Equal(Path.Combine(_root, "Arma Reforger"), dir);
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void A_folder_that_cannot_be_created_falls_back_to_the_base_never_loses_the_clip()
    {
        // A FILE with the game's name blocks the folder — CreateDirectory throws.
        File.WriteAllText(Path.Combine(_root, "Arma Reforger"), "in the way");

        var dir = GameFolderNaming.PrepareDirectory(_root, sortByGame: true, () => "Arma Reforger");

        Assert.Equal(_root, dir);
    }

    [Fact]
    public void A_throwing_game_lookup_falls_back_to_the_base()
    {
        var dir = GameFolderNaming.PrepareDirectory(_root, sortByGame: true,
            () => throw new InvalidOperationException("boom"));
        Assert.Equal(_root, dir);
    }

    // ---- gallery scan ----

    [Fact]
    public void Gallery_sees_old_flat_files_and_new_game_folders()
    {
        File.WriteAllText(Path.Combine(_root, "Clip_old.mp4"), "");
        Directory.CreateDirectory(Path.Combine(_root, "Arma Reforger"));
        File.WriteAllText(Path.Combine(_root, "Arma Reforger", "Clip_new.mp4"), "");
        Directory.CreateDirectory(Path.Combine(_root, "Wardogs"));
        File.WriteAllText(Path.Combine(_root, "Wardogs", "Rec_new.mp4"), "");
        File.WriteAllText(Path.Combine(_root, "Wardogs", "notes.txt"), "");   // filtered out

        var media = GameFolderNaming.EnumerateMedia(_root, f => f.Extension == ".mp4");

        Assert.Equal(3, media.Count);
        Assert.Contains(media, m => m.File.Name == "Clip_old.mp4" && m.Game is null);
        Assert.Contains(media, m => m.File.Name == "Clip_new.mp4" && m.Game == "Arma Reforger");
        Assert.Contains(media, m => m.File.Name == "Rec_new.mp4" && m.Game == "Wardogs");
    }

    [Fact]
    public void Gallery_scan_stops_after_one_level()
    {
        // Pointing the clips folder at a big tree must not crawl the whole drive.
        var deep = Path.Combine(_root, "A", "B");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "too_deep.mp4"), "");

        Assert.DoesNotContain(GameFolderNaming.EnumerateMedia(_root, _ => true),
            m => m.File.Name == "too_deep.mp4");
    }

    [Fact]
    public void Missing_folder_scans_as_empty()
        => Assert.Empty(GameFolderNaming.EnumerateMedia(Path.Combine(_root, "nope"), _ => true));

    [Fact]
    public void Sorting_is_on_by_default()
        => Assert.True(new WKI_Clipper.Models.AppSettings().Output.SortByGame);
}
