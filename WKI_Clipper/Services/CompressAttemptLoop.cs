using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WKI_Clipper.Services;

/// <summary>One encode the loop asks for.</summary>
/// <param name="Number">1-based, counts every run including failed ones.</param>
internal readonly record struct AttemptSpec(int Number, int RungIndex, long RequestBps, bool Software);

internal enum AttemptOutcome { Ok, Failed, Cancelled }

/// <param name="FileBytes">Size of the finished file (Ok only).</param>
/// <param name="OutputPath">The finished file (Ok only).</param>
internal sealed record AttemptRun(AttemptOutcome Outcome, long FileBytes, string? OutputPath, string Message);

/// <param name="TooLong">Every attempt, software included, ended over the limit.</param>
internal sealed record LoopResult(
    CompressStatus Status, string? OutputPath, long OutputBytes, int Attempts, string Message, bool TooLong);

/// <summary>
/// Runs encode attempts until one fits the size limit, following <see cref="SizeTargetPlanner.NextAttempt"/>.
/// The encode itself is a delegate, so the whole policy - overshoot, quality floor, software fallback,
/// failures - is tested with a simulated encoder instead of a real one.
/// </summary>
internal static class CompressAttemptLoop
{
    /// <summary>The software retries after a hardware failure come on top of the normal cap, but not without end.</summary>
    private const int HardRunCap = SizeTargetPlanner.MaxAttempts + 2;

    /// <param name="softwarePrimary">libx264 is the encoder (no hardware one): there is no fallback below it.</param>
    /// <param name="run">One attempt; reports Ok with the finished file, or Failed / Cancelled.</param>
    /// <param name="discard">Deletes an attempt's file that is not the result.</param>
    public static async Task<LoopResult> RunAsync(
        SizePlan plan, bool softwarePrimary, Func<AttemptSpec, Task<AttemptRun>> run, Action<string> discard)
    {
        var history = new List<AttemptRecord>();
        bool softwareFallbackAllowed = !softwarePrimary;
        var spec = new AttemptSpec(1, plan.StartIndex, plan.Start.VideoBps, softwarePrimary);

        while (true)
        {
            var result = await run(spec).ConfigureAwait(false);

            if (result.Outcome == AttemptOutcome.Cancelled)
                return new LoopResult(CompressStatus.Cancelled, null, 0, spec.Number, L.T("Abgebrochen.", "Cancelled."), false);

            if (result.Outcome == AttemptOutcome.Failed)
            {
                // A hardware encoder that cannot even run (driver, busy, unsupported input) is not worth more
                // tries: carry on in software from the same rung's budget. A software failure is final.
                if (spec.Software || spec.Number >= HardRunCap)
                    return new LoopResult(CompressStatus.Failed, null, 0, spec.Number, result.Message, false);

                softwareFallbackAllowed = false;
                spec = new AttemptSpec(spec.Number + 1, spec.RungIndex, plan.Rungs[spec.RungIndex].VideoBps, true);
                continue;
            }

            var path = result.OutputPath!;
            history.Add(new AttemptRecord(spec.RungIndex, spec.RequestBps,
                plan.ActualVideoBps(spec.RungIndex, result.FileBytes), result.FileBytes, spec.Software));

            var step = SizeTargetPlanner.NextAttempt(plan, history, softwareFallbackAllowed);
            if (step.Kind == AttemptStepKind.Done)
                return new LoopResult(CompressStatus.Succeeded, path, result.FileBytes, spec.Number, "", false);

            discard(path);   // over the limit: not a result

            if (step.Kind == AttemptStepKind.GiveUp || spec.Number >= HardRunCap)
                return new LoopResult(CompressStatus.Failed, null, 0, spec.Number,
                    L.T("Auch mit mehreren Versuchen unter dem Limit nicht erreichbar.",
                        "The limit could not be met even after several attempts."), true);

            if (step.Kind == AttemptStepKind.SoftwareFallback) softwareFallbackAllowed = false;
            spec = new AttemptSpec(spec.Number + 1, step.RungIndex, step.RequestBps, step.Software);
        }
    }
}
