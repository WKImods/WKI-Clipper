using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.Models;
using WKI_Clipper.Native;

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
    private readonly BufferFolderRecord _record;
    private FFmpegService? _ffmpeg;
    private FFmpegService? _concatFfmpeg;
    private AudioTrackSet? _audio;
    private WgcWindowCapture? _wgc;
    private VideoPipeService? _videoPipe;
    private CancellationTokenSource? _watchdogCts;
    private string _bufferDir = "";
    private string _segmentPattern = "";
    // The buffer folder of the previous StartCore in this process (the registry record covers earlier runs).
    private string _lastBufferDir = "";
    // StartCore runs on every warm restart; the unsafe-folder warning is logged once.
    private bool _bufferFolderWarned;
    private bool _unknownDriveLogged;

    // Serializes every lifecycle transition (Start/Stop/Restart).
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    // Increments on each warm restart so a new ffmpeg run writes new filenames
    // instead of clearing/overwriting the previous run's segments.
    private int _generation = -1;
    // Set on cold start; SaveLast/AvailableSeconds only consider segments from
    // this session, so stragglers from a previous run (e.g. locked files a cold
    // ClearBufferDir couldn't delete) can never be spliced into a clip.
    private DateTime _sessionStartUtc = DateTime.MinValue;
    // What each generation recorded (window+size or monitor+size, and how it was encoded). F9 only ever
    // concatenates segments of the CURRENT identity - a freecam window switch can never splice two
    // windows/resolutions/codecs into one clip, and after a quick A→B→A detour the earlier A-segments
    // are still usable.
    private readonly ConcurrentDictionary<int, GenerationInfo> _generations = new();
    private volatile SegmentIdentity? _currentIdentity;
    // Pauses of the buffer for manual recordings: they explain a hole in a stitched clip.
    private readonly object _suspendLock = new();
    private readonly List<SuspendInterval> _suspensions = new();
    private const int MaxSuspensions = 64;
    // Trailing-debounce for coalescing restart bursts.
    private readonly object _debounceLock = new();
    private CancellationTokenSource? _restartDebounceCts;
    // Reentrancy guard for SaveLastAsync (0 = idle, 1 = saving).
    private int _saving;
    // Progress of the running clip save (null when none) - the busy text of a second F8/F9 reads it.
    private volatile SaveProgressInfo? _currentSave;
    // When the current generation's ffmpeg started — the stall check needs a grace period.
    private DateTime _genStartedUtc = DateTime.MaxValue;
    // The ring the RUNNING ffmpeg was started with. Settings change instantly but segment_wrap is
    // fixed until the debounced restart, so picks and prune must be sized from this, not from
    // the settings alone. A reference (RingPlan is a class) so another thread never sees a torn plan.
    private volatile RingPlan _runningRing = RingPlan.Create(60, 5);
    // The ring, the identity and the generation of the RUNNING ffmpeg as one reference: a save reads it once and works
    // with that snapshot throughout. Reading the three separately (or the identity again for every file) lets a warm
    // restart in the middle of a scan - Alt-Tab in Auto mode - change the answer halfway and mix two recordings.
    private volatile BufferEpoch? _epoch;
    // The registry record is trusted once per process (the first start). If the write keeps failing, every
    // later start would otherwise look like a folder move and empty the folder in use.
    private bool _recordChecked;
    // Ends waits that must not outlive the service (the old-folder cleanup waits for a running save).
    private readonly CancellationTokenSource _disposeCts = new();
    // The longest lookback the buffer drive can hold (0 = no decision yet). Decided at every (re)start and by
    // the low-space guard; min'ed with the configured length in EffectiveDurationSeconds.
    private volatile int _diskCapSeconds;
    private volatile bool _diskLimited;
    private int _lastAnnouncedCap = -1;
    // What the disk ring occupies at the running lookback (status line).
    private long _diskRingBytes;

    // ---- RAM ring ----
    // The in-memory ring of this session; null while the buffer records to disk. Like the segment files it outlives
    // warm restarts (Alt-Tab, settings, a manual recording); a cold start empties it, switching the buffer off or to the
    // disk releases it.
    private MemorySegmentStore? _memory;
    private MemoryBufferFeed? _memoryFeed;
    // The storage decision of the running (or last) run and the settings it was made from: a warm restart with the same
    // settings keeps the store, so an Alt-Tab never flips RAM/disk and starts the lookback over.
    private StorageDecision? _storage;
    private string? _storageKey;
    private volatile int _memoryCapSeconds;
    private volatile bool _memoryLimited;
    private bool _lowMemoryAnnounced;
    private bool _recentLowMemoryAnnounced;
    private bool _tempSweepDone;
    private bool _codecFallbackLogged;
    // Low-RAM guard hysteresis: when it last cut, and the gen-2 collection count right after that cut.
    private DateTime _memoryCutUtc = DateTime.MinValue;
    private int _memoryCutGen2 = -1;
    private int _guarding;
    private static readonly TimeSpan MemoryGuardInterval = TimeSpan.FromSeconds(3);
    /// <summary>After a cut the OS needs a moment to show the freed RAM; a second cut waits at least this long.</summary>
    private static readonly TimeSpan MemoryCutSettle = TimeSpan.FromSeconds(10);

    public bool IsRunning => _ffmpeg?.IsRunning ?? false;

    /// <summary>Where the running buffer keeps its ring; null when it is not running.</summary>
    public BufferStorageKind? StorageKind => IsRunning ? _storage?.Kind : null;

    /// <summary>RAM the ring holds right now (RAM mode) or the fixed size of the disk ring (disk mode).</summary>
    public long StorageBytes => _storage?.Kind switch
    {
        BufferStorageKind.Memory => _memory?.BytesInUse ?? 0,
        BufferStorageKind.Disk => _diskRingBytes,
        _ => 0
    };

    /// <summary>True while the RAM lookback runs shorter than configured (budget or free RAM).</summary>
    public bool MemoryLimited => _memoryLimited;

    /// <summary>"Zwischenspeicher: …" for the status pages; null when the buffer is off.</summary>
    public string? StorageLine => StorageKind is { } kind ? BufferStoragePlanner.StatusLine(kind, StorageBytes) : null;
    /// <summary>The capture plan the running buffer resolved (for honest UI). Null when stopped.</summary>
    public CaptureTargetResolver.CapturePlan? CurrentPlan { get; private set; }
    public event EventHandler<ReplaySavedInfo>? ReplaySaved;
    public event EventHandler<string>? GifSaved;             // final gif path
    public event EventHandler<string>? BufferError;
    public event EventHandler<string>? BufferInfo;           // non-error notices (save busy, restarting)
    public event EventHandler<bool>? BufferStateChanged;     // true=running
    /// <summary>
    /// A clip/GIF save passed every guard and is about to run. Raised at the key press, not when the
    /// file is done - a long clip takes a while to write and the feedback belongs to the press.
    /// </summary>
    public event EventHandler<SaveKind>? SaveStarted;
    /// <summary>A save was refused or failed (busy, buffer off, no material, stale, aborted, ffmpeg failure).</summary>
    public event EventHandler<SaveFailure>? SaveFailed;
    /// <summary>Progress of a running clip save, about once a second (raised from a worker thread).</summary>
    public event EventHandler<SaveProgressInfo>? SaveProgress;

    public ReplayBufferService(SettingsService settings) : this(settings, new BufferFolderRecord()) { }

    internal ReplayBufferService(SettingsService settings, BufferFolderRecord record)
    {
        _settings = settings;
        _record = record;
    }

    // Subscribers (sound cues) must never be able to break a save.
    private void SignalStarted(SaveKind kind)
    {
        try { SaveStarted?.Invoke(this, kind); }
        catch (Exception ex) { Logger.Warn("SaveStarted handler threw: " + ex.Message); }
    }

    private void SignalFailed(SaveKind kind, SaveFailureReason reason)
    {
        try { SaveFailed?.Invoke(this, new SaveFailure(kind, reason)); }
        catch (Exception ex) { Logger.Warn("SaveFailed handler threw: " + ex.Message); }
    }

    private void SignalProgress(SaveProgressInfo info)
    {
        try { SaveProgress?.Invoke(this, info); }
        catch (Exception ex) { Logger.Warn("SaveProgress handler threw: " + ex.Message); }
    }

    /// <summary>
    /// The length the RING is sized for: every sizing and keying site (ring plan, disk plan, RAM decision, age limit,
    /// forecasts) uses this, never a per-clip length. It is the LONGEST of the standard F9 length and every game's own
    /// length (<see cref="EffectiveSettings.LongestClipSeconds(WKI_Clipper.Models.AppSettings)"/>), so the ring never
    /// resizes - and never restarts the capture - when another game comes to the front; each press saves its own length
    /// (<see cref="ResolvePressClip"/>).
    /// </summary>
    public int RingLookbackSeconds => EffectiveSettings.LongestClipSeconds(_settings.Current);

    /// <summary>
    /// Per-press clip length override (seconds), read on the calling thread when F9 is pressed; null or a non-positive
    /// answer = the length of <see cref="GameExeAtPress"/>'s game (its profile, else the standard length). Never more
    /// than <see cref="EffectiveDurationSeconds"/>: the ring holds nothing older.
    /// </summary>
    public Func<int>? ClipSecondsAtPress { get; set; }

    /// <summary>
    /// The exe of the game in front at the key press (null = none or not recognised), read on the calling thread at the
    /// same moment as <see cref="ResolveGameAtPress"/>. Decides which profile's F9 length the press uses and what the
    /// log says about it; without it every press uses the standard length.
    /// </summary>
    public Func<string?>? GameExeAtPress { get; set; }

    /// <summary>
    /// The lookback the buffer actually holds: <see cref="RingLookbackSeconds"/> or, when the buffer drive or the RAM is
    /// too small for it, the shorter length decided at the last (re)start / by the low-RAM guard.
    /// </summary>
    public int EffectiveDurationSeconds
    {
        get
        {
            int configured = RingLookbackSeconds;
            int cap = _diskCapSeconds;
            int effective = cap > 0 ? Math.Min(configured, cap) : configured;
            int memCap = _memoryCapSeconds;
            return memCap > 0 ? Math.Min(effective, memCap) : effective;
        }
    }

    /// <summary>True while the lookback runs shorter than configured because the buffer drive is too small.</summary>
    public bool DiskLimited => _diskLimited;

    /// <summary>Cold start: clears the buffer directory and starts a fresh generation.</summary>
    public void Start() => _ = StartInternalAsync(clearHistory: true);

    /// <summary>
    /// True while something else needs the capture hardware exclusively (a manual
    /// recording). The buffer then stays down even if a debounced restart fires.
    /// </summary>
    public bool Suspended { get; private set; }

    /// <summary>
    /// Steps aside for a manual recording. AMF's capture component only allows one
    /// instance — a second one fails outright — and running two capture+encode pipelines
    /// at once was costing far more in-game FPS than anything else the app does.
    /// </summary>
    public async Task SuspendAsync()
    {
        Suspended = true;
        lock (_suspendLock)
        {
            _suspensions.Add(new SuspendInterval(DateTime.UtcNow, null));
            while (_suspensions.Count > MaxSuspensions) _suspensions.RemoveAt(0);
        }
        await StopAsync().ConfigureAwait(false);
        // The RAM ring outlives the pause (its footage goes into the next clip), so its low-RAM guard has to as well -
        // the stopped watchdog no longer runs it, and a recording is exactly when the game needs memory.
        if (_memory is not null) StartSuspendGuard();
    }

    private CancellationTokenSource? _suspendGuardCts;

    private void StartSuspendGuard()
    {
        _suspendGuardCts?.Cancel();
        var cts = new CancellationTokenSource();
        _suspendGuardCts = cts;
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && Suspended && _memory is not null)
            {
                try
                {
                    await Task.Delay(MemoryGuardInterval, token).ConfigureAwait(false);
                    GuardMemory();
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Logger.Warn("RAM guard during pause failed: " + ex.Message); }
            }
        }, token);
    }

    private void StopSuspendGuard()
    {
        try { _suspendGuardCts?.Cancel(); } catch { }
        _suspendGuardCts = null;
    }

    /// <summary>Comes back after the recording, keeping the segments from before the pause.</summary>
    public void Resume()
    {
        Suspended = false;
        StopSuspendGuard();
        lock (_suspendLock)
        {
            int last = _suspensions.Count - 1;
            if (last >= 0 && _suspensions[last].ToUtc is null)
                _suspensions[last] = _suspensions[last] with { ToUtc = DateTime.UtcNow };
        }
        _ = StartInternalAsync(clearHistory: false);
    }

    private SuspendInterval[] SuspensionsSnapshot()
    {
        lock (_suspendLock) return _suspensions.ToArray();
    }

    private async Task StartInternalAsync(bool clearHistory)
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsRunning) return;
            if (Suspended) return;
            if (!_settings.Current.ReplayBuffer.Enabled) return;
            StartCore(clearHistory);
        }
        finally { _lifecycleLock.Release(); }
    }

    /// <summary>
    /// The buffer folder to use now, always a fully qualified path: the configured one, unless it is not a real folder
    /// ("D:", "D:\", a relative path), is the program's own folder, or overlaps the clips/screenshots folder - then the
    /// default, for this run. The settings file is left alone; the Paths page tells the user.
    /// </summary>
    private string ResolveBufferDir(bool warn)
    {
        var output = _settings.Current.Output;
        var bufferFolder = PathRules.SafeBufferFolder(output.BufferFolder, output.ClipsFolder, output.ScreenshotsFolder,
            PathRules.DefaultBufferFolder, PathRules.InstallDirectory);
        if (warn && !string.Equals(bufferFolder, output.BufferFolder, StringComparison.Ordinal) && !_bufferFolderWarned)
        {
            _bufferFolderWarned = true;
            var problem = PathRules.BufferFolderProblem(output.BufferFolder, PathRules.InstallDirectory);
            var why = problem switch
            {
                FolderProblem.NotFullyQualified => "it is not a full path (a bare drive or relative path means different folders to ffmpeg and to this program)",
                FolderProblem.DriveRoot => "it is the root of a drive",
                FolderProblem.InstallDirectory => "it is the program's own folder (or inside it)",
                _ => "it is the clips or screenshots folder (or inside it): its segments would show up as clips"   // a segment every few seconds
            };
            Logger.Warn($"ReplayBuffer: buffer folder '{output.BufferFolder}' is not usable - {why} - using '{SettingsService.ExpandPath(bufferFolder)}' instead for this run");
        }
        return SettingsService.ExpandPath(bufferFolder);
    }

    /// <summary>
    /// The segment muxer's output pattern. Fully qualified on purpose: ffmpeg runs in its own directory, so a relative
    /// folder would put the ring where this program never looks.
    /// </summary>
    internal static string SegmentPatternFor(string bufferDir, int generation)
        => Path.Combine(Path.GetFullPath(bufferDir), $"seg_{generation}_%02d.mp4");

    /// <summary>
    /// Cold start: a fresh session. The generation number keeps counting - it is never reused within a process, because
    /// a save that is still reading seg_N_* when the buffer is switched off and on would otherwise find the new run
    /// writing over the very files it is about to open. The old segments are deleted here only while no save runs;
    /// otherwise the session start (everything older is unusable) lets the retention rules remove them once the
    /// save is over (<see cref="PruneAfterSave"/>).
    /// </summary>
    private void ResetHistoryForColdStart()
    {
        if (Volatile.Read(ref _saving) == 0)
            ClearBufferDir(_bufferDir);
        else
            Logger.Info("ReplayBuffer: a clip is still being written - the old segments are removed after it.");
        CleanStaleClipParts();
        _generation++;
        _sessionStartUtc = DateTime.UtcNow;
        _generations.Clear();
        _lastAnnouncedCap = -1;
        // A save that still copies pinned RAM segments keeps them until it is done.
        if (_memory is { } mem && mem.BytesInUse > 0)
        {
            mem.Clear();
            CollectFreedMemory();
        }
        _storage = null;
        _storageKey = null;
        _memoryCapSeconds = 0;
        _memoryLimited = false;
        _lowMemoryAnnounced = false;
    }

    // Assumes _lifecycleLock is held.
    private void StartCore(bool clearHistory)
    {
        _bufferDir = ResolveBufferDir(warn: true);
        // A folder this app creates is its own and may be hidden; a folder the user picked that already exists is not.
        if (!Directory.Exists(_bufferDir))
        {
            Directory.CreateDirectory(_bufferDir);
            _record.AddCreated(_bufferDir);
        }

        // A moved buffer folder: the new one starts fresh, the old one (gigabytes at 30 minutes) is emptied.
        bool folderMoved = HandleBufferFolderChange();

        // Resolve the single capture plan (video monitor + audio route) FIRST, so
        // we can tell whether the capture target changed across a warm restart.
        // Same resolver as manual recording + the status UI, so F9 clips exactly
        // what the UI advertises. Resolved once per generation → pinned; alt-tab
        // does not restart the buffer, so the clip stays on the game's monitor.
        var plan = CaptureTargetResolver.Resolve(_settings.Current.Capture, _settings.Current);
        var prevPlan = CurrentPlan;

        if (clearHistory)
        {
            ResetHistoryForColdStart();
        }
        else
        {
            _generation++;
            // If the target MONITOR changed (e.g. the game launched on another
            // monitor), the previous segments are a different display/resolution.
            // Start a fresh save-window so they can never be spliced into a clip
            // (which would give wrong footage or break the stream-copy concat).
            if (folderMoved || (prevPlan is { } pp && pp.MonitorIndex != plan.MonitorIndex))
                _sessionStartUtc = DateTime.UtcNow;
        }

        // A reader of a run that died on its own (no StopCore since) has reached the end of its stream by now.
        DisposeMemoryFeed();

        // RAM or disk for this run, then: does the lookback fit there? Decided before anything is started.
        var storage = DecideStorage(clearHistory);
        bool memoryMode = storage.Kind == BufferStorageKind.Memory && _memory is not null;
        if (memoryMode)
        {
            _diskCapSeconds = 0;
            _diskLimited = false;
        }
        else if (!DecideDiskPlan(plan)) return;

        CurrentPlan = plan;

        _segmentPattern = SegmentPatternFor(_bufferDir, _generation);
        var ring = CurrentRingPlan();
        _runningRing = ring;
        int segSec = ring.SegmentSeconds;
        // Wrap = how many segments this run holds: what a save picks, +1 for tail tolerance,
        // + RingPlan.Reserve so the oldest picked segment is not the next one overwritten.
        int wrap = ring.Wrap;
        Logger.Info($"ReplayBuffer.Start: gen={_generation}, cold={clearHistory}, video='{plan.VideoLabel}', audio='{plan.AudioLabel}' (monitorIdx={plan.MonitorIndex}, pid={plan.AudioPid?.ToString() ?? "null"}), lookback={DurationText.Format(EffectiveDurationSeconds)}");

        // Audio pipe first (named pipe must exist before ffmpeg opens it).
        // If audio init fails, fall back to video-only.
        _audio = new AudioTrackSet(_settings.Current, plan.SysMode, plan.AudioPid);
        IReadOnlyList<string>? audioArgs = null;
        if (_audio.HasAnyAudio())
        {
            bool ok = _audio.Start();
            if (ok)
            {
                audioArgs = _audio.PipeArgs;
            }
            else
            {
                Logger.Warn("ReplayBuffer: audio pipe failed to start, continuing with video only. " + _audio.LastError);
                BufferError?.Invoke(this, L.T("Audio init fehlgeschlagen — ", "Audio init failed — ") + (_audio.LastError ?? L.T("unbekannt", "unknown")));
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

        // Record this generation's identity - F9 only concatenates segments of the SAME identity: the same
        // source (window+size or monitor+size) AND the same encoding (codec, fps, resolution, audio tracks,
        // capture path, keyframes). A codec or fps change followed by F9 within the ring would otherwise
        // concat H.264 with HEVC.
        var settings = _settings.Current;
        var keyframes = KeyframePolicy.Effective(settings.ReplayBuffer);
        string capturePath = videoArgs != null
            ? "wgc"
            : FFmpegCommandBuilder.UsesAmfNativeCapture(settings, rawInput: false) ? "amf" : "dda";
        string source = _wgc != null && videoArgs != null
            ? SegmentIdentity.SourceKey(true, plan.Hwnd, _wgc.Width, _wgc.Height, plan.MonitorIndex)
            : SegmentIdentity.SourceKey(false, IntPtr.Zero, plan.MonitorWidth, plan.MonitorHeight, plan.MonitorIndex);
        string stream = SegmentIdentity.StreamKey(settings.Video.Codec, settings.Video.Framerate,
            settings.Video.Resolution, audioArgs?.Count ?? 0, capturePath, keyframes);
        var identity = new SegmentIdentity(source, stream);
        int ownGeneration = _generation;
        _generations[ownGeneration] = new GenerationInfo(ownGeneration, identity,
            plan.TargetProcess ?? $"Monitor {plan.MonitorIndex + 1}", DateTime.UtcNow, null);
        _currentIdentity = identity;
        _epoch = new BufferEpoch(ring, identity, ownGeneration, _bufferDir, memoryMode ? _memory : null);

        var args = FFmpegCommandBuilder.Build(settings, _segmentPattern,
            segmentOutput: true, segmentDurationSec: segSec, segmentWrap: wrap,
            audioPipeArgs: audioArgs, monitorIndex: plan.MonitorIndex,
            videoInputArgs: videoArgs, keyframes: keyframes, mpegTsPipe: memoryMode);
        Logger.Info("[buffer-ffmpeg] CMD: " + args);

        if (memoryMode)
        {
            // Segments older than what a save can reach (plus slack) leave the RAM ring as soon as new ones arrive.
            _memory!.SetAgeLimit(TimeSpan.FromSeconds(SegmentRetention.AgeWindowSeconds(ring, CurrentRingPlan())));
        }
        else
        {
            // ffmpeg cannot (re)create a hidden file: names of this generation left over from an earlier run become visible.
            SegmentVisibility.ShowGeneration(_bufferDir, ownGeneration);
        }
        // Empty in RAM mode, the hidden ring in disk mode: nothing to look at in Explorer either way - if the folder is ours.
        SegmentVisibility.UpdateFolder(_bufferDir, IsOurFolder(_bufferDir));

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
                MarkGenerationStopped(ownGeneration);
                // Genuine crash: drop the dead instance so IsRunning is accurate
                // and the process handle isn't held until GC. (Re-check we're
                // still the current instance to avoid nulling a fresh restart.)
                if (_ffmpeg == ownFfmpeg) _ffmpeg = null;
                // A full drive is the one crash with an obvious cause the user can fix.
                if (DiskProbe.TryGetFreeSpace(_bufferDir, out long freeNow, out string driveNow)
                    && freeNow < DiskPlan.CriticalBytes)
                    BufferError?.Invoke(this, FullDiskMessage(driveNow));
                else
                    BufferError?.Invoke(this, L.T($"Buffer unerwartet beendet (ffmpeg code {code}) — schau ins Log.",
                                                  $"Buffer stopped unexpectedly (ffmpeg code {code}) — check the log."));
            }
        };

        try
        {
            _ffmpeg.Start(args);
            if (memoryMode)
            {
                // At once: ffmpeg blocks as soon as the stdout pipe is full.
                // A reader that fails keeps draining the pipe (ffmpeg must not block) and the run is restarted - instead
                // of ffmpeg hanging on a full stdout until the stall watchdog notices it 20-50 s later.
                var feed = new MemoryBufferFeed(_ffmpeg.StandardOutputStream!, _memory!.OpenWriter(ownGeneration), segSec,
                    KeyframePolicy.SegmentTimeDelta(settings.Video.Framerate, keyframes), () => ownFfmpeg.StopRequested,
                    cause =>
                    {
                        if (_ffmpeg != ownFfmpeg) return;
                        Logger.Warn($"ReplayBuffer: RAM reader of generation {ownGeneration} failed ({cause}) - restarting the buffer");
                        RequestRestart();
                    });
                _memoryFeed = feed;
                feed.Start();
            }
            _genStartedUtc = DateTime.UtcNow;
            if (_generations.TryGetValue(ownGeneration, out var started))
                _generations[ownGeneration] = started with { StartedUtc = _genStartedUtc };
            BufferStateChanged?.Invoke(this, true);
            StartWatchdog();
        }
        catch (Exception ex)
        {
            BufferError?.Invoke(this, ex.Message);
        }

        // Only now: the prune rules need the new generation registered and the running ring set.
        PruneOldSegments();
    }

    private static string FullDiskMessage(string drive)
        => L.T($"Speicher auf {drive} fast voll — Buffer gestoppt, damit Windows nicht vollläuft. Platz schaffen und Buffer wieder einschalten (Strg+F10).",
               $"Drive {drive} is almost full — buffer stopped so Windows does not run out of space. Free some space and turn the buffer back on (Ctrl+F10).");

    private void MarkGenerationStopped(int generation)
    {
        if (_generations.TryGetValue(generation, out var info) && info.StoppedUtc is null)
            _generations.TryUpdate(generation, info with { StoppedUtc = DateTime.UtcNow }, info);
    }

    // ---- buffer folder change ----

    /// <summary>
    /// A different buffer folder than before starts fresh: the own files of the NEW folder are removed (never while a
    /// save runs - the retention rules do it afterwards) and the OLD folder's segments are deleted in the background
    /// once no save can still be reading them. "Before" is the folder of the previous start in this process; the
    /// HKCU record (the folder of an earlier run) is trusted only at the first start of the process - if writing it
    /// kept failing, every warm restart would otherwise look like a move and empty the folder in use. Old folders
    /// whose cleanup never finished (app closed, save outlasted the wait) are listed in the record and retried at
    /// the first start of the next run.
    /// </summary>
    private bool HandleBufferFolderChange()
    {
        bool first = !_recordChecked;
        _recordChecked = true;

        var left = new List<string>();   // folders the buffer has moved away from
        if (first)
        {
            var recorded = _record.Read();
            if (BufferFolderRules.IsDifferentFolder(recorded, _bufferDir)) left.Add(recorded!);
        }
        if (!string.IsNullOrEmpty(_lastBufferDir) && BufferFolderRules.IsDifferentFolder(_lastBufferDir, _bufferDir)
            && !left.Any(o => !BufferFolderRules.IsDifferentFolder(o, _lastBufferDir)))
            left.Add(_lastBufferDir);

        var cleanup = new List<string>(left);
        if (first)
        {
            foreach (var pending in _record.ReadPending())
                if (BufferFolderRules.IsDifferentFolder(pending, _bufferDir)
                    && !cleanup.Any(o => !BufferFolderRules.IsDifferentFolder(o, pending)))
                    cleanup.Add(pending);
                else if (!BufferFolderRules.IsDifferentFolder(pending, _bufferDir))
                    _record.RemovePending(pending);   // the folder is in use again: nothing to clean
        }

        _lastBufferDir = _bufferDir;
        _record.Write(_bufferDir);

        // A folder the buffer leaves is no longer kept out of sight.
        foreach (var folder in left)
            if (IsOurFolder(folder)) SegmentVisibility.ShowFolder(folder);

        foreach (var folder in cleanup)
        {
            _record.AddPending(folder);   // before the work: it survives a closed app
            ScheduleOldFolderCleanup(folder);
        }
        if (left.Count == 0) return false;

        if (Volatile.Read(ref _saving) == 0)
        {
            int cleared = BufferFolderRules.DeleteOwnFiles(_bufferDir);
            Logger.Info($"ReplayBuffer: buffer folder is now '{_bufferDir}' (was '{string.Join("', '", left)}') - starting fresh, removed {cleared} old file(s) from the new folder");
        }
        else
        {
            Logger.Info($"ReplayBuffer: buffer folder is now '{_bufferDir}' (was '{string.Join("', '", left)}') - a clip is being written, old files in the new folder are removed after it");
        }
        return true;
    }

    private void ScheduleOldFolderCleanup(string oldDir)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var ct = _disposeCts.Token;
                // A save still reading from the old folder must finish first, however long a 30-minute clip takes on a
                // slow disk - giving up after a fixed time would leave gigabytes behind that nothing knows about.
                while (Volatile.Read(ref _saving) != 0)
                    await Task.Delay(500, ct).ConfigureAwait(false);

                // The user may have switched back meanwhile - never empty the folder in use.
                if (!BufferFolderRules.IsDifferentFolder(oldDir, _bufferDir))
                {
                    _record.RemovePending(oldDir);
                    return;
                }
                int n = BufferFolderRules.DeleteOwnFiles(oldDir);
                if (BufferFolderRules.HasOwnFiles(oldDir))
                {
                    Logger.Warn($"ReplayBuffer: the old buffer folder '{oldDir}' still holds files that could not be deleted ({n} removed) - tried again at the next start");
                    return;   // stays listed as pending
                }
                _record.RemovePending(oldDir);
                Logger.Info($"ReplayBuffer: emptied the old buffer folder '{oldDir}' ({n} file(s))");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Warn("Old buffer folder cleanup failed: " + ex.Message); }
        });
    }
    // ---- disk planning ----

    /// <summary>The newest complete segments of matching generations, ascending, as (bytes, end time).</summary>
    private List<(long Bytes, DateTime EndUtc)> RecentSamples(Func<GenerationInfo, bool> match, int max)
    {
        try
        {
            var files = new DirectoryInfo(_bufferDir).EnumerateFiles("seg_*.mp4")
                .Where(fi => fi.Length > 1024 && fi.LastWriteTimeUtc >= _sessionStartUtc
                             && SegmentName.TryParseGeneration(fi.Name, out int g)
                             && _generations.TryGetValue(g, out var info) && match(info))
                .OrderBy(fi => fi.LastWriteTimeUtc)
                .ToList();
            // The newest may still be open (and small).
            if (files.Count > 0) files.RemoveAt(files.Count - 1);
            return files.TakeLast(max).Select(fi => (fi.Length, fi.LastWriteTimeUtc)).ToList();
        }
        catch { return new List<(long, DateTime)>(); }
    }

    private DiskForecast BuildForecast(string dir, int durationSeconds, int segSec, int monitorIndex, long? observedBps)
    {
        long videoBps = FFmpegCommandBuilder.EffectiveBitrate(_settings.Current, monitorIndex);
        long segBytes = DiskPlan.SegmentBytes(DiskPlan.EstimateSegmentBytes(segSec, videoBps), observedBps, segSec);
        long required = DiskPlan.RequiredBytes(durationSeconds, segSec, segBytes);
        if (!DiskProbe.TryGetFreeSpace(dir, out long free, out string drive))
            return new DiskForecast(durationSeconds, required, 0, "", false,
                new DiskDecision(durationSeconds, false, false, required, long.MaxValue));

        // Our own segments in the folder are freed again when pruned - they count as usable.
        long usable = free + DiskProbe.OwnSegmentBytes(dir);
        return new DiskForecast(durationSeconds, required, free, drive, true,
            DiskPlan.Decide(durationSeconds, segSec, segBytes, usable));
    }

    /// <summary>
    /// What a lookback of <paramref name="durationSeconds"/> would cost on the buffer drive, and what the buffer would
    /// do about it. No side effects - the settings page shows it next to the length picker.
    /// </summary>
    public DiskForecast Forecast(int durationSeconds)
    {
        var rb = _settings.Current.ReplayBuffer;
        string dir = string.IsNullOrEmpty(_bufferDir) ? ResolveBufferDir(warn: false) : _bufferDir;
        var plan = CurrentPlan ?? CaptureTargetResolver.Resolve(_settings.Current.Capture, _settings.Current);
        return BuildForecast(dir, DurationText.Clamp(durationSeconds), Math.Max(1, rb.SegmentDurationSeconds), plan.MonitorIndex, null);
    }

    /// <summary>
    /// Pre-start check: the configured lookback if it fits, a shorter one if not, none if even the shortest does not.
    /// Returns false when the buffer must not start.
    /// </summary>
    private bool DecideDiskPlan(CaptureTargetResolver.CapturePlan plan)
    {
        var s = _settings.Current;
        var rb = s.ReplayBuffer;
        int configured = RingLookbackSeconds;
        int seg = Math.Max(1, rb.SegmentDurationSeconds);

        // The previous generation's rate is the best guess for the next one - if it encoded the same way.
        // (Audio track count is not known yet, so only codec, fps and resolution are matched.)
        string prefix = $"{s.Video.Codec}|{s.Video.Framerate}|{s.Video.Resolution}|";
        var observed = DiskPlan.ObservedBytesPerSecond(RecentSamples(
            g => g.Identity.Stream.StartsWith(prefix, StringComparison.Ordinal), 12));

        var f = BuildForecast(_bufferDir, configured, seg, plan.MonitorIndex, observed);
        _diskRingBytes = f.RequiredBytes;
        if (f.Decision.Shortened)
            _diskRingBytes = f.RequiredBytes * DiskPlan.RingSegments(f.Decision.EffectiveSeconds, seg)
                             / Math.Max(1, DiskPlan.RingSegments(configured, seg));
        if (!f.Known)
        {
            // UNC/unknown drive: no way to ask, so no check.
            if (!_unknownDriveLogged)
            {
                _unknownDriveLogged = true;
                Logger.Info($"ReplayBuffer: free space of '{_bufferDir}' is unknown (network or unavailable drive) - skipping the disk check");
            }
            _diskCapSeconds = 0;
            _diskLimited = false;
            return true;
        }

        var d = f.Decision;
        if (d.CannotStart)
        {
            _diskCapSeconds = 0;
            _diskLimited = false;
            Logger.Warn($"ReplayBuffer: not starting - {DiskPlan.FormatBytes(f.FreeBytes)} free on {f.Drive}, even {DurationText.Format(DurationText.MinSeconds)} needs more");
            BufferError?.Invoke(this, L.T(
                $"Zu wenig Speicher auf {f.Drive} (frei {DiskPlan.FormatBytes(f.FreeBytes)}) — Buffer startet nicht. Platz schaffen oder Buffer-Ordner auf ein anderes Laufwerk legen (Einstellungen → Pfade).",
                $"Not enough space on {f.Drive} ({DiskPlan.FormatBytes(f.FreeBytes)} free) — the buffer will not start. Free some space or move the buffer folder to another drive (Settings → Paths)."));
            return false;
        }

        _diskCapSeconds = d.EffectiveSeconds;
        _diskLimited = d.Shortened;
        if (d.Shortened)
        {
            if (d.EffectiveSeconds != _lastAnnouncedCap)
            {
                _lastAnnouncedCap = d.EffectiveSeconds;
                BufferInfo?.Invoke(this, L.T(
                    $"Zu wenig Speicher auf {f.Drive} für {DurationText.Format(configured)} Rückblick (braucht ~{DiskPlan.FormatBytes(d.RequiredBytes)}, frei {DiskPlan.FormatBytes(f.FreeBytes)}) — Buffer läuft mit {DurationText.Format(d.EffectiveSeconds)}. Tipp: Buffer-Ordner auf ein Laufwerk mit mehr Platz legen.",
                    $"Not enough space on {f.Drive} for a {DurationText.Format(configured)} lookback (needs ~{DiskPlan.FormatBytes(d.RequiredBytes)}, {DiskPlan.FormatBytes(f.FreeBytes)} free) — the buffer runs with {DurationText.Format(d.EffectiveSeconds)}. Tip: move the buffer folder to a drive with more space."));
            }
        }
        else
        {
            _lastAnnouncedCap = -1;
        }
        return true;
    }

    /// <summary>The default buffer folder or one this app created: only such a folder is ever hidden.</summary>
    private bool IsOurFolder(string dir)
        => !string.IsNullOrWhiteSpace(dir)
           && (!BufferFolderRules.IsDifferentFolder(dir, SettingsService.ExpandPath(PathRules.DefaultBufferFolder))
               || _record.WasCreated(dir));

    // ---- RAM or disk ----

    /// <summary>Audio tracks the run will most likely have (as AudioTrackSet splits them), for the size estimate.</summary>
    private static int ExpectedAudioTracks(AppSettings s)
    {
        bool mic = s.Audio.RecordMicrophone && !string.IsNullOrWhiteSpace(s.Audio.MicDeviceId);
        bool sys = s.Audio.RecordSystemSound;
        return mic && sys && s.Audio.SeparateMicTrack ? 2 : (mic || sys ? 1 : 0);
    }

    /// <summary>
    /// The storage a lookback of <paramref name="durationSeconds"/> would use with the current settings and free RAM.
    /// No side effects - the settings page shows it next to the length picker.
    /// </summary>
    public StorageDecision PlanStorage(int durationSeconds)
    {
        var s = _settings.Current;
        var rb = s.ReplayBuffer;
        var (ram, budget, recentLowMemory) = StorageInputs(coldStart: _memory is null);
        return BufferStoragePlanner.Decide(rb.StorageMode, MpegTs.SupportsCodec(s.Video.Codec), durationSeconds,
            Math.Max(1, rb.SegmentDurationSeconds), FFmpegCommandBuilder.MaxEffectiveBitrate(s), ExpectedAudioTracks(s),
            budget, ram, recentLowMemory);
    }

    /// <summary>
    /// The longest lookback Auto keeps in RAM with the current settings and RAM state (0 = always disk) - the settings page
    /// shows this instead of a fixed "about 5 minutes".
    /// </summary>
    public int LongestMemoryLookback()
    {
        var s = _settings.Current;
        var (ram, budget, recentLowMemory) = StorageInputs(coldStart: _memory is null);
        return BufferStoragePlanner.LongestAutoMemoryLookback(MpegTs.SupportsCodec(s.Video.Codec),
            Math.Max(1, s.ReplayBuffer.SegmentDurationSeconds), FFmpegCommandBuilder.MaxEffectiveBitrate(s),
            ExpectedAudioTracks(s), budget, ram, recentLowMemory);
    }

    /// <summary>
    /// RAM state, RAM budget and low-RAM history for a storage decision. Our own ring counts as available: it is ours to
    /// give back, and leaving it out made a warm restart see its own 1.2 GB as "the game's" and shrink or drop the ring.
    /// The history only matters at a cold start.
    /// </summary>
    private (RamStatus? Ram, long Budget, bool RecentLowMemory) StorageInputs(bool coldStart)
    {
        var rb = _settings.Current.ReplayBuffer;
        var measured = PhysicalMemory.Read();
        var ram = BufferStoragePlanner.WithOwnRing(measured, _memory?.BytesInUse ?? 0);
        long budget = (long)BufferStoragePlanner.EffectiveMaxMemoryMB(rb.MaxMemoryMB, ram?.TotalBytes ?? 0) << 20;
        bool recent = coldStart && rb.StorageMode == BufferStorageMode.Auto
                      && _record.ReadLowMemory() is { } t
                      && DateTime.UtcNow - t < BufferStoragePlanner.RecentLowMemoryWindow;
        return (ram, budget, recent);
    }

    /// <summary>Forgets that the RAM ring had to be shortened (the user changed the storage setting).</summary>
    public void ClearLowMemoryHistory() => _record.WriteLowMemory(null);

    /// <summary>
    /// RAM or disk for the next run. A cold start or changed settings decide anew. A warm restart with the same settings
    /// (Alt-Tab, window resize, a manual recording) keeps the previous decision AS IT IS: never a shorter RAM ring, never a
    /// switch from RAM to disk - that would throw the lookback away, and memory pressure is the low-RAM guard's job
    /// (<see cref="GuardMemory"/>). Switching stores starts a fresh lookback: RAM segments and mp4 files are different
    /// containers and are never joined into one clip, so the old footage is released (RAM) or left to the prune (disk) -
    /// documented loss. Assumes _lifecycleLock is held.
    /// </summary>
    private StorageDecision DecideStorage(bool coldStart)
    {
        var s = _settings.Current;
        var rb = s.ReplayBuffer;
        int seg = Math.Max(1, rb.SegmentDurationSeconds);
        int lookback = RingLookbackSeconds;
        long video = FFmpegCommandBuilder.MaxEffectiveBitrate(s);
        int tracks = ExpectedAudioTracks(s);
        string key = string.Join("|", rb.StorageMode, rb.MaxMemoryMB, lookback, seg, s.Video.Codec, video, tracks);

        var previous = _storage;
        bool keep = !coldStart && previous is not null && _storageKey == key
                    && (previous.Kind == BufferStorageKind.Disk || _memory is not null);
        if (keep)
        {
            _storageKey = key;
            return previous!;
        }

        var (ram, budget, recentLowMemory) = StorageInputs(coldStart);
        bool codecOk = MpegTs.SupportsCodec(s.Video.Codec);
        var d = BufferStoragePlanner.Decide(rb.StorageMode, codecOk, lookback, seg, video, tracks, budget, ram, recentLowMemory);

        if (d.Reason == StorageReason.CodecUnsupported && rb.StorageMode != BufferStorageMode.Disk && !_codecFallbackLogged)
        {
            _codecFallbackLogged = true;
            Logger.Info($"ReplayBuffer: {s.Video.Codec} does not survive MPEG-TS - the ring stays on the disk for this codec");
        }
        if (d.Reason == StorageReason.RecentLowMemory && !_recentLowMemoryAnnounced)
        {
            _recentLowMemoryAnnounced = true;
            BufferInfo?.Invoke(this, L.T(
                "Beim letzten Mal war der Arbeitsspeicher knapp und der RAM-Rückblick musste gekürzt werden — der Buffer nutzt deshalb vorerst die Festplatte (versteckter Ordner). Ändern unter Video → Zwischenspeicher.",
                "Last time the RAM ran low and the in-memory lookback had to be shortened — the buffer uses the disk (hidden folder) for now. Change it under Video → Buffer storage."));
        }

        if (previous is not null && previous.Kind != d.Kind && !coldStart)
        {
            _sessionStartUtc = DateTime.UtcNow;
            Logger.Info($"ReplayBuffer: storage switches {previous.Kind} -> {d.Kind} - the lookback starts over");
        }

        if (d.Kind == BufferStorageKind.Memory)
        {
            if (_memory is null) _memory = new MemorySegmentStore(d.BudgetBytes);
            else _memory.SetBudget(d.BudgetBytes);
            _memoryCapSeconds = d.Shortened ? d.LookbackSeconds : 0;
            _memoryLimited = d.Shortened;
            _memoryCutUtc = DateTime.MinValue;
            _memoryCutGen2 = -1;
            if (d.Shortened && !_lowMemoryAnnounced)
            {
                _lowMemoryAnnounced = true;
                BufferInfo?.Invoke(this, L.T(
                    $"Zu wenig Arbeitsspeicher für {DurationText.Format(lookback)} Rückblick im RAM — Buffer läuft mit {DurationText.Format(d.LookbackSeconds)}. Für längere Rückblicke „Zwischenspeicher: Automatisch“ oder „Festplatte“ wählen.",
                    $"Not enough RAM for a {DurationText.Format(lookback)} lookback in memory — the buffer runs with {DurationText.Format(d.LookbackSeconds)}. For longer lookbacks choose storage “Automatic” or “Disk”."));
            }
        }
        else
        {
            ReleaseMemory();
        }

        Logger.Info($"ReplayBuffer: storage {d.Kind} ({d.Reason}), ring ~{DiskPlan.FormatBytes(d.EstimatedBytes)}" +
                    (d.Kind == BufferStorageKind.Memory ? $", RAM budget {DiskPlan.FormatBytes(d.BudgetBytes)}" : "") +
                    (ram is { } r ? $", free RAM (incl. own ring) {DiskPlan.FormatBytes(r.AvailableBytes)} of {DiskPlan.FormatBytes(r.TotalBytes)}" : ""));
        _storage = d;
        _storageKey = key;
        return d;
    }

    /// <summary>Gives the RAM ring back (pinned segments go when their save is done).</summary>
    private void ReleaseMemory()
    {
        var mem = _memory;
        _memory = null;
        _memoryCapSeconds = 0;
        _memoryLimited = false;
        if (mem is null) return;
        bool held = mem.BytesInUse > 0;
        mem.Dispose();
        if (held) CollectFreedMemory();
    }

    /// <summary>
    /// Evicted blocks are large-object-heap arrays: dropped, they are garbage, and the OS sees no free RAM until a gen-2
    /// collection - a low-RAM guard measuring before that would cut again and again for nothing. One blocking collection
    /// right after the ring shrank makes the memory really free. Not compacting: the blocks are all the same size, and the
    /// freed regions are decommitted without moving anything.
    /// </summary>
    private static void CollectFreedMemory()
    {
        try { GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false); }
        catch (Exception ex) { Logger.Warn("GC after freeing the RAM ring failed: " + ex.Message); }
    }

    /// <summary>Lets the reader of the stopped ffmpeg finish its stream (the last segment), then drops it.</summary>
    private void DisposeMemoryFeed()
    {
        var feed = _memoryFeed;
        _memoryFeed = null;
        if (feed is null) return;
        if (!feed.WaitForEnd(TimeSpan.FromSeconds(3)))
            Logger.Warn("RAM buffer: the reader did not finish within 3 s");
        Logger.Info($"RAM buffer: generation {feed.Writer.Generation} read {feed.BytesRead / (1024 * 1024)} MiB, " +
                    $"{feed.Writer.Completed} segments ({feed.Writer.Dropped} dropped), reader CPU {feed.CpuTime.TotalSeconds:0.00} s");
        feed.Dispose();
    }

    public async Task StopAsync()
    {
        CancelDebounce();
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            // Off for real (not a pause for a manual recording): the RAM goes back now, the next start is a cold one anyway.
            if (!Suspended)
            {
                StopSuspendGuard();
                ReleaseMemory();
                _storage = null;
                _storageKey = null;
            }
        }
        finally { _lifecycleLock.Release(); }
    }

    // Assumes _lifecycleLock is held. Stops ffmpeg + audio + wgc; does NOT clear segments.
    private async Task StopCoreAsync()
    {
        _watchdogCts?.Cancel();
        if (_ffmpeg is not null)
            await _ffmpeg.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        MarkGenerationStopped(_generation);
        // ffmpeg is gone: its reader drains the rest of the pipe and closes the last RAM segment.
        DisposeMemoryFeed();
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
    /// Approximate seconds currently available to save (complete segments of the current identity inside the
    /// lookback window × segment length, capped at the effective lookback). For honest UI display.
    /// </summary>
    public int AvailableSeconds()
    {
        if (!IsRunning || string.IsNullOrEmpty(_bufferDir)) return 0;
        int segSec = Math.Max(1, _runningRing.SegmentSeconds);
        int target = EffectiveDurationSeconds;
        var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(target);
        var identity = _currentIdentity;   // read once: not once per file
        if (_epoch?.Memory is { } mem)
        {
            double seconds = mem.Snapshot()
                .Where(s => s.EndUtc >= _sessionStartUtc && s.EndUtc > cutoff && IsIdentity(s.Generation, identity))
                .Sum(s => s.Duration.TotalSeconds);
            return Math.Min(target, (int)seconds);
        }
        int complete;
        try
        {
            complete = new DirectoryInfo(_bufferDir).EnumerateFiles("seg_*.mp4")
                .Count(fi => fi.Length > 1024
                             && fi.LastWriteTimeUtc >= _sessionStartUtc
                             && fi.LastWriteTimeUtc > cutoff
                             && IsCurrentIdentity(fi.Name, identity));
        }
        catch { return 0; }
        // Exclude the one segment currently being written.
        complete = Math.Max(0, complete - 1);
        return Math.Min(target, complete * segSec);
    }

    private static string LocalClock(DateTime utc)
        => utc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <param name="Pins">RAM mode: the picked segments, pinned until the save has copied them out.</param>
    private sealed record ClipScan(BufferMaterial Material, StitchPlan? Plan, MemoryPin? Pins = null);
    private sealed record GifScan(BufferMaterial Material, IReadOnlyList<SegmentCandidate> Pick, MemoryPin? Pins = null);

    /// <summary>
    /// Save the most recent N seconds into a single MP4. Concat demuxer + stream
    /// copy — no re-encoding, instant for short clips, tens of seconds for 30 minutes.
    /// Reentrancy-guarded and stall-bounded (see <see cref="ConcatWatch"/>).
    /// </summary>
    public async Task<string?> SaveLastAsync()
    {
        // Reentrancy guard: a second F9 while a save is in flight must not kill
        // the running concat (which would corrupt the first clip).
        if (Interlocked.CompareExchange(ref _saving, 1, 0) != 0)
        {
            BufferInfo?.Invoke(this, ClipSavePolicy.BusyMessage(_currentSave, SaveKind.Clip));
            SignalFailed(SaveKind.Clip, SaveFailureReason.Busy);
            return null;
        }
        string? listPath = null;
        string? chaptersPath = null;
        MemoryPin? pins = null;
        var temps = new List<string>();
        try
        {
            // One snapshot of the running recording - ring, identity, generation, folder - for the whole save. A warm
            // restart during the scan (Alt-Tab in Auto mode) must not change the answer halfway.
            var epoch = _epoch;
            if (!IsRunning || epoch is null)
            {
                BufferInfo?.Invoke(this, L.T("Buffer läuft gerade nicht (evtl. Neustart) — gleich nochmal probieren.",
                                             "Buffer isn't running right now (possibly restarting) — try again in a moment."));
                SignalFailed(SaveKind.Clip, SaveFailureReason.NotRunning);
                return null;
            }
            bool fromMemory = epoch.Memory is not null;

            // Sized from the RUNNING ring: a duration raised a moment ago (restart still pending)
            // must not make this save reach into the overwrite reserve of the old, smaller ring.
            // The game's own F9 length (or the standard one) is read here, on the pressing thread, and capped by the ring.
            var pressClip = ResolvePressClip();
            int requested = pressClip.Seconds;
            Logger.Info("SaveLast: " + PressClipLength.Describe(pressClip));
            var bufferDir = epoch.BufferDir;
            var nowUtc = DateTime.UtcNow;
            // What is on screen NOW decides the game folder - not what is on screen when the scan is done.
            string? game = ResolveGameAtPress();

            // Directory scan + 360 header reads + planning: never on the UI thread (F9 arrives there).
            var scan = await Task.Run(() => ScanForClip(epoch, requested, nowUtc)).ConfigureAwait(false);
            pins = scan.Pins;
            if (scan.Material == BufferMaterial.Stalled)
            {
                ReportStalled(SaveKind.Clip);
                return null;
            }
            var plan = scan.Plan;
            if (plan is null || plan.Segments.Count == 0)
            {
                Logger.Info("SaveLast: no complete segments yet.");
                BufferInfo?.Invoke(this,
                    L.T("Buffer wurde gerade neu gestartet — noch nicht genug Material. Gleich nochmal.",
                        "Buffer just restarted — not enough material yet. Try again shortly."));
                SignalFailed(SaveKind.Clip, SaveFailureReason.NotEnoughMaterial);
                return null;
            }

            // The clip is a full copy of the footage: refuse before writing when the clips drive cannot hold it. From RAM
            // the footage is written out once more first (temporary .ts next to the clip).
            var o = _settings.Current.Output;
            long need = (long)(plan.TotalBytes * 1.02) + 256L * 1024 * 1024 + (fromMemory ? plan.TotalBytes : 0);
            if (DiskProbe.TryGetFreeSpace(SettingsService.ExpandPath(o.ClipsFolder), out long clipsFree, out string clipsDrive)
                && clipsFree < need)
            {
                BufferError?.Invoke(this, L.T(
                    $"Zu wenig Speicher auf {clipsDrive} für den Clip (braucht ~{DiskPlan.FormatBytes(need)}, frei {DiskPlan.FormatBytes(clipsFree)}).",
                    $"Not enough space on {clipsDrive} for the clip (needs ~{DiskPlan.FormatBytes(need)}, {DiskPlan.FormatBytes(clipsFree)} free)."));
                SignalFailed(SaveKind.Clip, SaveFailureReason.NoDiskSpace);
                return null;
            }

            // Every guard passed: from here on the clip is going to be written.
            SignalStarted(SaveKind.Clip);

            var clipsDir = OutputDirectory(game);
            var outputPath = Path.Combine(clipsDir, $"Clip_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");
            // Written as .part and renamed on success, so the gallery never lists a half-written clip.
            var partPath = ClipOutputFiles.PartPathFor(outputPath);

            int clipSeconds = (int)Math.Ceiling(plan.TotalDuration.TotalSeconds);
            var startedUtc = DateTime.UtcNow;
            _currentSave = new SaveProgressInfo(SaveKind.Clip, clipSeconds, 0, SavePhase.Copying, TimeSpan.Zero);

            // From RAM the picked segments are written out first (one hidden .ts per uninterrupted run, next to the clip),
            // then they are unpinned: the ring records on during the concat without having to hold them.
            IReadOnlyList<SegmentCandidate> inputs = plan.Segments;
            if (fromMemory)
            {
                var lastSignal = DateTime.MinValue;
                inputs = await MemoryClipMaterializer.MaterializeAsync(plan.Segments,
                    r => ClipOutputFiles.TempPathFor(outputPath, $"{r}.ts"), temps, written =>
                    {
                        // Copying out is the first half of the progress, the concat the second.
                        var info = new SaveProgressInfo(SaveKind.Clip, clipSeconds,
                            0.5 * ClipSavePolicy.Fraction(written, plan.TotalBytes), SavePhase.Copying, DateTime.UtcNow - startedUtc);
                        _currentSave = info;
                        if (DateTime.UtcNow - lastSignal < ConcatWatch.PollInterval) return;
                        lastSignal = DateTime.UtcNow;
                        SignalProgress(info);
                    }, _disposeCts.Token).ConfigureAwait(false);
                pins?.Dispose();
                pins = null;
            }

            listPath = fromMemory ? ClipOutputFiles.TempPathFor(outputPath, "list.txt") : Path.Combine(bufferDir, "_concat_list.txt");
            await WriteHelperFileAsync(listPath, string.Join("\n", ClipStitcher.ConcatListLines(inputs)) + "\n", fromMemory)
                .ConfigureAwait(false);

            // Gaps become chapters (ffmetadata is a second ffmpeg input; the concat demuxer has no chapter option).
            string? chapters = null;
            if (plan.Chapters.Count > 0)
            {
                chaptersPath = fromMemory ? ClipOutputFiles.TempPathFor(outputPath, "chapters.txt") : Path.Combine(bufferDir, "_chapters.txt");
                await WriteHelperFileAsync(chaptersPath, ClipStitcher.ChapterMetadata(plan.Chapters), fromMemory).ConfigureAwait(false);
                chapters = chaptersPath;
            }

            bool faststart = ClipSavePolicy.UseFaststart(plan.TotalBytes);
            var args = FFmpegCommandBuilder.BuildConcat(listPath, partPath, faststart, chapters, audioVideoOnly: fromMemory);

            // Waits for the concat as long as its output keeps growing, so a hung process
            // can't block every future save but a slow disk doesn't kill a good clip. Also aborts
            // a copy slower than the recording (the muxer would overwrite segments still unread) - not from RAM: the
            // copies on disk are the save's own. The mp4 comes out a few percent smaller than the .ts input.
            long expectedBytes = fromMemory ? (long)(plan.TotalBytes * 0.97) : plan.TotalBytes;
            var expected = new ConcatExpectation(expectedBytes,
                fromMemory ? 0 : plan.TotalBytes / Math.Max(1.0, plan.TotalDuration.TotalSeconds));
            var run = await RunWatchedFfmpegAsync(args, partPath, clipSeconds, expected: expected,
                onOutputLength: len =>
                {
                    double fraction = ClipSavePolicy.Fraction(len, expectedBytes);
                    var info = new SaveProgressInfo(SaveKind.Clip, clipSeconds,
                        fromMemory ? 0.5 + 0.5 * fraction : fraction,
                        ClipSavePolicy.Phase(len, expectedBytes, faststart),
                        DateTime.UtcNow - startedUtc);
                    _currentSave = info;
                    SignalProgress(info);
                }).ConfigureAwait(false);

            if (run.AbortedBy is { } why)
            {
                ClipOutputFiles.Discard(partPath);
                Logger.Warn($"SaveLast: concat aborted ({why}) after {plan.Segments.Count} segments — output discarded.");
                BufferError?.Invoke(this, L.T("Clip-Erstellung hat zu lange gedauert und wurde abgebrochen.",
                                              "Clip creation took too long and was aborted."));
                SignalFailed(SaveKind.Clip, SaveFailureReason.Aborted);
                return null;
            }

            int exitCode = run.ExitCode;
            var outcome = await ClipOutputFiles.FinishAsync(partPath, outputPath, exitCode).ConfigureAwait(false);
            if (outcome == FinishOutcome.RenameFailed)
            {
                // The clip itself is fine and still on disk as .part - tell the user where, not "concat code 0".
                BufferError?.Invoke(this, L.T($"Clip gespeichert, aber Umbenennen fehlgeschlagen: {partPath}",
                                              $"Clip saved, but renaming failed: {partPath}"));
                SignalFailed(SaveKind.Clip, SaveFailureReason.RenameFailed);
                return null;
            }
            if (outcome == FinishOutcome.Published)
            {
                // F9 used to leave no trace at all, which turned one bad clip into file
                // forensics. One line is enough to answer "what did it actually grab".
                long outBytes = new FileInfo(outputPath).Length;
                double ageSec = (DateTime.UtcNow - plan.Segments[^1].EndUtc).TotalSeconds;
                Logger.Info($"SaveLast: {plan.Segments.Count} segments ≈{plan.TotalDuration.TotalSeconds:0}s gaps={plan.Gaps.Count} faststart={faststart} " +
                            $"(newest {ageSec:0.0}s old) → {Path.GetFileName(outputPath)} ({outBytes / (1024 * 1024)} MB)");
                ReplaySaved?.Invoke(this, new ReplaySavedInfo(outputPath, plan.TotalDuration.TotalSeconds,
                    requested, game, plan.Gaps, outBytes));
                return outputPath;
            }
            else
            {
                BufferError?.Invoke(this, L.T($"Clip-Erstellung fehlgeschlagen (concat code {exitCode}).",
                                              $"Clip creation failed (concat code {exitCode})."));
                SignalFailed(SaveKind.Clip, SaveFailureReason.Failed);
                return null;
            }
        }
        catch (Exception ex)
        {
            // An IO error (clips folder, concat list) used to surface only as a log line from the hotkey handler.
            Logger.Warn("SaveLast: unexpected error: " + ex.Message);
            SignalFailed(SaveKind.Clip, SaveFailureReason.Failed);
            throw;
        }
        finally
        {
            try { if (listPath != null && File.Exists(listPath)) File.Delete(listPath); } catch { }
            try { if (chaptersPath != null && File.Exists(chaptersPath)) File.Delete(chaptersPath); } catch { }
            pins?.Dispose();
            DeleteTemps(temps);
            _currentSave = null;
            Interlocked.Exchange(ref _saving, 0);
            PruneAfterSave();
        }
    }

    /// <summary>A prune that was blocked by the save runs now; off the caller's thread, the folder can hold hundreds of files.</summary>
    private void PruneAfterSave() => _ = Task.Run(PruneOldSegments);

    /// <summary>
    /// The clip length of this press and where it came from: the game's own length (profile of
    /// <see cref="GameExeAtPress"/>) or the standard length when no game is recognised, overridden by a positive
    /// <see cref="ClipSecondsAtPress"/>, and never more than the lookback the ring actually holds
    /// (<see cref="EffectiveDurationSeconds"/>). The ring itself is sized for <see cref="RingLookbackSeconds"/>.
    /// </summary>
    internal PressClip ResolvePressClip()
    {
        int ring = EffectiveDurationSeconds;
        string? exe = null;
        if (GameExeAtPress is { } getExe)
        {
            try { exe = getExe(); }
            catch (Exception ex) { Logger.Warn("Game at press failed: " + ex.Message); }
        }

        var clip = PressClipLength.Resolve(ring, _settings.Current, exe);
        if (ClipSecondsAtPress is { } hook)
        {
            try
            {
                int v = hook();
                if (v > 0) clip = PressClipLength.WithRequested(clip, ring, v);
            }
            catch (Exception ex) { Logger.Warn("Clip length at press failed: " + ex.Message); }
        }
        return clip;
    }

    /// <summary>The length this press saves (see <see cref="ResolvePressClip"/>).</summary>
    internal int RequestedSecondsAtPress() => ResolvePressClip().Seconds;

    /// <summary>
    /// A concat list / chapter file. Next to a RAM save's output it is hidden; any earlier file of that name goes first
    /// (a hidden file cannot be overwritten in place). UTF-8 without BOM: chapter titles carry umlauts.
    /// </summary>
    private static async Task WriteHelperFileAsync(string path, string content, bool hidden)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)).ConfigureAwait(false);
        if (hidden) MemoryClipMaterializer.TryHide(path);
    }

    private static void DeleteTemps(List<string> temps)
    {
        foreach (var t in temps)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try { if (File.Exists(t)) File.Delete(t); break; }
                catch { Thread.Sleep(100); }   // a killed ffmpeg may still hold it for a moment
            }
        }
        temps.Clear();
    }

    /// <summary>
    /// Names the game folder for a capture of the current plan. Set by the composition root;
    /// without it (or with sorting off) everything lands in the clips folder as before.
    /// </summary>
    public Func<CaptureTargetResolver.CapturePlan?, string>? GameFolderFor { get; set; }

    /// <summary>
    /// The game folder, resolved at press time: the footage is the past seconds, and what is on screen
    /// when F9 is hit is the best evidence of which game that was. Evaluated exactly once per save.
    /// </summary>
    private string? ResolveGameAtPress()
    {
        if (!_settings.Current.Output.SortByGame || GameFolderFor is not { } f) return null;
        try { return f(CurrentPlan); }
        catch (Exception ex)
        {
            Logger.Warn("Game folder lookup failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>Clip/GIF target folder for a game folder resolved earlier by <see cref="ResolveGameAtPress"/>.</summary>
    private string OutputDirectory(string? game)
    {
        var o = _settings.Current.Output;
        return GameFolderNaming.PrepareDirectory(o.ClipsFolder, o.SortByGame,
            GameFolderFor is not null ? () => game : null);
    }

    /// <summary>
    /// The ring stopped turning. Refusing is better than handing out an old clip that looks
    /// real, and a restart is the only recovery — the ffmpeg process is still alive, so
    /// nothing times out or crashes on its own.
    /// </summary>
    private void ReportStalled(SaveKind kind)
    {
        Logger.Warn("Replay buffer is stale — no new completed segment. Refusing the clip and restarting.");
        BufferError?.Invoke(this, L.T(
            "Der Buffer liefert keine neuen Daten mehr — der Clip wäre veraltet. Buffer wird neu gestartet.",
            "The buffer stopped producing data — the clip would be stale. Restarting the buffer."));
        SignalFailed(kind, SaveFailureReason.Stale);
        RequestRestart();
    }

    /// <summary>
    /// True when the ring has stopped producing completed segments. Deliberately measured on
    /// the newest COMPLETED segment: the open one keeps its timestamp fresh while it grows,
    /// so it would report health right up to a 46 MB segment nobody can use.
    /// </summary>
    private bool IsStalled(out TimeSpan age)
    {
        age = TimeSpan.Zero;
        if (!IsRunning || string.IsNullOrEmpty(_bufferDir)) return false;
        try
        {
            int segSec = Math.Max(1, _settings.Current.ReplayBuffer.SegmentDurationSeconds);
            var stale = BufferHealth.StaleAfter(segSec);

            // A fresh generation needs time to complete its first segment.
            var sinceStart = DateTime.UtcNow - _genStartedUtc;
            if (sinceStart < stale + TimeSpan.FromSeconds(segSec)) return false;

            var identity = _currentIdentity;   // read once: not once per file
            if (_epoch?.Memory is { } mem)
            {
                // The RAM ring holds complete segments only: the newest one is the measure.
                var newest = mem.Snapshot().LastOrDefault(s => IsIdentity(s.Generation, identity) && s.EndUtc >= _genStartedUtc);
                age = newest is null ? sinceStart : DateTime.UtcNow - newest.EndUtc;
                return age > stale;
            }
            var times = new DirectoryInfo(_bufferDir).EnumerateFiles("seg_*.mp4")
                .Where(fi => fi.Length > 1024 && IsCurrentIdentity(fi.Name, identity))
                .Select(fi => fi.LastWriteTimeUtc)
                .OrderBy(t => t)
                .ToList();

            // Fewer than two files after the grace period means nothing ever completed.
            if (times.Count < 2) { age = sinceStart; return true; }

            age = DateTime.UtcNow - times[^2];
            return age > stale;
        }
        catch { return false; }
    }

    /// <summary>
    /// The complete segments of the identity in <paramref name="epoch"/>, oldest→newest: everything the ring holds,
    /// session-filtered, without the segment ffmpeg is still writing (no moov atom yet). Everything that depends on
    /// "the running recording" - identity, generation, folder, ring - comes from the one snapshot.
    /// </summary>
    private (List<SegmentCandidate> Complete, BufferMaterial Material) CollectComplete(BufferEpoch epoch, DateTime nowUtc)
    {
        var ring = epoch.Ring;
        if (epoch.Memory is not null)
            throw new InvalidOperationException("RAM epochs are collected with CollectCompleteMemory");
        var files = new DirectoryInfo(epoch.BufferDir).EnumerateFiles("seg_*.mp4")
            .Where(fi => fi.Length > 1024
                         && fi.LastWriteTimeUtc >= _sessionStartUtc
                         && IsCurrentIdentity(fi.Name, epoch.Identity))
            .OrderBy(fi => fi.LastWriteTimeUtc)
            .ToList();

        // The newest file is the one being written - but only if it belongs to the running generation. Dropping
        // the newest unconditionally threw away a good segment of the previous generation right after a restart.
        if (files.Count > 0 && SegmentName.TryParseGeneration(files[^1].Name, out int newestGen) && newestGen == epoch.Generation)
            files.RemoveAt(files.Count - 1);
        if (files.Count == 0) return (new List<SegmentCandidate>(), BufferMaterial.NotEnoughYet);

        // _sessionStartUtc is the only other time filter, and after hours of uptime it lets
        // everything through. A ring that stopped turning therefore used to be served as
        // "the last N seconds": one real case handed out a single 15-hour-old segment, and
        // did so identically on every press because nothing on disk changed any more.
        if (BufferHealth.Judge(files[^1].LastWriteTimeUtc, nowUtc, Math.Max(1, ring.SegmentSeconds)) == BufferMaterial.Stalled)
            return (new List<SegmentCandidate>(), BufferMaterial.Stalled);

        var list = files.Select(fi =>
        {
            SegmentName.TryParseGeneration(fi.Name, out int g);
            return new SegmentCandidate(fi.FullName, g, fi.LastWriteTimeUtc, fi.Length, null);
        }).ToList();
        return (list, BufferMaterial.Ok);
    }

    /// <summary>
    /// The RAM counterpart of <see cref="CollectComplete"/>: the complete segments of the epoch's identity, session-
    /// filtered, oldest first - all pinned in one step with the snapshot, so nothing the scan looks at can be evicted
    /// before the save narrows the pin to what it picked. The returned pin is null when the material is not usable.
    /// </summary>
    private (List<SegmentCandidate> Complete, BufferMaterial Material, MemoryPin? Pin) CollectCompleteMemory(
        BufferEpoch epoch, MemorySegmentStore mem, DateTime nowUtc)
    {
        var pin = mem.SnapshotPinned();
        var segments = pin.Segments
            .Where(s => s.EndUtc >= _sessionStartUtc && IsIdentity(s.Generation, epoch.Identity))
            .ToList();
        if (segments.Count == 0)
        {
            pin.Dispose();
            return (new List<SegmentCandidate>(), BufferMaterial.NotEnoughYet, null);
        }
        if (BufferHealth.Judge(segments[^1].EndUtc, nowUtc, Math.Max(1, epoch.Ring.SegmentSeconds)) == BufferMaterial.Stalled)
        {
            pin.Dispose();
            return (new List<SegmentCandidate>(), BufferMaterial.Stalled, null);
        }
        var list = segments.Select(s => new SegmentCandidate($"ram:{s.Generation}:{s.Sequence}", s.Generation, s.EndUtc,
            s.Bytes, s.Duration) { Memory = s }).ToList();
        return (list, BufferMaterial.Ok, pin);
    }

    /// <summary>Complete segments of either store; RAM ones come pinned (see <see cref="CollectCompleteMemory"/>).</summary>
    private (List<SegmentCandidate> Complete, BufferMaterial Material, MemoryPin? Pin) CollectAny(BufferEpoch epoch, DateTime nowUtc)
    {
        if (epoch.Memory is { } mem) return CollectCompleteMemory(epoch, mem, nowUtc);
        var (complete, material) = CollectComplete(epoch, nowUtc);
        return (complete, material, null);
    }

    /// <summary>Swaps the broad scan pin for one on the picked segments only.</summary>
    private static MemoryPin? NarrowPin(BufferEpoch epoch, MemoryPin? broad, IEnumerable<SegmentCandidate> picked)
    {
        if (broad is null || epoch.Memory is not { } mem) return null;
        var narrow = mem.Pin(picked.Where(c => c.Memory is not null).Select(c => c.Memory!));
        broad.Dispose();
        return narrow;
    }

    /// <summary>
    /// Picks and plans a clip: with stitching on, everything inside the lookback window (holes become gaps with
    /// chapters), otherwise only the newest uninterrupted run. Runs on a worker thread.
    /// </summary>
    private ClipScan ScanForClip(BufferEpoch epoch, int requestedSeconds, DateTime nowUtc)
    {
        var (complete, material, broad) = CollectAny(epoch, nowUtc);
        try { return PlanClip(epoch, requestedSeconds, nowUtc, complete, material, broad); }
        catch
        {
            broad?.Dispose();   // pinned segments must never outlive a failed scan
            throw;
        }
    }

    private ClipScan PlanClip(BufferEpoch epoch, int requestedSeconds, DateTime nowUtc, List<SegmentCandidate> complete,
        BufferMaterial material, MemoryPin? broad)
    {
        var ring = epoch.Ring;
        if (material == BufferMaterial.Stalled) return new ClipScan(BufferMaterial.Stalled, null);
        if (complete.Count == 0) return new ClipScan(BufferMaterial.NotEnoughYet, null);

        IReadOnlyList<SegmentCandidate> selected;
        if (_settings.Current.ReplayBuffer.StitchAcrossGaps)
        {
            selected = ClipStitcher.SelectWindow(complete, nowUtc, requestedSeconds, ring.PickCount);
        }
        else
        {
            // Only the newest uninterrupted run counts as "just now".
            int start = BufferHealth.ContiguousTailStart(complete.Select(c => c.EndUtc).ToList(), ring.SegmentSeconds);
            selected = complete.Skip(start).TakeLast(ring.PickForSeconds(requestedSeconds)).ToList();
        }

        // The real durations drive the chapters and the saved length; an unreadable header (segment still open) drops the
        // segment. RAM segments know theirs from the key frame timestamps.
        var withDurations = selected
            .Select(c => c.Memory is not null ? c : c with { Duration = Mp4Duration.TryRead(c.Path, out var d) ? d : null })
            .ToList();

        var current = epoch.Identity;
        var generations = _generations.Values.ToList();
        var suspensions = SuspensionsSnapshot();
        var plan = ClipStitcher.Plan(withDurations,
            (from, to) => ClipStitcher.ClassifyGap(from, to, nowUtc, current, generations, suspensions),
            (run, from, to, gap) => ClipStitcher.ChapterTitle(run, from, to, gap, LocalClock));
        return new ClipScan(BufferMaterial.Ok, plan, NarrowPin(epoch, broad, plan.Segments));
    }

    /// <summary>The newest uninterrupted run, capped to <paramref name="neededSegments"/> (a GIF never jumps across a gap).</summary>
    private GifScan ScanForGif(BufferEpoch epoch, int neededSegments, DateTime nowUtc)
    {
        var ring = epoch.Ring;
        var (complete, material, broad) = CollectAny(epoch, nowUtc);
        if (material != BufferMaterial.Ok) return new GifScan(material, Array.Empty<SegmentCandidate>());

        int start = BufferHealth.ContiguousTailStart(complete.Select(c => c.EndUtc).ToList(), ring.SegmentSeconds);
        var pick = complete.Skip(start).TakeLast(Math.Max(1, neededSegments)).ToList();
        return new GifScan(BufferMaterial.Ok, pick, NarrowPin(epoch, broad, pick));
    }

    /// <summary>
    /// Save the last few seconds of the buffer as a looping GIF. Shares the save
    /// reentrancy guard with <see cref="SaveLastAsync"/> (both drive one concat ffmpeg).
    /// Two steps: stream-copy the covering segments into a temp mp4, then GIF its last
    /// N seconds via a palette pass.
    /// </summary>
    public async Task<string?> SaveGifAsync()
    {
        if (Interlocked.CompareExchange(ref _saving, 1, 0) != 0)
        {
            BufferInfo?.Invoke(this, ClipSavePolicy.BusyMessage(_currentSave, SaveKind.Gif));
            SignalFailed(SaveKind.Gif, SaveFailureReason.Busy);
            return null;
        }
        string? tempMp4 = null;
        string? listPath = null;
        MemoryPin? pins = null;
        var temps = new List<string>();
        try
        {
            var epoch = _epoch;   // one snapshot of the running recording for the whole save (see SaveLastAsync)
            if (!IsRunning || epoch is null)
            {
                BufferInfo?.Invoke(this, L.T("Buffer läuft gerade nicht — gleich nochmal.", "Buffer isn't running right now — try again shortly."));
                SignalFailed(SaveKind.Gif, SaveFailureReason.NotRunning);
                return null;
            }

            var g = _settings.Current.Gif;
            int gifSec = Math.Clamp(g.DurationSeconds, 1, 30);
            var ring = epoch.Ring;   // the running ffmpeg's ring, not what the settings say now
            int segSec = ring.SegmentSeconds;
            // +1 so the trim has enough material; never more than the ring hands out, or the
            // oldest segment would sit inside the overwrite reserve.
            int neededSegments = ring.ClampPick((int)Math.Ceiling((double)gifSec / segSec) + 1);
            var bufferDir = epoch.BufferDir;
            var nowUtc = DateTime.UtcNow;
            string? game = ResolveGameAtPress();

            var scan = await Task.Run(() => ScanForGif(epoch, neededSegments, nowUtc)).ConfigureAwait(false);
            pins = scan.Pins;
            if (scan.Material == BufferMaterial.Stalled)
            {
                ReportStalled(SaveKind.Gif);
                return null;
            }
            var pick = scan.Pick;
            if (pick.Count == 0)
            {
                BufferInfo?.Invoke(this, L.T("Noch nicht genug Material für ein GIF. Gleich nochmal.",
                                             "Not enough material for a GIF yet. Try again shortly."));
                SignalFailed(SaveKind.Gif, SaveFailureReason.NotEnoughMaterial);
                return null;
            }

            SignalStarted(SaveKind.Gif);

            var clipsDir = OutputDirectory(game);
            var outputPath = Path.Combine(clipsDir, $"Gif_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.gif");
            var partPath = ClipOutputFiles.PartPathFor(outputPath);

            // 1) concat the covering segments into a temp mp4 (stream copy, instant). From RAM the segments are written
            //    out first, like for a clip, and every helper file sits next to the GIF (hidden, deleted afterwards).
            bool fromMemory = epoch.Memory is not null;
            if (fromMemory)
            {
                var runs = await MemoryClipMaterializer.MaterializeAsync(pick,
                    r => ClipOutputFiles.TempPathFor(outputPath, $"{r}.ts"), temps, null, _disposeCts.Token).ConfigureAwait(false);
                pins?.Dispose();
                pins = null;
                tempMp4 = ClipOutputFiles.TempPathFor(outputPath, "src");
                listPath = ClipOutputFiles.TempPathFor(outputPath, "list.txt");
                await WriteHelperFileAsync(listPath, string.Join("\n", ClipStitcher.ConcatListLines(runs)) + "\n", hidden: true)
                    .ConfigureAwait(false);
            }
            else
            {
                tempMp4 = Path.Combine(bufferDir, "_gif_src.mp4");
                listPath = Path.Combine(bufferDir, "_gif_list.txt");
                await File.WriteAllLinesAsync(listPath,
                    pick.Select(c => $"file '{c.Path.Replace("'", @"'\''")}'")).ConfigureAwait(false);
            }
            var concat = await RunWatchedFfmpegAsync(FFmpegCommandBuilder.BuildConcat(listPath, tempMp4, audioVideoOnly: fromMemory),
                tempMp4, pick.Count * segSec,
                expected: fromMemory ? null : ConcatExpectation.FromSegments(pick.Select(f => f.Bytes).ToList(), segSec)).ConfigureAwait(false);
            if (!concat.Succeeded || !File.Exists(tempMp4))
            {
                if (concat.AbortedBy is { } concatWhy)
                    Logger.Warn($"SaveGif: concat aborted ({concatWhy}) — GIF not created.");
                BufferError?.Invoke(this, L.T("GIF-Erstellung fehlgeschlagen (Concat).", "GIF creation failed (concat)."));
                SignalFailed(SaveKind.Gif, concat.AbortedBy is null ? SaveFailureReason.Failed : SaveFailureReason.Aborted);
                return null;
            }

            // 2) last gifSec seconds of the temp mp4 → gif. palettegen needs every frame before
            // it emits the first byte, so the output is silent for the whole analysis pass: the
            // 20 s stall rule would kill a healthy encode, only the absolute cap applies here.
            var encode = await RunWatchedFfmpegAsync(
                FFmpegCommandBuilder.BuildGif(tempMp4, partPath, gifSec, g.Framerate, g.Width),
                partPath, gifSec, stallLimit: ConcatWatch.SafetyCap(gifSec)).ConfigureAwait(false);
            if (encode.AbortedBy is { } why)
            {
                ClipOutputFiles.Discard(partPath);
                Logger.Warn($"SaveGif: encode aborted ({why}) — output discarded.");
                BufferError?.Invoke(this, L.T("GIF-Erstellung fehlgeschlagen.", "GIF creation failed."));
                SignalFailed(SaveKind.Gif, SaveFailureReason.Aborted);
                return null;
            }

            var outcome = await ClipOutputFiles.FinishAsync(partPath, outputPath, encode.ExitCode).ConfigureAwait(false);
            if (outcome == FinishOutcome.RenameFailed)
            {
                BufferError?.Invoke(this, L.T($"GIF gespeichert, aber Umbenennen fehlgeschlagen: {partPath}",
                                              $"GIF saved, but renaming failed: {partPath}"));
                SignalFailed(SaveKind.Gif, SaveFailureReason.RenameFailed);
                return null;
            }
            if (outcome != FinishOutcome.Published)
            {
                BufferError?.Invoke(this, L.T("GIF-Erstellung fehlgeschlagen.", "GIF creation failed."));
                SignalFailed(SaveKind.Gif, SaveFailureReason.Failed);
                return null;
            }

            GifSaved?.Invoke(this, outputPath);
            return outputPath;
        }
        catch (Exception ex)
        {
            Logger.Warn("SaveGif: unexpected error: " + ex.Message);
            SignalFailed(SaveKind.Gif, SaveFailureReason.Failed);
            throw;
        }
        finally
        {
            try { if (listPath != null && File.Exists(listPath)) File.Delete(listPath); } catch { }
            try { if (tempMp4 != null && File.Exists(tempMp4)) File.Delete(tempMp4); } catch { }
            pins?.Dispose();
            DeleteTemps(temps);
            Interlocked.Exchange(ref _saving, 0);
            PruneAfterSave();
        }
    }

    /// <summary>Test seam: the ffmpeg.exe the one-shot concat/gif runs use (null = the usual lookup).</summary>
    internal string? FfmpegPathOverride { get; set; }

    /// <summary>Outcome of <see cref="RunWatchedFfmpegAsync"/>. AbortedBy is set when we killed ffmpeg.</summary>
    private readonly record struct WatchedRun(int ExitCode, ConcatVerdict? AbortedBy)
    {
        public bool Succeeded => AbortedBy is null && ExitCode == 0;
    }

    /// <summary>
    /// Runs a one-shot ffmpeg (concat/gif) on the shared concat slot and waits for it while
    /// <paramref name="outputPath"/> keeps growing (<see cref="ConcatWatch"/>). A hung process
    /// is killed; a slow but working one is left alone. Never throws for a missing ffmpeg.
    /// <paramref name="onOutputLength"/> gets the sampled output size once per poll (progress).
    /// </summary>
    private async Task<WatchedRun> RunWatchedFfmpegAsync(string args, string outputPath, int clipSeconds,
        TimeSpan? stallLimit = null, ConcatExpectation? expected = null, Action<long>? onOutputLength = null)
    {
        _concatFfmpeg?.Dispose();
        _concatFfmpeg = new FFmpegService(FfmpegPathOverride);
        if (!_concatFfmpeg.IsAvailable()) return new WatchedRun(-1, null);
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _concatFfmpeg.Exited += (_, code) => tcs.TrySetResult(code);
        _concatFfmpeg.Start(args);

        var watch = new ConcatWatch(DateTime.UtcNow, clipSeconds, stallLimit, expected);
        while (true)
        {
            var done = await Task.WhenAny(tcs.Task, Task.Delay(ConcatWatch.PollInterval)).ConfigureAwait(false);
            if (done == tcs.Task)
                return new WatchedRun(await tcs.Task.ConfigureAwait(false), null);

            long length = OutputLength(outputPath);
            if (onOutputLength is not null)
            {
                try { onOutputLength(length); }
                catch (Exception ex) { Logger.Warn("Save progress handler threw: " + ex.Message); }
            }
            var verdict = watch.Observe(DateTime.UtcNow, length);
            if (verdict != ConcatVerdict.Progressing)
            {
                try { _concatFfmpeg?.Dispose(); } catch { }
                return new WatchedRun(-1, verdict);
            }
        }
    }

    /// <summary>
    /// Current size of a file another process is still writing. Asks the open handle rather than
    /// the directory entry, which NTFS updates lazily for a file that is being written - a stale
    /// size would read as "stopped growing" and abort a healthy concat.
    /// </summary>
    private static long OutputLength(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return fs.Length;
        }
        catch (FileNotFoundException) { return 0; }
        catch (DirectoryNotFoundException) { return 0; }
        catch
        {
            try { return new FileInfo(path).Length; } catch { return 0; }
        }
    }

    /// <summary>
    /// The fast companion of the watchdog, every second: RAM mode checks the free RAM (the 30 s watchdog would be late
    /// when a game loads), disk mode keeps the ring hidden except for the slots ffmpeg reopens next (<see cref="SegmentVisibility"/>).
    /// </summary>
    private void StartMaintenance(CancellationToken token)
    {
        _ = Task.Run(async () =>
        {
            var nextGuard = DateTime.UtcNow + MemoryGuardInterval;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                    var epoch = _epoch;
                    if (epoch is null) continue;
                    if (epoch.Memory is not null)
                    {
                        // The one place the running buffer checks the free RAM (the 30 s watchdog does not).
                        if (DateTime.UtcNow < nextGuard) continue;
                        nextGuard = DateTime.UtcNow + MemoryGuardInterval;
                        GuardMemory();
                    }
                    else
                    {
                        SegmentVisibility.Apply(epoch.BufferDir, epoch.Generation, epoch.Ring.Wrap, epoch.Ring.SegmentSeconds);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Logger.Warn("Buffer maintenance failed: " + ex.Message); }
            }
        }, token);
    }

    /// <summary>
    /// Low-RAM guard of the RAM ring: below the safety floor (max(1.5 GiB, 10 %) free) the ring gives back the difference,
    /// oldest footage first, and the lookback shortens - one toast per session - instead of the game running out of memory.
    /// The freed blocks are collected at once (otherwise the OS sees nothing freed), and a second cut needs both a settle
    /// time and a gen-2 collection since the last one - without that hysteresis the guard kept cutting every tick for memory
    /// that was already free but not yet collected. The budget is never raised again within the run; a cut below the
    /// configured length is remembered, and Auto uses the disk at the next cold start (<see cref="StorageInputs"/>).
    /// Runs from one loop at a time (maintenance while recording, the pause guard while suspended).
    /// </summary>
    private void GuardMemory()
    {
        if (Interlocked.CompareExchange(ref _guarding, 1, 0) != 0) return;
        try
        {
            var mem = _memory;
            if (mem is null) return;
            var now = DateTime.UtcNow;
            if (_memoryCutGen2 >= 0 && (now - _memoryCutUtc < MemoryCutSettle || GC.CollectionCount(2) <= _memoryCutGen2))
                return;
            if (PhysicalMemory.Read() is not { } ram) return;
            var s = _settings.Current;
            int seg = Math.Max(1, _runningRing.SegmentSeconds);
            long video = FFmpegCommandBuilder.MaxEffectiveBitrate(s);
            int tracks = ExpectedAudioTracks(s);
            long minimum = BufferStoragePlanner.EstimateRingBytes(DurationText.MinSeconds, seg, video, tracks);
            if (BufferStoragePlanner.ShrinkBudget(mem.BudgetBytes, mem.BytesInUse, ram, minimum) is not { } budget) return;

            long freed = mem.SetBudget(budget);
            mem.TrimPool();
            _memoryCutUtc = now;
            _memoryCutGen2 = GC.CollectionCount(2);
            CollectFreedMemory();

            int seconds = BufferStoragePlanner.LookbackForBytes(budget, RingLookbackSeconds, seg, video, tracks);
            int cap = _memoryCapSeconds;
            if (cap == 0 || seconds < cap) _memoryCapSeconds = seconds;
            _memoryLimited = true;
            if (EffectiveDurationSeconds < RingLookbackSeconds) _record.WriteLowMemory(now);
            Logger.Warn($"RAM buffer: only {DiskPlan.FormatBytes(ram.AvailableBytes)} RAM free (floor {DiskPlan.FormatBytes(BufferStoragePlanner.SafetyFloorBytes(ram.TotalBytes))}) - " +
                        $"budget {DiskPlan.FormatBytes(budget)}, freed {DiskPlan.FormatBytes(freed)}, lookback {DurationText.Format(EffectiveDurationSeconds)}");
            if (_lowMemoryAnnounced) return;
            _lowMemoryAnnounced = true;
            BufferInfo?.Invoke(this, L.T(
                $"Wenig freier Arbeitsspeicher — der Replay-Buffer gibt RAM frei und hält nur noch {DurationText.Format(EffectiveDurationSeconds)} Rückblick, damit das Spiel genug hat. Beim nächsten Start nutzt „Automatisch“ die Festplatte.",
                $"Low free RAM — the replay buffer gives memory back and keeps only {DurationText.Format(EffectiveDurationSeconds)} of lookback so the game has enough. “Automatic” uses the disk from the next start on."));
        }
        finally { Interlocked.Exchange(ref _guarding, 0); }
    }

    private void StartWatchdog()
    {
        _watchdogCts = new CancellationTokenSource();
        var token = _watchdogCts.Token;
        StartMaintenance(token);
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);

                    // A wedged ffmpeg does not exit, so nothing else here would ever notice:
                    // the process lives, the open segment keeps growing, and every F9 then
                    // returns the same stale footage. Restarting is the only way back.
                    if (IsStalled(out var stalledFor))
                    {
                        // A full drive stalls ffmpeg too - restarting it in a loop would only make that worse.
                        if (DiskProbe.TryGetFreeSpace(_bufferDir, out long freeStalled, out string driveStalled)
                            && freeStalled < DiskPlan.CriticalBytes)
                        {
                            await StopForFullDiskAsync(driveStalled).ConfigureAwait(false);
                            return;
                        }
                        Logger.Warn($"Replay buffer stalled: no completed segment for {stalledFor.TotalSeconds:0}s — restarting.");
                        BufferError?.Invoke(this, L.T(
                            $"Buffer lieferte {stalledFor.TotalSeconds:0}s lang keine neuen Daten — wurde neu gestartet.",
                            $"Buffer produced no new data for {stalledFor.TotalSeconds:0}s — restarted."));
                        await RestartIfRunningAsync().ConfigureAwait(false);
                        return;   // the restart starts a fresh watchdog
                    }

                    PruneOldSegments();
                    // Something of the user's appeared in our hidden folder: it becomes visible again.
                    SegmentVisibility.UpdateFolder(_bufferDir, IsOurFolder(_bufferDir));

                    // The RAM ring has a hard budget and no files; its free-RAM guard runs in the maintenance loop.
                    if (_epoch?.Memory is not null) continue;

                    // A 30-minute ring is gigabytes: watch the drive, not just the ring.
                    if (await GuardDiskAsync().ConfigureAwait(false)) return;

                    long total = 0, largest = 0;
                    int count = 0;
                    foreach (var f in Directory.EnumerateFiles(_bufferDir, "seg_*.mp4"))
                    {
                        try
                        {
                            long len = new FileInfo(f).Length;
                            total += len;
                            largest = Math.Max(largest, len);
                            count++;
                        }
                        catch { }
                    }
                    // The limit follows the same keep rule as prune: a fixed byte cap restarted a
                    // perfectly healthy ring every 30 s once it held two rings of high-bitrate
                    // footage. Only prune failing to keep up can exceed this.
                    var keep = EffectiveKeep();
                    if (BufferHealth.IsRunaway(total, keep, largest))
                    {
                        long limit = BufferHealth.RunawayLimitBytes(keep, largest);
                        Logger.Warn($"Replay buffer holds {total / (1024 * 1024)} MiB in {count} segments, over the ring limit of " +
                                    $"{limit / (1024 * 1024)} MiB (keep={keep}, largest segment {largest / (1024 * 1024)} MiB) — prune is not keeping up, restarting.");
                        BufferError?.Invoke(this, L.T(
                            $"Buffer-Ordner ist auf {total / (1024 * 1024)} MiB angewachsen (Limit {limit / (1024 * 1024)} MiB) — wird neu gestartet.",
                            $"Buffer folder grew to {total / (1024 * 1024)} MiB (limit {limit / (1024 * 1024)} MiB) — restarting."));
                        await RestartIfRunningAsync().ConfigureAwait(false);
                        return;
                    }
                }
                catch (OperationCanceledException) { return; }
                catch { /* ignore */ }
            }
        }, token);
    }

    /// <summary>Stops the buffer for good (Enabled stays as the user set it) and says why.</summary>
    private async Task StopForFullDiskAsync(string drive)
    {
        Logger.Warn($"Replay buffer: drive {drive} is almost full - stopping the buffer.");
        await StopAsync().ConfigureAwait(false);
        BufferError?.Invoke(this, FullDiskMessage(drive));
    }

    /// <summary>
    /// Runtime disk guard, once per watchdog tick. Critical free space stops the buffer; low free space shortens
    /// the lookback to what still fits and restarts (the warm restart prunes with the smaller window). The decision
    /// uses the OBSERVED segment size, so an underestimated bitrate cannot loop shrink→same cap.
    /// Returns true when it stopped or restarted the buffer (the watchdog then ends).
    /// </summary>
    private async Task<bool> GuardDiskAsync()
    {
        if (!DiskProbe.TryGetFreeSpace(_bufferDir, out long free, out string drive)) return false;

        if (free < DiskPlan.CriticalBytes)
        {
            await StopForFullDiskAsync(drive).ConfigureAwait(false);
            return true;
        }
        if (free >= DiskPlan.LowSpaceBytes) return false;

        int seg = Math.Max(1, _runningRing.SegmentSeconds);
        int current = EffectiveDurationSeconds;
        int gen = _generation;
        long videoBps = FFmpegCommandBuilder.EffectiveBitrate(_settings.Current, CurrentPlan?.MonitorIndex ?? 0);
        long segBytes = DiskPlan.SegmentBytes(DiskPlan.EstimateSegmentBytes(seg, videoBps),
            DiskPlan.ObservedBytesPerSecond(RecentSamples(g => g.Generation == gen, 12)), seg);
        var d = DiskPlan.Decide(current, seg, segBytes, free + DiskProbe.OwnSegmentBytes(_bufferDir));

        if (d.CannotStart)
        {
            await StopForFullDiskAsync(drive).ConfigureAwait(false);
            return true;
        }
        if (d.EffectiveSeconds >= current) return false;

        _diskCapSeconds = d.EffectiveSeconds;
        _diskLimited = true;
        _lastAnnouncedCap = d.EffectiveSeconds;
        Logger.Warn($"Replay buffer: only {DiskPlan.FormatBytes(free)} free on {drive} - shortening the lookback to {DurationText.Format(d.EffectiveSeconds)}.");
        BufferInfo?.Invoke(this, L.T(
            $"Wenig Speicher auf {drive} — Rückblick auf {DurationText.Format(d.EffectiveSeconds)} gekürzt (frei {DiskPlan.FormatBytes(free)}).",
            $"Low space on {drive} — lookback shortened to {DurationText.Format(d.EffectiveSeconds)} ({DiskPlan.FormatBytes(free)} free)."));
        await RestartIfRunningAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Deletes what can never be part of a clip again (see <see cref="SegmentRetention"/>) and, as a backstop,
    /// anything beyond the keep count. This is what bounds disk usage now that restarts no longer wipe the directory:
    /// the previous generation goes as soon as it is unusable instead of staying next to the current ring.
    /// </summary>
    private void PruneOldSegments()
    {
        // The RAM ring follows the same retention rules; pins protect what a running save still copies.
        if (_memory is { } mem && _currentIdentity is { } memIdentity)
        {
            var memCtx = new RetentionContext(DateTime.UtcNow, _sessionStartUtc,
                SegmentRetention.AgeWindowSeconds(_runningRing, CurrentRingPlan()),
                _generation, memIdentity, _generations, User32.IsWindow);
            mem.Prune(s => SegmentRetention.ShouldDelete(s.Generation, s.EndUtc, memCtx));
        }
        if (string.IsNullOrEmpty(_bufferDir)) return;
        // A save's concat opens its input files one after another; deleting one it has not
        // reached yet (the settings were shortened meanwhile) would break or splice the clip.
        if (Volatile.Read(ref _saving) != 0) return;
        try
        {
            var files = new DirectoryInfo(_bufferDir).EnumerateFiles("seg_*.mp4").ToList();
            var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (_currentIdentity is { } identity)
            {
                var ctx = new RetentionContext(DateTime.UtcNow, _sessionStartUtc,
                    SegmentRetention.AgeWindowSeconds(_runningRing, CurrentRingPlan()),
                    _generation, identity, _generations, User32.IsWindow);
                foreach (var fi in files)
                {
                    if (!SegmentRetention.ShouldDelete(new SegmentFileFacts(fi.Name, fi.LastWriteTimeUtc), ctx)) continue;
                    try { fi.Delete(); deleted.Add(fi.FullName); } catch { }
                }
            }

            // Keep the window we might save plus a full extra generation of slack.
            int keep = EffectiveKeep();
            foreach (var fi in files.Where(f => !deleted.Contains(f.FullName))
                                    .OrderByDescending(f => f.LastWriteTimeUtc)
                                    .Skip(keep))
            {
                try { fi.Delete(); } catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// True when the segment file belongs to a generation with the CURRENT identity (same window+size /
    /// monitor+size and the same encoding). Filenames are seg_{gen}_{NN}.mp4.
    /// </summary>
    private bool IsCurrentIdentity(string fileName, SegmentIdentity? id)
    {
        if (id is null) return true;
        return SegmentName.TryParseGeneration(fileName, out int gen) && IsIdentity(gen, id);
    }

    /// <summary>True when generation <paramref name="gen"/> recorded <paramref name="id"/> (null = any).</summary>
    private bool IsIdentity(int gen, SegmentIdentity? id)
        => id is null || (_generations.TryGetValue(gen, out var info) && info.Identity == id);

    private RingPlan CurrentRingPlan()
    {
        var rb = _settings.Current.ReplayBuffer;
        return RingPlan.Create(EffectiveDurationSeconds, rb.SegmentDurationSeconds);
    }

    /// <summary>Keep count for prune and the size watchdog: the larger of running and configured ring.</summary>
    private int EffectiveKeep() => RingPlan.KeepFor(_runningRing, CurrentRingPlan());

    /// <summary>
    /// A save interrupted by a crash or kill leaves "Clip_*.mp4.part" / "Gif_*.gif.part" behind.
    /// Sweeps those once per cold start; off the lifecycle lock because the clips folder can be big.
    /// </summary>
    private void CleanStaleClipParts()
    {
        var clipsDir = SettingsService.ExpandPath(_settings.Current.Output.ClipsFolder);
        // The first cold start of the process removes RAM-save helper files (.wkitmp.*, gigabytes of hidden .ts) of ANY
        // age: the app is single-instance (App's session mutex) and no save of this process can have run before its first
        // start, so every such file is an orphan of a crash. Later cold starts keep the age limit - a save may be running.
        bool anyAge = !_tempSweepDone && Volatile.Read(ref _saving) == 0;
        _tempSweepDone = true;
        _ = Task.Run(() =>
        {
            try
            {
                int n = ClipOutputFiles.DeleteStaleParts(clipsDir, DateTime.UtcNow, tempsOfAnyAge: anyAge);
                if (n > 0) Logger.Info($"ReplayBuffer: removed {n} stale partial clip file(s) from '{clipsDir}'");
            }
            catch (Exception ex) { Logger.Warn("Stale partial clip cleanup failed: " + ex.Message); }
        });
    }

    private static void ClearBufferDir(string dir) => BufferFolderRules.DeleteOwnFiles(dir);

    public void Dispose()
    {
        CancelDebounce();
        try { _disposeCts.Cancel(); } catch { }
        _watchdogCts?.Cancel();
        _ffmpeg?.Dispose();
        _concatFfmpeg?.Dispose();
        _videoPipe?.Dispose();
        _wgc?.Dispose();
        _memoryFeed?.Dispose();
        _memoryFeed = null;
        StopSuspendGuard();
        ReleaseMemory();
    }
}
