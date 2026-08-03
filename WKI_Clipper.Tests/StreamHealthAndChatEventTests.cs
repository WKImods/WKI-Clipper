using System;
using System.Linq;
using WKI_Clipper.Services;
using Xunit;
// WinForms also defines CheckState; ours is the checklist traffic light.
using CheckState = WKI_Clipper.Services.CheckState;

namespace WKI_Clipper.Tests;

/// <summary>
/// Covers the v0.8.0 stream-health work: derived output stats, the disk check that has
/// to look at OBS's drive rather than the clipper's, and the Twitch event lines
/// (raid/sub/bits) the parser used to throw away.
/// </summary>
public class StreamHealthAndChatEventTests
{
    // ---- stream health ----

    [Fact]
    public void First_sample_reports_no_rate()
    {
        var cur = new StreamSample(0, 1_000_000, 3000, 0, 60_000);
        var r = StreamHealth.Compute(null, cur);

        Assert.False(r.HasRate);          // one sample cannot produce a rate
        Assert.Equal(0, r.Kbps);
        Assert.Equal(TimeSpan.FromMinutes(1), r.Uptime);
        Assert.Equal(CheckState.Unknown, StreamHealth.Rate(r));
    }

    [Fact]
    public void Two_samples_yield_recent_drop_percentage_and_bitrate()
    {
        var prev = new StreamSample(0, 1_000_000, 3000, 10, 60_000);
        // 2 s later: 120 more frames, 6 of them skipped, 1.5 MB more sent.
        var cur = new StreamSample(TimeSpan.FromSeconds(2).Ticks, 2_500_000, 3120, 16, 62_000);

        var r = StreamHealth.Compute(prev, cur);

        Assert.True(r.HasRate);
        Assert.Equal(5.0, r.RecentDropPercent, 3);            // 6 / 120
        Assert.Equal(6000, r.Kbps, 3);                        // 1.5 MB in 2 s
        Assert.Equal(16 / 3120.0 * 100, r.TotalDropPercent, 3);
        Assert.Equal(CheckState.Fail, StreamHealth.Rate(r));
    }

    [Fact]
    public void Recent_drops_are_visible_even_when_the_cumulative_ratio_stays_tiny()
    {
        // The whole point of measuring deltas: a long clean stream that starts dropping
        // NOW still shows a healthy cumulative number, which is why OBS's own display
        // is useless for noticing it.
        var prev = new StreamSample(0, 0, 1_000_000, 100, 3_600_000);
        var cur = new StreamSample(TimeSpan.FromSeconds(2).Ticks, 0, 1_000_120, 130, 3_602_000);

        var r = StreamHealth.Compute(prev, cur);

        Assert.Equal(25.0, r.RecentDropPercent, 3);        // 30 / 120 — alarming
        Assert.True(r.TotalDropPercent < 0.02);            // cumulative still looks fine
        Assert.Equal(CheckState.Fail, StreamHealth.Rate(r));
    }

    [Fact]
    public void Restarted_output_does_not_produce_negative_or_absurd_rates()
    {
        var prev = new StreamSample(0, 5_000_000, 9000, 50, 300_000);
        // Counters reset because the stream was stopped and started again.
        var cur = new StreamSample(TimeSpan.FromSeconds(2).Ticks, 1000, 40, 0, 2000);

        var r = StreamHealth.Compute(prev, cur);

        Assert.False(r.HasRate);
        Assert.Equal(0, r.Kbps);
        Assert.Equal(0, r.RecentDropPercent);
    }

    [Theory]
    [InlineData(0.0, CheckState.Ok)]
    [InlineData(0.9, CheckState.Ok)]
    [InlineData(1.0, CheckState.Warn)]
    [InlineData(4.9, CheckState.Warn)]
    [InlineData(5.0, CheckState.Fail)]
    public void Drop_thresholds_map_to_traffic_lights(double drop, CheckState expected)
    {
        var r = new StreamHealthReading(TimeSpan.Zero, 6000, drop, drop, HasRate: true);
        Assert.Equal(expected, StreamHealth.Rate(r));
    }

    [Fact]
    public void Formats_uptime_and_bitrate_readably()
    {
        Assert.Equal("2 h 14 m", StreamHealth.FormatUptime(TimeSpan.FromMinutes(134)));
        Assert.Equal("3 m 07 s", StreamHealth.FormatUptime(TimeSpan.FromSeconds(187)));
        // Decimal separator follows the user's locale, so compare the same way.
        Assert.Equal(6.0.ToString("0.0") + " Mbit/s", StreamHealth.FormatBitrate(6000));
        Assert.Equal("850 kbit/s", StreamHealth.FormatBitrate(850));
    }

    // ---- preflight: the disk that actually matters ----

    private static PreflightInput Base(double freeGb = 500, double obsGb = 0, bool obsSeparate = false,
                                       bool streaming = false, StreamHealthReading? health = null)
        => new(ObsConnected: true, CurrentScene: "Arma Reforger", Streaming: streaming,
               ReplayBufferActive: true, MicInputKnown: true, MicMuted: false, MicInputName: "Mic",
               ClipperBufferRunning: true, FreeDiskGb: freeGb,
               ObsFreeDiskGb: obsGb, ObsDiskSeparate: obsSeparate, Health: health);

    private static CheckResult HealthRow(System.Collections.Generic.List<CheckResult> checks)
        => checks.Single(c => c.Title is "Stream-Qualität" or "Stream health");

    [Fact]
    public void Full_obs_drive_is_caught_even_when_the_clipper_drive_is_empty()
    {
        // The bug this prevents: OBS records to D: with 3 GB left while the check
        // reported the clipper's C: with 500 GB and showed a green light.
        var checks = PreflightChecks.Evaluate(Base(freeGb: 500, obsGb: 3, obsSeparate: true));

        var obsRow = checks.Single(c => c.Title.Contains("OBS", StringComparison.OrdinalIgnoreCase)
                                     && c.Detail.EndsWith("GB", StringComparison.Ordinal));
        Assert.Equal(CheckState.Warn, obsRow.State);
        Assert.Contains("3", obsRow.Detail);
    }

    [Fact]
    public void One_disk_row_when_both_write_to_the_same_volume()
    {
        var checks = PreflightChecks.Evaluate(Base(freeGb: 500, obsGb: 500, obsSeparate: false));
        Assert.Single(checks, c => c.Detail.EndsWith("GB", StringComparison.Ordinal));
    }

    [Fact]
    public void Live_stream_shows_uptime_and_a_health_row()
    {
        var health = new StreamHealthReading(TimeSpan.FromMinutes(134), 6000, 0.2, 0.1, HasRate: true);
        var checks = PreflightChecks.Evaluate(Base(streaming: true, health: health));

        Assert.Contains(checks, c => c.Detail.Contains("2 h 14 m"));
        var row = HealthRow(checks);
        Assert.Equal(CheckState.Ok, row.State);
        Assert.Contains("Mbit/s", row.Detail);
    }

    [Fact]
    public void Breaking_stream_turns_the_health_row_red()
    {
        var health = new StreamHealthReading(TimeSpan.FromMinutes(5), 900, 12, 3, HasRate: true);
        Assert.Equal(CheckState.Fail, HealthRow(PreflightChecks.Evaluate(Base(streaming: true, health: health))).State);
    }

    [Fact]
    public void Health_row_says_measuring_before_the_second_sample()
    {
        var health = new StreamHealthReading(TimeSpan.FromSeconds(4), 0, 0, 0, HasRate: false);
        Assert.Equal(CheckState.Unknown, HealthRow(PreflightChecks.Evaluate(Base(streaming: true, health: health))).State);
    }

    // ---- chat events ----

    private const string RaidLine =
        "@badges=;color=#FF0000;display-name=RaiderPerson;login=raiderperson;mod=0;msg-id=raid;" +
        "msg-param-viewerCount=42;subscriber=0;" +
        "system-msg=42\\sraiders\\sfrom\\sRaiderPerson\\shave\\sjoined!;user-type= " +
        ":tmi.twitch.tv USERNOTICE #oskar_blitz";

    [Fact]
    public void Parses_a_raid_with_its_wording_and_headcount()
    {
        var m = IrcParser.ParseLine(RaidLine);

        Assert.NotNull(m);
        Assert.Equal(ChatKind.Raid, m!.Kind);
        Assert.True(m.IsEvent);
        Assert.Equal("RaiderPerson", m.User);
        Assert.Equal(42, m.RaidViewers);
        Assert.Equal("42 raiders from RaiderPerson have joined!", m.SystemText);
    }

    [Fact]
    public void ParsePrivmsg_still_ignores_events()
    {
        // Back-compat: callers asking specifically for chat messages must not suddenly
        // receive USERNOTICE lines.
        Assert.Null(IrcParser.ParsePrivmsg(RaidLine));
    }

    [Fact]
    public void Parses_a_resub_keeping_the_viewers_own_message()
    {
        const string line =
            "@badges=staff/1;display-name=ronni;login=ronni;msg-id=resub;msg-param-cumulative-months=6;" +
            "system-msg=ronni\\ssubscribed\\sfor\\s6\\smonths! " +
            ":tmi.twitch.tv USERNOTICE #oskar_blitz :Great stream -- keep it up!";

        var m = IrcParser.ParseLine(line);

        Assert.Equal(ChatKind.Sub, m!.Kind);
        Assert.Equal("ronni subscribed for 6 months!", m.SystemText);
        Assert.Equal("Great stream -- keep it up!", m.Text);
    }

    [Fact]
    public void Parses_a_gift_sub()
    {
        const string line =
            "@display-name=Gifter;login=gifter;msg-id=subgift;" +
            "system-msg=Gifter\\sgifted\\sa\\sTier\\s1\\ssub\\sto\\sFriend! " +
            ":tmi.twitch.tv USERNOTICE #oskar_blitz";

        var m = IrcParser.ParseLine(line);

        Assert.Equal(ChatKind.SubGift, m!.Kind);
        Assert.Contains("gifted", m.SystemText!);
    }

    [Fact]
    public void Cheered_bits_are_flagged_on_a_normal_message()
    {
        const string line =
            "@badges=;bits=100;color=#0000FF;display-name=Cheerer;mod=0 " +
            ":cheerer!cheerer@cheerer.tmi.twitch.tv PRIVMSG #oskar_blitz :cheer100 nice play";

        var m = IrcParser.ParseLine(line);

        Assert.Equal(ChatKind.Bits, m!.Kind);
        Assert.Equal(100, m.Bits);
        Assert.False(m.IsEvent);            // still a chat line, just highlighted
        Assert.Equal("cheer100 nice play", m.Text);
        Assert.NotNull(IrcParser.ParsePrivmsg(line));
    }

    [Fact]
    public void Announcement_falls_back_to_its_body_text()
    {
        const string line =
            "@display-name=Mod;login=mod;msg-id=announcement " +
            ":tmi.twitch.tv USERNOTICE #oskar_blitz :Server is back up";

        var m = IrcParser.ParseLine(line);

        Assert.Equal(ChatKind.Announcement, m!.Kind);
        Assert.Equal("Server is back up", m.SystemText);
    }

    [Fact]
    public void Usernotice_without_anything_to_show_is_dropped()
    {
        const string line = "@login=x;msg-id=viewermilestone :tmi.twitch.tv USERNOTICE #oskar_blitz";
        Assert.Null(IrcParser.ParseLine(line));
    }

    [Fact]
    public void Ordinary_messages_are_unchanged()
    {
        const string line =
            "@badges=moderator/1;color=#1E90FF;display-name=Helper;mod=1 " +
            ":helper!helper@helper.tmi.twitch.tv PRIVMSG #oskar_blitz :hi: there";

        var m = IrcParser.ParseLine(line);

        Assert.Equal(ChatKind.Message, m!.Kind);
        Assert.Equal("Helper", m.User);
        Assert.Equal("hi: there", m.Text);   // colons inside the body survive
        Assert.True(m.IsMod);
        Assert.Equal(0, m.Bits);
    }
}
