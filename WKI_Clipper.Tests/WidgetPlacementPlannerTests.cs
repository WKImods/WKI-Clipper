using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// WidgetPlacementPlanner is the decision core WidgetHost runs when the board opens (restore a
/// place, move clear of the sidebar), when the user changes something, and when the board closes
/// (what may be saved). These tests drive it through the same calls in the same order.
/// </summary>
public sealed class WidgetPlacementPlannerTests
{
    private static readonly LayoutRect Area100 = new(0, 0, 3440, 1392);
    private static readonly LayoutRect Area175 = new(0, 0, 3440, 1356);
    private const string Monitor = @"\\.\DISPLAY1";

    // The reported board at 100 %: physical pixels, sizes in DIPs are the same numbers.
    private static readonly (WidgetId Id, LayoutRect Rect)[] RealLayout =
    {
        (WidgetId.Audio,       new LayoutRect( 304,   72, 432, 552)),
        (WidgetId.Gallery,     new LayoutRect( 744,   72, 872, 552)),
        (WidgetId.Performance, new LayoutRect( 304,  632, 432, 424)),
        (WidgetId.Spotify,     new LayoutRect( 304, 1176, 416, 200)),
        (WidgetId.Crosshair,   new LayoutRect( 744,  632, 360, 744)),
        (WidgetId.Music,       new LayoutRect(1112,  632, 504, 408)),
        (WidgetId.Capture,     new LayoutRect(1624,   72, 472, 552)),
        (WidgetId.WhatsApp,    new LayoutRect(2104,   72, 952, 824)),
    };

    /// <summary>
    /// The host's board, minus the windows: opening = ShowWidget for every widget (restore, record
    /// the placement) then KeepWidgetsClearOfSidebar; closing = the save loop of CloseBoard.
    /// </summary>
    private sealed class Board
    {
        public readonly WidgetPlacementPlanner Planner = new();
        public readonly List<WidgetState> States = new();
        public readonly Dictionary<WidgetId, LayoutRect> Rects = new();
        public int Saves;
        private LayoutRect _area;
        private double _scale = 1;

        public Board()
        {
            foreach (var (id, r) in RealLayout)
            {
                var (rx, ry) = WidgetLayout.ToRelative(r, Area100);
                States.Add(new WidgetState { Id = id, Width = r.W, Height = r.H, RelX = rx, RelY = ry, MonitorDeviceName = Monitor });
            }
        }

        public WidgetState State(WidgetId id) => States.Single(s => s.Id == id);

        public List<(WidgetId Id, double? RelX, double? RelY, double W, double H)> Snapshot()
            => States.Select(s => (s.Id, s.RelX, s.RelY, s.Width, s.Height)).ToList();

        public void Open(LayoutRect area, double scale)
        {
            _area = area;
            _scale = scale;
            Rects.Clear();
            for (int i = 0; i < States.Count; i++)
            {
                var st = States[i];
                var r = Planner.Restore(st, st.Width, st.Height, i, legacy: false, area, scale);
                Rects[st.Id] = r;
                Planner.Placed(st.Id, r);
            }
            var sidebar = WidgetLayout.SidebarRect(area, scale, collapsed: false);
            var windows = Rects.Select(kv => (kv.Key, kv.Value)).ToList();
            foreach (var (id, r) in Planner.ClearOfSidebar(windows, sidebar, area, scale))
                Rects[id] = r;
        }

        public void Close()
        {
            foreach (var st in States) Commit(st, Rects[st.Id]);
        }

        public void UserDrag(WidgetId id, double dx, double dy)
        {
            var moved = Rects[id].At(Rects[id].X + dx, Rects[id].Y + dy);
            Rects[id] = moved;
            Planner.UserChanged(id);
            Commit(State(id), moved);
        }

        private void Commit(WidgetState st, LayoutRect r)
        {
            if (Planner.Commit(st, r, r.W / _scale, r.H / _scale, _area, Monitor)) Saves++;
        }
    }

    // ---- 100 % -> 175 % -> 100 % through the planner ----

    [Fact]
    public void Opening_and_closing_at_100_then_175_then_100_percent_saves_nothing_and_restores_every_rect()
    {
        var board = new Board();
        var before = board.Snapshot();

        board.Open(Area100, 1.0);
        var first = board.Rects.ToDictionary(kv => kv.Key, kv => kv.Value);
        board.Close();

        board.Open(Area175, 1.75);
        board.Close();

        board.Open(Area100, 1.0);
        board.Close();

        foreach (var (id, original) in RealLayout)
        {
            Assert.Equal(original, first[id]);
            Assert.Equal(original, board.Rects[id]);
        }
        Assert.Equal(0, board.Saves);
        Assert.Equal(before, board.Snapshot());
    }

    [Fact]
    public void The_sidebar_clearance_at_175_percent_really_moves_widgets_and_still_saves_nothing()
    {
        var board = new Board();
        board.Open(Area175, 1.75);

        // The four that end up under the sidebar were pushed...
        var pushed = new[] { WidgetId.Audio, WidgetId.Gallery, WidgetId.Performance, WidgetId.Spotify };
        var sidebar = WidgetLayout.SidebarRect(Area175, 1.75, collapsed: false);
        foreach (var id in pushed) Assert.False(board.Rects[id].Overlaps(sidebar), $"{id} is still under the sidebar");
        Assert.Equal(532, board.Rects[WidgetId.Audio].X);

        // ...and neither that nor closing the board saved anything.
        board.Close();
        Assert.Equal(0, board.Saves);
    }

    [Fact]
    public void A_save_attempt_right_after_the_sidebar_push_is_refused()
    {
        // The old KeepWidgetsClearOfSidebar saved right here.
        var board = new Board();
        board.Open(Area175, 1.75);
        var audio = board.Rects[WidgetId.Audio];

        Assert.False(board.Planner.Commit(board.State(WidgetId.Audio), audio, audio.W / 1.75, audio.H / 1.75, Area175, Monitor));
    }

    [Fact]
    public void One_user_drag_at_175_percent_is_exactly_one_save()
    {
        var board = new Board();
        var before = board.Snapshot();
        board.Open(Area175, 1.75);

        board.UserDrag(WidgetId.Crosshair, 100, 50);
        board.Close();

        Assert.Equal(1, board.Saves);
        var after = board.Snapshot();
        foreach (var (id, _) in RealLayout.Where(l => l.Id != WidgetId.Crosshair))
            Assert.Equal(before.Single(s => s.Id == id), after.Single(s => s.Id == id));

        // The dragged one holds the drop spot, as the user chose it at this scaling.
        var (rx, ry) = WidgetLayout.ToRelative(board.Rects[WidgetId.Crosshair], Area175);
        Assert.Equal(rx, board.State(WidgetId.Crosshair).RelX!.Value, 9);
        Assert.Equal(ry, board.State(WidgetId.Crosshair).RelY!.Value, 9);
    }

    [Fact]
    public void Dragging_a_widget_that_was_pushed_aside_saves_only_that_one()
    {
        var board = new Board();
        var before = board.Snapshot();
        board.Open(Area175, 1.75);

        board.UserDrag(WidgetId.Audio, 60, 40);
        board.Close();
        board.Open(Area100, 1.0);
        board.Close();

        Assert.Equal(1, board.Saves);
        foreach (var (id, original) in RealLayout.Where(l => l.Id != WidgetId.Audio))
        {
            Assert.Equal(before.Single(s => s.Id == id), board.Snapshot().Single(s => s.Id == id));
            Assert.Equal(original, board.Rects[id]);
        }
    }

    [Fact]
    public void A_second_save_of_the_same_place_is_not_a_save()
    {
        var board = new Board();
        board.Open(Area175, 1.75);
        board.UserDrag(WidgetId.Crosshair, 100, 50);

        // A drag that ends where the last one did (or a click that only looks like one).
        board.Planner.UserChanged(WidgetId.Crosshair);
        board.Close();

        Assert.Equal(1, board.Saves);
    }

    // ---- saving an explicitly given rectangle (no read-back of a window the code just resized) ----

    [Fact]
    public void A_size_only_change_is_saved_when_the_new_rect_is_passed_explicitly()
    {
        // Grid turned on: the arrangement snaps 432 px to 440 px without moving the window.
        var planner = new WidgetPlacementPlanner();
        var st = new WidgetState { Id = WidgetId.Audio, Width = 432, Height = 552, RelX = 0.15, RelY = 0.25, MonitorDeviceName = Monitor };
        var stale = new LayoutRect(304, 72, 432, 552);
        var snapped = new LayoutRect(304, 72, 440, 552);
        planner.Placed(WidgetId.Audio, stale);
        planner.UserChanged(WidgetId.Audio);

        // Reading the window back right after setting Width still gives the old rect: refused...
        Assert.False(planner.Commit(st, stale, 432, 552, Area100, Monitor));
        // ...the arranged rect is what has to be saved, and the pending user change is still there.
        Assert.True(planner.Commit(st, snapped, 440, 552, Area100, Monitor));
        Assert.Equal(440, st.Width);
        var (rx, ry) = WidgetLayout.ToRelative(snapped, Area100);
        Assert.Equal(rx, st.RelX!.Value, 9);
        Assert.Equal(ry, st.RelY!.Value, 9);
        Assert.Equal((304.0, 72.0), (st.X, st.Y));
    }

    [Fact]
    public void Commit_does_nothing_for_a_widget_the_user_did_not_change()
    {
        var planner = new WidgetPlacementPlanner();
        var st = new WidgetState { Id = WidgetId.Audio, Width = 432, Height = 552, RelX = 0.15, RelY = 0.25 };
        planner.Placed(WidgetId.Audio, new LayoutRect(304, 72, 432, 552));

        Assert.False(planner.Commit(st, new LayoutRect(999, 99, 440, 552), 440, 552, Area100, Monitor));
        Assert.Equal((0.15, 0.25, 432.0), (st.RelX, st.RelY, st.Width));
    }

    // ---- Spotify compact <-> full: the window grows from its top-left ----

    [Fact]
    public void Spotify_full_view_reopens_at_the_spot_the_window_grew_to()
    {
        // Compact 416x200 at (2000,100) grows to 1474x873 from its top-left corner and is clamped.
        var current = new LayoutRect(2000, 100, 416, 200);
        var intended = WidgetPlacementPlanner.IntendedAfterResize(current, 1474, 873, 1.0);
        Assert.Equal(new LayoutRect(2000, 100, 1474, 873), intended);

        var (x, y) = WidgetLayout.Clamp(intended.X, intended.Y, intended.W, intended.H, Area100.X, Area100.Y, Area100.Right, Area100.Bottom, 8);
        Assert.Equal((1958.0, 100.0), (x, y));   // where the host leaves the window now

        // What is stored from the intended rect restores to exactly that spot.
        var (rx, ry) = WidgetLayout.ToRelative(intended, Area100);
        var st = new WidgetState { Id = WidgetId.Spotify, Width = 1474, Height = 873, RelX = rx, RelY = ry };
        var reopened = new WidgetPlacementPlanner().Restore(st, 1474, 873, 0, legacy: false, Area100, 1.0);
        Assert.Equal(new LayoutRect(1958, 100, 1474, 873), reopened);
    }

    [Fact]
    public void Storing_the_old_centre_for_the_new_size_was_the_bug()
    {
        var compact = new LayoutRect(2000, 100, 416, 200);
        var (rx, ry) = WidgetLayout.ToRelative(compact, Area100);   // centre of the OLD size
        var st = new WidgetState { Id = WidgetId.Spotify, Width = 1474, Height = 873, RelX = rx, RelY = ry };

        var reopened = new WidgetPlacementPlanner().Restore(st, 1474, 873, 0, legacy: false, Area100, 1.0);

        Assert.Equal(new LayoutRect(1471, 8, 1474, 873), reopened);   // ~490 px left, ~90 px up
    }

    [Fact]
    public void The_spotify_switch_round_trips_at_175_percent_too()
    {
        var current = new LayoutRect(2000, 100, 416 * 1.75, 200 * 1.75);
        var intended = WidgetPlacementPlanner.IntendedAfterResize(current, 1474, 873, 1.75);
        Assert.Equal(new LayoutRect(2000, 100, 1474 * 1.75, 873 * 1.75), intended);

        var (x, y) = WidgetLayout.Clamp(intended.X, intended.Y, intended.W, intended.H, Area175.X, Area175.Y, Area175.Right, Area175.Bottom, 8 * 1.75);
        var (rx, ry) = WidgetLayout.ToRelative(intended, Area175);
        var st = new WidgetState { Id = WidgetId.Spotify, Width = 1474, Height = 873, RelX = rx, RelY = ry };
        var reopened = new WidgetPlacementPlanner().Restore(st, 1474, 873, 0, legacy: false, Area175, 1.75);

        Assert.Equal((Math.Round(x), Math.Round(y)), (reopened.X, reopened.Y));
    }

    // ---- restoring a place ----

    [Fact]
    public void A_widget_that_was_never_placed_starts_staggered_right_of_the_expanded_sidebar()
    {
        var st = new WidgetState { Id = WidgetId.Capture, Width = 360, Height = 430 };
        var r = new WidgetPlacementPlanner().Restore(st, 360, 430, index: 2, legacy: false, Area100, 1.0);
        Assert.Equal(new LayoutRect(280 + 40 + 2 * 44, 40 + 2 * 44, 360, 430), r);
    }

    [Fact]
    public void A_place_saved_before_the_relative_scheme_uses_its_pixels_once()
    {
        var st = new WidgetState { Id = WidgetId.Capture, Width = 400, Height = 300, X = 500, Y = 300 };
        var r = new WidgetPlacementPlanner().Restore(st, 400, 300, index: 0, legacy: true, Area100, 1.0);
        Assert.Equal(new LayoutRect(500, 300, 400, 300), r);
    }
}
