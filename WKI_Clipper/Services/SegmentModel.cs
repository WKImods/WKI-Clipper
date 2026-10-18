using System;
using System.Globalization;
using System.Text.RegularExpressions;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>
/// What a buffer generation recorded. Two segments may be concatenated (stream copy) only when both
/// parts are equal: <see cref="Source"/> is WHAT was captured (window+size or monitor+size),
/// <see cref="Stream"/> is HOW it was encoded (codec, fps, resolution, audio tracks, capture path, keyframes).
/// Bitrate is deliberately not part of the stream key: a quality change keeps the history, level
/// differences between the parts are harmless for playback.
/// </summary>
public sealed record SegmentIdentity(string Source, string Stream)
{
    /// <summary>The same strings the buffer always used as identity (so nothing changes for existing logic).</summary>
    public static string SourceKey(bool wgc, IntPtr hwnd, int width, int height, int monitorIndex)
        => wgc ? $"wgc:{hwnd}:{width}x{height}" : $"mon:{monitorIndex}:{width}x{height}";

    /// <param name="capturePath">"wgc", "amf" or "dda": the capture paths produce different encoder input.</param>
    /// <param name="audioTracks">0 when audio init failed - that generation is video-only and cannot join the others.</param>
    public static string StreamKey(string codec, int fps, ResolutionPreset resolution, int audioTracks,
                                   string capturePath, KeyframeMode keyframes)
        => $"{codec}|{fps}|{resolution}|a{audioTracks}|{capturePath}|{keyframes}";

    /// <summary>
    /// The part of the stream key the USER chose: codec, fps, resolution and keyframe mode. The rest of
    /// <see cref="Stream"/> (capture path, audio track count) follows the capture TARGET: an exclusive-fullscreen game
    /// records "dda", an Alt-Tab to a window "wgc"; a new target may lack the process-loopback audio. Those
    /// differences keep two generations out of one clip, but they say nothing about whether the old footage is still
    /// worth keeping - only a changed setting does.
    /// </summary>
    public string SettingsKey
    {
        get
        {
            var parts = Stream.Split('|');
            // codec|fps|resolution|a{tracks}|capturePath|keyframes - anything else is compared as a whole
            return parts.Length == 6 ? $"{parts[0]}|{parts[1]}|{parts[2]}|{parts[5]}" : Stream;
        }
    }

    /// <summary>The settings key of the stream key <see cref="StreamKey"/> would build for these settings.</summary>
    public static string SettingsKeyOf(string codec, int fps, ResolutionPreset resolution, KeyframeMode keyframes)
        => $"{codec}|{fps}|{resolution}|{keyframes}";

    public bool IsWgc => Source.StartsWith("wgc:", StringComparison.Ordinal);

    /// <summary>The captured window for a WGC source, <see cref="IntPtr.Zero"/> for a monitor.</summary>
    public IntPtr WgcHwnd
    {
        get
        {
            if (!IsWgc) return IntPtr.Zero;
            var parts = Source.Split(':');
            return parts.Length >= 3 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
                ? new IntPtr(h)
                : IntPtr.Zero;
        }
    }
}

/// <summary>One ffmpeg run of the buffer: what it recorded, under which label, and when it ran.</summary>
public sealed record GenerationInfo(int Generation, SegmentIdentity Identity, string TargetLabel,
                                    DateTime StartedUtc, DateTime? StoppedUtc);

/// <summary>
/// What the running ffmpeg of the buffer records: its ring, the identity of its footage, its generation number and the
/// folder it writes to. Published as ONE reference when a run starts, so a save that reads it once cannot see half of
/// one run and half of the next.
/// </summary>
internal sealed record BufferEpoch(RingPlan Ring, SegmentIdentity Identity, int Generation, string BufferDir);

/// <summary>A pause of the buffer for a manual recording; <see cref="ToUtc"/> is null while it lasts.</summary>
public readonly record struct SuspendInterval(DateTime FromUtc, DateTime? ToUtc);

public static class SegmentName
{
    // [0-9] and \z on purpose (see PathRules): \d matches non-ASCII digits, $ tolerates a trailing newline.
    private static readonly Regex Rx = new(@"^seg_([0-9]+)_[0-9]+\.mp4\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"seg_12_07.mp4" gives 12. Anything else (including overflow) gives false. Case-insensitive.</summary>
    public static bool TryParseGeneration(string? fileName, out int generation)
    {
        generation = 0;
        if (fileName is null) return false;
        var m = Rx.Match(fileName);
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out generation);
    }
}
