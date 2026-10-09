using System;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>v0.12: Spotify + WhatsApp widgets — navigation rules, title parsing, migration.</summary>
public sealed class WebWidgetTests
{
    [Theory]
    [InlineData("https://web.whatsapp.com/")]
    [InlineData("https://web.whatsapp.com/send?phone=1")]
    [InlineData("about:blank")]
    public void WhatsApp_keeps_its_own_pages(string uri)
        => Assert.Equal(WebNavDecision.Allow, WebAppRules.Decide(WebApp.WhatsApp, uri));

    [Theory]
    [InlineData("https://web.whatsapp.com.evil.de/")]        // suffix trick
    [InlineData("https://evilweb.whatsapp.com.de/")]
    [InlineData("https://web.whatsapp.com@evil.de/")]        // userinfo trick: host is evil.de
    [InlineData("https://evil.de/web.whatsapp.com")]
    [InlineData("http://web.whatsapp.com/")]                 // not over https
    [InlineData("https://www.whatsapp.com/faq")]             // other WhatsApp sites: browser
    [InlineData("https://open.spotify.com/")]                // other app's site
    [InlineData("https://accounts.google.com/")]             // login providers are Spotify-only
    public void Foreign_web_links_open_in_the_browser(string uri)
        => Assert.Equal(WebNavDecision.OpenExternal, WebAppRules.Decide(WebApp.WhatsApp, uri));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("spotify:track:123")]
    [InlineData("ms-settings:privacy")]
    [InlineData("data:text/html,hi")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void Non_web_addresses_are_dropped(string? uri)
    {
        Assert.Equal(WebNavDecision.Block, WebAppRules.Decide(WebApp.WhatsApp, uri));
        Assert.Equal(WebNavDecision.Block, WebAppRules.Decide(WebApp.Spotify, uri));
    }

    [Theory]
    [InlineData("https://open.spotify.com/playlist/x")]
    [InlineData("https://accounts.spotify.com/de/login")]
    [InlineData("https://spotify.com/")]
    [InlineData("https://accounts.google.com/o/oauth2/auth")]
    [InlineData("https://appleid.apple.com/auth/authorize")]
    [InlineData("https://www.facebook.com/v12.0/dialog/oauth")]
    public void Spotify_allows_its_pages_and_sign_in_providers(string uri)
        => Assert.Equal(WebNavDecision.Allow, WebAppRules.Decide(WebApp.Spotify, uri));

    [Theory]
    [InlineData("https://spotify.com.evil.de/")]
    [InlineData("https://evilspotify.com/")]
    [InlineData("https://www.google.com/search?q=x")]
    [InlineData("https://www.apple.com/")]
    [InlineData("https://facebook.com.evil.de/")]
    [InlineData("https://l.facebook.com/l.php?u=https%3A%2F%2Fevil.de")]   // link shim
    public void Spotify_lookalikes_go_to_the_browser(string uri)
        => Assert.Equal(WebNavDecision.OpenExternal, WebAppRules.Decide(WebApp.Spotify, uri));

    [Theory]
    [InlineData("WhatsApp", 0)]
    [InlineData("WhatsApp Web", 0)]
    [InlineData("(3) WhatsApp", 3)]
    [InlineData("WhatsApp (12)", 12)]
    [InlineData("(250) WhatsApp Business", 250)]
    public void Unread_count_comes_from_the_title(string title, int expected)
        => Assert.Equal(expected, WebAppRules.ParseWhatsAppUnread(title));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("web.whatsapp.com")]   // loading, not the app title yet
    public void Non_app_titles_do_not_touch_the_count(string? title)
        => Assert.Null(WebAppRules.ParseWhatsAppUnread(title));

    [Fact]
    public void Spotify_web_title_parsing()
    {
        Assert.Equal(("Song", "Artist"), WebAppRules.ParseSpotifyTitle("Song • Artist"));
        Assert.Null(WebAppRules.ParseSpotifyTitle("Spotify – Webplayer: Musik für alle"));
        Assert.Null(WebAppRules.ParseSpotifyTitle(null));
    }

    [Fact]
    public void Profiles_are_separate_per_app()
        => Assert.NotEqual(WebAppRules.ProfileName(WebApp.WhatsApp), WebAppRules.ProfileName(WebApp.Spotify));

    [Fact]
    public void V8_gets_both_widgets_private_whatsapp_and_unbound_spotify_hotkeys()
    {
        var s = new AppSettings { SchemaVersion = 8 };
        s.Widgets.Widgets.RemoveAll(w => w.Id is WidgetId.Spotify or WidgetId.WhatsApp);
        s.Hotkeys.Remove(HotkeyActions.SpotifyPlayPause);
        s.Hotkeys.Remove(HotkeyActions.SpotifyNext);
        s.Hotkeys.Remove(HotkeyActions.SpotifyPrevious);
        s.Hotkeys[HotkeyActions.SaveReplay] = new HotkeyBinding { Modifiers = HotkeyModifier.Shift, Key = 0x70 };

        Assert.True(SettingsService.MigrateIfNeeded(s));

        Assert.Equal(SettingsService.CurrentSchemaVersion, s.SchemaVersion);
        var wa = s.Widgets.Widgets.Find(w => w.Id == WidgetId.WhatsApp)!;
        Assert.True(wa.ExcludeFromCapture);
        Assert.True(wa.ClickThrough);
        Assert.False(wa.Visible);
        Assert.Contains(s.Widgets.Widgets, w => w.Id == WidgetId.Spotify);
        Assert.Equal(0u, s.Hotkeys[HotkeyActions.SpotifyPlayPause].Key);   // unbound
        Assert.Equal(0u, s.Hotkeys[HotkeyActions.SpotifyNext].Key);
        Assert.Equal(0u, s.Hotkeys[HotkeyActions.SpotifyPrevious].Key);
        Assert.Equal(0x70u, s.Hotkeys[HotkeyActions.SaveReplay].Key);      // user binding untouched
        Assert.True(s.WhatsApp.Muted);
    }

    [Fact]
    public void Unbound_spotify_hotkeys_never_collide()
    {
        var s = new AppSettings();
        Assert.Null(HotkeyService.FindCollision(s, new HotkeyBinding()));
    }

    [Fact]
    public void The_widget_browser_is_never_a_capture_target()
        => Assert.False(CaptureTargetResolver.IsCouplableApp("msedgewebview2"));

    [Theory]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", true)]
    [InlineData("Spotify.exe", true)]
    [InlineData("WKI_Clipper.exe", false)]
    [InlineData("MSEdge", false)]
    [InlineData(null, false)]
    public void Only_the_spotify_app_session_is_used(string? aumid, bool expected)
        => Assert.Equal(expected, SpotifyMediaService.IsSpotifyAppId(aumid));

    [Fact]
    public void Progress_is_interpolated_while_playing_and_clamped()
    {
        var t0 = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        var playing = SpotifySnapshot.Empty with
        {
            HasSession = true, IsPlaying = true,
            Position = TimeSpan.FromSeconds(30), Duration = TimeSpan.FromSeconds(200), PositionUpdatedAt = t0
        };
        Assert.Equal(TimeSpan.FromSeconds(40), playing.PositionAt(t0.AddSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(200), playing.PositionAt(t0.AddHours(1)));      // never past the end

        var paused = playing with { IsPlaying = false };
        Assert.Equal(TimeSpan.FromSeconds(30), paused.PositionAt(t0.AddSeconds(10)));
    }
}
