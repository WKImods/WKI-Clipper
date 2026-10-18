using System;
using System.Collections.Generic;
using System.Threading;
using WKI_Clipper.Native;
using Xunit;
using Xunit.Abstractions;

namespace WKI_Clipper.Tests;

/// <summary>
/// Reads the REAL GPU sensors of this machine through the D3DKMT thunk. Gated behind PERF_LIVE=1 —
/// the values depend on the hardware, so it must not run in a normal test pass. Verifies that the
/// node perf data (clock in Hz, voltage in mV) and the memory clock come out in sane display units.
/// </summary>
public sealed class GpuSensorsLiveTests
{
    private readonly ITestOutputHelper _out;
    public GpuSensorsLiveTests(ITestOutputHelper output) => _out = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("PERF_LIVE") == "1";

    [Fact]
    public void Core_clock_voltage_and_memory_clock_are_in_plausible_units()
    {
        if (!Enabled) return;

        using var sensors = new GpuSensors();
        var readings = new List<GpuSensorReading>();
        for (int i = 0; i < 8; i++)
        {
            var r = sensors.Read();
            readings.Add(r);
            _out.WriteLine($"[{i}] clock={r.CoreClockMhz} MHz  voltage={r.CoreVoltageMv} mV  vramClock={r.MemoryClockMhz} MHz  temp={r.TemperatureC} C  fan={r.FanRpm} rpm");
            Thread.Sleep(400);
        }

        Assert.Contains(readings, r => r.CoreClockMhz is > 10 and < 5000);
        Assert.Contains(readings, r => r.CoreVoltageMv is > 300 and < 1600);
        Assert.Contains(readings, r => r.MemoryClockMhz is > 100 and < 6000);
    }
}
