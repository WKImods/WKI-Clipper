using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Timers;
using WKI_Clipper.Native;

namespace WKI_Clipper.Services;

/// <summary>One 1 Hz hardware snapshot for the performance widget.</summary>
public readonly record struct PerfSample(
    double CpuPercent,
    double GpuPercent,
    ulong RamUsedBytes,
    ulong RamTotalBytes,
    ulong VramUsedBytes);

/// <summary>
/// Polls CPU / GPU / RAM / VRAM at 1 Hz using Windows performance counters (no admin,
/// no external dependency — keeps the per-user, lightweight DNA). Reference-counted:
/// the timer only runs while at least one performance widget is visible, so a closed
/// board costs nothing. FPS is intentionally out of scope.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PerformanceMonitorService : IDisposable
{
    private readonly System.Timers.Timer _timer = new(1000) { AutoReset = true };
    private readonly object _gate = new();
    private int _viewers;
    private bool _initialized;

    private PerformanceCounter? _cpu;

    public event Action<PerfSample>? Sampled;
    public PerfSample Last { get; private set; }

    public PerformanceMonitorService()
    {
        _timer.Elapsed += (_, _) => Poll();
    }

    /// <summary>Called by a widget when it becomes visible. Starts polling on 0→1.</summary>
    public void AddViewer()
    {
        lock (_gate)
        {
            _viewers++;
            if (_viewers == 1)
            {
                EnsureInitialized();
                _timer.Start();
                // Prime once immediately so the widget isn't blank for a second.
                Poll();
            }
        }
    }

    /// <summary>Called by a widget when it hides. Stops polling on 1→0.</summary>
    public void RemoveViewer()
    {
        lock (_gate)
        {
            if (_viewers > 0) _viewers--;
            if (_viewers == 0) _timer.Stop();
        }
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            // "% Processor Utility" tracks Task-Manager's CPU number; fall back to the
            // classic "% Processor Time" if the newer counter set is unavailable.
            if (PerformanceCounterCategory.Exists("Processor Information"))
                _cpu = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total", readOnly: true);
            else
                _cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
            _cpu.NextValue(); // first read primes the delta
        }
        catch (Exception ex)
        {
            Logger.Warn("Perf: CPU counter init failed: " + ex.Message);
            _cpu = null;
        }
    }

    private void Poll()
    {
        try
        {
            double cpu = ReadCpu();
            double gpu = ReadGpuUtilization();
            (ulong ramUsed, ulong ramTotal) = ReadRam();
            ulong vram = ReadVram();

            var sample = new PerfSample(cpu, gpu, ramUsed, ramTotal, vram);
            Last = sample;
            Sampled?.Invoke(sample);
        }
        catch (Exception ex)
        {
            Logger.Warn("Perf poll failed: " + ex.Message);
        }
    }

    private double ReadCpu()
    {
        try { return _cpu?.NextValue() ?? 0; }
        catch { return 0; }
    }

    /// <summary>Sum of all 3D GPU-engine instances — the Task-Manager approach.</summary>
    private static double ReadGpuUtilization()
    {
        try
        {
            if (!PerformanceCounterCategory.Exists("GPU Engine")) return 0;
            var cat = new PerformanceCounterCategory("GPU Engine");
            double total = 0;
            foreach (var inst in cat.GetInstanceNames())
            {
                if (!inst.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, readOnly: true);
                    total += c.NextValue();
                }
                catch { /* instance vanished between enum and read */ }
            }
            return Math.Min(total, 100);
        }
        catch { return 0; }
    }

    private static (ulong used, ulong total) ReadRam()
    {
        try
        {
            var mem = new Kernel32.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Kernel32.MEMORYSTATUSEX>() };
            if (Kernel32.GlobalMemoryStatusEx(ref mem))
                return (mem.ullTotalPhys - mem.ullAvailPhys, mem.ullTotalPhys);
        }
        catch { }
        return (0, 0);
    }

    /// <summary>Sum of dedicated GPU adapter memory in use across adapters.</summary>
    private static ulong ReadVram()
    {
        try
        {
            if (!PerformanceCounterCategory.Exists("GPU Adapter Memory")) return 0;
            var cat = new PerformanceCounterCategory("GPU Adapter Memory");
            double total = 0;
            foreach (var inst in cat.GetInstanceNames())
            {
                try
                {
                    using var c = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", inst, readOnly: true);
                    total += c.NextValue();
                }
                catch { }
            }
            return (ulong)total;
        }
        catch { return 0; }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        try { _cpu?.Dispose(); } catch { }
    }
}
