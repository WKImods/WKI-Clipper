using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace WKI_Clipper.Services;

/// <summary>
/// The decisions of the Discord compress that need no process, disk or window: labels, folder and
/// file names, the note of the copy, the encoder choice. Pure so each one is a unit test.
/// </summary>
public static class DiscordCompressRules
{
    /// <summary>The sizes offered in the settings: Discord's tiers and a few round numbers between.</summary>
    public static readonly long[] TargetPresets =
    {
        10_000_000, 20_000_000, 50_000_000, 100_000_000, 500_000_000, 1_000_000_000
    };

    public const string FolderPrefix = "Komprimiert ";
    private const long MinimumTarget = 1_000_000;

    /// <summary>"20 MB", "1 GB", "8.5 MB" - decimal units, the same on every system language.</summary>
    public static string TargetLabel(long bytes)
    {
        var (value, unit) = Scale(bytes);
        return value + " " + unit;
    }

    /// <summary>"20MB" - the form used in folder and file names (no space).</summary>
    public static string CompactLabel(long bytes)
    {
        var (value, unit) = Scale(bytes);
        return value + unit;
    }

    private static (string Value, string Unit) Scale(long bytes)
    {
        bool gb = bytes >= 1_000_000_000;
        double v = bytes / (gb ? 1e9 : 1e6);
        var text = v.ToString("0.#", CultureInfo.InvariantCulture);
        return (text, gb ? "GB" : "MB");
    }

    /// <summary>"Komprimiert 20MB".</summary>
    public static string FolderName(long targetBytes) => FolderPrefix + CompactLabel(targetBytes);

    /// <summary>
    /// "&lt;clipsBase&gt;\Komprimiert 20MB\&lt;name&gt;_20MB.mp4". The result is always an mp4,
    /// whatever the source container was.
    /// </summary>
    public static string OutputPath(string clipsBase, string sourcePath, long targetBytes)
    {
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        return Path.Combine(clipsBase, FolderName(targetBytes), $"{stem}_{CompactLabel(targetBytes)}.mp4");
    }

    /// <summary>
    /// The note of the compressed copy: the original's note plus " (Discord 20 MB)". A clip without a
    /// note gets none. Past the store's length limit the note gives way, never the marker.
    /// </summary>
    public static string NoteForCopy(string? note, long targetBytes)
    {
        var text = (note ?? "").Trim();
        if (text.Length == 0) return "";

        var suffix = $" (Discord {TargetLabel(targetBytes)})";
        int room = GalleryMetaService.MaxNoteLength - suffix.Length;
        if (text.Length > room)
        {
            int cut = room;
            if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
            text = text[..Math.Max(0, cut)].TrimEnd();
        }
        return text + suffix;
    }

    public static string TooLongMessage(long targetBytes)
    {
        var label = TargetLabel(targetBytes);
        return L.T($"Clip zu lang für {label} — erst schneiden oder größeres Ziel wählen",
                   $"Clip too long for {label} — trim it first or pick a larger target");
    }

    /// <summary>
    /// The H.264 encoder to use, from the names the codec detection found: h264_amf, then h264_nvenc,
    /// then h264_qsv, else libx264. Discord plays H.264 everywhere; HEVC and AV1 are never considered.
    /// </summary>
    public static CompressEncoder PickEncoder(IEnumerable<string> detected)
    {
        var names = new HashSet<string>(detected, StringComparer.OrdinalIgnoreCase);
        if (names.Contains("h264_amf")) return CompressEncoder.Amf;
        if (names.Contains("h264_nvenc")) return CompressEncoder.Nvenc;
        if (names.Contains("h264_qsv")) return CompressEncoder.Qsv;
        return CompressEncoder.X264;
    }

    /// <summary>A hand-edited settings file must not be able to ask for a 0-byte or negative target.</summary>
    public static long EffectiveTarget(long stored, long fallback)
        => stored <= 0 ? fallback : Math.Max(MinimumTarget, stored);
}
