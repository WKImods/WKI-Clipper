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
            vt = 0x1011,  // VT_BLOB
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
