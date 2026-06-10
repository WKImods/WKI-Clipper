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
                0, // hnsBufferDuration — default
                0, // hnsPeriodicity
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

            // NAudio's AudioClient wraps an IAudioClient COM interface reference.
            // The activated object is already an RCW for IUnknown — QI it for IAudioClient.
            var audioClientInterface = (NAudio.CoreAudioApi.Interfaces.IAudioClient)unk;
            _audioClient = new AudioClient(audioClientInterface);
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
