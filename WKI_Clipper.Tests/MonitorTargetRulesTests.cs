using System;
using System.Linq;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Fake monitors and GPUs for the monitor rules tests (shaped like the developer PC: dGPU = adapter 0).</summary>
internal static class TestDisplays
{
    public static readonly DisplayAdapterInfo Dgpu = new(0, 0x15B44, "AMD Radeon RX 9070 XT", false);
    public static readonly DisplayAdapterInfo Igpu = new(1, 0x195A4, "AMD Radeon(TM) Graphics", false);
    public static readonly DisplayAdapterInfo Basic = new(2, 0x1A2B3, "Microsoft Basic Render Driver", true);

    public const ushort LgMfg = 0x6D1E, LgProduct = 0x780B;
    public const ushort DellMfg = 0xAC10, DellProduct = 0xA0F5;

    public static string PathOf(string model, int uid)
        => $@"\\?\DISPLAY#{model}#7&2e952966&2&UID{uid}#{{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}}";

    /// <summary>The primary 3440x1440 LG on DP, output 0 of the dGPU.</summary>
    public static DisplayInfo Lg(int x = 0, int y = 0, int w = 3440, int h = 1440, bool primary = true,
        int? adapter = 0, int? output = 0, string gdi = @"\\.\DISPLAY1")
        => new(0x10001, gdi, PathOf("GSM780B", 256), "LG ULTRAGEAR+", x, y, w, h, primary, adapter, output, "DP",
               LgMfg, LgProduct);

    /// <summary>A 2560x1440 DELL on HDMI right of the LG, output 1 of the dGPU. <paramref name="uid"/> tells identical models apart.</summary>
    public static DisplayInfo Dell(int uid = 257, string gdi = @"\\.\DISPLAY2", nint hmon = 0x20001, int x = 3440,
        int y = 0, int w = 2560, int h = 1440, bool primary = false, int? adapter = 0, int? output = 1,
        string connector = "HDMI", string? path = "")
        => new(hmon, gdi, path == "" ? PathOf("DELA0F5", uid) : path, "DELL S2721DGF", x, y, w, h, primary, adapter, output,
               connector, DellMfg, DellProduct);

    public static DisplayTopology Topology(params DisplayInfo[] displays)
        => new(new[] { Dgpu, Igpu, Basic }, displays, 0, null);

    /// <summary>A saved monitor that is not connected in any test topology.</summary>
    public static MonitorRef Samsung()
        => new(PathOf("SAM7154", 300), @"\\.\DISPLAY3", "SAMSUNG Odyssey G9", 0x2D4C, 0x7154);
}

public sealed class MonitorTargetRulesTests
{
    private static int OutputOf(MonitorResolution r, DisplayTopology topo)
        => CaptureSourceRules.ForDisplay(r.Display!, topo, r)!.OutputIndex;

    [Fact]
    public void Primary_not_first_in_enum_order_maps_to_its_dxgi_output()
    {
        var dell = TestDisplays.Dell(output: 1);
        var lg = TestDisplays.Lg(output: 0);
        var topo = TestDisplays.Topology(dell, lg);   // EnumDisplayMonitors order: the secondary comes first

        var byDefault = MonitorTargetRules.Resolve(null, topo);
        Assert.Equal(MonitorResolutionStatus.PrimaryByDefault, byDefault.Status);
        Assert.Equal(0, OutputOf(byDefault, topo));

        var chosen = MonitorTargetRules.Resolve(MonitorRef.Of(dell), topo);
        Assert.Equal(MonitorResolutionStatus.Exact, chosen.Status);
        Assert.Equal(1, OutputOf(chosen, topo));
    }

    [Fact]
    public void Saved_path_wins_over_swapped_gdi_names()
    {
        var saved = MonitorRef.Of(TestDisplays.Dell(gdi: @"\\.\DISPLAY2"));
        // After a replug the driver handed out the source slots the other way round.
        var topo = TestDisplays.Topology(TestDisplays.Lg(gdi: @"\\.\DISPLAY2"), TestDisplays.Dell(gdi: @"\\.\DISPLAY1"));

        var r = MonitorTargetRules.Resolve(saved, topo);

        Assert.Equal(MonitorResolutionStatus.Exact, r.Status);
        Assert.Equal("DELL S2721DGF", r.Display!.FriendlyName);
        Assert.Equal(@"\\.\DISPLAY1", r.Display.GdiName);
        Assert.False(r.IsFallback);
        // The stale GDI name (the tie-breaker for identical models) is refreshed.
        Assert.True(r.UpgradeSavedRef);
        Assert.Equal(@"\\.\DISPLAY1", MonitorRef.Of(r.Display).GdiName);
    }

    [Fact]
    public void Saved_path_matches_without_regard_to_case()
    {
        var dell = TestDisplays.Dell();
        var saved = MonitorRef.Of(dell) with { DevicePath = dell.DevicePath!.ToUpperInvariant() };

        var r = MonitorTargetRules.Resolve(saved, TestDisplays.Topology(TestDisplays.Lg(), dell));

        Assert.Equal(MonitorResolutionStatus.Exact, r.Status);
        Assert.Equal(dell.HMonitor, r.Display!.HMonitor);
    }

    [Fact]
    public void Gdi_name_only_resolves_as_legacy_and_requests_upgrade()
    {
        // A 0.19 settings file: only the GDI name was stored.
        var profile = new CaptureProfile { Mode = CaptureMode.Monitor, MonitorDeviceName = @"\\.\DISPLAY2" };
        var saved = MonitorRef.FromProfile(profile);
        Assert.Equal(new MonitorRef(null, @"\\.\DISPLAY2", null, null, null), saved);

        var r = MonitorTargetRules.Resolve(saved, TestDisplays.Topology(TestDisplays.Lg(), TestDisplays.Dell()));

        Assert.Equal(MonitorResolutionStatus.Legacy, r.Status);
        Assert.Equal("DELL S2721DGF", r.Display!.FriendlyName);
        Assert.True(r.UpgradeSavedRef);
        var upgraded = MonitorRef.Of(r.Display);
        Assert.Equal(TestDisplays.PathOf("DELA0F5", 257), upgraded.DevicePath);
        Assert.Equal(TestDisplays.DellMfg, upgraded.EdidMfg);
        Assert.Equal(TestDisplays.DellProduct, upgraded.EdidProduct);
    }

    [Fact]
    public void Missing_monitor_falls_back_to_primary_never_to_index_0()
    {
        var topo = TestDisplays.Topology(TestDisplays.Dell(output: 1), TestDisplays.Lg(output: 0));

        var r = MonitorTargetRules.Resolve(TestDisplays.Samsung(), topo);

        Assert.Equal(MonitorResolutionStatus.FallbackPrimary, r.Status);
        Assert.True(r.IsFallback);
        Assert.Equal("LG ULTRAGEAR+", r.Display!.FriendlyName);   // the primary, not Displays[0]
        Assert.True(r.Display.IsPrimary);
        Assert.Equal(0, OutputOf(r, topo));
        Assert.Equal("„SAMSUNG Odyssey G9“ ist nicht verbunden", r.Reason);
        Assert.False(r.UpgradeSavedRef);
    }

    [Fact]
    public void A_saved_path_that_is_gone_is_never_matched_by_the_gdi_name_alone()
    {
        // The Samsung was DISPLAY2; now the DELL holds that slot.
        var saved = TestDisplays.Samsung() with { GdiName = @"\\.\DISPLAY2" };

        var r = MonitorTargetRules.Resolve(saved, TestDisplays.Topology(TestDisplays.Lg(), TestDisplays.Dell()));

        Assert.Equal(MonitorResolutionStatus.FallbackPrimary, r.Status);
        Assert.Equal("LG ULTRAGEAR+", r.Display!.FriendlyName);
    }

    [Fact]
    public void Null_setting_is_primary_without_fallback()
    {
        var saved = MonitorRef.FromProfile(new CaptureProfile { Mode = CaptureMode.Monitor });
        Assert.Null(saved);

        var r = MonitorTargetRules.Resolve(saved, TestDisplays.Topology(TestDisplays.Dell(), TestDisplays.Lg()));

        Assert.Equal(MonitorResolutionStatus.PrimaryByDefault, r.Status);
        Assert.False(r.IsFallback);
        Assert.Equal("LG ULTRAGEAR+", r.Display!.FriendlyName);
        Assert.False(r.UpgradeSavedRef);
    }

    [Fact]
    public void Identical_models_with_changed_path_are_not_guessed()
    {
        var topo = TestDisplays.Topology(
            TestDisplays.Lg(),
            TestDisplays.Dell(uid: 257, gdi: @"\\.\DISPLAY2", hmon: 0x20001, x: 3440, output: 1),
            TestDisplays.Dell(uid: 258, gdi: @"\\.\DISPLAY3", hmon: 0x30001, x: -2560, output: 2));
        var savedPath = TestDisplays.PathOf("DELA0F5", 259);   // the path changed (driver reinstall)

        var withOtherGdi = new MonitorRef(savedPath, @"\\.\DISPLAY4", "DELL S2721DGF", TestDisplays.DellMfg, TestDisplays.DellProduct);
        var withoutGdi = withOtherGdi with { GdiName = null };

        foreach (var saved in new[] { withOtherGdi, withoutGdi })
        {
            var r = MonitorTargetRules.Resolve(saved, topo);
            Assert.Equal(MonitorResolutionStatus.FallbackPrimary, r.Status);
            Assert.Equal("LG ULTRAGEAR+", r.Display!.FriendlyName);
            Assert.False(r.UpgradeSavedRef);
        }
    }

    [Fact]
    public void Identical_models_with_changed_path_resolve_by_gdi_tie_breaker()
    {
        var right = TestDisplays.Dell(uid: 257, gdi: @"\\.\DISPLAY2", hmon: 0x20001, x: 3440, output: 1);
        var left = TestDisplays.Dell(uid: 258, gdi: @"\\.\DISPLAY3", hmon: 0x30001, x: -2560, output: 2);
        var topo = TestDisplays.Topology(TestDisplays.Lg(), right, left);
        var saved = new MonitorRef(TestDisplays.PathOf("DELA0F5", 259), @"\\.\DISPLAY3", "DELL S2721DGF",
                                   TestDisplays.DellMfg, TestDisplays.DellProduct);

        var r = MonitorTargetRules.Resolve(saved, topo);

        Assert.Equal(MonitorResolutionStatus.Matched, r.Status);
        Assert.Equal(left.HMonitor, r.Display!.HMonitor);
        Assert.Equal(2, OutputOf(r, topo));
        Assert.True(r.UpgradeSavedRef);
        Assert.Equal(left.DevicePath, MonitorRef.Of(r.Display).DevicePath);
    }

    [Fact]
    public void Unique_model_on_a_new_port_matches_only_with_the_saved_gdi_name()
    {
        var saved = MonitorRef.Of(TestDisplays.Dell(uid: 257));
        var movedSameSlot = TestDisplays.Dell(uid: 260);

        var r = MonitorTargetRules.Resolve(saved, TestDisplays.Topology(TestDisplays.Lg(), movedSameSlot));

        Assert.Equal(MonitorResolutionStatus.Matched, r.Status);
        Assert.Equal(movedSameSlot.DevicePath, r.Display!.DevicePath);
        Assert.True(r.UpgradeSavedRef);

        // Another path AND another GDI slot: exactly what an identical twin of a monitor that is gone looks like - not guessed.
        var movedOtherSlot = TestDisplays.Dell(uid: 260, gdi: @"\\.\DISPLAY4");
        var guess = MonitorTargetRules.Resolve(saved, TestDisplays.Topology(TestDisplays.Lg(), movedOtherSlot));

        Assert.Equal(MonitorResolutionStatus.FallbackPrimary, guess.Status);
        Assert.Equal("LG ULTRAGEAR+", guess.Display!.FriendlyName);
        Assert.False(guess.UpgradeSavedRef);
    }

    [Fact]
    public void Identical_twin_of_a_missing_chosen_monitor_is_not_taken_for_it()
    {
        // Two DELLs of the same model: A is the primary on the left, B (chosen) on the right. B is unplugged.
        var a = TestDisplays.Dell(uid: 257, gdi: @"\\.\DISPLAY1", hmon: 0x10001, x: 0, primary: true, output: 0);
        var b = TestDisplays.Dell(uid: 258, gdi: @"\\.\DISPLAY2", hmon: 0x20001, x: 2560, output: 1);
        var saved = MonitorRef.Of(b);

        var gone = MonitorTargetRules.Resolve(saved, TestDisplays.Topology(a));

        Assert.Equal(MonitorResolutionStatus.FallbackPrimary, gone.Status);   // visible: warning, "Ersatz: ..." label
        Assert.Same(a, gone.Display);
        Assert.Equal("„DELL S2721DGF“ ist nicht verbunden", gone.Reason);
        Assert.False(gone.UpgradeSavedRef);                                    // the setting keeps B
        Assert.Null(MonitorTargetRules.Locate(saved, TestDisplays.Topology(a)));

        // Asleep, A reports no device path: still not B.
        Assert.Equal(MonitorResolutionStatus.FallbackPrimary,
                     MonitorTargetRules.Resolve(saved, TestDisplays.Topology(a with { DevicePath = null })).Status);

        // B is back: exactly B again.
        var back = MonitorTargetRules.Resolve(saved, TestDisplays.Topology(a, b));
        Assert.Equal(MonitorResolutionStatus.Exact, back.Status);
        Assert.Same(b, back.Display);

        // Three monitors: the LG is the primary, A and B are twins, B is missing - the LG is recorded, not A.
        var a2 = TestDisplays.Dell(uid: 257, gdi: @"\\.\DISPLAY2", hmon: 0x20001, x: 3440, output: 1);
        var b2 = TestDisplays.Dell(uid: 258, gdi: @"\\.\DISPLAY3", hmon: 0x30001, x: 6000, output: 2);
        var three = MonitorTargetRules.Resolve(MonitorRef.Of(b2), TestDisplays.Topology(TestDisplays.Lg(), a2));
        Assert.Equal(MonitorResolutionStatus.FallbackPrimary, three.Status);
        Assert.Equal("LG ULTRAGEAR+", three.Display!.FriendlyName);
        Assert.False(three.UpgradeSavedRef);
    }

    [Fact]
    public void Sleeping_monitor_without_a_device_path_matches_by_edid_and_keeps_the_saved_path()
    {
        // Seen live: a sleeping DisplayPort monitor keeps name, EDID, rect and DXGI output, but CCD reports no path.
        var awake = TestDisplays.Dell();
        var saved = MonitorRef.Of(awake);
        var asleep = awake with { DevicePath = null };
        var topo = TestDisplays.Topology(TestDisplays.Lg(), asleep);

        var r = MonitorTargetRules.Resolve(saved, topo);

        Assert.Equal(MonitorResolutionStatus.Matched, r.Status);
        Assert.Equal(asleep.HMonitor, r.Display!.HMonitor);
        Assert.Equal(awake.DevicePath, r.Display.DevicePath);   // the identity stays the saved one
        Assert.False(r.UpgradeSavedRef);                         // nothing new to save, and never a lost path
        Assert.Equal(awake.DevicePath!.ToLowerInvariant(), CaptureSourceRules.ForDisplay(r.Display, topo, r)!.DisplayKey);
    }

    [Fact]
    public void Monitor_on_another_adapter_falls_back_with_both_gpu_names()
    {
        var onIgpu = TestDisplays.Dell(gdi: @"\\.\DISPLAY6", adapter: 1, output: 0);
        var topo = TestDisplays.Topology(TestDisplays.Lg(), onIgpu);

        var r = MonitorTargetRules.Resolve(MonitorRef.Of(onIgpu), topo);

        Assert.Equal(MonitorResolutionStatus.FallbackPrimary, r.Status);
        Assert.Equal("LG ULTRAGEAR+", r.Display!.FriendlyName);
        Assert.Equal("„DELL S2721DGF“ hängt an „AMD Radeon(TM) Graphics“, die Aufnahme erreicht nur „AMD Radeon RX 9070 XT“",
                     r.Reason);
        Assert.False(r.UpgradeSavedRef);
    }

    [Fact]
    public void Monitor_without_a_dxgi_output_falls_back_with_a_reason()
    {
        var noOutput = TestDisplays.Dell(adapter: null, output: null);

        var r = MonitorTargetRules.Resolve(MonitorRef.Of(noOutput), TestDisplays.Topology(TestDisplays.Lg(), noOutput));

        Assert.Equal(MonitorResolutionStatus.FallbackPrimary, r.Status);
        Assert.Equal("LG ULTRAGEAR+", r.Display!.FriendlyName);
        Assert.Contains("DELL S2721DGF", r.Reason);
        Assert.Contains("DXGI", r.Reason);
    }

    [Fact]
    public void No_displays_is_reported()
    {
        var empty = DisplayTopology.Empty("EnumDisplayMonitors failed");

        foreach (var saved in new[] { null, MonitorRef.Of(TestDisplays.Lg()) })
        {
            var r = MonitorTargetRules.Resolve(saved, empty);
            Assert.Equal(MonitorResolutionStatus.NoDisplays, r.Status);
            Assert.Null(r.Display);
            Assert.False(r.IsFallback);
            Assert.False(r.UpgradeSavedRef);
            Assert.False(string.IsNullOrWhiteSpace(r.Reason));
        }
    }

    [Fact]
    public void Primary_the_capture_cannot_reach_is_reported_as_no_displays()
    {
        var primaryOnIgpu = TestDisplays.Lg(adapter: 1, output: 0);
        var primaryWithoutOutput = TestDisplays.Lg(adapter: null, output: null);

        foreach (var primary in new[] { primaryOnIgpu, primaryWithoutOutput })
        {
            var topo = TestDisplays.Topology(primary, TestDisplays.Dell(output: 0));

            var unset = MonitorTargetRules.Resolve(null, topo);
            Assert.Equal(MonitorResolutionStatus.NoDisplays, unset.Status);
            Assert.Null(unset.Display);
            Assert.Contains("LG ULTRAGEAR+", unset.Reason);

            var missing = MonitorTargetRules.Resolve(TestDisplays.Samsung(), topo);
            Assert.Equal(MonitorResolutionStatus.NoDisplays, missing.Status);
            Assert.Contains("SAMSUNG Odyssey G9", missing.Reason);

            // A reachable chosen monitor is still recorded.
            var chosen = MonitorTargetRules.Resolve(MonitorRef.Of(TestDisplays.Dell()), topo);
            Assert.Equal(MonitorResolutionStatus.Exact, chosen.Status);
        }
    }

    [Fact]
    public void Same_monitor_in_another_mode_and_position_and_primary_flag_still_resolves_exact()
    {
        var saved = MonitorRef.Of(TestDisplays.Dell());
        // R1: the DELL now runs 1920x1080, sits left of the LG, is the primary and DXGI output 0.
        var topo = TestDisplays.Topology(
            TestDisplays.Lg(x: 0, primary: false, output: 1),
            TestDisplays.Dell(x: -1920, w: 1920, h: 1080, primary: true, output: 0));

        var r = MonitorTargetRules.Resolve(saved, topo);

        Assert.Equal(MonitorResolutionStatus.Exact, r.Status);
        Assert.Equal("DELL S2721DGF", r.Display!.FriendlyName);
        Assert.Equal(1920, r.Display.Width);
        Assert.False(r.UpgradeSavedRef);
        Assert.Equal(0, OutputOf(r, topo));
    }

    [Fact]
    public void Every_resolved_display_is_capturable()
    {
        var topo = TestDisplays.Topology(TestDisplays.Lg(), TestDisplays.Dell(), TestDisplays.Dell(uid: 258,
            gdi: @"\\.\DISPLAY6", hmon: 0x30001, adapter: 1, output: 0));
        var saves = new MonitorRef?[]
        {
            null, MonitorRef.Of(TestDisplays.Dell()), TestDisplays.Samsung(),
            new(null, @"\\.\DISPLAY2", null, null, null), new(null, @"\\.\DISPLAY6", null, null, null)
        };

        foreach (var saved in saves)
        {
            var r = MonitorTargetRules.Resolve(saved, topo);
            Assert.NotEqual(MonitorResolutionStatus.NoDisplays, r.Status);
            Assert.NotNull(CaptureSourceRules.ForDisplay(r.Display!, topo, r));
        }
    }

    [Fact]
    public void Profile_fields_become_the_saved_ref_and_blank_values_count_as_unset()
    {
        var profile = new CaptureProfile
        {
            Mode = CaptureMode.Monitor,
            MonitorDevicePath = TestDisplays.PathOf("DELA0F5", 257),
            MonitorDeviceName = @"\\.\DISPLAY2",
            MonitorFriendlyName = "DELL S2721DGF",
            MonitorEdidMfg = TestDisplays.DellMfg,
            MonitorEdidProduct = TestDisplays.DellProduct,
        };
        Assert.Equal(MonitorRef.Of(TestDisplays.Dell()), MonitorRef.FromProfile(profile));

        var blank = new CaptureProfile { MonitorDevicePath = "", MonitorDeviceName = "  ", MonitorFriendlyName = "" };
        Assert.Null(MonitorRef.FromProfile(blank));
    }
}
