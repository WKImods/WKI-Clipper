using System;
using System.Collections.Generic;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Pure frame-time math behind the FPS overlay. Timestamps are synthetic QPC ticks at
/// 10 MHz (the QPC frequency of current Windows), so 1 ms = 10 000 ticks.
/// </summary>
public class FrameStatsTests
{
    private const long Freq = 10_000_000;
    private const long T0 = 5_000_000_000;   // arbitrary non-zero QPC start

    private static long Ticks(double ms) => (long)Math.Round(ms * Freq / 1000.0);

    /// <summary>Feeds presents with the given frame times; the first present is the baseline.</summary>
    private static long Feed(FrameStats s, long start, IEnumerable<double> frameTimesMs, bool baseline = true)
    {
        long t = start;
        if (baseline) s.Add(t);
        foreach (var ms in frameTimesMs)
        {
            t += Ticks(ms);
            s.Add(t);
        }
        return t;
    }

    private static IEnumerable<double> Repeat(double ms, int count)
    {
        for (int i = 0; i < count; i++) yield return ms;
    }

    [Fact]
    public void Nothing_fed_means_nothing_known()
    {
        var m = new FrameStats(Freq).Compute();
        Assert.Null(m.Fps);
        Assert.Null(m.AvgFrameTimeMs);
        Assert.Null(m.Low1);
        Assert.Null(m.Low01);
        Assert.Null(m.StutterPercent);
    }

    [Fact]
    public void A_steady_60_fps_stream_reads_60_everywhere()
    {
        var s = new FrameStats(Freq);
        Feed(s, T0, Repeat(1000.0 / 60, 600));   // 10 s

        var m = s.Compute();
        Assert.Equal(60, m.Fps!.Value, 1);
        Assert.Equal(16.667, m.AvgFrameTimeMs!.Value, 2);
        Assert.Equal(60, m.Low1!.Value, 1);
        Assert.Equal(60, m.Low01!.Value, 1);
        Assert.Equal(0, m.StutterPercent!.Value, 3);
    }

    [Fact]
    public void Average_fps_only_looks_at_the_last_second()
    {
        var s = new FrameStats(Freq);
        long t = Feed(s, T0, Repeat(20, 100));          // 2 s at 50 fps
        Feed(s, t, Repeat(10, 100), baseline: false);   // then 1 s at 100 fps

        var m = s.Compute();
        Assert.Equal(100, m.Fps!.Value, 1);
        Assert.Equal(10, m.AvgFrameTimeMs!.Value, 2);
    }

    [Fact]
    public void Fps_needs_at_least_half_a_second_of_frames()
    {
        var s = new FrameStats(Freq);
        Feed(s, T0, Repeat(10, 40));   // 400 ms
        Assert.Null(s.Compute().Fps);

        var s2 = new FrameStats(Freq);
        Feed(s2, T0, Repeat(10, 50));  // 500 ms
        Assert.Equal(100, s2.Compute().Fps!.Value, 1);
    }

    [Fact]
    public void A_single_long_frame_still_yields_an_fps_value()
    {
        // A loading screen at 0.7 fps: one 1.4 s frame covers more than the 1 s window.
        var s = new FrameStats(Freq);
        Feed(s, T0, new[] { 1400.0 });
        Assert.Equal(1000.0 / 1400, s.Compute().Fps!.Value, 3);
    }

    [Fact]
    public void Lows_and_stutter_need_at_least_100_frames()
    {
        var s = new FrameStats(Freq);
        Feed(s, T0, Repeat(10, 99));
        var m = s.Compute();
        Assert.NotNull(m.Fps);          // 990 ms of frames is enough for the average
        Assert.Null(m.Low1);
        Assert.Null(m.Low01);
        Assert.Null(m.StutterPercent);

        s.Add(T0 + Ticks(1000));        // the 100th frame
        m = s.Compute();
        Assert.Equal(100, s.FrameCount);
        Assert.Equal(100, m.Low1!.Value, 1);
        Assert.Equal(100, m.Low01!.Value, 1);
        Assert.Equal(0, m.StutterPercent!.Value, 3);
    }

    /// <summary>
    /// The time-based definition: sort descending, accumulate frame time until 1 % (0.1 %) of
    /// the window's duration is covered, report the FPS of the frame that crosses the line.
    /// 1 x 100 ms + 20 x 30 ms + 979 x 10 ms = 10 490 ms. 0.1 % = 10.49 ms: the 100 ms frame
    /// alone crosses it -> 10 fps. 1 % = 104.9 ms: 100 + 30 crosses it -> 33.3 fps.
    /// (The count-based "average of the worst 10 frames" would give 27 fps instead.)
    /// </summary>
    [Fact]
    public void One_and_point_one_percent_lows_use_the_time_based_definition()
    {
        var frames = new List<double>();
        for (int i = 0; i < 1000; i++)
        {
            if (i == 500) frames.Add(100);
            else if (i % 50 == 25) frames.Add(30);   // 20 of them, spread out
            else frames.Add(10);
        }
        Assert.Equal(20, frames.FindAll(f => f == 30).Count);

        var s = new FrameStats(Freq);
        Feed(s, T0, frames);
        var m = s.Compute();

        Assert.Equal(1000.0 / 30, m.Low1!.Value, 2);
        Assert.Equal(10, m.Low01!.Value, 2);
    }

    [Fact]
    public void Lows_never_exceed_the_average()
    {
        var rnd = new Random(42);
        var frames = new List<double>();
        for (int i = 0; i < 3000; i++) frames.Add(5 + rnd.NextDouble() * 15);

        var s = new FrameStats(Freq);
        Feed(s, T0, frames);
        var m = s.Compute();

        Assert.True(m.Low01 <= m.Low1, $"0.1 % low {m.Low01} above 1 % low {m.Low1}");
        Assert.True(m.Low1 <= m.Fps, $"1 % low {m.Low1} above average {m.Fps}");
    }

    [Fact]
    public void Micro_stutter_counts_frames_longer_than_twice_the_median()
    {
        // 950 x 10 ms, 30 x 25 ms (stutter), 20 x 20 ms (exactly 2x median: not stutter).
        var frames = new List<double>();
        for (int i = 0; i < 1000; i++)
            frames.Add(i % 50 == 7 ? 20 : (i % 100 < 3 ? 25 : 10));
        Assert.Equal(30, frames.FindAll(f => f == 25).Count);
        Assert.Equal(20, frames.FindAll(f => f == 20).Count);

        var s = new FrameStats(Freq);
        Feed(s, T0, frames);

        Assert.Equal(3.0, s.Compute().StutterPercent!.Value, 3);
    }

    [Fact]
    public void A_gap_over_two_seconds_starts_over()
    {
        var s = new FrameStats(Freq);
        long t = Feed(s, T0, Repeat(10, 300));
        Assert.NotNull(s.Compute().Low1);

        // Alt-tab: the game stops presenting for 2.5 s. The gap itself is not a frame.
        t += Ticks(2500);
        s.Add(t);
        Assert.Equal(0, s.FrameCount);

        Feed(s, t, Repeat(10, 60), baseline: false);
        var m = s.Compute();
        Assert.Equal(60, s.FrameCount);
        Assert.Equal(100, m.Fps!.Value, 1);
        Assert.Null(m.Low1);   // the old 300 frames are gone
    }

    [Fact]
    public void A_gap_just_under_two_seconds_is_a_real_frame()
    {
        var s = new FrameStats(Freq);
        long t = Feed(s, T0, Repeat(10, 200));
        t += Ticks(1900);
        s.Add(t);

        Assert.Equal(201, s.FrameCount);
        Assert.Equal(1000.0 / 1900, s.Compute().Low01!.Value, 3);
    }

    [Fact]
    public void Out_of_order_and_duplicate_timestamps_are_ignored()
    {
        var s = new FrameStats(Freq);
        s.Add(T0);
        s.Add(T0 + Ticks(10));
        s.Add(T0 + Ticks(5));    // late event from another CPU buffer
        s.Add(T0 + Ticks(10));   // duplicate
        s.Add(T0 + Ticks(20));

        Assert.Equal(2, s.FrameCount);
    }

    [Fact]
    public void The_lows_window_rolls_over_old_frames()
    {
        var s = new FrameStats(Freq);
        long t = Feed(s, T0, Repeat(40, 125));         // 5 s of bad 25 fps
        Feed(s, t, Repeat(10, 2100), baseline: false); // then 21 s of 100 fps

        var m = s.Compute();
        Assert.Equal(100, m.Low1!.Value, 1);
        Assert.Equal(100, m.Low01!.Value, 1);
        Assert.InRange(s.WindowSeconds, FrameStats.LowsWindowSeconds - 0.05, FrameStats.LowsWindowSeconds + 0.05);
    }

    [Fact]
    public void Reset_forgets_frames_and_the_baseline()
    {
        var s = new FrameStats(Freq);
        Feed(s, T0, Repeat(10, 300));
        s.Reset();
        Assert.Equal(0, s.FrameCount);
        Assert.Null(s.Compute().Fps);

        // The next present is a new baseline, not a frame relative to the old stream.
        s.Add(T0 + Ticks(60_000));
        Assert.Equal(0, s.FrameCount);
        s.Add(T0 + Ticks(60_010));
        Assert.Equal(1, s.FrameCount);
    }

    [Fact]
    public void Very_high_frame_rates_stay_bounded()
    {
        // 5000 fps for 25 s would be 125 000 frames; the buffer is capped, the math still works.
        var s = new FrameStats(Freq);
        Feed(s, T0, Repeat(0.2, 125_000));
        var m = s.Compute();
        Assert.True(s.FrameCount <= FrameStats.MaxFrames);
        Assert.Equal(5000, m.Fps!.Value, 0);
        Assert.Equal(5000, m.Low1!.Value, 0);
    }
}
