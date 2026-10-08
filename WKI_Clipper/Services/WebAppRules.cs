using System;
using System.Text.RegularExpressions;

namespace WKI_Clipper.Services;

public enum WebApp { WhatsApp, Spotify }

/// <summary>What the embedded browser does with a navigation or a new-window request.</summary>
public enum WebNavDecision
{
    /// <summary>Stay inside the widget.</summary>
    Allow,
    /// <summary>Hand the link to the user's default browser.</summary>
    OpenExternal,
    /// <summary>Drop it (non-web scheme, e.g. javascript:, file:, spotify:).</summary>
    Block
}

/// <summary>
/// Pure rules for the embedded web apps: which pages may load inside a widget, and how
/// to read the page title. Kept free of WebView2 types so it can be unit-tested.
///
/// Links in chats come from strangers, so the widget only ever shows the app's own
/// pages over https (plus, for Spotify, the sign-in pages of the login providers it
/// offers). Everything else goes to the default browser, and only if it is http/https.
/// </summary>
public static class WebAppRules
{
    public static string HomeUrl(WebApp app) => app switch
    {
        WebApp.WhatsApp => "https://web.whatsapp.com/",
        WebApp.Spotify  => "https://open.spotify.com/",
        _ => "about:blank"
    };

    /// <summary>WebView2 profile name — separate cookie jars, one shared browser process.</summary>
    public static string ProfileName(WebApp app) => app switch
    {
        WebApp.WhatsApp => "whatsapp",
        WebApp.Spotify  => "spotify",
        _ => "default"
    };

    /// <summary>Decision for a top-level navigation or a popup request.</summary>
    public static WebNavDecision Decide(WebApp app, string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return WebNavDecision.Block;

        // The blank page WebView2 uses while it sets up a popup.
        if (u.Scheme == "about" && u.AbsoluteUri == "about:blank") return WebNavDecision.Allow;

        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return WebNavDecision.Block;

        if (u.Scheme == Uri.UriSchemeHttps && IsInApp(app, u.IdnHost)) return WebNavDecision.Allow;
        return WebNavDecision.OpenExternal;
    }

    private static bool IsInApp(WebApp app, string host) => app switch
    {
        WebApp.WhatsApp => host.Equals("web.whatsapp.com", StringComparison.OrdinalIgnoreCase),
        WebApp.Spotify  => IsHostOrSubdomain(host, "spotify.com") || IsLoginProvider(host),
        _ => false
    };

    /// <summary>
    /// Spotify's "continue with Google / Apple / Facebook" sign-in runs through these
    /// sites; blocking them would make those accounts impossible to log in with.
    /// </summary>
    private static bool IsLoginProvider(string host)
        => host.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("accounts.youtube.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("consent.google.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("appleid.apple.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("idmsa.apple.com", StringComparison.OrdinalIgnoreCase)
        || IsHostOrSubdomain(host, "facebook.com");

    /// <summary>"spotify.com" or "x.spotify.com" — but never "spotify.com.evil.de" or "evilspotify.com".</summary>
    internal static bool IsHostOrSubdomain(string host, string domain)
        => host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    // "(3) WhatsApp", "WhatsApp (3)" — the count is the only thing read from the page.
    private static readonly Regex UnreadPattern =
        new(@"^\s*\((\d{1,5})\)|\((\d{1,5})\)\s*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Unread chats from the WhatsApp Web title, 0 when the title has no count, null
    /// when the title does not look like WhatsApp at all (e.g. while loading).
    /// </summary>
    public static int? ParseWhatsAppUnread(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        // Case-sensitive on purpose: while loading, the title is the bare URL
        // ("web.whatsapp.com"), which must not reset the badge to 0.
        if (title.IndexOf("WhatsApp", StringComparison.Ordinal) < 0) return null;
        var m = UnreadPattern.Match(title);
        if (!m.Success) return 0;
        var digits = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        return int.TryParse(digits, out int n) ? n : 0;
    }

    /// <summary>
    /// "Song • Artist" while the Spotify web player itself is playing; null for the idle
    /// title ("Spotify – Web Player: …").
    /// </summary>
    public static (string Title, string Artist)? ParseSpotifyTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        int i = title.IndexOf(" • ", StringComparison.Ordinal);
        if (i <= 0) return null;
        var song = title[..i].Trim();
        var artist = title[(i + 3)..].Trim();
        return song.Length == 0 ? null : (song, artist);
    }
}
