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
}
