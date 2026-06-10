using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>
/// Runs ffmpeg in segment mode permanently while enabled. On SaveLast(),
/// concatenates the most recent N segments into a single MP4 in the clips folder.
/// </summary>
public sealed class ReplayBufferService : IDisposable
{
    private readonly SettingsService _settings;
    private FFmpegService? _ffmpeg;
    private FFmpegService? _concatFfmpeg;
    private AudioPipeService? _audio;
    private CancellationTokenSource? _watchdogCts;
    private string _bufferDir = "";
    private string _segmentPattern = "";

    public bool IsRunning => _ffmpeg?.IsRunning ?? false;
    public event EventHandler<string>? ReplaySaved;          // final clip path
    public event EventHandler<string>? BufferError;
    public event EventHandler<bool>? BufferStateChanged;     // true=running

    public ReplayBufferService(SettingsService settings)
    {
        _settings = settings;
    }

    public void Start()
    {
        if (IsRunning) return;
        if (!_settings.Current.ReplayBuffer.Enabled) return;

        _bufferDir = SettingsService.ExpandPath(_settings.Current.Output.BufferFolder);
        Directory.CreateDirectory(_bufferDir);
        ClearBufferDir(_bufferDir);

        _segmentPattern = Path.Combine(_bufferDir, "seg_%02d.mp4");
        int segSec = Math.Max(1, _settings.Current.ReplayBuffer.SegmentDurationSeconds);
        int durSec = Math.Max(segSec, _settings.Current.ReplayBuffer.DurationSeconds);
        // Wrap = how many segments we hold. We hold ceil(dur/seg) + 1 for tail tolerance.
        int wrap = (int)Math.Ceiling((double)durSec / segSec) + 1;

        // Audio pipe first (named pipe must exist before ffmpeg opens it).
        // If audio init fails, fall back to video-only — otherwise ffmpeg
        // would try to read from a dead pipe and the whole buffer dies.
        int? gamePid = null;
        Logger.Info($"ReplayBuffer.Start: SystemCaptureMode={_settings.Current.Audio.SystemCaptureMode}, GameProcessName='{_settings.Current.Audio.GameProcessName ?? "(null)"}', WatcherPid={App.Host?.GameWatcher?.CurrentPid?.ToString() ?? "null"}");
        if (_settings.Current.Audio.SystemCaptureMode == AudioCaptureMode.GameOnly)
        {
            gamePid = App.Host?.GameWatcher?.CurrentPid;
            if (gamePid == null && !string.IsNullOrEmpty(_settings.Current.Audio.GameProcessName))
            {
                // Watcher hasn't found it yet — try a direct lookup
                try
                {
                    var procs = System.Diagnostics.Process.GetProcessesByName(
                        _settings.Current.Audio.GameProcessName);
                    if (procs.Length > 0)
                    {
                        gamePid = procs[0].Id;
                        foreach (var p in procs) p.Dispose();
                    }
                }
                catch { }
            }
            if (gamePid != null)
                Logger.Info($"ReplayBuffer: GameOnly mode, target PID {gamePid}");
            else
                Logger.Info("ReplayBuffer: GameOnly mode but process not found, falling back to AllAudio");
        }
        _audio = new AudioPipeService(_settings.Current, gamePid);
        string? audioArgs = null;
        if (_audio.HasAnyAudio())
        {
            bool ok = _audio.Start();
            if (ok)
            {
                audioArgs = _audio.FFmpegInputArgs;
            }
            else
            {
                Logger.Warn("ReplayBuffer: audio pipe failed to start, continuing with video only. " + _audio.LastError);
                BufferError?.Invoke(this, "Audio init fehlgeschlagen — " + (_audio.LastError ?? "unbekannt"));
                _audio.Dispose();
                _audio = null;
            }
        }

        var args = FFmpegCommandBuilder.Build(_settings.Current, _segmentPattern,
            segmentOutput: true, segmentDurationSec: segSec, segmentWrap: wrap,
            audioPipeArgs: audioArgs);
        Logger.Info("[buffer-ffmpeg] CMD: " + args);

        _ffmpeg = new FFmpegService();
        if (!_ffmpeg.IsAvailable())
        {
            BufferError?.Invoke(this, "ffmpeg.exe not found");
            _audio?.Dispose();
            _audio = null;
            return;
        }
        // Pipe ffmpeg's stderr into our log so we can actually see what went
        // wrong when it dies (e.g. bad pipe format, codec init failure, etc.).
        _ffmpeg.StdErrLine += (_, line) =>
        {
            if (!string.IsNullOrWhiteSpace(line)) Logger.Info("[buffer-ffmpeg] " + line);
        };
        // Capture LOCAL references so this handler only reacts to ITS OWN
        // ffmpeg/audio instances. Without this, an old ffmpeg dying after a
        // restart would dispose the brand-new audio pipe (this._audio already
        // points at the new instance) and fire a false BufferError.
        var ownFfmpeg = _ffmpeg;
        var ownAudio = _audio;
        _ffmpeg.Exited += (_, code) =>
        {
            // Race-safe: ignore if we've already moved on to a new ffmpeg.
            if (_ffmpeg != ownFfmpeg) return;

            BufferStateChanged?.Invoke(this, false);
            if (_audio == ownAudio)
            {
                ownAudio?.Dispose();
                _audio = null;
            }

            // Only report as error if THIS process wasn't intentionally stopped.
            // StopAsync sets StopRequested=true; the ensuing exit code (often -1
            // from forced kill on timeout) is then expected, not a failure.
            if (!ownFfmpeg.StopRequested && code != 0 && code != 255)
                BufferError?.Invoke(this, $"buffer ffmpeg exited with code {code}");
        };

        try
        {
            _ffmpeg.Start(args);
            BufferStateChanged?.Invoke(this, true);
            StartWatchdog();
        }
        catch (Exception ex)
        {
            BufferError?.Invoke(this, ex.Message);
        }
    }

    public async Task StopAsync()
    {
        _watchdogCts?.Cancel();
        if (_ffmpeg is not null)
            await _ffmpeg.StopAsync(TimeSpan.FromSeconds(5));
        _ffmpeg?.Dispose();
        _ffmpeg = null;
        _audio?.Dispose();
        _audio = null;
        BufferStateChanged?.Invoke(this, false);
    }

    public async Task ToggleAsync()
    {
        if (IsRunning) await StopAsync();
        else Start();
    }

    /// <summary>
    /// Stops and restarts the buffer if it is currently running. Used when
    /// settings change so the running ffmpeg/NAudio pipeline picks up the new
    /// audio device / mic toggle / resolution / codec / etc. without the user
    /// having to toggle it manually.
    /// </summary>
    public async Task RestartIfRunningAsync()
    {
        if (!IsRunning) return;
        Logger.Info("ReplayBuffer: restart triggered by settings change");
        await StopAsync();
        Start();
    }

    /// <summary>
    /// Save the most recent N seconds (taken from settings) into a single MP4.
    /// Uses concat demuxer + stream copy — no re-encoding, instant.
    /// </summary>
    public async Task<string?> SaveLastAsync()
    {
        if (!IsRunning || string.IsNullOrEmpty(_bufferDir))
            return null;

        int targetSec = _settings.Current.ReplayBuffer.DurationSeconds;
        int segSec = Math.Max(1, _settings.Current.ReplayBuffer.SegmentDurationSeconds);
        int neededSegments = (int)Math.Ceiling((double)targetSec / segSec);

        // Pick segments sorted oldest→newest by mtime, take the last N that exist
        var segments = Directory.EnumerateFiles(_bufferDir, "seg_*.mp4")
            .Select(p => new FileInfo(p))
            .Where(fi => fi.Length > 1024) // skip the segment currently being written if it's tiny
            .OrderBy(fi => fi.LastWriteTimeUtc)
            .ToList();

        if (segments.Count == 0)
        {
            BufferError?.Invoke(this,
                "Keine Buffer-Segmente vorhanden — ffmpeg läuft wahrscheinlich nicht. Schau ins Log unter %LOCALAPPDATA%\\WKI_Clipper\\wki_clipper.log");
            return null;
        }

        var pick = segments.TakeLast(neededSegments).ToList();

        var clipsDir = SettingsService.ExpandPath(_settings.Current.Output.ClipsFolder);
        Directory.CreateDirectory(clipsDir);
        var outputPath = Path.Combine(clipsDir, $"Clip_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");

        var listPath = Path.Combine(_bufferDir, "_concat_list.txt");
        await File.WriteAllLinesAsync(
            listPath,
            pick.Select(fi => $"file '{fi.FullName.Replace("'", @"'\''")}'")
        );

        var args = FFmpegCommandBuilder.BuildConcat(listPath, outputPath);

        _concatFfmpeg?.Dispose();
        _concatFfmpeg = new FFmpegService();
        var tcs = new TaskCompletionSource<int>();
        _concatFfmpeg.Exited += (_, code) => tcs.TrySetResult(code);
        _concatFfmpeg.Start(args);
        var exitCode = await tcs.Task;
        try { File.Delete(listPath); } catch { }

        if (exitCode == 0 && File.Exists(outputPath))
        {
            ReplaySaved?.Invoke(this, outputPath);
            return outputPath;
        }
        else
        {
            BufferError?.Invoke(this, $"concat ffmpeg failed (code {exitCode})");
            return null;
        }
    }

    private void StartWatchdog()
    {
        _watchdogCts = new CancellationTokenSource();
        var token = _watchdogCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                    long total = 0;
                    foreach (var f in Directory.EnumerateFiles(_bufferDir, "seg_*.mp4"))
                    {
                        try { total += new FileInfo(f).Length; } catch { }
                    }
                    // Anything over 2 GiB means segment wrap-around isn't working.
                    if (total > 2L * 1024 * 1024 * 1024)
                    {
                        BufferError?.Invoke(this, "buffer exceeded 2 GiB — restarting");
                        await StopAsync();
                        Start();
                        return;
                    }
                }
                catch (OperationCanceledException) { return; }
                catch { /* ignore */ }
            }
        }, token);
    }

    private static void ClearBufferDir(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "seg_*.mp4"))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        _watchdogCts?.Cancel();
        _ffmpeg?.Dispose();
        _concatFfmpeg?.Dispose();
    }
}
