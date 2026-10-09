using System;
using System.Collections.Generic;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

public enum SidebarSection { Capture, Stream, Apps }

public enum BoardStatusKind { Active, Paused, Recording }

/// <summary>
/// What the sidebar shows: sections, per-widget label and icon, and the live status line.
/// Pure (no WPF) so the structure is unit-tested; the sidebar window only renders it.
/// </summary>
public static class WidgetCatalog
{
    /// <summary>Sidebar order. Settings is not in a section — it sits in the sidebar footer.</summary>
    public static readonly (SidebarSection Section, WidgetId[] Widgets)[] Sections =
    {
        (SidebarSection.Capture, new[] { WidgetId.Capture, WidgetId.Audio, WidgetId.Gallery, WidgetId.Performance, WidgetId.Crosshair }),
        (SidebarSection.Stream,  new[] { WidgetId.Streaming, WidgetId.Mixer, WidgetId.Sources, WidgetId.Preflight, WidgetId.Chat }),
        (SidebarSection.Apps,    new[] { WidgetId.WhatsApp, WidgetId.Music, WidgetId.Spotify }),
    };

    /// <summary>All widgets in sidebar order, Settings last (also the stagger order for default placement).</summary>
    public static IReadOnlyList<WidgetId> Order
    {
        get
        {
            var list = new List<WidgetId>();
            foreach (var (_, widgets) in Sections) list.AddRange(widgets);
            list.Add(WidgetId.Settings);
            return list;
        }
    }

    public static string SectionTitle(SidebarSection s) => s switch
    {
        SidebarSection.Capture => L.T("Aufnahme", "Capture"),
        SidebarSection.Stream  => "Stream",
        SidebarSection.Apps    => "Apps",
        _ => s.ToString()
    };

    public static string Label(WidgetId id) => id switch
    {
        WidgetId.Capture     => L.T("Aufnahme", "Capture"),
        WidgetId.Audio       => "Audio",
        WidgetId.Gallery     => L.T("Galerie", "Gallery"),
        WidgetId.Performance => L.T("Leistung", "Performance"),
        WidgetId.Crosshair   => L.T("Fadenkreuz", "Crosshair"),
        WidgetId.Streaming   => "Streaming",
        WidgetId.Mixer       => "Mixer",
        WidgetId.Sources     => L.T("Quellen", "Sources"),
        WidgetId.Preflight   => "Go Live",
        WidgetId.Chat        => "Chat",
        WidgetId.WhatsApp    => "WhatsApp",
        // "Stream-Musik" sets the own NCS player apart from Spotify right next to it.
        WidgetId.Music       => L.T("Stream-Musik", "Stream music"),
        WidgetId.Spotify     => "Spotify",
        WidgetId.Settings    => L.T("Einstellungen", "Settings"),
        _                    => id.ToString()
    };

    /// <summary>Segoe Fluent Icons code point; null = drawn by hand (the crosshair).</summary>
    public static string? Glyph(WidgetId id) => id switch
    {
        WidgetId.Capture     => "",   // video
        WidgetId.Audio       => "",   // microphone
        WidgetId.Gallery     => "",   // photo
        WidgetId.Performance => "",   // diagnostic
        WidgetId.Crosshair   => null,
        WidgetId.Streaming   => "",   // broadcast
        WidgetId.Mixer       => "",   // sliders
        WidgetId.Sources     => "",   // screen
        WidgetId.Preflight   => "",   // lightning
        WidgetId.Chat        => "",   // message
        WidgetId.WhatsApp    => "",   // phone
        WidgetId.Music       => "",   // music note
        WidgetId.Spotify     => "",   // headphones
        WidgetId.Settings    => "",   // gear
        _                    => ""
    };

    /// <summary>The sidebar's status line: what the capture is doing right now.</summary>
    public static (string Text, BoardStatusKind Kind) Status(bool bufferRunning, int bufferSeconds,
                                                             bool recording, TimeSpan recordingFor)
    {
        if (recording)
            return (L.T($"Aufnahme läuft · {Clock(recordingFor)}", $"Recording · {Clock(recordingFor)}"), BoardStatusKind.Recording);
        return bufferRunning
            ? (L.T($"Buffer aktiv · {bufferSeconds} s", $"Buffer active · {bufferSeconds} s"), BoardStatusKind.Active)
            : (L.T("Buffer pausiert", "Buffer paused"), BoardStatusKind.Paused);
    }

    private static string Clock(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
}
