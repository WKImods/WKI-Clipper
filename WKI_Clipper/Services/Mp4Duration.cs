using System;
using System.IO;
using System.Text;

namespace WKI_Clipper.Services;

/// <summary>
/// Reads how long a finished MP4 segment is. A 30-minute clip is stitched from 360 segments, and neither the wall
/// clock nor "segment count x 5 s" tells how long each really is (a segment ends at a keyframe, and its audio
/// overhangs the video by up to one AAC frame) - chapter positions and the saved length have to come from the files.
/// Reading the header is a few KB per file, ffmpeg would cost a process per segment.
///
/// Two different numbers, on purpose: <see cref="TryRead"/> is the movie duration (<c>moov/mvhd</c>, the longer one of
/// video and audio); <see cref="TryReadConcatDuration"/> is the end of the last video frame, which is what the
/// concat list has to pin (see <see cref="ClipStitcher.ConcatListLines"/> for the measurement behind it).
/// </summary>
public static class Mp4Duration
{
    private const int MaxTopLevelBoxes = 64;
    private const int MaxMoovChildren = 64;

    /// <summary>
    /// The duration to pin for this segment in the concat list: where the last video frame ends, from the sample
    /// tables (<see cref="Mp4Index"/>; start offset of the video and edit list included, relative to the file start
    /// like ffmpeg's time axis). A segment whose video the index cannot read (no video track, fragmented file, an edit
    /// list it does not support) gives the movie duration instead - a readable segment is never dropped because of
    /// this. False when neither can be read (no moov yet: the segment is still being written). Never throws.
    /// </summary>
    public static bool TryReadConcatDuration(string path, out TimeSpan duration)
    {
        if (Mp4Index.TryRead(path) is { } index
            && double.IsFinite(index.VideoEndSeconds) && index.VideoEndSeconds > 0
            && index.VideoEndSeconds < TimeSpan.MaxValue.TotalSeconds)
        {
            // Rounded to one tick (100 ns), never to milliseconds: the chapters add up 360 of these.
            duration = TimeSpan.FromTicks((long)Math.Round(index.VideoEndSeconds * TimeSpan.TicksPerSecond));
            return true;
        }
        return TryRead(path, out duration);
    }

    /// <summary>
    /// The movie duration, i.e. the longer one of video and audio (NOT what the concat list pins, see
    /// <see cref="TryReadConcatDuration"/>). Walks the top-level boxes (32-bit size + type; size 1 = 64-bit
    /// largesize; size 0 = to end of file), finds moov then mvhd (v0: 4-byte creation/modification/timescale/duration;
    /// v1: 8/8/4/8) and returns duration/timescale. False for a missing moov (segment still being written),
    /// timescale 0, an all-ones duration, box sizes below 8 or past the end of the file, and more than 64 top-level
    /// boxes. Opens with FileShare.ReadWrite | FileShare.Delete. Never throws.
    /// </summary>
    public static bool TryRead(string path, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, FileOptions.None);
            return TryRead(fs, out duration);
        }
        catch { return false; }
    }

    internal static bool TryRead(Stream s, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        try
        {
            long length = s.Length;
            long pos = 0;
            for (int boxes = 0; pos + 8 <= length; boxes++)
            {
                if (boxes >= MaxTopLevelBoxes) return false;
                if (!ReadBoxHeader(s, pos, length, out string type, out long headerSize, out long size)) return false;
                if (type == "moov")
                    return ReadMoov(s, pos + headerSize, pos + size, out duration);
                pos += size;
            }
            return false;
        }
        catch { return false; }
    }

    private static bool ReadMoov(Stream s, long start, long end, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        long pos = start;
        for (int children = 0; pos + 8 <= end; children++)
        {
            if (children >= MaxMoovChildren) return false;
            if (!ReadBoxHeader(s, pos, end, out string type, out long headerSize, out long size)) return false;
            if (type == "mvhd") return ReadMvhd(s, pos + headerSize, pos + size, out duration);
            pos += size;
        }
        return false;
    }

    private static bool ReadMvhd(Stream s, long start, long end, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (end - start < 4) return false;
        s.Position = start;
        int version = s.ReadByte();
        s.Seek(3, SeekOrigin.Current);   // flags

        ulong rawDuration;
        uint timescale;
        if (version == 0)
        {
            if (end - start < 4 + 16) return false;
            var b = new byte[16];
            ReadFully(s, b);
            timescale = ReadUInt32(b, 8);
            uint d = ReadUInt32(b, 12);
            rawDuration = d == uint.MaxValue ? ulong.MaxValue : d;
        }
        else if (version == 1)
        {
            if (end - start < 4 + 28) return false;
            var b = new byte[28];
            ReadFully(s, b);
            timescale = ReadUInt32(b, 16);
            rawDuration = ReadUInt64(b, 20);
        }
        else return false;

        if (timescale == 0 || rawDuration == ulong.MaxValue) return false;
        decimal ticks = (decimal)rawDuration * TimeSpan.TicksPerSecond / timescale;
        if (ticks > long.MaxValue) return false;
        duration = TimeSpan.FromTicks((long)ticks);
        return true;
    }

    /// <summary>Header at <paramref name="pos"/>; the box has to end inside <paramref name="limit"/>.</summary>
    private static bool ReadBoxHeader(Stream s, long pos, long limit, out string type, out long headerSize, out long size)
    {
        type = "";
        headerSize = 8;
        size = 0;
        s.Position = pos;
        var h = new byte[8];
        if (!TryReadFully(s, h)) return false;
        long size32 = ReadUInt32(h, 0);
        type = Encoding.Latin1.GetString(h, 4, 4);

        if (size32 == 1)
        {
            var big = new byte[8];
            if (!TryReadFully(s, big)) return false;
            ulong large = ReadUInt64(big, 0);
            if (large > long.MaxValue) return false;
            headerSize = 16;
            size = (long)large;
        }
        else if (size32 == 0)
        {
            size = limit - pos;   // to the end of the enclosing box/file
        }
        else size = size32;

        if (size < headerSize) return false;
        if (pos + size > limit) return false;
        return true;
    }

    private static uint ReadUInt32(byte[] b, int o)
        => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

    private static ulong ReadUInt64(byte[] b, int o)
        => ((ulong)ReadUInt32(b, o) << 32) | ReadUInt32(b, o + 4);

    private static bool TryReadFully(Stream s, byte[] buffer)
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

    private static void ReadFully(Stream s, byte[] buffer)
    {
        if (!TryReadFully(s, buffer)) throw new EndOfStreamException();
    }
}
