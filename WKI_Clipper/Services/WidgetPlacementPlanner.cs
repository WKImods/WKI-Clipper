using System;
using System.Collections.Generic;
using WKI_Clipper.Models;
using WKI_Clipper.Views;

namespace WKI_Clipper.Services;

/// <summary>
/// The pure decision core behind WidgetHost's placement: where a widget goes when the board
/// shows it, which windows have to step out from under the sidebar, and — the point of it —
/// what may be written back as the user's place. The host only feeds in real window rectangles
/// and applies the results with SetWindowPos; every rule lives here so a test can run the
/// same open / move / close sequence the host does.
///
/// Persistence is opt-in (see <see cref="PlacementLedger"/>): everything this class places on
/// its own is recorded as a system placement and can never be committed; only
/// <see cref="UserChanged"/> followed by <see cref="Commit"/> writes a <see cref="WidgetState"/>.
/// </summary>
public sealed class WidgetPlacementPlanner
{
    /// <summary>Distance kept to the work-area edge, and between neighbouring widgets (DIPs).</summary>
    public const double EdgeMarginDip = 8, GapDip = 8;

    private readonly PlacementLedger _ledger = new();

    /// <summary>
    /// Physical rectangle for a widget that is about to be shown: its stored place (window
    /// centre relative to the work area) at this monitor's scaling, clamped onto the screen.
    /// <paramref name="legacy"/>: stored before the relative scheme existed — X/Y are pixels.
    /// Never placed at all: staggered by <paramref name="index"/>, right of the expanded sidebar.
    /// Whole pixels, as SetWindowPos will apply it.
    /// </summary>
    public LayoutRect Restore(WidgetState st, double widthDip, double heightDip, int index, bool legacy,
                              LayoutRect area, double scale)
    {
        double pw = widthDip * scale, ph = heightDip * scale, margin = EdgeMarginDip * scale;

        double x, y;
        if (st.RelX is double rx && st.RelY is double ry)
            (x, y) = WidgetLayout.FromRelative(rx, ry, pw, ph, area, margin);
        else if (legacy)
            // Saved before v0.13: X/Y came from WPF at the old 100 % assumption, i.e. pixels.
            (x, y) = WidgetLayout.Clamp(st.X, st.Y, pw, ph, area.X, area.Y, area.Right, area.Bottom, margin);
        else
            // Never placed: staggered default within the monitor's work area, right of the (expanded) sidebar.
            (x, y) = WidgetLayout.Clamp(area.X + (WidgetSidebarWindow.ExpandedWidth + 40 + index * 44) * scale,
                                        area.Y + (40 + index * 44) * scale,
                                        pw, ph, area.X, area.Y, area.Right, area.Bottom, margin);
        return new LayoutRect(Math.Round(x), Math.Round(y), pw, ph);
    }

    /// <summary>The app has put the window at <paramref name="actual"/> (read back after it was shown and sized).</summary>
    public void Placed(WidgetId id, LayoutRect actual) => _ledger.MarkSystem(id, actual);

    /// <summary>
    /// Windows under the sidebar move to its right edge (<see cref="WidgetLayout.ClearOf"/>). The
    /// result lists only the windows that move, in whole pixels; each is recorded as a system
    /// placement (the host re-records the real rectangle after moving), so nothing can be saved
    /// from it. The sidebar is DIP-sized, so the same layout needs this at 175 % but not at 100 %.
    /// </summary>
    public IReadOnlyList<(WidgetId Id, LayoutRect Rect)> ClearOfSidebar(
        IEnumerable<(WidgetId Id, LayoutRect Rect)> windows, LayoutRect sidebar, LayoutRect area, double scale)
    {
        var moves = new List<(WidgetId Id, LayoutRect Rect)>();
        foreach (var (id, rect) in windows)
        {
            if (!rect.Overlaps(sidebar)) continue;
            var (x, y) = WidgetLayout.ClearOf(rect, sidebar, area, GapDip * scale, EdgeMarginDip * scale);
            var moved = rect.At(Math.Round(x), Math.Round(y));
            _ledger.MarkSystem(id, moved);
            moves.Add((id, moved));
        }
        return moves;
    }

    /// <summary>
    /// Widgets that really intersect the shortcut bar move straight down below it (<see cref="BarLayout.PushBelow"/>), kept inside
    /// the work area. The result lists only windows that actually end up somewhere else, in whole pixels. UNLIKE the sidebar clearance
    /// this move is marked as the user's: the pushed place is meant to be saved, so the widget is below the bar the next time the
    /// board opens instead of being pushed again on every open - the host commits it right after moving.
    /// </summary>
    public IReadOnlyList<(WidgetId Id, LayoutRect Rect)> ClearOfBar(
        IEnumerable<(WidgetId Id, LayoutRect Rect)> windows, LayoutRect bar, LayoutRect area, double scale)
    {
        var all = new List<(WidgetId Id, LayoutRect Rect)>(windows);
        var moves = new List<(WidgetId Id, LayoutRect Rect)>();
        foreach (var (id, pushed) in BarLayout.PushBelow(bar, all, GapDip * scale))
        {
            var (x, y) = WidgetLayout.Clamp(pushed.X, pushed.Y, pushed.W, pushed.H, area.X, area.Y, area.Right, area.Bottom, EdgeMarginDip * scale);
            var moved = pushed.At(Math.Round(x), Math.Round(y));
            if (all.Exists(w => w.Id == id && w.Rect == moved)) continue;   // nowhere lower to go
            _ledger.MarkUser(id);
            moves.Add((id, moved));
        }
        return moves;
    }

    /// <summary>The user dragged or resized the window, or re-arranged it on purpose (grid / overlap switch).</summary>
    public void UserChanged(WidgetId id) => _ledger.MarkUser(id);

    /// <summary>
    /// Writes <paramref name="rect"/> (physical) and its DIP size into <paramref name="st"/> — but only
    /// if the user changed the window since the last save, and <paramref name="rect"/> is not just the
    /// spot the app put it at. Pass the rectangle the code has just applied, not one read back from a
    /// window it resized in the same breath: layout has not caught up with that yet.
    /// Returns true when the stored state actually changed.
    /// </summary>
    public bool Commit(WidgetState st, LayoutRect rect, double widthDip, double heightDip,
                       LayoutRect area, string monitorDeviceName)
    {
        if (!_ledger.ShouldSave(st.Id, rect)) return false;
        var (rx, ry) = WidgetLayout.ToRelative(rect, area);
        _ledger.Saved(st.Id);

        // Same place as already stored (e.g. a drag that ended where the last one did): no write.
        if (st.RelX is double oldX && st.RelY is double oldY
            && Math.Abs(oldX - rx) < 1e-9 && Math.Abs(oldY - ry) < 1e-9
            && Math.Abs(st.Width - widthDip) < 0.01 && Math.Abs(st.Height - heightDip) < 0.01
            && st.MonitorDeviceName == monitorDeviceName)
            return false;

        st.RelX = rx;
        st.RelY = ry;
        st.X = rect.X;
        st.Y = rect.Y;
        st.Width = widthDip;
        st.Height = heightDip;
        st.MonitorDeviceName = monitorDeviceName;
        return true;
    }

    /// <summary>
    /// Where a window that is resized in place wants to be: same top-left, new size. A resize
    /// grows from the top-left corner, so the stored CENTRE has to follow the new size — keeping
    /// the old centre would reopen the window shifted by half the growth. Built from the DIP
    /// size × scale, because the window's own rectangle may not show the new size yet.
    /// </summary>
    public static LayoutRect IntendedAfterResize(LayoutRect current, double widthDip, double heightDip, double scale)
        => new(current.X, current.Y, widthDip * scale, heightDip * scale);
}
