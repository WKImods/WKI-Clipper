using System;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public class TwitchChannelCheckTests
{
    // Real lines captured from irc-ws.chat.twitch.tv on 2026-10-20.
    private const string RoomStateGetWooked =
        "@emote-only=0;followers-only=-1;r9k=0;room-id=92738751;slow=0;subs-only=0 :tmi.twitch.tv ROOMSTATE #get_wooked";
    private const string JoinEcho = ":justinfan66473!justinfan66473@justinfan66473.tmi.twitch.tv JOIN #get_wooked";

    [Theory]
    [InlineData("get_wooked", "get_wooked")]
    [InlineData("  #Get_Wooked ", "get_wooked")]
    [InlineData("@get_wooked", "get_wooked")]
    [InlineData("https://www.twitch.tv/get_wooked", "get_wooked")]
    [InlineData("twitch.tv/get_wooked/", "get_wooked")]
    [InlineData("https://www.twitch.tv/get_wooked/videos?filter=archives", "get_wooked")]
    [InlineData("https://m.twitch.tv/get_wooked", "get_wooked")]
    [InlineData("https://www.twitch.tv/popout/get_wooked/chat?popout=", "get_wooked")]
    [InlineData("https://www.twitch.tv/embed/get_wooked/chat?parent=x", "get_wooked")]
    public void Names_and_links_normalize_to_the_login(string input, string expected)
        => Assert.Equal(expected, TwitchChannelName.Normalize(input));

    [Theory]
    [InlineData("twitch.tv/get_wooked", "get_wooked")]
    [InlineData("www.twitch.tv/get_wooked", "get_wooked")]
    [InlineData("WWW.TWITCH.TV/Get_Wooked", "get_wooked")]
    [InlineData("http://twitch.tv/get_wooked", "get_wooked")]
    [InlineData("https://m.twitch.tv/get_wooked/about", "get_wooked")]
    [InlineData("https://twitch.tv/get_wooked#top", "get_wooked")]
    [InlineData("twitch.tv/popout/get_wooked/chat", "get_wooked")]
    [InlineData("m.twitch.tv/embed/get_wooked/chat", "get_wooked")]
    [InlineData("https://www.twitch.tv/moderator/get_wooked", "get_wooked")]       // the mod view of the channel
    public void Only_twitch_tv_hosts_with_or_without_scheme_and_subdomain_are_links(string input, string expected)
        => Assert.Equal(expected, TwitchChannelName.Normalize(input));

    [Theory]
    [InlineData("evil.com/twitch.tv/get_wooked")]                 // the host is evil.com, twitch.tv is just part of its path
    [InlineData("https://evil.com/twitch.tv/get_wooked")]
    [InlineData("https://evil.com/?u=twitch.tv/get_wooked")]
    [InlineData("?q=twitch.tv/get_wooked")]
    [InlineData("nottwitch.tv/get_wooked")]
    [InlineData("https://twitch.tv.evil.com/get_wooked")]
    [InlineData("https://twitch.tv@evil.com/get_wooked")]
    [InlineData("https://clips.twitch.tv/FunnySlug")]
    [InlineData("https://dashboard.twitch.tv/get_wooked")]
    [InlineData("ftp://twitch.tv/get_wooked")]
    [InlineData("twitch.tv")]
    [InlineData("https://www.twitch.tv")]
    public void Foreign_hosts_and_hosts_without_a_channel_are_rejected(string input)
        => Assert.Null(TwitchChannelName.Normalize(input));

    [Theory]
    [InlineData("https://www.twitch.tv/videos/2283746123")]
    [InlineData("twitch.tv/directory/category/arma-reforger")]
    [InlineData("twitch.tv/directory")]
    [InlineData("twitch.tv/settings")]
    [InlineData("twitch.tv/p/legal")]
    [InlineData("twitch.tv/downloads")]
    [InlineData("twitch.tv/search?term=get_wooked")]
    [InlineData("twitch.tv/subscriptions")]
    [InlineData("twitch.tv/inventory")]
    [InlineData("twitch.tv/wallet")]
    [InlineData("https://www.twitch.tv/drops/inventory")]
    [InlineData("twitch.tv/friends")]
    [InlineData("TWITCH.TV/Videos/1")]
    [InlineData("https://www.twitch.tv/moderator/")]
    public void Twitch_pages_that_are_not_channels_are_rejected(string input)
        => Assert.Null(TwitchChannelName.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ab")]
    [InlineData("get wooked")]
    [InlineData("get-wooked")]
    [InlineData("https://www.twitch.tv/")]
    [InlineData("https://www.twitch.tv/popout/")]
    [InlineData("abcdefghijklmnopqrstuvwxyz")]
    public void Invalid_input_gives_null(string? input) => Assert.Null(TwitchChannelName.Normalize(input));

    [Fact]
    public void Roomstate_for_the_joined_channel_is_recognised()
    {
        Assert.True(IrcParser.IsRoomStateFor(RoomStateGetWooked, "get_wooked"));
        Assert.True(IrcParser.IsRoomStateFor(RoomStateGetWooked + "\r\n", "GET_WOOKED"));
        Assert.False(IrcParser.IsRoomStateFor(RoomStateGetWooked, "oskar_blitz"));
        Assert.False(IrcParser.IsRoomStateFor(RoomStateGetWooked, "get_wook"));
        Assert.False(IrcParser.IsRoomStateFor(JoinEcho, "get_wooked"));
    }

    [Fact]
    public void Room_id_is_read_from_the_tags()
    {
        Assert.Equal("92738751", IrcParser.TagValue(RoomStateGetWooked, "room-id"));
        Assert.Null(IrcParser.TagValue(JoinEcho, "room-id"));
    }

    [Fact]
    public void Roomstate_confirms_the_channel_and_no_missing_is_reported()
    {
        var w = new ChannelJoinWatch();
        var t0 = new DateTime(2026, 10, 20, 20, 0, 0, DateTimeKind.Utc);
        w.Start("get_wooked", t0);
        Assert.False(w.OnLine(JoinEcho));
        Assert.True(w.OnLine(RoomStateGetWooked));
        Assert.True(w.Confirmed);
        Assert.Equal("92738751", w.RoomId);
        Assert.False(w.CheckMissing(t0.AddSeconds(30)));
        Assert.False(w.Missing);
    }

    [Fact]
    public void Renamed_channel_without_answer_is_reported_once_after_the_timeout()
    {
        var w = new ChannelJoinWatch();
        var t0 = new DateTime(2026, 10, 20, 20, 0, 0, DateTimeKind.Utc);
        w.Start("oskar_blitz", t0);
        Assert.False(w.CheckMissing(t0.AddSeconds(5)));            // still within the grace time
        Assert.True(w.CheckMissing(t0 + ChannelJoinWatch.Timeout)); // silence → missing
        Assert.True(w.Missing);
        Assert.False(w.CheckMissing(t0.AddMinutes(5)));            // only once per join
        Assert.False(w.OnLine(RoomStateGetWooked));                // another channel's state does not confirm it
    }

    [Fact]
    public void A_new_join_resets_the_missing_state()
    {
        var w = new ChannelJoinWatch();
        var t0 = new DateTime(2026, 10, 20, 20, 0, 0, DateTimeKind.Utc);
        w.Start("oskar_blitz", t0);
        Assert.True(w.CheckMissing(t0.AddSeconds(11)));
        w.Start("get_wooked", t0.AddSeconds(20));
        Assert.False(w.Missing);
        Assert.True(w.OnLine(RoomStateGetWooked));
    }

    [Fact]
    public void Empty_channel_never_reports_missing()
    {
        var w = new ChannelJoinWatch();
        var t0 = DateTime.UtcNow;
        w.Start("", t0);
        Assert.False(w.CheckMissing(t0.AddMinutes(1)));
    }

    // ---- the check after the join: a dropped or silent link is not a missing channel ----

    /// <summary>The connection as the check sees it, and a clock that does not wait.</summary>
    private sealed class FakeLink
    {
        public int Generation = 1;
        public bool Connected = true;
        public long LastRxTicks;
        public readonly ChannelJoinWatch Watch = new();
        public readonly DateTime JoinedAt = new(2026, 10, 20, 20, 0, 0, DateTimeKind.Utc);
        public TimeSpan? Waited;
        public Action? DuringWait;       // what happens on the line while the check sleeps

        public FakeLink(string channel = "oskar_blitz")
        {
            Watch.Start(channel, JoinedAt);
            LastRxTicks = JoinedAt.AddSeconds(1).Ticks;      // the welcome lines after the JOIN: the link lives
        }

        public Task<bool> Check(int generation, CancellationToken ct = default) => ChannelJoinCheck.RunAsync(
            delay: (span, token) => { Waited = span; DuringWait?.Invoke(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            isCurrent: () => generation == Generation,
            linkAlive: () => ChannelJoinCheck.LinkAlive(Connected, LastRxTicks, JoinedAt.Ticks),
            checkMissing: () => Watch.CheckMissing(JoinedAt + ChannelJoinWatch.Timeout + TimeSpan.FromSeconds(1)),
            ct);
    }

    [Fact]
    public async Task A_live_link_that_never_answered_the_join_reports_the_channel_missing_once()
    {
        var link = new FakeLink();
        Assert.True(await link.Check(link.Generation));
        Assert.Equal(ChannelJoinWatch.Timeout + TimeSpan.FromMilliseconds(250), link.Waited);   // the grace after the timeout
        Assert.True(link.Watch.Missing);
        Assert.False(await link.Check(link.Generation));                                         // once per join
    }

    [Fact]
    public async Task A_confirmed_channel_is_never_reported()
    {
        var link = new FakeLink("get_wooked");
        link.Watch.OnLine(RoomStateGetWooked);
        Assert.False(await link.Check(link.Generation));
    }

    [Fact]
    public async Task A_reconnect_while_waiting_cancels_the_check()
    {
        var link = new FakeLink();
        int generation = link.Generation;
        link.DuringWait = () => link.Generation++;           // the link dropped and came back: a newer join owns the watch

        Assert.False(await link.Check(generation));

        Assert.False(link.Watch.Missing);                    // and the old join was not "used up" as missing
    }

    [Fact]
    public async Task A_connection_that_dropped_while_waiting_is_not_a_missing_channel()
    {
        var link = new FakeLink();
        link.DuringWait = () => link.Connected = false;      // the reconnect attempts keep failing: still the same generation

        Assert.False(await link.Check(link.Generation));

        Assert.False(link.Watch.Missing);
        link.Connected = true;
        link.DuringWait = null;
        Assert.True(await link.Check(link.Generation));      // had it been alive at the deadline, the verdict would have been "missing"
    }

    [Fact]
    public async Task A_socket_that_delivered_nothing_since_the_join_is_not_a_missing_channel()
    {
        var link = new FakeLink();
        link.LastRxTicks = link.JoinedAt.AddMinutes(-5).Ticks;   // half-open: "connected", but nothing has come in since the JOIN
        Assert.False(await link.Check(link.Generation));
        Assert.False(link.Watch.Missing);

        link.LastRxTicks = 0;                                    // not a single byte yet
        Assert.False(await link.Check(link.Generation));
    }

    [Theory]
    [InlineData(true, 10, 5, true)]       // data after the JOIN on an open socket
    [InlineData(true, 5, 5, false)]       // nothing newer than the JOIN itself
    [InlineData(true, 4, 5, false)]
    [InlineData(true, 0, 5, false)]
    [InlineData(false, 10, 5, false)]     // the socket is gone
    public void The_link_lives_when_the_socket_is_open_and_something_arrived_after_the_join(bool connected, long rx, long joined, bool alive)
        => Assert.Equal(alive, ChannelJoinCheck.LinkAlive(connected, rx, joined));

    [Fact]
    public async Task A_cancelled_check_reports_nothing()
    {
        var link = new FakeLink();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(await link.Check(link.Generation, cts.Token));
        Assert.False(link.Watch.Missing);
    }
}
