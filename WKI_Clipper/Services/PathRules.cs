using System;
using System.IO;
using System.Text.RegularExpressions;

namespace WKI_Clipper.Services;

/// <summary>
/// Pure path rules that keep the replay buffer's seg_N_NN.mp4 files out of the gallery.
/// The buffer writes a segment every few seconds; if its folder is (inside) the clips
/// folder, every one of them shows up as a brand-new "clip".
/// </summary>
internal static class PathRules
{
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
    internal static string SafeBufferFolder(string buffer, string clips, string screenshots, string fallback)
        => IsUnsafeBufferFolder(buffer, clips, screenshots) ? fallback : buffer;

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
