using System;
using System.Linq;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>v0.15 redesign: sidebar structure and status line.</summary>
public sealed class WidgetCatalogTests
{
    [Fact]
    public void Every_widget_except_settings_sits_in_exactly_one_section()
    {
        var inSections = WidgetCatalog.Sections.SelectMany(s => s.Widgets).ToList();
        foreach (var id in Enum.GetValues<WidgetId>())
        {
            int count = inSections.Count(w => w == id);
            Assert.Equal(id == WidgetId.Settings ? 0 : 1, count);
        }
    }

    [Fact]
    public void Order_is_section_order_with_settings_last()
    {
        var order = WidgetCatalog.Order;
        Assert.Equal(Enum.GetValues<WidgetId>().Length, order.Count);
        Assert.Equal(WidgetId.Capture, order[0]);
        Assert.Equal(WidgetId.Settings, order[^1]);
        Assert.True(order.ToList().IndexOf(WidgetId.Chat) < order.ToList().IndexOf(WidgetId.WhatsApp));   // Stream before Apps
    }

    [Fact]
    public void Only_the_crosshair_is_drawn_by_hand()
    {
        foreach (var id in Enum.GetValues<WidgetId>())
            Assert.Equal(id == WidgetId.Crosshair, WidgetCatalog.Glyph(id) is null);
    }

    [Fact]
    public void Status_prefers_a_running_recording()
    {
        var (text, kind) = WidgetCatalog.Status(bufferRunning: true, 60, recording: true, TimeSpan.FromSeconds(83));
        Assert.Equal(BoardStatusKind.Recording, kind);
        Assert.EndsWith("01:23", text);
    }

    [Theory]
    [InlineData(true, BoardStatusKind.Active)]
    [InlineData(false, BoardStatusKind.Paused)]
    public void Status_reflects_the_buffer(bool running, BoardStatusKind expected)
        => Assert.Equal(expected, WidgetCatalog.Status(running, 60, false, TimeSpan.Zero).Kind);
}
