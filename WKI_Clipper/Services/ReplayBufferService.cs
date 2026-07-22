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
    private WgcWindowCapture? _wgc;
    private VideoPipeService? _videoPipe;
    private CancellationTokenSource? _watchdogCts;
    private string _bufferDir = "";
    private string _segmentPattern = "";

    // Serializes every lifecycle transition (Start/Stop/Restart).
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    // Increments on each warm restart so a new ffmpeg run writes new filenames
    // instead of clearing/overwriting the previous run's segments.
    private int _generation = -1;
    // Set on cold start; SaveLast/AvailableSeconds only consider segments from
    // this session, so stragglers from a previous run (e.g. locked files a cold
    // ClearBufferDir couldn't delete) can never be spliced into a clip.
    private DateTime _sessionStartUtc = DateTime.MinValue;
    // Video identity per generation (window+size or monitor+size). F9 only ever
    // concatenates segments of the CURRENT identity — a freecam window switch can
    // never splice two windows/resolutions into one clip, and after a quick
    // A→B→A detour the earlier A-segments are still usable.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _genIdentity = new();
    private string? _currentIdentity;
    // Trailing-debounce for coalescing restart bursts.
    private readonly object _debounceLock = new();
    private CancellationTokenSource? _restartDebounceCts;
    // Reentrancy guard for SaveLastAsync (0 = idle, 1 = saving).
    private int _saving;

    public bool IsRunning => _ffmpeg?.IsRunning ?? false;
    /// <summary>The capture plan the running buffer resolved (for honest UI). Null when stopped.</summary>
    public CaptureTargetResolver.CapturePlan? CurrentPlan { get; private set; }
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

        // Resolve the single capture plan (video monitor + audio route) FIRST, so
        // we can tell whether the capture target changed across a warm restart.
        // Same resolver as manual recording + the status UI, so F9 clips exactly
        // what the UI advertises. Resolved once per generation → pinned; alt-tab
        // does not restart the buffer, so the clip stays on the game's monitor.
        var plan = CaptureTargetResolver.Resolve(_settings.Current.Capture, _settings.Current);
        var prevPlan = CurrentPlan;

        if (clearHistory)
        {
            ClearBufferDir(_bufferDir);
            _generation = 0;
            _sessionStartUtc = DateTime.UtcNow;
            _genIdentity.Clear();
        }
        else
        {
            _generation++;
            // If the target MONITOR changed (e.g. the game launched on another
            // monitor), the previous segments are a different display/resolution.
            // Start a fresh save-window so they can never be spliced into a clip
            // (which would give wrong footage or break the stream-copy concat).
            if (prevPlan is { } pp && pp.MonitorIndex != plan.MonitorIndex)
                _sessionStartUtc = DateTime.UtcNow;
            // Keep the buffer folder from growing without bound across restarts.
            PruneOldSegments();
        }

        CurrentPlan = plan;

        _segmentPattern = Path.Combine(_bufferDir, $"seg_{_generation}_%02d.mp4");
        int segSec = Math.Max(1, _settings.Current.ReplayBuffer.SegmentDurationSeconds);
        int durSec = Math.Max(segSec, _settings.Current.ReplayBuffer.DurationSeconds);
        // Wrap = how many segments this run holds. ceil(dur/seg) + 1 for tail tolerance.
        int wrap = (int)Math.Ceiling((double)durSec / segSec) + 1;
        Logger.Info($"ReplayBuffer.Start: gen={_generation}, cold={clearHistory}, video='{plan.VideoLabel}', audio='{plan.AudioLabel}' (monitorIdx={plan.MonitorIndex}, pid={plan.AudioPid?.ToString() ?? "null"})");

        // Audio pipe first (named pipe must exist before ffmpeg opens it).
        // If audio init fails, fall back to video-only.
        _audio = new AudioPipeService(_settings.Current, plan.SysMode, plan.AudioPid);
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

        // Occlusion-proof WGC window capture when the plan targets a window —
        // the clip stays on the game even when another window covers it. Any
        // failure falls back to monitor ddagrab (plan.MonitorIndex).
        string? videoArgs = null;
        if (plan.UseWgc)
        {
            try
            {
                _wgc = new WgcWindowCapture(plan.Hwnd);
                _videoPipe = new VideoPipeService(_wgc, _settings.Current.Video.Framerate);
                _videoPipe.Start();
                videoArgs = _videoPipe.FFmpegInputArgs;

                var ownWgcLocal = _wgc;
                _wgc.TargetInvalidated += reason =>
                {
                    if (_wgc != ownWgcLocal) return;
                    // Window closed or resized (e.g. F5 → fullscreen): rawvideo
                    // needs a fixed WxH, so re-resolve + restart (history-
                    // preserving; falls back to monitor if the window is gone).
                    Logger.Info($"ReplayBuffer: WGC-Ziel ungültig ({reason}) — richte neu aus");
                    RequestRestart();
                };
            }
            catch (Exception ex)
            {
                Logger.Warn("WGC init failed — falling back to monitor capture: " + ex.Message);
                try { _videoPipe?.Dispose(); } catch { }
                try { _wgc?.Dispose(); } catch { }
                _videoPipe = null;
                _wgc = null;
                videoArgs = null;
            }
        }

        // Record this generation's video identity — F9 only concatenates segments
        // of the SAME identity (same window+size or monitor+size).
        string identity = _wgc != null && videoArgs != null
            ? $"wgc:{plan.Hwnd}:{_wgc.Width}x{_wgc.Height}"
            : $"mon:{plan.MonitorIndex}:{plan.MonitorWidth}x{plan.MonitorHeight}";
        _genIdentity[_generation] = identity;
        _currentIdentity = identity;

        var args = FFmpegCommandBuilder.Build(_settings.Current, _segmentPattern,
            segmentOutput: true, segmentDurationSec: segSec, segmentWrap: wrap,
            audioPipeArgs: audioArgs, monitorIndex: plan.MonitorIndex,
            videoInputArgs: videoArgs);
        Logger.Info("[buffer-ffmpeg] CMD: " + args);

        _ffmpeg = new FFmpegService();
        if (!_ffmpeg.IsAvailable())
        {
            BufferError?.Invoke(this, "ffmpeg.exe not found");
            _audio?.Dispose();
            _audio = null;
            _videoPipe?.Dispose();
            _videoPipe = null;
            _wgc?.Dispose();
            _wgc = null;
            return;
        }
        _ffmpeg.StdErrLine += (_, line) =>
        {
            if (!string.IsNullOrWhiteSpace(line)) Logger.Info("[buffer-ffmpeg] " + line);
        };
        // Capture LOCAL references so this handler only reacts to ITS OWN
        // ffmpeg/audio/wgc instances (an old ffmpeg dying after a restart must
        // not dispose the brand-new pipeline or fire a false error).
        var ownFfmpeg = _ffmpeg;
        var ownAudio = _audio;
        var ownWgc = _wgc;
        var ownVideoPipe = _videoPipe;
        _ffmpeg.Exited += (_, code) =>
        {
            if (_ffmpeg != ownFfmpeg) return;

            BufferStateChanged?.Invoke(this, false);
            if (_audio == ownAudio)
            {
                ownAudio?.Dispose();
                _audio = null;
            }
            if (_videoPipe == ownVideoPipe)
            {
                ownVideoPipe?.Dispose();
                _videoPipe = null;
            }
            if (_wgc == ownWgc)
            {
                ownWgc?.Dispose();
                _wgc = null;
            }

            // Any exit we did NOT request is unexpected — report it, regardless
            // of the exit code (0/255 self-exit means the pipeline died). Only a
            // StopRequested exit (our own graceful/forced stop) is silent.
            if (!ownFfmpeg.StopRequested)
            {
                // Genuine crash: drop the dead instance so IsRunning is accurate
                // and the process handle isn't held until GC. (Re-check we're
                // still the current instance to avoid nulling a fresh restart.)
                if (_ffmpeg == ownFfmpeg) _ffmpeg = null;
                BufferError?.Invoke(this, $"Buffer unerwartet beendet (ffmpeg code {code}) — schau ins Log.");
            }
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

    // Assumes _lifecycleLock is held. Stops ffmpeg + audio + wgc; does NOT clear segments.
    private async Task StopCoreAsync()
    {
        _watchdogCts?.Cancel();
        if (_ffmpeg is not null)
            await _ffmpeg.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        _ffmpeg?.Dispose();
        _ffmpeg = null;
        _audio?.Dispose();
        _audio = null;
        _videoPipe?.Dispose();
        _videoPipe = null;
        _wgc?.Dispose();
        _wgc = null;
        CurrentPlan = null;
        BufferStateChanged?.Invoke(this, false);
    }

    public async Task ToggleAsync()
    {
        if (IsRunning)
        {
            await StopAsync().ConfigureAwait(false);
        }
        else
        {
            // Toggling ON is an explicit intent — honour it even if the auto-start
            // checkbox was off, instead of silently doing nothing (#19).
            if (!_settings.Current.ReplayBuffer.Enabled)
            {
                _settings.Current.ReplayBuffer.Enabled = true;
                _settings.Save();
            }
            await StartInternalAsync(clearHistory: true).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Coalesces settings-driven restart requests: many rapid calls (sliders,
    /// codec-list refresh) collapse into a single FULL restart ~300 ms after the
    /// last request, instead of a restart storm.
    /// </summary>
    public void RequestRestart() => RequestWork(fullRestart: true);

    /// <summary>
    /// Target changed (game started/stopped, foreground re-pin): debounced like
    /// RequestRestart, but if the video monitor is unchanged only the AUDIO
    /// source is swapped in place — zero video interruption, zero history loss.
    /// A pending full-restart request always escalates (never downgraded).
    /// </summary>
    public void RequestRetarget() => RequestWork(fullRestart: false);

    private bool _pendingFullRestart;

    private void RequestWork(bool fullRestart)
    {
        lock (_debounceLock)
        {
            if (fullRestart) _pendingFullRestart = true;
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
                bool full;
                lock (_debounceLock)
                {
                    full = _pendingFullRestart;
                    _pendingFullRestart = false;
                }
                try
                {
                    if (full) await RestartIfRunningAsync().ConfigureAwait(false);
                    else await RetargetAsync().ConfigureAwait(false);
                }
                catch (Exception ex) { Logger.Error("Debounced buffer restart/retarget failed", ex); }
            });
        }
    }

    /// <summary>
    /// Re-resolves the capture plan. If only the audio target changed (same
    /// monitor) the audio source is swapped in place; otherwise falls back to a
    /// full warm restart (history-preserving) to move the video to the new monitor.
    /// </summary>
    public async Task RetargetAsync()
    {
        bool needFullRestart = false;
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsRunning) return;
            var plan = CaptureTargetResolver.Resolve(_settings.Current.Capture, _settings.Current);
            // "Video unchanged" = same monitor AND, for WGC window capture, the
            // same window — a different hwnd needs a new video pipeline.
            bool videoUnchanged = CurrentPlan is { } prev
                && prev.MonitorIndex == plan.MonitorIndex
                && prev.UseWgc == plan.UseWgc
                && (!plan.UseWgc || prev.Hwnd == plan.Hwnd);
            if (videoUnchanged && _audio != null)
            {
                if (_audio.SwapSystemSource(plan.SysMode, plan.AudioPid))
                {
                    CurrentPlan = plan;
                    Logger.Info($"ReplayBuffer: retargeted audio in place → video='{plan.VideoLabel}', audio='{plan.AudioLabel}' (kein Video-Neustart, Historie intakt)");
                    return;
                }
            }
            // Monitor changed, no audio pipe, or swap failed → full warm restart.
            needFullRestart = true;
        }
        finally { _lifecycleLock.Release(); }

        if (needFullRestart)
            await RestartIfRunningAsync().ConfigureAwait(false);
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
                .Count(fi => fi.Length > 1024
                             && fi.LastWriteTimeUtc >= _sessionStartUtc
                             && IsCurrentIdentity(fi.Name));
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
                .Where(fi => fi.Length > 1024
                             && fi.LastWriteTimeUtc >= _sessionStartUtc
                             && IsCurrentIdentity(fi.Name))
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

    /// <summary>
    /// True when the segment file belongs to a generation with the CURRENT video
    /// identity (same window+size / monitor+size). Filenames are seg_{gen}_{NN}.mp4.
    /// </summary>
    private bool IsCurrentIdentity(string fileName)
    {
        var id = _currentIdentity;
        if (id is null) return true;
        var parts = Path.GetFileNameWithoutExtension(fileName).Split('_');
        return parts.Length == 3
               && int.TryParse(parts[1], out int gen)
               && _genIdentity.TryGetValue(gen, out var genId)
               && genId == id;
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
        _videoPipe?.Dispose();
        _wgc?.Dispose();
    }
}
