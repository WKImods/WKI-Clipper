using System;
using System.Buffers.Binary;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Decoding and filtering of raw ETW present events, fed with synthetic records — the layouts
/// mirror the providers' manifests and PresentMon's Microsoft_Windows_DXGI.h / _D3D9.h.
/// </summary>
public class PresentEventParserTests
{
    private const int Game = 4242;
    private const int Other = 777;
    private const ushort Header64 = 0x0040;   // EVENT_HEADER_FLAG_64_BIT_HEADER
    private const ushort Header32 = 0x0020;   // EVENT_HEADER_FLAG_32_BIT_HEADER
    private const ulong SwapChain = 0x0000_01F2_3456_7890;

    private static EtwEventHeader Header(Guid provider, ushort id, int pid = Game, ushort flags = Header64, long ts = 123_456_789)
        => new(provider, id, flags, pid, ts);

    /// <summary>DXGI Present_Start / PresentMultiplaneOverlay_Start: pIDXGISwapChain, Flags, SyncInterval.</summary>
    private static byte[] DxgiPayload(ulong swapChain, uint flags, bool is32Bit = false)
    {
        int ptr = is32Bit ? 4 : 8;
        var b = new byte[ptr + 8];
        if (is32Bit) BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)swapChain);
        else BinaryPrimitives.WriteUInt64LittleEndian(b, swapChain);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(ptr), flags);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(ptr + 4), 1);
        return b;
    }

    /// <summary>D3D9 Present_Start: pSwapchain, Flags.</summary>
    private static byte[] D3D9Payload(ulong swapChain)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(b, swapChain);
        return b;
    }

    [Fact]
    public void A_dxgi_present_of_the_target_is_a_present_with_its_swap_chain()
    {
        var h = Header(PresentEventParser.DxgiProvider, PresentEventParser.DxgiPresentStart);
        var kind = PresentEventParser.Classify(h, DxgiPayload(SwapChain, 0), Game, out var e);

        Assert.Equal(EtwEventKind.Present, kind);
        Assert.Equal(PresentSource.Dxgi, e.Source);
        Assert.Equal(SwapChain, e.SwapChain);
        Assert.Equal(Game, e.ProcessId);
        Assert.Equal(123_456_789, e.Timestamp);
        Assert.Equal(PresentApi.Dxgi, e.Api);
        Assert.True(e.IsRuntime);
    }

    [Fact]
    public void Dxgi_present_test_is_not_a_frame()
    {
        // DXGI_PRESENT_TEST only asks "would this present work?" (e.g. still fullscreen).
        var h = Header(PresentEventParser.DxgiProvider, PresentEventParser.DxgiPresentStart);
        Assert.Equal(EtwEventKind.Ignore, PresentEventParser.Classify(h, DxgiPayload(SwapChain, 0x1), Game, out _));
        // Other flags (e.g. DXGI_PRESENT_RESTART 0x4) are real presents.
        Assert.Equal(EtwEventKind.Present, PresentEventParser.Classify(h, DxgiPayload(SwapChain, 0x4), Game, out _));
    }

    [Fact]
    public void A_32_bit_game_has_4_byte_pointers()
    {
        var h = Header(PresentEventParser.DxgiProvider, PresentEventParser.DxgiPresentStart, flags: Header32);

        Assert.Equal(EtwEventKind.Present, PresentEventParser.Classify(h, DxgiPayload(0x1234_5678, 0, is32Bit: true), Game, out var e));
        Assert.Equal(0x1234_5678UL, e.SwapChain);
        // The flags field follows the 4-byte pointer — reading at offset 8 would miss the test flag.
        Assert.Equal(EtwEventKind.Ignore, PresentEventParser.Classify(h, DxgiPayload(0x1234_5678, 0x1, is32Bit: true), Game, out _));
    }

    [Fact]
    public void Multiplane_overlay_presents_count_like_normal_presents()
    {
        var h = Header(PresentEventParser.DxgiProvider, PresentEventParser.DxgiPresentMpoStart);
        Assert.Equal(EtwEventKind.Present, PresentEventParser.Classify(h, DxgiPayload(SwapChain, 0), Game, out var e));
        Assert.Equal(PresentSource.Dxgi, e.Source);
    }

    [Theory]
    [InlineData(43)]   // Present_Stop
    [InlineData(44)]
    [InlineData(1)]
    public void Other_dxgi_events_are_ignored(ushort id)
    {
        var h = Header(PresentEventParser.DxgiProvider, id);
        Assert.Equal(EtwEventKind.Ignore, PresentEventParser.Classify(h, DxgiPayload(SwapChain, 0), Game, out _));
    }

    [Fact]
    public void A_d3d9_present_is_a_runtime_present()
    {
        var h = Header(PresentEventParser.D3D9Provider, PresentEventParser.D3D9PresentStart);
        Assert.Equal(EtwEventKind.Present, PresentEventParser.Classify(h, D3D9Payload(SwapChain), Game, out var e));
        Assert.Equal(PresentSource.D3D9, e.Source);
        Assert.Equal(SwapChain, e.SwapChain);
        Assert.Equal(PresentApi.D3D9, e.Api);
        Assert.True(e.IsRuntime);

        var stop = Header(PresentEventParser.D3D9Provider, 2);
        Assert.Equal(EtwEventKind.Ignore, PresentEventParser.Classify(stop, D3D9Payload(SwapChain), Game, out _));
    }

    [Theory]
    [InlineData(0xAB, "KernelPresentHistory")]
    [InlineData(0xB8, "KernelPresentInfo")]
    [InlineData(0xD7, "KernelPresentHistoryDetailed")]
    public void Kernel_present_events_are_fallback_presents_without_a_swap_chain(ushort id, string expected)
    {
        var h = Header(PresentEventParser.DxgKrnlProvider, id);
        Assert.Equal(EtwEventKind.Present, PresentEventParser.Classify(h, new byte[40], Game, out var e));
        Assert.Equal(expected, e.Source.ToString());
        Assert.Equal(0UL, e.SwapChain);
        Assert.Equal(PresentApi.Other, e.Api);
        Assert.False(e.IsRuntime);
    }

    [Theory]
    [InlineData(0x11)]   // VSyncDPC_Info
    [InlineData(0xA8)]   // Flip_Info
    public void Other_kernel_events_are_ignored(ushort id)
    {
        var h = Header(PresentEventParser.DxgKrnlProvider, id);
        Assert.Equal(EtwEventKind.Ignore, PresentEventParser.Classify(h, new byte[40], Game, out _));
    }

    [Fact]
    public void Only_the_target_process_counts()
    {
        var foreign = Header(PresentEventParser.DxgiProvider, PresentEventParser.DxgiPresentStart, pid: Other);
        Assert.Equal(EtwEventKind.Ignore, PresentEventParser.Classify(foreign, DxgiPayload(SwapChain, 0), Game, out _));

        // No target: nothing counts, not even pid 0 events.
        var idle = Header(PresentEventParser.DxgiProvider, PresentEventParser.DxgiPresentStart, pid: 0);
        Assert.Equal(EtwEventKind.Ignore, PresentEventParser.Classify(idle, DxgiPayload(SwapChain, 0), 0, out _));
    }

    [Fact]
    public void Lost_event_notices_count_regardless_of_process()
    {
        var h = Header(PresentEventParser.LostEventProvider, 32, pid: 0);
        Assert.Equal(EtwEventKind.Lost, PresentEventParser.Classify(h, ReadOnlySpan<byte>.Empty, Game, out var e));
        Assert.Equal(123_456_789, e.Timestamp);
    }

    [Fact]
    public void Truncated_payloads_and_unknown_providers_are_ignored()
    {
        var dxgi = Header(PresentEventParser.DxgiProvider, PresentEventParser.DxgiPresentStart);
        Assert.Equal(EtwEventKind.Ignore, PresentEventParser.Classify(dxgi, new byte[8], Game, out _));   // flags missing
        var d3d9 = Header(PresentEventParser.D3D9Provider, PresentEventParser.D3D9PresentStart);
        Assert.Equal(EtwEventKind.Ignore, PresentEventParser.Classify(d3d9, new byte[4], Game, out _));

        var unknown = Header(Guid.NewGuid(), PresentEventParser.DxgiPresentStart);
        Assert.Equal(EtwEventKind.Ignore, PresentEventParser.Classify(unknown, DxgiPayload(SwapChain, 0), Game, out _));
    }

    [Fact]
    public void Swap_chains_of_different_runtimes_never_share_a_stream_key()
    {
        var dxgi = new PresentEvent(Game, PresentSource.Dxgi, SwapChain, 1);
        var d3d9 = new PresentEvent(Game, PresentSource.D3D9, SwapChain, 1);
        var other = new PresentEvent(Game, PresentSource.Dxgi, SwapChain + 0x100, 1);

        Assert.NotEqual(dxgi.StreamKey, d3d9.StreamKey);
        Assert.NotEqual(dxgi.StreamKey, other.StreamKey);
        Assert.Equal(dxgi.StreamKey, (dxgi with { Timestamp = 99 }).StreamKey);
    }

    /// <summary>EVENT_RECORD (x64) offsets: header fields, UserDataLength @86, UserData @96.</summary>
    [Fact]
    public void Header_fields_are_read_from_the_native_event_record_layout()
    {
        var rec = new byte[EtwRecordLayout.EventRecordSize];
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(4), Header32);              // Flags
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(8), 1111);                  // ThreadId
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(12), Game);                 // ProcessId
        BinaryPrimitives.WriteInt64LittleEndian(rec.AsSpan(16), 987_654_321_000);       // TimeStamp
        PresentEventParser.DxgiProvider.TryWriteBytes(rec.AsSpan(24));                  // ProviderId
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(40), 42);                   // EventDescriptor.Id
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(86), 16);                   // UserDataLength
        BinaryPrimitives.WriteInt64LittleEndian(rec.AsSpan(96), 0x7FF0_1234_5678);      // UserData

        var h = EtwRecordLayout.ReadHeader(rec);
        Assert.Equal(PresentEventParser.DxgiProvider, h.ProviderId);
        Assert.Equal((ushort)42, h.EventId);
        Assert.Equal(Header32, h.Flags);
        Assert.Equal(Game, h.ProcessId);
        Assert.Equal(987_654_321_000, h.Timestamp);
        Assert.Equal(16, EtwRecordLayout.UserDataLength(rec));
        Assert.Equal(0x7FF0_1234_5678L, EtwRecordLayout.UserDataPointer(rec).ToInt64());
    }
}
