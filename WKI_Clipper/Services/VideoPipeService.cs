using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace WKI_Clipper.Services;

/// <summary>
/// Pumps the latest <see cref="WgcWindowCapture"/> frame to ffmpeg through a
/// named pipe as rawvideo BGRA at a CONSTANT frame rate. WGC delivers frames
/// only when the content changes; ffmpeg's rawvideo demuxer assumes CFR — so a
/// deadline-paced writer duplicates the latest frame when nothing new arrived.
/// Deadlines are absolute (frameIdx / fps against one stopwatch), so timing
/// jitter never accumulates into A/V drift.
/// </summary>
public sealed class VideoPipeService : IDisposable
{
    private readonly WgcWindowCapture _capture;
    private readonly int _fps;
    private readonly string _pipeName;
    private NamedPipeServerStream? _server;
    private CancellationTokenSource? _cts;
    private Task? _pumpTask;

    public bool IsRunning { get; private set; }

    public string FFmpegInputArgs =>
        $"-f rawvideo -pixel_format bgra -video_size {_capture.Width}x{_capture.Height} " +
        $"-framerate {_fps} -i \"\\\\.\\pipe\\{_pipeName}\"";

    public VideoPipeService(WgcWindowCapture capture, int fps)
    {
        _capture = capture;
        _fps = Math.Max(1, fps);
        _pipeName = "WKI_Clipper_Video_" + Guid.NewGuid().ToString("N")[..8];
    }

    public void Start()
    {
        if (IsRunning) return;
        // 4 MiB pipe buffer ≈ a fraction of one 3440×1440 frame — enough to
        // decouple write/read timing without buffering seconds of video.
        _server = new NamedPipeServerStream(
            _pipeName, PipeDirection.Out,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 1 << 22, outBufferSize: 1 << 22);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsRunning = true;
        _pumpTask = Task.Run(() => PumpAsync(ct), ct);
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await _server!.WaitForConnectionAsync(ct).ConfigureAwait(false);
            Logger.Info($"Video pipe: ffmpeg connected ({_capture.Width}×{_capture.Height}@{_fps}, {_capture.FrameBytes / 1024} KiB/frame)");

            var frame = new byte[_capture.FrameBytes]; // starts black until the first WGC frame
            var sw = Stopwatch.StartNew();
            long frameIdx = 0;

            while (!ct.IsCancellationRequested && _server.IsConnected)
            {
                double targetMs = frameIdx * 1000.0 / _fps;
                double waitMs = targetMs - sw.Elapsed.TotalMilliseconds;
                if (waitMs > 2)
                    await Task.Delay((int)(waitMs - 1), ct).ConfigureAwait(false);

                _capture.TryCopyLatest(frame); // false → previous content is duplicated
                await _server.WriteAsync(frame.AsMemory(), ct).ConfigureAwait(false);
                frameIdx++;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Pipe broken = ffmpeg went away (stop/restart) — routine during teardown.
            if (!(_cts?.IsCancellationRequested ?? true))
                Logger.Warn("Video pipe pump ended: " + ex.Message);
        }
    }

    public void Dispose()
    {
        IsRunning = false;
        try { _cts?.Cancel(); } catch { }
        try { _server?.Dispose(); } catch { }
        _server = null;
        _cts?.Dispose();
        _cts = null;
    }
}
