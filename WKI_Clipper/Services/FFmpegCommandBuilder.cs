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
    /// True while something on screen must stay out of the footage via
    /// WDA_EXCLUDEFROMCAPTURE — the crosshair, or a capture-excluded widget (WhatsApp).
    /// The capture path must then honour display affinity, which AMD's vsrc_amf does not.
    ///
    /// Deliberately independent of whether the widget is open: switching the pipeline
    /// only when it appears would put its first seconds into the buffer before the
    /// restarted capture takes over. Private beats the cheaper capture path.
    /// </summary>
    public static bool MustHonorCaptureExclusion(AppSettings settings)
        => settings.Crosshair.Enabled
        || settings.Widgets.Widgets.Exists(w => w.ExcludeFromCapture)
        || (settings.PerfOverlay.Enabled && settings.PerfOverlay.HideFromCapture);

    /// <summary>True when the settings could select AMD's own capture at all (opt-in + AMF encoder).</summary>
    public static bool AmfCaptureConfigured(AppSettings settings)
        => settings.Video.UseAmfCapture && settings.Video.Codec.Contains("amf");

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
    /// <param name="keyframes">
    /// Keyframe placement for the replay buffer (<paramref name="segmentOutput"/> only; manual recordings never
    /// get keyframe arguments). The default adds nothing, so the command line stays what it always was.
    /// </param>
    public static string Build(AppSettings settings, string outputPath, bool segmentOutput,
        int segmentDurationSec = 5, int segmentWrap = 12, IReadOnlyList<string>? audioPipeArgs = null,
        int monitorIndex = 0, string? videoInputArgs = null,
        KeyframeMode keyframes = KeyframeMode.EncoderDefault, bool mpegTsPipe = false)
    {
        var sb = new StringBuilder();
        sb.Append("-hide_banner -loglevel warning -nostats ");
        // Low-latency demuxer/muxer flags — kill ffmpeg's internal buffering so
        // PCM bytes from our pipe reach the encoder immediately instead of
        // sitting in a 500 ms-1 s queue (which manifests as audio lag in clips).
        sb.Append("-fflags +nobuffer+flush_packets -flags low_delay ");

        bool rawInput = !string.IsNullOrWhiteSpace(videoInputArgs);
        var (targetW, targetH) = GetResolution(settings.Video.Resolution);
        bool needScale = targetW > 0 && targetH > 0;
        // Keyframe arguments only ever apply to the segmented buffer; "" for the encoder default.
        string keyframeArgs = segmentOutput
            ? KeyframePolicy.EncoderArgs(settings.Video.Codec, settings.Video.Framerate, segmentDurationSec, keyframes)
            : "";
        if (keyframeArgs.Length > 0) keyframeArgs += " ";

        // On AMD, AMF can capture the monitor itself and hand its own surfaces straight to
        // the encoder, so frames never leave the GPU. The ddagrab path has to copy every
        // frame down to system memory (hwdownload) and the encoder uploads it back: at
        // 3440x1440x4 bytes and 60 fps that is ~1.2 GB/s each way — bandwidth the game
        // needs too. Measured on this machine over 10 s of capture: 5.24 s of CPU on the
        // old path, 0.84 s on this one.
        // Restricted to full-monitor capture without downscale: scaling would need vpp_amf
        // (and cannot letterbox), and window capture comes through the rawvideo pipe anyway.
        //
        // Also excluded while the crosshair overlay or a capture-excluded widget (WhatsApp)
        // is in use: vsrc_amf IGNORES WDA_EXCLUDEFROMCAPTURE, so AMF's capture records the
        // aiming overlay into every clip, while ddagrab honours it. Verified side by side on
        // the same screen at the same moment - AMF frame had the crosshair, ddagrab frame
        // did not. Correct (and private) footage beats the cheaper capture path.
        //
        // On top of that the whole path is opt-in (Video.UseAmfCapture, default off): it is
        // driver-level, and a system-wide stutter that survived closing the app pointed at
        // it. ddagrab is the safe default; the speed-up is not worth an unexplained machine.
        bool amfNativeCapture = UsesAmfNativeCapture(settings, rawInput);

        if (rawInput)
        {
            // WGC window frames, already BGRA in system memory.
            sb.Append(videoInputArgs).Append(' ');
        }
        else if (amfNativeCapture)
        {
            sb.Append("-init_hw_device amf=amf0 -filter_hw_device amf0 ");
            sb.Append("-f lavfi -i \"vsrc_amf=monitor_index=").Append(monitorIndex)
              .Append(":framerate=").Append(settings.Video.Framerate).Append("\" ");
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
        string scaleFilter = needScale
            ? $",scale={targetW}:{targetH}:force_original_aspect_ratio=decrease," +
              $"pad={targetW}:{targetH}:(ow-iw)/2:(oh-ih)/2:black,setsar=1"
            : "";

        if (isHwAccel)
        {
            if (amfNativeCapture)
            {
                // vsrc_amf already produces AMF surfaces the encoder takes as they are.
                // Any filter here would drag the frame back through system memory and
                // undo the whole point, so the chain stays empty.
            }
            else if (rawInput)
            {
                // Rawvideo frames are already BGRA in system memory — no hwdownload.
                if (needScale) sb.Append("-vf \"").Append(scaleFilter.TrimStart(',')).Append("\" ");
            }
            else
            {
                // ddagrab → D3D11 textures. AMF/NVENC/QSV can't consume D3D11
                // textures directly (SubmitInput error 18 — re-confirmed on ffmpeg 8.1,
                // and hwmap to an AMF device fails too), so hwdownload is required.
                // CPU scale/pad is skipped when resolution is Native.
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
            sb.Append(keyframeArgs);
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
            sb.Append(keyframeArgs);
            sb.Append("-pix_fmt yuv420p ");
        }

        if (audioInputs > 0)
        {
            sb.Append("-c:a aac -b:a 192k ");
        }

        if (segmentOutput && mpegTsPipe)
        {
            // RAM buffer: one continuous MPEG-TS on stdout, split into segments in memory (MpegTsSegmenter).
            sb.Append(MpegTsPipeMuxerArgs);
        }
        else if (segmentOutput)
        {
            sb.Append(SegmentMuxerArgs(segmentDurationSec, segmentWrap,
                KeyframePolicy.SegmentTimeDelta(settings.Video.Framerate, keyframes)));
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
    // -f mp4 is explicit because the output is written as "<name>.mp4.part" (renamed on success):
    // ffmpeg would otherwise guess the container from the ".part" extension and fail.
    //
    // faststart: moves the moov atom to the front with a second full rewrite of the file. Right for small clips
    // (Discord inline playback), wasteful for a multi-GB clip - see ClipSavePolicy.UseFaststart.
    // chaptersFile: an ffmetadata file (ClipStitcher.ChapterMetadata) read as a second input; -map_chapters 1 takes
    // the chapters from it. The concat demuxer has no chapter option of its own. The mp4 muxer then writes both the
    // Nero chpl atom and a QuickTime text track, so downstream jobs must map 0:v/0:a (or -dn), never a blind -map 0.
    //
    // audioVideoOnly: for MPEG-TS inputs (RAM buffer) only video and audio are taken (-map 0:v -map 0:a?): a TS can carry
    // other streams, and the mp4 muxer must never get a blind -map 0 of it. Every audio track is still kept.
    public static string BuildConcat(string listFilePath, string outputPath, bool faststart = true, string? chaptersFile = null,
        bool audioVideoOnly = false)
    {
        var sb = new StringBuilder("-hide_banner -loglevel warning -f concat -safe 0 -i \"");
        sb.Append(listFilePath).Append("\" ");
        bool chapters = !string.IsNullOrWhiteSpace(chaptersFile);
        if (chapters) sb.Append("-f ffmetadata -i \"").Append(chaptersFile).Append("\" ");
        sb.Append(audioVideoOnly ? "-map 0:v -map 0:a? " : "-map 0 ");
        if (chapters) sb.Append("-map_chapters 1 ");
        sb.Append("-c copy ");
        if (faststart) sb.Append("-movflags +faststart ");
        sb.Append("-f mp4 \"").Append(outputPath).Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// The segment-muxer part of the buffer command, trailing space included (the output path follows). The optional
    /// <c>-segment_time_delta</c> lets a keyframe slightly before the boundary (half a frame) still cut the segment.
    /// </summary>
    internal static string SegmentMuxerArgs(int segmentSeconds, int wrap, double? timeDelta)
    {
        var sb = new StringBuilder("-f segment ");
        sb.Append("-segment_time ").Append(segmentSeconds).Append(' ');
        sb.Append("-segment_wrap ").Append(wrap).Append(' ');
        if (timeDelta is { } d)
            sb.Append("-segment_time_delta ").Append(d.ToString("0.######", CultureInfo.InvariantCulture)).Append(' ');
        sb.Append("-reset_timestamps 1 ");
        sb.Append("-segment_format mp4 ");
        return sb.ToString();
    }

    /// <summary>
    /// The muxer part of the RAM buffer command. -pes_payload_size 0 writes every audio frame as its own PES instead of
    /// bundling ~100 ms: a RAM segment starts at a video key frame and the audio from before it is trimmed per PES, so the
    /// fine grain keeps that trim to one AAC frame. ffmpeg sets the random_access_indicator on every key frame and repeats
    /// PAT/PMT right before it - that is where the segments are cut.
    /// </summary>
    internal const string MpegTsPipeMuxerArgs = "-f mpegts -pes_payload_size 0 pipe:1";

    /// <summary>The highest effective video bitrate over all monitors (a stable estimate across Alt-Tabs in Auto mode).</summary>
    internal static int MaxEffectiveBitrate(AppSettings settings)
    {
        int best = EffectiveBitrate(settings, 0);
        try
        {
            int n = Screen.AllScreens.Length;
            for (int i = 1; i < n; i++) best = Math.Max(best, EffectiveBitrate(settings, i));
        }
        catch { }
        return best;
    }

    /// <summary>
    /// True when AMD's own screen capture (vsrc_amf) would be used: opted in, AMF encoder, full-monitor capture
    /// without downscale (a window comes through the rawvideo pipe), and nothing that must stay out of the footage.
    /// Part of the segment identity: this path and ddagrab/WGC give the encoder different input.
    /// </summary>
    internal static bool UsesAmfNativeCapture(AppSettings s, bool rawInput)
    {
        var (w, h) = GetResolution(s.Video.Resolution);
        bool needScale = w > 0 && h > 0;
        return AmfCaptureConfigured(s) && !rawInput && !needScale && !MustHonorCaptureExclusion(s);
    }

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
               $"-vf \"{vf}\" -f gif -loop 0 -y \"{outputPath}\"";   // -f gif: output is "<name>.gif.part"
    }

    /// <summary>
    /// Re-encodes a clip to fit a size limit (Discord). Only meant to run through
    /// <see cref="FFmpegJobRunner"/>, which owns the overwrite policy (so there is no -y here), the
    /// progress flags and the ".part" rename; the output must be the runner's part path, and the
    /// muxer is named because ffmpeg cannot infer it from ".part".
    ///
    /// The video is mapped explicitly (<c>0:v:0</c>) and so is the audio - never <c>-map 0</c>: long
    /// clips can carry data tracks (chapter markers) that a Discord upload must not contain.
    /// </summary>
    public static string BuildCompress(CompressCommand c)
    {
        if (c.Pass is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(c), "Pass must be 0, 1 or 2.");
        if (c.Pass != 0 && c.Encoder != CompressEncoder.X264)
            throw new ArgumentException("Only the software encoder has a file pass.", nameof(c));
        if (c.Pass != 0 && string.IsNullOrWhiteSpace(c.PassLogFile))
            throw new ArgumentException("A 2-pass encode needs a stats file.", nameof(c));
        if (c.Width <= 0) throw new ArgumentOutOfRangeException(nameof(c), "Width must be positive.");
        if (c.Fps <= 0) throw new ArgumentOutOfRangeException(nameof(c), "Fps must be positive.");
        if (c.VideoBps <= 0) throw new ArgumentOutOfRangeException(nameof(c), "VideoBps must be positive.");

        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("-hide_banner -loglevel warning ");
        if (c.HwDecode) sb.Append("-hwaccel d3d11va ");
        sb.Append("-i \"").Append(c.InputPath).Append("\" -map 0:v:0 ");
        sb.Append("-vf \"scale=").Append(c.Width.ToString(inv)).Append(":-2:flags=bicubic,fps=")
          .Append(c.Fps.ToString(inv)).Append(",format=yuv420p\" ");

        bool audio = c.Pass != 1 && c.AudioKbps > 0 && c.AudioTrackCount > 0;
        if (audio && c.AudioTrackCount >= 2)
            sb.Append("-filter_complex \"[0:a:0][0:a:1]amix=inputs=2:duration=longest:normalize=0[a]\" -map \"[a]\" ");
        else if (audio)
            sb.Append("-map 0:a:0? ");

        string v = c.VideoBps.ToString(inv);
        string buffer = (c.VideoBps * 2).ToString(inv);
        switch (c.Encoder)
        {
            case CompressEncoder.Amf:
                sb.Append("-c:v h264_amf -usage transcoding -quality quality -profile:v high -rc cbr -enforce_hrd 1 -b:v ")
                  .Append(v).Append(' ');
                break;
            case CompressEncoder.Nvenc:
                sb.Append("-c:v h264_nvenc -preset p5 -tune hq -rc cbr -b:v ").Append(v)
                  .Append(" -maxrate ").Append(v).Append(" -bufsize ").Append(buffer).Append(' ');
                break;
            case CompressEncoder.Qsv:
                sb.Append("-c:v h264_qsv -preset medium -b:v ").Append(v)
                  .Append(" -maxrate ").Append(v).Append(" -bufsize ").Append(buffer).Append(' ');
                break;
            default:
                sb.Append("-c:v libx264 -preset medium -b:v ").Append(v).Append(' ');
                if (c.Pass != 0) sb.Append("-pass ").Append(c.Pass.ToString(inv)).Append(" -passlogfile \"").Append(c.PassLogFile).Append("\" ");
                if (c.Threads is > 0) sb.Append("-threads ").Append(c.Threads.Value.ToString(inv)).Append(' ');
                break;
        }

        if (c.Pass == 1)
        {
            // Measuring pass: the stats file is the product, the video goes nowhere.
            sb.Append("-an -f null NUL");
            return sb.ToString();
        }

        if (audio)
            sb.Append("-c:a aac -b:a ").Append(c.AudioKbps.ToString(inv)).Append("k -ar 48000 -ac 2 ");
        else
            sb.Append("-an ");
        sb.Append("-movflags +faststart -f mp4 \"").Append(c.OutputPath).Append('"');
        return sb.ToString();
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
    internal static int EffectiveBitrate(AppSettings settings, int monitorIndex)
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
