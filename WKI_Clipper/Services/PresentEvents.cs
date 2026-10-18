using System;
using System.Buffers.Binary;

namespace WKI_Clipper.Services;

/// <summary>Which graphics path the measured frames come from (for the overlay / diagnostics).</summary>
public enum PresentApi : byte
{
    Unknown = 0,
    /// <summary>D3D10/11/12 via IDXGISwapChain::Present.</summary>
    Dxgi = 1,
    /// <summary>Direct3D 9 (Ex).</summary>
    D3D9 = 2,
    /// <summary>No runtime events — counted from the kernel's present events (Vulkan / OpenGL).</summary>
    Other = 3,
}

/// <summary>The ETW event a present was counted from.</summary>
internal enum PresentSource : byte
{
    Dxgi = 1,
    D3D9 = 2,
    KernelPresentHistory = 3,
    KernelPresentInfo = 4,
    KernelPresentHistoryDetailed = 5,
}

/// <summary>One present call of one process, timestamp in raw QPC ticks.</summary>
internal readonly record struct PresentEvent(int ProcessId, PresentSource Source, ulong SwapChain, long Timestamp)
{
    public bool IsRuntime => Source is PresentSource.Dxgi or PresentSource.D3D9;

    public PresentApi Api => Source switch
    {
        PresentSource.Dxgi => PresentApi.Dxgi,
        PresentSource.D3D9 => PresentApi.D3D9,
        _ => PresentApi.Other,
    };

    /// <summary>
    /// Identifies one present stream: the swap chain, tagged with the event source so a DXGI and
    /// a D3D9 swap chain (or the two kernel markers) never merge. User-mode pointers fit in 56 bits.
    /// </summary>
    public ulong StreamKey => ((ulong)Source << 56) | (SwapChain & 0x00FF_FFFF_FFFF_FFFFUL);
}

/// <summary>The EVENT_HEADER fields the FPS engine needs.</summary>
internal readonly record struct EtwEventHeader(Guid ProviderId, ushort EventId, ushort Flags, int ProcessId, long Timestamp);

internal enum EtwEventKind { Ignore, Present, Lost }

/// <summary>
/// Turns raw ETW events into presents of the target process. Pure: no ETW, no clock. Event ids,
/// payload layouts and the test-present rule follow the providers' manifests and PresentMon
/// (MIT) — Microsoft_Windows_DXGI.h, Microsoft_Windows_D3D9.h, Microsoft_Windows_DxgKrnl.h.
/// </summary>
internal static class PresentEventParser
{
    public static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    public static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    public static readonly Guid DxgKrnlProvider = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    /// <summary>RT_LostEvent: ETW's own notice that a real-time consumer lost events or buffers.</summary>
    public static readonly Guid LostEventProvider = new("6A399AE0-4BC6-4DE9-870B-3657F8947E7E");

    public const ushort DxgiPresentStart = 42;
    public const ushort DxgiPresentMpoStart = 55;
    public const ushort D3D9PresentStart = 1;
    public const ushort KernelPresentHistoryStart = 0xAB;
    public const ushort KernelPresentInfo = 0xB8;
    public const ushort KernelPresentHistoryDetailedStart = 0xD7;

    /// <summary>Provider keywords the present events carry (from the manifests).</summary>
    public const ulong DxgiEventsKeyword = 0x2;
    public const ulong D3D9EventsKeyword = 0x2;
    public const ulong DxgKrnlPresentKeyword = 0x8000000;

    /// <summary>DXGI_PRESENT_TEST: a "would this work?" probe, not a frame.</summary>
    public const uint DxgiPresentTest = 0x1;

    private const ushort Header32Bit = 0x0020;   // EVENT_HEADER_FLAG_32_BIT_HEADER

    public static EtwEventKind Classify(in EtwEventHeader header, ReadOnlySpan<byte> userData, int targetPid, out PresentEvent present)
    {
        present = default;

        if (header.ProviderId == LostEventProvider)
        {
            present = new PresentEvent(header.ProcessId, default, 0, header.Timestamp);
            return EtwEventKind.Lost;
        }

        if (targetPid == 0 || header.ProcessId != targetPid) return EtwEventKind.Ignore;

        // Pointer-sized fields follow the bitness of the logging process (a 32-bit game on 64-bit Windows).
        int ptr = (header.Flags & Header32Bit) != 0 ? 4 : 8;

        if (header.ProviderId == DxgiProvider)
        {
            if (header.EventId is not (DxgiPresentStart or DxgiPresentMpoStart)) return EtwEventKind.Ignore;
            // pIDXGISwapChain, Flags, SyncInterval
            if (userData.Length < ptr + 4) return EtwEventKind.Ignore;
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(userData.Slice(ptr));
            if ((flags & DxgiPresentTest) != 0) return EtwEventKind.Ignore;
            present = new PresentEvent(header.ProcessId, PresentSource.Dxgi, ReadPointer(userData, ptr), header.Timestamp);
            return EtwEventKind.Present;
        }

        if (header.ProviderId == D3D9Provider)
        {
            if (header.EventId != D3D9PresentStart) return EtwEventKind.Ignore;
            // pSwapchain, Flags — D3D9 has no test-present flag.
            if (userData.Length < ptr) return EtwEventKind.Ignore;
            present = new PresentEvent(header.ProcessId, PresentSource.D3D9, ReadPointer(userData, ptr), header.Timestamp);
            return EtwEventKind.Present;
        }

        if (header.ProviderId == DxgKrnlProvider)
        {
            PresentSource? source = header.EventId switch
            {
                KernelPresentHistoryStart => PresentSource.KernelPresentHistory,
                KernelPresentInfo => PresentSource.KernelPresentInfo,
                KernelPresentHistoryDetailedStart => PresentSource.KernelPresentHistoryDetailed,
                _ => null,
            };
            if (source is null) return EtwEventKind.Ignore;
            // No swap chain in these payloads: all of the process's kernel presents form one stream per marker.
            present = new PresentEvent(header.ProcessId, source.Value, 0, header.Timestamp);
            return EtwEventKind.Present;
        }

        return EtwEventKind.Ignore;
    }

    private static ulong ReadPointer(ReadOnlySpan<byte> data, int size)
        => size == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : BinaryPrimitives.ReadUInt64LittleEndian(data);
}

/// <summary>
/// Field offsets of the native EVENT_RECORD (x64 process). The consumer copies the record into
/// a byte buffer and decodes it here, so the layout is unit-testable without unsafe code.
/// </summary>
internal static class EtwRecordLayout
{
    public const int EventRecordSize = 112;

    private const int FlagsOffset = 4;
    private const int ProcessIdOffset = 12;
    private const int TimeStampOffset = 16;
    private const int ProviderIdOffset = 24;
    private const int EventIdOffset = 40;
    private const int UserDataLengthOffset = 86;
    private const int UserDataOffset = 96;

    public static EtwEventHeader ReadHeader(ReadOnlySpan<byte> record) => new(
        new Guid(record.Slice(ProviderIdOffset, 16)),
        BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(EventIdOffset)),
        BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(FlagsOffset)),
        BinaryPrimitives.ReadInt32LittleEndian(record.Slice(ProcessIdOffset)),
        BinaryPrimitives.ReadInt64LittleEndian(record.Slice(TimeStampOffset)));

    public static int ProcessId(ReadOnlySpan<byte> record)
        => BinaryPrimitives.ReadInt32LittleEndian(record.Slice(ProcessIdOffset));

    public static int UserDataLength(ReadOnlySpan<byte> record)
        => BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(UserDataLengthOffset));

    public static IntPtr UserDataPointer(ReadOnlySpan<byte> record)
        => (IntPtr)BinaryPrimitives.ReadInt64LittleEndian(record.Slice(UserDataOffset));
}
