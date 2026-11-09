using System;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class CaptureSourceRulesTests
{
    private static CaptureSource SourceOf(DisplayInfo d, DisplayTopology topo)
    {
        var r = MonitorTargetRules.Resolve(MonitorRef.Of(d), topo);
        Assert.Equal(d.HMonitor, r.Display!.HMonitor);
        return CaptureSourceRules.ForDisplay(r.Display, topo, r)!;
    }

    [Fact]
    public void Source_carries_output_adapter_size_label_and_resolution()
    {
        var dell = TestDisplays.Dell();
        var topo = TestDisplays.Topology(TestDisplays.Lg(), dell);
        var r = MonitorTargetRules.Resolve(MonitorRef.Of(dell), topo);

        var s = CaptureSourceRules.ForDisplay(dell, topo, r)!;

        Assert.Equal(dell.DevicePath!.ToLowerInvariant(), s.DisplayKey);
        Assert.Equal(@"\\.\DISPLAY2", s.GdiName);
        Assert.Equal(dell.HMonitor, s.HMonitor);
        Assert.Equal(1, s.OutputIndex);
        Assert.Equal(TestDisplays.Dgpu.Luid, s.AdapterLuid);
        Assert.Equal(2560, s.Width);
        Assert.Equal(1440, s.Height);
        Assert.Equal("DELL S2721DGF · 2560×1440 · rechts", s.Label);
        Assert.Same(r, s.Resolution);
    }

    [Fact]
    public void Display_key_is_the_lower_case_path_else_the_gdi_name()
    {
        var withPath = TestDisplays.Dell();
        var withoutPath = TestDisplays.Dell(path: null, gdi: @"\\.\display2");
        var topo = TestDisplays.Topology(TestDisplays.Lg(), withPath);
        var r = MonitorTargetRules.Resolve(null, topo);

        Assert.Equal(withPath.DevicePath!.ToLowerInvariant(), CaptureSourceRules.ForDisplay(withPath, topo, r)!.DisplayKey);
        Assert.Equal(@"gdi:\\.\DISPLAY2", CaptureSourceRules.ForDisplay(withoutPath, topo, r)!.DisplayKey);
    }

    [Fact]
    public void No_source_without_an_output_on_the_default_adapter()
    {
        var topo = TestDisplays.Topology(TestDisplays.Lg());
        var r = MonitorTargetRules.Resolve(null, topo);

        Assert.Null(CaptureSourceRules.ForDisplay(TestDisplays.Dell(adapter: 1, output: 0), topo, r));
        Assert.Null(CaptureSourceRules.ForDisplay(TestDisplays.Dell(adapter: 0, output: null), topo, r));
        Assert.Null(CaptureSourceRules.ForDisplay(TestDisplays.Dell(adapter: null, output: null), topo, r));

        var noAdapters = new DisplayTopology(Array.Empty<DisplayAdapterInfo>(), new[] { TestDisplays.Lg() }, -1, "DXGI failed");
        Assert.Null(CaptureSourceRules.ForDisplay(TestDisplays.Lg(), noAdapters, r));
    }

    [Fact]
    public void Mode_change_is_same_monitor_but_not_same_source()
    {
        var before = SourceOf(TestDisplays.Dell(), TestDisplays.Topology(TestDisplays.Lg(), TestDisplays.Dell()));
        var again = SourceOf(TestDisplays.Dell(), TestDisplays.Topology(TestDisplays.Lg(), TestDisplays.Dell()));
        var lowerMode = SourceOf(TestDisplays.Dell(w: 1920, h: 1080),
                                 TestDisplays.Topology(TestDisplays.Lg(), TestDisplays.Dell(w: 1920, h: 1080)));

        Assert.True(CaptureSourceRules.SameSource(before, again));
        Assert.True(CaptureSourceRules.SameMonitor(before, lowerMode));
        Assert.False(CaptureSourceRules.SameSource(before, lowerMode));
    }

    [Fact]
    public void New_primary_renumbers_the_output_same_monitor_but_not_same_source()
    {
        var before = SourceOf(TestDisplays.Dell(output: 1), TestDisplays.Topology(TestDisplays.Lg(), TestDisplays.Dell(output: 1)));
        var dellPrimary = TestDisplays.Dell(primary: true, output: 0);
        var after = SourceOf(dellPrimary, TestDisplays.Topology(TestDisplays.Lg(primary: false, output: 1), dellPrimary));

        Assert.True(CaptureSourceRules.SameMonitor(before, after));
        Assert.False(CaptureSourceRules.SameSource(before, after));
    }

    [Fact]
    public void Other_monitor_with_same_output_and_size_is_not_same_monitor()
    {
        var first = TestDisplays.Dell(uid: 257);
        var second = TestDisplays.Dell(uid: 258);   // identical model, same output index and size after a swap
        var a = SourceOf(first, TestDisplays.Topology(TestDisplays.Lg(), first));
        var b = SourceOf(second, TestDisplays.Topology(TestDisplays.Lg(), second));

        Assert.Equal(a.OutputIndex, b.OutputIndex);
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        Assert.False(CaptureSourceRules.SameMonitor(a, b));
        Assert.False(CaptureSourceRules.SameSource(a, b));
    }

    [Fact]
    public void Amf_index_only_on_a_single_output_adapter_topology()
    {
        var dell = TestDisplays.Dell(output: 1);
        var single = TestDisplays.Topology(TestDisplays.Lg(), dell);
        Assert.Equal(1, SourceOf(dell, single).AmfIndex);
        Assert.Equal(0, SourceOf(TestDisplays.Lg(), single).AmfIndex);

        // A second adapter with an output: vsrc_amf's monitor numbering is not certain any more.
        var onIgpu = TestDisplays.Dell(uid: 258, gdi: @"\\.\DISPLAY6", hmon: 0x30001, x: -2560, adapter: 1, output: 0);
        var twoAdapters = TestDisplays.Topology(TestDisplays.Lg(), dell, onIgpu);
        var s = SourceOf(dell, twoAdapters);
        Assert.Null(s.AmfIndex);
        Assert.Equal(1, s.OutputIndex);   // ddagrab still has its certain index

        // A monitor DXGI did not report: also not certain.
        var unknown = TestDisplays.Dell(uid: 259, gdi: @"\\.\DISPLAY3", hmon: 0x40001, x: -2560, adapter: null, output: null);
        Assert.Null(SourceOf(dell, TestDisplays.Topology(TestDisplays.Lg(), dell, unknown)).AmfIndex);
    }
}
