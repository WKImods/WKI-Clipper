using System;
using System.Collections.Generic;
using System.Linq;

namespace WKI_Clipper.Services;

/// <summary>
/// One step of the resolution / frame-rate ladder with the bitrate budget that fits the size limit.
/// </summary>
/// <param name="Width">Output width in pixels (even). The filter is <c>scale={Width}:-2</c>.</param>
/// <param name="Height">Output height in pixels (even).</param>
/// <param name="VideoBps">Video bitrate to ask the encoder for.</param>
/// <param name="AudioKbps">Audio bitrate, 0 when the clip has no audio.</param>
/// <param name="Bpp">Bits per pixel: <c>VideoBps / (Width x Height x Fps)</c> - the quality measure the ladder is judged on.</param>
/// <param name="AudioBytes">Planned size of the audio track (incl. 2 % slack).</param>
/// <param name="MuxBytes">Planned container overhead.</param>
public sealed record CompressRung(
    int Width, int Height, int Fps, long VideoBps, int AudioKbps, double Bpp, long AudioBytes, long MuxBytes);

/// <summary>
/// The outcome of <see cref="SizeTargetPlanner.Plan"/>: the whole ladder (so a retry can step down
/// without planning again) and where to start. <see cref="StartIndex"/> is -1 when even the lowest
/// rung is too thin to be worth encoding.
/// </summary>
public sealed record SizePlan(IReadOnlyList<CompressRung> Rungs, int StartIndex, long TargetBytes, double DurationSec)
{
    public bool TooLong => StartIndex < 0;

    public CompressRung Start => StartIndex >= 0
        ? Rungs[StartIndex]
        : throw new InvalidOperationException("The clip does not fit the target - there is no start rung.");

    /// <summary>
    /// The video rate an encoder actually delivered, derived from the size of its file by taking
    /// away the planned audio and container share. Never below 1.
    /// </summary>
    public long ActualVideoBps(int rungIndex, long fileBytes)
    {
        var rung = Rungs[rungIndex];
        double videoBits = (fileBytes - rung.AudioBytes - rung.MuxBytes) * 8.0;
        return Math.Max(1L, (long)(videoBits / Math.Max(0.001, DurationSec)));
    }
}

/// <summary>One finished encode attempt, as the retry policy needs to see it.</summary>
/// <param name="RequestBps">The video bitrate that was asked for.</param>
/// <param name="ActualVideoBps">The video rate that came out (<see cref="SizePlan.ActualVideoBps"/>).</param>
/// <param name="Software">libx264 2-pass instead of the hardware encoder.</param>
public sealed record AttemptRecord(int RungIndex, long RequestBps, long ActualVideoBps, long FileBytes, bool Software);

public enum AttemptStepKind
{
    /// <summary>The last file fits - keep it.</summary>
    Done,
    /// <summary>Same rung, lower request.</summary>
    Retry,
    /// <summary>The encoder cannot go lower at this resolution: a smaller rung, requested from its own budget.</summary>
    NextRung,
    /// <summary>libx264 2-pass on the lowest rung.</summary>
    SoftwareFallback,
    /// <summary>Nothing left to try.</summary>
    GiveUp
}

public sealed record AttemptStep(AttemptStepKind Kind, int RungIndex, long RequestBps, bool Software);

/// <summary>
/// Everything about "how do I get this clip under N bytes" that needs no process: which resolution
/// and frame rate the size allows, which bitrate to request, and what to do when a hardware encoder
/// misses the target. Pure so every threshold is a table-driven test.
///
/// Why this much machinery: a hardware encoder (AMF) has no file-size 2-pass. It hits a size target
/// well only while the bitrate stays above a quality floor of about 0.035 bits per pixel; below it,
/// it overshoots by 11-79 %. So the planner picks a resolution where the bitrate is comfortable,
/// and the retry policy notices when the floor is reached instead of lowering the request forever.
/// </summary>
public static class SizeTargetPlanner
{
    /// <summary>Share of the limit kept free (cbr + enforce_hrd lands at 97-98 % when above the floor).</summary>
    public const decimal SafetyMargin = 0.02m;

    /// <summary>A rung at this many bits per pixel or more is comfortable.</summary>
    public const double PreferredBpp = 0.05;

    /// <summary>Below this the hardware encoder cannot hold a size target - the clip is too long for the limit.</summary>
    public const double MinimumBpp = 0.035;

    /// <summary>Frame rates above 30 only make sense at this many bits per pixel.</summary>
    public const double HighFpsMinimumBpp = 0.045;

    public const int MaxAttempts = 5;

    /// <summary>Under this a retry has nothing left to lower.</summary>
    public const long MinVideoBps = 40_000;

    /// <summary>A step down must be worth it: at least this much more bits per pixel than the rung it leaves.</summary>
    private const double RungGainFactor = 1.15;

    /// <summary>Each retry asks for at least this much less than the one before.</summary>
    private const double RetryDamping = 0.97;

    /// <summary>Short side in pixels and frame rate of each rung, best first. 480p is the owner's minimum.</summary>
    private static readonly (int ShortSide, int Fps)[] Ladder =
    {
        (1080, 60), (1080, 30), (720, 60), (720, 30), (540, 30), (480, 30)
    };

    /// <param name="sourceBitsPerSecond">
    /// Overall bitrate of the source file. Caps the video rate: a constant-bitrate encoder spends every
    /// bit it is given, so without a cap "500 MB" would turn a 30 s clip into a 490 MB file.
    /// </param>
    /// <param name="sourceFps">0 or less = unknown, planned as 30.</param>
    /// <exception cref="ArgumentOutOfRangeException">Duration, size or target not positive.</exception>
    public static SizePlan Plan(double durationSec, int sourceWidth, int sourceHeight, double sourceFps, long targetBytes,
        bool hasAudio = true, long? sourceBitsPerSecond = null)
    {
        if (!(durationSec > 0) || double.IsInfinity(durationSec)) throw new ArgumentOutOfRangeException(nameof(durationSec));
        if (sourceWidth <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        if (sourceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceHeight));
        if (targetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(targetBytes));

        int fpsCap = double.IsNaN(sourceFps) || sourceFps <= 0 ? 30 : Math.Max(1, (int)Math.Round(sourceFps));
        long? videoCap = sourceBitsPerSecond is > 0 ? sourceBitsPerSecond : null;

        var rungs = new List<CompressRung>();
        foreach (var (shortSide, ladderFps) in Ladder)
        {
            int fps = Math.Min(ladderFps, fpsCap);
            var (w, h) = Scaled(sourceWidth, sourceHeight, shortSide);
            if (rungs.Count > 0 && rungs[^1].Width == w && rungs[^1].Height == h && rungs[^1].Fps == fps) continue;

            var budget = Budget(durationSec, fps, targetBytes, hasAudio, videoCap);
            double bpp = budget.VideoBps / ((double)w * h * fps);
            rungs.Add(new CompressRung(w, h, fps, budget.VideoBps, budget.AudioKbps, bpp, budget.AudioBytes, budget.MuxBytes));
        }

        return new SizePlan(rungs, ChooseStart(rungs), targetBytes, durationSec);
    }

    /// <summary>
    /// First rung with at least <see cref="PreferredBpp"/>, else the first with at least
    /// <see cref="MinimumBpp"/>; a rung above 30 fps must also reach <see cref="HighFpsMinimumBpp"/>.
    /// -1 when nothing qualifies.
    /// </summary>
    internal static int ChooseStart(IReadOnlyList<CompressRung> rungs)
    {
        foreach (var threshold in new[] { PreferredBpp, MinimumBpp })
            for (int i = 0; i < rungs.Count; i++)
                if (Admissible(rungs[i], threshold)) return i;
        return -1;
    }

    private static bool Admissible(CompressRung rung, double threshold)
        => rung.Bpp >= threshold && (rung.Fps <= 30 || rung.Bpp >= HighFpsMinimumBpp);

    /// <summary>
    /// Short side of the result is the rung's height, but never more than the source has (no upscaling);
    /// the other side follows the aspect ratio, so an ultrawide stays an ultrawide. Both sides even.
    /// </summary>
    private static (int Width, int Height) Scaled(int sourceWidth, int sourceHeight, int rungShortSide)
    {
        int shortSide = Math.Min(sourceWidth, sourceHeight);
        double scale = (double)Math.Min(rungShortSide, shortSide) / shortSide;
        return (Even(sourceWidth * scale), Even(sourceHeight * scale));
    }

    private static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2.0, MidpointRounding.AwayFromZero) * 2);

    /// <summary>
    /// The video bitrate that leaves room for audio and container inside the limit. The audio rate
    /// steps down (128 / 96 / 64 kbit/s) as the video share shrinks; each step is judged on the video
    /// rate that remains after THAT audio rate.
    /// </summary>
    private static (long VideoBps, int AudioKbps, long AudioBytes, long MuxBytes) Budget(
        double durationSec, int fps, long limitBytes, bool hasAudio, long? videoCap)
    {
        decimal dur = (decimal)durationSec;
        decimal usableBits = limitBytes * (1m - SafetyMargin) * 8m;
        // 12 bytes per sample entry; 47 = audio frames per second (48000 / 1024)
        decimal muxBytes = 2000m + 12m * dur * (fps + (hasAudio ? 47 : 0));

        foreach (int audioKbps in hasAudio ? new[] { 128, 96, 64 } : new[] { 0 })
        {
            decimal audioBytes = audioKbps * 1000m / 8m * dur * 1.02m;
            decimal video = Math.Floor((usableBits - audioBytes * 8m - muxBytes * 8m) / dur);

            bool fits = audioKbps switch { 128 => video >= 1_500_000m, 96 => video >= 600_000m, _ => true };
            if (!fits) continue;

            long videoBps = (long)Math.Max(0m, Math.Min(video, long.MaxValue));
            if (videoCap is { } cap) videoBps = Math.Min(videoBps, cap);
            return (videoBps, audioKbps, (long)Math.Ceiling(audioBytes), (long)Math.Ceiling(muxBytes));
        }
        throw new InvalidOperationException("unreachable: the last audio step always fits");
    }

    // ---- retry policy ----

    /// <summary>
    /// What to do after an attempt. The correction aims at the rung's BUDGET:
    /// <c>request = previous x (budget / actual) x 0.97</c> (and at least 3 % below the previous
    /// request). For the first retry this is exactly "previous x (previous / actual) x 0.97" because
    /// the first request is the budget; for later ones, aiming at the previous request instead would
    /// drift ever lower than needed.
    ///
    /// The quality floor is reached when, compared with an earlier attempt on the same rung, the
    /// request fell by more than 20 % while the result fell by less than 15 %. Then the next rung that
    /// brings at least 15 % more bits per pixel is requested from its own budget. After
    /// <see cref="MaxAttempts"/> attempts (or when no rung is left) libx264 2-pass on the lowest rung
    /// is the last resort, if <paramref name="softwareFallbackAllowed"/>; if that misses too, give up.
    /// </summary>
    /// <param name="history">Every attempt so far, oldest first. Not empty.</param>
    /// <param name="softwareFallbackAllowed">False when libx264 is already the encoder in use (no fallback below it).</param>
    public static AttemptStep NextAttempt(SizePlan plan, IReadOnlyList<AttemptRecord> history, bool softwareFallbackAllowed)
    {
        if (history.Count == 0) throw new ArgumentException("At least one attempt is needed.", nameof(history));
        var last = history[^1];

        if (last.FileBytes <= plan.TargetBytes)
            return new AttemptStep(AttemptStepKind.Done, last.RungIndex, last.RequestBps, last.Software);

        // The fallback itself missed: nothing below it.
        if (last.Software && history.Any(a => !a.Software))
            return GiveUp(last);

        if (history.Count >= MaxAttempts)
            return softwareFallbackAllowed && !last.Software ? SoftwareFallback(plan) : GiveUp(last);

        var rung = plan.Rungs[last.RungIndex];
        bool floor = !last.Software && FloorReached(history, last);

        double correction = (double)rung.VideoBps / Math.Max(1L, last.ActualVideoBps);
        long request = (long)Math.Floor(last.RequestBps * correction * RetryDamping);
        request = Math.Min(request, (long)Math.Floor(last.RequestBps * RetryDamping));
        if (request < MinVideoBps) floor = true;

        return floor
            ? StepDown(plan, last, softwareFallbackAllowed)
            : new AttemptStep(AttemptStepKind.Retry, last.RungIndex, request, last.Software);
    }

    private static bool FloorReached(IReadOnlyList<AttemptRecord> history, AttemptRecord last)
    {
        for (int i = 0; i < history.Count - 1; i++)
        {
            var earlier = history[i];
            if (earlier.RungIndex != last.RungIndex || earlier.Software) continue;

            bool requestFell = last.RequestBps < earlier.RequestBps * 0.80;
            bool resultBarelyMoved = last.ActualVideoBps > earlier.ActualVideoBps * 0.85;
            if (requestFell && resultBarelyMoved) return true;
        }
        return false;
    }

    private static AttemptStep StepDown(SizePlan plan, AttemptRecord last, bool softwareFallbackAllowed)
    {
        var current = plan.Rungs[last.RungIndex];
        for (int i = last.RungIndex + 1; i < plan.Rungs.Count; i++)
        {
            var candidate = plan.Rungs[i];
            if (candidate.Bpp >= current.Bpp * RungGainFactor && Admissible(candidate, MinimumBpp))
                return new AttemptStep(AttemptStepKind.NextRung, i, candidate.VideoBps, last.Software);
        }
        return softwareFallbackAllowed && !last.Software ? SoftwareFallback(plan) : GiveUp(last);
    }

    private static AttemptStep SoftwareFallback(SizePlan plan)
    {
        int lowest = plan.Rungs.Count - 1;
        return new AttemptStep(AttemptStepKind.SoftwareFallback, lowest, plan.Rungs[lowest].VideoBps, Software: true);
    }

    private static AttemptStep GiveUp(AttemptRecord last)
        => new(AttemptStepKind.GiveUp, last.RungIndex, last.RequestBps, last.Software);

    // ---- copy instead of encode ----

    /// <summary>
    /// A clip that already fits needs no encode - but only when it is already something Discord plays
    /// everywhere: H.264 video and exactly one AAC track. HEVC/AV1 or a separate mic track would not
    /// survive the trip, so those are re-encoded even when small.
    /// </summary>
    public static bool ShouldCopyInstead(MediaProbe probe, long sourceBytes, long targetBytes)
        => sourceBytes > 0
           && sourceBytes <= targetBytes
           && string.Equals(probe.VideoCodec, "h264", StringComparison.OrdinalIgnoreCase)
           && probe.AudioCodecs.Count == 1
           && string.Equals(probe.AudioCodecs[0], "aac", StringComparison.OrdinalIgnoreCase);
}
