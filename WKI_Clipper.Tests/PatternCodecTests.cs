using System;
using System.Collections.Generic;
using System.IO;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Mouse-patterns design 5.2: the compact, versioned sample encoding and everything a corrupt file can do to it.</summary>
public sealed class PatternCodecTests
{
    private static PatternTrack Track(params (int At, int Dx, int Dy)[] bins)
    {
        var at = new int[bins.Length]; var dx = new int[bins.Length]; var dy = new int[bins.Length];
        for (int i = 0; i < bins.Length; i++) (at[i], dx[i], dy[i]) = bins[i];
        return new PatternTrack(at, dx, dy);
    }

    private static PatternData Sample() => new(
        Track((0, 3, -1), (1, 5, 0), (9, -12, 40), (1500, 0, -2), (60_000, int.MaxValue, int.MinValue)),
        Track((0, 1, 1), (8, -300, 7), (2000, 2, 0)),
        60_000);

    private static void AssertSame(PatternTrack expected, PatternTrack actual)
    {
        Assert.Equal(expected.AtMs, actual.AtMs);
        Assert.Equal(expected.Dx, actual.Dx);
        Assert.Equal(expected.Dy, actual.Dy);
    }

    private static string Payload(Action<BinaryWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms)) write(w);
        return PatternCodec.Prefix + Convert.ToBase64String(ms.ToArray());
    }

    private static void ZigZag(BinaryWriter w, long v)
    {
        ulong u = (ulong)((v << 1) ^ (v >> 63));
        do
        {
            byte b = (byte)(u & 0x7F);
            u >>= 7;
            w.Write(u != 0 ? (byte)(b | 0x80) : b);
        } while (u != 0);
    }

    /// <summary>A hand-built payload in the format the codec writes: duration, then per track a count and (time delta, dx, dy) triples.</summary>
    private static string Build(double duration, params (long At, long Dx, long Dy)[][] tracks)
        => Payload(w =>
        {
            w.Write(duration);
            foreach (var t in tracks)
            {
                ZigZag(w, t.Length);
                long last = 0;
                for (int i = 0; i < t.Length; i++)
                {
                    ZigZag(w, i == 0 ? t[i].At : t[i].At - last);
                    last = t[i].At;
                    ZigZag(w, t[i].Dx);
                    ZigZag(w, t[i].Dy);
                }
            }
        });

    [Fact]
    public void Codec_round_trips_both_tracks()
    {
        var data = Sample();
        var text = PatternCodec.Encode(data);

        Assert.StartsWith(PatternCodec.Prefix, text);
        Assert.True(PatternCodec.TryDecode(text, out var back));
        AssertSame(data.Raw, back!.Raw);
        AssertSame(data.Pointer, back.Pointer);
        Assert.Equal(data.DurationMs, back.DurationMs);
        Assert.Equal(data.Raw.Total, back.Raw.Total);
    }

    [Fact]
    public void Empty_tracks_round_trip()
    {
        var data = new PatternData(PatternTrack.Empty, Track((4, 1, 0)), 4.5);
        Assert.True(PatternCodec.TryDecode(PatternCodec.Encode(data), out var back));
        Assert.Equal(0, back!.Raw.Count);
        AssertSame(data.Pointer, back.Pointer);
        Assert.Equal(4.5, back.DurationMs);
    }

    [Fact]
    public void A_hand_built_payload_decodes_so_the_format_is_what_the_tests_assume()
    {
        Assert.True(PatternCodec.TryDecode(Build(10, new[] { (2L, 1L, -1L), (10L, 0L, 5L) }, Array.Empty<(long, long, long)>()), out var d));
        AssertSame(Track((2, 1, -1), (10, 0, 5)), d!.Raw);
        Assert.Equal(0, d.Pointer.Count);
    }

    [Fact]
    public void A_full_sixty_second_track_stays_well_below_the_line_limit()
    {
        var n = MousePatternRules.MaxDurationMs + 1;
        var at = new int[n]; var dx = new int[n]; var dy = new int[n];
        for (int i = 0; i < n; i++) { at[i] = i; dx[i] = 1000 - i % 2000; dy[i] = i % 7 == 0 ? 3 : -2; }
        var track = new PatternTrack(at, dx, dy);
        var text = PatternCodec.Encode(new PatternData(track, track, MousePatternRules.MaxDurationMs));

        Assert.True(text.Length < 2 * 1024 * 1024, $"{text.Length} chars");
        Assert.True(PatternCodec.TryDecode(text, out var back));
        AssertSame(track, back!.Pointer);
    }

    [Fact]
    public void Track_totals_sum_in_64_bit()
    {
        var t = Track((0, int.MaxValue, -5), (1, int.MaxValue, -5));
        Assert.Equal(2, t.Count);
        Assert.Equal(((long)int.MaxValue * 2, -10L), t.Total);
        Assert.Equal((0L, 0L), PatternTrack.Empty.Total);
    }

    public static IEnumerable<object?[]> Tampered()
    {
        var good = PatternCodec.Encode(Sample());
        yield return new object?[] { "null", null };
        yield return new object?[] { "empty", "" };
        yield return new object?[] { "no prefix", good[PatternCodec.Prefix.Length..] };
        yield return new object?[] { "wrong version", "p2:" + good[PatternCodec.Prefix.Length..] };
        yield return new object?[] { "bad base64", PatternCodec.Prefix + "!!not base64!!" };
        yield return new object?[] { "truncated", Resize(good, -7) };
        yield return new object?[] { "cut after the duration", Payload(w => w.Write(5.0)) };
        yield return new object?[] { "short duration", Payload(w => w.Write(new byte[] { 1, 2, 3 })) };
        yield return new object?[] { "trailing bytes", Resize(Build(5, new[] { (1L, 1L, 1L) }, new[] { (1L, 1L, 1L) }), +1) };
        yield return new object?[] { "second track missing", Build(5, new[] { (1L, 1L, 1L) }) };
        yield return new object?[] { "count longer than the data", Payload(w => { w.Write(5.0); ZigZag(w, 3); ZigZag(w, 1); ZigZag(w, 1); ZigZag(w, 1); ZigZag(w, 0); }) };
        yield return new object?[] { "non-increasing times", Build(5, new[] { (3L, 1L, 1L), (3L, 1L, 1L) }, Array.Empty<(long, long, long)>()) };
        yield return new object?[] { "decreasing times", Build(5, new[] { (3L, 1L, 1L), (2L, 1L, 1L) }, Array.Empty<(long, long, long)>()) };
        yield return new object?[] { "negative first time", Build(5, new[] { (-1L, 1L, 1L) }, Array.Empty<(long, long, long)>()) };
        yield return new object?[] { "time beyond 60 s", Build(5,new[] { (60_001L, 1L, 1L) }, Array.Empty<(long, long, long)>()) };
        yield return new object?[] { "duration beyond 60 s", Build(60_000.5, new[] { (1L, 1L, 1L) }, Array.Empty<(long, long, long)>()) };
        yield return new object?[] { "NaN duration", Build(double.NaN, Array.Empty<(long, long, long)>(), Array.Empty<(long, long, long)>()) };
        yield return new object?[] { "negative duration", Build(-1, Array.Empty<(long, long, long)>(), Array.Empty<(long, long, long)>()) };
        yield return new object?[] { "too many bins", Payload(w => { w.Write(5.0); ZigZag(w, PatternCodec.MaxBins + 1); }) };
        yield return new object?[] { "negative count", Payload(w => { w.Write(5.0); ZigZag(w, -1); }) };
        yield return new object?[] { "zero-motion bin", Build(5, new[] { (1L, 0L, 0L) }, Array.Empty<(long, long, long)>()) };
        yield return new object?[] { "dx beyond int", Build(5, new[] { (1L, (long)int.MaxValue + 1, 1L) }, Array.Empty<(long, long, long)>()) };
        yield return new object?[] { "overlong varint", Payload(w => { w.Write(5.0); w.Write(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 }); }) };
    }

    /// <summary>The same payload with bytes cut off the end (negative) or zero bytes appended (positive), still valid base64.</summary>
    private static string Resize(string encoded, int by)
    {
        var bytes = Convert.FromBase64String(encoded[PatternCodec.Prefix.Length..]);
        Array.Resize(ref bytes, bytes.Length + by);
        return PatternCodec.Prefix + Convert.ToBase64String(bytes);
    }

    [Theory]
    [MemberData(nameof(Tampered))]
    public void Codec_rejects_tampered_input(string what, string? text)
    {
        Assert.False(PatternCodec.TryDecode(text, out var data), what);
        Assert.Null(data);
    }

    [Fact]
    public void An_oversized_string_is_rejected_without_decoding()
        => Assert.False(PatternCodec.TryDecode(PatternCodec.Prefix + new string('A', PatternCodec.MaxEncodedLength), out _));

    [Fact]
    public void Encode_refuses_malformed_tracks_and_drops_zero_motion_bins()
    {
        Assert.Throws<ArgumentException>(() => PatternCodec.Encode(new PatternData(new PatternTrack(new[] { 1, 2 }, new[] { 1 }, new[] { 1, 1 }), PatternTrack.Empty, 5)));
        Assert.Throws<ArgumentException>(() => PatternCodec.Encode(new PatternData(Track((2, 1, 1), (2, 1, 1)), PatternTrack.Empty, 5)));
        Assert.Throws<ArgumentException>(() => PatternCodec.Encode(new PatternData(Track((-1, 1, 1)), PatternTrack.Empty, 5)));
        Assert.Throws<ArgumentException>(() => PatternCodec.Encode(new PatternData(PatternTrack.Empty, PatternTrack.Empty, double.NaN)));
        Assert.Throws<ArgumentException>(() => PatternCodec.Encode(new PatternData(Track((60_001, 1, 1)), PatternTrack.Empty, 60_000)));

        Assert.True(PatternCodec.TryDecode(PatternCodec.Encode(new PatternData(Track((1, 0, 0), (2, 1, 0)), PatternTrack.Empty, 2)), out var d));
        AssertSame(Track((2, 1, 0)), d!.Raw);
    }
}
