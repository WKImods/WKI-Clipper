using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>A widget window the host currently owns, and whether its content is a web page (WhatsApp, Spotify).</summary>
public readonly record struct WidgetWindowInfo(WidgetId Id, bool IsWeb);

/// <summary>
/// What the widget host does when the whole UI has to be built anew (a language or a colour change): which windows save where they
/// are, which are torn down and rebuilt, which stay. Web widgets stay: rebuilding would throw their page away - an open WhatsApp
/// chat, a half-typed message - so they only get new texts and, for a theme, a new icon colour (their chrome recolours itself).
/// </summary>
public sealed record WidgetRebuildPlan(IReadOnlyList<WidgetId> CaptureGeometry, IReadOnlyList<WidgetId> Keep,
                                       IReadOnlyList<WidgetId> Rebuild, bool ReopenBoard)
{
    /// <param name="windows">Every window the host owns, in its own order.</param>
    /// <param name="boardWasOpen">Open: the board opens again. Closed: only the pinned widgets come back (as before the rebuild).</param>
    public static WidgetRebuildPlan For(IEnumerable<WidgetWindowInfo> windows, bool boardWasOpen)
    {
        var all = windows.ToList();
        return new WidgetRebuildPlan(
            // Every window saves its place first - also the ones that stay: the board is closed and reopened around them.
            CaptureGeometry: all.Select(w => w.Id).ToList(),
            Keep: all.Where(w => w.IsWeb).Select(w => w.Id).ToList(),
            Rebuild: all.Where(w => !w.IsWeb).Select(w => w.Id).ToList(),
            ReopenBoard: boardWasOpen);
    }
}
