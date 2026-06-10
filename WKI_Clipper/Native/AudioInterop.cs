using System;
using System.Runtime.InteropServices;
using NAudio.Wave;

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
internal enum AUDCLNT_SHAREMODE
{
    SHARED = 0,
    EXCLUSIVE = 1
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

[Flags]
internal enum AUDCLNT_BUFFERFLAGS
{
    NONE = 0,
    DATA_DISCONTINUITY = 0x1,
    SILENT = 0x2,
    TIMESTAMP_ERROR = 0x4
}

// ── Native Structs ────────────────────────────────────────────────

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
            vt = 0x0041,  // VT_BLOB (65)
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

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WAVEFORMATEX
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;

    /// <summary>Convert to NAudio WaveFormat for use in WaveInEventArgs.</summary>
    public WaveFormat ToWaveFormat()
    {
        if (wFormatTag == 0xFFFE) // WAVE_FORMAT_EXTENSIBLE
        {
            // The WAVEFORMATEXTENSIBLE struct follows WAVEFORMATEX
            // For IEEE float, the SubFormat GUID starts at offset +2 after cbSize
            return WaveFormat.CreateIeeeFloatWaveFormat((int)nSamplesPerSec, nChannels);
        }
        if (wFormatTag == 3) // IEEE_FLOAT
            return WaveFormat.CreateIeeeFloatWaveFormat((int)nSamplesPerSec, nChannels);
        if (wFormatTag == 1) // PCM
            return new WaveFormat((int)nSamplesPerSec, wBitsPerSample, nChannels);
        // Fallback
        return WaveFormat.CreateIeeeFloatWaveFormat((int)nSamplesPerSec, nChannels);
    }
}

// ── COM Interfaces (exact Windows SDK vtable order) ───────────────

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

/// <summary>
/// IAudioClient COM interface with exact Windows SDK vtable layout.
/// All methods use PreserveSig to return raw HRESULTs.
/// We define our OWN interface because NAudio's internal IAudioClient has
/// a vtable mismatch that causes E_NOTIMPL when used with Process Loopback.
/// </summary>
[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWasapiAudioClient
{
    // Slot 3
    [PreserveSig]
    int Initialize(
        AUDCLNT_SHAREMODE shareMode,
        AUDCLNT_STREAMFLAGS streamFlags,
        long hnsBufferDuration,
        long hnsPeriodicity,
        IntPtr pFormat,     // WAVEFORMATEX*
        IntPtr audioSessionGuid);  // LPCGUID, pass IntPtr.Zero

    // Slot 4
    [PreserveSig]
    int GetBufferSize(out uint numBufferFrames);

    // Slot 5
    [PreserveSig]
    int GetStreamLatency(out long hnsLatency);

    // Slot 6
    [PreserveSig]
    int GetCurrentPadding(out uint numPaddingFrames);

    // Slot 7
    [PreserveSig]
    int IsFormatSupported(
        AUDCLNT_SHAREMODE shareMode,
        IntPtr pFormat,
        out IntPtr ppClosestMatch);

    // Slot 8
    [PreserveSig]
    int GetMixFormat(out IntPtr ppDeviceFormat);

    // Slot 9
    [PreserveSig]
    int GetDevicePeriod(out long hnsDefaultDevicePeriod, out long hnsMinimumDevicePeriod);

    // Slot 10
    [PreserveSig]
    int Start();

    // Slot 11
    [PreserveSig]
    int Stop();

    // Slot 12
    [PreserveSig]
    int Reset();

    // Slot 13
    [PreserveSig]
    int SetEventHandle(IntPtr eventHandle);

    // Slot 14
    [PreserveSig]
    int GetService(
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
}

/// <summary>
/// IAudioCaptureClient COM interface with exact Windows SDK vtable layout.
/// </summary>
[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWasapiCaptureClient
{
    // Slot 3
    [PreserveSig]
    int GetBuffer(
        out IntPtr ppData,
        out uint pNumFramesToRead,
        out AUDCLNT_BUFFERFLAGS pdwFlags,
        out ulong pu64DevicePosition,
        out ulong pu64QPCPosition);

    // Slot 4
    [PreserveSig]
    int ReleaseBuffer(uint numFramesRead);

    // Slot 5
    [PreserveSig]
    int GetNextPacketSize(out uint pNumFramesInNextPacket);
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

    // IAudioCaptureClient GUID
    public static readonly Guid IID_IAudioCaptureClient =
        new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    [DllImport("mmdevapi.dll", PreserveSig = true)]
    public static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        ref PROPVARIANT activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [DllImport("ole32.dll")]
    internal static extern void CoTaskMemFree(IntPtr ptr);
}
