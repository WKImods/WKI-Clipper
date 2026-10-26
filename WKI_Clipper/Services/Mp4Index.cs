using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WKI_Clipper.Services;

/// <summary>What the clip editor needs to know about the video track of an MP4, without ffprobe.</summary>
/// <param name="Codec">Sample-entry FourCC of the video track: "avc1", "avc3", "hvc1", "hev1", "av01", ...</param>
/// <param name="PreviewSeconds">Per frame in display order: the raw composition time (stts + ctts) / timescale WITHOUT any
/// edit list — exactly the time axis of WPF's MediaElement (measured).</param>
/// <param name="FileSeconds">Per frame: ffmpeg's time axis (edit list applied, minus the file start) — what -ss / -t mean.</param>
/// <param name="Keyframes">Ascending frame indexes (display order) of the sync samples; without an stss box: all of them.</param>
/// <param name="HasBFrames">Display order differs from decode order.</param>
/// <param name="VideoBytes">Sum of the video sample sizes (stsz) — the exact video bitrate without ffprobe.</param>
/// <param name="VideoEndSeconds">Where the last video frame ends on the <see cref="FileSeconds"/> axis: the last frame in
/// display order plus its own stts duration. Unlike the movie duration (the longer one of video and audio) it ignores
/// an audio track that runs past the last frame; it is the point where the next file of a concat must start.</param>
public sealed record Mp4VideoIndex(
    string Codec, int Width, int Height,
    double[] PreviewSeconds, double[] FileSeconds, int[] Keyframes,
    bool HasBFrames, long VideoBytes, int AudioTrackCount, double MovieDurationSeconds, double VideoEndSeconds)
{
    public int FrameCount => PreviewSeconds.Length;
}

/// <summary>
/// Reads the sample tables of an MP4's video track (<c>moov/trak/mdia/minf/stbl</c>: stts, ctts, stss, stsz) and
/// its edit list. A 30-minute clip costs ~30 ms and ~3.7 MB of reading (an ffmpeg keyframe pass reads 3 GB and
/// needs 3-7 s). The two time axes it returns are the heart of the clip editor: the preview (Media Foundation)
/// ignores edit lists, ffmpeg applies them — without both, cuts are off by up to half a second.
///
/// Anything it does not fully understand (fragmented files, several real edits, a playback rate other than 1)
/// gives null, never a guess.
/// </summary>
public static class Mp4Index
{
    private const int MaxTopLevelBoxes = 64;
    private const long MaxMoovBytes = 128L * 1024 * 1024;
    private const int MaxSamples = 10_000_000;
    private const int MaxBoxesPerLevel = 100_000;

    /// <summary>
    /// Reads the box headers up to moov and then moov itself. Opens with FileShare.ReadWrite | Delete (the clip may be
    /// open in the preview). Null for: no moov, no video track, empty or inconsistent tables, an unsupported edit
    /// list, limits exceeded. Never throws.
    /// </summary>
    public static Mp4VideoIndex? TryRead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, FileOptions.None);
            return TryRead(fs);
        }
        catch { return null; }
    }

    internal static Mp4VideoIndex? TryRead(Stream stream)
    {
        try
        {
            long length = stream.Length;
            long pos = 0;
            for (int boxes = 0; pos + 8 <= length; boxes++)
            {
                if (boxes >= MaxTopLevelBoxes) return null;
                if (!ReadHeader(stream, pos, length, out var type, out long headerSize, out long size)) return null;
                if (type == "moov")
                {
                    long payload = size - headerSize;
                    if (payload < 8 || payload > MaxMoovBytes) return null;
                    var moov = new byte[payload];
                    stream.Position = pos + headerSize;
                    if (!ReadFully(stream, moov)) return null;
                    return ParseMoov(moov);
                }
                pos += size;
            }
            return null;
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- top level (stream)

    private static bool ReadHeader(Stream s, long pos, long limit, out string type, out long headerSize, out long size)
    {
        type = "";
        headerSize = 8;
        size = 0;
        s.Position = pos;
        var h = new byte[8];
        if (!ReadFully(s, h)) return false;
        long size32 = U32(h, 0);
        type = Encoding.Latin1.GetString(h, 4, 4);

        if (size32 == 1)
        {
            var big = new byte[8];
            if (!ReadFully(s, big)) return false;
            ulong large = U64(big, 0);
            if (large > long.MaxValue) return false;
            headerSize = 16;
            size = (long)large;
        }
        else if (size32 == 0) size = limit - pos;   // to the end of the file
        else size = size32;

        return size >= headerSize && pos + size <= limit;
    }

    private static bool ReadFully(Stream s, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = s.Read(buffer, read, buffer.Length - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    // ---------------------------------------------------------------- moov (in memory)

    /// <summary>A box inside the moov buffer; <see cref="Start"/>..<see cref="End"/> is its payload.</summary>
    private readonly record struct Box(string Type, int Start, int End);

    private sealed class Track
    {
        public string Handler = "";
        public string Codec = "";
        public int Width, Height;
        public uint Timescale;
        public List<(long Duration, long MediaTime, int RateInt, int RateFrac)> Edits = new();
        public long SampleCount;

        // video only
        public long[] Dts = Array.Empty<long>();
        public long LastSampleDuration;      // stts delta of the last sample in decode order (no next dts to subtract)
        public long[]? CttsOffsets;
        public int[]? SyncSamples;
        public long VideoBytes;
    }

    private static Mp4VideoIndex? ParseMoov(byte[] b)
    {
        uint movieTs = 0;
        double movieDuration = 0;
        var tracks = new List<Track>();

        foreach (var child in Children(b, 0, b.Length))
        {
            if (child.Type == "mvhd")
            {
                int v = b[child.Start];
                if (v == 0) { movieTs = U32(b, child.Start + 12); movieDuration = movieTs == 0 ? 0 : U32(b, child.Start + 16) / (double)movieTs; }
                else if (v == 1) { movieTs = U32(b, child.Start + 20); movieDuration = movieTs == 0 ? 0 : U64(b, child.Start + 24) / (double)movieTs; }
                else return null;
            }
            else if (child.Type == "trak")
            {
                var track = ParseTrak(b, child);
                if (track is null) return null;
                tracks.Add(track);
            }
        }

        // Edit lists need the movie timescale, which normally precedes the tracks but need not.
        var video = tracks.Find(t => t.Handler == "vide");
        if (video is null || video.SampleCount <= 0 || video.Timescale == 0) return null;

        double fileStart = double.MaxValue;
        long videoLeer = 0, videoMediaTime = 0;
        foreach (var t in tracks)
        {
            if (t.SampleCount <= 0 || t.Timescale == 0) continue;
            if (!ResolveEdits(t, movieTs, out long leer, out long media)) return null;
            if (t == video) { videoLeer = leer; videoMediaTime = media; }
            else fileStart = Math.Min(fileStart, leer / (double)t.Timescale);
        }

        int n = (int)video.SampleCount;

        // composition time per sample in decode order
        var cts = new long[n];
        for (int i = 0; i < n; i++) cts[i] = video.Dts[i] + (video.CttsOffsets is { } o ? o[i] : 0);

        // display order: sorted by composition time (stable). Most streams are already in order.
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        bool reordered = false;
        for (int i = 1; i < n; i++)
            if (cts[i] < cts[i - 1]) { reordered = true; break; }
        if (reordered)
            Array.Sort(order, (x, y) => cts[x] != cts[y] ? cts[x].CompareTo(cts[y]) : x.CompareTo(y));

        double ts = video.Timescale;
        var preview = new double[n];
        var pts = new double[n];
        var rank = new int[n];
        for (int k = 0; k < n; k++)
        {
            long c = cts[order[k]];
            rank[order[k]] = k;
            preview[k] = c / ts;
            pts[k] = (c - videoMediaTime + videoLeer) / ts;
        }
        fileStart = Math.Min(fileStart, pts[0]);

        var file = new double[n];
        for (int k = 0; k < n; k++) file[k] = pts[k] - fileStart;

        // The last frame on screen ends at its own time plus its own stts delta. With B-frames that is not necessarily
        // the last sample in decode order.
        int lastDecoded = order[n - 1];
        long lastTicks = lastDecoded + 1 < n ? video.Dts[lastDecoded + 1] - video.Dts[lastDecoded] : video.LastSampleDuration;
        double videoEnd = file[n - 1] + lastTicks / ts;

        int[] keyframes;
        if (video.SyncSamples is null)
        {
            keyframes = new int[n];
            for (int k = 0; k < n; k++) keyframes[k] = k;
        }
        else
        {
            var list = new List<int>(video.SyncSamples.Length);
            foreach (var s in video.SyncSamples)
                if (s >= 1 && s <= n) list.Add(rank[s - 1]);
            list.Sort();
            // distinct (a malformed stss could repeat a sample)
            var distinct = new List<int>(list.Count);
            foreach (var k in list) if (distinct.Count == 0 || distinct[^1] != k) distinct.Add(k);
            keyframes = distinct.ToArray();
        }

        int audio = tracks.FindAll(t => t.Handler == "soun").Count;
        return new Mp4VideoIndex(video.Codec, video.Width, video.Height, preview, file, keyframes,
            reordered, video.VideoBytes, audio, movieDuration, videoEnd);
    }

    /// <summary>
    /// Leading empty edits (media_time -1) shift the track start (in track ticks, rounded like ffmpeg does);
    /// the first real edit's media_time is the point of the media that plays first. More than one real edit,
    /// an empty edit after a real one or a rate other than 1.0 are not supported.
    /// </summary>
    private static bool ResolveEdits(Track t, uint movieTs, out long leerShift, out long mediaTime)
    {
        leerShift = 0;
        mediaTime = 0;
        bool real = false;
        foreach (var (duration, media, rateInt, rateFrac) in t.Edits)
        {
            if (rateInt != 1 || rateFrac != 0) return false;
            if (real) return false;
            if (media == -1)
            {
                if (movieTs == 0) return false;
                leerShift += (long)Math.Round(duration * (double)t.Timescale / movieTs, MidpointRounding.AwayFromZero);
            }
            else if (media >= 0)
            {
                real = true;
                mediaTime = media;
            }
            else return false;
        }
        // Only the video honours media_time; for audio it is the encoder delay and ffmpeg reports start 0.
        if (t.Handler != "vide") mediaTime = 0;
        return true;
    }

    private static Track? ParseTrak(byte[] b, Box trak)
    {
        var t = new Track();

        var edts = Find(b, trak, "edts");
        if (edts is { } e && Find(b, e, "elst") is { } elst) ReadElst(b, elst, t);

        if (Find(b, trak, "mdia") is not { } mdia) return t;   // not a media track: ignored (SampleCount stays 0)

        if (Find(b, mdia, "mdhd") is not { } mdhd) return t;
        int mv = b[mdhd.Start];
        t.Timescale = mv == 0 ? U32(b, mdhd.Start + 12) : mv == 1 ? U32(b, mdhd.Start + 20) : 0;

        if (Find(b, mdia, "hdlr") is { } hdlr && hdlr.End - hdlr.Start >= 12)
            t.Handler = Encoding.Latin1.GetString(b, hdlr.Start + 8, 4);

        if (Find(b, mdia, "minf") is not { } minf || Find(b, minf, "stbl") is not { } stbl) return t;

        var stsz = Find(b, stbl, "stsz");
        var stts = Find(b, stbl, "stts");

        if (Find(b, stbl, "stsd") is { } stsd && stsd.End - stsd.Start >= 16)
        {
            t.Codec = Encoding.Latin1.GetString(b, stsd.Start + 12, 4);
            if (t.Handler == "vide" && stsd.End - stsd.Start >= 8 + 36)
            {
                t.Width = U16(b, stsd.Start + 8 + 32);
                t.Height = U16(b, stsd.Start + 8 + 34);
            }
        }

        // Sample count: stsz is authoritative; a track without it (or empty) has no samples worth timing.
        if (stsz is not { } sz) return t;
        if (sz.End - sz.Start < 12) throw new InvalidDataException("stsz too short");
        uint uniform = U32(b, sz.Start + 4);
        uint count = U32(b, sz.Start + 8);
        if (count > MaxSamples) throw new InvalidDataException("too many samples");
        t.SampleCount = count;

        if (t.Handler != "vide") return t;
        if (count == 0) return t;

        // video: the full tables
        long bytes;
        if (uniform != 0) bytes = (long)uniform * count;
        else
        {
            if ((long)(sz.End - sz.Start - 12) < (long)count * 4) throw new InvalidDataException("stsz table cut off");
            bytes = 0;
            for (int i = 0; i < count; i++) bytes += U32(b, sz.Start + 12 + i * 4);
        }
        t.VideoBytes = bytes;

        if (stts is not { } st) throw new InvalidDataException("video track without stts");
        t.Dts = ExpandStts(b, st, (int)count, out t.LastSampleDuration);

        if (Find(b, stbl, "ctts") is { } ctts) t.CttsOffsets = ExpandCtts(b, ctts, (int)count);

        if (Find(b, stbl, "stss") is { } stss)
        {
            int entries = (int)U32(b, stss.Start + 4);
            if (entries < 0 || (long)(stss.End - stss.Start - 8) < (long)entries * 4) throw new InvalidDataException("stss cut off");
            var sync = new int[entries];
            for (int i = 0; i < entries; i++) sync[i] = (int)U32(b, stss.Start + 8 + i * 4);
            t.SyncSamples = sync;
        }
        return t;
    }

    private static void ReadElst(byte[] b, Box elst, Track t)
    {
        int version = b[elst.Start];
        int entries = (int)U32(b, elst.Start + 4);
        int size = version == 1 ? 20 : 12;
        if (version > 1) throw new InvalidDataException("elst version");
        if (entries < 0 || (long)(elst.End - elst.Start - 8) < (long)entries * size) throw new InvalidDataException("elst cut off");

        int at = elst.Start + 8;
        for (int i = 0; i < entries; i++, at += size)
        {
            long duration, media;
            int rateAt;
            if (version == 0)
            {
                duration = U32(b, at);
                media = (int)U32(b, at + 4);      // signed: -1 = empty edit
                rateAt = at + 8;
            }
            else
            {
                duration = (long)U64(b, at);
                media = (long)U64(b, at + 8);
                rateAt = at + 16;
            }
            t.Edits.Add((duration, media, U16(b, rateAt), U16(b, rateAt + 2)));
        }
    }

    private static long[] ExpandStts(byte[] b, Box stts, int sampleCount, out long lastDelta)
    {
        lastDelta = 0;
        int entries = (int)U32(b, stts.Start + 4);
        if (entries < 0 || (long)(stts.End - stts.Start - 8) < (long)entries * 8) throw new InvalidDataException("stts cut off");

        long total = 0;
        for (int i = 0; i < entries; i++) total += U32(b, stts.Start + 8 + i * 8);
        if (total != sampleCount) throw new InvalidDataException("stts and stsz disagree on the sample count");

        var dts = new long[sampleCount];
        long time = 0;
        int at = 0;
        for (int i = 0; i < entries; i++)
        {
            long count = U32(b, stts.Start + 8 + i * 8);
            long delta = U32(b, stts.Start + 8 + i * 8 + 4);
            for (long j = 0; j < count; j++) { dts[at++] = time; time += delta; }
            if (count > 0) lastDelta = delta;
        }
        return dts;
    }

    private static long[] ExpandCtts(byte[] b, Box ctts, int sampleCount)
    {
        int version = b[ctts.Start];
        int entries = (int)U32(b, ctts.Start + 4);
        if (entries < 0 || (long)(ctts.End - ctts.Start - 8) < (long)entries * 8) throw new InvalidDataException("ctts cut off");

        var offsets = new long[sampleCount];   // samples a short ctts does not cover keep offset 0
        int at = 0;
        for (int i = 0; i < entries && at < sampleCount; i++)
        {
            long count = U32(b, ctts.Start + 8 + i * 8);
            uint raw = U32(b, ctts.Start + 8 + i * 8 + 4);
            long offset = version == 0 ? raw : (int)raw;
            for (long j = 0; j < count && at < sampleCount; j++) offsets[at++] = offset;
        }
        return offsets;
    }

    // ---------------------------------------------------------------- box walking

    private static IEnumerable<Box> Children(byte[] b, int start, int end)
    {
        int pos = start;
        for (int count = 0; pos + 8 <= end; count++)
        {
            if (count >= MaxBoxesPerLevel) throw new InvalidDataException("too many boxes");
            long size = U32(b, pos);
            string type = Encoding.Latin1.GetString(b, pos + 4, 4);
            int header = 8;
            if (size == 1)
            {
                ulong large = U64(b, pos + 8);
                if (large > int.MaxValue) throw new InvalidDataException("box too large");
                size = (long)large;
                header = 16;
            }
            else if (size == 0) size = end - pos;

            if (size < header || pos + size > end) throw new InvalidDataException("box outside its parent");
            yield return new Box(type, pos + header, pos + (int)size);
            pos += (int)size;
        }
    }

    private static Box? Find(byte[] b, Box parent, string type)
    {
        foreach (var c in Children(b, parent.Start, parent.End))
            if (c.Type == type) return c;
        return null;
    }

    // ---------------------------------------------------------------- primitives

    private static int U16(byte[] b, int o) => (b[o] << 8) | b[o + 1];

    private static uint U32(byte[] b, int o)
        => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

    private static ulong U64(byte[] b, int o) => ((ulong)U32(b, o) << 32) | U32(b, o + 4);
}
