using System;
using System.Collections.Generic;
using System.Linq;

namespace WKI_Clipper.Services;

/// <summary>One chat line ready for display.</summary>
public sealed class ChatMessage
{
    public string User { get; init; } = "";
    /// <summary>#RRGGBB from the IRC color tag, or null when the user never picked one.</summary>
    public string? Color { get; init; }
    public string Text { get; init; } = "";
    public bool IsMod { get; init; }
    public bool IsSub { get; init; }
    public bool IsVip { get; init; }
    public bool IsBroadcaster { get; init; }
    public DateTime ReceivedLocal { get; init; } = DateTime.Now;
}

/// <summary>
/// Parser for Twitch's IRCv3 lines. Pure and side-effect free — the interesting edge
/// cases (missing tags, escaped values, colons inside the message) are unit-tested
/// instead of being debugged against a live chat.
/// </summary>
public static class IrcParser
{
    /// <summary>
    /// Parses a PRIVMSG line into a <see cref="ChatMessage"/>. Returns null for every
    /// other line type (JOIN, NOTICE, PING, …) so callers can simply ignore those.
    /// </summary>
    public static ChatMessage? ParsePrivmsg(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        Dictionary<string, string>? tags = null;
        string rest = line;

        if (rest[0] == '@')
        {
            int sp = rest.IndexOf(' ');
            if (sp < 0) return null;
            tags = ParseTags(rest[1..sp]);
            rest = rest[(sp + 1)..];
        }

        // :nick!user@host PRIVMSG #channel :message
        if (rest.Length == 0 || rest[0] != ':') return null;
        int prefixEnd = rest.IndexOf(' ');
        if (prefixEnd < 0) return null;
        string prefix = rest[1..prefixEnd];
        rest = rest[(prefixEnd + 1)..];

        if (!rest.StartsWith("PRIVMSG ", StringComparison.Ordinal)) return null;

        // The message body starts at the FIRST " :" after the command — everything
        // after it is literal text and may itself contain colons.
        int bodyStart = rest.IndexOf(" :", StringComparison.Ordinal);
        if (bodyStart < 0) return null;
        string text = rest[(bodyStart + 2)..].TrimEnd('\r', '\n');

        string nick = prefix.Split('!')[0];
        string user = Tag(tags, "display-name") is { Length: > 0 } dn ? dn : nick;
        var badges = ParseBadges(Tag(tags, "badges"));

        return new ChatMessage
        {
            User = user,
            Color = Tag(tags, "color") is { Length: > 0 } c && c.StartsWith('#') ? c : null,
            Text = text,
            IsMod = badges.Contains("moderator") || Tag(tags, "mod") == "1",
            IsSub = badges.Contains("subscriber") || Tag(tags, "subscriber") == "1",
            IsVip = badges.Contains("vip"),
            IsBroadcaster = badges.Contains("broadcaster")
        };
    }

    /// <summary>True for a server PING — the caller must answer with PONG or get dropped.</summary>
    public static bool IsPing(string? line)
        => line != null && line.StartsWith("PING", StringComparison.Ordinal);

    /// <summary>The PONG answer for a given PING line.</summary>
    public static string PongFor(string pingLine)
    {
        int idx = pingLine.IndexOf(':');
        return idx >= 0 ? "PONG " + pingLine[idx..] : "PONG :tmi.twitch.tv";
    }

    private static Dictionary<string, string> ParseTags(string raw)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq < 0) { map[part] = ""; continue; }
            map[part[..eq]] = Unescape(part[(eq + 1)..]);
        }
        return map;
    }

    /// <summary>IRCv3 tag escaping: \s = space, \: = semicolon, \\ = backslash, \r \n.</summary>
    private static string Unescape(string v)
    {
        if (!v.Contains('\\')) return v;
        var sb = new System.Text.StringBuilder(v.Length);
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i] != '\\' || i + 1 >= v.Length) { sb.Append(v[i]); continue; }
            char n = v[++i];
            sb.Append(n switch { 's' => ' ', ':' => ';', 'r' => '\r', 'n' => '\n', '\\' => '\\', _ => n });
        }
        return sb.ToString();
    }

    private static HashSet<string> ParseBadges(string? raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(raw)) return set;
        foreach (var b in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
            set.Add(b.Split('/')[0]);
        return set;
    }

    private static string? Tag(Dictionary<string, string>? tags, string key)
        => tags != null && tags.TryGetValue(key, out var v) ? v : null;
}
