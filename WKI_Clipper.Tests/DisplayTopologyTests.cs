using System;
using System.Linq;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class DisplayTopologyTests
{
    private static readonly DisplayAdapterInfo Dgpu = new(0, 0x15B44, "AMD Radeon RX 9070 XT", false);
    private static readonly DisplayAdapterInfo Igpu = new(1, 0x195A4, "AMD Radeon(TM) Graphics", false);
    private static readonly DisplayAdapterInfo Basic = new(2, 0x1A2B3, "Microsoft Basic Render Driver", true);

    private static DisplayInfo Display(string gdi, nint hmon, int x, int y, int w, int h, bool primary = false,
        int? adapter = 0, int? output = 0, string? name = "LG ULTRAGEAR+", string? connector = "DP")
        => new(hmon, gdi, $@"\\?\DISPLAY#GSM780B#7&2e952966&2&UID{hmon}#{{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}}",
               name, x, y, w, h, primary, adapter, output, connector, 0x6D1E, 0x780B);

    private static DisplayTopology Topology(params DisplayInfo[] displays)
        => new(new[] { Dgpu, Igpu, Basic }, displays, 0, null);

    private static DisplayInfo Left4K(int w = 3840, int h = 2160)
        => Display(@"\\.\DISPLAY1", 0x10001, 0, 0, w, h, primary: true, output: 0);

    private static DisplayInfo Right1440()
        => Display(@"\\.\DISPLAY2", 0x20001, 3840, 0, 2560, 1440, output: 1, name: "DELL S2721DGF", connector: "HDMI");

    [Fact]
    public void Fingerprint_is_stable_for_equal_topologies_and_changes_with_a_mode_change()
    {
        var a = Topology(Left4K(), Right1440());
        var b = Topology(Left4K(), Right1440());
        Assert.Equal(a.Fingerprint, b.Fingerprint);

        var lowerMode = Topology(Left4K(1920, 1080), Right1440());
        Assert.NotEqual(a.Fingerprint, lowerMode.Fingerprint);
    }

    [Fact]
    public void Fingerprint_changes_when_the_output_or_the_primary_monitor_changes()
    {
        var a = Topology(Left4K(), Right1440());
        var swappedOutputs = Topology(Left4K() with { OutputIndex = 1 }, Right1440() with { OutputIndex = 0 });
        var otherPrimary = Topology(Left4K() with { IsPrimary = false }, Right1440() with { IsPrimary = true });

        Assert.NotEqual(a.Fingerprint, swappedOutputs.Fingerprint);
        Assert.NotEqual(a.Fingerprint, otherPrimary.Fingerprint);
    }

    [Fact]
    public void Fingerprint_ignores_enumeration_order()
    {
        var a = new DisplayTopology(new[] { Dgpu, Igpu, Basic }, new[] { Left4K(), Right1440() }, 0, null);
        var b = new DisplayTopology(new[] { Basic, Igpu, Dgpu }, new[] { Right1440(), Left4K() }, 0, null);
        Assert.Equal(a.Fingerprint, b.Fingerprint);
    }

    [Fact]
    public void Dump_lists_every_adapter_and_display_with_gdi_name_and_output()
    {
        var onIgpu = Display(@"\\.\DISPLAY6", 0x30001, -1920, 0, 1920, 1080, adapter: 1, output: 0, name: null, connector: null);
        var topo = new DisplayTopology(new[] { Dgpu, Igpu, Basic }, new[] { Left4K(), Right1440(), onIgpu }, 0,
                                       @"\\.\DISPLAY6: no display-config path");

        string dump = topo.Dump();
        var lines = dump.Split(Environment.NewLine);

        Assert.Equal(1 + 3 + 3, lines.Length);   // header + one line per adapter + one per display
        Assert.Contains("no display-config path", lines[0]);
        Assert.Contains(lines, l => l.Contains("AMD Radeon RX 9070 XT") && l.Contains("00000000:00015B44") && l.Contains("default"));
        Assert.Contains(lines, l => l.Contains("AMD Radeon(TM) Graphics") && l.Contains("00000000:000195A4"));
        Assert.Contains(lines, l => l.Contains("Microsoft Basic Render Driver") && l.Contains("software"));

        var displayLines = lines.Where(l => l.StartsWith("  display ")).ToArray();
        string first = Assert.Single(displayLines, l => l.Contains(@"\\.\DISPLAY1"));
        Assert.Contains("'LG ULTRAGEAR+'", first);
        Assert.Contains("3840x2160@(0,0)", first);
        Assert.Contains("primary", first);
        Assert.Contains("adapter=0 output=0", first);
        Assert.Contains("path=", first);

        string second = Assert.Single(displayLines, l => l.Contains(@"\\.\DISPLAY2"));
        Assert.Contains("adapter=0 output=1", second);
        Assert.Contains("HDMI", second);

        string third = Assert.Single(displayLines, l => l.Contains(@"\\.\DISPLAY6"));
        Assert.Contains("1920x1080@(-1920,0)", third);
        Assert.Contains("adapter=1 output=0", third);
    }

    [Fact]
    public void Primary_and_ByHMonitor_find_their_display()
    {
        var topo = Topology(Right1440(), Left4K());

        Assert.Equal(@"\\.\DISPLAY1", topo.Primary?.GdiName);
        Assert.Equal(@"\\.\DISPLAY2", topo.ByHMonitor(0x20001)?.GdiName);
        Assert.Null(topo.ByHMonitor(0x99999));
        Assert.Null(topo.ByHMonitor(0));
    }

    [Fact]
    public void Empty_has_no_displays_and_carries_the_problem()
    {
        var topo = DisplayTopology.Empty("DXGI failed");

        Assert.Empty(topo.Displays);
        Assert.Empty(topo.Adapters);
        Assert.Null(topo.Primary);
        Assert.Equal("DXGI failed", topo.Problem);
        Assert.Contains("DXGI failed", topo.Dump());
    }

    [Theory]
    [InlineData(10u, "DP")]            // DISPLAYPORT_EXTERNAL
    [InlineData(5u, "HDMI")]
    [InlineData(4u, "DVI")]
    [InlineData(18u, "USB-C")]         // DISPLAYPORT_USB_TUNNEL
    [InlineData(11u, "intern")]        // DISPLAYPORT_EMBEDDED (laptop panel)
    [InlineData(13u, "intern")]        // UDI_EMBEDDED
    [InlineData(6u, "intern")]         // LVDS
    [InlineData(0x80000000u, "intern")]
    [InlineData(0u, null)]             // VGA
    [InlineData(15u, null)]            // Miracast
    [InlineData(0xFFFFFFFFu, null)]    // OTHER
    public void Connector_is_the_short_text_of_the_output_technology(uint technology, string? expected)
        => Assert.Equal(expected, DisplayTopologyReader.ConnectorText(technology));
}
