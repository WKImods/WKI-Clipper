using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WKI_Clipper.Models;

/// <summary>Which block of the shortcut bar an entry sits in.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShortcutGroup { Game, Launcher, Browser }

/// <summary>How an entry is started (the start path, see the shortcut bar spec section 4.2).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShortcutKind { CatalogLauncher, StoreApp, Browser, DefaultBrowser, SteamApp, EpicApp, ShellLink, Exe }

/// <summary>One item of the shortcut bar. The list order of <see cref="ShortcutSettings.Items"/> is the display order.</summary>
public sealed class ShortcutEntry
{
    public string Id { get; set; } = "";
    public ShortcutGroup Group { get; set; }
    public ShortcutKind Kind { get; set; }

    /// <summary>exe/lnk/url path, Steam appid, Epic AppName, AUMID, or catalog/browser id - depending on <see cref="Kind"/>.</summary>
    public string Target { get; set; } = "";

    public string? Arguments { get; set; }
    public string DisplayName { get; set; } = "";

    /// <summary>File (exe/lnk/url) or "shell:AppsFolder\AUMID" the icon is read from. null = <see cref="Target"/>.</summary>
    public string? IconSource { get; set; }

    /// <summary>Hidden from the bar by the user; the entry stays in the list.</summary>
    public bool Hidden { get; set; }

    /// <summary>The program was not found by the last scan: greyed out until "Neu suchen" removes it.</summary>
    public bool Missing { get; set; }

    /// <summary>Optional global hotkey. null / Key==0 = none.</summary>
    public HotkeyBinding? Hotkey { get; set; }
}

/// <summary>A game the clipper saw in front, remembered as a suggestion for the edit dialog.</summary>
public sealed class SeenGame
{
    public string ExePath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// The shortcut bar at the top of the board. Every field has an initializer, so a settings file
/// written before the section existed simply loads the defaults.
/// </summary>
public sealed class ShortcutSettings
{
    /// <summary>true = the groups are shown; false = only the game chip.</summary>
    public bool Expanded { get; set; } = true;

    public List<ShortcutEntry> Items { get; set; } = new();

    /// <summary>Id of the browser the Windows default is replaced by. null = the Windows default browser.</summary>
    public string? DefaultBrowserId { get; set; }

    /// <summary>Newest first, max 30, no duplicates.</summary>
    public List<SeenGame> RecentGames { get; set; } = new();
}
