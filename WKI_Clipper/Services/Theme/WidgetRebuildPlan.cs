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
                                       IReadOnlyList<WidgetId> Rebuild, bool ReopenBoard, string? ReopenOnDevice)
{
    /// <param name="windows">Every window the host owns, in its own order.</param>
    /// <param name="boardWasOpen">Open: the board opens again. Closed: only the pinned widgets come back (as before the rebuild).</param>
    /// <param name="boardDevice">Device name of the monitor the board is on, when known.</param>
    /// <param name="screenDevices">The monitors there are now.</param>
    /// <param name="primaryDevice">Where the board goes when its monitor is gone.</param>
    public static WidgetRebuildPlan For(IEnumerable<WidgetWindowInfo> windows, bool boardWasOpen,
                                        string? boardDevice = null, IReadOnlyCollection<string>? screenDevices = null, string? primaryDevice = null)
    {
        var all = windows.ToList();

        // The board comes back where it was - not where the mouse happens to be when a colour is committed (it can be on the other monitor).
        string? reopenOn = null;
        if (boardWasOpen && boardDevice is not null)
            reopenOn = screenDevices?.Contains(boardDevice, StringComparer.OrdinalIgnoreCase) == true ? boardDevice : primaryDevice;

        return new WidgetRebuildPlan(
            // Every window saves its place first - also the ones that stay: the board is closed and reopened around them.
            CaptureGeometry: all.Select(w => w.Id).ToList(),
            Keep: all.Where(w => w.IsWeb).Select(w => w.Id).ToList(),
            Rebuild: all.Where(w => !w.IsWeb).Select(w => w.Id).ToList(),
            ReopenBoard: boardWasOpen,
            ReopenOnDevice: reopenOn);
    }
}

/// <summary>
/// When a theme change may rebuild the widgets. With the board open it is rebuilt at once. With the board closed (the picker was
/// closed by the board's hotkey, and its last choice went on while closing) the user is back in the game: tearing down and
/// rebuilding the pinned widgets over it would flicker and cost - so the rebuild is owed to the board's next opening instead.
/// </summary>
public sealed class ThemeRebuildGate
{
    private bool _owed;

    /// <summary>A theme was applied. True: rebuild now. False: remembered for the next opening.</summary>
    public bool OnThemeChanged(bool boardOpen)
    {
        _owed = !boardOpen;
        return boardOpen;
    }

    /// <summary>The board is about to open: true exactly once if a rebuild is owed.</summary>
    public bool TakePending()
    {
        bool owed = _owed;
        _owed = false;
        return owed;
    }
}
