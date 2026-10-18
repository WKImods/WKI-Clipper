using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using WKI_Clipper.Native;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// D3DKMT_NODE_PERFDATA (KMTQAITYPE_NODEPERFDATA = 61): the layout is pinned to the Windows SDK's
/// d3dkmthk.h and the parser is a pure function over the raw bytes, so a wrong offset or unit shows
/// up here instead of as a silently wrong overlay value. The numbers below were read live from an
/// RX 9070 XT (idle 3D node: 111 MHz / 701 mV, node max 2460 MHz).
/// </summary>
public sealed class GpuNodePerfTests
{
    private static byte[] Raw(ulong freqHz, ulong maxHz, uint voltageMv)
    {
        var b = new byte[GpuNodePerf.Size];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), 0);          // NodeOrdinal
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 0);          // PhysicalAdapterIndex
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), freqHz);     // Frequency
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), maxHz);     // MaxFrequency
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(32), voltageMv); // Voltage
        return b;
    }

    [Fact]
    public void The_struct_is_56_bytes_like_the_sdk_header()
    {
        Assert.Equal(56, GpuNodePerf.Size);
        Assert.Equal(GpuNodePerf.Size, Marshal.SizeOf<D3DKMT_NODE_PERFDATA>());
    }

    [Theory]
    [InlineData(nameof(D3DKMT_NODE_PERFDATA.NodeOrdinal), 0)]
    [InlineData(nameof(D3DKMT_NODE_PERFDATA.PhysicalAdapterIndex), 4)]
    [InlineData(nameof(D3DKMT_NODE_PERFDATA.Frequency), 8)]
    [InlineData(nameof(D3DKMT_NODE_PERFDATA.MaxFrequency), 16)]
    [InlineData(nameof(D3DKMT_NODE_PERFDATA.MaxFrequencyOC), 24)]
    [InlineData(nameof(D3DKMT_NODE_PERFDATA.Voltage), 32)]
    [InlineData(nameof(D3DKMT_NODE_PERFDATA.VoltageMax), 36)]
    [InlineData(nameof(D3DKMT_NODE_PERFDATA.VoltageMaxOC), 40)]
    [InlineData(nameof(D3DKMT_NODE_PERFDATA.MaxTransitionLatency), 48)]
    public void Field_offsets_match_the_sdk_header(string field, int offset)
        => Assert.Equal(offset, (int)Marshal.OffsetOf<D3DKMT_NODE_PERFDATA>(field));

    [Fact]
    public void Frequency_is_hertz_and_voltage_is_millivolts()
    {
        var r = GpuNodePerf.Parse(Raw(2_460_000_000UL, 2_970_000_000UL, 1020))!.Value;
        Assert.Equal(2460.0, r.ClockMhz);
        Assert.Equal(2970.0, r.MaxClockMhz);
        Assert.Equal(1020u, r.VoltageMv);
    }

    [Fact]
    public void An_idle_reading_from_the_real_card_parses()
    {
        var r = GpuNodePerf.Parse(Raw(111_000_000UL, 2_460_000_000UL, 701))!.Value;
        Assert.Equal(111.0, r.ClockMhz);
        Assert.Equal(701u, r.VoltageMv);
    }

    [Fact]
    public void Values_that_read_zero_are_hidden()
    {
        var r = GpuNodePerf.Parse(Raw(0, 0, 0))!.Value;
        Assert.Null(r.ClockMhz);
        Assert.Null(r.MaxClockMhz);
        Assert.Null(r.VoltageMv);
    }

    [Fact]
    public void Implausible_values_are_hidden_instead_of_shown()
    {
        var r = GpuNodePerf.Parse(Raw(ulong.MaxValue, 50_000_000_000UL, 90_000))!.Value;
        Assert.Null(r.ClockMhz);
        Assert.Null(r.MaxClockMhz);
        Assert.Null(r.VoltageMv);
    }

    [Fact]
    public void A_truncated_buffer_is_not_parsed()
    {
        Assert.Null(GpuNodePerf.Parse(new byte[GpuNodePerf.Size - 1]));
        Assert.Null(GpuNodePerf.Parse(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void A_marshalled_struct_parses_to_the_same_values()
    {
        var s = new D3DKMT_NODE_PERFDATA { Frequency = 1_466_000_000UL, MaxFrequency = 2_460_000_000UL, Voltage = 707 };
        var bytes = new byte[Marshal.SizeOf<D3DKMT_NODE_PERFDATA>()];
        MemoryMarshal.Write(bytes, in s);
        var r = GpuNodePerf.Parse(bytes)!.Value;
        Assert.Equal(1466.0, r.ClockMhz);
        Assert.Equal(707u, r.VoltageMv);
    }

    [Theory]
    [InlineData(2_505_000_000UL, 2505.0)]    // VRAM clock of the 9070 XT (MemoryFrequency)
    [InlineData(1_500_000UL, 1.5)]
    [InlineData(0UL, null)]
    [InlineData(20_000_000_000UL, null)]     // 20 GHz: garbage, not a clock
    public void Hertz_become_megahertz_or_nothing(ulong hz, double? expected)
        => Assert.Equal(expected, GpuNodePerf.HzToMhz(hz));
}
