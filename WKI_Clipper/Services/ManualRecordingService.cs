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
    public event EventHandler<string>? RecordingStopped;
    public event EventHandler<string>? RecordingError;
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
        _audio = new AudioPipeService(_settings.Current);
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
        _ffmpeg.Exited += (_, code) =>
        {
            if (code != 0 && code != 255 /* SIGINT */)
                RecordingError?.Invoke(this, $"FFmpeg exited with code {code}");
            _audio?.Dispose();
            _audio = null;
            RecordingStopped?.Invoke(this, path);
            CurrentOutputPath = null;
            StartedAt = null;
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
