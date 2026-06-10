using System;
using System.Runtime.InteropServices;

namespace WKI_Clipper.Native;

/// <summary>
/// Raw COM vtable wrapper for IAudioClient, bypassing .NET COM interop entirely.
/// This avoids RCW cache conflicts with NAudio's IAudioClient (same GUID) and
/// cross-apartment marshaling issues with Process Loopback activation.
///
/// Vtable layout (x64, each slot = 8 bytes):
///   0: QueryInterface  1: AddRef  2: Release
///   3: Initialize  4: GetBufferSize  5: GetStreamLatency
///   6: GetCurrentPadding  7: IsFormatSupported  8: GetMixFormat
///   9: GetDevicePeriod  10: Start  11: Stop
///   12: Reset  13: SetEventHandle  14: GetService
/// </summary>
internal sealed class RawAudioClient : IDisposable
{
    private IntPtr _pAudioClient;

    // Delegate types matching native COM method signatures
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int InitializeDelegate(
        IntPtr @this, int shareMode, uint streamFlags,
        long hnsBufferDuration, long hnsPeriodicity,
        IntPtr pFormat, IntPtr audioSessionGuid);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetMixFormatDelegate(IntPtr @this, out IntPtr ppDeviceFormat);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int StartDelegate(IntPtr @this);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int StopDelegate(IntPtr @this);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetServiceDelegate(IntPtr @this, ref Guid riid, out IntPtr ppv);

    // Cached delegates
    private InitializeDelegate? _initialize;
    private GetMixFormatDelegate? _getMixFormat;
    private StartDelegate? _start;
    private StopDelegate? _stop;
    private GetServiceDelegate? _getService;

    private RawAudioClient(IntPtr pAudioClient)
    {
        _pAudioClient = pAudioClient;
        CacheVtable();
    }

    /// <summary>
    /// Create a RawAudioClient from the activated COM object.
    /// QIs for IAudioClient using the raw COM pointer, avoiding .NET RCW.
    /// </summary>
    public static RawAudioClient FromActivatedObject(object comObject)
    {
        IntPtr pUnk = Marshal.GetIUnknownForObject(comObject);
        try
        {
            Guid iid = AudioInterop.IID_IAudioClient;
            int hr = Marshal.QueryInterface(pUnk, ref iid, out IntPtr pAudioClient);
            if (hr != 0)
                throw new COMException($"QueryInterface for IAudioClient failed: 0x{hr:X8}", hr);
            // pAudioClient has an extra ref from QI — that's our ref, don't release it
            return new RawAudioClient(pAudioClient);
        }
        finally
        {
            Marshal.Release(pUnk);
        }
    }

    private void CacheVtable()
    {
        IntPtr vtable = Marshal.ReadIntPtr(_pAudioClient);
        _initialize = GetDelegate<InitializeDelegate>(vtable, 3);
        _getMixFormat = GetDelegate<GetMixFormatDelegate>(vtable, 8);
        _start = GetDelegate<StartDelegate>(vtable, 10);
        _stop = GetDelegate<StopDelegate>(vtable, 11);
        _getService = GetDelegate<GetServiceDelegate>(vtable, 14);
    }

    private static T GetDelegate<T>(IntPtr vtable, int slot) where T : Delegate
    {
        IntPtr funcPtr = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(funcPtr);
    }

    public int GetMixFormat(out IntPtr ppDeviceFormat)
        => _getMixFormat!(_pAudioClient, out ppDeviceFormat);

    public int Initialize(int shareMode, uint streamFlags,
        long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat)
        => _initialize!(_pAudioClient, shareMode, streamFlags,
            hnsBufferDuration, hnsPeriodicity, pFormat, IntPtr.Zero);

    public int Start() => _start!(_pAudioClient);
    public int Stop() => _stop!(_pAudioClient);

    public int GetService(Guid riid, out IntPtr ppv)
    {
        return _getService!(_pAudioClient, ref riid, out ppv);
    }

    public void Dispose()
    {
        if (_pAudioClient != IntPtr.Zero)
        {
            Marshal.Release(_pAudioClient);
            _pAudioClient = IntPtr.Zero;
        }
    }
}

/// <summary>
/// Raw COM vtable wrapper for IAudioCaptureClient.
///
/// Vtable layout:
///   0-2: IUnknown
///   3: GetBuffer  4: ReleaseBuffer  5: GetNextPacketSize
/// </summary>
internal sealed class RawCaptureClient : IDisposable
{
    private IntPtr _pCaptureClient;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBufferDelegate(
        IntPtr @this, out IntPtr ppData, out uint pNumFramesToRead,
        out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseBufferDelegate(IntPtr @this, uint numFramesRead);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetNextPacketSizeDelegate(IntPtr @this, out uint pNumFramesInNextPacket);

    private GetBufferDelegate? _getBuffer;
    private ReleaseBufferDelegate? _releaseBuffer;
    private GetNextPacketSizeDelegate? _getNextPacketSize;

    public RawCaptureClient(IntPtr pCaptureClient)
    {
        _pCaptureClient = pCaptureClient;
        IntPtr vtable = Marshal.ReadIntPtr(pCaptureClient);
        _getBuffer = GetDelegate<GetBufferDelegate>(vtable, 3);
        _releaseBuffer = GetDelegate<ReleaseBufferDelegate>(vtable, 4);
        _getNextPacketSize = GetDelegate<GetNextPacketSizeDelegate>(vtable, 5);
    }

    private static T GetDelegate<T>(IntPtr vtable, int slot) where T : Delegate
    {
        IntPtr funcPtr = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(funcPtr);
    }

    public int GetBuffer(out IntPtr ppData, out uint numFramesToRead,
        out uint flags, out ulong devicePosition, out ulong qpcPosition)
        => _getBuffer!(_pCaptureClient, out ppData, out numFramesToRead,
            out flags, out devicePosition, out qpcPosition);

    public int ReleaseBuffer(uint numFramesRead)
        => _releaseBuffer!(_pCaptureClient, numFramesRead);

    public int GetNextPacketSize(out uint numFramesInNextPacket)
        => _getNextPacketSize!(_pCaptureClient, out numFramesInNextPacket);

    public void Dispose()
    {
        if (_pCaptureClient != IntPtr.Zero)
        {
            Marshal.Release(_pCaptureClient);
            _pCaptureClient = IntPtr.Zero;
        }
    }
}
