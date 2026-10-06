using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace WKI_Clipper.Services;

/// <summary>
/// Pure rules for sorting captures into per-game folders: which name a game gets, how it
/// is made safe for the file system, and where files land. Kept free of Win32 so every
/// edge case is unit-tested instead of discovered in someone's clips folder.
/// </summary>
public static class GameFolderNaming
{
    /// <summary>Folder for the desktop, the shell and anything that is not an app.</summary>
    public const string DesktopFolder = "Desktop";
    /// <summary>Folder when no name could be derived at all.</summary>
    public const string UnknownFolder = "Unknown";

    private const int MaxLength = 60;

    /// <summary>
    /// Product names that identify an ENGINE or the OS rather than the game — taking them
    /// would put every Unreal game into one "Unreal Engine" folder.
    /// </summary>
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Unreal Engine", "UnrealEngine", "UE4", "UE5", "UE4Game", "UnrealGame",
        "BootstrapPackagedGame", "Unity", "Unity Player", "UnityPlayer", "Godot Engine",
        "Microsoft Windows Operating System", "Windows Operating System",
        // Localized on non-English Windows - found on a German system via the live test.
        // The language-independent guard is IsWindowsSystemImage; this is the backstop.
        "Betriebssystem Microsoft Windows",
    };

    /// <summary>
    /// True for executables shipped with Windows itself (anything under the Windows
    /// directory). Language-independent, unlike product names, which Windows localizes
    /// ("Betriebssystem Microsoft Windows" on a German system). Games never live there.
    /// </summary>
    public static bool IsWindowsSystemImage(string? exePath, string? windowsDir)
    {
        if (string.IsNullOrWhiteSpace(exePath) || string.IsNullOrWhiteSpace(windowsDir)) return false;
        var prefix = windowsDir.TrimEnd('\\', '/') + "\\";
        return exePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Build-configuration suffixes engines append to the exe name.</summary>
    private static readonly string[] ProcessSuffixes =
    {
        "-Win64-Shipping", "-WinGDK-Shipping", "-Win64-Test", "-Shipping", "_BE", "_x64", "-x64",
    };

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Folder name for a game from its exe metadata. Prefers the product name ("Arma
    /// Reforger"), then the file description, then the process name with engine build
    /// suffixes stripped. The window title is deliberately not used: it changes with
    /// every map, menu or editor tab and would scatter one game over many folders.
    /// </summary>
    public static string FromMetadata(string? productName, string? fileDescription, string? processName)
    {
        var product = Sanitize(productName);
        if (product.Length > 0 && !GenericNames.Contains(product))
            return RefineWithDescription(product, fileDescription) ?? product;

        var description = Sanitize(fileDescription);
        if (description.Length > 0 && !GenericNames.Contains(description)) return description;

        var fromProcess = Sanitize(StripProcessSuffixes(processName));
        return fromProcess.Length > 0 ? fromProcess : UnknownFolder;
    }

    /// <summary>Words that only restate "this is the program", never which program.</summary>
    private static readonly HashSet<string> GenericSuffixWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "game", "client", "launcher", "application", "app", "executable", "exe",
        "x64", "x86", "64-bit", "32-bit", "64bit", "32bit", "win64", "win32", "shipping",
    };

    /// <summary>
    /// One product often ships several programs that report the SAME product name — Arma
    /// Reforger's game and its Workbench both say "Arma Reforger". When the description
    /// extends the product name by something distinguishing ("Arma Reforger Workbench"),
    /// it wins, so modding footage does not mix with gameplay. A merely generic extension
    /// ("Arma Reforger game") does not.
    /// </summary>
    private static string? RefineWithDescription(string product, string? fileDescription)
    {
        var desc = Sanitize(fileDescription);
        if (desc.Length <= product.Length + 1) return null;
        if (!desc.StartsWith(product + " ", StringComparison.OrdinalIgnoreCase)) return null;

        var rest = desc[(product.Length + 1)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('(', ')', '-', ',', '.'))
            .Where(w => w.Length > 0);
        return rest.All(GenericSuffixWords.Contains) ? null : desc;
    }

    /// <summary>"WardogsClient-Win64-Shipping" → "WardogsClient".</summary>
    public static string StripProcessSuffixes(string? processName)
    {
        var name = (processName ?? "").Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        bool changed;
        do
        {
            changed = false;
            foreach (var suffix in ProcessSuffixes)
            {
                if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    name = name[..^suffix.Length];
                    changed = true;
                }
            }
        } while (changed);
        return name;
    }

    /// <summary>
    /// Makes a display name safe as one path segment: trademark signs and characters
    /// Windows forbids are dropped, whitespace collapsed, trailing dots/spaces removed
    /// (Windows silently strips them, which would split one game over two folders),
    /// reserved device names defused, and the length capped.
    /// </summary>
    public static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (c is '®' or '™' or '©') continue;
            if (char.IsControl(c) || Array.IndexOf(invalid, c) >= 0) { sb.Append(' '); continue; }
            sb.Append(c);
        }

        var s = Regex.Replace(sb.ToString(), @"\s+", " ").Trim().TrimEnd('.', ' ');
        if (s.Length > MaxLength) s = s[..MaxLength].TrimEnd('.', ' ');
        if (ReservedNames.Contains(s)) s += "_";
        return s;
    }

    /// <summary>
    /// Where a capture is written. With sorting off (or no game known) it is the base
    /// folder exactly as before, so the feature can never strand files anywhere new.
    /// </summary>
    public static string TargetDirectory(string baseDir, string? gameFolder, bool sortByGame)
    {
        if (!sortByGame) return baseDir;
        var segment = Sanitize(gameFolder);
        return segment.Length == 0 ? baseDir : Path.Combine(baseDir, segment);
    }

    /// <summary>
    /// Resolves and creates the folder a capture is written to. If the game subfolder
    /// cannot be created for any reason, the base folder is used: sorting is a comfort
    /// feature and must never be the reason a clip is lost.
    /// </summary>
    public static string PrepareDirectory(string configuredFolder, bool sortByGame, Func<string?>? gameFolder)
    {
        var baseDir = SettingsService.ExpandPath(configuredFolder);
        string? game = null;
        if (sortByGame && gameFolder != null)
        {
            try { game = gameFolder(); }
            catch (Exception ex) { Logger.Warn("Game folder lookup failed: " + ex.Message); }
        }

        var dir = TargetDirectory(baseDir, game, sortByGame);
        try
        {
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not create game folder '{dir}', saving to the base folder: {ex.Message}");
            Directory.CreateDirectory(baseDir);
            return baseDir;
        }
    }

    /// <summary>
    /// Media files in the base folder AND one level of game subfolders. Exactly one level
    /// on purpose: a full recursive walk would crawl an entire drive if someone points the
    /// clips folder at D:\, and the layout this feature creates is never deeper.
    /// The game is the subfolder name, or null for files directly in the base folder
    /// (everything saved before sorting existed).
    /// </summary>
    public static List<(FileInfo File, string? Game)> EnumerateMedia(string baseDir, Func<FileInfo, bool> include)
    {
        var result = new List<(FileInfo, string?)>();
        if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir)) return result;

        var root = new DirectoryInfo(baseDir);
        try
        {
            foreach (var f in root.EnumerateFiles())
                if (include(f)) result.Add((f, null));
        }
        catch (Exception ex) { Logger.Warn($"Media scan of '{baseDir}' failed: {ex.Message}"); }

        IEnumerable<DirectoryInfo> subdirs;
        try { subdirs = root.EnumerateDirectories().ToList(); }
        catch { return result; }

        foreach (var dir in subdirs)
        {
            try
            {
                foreach (var f in dir.EnumerateFiles())
                    if (include(f)) result.Add((f, dir.Name));
            }
            catch { /* one unreadable folder must not hide the rest */ }
        }
        return result;
    }
}
