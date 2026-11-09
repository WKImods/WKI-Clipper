using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class MonitorLabelsTests
{
    [Fact]
    public void Label_has_name_size_side_and_primary()
    {
        var lg = TestDisplays.Lg();
        var right = TestDisplays.Dell(x: 3440, y: 0);
        var topo = TestDisplays.Topology(right, lg);

        Assert.Equal("LG ULTRAGEAR+ · 3440×1440 · Hauptmonitor", MonitorLabels.Build(lg, topo));
        Assert.Equal("DELL S2721DGF · 2560×1440 · rechts", MonitorLabels.Build(right, topo));
    }

    [Theory]
    [InlineData(-2560, 0, "links")]
    [InlineData(3440, 200, "rechts")]
    [InlineData(440, -1440, "oben")]
    [InlineData(440, 1440, "unten")]
    [InlineData(3440, -1300, "rechts")]   // diagonal: the larger offset of the centres decides
    public void Side_is_taken_from_the_centre_relative_to_the_primary(int x, int y, string side)
    {
        var dell = TestDisplays.Dell(x: x, y: y);
        var topo = TestDisplays.Topology(TestDisplays.Lg(), dell);

        Assert.Equal($"DELL S2721DGF · 2560×1440 · {side}", MonitorLabels.Build(dell, topo));
    }

    [Fact]
    public void Size_is_the_current_mode()
    {
        var dell = TestDisplays.Dell(w: 1920, h: 1080);

        Assert.Equal("DELL S2721DGF · 1920×1080 · rechts", MonitorLabels.Build(dell, TestDisplays.Topology(TestDisplays.Lg(), dell)));
    }

    [Fact]
    public void Monitor_without_a_name_is_called_unnamed()
    {
        var unnamed = TestDisplays.Dell() with { FriendlyName = null };
        var topo = TestDisplays.Topology(TestDisplays.Lg(), unnamed);

        Assert.Equal("Monitor ohne Namen · 2560×1440 · rechts", MonitorLabels.Build(unnamed, topo));
        Assert.Equal("Monitor ohne Namen (nicht verbunden)", MonitorLabels.BuildMissing(MonitorRef.Of(unnamed)));
    }

    [Fact]
    public void Duplicate_names_get_connector_then_number()
    {
        var right = TestDisplays.Dell(uid: 257, gdi: @"\\.\DISPLAY2", hmon: 0x20001, x: 3440, connector: "HDMI");
        var left = TestDisplays.Dell(uid: 258, gdi: @"\\.\DISPLAY3", hmon: 0x30001, x: -2560, connector: "DP");
        var lg = TestDisplays.Lg();
        var byConnector = TestDisplays.Topology(lg, right, left);

        Assert.Equal("DELL S2721DGF (HDMI) · 2560×1440 · rechts", MonitorLabels.Build(right, byConnector));
        Assert.Equal("DELL S2721DGF (DP) · 2560×1440 · links", MonitorLabels.Build(left, byConnector));
        Assert.Equal("LG ULTRAGEAR+ · 3440×1440 · Hauptmonitor", MonitorLabels.Build(lg, byConnector));

        // Same connector too: numbered by X, then Y.
        var leftHdmi = left with { Connector = "HDMI" };
        var above = TestDisplays.Dell(uid: 259, gdi: @"\\.\DISPLAY4", hmon: 0x40001, x: 3440, y: -1440, connector: "HDMI");
        var numbered = TestDisplays.Topology(right, above, lg, leftHdmi);

        Assert.Equal("DELL S2721DGF (HDMI) · 2560×1440 · links", MonitorLabels.Build(leftHdmi, numbered));
        Assert.Equal("DELL S2721DGF (HDMI) (2) · 2560×1440 · rechts", MonitorLabels.Build(above, numbered));
        Assert.Equal("DELL S2721DGF (HDMI) (3) · 2560×1440 · rechts", MonitorLabels.Build(right, numbered));
    }

    [Fact]
    public void Missing_monitor_label_says_not_connected()
        => Assert.Equal("SAMSUNG Odyssey G9 (nicht verbunden)", MonitorLabels.BuildMissing(TestDisplays.Samsung()));

    [Fact]
    public void Auto_entry_is_the_primary_monitor()
        => Assert.Equal("Hauptmonitor (automatisch)", MonitorLabels.AutoPrimary);

    [Fact]
    public void Fallback_label_names_both()
    {
        var lg = TestDisplays.Lg();
        string primary = MonitorLabels.Build(lg, TestDisplays.Topology(lg));

        Assert.Equal("Ersatz: LG ULTRAGEAR+ · 3440×1440 · Hauptmonitor (SAMSUNG Odyssey G9 nicht verbunden)",
                     MonitorLabels.Fallback(primary, TestDisplays.Samsung()));
    }
}

/// <summary>The monitor texts that follow the app language.</summary>
[Collection("Language")]
public sealed class MonitorLabelsEnglishTests
{
    [Fact]
    public void English_labels()
    {
        using var _ = TestLanguage.English();
        var lg = TestDisplays.Lg();
        var dell = TestDisplays.Dell(x: -2560);
        var topo = TestDisplays.Topology(lg, dell);

        Assert.Equal("LG ULTRAGEAR+ · 3440×1440 · primary", MonitorLabels.Build(lg, topo));
        Assert.Equal("DELL S2721DGF · 2560×1440 · left", MonitorLabels.Build(dell, topo));
        Assert.Equal("Unnamed monitor · 2560×1440 · left", MonitorLabels.Build(dell with { FriendlyName = null }, topo));
        Assert.Equal("SAMSUNG Odyssey G9 (not connected)", MonitorLabels.BuildMissing(TestDisplays.Samsung()));
        Assert.Equal("Primary monitor (automatic)", MonitorLabels.AutoPrimary);
        Assert.Equal("Fallback: LG ULTRAGEAR+ (SAMSUNG Odyssey G9 not connected)",
                     MonitorLabels.Fallback("LG ULTRAGEAR+", TestDisplays.Samsung()));
    }

    [Fact]
    public void English_fallback_reasons()
    {
        using var _ = TestLanguage.English();
        var onIgpu = TestDisplays.Dell(gdi: @"\\.\DISPLAY6", adapter: 1, output: 0);
        var topo = TestDisplays.Topology(TestDisplays.Lg(), onIgpu);

        Assert.Equal("“SAMSUNG Odyssey G9” is not connected",
                     MonitorTargetRules.Resolve(TestDisplays.Samsung(), topo).Reason);
        Assert.Equal("“DELL S2721DGF” is connected to “AMD Radeon(TM) Graphics”, the capture only reaches “AMD Radeon RX 9070 XT”",
                     MonitorTargetRules.Resolve(MonitorRef.Of(onIgpu), topo).Reason);
    }
}
