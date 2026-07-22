using System;
using System.IO;
using System.Threading.Tasks;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

public sealed class ManualRecordingService : IDisposable
{
    private readonly SettingsService _settings;
    private FFmpegService? _ffmpeg;
    private AudioPipeService? _audio;
    private WgcWindowCapture? _wgc;
    private VideoPipeService? _videoPipe;

    public string? CurrentOutputPath { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public bool IsRecording => _ffmpeg?.IsRunning ?? false;

    public event EventHandler<string>? RecordingStarted;
    /// <summary>Fired once when a recording ends — carries whether the file is usable.</summary>
    public event EventHandler<RecordingResult>? RecordingStopped;
    public event EventHandler<string>? FFmpegLog;

    public ManualRecordingService(SettingsService settings)
    {
        _settings = settings;
    }

    public string Start()
    {
        if (IsRecording) throw new InvalidOperationException("Recording already in progress.");

        var clipsDir = SettingsService.ExpandPath(_settings.Current.Output.ClipsFolder);
        Directory.CreateDirectory(clipsDir);
        var filename = $"Rec_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";
        var path = Path.Combine(clipsDir, filename);

        // Same resolver as the buffer: in Auto mode the recording pins the window
        // that is active at Strg+F9 (occlusion-proof WGC) and STAYS on it until
        // the recording is stopped — switching windows afterwards changes nothing.
        var plan = CaptureTargetResolver.Resolve(_settings.Current.Capture, _settings.Current);
        Logger.Info($"ManualRecording target: video='{plan.VideoLabel}', audio='{plan.AudioLabel}' (monitorIdx={plan.MonitorIndex}, pid={plan.AudioPid?.ToString() ?? "null"})");

        // Start the audio pipe FIRST so the named pipe exists before ffmpeg opens
        // it. If audio init fails, build the ffmpeg command WITHOUT the pipe input
        // so recording still works (silent video).
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

        var args = FFmpegCommandBuilder.Build(_settings.Current, path,
            segmentOutput: false, audioPipeArgs: audioArgs, monitorIndex: plan.MonitorIndex,
            videoInputArgs: videoArgs);
        Logger.Info("[rec-ffmpeg] CMD: " + args);

        _ffmpeg = new FFmpegService();
        if (!_ffmpeg.IsAvailable())
        {
            _audio?.Dispose();
            throw new FileNotFoundException("ffmpeg.exe not found. Place it in Assets/ffmpeg/ or add to PATH.", _ffmpeg.FFmpegPath);
        }

        _ffmpeg.StdErrLine += (_, line) =>
        {
            if (!string.IsNullOrWhiteSpace(line)) Logger.Info("[rec-ffmpeg] " + line);
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

            // A usable clip must exist and be non-trivial (a failed ffmpeg leaves
            // a 0-byte / moov-less stub). We requested the stop → exit -1 from the
            // kill-on-timeout is fine as long as the file is valid.
            bool fileValid = false;
            try { fileValid = File.Exists(path) && new FileInfo(path).Length > 8 * 1024; } catch { }
            bool userStopped = ownFfmpeg.StopRequested;
            bool success = fileValid && (userStopped || code == 0);

            CurrentOutputPath = null;
            StartedAt = null;

            if (success)
            {
                RecordingStopped?.Invoke(this, new RecordingResult(path, true, null));
            }
            else
            {
                if (!fileValid)
                {
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                }
                string err = userStopped
                    ? L.T("Aufnahme unvollständig — die Datei ist defekt und wurde verworfen.",
                          "Recording incomplete — the file is broken and was discarded.")
                    : L.T($"Aufnahme fehlgeschlagen (ffmpeg code {code}). Schau ins Log.",
                          $"Recording failed (ffmpeg code {code}). Check the log.");
                RecordingStopped?.Invoke(this, new RecordingResult(path, false, err));
            }
        };

        _ffmpeg.Start(args);
        CurrentOutputPath = path;
        StartedAt = DateTime.Now;
        RecordingStarted?.Invoke(this, path);
        return path;
    }

    public async Task StopAsync()
    {
        if (_ffmpeg is null || !_ffmpeg.IsRunning) return;
        await _ffmpeg.StopAsync(TimeSpan.FromSeconds(10));
    }

    public async Task ToggleAsync()
    {
        if (IsRecording) await StopAsync();
        else Start();
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
        _ffmpeg?.Dispose();
        _audio?.Dispose();
        _videoPipe?.Dispose();
        _wgc?.Dispose();
    }
}

/// <summary>Outcome of a finished manual recording.</summary>
public readonly record struct RecordingResult(string Path, bool Success, string? Error);
