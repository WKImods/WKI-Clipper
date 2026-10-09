using System;
using System.Runtime.InteropServices;

namespace WKI_Clipper.Native;

/// <summary>One reading of the discrete GPU's sensors. Null = the driver does not report it.</summary>
public readonly record struct GpuSensorReading(double? TemperatureC, uint? FanRpm, ulong? DedicatedVramBytes);

/// <summary>
/// GPU temperature, fan speed and VRAM size through the kernel-mode thunk the Task Manager
/// uses (D3DKMTQueryAdapterInfo / KMTQAITYPE_ADAPTERPERFDATA). User mode, no admin, no
/// driver of our own — so nothing an anti-cheat could object to. Any WDDM 2.4+ driver
/// fills it in (verified on the RX 9070 XT).
///
/// The adapter handles from D3DKMTEnumAdapters2 are OPEN handles: they are enumerated once,
/// kept, and closed on dispose (re-enumerating every second would leak them).
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
    private const int KMTQAITYPE_ADAPTERPERFDATA = 62;

    private uint[] _handles = Array.Empty<uint>();
    private uint _gpu;            // the discrete GPU: most dedicated VRAM
    private ulong _gpuVram;
    private bool _enumerated;
    private bool _everWorked;
    private bool _unsupported;

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
            return new GpuSensorReading(
                perf.Temperature > 0 ? perf.Temperature / 10.0 : null,
                perf.Temperature > 0 ? perf.FanRPM : null,   // fan 0 rpm is real (zero-RPM idle)
                _gpuVram > 0 ? _gpuVram : null);
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
        _enumerated = false;
    }

    public void Dispose() => Close();
}
