using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.Native;

namespace WKI_Clipper.Services;

/// <summary>
/// Thin wrapper around an ffmpeg.exe process. Owns the process lifecycle and
/// streams stderr lines back to the caller. Stop sends Ctrl-C so ffmpeg writes
/// a clean MOOV atom before exiting.
/// </summary>
public sealed class FFmpegService : IDisposable
{
    public string FFmpegPath { get; }
    public string FFprobePath { get; }

    private Process? _process;
    private CancellationTokenSource? _readerCts;
    /// <summary>Set by StopAsync so the Exited handler can distinguish
    /// "user/owner asked us to stop" from "ffmpeg died unexpectedly".</summary>
    public bool StopRequested { get; private set; }

    public event EventHandler<string>? StdErrLine;
    public event EventHandler<int>? Exited;

    public bool IsRunning => _process is { HasExited: false };

    public FFmpegService(string? ffmpegPath = null)
    {
        FFmpegPath = ffmpegPath ?? ResolveFFmpegPath();
        FFprobePath = Path.Combine(Path.GetDirectoryName(FFmpegPath) ?? "", "ffprobe.exe");
    }

    private static string ResolveFFmpegPath()
    {
        // 1) bundled binary next to the .exe
        var baseDir = AppContext.BaseDirectory;
        var bundled = Path.Combine(baseDir, "Assets", "ffmpeg", "ffmpeg.exe");
        if (File.Exists(bundled)) return bundled;

        // 2) PATH lookup
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
        }

        // 3) default — caller will get a "not found" exception when actually starting
        return bundled;
    }

    public bool IsAvailable() => File.Exists(FFmpegPath);

    /// <summary>
    /// Returns true only if the encoder is BOTH compiled into ffmpeg AND can
    /// actually initialise on this machine — by trying to encode a single frame
    /// of solid black to /dev/null. This catches the gyan.dev case where
    /// `ffmpeg -encoders` lists everything (e.g. nvenc) even when the
    /// matching GPU isn't installed.
    /// </summary>
    public async Task<bool> HasEncoderAsync(string codecName, CancellationToken ct = default)
    {
        if (!IsAvailable()) return false;
        // 640x480 is the smallest frame size HEVC AMF accepts. Smaller sizes
        // (e.g. 320x240) make hevc_amf and some NVENC variants fail init even
        // when the GPU is fully capable. Explicit bitrate avoids "incorrect
        // parameters" init failures on AMF encoders.
        var args = "-hide_banner -loglevel error -f lavfi -i color=c=black:s=640x480:r=30 -frames:v 1 -c:v "
                 + codecName + " -b:v 1M -f null -";
        var psi = new ProcessStartInfo
        {
            FileName = FFmpegPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return false;
            // Drain pipes so the process doesn't block.
            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            // Hard 5-second cap per encoder probe.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            try { await p.WaitForExitAsync(timeoutCts.Token); }
            catch (OperationCanceledException) { try { p.Kill(); } catch { } return false; }
            await Task.WhenAll(stderrTask, stdoutTask);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// List DirectShow audio devices visible to ffmpeg.
    /// Used for the audio-device picker in settings.
    /// </summary>
    public async Task<List<string>> ListDShowAudioDevicesAsync(CancellationToken ct = default)
    {
        var devices = new List<string>();
        if (!IsAvailable()) return devices;

        var psi = new ProcessStartInfo
        {
            FileName = FFmpegPath,
            Arguments = "-hide_banner -list_devices true -f dshow -i dummy",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p is null) return devices;

        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);

        bool inAudioSection = false;
        foreach (var rawLine in stderr.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Contains("DirectShow audio devices", StringComparison.OrdinalIgnoreCase))
            {
                inAudioSection = true;
                continue;
            }
            if (line.Contains("DirectShow video devices", StringComparison.OrdinalIgnoreCase))
            {
                inAudioSection = false;
                continue;
            }
            if (!inAudioSection) continue;

            // Lines look like:  [dshow @ ...] "Microphone (Realtek...)"
            var firstQuote = line.IndexOf('"');
            var lastQuote = line.LastIndexOf('"');
            if (firstQuote >= 0 && lastQuote > firstQuote)
            {
                var name = line.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
                if (!string.IsNullOrWhiteSpace(name) && !devices.Contains(name))
                    devices.Add(name);
            }
        }
        return devices;
    }

    public void Start(string arguments, string? workingDir = null)
    {
        if (IsRunning) throw new InvalidOperationException("FFmpeg is already running.");
        if (!IsAvailable()) throw new FileNotFoundException("ffmpeg.exe not found", FFmpegPath);

        var psi = new ProcessStartInfo
        {
            FileName = FFmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDir ?? Path.GetDirectoryName(FFmpegPath) ?? Environment.CurrentDirectory
        };

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Exited += (_, _) =>
        {
            var code = SafeExitCode(_process);
            Exited?.Invoke(this, code);
        };

        if (!_process.Start())
            throw new InvalidOperationException("Failed to start ffmpeg.");

        _readerCts = new CancellationTokenSource();
        _ = Task.Run(() => PumpStdErr(_process, _readerCts.Token));
    }

    private async Task PumpStdErr(Process p, CancellationToken ct)
    {
        try
        {
            string? line;
            while (!ct.IsCancellationRequested && (line = await p.StandardError.ReadLineAsync(ct)) != null)
            {
                StdErrLine?.Invoke(this, line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* ignore stream closed */ }
    }

    /// <summary>
    /// Graceful stop: write "q\n" to stdin. FFmpeg interprets this as "quit"
    /// and writes a proper trailer. Falls back to Kill after timeout.
    /// </summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        if (!IsRunning || _process is null) return;

        StopRequested = true;
        try
        {
            await _process.StandardInput.WriteAsync("q\n");
            await _process.StandardInput.FlushAsync();
        }
        catch { /* stdin may already be closed */ }

        try
        {
            var exited = await Task.Run(() => _process.WaitForExit((int)timeout.TotalMilliseconds));
            if (!exited)
            {
                try { _process.Kill(entireProcessTree: false); } catch { }
            }
        }
        finally
        {
            _readerCts?.Cancel();
        }
    }

    private static int SafeExitCode(Process? p)
    {
        try { return p?.ExitCode ?? -1; } catch { return -1; }
    }

    public void Dispose()
    {
        try { _readerCts?.Cancel(); } catch { }
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: false);
        }
        catch { }
        _process?.Dispose();
        _process = null;
    }
}
