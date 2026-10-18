using System;
using System.Runtime.InteropServices;

namespace WKI_Clipper.Native;

/// <summary>
/// D3DKMT_NODE_PERFDATA from the Windows SDK's d3dkmthk.h (KMTQAITYPE_NODEPERFDATA = 61):
/// clock and voltage of ONE engine node (the 3D node for the overlay). Natural alignment gives
/// the SDK's 56 bytes (a 4-byte hole after VoltageMaxOC keeps MaxTransitionLatency 8-aligned).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D3DKMT_NODE_PERFDATA
{
    public uint NodeOrdinal;            // in:  engine node
    public uint PhysicalAdapterIndex;   // in:  index in an LDA chain (0)
    public ulong Frequency;             // out: Hz
    public ulong MaxFrequency;          // out: Hz
    public ulong MaxFrequencyOC;        // out: Hz
    public uint Voltage;                // out: mV
    public uint VoltageMax;             // out: mV
    public uint VoltageMaxOC;           // out: mV
    public ulong MaxTransitionLatency;  // out: 100 ns
}

/// <summary>Clock / voltage of an engine node in display units; null = the driver reports nothing usable.</summary>
internal readonly record struct GpuNodePerfReading(double? ClockMhz, double? MaxClockMhz, uint? VoltageMv);

/// <summary>
/// Pure decoding of the node perf data. The units were checked live on an RX 9070 XT: Frequency is
/// Hz (idle 3D node 111 MHz, node maximum 2460 MHz), Voltage is mV (701 at idle). A field that
/// reads 0 is "not reported" and an absurd value is garbage — both come out as null so the overlay
/// hides the line instead of showing a wrong number.
/// </summary>
internal static class GpuNodePerf
{
    /// <summary>sizeof(D3DKMT_NODE_PERFDATA) in the SDK; also what the thunk expects as the buffer size.</summary>
    public const int Size = 56;

    // Upper bounds well above any real GPU / memory clock or core voltage.
    private const double MaxPlausibleMhz = 10_000;
    private const uint MaxPlausibleMv = 5_000;

    /// <summary>Decodes a raw D3DKMT_NODE_PERFDATA; null when the buffer is too short to be one.</summary>
    public static GpuNodePerfReading? Parse(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < Size) return null;
        return From(MemoryMarshal.Read<D3DKMT_NODE_PERFDATA>(raw));
    }

    /// <summary>Display values of an already marshalled struct.</summary>
    public static GpuNodePerfReading From(in D3DKMT_NODE_PERFDATA d)
        => new(HzToMhz(d.Frequency), HzToMhz(d.MaxFrequency), ValidMillivolts(d.Voltage));

    /// <summary>Hz → MHz; 0 and implausible values (&gt; 10 GHz) are null.</summary>
    public static double? HzToMhz(ulong hz)
    {
        if (hz == 0) return null;
        double mhz = hz / 1_000_000.0;
        return mhz <= MaxPlausibleMhz ? mhz : null;
    }

    public static uint? ValidMillivolts(uint mv) => mv is > 0 and <= MaxPlausibleMv ? mv : null;
}
