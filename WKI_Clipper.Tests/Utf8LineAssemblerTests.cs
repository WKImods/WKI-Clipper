using System.Collections.Generic;
using System.Linq;
using System.Text;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// The socket hands over arbitrary byte counts, so both a line AND a single character can be
/// cut in half between two reads. The character case is the one that used to corrupt German
/// chat permanently, so it is pinned down here with a real split.
/// </summary>
public class Utf8LineAssemblerTests
{
    private static List<string> Feed(Utf8LineAssembler a, byte[] bytes, int from, int to)
    {
        var slice = bytes[from..to];
        return a.Append(slice, slice.Length).ToList();
    }

    [Fact]
    public void A_two_byte_character_split_across_chunks_survives()
    {
        var bytes = Encoding.UTF8.GetBytes("Schöne Grüße\r\n");
        // Cut between the two bytes of "ö" — the exact case that produced "Sch??ne".
        int oIndex = Encoding.UTF8.GetByteCount("Sch");
        var a = new Utf8LineAssembler();

        Assert.Empty(Feed(a, bytes, 0, oIndex + 1));          // ends mid-character
        var lines = Feed(a, bytes, oIndex + 1, bytes.Length);

        Assert.Equal(new[] { "Schöne Grüße" }, lines);
        Assert.DoesNotContain('�', lines[0]);
    }

    [Fact]
    public void A_four_byte_emoji_split_across_chunks_survives()
    {
        var bytes = Encoding.UTF8.GetBytes("gg 🎉 wp\n");
        int emojiStart = Encoding.UTF8.GetByteCount("gg ");
        var a = new Utf8LineAssembler();

        // Split in the middle of the 4-byte sequence, which decodes to a surrogate pair.
        Assert.Empty(Feed(a, bytes, 0, emojiStart + 2));
        var lines = Feed(a, bytes, emojiStart + 2, bytes.Length);

        Assert.Equal(new[] { "gg 🎉 wp" }, lines);
    }

    [Fact]
    public void Stateless_decoding_would_have_corrupted_it()
    {
        // Guards the reason this class exists: the previous implementation decoded each
        // chunk on its own, and that demonstrably loses the character.
        var bytes = Encoding.UTF8.GetBytes("schön\n");
        int cut = Encoding.UTF8.GetByteCount("sch") + 1;

        string naive = Encoding.UTF8.GetString(bytes, 0, cut)
                     + Encoding.UTF8.GetString(bytes, cut, bytes.Length - cut);
        Assert.Contains('�', naive);

        var a = new Utf8LineAssembler();
        Feed(a, bytes, 0, cut);
        Assert.Equal(new[] { "schön" }, Feed(a, bytes, cut, bytes.Length));
    }

    [Fact]
    public void A_line_split_across_chunks_is_assembled()
    {
        var a = new Utf8LineAssembler();
        var first = Encoding.UTF8.GetBytes("PING :tmi.tw");
        var second = Encoding.UTF8.GetBytes("itch.tv\r\n");

        Assert.Empty(a.Append(first, first.Length));
        Assert.Equal(new[] { "PING :tmi.twitch.tv" }, a.Append(second, second.Length).ToList());
    }

    [Fact]
    public void Several_lines_in_one_chunk_all_come_out_and_the_tail_is_held()
    {
        var a = new Utf8LineAssembler();
        var chunk = Encoding.UTF8.GetBytes("one\r\ntwo\r\nthree-incompl");

        var lines = a.Append(chunk, chunk.Length).ToList();
        Assert.Equal(new[] { "one", "two" }, lines);

        var rest = Encoding.UTF8.GetBytes("ete\r\n");
        Assert.Equal(new[] { "three-incomplete" }, a.Append(rest, rest.Length).ToList());
    }

    [Fact]
    public void Blank_lines_are_dropped_and_carriage_returns_trimmed()
    {
        var a = new Utf8LineAssembler();
        var chunk = Encoding.UTF8.GetBytes("a\r\n\r\n\r\nb\r\n");
        Assert.Equal(new[] { "a", "b" }, a.Append(chunk, chunk.Length).ToList());
    }

    [Fact]
    public void Empty_and_missing_input_are_harmless()
    {
        var a = new Utf8LineAssembler();
        Assert.Empty(a.Append(new byte[16], 0));
        Assert.Empty(a.Append(null!, 5));
        Assert.Empty(a.Append(new byte[4], -1));
    }

    [Fact]
    public void Byte_at_a_time_delivery_still_produces_the_right_line()
    {
        // The pathological case: the socket returns a single byte per call.
        var bytes = Encoding.UTF8.GetBytes("@tag=ä :nick!u@h PRIVMSG #c :grüß dich 🎮\r\n");
        var a = new Utf8LineAssembler();
        var collected = new List<string>();

        foreach (var b in bytes) collected.AddRange(a.Append(new[] { b }, 1));

        Assert.Equal(new[] { "@tag=ä :nick!u@h PRIVMSG #c :grüß dich 🎮" }, collected);
    }
}
