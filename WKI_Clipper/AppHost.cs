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
    /// <summary>Finite encodes (Discord compress, clip editor): one at a time, cancellable. Not the F9 path.</summary>
    public FFmpegJobRunner Jobs { get; }
    public PerformanceMonitorService Performance { get; }
    /// <summary>
    /// FPS / frame times / 1 % lows of a game via a passive ETW session (the PresentMon mechanism, no
    /// injection). Created on first access; the session itself runs only while someone subscribes.
    /// </summary>
    public FrameTimeMonitor FrameTimes => LazyInitializer.EnsureInitialized(ref _frameTimes, CreateFrameTimes);
    private FrameTimeMonitor? _frameTimes;

    private FrameTimeMonitor CreateFrameTimes()
    {
        // Born after the game came to the front: it has to start on that game, not on "none".
        var monitor = new FrameTimeMonitor();
        monitor.SetTargetProcess(CurrentGame?.Pid ?? 0);
        return monitor;
    }

    /// <summary>
    /// Which game is in front (any capture mode), debounced. Created in <see cref="Initialize"/> because it
    /// shares the foreground tracker's WinEvent hook, which needs the UI thread's message pump.
    /// </summary>
    public GameDetector? GameDetector { get; private set; }

    /// <summary>The game in front, or null.</summary>
    public GameInfo? CurrentGame => GameDetector?.Current;

    /// <summary>
    /// "F9 in &lt;Spiel&gt;: &lt;Länge&gt;" for the status lines when a press would save less than the ring holds (a game's
    /// own length, or the standard length while a profile elsewhere made the ring longer); null = F9 saves the whole ring.
    /// </summary>
    public string? F9StatusNote() => PressStatusNow().Note;

    /// <summary>
    /// The exe an F9 press attributes the clip to - ONE answer for the length, the log and the status lines, from the same
    /// evidence the clip's folder uses: the window a WGC capture pins (that is the footage), else the game really in front
    /// now (or, with our own window in front, the last external one), and only last the detector's debounced state (which
    /// lags a game that just came to the front by 1.5 s and is empty while a blocked window such as Discord has the focus).
    /// No game = null = the standard length.
    /// </summary>
    public string? ResolvePressExe() => PressClipLength.ResolveExe(
        () => Games?.PinnedExe(ReplayBuffer.CurrentPlan),
        () => GameDetector?.GameExeInFrontNow(),
        () => GameDetector?.Current?.ExeName);

    /// <summary>
    /// What a press would save right now (seconds) and the status note for it ("F9 in Arma Reforger: 2 min"; null when F9
    /// saves the whole ring). One exe resolution per call, shared by the label and the note, so the shown length is the one
    /// the key press saves. Cheap enough for a 1 Hz status tick: a few window queries, no process enumeration.
    /// </summary>
    public (int Seconds, string? Note) PressStatusNow()
    {
        var settings = Settings.Current;
        int ring = ReplayBuffer.EffectiveDurationSeconds;
        string? exe = ResolvePressExe();
        var game = CurrentGame;
        string? name = game is not null && EffectiveSettings.ProfileKey(game.ExeName) == EffectiveSettings.ProfileKey(exe)
            ? game.DisplayName : null;
        string? note = PressClipLength.StatusNote(settings, exe, name, ring) is { } n ? PressClipLength.StatusText(n) : null;
        return (PressClipLength.Resolve(ring, settings, exe).Seconds, note);
    }

    /// <summary>
    /// The game in front changed (null = none). Raised on a pool thread AFTER the FPS engine was pointed at
    /// the new game; UI consumers marshal themselves.
    /// </summary>
    public event Action<GameInfo?>? GameChanged;

    private void OnGameChanged(GameInfo? game)
    {
        _frameTimes?.SetTargetProcess(game?.Pid ?? 0);
        LogProfileChange(game);
        RememberShortcutGame(game);
        try { GameChanged?.Invoke(game); }
        catch (Exception ex) { Logger.Warn("GameChanged handler threw: " + ex.Message); }
    }

    /// <summary>
    /// A game came to the front: its exe becomes a suggestion in the shortcut bar's edit dialog. The path is read from the pid the
    /// way the game folders do it (query-only rights); a game that cannot be read (elevated, already gone) is skipped silently.
    /// </summary>
    private void RememberShortcutGame(GameInfo? game)
    {
        if (game is null) return;
        try
        {
            var exePath = GameContext.ImagePath(game.Pid);
            if (string.IsNullOrEmpty(exePath)) return;
            Shortcuts.RememberGame(exePath, game.DisplayName);
        }
        catch (Exception ex) { Logger.Warn("Remembering the game for the shortcut bar failed: " + ex.Message); }
    }

    /// <summary>
    /// Re-registers the global hotkeys when - and only when - a shortcut entry's hotkey changed since they were last registered.
    /// Marshals to the UI thread, which owns the hotkey window. The editor calls this after it changed a hotkey.
    /// </summary>
    public void RefreshShortcutHotkeys()
    {
        var signature = ShortcutHotkeys.Signature(Settings.Current.Shortcuts.Items);
        lock (_shortcutHotkeyGate)
        {
            if (signature == _shortcutHotkeySignature) return;
            _shortcutHotkeySignature = signature;
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess()) Hotkeys.RegisterAll();
        else dispatcher.BeginInvoke(new Action(Hotkeys.RegisterAll));
    }

    /// <summary>Key of the game profile whose values apply right now (null = the standard settings); what the log last said.</summary>
    private string? _activeProfileKey;

    /// <summary>
    /// One Info line whenever the active profile changes - a game with a profile came to the front, it left, or the
    /// user created one for the game in front. The F9 length and the crosshair then follow it.
    /// </summary>
    private void LogProfileChange(GameInfo? game)
    {
        try
        {
            var settings = Settings.Current;
            string? key = GameProfiles.ActiveKey(settings, game?.ExeName);
            if (string.Equals(System.Threading.Interlocked.Exchange(ref _activeProfileKey, key), key, StringComparison.Ordinal)) return;
            Logger.Info(key is null
                ? "Game profile: none active - the standard settings apply."
                : $"Game profile: '{game!.DisplayName}' ({key}) is active - {GameProfiles.Describe(settings, key)}.");
        }
        catch (Exception ex) { Logger.Warn("Profile log failed: " + ex.Message); }
    }

    /// <summary>
    /// What the crosshair widget is editing while it is on screen (null = not on screen). The overlay previews that
    /// look while the board is open, and a drag of the crosshair belongs to that target. Set by the widget.
    /// </summary>
    public CrosshairEditTarget? CrosshairEditing { get; set; }
    public GalleryMetaService GalleryMeta { get; }
    public ObsWebSocketService Obs { get; }
    public TwitchChatService Chat { get; }
    public MusicPlayerService Music { get; }
    public CrosshairLibraryService Crosshairs { get; }
    /// <summary>Short feedback sounds (clip saved, screenshot, recording, error).</summary>
    public SoundCueService Sounds { get; }
    /// <summary>"Discord 20 MB" / "Discord 500 MB": re-encodes a gallery clip to fit an upload limit.</summary>
    public DiscordCompressService Discord { get; }
    /// <summary>The clip editor's cut ("Schneiden…"): stream copy or frame-accurate re-encode of a range of a gallery clip.</summary>
    public ClipCutService ClipCut { get; }
    /// <summary>The user's Spotify app via the Windows media session (started on first use).</summary>
    public SpotifyMediaService Spotify { get; } = new();
    /// <summary>Controller combos (hold View+Menu etc.): polls XInput in the background, raises the hotkey action names.</summary>
    public GamepadService Gamepad { get; }

    /// <summary>
    /// The shortcut bar's service: items, scans, start. Scans only when asked (board open, "Neu suchen"); nothing runs on its
    /// own, so it costs nothing while a game is in front.
    /// </summary>
    public ShortcutService Shortcuts { get; }

    /// <summary>The shell behind <see cref="Shortcuts"/> (the edit dialog reads Start-menu shortcuts through it, too).</summary>
    public IShortcutShell ShortcutShell { get; }

    /// <summary>
    /// Set by App once the widget host exists: closes the board, and does nothing while it is closed (a hotkey start).
    /// Keeps the UI-owning host out of AppHost, like <see cref="CrosshairRefresh"/>.
    /// </summary>
    public Action? CloseBoard { get; set; }

    /// <summary>What the global hotkeys of the shortcut entries looked like when they were last registered.</summary>
    private string _shortcutHotkeySignature = "";
    private readonly object _shortcutHotkeyGate = new();

    /// <summary>
    /// Set by the widget host: re-applies the crosshair overlay (image, sliders,
    /// visibility) after settings changed. Keeps the UI-owning window out of AppHost.
    /// </summary>
    public Action? CrosshairRefresh { get; set; }

    /// <summary>Re-render the live crosshair overlay from current settings.</summary>
    public void RefreshCrosshair() => CrosshairRefresh?.Invoke();

    /// <summary>Set by the widget host: re-applies the on-screen performance overlay.</summary>
    public Action? PerfOverlayRefresh { get; set; }
    public void RefreshPerfOverlay() => PerfOverlayRefresh?.Invoke();
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
        Jobs = new FFmpegJobRunner(FFmpeg);
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
        // A typo or a renamed channel leaves the socket open and silent; say so once per channel
        // (the silent-socket check reconnects every few minutes and would repeat it).
        string? missingChannelShown = null;
        Chat.ChannelNotFound += ch =>
        {
            if (string.Equals(missingChannelShown, ch, StringComparison.OrdinalIgnoreCase)) return;
            missingChannelShown = ch;
            ToastService.Show(ToastKind.Warning,
                L.T("Twitch-Chat: Kanal nicht gefunden", "Twitch chat: channel not found"),
                L.T($"#{ch} gibt es nicht (mehr) — umbenannt? Im Chat-Widget den neuen Namen oder Link eintragen.",
                    $"#{ch} does not exist (anymore) — renamed? Enter the new name or link in the chat widget."),
                durationSeconds: 10);
        };
        Music = new MusicPlayerService(Settings);
        Crosshairs = new CrosshairLibraryService();
        Sounds = new SoundCueService(Settings);
        Discord = new DiscordCompressService(Jobs, () => Settings.Current, GalleryMeta)
        {
            // Detection runs after startup; until it is done this is empty and the compress uses libx264.
            AvailableEncoders = () => AvailableCodecs.Select(c => c.FFmpegName),
            // Lazy on purpose: the game detector is created later in startup (see Initialize).
            GameInForeground = () => GameForegroundDetector.IsGameInForeground(GameDetector)
        };
        ClipCut = new ClipCutService(Jobs, () => Settings.Current, GalleryMeta)
        {
            AvailableEncoders = () => AvailableCodecs.Select(c => c.FFmpegName)
        };

        // Not started here: App starts it once the action handler is subscribed (Gamepad.ApplySettings).
        Gamepad = new GamepadService(Settings);

        ShortcutShell = new Win32ShortcutShell();
        Shortcuts = new ShortcutService(Settings, ShortcutShell, new DiskFileSystemView(), new WindowsRegistryView(),
            closeBoard: () => CloseBoard?.Invoke(),
            toast: msg => ToastService.Show(ToastKind.Warning, L.T("Verknüpfungsleiste", "Shortcut bar"), msg, durationSeconds: 6));
        // A hotkey of an entry changed (editor, prune): the global registration follows. Other changes (icons, order) do not touch it.
        Shortcuts.ItemsChanged += (_, _) => RefreshShortcutHotkeys();

        WireCaptureExclusivity();
        WireGameFolders();
        WireClipNotes();
        ResolveDefaultAudioDevices();
    }

    /// <summary>
    /// A clip that had to be stitched across gaps (Alt-Tab, Ctrl+F9 recording) says so in its gallery note, so the
    /// missing minutes are not mistaken for lost footage. Appended to a note that already exists, never replacing it.
    /// </summary>
    private void WireClipNotes()
    {
        ReplayBuffer.ReplaySaved += (_, info) =>
        {
            if (info.Gaps.Count == 0) return;
            _ = Task.Run(() =>
            {
                try
                {
                    var key = GalleryMetaKey.For(GalleryBase.Clips,
                        SettingsService.ExpandPath(Settings.Current.Output.ClipsFolder), info.Path);
                    var note = ClipStitcher.GapNote(info.Gaps, t => t.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
                    var old = GalleryMeta.GetNote(key);
                    GalleryMeta.SetNote(key, old.Length == 0 ? note : old + "\n" + note);
                }
                catch (Exception ex) { Logger.Warn("Clip gap note failed: " + ex.Message); }
            });
        };
    }

    /// <summary>Decides which game a capture belongs to (per-game subfolders).</summary>
    public GameContext Games { get; private set; } = null!;

    private void WireGameFolders()
    {
        // Lazy on purpose: the foreground tracker is created later in startup.
        Games = new GameContext(() => Foreground?.LastExternalForegroundPid);
        ReplayBuffer.GameFolderFor = Games.ResolveFolder;
        ManualRecording.GameFolderFor = Games.ResolveFolder;
        Screenshots.GameFolderFor = Games.ResolveFolder;

        // F9 length per game: ONE read at the key press names the game the clip belongs to (pinned window, else the game
        // really in front, else the detector's state - see ResolvePressExe); the buffer derives the length AND the log line
        // from that single exe, so they cannot disagree. Nothing recognised = the standard length. The ring is NOT resized
        // by this - it always holds the longest length (EffectiveSettings.LongestClipSeconds).
        ReplayBuffer.GameExeAtPress = ResolvePressExe;
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

    /// <summary>
    /// Once per start: a clips or screenshots folder that sits in the program's own folder or is not a full path is
    /// logged, never moved (the files stay where they are; the Paths page tells the user). The buffer folder falls back
    /// to the default at its own start, which logs it.
    /// </summary>
    private void LogFolderWarnings()
    {
        try
        {
            var o = Settings.Current.Output;
            foreach (var (name, folder) in new[] { ("Clips", o.ClipsFolder), ("Screenshots", o.ScreenshotsFolder) })
            {
                var problem = PathRules.OutputFolderProblem(folder, PathRules.InstallDirectory);
                if (problem == FolderProblem.None) continue;
                Logger.Warn($"{name} folder '{folder}' is not a good place ({problem}): " +
                            (problem == FolderProblem.InstallDirectory
                                ? "it is inside the program folder, where updates and the uninstaller work. "
                                : "a bare drive or relative path means different folders to different programs. ") +
                            "Nothing is moved - the Paths page recommends another folder.");
            }
        }
        catch (Exception ex) { Logger.Warn("Folder check failed: " + ex.Message); }
    }

    public void Initialize()
    {
        LogFolderWarnings();
        Hotkeys.Initialize();
        lock (_shortcutHotkeyGate) _shortcutHotkeySignature = ShortcutHotkeys.Signature(Settings.Current.Shortcuts.Items);   // what Initialize just registered
        // Codec detection runs async; not blocking startup.
        _ = DetectCodecsAsync();
        // A crash mid-encode leaves "*.mp4.part" behind; sweep those off the UI thread.
        _ = Task.Run(() => Jobs.CleanupStaleParts());
        // ... and a 2-pass Discord compress leaves its pass-1 statistics (hundreds of MB for a long clip) the same way.
        _ = Task.Run(() => Discord.CleanupStaleStats());
        // A crashed clipper leaves its FPS session running (providers stay enabled until reboot).
        _ = Task.Run(FrameTimeMonitor.StopOrphanedSession);
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

        // "Is a game in front" in every capture mode. Shares the hook above; the Window-mode target comes
        // from the live settings, and a settings save re-checks the window that is in front.
        GameDetector = new GameDetector(
            windowModeTarget: () => Settings.Current.Capture.Mode == CaptureMode.Window
                ? Settings.Current.Capture.TargetProcessName : null,
            displayName: f => Games.DisplayNameFor(f.ExePath, f.ProcessName));
        GameDetector.GameChanged += OnGameChanged;
        Foreground.ForegroundWindowChanged += GameDetector.OnForegroundChanged;
        // A save may also create or delete the profile of the game in front (crosshair widget, F9 length list).
        Settings.SettingsChanged += (_, _) => { GameDetector?.Reevaluate(); LogProfileChange(CurrentGame); };
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
        // Last chance for notes that could not be saved earlier (locked file).
        try { GalleryMeta.Flush(); } catch (Exception ex) { Logger.Warn("GalleryMeta flush on exit failed: " + ex.Message); }
        Foreground?.Dispose();
        GameDetector?.Dispose();
        GameWatcher?.Dispose();
        Performance.Dispose();
        _frameTimes?.Dispose();
        Crosshairs.Dispose();
        Obs.Dispose();
        Chat.Dispose();
        Music.Dispose();
        Sounds.Dispose();
        Spotify.Dispose();
        Gamepad.Dispose();
        Shortcuts.Dispose();
        Hotkeys.Dispose();
        ManualRecording.Dispose();
        ReplayBuffer.Dispose();
        Discord.Dispose();
        ClipCut.Dispose();
        Jobs.Dispose();
        FFmpeg.Dispose();
    }
}

public sealed record CodecInfo(string FFmpegName, string Label);
