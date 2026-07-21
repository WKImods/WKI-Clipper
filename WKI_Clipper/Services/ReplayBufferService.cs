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
///
/// Lifecycle is serialized through <see cref="_lifecycleLock"/> so overlapping
/// Start/Stop/Restart requests (settings sliders, game-detection events, the
/// watchdog, hotkeys) can never spawn two ffmpeg processes writing the same
/// segment files. Settings-driven restarts go through <see cref="RequestRestart"/>
/// which coalesces bursts into a single restart.
///
/// Restarts do NOT wipe the buffer directory: each ffmpeg run writes a fresh
/// segment "generation" (seg_{gen}_NN.mp4) and old generations survive so a clip
/// saved right after a restart still contains the seconds recorded before it.
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

    // Serializes every lifecycle transition (Start/Stop/Restart).
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    // Increments on each warm restart so a new ffmpeg run writes new filenames
    // instead of clearing/overwriting the previous run's segments.
    private int _generation = -1;
    // Trailing-debounce for coalescing restart bursts.
    private readonly object _debounceLock = new();
    private CancellationTokenSource? _restartDebounceCts;
    // Reentrancy guard for SaveLastAsync (0 = idle, 1 = saving).
    private int _saving;

    public bool IsRunning => _ffmpeg?.IsRunning ?? false;
    public event EventHandler<string>? ReplaySaved;          // final clip path
    public event EventHandler<string>? BufferError;
    public event EventHandler<string>? BufferInfo;           // non-error notices (save busy, restarting)
    public event EventHandler<bool>? BufferStateChanged;     // true=running

    public ReplayBufferService(SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>Cold start: clears the buffer directory and starts a fresh generation.</summary>
    public void Start() => _ = StartInternalAsync(clearHistory: true);

    private async Task StartInternalAsync(bool clearHistory)
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsRunning) return;
            if (!_settings.Current.ReplayBuffer.Enabled) return;
            StartCore(clearHistory);
        }
        finally { _lifecycleLock.Release(); }
    }

    // Assumes _lifecycleLock is held.
    private void StartCore(bool clearHistory)
    {
        _bufferDir = SettingsService.ExpandPath(_settings.Current.Output.BufferFolder);
        Directory.CreateDirectory(_bufferDir);

        if (clearHistory)
        {
            ClearBufferDir(_bufferDir);
            _generation = 0;
        }
        else
        {
            _generation++;
            // Keep the buffer folder from growing without bound across restarts.
            PruneOldSegments();
        }

        _segmentPattern = Path.Combine(_bufferDir, $"seg_{_generation}_%02d.mp4");
        int segSec = Math.Max(1, _settings.Current.ReplayBuffer.SegmentDurationSeconds);
        int durSec = Math.Max(segSec, _settings.Current.ReplayBuffer.DurationSeconds);
        // Wrap = how many segments this run holds. ceil(dur/seg) + 1 for tail tolerance.
        int wrap = (int)Math.Ceiling((double)durSec / segSec) + 1;

        // Audio pipe first (named pipe must exist before ffmpeg opens it).
        // If audio init fails, fall back to video-only.
        int? gamePid = null;
        Logger.Info($"ReplayBuffer.Start: gen={_generation}, cold={clearHistory}, SystemCaptureMode={_settings.Current.Audio.SystemCaptureMode}, GameProcessName='{_settings.Current.Audio.GameProcessName ?? "(null)"}', WatcherPid={App.Host?.GameWatcher?.CurrentPid?.ToString() ?? "null"}");
        if (_settings.Current.Audio.SystemCaptureMode == AudioCaptureMode.GameOnly)
        {
            gamePid = App.Host?.GameWatcher?.CurrentPid;
            if (gamePid == null && !string.IsNullOrEmpty(_settings.Current.Audio.GameProcessName))
            {
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
        _ffmpeg.StdErrLine += (_, line) =>
        {
            if (!string.IsNullOrWhiteSpace(line)) Logger.Info("[buffer-ffmpeg] " + line);
        };
        // Capture LOCAL references so this handler only reacts to ITS OWN
        // ffmpeg/audio instances (an old ffmpeg dying after a restart must not
        // dispose the brand-new audio pipe or fire a false error).
        var ownFfmpeg = _ffmpeg;
        var ownAudio = _audio;
        _ffmpeg.Exited += (_, code) =>
        {
            if (_ffmpeg != ownFfmpeg) return;

            BufferStateChanged?.Invoke(this, false);
            if (_audio == ownAudio)
            {
                ownAudio?.Dispose();
                _audio = null;
            }

            // Any exit we did NOT request is unexpected — report it, regardless
            // of the exit code (0/255 self-exit means the pipeline died). Only a
            // StopRequested exit (our own graceful/forced stop) is silent.
            if (!ownFfmpeg.StopRequested)
                BufferError?.Invoke(this, $"Buffer unerwartet beendet (ffmpeg code {code}) — schau ins Log.");
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
        CancelDebounce();
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally { _lifecycleLock.Release(); }
    }

    // Assumes _lifecycleLock is held. Stops ffmpeg + audio; does NOT clear segments.
    private async Task StopCoreAsync()
    {
        _watchdogCts?.Cancel();
        if (_ffmpeg is not null)
            await _ffmpeg.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        _ffmpeg?.Dispose();
        _ffmpeg = null;
        _audio?.Dispose();
        _audio = null;
        BufferStateChanged?.Invoke(this, false);
    }

    public async Task ToggleAsync()
    {
        if (IsRunning) await StopAsync().ConfigureAwait(false);
        else await StartInternalAsync(clearHistory: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Coalesces settings-driven restart requests: many rapid calls (sliders,
    /// codec-list refresh, game events) collapse into a single restart ~300 ms
    /// after the last request, instead of a restart storm.
    /// </summary>
    public void RequestRestart()
    {
        lock (_debounceLock)
        {
            _restartDebounceCts?.Cancel();
            _restartDebounceCts?.Dispose();
            var cts = new CancellationTokenSource();
            _restartDebounceCts = cts;
            var token = cts.Token;
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(300, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (token.IsCancellationRequested) return;
                await RestartIfRunningAsync().ConfigureAwait(false);
            });
        }
    }

    private void CancelDebounce()
    {
        lock (_debounceLock)
        {
            _restartDebounceCts?.Cancel();
            _restartDebounceCts?.Dispose();
            _restartDebounceCts = null;
        }
    }

    /// <summary>
    /// Warm restart: stops and restarts ffmpeg to pick up new settings WITHOUT
    /// clearing the recorded segments (a new generation is written, old segments
    /// survive so a clip saved right after still has its history).
    /// </summary>
    public async Task RestartIfRunningAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsRunning) return;
            Logger.Info("ReplayBuffer: restart triggered by settings change");
            await StopCoreAsync().ConfigureAwait(false);
            StartCore(clearHistory: false);
        }
        finally { _lifecycleLock.Release(); }
    }

    /// <summary>
    /// Approximate seconds currently available to save (complete segments × seg
    /// length, capped at the configured buffer length). For honest UI display.
    /// </summary>
    public int AvailableSeconds()
    {
        if (!IsRunning || string.IsNullOrEmpty(_bufferDir)) return 0;
        int segSec = Math.Max(1, _settings.Current.ReplayBuffer.SegmentDurationSeconds);
        int target = _settings.Current.ReplayBuffer.DurationSeconds;
        int complete;
        try
        {
            complete = Directory.EnumerateFiles(_bufferDir, "seg_*.mp4")
                .Select(p => new FileInfo(p))
                .Count(fi => fi.Length > 1024);
        }
        catch { return 0; }
        // Exclude the one segment currently being written.
        complete = Math.Max(0, complete - 1);
        return Math.Min(target, complete * segSec);
    }

    /// <summary>
    /// Save the most recent N seconds into a single MP4. Concat demuxer + stream
    /// copy — no re-encoding, instant. Reentrancy-guarded and timeout-bounded.
    /// </summary>
    public async Task<string?> SaveLastAsync()
    {
        // Reentrancy guard: a second F9 while a save is in flight must not kill
        // the running concat (which would corrupt the first clip).
        if (Interlocked.CompareExchange(ref _saving, 1, 0) != 0)
        {
            BufferInfo?.Invoke(this, "Speichern läuft bereits — kurz warten.");
            return null;
        }
        try
        {
            if (!IsRunning || string.IsNullOrEmpty(_bufferDir))
            {
                BufferInfo?.Invoke(this, "Buffer läuft gerade nicht (evtl. Neustart) — gleich nochmal probieren.");
                return null;
            }

            int targetSec = _settings.Current.ReplayBuffer.DurationSeconds;
            int segSec = Math.Max(1, _settings.Current.ReplayBuffer.SegmentDurationSeconds);
            int neededSegments = (int)Math.Ceiling((double)targetSec / segSec);

            // Sorted oldest→newest by mtime. Drop the newest file (the segment
            // ffmpeg is currently writing — it has no moov atom yet and would
            // break the concat). Take the last N complete segments across
            // generations. Guard by size so a just-created tiny file is skipped.
            var segments = Directory.EnumerateFiles(_bufferDir, "seg_*.mp4")
                .Select(p => new FileInfo(p))
                .Where(fi => fi.Length > 1024)
                .OrderBy(fi => fi.LastWriteTimeUtc)
                .ToList();

            // The last (newest) file is the one being written — exclude it.
            if (segments.Count > 1)
                segments.RemoveAt(segments.Count - 1);

            if (segments.Count == 0)
            {
                BufferInfo?.Invoke(this,
                    "Buffer wurde gerade neu gestartet — noch nicht genug Material. Gleich nochmal.");
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
            ).ConfigureAwait(false);

            var args = FFmpegCommandBuilder.BuildConcat(listPath, outputPath);

            _concatFfmpeg?.Dispose();
            _concatFfmpeg = new FFmpegService();
            var tcs = new TaskCompletionSource<int>();
            _concatFfmpeg.Exited += (_, code) => tcs.TrySetResult(code);
            _concatFfmpeg.Start(args);

            // Bound the wait so a hung concat can't block every future save.
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
            try { File.Delete(listPath); } catch { }

            if (completed != tcs.Task)
            {
                try { _concatFfmpeg?.Dispose(); } catch { }
                try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
                BufferError?.Invoke(this, "Clip-Erstellung hat zu lange gedauert und wurde abgebrochen.");
                return null;
            }

            int exitCode = await tcs.Task.ConfigureAwait(false);
            if (exitCode == 0 && File.Exists(outputPath))
            {
                ReplaySaved?.Invoke(this, outputPath);
                return outputPath;
            }
            else
            {
                try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
                BufferError?.Invoke(this, $"Clip-Erstellung fehlgeschlagen (concat code {exitCode}).");
                return null;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _saving, 0);
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
                    await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                    PruneOldSegments();
                    long total = 0;
                    foreach (var f in Directory.EnumerateFiles(_bufferDir, "seg_*.mp4"))
                    {
                        try { total += new FileInfo(f).Length; } catch { }
                    }
                    // Over 2 GiB even after pruning means something is wrong — restart.
                    if (total > 2L * 1024 * 1024 * 1024)
                    {
                        BufferError?.Invoke(this, "buffer exceeded 2 GiB — restarting");
                        await RestartIfRunningAsync().ConfigureAwait(false);
                        return;
                    }
                }
                catch (OperationCanceledException) { return; }
                catch { /* ignore */ }
            }
        }, token);
    }

    /// <summary>
    /// Keeps the newest segments (enough to cover the buffer window plus the
    /// currently-filling generation) and deletes older ones. This is what bounds
    /// disk usage now that restarts no longer wipe the directory.
    /// </summary>
    private void PruneOldSegments()
    {
        if (string.IsNullOrEmpty(_bufferDir)) return;
        try
        {
            int segSec = Math.Max(1, _settings.Current.ReplayBuffer.SegmentDurationSeconds);
            int durSec = Math.Max(segSec, _settings.Current.ReplayBuffer.DurationSeconds);
            int wrap = (int)Math.Ceiling((double)durSec / segSec) + 1;
            // Keep the window we might save plus a full extra generation of slack.
            int keep = (int)Math.Ceiling((double)durSec / segSec) + wrap + 2;

            var files = Directory.EnumerateFiles(_bufferDir, "seg_*.mp4")
                .Select(p => new FileInfo(p))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .ToList();
            foreach (var fi in files.Skip(keep))
            {
                try { fi.Delete(); } catch { }
            }
        }
        catch { }
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
        CancelDebounce();
        _watchdogCts?.Cancel();
        _ffmpeg?.Dispose();
        _concatFfmpeg?.Dispose();
    }
}
