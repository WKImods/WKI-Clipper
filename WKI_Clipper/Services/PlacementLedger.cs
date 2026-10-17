using System.Collections.Generic;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>
/// Remembers, per widget, whether its current window rectangle may be written back as the
/// user's chosen place. Persistence is opt-in: only a drag, a resize or a deliberate
/// re-arrangement (<see cref="MarkUser"/>) makes a rectangle saveable. Everything the app does
/// on its own — restoring a place, clamping it onto a smaller work area, moving a window out
/// from under the sidebar, re-laying out after a display change — is a system placement and is
/// never saved: at another display scaling it would come back displaced, because the stored
/// place is relative to the work area and only the app's emergency spot was written.
/// </summary>
public sealed class PlacementLedger
{
    private readonly Dictionary<WidgetId, LayoutRect> _system = new();
    private readonly HashSet<WidgetId> _userChanged = new();

    /// <summary>
    /// The app put the window at <paramref name="rect"/>. A user change that was still waiting
    /// to be saved is superseded: the window no longer sits where the user left it.
    /// </summary>
    public void MarkSystem(WidgetId id, LayoutRect rect)
    {
        _system[id] = rect;
        _userChanged.Remove(id);
    }

    /// <summary>The user dragged or resized the window, or re-arranged it on purpose (grid / overlap switch).</summary>
    public void MarkUser(WidgetId id) => _userChanged.Add(id);

    /// <summary>
    /// True when <paramref name="rect"/> is the user's to keep: marked since the last save and not
    /// merely the spot the app put the window at (a click on the title bar runs through the same
    /// path as a drag, without moving anything).
    /// </summary>
    public bool ShouldSave(WidgetId id, LayoutRect rect)
        => _userChanged.Contains(id)
           && !(_system.TryGetValue(id, out var placed) && placed == rect);

    /// <summary>The state was written. The old system spot is forgotten too: it says nothing about later moves.</summary>
    public void Saved(WidgetId id)
    {
        _userChanged.Remove(id);
        _system.Remove(id);
    }
}
