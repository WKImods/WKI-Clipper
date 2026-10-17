using System;

namespace WKI_Clipper.Services;

/// <summary>
/// One ffmpeg encode for <see cref="FFmpegJobRunner"/>.
/// </summary>
/// <param name="Arguments">
/// Full ffmpeg arguments. The output MUST be <see cref="PartPath"/> (quoted), with the muxer named
/// explicitly (<c>-f mp4</c>) because ffmpeg cannot infer a format from the ".part" extension. The
/// runner adds <c>-hide_banner -nostats -n -progress pipe:1</c> in front and, when a thread cap is
/// set, <c>-threads N</c> before the output. It owns the overwrite policy: any <c>-y</c>/<c>-n</c>
/// in here is removed (ffmpeg refuses the combination of both).
/// </param>
/// <param name="FinalPath">Where the finished file ends up (a " (2)" suffix is added if it is taken).</param>
/// <param name="ExpectedDuration">Length of the result, for the progress fraction. Zero = unknown.</param>
/// <param name="EstimatedOutputBytes">Rough size of the result, for the free-disk-space check.</param>
/// <param name="ThreadCap">Optional encoder thread limit (keeps a long software encode from starving the game).</param>
/// <param name="StallTimeout">
/// A job whose encoded position does not advance for this long is terminated (a hung driver or a
/// vanished network input would otherwise block the queue forever). Null = 60 s, zero = never.
/// </param>
public sealed record FFmpegJob(
    string Arguments,
    string FinalPath,
    TimeSpan ExpectedDuration,
    long EstimatedOutputBytes,
    int? ThreadCap = null,
    TimeSpan? StallTimeout = null)
{
    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(60);

    public string PartPath => PartPathFor(FinalPath);

    public TimeSpan EffectiveStallTimeout => StallTimeout ?? DefaultStallTimeout;

    public static string PartPathFor(string finalPath) => finalPath + FFmpegJobRules.PartSuffix;
}

/// <param name="Fraction">0..1 of the expected duration; 0 while the duration is unknown.</param>
/// <param name="Position">Encoded media time so far.</param>
public readonly record struct FFmpegProgress(double Fraction, TimeSpan Position);

public enum FFmpegJobStatus { Succeeded, Failed, Cancelled }

/// <param name="OutputPath">The final file (set only on success).</param>
/// <param name="ExitCode">ffmpeg's exit code, or -1 when it never ran / was cancelled / was terminated.</param>
/// <param name="Message">Short, user-facing reason (bilingual) — empty on success.</param>
/// <param name="StderrTail">Last lines ffmpeg printed to stderr; the diagnosis of a failed job.</param>
public sealed record FFmpegJobResult(
    FFmpegJobStatus Status, string? OutputPath, int ExitCode, string Message, string StderrTail)
{
    public bool Succeeded => Status == FFmpegJobStatus.Succeeded;
}
