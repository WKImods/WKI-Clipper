using System;

namespace WKI_Clipper.Services;

/// <summary>
/// Sizing of the segment ring, in one place so ffmpeg's segment_wrap, the prune rule, the
/// number of segments a save picks and the watchdog's size limit can never drift apart.
///
/// Why the reserve exists: the segment muxer wraps around and TRUNCATES the next file name when
/// it opens a segment. A save concatenates the newest <see cref="PickCount"/> complete segments,
/// so with a wrap of PickCount + 1 the oldest segment of the save was exactly the next one to be
/// overwritten - a concat that takes a few seconds could read a file the muxer had just emptied.
/// <see cref="Reserve"/> extra slots keep the oldest picked segment that many openings away from
/// being reused. A save still picks only <see cref="PickCount"/> segments, so the clip length
/// does not change; the ring just holds a little more than it hands out.
/// </summary>
public readonly record struct RingPlan(int SegmentSeconds, int PickCount, int Wrap, int Keep)
{
    /// <summary>Extra ring slots between the oldest segment of a save and the next overwrite.</summary>
    public const int Reserve = 6;

    public static RingPlan Create(int durationSeconds, int segmentSeconds)
    {
        int seg = Math.Max(1, segmentSeconds);
        int dur = Math.Max(seg, durationSeconds);
        int pick = (int)Math.Ceiling((double)dur / seg);
        // pick + 1 was the tail tolerance before; Reserve is the new safety margin on top.
        int wrap = pick + 1 + Reserve;
        // Window we might save + one full ring of slack for the previous generation after a warm
        // restart. Prune keeps the newest `Keep` files; the watchdog size limit derives from it.
        int keep = pick + wrap + 2;
        return new RingPlan(seg, pick, wrap, keep);
    }

    /// <summary>Segment openings before the oldest picked segment's file name is reused.</summary>
    public int OverwriteDistance => Wrap - PickCount;

    /// <summary>
    /// A save may want fewer segments than the ring picks (GIF), never more: picking beyond
    /// <see cref="PickCount"/> eats into the reserve.
    /// </summary>
    public int ClampPick(int wanted) => Math.Clamp(wanted, 1, PickCount);
}
