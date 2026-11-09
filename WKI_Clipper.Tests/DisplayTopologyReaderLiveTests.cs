using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using WKI_Clipper.Native;
using WKI_Clipper.Services;
using Xunit;
using Xunit.Abstractions;

namespace WKI_Clipper.Tests;

/// <summary>
/// Reads the REAL display topology of this machine (EnumDisplayMonitors + CCD + DXGI). Gated behind
/// DISPLAY_LIVE=1 - the result depends on the monitors and GPUs, so it must not run in a normal test pass.
/// Reads only; changes nothing.
/// </summary>
public sealed class DisplayTopologyReaderLiveTests
{
    private readonly ITestOutputHelper _out;
    public DisplayTopologyReaderLiveTests(ITestOutputHelper output) => _out = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("DISPLAY_LIVE") == "1";

    private DisplayTopology ReadAndLog()
    {
        var topo = DisplayTopologyReader.Read();
        _out.WriteLine(topo.Dump());
        _out.WriteLine("fingerprint: " + topo.Fingerprint);
        _out.WriteLine("quick key: " + DisplayTopologyReader.QuickKey());
        return topo;
    }

    [Fact]
    public void Every_enum_display_monitor_has_a_display_with_its_hmonitor()
    {
        if (!Enabled) return;

        var topo = ReadAndLog();
        var monitors = new List<IntPtr>();
        User32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref User32.RECT _, IntPtr _) =>
        {
            monitors.Add(h);
            return true;
        }, IntPtr.Zero);

        Assert.NotEmpty(monitors);
        Assert.Equal(monitors.Count, topo.Displays.Count);
        foreach (var h in monitors)
            Assert.NotNull(topo.ByHMonitor(h));
    }

    [Fact]
    public void Primary_is_output_0_of_the_default_adapter()
    {
        if (!Enabled) return;

        var topo = ReadAndLog();
        var primary = topo.Primary;

        Assert.NotNull(primary);
        Assert.Equal(topo.DefaultAdapterIndex, primary!.AdapterIndex);
        Assert.Equal(0, primary.OutputIndex);
        Assert.Equal(0, primary.X);
        Assert.Equal(0, primary.Y);
    }

    [Fact]
    public void Friendly_name_and_device_path_are_present()
    {
        if (!Enabled) return;

        // Needs every monitor awake: a sleeping DisplayPort monitor has no device path (its PnP device is not present).
        var topo = ReadAndLog();

        Assert.Null(topo.Problem);
        Assert.NotEmpty(topo.Displays);
        foreach (var d in topo.Displays)
        {
            Assert.False(string.IsNullOrWhiteSpace(d.FriendlyName), d.GdiName);
            Assert.StartsWith(@"\\?\DISPLAY#", d.DevicePath ?? "");
            Assert.NotNull(d.EdidMfg);
            Assert.NotNull(d.EdidProduct);
        }
    }

    [Fact]
    public void Sizes_are_the_same_physical_pixels_on_a_dpi_unaware_thread()
    {
        if (!Enabled) return;

        var aware = ReadAndLog();
        DisplayTopology? unaware = null;
        var thread = new Thread(() =>
        {
            DisplayConfig.SetThreadDpiAwarenessContext(DisplayConfig.DPI_AWARENESS_CONTEXT_UNAWARE);
            unaware = DisplayTopologyReader.Read();
        });
        thread.Start();
        thread.Join();

        Assert.NotNull(unaware);
        Assert.Equal(aware.Fingerprint, unaware!.Fingerprint);
    }
}
