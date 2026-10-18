using System;
using System.IO;
using System.Text.RegularExpressions;

namespace WKI_Clipper.Services;

/// <summary>Why a working folder (buffer, clips, screenshots) cannot be used as configured.</summary>
internal enum FolderProblem
{
    None,
    /// <summary>Empty, relative, or only a drive ("D:", "D:foo"): the answer to "which folder" depends on the program asking.</summary>
    NotFullyQualified,
    /// <summary>The root of a drive or share ("D:\", "\\server\share"). The buffer writes a ring of files: never into a root.</summary>
    DriveRoot,
    /// <summary>The program's own folder (or below it): updates and the uninstaller work there.</summary>
    InstallDirectory
}

/// <summary>
/// Pure path rules for the working folders. They keep the replay buffer's seg_N_NN.mp4 files out of the gallery
/// (the buffer writes a segment every few seconds; if its folder is (inside) the clips folder, every one of them shows
/// up as a brand-new "clip"), and they keep every working folder a real, fully qualified folder that is neither a
/// drive root nor the program's own folder.
///
/// Why "D:" is not a folder: it means "the current directory on drive D", and two programs disagree about it. ffmpeg is
/// started in its own directory and wrote the ring there; the clipper looked into ITS current directory, never saw the
/// segments, took the buffer for stalled, restarted it - and every new ring went where nothing pruned. The user's disk
/// filled up, and the installed program, the clips and the ring all sat in one folder.
/// </summary>
internal static class PathRules
{
    /// <summary>The folder the running program lives in (updates and the uninstaller work there).</summary>
    public static string InstallDirectory => AppContext.BaseDirectory;

    /// <summary>Same literal as <c>OutputSettings.BufferFolder</c>'s default (guarded by a test).</summary>
    public const string DefaultBufferFolder = @"%LOCALAPPDATA%\WKI_Clipper\buffer";

    // [0-9] and \z on purpose: \d also matches non-ASCII digits and $ tolerates a trailing newline.
    private static readonly Regex SegmentName = new(
        @"^seg_[0-9]+_[0-9]+\.mp4\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="folder"/> itself or anywhere below it.
    /// Environment variables are expanded, paths are normalized (slashes, "..", trailing
    /// separators) and compared ignoring case. Empty or invalid paths are never "inside".
    /// </summary>
    internal static bool IsSameOrInside(string? path, string? folder)
    {
        var p = Normalize(path);
        var f = Normalize(folder);
        if (p is null || f is null) return false;

        if (p.Equals(f, StringComparison.OrdinalIgnoreCase)) return true;

        // Compare against "folder\" so that C:\Clips2 is not mistaken for a child of C:\Clips.
        var prefix = f.EndsWith(Path.DirectorySeparatorChar) ? f : f + Path.DirectorySeparatorChar;
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The buffer folder would drop its segments into the clips or screenshots folder.</summary>
    internal static bool IsUnsafeBufferFolder(string? buffer, string? clips, string? screenshots)
        => IsSameOrInside(buffer, clips) || IsSameOrInside(buffer, screenshots);

    /// <summary>
    /// The buffer folder to actually use: the configured one, unless it overlaps the clips or
    /// screenshots folder — then the fallback. Returns the arguments unexpanded, as configured.
    /// </summary>
    internal static string SafeBufferFolder(string buffer, string clips, string screenshots, string fallback,
        string? installDir = null)
        => BufferFolderProblem(buffer, installDir) != FolderProblem.None || IsUnsafeBufferFolder(buffer, clips, screenshots)
            ? fallback
            : buffer;

    /// <summary>
    /// Whether the buffer folder can be used: a fully qualified folder below a drive or share root (never "D:", "D:\"
    /// or a relative path) and not the program's own folder or anything inside it. <paramref name="installDir"/> null
    /// skips the last rule (tests, callers without one).
    /// </summary>
    internal static FolderProblem BufferFolderProblem(string? folder, string? installDir)
    {
        var shape = ShapeProblem(folder, out var full);
        if (shape != FolderProblem.None) return shape;
        if (IsRoot(full!)) return FolderProblem.DriveRoot;
        return InsideInstallDirectory(folder, installDir) ? FolderProblem.InstallDirectory : FolderProblem.None;
    }

    /// <summary>
    /// Whether a clips or screenshots folder is acceptable: fully qualified and not the program's own folder or inside
    /// it. A drive root is allowed here (a clip is one file, not a ring).
    /// </summary>
    internal static FolderProblem OutputFolderProblem(string? folder, string? installDir)
    {
        var shape = ShapeProblem(folder, out _);
        if (shape != FolderProblem.None) return shape;
        return InsideInstallDirectory(folder, installDir) ? FolderProblem.InstallDirectory : FolderProblem.None;
    }

    private static bool InsideInstallDirectory(string? folder, string? installDir)
        => !string.IsNullOrWhiteSpace(installDir) && IsSameOrInside(folder, installDir);

    /// <summary>NotFullyQualified when the text names no definite folder; otherwise None, with the resolved full path.</summary>
    private static FolderProblem ShapeProblem(string? folder, out string? full)
    {
        full = null;
        if (string.IsNullOrWhiteSpace(folder)) return FolderProblem.NotFullyQualified;
        try
        {
            var invalid = Path.GetInvalidPathChars();
            var trimmed = folder.Trim();
            if (trimmed.AsSpan().IndexOfAny(invalid) >= 0) return FolderProblem.NotFullyQualified;
            var expanded = Environment.ExpandEnvironmentVariables(trimmed);
            if (expanded.AsSpan().IndexOfAny(invalid) >= 0) return FolderProblem.NotFullyQualified;
            // "D:", "D:foo", "\foo", "buffer", ".\x" - resolved against whatever the asking program's current directory is.
            if (!Path.IsPathFullyQualified(expanded)) return FolderProblem.NotFullyQualified;
            full = Path.GetFullPath(expanded);
            // "\\server" alone is no share: a UNC path needs a server AND a share ("\\?\" / "\\.\" device paths aside).
            if (full.StartsWith(@"\\", StringComparison.Ordinal) && !full.StartsWith(@"\\?\", StringComparison.Ordinal)
                && !full.StartsWith(@"\\.\", StringComparison.Ordinal)
                && full.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length < 2)
            {
                full = null;
                return FolderProblem.NotFullyQualified;
            }
            return FolderProblem.None;
        }
        catch (Exception)
        {
            return FolderProblem.NotFullyQualified;
        }
    }

    private static bool IsRoot(string full)
    {
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root)) return false;
        char[] seps = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        return string.Equals(full.TrimEnd(seps), root.TrimEnd(seps), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A folder to suggest instead: "{drive}:\WKI_Clipper\{leaf}", on the drive the user typed (or the placeholder
    /// when the text names none).
    /// </summary>
    internal static string SuggestFolder(string leaf, string? typed, string drivePlaceholder)
    {
        var m = typed is null ? null : Regex.Match(typed, @"^\s*([A-Za-z]):", RegexOptions.CultureInvariant);
        var drive = m is { Success: true } ? m.Groups[1].Value.ToUpperInvariant() : drivePlaceholder;
        return $@"{drive}:\WKI_Clipper\{leaf}";
    }

    /// <summary>True for exactly the file names the replay buffer writes: seg_{generation}_{NN}.mp4.</summary>
    internal static bool IsBufferSegment(string? fileName)
        => fileName is not null && SegmentName.IsMatch(fileName);

    private static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            // GetFullPath lets NUL and friends through, and ExpandEnvironmentVariables silently
            // cuts the string at a NUL — check the raw text first. Such a path is no real folder.
            var invalid = Path.GetInvalidPathChars();
            if (raw.AsSpan().IndexOfAny(invalid) >= 0) return null;
            var expanded = SettingsService.ExpandPath(raw);
            if (expanded.AsSpan().IndexOfAny(invalid) >= 0) return null;

            var full = Path.GetFullPath(expanded);
            var root = Path.GetPathRoot(full) ?? "";
            // A drive root keeps its separator: trimmed, "C:\" would become the drive-relative "C:".
            return full.Length > root.Length
                ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : full;
        }
        catch (Exception)
        {
            // Illegal characters / unusable path: it cannot be inside anything.
            return null;
        }
    }
}
