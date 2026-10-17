namespace WKI_Clipper.Services;

/// <summary>The H.264 encoder a Discord compress runs on, best first.</summary>
public enum CompressEncoder
{
    /// <summary>AMD AMF (h264_amf).</summary>
    Amf,
    /// <summary>NVIDIA NVENC (h264_nvenc). Settings are not calibrated on real hardware.</summary>
    Nvenc,
    /// <summary>Intel Quick Sync (h264_qsv). Settings are not calibrated on real hardware.</summary>
    Qsv,
    /// <summary>libx264, two passes. Slow, but the only encoder that hits a size target to the percent.</summary>
    X264
}

/// <summary>Input of <see cref="FFmpegCommandBuilder.BuildCompress"/>.</summary>
/// <param name="OutputPath">Where ffmpeg writes (the runner's ".part" path). Ignored by software pass 1, which writes nowhere.</param>
/// <param name="Width">Scale target width; the height follows (<c>scale=W:-2</c>).</param>
/// <param name="AudioKbps">0 = no audio in the result.</param>
/// <param name="AudioTrackCount">Audio tracks in the source: 0 none, 1 one, 2 or more are mixed into one.</param>
/// <param name="HwDecode">Decode on the GPU (d3d11va). Off after it failed once.</param>
/// <param name="Pass">0 = single pass, 1 / 2 = libx264 2-pass.</param>
/// <param name="PassLogFile">Stats file prefix of the 2-pass encode.</param>
/// <param name="Threads">Encoder thread limit; only the software encoder has threads worth limiting.</param>
public sealed record CompressCommand(
    string InputPath,
    string OutputPath,
    CompressEncoder Encoder,
    int Width,
    int Fps,
    long VideoBps,
    int AudioKbps,
    int AudioTrackCount,
    bool HwDecode = false,
    int Pass = 0,
    string? PassLogFile = null,
    int? Threads = null);
