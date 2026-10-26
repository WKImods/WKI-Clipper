using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using WKI_Clipper.Models;
using WKI_Clipper.Native;
using WKI_Clipper.Services;
using WKI_Clipper.Views;
using Xunit;
using Shape = System.Windows.Shapes.Shape;
using Size = System.Windows.Size;

namespace WKI_Clipper.Tests;

/// <summary>
/// Dragging the performance overlay while the board is open: the window's drag mode (never shown, only constructed and
/// given a handle), what a drop writes into the settings, and the "Position" list of the settings widget.
/// </summary>
[Collection("WpfApplication")]   // only one System.Windows.Application per process
public sealed class PerfOverlayDragTests : IDisposable
{
    private static readonly MonitorBox Main = new(@"\\.\DISPLAY1", new LayoutRect(0, 0, 2560, 1440), Primary: true);
    private static readonly MonitorBox Side = new(@"\\.\DISPLAY2", new LayoutRect(2560, 0, 1920, 1080), Primary: false);
    private static readonly MonitorBox[] Both = { Main, Side };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_overlay_drag_" + Guid.NewGuid().ToString("N"));

    public PerfOverlayDragTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static T OnSta<T>(Func<T> body) => WpfTestSupport.OnSta(() => { WpfTestSupport.EnsureTheme(); return body(); });

    private static int ExStyleOf(Window w) => User32.GetWindowLong(new WindowInteropHelper(w).EnsureHandle(), User32.GWL_EXSTYLE);

    // ---- drag mode: only while the board is open ----

    [Fact]
    public void Drag_mode_only_while_board_open()
    {
        // the one rule the host applies when the board opens, closes and on every refresh
        Assert.True(PerfOverlayPlacement.DragAllowed(boardOpen: true));
        Assert.False(PerfOverlayPlacement.DragAllowed(boardOpen: false));

        var r = OnSta(() =>
        {
            var w = new PerfOverlayWindow();
            try
            {
                var fresh = (w.DragMode, w.IsHitTestVisible, w.DragOutlineShown);
                w.SetDragMode(PerfOverlayPlacement.DragAllowed(boardOpen: true));
                var open = (w.DragMode, w.IsHitTestVisible, w.DragOutlineShown);
                w.SetDragMode(PerfOverlayPlacement.DragAllowed(boardOpen: false));
                var closed = (w.DragMode, w.IsHitTestVisible, w.DragOutlineShown);
                return (fresh, open, closed);
            }
            finally { w.Close(); }
        });

        Assert.Equal((false, false, false), r.fresh);     // a new overlay can never be grabbed
        Assert.Equal((true, true, true), r.open);         // board open: grabbable, dashed outline
        Assert.Equal((false, false, false), r.closed);    // board closed: click-through again, outline gone
    }

    [Fact]
    public void The_real_window_is_click_through_except_in_drag_mode()
    {
        var ex = OnSta(() =>
        {
            var w = new PerfOverlayWindow();
            try
            {
                int initial = ExStyleOf(w);            // handle only - the window is never shown
                w.SetDragMode(true);
                int dragging = ExStyleOf(w);
                w.SetDragMode(false);
                int after = ExStyleOf(w);
                return (initial, dragging, after);
            }
            finally { w.Close(); }
        });

        Assert.NotEqual(0, ex.initial & User32.WS_EX_TRANSPARENT);
        Assert.Equal(0, ex.dragging & User32.WS_EX_TRANSPARENT);        // grabbable
        Assert.NotEqual(0, ex.after & User32.WS_EX_TRANSPARENT);        // and click-through again
        // never takes focus, never in Alt-Tab - in drag mode too
        foreach (int style in new[] { ex.initial, ex.dragging, ex.after })
        {
            Assert.NotEqual(0, style & User32.WS_EX_NOACTIVATE);
            Assert.NotEqual(0, style & User32.WS_EX_TOOLWINDOW);
        }
    }

    [Fact]
    public void Drag_mode_set_before_the_window_has_a_handle_is_applied_when_it_gets_one()
    {
        int ex = OnSta(() =>
        {
            var w = new PerfOverlayWindow();
            try
            {
                w.SetDragMode(true);                  // the board was open before the overlay first appeared
                return ExStyleOf(w);
            }
            finally { w.Close(); }
        });
        Assert.Equal(0, ex & User32.WS_EX_TRANSPARENT);
    }

    [Fact]
    public void A_drop_is_reported_only_in_drag_mode()
    {
        var dropped = new List<LayoutRect>();
        OnSta(() =>
        {
            var w = new PerfOverlayWindow();
            try
            {
                w.Dropped += dropped.Add;
                w.ReportDrop(new LayoutRect(1, 2, 3, 4));       // a stray event with the board closed
                w.SetDragMode(true);
                w.ReportDrop(new LayoutRect(10, 20, 30, 40));
                w.SetDragMode(false);
                w.ReportDrop(new LayoutRect(5, 6, 7, 8));
                return 0;
            }
            finally { w.Close(); }
        });
        Assert.Equal(new[] { new LayoutRect(10, 20, 30, 40) }, dropped);
    }

    [Fact]
    public void The_drag_outline_is_dashed_and_follows_the_accent_resource()
    {
        var r = OnSta(() =>
        {
            var w = new PerfOverlayWindow();
            try
            {
                Shape outline = w.DragOutline;
                var source = System.Windows.DependencyPropertyHelper.GetValueSource(outline, Shape.StrokeProperty);
                return (dashed: outline.StrokeDashArray.Count > 0, dynamicStroke: source.IsExpression);
            }
            finally { w.Close(); }
        });
        Assert.True(r.dashed);
        Assert.True(r.dynamicStroke);       // a DynamicResource: a theme switch recolours it
    }

    // ---- what a drop writes ----

    [Fact]
    public void Drop_saves_anchor_or_free()
    {
        var service = NewService();
        var s = service.Current.PerfOverlay;

        // in the open: a free place on the monitor it landed on
        PerfOverlayPlacement.ApplyDrop(s, new LayoutRect(2560 + 300, 500, 200, 100), Both, snap: 24, margin: 10);
        service.Save();
        var free = new SettingsService(_dir).Load().PerfOverlay;
        Assert.Equal(OverlayAnchor.Free, free.Anchor);
        Assert.NotNull(free.Free);
        Assert.Equal(Side.Device, free.Free!.Monitor);
        Assert.Equal(0, free.Free.HEdge);
        Assert.Equal(300.0 / 1920, free.Free.RelDX, 6);

        // next to a corner of the primary monitor: that anchor, and the old free place is forgotten
        PerfOverlayPlacement.ApplyDrop(s, new LayoutRect(2350 + 5, 10 + 8, 200, 100), Both, snap: 24, margin: 10);
        service.Save();
        var snapped = new SettingsService(_dir).Load().PerfOverlay;
        Assert.Equal(OverlayAnchor.TopRight, snapped.Anchor);
        Assert.Null(snapped.Free);
    }

    [Fact]
    public void A_drop_that_lands_on_no_monitor_at_all_changes_nothing()
    {
        var s = new PerfOverlaySettings { Anchor = OverlayAnchor.BottomLeft };
        PerfOverlayPlacement.ApplyDrop(s, new LayoutRect(0, 0, 200, 100), Array.Empty<MonitorBox>(), 24, 10);
        Assert.Equal(OverlayAnchor.BottomLeft, s.Anchor);
        Assert.Null(s.Free);
    }

    [Theory]
    [InlineData(OverlayAnchor.TopLeft)]
    [InlineData(OverlayAnchor.TopCenter)]
    [InlineData(OverlayAnchor.BottomRight)]
    public void Selecting_a_fixed_anchor_clears_the_free_place(OverlayAnchor anchor)
    {
        var s = new PerfOverlaySettings
        {
            Anchor = OverlayAnchor.Free,
            Free = new OverlayFreePlace { Monitor = Main.Device, HEdge = 2, VEdge = 2, RelDX = 0.1, RelDY = 0.1 }
        };

        PerfOverlayPlacement.SelectAnchor(s, anchor);

        Assert.Equal(anchor, s.Anchor);
        Assert.Null(s.Free);
    }

    [Fact]
    public void Selecting_free_keeps_a_stored_place()
    {
        var place = new OverlayFreePlace { Monitor = Main.Device, HEdge = 1, VEdge = 1 };
        var s = new PerfOverlaySettings { Anchor = OverlayAnchor.Free, Free = place };
        PerfOverlayPlacement.SelectAnchor(s, OverlayAnchor.Free);
        Assert.Same(place, s.Free);
        Assert.Equal(OverlayAnchor.Free, s.Anchor);
    }

    // ---- the "Position" list of the settings widget ----

    private static readonly OverlayAnchor[] NineInOrder =
    {
        OverlayAnchor.TopLeft, OverlayAnchor.TopCenter, OverlayAnchor.TopRight,
        OverlayAnchor.MiddleLeft, OverlayAnchor.MiddleRight,
        OverlayAnchor.BottomLeft, OverlayAnchor.BottomCenter, OverlayAnchor.BottomRight,
        OverlayAnchor.Free,
    };

    [Fact]
    public void Position_selector_lists_nine_entries()
    {
        var r = OnSta(() =>
        {
            var selector = new PerformanceView.PositionSelector(new PerfOverlaySettings { Anchor = OverlayAnchor.BottomCenter }, () => { });
            var items = selector.Combo.Items.Cast<ComboBoxItem>().ToList();
            // lay it out like the widget does - the row must render with the app theme
            selector.Root.Measure(new Size(400, 400));
            selector.Root.Arrange(new Rect(selector.Root.DesiredSize));
            return (tags: items.Select(i => (OverlayAnchor)i.Tag).ToArray(),
                    texts: items.Select(i => (string)i.Content).ToArray(),
                    selected: (OverlayAnchor)((ComboBoxItem)selector.Combo.SelectedItem).Tag,
                    hint: selector.Hint.Text,
                    drawn: selector.Root.DesiredSize.Height > 0);
        });

        Assert.Equal(NineInOrder, r.tags);
        Assert.Equal(9, r.texts.Distinct().Count());
        Assert.All(r.texts, t => Assert.False(string.IsNullOrWhiteSpace(t)));
        Assert.Equal(OverlayAnchor.BottomCenter, r.selected);
        Assert.True(r.drawn);
        Assert.True(r.hint.Contains("Frei") || r.hint.Contains("Free"), r.hint);   // "Free: open the board and drag the overlay"
    }

    [Fact]
    public void Selecting_fixed_anchor_clears_free_place()
    {
        int applied = 0;
        var cfg = new PerfOverlaySettings
        {
            Anchor = OverlayAnchor.Free,
            Free = new OverlayFreePlace { Monitor = Main.Device, HEdge = 0, VEdge = 0, RelDX = 0.3, RelDY = 0.3 }
        };

        var r = OnSta(() =>
        {
            var selector = new PerformanceView.PositionSelector(cfg, () => applied++);
            var before = (OverlayAnchor)((ComboBoxItem)selector.Combo.SelectedItem).Tag;
            selector.Combo.SelectedIndex = Array.IndexOf(NineInOrder, OverlayAnchor.MiddleRight);
            return before;
        });

        Assert.Equal(OverlayAnchor.Free, r);                 // started on the stored free place
        Assert.Equal(OverlayAnchor.MiddleRight, cfg.Anchor);
        Assert.Null(cfg.Free);
        Assert.Equal(1, applied);                            // applied once, for that one choice
    }

    [Fact]
    public void Selecting_free_from_the_list_applies_without_inventing_a_place()
    {
        int applied = 0;
        var cfg = new PerfOverlaySettings { Anchor = OverlayAnchor.TopLeft };
        OnSta(() =>
        {
            var selector = new PerformanceView.PositionSelector(cfg, () => applied++);
            selector.Combo.SelectedIndex = Array.IndexOf(NineInOrder, OverlayAnchor.Free);
            return 0;
        });

        // the host seats a "Free" without a place where the overlay is right now; the list only says "Free"
        Assert.Equal(OverlayAnchor.Free, cfg.Anchor);
        Assert.Null(cfg.Free);
        Assert.Equal(1, applied);
    }

    [Fact]
    public void The_list_follows_a_drop_without_applying_it_again()
    {
        int applied = 0;
        var cfg = new PerfOverlaySettings { Anchor = OverlayAnchor.TopRight };
        var r = OnSta(() =>
        {
            var selector = new PerformanceView.PositionSelector(cfg, () => applied++);
            cfg.Anchor = OverlayAnchor.Free;                  // the overlay was dragged into the open
            selector.Sync();
            return (OverlayAnchor)((ComboBoxItem)selector.Combo.SelectedItem).Tag;
        });
        Assert.Equal(OverlayAnchor.Free, r);
        Assert.Equal(0, applied);
    }

    private SettingsService NewService()
    {
        var s = new SettingsService(_dir);
        s.Current.SchemaVersion = SettingsService.CurrentSchemaVersion;
        s.Current.Output.ClipsFolder = Path.Combine(_dir, "clips");
        s.Current.Output.ScreenshotsFolder = Path.Combine(_dir, "shots");
        s.Current.Output.BufferFolder = Path.Combine(_dir, "buffer");
        return s;
    }
}
