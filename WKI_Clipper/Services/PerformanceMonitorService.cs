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

    // GPU engine counters MUST be kept alive between polls: "Utilization Percentage" is
    // a rate counter, and the first NextValue() on a fresh instance always returns 0.
    // Creating them per poll (as this did originally) therefore reported 0 % forever.
    private readonly Dictionary<string, PerformanceCounter> _gpuCounters = new();
    // Reading all ~250 engine instances costs ~190 ms — far too much every second for an
    // overlay that must not cost FPS. So the full sweep runs rarely and only marks which
    // engines are actually busy; the 1 Hz poll then reads just those few.
    private readonly HashSet<string> _gpuActive = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _gpuInstancesRefreshed = DateTime.MinValue;
    /// <summary>How often the expensive all-instance sweep runs (seconds).</summary>
    private const int SweepIntervalSec = 5;

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
            double gpu = ReadGpu();
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
        try
        {
            // "% Processor Utility" can exceed 100 % — it relates work done to the
            // NOMINAL clock, so a turbo-boosted core reports e.g. 124 %. Clamp it.
            return Math.Clamp(_cpu?.NextValue() ?? 0, 0, 100);
        }
        catch { return 0; }
    }

    /// <summary>
    /// Sum of all 3D GPU-engine instances (the Task-Manager approach). The instances are
    /// per process, so the set changes as apps come and go — the list is refreshed
    /// periodically while the counters themselves stay alive so their rates keep working.
    /// </summary>
    private double ReadGpu()
    {
        try
        {
            if (RefreshGpuCountersIfDue() is { } sweepTotal)
                return sweepTotal;   // the sweep already read everything — use its result

            double total = 0;
            List<string>? dead = null;
            foreach (var name in _gpuActive)
            {
                if (!_gpuCounters.TryGetValue(name, out var counter)) continue;
                try { total += counter.NextValue(); }
                catch { (dead ??= new()).Add(name); }   // process ended mid-poll
            }
            if (dead != null)
                foreach (var d in dead)
                {
                    try { _gpuCounters[d].Dispose(); } catch { }
                    _gpuCounters.Remove(d);
                    _gpuActive.Remove(d);
                }

            return Math.Clamp(total, 0, 100);
        }
        catch { return 0; }
    }

    /// <summary>
    /// Full sweep over every 3D engine instance: keeps the counter cache in sync with the
    /// running processes and re-picks the "active" set the cheap 1 Hz polls read. Returns
    /// the total it measured (so the caller doesn't read everything twice), or null when
    /// no sweep was due.
    /// </summary>
    private double? RefreshGpuCountersIfDue()
    {
        if ((DateTime.UtcNow - _gpuInstancesRefreshed).TotalSeconds < SweepIntervalSec) return null;
        _gpuInstancesRefreshed = DateTime.UtcNow;

        if (!PerformanceCounterCategory.Exists("GPU Engine")) return null;
        var cat = new PerformanceCounterCategory("GPU Engine");
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var inst in cat.GetInstanceNames())
        {
            if (!inst.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)) continue;
            live.Add(inst);
            if (_gpuCounters.ContainsKey(inst)) continue;
            try
            {
                var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, readOnly: true);
                c.NextValue();               // prime: the first read of a rate counter is always 0
                _gpuCounters[inst] = c;
            }
            catch { /* instance vanished between enumeration and creation */ }
        }

        foreach (var gone in _gpuCounters.Keys.Where(k => !live.Contains(k)).ToList())
        {
            try { _gpuCounters[gone].Dispose(); } catch { }
            _gpuCounters.Remove(gone);
            _gpuActive.Remove(gone);
        }

        // Read everything once and remember who is actually doing work. A newly started
        // game joins the active set at the next sweep, i.e. within a few seconds.
        double total = 0;
        _gpuActive.Clear();
        foreach (var (name, counter) in _gpuCounters)
        {
            try
            {
                float v = counter.NextValue();
                total += v;
                if (v > 0.05f) _gpuActive.Add(name);
            }
            catch { /* dropped on the next sweep */ }
        }
        return Math.Clamp(total, 0, 100);
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
        foreach (var c in _gpuCounters.Values) { try { c.Dispose(); } catch { } }
        _gpuCounters.Clear();
    }
}
