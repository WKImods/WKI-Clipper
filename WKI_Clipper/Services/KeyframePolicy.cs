using System;
using System.Globalization;
using System.Text.Json.Serialization;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>
/// How the buffer's encoder places keyframes. The segment muxer can only cut at a keyframe, so with the
/// encoder's own GOP the segments drift past their nominal length and a 30-minute window gets fuzzy edges.
/// Forcing a keyframe at every segment boundary (or a fixed GOP) makes every segment exactly one segment long.
/// The default stays <see cref="KeyframeMode.EncoderDefault"/> until the measurement
/// (tools/measure-keyframes.ps1) has shown that a variant costs neither quality nor bitrate.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KeyframeMode
{
    /// <summary>No keyframe arguments: today's behaviour, byte-identical command line.</summary>
    EncoderDefault,
    /// <summary>A forced IDR frame at every segment boundary.</summary>
    ForcedAtSegmentBoundary,
    /// <summary>A fixed GOP of one second.</summary>
    FixedOneSecondGop,
    /// <summary>
    /// <see cref="ForcedAtSegmentBoundary"/> plus a GOP as long as the segment (<c>-g fps x segment seconds</c>), so the
    /// encoder's own keyframes coincide with the forced ones instead of adding to them. Variant C of
    /// tools/measure-keyframes.ps1 and its measured winner: exact segments at about half the IDR rate of plain B.
    /// </summary>
    ForcedBoundaryWithSegmentGop
}

public static class KeyframePolicy
{
    /// <summary>Flipped to the measured winner only after the acceptance run (docs: Phase 4 spec, 8.2).</summary>
    public const KeyframeMode Default = KeyframeMode.EncoderDefault;

    public static KeyframeMode Effective(ReplayBufferSettings rb) => rb.KeyframeModeOverride ?? Default;

    /// <summary>
    /// Encoder arguments for the mode, without a trailing space; "" for <see cref="KeyframeMode.EncoderDefault"/>.
    /// Forced keyframes only cut a segment when they are IDR frames, and every encoder family names that
    /// switch differently (AMF/QSV <c>-forced_idr</c>, NVENC/x264/x265 <c>-forced-idr</c>) - measured on ffmpeg 8.1.
    /// </summary>
    public static string EncoderArgs(string codec, int fps, int segmentSeconds, KeyframeMode mode)
    {
        codec ??= "";
        switch (mode)
        {
            case KeyframeMode.ForcedAtSegmentBoundary:
            case KeyframeMode.ForcedBoundaryWithSegmentGop:
            {
                int seg = Math.Max(1, segmentSeconds);
                string expr = $"-force_key_frames \"expr:gte(t,n_forced*{seg.ToString(CultureInfo.InvariantCulture)})\"";
                string idr = ForcedIdrFlag(codec);
                string forced = idr.Length == 0 ? expr : expr + " " + idr;
                if (mode == KeyframeMode.ForcedBoundaryWithSegmentGop)
                    forced += $" -g {(Math.Max(1, fps) * seg).ToString(CultureInfo.InvariantCulture)}";
                return forced;
            }
            case KeyframeMode.FixedOneSecondGop:
            {
                string gop = $"-g {Math.Max(1, fps).ToString(CultureInfo.InvariantCulture)}";
                return codec.Contains("hevc_amf", StringComparison.Ordinal) ? gop + " -gops_per_idr 1" : gop;
            }
            default:
                return "";
        }
    }

    /// <summary>
    /// Slack for the segment muxer when it decides whether a frame is "on" the boundary: half a frame.
    /// Null for <see cref="KeyframeMode.EncoderDefault"/> (no <c>-segment_time_delta</c> in the command).
    /// </summary>
    public static double? SegmentTimeDelta(int fps, KeyframeMode mode)
        => mode == KeyframeMode.EncoderDefault ? null : 0.5 / Math.Max(1, fps);

    private static string ForcedIdrFlag(string codec)
    {
        if (codec.Contains("_amf", StringComparison.Ordinal)) return "-forced_idr 1";
        if (codec.Contains("_qsv", StringComparison.Ordinal)) return "-forced_idr 1";
        if (codec.Contains("_nvenc", StringComparison.Ordinal)) return "-forced-idr 1";
        if (codec is "libx264" or "libx265") return "-forced-idr 1";
        return "";
    }
}
