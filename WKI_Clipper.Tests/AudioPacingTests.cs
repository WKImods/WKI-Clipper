using System;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Guards the wall-clock pacing maths behind the "keep the pipe alive while idle" fix.
/// The two historical production bugs in this area were both OVER-production (silence
/// pumped faster than real time, and the mic driving the stream length), so most of
/// these tests assert that the helpers refuse to hand out more than real time allows.
/// </summary>
public sealed class AudioPacingTests
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int TickFrames = SampleRate / 100;      // 480 = 10 ms
    private const int TickSamples = TickFrames * Channels; // 960

    // ---- DeficitFrames ----

    [Fact]
    public void Deficit_is_zero_when_output_matches_the_clock()
        => Assert.Equal(0, AudioPacing.DeficitFrames(TimeSpan.FromSeconds(1), SampleRate, SampleRate));

    [Fact]
    public void Deficit_counts_the_frames_still_owed()
    {
        // 1 s elapsed, only half a second written → 24000 frames behind.
        long d = AudioPacing.DeficitFrames(TimeSpan.FromSeconds(1), SampleRate / 2, SampleRate);
        Assert.Equal(24000, d);
    }

    [Fact]
    public void Deficit_goes_negative_when_ahead_of_the_clock()
    {
        long d = AudioPacing.DeficitFrames(TimeSpan.FromSeconds(1), SampleRate * 2, SampleRate);
        Assert.True(d < 0);
    }

    // ---- SilenceFramesToWrite ----

    [Fact]
    public void No_silence_while_within_the_jitter_slack()
    {
        Assert.Equal(0, AudioPacing.SilenceFramesToWrite(0, TickFrames));
        Assert.Equal(0, AudioPacing.SilenceFramesToWrite(TickFrames, TickFrames));          // 1 tick behind
        Assert.Equal(0, AudioPacing.SilenceFramesToWrite(2 * TickFrames - 1, TickFrames));  // just under
    }

    [Fact]
    public void Silence_starts_once_the_slack_is_exceeded()
        => Assert.Equal(TickFrames, AudioPacing.SilenceFramesToWrite(2 * TickFrames, TickFrames));

    [Fact]
    public void Silence_never_exceeds_one_tick_even_with_a_huge_deficit()
    {
        // A 10-minute deficit must NOT be flushed at once — that is exactly the old
        // "seconds of fake audio" bug. One tick per iteration, paced by the clock.
        int frames = AudioPacing.SilenceFramesToWrite(SampleRate * 600L, TickFrames);
        Assert.Equal(TickFrames, frames);
    }

    [Fact]
    public void Negative_deficit_never_produces_silence()
        => Assert.Equal(0, AudioPacing.SilenceFramesToWrite(-SampleRate, TickFrames));

    [Fact]
    public void Silence_sequence_tracks_real_time_and_cannot_run_ahead()
    {
        // Simulate 1 s of a fully idle device: repeatedly ask for silence and feed the
        // result back, asserting we never write more than the elapsed time allows.
        long written = 0;
        for (int ms = 0; ms <= 1000; ms += 10)
        {
            long deficit = AudioPacing.DeficitFrames(TimeSpan.FromMilliseconds(ms), written, SampleRate);
            written += AudioPacing.SilenceFramesToWrite(deficit, TickFrames);

            long allowedByClock = (long)(ms / 1000.0 * SampleRate);
            Assert.True(written <= allowedByClock,
                $"at {ms} ms: wrote {written} frames but the clock only allows {allowedByClock}");
        }
        // And it must actually keep up (within the 2-tick slack), not stall.
        Assert.True(written >= SampleRate - 2 * TickFrames, $"only wrote {written} frames in 1 s");
    }

    // ---- MicAllowanceSamples ----

    [Fact]
    public void Mic_may_fill_a_full_tick_when_far_enough_behind()
        => Assert.Equal(TickSamples, AudioPacing.MicAllowanceSamples(SampleRate, Channels, TickSamples));

    [Fact]
    public void Mic_allowance_is_capped_at_one_tick()
        => Assert.Equal(TickSamples, AudioPacing.MicAllowanceSamples(SampleRate * 60L, Channels, TickSamples));

    [Fact]
    public void Mic_allowance_scales_with_a_small_deficit()
    {
        // 100 frames behind → 100 frames * 2 channels of samples.
        Assert.Equal(200, AudioPacing.MicAllowanceSamples(100, Channels, TickSamples));
    }

    [Fact]
    public void Mic_gets_nothing_when_not_behind()
    {
        Assert.Equal(0, AudioPacing.MicAllowanceSamples(0, Channels, TickSamples));
        Assert.Equal(0, AudioPacing.MicAllowanceSamples(-500, Channels, TickSamples));
    }

    [Fact]
    public void Degenerate_arguments_are_handled()
    {
        Assert.Equal(0, AudioPacing.SilenceFramesToWrite(10_000, 0));
        Assert.Equal(0, AudioPacing.MicAllowanceSamples(10_000, 0, TickSamples));
        Assert.Equal(0, AudioPacing.MicAllowanceSamples(10_000, Channels, 0));
    }
}
