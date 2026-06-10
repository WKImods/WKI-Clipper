# Game-Only Audio Capture — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enable process-specific audio capture via WASAPI Process Loopback so only the game's audio ends up in clips — no Discord, no Chrome, no background noise.

**Architecture:** New `ProcessLoopbackCapture` class wraps the COM `ActivateAudioInterfaceAsync` API and implements `IWaveIn` (same interface as NAudio's `WasapiLoopbackCapture`). `AudioPipeService` switches between them based on a new `SystemCaptureMode` setting. A `GameProcessWatcher` polls for the target process and triggers buffer restarts on start/stop.

**Tech Stack:** .NET 8, WPF, NAudio 2.2.1, Windows WASAPI COM Interop (mmdevapi.dll), Win32 P/Invoke

---

## File Map

| File | Action | Responsibility |
|------|--------|---------------|
| `Native/AudioInterop.cs` | CREATE | COM interop structs, interfaces, P/Invoke for `ActivateAudioInterfaceAsync` |
| `Services/ProcessLoopbackCapture.cs` | CREATE | `IWaveIn` implementation using process-specific loopback |
| `Services/GameProcessWatcher.cs` | CREATE | Polls for target process, fires Found/Lost events |
| `Models/AppSettings.cs` | MODIFY | Add `AudioCaptureMode` enum + two new fields to `AudioSettings` |
| `Native/User32.cs` | MODIFY | Add `GetWindowThreadProcessId` for auto-detect |
| `Services/AudioPipeService.cs` | MODIFY | Switch sys capture source based on CaptureMode |
| `AppHost.cs` | MODIFY | Wire GameProcessWatcher, expose CurrentGamePid |
| `Views/AudioSettingsView.xaml.cs` | MODIFY | Replace Per-App hint card with Game-Audio card |
| `App.xaml.cs` | MODIFY | Wire GameProcessWatcher toast events |

---

### Task 1: COM Interop Definitions

**Files:**
- Create: `WKI_Clipper/Native/AudioInterop.cs`

All COM types needed for `ActivateAudioInterfaceAsync` in one file. No logic, just definitions.

- [ ] **Step 1: Create `Native/AudioInterop.cs`**

```csharp
using System;
using System.Runtime.InteropServices;

namespace WKI_Clipper.Native;

// ── Enums ──────────────────────────────────────────────────────────

internal enum AUDIOCLIENT_ACTIVATION_TYPE
{
    DEFAULT = 0,
    PROCESS_LOOPBACK = 1
}

internal enum PROCESS_LOOPBACK_MODE
{
    INCLUDE_TARGET_PROCESS_TREE = 0,
    EXCLUDE_TARGET_PROCESS_TREE = 1
}

[Flags]
internal enum AUDCLNT_STREAMFLAGS : uint
{
    NONE = 0,
    CROSSPROCESS        = 0x00010000,
    LOOPBACK            = 0x00020000,
    EVENTCALLBACK       = 0x00040000,
    NOPERSIST           = 0x00080000,
    AUTOCONVERTPCM      = 0x80000000,
    SRC_DEFAULT_QUALITY = 0x08000000,
}

// ── Structs ────────────────────────────────────────────────────────

[StructLayout(LayoutKind.Sequential)]
internal struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
{
    public uint TargetProcessId;
    public PROCESS_LOOPBACK_MODE ProcessLoopbackMode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AUDIOCLIENT_ACTIVATION_PARAMS
{
    public AUDIOCLIENT_ACTIVATION_TYPE ActivationType;
    public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPVARIANT : IDisposable
{
    public ushort vt;
    public ushort wReserved1;
    public ushort wReserved2;
    public ushort wReserved3;
    public IntPtr blob_cbSize;
    public IntPtr blob_pBlobData;

    /// <summary>Create a VT_BLOB wrapping an AUDIOCLIENT_ACTIVATION_PARAMS.</summary>
    public static PROPVARIANT ForActivationParams(AUDIOCLIENT_ACTIVATION_PARAMS p)
    {
        int size = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
        IntPtr ptr = Marshal.AllocCoTaskMem(size);
        Marshal.StructureToPtr(p, ptr, false);
        return new PROPVARIANT
        {
            vt = 0x1011,  // VT_BLOB (65 = VT_BLOB, but as PROPVARIANT we use the union directly)
            blob_cbSize = (IntPtr)size,
            blob_pBlobData = ptr
        };
    }

    public void Dispose()
    {
        if (blob_pBlobData != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(blob_pBlobData);
            blob_pBlobData = IntPtr.Zero;
        }
    }
}

// ── COM Interfaces ─────────────────────────────────────────────────

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    void GetActivateResult(out int activateResult,
        [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
}

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
}

// ── P/Invoke ───────────────────────────────────────────────────────

internal static class AudioInterop
{
    // The virtual device path for process loopback.
    public const string VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK =
        "VAD\\Process_Loopback";

    // IAudioClient GUID
    public static readonly Guid IID_IAudioClient =
        new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");

    [DllImport("mmdevapi.dll", PreserveSig = true)]
    public static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        ref PROPVARIANT activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build WKI_Clipper.sln -c Debug`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```
git add WKI_Clipper/Native/AudioInterop.cs
git commit -m "feat: add COM interop types for WASAPI Process Loopback API"
```

---

### Task 2: ProcessLoopbackCapture — IWaveIn Implementation

**Files:**
- Create: `WKI_Clipper/Services/ProcessLoopbackCapture.cs`

This is the core capture class. It activates an IAudioClient in process-loopback mode and fires `DataAvailable` events identical to `WasapiLoopbackCapture`.

- [ ] **Step 1: Create `Services/ProcessLoopbackCapture.cs`**

```csharp
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using WKI_Clipper.Native;

namespace WKI_Clipper.Services;

/// <summary>
/// Captures audio from a single process tree using the WASAPI Process Loopback API
/// (Windows 10 2004+). Implements IWaveIn so it can be used as a drop-in replacement
/// for WasapiLoopbackCapture in AudioPipeService.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ProcessLoopbackCapture : IWaveIn, IActivateAudioInterfaceCompletionHandler
{
    private readonly uint _processId;
    private AudioClient? _audioClient;
    private AudioCaptureClient? _captureClient;
    private WaveFormat? _waveFormat;
    private Thread? _captureThread;
    private volatile bool _isCapturing;
    private readonly ManualResetEventSlim _activationComplete = new(false);
    private Exception? _activationError;

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public WaveFormat WaveFormat
    {
        get => _waveFormat ?? WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        set { /* ignored — format is determined by the audio engine */ }
    }

    public ProcessLoopbackCapture(uint processId)
    {
        _processId = processId;
    }

    /// <summary>
    /// Activates the audio client for the target process and starts capturing.
    /// Blocks briefly while the async COM activation completes (~5-50ms).
    /// </summary>
    public void StartRecording()
    {
        if (_isCapturing) return;

        // Build activation params
        var loopbackParams = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE.PROCESS_LOOPBACK,
            ProcessLoopbackParams = new AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
            {
                TargetProcessId = _processId,
                ProcessLoopbackMode = PROCESS_LOOPBACK_MODE.INCLUDE_TARGET_PROCESS_TREE
            }
        };

        var propVariant = PROPVARIANT.ForActivationParams(loopbackParams);

        try
        {
            int hr = AudioInterop.ActivateAudioInterfaceAsync(
                AudioInterop.VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
                AudioInterop.IID_IAudioClient,
                ref propVariant,
                this, // completion handler
                out _);

            if (hr != 0)
                throw Marshal.GetExceptionForHR(hr)
                      ?? new COMException("ActivateAudioInterfaceAsync failed", hr);

            // Wait for the async activation to complete (typically < 50ms)
            if (!_activationComplete.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("WASAPI process loopback activation timed out");

            if (_activationError != null)
                throw _activationError;

            // Configure the audio client
            _waveFormat = _audioClient!.MixFormat;

            _audioClient.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback,
                hnsBufferDuration: 0, // default
                hnsPeriodicity: 0,
                _waveFormat,
                Guid.Empty);

            _captureClient = _audioClient.AudioCaptureClient;

            // Start the capture thread
            _isCapturing = true;
            _captureThread = new Thread(CaptureLoop)
            {
                Name = $"ProcessLoopback-{_processId}",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _captureThread.Start();

            _audioClient.Start();

            Logger.Info($"ProcessLoopbackCapture started for PID {_processId} | {_waveFormat}");
        }
        finally
        {
            propVariant.Dispose();
        }
    }

    /// <summary>COM callback — called on a thread pool thread when activation completes.</summary>
    void IActivateAudioInterfaceCompletionHandler.ActivateCompleted(
        IActivateAudioInterfaceAsyncOperation activateOperation)
    {
        try
        {
            activateOperation.GetActivateResult(out int hr, out object unk);
            if (hr != 0)
            {
                _activationError = Marshal.GetExceptionForHR(hr)
                                   ?? new COMException("Process loopback activation failed", hr);
                return;
            }

            // NAudio's AudioClient wraps IAudioClient COM pointer.
            // We need to get the raw IAudioClient from the activation result.
            var ptr = Marshal.GetIUnknownForObject(unk);
            try
            {
                _audioClient = new AudioClient(ptr);
            }
            finally
            {
                Marshal.Release(ptr);
            }
        }
        catch (Exception ex)
        {
            _activationError = ex;
        }
        finally
        {
            _activationComplete.Set();
        }
    }

    private void CaptureLoop()
    {
        try
        {
            // Use polling instead of event-driven — simpler and matches the
            // cadence of WasapiLoopbackCapture's default behavior (~10ms).
            while (_isCapturing)
            {
                Thread.Sleep(10);
                if (_captureClient == null || !_isCapturing) break;

                ReadAvailableData();
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"ProcessLoopbackCapture loop error (PID {_processId})", ex);
            RecordingStopped?.Invoke(this, new StoppedEventArgs(ex));
        }
    }

    private void ReadAvailableData()
    {
        int packetSize = _captureClient!.GetNextPacketSize();
        while (packetSize > 0 && _isCapturing)
        {
            var buffer = _captureClient.GetBuffer(out int numFramesRead, out AudioClientBufferFlags flags);

            if (numFramesRead > 0)
            {
                int bytesPerFrame = _waveFormat!.BlockAlign;
                int byteCount = numFramesRead * bytesPerFrame;
                var data = new byte[byteCount];

                if (flags.HasFlag(AudioClientBufferFlags.Silent))
                {
                    // Buffer is silent — return zeroes
                    Array.Clear(data, 0, byteCount);
                }
                else
                {
                    Marshal.Copy(buffer, data, 0, byteCount);
                }

                DataAvailable?.Invoke(this, new WaveInEventArgs(data, byteCount));
            }

            _captureClient.ReleaseBuffer(numFramesRead);
            packetSize = _captureClient.GetNextPacketSize();
        }
    }

    public void StopRecording()
    {
        _isCapturing = false;
        try { _audioClient?.Stop(); } catch { }
        _captureThread?.Join(TimeSpan.FromSeconds(2));
        _captureThread = null;
        RecordingStopped?.Invoke(this, new StoppedEventArgs());
        Logger.Info($"ProcessLoopbackCapture stopped for PID {_processId}");
    }

    public void Dispose()
    {
        StopRecording();
        _captureClient = null;
        try { _audioClient?.Dispose(); } catch { }
        _audioClient = null;
        _activationComplete.Dispose();
    }
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build WKI_Clipper.sln -c Debug`
Expected: Build succeeded. If NAudio's `AudioClient` constructor doesn't accept `IntPtr`, we need to adjust — check the NAudio 2.2.1 API and fix accordingly.

- [ ] **Step 3: Commit**

```
git add WKI_Clipper/Services/ProcessLoopbackCapture.cs
git commit -m "feat: add ProcessLoopbackCapture (IWaveIn via WASAPI Process Loopback)"
```

---

### Task 3: Settings Model Extension

**Files:**
- Modify: `WKI_Clipper/Models/AppSettings.cs`

- [ ] **Step 1: Add `AudioCaptureMode` enum and new fields**

Add after the existing `AudioSettings` class (after line 48), before `VideoSettings`:

```csharp
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioCaptureMode
{
    /// <summary>Standard WASAPI Loopback — captures ALL system audio.</summary>
    AllAudio,
    /// <summary>Process Loopback — captures ONLY audio from a specific process tree.</summary>
    GameOnly
}
```

Add two new properties to the `AudioSettings` class (after line 47, before the closing brace):

```csharp
    /// <summary>
    /// AllAudio = standard loopback (everything). GameOnly = only the selected process.
    /// </summary>
    public AudioCaptureMode SystemCaptureMode { get; set; } = AudioCaptureMode.AllAudio;

    /// <summary>
    /// Process name (without .exe) for GameOnly mode. null = auto-detect foreground window.
    /// </summary>
    public string? GameProcessName { get; set; }
```

- [ ] **Step 2: Build**

Run: `dotnet build WKI_Clipper.sln -c Debug`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```
git add WKI_Clipper/Models/AppSettings.cs
git commit -m "feat: add AudioCaptureMode + GameProcessName settings"
```

---

### Task 4: User32 — GetWindowThreadProcessId

**Files:**
- Modify: `WKI_Clipper/Native/User32.cs`

Needed for auto-detecting the foreground window's PID.

- [ ] **Step 1: Add P/Invoke declaration**

Add after line 42 (`public const uint PW_RENDERFULLCONTENT = 0x00000002;`), before the `RECT` struct:

```csharp
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
```

- [ ] **Step 2: Build**

Run: `dotnet build WKI_Clipper.sln -c Debug`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```
git add WKI_Clipper/Native/User32.cs
git commit -m "feat: add GetWindowThreadProcessId P/Invoke"
```

---

### Task 5: GameProcessWatcher

**Files:**
- Create: `WKI_Clipper/Services/GameProcessWatcher.cs`

Polls every 5 seconds for the target process. Fires events when it starts or stops.

- [ ] **Step 1: Create `Services/GameProcessWatcher.cs`**

```csharp
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WKI_Clipper.Services;

/// <summary>
/// Polls for a named process every 5 seconds and raises events when the
/// process starts or stops. Used to auto-restart the replay buffer in
/// GameOnly audio capture mode.
/// </summary>
public sealed class GameProcessWatcher : IDisposable
{
    private readonly string _processName;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;

    /// <summary>PID of the currently detected process, or null.</summary>
    public int? CurrentPid { get; private set; }

    /// <summary>Raised on a thread-pool thread when the target process is found.</summary>
    public event Action<int>? ProcessFound;

    /// <summary>Raised on a thread-pool thread when the target process exits.</summary>
    public event Action? ProcessLost;

    /// <param name="processName">Process name WITHOUT .exe extension (e.g. "ArmaReforger").</param>
    public GameProcessWatcher(string processName)
    {
        _processName = processName;
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoop(_cts.Token));
        Logger.Info($"GameProcessWatcher started, watching for: {_processName}");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _pollTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts?.Dispose();
        _cts = null;
        _pollTask = null;
        CurrentPid = null;
        Logger.Info("GameProcessWatcher stopped.");
    }

    private async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(5000, ct).ConfigureAwait(false);
                CheckProcess();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Logger.Error("GameProcessWatcher poll error", ex);
            }
        }
    }

    private void CheckProcess()
    {
        Process? found = null;
        try
        {
            // GetProcessesByName expects name without .exe
            var procs = Process.GetProcessesByName(_processName);
            if (procs.Length > 0)
            {
                // Take the oldest (lowest start time) to be stable across multi-instance
                found = procs.OrderBy(p =>
                {
                    try { return p.StartTime; }
                    catch { return DateTime.MaxValue; }
                }).First();
            }
            // Dispose the others
            foreach (var p in procs)
            {
                if (p != found) p.Dispose();
            }
        }
        catch { }

        if (found != null)
        {
            int pid = found.Id;
            found.Dispose();

            if (CurrentPid == null)
            {
                CurrentPid = pid;
                Logger.Info($"GameProcessWatcher: {_processName} found (PID {pid})");
                ProcessFound?.Invoke(pid);
            }
            else if (CurrentPid != pid)
            {
                // PID changed (process restarted)
                CurrentPid = pid;
                Logger.Info($"GameProcessWatcher: {_processName} restarted (new PID {pid})");
                ProcessFound?.Invoke(pid);
            }
        }
        else if (CurrentPid != null)
        {
            CurrentPid = null;
            Logger.Info($"GameProcessWatcher: {_processName} exited");
            ProcessLost?.Invoke();
        }
    }

    public void Dispose() => Stop();
}
```

- [ ] **Step 2: Build**

Run: `dotnet build WKI_Clipper.sln -c Debug`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```
git add WKI_Clipper/Services/GameProcessWatcher.cs
git commit -m "feat: add GameProcessWatcher (polls for target process, fires events)"
```

---

### Task 6: AudioPipeService — Process Loopback Integration

**Files:**
- Modify: `WKI_Clipper/Services/AudioPipeService.cs`

Switch system audio capture between `WasapiLoopbackCapture` and `ProcessLoopbackCapture` based on settings + available game PID.

- [ ] **Step 1: Change `_sysCapture` type and add new fields**

Replace lines 39 and 58-66 (the field declaration and constructor):

Change field at line 39 from:
```csharp
    private WasapiLoopbackCapture? _sysCapture;
```
to:
```csharp
    private IWaveIn? _sysCapture;
```

Add a new field after `_sysVolume` (after line 56):
```csharp
    private readonly AudioCaptureMode _captureMode;
    private readonly int? _gamePid;
```

Extend the constructor (currently lines 58-67) to accept an optional game PID:

Replace the constructor with:
```csharp
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
```

- [ ] **Step 2: Replace the system loopback init block**

Replace lines 102-141 (the `if (_wantSys)` block) with:

```csharp
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
```

- [ ] **Step 3: Fix the `Stop()` method**

The `Stop()` method at lines 433-447 calls `_sysCapture?.StopRecording()` and `_sysCapture?.Dispose()`. Since `_sysCapture` is now `IWaveIn?`, `StopRecording()` and `Dispose()` are still available (IWaveIn extends IDisposable and has StopRecording). No change needed — but verify it compiles.

- [ ] **Step 4: Build**

Run: `dotnet build WKI_Clipper.sln -c Debug`
Expected: Build succeeded. If `IWaveIn` doesn't expose `StopRecording` directly, adjust the Stop() method to cast or use pattern matching.

- [ ] **Step 5: Commit**

```
git add WKI_Clipper/Services/AudioPipeService.cs
git commit -m "feat: AudioPipeService switches between standard and process loopback"
```

---

### Task 7: AppHost — Wire GameProcessWatcher

**Files:**
- Modify: `WKI_Clipper/AppHost.cs`

AppHost owns the watcher and exposes the current game PID for AudioPipeService.

- [ ] **Step 1: Add watcher field and property**

Add after line 19 (`public FFmpegService FFmpeg { get; }`) :

```csharp
    public GameProcessWatcher? GameWatcher { get; private set; }
```

- [ ] **Step 2: Add watcher setup to `Initialize()`**

Replace `Initialize()` (lines 75-80) with:

```csharp
    public void Initialize()
    {
        Hotkeys.Initialize();
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
        if (audio.SystemCaptureMode == Models.AudioCaptureMode.GameOnly
            && !string.IsNullOrEmpty(audio.GameProcessName))
        {
            var gameName = audio.GameProcessName;
            GameWatcher = new GameProcessWatcher(gameName);
            GameWatcher.ProcessFound += pid =>
            {
                Logger.Info($"Game process found: {gameName} (PID {pid}) — restarting buffer");
                _ = ReplayBuffer.RestartIfRunningAsync();
                if (Settings.Current.Behavior.ShowToastNotifications)
                    ToastService.Show(Views.ToastKind.Info,
                        "Spiel erkannt",
                        $"{gameName} (PID {pid}) — nur Game-Audio aktiv");
            };
            GameWatcher.ProcessLost += () =>
            {
                Logger.Info("Game process lost — reverting to all audio, restarting buffer");
                _ = ReplayBuffer.RestartIfRunningAsync();
                if (Settings.Current.Behavior.ShowToastNotifications)
                    ToastService.Show(Views.ToastKind.Info,
                        "Spiel beendet",
                        $"{gameName} — alle Sounds aktiv");
            };
            GameWatcher.Start();
        }
    }
```

- [ ] **Step 3: Dispose the watcher**

In `Dispose()` (line 129), add before `Hotkeys.Dispose();`:

```csharp
        GameWatcher?.Dispose();
```

- [ ] **Step 4: Build**

Run: `dotnet build WKI_Clipper.sln -c Debug`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```
git add WKI_Clipper/AppHost.cs
git commit -m "feat: AppHost owns GameProcessWatcher, restarts buffer on process start/stop"
```

---

### Task 8: ReplayBufferService — Pass Game PID to AudioPipeService

**Files:**
- Modify: `WKI_Clipper/Services/ReplayBufferService.cs`

The buffer needs to pass the current game PID when creating AudioPipeService.

- [ ] **Step 1: Change constructor to accept AppHost reference**

Replace the constructor and field (lines 15-32):

Add a field after `_settings`:
```csharp
    private readonly AppHost? _appHost;
```

Add a second constructor overload (keep the existing one for backward compat):
```csharp
    public ReplayBufferService(SettingsService settings, AppHost? appHost = null)
    {
        _settings = settings;
        _appHost = appHost;
    }
```

Wait — actually, looking at the existing code, `ReplayBufferService` is created in AppHost's constructor (line 35), but AppHost doesn't exist yet at that point. Better approach: just read the PID from `App.Host.GameWatcher?.CurrentPid` at Start() time.

- [ ] **Step 1 (revised): Modify `Start()` to resolve game PID**

In `Start()`, replace line 52:
```csharp
        _audio = new AudioPipeService(_settings.Current);
```
with:
```csharp
        int? gamePid = null;
        if (_settings.Current.Audio.SystemCaptureMode == Models.AudioCaptureMode.GameOnly)
        {
            gamePid = App.Host?.GameWatcher?.CurrentPid;
            if (gamePid == null && !string.IsNullOrEmpty(_settings.Current.Audio.GameProcessName))
            {
                // Watcher hasn't found it yet — try a direct lookup
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
                Logger.Info($"ReplayBuffer: GameOnly mode, target PID {gamePid}");
            else
                Logger.Info("ReplayBuffer: GameOnly mode but process not found, falling back to AllAudio");
        }
        _audio = new AudioPipeService(_settings.Current, gamePid);
```

- [ ] **Step 2: Build**

Run: `dotnet build WKI_Clipper.sln -c Debug`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```
git add WKI_Clipper/Services/ReplayBufferService.cs
git commit -m "feat: ReplayBufferService resolves game PID for process loopback"
```

---

### Task 9: ManualRecordingService — Auto-Detect + Game PID

**Files:**
- Modify: `WKI_Clipper/Services/ManualRecordingService.cs`

Manual recording uses auto-detect (foreground PID) when no GameProcessName is set.

- [ ] **Step 1: Add PID resolution to `Start()`**

After line 42 (`_audio = new AudioPipeService(_settings.Current);`), replace that line with the PID resolution logic:

```csharp
        int? gamePid = null;
        if (_settings.Current.Audio.SystemCaptureMode == Models.AudioCaptureMode.GameOnly)
        {
            // Try the watcher's known PID first
            gamePid = App.Host?.GameWatcher?.CurrentPid;

            // If no watcher PID and no fixed process name → auto-detect foreground
            if (gamePid == null && string.IsNullOrEmpty(_settings.Current.Audio.GameProcessName))
            {
                var hwnd = Native.User32.GetForegroundWindow();
                if (hwnd != IntPtr.Zero)
                {
                    Native.User32.GetWindowThreadProcessId(hwnd, out uint pid);
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
```

- [ ] **Step 2: Build**

Run: `dotnet build WKI_Clipper.sln -c Debug`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```
git add WKI_Clipper/Services/ManualRecordingService.cs
git commit -m "feat: ManualRecording supports GameOnly mode with auto-detect foreground"
```

---

### Task 10: App.xaml.cs — Add `using` for ToastService in AppHost

**Files:**
- Modify: `WKI_Clipper/AppHost.cs`

Toast events are now wired inside `StartGameWatcherIfNeeded()` (Task 7). This task verifies the `using` import is present.

- [ ] **Step 1: Verify AppHost can call ToastService**

AppHost already imports `WKI_Clipper.Services` (line 4). `ToastService` is in that namespace. `Views.ToastKind` needs `WKI_Clipper.Views` — add this using if not present:

At the top of `AppHost.cs`, add:
```csharp
using WKI_Clipper.Views;
```

- [ ] **Step 2: Build**

Run: `dotnet build WKI_Clipper.sln -c Debug`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```
git add WKI_Clipper/AppHost.cs
git commit -m "fix: add Views using for ToastKind in AppHost"
```

---

### Task 11: AudioSettingsView — Game Audio Card

**Files:**
- Modify: `WKI_Clipper/Views/AudioSettingsView.xaml.cs`

Replace `BuildPerAppHintCard` with an interactive Game Audio settings card.

- [ ] **Step 1: Replace `BuildPerAppHintCard` method**

Delete the entire `BuildPerAppHintCard` method (lines 63-113) and replace with:

```csharp
    private FrameworkElement BuildGameAudioCard(AppHost host)
    {
        var stack = new StackPanel();

        // Header
        stack.Children.Add(new TextBlock
        {
            Text = "Spiel-Audio",
            FontWeight = System.Windows.FontWeights.SemiBold,
            FontSize = 14,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 6)
        });

        // Radio: AllAudio vs GameOnly
        var radioAll = new System.Windows.Controls.RadioButton
        {
            Content = "Alle Sounds aufnehmen",
            IsChecked = host.Settings.Current.Audio.SystemCaptureMode == Models.AudioCaptureMode.AllAudio,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 4)
        };
        var radioGame = new System.Windows.Controls.RadioButton
        {
            Content = "Nur Spiel-Audio aufnehmen",
            IsChecked = host.Settings.Current.Audio.SystemCaptureMode == Models.AudioCaptureMode.GameOnly,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 8)
        };
        stack.Children.Add(radioAll);
        stack.Children.Add(radioGame);

        // Process picker panel — only visible when GameOnly
        var pickerPanel = new StackPanel
        {
            Visibility = radioGame.IsChecked == true ? Visibility.Visible : Visibility.Collapsed,
            Margin = new Thickness(20, 0, 0, 0) // indent under radio
        };

        // Process dropdown
        var processBox = new System.Windows.Controls.ComboBox
        {
            MinWidth = 320,
            Margin = new Thickness(0, 0, 0, 8)
        };
        RefreshProcessList(processBox, host.Settings.Current.Audio.GameProcessName);

        var refreshBtn = new System.Windows.Controls.Button
        {
            Content = "Aktualisieren",
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(8, 0, 0, 0)
        };
        refreshBtn.Click += (_, _) =>
            RefreshProcessList(processBox, host.Settings.Current.Audio.GameProcessName);

        var processRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(refreshBtn, Dock.Right);
        processRow.Children.Add(refreshBtn);
        processRow.Children.Add(processBox);
        pickerPanel.Children.Add(BuildLabeledRow("Prozess", processRow));

        // Status text
        var statusText = new TextBlock
        {
            Style = (Style)FindResource("MutedStyle"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };
        UpdateGameStatus(statusText, host);
        pickerPanel.Children.Add(statusText);

        stack.Children.Add(pickerPanel);

        // Explanation
        var hint = new TextBlock
        {
            Text = "Im Modus \"Nur Spiel-Audio\" wird nur der Sound des ausgewaehlten Prozesses aufgenommen. Discord, Browser und andere Apps sind automatisch stumm im Clip. Der Buffer startet automatisch neu wenn das Spiel erkannt wird.",
            Style = (Style)FindResource("MutedStyle"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        stack.Children.Add(hint);

        // Event handlers
        radioAll.Checked += (_, _) =>
        {
            host.Settings.Current.Audio.SystemCaptureMode = Models.AudioCaptureMode.AllAudio;
            host.Settings.Save();
            pickerPanel.Visibility = Visibility.Collapsed;
            host.StartGameWatcherIfNeeded();
            _ = host.ReplayBuffer.RestartIfRunningAsync();
        };
        radioGame.Checked += (_, _) =>
        {
            host.Settings.Current.Audio.SystemCaptureMode = Models.AudioCaptureMode.GameOnly;
            host.Settings.Save();
            pickerPanel.Visibility = Visibility.Visible;
            host.StartGameWatcherIfNeeded();
            _ = host.ReplayBuffer.RestartIfRunningAsync();
        };
        processBox.SelectionChanged += (_, _) =>
        {
            if (processBox.SelectedItem is ProcessListEntry entry)
            {
                host.Settings.Current.Audio.GameProcessName =
                    entry.IsAutoDetect ? null : entry.ProcessName;
                host.Settings.Save();
                host.StartGameWatcherIfNeeded();
                _ = host.ReplayBuffer.RestartIfRunningAsync();
                UpdateGameStatus(statusText, host);
            }
        };

        return Card(stack);
    }

    private static void RefreshProcessList(System.Windows.Controls.ComboBox box, string? currentName)
    {
        box.Items.Clear();

        // First entry: auto-detect
        var autoEntry = new ProcessListEntry("Automatisch (Vordergrundfenster)", null, true);
        box.Items.Add(autoEntry);

        // All processes with a main window
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proc in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (proc.MainWindowHandle == IntPtr.Zero) continue;
                string name = proc.ProcessName;
                if (!seen.Add(name)) continue;

                string title = proc.MainWindowTitle;
                string display = string.IsNullOrEmpty(title) ? name : $"{title} ({name})";
                var entry = new ProcessListEntry(display, name, false);
                box.Items.Add(entry);
            }
            catch { }
            finally { proc.Dispose(); }
        }

        // Select current
        if (string.IsNullOrEmpty(currentName))
        {
            box.SelectedIndex = 0; // auto-detect
        }
        else
        {
            bool found = false;
            for (int i = 1; i < box.Items.Count; i++)
            {
                if (box.Items[i] is ProcessListEntry e
                    && string.Equals(e.ProcessName, currentName, StringComparison.OrdinalIgnoreCase))
                {
                    box.SelectedIndex = i;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                // Process not running — add a placeholder
                var placeholder = new ProcessListEntry($"{currentName} (nicht aktiv)", currentName, false);
                box.Items.Add(placeholder);
                box.SelectedIndex = box.Items.Count - 1;
            }
        }
    }

    private static void UpdateGameStatus(TextBlock statusText, AppHost host)
    {
        if (host.Settings.Current.Audio.SystemCaptureMode != Models.AudioCaptureMode.GameOnly)
        {
            statusText.Text = "";
            return;
        }
        var name = host.Settings.Current.Audio.GameProcessName ?? "Vordergrundfenster";
        var pid = host.GameWatcher?.CurrentPid;
        if (pid.HasValue)
        {
            statusText.Text = $"Aktiv: {name} (PID {pid}) — nur Game-Audio wird aufgenommen";
            statusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x4A, 0xD8, 0x6A));
        }
        else
        {
            statusText.Text = $"{name} nicht gestartet — aktuell werden alle Sounds aufgenommen";
            statusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xE0, 0xA8, 0x40));
        }
    }

    private sealed record ProcessListEntry(string DisplayName, string? ProcessName, bool IsAutoDetect)
    {
        public override string ToString() => DisplayName;
    }
```

- [ ] **Step 2: Update `OnLoaded` to use new method name**

In `OnLoaded` (line 55), change:
```csharp
        RowsContainer.Children.Add(BuildPerAppHintCard(host));
```
to:
```csharp
        RowsContainer.Children.Add(BuildGameAudioCard(host));
```

- [ ] **Step 3: Build**

Run: `dotnet build WKI_Clipper.sln -c Debug`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```
git add WKI_Clipper/Views/AudioSettingsView.xaml.cs
git commit -m "feat: replace per-app hint with interactive Game Audio settings card"
```

---

### Task 12: AppHost Constructor — Pass Self to ReplayBufferService

**Files:**
- Modify: `WKI_Clipper/AppHost.cs`

No change needed here — Task 8 already handles PID resolution by reading `App.Host.GameWatcher?.CurrentPid` directly. This task is a verification checkpoint.

- [ ] **Step 1: Full build + verify no circular dependencies**

Run: `dotnet build WKI_Clipper.sln -c Debug`
Expected: Build succeeded, 0 warnings about circular references.

- [ ] **Step 2: Run the dev build to smoke-test**

Run:
```
dotnet build WKI_Clipper.sln -c Debug
.\WKI_Clipper\bin\Debug\net8.0-windows10.0.19041.0\WKI_Clipper.exe
```

Verify:
1. App starts, tray icon appears
2. Strg+Alt+G opens overlay
3. Audio tab shows "Spiel-Audio" card with radio buttons
4. "Alle Sounds aufnehmen" is selected by default
5. Switching to "Nur Spiel-Audio" shows the process dropdown
6. Dropdown lists running windowed processes
7. Switching back to "Alle Sounds" hides the dropdown
8. No crashes, no exceptions in the log

- [ ] **Step 3: Test GameOnly with a real process**

1. Open any app with audio (e.g. Chrome playing a video)
2. In Audio Settings, switch to "Nur Spiel-Audio"
3. Select Chrome from the dropdown
4. Buffer should restart (check log)
5. Press F9 — clip should contain ONLY Chrome's audio
6. Switch back to "Alle Sounds" — buffer restarts, F9 captures everything

- [ ] **Step 4: Commit any fixes**

```
git add -u
git commit -m "fix: integration fixes from smoke testing"
```

---

### Task 13: Final Cleanup + Git Push

**Files:**
- No new files

- [ ] **Step 1: Check for unused code**

The old `BuildMixGraph()` method in `AudioPipeService.cs` (lines 379-405) is already unused (leftover from the MixingSampleProvider era). Remove it if the build succeeds without it:

Delete lines 379-405 (`private ISampleProvider? BuildMixGraph()` and its body).

- [ ] **Step 2: Build final**

Run: `dotnet build WKI_Clipper.sln -c Debug`
Expected: Clean build, 0 errors, 0 warnings about unused code.

- [ ] **Step 3: Commit and push**

```
git add -u
git commit -m "chore: remove unused BuildMixGraph method"
git push origin master
```

- [ ] **Step 4: Update project memory**

Update `~/.claude/projects/.../memory/project_wki_clipper.md`:
- Add Game-Only Audio Capture feature to the status section
- Move WASAPI Process Loopback from TODO to done
- Document the new settings fields
