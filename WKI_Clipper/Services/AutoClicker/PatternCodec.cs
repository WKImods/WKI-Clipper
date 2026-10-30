using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>
/// The compact text form of <see cref="PatternData"/> (mouse-patterns design 5.2): <see cref="Prefix"/> + base64 of
/// [duration as 8-byte little-endian double] then per track (raw, pointer) [count] and per bin [time delta][dx][dy], every integer a
/// zig-zag varint. The first time delta is the bin's own time. A 60 s recording at 1 ms bins stays far below the protocol's line limit.
/// <see cref="TryDecode"/> never throws: a file or message that does not describe a well-formed pattern is rejected as a whole.
/// </summary>
public static class PatternCodec
{
    /// <summary>Format version 1. A future format gets its own prefix; this reader rejects it.</summary>
    public const string Prefix = "p1:";

    /// <summary>Safety cap per track; 60 s at 1 ms bins need at most 60 001.</summary>
    public const int MaxBins = 120_000;

    private const int MaxVarintBytes = 5;   // a zig-zag int32
    private const int MaxPayloadBytes = sizeof(double) + 2 * (MaxVarintBytes + MaxBins * 3 * MaxVarintBytes);

    /// <summary>The longest text a valid pattern can have; anything longer is rejected before it is decoded.</summary>
    public const int MaxEncodedLength = 3 /* Prefix */ + 4 * ((MaxPayloadBytes + 2) / 3);

    /// <summary>
    /// The text form. Bins without motion are dropped (they carry nothing); tracks whose arrays differ in length, whose times are not
    /// strictly increasing from &gt;= 0 or reach beyond <see cref="MousePatternRules.MaxDurationMs"/>, and a duration that is not a
    /// number in 0 .. MaxDurationMs throw <see cref="ArgumentException"/> - such data could never be read back.
    /// </summary>
    public static string Encode(PatternData data)
    {
        if (!ValidDuration(data.DurationMs)) throw new ArgumentException("The duration is out of range.", nameof(data));

        using var ms = new MemoryStream();
        Span<byte> duration = stackalloc byte[sizeof(double)];
        BinaryPrimitives.WriteDoubleLittleEndian(duration, data.DurationMs);
        ms.Write(duration);
        WriteTrack(ms, data.Raw, nameof(data.Raw));
        WriteTrack(ms, data.Pointer, nameof(data.Pointer));
        return Prefix + Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>False (and null) for null, a wrong prefix or version, bad base64, a cut or padded payload, non-increasing or negative
    /// times, more than <see cref="MaxBins"/> bins or times/duration beyond <see cref="MousePatternRules.MaxDurationMs"/>, bins without motion.</summary>
    public static bool TryDecode(string? text, [NotNullWhen(true)] out PatternData? data)
    {
        data = null;
        if (text is null || text.Length > MaxEncodedLength || !text.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        byte[] bytes;
        try { bytes = Convert.FromBase64String(text[Prefix.Length..]); }
        catch (FormatException) { return false; }

        if (bytes.Length < sizeof(double)) return false;
        double durationMs = BinaryPrimitives.ReadDoubleLittleEndian(bytes);
        if (!ValidDuration(durationMs)) return false;

        int pos = sizeof(double);
        if (!TryReadTrack(bytes, ref pos, out var raw) || !TryReadTrack(bytes, ref pos, out var pointer)) return false;
        if (pos != bytes.Length) return false;

        data = new PatternData(raw, pointer, durationMs);
        return true;
    }

    private static bool ValidDuration(double ms) => double.IsFinite(ms) && ms >= 0 && ms <= MousePatternRules.MaxDurationMs;

    private static void WriteTrack(Stream s, PatternTrack track, string name)
    {
        int n = track.AtMs.Length;
        if (track.Dx.Length != n || track.Dy.Length != n) throw new ArgumentException($"The {name} track's arrays differ in length.");

        int count = 0;
        for (int i = 0; i < n; i++)
        {
            if (track.AtMs[i] < 0 || track.AtMs[i] > MousePatternRules.MaxDurationMs || i > 0 && track.AtMs[i] <= track.AtMs[i - 1])
                throw new ArgumentException($"The {name} track's times are not strictly increasing within 0 .. {MousePatternRules.MaxDurationMs} ms.");
            if (track.Dx[i] != 0 || track.Dy[i] != 0) count++;
        }

        WriteVarint(s, count);
        int last = 0;
        for (int i = 0; i < n; i++)
        {
            if (track.Dx[i] == 0 && track.Dy[i] == 0) continue;
            WriteVarint(s, track.AtMs[i] - last);
            last = track.AtMs[i];
            WriteVarint(s, track.Dx[i]);
            WriteVarint(s, track.Dy[i]);
        }
    }

    private static bool TryReadTrack(byte[] b, ref int pos, out PatternTrack track)
    {
        track = PatternTrack.Empty;
        if (!TryReadVarint(b, ref pos, out int count) || count < 0 || count > MaxBins) return false;
        if (count > (b.Length - pos) / 3) return false;   // every bin needs at least three bytes: a cut payload, before allocating

        var at = new int[count]; var dx = new int[count]; var dy = new int[count];
        long time = 0;
        for (int i = 0; i < count; i++)
        {
            if (!TryReadVarint(b, ref pos, out int delta) || !TryReadVarint(b, ref pos, out dx[i]) || !TryReadVarint(b, ref pos, out dy[i]))
                return false;
            if (i > 0 ? delta <= 0 : delta < 0) return false;
            time += delta;
            if (time > MousePatternRules.MaxDurationMs) return false;
            if (dx[i] == 0 && dy[i] == 0) return false;
            at[i] = (int)time;
        }
        if (count > 0) track = new PatternTrack(at, dx, dy);
        return true;
    }

    private static void WriteVarint(Stream s, int value)
    {
        uint u = (uint)((value << 1) ^ (value >> 31));
        while (u >= 0x80)
        {
            s.WriteByte((byte)(u | 0x80));
            u >>= 7;
        }
        s.WriteByte((byte)u);
    }

    private static bool TryReadVarint(byte[] b, ref int pos, out int value)
    {
        value = 0;
        uint u = 0;
        for (int i = 0; i < MaxVarintBytes; i++)
        {
            if (pos >= b.Length) return false;
            byte next = b[pos++];
            if (i == MaxVarintBytes - 1 && next > 0x0F) return false;   // more than 32 bits, or a continuation past the fifth byte
            u |= (uint)(next & 0x7F) << (7 * i);
            if ((next & 0x80) == 0)
            {
                value = (int)(u >> 1) ^ -(int)(u & 1);
                return true;
            }
        }
        return false;
    }
}
