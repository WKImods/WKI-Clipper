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
    public PerformanceMonitorService Performance { get; }
    public GalleryMetaService GalleryMeta { get; }
    public ObsWebSocketService Obs { get; }
    public TwitchChatService Chat { get; }
    public MusicPlayerService Music { get; }
    public CrosshairLibraryService Crosshairs { get; }

    /// <summary>
    /// Set by the widget host: re-applies the crosshair overlay (image, sliders,
    /// visibility) after settings changed. Keeps the UI-owning window out of AppHost.
    /// </summary>
    public Action? CrosshairRefresh { get; set; }

    /// <summary>Re-render the live crosshair overlay from current settings.</summary>
    public void RefreshCrosshair() => CrosshairRefresh?.Invoke();
    public GameProcessWatcher? GameWatcher { get; private set; }
    public ForegroundTracker? Foreground { get; private set; }

    /// <summary>
    /// Codecs the local ffmpeg.exe actually supports. Populated in Initialize.
    /// Keys: ffmpeg codec name, Values: German display label.
    /// </summary>
    public IReadOnlyList<CodecInfo> AvailableCodecs { get; private set; } = Array.Empty<CodecInfo>();

    public AppHost()
    {
        Settings = new SettingsService();
        Settings.Load();
        L.Init(Settings.Current);
        Settings.SettingsChanged += (_, s) => L.Init(s);

        FFmpeg = new FFmpegService();
        Hotkeys = new HotkeyService(Settings);
        ManualRecording = new ManualRecordingService(Settings);
        ReplayBuffer = new ReplayBufferService(Settings);
        Screenshots = new ScreenshotService(Settings);
        AudioDevices = new AudioDeviceService();
        Performance = new PerformanceMonitorService();
        GalleryMeta = new GalleryMetaService();
        Obs = new ObsWebSocketService(Settings);
        // A stream that starts breaking up has to reach you even when the preflight
        // widget is closed — that is the whole point of noticing it before chat does.
        Obs.HealthWarning += r => ToastService.Show(ToastKind.Warning,
            L.T("Stream verliert Frames", "Stream is dropping frames"),
            L.T($"{r.RecentDropPercent:0.0} % verworfen · {StreamHealth.FormatBitrate(r.Kbps)} — Encoder oder Upload prüfen.",
                $"{r.RecentDropPercent:0.0} % dropped · {StreamHealth.FormatBitrate(r.Kbps)} — check encoder or upload."),
            durationSeconds: 8);
        Chat = new TwitchChatService(Settings);
        // Raids and gift bombs are the two things worth interrupting a game for.
        Chat.MessageReceived += m =>
        {
            if (!Settings.Current.Chat.EventToasts) return;
            if (m.Kind is not (ChatKind.Raid or ChatKind.SubGift)) return;
            ToastService.Show(ToastKind.Info,
                m.Kind == ChatKind.Raid ? L.T("Raid!", "Raid!") : L.T("Sub-Geschenk", "Gifted subs"),
                m.SystemText ?? m.User, durationSeconds: 8);
        };
        Music = new MusicPlayerService(Settings);
        Crosshairs = new CrosshairLibraryService();

        WireCaptureExclusivity();
        ResolveDefaultAudioDevices();
    }

    // True while the buffer was taken down for a manual recording, so it only comes back
    // if it was actually running before.
    private bool _bufferPausedForRecording;

    /// <summary>
    /// Only one screen capture at a time. AMF's capture component refuses a second
    /// instance outright, and two capture+encode pipelines cost far more in-game FPS than
    /// anything else the app does — so the replay buffer steps aside for a manual
    /// recording and comes back afterwards.
    /// </summary>
    private void WireCaptureExclusivity()
    {
        ManualRecording.SuspendCompetingCapture = async () =>
        {
            _bufferPausedForRecording = ReplayBuffer.IsRunning;
            if (!_bufferPausedForRecording) return;
            Logger.Info("Pausing the replay buffer for a manual recording.");
            await ReplayBuffer.SuspendAsync().ConfigureAwait(false);
        };

        // Resume on the EVENT, not in the toggle: this also covers a recording that ends
        // by itself (ffmpeg error, disk full), where nobody presses stop.
        ManualRecording.RecordingStopped += (_, _) =>
        {
            if (!_bufferPausedForRecording) return;
            _bufferPausedForRecording = false;
            Logger.Info("Resuming the replay buffer after the recording.");
            ReplayBuffer.Resume();
        };
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

        // Event-based foreground tracking for the Auto mode (re-pins the buffer
        // when a game launches). Must be created here: Initialize runs on the
        // WPF UI thread, whose message pump delivers the WinEvent callbacks.
        // OBS auto-connect: harmless while OBS is closed — the service just keeps
        // retrying every 5 s until the user starts OBS.
        if (Settings.Current.Streaming.Obs.AutoConnect)
            Obs.Enable();

        // Chat reads a public channel anonymously; reconnects itself if Twitch is down.
        if (Settings.Current.Chat.AutoConnect)
            Chat.Restart();

        Foreground = new ForegroundTracker(Settings);
        Foreground.RetargetRequested += name =>
        {
            // Freecam: the BUFFER follows the focused window. A running Ctrl+F9
            // recording is deliberately untouched (it stays on its own window,
            // video AND audio). No toast — window switches are frequent; the
            // Aufnahme-Tab live head shows the new target.
            Logger.Info($"Freecam-Retarget → {name}");
            ReplayBuffer.RequestRetarget();
        };
        Foreground.Start();
    }

    /// <summary>
    /// The WATCHED game (Window mode) started or stopped. Buffer: debounced
    /// retarget — audio swaps in place when the video target is unchanged, else
    /// a history-preserving warm restart. A RUNNING manual recording gets its
    /// audio re-routed to the game too (its video pin is unaffected).
    /// </summary>
    private void OnCaptureTargetChanged()
    {
        ReplayBuffer.RequestRetarget();
        // Only pinned-mode recordings follow the game's audio; an Auto-mode
        // recording is a freecam screen recording with all sounds — leave it.
        if (ManualRecording.IsRecording && Settings.Current.Capture.Mode != CaptureMode.Auto)
        {
            var plan = CaptureTargetResolver.Resolve(Settings.Current.Capture, Settings.Current);
            ManualRecording.RetargetAudio(plan.SysMode, plan.AudioPid);
        }
    }

    /// <summary>
    /// Starts or restarts the GameProcessWatcher based on current settings.
    /// Called at init and whenever the user changes GameOnly settings.
    /// </summary>
    public void StartGameWatcherIfNeeded()
    {
        GameWatcher?.Dispose();
        GameWatcher = null;

        // Which process to watch: the Window-mode capture target takes priority
        // (so the buffer re-pins onto the game's monitor the moment it launches),
        // else the legacy GameOnly audio process name.
        var cap = Settings.Current.Capture;
        var audio = Settings.Current.Audio;
        string? watchName = cap.Mode == CaptureMode.Window && !string.IsNullOrEmpty(cap.TargetProcessName)
            ? cap.TargetProcessName
            : (audio.SystemCaptureMode == AudioCaptureMode.GameOnly ? audio.GameProcessName : null);

        Logger.Info($"StartGameWatcherIfNeeded: captureMode={cap.Mode}, watchName='{watchName ?? "(null)"}'");

        if (!string.IsNullOrEmpty(watchName))
        {
            var gameName = watchName;
            GameWatcher = new GameProcessWatcher(gameName);
            GameWatcher.ProcessFound += pid =>
            {
                Logger.Info($"Target process found: {gameName} (PID {pid}) — re-pinning capture");
                OnCaptureTargetChanged();
                if (Settings.Current.Behavior.ShowToastNotifications)
                    ToastService.Show(ToastKind.Info,
                        L.T("Ziel erkannt", "Target detected"),
                        L.T($"{gameName} — Aufnahme richtet sich darauf aus", $"{gameName} — capture is aligning to it"));
            };
            GameWatcher.ProcessLost += () =>
            {
                Logger.Info("Target process lost — re-resolving capture");
                OnCaptureTargetChanged();
                if (Settings.Current.Behavior.ShowToastNotifications)
                    ToastService.Show(ToastKind.Info,
                        L.T("Ziel beendet", "Target closed"),
                        L.T($"{gameName} — Aufnahme neu ausgerichtet", $"{gameName} — capture re-aligned"));
            };
            // Start() does an immediate synchronous check before starting the poll loop
            GameWatcher.Start();
        }
        else
        {
            Logger.Info("StartGameWatcherIfNeeded: no watcher needed");
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
        Foreground?.Dispose();
        GameWatcher?.Dispose();
        Performance.Dispose();
        Crosshairs.Dispose();
        Obs.Dispose();
        Chat.Dispose();
        Music.Dispose();
        Hotkeys.Dispose();
        ManualRecording.Dispose();
        ReplayBuffer.Dispose();
        FFmpeg.Dispose();
    }
}

public sealed record CodecInfo(string FFmpegName, string Label);
