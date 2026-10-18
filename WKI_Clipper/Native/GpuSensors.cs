using System;
using System.Runtime.InteropServices;

namespace WKI_Clipper.Native;

/// <summary>
/// One reading of the discrete GPU's sensors. Null = the driver does not report it (or reports 0,
/// which means the same). Clock in MHz, voltage in mV.
/// </summary>
public readonly record struct GpuSensorReading(
    double? TemperatureC,
    uint? FanRpm,
    ulong? DedicatedVramBytes,
    double? CoreClockMhz = null,
    uint? CoreVoltageMv = null,
    double? MemoryClockMhz = null);

/// <summary>
/// GPU temperature, fan speed, VRAM size, core clock / voltage and VRAM clock through the
/// kernel-mode thunk the Task Manager uses (D3DKMTQueryAdapterInfo: KMTQAITYPE_ADAPTERPERFDATA
/// for the adapter, KMTQAITYPE_NODEPERFDATA for the 3D engine node). User mode, no admin, no
/// driver of our own — so nothing an anti-cheat could object to. Any WDDM 2.4+ driver
/// fills it in (verified on the RX 9070 XT).
///
/// The adapter handles from D3DKMTEnumAdapters2 are OPEN handles: they are enumerated once,
/// kept, and closed on dispose (re-enumerating every second would leak them).
///
/// The 3D node is found once per enumeration with KMTQAITYPE_NODEMETADATA (first node whose
/// engine type is 3D); if the driver does not answer that, node 0 is used — on AMD and NVIDIA
/// the 3D engine is node 0.
/// </summary>
internal sealed class GpuSensors : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTERINFO
    {
        public uint hAdapter;
        public LUID AdapterLuid;
        public uint NumOfSources;
        public int bPrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ENUMADAPTERS2 { public uint NumAdapters; public IntPtr pAdapters; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct D3DKMT_ADAPTER_PERFDATA
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOC;
        public ulong MemoryBandwidth;
        public ulong PCIEBandwidth;
        public uint FanRPM;
        public uint Power;
        public uint Temperature;   // deci-degrees Celsius
        public byte PowerStateOverride;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_SEGMENTSIZEINFO
    {
        public ulong DedicatedVideoMemorySize;
        public ulong DedicatedSystemMemorySize;
        public ulong SharedSystemMemorySize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER { public uint hAdapter; }

    [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 e);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO q);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER c);

    private const int KMTQAITYPE_GETSEGMENTSIZE = 3;
    private const int KMTQAITYPE_NODEMETADATA = 25;
    private const int KMTQAITYPE_NODEPERFDATA = 61;
    private const int KMTQAITYPE_ADAPTERPERFDATA = 62;

    /// <summary>
    /// D3DKMT_NODEMETADATA on the wire: the thunk wants exactly 78 bytes (4 ordinal + 4 engine type +
    /// 32 WCHAR name + 4 flags + 2 BOOLEAN) and rejects the 80 a padded C struct would have
    /// (STATUS_INVALID_PARAMETER) — found live on the RX 9070 XT.
    /// </summary>
    private const int NodeMetadataSize = 78;
    private const int DXGK_ENGINE_TYPE_3D = 1;
    private const int MaxNodesProbed = 32;

    private uint[] _handles = Array.Empty<uint>();
    private uint _gpu;            // the discrete GPU: most dedicated VRAM
    private ulong _gpuVram;
    private uint _node3D;         // ordinal of the 3D engine node of _gpu
    private bool _enumerated;
    private bool _everWorked;
    private bool _unsupported;
    private bool _nodePerfEverWorked;
    private bool _nodePerfUnsupported;

    public GpuSensorReading Read()
    {
        try
        {
            if (_unsupported) return default;
            if (!_enumerated) Enumerate();
            if (_gpu == 0) return default;

            var perf = new D3DKMT_ADAPTER_PERFDATA();
            if (!Query(_gpu, KMTQAITYPE_ADAPTERPERFDATA, ref perf))
            {
                // Never worked: an older driver without perf data — stop asking.
                // Worked before: driver reset / adapter gone — re-enumerate on the next read.
                if (!_everWorked) { _unsupported = true; Services.Logger.Info("GPU sensors: driver reports no perf data."); }
                Close();
                return default;
            }
            _everWorked = true;
            var node = ReadNode();
            return new GpuSensorReading(
                perf.Temperature > 0 ? perf.Temperature / 10.0 : null,
                perf.Temperature > 0 ? perf.FanRPM : null,   // fan 0 rpm is real (zero-RPM idle)
                _gpuVram > 0 ? _gpuVram : null,
                node?.ClockMhz,
                node?.VoltageMv,
                GpuNodePerf.HzToMhz(perf.MemoryFrequency));
        }
        catch (Exception ex)
        {
            Services.Logger.Warn("GPU sensors: " + ex.Message);
            return default;
        }
    }

    private void Enumerate()
    {
        _enumerated = true;
        var e = new D3DKMT_ENUMADAPTERS2();
        if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return;

        int size = Marshal.SizeOf<D3DKMT_ADAPTERINFO>();
        e.pAdapters = Marshal.AllocHGlobal(size * (int)e.NumAdapters);
        try
        {
            if (D3DKMTEnumAdapters2(ref e) != 0) return;
            _handles = new uint[e.NumAdapters];
            for (int i = 0; i < e.NumAdapters; i++)
            {
                var a = Marshal.PtrToStructure<D3DKMT_ADAPTERINFO>(e.pAdapters + i * size);
                _handles[i] = a.hAdapter;
                var seg = new D3DKMT_SEGMENTSIZEINFO();
                if (Query(a.hAdapter, KMTQAITYPE_GETSEGMENTSIZE, ref seg) && seg.DedicatedVideoMemorySize > _gpuVram)
                {
                    _gpuVram = seg.DedicatedVideoMemorySize;
                    _gpu = a.hAdapter;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(e.pAdapters);
        }

        if (_gpu != 0) _node3D = FindNode3D(_gpu);
    }

    /// <summary>
    /// Ordinal of the first 3D engine node, from the node metadata; node 0 when the driver does not
    /// answer (the 3D engine is node 0 on AMD and NVIDIA). Logged once per enumeration.
    /// </summary>
    private static uint FindNode3D(uint adapter)
    {
        IntPtr buf = Marshal.AllocHGlobal(NodeMetadataSize);
        try
        {
            for (uint ordinal = 0; ordinal < MaxNodesProbed; ordinal++)
            {
                for (int i = 0; i < NodeMetadataSize; i++) Marshal.WriteByte(buf, i, 0);
                Marshal.WriteInt32(buf, 0, (int)ordinal);   // NodeOrdinalAndAdapterIndex: low word = node, high word = physical adapter 0
                var q = new D3DKMT_QUERYADAPTERINFO
                {
                    hAdapter = adapter, Type = KMTQAITYPE_NODEMETADATA,
                    pPrivateDriverData = buf, PrivateDriverDataSize = NodeMetadataSize
                };
                if (D3DKMTQueryAdapterInfo(ref q) != 0)
                {
                    Services.Logger.Info(ordinal == 0
                        ? "GPU sensors: node metadata unavailable — assuming the 3D engine is node 0."
                        : $"GPU sensors: no 3D node among {ordinal} engine nodes — assuming node 0.");
                    return 0;
                }
                if (Marshal.ReadInt32(buf, 4) == DXGK_ENGINE_TYPE_3D)
                {
                    Services.Logger.Info($"GPU sensors: 3D engine is node {ordinal} (node metadata).");
                    return ordinal;
                }
            }
            Services.Logger.Info("GPU sensors: no 3D node found — assuming node 0.");
            return 0;
        }
        catch (Exception ex)
        {
            Services.Logger.Warn("GPU sensors: node lookup failed — assuming node 0: " + ex.Message);
            return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>
    /// Clock and voltage of the 3D node. Independent of the adapter data: a driver without node
    /// perf data simply has no clock/voltage line. Never worked → stop asking.
    /// </summary>
    private GpuNodePerfReading? ReadNode()
    {
        if (_nodePerfUnsupported) return null;
        var data = new D3DKMT_NODE_PERFDATA { NodeOrdinal = _node3D, PhysicalAdapterIndex = 0 };
        if (!Query(_gpu, KMTQAITYPE_NODEPERFDATA, ref data))
        {
            if (!_nodePerfEverWorked)
            {
                _nodePerfUnsupported = true;
                Services.Logger.Info("GPU sensors: driver reports no node perf data (no clock / voltage).");
            }
            return null;
        }
        _nodePerfEverWorked = true;
        return GpuNodePerf.From(in data);
    }

    private static bool Query<T>(uint adapter, int type, ref T data) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(data, buf, false);
            var q = new D3DKMT_QUERYADAPTERINFO { hAdapter = adapter, Type = type, pPrivateDriverData = buf, PrivateDriverDataSize = (uint)size };
            if (D3DKMTQueryAdapterInfo(ref q) != 0) return false;
            data = Marshal.PtrToStructure<T>(buf);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private void Close()
    {
        foreach (var h in _handles)
        {
            var c = new D3DKMT_CLOSEADAPTER { hAdapter = h };
            try { D3DKMTCloseAdapter(ref c); } catch { }
        }
        _handles = Array.Empty<uint>();
        _gpu = 0;
        _gpuVram = 0;
        _node3D = 0;
        _enumerated = false;
    }

    public void Dispose() => Close();
}
