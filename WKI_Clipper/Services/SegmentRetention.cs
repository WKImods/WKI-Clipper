using System;
using System.Collections.Generic;

namespace WKI_Clipper.Services;

public sealed record SegmentFileFacts(string Name, DateTime LastWriteUtc);

/// <param name="AgeWindowSeconds">See <see cref="SegmentRetention.AgeWindowSeconds"/>.</param>
/// <param name="WindowAlive">Is the captured window still there? Injected so the rule stays pure.</param>
public sealed record RetentionContext(DateTime NowUtc, DateTime SessionStartUtc, int AgeWindowSeconds,
    int CurrentGeneration, SegmentIdentity CurrentIdentity,
    IReadOnlyDictionary<int, GenerationInfo> Generations, Func<IntPtr, bool> WindowAlive);

/// <summary>
/// Which segment files may be deleted. A 30-minute ring is ~6.5 GB (up to ~25 GB at "very high"), so holding
/// the previous generation next to the current one (what a plain "keep the newest N files" rule does after a
/// warm restart) would double that. Segments are dropped as soon as they can never be part of a clip again.
/// </summary>
public static class SegmentRetention
{
    /// <summary>
    /// The footage a save can still reach plus slack: the larger of the running and the configured pick window,
    /// plus (<see cref="RingPlan.Reserve"/> + 2) segments for the open segment, prune lag and tail tolerance.
    /// </summary>
    public static int AgeWindowSeconds(RingPlan running, RingPlan configured)
    {
        long window = Math.Max((long)running.PickCount * running.SegmentSeconds,
                               (long)configured.PickCount * configured.SegmentSeconds);
        long slack = (long)(RingPlan.Reserve + 2) * Math.Max(running.SegmentSeconds, configured.SegmentSeconds);
        return (int)Math.Min(int.MaxValue, window + slack);
    }

    /// <summary>First matching rule wins.</summary>
    public static bool ShouldDelete(SegmentFileFacts f, RetentionContext c)
    {
        // 1) Not ours: only a file from before this session is safe to remove - a newer one may be the segment ffmpeg is
        //    creating right now.
        if (!SegmentName.TryParseGeneration(f.Name, out int gen)) return f.LastWriteUtc < c.SessionStartUtc;
        return ShouldDelete(gen, f.LastWriteUtc, c);
    }

    /// <summary>The same rules for a segment known by its generation and end time (the RAM ring has no file names).</summary>
    public static bool ShouldDelete(int gen, DateTime endUtc, RetentionContext c)
    {
        var f = new SegmentFileFacts("", endUtc);
        // 1) An unknown generation: only a segment from before this session is safe to remove.
        if (!c.Generations.TryGetValue(gen, out var info))
            return f.LastWriteUtc < c.SessionStartUtc;

        // 2) Earlier session / other monitor: unusable by definition.
        if (f.LastWriteUtc < c.SessionStartUtc) return true;

        bool tooOld = f.LastWriteUtc < c.NowUtc - TimeSpan.FromSeconds(c.AgeWindowSeconds);

        // 3) The running generation: the ring overwrites its own files, so only age matters.
        if (gen == c.CurrentGeneration) return tooOld;

        // 4) Different codec/fps/resolution/keyframes: the user changed the recording settings, so the old footage can
        //    never be concatenated with what records now. The previous generation is gone at once - a quality change
        //    in minute 20 of a 30-minute ring must not leave two rings on disk.
        //    Only the SETTINGS count here. The capture path and the audio track count follow the capture target: an
        //    Alt-Tab from an exclusive-fullscreen game ("dda") to a window ("wgc") must not delete the game's
        //    footage. Such a generation is still never joined into a clip with the current one (the full stream key
        //    differs) - it just stays until it ages out (rule 6), and F9 after returning to the game gets it back.
        if (info.Identity.SettingsKey != c.CurrentIdentity.SettingsKey) return true;

        // 5) Another window whose window is gone (game closed): nothing will ever record into that identity again.
        if (info.Identity.IsWgc && info.Identity.Source != c.CurrentIdentity.Source
            && !c.WindowAlive(info.Identity.WgcHwnd))
            return true;

        // 6) / 7) Same stream, still reachable: keep it until it ages out.
        return tooOld;
    }
}
