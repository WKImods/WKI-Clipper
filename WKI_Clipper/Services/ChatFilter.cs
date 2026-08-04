using System;

namespace WKI_Clipper.Services;

/// <summary>
/// Pure text checks for the chat widget. Kept out of the view so the fiddly matching
/// rules can be unit-tested instead of eyeballed against a live chat.
/// </summary>
public static class ChatFilter
{
    /// <summary>
    /// True when <paramref name="text"/> addresses <paramref name="name"/>. Matches with or
    /// without the leading '@' and ignores case, but only on a whole-name boundary — a
    /// viewer typing "oskar_blitzz" or "notoskar_blitz" is not talking to you.
    /// </summary>
    public static bool IsMention(string? text, string? name)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(name)) return false;

        string n = name.Trim().TrimStart('@');
        if (n.Length == 0 || n.Length > text.Length) return false;

        int from = 0;
        while (from <= text.Length - n.Length)
        {
            int at = text.IndexOf(n, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;

            // Twitch logins are [A-Za-z0-9_], so anything else counts as a boundary.
            // '@' is not a name character, which makes "@name" match without a special case.
            bool leftFree = at == 0 || !IsNameChar(text[at - 1]);
            int end = at + n.Length;
            bool rightFree = end >= text.Length || !IsNameChar(text[end]);
            if (leftFree && rightFree) return true;

            from = at + 1;
        }
        return false;
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
