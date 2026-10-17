using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace WKI_Clipper.Services;

/// <summary>What `ffmpeg -i file` reports about a media file (ffprobe.exe is not shipped).</summary>
/// <param name="Duration">Null when ffmpeg prints "N/A" (live capture, broken container).</param>
/// <param name="VideoCodec">Codec of the first real video stream (cover art excluded); null for audio-only files.</param>
public sealed record MediaProbe(
    TimeSpan? Duration,
    string? VideoCodec,
    int Width,
    int Height,
    double Fps,
    IReadOnlyList<string> AudioCodecs)
{
    public int AudioStreamCount => AudioCodecs.Count;
}

/// <summary>Pure parser for the stderr of `ffmpeg -hide_banner -i file` — unit-tested with real samples.</summary>
public static class FFmpegProbeParser
{
    private static readonly Regex DurationRx = new(
        @"^\s*Duration:\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "  Stream #0:0[0x1](und): Video: h264 (High) ..."  /  "  Stream #0:1: Audio: aac ..."
    private static readonly Regex StreamRx = new(
        @"^\s*Stream #\d+:\d+[^:]*: (Video|Audio|Subtitle|Data|Attachment): (.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SizeRx = new(@"^(\d{1,5})x(\d{1,5})(?:\s|$)", RegexOptions.Compiled);
    private static readonly Regex FpsRx = new(@"^(\d+(?:\.\d+)?)\s+fps$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TbrRx = new(@"^(\d+(?:\.\d+)?)(k?)\s+tbr$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Null when the text does not describe a media file (missing file, invalid data).</summary>
    public static MediaProbe? Parse(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return null;

        bool sawInput = false;
        TimeSpan? duration = null;
        string? videoCodec = null;
        int width = 0, height = 0;
        double fps = 0;
        var audio = new List<string>();

        foreach (var raw in stderr.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("Output #", StringComparison.Ordinal)
                || line.StartsWith("Stream mapping:", StringComparison.Ordinal)) break;
            if (line.StartsWith("Input #", StringComparison.Ordinal)) { sawInput = true; continue; }

            if (duration is null && DurationRx.Match(line) is { Success: true } d)
            {
                duration = TimeSpan.FromHours(int.Parse(d.Groups[1].Value, CultureInfo.InvariantCulture))
                    + TimeSpan.FromMinutes(int.Parse(d.Groups[2].Value, CultureInfo.InvariantCulture))
                    + TimeSpan.FromSeconds(double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture));
                continue;
            }

            if (StreamRx.Match(line) is not { Success: true } s) continue;
            sawInput = true;
            var details = s.Groups[2].Value;

            switch (s.Groups[1].Value)
            {
                case "Audio":
                    audio.Add(FirstWord(details));
                    break;
                case "Video" when videoCodec is null && !details.Contains("(attached pic)", StringComparison.Ordinal):
                    videoCodec = FirstWord(details);
                    ReadVideoFields(details, out width, out height, out fps);
                    break;
            }
        }

        return sawInput ? new MediaProbe(duration, videoCodec, width, height, fps, audio) : null;
    }

    private static void ReadVideoFields(string details, out int width, out int height, out double fps)
    {
        width = height = 0;
        fps = 0;
        double tbr = 0;
        foreach (var field in SplitTopLevel(details))
        {
            if (width == 0 && SizeRx.Match(field) is { Success: true } size)
            {
                width = int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture);
                height = int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture);
            }
            else if (FpsRx.Match(field) is { Success: true } f)
                fps = double.Parse(f.Groups[1].Value, CultureInfo.InvariantCulture);
            else if (TbrRx.Match(field) is { Success: true } t)
                tbr = double.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture) * (t.Groups[2].Value == "k" ? 1000 : 1);
        }
        if (fps == 0) fps = tbr;   // no "fps" field for variable-frame-rate sources
    }

    private static string FirstWord(string details)
    {
        int end = details.IndexOfAny(new[] { ' ', ',' });
        return end < 0 ? details : details[..end];
    }

    /// <summary>Splits on commas that are not inside (...) or [...] — "yuv420p(tv, progressive)" stays one field.</summary>
    private static List<string> SplitTopLevel(string text)
    {
        var fields = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth = Math.Max(0, depth - 1);
            else if (c == ',' && depth == 0)
            {
                fields.Add(text[start..i].Trim());
                start = i + 1;
            }
        }
        fields.Add(text[start..].Trim());
        return fields;
    }
}
