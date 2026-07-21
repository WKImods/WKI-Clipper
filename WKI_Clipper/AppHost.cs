using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.Views;

namespace WKI_Clipper;

/// <summary>
/// Simple service composition root — no DI container needed at this size.
/// </summary>
public sealed class AppHost : IDisposable
{
    public SettingsService Settings { get; }
    public HotkeyService Hotkeys { get; }
    public ManualRecordingService ManualRecording { get; }
    public ReplayBufferService ReplayBuffer { get; }
    public ScreenshotService Screenshots { get; }
    public AudioDeviceService AudioDevices { get; }
    public FFmpegService FFmpeg { get; }
    public GameProcessWatcher? GameWatcher { get; private set; }

    /// <summary>
    /// Codecs the local ffmpeg.exe actually supports. Populated in Initialize.
    /// Keys: ffmpeg codec name, Values: German display label.
    /// </summary>
    public IReadOnlyList<CodecInfo> AvailableCodecs { get; private set; } = Array.Empty<CodecInfo>();

    public AppHost()
    {
        Settings = new SettingsService();
        Settings.Load();

        FFmpeg = new FFmpegService();
        Hotkeys = new HotkeyService(Settings);
        ManualRecording = new ManualRecordingService(Settings);
        ReplayBuffer = new ReplayBufferService(Settings);
        Screenshots = new ScreenshotService(Settings);
        AudioDevices = new AudioDeviceService();

        ResolveDefaultAudioDevices();
    }

    private void ResolveDefaultAudioDevices()
    {
        try
        {
            bool dirty = false;
            if (string.IsNullOrEmpty(Settings.Current.Audio.MicDeviceId) || Settings.Current.Audio.MicDeviceId == "default")
            {
                var def = AudioDevices.GetDefaultMicrophone();
                if (def != null)
                {
                    Settings.Current.Audio.MicDeviceId = def.Name;
                    Logger.Info($"Default microphone resolved: {def.Name}");
                    dirty = true;
                }
            }
            if (string.IsNullOrEmpty(Settings.Current.Audio.SystemDeviceId) || Settings.Current.Audio.SystemDeviceId == "default")
            {
                var def = AudioDevices.GetDefaultRenderDevice();
                if (def != null)
                {
                    Settings.Current.Audio.SystemDeviceId = def.Name;
                    Logger.Info($"Default render device resolved: {def.Name}");
                    dirty = true;
                }
            }
            if (dirty) Settings.Save();
        }
        catch (Exception ex)
        {
            Logger.Error("ResolveDefaultAudioDevices failed", ex);
        }
    }

    public void Initialize()
    {
        Hotkeys.Initialize();
        // Codec detection runs async; not blocking startup.
        _ = DetectCodecsAsync();
        StartGameWatcherIfNeeded();
    }

    /// <summary>
    /// Starts or restarts the GameProcessWatcher based on current settings.
    /// Called at init and whenever the user changes GameOnly settings.
    /// </summary>
    public void StartGameWatcherIfNeeded()
    {
        GameWatcher?.Dispose();
        GameWatcher = null;

        var audio = Settings.Current.Audio;
        Logger.Info($"StartGameWatcherIfNeeded: mode={audio.SystemCaptureMode}, processName='{audio.GameProcessName ?? "(null)"}'");

        if (audio.SystemCaptureMode == AudioCaptureMode.GameOnly
            && !string.IsNullOrEmpty(audio.GameProcessName))
        {
            var gameName = audio.GameProcessName;
            GameWatcher = new GameProcessWatcher(gameName);
            GameWatcher.ProcessFound += pid =>
            {
                Logger.Info($"Game process found: {gameName} (PID {pid}) — restarting buffer");
                ReplayBuffer.RequestRestart();
                if (Settings.Current.Behavior.ShowToastNotifications)
                    ToastService.Show(ToastKind.Info,
                        "Spiel erkannt",
                        $"{gameName} (PID {pid}) — nur Game-Audio aktiv");
            };
            GameWatcher.ProcessLost += () =>
            {
                Logger.Info("Game process lost — reverting to all audio, restarting buffer");
                ReplayBuffer.RequestRestart();
                if (Settings.Current.Behavior.ShowToastNotifications)
                    ToastService.Show(ToastKind.Info,
                        "Spiel beendet",
                        $"{gameName} — alle Sounds aktiv");
            };
            // Start() does an immediate synchronous check before starting the poll loop
            GameWatcher.Start();
        }
        else
        {
            Logger.Info("StartGameWatcherIfNeeded: no watcher needed (AllAudio or no process name)");
        }
    }

    private async Task DetectCodecsAsync()
    {
        try
        {
            if (!FFmpeg.IsAvailable())
            {
                Logger.Warn("ffmpeg.exe not found — codec detection skipped");
                AvailableCodecs = new[] { new CodecInfo("libx264", "H.264 (CPU)") };
                return;
            }

            var detected = new List<CodecInfo>();
            // AMD AMF
            if (await FFmpeg.HasEncoderAsync("h264_amf"))   detected.Add(new CodecInfo("h264_amf",   "H.264 (AMD GPU)"));
            if (await FFmpeg.HasEncoderAsync("hevc_amf"))   detected.Add(new CodecInfo("hevc_amf",   "H.265 / HEVC (AMD GPU)"));
            if (await FFmpeg.HasEncoderAsync("av1_amf"))    detected.Add(new CodecInfo("av1_amf",    "AV1 (AMD GPU)"));
            // NVIDIA NVENC
            if (await FFmpeg.HasEncoderAsync("h264_nvenc")) detected.Add(new CodecInfo("h264_nvenc", "H.264 (NVIDIA GPU)"));
            if (await FFmpeg.HasEncoderAsync("hevc_nvenc")) detected.Add(new CodecInfo("hevc_nvenc", "H.265 / HEVC (NVIDIA GPU)"));
            if (await FFmpeg.HasEncoderAsync("av1_nvenc"))  detected.Add(new CodecInfo("av1_nvenc",  "AV1 (NVIDIA GPU, RTX 4000+)"));
            // Intel Quick Sync
            if (await FFmpeg.HasEncoderAsync("h264_qsv"))   detected.Add(new CodecInfo("h264_qsv",   "H.264 (Intel Quick Sync)"));
            if (await FFmpeg.HasEncoderAsync("hevc_qsv"))   detected.Add(new CodecInfo("hevc_qsv",   "H.265 / HEVC (Intel Quick Sync)"));
            if (await FFmpeg.HasEncoderAsync("av1_qsv"))    detected.Add(new CodecInfo("av1_qsv",    "AV1 (Intel Arc / Quick Sync)"));
            // CPU fallback
            detected.Add(new CodecInfo("libx264", "H.264 (CPU)"));
            detected.Add(new CodecInfo("libx265", "H.265 / HEVC (CPU)"));
            AvailableCodecs = detected;

            // If user's saved codec doesn't exist on this machine, fall back to the
            // first available one. Persists.
            bool currentExists = false;
            foreach (var c in detected) if (c.FFmpegName == Settings.Current.Video.Codec) { currentExists = true; break; }
            if (!currentExists && detected.Count > 0)
            {
                Logger.Warn($"Saved codec '{Settings.Current.Video.Codec}' not available — falling back to '{detected[0].FFmpegName}'");
                Settings.Current.Video.Codec = detected[0].FFmpegName;
                Settings.Save();
            }
            Logger.Info("Codec detection done: " + string.Join(", ", detected.ConvertAll(c => c.FFmpegName)));
        }
        catch (Exception ex)
        {
            Logger.Error("DetectCodecsAsync failed", ex);
        }
    }

    public void Dispose()
    {
        GameWatcher?.Dispose();
        Hotkeys.Dispose();
        ManualRecording.Dispose();
        ReplayBuffer.Dispose();
        FFmpeg.Dispose();
    }
}

public sealed record CodecInfo(string FFmpegName, string Label);
