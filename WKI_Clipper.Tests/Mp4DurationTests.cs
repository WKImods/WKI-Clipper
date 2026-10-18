using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>The mvhd reader, against hand-built boxes (no ffmpeg needed).</summary>
public sealed class Mp4DurationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_mp4dur_" + Guid.NewGuid().ToString("N"));

    public Mp4DurationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- box builders ----

    private static byte[] BE32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    private static byte[] BE64(ulong v) => BE32((uint)(v >> 32)).Concat(BE32((uint)v)).ToArray();

    private static byte[] Box(string type, params byte[][] payload)
    {
        var body = payload.SelectMany(p => p).ToArray();
        return BE32((uint)(8 + body.Length)).Concat(Encoding.ASCII.GetBytes(type)).Concat(body).ToArray();
    }

    private static byte[] MvhdV0(uint timescale, uint duration)
        => Box("mvhd", new byte[] { 0, 0, 0, 0 }, BE32(0), BE32(0), BE32(timescale), BE32(duration), new byte[80]);

    private static byte[] MvhdV1(uint timescale, ulong duration)
        => Box("mvhd", new byte[] { 1, 0, 0, 0 }, BE64(0), BE64(0), BE32(timescale), BE64(duration), new byte[80]);

    private static byte[] Ftyp() => Box("ftyp", Encoding.ASCII.GetBytes("isom"), BE32(512), Encoding.ASCII.GetBytes("isomiso2"));

    private static byte[] LargeMdat(int payloadLength)
        => BE32(1).Concat(Encoding.ASCII.GetBytes("mdat")).Concat(BE64((ulong)(16 + payloadLength))).Concat(new byte[payloadLength]).ToArray();

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static bool Read(byte[] bytes, out TimeSpan d)
    {
        using var ms = new MemoryStream(bytes);
        return Mp4Duration.TryRead(ms, out d);
    }

    // ---- tests ----

    [Fact]
    public void Version0_duration_is_read_exactly()
    {
        var file = Concat(Ftyp(), Box("mdat", new byte[100]), Box("moov", MvhdV0(1000, 5000)));
        Assert.True(Read(file, out var d));
        Assert.Equal(TimeSpan.FromSeconds(5), d);
    }

    [Fact]
    public void Version1_duration_is_read_exactly()
    {
        var file = Concat(Ftyp(), Box("moov", MvhdV1(90000, 90000UL * 5 + 450)));
        Assert.True(Read(file, out var d));
        Assert.Equal(TimeSpan.FromSeconds(5.005), d);
    }

    [Fact]
    public void A_non_millisecond_timescale_is_converted_without_rounding_to_milliseconds()
    {
        var file = Concat(Ftyp(), Box("moov", MvhdV0(48000, 48000 * 5 + 416)));   // 5.008666... s
        Assert.True(Read(file, out var d));
        Assert.Equal(5.0086666, d.TotalSeconds, 5);
    }

    [Fact]
    public void A_large_mdat_with_a_64_bit_size_is_skipped()
    {
        var file = Concat(Ftyp(), LargeMdat(5000), Box("moov", MvhdV0(1000, 4990)));
        Assert.True(Read(file, out var d));
        Assert.Equal(TimeSpan.FromMilliseconds(4990), d);
    }

    [Fact]
    public void Moov_before_mdat_works_too()
    {
        var file = Concat(Ftyp(), Box("moov", MvhdV0(1000, 1234)), Box("mdat", new byte[10]));
        Assert.True(Read(file, out var d));
        Assert.Equal(TimeSpan.FromMilliseconds(1234), d);
    }

    [Fact]
    public void Other_boxes_inside_moov_before_the_header_are_skipped()
    {
        var file = Concat(Ftyp(), Box("moov", Box("free", new byte[20]), MvhdV0(1000, 777)));
        Assert.True(Read(file, out var d));
        Assert.Equal(TimeSpan.FromMilliseconds(777), d);
    }

    [Fact]
    public void A_segment_still_being_written_has_no_moov()
    {
        // ffmpeg writes ftyp and an mdat of unknown size (0 = to end of file) until the segment closes.
        var open = Concat(Ftyp(), BE32(0), Encoding.ASCII.GetBytes("mdat"), new byte[500]);
        Assert.False(Read(open, out _));
    }

    [Fact]
    public void No_moov_at_all_is_false()
        => Assert.False(Read(Concat(Ftyp(), Box("mdat", new byte[10])), out _));

    [Fact]
    public void Moov_without_mvhd_is_false()
        => Assert.False(Read(Concat(Ftyp(), Box("moov", Box("free", new byte[10]))), out _));

    [Fact]
    public void A_box_smaller_than_its_own_header_is_false()
    {
        var bad = Concat(Ftyp(), BE32(4), Encoding.ASCII.GetBytes("junk"), new byte[20]);
        Assert.False(Read(bad, out _));
    }

    [Fact]
    public void A_box_that_claims_to_end_past_the_file_is_false()
    {
        var bad = Concat(Ftyp(), BE32(10_000), Encoding.ASCII.GetBytes("mdat"), new byte[20]);
        Assert.False(Read(bad, out _));
    }

    [Fact]
    public void A_truncated_moov_is_false()
    {
        var moov = Box("moov", MvhdV0(1000, 5000));
        var truncated = Concat(Ftyp(), moov.Take(moov.Length - 40).ToArray());
        Assert.False(Read(truncated, out _));
    }

    [Fact]
    public void Timescale_zero_is_false()
        => Assert.False(Read(Concat(Ftyp(), Box("moov", MvhdV0(0, 5000))), out _));

    [Fact]
    public void All_ones_duration_is_false_for_both_versions()
    {
        Assert.False(Read(Concat(Ftyp(), Box("moov", MvhdV0(1000, uint.MaxValue))), out _));
        Assert.False(Read(Concat(Ftyp(), Box("moov", MvhdV1(1000, ulong.MaxValue))), out _));
    }

    [Fact]
    public void An_unknown_mvhd_version_is_false()
    {
        var mvhd = Box("mvhd", new byte[] { 7, 0, 0, 0 }, new byte[100]);
        Assert.False(Read(Concat(Ftyp(), Box("moov", mvhd)), out _));
    }

    [Fact]
    public void More_than_64_top_level_boxes_is_false()
    {
        var parts = new List<byte[]> { Ftyp() };
        for (int i = 0; i < 70; i++) parts.Add(Box("free", new byte[4]));
        parts.Add(Box("moov", MvhdV0(1000, 5000)));
        Assert.False(Read(parts.SelectMany(p => p).ToArray(), out _));
    }

    [Fact]
    public void Empty_and_tiny_files_are_false()
    {
        Assert.False(Read(Array.Empty<byte>(), out _));
        Assert.False(Read(new byte[] { 0, 0, 0 }, out _));
    }

    [Fact]
    public void Garbage_never_throws()
    {
        var rnd = new Random(42);
        for (int i = 0; i < 200; i++)
        {
            var junk = new byte[rnd.Next(0, 300)];
            rnd.NextBytes(junk);
            Read(junk, out _);   // must simply return
        }
    }

    [Fact]
    public void Reads_a_file_that_another_process_still_has_open()
    {
        var path = Path.Combine(_dir, "seg_0_00.mp4");
        File.WriteAllBytes(path, Concat(Ftyp(), Box("moov", MvhdV0(1000, 5008))));
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        Assert.True(Mp4Duration.TryRead(path, out var d));
        Assert.Equal(TimeSpan.FromMilliseconds(5008), d);
    }

    [Fact]
    public void A_missing_file_is_false()
        => Assert.False(Mp4Duration.TryRead(Path.Combine(_dir, "nope.mp4"), out _));
}
