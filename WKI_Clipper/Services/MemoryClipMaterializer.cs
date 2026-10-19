using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WKI_Clipper.Services;

/// <summary>
/// Turns the RAM segments a save picked into files ffmpeg's concat demuxer can read: one hidden .ts per uninterrupted run
/// (same generation, consecutive sequence numbers). Inside a run the bytes are simply the recorded stream, so its
/// timestamps continue without a seam; between runs (a gap) the concat list's <c>duration</c> lines place each file, exactly
/// as for the disk segments.
///
/// The first segment of a run starts at a video key frame, but the muxer interleaves audio a little late, so a few audio
/// packets from just BEFORE the key frame follow it. Left in, they would make the file start earlier than its video and
/// push the video of that run later by the same amount - fine on its own, but the run after a gap would then overlap it.
/// So audio PES packets older than the key frame are left out of the first segment (up to one AAC frame of audio at the
/// very start of a run is lost; nothing of the video).
/// </summary>
internal static class MemoryClipMaterializer
{
    /// <summary>Uninterrupted runs of the picked candidates (in order): same generation and consecutive sequence numbers.</summary>
    public static List<List<SegmentCandidate>> Runs(IReadOnlyList<SegmentCandidate> picked)
    {
        var runs = new List<List<SegmentCandidate>>();
        List<SegmentCandidate>? current = null;
        MemorySegment? previous = null;
        foreach (var c in picked)
        {
            var m = c.Memory ?? throw new ArgumentException("not a RAM segment: " + c.Path, nameof(picked));
            bool continues = previous is not null && current is not null
                             && m.Generation == previous.Generation && m.Sequence == previous.Sequence + 1;
            if (!continues)
            {
                current = new List<SegmentCandidate>();
                runs.Add(current);
            }
            current!.Add(c);
            previous = m;
        }
        return runs;
    }

    /// <summary>
    /// Writes every run to <c>pathForRun(index)</c> (created new, then hidden) and returns one candidate per run - path,
    /// summed duration and bytes - ready for <see cref="ClipStitcher.ConcatListLines"/>. <paramref name="created"/> collects
    /// every file created, also when this throws, so the caller can delete them.
    /// </summary>
    public static async Task<List<SegmentCandidate>> MaterializeAsync(IReadOnlyList<SegmentCandidate> picked,
        Func<int, string> pathForRun, List<string> created, Action<long>? onBytes, CancellationToken ct)
    {
        var result = new List<SegmentCandidate>();
        long total = 0;
        var runs = Runs(picked);
        for (int r = 0; r < runs.Count; r++)
        {
            var run = runs[r];
            string path = pathForRun(r);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            created.Add(path);
            long written;
            await using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                             1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                written = await WriteRunAsync(run.Select(c => c.Memory!).ToList(), fs, b =>
                {
                    total += b;
                    onBytes?.Invoke(total);
                }, ct).ConfigureAwait(false);
            }
            TryHide(path);
            var duration = TimeSpan.Zero;
            foreach (var c in run) duration += c.Duration ?? TimeSpan.Zero;
            result.Add(new SegmentCandidate(path, run[0].Generation, run[^1].EndUtc, written, duration));
        }
        return result;
    }

    internal static void TryHide(string path)
    {
        try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden); }
        catch (Exception ex) { Logger.Warn($"Could not hide '{path}': {ex.Message}"); }
    }

    /// <summary>One run as a continuous TS: PSI first when the segment does not carry its own, early audio trimmed.</summary>
    public static async Task<long> WriteRunAsync(IReadOnlyList<MemorySegment> run, Stream output, Action<long>? onBytes,
        CancellationToken ct)
    {
        long written = 0;
        if (run.Count == 0) return 0;
        var first = run[0];
        if (!first.StartsWithPat && first.Psi.Length > 0)
        {
            await output.WriteAsync(first.Psi, ct).ConfigureAwait(false);
            written += first.Psi.Length;
        }

        var lead = TrimLead(first);
        await output.WriteAsync(lead, ct).ConfigureAwait(false);
        written += lead.Length;
        onBytes?.Invoke(lead.Length);
        long skip = LeadBytes(first);   // bytes of the first segment already handled by TrimLead

        for (int i = 0; i < run.Count; i++)
        {
            long offset = 0;
            foreach (var chunk in run[i].Chunks())
            {
                ct.ThrowIfCancellationRequested();
                var part = chunk;
                if (i == 0)
                {
                    if (offset + part.Length <= skip) { offset += part.Length; continue; }
                    if (offset < skip) part = part[(int)(skip - offset)..];
                }
                offset += chunk.Length;
                await output.WriteAsync(part, ct).ConfigureAwait(false);
                written += part.Length;
                onBytes?.Invoke(part.Length);
            }
        }
        return written;
    }

    /// <summary>How far into the first segment early audio can reach: two seconds of it, at most the whole segment.</summary>
    private static long LeadBytes(MemorySegment s)
    {
        if (s.Duration <= TimeSpan.Zero) return s.Bytes;
        long perSecond = (long)(s.Bytes / Math.Max(0.001, s.Duration.TotalSeconds));
        long lead = Math.Min(s.Bytes, Math.Max(perSecond * 2, 64 * MpegTs.PacketSize));
        return lead - lead % MpegTs.PacketSize;
    }

    /// <summary>
    /// The first <see cref="LeadBytes"/> of a segment with every audio (non-video ES) PES whose PTS lies before the
    /// segment's key frame removed. A PES is dropped as a whole (all its packets); per PID only PES before the first kept
    /// one are dropped, so continuity counters of what is kept never jump.
    /// </summary>
    internal static byte[] TrimLead(MemorySegment s)
    {
        long leadLength = LeadBytes(s);
        var lead = new byte[leadLength];
        int filled = 0;
        foreach (var chunk in s.Chunks())
        {
            int n = (int)Math.Min(chunk.Length, leadLength - filled);
            chunk.Span[..n].CopyTo(lead.AsSpan(filled));
            filled += n;
            if (filled >= leadLength) break;
        }
        return TrimEarlyAudio(lead.AsSpan(0, filled), s.Psi, s.StartPts & (MpegTs.PtsModulus - 1));
    }

    /// <summary>Pure core of <see cref="TrimLead"/>: whole packets in, the kept packets out.</summary>
    internal static byte[] TrimEarlyAudio(ReadOnlySpan<byte> packets, ReadOnlySpan<byte> psi, long keyPtsRaw)
    {
        int pmtPid = -1;
        int videoPid = -1;
        var audio = new HashSet<int>();
        void Learn(ReadOnlySpan<byte> p)
        {
            int pid = MpegTs.Pid(p);
            if (pid == MpegTs.PatPid && MpegTs.TryParsePat(p, out int pmt)) pmtPid = pmt;
            else if (pid == pmtPid && MpegTs.TryParsePmt(p, out var streams))
            {
                foreach (var st in streams)
                {
                    if (videoPid < 0 && MpegTs.IsSplittableVideo(st.StreamType)) videoPid = st.Pid;
                    else if (st.Pid != videoPid) audio.Add(st.Pid);
                }
            }
        }
        for (int i = 0; i + MpegTs.PacketSize <= psi.Length; i += MpegTs.PacketSize) Learn(psi.Slice(i, MpegTs.PacketSize));

        var state = new Dictionary<int, bool>();   // pid -> dropping the current PES
        var settled = new HashSet<int>();          // pid -> first kept PES seen: keep the rest
        var output = new byte[packets.Length];
        int o = 0;
        for (int i = 0; i + MpegTs.PacketSize <= packets.Length; i += MpegTs.PacketSize)
        {
            var p = packets.Slice(i, MpegTs.PacketSize);
            Learn(p);
            int pid = MpegTs.Pid(p);
            if (audio.Contains(pid) && !settled.Contains(pid))
            {
                if (MpegTs.PayloadUnitStart(p))
                {
                    bool early = MpegTs.TryReadPesPts(p, out long pts) && !MpegTs.AtOrAfter(pts, keyPtsRaw);
                    state[pid] = early;
                    if (!early) settled.Add(pid);
                }
                if (state.TryGetValue(pid, out bool drop) ? drop : true) continue;   // a PES that began before the lead: drop too
            }
            p.CopyTo(output.AsSpan(o));
            o += MpegTs.PacketSize;
        }
        return o == output.Length ? output : output.AsSpan(0, o).ToArray();
    }
}
