using System.Linq;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>The monitor list of the capture view (decision 3): automatic entry, one entry per monitor, the missing saved one.</summary>
public sealed class CaptureMonitorListTests
{
    [Fact]
    public void Unset_selects_the_auto_entry()
    {
        var lg = TestDisplays.Lg();
        var dell = TestDisplays.Dell();
        var topo = TestDisplays.Topology(dell, lg);   // enumeration order: the secondary first

        var (items, selected) = CaptureMonitorList.Build(topo, null);

        Assert.Equal(0, selected);
        Assert.Equal(new CaptureMonitorChoice("Hauptmonitor (automatisch)", null), items[0]);
        // Then every monitor, left to right, labelled like Windows and carrying its identity.
        Assert.Equal(new[]
        {
            new CaptureMonitorChoice(MonitorLabels.Build(lg, topo), MonitorRef.Of(lg)),
            new CaptureMonitorChoice(MonitorLabels.Build(dell, topo), MonitorRef.Of(dell))
        }, items.Skip(1));
        Assert.Equal("LG ULTRAGEAR+ · 3440×1440 · Hauptmonitor", items[1].Display);
        Assert.DoesNotContain(items, i => i.Display.StartsWith("Monitor "));
    }

    [Fact]
    public void Saved_missing_monitor_is_listed_as_not_connected_and_selected()
    {
        var topo = TestDisplays.Topology(TestDisplays.Lg(), TestDisplays.Dell());
        var samsung = TestDisplays.Samsung();

        var (items, selected) = CaptureMonitorList.Build(topo, samsung);

        Assert.Equal(4, items.Count);
        Assert.Equal(3, selected);
        Assert.Equal(new CaptureMonitorChoice("SAMSUNG Odyssey G9 (nicht verbunden)", samsung), items[selected]);
    }

    [Fact]
    public void Missing_monitor_with_an_identical_twin_connected_is_listed_as_not_connected()
    {
        var twin = TestDisplays.Dell(uid: 257, gdi: @"\\.\DISPLAY2", output: 1);
        var chosen = MonitorRef.Of(TestDisplays.Dell(uid: 258, gdi: @"\\.\DISPLAY3", hmon: 0x30001, x: 6000, output: 2));

        var (items, selected) = CaptureMonitorList.Build(TestDisplays.Topology(TestDisplays.Lg(), twin), chosen);

        Assert.Equal(new CaptureMonitorChoice("DELL S2721DGF (nicht verbunden)", chosen), items[selected]);
        Assert.Equal(MonitorRef.Of(twin), items.Single(i => i.Display.StartsWith("DELL S2721DGF ·")).Value);
    }

    [Fact]
    public void Rebuild_keeps_the_selection()
    {
        var dell = TestDisplays.Dell(output: 1);
        var saved = MonitorRef.Of(dell);
        var before = TestDisplays.Topology(TestDisplays.Lg(), dell);
        var (itemsBefore, selectedBefore) = CaptureMonitorList.Build(before, saved);
        Assert.Equal(saved, itemsBefore[selectedBefore].Value);

        // The DELL became the primary (output 0, other GDI slots, new position) and is in another mode; a third monitor joined.
        var dellNow = TestDisplays.Dell(output: 0, gdi: @"\\.\DISPLAY1", x: 0, primary: true, w: 1920, h: 1080);
        var lgNow = TestDisplays.Lg(output: 1, gdi: @"\\.\DISPLAY2", x: -3440, primary: false);
        var extra = TestDisplays.Dell(uid: 300, hmon: 0x30001, gdi: @"\\.\DISPLAY3", x: 1920, output: 2);
        var after = TestDisplays.Topology(extra, lgNow, dellNow);

        var (items, selected) = CaptureMonitorList.Build(after, saved);

        Assert.Equal(4, items.Count);   // auto + three monitors, no "not connected" entry
        Assert.Equal(dellNow.DevicePath, items[selected].Value!.DevicePath);
        Assert.Equal(MonitorLabels.Build(dellNow, after), items[selected].Display);
        Assert.Contains("Hauptmonitor", items[selected].Display);
    }

    [Fact]
    public void Saved_monitor_on_another_gpu_stays_selected()
    {
        // Connected, but the capture cannot reach it: it is still the user's choice (the status line shows the fallback).
        var dell = TestDisplays.Dell(adapter: 1, output: 0);
        var topo = TestDisplays.Topology(TestDisplays.Lg(), dell);

        var (items, selected) = CaptureMonitorList.Build(topo, MonitorRef.Of(dell));

        Assert.Equal(3, items.Count);
        Assert.Equal(dell.HMonitor, topo.Displays.Single(d => MonitorRef.Of(d) == items[selected].Value).HMonitor);
    }

    [Fact]
    public void Sleeping_saved_monitor_stays_selected_and_keeps_its_path()
    {
        // A sleeping DisplayPort monitor: Windows reports no device path, the EDID model still finds it.
        var dell = TestDisplays.Dell();
        var saved = MonitorRef.Of(dell);
        var topo = TestDisplays.Topology(TestDisplays.Lg(), dell with { DevicePath = null });

        var (items, selected) = CaptureMonitorList.Build(topo, saved);

        Assert.Equal(3, items.Count);
        Assert.Equal(saved, items[selected].Value);   // picking it again must not drop the path
    }

    [Fact]
    public void Old_setting_with_only_the_gdi_name_selects_that_monitor()
    {
        var topo = TestDisplays.Topology(TestDisplays.Lg(), TestDisplays.Dell());

        var (items, selected) = CaptureMonitorList.Build(topo, new MonitorRef(null, @"\\.\DISPLAY2", null, null, null));

        Assert.Equal(3, items.Count);
        Assert.Equal("DELL S2721DGF", items[selected].Value!.FriendlyName);
    }
}
