using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

public sealed class ManualRecordingService : IDisposable
{
    // A failed ffmpeg leaves a 0-byte / moov-less stub; anything smaller is not a usable recording.
    private const long MinValidBytes = 8 * 1024;

    private readonly SettingsService _settings;
    private FFmpegService? _ffmpeg;
    private AudioTrackSet? _audio;
    private WgcWindowCapture? _wgc;
    private VideoPipeService? _videoPipe;

    // Guards the parts of one recording: the part that runs, the pending next part, the end.
    private readonly object _gate = new();
    // The automatic continuations after a display mode switch: the same bounds as the buffer's restarts (R2, R8).
    private readonly AutoRestartLedger _continuations = new();
    private CaptureTargetResolver.CapturePlan? _plan;   // the running (or last) part's plan
    private string? _firstPath;                         // part 1's file; the next parts are named after it
    private int _part;                                  // number of the running (or last) part
    private PendingPart? _pending;                      // a mode switch ended a part; the next one starts soon (also read without _gate)

    public string? CurrentOutputPath { get; private set; }
    public DateTime? StartedAt { get; private set; }

    /// <summary>
    /// True from Ctrl+F9 until the recording ends - also in the short pause between two parts after a display mode switch
    /// (a Ctrl+F9 then stops it instead of starting a second recording).
    /// </summary>
    public bool IsRecording => (_ffmpeg?.IsRunning ?? false) || Volatile.Read(ref _pending) is not null;

    public event EventHandler<string>? RecordingStarted;
    /// <summary>Fired once when a recording ends — carries whether the file is usable.</summary>
    public event EventHandler<RecordingResult>? RecordingStopped;
    /// <summary>
    /// A display mode switch ended a part; it is kept and the recording goes on in the next part (raised once that one
    /// runs). <see cref="RecordingStopped"/> comes only when the whole recording ends.
    /// </summary>
    public event EventHandler<RecordingPart>? RecordingContinued;
    public event EventHandler<string>? FFmpegLog;

    public ManualRecordingService(SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>Names the game folder for a recording of the given plan (set by the composition root).</summary>
    public Func<CaptureTargetResolver.CapturePlan?, string>? GameFolderFor { get; set; }

    /// <summary>When the last display change happened (set by the composition root); null = none known. An ffmpeg exit soon after it is a mode switch.</summary>
    public Func<DateTime?>? LastDisplayChangeUtc { get; set; }

    /// <summary>The display topology plans are resolved on; null = <see cref="DisplayTopologyCache.Shared"/> (tests set their own).</summary>
    internal DisplayTopologyCache? Topology { get; set; }

    /// <summary>How long the display gets to settle before the next part starts.</summary>
    internal TimeSpan ContinueDelay { get; set; } = DisplayRestartPolicy.RestartDelay;

    /// <summary>Tests: replaces starting the capture of a part (gets its plan and file); the test then plays ffmpeg's end.</summary>
    internal Action<CaptureTargetResolver.CapturePlan, string>? StartPartOverride { get; set; }

    public string Start()
    {
        string path;
        lock (_gate)
        {
            if (IsRecording) throw new InvalidOperationException("Recording already in progress.");

            // Same resolver as the buffer: in Auto mode the recording pins the window
            // that is active at Strg+F9 (occlusion-proof WGC) and STAYS on it until
            // the recording is stopped — switching windows afterwards changes nothing.
            // Resolved BEFORE the path: the plan's pinned window also names the game folder.
            var plan = CaptureTargetResolver.Resolve(_settings.Current.Capture, _settings.Current, Topology);

            var o = _settings.Current.Output;
            var clipsDir = GameFolderNaming.PrepareDirectory(o.ClipsFolder, o.SortByGame,
                GameFolderFor is { } f ? () => f(plan) : null);
            var filename = $"Rec_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";
            path = Path.Combine(clipsDir, filename);

            StartPart(plan, path);
            _firstPath = path;
            _part = 1;
            CurrentOutputPath = path;
            StartedAt = DateTime.Now;
        }
        RecordingStarted?.Invoke(this, path);
        return path;
    }

    /// <summary>The file of part <paramref name="part"/> (2, 3, …) next to part 1: "Rec_…_Teil2.mp4".</summary>
    internal static string PartPath(string firstPath, int part)
        => Path.Combine(Path.GetDirectoryName(firstPath) ?? "", $"{Path.GetFileNameWithoutExtension(firstPath)}_Teil{part}.mp4");

    /// <summary>Starts audio, video and ffmpeg for one part into <paramref name="path"/>. Assumes _gate is held.</summary>
    private void StartPart(CaptureTargetResolver.CapturePlan plan, string path)
    {
        _plan = plan;
        if (StartPartOverride is { } fake)
        {
            fake(plan, path);
            return;
        }
        Logger.Info($"ManualRecording target: video='{plan.VideoLabel}', audio='{plan.AudioLabel}' (output={plan.Source.OutputIndex}, pid={plan.AudioPid?.ToString() ?? "null"})");
        if (plan.Warning is { } warning)
            Logger.Warn($"ManualRecording: {warning}{Environment.NewLine}{TopologyNow().Dump()}");

        // Start the audio pipe FIRST so the named pipe exists before ffmpeg opens
        // it. If audio init fails, build the ffmpeg command WITHOUT the pipe input
        // so recording still works (silent video).
        _audio = new AudioTrackSet(_settings.Current, plan.SysMode, plan.AudioPid);
        IReadOnlyList<string>? audioArgs = null;
        if (_audio.HasAnyAudio())
        {
            bool ok = _audio.Start();
            if (ok)
            {
                audioArgs = _audio.PipeArgs;
                if (_audio.Separate) Logger.Info("ManualRecording: separate mic track (2 audio streams)");
            }
            else
            {
                Logger.Warn("ManualRecording: audio pipe failed to start, continuing with video only. " + _audio.LastError);
                _audio.Dispose();
                _audio = null;
            }
        }

        // Occlusion-proof WGC window capture when the plan targets a window;
        // falls back to monitor ddagrab on any failure.
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
                    // Window closed/resized mid-recording: stop gracefully so the
                    // file stays playable up to this point.
                    Logger.Info($"ManualRecording: WGC-Ziel ungültig ({reason}) — Aufnahme wird sauber beendet");
                    _ = StopAsync();
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

        var settings = _settings.Current;
        string capturePath = videoArgs != null
            ? "wgc"
            : FFmpegCommandBuilder.UsesAmfNativeCapture(settings, rawInput: false, plan.Source.AmfIndex) ? "amf" : "dda";
        Logger.Info(CaptureSourceRules.LogLine(
            settings.Capture.Mode == CaptureMode.Monitor ? MonitorRef.FromProfile(settings.Capture) : null,
            plan.Source, TopologyNow(), capturePath));

        var args = FFmpegCommandBuilder.Build(settings, path,
            segmentOutput: false, audioPipeArgs: audioArgs, source: plan.Source,
            videoInputArgs: videoArgs);
        Logger.Info("[rec-ffmpeg] CMD: " + args);

        _ffmpeg = new FFmpegService();
        if (!_ffmpeg.IsAvailable())
        {
            _audio?.Dispose();
            _audio = null;
            _videoPipe?.Dispose();
            _videoPipe = null;
            _wgc?.Dispose();
            _wgc = null;
            throw new FileNotFoundException("ffmpeg.exe not found. Place it in Assets/ffmpeg/ or add to PATH.", _ffmpeg.FFmpegPath);
        }

        // Why a part ended is in its last lines: a display mode switch leaves ddagrab's errors there.
        var stderrTail = new StderrTail(DisplayRestartPolicy.StderrLines);
        _ffmpeg.StdErrLine += (_, line) =>
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                Logger.Info("[rec-ffmpeg] " + line);
                stderrTail.Add(line);
            }
            FFmpegLog?.Invoke(this, line);
        };
        // Capture the own instances so a stale exit (after a new recording began)
        // can't fire events for the wrong session.
        var ownFfmpeg = _ffmpeg;
        var ownAudio = _audio;
        var ownWgc = _wgc;
        var ownVideoPipe = _videoPipe;
        _ffmpeg.Exited += (_, code) =>
        {
            if (_ffmpeg != ownFfmpeg) return;

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

            bool stopRequested = ownFfmpeg.StopRequested;
            // The pipe may still hold the last lines when the process is already gone.
            if (!stopRequested) ownFfmpeg.WaitForStdErrEnd(TimeSpan.FromMilliseconds(500));
            OnPartExited(path, code, stopRequested, stderrTail.Text);
        };

        _ffmpeg.Start(args);
    }

    /// <summary>
    /// The ffmpeg of the part recording into <paramref name="path"/> ended. A usable file the user asked to stop (or that
    /// ffmpeg finished by itself) ends the recording as a success, as before. An end the display explains - a mode-change
    /// error in <paramref name="recentStderr"/> or a display change just before (R2) - keeps the part and continues in the
    /// next one after <see cref="ContinueDelay"/>, at most <see cref="DisplayRestartPolicy.MaxRestarts"/> times per
    /// <see cref="DisplayRestartPolicy.RestartWindow"/>. Anything else fails as before.
    /// </summary>
    internal PartExitAction OnPartExited(string path, int code, bool stopRequested, string? recentStderr)
    {
        // A usable clip must exist and be non-trivial. We requested the stop → exit -1
        // from the kill-on-timeout is fine as long as the file is valid.
        bool fileValid = false;
        try { fileValid = File.Exists(path) && new FileInfo(path).Length > MinValidBytes; } catch { }

        RecordingResult result;
        PartExitAction action;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var lastChange = LastDisplayChangeUtc?.Invoke();
            if (fileValid && !stopRequested && _continuations.TryTake(recentStderr, lastChange, now, out int attempt))
            {
                ScheduleNextPart(path, attempt);
                return PartExitAction.Continues;
            }
            if (fileValid && !stopRequested && DisplayRestartPolicy.IsDisplayCaused(recentStderr, lastChange, now))
                Logger.Warn($"ManualRecording: ffmpeg ended after a display change, but {DisplayRestartPolicy.MaxRestarts} parts " +
                            $"within {DisplayRestartPolicy.RestartWindow.TotalSeconds:0} s already - not continuing again");

            bool success = fileValid && (stopRequested || code == 0);
            if (success)
            {
                result = new RecordingResult(path, true, null);
                action = PartExitAction.Saved;
            }
            else
            {
                if (!fileValid)
                {
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                }
                string err = stopRequested
                    ? L.T("Aufnahme unvollständig — die Datei ist defekt und wurde verworfen.",
                          "Recording incomplete — the file is broken and was discarded.")
                    : L.T($"Aufnahme fehlgeschlagen (ffmpeg code {code}). Schau ins Log.",
                          $"Recording failed (ffmpeg code {code}). Check the log.");
                if (_part > 1)
                    err += L.T(" Die Teile davor sind gespeichert.", " The parts before it are saved.");
                result = new RecordingResult(path, false, err);
                action = PartExitAction.Failed;
            }
            EndRecording();
        }
        RecordingStopped?.Invoke(this, result);
        return action;
    }

    /// <summary>The part in <paramref name="keptPath"/> is kept; the next one starts after the display settled. Assumes _gate is held.</summary>
    private void ScheduleNextPart(string keptPath, int attempt)
    {
        int part = _part + 1;
        var pending = new PendingPart(new CancellationTokenSource(), keptPath, PartPath(_firstPath ?? keptPath, part), part);
        Volatile.Write(ref _pending, pending);
        Logger.Info($"ManualRecording: ffmpeg ended on a display change - part {_part} kept ({Path.GetFileName(keptPath)}), " +
                    $"part {part} starts in {ContinueDelay.TotalMilliseconds:0} ms ({attempt}/{DisplayRestartPolicy.MaxRestarts})");
        var delay = ContinueDelay;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, pending.Cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            StartNextPart(pending);
        });
    }

    private void StartNextPart(PendingPart pending)
    {
        RecordingPart? continued = null;
        RecordingResult? ended = null;
        lock (_gate)
        {
            if (_pending != pending || pending.Cts.IsCancellationRequested) return;
            try
            {
                // Same window and audio as before (a recording stays on what it pinned); the monitor at its new mode.
                var plan = CaptureTargetResolver.Reresolve(_plan!.Value, _settings.Current.Capture, Topology);
                StartPart(plan, pending.NextPath);
                _part = pending.Part;
                CurrentOutputPath = pending.NextPath;
                continued = new RecordingPart(pending.KeptPath, pending.NextPath, pending.Part);
            }
            catch (Exception ex)
            {
                Logger.Warn($"ManualRecording: part {pending.Part} could not start - the recording ends with part {pending.Part - 1}: {ex.Message}");
                ended = new RecordingResult(pending.KeptPath, true, null,
                    L.T($"Nach dem Auflösungswechsel ging es nicht weiter: {ex.Message}",
                        $"Could not continue after the resolution change: {ex.Message}"));
                EndRecording();
            }
            // Only now: until the next part runs (or the recording ended) a Ctrl+F9 must see a recording.
            Volatile.Write(ref _pending, null);
        }
        if (continued is { } c) RecordingContinued?.Invoke(this, c);
        if (ended is { } e) RecordingStopped?.Invoke(this, e);
    }

    /// <summary>The recording is over. Assumes _gate is held.</summary>
    private void EndRecording()
    {
        CurrentOutputPath = null;
        StartedAt = null;
        _firstPath = null;
        _part = 0;
    }

    private DisplayTopology TopologyNow() => (Topology ?? DisplayTopologyCache.Shared).Get();

    public async Task StopAsync()
    {
        RecordingResult? ended = null;
        lock (_gate)
        {
            if (_pending is { } pending)
            {
                // Between two parts nothing runs: the kept part is the end of the recording.
                pending.Cts.Cancel();
                Volatile.Write(ref _pending, null);
                ended = new RecordingResult(pending.KeptPath, true, null);
                EndRecording();
            }
        }
        if (ended is { } result)
        {
            RecordingStopped?.Invoke(this, result);
            return;
        }

        var ffmpeg = _ffmpeg;
        if (ffmpeg is null || !ffmpeg.IsRunning) return;
        await ffmpeg.StopAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Awaited before a recording starts, so the composition root can free the capture
    /// hardware. AMF's capture component is exclusive: a second instance fails with
    /// "Failed to initialize capture component", and even on the older ddagrab path two
    /// simultaneous capture+encode pipelines were the biggest single cost to in-game FPS.
    /// </summary>
    public Func<Task>? SuspendCompetingCapture { get; set; }

    /// <summary>
    /// Called when a recording could not start after <see cref="SuspendCompetingCapture"/> (no monitor, no ffmpeg): what
    /// stepped aside comes back. A recording that ran ends with <see cref="RecordingStopped"/> instead.
    /// </summary>
    public Action? ResumeCompetingCapture { get; set; }

    public async Task ToggleAsync()
    {
        if (IsRecording) { await StopAsync(); return; }

        if (SuspendCompetingCapture is { } suspend)
        {
            // A failure here must not block the recording — worst case both run, as before.
            try { await suspend().ConfigureAwait(false); }
            catch (Exception ex) { Logger.Warn("Could not free the capture for recording: " + ex.Message); }
        }
        try
        {
            Start();
        }
        catch
        {
            // Nothing records: the paused buffer must not stay paused.
            try { ResumeCompetingCapture?.Invoke(); }
            catch (Exception ex) { Logger.Warn("Could not bring the capture back: " + ex.Message); }
            throw;
        }
    }

    /// <summary>
    /// Swap the RUNNING recording's system-audio source in place (e.g. the game
    /// launched mid-recording) — video keeps rolling untouched.
    /// </summary>
    public void RetargetAudio(SystemAudioMode mode, int? pid)
    {
        if (!IsRecording) return;
        if (_audio?.SwapSystemSource(mode, pid) == true)
            Logger.Info($"ManualRecording: audio retargeted in place → mode={mode}, pid={pid?.ToString() ?? "null"}");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _pending?.Cts.Cancel();
            Volatile.Write(ref _pending, null);
        }
        _ffmpeg?.Dispose();
        _audio?.Dispose();
        _videoPipe?.Dispose();
        _wgc?.Dispose();
    }

    /// <summary>A part ended by a mode switch: kept in <paramref name="KeptPath"/>; part <paramref name="Part"/> goes to <paramref name="NextPath"/>.</summary>
    private sealed record PendingPart(CancellationTokenSource Cts, string KeptPath, string NextPath, int Part);
}

/// <summary>Outcome of a finished manual recording.</summary>
/// <param name="Note">Something worth knowing about a usable recording (e.g. why it ended earlier than asked).</param>
public readonly record struct RecordingResult(string Path, bool Success, string? Error, string? Note = null);

/// <summary>
/// A display mode switch ended a recording's part: it is kept in <paramref name="KeptPath"/> and the recording goes on as
/// part <paramref name="Part"/> in <paramref name="NextPath"/>.
/// </summary>
public readonly record struct RecordingPart(string KeptPath, string NextPath, int Part)
{
    public string Message => L.T($"Auflösung gewechselt – Aufnahme läuft in Teil {Part} weiter",
                                 $"Resolution changed – recording continues in part {Part}");
}

/// <summary>What the end of a recording's part led to.</summary>
internal enum PartExitAction
{
    /// <summary>The recording ended with a usable file.</summary>
    Saved,
    /// <summary>The recording failed, as before.</summary>
    Failed,
    /// <summary>A display mode switch: the part is kept and the next one is scheduled.</summary>
    Continues
}
