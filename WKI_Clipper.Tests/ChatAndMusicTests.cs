using System.Linq;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class ChatAndMusicTests
{
    // ---- IRC parsing (real Twitch line shapes) ----

    private const string FullLine =
        "@badge-info=subscriber/12;badges=broadcaster/1,subscriber/12;color=#1E90FF;display-name=Oskar_Blitz;mod=0 " +
        ":oskar_blitz!oskar_blitz@oskar_blitz.tmi.twitch.tv PRIVMSG #oskar_blitz :moin moin";

    [Fact]
    public void Parses_tags_name_color_and_text()
    {
        var m = IrcParser.ParsePrivmsg(FullLine)!;
        Assert.Equal("Oskar_Blitz", m.User);
        Assert.Equal("#1E90FF", m.Color);
        Assert.Equal("moin moin", m.Text);
        Assert.True(m.IsBroadcaster);
        Assert.True(m.IsSub);
        Assert.False(m.IsMod);
    }

    [Fact]
    public void Message_may_contain_colons_and_urls()
    {
        var line = ":a!a@a.tmi.twitch.tv PRIVMSG #chan :look: https://twitch.tv/oskar_blitz :)";
        var m = IrcParser.ParsePrivmsg(line)!;
        Assert.Equal("look: https://twitch.tv/oskar_blitz :)", m.Text);
    }

    [Fact]
    public void Falls_back_to_nick_when_no_display_name_tag()
    {
        var m = IrcParser.ParsePrivmsg(":someone!someone@someone.tmi.twitch.tv PRIVMSG #chan :hi")!;
        Assert.Equal("someone", m.User);
        Assert.Null(m.Color);          // no color tag → widget picks its own
    }

    [Fact]
    public void Unescapes_ircv3_tag_values()
    {
        var line = @"@display-name=Cool\sGuy;color=#FF0000 :c!c@c.tmi.twitch.tv PRIVMSG #chan :yo";
        Assert.Equal("Cool Guy", IrcParser.ParsePrivmsg(line)!.User);
    }

    [Fact]
    public void Detects_mod_and_vip_badges()
    {
        var line = "@badges=moderator/1,vip/1;display-name=Mod :m!m@m.tmi.twitch.tv PRIVMSG #chan :hey";
        var m = IrcParser.ParsePrivmsg(line)!;
        Assert.True(m.IsMod);
        Assert.True(m.IsVip);
    }

    [Theory]
    [InlineData("PING :tmi.twitch.tv")]
    [InlineData(":tmi.twitch.tv 001 justinfan123 :Welcome, GLHF!")]
    [InlineData(":justinfan1!justinfan1@x.tmi.twitch.tv JOIN #chan")]
    [InlineData("")]
    [InlineData(null)]
    public void Non_privmsg_lines_are_ignored(string? line)
        => Assert.Null(IrcParser.ParsePrivmsg(line));

    [Fact]
    public void Ping_is_detected_and_answered_with_the_same_token()
    {
        Assert.True(IrcParser.IsPing("PING :tmi.twitch.tv"));
        Assert.Equal("PONG :tmi.twitch.tv", IrcParser.PongFor("PING :tmi.twitch.tv"));
    }

    // ---- now-playing name parsing ----

    [Theory]
    [InlineData(@"C:\m\Elektronomia - Sky High.mp3", "Elektronomia", "Sky High")]
    [InlineData(@"C:\m\JustATitle.mp3", "", "JustATitle")]
    [InlineData(@"C:\m\A - B - C.mp3", "A", "B - C")]   // split on the FIRST separator only
    public void Track_name_splits_artist_and_title(string path, string artist, string title)
    {
        var (a, t) = TrackName.Parse(path);
        Assert.Equal(artist, a);
        Assert.Equal(title, t);
    }

    [Fact]
    public void Now_playing_template_is_filled_and_stays_clean_without_artist()
    {
        Assert.Equal("Elektronomia - Sky High",
            TrackName.Format("{artist} - {title}", @"C:\m\Elektronomia - Sky High.mp3"));
        // No artist → no dangling dash in the OBS text source.
        Assert.Equal("JustATitle", TrackName.Format("{artist} - {title}", @"C:\m\JustATitle.mp3"));
        Assert.Equal("♪ Sky High", TrackName.Format("♪ {title}", @"C:\m\Elektronomia - Sky High.mp3"));
    }

    // ---- playlist order ----

    private static Playlist Sequential(int n)
    {
        var p = new Playlist(seed: 1);
        p.SetTracks(Enumerable.Range(0, n).Select(i => $@"C:\m\{i}.mp3"), shuffle: false);
        return p;
    }

    [Fact]
    public void Next_walks_forward_and_wraps_when_repeating()
    {
        var p = Sequential(3);
        Assert.Equal(@"C:\m\0.mp3", p.Current);
        Assert.Equal(@"C:\m\1.mp3", p.Next());
        Assert.Equal(@"C:\m\2.mp3", p.Next());
        Assert.Equal(@"C:\m\0.mp3", p.Next());   // wrapped
    }

    [Fact]
    public void Next_stops_at_the_end_without_repeat()
    {
        var p = Sequential(2);
        p.Repeat = false;
        p.Next();
        Assert.Null(p.Next());
    }

    [Fact]
    public void Prev_walks_back_and_jump_selects_a_track()
    {
        var p = Sequential(3);
        p.JumpTo(2);
        Assert.Equal(@"C:\m\2.mp3", p.Current);
        Assert.Equal(@"C:\m\1.mp3", p.Prev());
    }

    [Fact]
    public void Shuffle_keeps_every_track_exactly_once_and_holds_the_current_one()
    {
        var p = new Playlist(seed: 42);
        p.SetTracks(Enumerable.Range(0, 6).Select(i => $@"C:\m\{i}.mp3"), shuffle: true);
        p.JumpTo(3);
        var before = p.Current;

        p.SetShuffle(false);
        Assert.Equal(before, p.Current);          // switching mode doesn't skip the track

        var seen = new System.Collections.Generic.HashSet<string?>();
        p.JumpTo(0);
        for (int i = 0; i < 6; i++) { seen.Add(p.Current); p.Next(); }
        Assert.Equal(6, seen.Count);              // full coverage, no duplicates
    }

    [Fact]
    public void Empty_playlist_is_harmless()
    {
        var p = new Playlist(seed: 1);
        p.SetTracks(System.Array.Empty<string>(), shuffle: true);
        Assert.Null(p.Current);
        Assert.Null(p.Next());
        Assert.Null(p.Prev());
    }

    [Fact]
    public void Missing_music_folder_scans_to_empty()
        => Assert.Empty(Playlist.ScanFolder(@"Z:\definitely\not\here"));

    // ---- migration v7 ----

    [Fact]
    public void V6_gains_chat_and_music_widgets_with_clickthrough_chat()
    {
        var s = new AppSettings { SchemaVersion = 6 };
        s.Widgets.Widgets.RemoveAll(w => w.Id is WidgetId.Chat or WidgetId.Music);

        Assert.True(SettingsService.MigrateIfNeeded(s));
        Assert.Equal(SettingsService.CurrentSchemaVersion, s.SchemaVersion);
        Assert.NotNull(s.Chat);
        Assert.NotNull(s.Music);
        Assert.Equal("oskar_blitz", s.Chat.Channel);
        var chat = s.Widgets.Widgets.First(w => w.Id == WidgetId.Chat);
        Assert.True(chat.ClickThrough);           // must not eat clicks over a game
        Assert.Contains(s.Widgets.Widgets, w => w.Id == WidgetId.Music);
    }

    [Fact]
    public void V7_gains_the_sources_widget()
    {
        var s = new AppSettings { SchemaVersion = 7 };
        s.Widgets.Widgets.RemoveAll(w => w.Id == WidgetId.Sources);

        Assert.True(SettingsService.MigrateIfNeeded(s));
        Assert.Equal(SettingsService.CurrentSchemaVersion, s.SchemaVersion);
        Assert.Contains(s.Widgets.Widgets, w => w.Id == WidgetId.Sources);
    }

    [Fact]
    public void Old_widget_states_default_to_not_click_through()
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize<WidgetState>(
            "{\"Id\":\"Capture\",\"Visible\":true,\"Width\":300,\"Height\":300}")!;
        Assert.False(legacy.ClickThrough);
    }
}
