using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// The attempt loop with a simulated encoder: no ffmpeg, but every branch of the policy
/// (overshoot, quality floor, software fallback, failures) runs end to end.
/// </summary>
public sealed class CompressAttemptLoopTests
{
    // 30 s of 1920x1080@60 into 20 MB: starts at 1080p30 (rung 1), 720p30 is rung 3.
    private static SizePlan Plan() => SizeTargetPlanner.Plan(30, 1920, 1080, 60, 20_000_000);

    private sealed class Rig
    {
        public required SizePlan Plan { get; init; }
        public List<AttemptSpec> Specs { get; } = new();
        public List<string> Discarded { get; } = new();
        public Func<AttemptSpec, AttemptRun>? Behaviour { get; set; }

        public Task<AttemptRun> Run(AttemptSpec spec)
        {
            Specs.Add(spec);
            return Task.FromResult(Behaviour!(spec));
        }

        public AttemptRun Bytes(AttemptSpec spec, double actualVideoBps)
        {
            var r = Plan.Rungs[spec.RungIndex];
            long bytes = (long)(actualVideoBps * Plan.DurationSec / 8) + r.AudioBytes + r.MuxBytes;
            return new AttemptRun(AttemptOutcome.Ok, bytes, "attempt" + spec.Number, "");
        }

        public Task<LoopResult> Go(bool softwarePrimary = false)
            => CompressAttemptLoop.RunAsync(Plan, softwarePrimary, Run, Discarded.Add);
    }

    private static Rig NewRig(SizePlan? plan = null) => new() { Plan = plan ?? Plan() };

    /// <summary>A hardware encoder: delivers 98 % of the request, but never less than a bits-per-pixel floor.</summary>
    private static Func<AttemptSpec, AttemptRun> HardwareWithFloor(Rig rig, double floorBpp, double overshoot = 1.0)
        => spec =>
        {
            var r = rig.Plan.Rungs[spec.RungIndex];
            if (spec.Software) return rig.Bytes(spec, spec.RequestBps * 0.97);   // 2-pass software is accurate
            double floor = floorBpp * r.Width * r.Height * r.Fps;
            return rig.Bytes(spec, Math.Max(spec.RequestBps * 0.98 * overshoot, floor));
        };

    [Fact]
    public async Task A_healthy_encoder_needs_one_attempt_and_deletes_nothing()
    {
        var rig = NewRig();
        rig.Behaviour = HardwareWithFloor(rig, floorBpp: 0.0);

        var result = await rig.Go();

        Assert.Equal(CompressStatus.Succeeded, result.Status);
        Assert.Equal(1, result.Attempts);
        Assert.Equal("attempt1", result.OutputPath);
        Assert.Empty(rig.Discarded);
        Assert.Equal(rig.Plan.StartIndex, rig.Specs[0].RungIndex);
        Assert.Equal(rig.Plan.Start.VideoBps, rig.Specs[0].RequestBps);
        Assert.False(rig.Specs[0].Software);
        Assert.True(result.OutputBytes <= rig.Plan.TargetBytes);
    }

    [Fact]
    public async Task A_proportional_overshoot_is_fixed_by_the_second_attempt_and_the_first_file_is_deleted()
    {
        var rig = NewRig();
        rig.Behaviour = HardwareWithFloor(rig, floorBpp: 0.0, overshoot: 1.10);   // +10 % on every request

        var result = await rig.Go();

        Assert.Equal(CompressStatus.Succeeded, result.Status);
        Assert.Equal(2, result.Attempts);
        Assert.Equal("attempt2", result.OutputPath);
        Assert.Equal(new[] { "attempt1" }, rig.Discarded);
        Assert.Equal(rig.Specs[0].RungIndex, rig.Specs[1].RungIndex);
        Assert.True(rig.Specs[1].RequestBps < rig.Specs[0].RequestBps);
    }

    [Fact]
    public async Task A_quality_floor_above_the_planned_bits_per_pixel_moves_down_a_rung_without_wasting_attempts()
    {
        var rig = NewRig();
        // Start rung 1080p30 plans 0.082 bpp; this encoder cannot do better than 0.12 there.
        rig.Behaviour = HardwareWithFloor(rig, floorBpp: 0.12);

        var result = await rig.Go();

        Assert.Equal(CompressStatus.Succeeded, result.Status);
        Assert.Equal(3, result.Attempts);   // miss, miss + floor detected, 720p30 fits
        var last = rig.Plan.Rungs[rig.Specs[^1].RungIndex];
        Assert.Equal((1280, 720, 30), (last.Width, last.Height, last.Fps));
        Assert.Equal(last.VideoBps, rig.Specs[^1].RequestBps);   // asked from that rung's own budget
        Assert.Equal(new[] { "attempt1", "attempt2" }, rig.Discarded);
        Assert.Equal("attempt3", result.OutputPath);
        Assert.All(rig.Specs, s => Assert.False(s.Software));
    }

    [Fact]
    public async Task An_encoder_that_never_gets_there_ends_in_the_software_fallback_on_the_lowest_rung()
    {
        var rig = NewRig();
        rig.Behaviour = HardwareWithFloor(rig, floorBpp: 10.0);   // hardware is hopeless

        var result = await rig.Go();

        Assert.Equal(CompressStatus.Succeeded, result.Status);
        Assert.Equal(SizeTargetPlanner.MaxAttempts + 1, result.Attempts);
        var fallback = rig.Specs[^1];
        Assert.True(fallback.Software);
        Assert.Equal(rig.Plan.Rungs.Count - 1, fallback.RungIndex);
        Assert.Equal(rig.Plan.Rungs[^1].VideoBps, fallback.RequestBps);
        Assert.Equal(SizeTargetPlanner.MaxAttempts, rig.Discarded.Count);   // every hardware miss was deleted
        Assert.Equal(fallback.Number, int.Parse(result.OutputPath!["attempt".Length..]));
    }

    [Fact]
    public async Task When_the_software_fallback_misses_too_the_loop_gives_up_and_deletes_the_file()
    {
        var rig = NewRig();
        rig.Behaviour = spec => rig.Bytes(spec, rig.Plan.Rungs[spec.RungIndex].VideoBps * 1.5);   // nothing fits, ever

        var result = await rig.Go();

        Assert.Equal(CompressStatus.Failed, result.Status);
        Assert.True(result.TooLong);
        Assert.Null(result.OutputPath);
        Assert.Equal(rig.Specs.Count, rig.Discarded.Count);   // not a single oversize file is left behind
        Assert.True(rig.Specs[^1].Software);
        Assert.Equal(1, rig.Specs.Count(s => s.Software));    // the fallback is tried once
    }

    [Fact]
    public async Task A_broken_hardware_encoder_continues_in_software_on_the_same_rung()
    {
        var rig = NewRig();
        rig.Behaviour = spec => spec.Software
            ? rig.Bytes(spec, spec.RequestBps * 0.97)
            : new AttemptRun(AttemptOutcome.Failed, 0, null, "AMF init failed");

        var result = await rig.Go();

        Assert.Equal(CompressStatus.Succeeded, result.Status);
        Assert.Equal(2, result.Attempts);
        Assert.False(rig.Specs[0].Software);
        Assert.True(rig.Specs[1].Software);
        Assert.Equal(rig.Specs[0].RungIndex, rig.Specs[1].RungIndex);
        Assert.Equal(rig.Plan.Rungs[rig.Specs[0].RungIndex].VideoBps, rig.Specs[1].RequestBps);
        Assert.Empty(rig.Discarded);
    }

    [Fact]
    public async Task A_broken_software_encoder_fails_with_its_own_message()
    {
        var rig = NewRig();
        rig.Behaviour = _ => new AttemptRun(AttemptOutcome.Failed, 0, null, "disk full");

        var result = await rig.Go();

        Assert.Equal(CompressStatus.Failed, result.Status);
        Assert.False(result.TooLong);
        Assert.Equal("disk full", result.Message);
        Assert.Equal(2, rig.Specs.Count);   // hardware, then software - then nothing is left
    }

    [Fact]
    public async Task A_cancel_ends_the_loop_at_once()
    {
        var rig = NewRig();
        rig.Behaviour = _ => new AttemptRun(AttemptOutcome.Cancelled, 0, null, "");

        var result = await rig.Go();

        Assert.Equal(CompressStatus.Cancelled, result.Status);
        Assert.Single(rig.Specs);
    }

    [Fact]
    public async Task Software_as_the_only_encoder_retries_with_the_correction_and_has_no_fallback()
    {
        var rig = NewRig();
        rig.Behaviour = spec => rig.Bytes(spec, spec.RequestBps * 1.05);   // x264 two-pass miss of 5 %

        var result = await rig.Go(softwarePrimary: true);

        Assert.Equal(CompressStatus.Succeeded, result.Status);
        Assert.Equal(2, result.Attempts);
        Assert.All(rig.Specs, s => Assert.True(s.Software));
        Assert.Equal(rig.Plan.StartIndex, rig.Specs[0].RungIndex);
    }

    [Fact]
    public async Task Software_that_keeps_missing_gives_up_after_five_attempts()
    {
        var rig = NewRig();
        rig.Behaviour = spec => rig.Bytes(spec, rig.Plan.Rungs[spec.RungIndex].VideoBps * 1.5);

        var result = await rig.Go(softwarePrimary: true);

        Assert.Equal(CompressStatus.Failed, result.Status);
        Assert.True(result.TooLong);
        Assert.True(rig.Specs.Count <= SizeTargetPlanner.MaxAttempts + 2);
        Assert.Equal(rig.Specs.Count, rig.Discarded.Count);
    }

    [Fact]
    public async Task Attempt_numbers_count_up_from_one()
    {
        var rig = NewRig();
        rig.Behaviour = HardwareWithFloor(rig, floorBpp: 0.0, overshoot: 1.10);

        await rig.Go();

        Assert.Equal(Enumerable.Range(1, rig.Specs.Count), rig.Specs.Select(s => s.Number));
    }
}
