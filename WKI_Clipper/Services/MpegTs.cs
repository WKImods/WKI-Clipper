using System;
using System.Collections.Generic;

namespace WKI_Clipper.Services;

/// <summary>An elementary stream of the program: PID and the PMT stream_type.</summary>
internal readonly record struct TsStream(int Pid, byte StreamType);

/// <summary>
/// The few parts of MPEG-TS the RAM buffer needs: packet header fields, PAT/PMT and PES timestamps. ffmpeg's mpegts
/// muxer is the only producer, so the parsing is deliberately narrow (one program, PSI sections in one packet) and every
/// reader returns false instead of throwing on anything else.
/// </summary>
internal static class MpegTs
{
    public const int PacketSize = 188;
    public const byte SyncByte = 0x47;
    public const int PatPid = 0x0000;
    public const int NitPid = 0x0010;
    public const int SdtPid = 0x0011;
    public const int NullPid = 0x1FFF;
    /// <summary>PTS/DTS are 33-bit counters of a 90 kHz clock; they wrap after ~26.5 hours.</summary>
    public const long PtsModulus = 1L << 33;
    public const int ClockRate = 90_000;

    public static int Pid(ReadOnlySpan<byte> p) => ((p[1] & 0x1F) << 8) | p[2];
    public static bool PayloadUnitStart(ReadOnlySpan<byte> p) => (p[1] & 0x40) != 0;
    public static bool HasAdaptation(ReadOnlySpan<byte> p) => (p[3] & 0x20) != 0;
    public static bool HasPayload(ReadOnlySpan<byte> p) => (p[3] & 0x10) != 0;

    /// <summary>
    /// random_access_indicator of the adaptation field. ffmpeg's muxer sets it on the first packet of every PES that
    /// carries a key frame (unless -mpegts_flags omit_rai) - for video that is exactly where the segment muxer would cut.
    /// </summary>
    public static bool RandomAccess(ReadOnlySpan<byte> p)
        => HasAdaptation(p) && p[4] > 0 && (p[5] & 0x40) != 0;

    /// <summary>Offset of the payload inside the packet, or -1 when there is none.</summary>
    public static int PayloadOffset(ReadOnlySpan<byte> p)
    {
        if (!HasPayload(p)) return -1;
        int off = 4;
        if (HasAdaptation(p)) off += 1 + p[4];
        return off < PacketSize ? off : -1;
    }

    /// <summary>The PTS of a PES that starts in this packet (raw 33-bit value).</summary>
    public static bool TryReadPesPts(ReadOnlySpan<byte> p, out long pts)
    {
        pts = 0;
        if (!PayloadUnitStart(p)) return false;
        int o = PayloadOffset(p);
        if (o < 0 || o + 14 > PacketSize) return false;
        var pes = p[o..];
        if (pes[0] != 0 || pes[1] != 0 || pes[2] != 1) return false;
        byte streamId = pes[3];
        // Streams without the optional PES header (padding, private_stream_2, ...) carry no timestamps.
        if (streamId is 0xBC or 0xBE or 0xBF or 0xF0 or 0xF1 or 0xF2 or 0xF8 or 0xFF) return false;
        if ((pes[6] & 0xC0) != 0x80) return false;
        if ((pes[7] & 0x80) == 0) return false;   // no PTS
        pts = ReadTimestamp(pes.Slice(9, 5));
        return true;
    }

    private static long ReadTimestamp(ReadOnlySpan<byte> b)
        => ((long)(b[0] >> 1) & 0x07) << 30
         | (long)b[1] << 22
         | ((long)b[2] >> 1) << 15
         | (long)b[3] << 7
         | (long)b[4] >> 1;

    /// <summary>PMT PID of the first program in a PAT packet.</summary>
    public static bool TryParsePat(ReadOnlySpan<byte> p, out int pmtPid)
    {
        pmtPid = -1;
        if (!TrySection(p, 0x00, out var section)) return false;
        // 8 header bytes, then 4-byte entries, CRC at the end.
        for (int i = 8; i + 4 <= section.Length - 4; i += 4)
        {
            int program = (section[i] << 8) | section[i + 1];
            int pid = ((section[i + 2] & 0x1F) << 8) | section[i + 3];
            if (program != 0) { pmtPid = pid; return true; }
        }
        return false;
    }

    /// <summary>The elementary streams of a PMT packet.</summary>
    public static bool TryParsePmt(ReadOnlySpan<byte> p, out List<TsStream> streams)
    {
        streams = new List<TsStream>();
        if (!TrySection(p, 0x02, out var section)) return false;
        if (section.Length < 12 + 4) return false;
        int programInfoLength = ((section[10] & 0x0F) << 8) | section[11];
        int i = 12 + programInfoLength;
        int end = section.Length - 4;   // CRC
        while (i + 5 <= end)
        {
            byte type = section[i];
            int pid = ((section[i + 1] & 0x1F) << 8) | section[i + 2];
            int esInfo = ((section[i + 3] & 0x0F) << 8) | section[i + 4];
            streams.Add(new TsStream(pid, type));
            i += 5 + esInfo;
        }
        return streams.Count > 0;
    }

    /// <summary>A PSI section that starts and ends inside this packet (ffmpeg's PAT/PMT always do).</summary>
    private static bool TrySection(ReadOnlySpan<byte> p, byte tableId, out ReadOnlySpan<byte> section)
    {
        section = default;
        if (!PayloadUnitStart(p)) return false;
        int o = PayloadOffset(p);
        if (o < 0) return false;
        int start = o + 1 + p[o];   // pointer_field
        if (start + 3 > PacketSize || p[start] != tableId) return false;
        int length = ((p[start + 1] & 0x0F) << 8) | p[start + 2];
        if (start + 3 + length > PacketSize) return false;
        section = p.Slice(start, 3 + length);
        return true;
    }

    /// <summary>H.264 and HEVC: the video types the RAM buffer can split at key frames and read back from a .ts.</summary>
    public static bool IsSplittableVideo(byte streamType) => streamType is 0x1B or 0x24;

    /// <summary>
    /// Whether an encoder's output survives the MPEG-TS round trip. Measured with ffmpeg 8.1: H.264 and HEVC read back as
    /// video; AV1 is written but read back as a data stream ("bin_data"), so AV1 encoders record to disk.
    /// </summary>
    public static bool SupportsCodec(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec)) return false;
        return codec.StartsWith("h264", StringComparison.OrdinalIgnoreCase)
            || codec.StartsWith("hevc", StringComparison.OrdinalIgnoreCase)
            || codec.Equals("libx264", StringComparison.OrdinalIgnoreCase)
            || codec.Equals("libx265", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The 64-bit value of a raw 33-bit timestamp that is closest to <paramref name="reference"/>.</summary>
    public static long Unwrap(long raw, long reference)
    {
        raw &= PtsModulus - 1;
        long candidate = (reference & ~(PtsModulus - 1)) | raw;
        if (candidate - reference > PtsModulus / 2) candidate -= PtsModulus;
        else if (reference - candidate > PtsModulus / 2) candidate += PtsModulus;
        return candidate;
    }

    /// <summary>True when raw timestamp <paramref name="a"/> is at or after <paramref name="b"/> (wrap-aware).</summary>
    public static bool AtOrAfter(long a, long b)
        => ((a - b) & (PtsModulus - 1)) < PtsModulus / 2;
}

/// <summary>Receives what <see cref="MpegTsSegmenter"/> makes of the byte stream, in stream order.</summary>
internal interface ITsSegmentSink
{
    /// <summary>A segment begins at a video key frame. <paramref name="psi"/> = the latest PAT+PMT packets (copy).</summary>
    void SegmentStarted(long startPts, byte[] psi, bool startsWithPat);
    /// <summary>Bytes of the open segment (whole TS packets).</summary>
    void Append(ReadOnlySpan<byte> bytes);
    /// <summary>The open segment is complete; <paramref name="endPts"/> = the PTS of the key frame that starts the next one.</summary>
    void SegmentEnded(long endPts);
}

/// <summary>
/// Splits ffmpeg's MPEG-TS output into segments that each start with PAT/PMT and a video key frame, the way the segment
/// muxer cuts mp4 files: a cut happens at the first key frame at least <c>segment - delta</c> after the segment start.
/// Timestamps are unwrapped to 64 bits, so durations stay right across the 26.5 h PTS wrap.
///
/// PSI and PCR-only packets are held back until the next elementary-stream packet: the PAT/PMT that ffmpeg writes right
/// before a key frame then lands in the NEW segment, which can be read on its own.
/// Pure (no I/O, no clock): fed with arbitrary chunks, unit-tested with ffmpeg-made fixtures.
/// </summary>
internal sealed class MpegTsSegmenter
{
    private readonly ITsSegmentSink _sink;
    private readonly long _cutTicks;
    private readonly byte[] _carry = new byte[MpegTs.PacketSize];
    private int _carryLength;
    private readonly List<byte> _pending = new(MpegTs.PacketSize * 4);
    private bool _pendingHasPat;
    private const int MaxPendingPackets = 32;

    private int _pmtPid = -1;
    private int _videoPid = -1;
    private readonly byte[] _pat = new byte[MpegTs.PacketSize];
    private readonly byte[] _pmt = new byte[MpegTs.PacketSize];
    private bool _havePat, _havePmt;

    private bool _started;
    private long _segmentStart;
    private long _ptsReference = -1;
    private long _lastVideoPts = long.MinValue;
    private long _prevVideoPts = long.MinValue;
    private long _frameTicks;   // smallest positive PTS step seen recently
    private bool _resyncLogged;

    /// <param name="segmentSeconds">Target segment length (the segment muxer's -segment_time).</param>
    /// <param name="deltaSeconds">-segment_time_delta: a key frame this much early still cuts.</param>
    public MpegTsSegmenter(ITsSegmentSink sink, int segmentSeconds, double? deltaSeconds = null)
    {
        _sink = sink;
        long target = (long)Math.Max(1, segmentSeconds) * MpegTs.ClockRate;
        long delta = (long)Math.Round(Math.Max(0, deltaSeconds ?? 0) * MpegTs.ClockRate);
        _cutTicks = Math.Max(1, target - delta);
    }

    /// <summary>Stream type of the video in the PMT (0 while unknown).</summary>
    public byte VideoStreamType { get; private set; }

    /// <summary>True once the first key frame opened a segment.</summary>
    public bool Started => _started;

    /// <summary>Where the open segment would end if the stream stopped now: the last video frame plus one frame.</summary>
    public long? EstimatedEndPts
        => _started && _lastVideoPts != long.MinValue
            ? _lastVideoPts + (_frameTicks > 0 ? _frameTicks : 1)
            : null;

    public void Feed(ReadOnlySpan<byte> data)
    {
        // A packet split over two reads: complete it from the carry first.
        if (_carryLength > 0)
        {
            int need = MpegTs.PacketSize - _carryLength;
            if (data.Length < need)
            {
                data.CopyTo(_carry.AsSpan(_carryLength));
                _carryLength += data.Length;
                return;
            }
            data[..need].CopyTo(_carry.AsSpan(_carryLength));
            data = data[need..];
            _carryLength = 0;
            if (_carry[0] == MpegTs.SyncByte) ProcessAlone(_carry);
        }

        int i = 0;
        int run = -1;   // start of a contiguous range of packets that go into the open segment
        while (i + MpegTs.PacketSize <= data.Length)
        {
            if (data[i] != MpegTs.SyncByte)
            {
                Flush(data, ref run, i);
                int next = data[(i + 1)..].IndexOf(MpegTs.SyncByte);
                if (!_resyncLogged)
                {
                    _resyncLogged = true;
                    Logger.Warn("RAM buffer: MPEG-TS stream lost sync - skipping to the next packet start");
                }
                if (next < 0) { i = data.Length; break; }
                i += 1 + next;
                continue;
            }

            var packet = data.Slice(i, MpegTs.PacketSize);
            switch (Classify(packet, out long keyPts))
            {
                case PacketAction.Hold:
                    Flush(data, ref run, i);
                    Hold(packet);
                    break;
                case PacketAction.KeyFrame:
                    Flush(data, ref run, i);
                    OnKeyFrame(keyPts);
                    ReleasePending();
                    if (_started) run = i;
                    break;
                default:
                    if (_pending.Count > 0)
                    {
                        Flush(data, ref run, i);
                        ReleasePending();
                    }
                    if (_started && run < 0) run = i;
                    break;
            }
            i += MpegTs.PacketSize;
        }
        Flush(data, ref run, i);

        if (i < data.Length)
        {
            var rest = data[i..];
            rest.CopyTo(_carry);
            _carryLength = rest.Length;
        }
    }

    private void Flush(ReadOnlySpan<byte> data, ref int run, int end)
    {
        if (run >= 0 && end > run && _started) _sink.Append(data[run..end]);
        run = -1;
    }

    private void ProcessAlone(ReadOnlySpan<byte> packet)
    {
        switch (Classify(packet, out long keyPts))
        {
            case PacketAction.Hold:
                Hold(packet);
                return;
            case PacketAction.KeyFrame:
                OnKeyFrame(keyPts);
                ReleasePending();
                break;
            default:
                ReleasePending();
                break;
        }
        if (_started) _sink.Append(packet);
    }

    private enum PacketAction { Append, Hold, KeyFrame }

    private PacketAction Classify(ReadOnlySpan<byte> p, out long keyPts)
    {
        keyPts = 0;
        int pid = MpegTs.Pid(p);
        if (pid == MpegTs.PatPid)
        {
            if (MpegTs.TryParsePat(p, out int pmt)) _pmtPid = pmt;
            p.CopyTo(_pat);
            _havePat = true;
            return PacketAction.Hold;
        }
        if (pid == _pmtPid)
        {
            if (MpegTs.TryParsePmt(p, out var streams))
            {
                foreach (var s in streams)
                {
                    if (!MpegTs.IsSplittableVideo(s.StreamType) && s.StreamType is not (0x01 or 0x02 or 0x10 or 0x06)) continue;
                    if (_videoPid < 0 || _videoPid == s.Pid)
                    {
                        _videoPid = s.Pid;
                        VideoStreamType = s.StreamType;
                        break;
                    }
                }
            }
            p.CopyTo(_pmt);
            _havePmt = true;
            return PacketAction.Hold;
        }
        if (pid is MpegTs.SdtPid or MpegTs.NitPid or MpegTs.NullPid || !MpegTs.HasPayload(p))
            return PacketAction.Hold;

        if (pid == _videoPid && MpegTs.PayloadUnitStart(p) && MpegTs.TryReadPesPts(p, out long raw))
        {
            long pts = _ptsReference < 0 ? raw : MpegTs.Unwrap(raw, _ptsReference);
            _ptsReference = pts;
            TrackFrame(pts);
            if (MpegTs.RandomAccess(p) && MpegTs.IsSplittableVideo(VideoStreamType))
            {
                keyPts = pts;
                return PacketAction.KeyFrame;
            }
        }
        return PacketAction.Append;
    }

    private void TrackFrame(long pts)
    {
        if (_prevVideoPts != long.MinValue)
        {
            long step = pts - _prevVideoPts;
            // Smallest positive step = one frame (B-frames reorder PTS, so a single step can be several frames).
            if (step > 0 && (_frameTicks == 0 || step < _frameTicks)) _frameTicks = step;
        }
        _prevVideoPts = pts;
        if (_lastVideoPts == long.MinValue || pts > _lastVideoPts) _lastVideoPts = pts;
    }

    private void OnKeyFrame(long pts)
    {
        if (!_started)
        {
            Begin(pts);
            return;
        }
        if (pts - _segmentStart >= _cutTicks)
        {
            _sink.SegmentEnded(pts);
            Begin(pts);
        }
    }

    private void Begin(long pts)
    {
        _started = true;
        _segmentStart = pts;
        var psi = new byte[(_havePat ? MpegTs.PacketSize : 0) + (_havePmt ? MpegTs.PacketSize : 0)];
        if (_havePat) _pat.CopyTo(psi, 0);
        if (_havePmt) _pmt.CopyTo(psi, _havePat ? MpegTs.PacketSize : 0);
        _sink.SegmentStarted(pts, psi, _pendingHasPat);
    }

    private void Hold(ReadOnlySpan<byte> packet)
    {
        if (_pending.Count >= MaxPendingPackets * MpegTs.PacketSize) ReleasePending();
        // An SDT may come first; what matters is that the segment carries its own PAT before the key frame.
        if (MpegTs.Pid(packet) == MpegTs.PatPid) _pendingHasPat = true;
        foreach (var b in packet) _pending.Add(b);
    }

    private void ReleasePending()
    {
        if (_pending.Count == 0) return;
        if (_started)
        {
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pending);
            _sink.Append(span);
        }
        _pending.Clear();
        _pendingHasPat = false;
    }
}
