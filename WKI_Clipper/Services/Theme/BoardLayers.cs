using System.Collections.Generic;

namespace WKI_Clipper.Services;

/// <summary>The windows of the board that have to stay above the performance overlay while the board is open.</summary>
public enum BoardLayer { Sidebar, Bar, Picker }

/// <summary>
/// The stacking of the board's windows around the performance overlay. While the board is open the overlay is grabbable and topmost -
/// wherever it is anchored it must not take the clicks meant for what lies under it: an anchor at the top centre sits where the shortcut
/// bar is, one at the bottom left where the sidebar's footer chips (Raster, Ueberlappen, Farben, Einstellungen) are. So those windows are
/// raised above it; the overlay stays grabbable wherever it sticks out from under them.
/// </summary>
public static class BoardZOrder
{
    /// <summary>The windows to raise after the overlay, bottom to top - only those that are on screen. The colour picker is the topmost of the board.</summary>
    public static IReadOnlyList<BoardLayer> RaisedAboveOverlay(bool sidebarShown, bool barShown, bool pickerShown)
    {
        var order = new List<BoardLayer>(3);
        if (sidebarShown) order.Add(BoardLayer.Sidebar);
        if (barShown) order.Add(BoardLayer.Bar);
        if (pickerShown) order.Add(BoardLayer.Picker);
        return order;
    }
}

/// <summary>What Esc closes.</summary>
public enum EscapeTarget { ColourPicker, Board }

/// <summary>
/// Esc closes the topmost thing of the board first: the colour picker while it is open, the board itself only when nothing sits on it.
/// (A colour switch rebuilds the board and gives the keyboard to its backdrop, so Esc often does not reach the picker itself.)
/// </summary>
public static class BoardEscape
{
    public static EscapeTarget Target(bool pickerOpen) => pickerOpen ? EscapeTarget.ColourPicker : EscapeTarget.Board;
}
