using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Which exe an F9 press (and the status lines) attribute the clip to. The length has to follow the footage and the
/// folder - the pinned window, then what is really in front - not only the debounced detector state.
/// </summary>
public sealed class PressExeTests
{
    private static Func<string?> Source(List<string> asked, string tag, string? answer)
        => () => { asked.Add(tag); return answer; };

    [Fact]
    public void The_pinned_window_wins_over_the_live_foreground_which_wins_over_the_detector()
    {
        var asked = new List<string>();
        Assert.Equal("pinned.exe", PressClipLength.ResolveExe(
            Source(asked, "pinned", "pinned.exe"), Source(asked, "live", "live.exe"), Source(asked, "detector", "detector.exe")));
        Assert.Equal("live.exe", PressClipLength.ResolveExe(
            Source(asked, "pinned", null), Source(asked, "live", "live.exe"), Source(asked, "detector", "detector.exe")));
        Assert.Equal("detector.exe", PressClipLength.ResolveExe(
            Source(asked, "pinned", null), Source(asked, "live", null), Source(asked, "detector", "detector.exe")));
        Assert.Null(PressClipLength.ResolveExe(
            Source(asked, "pinned", null), Source(asked, "live", null), Source(asked, "detector", null)));
    }

    [Fact]
    public void Later_sources_are_not_asked_once_an_earlier_one_answered()
    {
        var asked = new List<string>();
        PressClipLength.ResolveExe(Source(asked, "pinned", "pinned.exe"), Source(asked, "live", "live.exe"));
        Assert.Equal(new[] { "pinned" }, asked);   // no foreground probe when the footage is a pinned window
    }

    [Fact]
    public void A_source_that_throws_or_answers_blank_is_skipped()
    {
        var asked = new List<string>();
        Assert.Equal("detector.exe", PressClipLength.ResolveExe(
            () => throw new InvalidOperationException("probe broke"),
            Source(asked, "blank", "  "),
            Source(asked, "empty", ""),
            Source(asked, "detector", "detector.exe")));
        Assert.Equal(new[] { "blank", "empty", "detector" }, asked);
    }

    [Fact]
    public void No_sources_means_no_game_and_the_standard_length()
    {
        Assert.Null(PressClipLength.ResolveExe());
        Assert.Null(PressClipLength.ResolveExe(() => null));
    }
}

/// <summary>The exe of the window a WGC capture pins - what the clip's footage and folder are - on a real (never shown) window.</summary>
public sealed class PinnedExeTests
{
    private static CaptureTargetResolver.CapturePlan Plan(IntPtr hwnd, bool wgc)
        => new(0, "", 0, 0, default, null, "x", hwnd, "", "", UseWgc: wgc);

    [Fact]
    public void A_pinned_window_names_the_exe_of_its_process()
    {
        string? exe = WpfTestSupport.OnSta(() =>
        {
            using var form = new System.Windows.Forms.Form();
            IntPtr hwnd = form.Handle;                      // creates the window; it is never shown
            return new GameContext(() => null).PinnedExe(Plan(hwnd, wgc: true));
        });

        Assert.Equal(Path.GetFileName(Environment.ProcessPath), exe, ignoreCase: true);
    }

    [Fact]
    public void Without_a_pinned_window_there_is_no_pinned_exe()
    {
        var ctx = new GameContext(() => null);
        Assert.Null(ctx.PinnedExe(null));
        Assert.Null(ctx.PinnedExe(Plan(IntPtr.Zero, wgc: true)));                 // nothing pinned
        Assert.Null(ctx.PinnedExe(Plan(new IntPtr(0x7FFFFFF0), wgc: true)));      // not a window
    }

    [Fact]
    public void A_plan_that_is_not_a_window_capture_pins_nothing()
    {
        string? exe = WpfTestSupport.OnSta(() =>
        {
            using var form = new System.Windows.Forms.Form();
            return new GameContext(() => null).PinnedExe(Plan(form.Handle, wgc: false));   // monitor capture: the folder follows the foreground
        });
        Assert.Null(exe);
    }
}
