using System;
using System.Text.RegularExpressions;

namespace WKI_Clipper.Services;

/// <summary>
/// Turns whatever a user pastes into the chat's channel box into a Twitch login:
/// "get_wooked", "#get_wooked", "@get_wooked", "twitch.tv/get_wooked",
/// "https://www.twitch.tv/get_wooked/videos" or a popout-chat link. Returns null when no
/// valid login is in it, so a typo never silently joins an empty channel.
/// </summary>
public static partial class TwitchChannelName
{
    [GeneratedRegex("^[a-z0-9_]{3,25}$")]
    private static partial Regex LoginPattern();

    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        string s = input.Trim();

        int host = s.IndexOf("twitch.tv/", StringComparison.OrdinalIgnoreCase);
        if (host >= 0)
        {
            string path = s[(host + "twitch.tv/".Length)..];
            int cut = path.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) path = path[..cut];
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            // twitch.tv/popout/<login>/chat and twitch.tv/embed/<login>/chat carry the login second.
            s = parts[0].Equals("popout", StringComparison.OrdinalIgnoreCase)
                || parts[0].Equals("embed", StringComparison.OrdinalIgnoreCase)
                ? (parts.Length > 1 ? parts[1] : "")
                : parts[0];
        }

        s = s.TrimStart('#', '@').Trim().ToLowerInvariant();
        return LoginPattern().IsMatch(s) ? s : null;
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
