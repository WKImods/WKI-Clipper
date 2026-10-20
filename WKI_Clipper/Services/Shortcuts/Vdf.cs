using System;
using System.Collections.Generic;
using System.Text;

namespace WKI_Clipper.Services;

/// <summary>
/// Minimal Valve KeyValues reader (libraryfolders.vdf, appmanifest_*.acf). A block becomes a
/// <c>Dictionary&lt;string, object&gt;</c> whose values are strings or nested dictionaries; keys are
/// case-insensitive (Steam writes "AppState"/"appid" in mixed case). Never throws: a truncated or
/// garbled file yields whatever was complete before the damage, because Steam rewrites these files
/// while the clipper may be reading them.
/// </summary>
public static class Vdf
{
    // Deeper than any real manifest; stops a pathological file from exhausting the stack.
    private const int MaxDepth = 64;

    private enum Tok { End, Open, Close, Text }

    public static Dictionary<string, object> Parse(string text)
    {
        var root = NewBlock();
        if (string.IsNullOrEmpty(text)) return root;
        int pos = 0;
        ParseBlock(text, ref pos, root, depth: 0);
        return root;
    }

    private static Dictionary<string, object> NewBlock() => new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fills <paramref name="block"/> until its closing brace (or the end of the text at depth 0).</summary>
    private static void ParseBlock(string text, ref int pos, Dictionary<string, object> block, int depth)
    {
        while (true)
        {
            var kind = Next(text, ref pos, out var key);
            switch (kind)
            {
                case Tok.End:
                    return;
                case Tok.Close:
                    if (depth == 0) continue;   // stray brace at the top level: ignore it
                    return;
                case Tok.Open:
                    // A block without a name: skip it (parse into a throw-away block).
                    if (depth + 1 >= MaxDepth) { pos = text.Length; return; }
                    ParseBlock(text, ref pos, NewBlock(), depth + 1);
                    continue;
            }

            // key is a Text token; the next token decides between "key" "value" and "key" { ... }
            var valueKind = Next(text, ref pos, out var value);
            switch (valueKind)
            {
                case Tok.Text:
                    block[key] = value;
                    break;
                case Tok.Open:
                    if (depth + 1 >= MaxDepth) { pos = text.Length; return; }
                    var child = NewBlock();
                    block[key] = child;
                    ParseBlock(text, ref pos, child, depth + 1);
                    break;
                case Tok.Close:
                    // "key" followed by the end of its block: the key has no value.
                    if (depth == 0) continue;
                    return;
                default:
                    return;
            }
        }
    }

    /// <summary>Next token: a quoted or bare string, a brace, or the end. Whitespace and // comments are skipped.</summary>
    private static Tok Next(string s, ref int i, out string value)
    {
        value = "";
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c) || c == '\uFEFF') { i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            break;
        }
        if (i >= s.Length) return Tok.End;

        char ch = s[i];
        if (ch == '{') { i++; return Tok.Open; }
        if (ch == '}') { i++; return Tok.Close; }

        var sb = new StringBuilder();
        if (ch == '"')
        {
            i++;
            while (i < s.Length && s[i] != '"')
            {
                char q = s[i];
                if (q == '\\' && i + 1 < s.Length)
                {
                    char e = s[i + 1];
                    switch (e)
                    {
                        case '\\': sb.Append('\\'); i += 2; continue;
                        case '"':  sb.Append('"');  i += 2; continue;
                        case 'n':  sb.Append('\n'); i += 2; continue;
                        case 't':  sb.Append('\t'); i += 2; continue;
                        default:   sb.Append('\\'); i++;    continue;   // old files: "D:\SteamLibrary" keeps its backslash
                    }
                }
                sb.Append(q);
                i++;
            }
            if (i < s.Length) i++;   // closing quote (missing in a truncated file)
            value = sb.ToString();
            return Tok.Text;
        }

        while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '"' && s[i] != '{' && s[i] != '}')
        {
            sb.Append(s[i]);
            i++;
        }
        value = sb.ToString();
        return Tok.Text;
    }

    // ---- lookups used by the readers ----

    internal static string? Str(Dictionary<string, object>? block, string key)
        => block != null && block.TryGetValue(key, out var v) && v is string s ? s : null;

    internal static Dictionary<string, object>? Block(Dictionary<string, object>? block, string key)
        => block != null && block.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;
}
