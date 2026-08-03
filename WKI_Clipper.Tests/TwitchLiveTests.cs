using System;
using System.Threading.Tasks;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Real connection to Twitch's IRC gateway (read-only, anonymous). Gated behind
/// TWITCH_LIVE=1 so the normal test run stays offline and hermetic.
/// </summary>
public sealed class TwitchLiveTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("TWITCH_LIVE") == "1";

    [Fact]
    public async Task Connects_anonymously_and_joins_the_channel()
    {
        if (!Enabled) return;

        var settings = new SettingsService();
        settings.Load();
        settings.Current.Chat.Channel = "oskar_blitz";

        using var chat = new TwitchChatService(settings);
        var connected = new TaskCompletionSource();
        chat.StatusChanged += () => { if (chat.IsConnected) connected.TrySetResult(); };

        chat.Restart();
        var done = await Task.WhenAny(connected.Task, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.True(done == connected.Task,
            "Could not connect to irc-ws.chat.twitch.tv — is the machine online?");
        Assert.Equal("oskar_blitz", chat.Channel);

        chat.Stop();
        Assert.False(chat.IsConnected);
    }

    /// <summary>
    /// Proves the reworked read loop still reads. It now races the receive against a
    /// timer instead of awaiting it forever, and getting that wrong would silently kill
    /// the chat — exactly the failure this change was meant to detect.
    /// </summary>
    [Fact]
    public async Task Reads_real_traffic_and_tracks_when_it_last_arrived()
    {
        if (!Enabled) return;

        var settings = new SettingsService();
        settings.Load();
        // A channel that is reliably busy — an idle channel would prove nothing.
        settings.Current.Chat.Channel = Environment.GetEnvironmentVariable("TWITCH_LIVE_CHANNEL") ?? "xqc";

        using var chat = new TwitchChatService(settings);
        var gotLine = new TaskCompletionSource<ChatMessage>();
        chat.MessageReceived += m => gotLine.TrySetResult(m);

        chat.Restart();
        var done = await Task.WhenAny(gotLine.Task, Task.Delay(TimeSpan.FromSeconds(45)));

        // Even with no chatter, the server's own welcome/JOIN traffic must be recorded.
        Assert.True(chat.LastReceivedUtc.HasValue, "no bytes were received at all — the read loop is broken");
        Assert.True((DateTime.UtcNow - chat.LastReceivedUtc!.Value).TotalSeconds < 60);

        Assert.True(done == gotLine.Task, "no chat line arrived within 45 s in a busy channel");
        var msg = await gotLine.Task;
        Assert.False(string.IsNullOrWhiteSpace(msg.User));

        chat.Stop();
    }
}
