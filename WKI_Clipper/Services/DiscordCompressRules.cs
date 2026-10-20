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

    /// <summary>
    /// How much disk an encode attempt can really write, for the free-space check: the video it asks for (a hardware
    /// encoder may overshoot by a quarter) plus the planned audio and container share - never more than the target,
    /// which is the limit the result has to meet anyway. NOT the target itself: the video rate is capped at the
    /// source's own, so a 30 s clip aimed at 500 MB comes out at a few tens of MB, and demanding 500 MB (x 1.1 plus
    /// the reserve) would refuse it on a small disk for no reason.
    /// </summary>
    public static long EstimateOutputBytes(long requestBps, double seconds, long audioBytes, long muxBytes, long targetBytes)
    {
        double video = Math.Max(0, requestBps) / 8.0 * Math.Max(0, seconds) * 1.25;
        long total = (long)Math.Ceiling(video) + Math.Max(0, audioBytes) + Math.Max(0, muxBytes);
        return Math.Min(Math.Max(0, targetBytes), total);
    }

    /// <summary>
    /// Size of the x264 pass-1 statistics: the ".log" (a text line per frame) and the ".mbtree" (2 bytes per
    /// macroblock per frame - about 16 KB per 1080p frame). 600 MB for ten minutes of 1080p60.
    /// </summary>
    public static long EstimatePassLogBytes(int width, int height, int fps, TimeSpan duration)
    {
        if (width <= 0 || height <= 0 || fps <= 0 || duration <= TimeSpan.Zero) return 0;
        long macroblocks = (long)((width + 15) / 16) * ((height + 15) / 16);
        double frames = fps * duration.TotalSeconds;
        return (long)Math.Ceiling((macroblocks * 2 + 200) * frames);
    }

    private static readonly System.Text.RegularExpressions.Regex PassStatsName = new(
        @"^wki_[0-9a-f]{32}-\d+\.log(\.mbtree)?(\.temp)?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant
        | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>A file name the 2-pass encode writes next to its prefix "wki_&lt;32 hex&gt;" (and that a crash can leave behind).</summary>
    public static bool IsPassStatsFile(string fileName) => PassStatsName.IsMatch(fileName);

    /// <summary>
    /// Whether an ffmpeg failure (its last stderr lines) points at hardware DECODING: the d3d11va device, the
    /// hwaccel setup. An encoder that cannot start (AMF/NVENC busy), a full disk or a damaged input fail the same
    /// way for a CPU decode, so they are not a reason to switch decoding off.
    /// </summary>
    public static bool LooksLikeHwDecodeFailure(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return false;
        foreach (var marker in HwDecodeMarkers)
            if (stderr.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static readonly string[] HwDecodeMarkers =
    {
        "d3d11va", "hwaccel", "Failed setup for format d3d11", "Device creation failed"
    };

    /// <summary>A hand-edited settings file must not be able to ask for a 0-byte or negative target.</summary>
    public static long EffectiveTarget(long stored, long fallback)
        => stored <= 0 ? fallback : Math.Max(MinimumTarget, stored);

    /// <summary>
    /// The two targets of the "Discord" buttons from the settings (20 MB / 500 MB when unset or invalid) — the gallery
    /// menu and the clip editor offer the same two.
    /// </summary>
    public static (long Small, long Large) Targets(Models.DiscordCompressSettings settings)
        => (EffectiveTarget(settings.SmallTargetBytes, 20_000_000), EffectiveTarget(settings.LargeTargetBytes, 500_000_000));
}
