using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using NAudio.Wave;
using WKI_Clipper.Native;

namespace WKI_Clipper.Services;

/// <summary>
/// Captures audio from a single process tree using the WASAPI Process Loopback API
/// (Windows 10 2004+). Implements IWaveIn so it can be used as a drop-in replacement
/// for WasapiLoopbackCapture in AudioPipeService.
///
/// Uses raw COM vtable calls (RawAudioClient / RawCaptureClient) to bypass .NET COM
/// interop entirely. This avoids RCW cache conflicts with NAudio's IAudioClient
/// (same GUID) and cross-apartment marshaling issues.
///
/// All IAudioClient operations happen on MTA threads (callback + capture thread)
/// because the Process Loopback COM object doesn't support cross-apartment QI.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ProcessLoopbackCapture : IWaveIn, IActivateAudioInterfaceCompletionHandler
{
    private readonly uint _processId;
    private RawAudioClient? _audioClient;
    private RawCaptureClient? _captureClient;
    private WaveFormat? _waveFormat;
    private int _blockAlign;
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

    public void StartRecording()
    {
        if (_isCapturing) return;

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
                this,
                out _);

            if (hr != 0)
                throw Marshal.GetExceptionForHR(hr)
                      ?? new COMException("ActivateAudioInterfaceAsync failed", hr);

            if (!_activationComplete.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("WASAPI process loopback activation timed out");

            if (_activationError != null)
                throw _activationError;

            _isCapturing = true;
            _captureThread = new Thread(CaptureLoop)
            {
                Name = $"ProcessLoopback-{_processId}",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _captureThread.Start();

            Logger.Info($"ProcessLoopbackCapture started for PID {_processId} | {_waveFormat}");
        }
        finally
        {
            propVariant.Dispose();
        }
    }

    /// <summary>
    /// COM callback — called on an MTA thread pool thread when activation completes.
    /// All IAudioClient init happens here to stay in the same COM apartment.
    /// Uses raw COM vtable calls to avoid .NET RCW issues.
    /// </summary>
    void IActivateAudioInterfaceCompletionHandler.ActivateCompleted(
        IActivateAudioInterfaceAsyncOperation activateOperation)
    {
        try
        {
            activateOperation.GetActivateResult(out int hrActivate, out object unk);
            if (hrActivate != 0)
            {
                Logger.Error($"ProcessLoopbackCapture activation failed: HRESULT=0x{hrActivate:X8} for PID {_processId}");
                _activationError = Marshal.GetExceptionForHR(hrActivate)
                                   ?? new COMException($"Process loopback activation failed (HRESULT 0x{hrActivate:X8})", hrActivate);
                return;
            }

            // Wrap in raw vtable accessor — bypasses .NET COM interop entirely
            _audioClient = RawAudioClient.FromActivatedObject(unk);

            // Try GetMixFormat first; Process Loopback may return E_NOTIMPL
            int hr = _audioClient.GetMixFormat(out IntPtr pFormat);
            if (hr == 0 && pFormat != IntPtr.Zero)
            {
                var wfx = Marshal.PtrToStructure<WAVEFORMATEX>(pFormat);
                _waveFormat = wfx.ToWaveFormat();
                _blockAlign = wfx.nBlockAlign;

                Logger.Info($"ProcessLoopbackCapture: GetMixFormat succeeded: {_waveFormat}");

                // Initialize with the device's native format
                hr = _audioClient.Initialize(
                    (int)AUDCLNT_SHAREMODE.SHARED,
                    (uint)AUDCLNT_STREAMFLAGS.LOOPBACK,
                    0, 0, pFormat);

                AudioInterop.CoTaskMemFree(pFormat);

                if (hr != 0)
                {
                    Logger.Warn($"ProcessLoopbackCapture: Initialize with MixFormat failed (0x{hr:X8}), trying fallback format");
                    InitializeWithFallbackFormat();
                }
            }
            else
            {
                Logger.Info($"ProcessLoopbackCapture: GetMixFormat not supported (0x{hr:X8}), using fallback format");
                if (pFormat != IntPtr.Zero) AudioInterop.CoTaskMemFree(pFormat);
                InitializeWithFallbackFormat();
            }

            // Get capture client via raw GetService
            Guid iidCapture = AudioInterop.IID_IAudioCaptureClient;
            hr = _audioClient.GetService(iidCapture, out IntPtr pCaptureClient);
            if (hr != 0)
            {
                Logger.Error($"ProcessLoopbackCapture: GetService(IAudioCaptureClient) failed: 0x{hr:X8}");
                Marshal.ThrowExceptionForHR(hr);
            }
            _captureClient = new RawCaptureClient(pCaptureClient);

            Logger.Info($"ProcessLoopbackCapture: COM init succeeded for PID {_processId} | {_waveFormat} | blockAlign={_blockAlign}");
        }
        catch (Exception ex)
        {
            Logger.Error($"ProcessLoopbackCapture: activation/init exception for PID {_processId}", ex);
            _activationError = ex;
        }
        finally
        {
            _activationComplete.Set();
        }
    }

    /// <summary>
    /// Initialize the audio client with a standard format (48kHz, stereo, float)
    /// plus AUTOCONVERTPCM so the audio engine handles any format conversion.
    /// Used when GetMixFormat is not available (common with process loopback).
    /// </summary>
    private void InitializeWithFallbackFormat()
    {
        _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        _blockAlign = _waveFormat.BlockAlign; // 8 bytes (2ch x 4 bytes float)

        // Marshal the WaveFormat to native WAVEFORMATEX
        int wfxSize = 18; // sizeof(WAVEFORMATEX)
        IntPtr pWfx = Marshal.AllocCoTaskMem(wfxSize);
        try
        {
            Marshal.WriteInt16(pWfx, 0, 3);         // wFormatTag = IEEE_FLOAT
            Marshal.WriteInt16(pWfx, 2, 2);         // nChannels = 2
            Marshal.WriteInt32(pWfx, 4, 48000);     // nSamplesPerSec
            Marshal.WriteInt32(pWfx, 8, 48000 * 8); // nAvgBytesPerSec = 48000 * 2ch * 4bytes
            Marshal.WriteInt16(pWfx, 12, 8);        // nBlockAlign = 2 * 4
            Marshal.WriteInt16(pWfx, 14, 32);       // wBitsPerSample = 32
            Marshal.WriteInt16(pWfx, 16, 0);        // cbSize = 0

            uint flags = (uint)(AUDCLNT_STREAMFLAGS.LOOPBACK
                              | AUDCLNT_STREAMFLAGS.AUTOCONVERTPCM
                              | AUDCLNT_STREAMFLAGS.SRC_DEFAULT_QUALITY);

            int hr = _audioClient!.Initialize(
                (int)AUDCLNT_SHAREMODE.SHARED,
                flags,
                200 * 10000, // 200ms buffer
                0,
                pWfx);

            if (hr != 0)
            {
                Logger.Error($"ProcessLoopbackCapture: Initialize with fallback format also failed: 0x{hr:X8}");
                Marshal.ThrowExceptionForHR(hr);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(pWfx);
        }
    }

    private void CaptureLoop()
    {
        try
        {
            int hr = _audioClient!.Start();
            if (hr != 0)
            {
                Logger.Error($"ProcessLoopbackCapture: AudioClient.Start() failed: 0x{hr:X8}");
                Marshal.ThrowExceptionForHR(hr);
            }

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
        int hr = _captureClient!.GetNextPacketSize(out uint packetSize);
        if (hr != 0) return;

        while (packetSize > 0 && _isCapturing)
        {
            hr = _captureClient.GetBuffer(
                out IntPtr pData,
                out uint numFramesRead,
                out uint flags,
                out _,
                out _);

            if (hr != 0) break;

            if (numFramesRead > 0)
            {
                int byteCount = (int)numFramesRead * _blockAlign;
                var data = new byte[byteCount];

                if ((flags & (uint)AUDCLNT_BUFFERFLAGS.SILENT) != 0)
                {
                    Array.Clear(data, 0, byteCount);
                }
                else
                {
                    Marshal.Copy(pData, data, 0, byteCount);
                }

                DataAvailable?.Invoke(this, new WaveInEventArgs(data, byteCount));
            }

            _captureClient.ReleaseBuffer(numFramesRead);

            hr = _captureClient.GetNextPacketSize(out packetSize);
            if (hr != 0) break;
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
        _captureClient?.Dispose();
        _captureClient = null;
        _audioClient?.Dispose();
        _audioClient = null;
        _activationComplete.Dispose();
    }
}
