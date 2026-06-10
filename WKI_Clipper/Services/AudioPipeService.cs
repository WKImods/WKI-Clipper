using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>
/// Captures system audio (WASAPI loopback) and microphone in-process via NAudio,
/// mixes both to a single s16le 48 kHz stereo stream, and writes the result into
/// a named pipe that FFmpeg reads as a raw PCM input.
///
/// Each source (mic / system) is initialised independently. If one fails the
/// other is still used. Only if BOTH fail does the service report "no audio".
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AudioPipeService : IDisposable
{
    public const int TargetSampleRate = 48000;
    public const int TargetChannels = 2;

    public string FFmpegInputArgs =>
        $"-f s16le -ar {TargetSampleRate} -ac {TargetChannels} -i \"\\\\.\\pipe\\{_pipeName}\"";

    public bool IsRunning { get; private set; }
    public bool MicActive { get; private set; }
    public bool SystemActive { get; private set; }
    public string? LastError { get; private set; }

    private readonly string _pipeName;
    private NamedPipeServerStream? _server;
    private IWaveIn? _sysCapture;
    private WasapiCapture? _micCapture;
    private BufferedWaveProvider? _sysBuf;
    private BufferedWaveProvider? _micBuf;
    private CancellationTokenSource? _cts;
    private Task? _connectTask;

    private readonly bool _wantMic;
    private readonly bool _wantSys;
    private long _sysBytesIn;
    private long _micBytesIn;
    private long _bytesPumped;
    private bool _micSignalSeen;
    private DateTime _lastStatsLog = DateTime.UtcNow;
    private readonly string? _micDeviceName;
    private readonly string? _sysDeviceName;
    private readonly float _micVolume;
    private readonly float _sysVolume;
    private readonly AudioCaptureMode _captureMode;
    private readonly int? _gamePid;

    public AudioPipeService(AppSettings settings, int? gamePid = null)
    {
        _wantMic = settings.Audio.RecordMicrophone && !string.IsNullOrWhiteSpace(settings.Audio.MicDeviceId);
        _wantSys = settings.Audio.RecordSystemSound && !string.IsNullOrWhiteSpace(settings.Audio.SystemDeviceId);
        _micDeviceName = settings.Audio.MicDeviceId;
        _sysDeviceName = settings.Audio.SystemDeviceId;
        _micVolume = (float)Math.Clamp(settings.Audio.MicVolume, 0.0, 8.0);
        _sysVolume = (float)Math.Clamp(settings.Audio.SystemVolume, 0.0, 8.0);
        _captureMode = settings.Audio.SystemCaptureMode;
        _gamePid = gamePid;
        _pipeName = "WKI_Clipper_Audio_" + Guid.NewGuid().ToString("N").Substring(0, 8);
    }

    public bool HasAnyAudio() => _wantMic || _wantSys;

    /// <summary>
    /// Returns true if at least one capture is active and the pipe is ready.
    /// Returns false if both captures failed — caller should then build the
    /// ffmpeg command WITHOUT the pipe input so recording still works.
    /// </summary>
    public bool Start()
    {
        if (!HasAnyAudio())
        {
            Logger.Info("AudioPipeService.Start: no audio sources enabled, skipping");
            return false;
        }
        if (IsRunning) return true;

        // Pipe server — SMALL buffer (~170 ms of audio). A large buffer (1 MB)
        // means up to 5 seconds of PCM sitting in the pipe waiting for ffmpeg
        // to drain, which manifests as audio-behind-video drift in the output.
        // 32 KB = 32768 / (48000 × 2 × 2) ≈ 170 ms worst case.
        const int pipeBuf = 32 * 1024;
        _server = new NamedPipeServerStream(
            _pipeName, PipeDirection.Out,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: pipeBuf, outBufferSize: pipeBuf);

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        using var enumerator = new MMDeviceEnumerator();

        // System loopback — try independently of mic
        if (_wantSys)
        {
            try
            {
                if (_captureMode == AudioCaptureMode.GameOnly && _gamePid.HasValue)
                {
                    // Process-specific loopback — only the game's audio
                    var plc = new ProcessLoopbackCapture((uint)_gamePid.Value);
                    _sysCapture = plc;
                    Logger.Info($"Using ProcessLoopbackCapture for PID {_gamePid.Value}");
                }
                else
                {
                    // Standard loopback — all system audio
                    var sysDev = FindRender(enumerator, _sysDeviceName)
                                ?? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    _sysCapture = new WasapiLoopbackCapture(sysDev);
                    Logger.Info($"Audio system loopback started: {sysDev.FriendlyName} | {_sysCapture.WaveFormat} | vol={_sysVolume:F2}");
                }

                _sysBuf = new BufferedWaveProvider(_sysCapture.WaveFormat)
                {
                    BufferLength = 1 << 21,
                    DiscardOnBufferOverflow = true,
                    ReadFully = false
                };
                _sysCapture.DataAvailable += (_, e) =>
                {
                    if (e.BytesRecorded > 0)
                    {
                        _sysBuf.AddSamples(e.Buffer, 0, e.BytesRecorded);
                        Interlocked.Add(ref _sysBytesIn, e.BytesRecorded);
                    }
                };
                _sysCapture.StartRecording();
                SystemActive = true;
            }
            catch (Exception ex)
            {
                Logger.Error("System audio capture init failed", ex);
                LastError = "System: " + ex.Message;
                _sysCapture?.Dispose(); _sysCapture = null;
                _sysBuf = null;
                SystemActive = false;
            }
        }

        // Microphone — try independently of system
        if (_wantMic)
        {
            try
            {
                var micDev = FindCapture(enumerator, _micDeviceName)
                            ?? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                // Match the sys capture cadence — NAudio default is 100 ms
                // event period for WasapiCapture, which together with the
                // ~10 ms loopback period creates a stuttering Math.Max
                // pattern in the writer. 20 ms matches the sys side.
                _micCapture = new WasapiCapture(micDev, useEventSync: true, audioBufferMillisecondsLength: 20);
                _micBuf = new BufferedWaveProvider(_micCapture.WaveFormat)
                {
                    BufferLength = 1 << 21,
                    DiscardOnBufferOverflow = true,
                    ReadFully = false
                };
                _micCapture.DataAvailable += (_, e) =>
                {
                    if (e.BytesRecorded > 0)
                    {
                        _micBuf.AddSamples(e.Buffer, 0, e.BytesRecorded);
                        Interlocked.Add(ref _micBytesIn, e.BytesRecorded);

                        // Detect "permission silently denied" — Windows delivers
                        // bytes but they're all zero. Sample a single short int
                        // from the middle of the buffer.
                        if (!_micSignalSeen && e.BytesRecorded >= 8)
                        {
                            int mid = e.BytesRecorded / 2;
                            // Look for any non-trivial sample value
                            for (int i = 0; i < Math.Min(64, e.BytesRecorded - 4); i += 4)
                            {
                                float sample = BitConverter.ToSingle(e.Buffer, mid + i);
                                if (Math.Abs(sample) > 0.001f)
                                {
                                    _micSignalSeen = true;
                                    Logger.Info("Mic signal confirmed (non-zero samples arriving).");
                                    break;
                                }
                            }
                        }
                    }
                };
                _micCapture.StartRecording();
                MicActive = true;
                Logger.Info($"Audio mic capture started: {micDev.FriendlyName} | {_micCapture.WaveFormat} | vol={_micVolume:F2}");
            }
            catch (Exception ex)
            {
                Logger.Error("Microphone capture init failed", ex);
                LastError = "Mic: " + ex.Message;
                _micCapture?.Dispose(); _micCapture = null;
                _micBuf = null;
                MicActive = false;
            }
        }

        // If both failed, tear everything down and signal failure.
        if (!MicActive && !SystemActive)
        {
            Logger.Error("AudioPipeService: both captures failed, no audio will be recorded.");
            try { _server?.Dispose(); } catch { }
            _server = null;
            return false;
        }

        IsRunning = true;
        _connectTask = Task.Run(() => RunWriterAsync(ct), ct);
        return true;
    }

    private async Task RunWriterAsync(CancellationToken ct)
    {
        try
        {
            await _server!.WaitForConnectionAsync(ct).ConfigureAwait(false);

            // Drop pre-roll (audio captured between Start() and pipe connect —
            // otherwise the writer pumps stale samples at output time 0).
            int dropMs = _sysBuf is null ? 0 : (int)(_sysBuf.BufferedDuration.TotalMilliseconds);
            int dropMsMic = _micBuf is null ? 0 : (int)(_micBuf.BufferedDuration.TotalMilliseconds);
            _sysBuf?.ClearBuffer();
            _micBuf?.ClearBuffer();
            Logger.Info($"Audio pipe: ffmpeg connected. Flushed pre-roll: sys={dropMs}ms mic={dropMsMic}ms");

            BuildProviders();
            if (_sysProvider is null && _micProvider is null)
            {
                Logger.Warn("Audio pipe: no source providers — exiting writer task");
                return;
            }

            // 10 ms chunks — small enough that callback-timing jitter doesn't
            // manifest as audible gaps, large enough to keep loop overhead low.
            const int TICK_FRAMES   = TargetSampleRate / 100;          // 480
            const int TICK_SAMPLES  = TICK_FRAMES * TargetChannels;    // 960 floats

            var sysFloatBuf = new float[TICK_SAMPLES];
            var micFloatBuf = new float[TICK_SAMPLES];
            var byteBuf = new byte[TICK_SAMPLES * 2];

            var lastTrimCheck = DateTime.UtcNow;

            // When both sources are active, the system loopback is the
            // primary clock source. Its WdlResamplingSampleProvider (96→48 kHz)
            // sometimes returns fewer samples than TICK_SAMPLES in one go
            // because of internal filter state. If the mic provider (no
            // resampler) is allowed to return MORE samples via Math.Max, it
            // force-pads the system portion with silence and overproduces
            // output — the sys buffer accumulates, the drift trim fires
            // constantly (every 500 ms!), and the discarded audio is audible
            // as stutter. Fix: read mic in lockstep with sys so the output
            // rate is driven by the system provider alone.
            bool hasPrimary = _sysProvider != null;

            while (!ct.IsCancellationRequested && _server.IsConnected)
            {
                int sysRead = _sysProvider?.Read(sysFloatBuf, 0, TICK_SAMPLES) ?? 0;
                int micRead = 0;

                if (_micProvider != null)
                {
                    if (hasPrimary && sysRead > 0)
                    {
                        // Mic reads at most what sys produced — stays in lockstep.
                        micRead = _micProvider.Read(micFloatBuf, 0, sysRead);
                    }
                    else if (!hasPrimary)
                    {
                        // Mic-only mode: mic drives the timeline itself.
                        micRead = _micProvider.Read(micFloatBuf, 0, TICK_SAMPLES);
                    }
                    // When hasPrimary && sysRead==0: skip mic too — wait for
                    // the primary source just like the single-source path does.
                }

                int read = Math.Max(sysRead, micRead);
                if (read == 0)
                {
                    await Task.Delay(1, ct).ConfigureAwait(false);
                    continue;
                }

                for (int i = 0; i < read; i++)
                {
                    float s = i < sysRead ? sysFloatBuf[i] : 0f;
                    float m = i < micRead ? micFloatBuf[i] : 0f;
                    float v = s + m;
                    if (v > 1f) v = 1f;
                    else if (v < -1f) v = -1f;
                    short val = (short)(v * 32767f);
                    byteBuf[2 * i] = (byte)(val & 0xFF);
                    byteBuf[2 * i + 1] = (byte)((val >> 8) & 0xFF);
                }

                await _server.WriteAsync(byteBuf.AsMemory(0, read * 2), ct).ConfigureAwait(false);
                Interlocked.Add(ref _bytesPumped, read * 2);

                // Drift check every 500 ms so a buffer that creeps past the
                // threshold gets trimmed promptly.
                if ((DateTime.UtcNow - lastTrimCheck).TotalSeconds >= 0.5)
                {
                    lastTrimCheck = DateTime.UtcNow;
                    TrimIfStale(_sysBuf, "sys");
                    TrimIfStale(_micBuf, "mic");
                }
                // Stats every 5 seconds (slower than the trim check).
                if ((DateTime.UtcNow - _lastStatsLog).TotalSeconds >= 5)
                {
                    var elapsed = (DateTime.UtcNow - _lastStatsLog).TotalSeconds;
                    _lastStatsLog = DateTime.UtcNow;
                    long sysIn = Interlocked.Exchange(ref _sysBytesIn, 0);
                    long micIn = Interlocked.Exchange(ref _micBytesIn, 0);
                    long out_ = Interlocked.Exchange(ref _bytesPumped, 0);
                    Logger.Info($"Audio throughput  ·  sys {sysIn / 1024 / elapsed:F1} KB/s  ·  mic {micIn / 1024 / elapsed:F1} KB/s  ·  out {out_ / 1024 / elapsed:F1} KB/s  ·  mic_signal={_micSignalSeen}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error("Audio pipe writer failed", ex);
        }
    }

    private static void TrimIfStale(BufferedWaveProvider? buf, string tag)
    {
        if (buf is null) return;
        var ms = buf.BufferedDuration.TotalMilliseconds;
        // With both captures on 20 ms event-sync, the buffer should
        // hover around 20–40 ms in steady state. We only trim when it
        // really drifts (> 150 ms) — trimming earlier eats fresh
        // samples and itself becomes audible as stutter.
        if (ms > 150)
        {
            var fmt = buf.WaveFormat;
            int bytesPerMs = fmt.AverageBytesPerSecond / 1000;
            int dropBytes = (int)((ms - 80) * bytesPerMs); // keep ~80 ms
            // BufferedWaveProvider has no "drop oldest" — fake it by reading
            // and discarding.
            var trash = new byte[dropBytes];
            buf.Read(trash, 0, dropBytes);
            Logger.Warn($"Audio drift trimmed: {tag} buffer was {ms:F0}ms, dropped {dropBytes / bytesPerMs}ms");
        }
    }

    private ISampleProvider? _sysProvider;
    private ISampleProvider? _micProvider;

    /// <summary>
    /// Initialises sys and mic sample provider chains. We DELIBERATELY do not
    /// wrap them in a MixingSampleProvider — that gets permanently stuck
    /// returning 0 whenever any input (specifically WdlResamplingSampleProvider)
    /// returns 0 on the first call after a buffer clear. The writer instead
    /// reads from each provider separately and mixes the floats manually.
    /// </summary>
    private void BuildProviders()
    {
        if (_sysBuf != null)
        {
            ISampleProvider sp = ToTargetFormat(_sysBuf);
            if (Math.Abs(_sysVolume - 1.0f) > 0.001f)
                sp = new VolumeSampleProvider(sp) { Volume = _sysVolume };
            _sysProvider = sp;
        }
        if (_micBuf != null)
        {
            ISampleProvider sp = ToTargetFormat(_micBuf);
            if (Math.Abs(_micVolume - 1.0f) > 0.001f)
                sp = new VolumeSampleProvider(sp) { Volume = _micVolume };
            _micProvider = sp;
        }
    }



    private static ISampleProvider ToTargetFormat(BufferedWaveProvider source)
    {
        ISampleProvider sp = source.ToSampleProvider();
        if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
        else if (sp.WaveFormat.Channels > 2) sp = new MultiplexingSampleProvider(new[] { sp }, 2);
        if (sp.WaveFormat.SampleRate != TargetSampleRate)
            sp = new WdlResamplingSampleProvider(sp, TargetSampleRate);
        return sp;
    }

    private static MMDevice? FindCapture(MMDeviceEnumerator e, string? nameOrId)
    {
        if (string.IsNullOrWhiteSpace(nameOrId)) return null;
        foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            if (d.ID == nameOrId || d.FriendlyName == nameOrId) return d;
        return null;
    }

    private static MMDevice? FindRender(MMDeviceEnumerator e, string? nameOrId)
    {
        if (string.IsNullOrWhiteSpace(nameOrId)) return null;
        foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            if (d.ID == nameOrId || d.FriendlyName == nameOrId) return d;
        return null;
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        try { _cts?.Cancel(); } catch { }
        try { _sysCapture?.StopRecording(); } catch { }
        try { _micCapture?.StopRecording(); } catch { }
        try { _server?.Dispose(); } catch { }
        try { _sysCapture?.Dispose(); } catch { }
        try { _micCapture?.Dispose(); } catch { }
        _sysCapture = null;
        _micCapture = null;
        _server = null;
        Logger.Info("AudioPipeService stopped.");
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
