using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace WKI_Clipper.Services;

/// <summary>
/// The decisions of the clip editor that need no process, disk or window: names, the note, time and size text,
/// estimates, encoder bitrate, and the exact ffmpeg arguments of both cut modes. Pure so each one is a unit test.
///
/// The argument strings were measured against stamped test footage (every frame carries its own number): fast and
/// exact cuts were frame-accurate on H.264 / HEVC sources, with an edit-list offset, with B-frames (the one
/// exception: a stream-copy END on a B-frame source can overshoot by about two frames). Do not "tidy" them.
/// </summary>
public static class ClipCutRules
{
    /// <summary>1 ms: below any frame duration, above the rounding of "0.000000" and of a 1/15360 time base.</summary>
    public const double EdgeMarginSeconds = 0.001;

    private const string Dash = "–";
    private static readonly Regex CutSuffix = new(@"^(?<stem>.*?)_cut(?: \(\d+\))?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ================================================================ names and notes

    /// <summary>
    /// "&lt;dir&gt;\&lt;stem&gt;_cut.mp4" — always an mp4, whatever the source container was. A "_cut" or "_cut (n)" already at
    /// the end of the stem is not doubled. Making the name unique (" (2)") is the job runner's.
    /// </summary>
    public static string OutputPath(string sourcePath)
    {
        var dir = Path.GetDirectoryName(sourcePath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var match = CutSuffix.Match(stem);
        stem = match.Success ? match.Groups["stem"].Value + "_cut" : stem + "_cut";
        return Path.Combine(dir, stem + ".mp4");
    }

    /// <summary>
    /// The note of the copy: "&lt;note&gt; (Ausschnitt 03:12–05:40)", or just "Ausschnitt 03:12–05:40" for a clip without
    /// a note (EN: "(excerpt …)" / "Excerpt …"). The start is rounded down and the end up, so the named range surely
    /// contains what it names. Past the store's length limit the note gives way, never the marker.
    /// </summary>
    public static string NoteForCut(string? note, double startSeconds, double endSeconds)
    {
        var range = FormatClock(Math.Floor(startSeconds + 1e-6)) + Dash + FormatClock(Math.Ceiling(endSeconds - 1e-6));
        var text = (note ?? "").Trim();
        if (text.Length == 0) return L.T("Ausschnitt ", "Excerpt ") + range;

        var suffix = L.T($" (Ausschnitt {range})", $" (excerpt {range})");
        int room = GalleryMetaService.MaxNoteLength - suffix.Length;
        if (text.Length > room)
        {
            int cut = room;
            if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
            text = text[..Math.Max(0, cut)].TrimEnd();
        }
        return text + suffix;
    }

    // ================================================================ text

    /// <summary>"mm:ss", from one hour "h:mm:ss"; whole seconds (rounded down); negative gives 0.</summary>
    public static string FormatClock(double seconds)
    {
        long total = seconds > 0 && !double.IsNaN(seconds) ? (long)Math.Floor(seconds + 1e-9) : 0;
        long h = total / 3600, m = total / 60 % 60, s = total % 60;
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{m:00}:{s:00}");
    }

    /// <summary>"mm:ss.fff", from one hour "h:mm:ss.fff". Rounded to the millisecond as a whole (59.9995 is 01:00.000, not 00:59.1000).</summary>
    public static string FormatPosition(double seconds)
    {
        long ms = seconds > 0 && !double.IsNaN(seconds) ? (long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero) : 0;
        long h = ms / 3_600_000, m = ms / 60_000 % 60, s = ms / 1000 % 60, f = ms % 1000;
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}.{f:000}")
            : string.Create(CultureInfo.InvariantCulture, $"{m:00}:{s:00}.{f:000}");
    }

    /// <summary>"980 MB", "1,0 GB" (EN "1.0 GB"): binary units, the decimal mark follows the app language.</summary>
    public static string FormatBytes(long bytes)
    {
        var mark = L.English ? "." : ",";
        if (bytes >= 1L << 30)
            return (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture).Replace(".", mark) + " GB";
        if (bytes >= 1L << 20)
            return (bytes / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB";
        return (Math.Max(0, bytes) / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
    }

    // ================================================================ messages

    /// <summary>Why a selection cannot be cut; null when it can.</summary>
    public static string? ValidationMessage(CutValidation validation) => validation switch
    {
        CutValidation.Reversed => L.T("Das Ende liegt vor dem Start.", "The end is before the start."),
        CutValidation.WholeClip => L.T("Die Auswahl umfasst den ganzen Clip — nichts zu schneiden.",
                                       "The selection covers the whole clip — nothing to trim."),
        _ => null
    };

    /// <summary>The note on a result whose frame count is not the expected one (-1 = the result could not be read).</summary>
    public static string VerifyMessage(int frames, int expected) => frames < 0
        ? L.T("Hinweis: Ergebnis konnte nicht geprüft werden — Original wurde behalten.",
              "Note: the result could not be checked — the original was kept.")
        : L.T($"Hinweis: Ergebnis hat {frames} statt {expected} Frames — Original wurde behalten.",
              $"Note: the result has {frames} instead of {expected} frames — the original was kept.");

    /// <summary>What happened to the original; shown in the editor (a warning for anything but a successful move).</summary>
    public static string RecycleMessage(RecycleOutcome outcome) => outcome switch
    {
        RecycleOutcome.Recycled => L.T("Original liegt im Papierkorb.", "The original is in the Recycle Bin."),
        RecycleOutcome.InUse => L.T("Original ist in Verwendung und wurde behalten.", "The original is in use and was kept."),
        RecycleOutcome.Declined => L.T("Original wurde behalten.", "The original was kept."),
        RecycleOutcome.DeletedPermanently => L.T("Original wurde gelöscht, liegt aber nicht im Papierkorb.",
                                                 "The original was deleted but is not in the Recycle Bin."),
        _ => L.T("Original konnte nicht in den Papierkorb verschoben werden und wurde behalten.",
                 "The original could not be moved to the Recycle Bin and was kept.")
    };

    // ================================================================ estimates

    /// <summary>Stream copy writes the source bytes of the range: share of the file x 1.02 + 1 MB.</summary>
    public static long EstimateFastBytes(long sourceBytes, double rangeSeconds, double totalSeconds)
    {
        double share = totalSeconds > 0 ? Math.Clamp(rangeSeconds / totalSeconds, 0, 1) : 1;
        return (long)Math.Round(sourceBytes * share * 1.02) + (1L << 20);
    }

    /// <summary>
    /// The video bitrate of an exact cut: the source's own (bytes x 8 / seconds), x1.4 for HEVC and x1.6 for AV1 (they
    /// are more efficient, H.264 needs more to look the same), x1.2 for anything else; between 2 and 80 Mbit/s,
    /// rounded to 1000.
    /// </summary>
    public static long ExactVideoBitrate(long videoBytes, double videoSeconds, string codec)
    {
        double source = videoSeconds > 0 && videoBytes > 0 ? videoBytes * 8.0 / videoSeconds : 12_000_000;
        double factor = codec.ToLowerInvariant() switch
        {
            "avc1" or "avc3" => 1.0,
            "hvc1" or "hev1" => 1.4,
            "av01" => 1.6,
            _ => 1.2
        };
        double bps = Math.Clamp(source * factor, 2_000_000, 80_000_000);
        return (long)(Math.Round(bps / 1000) * 1000);
    }

    /// <summary>(video + 192 kbit/s per audio track) x seconds / 8, plus 15 % (cbr overshoots by ~10 %).</summary>
    public static long EstimateExactBytes(long videoBps, int audioTracks, double rangeSeconds)
        => (long)Math.Round((videoBps + 192_000.0 * Math.Max(0, audioTracks)) * Math.Max(0, rangeSeconds) / 8 * 1.15);

    /// <summary>
    /// How long a re-encode takes, for the display only: 4.5x real time at 1080p60 (measured 4.4-5.5x with the AMD
    /// encoder), scaled by the pixel rate, between 0.5x and 20x. The software encoder (libx264 veryfast on half the
    /// cores) is rated 3x at 1080p60 - measured 5.6x on a Ryzen 7 9700X with test footage, real footage and weaker CPUs
    /// are slower - and may fall to 0.1x on a very large picture.
    /// </summary>
    public static double EstimateExactSeconds(double rangeSeconds, int width, int height, double fps, bool software = false)
    {
        double reference = software ? 3.0 : 4.5;
        double factor = reference;
        if (width > 0 && height > 0 && fps > 0)
            factor = reference * (1920.0 * 1080 * 60) / ((double)width * height * fps);
        factor = Math.Clamp(factor, software ? 0.1 : 0.5, 20);
        return Math.Max(0, rangeSeconds) / factor;
    }

    // ================================================================ encoder choice

    /// <summary>Hardware H.264 encoders (AMD VCN, NVENC, Quick Sync) cannot open a picture wider or taller than this.</summary>
    public const int HardwareH264MaxSide = 4096;

    /// <summary>
    /// The H.264 encoder of an exact cut: the one <see cref="DiscordCompressRules.PickEncoder"/> picks - except that a
    /// picture beyond <see cref="HardwareH264MaxSide"/> px (32:9 monitors at 5120x1440, dual 4K) goes to libx264, since
    /// the hardware encoders refuse to start there and the cut would fail every time. (The ffmpeg encoder test only
    /// encodes 640x480, so "detected" says nothing about the picture size.) Unknown size (0) keeps the normal choice.
    /// </summary>
    public static CompressEncoder PickExactEncoder(IEnumerable<string> detected, int width, int height)
        => width > HardwareH264MaxSide || height > HardwareH264MaxSide
            ? CompressEncoder.X264
            : DiscordCompressRules.PickEncoder(detected);

    // ================================================================ ffmpeg arguments

    /// <summary>
    /// Stream copy. <paramref name="fileStartKey"/> is the ffmpeg time of the keyframe the cut starts on;
    /// <paramref name="fileEndExclusive"/> null means "to the end" (no -t). -ss is always given — without it -t counts
    /// from the file start and cuts wrongly when the video has an offset. +1 ms after the keyframe, -1 ms before the
    /// next frame: exact frames in, exact frames out. make_zero keeps an mp4 edit list from hiding the keyframe.
    /// Never "-map 0": the chapter text track of a long clip would come along as an unplayable data stream.
    /// </summary>
    public static string BuildFastArgs(string source, string partPath, double fileStartKey, double? fileEndExclusive, bool faststart)
    {
        double start = fileStartKey + EdgeMarginSeconds;
        var sb = new StringBuilder("-hide_banner -loglevel warning");
        sb.Append(" -ss ").Append(Num(start));
        sb.Append(" -i ").Append(Quote(source));
        if (fileEndExclusive is { } end)
            sb.Append(" -t ").Append(Num(Math.Max(EdgeMarginSeconds, end - EdgeMarginSeconds - start)));
        sb.Append(" -map 0:v:0 -map 0:a? -c copy -avoid_negative_ts make_zero");
        if (faststart) sb.Append(" -movflags +faststart");
        sb.Append(" -f mp4 ").Append(Quote(partPath));
        return sb.ToString();
    }

    /// <summary>
    /// Re-encode of the range, frame-accurate. -ss before -i with 1 ms of margin starts at the first frame at or after
    /// the cut; -fps_mode passthrough keeps every frame (without it the last one is lost at the segment seams of a
    /// long clip). All audio tracks stay separate (a mic track is evidence, no mixing). No -hwaccel (CPU decoding was
    /// faster here) and no colour flags (they stay as in the source).
    /// </summary>
    public static string BuildExactArgs(string source, string partPath, double fileStartFrame, double? fileEndExclusive,
        CompressEncoder encoder, long videoBps, int audioTracks, bool faststart)
    {
        var sb = new StringBuilder("-hide_banner -loglevel warning");
        double seek = Math.Max(0, fileStartFrame - EdgeMarginSeconds);
        if (seek > 0) sb.Append(" -ss ").Append(Num(seek));
        sb.Append(" -i ").Append(Quote(source));
        if (fileEndExclusive is { } end)
            sb.Append(" -t ").Append(Num(Math.Max(EdgeMarginSeconds, end - fileStartFrame - EdgeMarginSeconds)));
        sb.Append(" -map 0:v:0 -map 0:a? -fps_mode passthrough ");
        sb.Append(VideoEncoderArgs(encoder, videoBps));
        sb.Append(audioTracks > 0 ? " -c:a aac -b:a 192k" : " -an");
        if (faststart) sb.Append(" -movflags +faststart");
        sb.Append(" -f mp4 ").Append(Quote(partPath));
        return sb.ToString();
    }

    /// <summary>One still frame as BMP on stdout (the fallback preview), scaled down to <paramref name="maxWidth"/>.</summary>
    public static string BuildFrameGrabArgs(string source, double fileSeconds, int maxWidth)
    {
        var sb = new StringBuilder("-hide_banner -loglevel error -nostdin");
        double seek = Math.Max(0, fileSeconds - EdgeMarginSeconds);
        if (seek > 0) sb.Append(" -ss ").Append(Num(seek));
        sb.Append(" -i ").Append(Quote(source));
        sb.Append(" -frames:v 1 -an -sn -dn -vf \"scale='min(")
          .Append(maxWidth.ToString(CultureInfo.InvariantCulture))
          .Append(",iw)':-2\" -f image2pipe -c:v bmp -");
        return sb.ToString();
    }

    private static string VideoEncoderArgs(CompressEncoder encoder, long bps)
    {
        var b = bps.ToString(CultureInfo.InvariantCulture);
        var buf = (bps * 2).ToString(CultureInfo.InvariantCulture);
        return encoder switch
        {
            CompressEncoder.Amf => $"-c:v h264_amf -usage transcoding -quality quality -profile:v high -rc cbr -b:v {b}",
            CompressEncoder.Nvenc => $"-c:v h264_nvenc -preset p5 -tune hq -rc cbr -b:v {b} -maxrate {b} -bufsize {buf}",
            CompressEncoder.Qsv => $"-c:v h264_qsv -preset medium -b:v {b} -maxrate {b} -bufsize {buf}",
            _ => $"-c:v libx264 -preset veryfast -crf 18 -maxrate {b} -bufsize {buf}"
        };
    }

    /// <summary>Culture-independent: a decimal comma would wreck the time.</summary>
    private static string Num(double seconds) => seconds.ToString("0.000000", CultureInfo.InvariantCulture);

    private static string Quote(string path) => "\"" + path + "\"";
}
