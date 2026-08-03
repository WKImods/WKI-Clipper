using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OBSWebsocketDotNet;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>Live OBS state mirrored from obs-websocket v5 events, for the button tiles.</summary>
public sealed class ObsStatus
{
    public bool Connected;
    public string? CurrentScene;
    public bool Streaming;
    public bool Recording;
    public bool RecordPaused;
    public bool ReplayBufferActive;
    public bool VirtualCamActive;
    /// <summary>
    /// Input name → muted. Concurrent: written by obs-websocket worker threads,
    /// read by the UI thread while painting tiles.
    /// </summary>
    public readonly ConcurrentDictionary<string, bool> InputMuted = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Input name → linear volume multiplier (1.0 = 0 dB). Same threading as InputMuted.</summary>
    public readonly ConcurrentDictionary<string, float> InputVolume = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Live output health while streaming, null otherwise. Immutable record, so the
    /// poll thread can publish it and the UI thread read it without tearing.
    /// </summary>
    public StreamHealthReading? Health;
    /// <summary>Where OBS writes recordings — often a different drive than the clipper's.</summary>
    public string? RecordDirectory;
}

/// <summary>Linear multiplier ↔ decibel helpers for the mixer faders (pure, unit-tested).</summary>
public static class ObsVolume
{
    /// <summary>Multiplier → dB. 0 (or below) maps to negative infinity.</summary>
    public static double MulToDb(double mul) => mul <= 0 ? double.NegativeInfinity : 20.0 * Math.Log10(mul);

    /// <summary>Fader label: "-6.0 dB", or "-∞" at silence.</summary>
    public static string FormatDb(double mul)
    {
        double db = MulToDb(mul);
        return double.IsNegativeInfinity(db) ? "-∞" : $"{db:0.0} dB";
    }
}

/// <summary>
/// Connection + request/event layer between the Streaming widget and OBS's built-in
/// WebSocket server (v5, obs-websocket-dotnet). Design points:
///  - Auto-reconnect: OBS is usually started AFTER the clipper, may close or restart
///    at any time. A 5 s timer retries while enabled; buttons arm only when connected.
///  - Every request is guarded — a dead connection degrades to an ActionError toast,
///    never an exception on the UI path.
///  - Library events arrive on socket worker threads; consumers get a single
///    StatusChanged and marshal to their dispatcher themselves.
/// </summary>
public sealed class ObsWebSocketService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly OBSWebsocket _obs = new();
    private readonly System.Timers.Timer _reconnect = new(5000) { AutoReset = true };
    /// <summary>Polls output stats while live — obs-websocket pushes no stats events.</summary>
    private readonly System.Timers.Timer _statsPoll = new(2000) { AutoReset = true };
    private volatile bool _wantConnected;
    private long _connectingSince; // ticks; 0 = not connecting
    private StreamSample? _prevSample;
    private long _lastHealthWarnTicks;
    private int _pollBusy;   // 0/1 — keeps requests from stacking if OBS stops answering

    public ObsStatus Status { get; } = new();
    public bool IsConnected => _obs.IsConnected;

    /// <summary>Connection or any OBS state changed (worker thread!).</summary>
    public event Action? StatusChanged;
    /// <summary>A button action failed (worker thread!) — localized message for a toast.</summary>
    public event Action<string>? ActionError;
    /// <summary>The live stream started dropping frames badly (worker thread!), rate-limited.</summary>
    public event Action<StreamHealthReading>? HealthWarning;

    public ObsWebSocketService(SettingsService settings)
    {
        _settings = settings;

        _obs.Connected += (_, _) =>
        {
            Interlocked.Exchange(ref _connectingSince, 0);
            Logger.Info("OBS connected.");
            try { PullFullStatus(); }
            catch (Exception ex) { Logger.Warn("OBS initial status pull failed: " + ex.Message); }
            Status.Connected = true;
            UpdateStatsPolling();
            StatusChanged?.Invoke();
        };
        _obs.Disconnected += (_, e) =>
        {
            Interlocked.Exchange(ref _connectingSince, 0);
            bool wasConnected = Status.Connected;
            Status.Connected = false;
            UpdateStatsPolling();
            if (wasConnected) Logger.Info($"OBS disconnected: {e?.DisconnectReason ?? "unknown"}");
            StatusChanged?.Invoke();
        };

        // --- live state events → mirror into Status ---
        _obs.CurrentProgramSceneChanged += (_, e) => { Status.CurrentScene = e.SceneName; StatusChanged?.Invoke(); };
        _obs.StreamStateChanged += (_, e) =>
        {
            Status.Streaming = e.OutputState.IsActive;
            UpdateStatsPolling();
            StatusChanged?.Invoke();
        };
        _obs.RecordStateChanged += (_, e) =>
        {
            Status.Recording = e.OutputState.IsActive;
            if (!e.OutputState.IsActive) Status.RecordPaused = false;
            // The output folder can be repointed between takes — refresh when one starts.
            if (e.OutputState.IsActive) PullRecordDirectory();
            StatusChanged?.Invoke();
        };
        _obs.ReplayBufferStateChanged += (_, e) => { Status.ReplayBufferActive = e.OutputState.IsActive; StatusChanged?.Invoke(); };
        _obs.VirtualcamStateChanged += (_, e) => { Status.VirtualCamActive = e.OutputState.IsActive; StatusChanged?.Invoke(); };
        _obs.InputMuteStateChanged += (_, e) => { Status.InputMuted[e.InputName] = e.InputMuted; StatusChanged?.Invoke(); };
        _obs.InputVolumeChanged += (_, e) =>
        {
            // Mirror OBS-side fader moves so our slider follows when volume is
            // changed in OBS itself (or by another stream-deck client).
            Status.InputVolume[e.Volume.InputName] = (float)e.Volume.InputVolumeMul;
            StatusChanged?.Invoke();
        };

        _reconnect.Elapsed += (_, _) => TryConnectTick();
        _statsPoll.Elapsed += (_, _) => PollStatsTick();
    }

    // ---- live stream health ----

    /// <summary>Stats polling runs only while actually live — no traffic when idle.</summary>
    private void UpdateStatsPolling()
    {
        if (Status.Connected && Status.Streaming)
        {
            if (!_statsPoll.Enabled) { _prevSample = null; _statsPoll.Start(); }
        }
        else
        {
            _statsPoll.Stop();
            _prevSample = null;
            Status.Health = null;
        }
    }

    /// <summary>
    /// Samples the output and derives uptime/bitrate/recent drops. Only the timer thread
    /// touches _prevSample, so no lock is needed.
    /// </summary>
    private void PollStatsTick()
    {
        if (!_obs.IsConnected) return;
        // The timer keeps firing every 2 s regardless of how long a request takes; without
        // this guard a hung OBS would pile up thread-pool work every tick.
        if (Interlocked.Exchange(ref _pollBusy, 1) == 1) return;
        try
        {
            var s = _obs.GetStreamStatus();
            if (!s.IsActive)
            {
                // The stream ended without us seeing the event — stop polling.
                Status.Streaming = false;
                UpdateStatsPolling();
                StatusChanged?.Invoke();
                return;
            }

            var cur = new StreamSample(DateTime.UtcNow.Ticks, s.BytesSent, s.TotalFrames, s.SkippedFrames, s.Duration);
            var reading = StreamHealth.Compute(_prevSample, cur);
            _prevSample = cur;
            Status.Health = reading;

            if (StreamHealth.Rate(reading) == CheckState.Fail) RaiseHealthWarning(reading);
            StatusChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Warn("OBS GetStreamStatus failed: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _pollBusy, 0);
        }
    }

    /// <summary>At most one warning per minute — a bad connection would otherwise spam toasts.</summary>
    private void RaiseHealthWarning(StreamHealthReading r)
    {
        long now = DateTime.UtcNow.Ticks;
        long last = Interlocked.Read(ref _lastHealthWarnTicks);
        if (last != 0 && now - last < TimeSpan.FromSeconds(60).Ticks) return;
        Interlocked.Exchange(ref _lastHealthWarnTicks, now);
        Logger.Warn($"Stream health: {r.RecentDropPercent:0.0} % frames dropped, {r.Kbps:0} kbit/s");
        HealthWarning?.Invoke(r);
    }

    private void PullRecordDirectory()
    {
        try { Status.RecordDirectory = _obs.GetRecordDirectory(); }
        catch (Exception ex) { Logger.Warn("OBS GetRecordDirectory failed: " + ex.Message); }
    }

    // ---- lifecycle ----

    /// <summary>Enable the connection (starts the reconnect loop). Reads settings fresh each attempt.</summary>
    public void Enable()
    {
        _wantConnected = true;
        _reconnect.Start();
        TryConnectTick();
    }

    /// <summary>Disable and drop the connection (user pressed "Trennen").</summary>
    public void Disable()
    {
        _wantConnected = false;
        _reconnect.Stop();
        try { if (_obs.IsConnected) _obs.Disconnect(); } catch { }
    }

    private void TryConnectTick()
    {
        if (!_wantConnected || _obs.IsConnected) return;

        // Don't stack connection attempts; a stuck one times out after 10 s.
        long since = Interlocked.Read(ref _connectingSince);
        if (since != 0 && (DateTime.UtcNow.Ticks - since) < TimeSpan.FromSeconds(10).Ticks) return;
        Interlocked.Exchange(ref _connectingSince, DateTime.UtcNow.Ticks);

        var o = _settings.Current.Streaming.Obs;
        string url = $"ws://{o.Host}:{o.Port}";
        string password = SecretProtector.Unprotect(o.PasswordProtected) ?? "";
        try { _obs.ConnectAsync(url, password); }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _connectingSince, 0);
            Logger.Warn("OBS ConnectAsync threw: " + ex.Message);
        }
    }

    /// <summary>Initial state after connecting — events only cover changes from here on.</summary>
    private void PullFullStatus()
    {
        Status.CurrentScene = _obs.GetCurrentProgramScene();
        var stream = _obs.GetStreamStatus();
        Status.Streaming = stream.IsActive;
        var rec = _obs.GetRecordStatus();
        Status.Recording = rec.IsRecording;
        Status.RecordPaused = rec.IsRecordingPaused;
        try { Status.ReplayBufferActive = _obs.GetReplayBufferStatus(); } catch { Status.ReplayBufferActive = false; }
        try { Status.VirtualCamActive = _obs.GetVirtualCamStatus().IsActive; } catch { Status.VirtualCamActive = false; }
        PullRecordDirectory();

        Status.InputMuted.Clear();
        Status.InputVolume.Clear();
        foreach (var name in ListInputNames())
        {
            try { Status.InputMuted[name] = _obs.GetInputMute(name); }
            catch { /* input without mute (e.g. non-audio) — skip */ }
            try { Status.InputVolume[name] = (float)_obs.GetInputVolume(name).VolumeMul; }
            catch { /* non-audio input has no volume — skip */ }
        }
    }

    /// <summary>Audio inputs OBS reports (those that actually have a volume), sorted.</summary>
    public List<string> ListAudioInputNames()
        => Status.InputVolume.Keys.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Sets an input's linear volume. Fire-and-forget on a worker thread, guarded like ExecuteAsync.</summary>
    public Task SetInputVolumeMulAsync(string inputName, float mul) => Task.Run(() =>
    {
        if (!_obs.IsConnected)
        {
            ActionError?.Invoke(L.T("OBS ist nicht verbunden.", "OBS is not connected."));
            return;
        }
        try
        {
            _obs.SetInputVolume(inputName, Math.Clamp(mul, 0f, 1f), false);
            Status.InputVolume[inputName] = mul;   // echo event confirms it shortly after
        }
        catch (Exception ex)
        {
            Logger.Warn($"OBS SetInputVolume failed for '{inputName}': {ex.Message}");
            ActionError?.Invoke(L.T($"Lautstärke konnte nicht gesetzt werden: {ex.Message}",
                                    $"Could not set volume: {ex.Message}"));
        }
    });

    /// <summary>Toggles an input's mute (mixer row button).</summary>
    public Task ToggleInputMuteAsync(string inputName) => Task.Run(() =>
    {
        if (!_obs.IsConnected)
        {
            ActionError?.Invoke(L.T("OBS ist nicht verbunden.", "OBS is not connected."));
            return;
        }
        try { _obs.ToggleInputMute(inputName); }
        catch (Exception ex) { Logger.Warn($"OBS ToggleInputMute failed for '{inputName}': {ex.Message}"); }
    });

    // ---- catalog queries for the button config dialog (call off the UI thread) ----

    public List<string> ListScenes()
    {
        try { return _obs.GetSceneList().Scenes.Select(s => s.Name).ToList(); }
        catch (Exception ex) { Logger.Warn("GetSceneList failed: " + ex.Message); return new(); }
    }

    public List<string> ListInputNames()
    {
        try { return _obs.GetInputList().Select(i => i.InputName).ToList(); }
        catch (Exception ex) { Logger.Warn("GetInputList failed: " + ex.Message); return new(); }
    }

    public List<string> ListSceneItems(string sceneName)
    {
        try { return _obs.GetSceneItemList(sceneName).Select(i => i.SourceName).ToList(); }
        catch (Exception ex) { Logger.Warn("GetSceneItemList failed: " + ex.Message); return new(); }
    }

    // ---- button execution ----

    /// <summary>Executes the button in slot <paramref name="slot"/> (hotkey path).</summary>
    public Task ExecuteSlotAsync(int slot)
    {
        var btn = _settings.Current.Streaming.Buttons.FirstOrDefault(b => b.Slot == slot);
        if (btn is null) return Task.CompletedTask;
        return ExecuteAsync(btn);
    }

    /// <summary>
    /// Runs the button's OBS request on a worker thread. Failures surface as
    /// ActionError (→ toast), never as exceptions.
    /// </summary>
    public Task ExecuteAsync(StreamButtonConfig btn) => Task.Run(() =>
    {
        if (!_obs.IsConnected)
        {
            ActionError?.Invoke(L.T("OBS ist nicht verbunden.", "OBS is not connected."));
            return;
        }
        try
        {
            switch (btn.Action)
            {
                case StreamAction.SetScene:
                    Require(btn.Param, out var scene);
                    _obs.SetCurrentProgramScene(scene);
                    break;
                case StreamAction.ToggleStream: _obs.ToggleStream(); break;
                case StreamAction.StartStream: _obs.StartStream(); break;
                case StreamAction.StopStream: _obs.StopStream(); break;
                case StreamAction.ToggleRecord: _obs.ToggleRecord(); break;
                case StreamAction.PauseResumeRecord:
                {
                    var rec = _obs.GetRecordStatus();
                    if (!rec.IsRecording)
                    {
                        ActionError?.Invoke(L.T("OBS nimmt gerade nicht auf.", "OBS is not recording."));
                        return;
                    }
                    if (rec.IsRecordingPaused) _obs.ResumeRecord(); else _obs.PauseRecord();
                    Status.RecordPaused = !rec.IsRecordingPaused;
                    StatusChanged?.Invoke();
                    break;
                }
                case StreamAction.SaveReplayBuffer: _obs.SaveReplayBuffer(); break;
                case StreamAction.ToggleReplayBuffer:
                    if (_obs.GetReplayBufferStatus()) _obs.StopReplayBuffer(); else _obs.StartReplayBuffer();
                    break;
                case StreamAction.ToggleInputMute:
                    Require(btn.Param, out var input);
                    _obs.ToggleInputMute(input);
                    break;
                case StreamAction.ToggleSceneItem:
                {
                    Require(btn.Param, out var itemScene);
                    Require(btn.Param2, out var itemName);
                    int id = _obs.GetSceneItemId(itemScene, itemName, 0);
                    bool enabled = _obs.GetSceneItemEnabled(itemScene, id);
                    _obs.SetSceneItemEnabled(itemScene, id, !enabled);
                    break;
                }
                case StreamAction.ToggleVirtualCam: _obs.ToggleVirtualCam(); break;
                case StreamAction.StudioModeTransition: _obs.TriggerStudioModeTransition(); break;
            }
            Logger.Info($"OBS action ok: {btn.Action} ({btn.Param ?? "-"})");
        }
        catch (Exception ex)
        {
            Logger.Warn($"OBS action failed: {btn.Action} — {ex.Message}");
            ActionError?.Invoke(L.T($"OBS-Aktion fehlgeschlagen: {ex.Message}",
                                    $"OBS action failed: {ex.Message}"));
        }
    });

    private static void Require(string? value, out string result)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(L.T("Button ist unvollständig konfiguriert (Parameter fehlt).",
                                                    "Button is not fully configured (missing parameter)."));
        result = value;
    }

    public void Dispose()
    {
        _wantConnected = false;
        _reconnect.Stop();
        _reconnect.Dispose();
        _statsPoll.Stop();
        _statsPoll.Dispose();
        try { if (_obs.IsConnected) _obs.Disconnect(); } catch { }
    }
}
