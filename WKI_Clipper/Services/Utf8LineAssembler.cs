using System;
using System.Collections.Generic;
using System.Text;

namespace WKI_Clipper.Services;

/// <summary>
/// Turns a stream of raw socket byte chunks into complete text lines.
///
/// Two things must survive a chunk boundary, and only one of them is obvious. A LINE can be
/// cut in half — that is what the pending buffer is for. But a multi-byte UTF-8 CHARACTER
/// can be cut in half too: the socket hands over whatever bytes have arrived, so "ö" or an
/// emote regularly straddles two receives. Decoding each chunk on its own would turn both
/// halves into U+FFFD, and since nothing revisits the raw bytes that damage is permanent.
/// A stateful <see cref="Decoder"/> carries the partial sequence into the next chunk.
/// </summary>
public sealed class Utf8LineAssembler
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _pending = new();
    private char[] _chars = Array.Empty<char>();

    /// <summary>
    /// Feeds one chunk and returns the lines it completed (without their line endings).
    /// An incomplete trailing line is kept for the next call.
    /// </summary>
    public IReadOnlyList<string> Append(byte[] bytes, int count)
    {
        var lines = new List<string>();
        if (bytes is null || count <= 0) return lines;

        int max = Encoding.UTF8.GetMaxCharCount(count);
        if (_chars.Length < max) _chars = new char[max];

        int decoded = _decoder.GetChars(bytes, 0, count, _chars, 0);
        _pending.Append(_chars, 0, decoded);

        string all = _pending.ToString();
        int lastNl = all.LastIndexOf('\n');
        if (lastNl < 0) return lines;          // nothing complete yet

        _pending.Clear();
        _pending.Append(all, lastNl + 1, all.Length - lastNl - 1);

        // Split first, trim the CR, and only then decide a line is empty — "\r\n\r\n" would
        // otherwise slip an empty entry through, since "\r" is not an empty string.
        foreach (var line in all[..lastNl].Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length > 0) lines.Add(trimmed);
        }
        return lines;
    }
}
