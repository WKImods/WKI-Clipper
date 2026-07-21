using System.Collections.Generic;
using System.Globalization;
using System.Text;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>
/// Builds ffmpeg command-line strings from AppSettings. Single source of truth
/// for the capture/encode pipeline so the manual-record and replay-buffer
/// services stay in lock-step.
/// </summary>
public static class FFmpegCommandBuilder
{
    /// <summary>
    /// Build a recording command.
    /// </summary>
    /// <param name="audioPipeArgs">
    /// When non-null/empty, the audio input is taken from an external named pipe
    /// (already pre-mixed by <see cref="AudioPipeService"/>) and the dshow path
    /// is skipped entirely. The string must be a complete ffmpeg input fragment
    /// like <c>-f s16le -ar 48000 -ac 2 -i "\\.\pipe\WKI_Clipper_Audio_xyz"</c>.
    /// </param>
    public static string Build(AppSettings settings, string outputPath, bool segmentOutput,
        int segmentDurationSec = 5, int segmentWrap = 12, string? audioPipeArgs = null,
        string? captureWindowTitle = null)
    {
        var sb = new StringBuilder();
        sb.Append("-hide_banner -loglevel warning -nostats ");
        // Low-latency demuxer/muxer flags — kill ffmpeg's internal buffering so
        // PCM bytes from our pipe reach the encoder immediately instead of
        // sitting in a 500 ms-1 s queue (which manifests as audio lag in clips).
        sb.Append("-fflags +nobuffer+flush_packets -flags low_delay ");

        bool windowCapture = !string.IsNullOrWhiteSpace(captureWindowTitle);

        if (windowCapture)
        {
            // gdigrab follows the named window's screen position. Works while the
            // window is visible (incl. when moved). When fully hidden behind
            // another window, it captures whatever is rendered at that location
            // (Windows GDI limitation — true hidden-window capture needs WGC).
            sb.Append("-f gdigrab -framerate ").Append(settings.Video.Framerate).Append(' ');
            sb.Append("-i title=\"").Append(EscapeForDshow(captureWindowTitle!)).Append("\" ");
        }
        else
        {
            // DXGI Desktop Duplication — always captures the full primary display
            // at its native resolution. Any downscale happens in the -vf chain
            // below. (ddagrab's video_size parameter CROPS to that region from
            // the top-left, it does NOT downscale.)
            sb.Append("-f lavfi -i \"ddagrab=output_idx=0:framerate=").Append(settings.Video.Framerate);
            sb.Append("\" ");
        }

        bool usePipe = !string.IsNullOrWhiteSpace(audioPipeArgs);
        int audioInputs = 0;
        var audioLabels = new List<string>();

        if (usePipe)
        {
            // AudioPipeService already mixed Mic + System into one PCM stream.
            // -itsoffset shifts audio timestamps to compensate WASAPI capture lag
            // (typical: -150 ms so audio appears 150 ms earlier and lines up with
            // the low-latency ddagrab video).
            double offsetSec = settings.Audio.OffsetMilliseconds / 1000.0;
            if (Math.Abs(offsetSec) > 0.001)
            {
                sb.Append("-itsoffset ").Append(offsetSec.ToString("0.000", CultureInfo.InvariantCulture)).Append(' ');
            }
            sb.Append(audioPipeArgs).Append(' ');
            audioInputs = 1;
        }
        else
        {
            // Legacy fallback: dshow with explicit device names (requires Stereo Mix
            // or VB-CABLE for system loopback). Kept for safety.
            if (settings.Audio.RecordSystemSound && !string.IsNullOrWhiteSpace(settings.Audio.SystemDeviceId)
                && settings.Audio.SystemDeviceId != "default")
            {
                sb.Append("-f dshow -i audio=\"").Append(EscapeForDshow(settings.Audio.SystemDeviceId)).Append("\" ");
                audioLabels.Add($"[{1 + audioInputs}:a]");
                audioInputs++;
            }
            if (settings.Audio.RecordMicrophone && !string.IsNullOrWhiteSpace(settings.Audio.MicDeviceId)
                && settings.Audio.MicDeviceId != "default")
            {
                sb.Append("-f dshow -i audio=\"").Append(EscapeForDshow(settings.Audio.MicDeviceId)).Append("\" ");
                audioLabels.Add($"[{1 + audioInputs}:a]");
                audioInputs++;
            }
        }

        // Audio mapping
        if (usePipe && audioInputs > 0)
        {
            sb.Append("-map 0:v -map 1:a ");
        }
        else if (audioInputs > 1)
        {
            sb.Append("-filter_complex \"");
            foreach (var lbl in audioLabels) sb.Append(lbl);
            sb.Append("amix=inputs=").Append(audioInputs).Append(":duration=longest:dropout_transition=2[aout]\" ");
            sb.Append("-map 0:v -map \"[aout]\" ");
        }
        else if (audioInputs == 1)
        {
            sb.Append("-map 0:v -map 1:a ");
        }
        else
        {
            sb.Append("-map 0:v ");
        }

        // Effective bitrate
        int bitrate = settings.Video.Quality == QualityPreset.Custom
            ? settings.Video.Bitrate
            : QualityPresets.ComputeBitrate(settings.Video.Quality, settings.Video.Resolution);

        // For ddagrab the frames are on the GPU (d3d11); hwdownload brings them to sysmem.
        // gdigrab already delivers BGRA in sysmem, so we skip hwdownload there.
        string codec = settings.Video.Codec;
        bool isAmf   = codec.Contains("amf");
        bool isNvenc = codec.Contains("nvenc");
        bool isQsv   = codec.Contains("qsv");
        bool isHwAccel = isAmf || isNvenc || isQsv;

        // Build the video filter chain. ddagrab → hwdownload → optional scale+pad
        // (preserves aspect ratio: scales to fit then letterboxes / pillarboxes
        // with black bars, so a 21:9 ultrawide capture lands in a 16:9 clip
        // without cropping).
        var (targetW, targetH) = GetResolution(settings.Video.Resolution);
        bool needScale = targetW > 0 && targetH > 0;
        string scaleFilter = needScale
            ? $",scale={targetW}:{targetH}:force_original_aspect_ratio=decrease," +
              $"pad={targetW}:{targetH}:(ow-iw)/2:(oh-ih)/2:black,setsar=1"
            : "";

        if (isHwAccel)
        {
            if (windowCapture)
            {
                // gdigrab delivers BGRA in system memory — scale on CPU if needed.
                if (needScale) sb.Append("-vf \"").Append(scaleFilter.TrimStart(',')).Append("\" ");
            }
            else
            {
                // ddagrab → D3D11 textures. AMF/NVENC/QSV can't consume D3D11
                // textures directly (SubmitInput error 18), so hwdownload is
                // required. But we skip CPU scale/pad when resolution is Native
                // — that alone saves ~25-50% FPS overhead on ultrawide monitors.
                sb.Append("-vf \"hwdownload,format=bgra").Append(scaleFilter).Append("\" ");
            }

            sb.Append("-c:v ").Append(codec).Append(' ');
            if (isAmf)
            {
                sb.Append("-quality balanced -rc cbr ");
            }
            else if (isNvenc)
            {
                sb.Append("-preset p4 -rc cbr ");
            }
            else if (isQsv)
            {
                sb.Append("-preset medium ");
            }
            sb.Append("-b:v ").Append(bitrate).Append(' ');
        }
        else
        {
            // CPU encoder (libx264 / libx265)
            if (windowCapture)
            {
                if (needScale)
                    sb.Append("-vf \"").Append(scaleFilter.TrimStart(',')).Append(",format=yuv420p\" ");
                else
                    sb.Append("-pix_fmt yuv420p ");
            }
            else
            {
                sb.Append("-vf \"hwdownload,format=bgra").Append(scaleFilter).Append(",format=yuv420p\" ");
            }
            sb.Append("-c:v ").Append(codec).Append(' ');
            // tune zerolatency = no lookahead, no B-frames → no 670 ms encoder queue.
            sb.Append("-preset veryfast -tune zerolatency ");
            sb.Append("-b:v ").Append(bitrate).Append(' ');
            sb.Append("-pix_fmt yuv420p ");
        }

        if (audioInputs > 0)
        {
            sb.Append("-c:a aac -b:a 192k ");
        }

        if (segmentOutput)
        {
            sb.Append("-f segment ");
            sb.Append("-segment_time ").Append(segmentDurationSec).Append(' ');
            sb.Append("-segment_wrap ").Append(segmentWrap).Append(' ');
            sb.Append("-reset_timestamps 1 ");
            sb.Append("-segment_format mp4 ");
            sb.Append('"').Append(outputPath).Append('"');
        }
        else
        {
            sb.Append("-movflags +faststart ");
            sb.Append('"').Append(outputPath).Append('"');
        }

        return sb.ToString();
    }

    public static string BuildConcat(string listFilePath, string outputPath)
        => $"-hide_banner -loglevel warning -f concat -safe 0 -i \"{listFilePath}\" -c copy -movflags +faststart \"{outputPath}\"";

    public static string BuildScreenshot(string outputPath)
        => $"-hide_banner -loglevel warning -f lavfi -i \"ddagrab=output_idx=0:framerate=1\" -frames:v 1 -y \"{outputPath}\"";

    public static string BuildThumbnail(string videoPath, string thumbnailPath, int width = 320)
        => $"-hide_banner -loglevel error -ss 1 -i \"{videoPath}\" -frames:v 1 -vf \"scale={width}:-1\" -y \"{thumbnailPath}\"";

    private static (int w, int h) GetResolution(ResolutionPreset preset) => preset switch
    {
        ResolutionPreset.FullHD => (1920, 1080),
        ResolutionPreset.WQHD => (2560, 1440),
        ResolutionPreset.UHD => (3840, 2160),
        ResolutionPreset.Native => (0, 0),
        _ => (0, 0)
    };

    private static string EscapeForDshow(string deviceName)
        => deviceName.Replace("\"", "\\\"");
}
