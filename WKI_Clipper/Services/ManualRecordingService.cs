using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using WKI_Clipper.Models;
using WKI_Clipper.Native;

namespace WKI_Clipper.Services;

public sealed class ManualRecordingService : IDisposable
{
    private readonly SettingsService _settings;
    private FFmpegService? _ffmpeg;
    private AudioPipeService? _audio;

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

        // Start the audio pipe FIRST so the named pipe exists before ffmpeg
        // tries to open it. If audio init fails, build the ffmpeg command
        // WITHOUT the pipe input so recording still works (silent video).
        int? gamePid = null;
        if (_settings.Current.Audio.SystemCaptureMode == AudioCaptureMode.GameOnly)
        {
            // Try the watcher's known PID first
            gamePid = App.Host?.GameWatcher?.CurrentPid;

            // If no watcher PID and no fixed process name → auto-detect foreground
            if (gamePid == null && string.IsNullOrEmpty(_settings.Current.Audio.GameProcessName))
            {
                var hwnd = User32.GetForegroundWindow();
                if (hwnd != IntPtr.Zero)
                {
                    User32.GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid > 0) gamePid = (int)pid;
                    Logger.Info($"ManualRecording: auto-detected foreground PID {pid}");
                }
            }

            // If fixed name but watcher didn't find it, try direct lookup
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
                Logger.Info($"ManualRecording: GameOnly mode, target PID {gamePid}");
            else
                Logger.Info("ManualRecording: GameOnly but no process found, fallback to AllAudio");
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
                Logger.Warn("ManualRecording: audio pipe failed to start, continuing with video only. " + _audio.LastError);
                _audio.Dispose();
                _audio = null;
            }
        }

        string? windowTitle = null;
        if (_settings.Current.Video.CaptureSource == CaptureSource.ActiveWindow)
        {
            windowTitle = ResolveForegroundWindowTitle();
            if (string.IsNullOrEmpty(windowTitle))
            {
                Logger.Warn("CaptureSource=ActiveWindow but foreground window has no title — falling back to Display");
            }
            else
            {
                Logger.Info($"Window capture target: {windowTitle}");
            }
        }

        var args = FFmpegCommandBuilder.Build(_settings.Current, path,
            segmentOutput: false, audioPipeArgs: audioArgs, captureWindowTitle: windowTitle);
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
        // Capture the own instance so a stale exit (after a new recording began)
        // can't fire events for the wrong session.
        var ownFfmpeg = _ffmpeg;
        var ownAudio = _audio;
        _ffmpeg.Exited += (_, code) =>
        {
            if (_ffmpeg != ownFfmpeg) return;

            if (_audio == ownAudio)
            {
                ownAudio?.Dispose();
                _audio = null;
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
                    ? "Aufnahme unvollständig — die Datei ist defekt und wurde verworfen."
                    : $"Aufnahme fehlgeschlagen (ffmpeg code {code}). Schau ins Log.";
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

    public void Dispose()
    {
        _ffmpeg?.Dispose();
        _audio?.Dispose();
    }

    private static string? ResolveForegroundWindowTitle()
    {
        var hwnd = User32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        int len = User32.GetWindowTextLength(hwnd);
        if (len <= 0) return null;
        var sb = new StringBuilder(len + 1);
        User32.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}

/// <summary>Outcome of a finished manual recording.</summary>
public readonly record struct RecordingResult(string Path, bool Success, string? Error);
