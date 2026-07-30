using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
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
    /// <param name="monitorIndex">
    /// DXGI output index (ddagrab output_idx) of the monitor to capture, resolved
    /// by <see cref="CaptureTargetResolver"/>. Used when no
    /// <paramref name="videoInputArgs"/> is supplied.
    /// </param>
    /// <param name="videoInputArgs">
    /// When non-null, the video comes from an external rawvideo named pipe
    /// (occlusion-proof WGC window capture via <see cref="VideoPipeService"/>)
    /// instead of ddagrab. Complete input fragment incl. -f/-pixel_format/
    /// -video_size/-framerate/-i.
    /// </param>
    public static string Build(AppSettings settings, string outputPath, bool segmentOutput,
        int segmentDurationSec = 5, int segmentWrap = 12, IReadOnlyList<string>? audioPipeArgs = null,
        int monitorIndex = 0, string? videoInputArgs = null)
    {
        var sb = new StringBuilder();
        sb.Append("-hide_banner -loglevel warning -nostats ");
        // Low-latency demuxer/muxer flags — kill ffmpeg's internal buffering so
        // PCM bytes from our pipe reach the encoder immediately instead of
        // sitting in a 500 ms-1 s queue (which manifests as audio lag in clips).
        sb.Append("-fflags +nobuffer+flush_packets -flags low_delay ");

        bool rawInput = !string.IsNullOrWhiteSpace(videoInputArgs);
        if (rawInput)
        {
            // WGC window frames, already BGRA in system memory.
            sb.Append(videoInputArgs).Append(' ');
        }
        else
        {
            // DXGI Desktop Duplication of the chosen monitor. Captures at native
            // resolution; any downscale happens in the -vf chain below.
            sb.Append("-f lavfi -i \"ddagrab=output_idx=").Append(monitorIndex)
              .Append(":framerate=").Append(settings.Video.Framerate).Append("\" ");
        }

        bool usePipe = audioPipeArgs != null && audioPipeArgs.Count > 0;
        int audioInputs = 0;
        var audioLabels = new List<string>();

        if (usePipe)
        {
            // One or more PCM pipes from AudioPipeService(s). A single pipe carries the
            // mixed mic+system stream; two pipes carry system (track 1) and mic (track 2)
            // separately. -itsoffset shifts audio timestamps to compensate WASAPI capture
            // lag (typical: -150 ms so audio lines up with the low-latency video); the
            // same offset applies to every audio input so the tracks stay aligned.
            double offsetSec = settings.Audio.OffsetMilliseconds / 1000.0;
            foreach (var pipeArg in audioPipeArgs!)
            {
                if (Math.Abs(offsetSec) > 0.001)
                    sb.Append("-itsoffset ").Append(offsetSec.ToString("0.000", CultureInfo.InvariantCulture)).Append(' ');
                sb.Append(pipeArg).Append(' ');
                audioInputs++;
            }
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
            // Video is input 0; each PCM pipe is its own output audio track.
            sb.Append("-map 0:v ");
            for (int i = 0; i < audioInputs; i++) sb.Append("-map ").Append(i + 1).Append(":a ");
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

        // Effective bitrate (monitor-aware for the Native preset).
        int bitrate = EffectiveBitrate(settings, monitorIndex);

        // ddagrab delivers frames on the GPU (d3d11); hwdownload brings them to sysmem.
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
            if (rawInput)
            {
                // Rawvideo frames are already BGRA in system memory — no hwdownload.
                if (needScale) sb.Append("-vf \"").Append(scaleFilter.TrimStart(',')).Append("\" ");
            }
            else
            {
                // ddagrab → D3D11 textures. AMF/NVENC/QSV can't consume D3D11
                // textures directly (SubmitInput error 18), so hwdownload is
                // required. CPU scale/pad is skipped when resolution is Native.
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
            if (rawInput)
                sb.Append("-vf \"").Append(needScale ? scaleFilter.TrimStart(',') + "," : "").Append("format=yuv420p\" ");
            else
                sb.Append("-vf \"hwdownload,format=bgra").Append(scaleFilter).Append(",format=yuv420p\" ");
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

    // -map 0 copies ALL streams (video + every audio track). Without it the concat
    // demuxer keeps only one audio stream per output, which would silently drop the
    // second track of a separate-mic-track recording. Harmless for single-track clips.
    public static string BuildConcat(string listFilePath, string outputPath)
        => $"-hide_banner -loglevel warning -f concat -safe 0 -i \"{listFilePath}\" -map 0 -c copy -movflags +faststart \"{outputPath}\"";

    /// <summary>
    /// Turns the LAST <paramref name="durationSec"/> seconds of a source video into a
    /// looping GIF. A two-pass palette (palettegen → paletteuse in one filter graph)
    /// keeps quality high at a sane size. -sseof seeks from the END of the source, so
    /// only the final seconds are used; if the source is shorter it is used whole.
    /// </summary>
    public static string BuildGif(string sourcePath, string outputPath, int durationSec, int fps, int width)
    {
        durationSec = Math.Clamp(durationSec, 1, 30);
        fps = Math.Clamp(fps, 5, 30);
        string scale = width > 0 ? $"scale={width}:-1:flags=lanczos," : "";
        string vf = $"fps={fps},{scale}split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=3";
        return $"-hide_banner -loglevel warning -sseof -{durationSec} -i \"{sourcePath}\" -t {durationSec} " +
               $"-vf \"{vf}\" -loop 0 -y \"{outputPath}\"";
    }

    // hwdownload,format=bgra is REQUIRED: ddagrab emits D3D11 GPU frames the PNG
    // encoder can't consume (ffmpeg exits with "Impossible to convert ... src: d3d11"),
    // same as every recording path. Without it the grab fails and falls back to a
    // black GDI capture for fullscreen games.
    public static string BuildScreenshot(string outputPath, int monitorIndex = 0)
        => $"-hide_banner -loglevel warning -f lavfi -i \"ddagrab=output_idx={monitorIndex}:framerate=1\" -vf \"hwdownload,format=bgra\" -frames:v 1 -y \"{outputPath}\"";

    public static string BuildThumbnail(string videoPath, string thumbnailPath, int width = 320)
        => $"-hide_banner -loglevel error -ss 1 -i \"{videoPath}\" -frames:v 1 -vf \"scale={width}:-1\" -y \"{thumbnailPath}\"";

    /// <summary>
    /// Effective video bitrate. For fixed resolutions it uses the quality-preset
    /// table; for Native it scales the WQHD base by the target monitor's actual
    /// pixel count so an ultrawide (e.g. 3440×1440) isn't under-allocated.
    /// </summary>
    private static int EffectiveBitrate(AppSettings settings, int monitorIndex)
    {
        if (settings.Video.Quality == QualityPreset.Custom)
            return settings.Video.Bitrate;
        if (settings.Video.Resolution != ResolutionPreset.Native)
            return QualityPresets.ComputeBitrate(settings.Video.Quality, settings.Video.Resolution);

        long pixels = MonitorPixels(monitorIndex);
        const long wqhdPixels = 2560L * 1440;
        int wqhdBitrate = QualityPresets.ComputeBitrate(settings.Video.Quality, ResolutionPreset.WQHD);
        double factor = Math.Clamp((double)pixels / wqhdPixels, 0.6, 2.2);
        return (int)(wqhdBitrate * factor);
    }

    private static long MonitorPixels(int monitorIndex)
    {
        try
        {
            var screens = Screen.AllScreens;
            if (monitorIndex >= 0 && monitorIndex < screens.Length)
                return (long)screens[monitorIndex].Bounds.Width * screens[monitorIndex].Bounds.Height;
        }
        catch { }
        return 2560L * 1440;
    }

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
