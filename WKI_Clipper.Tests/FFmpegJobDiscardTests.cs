using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// A job that writes nowhere (the measuring pass of a 2-pass encode: output "-f null NUL"): the runner
/// must not demand a ".part" file for it, must not create the final folder and must leave nothing behind.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FFmpegJobDiscardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_discard_" + Guid.NewGuid().ToString("N"));
    private readonly string? _ffmpeg = FFmpegTestSupport.Find();

    public FFmpegJobDiscardTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string PartsFile => Path.Combine(_dir, "parts.txt");

    private static string Source(int seconds)
        => $"-f lavfi -i testsrc2=size=320x180:rate=30 -t {seconds} -c:v libx264 -preset ultrafast -f null NUL";

    [Fact]
    public void Discarding_is_off_unless_asked_for()
        => Assert.False(new FFmpegJob("", "x", TimeSpan.Zero, 0).DiscardOutput);

    [Fact]
    public async Task A_discarding_job_succeeds_without_an_output_file_and_leaves_nothing()
    {
        if (_ffmpeg is null) return;
        using var runner = new FFmpegJobRunner(_ffmpeg, null, PartsFile);
        var placeholder = Path.Combine(_dir, "never-created", "stats");

        var result = await runner.RunAsync(
            new FFmpegJob(Source(2), placeholder, TimeSpan.FromSeconds(2), 0, DiscardOutput: true), null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message + Environment.NewLine + result.StderrTail);
        Assert.Null(result.OutputPath);
        Assert.False(Directory.Exists(Path.GetDirectoryName(placeholder)));
        Assert.False(File.Exists(placeholder + ".part"));
        Assert.True(!File.Exists(PartsFile) || File.ReadAllText(PartsFile).Trim().Length == 0);
    }

    [Fact]
    public async Task A_discarding_job_that_fails_still_reports_the_failure()
    {
        if (_ffmpeg is null) return;
        using var runner = new FFmpegJobRunner(_ffmpeg, null, PartsFile);

        var result = await runner.RunAsync(
            new FFmpegJob("-i \"" + Path.Combine(_dir, "missing.mp4") + "\" -f null NUL", Path.Combine(_dir, "stats"),
                          TimeSpan.FromSeconds(1), 0, DiscardOutput: true), null, CancellationToken.None);

        Assert.Equal(FFmpegJobStatus.Failed, result.Status);
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task A_discarding_job_does_not_touch_an_unrelated_part_file_at_the_placeholder()
    {
        if (_ffmpeg is null) return;
        using var runner = new FFmpegJobRunner(_ffmpeg, null, PartsFile);
        var placeholder = Path.Combine(_dir, "stats");
        File.WriteAllText(placeholder + ".part", "someone else's download");

        var result = await runner.RunAsync(
            new FFmpegJob(Source(1), placeholder, TimeSpan.FromSeconds(1), 0, DiscardOutput: true), null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("someone else's download", File.ReadAllText(placeholder + ".part"));
    }
}
