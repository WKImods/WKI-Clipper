using System;
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
}
