using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.Native;

namespace WKI_Clipper.Services;

/// <summary>
/// The fallback preview of the clip editor: one still frame per request, grabbed with ffmpeg (140-650 ms on a 30-minute
/// file). Used when Media Foundation cannot play the clip (HEVC / AV1 without the Store extension). Own process, not
/// the job runner's queue — a still must not wait behind a cut. A new request cancels the previous one (latest wins).
/// </summary>
public sealed class FramePreviewService : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _ffmpegPath;
    private readonly Lazy<KillOnCloseJob?> _guard = new(KillOnCloseJob.TryCreate);
    private readonly object _gate = new();
    private CancellationTokenSource? _current;
    private volatile bool _disposed;

    public FramePreviewService(string ffmpegPath) => _ffmpegPath = ffmpegPath;

    /// <summary>BMP bytes of the frame at <paramref name="fileSeconds"/> (ffmpeg's time axis), or null.</summary>
    public async Task<byte[]?> GrabAsync(string source, double fileSeconds, int maxWidth, CancellationToken ct)
    {
        if (_disposed || !File.Exists(_ffmpegPath) || !File.Exists(source)) return null;

        CancellationTokenSource mine;
        lock (_gate)
        {
            _current?.Cancel();
            mine = _current = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }
        mine.CancelAfter(Timeout);

        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = ClipCutRules.BuildFrameGrabArgs(source, fileSeconds, maxWidth),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            process = Process.Start(psi);
            if (process is null) return null;
            Guard(process);

            using var registration = mine.Token.Register(() => Kill(process));
            var stderr = process.StandardError.ReadToEndAsync();   // drained so ffmpeg never blocks on a full pipe
            var image = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(image, mine.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(mine.Token).ConfigureAwait(false);
            await stderr.ConfigureAwait(false);

            return process.ExitCode == 0 && image.Length > 54 ? image.ToArray() : null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Logger.Warn("Frame grab failed: " + ex.Message);
            return null;
        }
        finally
        {
            if (process is not null) { Kill(process); process.Dispose(); }
            lock (_gate) { if (ReferenceEquals(_current, mine)) _current = null; }
            mine.Dispose();
        }
    }

    private void Guard(Process process)
    {
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        try { _guard.Value?.TryAssign(process); } catch { }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) { _current?.Cancel(); }
        if (_guard.IsValueCreated) _guard.Value?.Dispose();
    }
}
