using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WKI_Clipper.Tests;

/// <summary>
/// Hand-assembled MP4 bytes for <c>Mp4Index</c> tests: only the boxes the index reads (no stco/stsc, a
/// dummy mdat), so every field of the parser can be driven from a test without ffmpeg.
/// </summary>
internal sealed class Mp4Builder
{
    public uint MovieTimescale = 1000;
    public uint MovieDuration = 10_000;
    public int MvhdVersion;
    public bool MoovFirst;                 // faststart layout
    public bool LargeSizeMdat;             // mdat with a 64-bit largesize header
    public bool LastBoxSizeZero;           // moov with size 0 (to end of file)
    public int Mp4TopLevelFiller;          // extra free boxes before moov
    public List<TrackSpec> Tracks { get; } = new();

    public sealed class TrackSpec
    {
        public string Handler = "vide";
        public string Codec = "avc1";
        public int Width = 1920, Height = 1080;
        public uint Timescale = 15360;
        public int MdhdVersion;
        public (uint Count, uint Delta)[] Stts = Array.Empty<(uint, uint)>();
        public (uint Count, int Offset)[]? Ctts;
        public int CttsVersion;
        public int[]? Stss;                        // 1-based sync samples, null = box omitted
        public uint UniformSize;                   // stsz sample_size != 0
        public uint[]? Sizes;                      // stsz table when UniformSize == 0
        public uint SampleCountOverride = uint.MaxValue;   // stsz count when it must differ from stts
        public (long SegmentDuration, long MediaTime, short RateInt)[]? Elst;
        public int ElstVersion;
    }

    public static TrackSpec Video(int frames, uint delta = 512, uint timescale = 15360, uint size = 1000)
        => new()
        {
            Handler = "vide",
            Timescale = timescale,
            Stts = new[] { ((uint)frames, delta) },
            UniformSize = size,
            Stss = null
        };

    public static TrackSpec Audio(int samples = 100, uint delta = 1024, uint timescale = 48000)
        => new()
        {
            Handler = "soun",
            Codec = "mp4a",
            Width = 0,
            Height = 0,
            Timescale = timescale,
            Stts = new[] { ((uint)samples, delta) },
            UniformSize = 300
        };

    // ---------------------------------------------------------------- assembly

    public byte[] Build()
    {
        var file = new MemoryStream();
        Write(file, Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(512), Encoding.ASCII.GetBytes("isomiso2avc1mp41")));

        var moov = MoovBytes();
        var mdatPayload = new byte[100];
        byte[] mdat = LargeSizeMdat
            ? Concat(U32(1), Encoding.ASCII.GetBytes("mdat"), U64(16 + (ulong)mdatPayload.Length), mdatPayload)
            : Box("mdat", mdatPayload);

        if (MoovFirst) Write(file, moov);
        for (int i = 0; i < Mp4TopLevelFiller; i++) Write(file, Box("free", new byte[4]));
        Write(file, mdat);
        if (!MoovFirst)
        {
            if (LastBoxSizeZero)
            {
                moov[0] = moov[1] = moov[2] = moov[3] = 0;   // size 0 = to the end of the file
            }
            Write(file, moov);
        }
        return file.ToArray();
    }

    private byte[] MoovBytes()
    {
        var parts = new List<byte[]> { Mvhd() };
        parts.AddRange(Tracks.Select(Trak));
        return Box("moov", parts.ToArray());
    }

    private byte[] Mvhd()
    {
        if (MvhdVersion == 0)
            return Box("mvhd", new byte[] { 0, 0, 0, 0 }, U32(0), U32(0), U32(MovieTimescale), U32(MovieDuration), new byte[80]);
        return Box("mvhd", new byte[] { 1, 0, 0, 0 }, U64(0), U64(0), U32(MovieTimescale), U64(MovieDuration), new byte[80]);
    }

    private static byte[] Trak(TrackSpec t)
    {
        var parts = new List<byte[]> { Box("tkhd", new byte[84]) };
        if (t.Elst is { } edits)
            parts.Add(Box("edts", Elst(t, edits)));

        var mdiaParts = new List<byte[]> { Mdhd(t), Hdlr(t) };
        mdiaParts.Add(Box("minf", Stbl(t)));
        parts.Add(Box("mdia", mdiaParts.ToArray()));
        return Box("trak", parts.ToArray());
    }

    private static byte[] Elst(TrackSpec t, (long SegmentDuration, long MediaTime, short RateInt)[] edits)
    {
        var body = new MemoryStream();
        Write(body, new byte[] { (byte)t.ElstVersion, 0, 0, 0 });
        Write(body, U32((uint)edits.Length));
        foreach (var (dur, mediaTime, rate) in edits)
        {
            if (t.ElstVersion == 0)
            {
                Write(body, U32((uint)dur));
                Write(body, U32(unchecked((uint)(int)mediaTime)));
            }
            else
            {
                Write(body, U64((ulong)dur));
                Write(body, U64(unchecked((ulong)mediaTime)));
            }
            Write(body, U16((ushort)rate));
            Write(body, U16(0));
        }
        return Box("elst", body.ToArray());
    }

    private static byte[] Mdhd(TrackSpec t)
        => t.MdhdVersion == 0
            ? Box("mdhd", new byte[] { 0, 0, 0, 0 }, U32(0), U32(0), U32(t.Timescale), U32(0), new byte[4])
            : Box("mdhd", new byte[] { 1, 0, 0, 0 }, U64(0), U64(0), U32(t.Timescale), U64(0), new byte[4]);

    private static byte[] Hdlr(TrackSpec t)
        => Box("hdlr", new byte[] { 0, 0, 0, 0 }, U32(0), Encoding.ASCII.GetBytes(t.Handler), new byte[12], new byte[] { 0 });

    private static byte[] Stbl(TrackSpec t)
    {
        var parts = new List<byte[]> { Stsd(t), Stts(t) };
        if (t.Ctts is { } ctts) parts.Add(Ctts(t, ctts));
        if (t.Stss is { } stss) parts.Add(Stss(stss));
        parts.Add(Stsz(t));
        return Box("stbl", parts.ToArray());
    }

    private static byte[] Stsd(TrackSpec t)
    {
        // VisualSampleEntry / generic entry: size, format, 6 reserved, data_reference_index, then 16 bytes, width, height ...
        var entry = new MemoryStream();
        Write(entry, new byte[6]);
        Write(entry, U16(1));
        Write(entry, new byte[16]);
        Write(entry, U16((ushort)t.Width));
        Write(entry, U16((ushort)t.Height));
        Write(entry, new byte[40]);
        var entryBody = entry.ToArray();
        var sampleEntry = Box(t.Codec, entryBody);
        return Box("stsd", new byte[] { 0, 0, 0, 0 }, U32(1), sampleEntry);
    }

    private static byte[] Stts(TrackSpec t)
    {
        var body = new MemoryStream();
        Write(body, new byte[] { 0, 0, 0, 0 });
        Write(body, U32((uint)t.Stts.Length));
        foreach (var (count, delta) in t.Stts) { Write(body, U32(count)); Write(body, U32(delta)); }
        return Box("stts", body.ToArray());
    }

    private static byte[] Ctts(TrackSpec t, (uint Count, int Offset)[] entries)
    {
        var body = new MemoryStream();
        Write(body, new byte[] { (byte)t.CttsVersion, 0, 0, 0 });
        Write(body, U32((uint)entries.Length));
        foreach (var (count, offset) in entries) { Write(body, U32(count)); Write(body, U32(unchecked((uint)offset))); }
        return Box("ctts", body.ToArray());
    }

    private static byte[] Stss(int[] samples)
    {
        var body = new MemoryStream();
        Write(body, new byte[] { 0, 0, 0, 0 });
        Write(body, U32((uint)samples.Length));
        foreach (var s in samples) Write(body, U32((uint)s));
        return Box("stss", body.ToArray());
    }

    private static byte[] Stsz(TrackSpec t)
    {
        long sttsCount = t.Stts.Sum(e => (long)e.Count);
        uint count = t.SampleCountOverride != uint.MaxValue
            ? t.SampleCountOverride
            : t.Sizes is { } sizes ? (uint)sizes.Length : (uint)sttsCount;
        var body = new MemoryStream();
        Write(body, new byte[] { 0, 0, 0, 0 });
        Write(body, U32(t.Sizes is null ? t.UniformSize : 0));
        Write(body, U32(count));
        if (t.Sizes is { } table) foreach (var s in table) Write(body, U32(s));
        return Box("stsz", body.ToArray());
    }

    // ---------------------------------------------------------------- primitives

    public static byte[] Box(string type, params byte[][] payload)
    {
        var body = Concat(payload);
        return Concat(U32((uint)(8 + body.Length)), Encoding.ASCII.GetBytes(type), body);
    }

    public static byte[] U16(ushort v) => new[] { (byte)(v >> 8), (byte)v };
    public static byte[] U32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    public static byte[] U64(ulong v) => Concat(U32((uint)(v >> 32)), U32((uint)v));

    public static byte[] Concat(params byte[][] parts)
    {
        var all = new byte[parts.Sum(p => p.Length)];
        int at = 0;
        foreach (var p in parts) { Buffer.BlockCopy(p, 0, all, at, p.Length); at += p.Length; }
        return all;
    }

    private static void Write(Stream s, byte[] bytes) => s.Write(bytes, 0, bytes.Length);
}
