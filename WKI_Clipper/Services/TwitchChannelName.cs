using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WKI_Clipper.Services;

/// <summary>
/// Turns whatever a user pastes into the chat's channel box into a Twitch login:
/// "get_wooked", "#get_wooked", "@get_wooked", "twitch.tv/get_wooked",
/// "https://www.twitch.tv/get_wooked/videos" or a popout-chat link. Returns null when no
/// valid login is in it, so a typo never silently joins an empty channel.
///
/// A link only counts when its HOST is twitch.tv (also www. and m., with or without http(s)://) - not when
/// "twitch.tv/" merely appears somewhere in a foreign address - and only when its first path part is a channel:
/// the pages of Twitch that are not channels (videos, directory, settings, ...) are rejected instead of joined
/// as a "channel" of that name.
/// </summary>
public static partial class TwitchChannelName
{
    [GeneratedRegex("^[a-z0-9_]{3,25}$")]
    private static partial Regex LoginPattern();

    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase) { "twitch.tv", "www.twitch.tv", "m.twitch.tv" };

    /// <summary>First path parts of twitch.tv that are pages, not channels.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "directory", "videos", "settings", "p", "u", "downloads", "search", "subscriptions", "inventory", "wallet", "drops",
        "friends", "messages", "turbo", "prime", "jobs", "store", "clip", "clips", "login", "signup", "following",
    };

    /// <summary>twitch.tv/popout/&lt;login&gt;/chat, /embed/&lt;login&gt;/chat and /moderator/&lt;login&gt; carry the login second.</summary>
    private static readonly HashSet<string> LoginSecond = new(StringComparer.OrdinalIgnoreCase) { "popout", "embed", "moderator" };

    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        string s = input.Trim();

        // A login itself, possibly with the # of a chat or the @ of a mention.
        string bare = s.TrimStart('#', '@').Trim().ToLowerInvariant();
        if (LoginPattern().IsMatch(bare)) return bare;

        return FromLink(s);
    }

    private static string? FromLink(string link)
    {
        string rest = link;
        int scheme = rest.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            string name = rest[..scheme];
            if (!name.Equals("http", StringComparison.OrdinalIgnoreCase) && !name.Equals("https", StringComparison.OrdinalIgnoreCase)) return null;
            rest = rest[(scheme + 3)..];
        }

        int hostEnd = rest.IndexOfAny(new[] { '/', '?', '#' });
        string host = hostEnd < 0 ? rest : rest[..hostEnd];
        if (hostEnd < 0 || !Hosts.Contains(host)) return null;       // a foreign host, or no path at all

        string path = rest[hostEnd..];
        if (path[0] != '/') return null;                              // twitch.tv?x - no path
        int cut = path.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) path = path[..cut];
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || Reserved.Contains(parts[0])) return null;

        string candidate = LoginSecond.Contains(parts[0]) ? (parts.Length > 1 ? parts[1] : "") : parts[0];
        candidate = candidate.ToLowerInvariant();
        return LoginPattern().IsMatch(candidate) ? candidate : null;
    }
}
/// <summary>
/// Tells a live channel from one that does not exist (typo, or a channel that was renamed).
/// Twitch answers a JOIN to a real channel with ROOMSTATE; a JOIN to an unknown login gets no
/// answer at all, while the socket stays open and looks perfectly "connected".
/// </summary>
public sealed class ChannelJoinWatch
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private string _channel = "";
    private DateTime _joinedUtc;
    private bool _reported;

    public bool Confirmed { get; private set; }
    public bool Missing { get; private set; }
    /// <summary>Twitch's numeric channel id from ROOMSTATE (stays the same across renames).</summary>
    public string? RoomId { get; private set; }

    public void Start(string channel, DateTime utcNow)
    {
        _channel = channel;
        _joinedUtc = utcNow;
        _reported = false;
        Confirmed = false;
        Missing = false;
        RoomId = null;
    }

    /// <summary>Feeds one IRC line; true when this line just confirmed the channel.</summary>
    public bool OnLine(string line)
    {
        if (Confirmed || !IrcParser.IsRoomStateFor(line, _channel)) return false;
        Confirmed = true;
        Missing = false;
        RoomId = IrcParser.TagValue(line, "room-id");
        return true;
    }

    /// <summary>True exactly once: the timeout passed without a confirmation.</summary>
    public bool CheckMissing(DateTime utcNow)
    {
        if (Confirmed || _reported || _channel.Length == 0 || utcNow - _joinedUtc < Timeout) return false;
        _reported = true;
        Missing = true;
        return true;
    }
}

/// <summary>
/// The check that runs a while after a JOIN and decides whether the channel is missing. Twitch's only sign of a channel that
/// does not exist is silence - and silence is just as much the sign of a link that dropped or hangs. So the verdict "missing" is
/// given only while the join is still the current one (a reconnect or a channel change makes it obsolete) AND the link provably
/// lived after the JOIN. Pure policy with the waiting and the connection state injected, so it is testable without a socket.
/// </summary>
internal static class ChannelJoinCheck
{
    /// <summary>Waits the timeout plus a grace; true when the channel is to be reported missing (the watch hands that out once).</summary>
    public static async Task<bool> RunAsync(Func<TimeSpan, CancellationToken, Task> delay, Func<bool> isCurrent, Func<bool> linkAlive,
                                            Func<bool> checkMissing, CancellationToken ct)
    {
        try { await delay(ChannelJoinWatch.Timeout + TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }

        if (!isCurrent()) return false;      // reconnected or switched meanwhile: that join has its own check
        if (!linkAlive()) return false;      // a dropped or hanging connection says nothing about the channel
        return checkMissing();
    }

    /// <summary>The socket is open and something came in after the JOIN went out (Twitch greets even a JOIN to an unknown channel).</summary>
    public static bool LinkAlive(bool connected, long lastReceivedTicks, long joinedAtTicks)
        => connected && lastReceivedTicks > joinedAtTicks;
}