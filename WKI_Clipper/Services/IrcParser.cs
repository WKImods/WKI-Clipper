using System;
using System.Collections.Generic;
using System.Linq;

namespace WKI_Clipper.Services;

/// <summary>What a chat line represents. Everything but <see cref="Message"/> and
/// <see cref="Bits"/> comes from USERNOTICE and is rendered as a highlighted event.</summary>
public enum ChatKind { Message, Bits, Raid, Sub, SubGift, Announcement, Other }

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

    public ChatKind Kind { get; init; } = ChatKind.Message;
    /// <summary>Cheered bits on a PRIVMSG, 0 otherwise.</summary>
    public int Bits { get; init; }
    /// <summary>Twitch's own wording for an event ("X is raiding with a party of 42").</summary>
    public string? SystemText { get; init; }
    /// <summary>Incoming raiders, 0 when not a raid.</summary>
    public int RaidViewers { get; init; }

    /// <summary>True for raids/subs/announcements — the lines worth looking up from a game.</summary>
    public bool IsEvent => Kind is not (ChatKind.Message or ChatKind.Bits);
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
    /// other line type (JOIN, NOTICE, PING, USERNOTICE …) so callers can ignore those.
    /// </summary>
    public static ChatMessage? ParsePrivmsg(string? line)
    {
        var msg = ParseLine(line);
        // Only PRIVMSG produces these two kinds.
        return msg is null || msg.IsEvent ? null : msg;
    }

    /// <summary>
    /// Parses any displayable line — chat messages (PRIVMSG) and channel events
    /// (USERNOTICE: raids, subs, gifts, announcements). Returns null for protocol
    /// chatter the widget has no use for.
    /// </summary>
    public static ChatMessage? ParseLine(string? line)
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

        // :nick!user@host COMMAND #channel :message   (USERNOTICE's prefix is tmi.twitch.tv)
        if (rest.Length == 0 || rest[0] != ':') return null;
        int prefixEnd = rest.IndexOf(' ');
        if (prefixEnd < 0) return null;
        string prefix = rest[1..prefixEnd];
        rest = rest[(prefixEnd + 1)..];

        int cmdEnd = rest.IndexOf(' ');
        string command = cmdEnd < 0 ? rest : rest[..cmdEnd];

        // The body starts at the FIRST " :" after the command — everything after it is
        // literal text and may itself contain colons.
        int bodyStart = rest.IndexOf(" :", StringComparison.Ordinal);
        string text = bodyStart < 0 ? "" : rest[(bodyStart + 2)..].TrimEnd('\r', '\n');

        var kind = ChatKind.Message;
        int bits = 0, raiders = 0;
        string? systemText = null;

        if (command.Equals("PRIVMSG", StringComparison.Ordinal))
        {
            if (bodyStart < 0) return null;   // a PRIVMSG without text is malformed
            if (int.TryParse(Tag(tags, "bits"), out bits) && bits > 0) kind = ChatKind.Bits;
        }
        else if (command.Equals("USERNOTICE", StringComparison.Ordinal))
        {
            systemText = Tag(tags, "system-msg");
            kind = (Tag(tags, "msg-id") ?? "").ToLowerInvariant() switch
            {
                "raid" => ChatKind.Raid,
                "sub" or "resub" => ChatKind.Sub,
                "subgift" or "submysterygift" or "giftpaidupgrade" or "anongiftpaidupgrade"
                    => ChatKind.SubGift,
                "announcement" => ChatKind.Announcement,
                _ => ChatKind.Other
            };
            int.TryParse(Tag(tags, "msg-param-viewerCount"), out raiders);
            // Announcements carry their content in the body, not in system-msg.
            if (string.IsNullOrWhiteSpace(systemText)) systemText = text;
            if (string.IsNullOrWhiteSpace(systemText)) return null;   // nothing to show
        }
        else return null;

        string nick = prefix.Split('!')[0];
        // USERNOTICE has no user in the prefix — the login lives in the tags.
        string user = FirstNonEmpty(Tag(tags, "display-name"), Tag(tags, "login"), nick) ?? nick;
        var badges = ParseBadges(Tag(tags, "badges"));

        return new ChatMessage
        {
            User = user,
            Color = Tag(tags, "color") is { Length: > 0 } c && c.StartsWith('#') ? c : null,
            Text = text,
            IsMod = badges.Contains("moderator") || Tag(tags, "mod") == "1",
            IsSub = badges.Contains("subscriber") || Tag(tags, "subscriber") == "1",
            IsVip = badges.Contains("vip"),
            IsBroadcaster = badges.Contains("broadcaster"),
            Kind = kind,
            Bits = bits,
            SystemText = systemText,
            RaidViewers = raiders
        };
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values) if (!string.IsNullOrWhiteSpace(v)) return v;
        return null;
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
