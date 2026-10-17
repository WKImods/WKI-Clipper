using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// The ledger decides which window rectangles may be written back as "the user's place".
/// Display-scaling bug: system moves (sidebar clearance, clamping, re-layout) were saved as if
/// the user had made them, so the layout came back displaced after 100 % → 175 % → 100 %.
/// </summary>
public sealed class PlacementLedgerTests
{
    private static readonly LayoutRect Placed = new(304, 72, 432, 552);
    private static readonly LayoutRect Pushed = new(532, 14, 756, 966);

    [Fact]
    public void A_widget_nobody_touched_is_never_saved()
    {
        var ledger = new PlacementLedger();
        Assert.False(ledger.ShouldSave(WidgetId.Audio, Placed));
    }

    [Fact]
    public void A_system_placement_is_not_saved()
    {
        var ledger = new PlacementLedger();
        ledger.MarkSystem(WidgetId.Audio, Placed);
        Assert.False(ledger.ShouldSave(WidgetId.Audio, Placed));
    }

    [Fact]
    public void A_different_rect_after_a_system_placement_is_still_not_saved()
    {
        // The old guard only compared for EXACT equality, so any later system move slipped through.
        var ledger = new PlacementLedger();
        ledger.MarkSystem(WidgetId.Audio, Placed);
        Assert.False(ledger.ShouldSave(WidgetId.Audio, Pushed));
    }

    [Fact]
    public void A_user_change_is_saved_whatever_the_rect()
    {
        var ledger = new PlacementLedger();
        ledger.MarkSystem(WidgetId.Audio, Placed);
        ledger.MarkUser(WidgetId.Audio);
        Assert.True(ledger.ShouldSave(WidgetId.Audio, Pushed));
        Assert.True(ledger.ShouldSave(WidgetId.Audio, new LayoutRect(0, 0, 1, 1)));
    }

    [Fact]
    public void A_user_change_without_any_system_placement_is_saved()
    {
        var ledger = new PlacementLedger();
        ledger.MarkUser(WidgetId.Audio);
        Assert.True(ledger.ShouldSave(WidgetId.Audio, Placed));
    }

    [Fact]
    public void Saved_clears_the_flag()
    {
        var ledger = new PlacementLedger();
        ledger.MarkUser(WidgetId.Audio);
        Assert.True(ledger.ShouldSave(WidgetId.Audio, Pushed));
        ledger.Saved(WidgetId.Audio);
        Assert.False(ledger.ShouldSave(WidgetId.Audio, Pushed));
    }

    [Fact]
    public void A_click_on_the_title_bar_without_moving_is_not_a_change()
    {
        // DragMove returns at once on a plain click, but DragFinished still fires.
        var ledger = new PlacementLedger();
        ledger.MarkSystem(WidgetId.Audio, Pushed);
        ledger.MarkUser(WidgetId.Audio);
        Assert.False(ledger.ShouldSave(WidgetId.Audio, Pushed));
    }

    [Fact]
    public void A_system_placement_after_the_user_mark_supersedes_it()
    {
        // The window no longer sits where the user left it, so that spot must not be saved.
        var ledger = new PlacementLedger();
        ledger.MarkUser(WidgetId.Audio);
        ledger.MarkSystem(WidgetId.Audio, Pushed);
        Assert.False(ledger.ShouldSave(WidgetId.Audio, Pushed));
        Assert.False(ledger.ShouldSave(WidgetId.Audio, Placed));
    }

    [Fact]
    public void After_a_save_an_old_system_rect_no_longer_blocks_a_new_user_change()
    {
        // The user moved the window away (saved), then the arrangement lands exactly on the
        // old system spot: that is a real change and the stored place has to follow.
        var ledger = new PlacementLedger();
        ledger.MarkSystem(WidgetId.Audio, Placed);
        ledger.MarkUser(WidgetId.Audio);
        Assert.True(ledger.ShouldSave(WidgetId.Audio, Pushed));
        ledger.Saved(WidgetId.Audio);

        ledger.MarkUser(WidgetId.Audio);
        Assert.True(ledger.ShouldSave(WidgetId.Audio, Placed));
    }

    [Fact]
    public void Widgets_are_tracked_independently()
    {
        var ledger = new PlacementLedger();
        ledger.MarkUser(WidgetId.Audio);
        ledger.MarkSystem(WidgetId.Gallery, Placed);

        Assert.True(ledger.ShouldSave(WidgetId.Audio, Pushed));
        Assert.False(ledger.ShouldSave(WidgetId.Gallery, Pushed));
        Assert.False(ledger.ShouldSave(WidgetId.Music, Pushed));

        ledger.Saved(WidgetId.Audio);
        Assert.False(ledger.ShouldSave(WidgetId.Audio, Pushed));
    }
}
