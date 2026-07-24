using System;

namespace WKI_Clipper.Services;

/// <summary>
/// Wall-clock pacing arithmetic for the audio writer loop. Pulled out of the loop so
/// it can be unit-tested: this is the maths that keeps the pipe alive while the
/// capture sources are idle WITHOUT ever producing audio faster than real time
/// (over-production is what caused the historical "audio seconds behind video" bug).
/// </summary>
internal static class AudioPacing
{
    /// <summary>
    /// How many frames the stream still owes: what wall-clock time says should have
    /// been produced minus what was actually written. Negative = we are ahead.
    /// </summary>
    public static long DeficitFrames(TimeSpan elapsed, long framesWritten, int sampleRate)
        => (long)(elapsed.TotalSeconds * sampleRate) - framesWritten;

    /// <summary>
    /// Frames of silence to inject when NO source produced samples. Returns 0 while
    /// the deficit is within <paramref name="tickFrames"/>*2 so ordinary capture
    /// jitter isn't papered over with silence that real samples would queue behind.
    /// Never returns more than one tick, so silence can only ever track real time.
    /// </summary>
    public static int SilenceFramesToWrite(long deficitFrames, int tickFrames)
    {
        if (tickFrames <= 0) return 0;
        if (deficitFrames < 2L * tickFrames) return 0;
        return (int)Math.Min(deficitFrames, tickFrames);
    }

    /// <summary>
    /// How many SAMPLES the microphone may contribute when it has to carry a tick on
    /// its own because the system source is idle. Bounded by the wall-clock deficit so
    /// the mic can never drive the stream length (the historical mic-stutter bug).
    /// </summary>
    public static int MicAllowanceSamples(long deficitFrames, int channels, int tickSamples)
    {
        if (deficitFrames <= 0 || channels <= 0 || tickSamples <= 0) return 0;
        long allowed = deficitFrames * channels;
        return (int)Math.Clamp(allowed, 0, tickSamples);
    }
}
