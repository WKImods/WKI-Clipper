using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.Models;
using WKI_Clipper.Views;

namespace WKI_Clipper.Services;

public enum CompressStage { Queued, WaitingForGame, Analyzing, Encoding, Copying }

/// <param name="Fraction">0..1 of the current attempt (a 2-pass encode: both passes together).</param>
/// <param name="Attempt">1-based number of the current encode; 0 before the first one starts.</param>
public readonly record struct CompressProgress(CompressStage Stage, double Fraction, int Attempt);

public enum CompressStatus { Succeeded, Failed, Cancelled }

/// <param name="Copied">The clip already fitted and was copied, not re-encoded.</param>
/// <param name="TooLong">Failed because the clip cannot fit the target at an acceptable quality (nothing is left on disk).</param>
public sealed record CompressResult(
    CompressStatus Status, string? OutputPath, long OutputBytes, string Message,
    bool Copied = false, int Attempts = 0, bool TooLong = false)
{
    public bool Succeeded => Status == CompressStatus.Succeeded;
}

/// <summary>What the gallery row shows of a compress job. <see cref="Outcome"/> is null while the job is active.</summary>
public sealed record CompressJobSnapshot(
    string SourcePath, long TargetBytes, CompressStage Stage, double Fraction, int Attempt,
    string? Error, CompressStatus? Outcome)
{
    public bool Active => Outcome is null;
}

/// <summary>
/// "Discord 20 MB" / "Discord 500 MB": re-encodes a clip so it fits the upload limit, one clip at a
/// time. Probe, plan (<see cref="SizeTargetPlanner"/>), encode attempts through the shared
/// <see cref="FFmpegJobRunner"/> (below-normal priority, kill-on-close, cancellable, ".part" until
/// finished), then: gallery note and star copied to the result, the file put on the clipboard (Ctrl+V
/// in Discord), a toast that opens the folder.
///
/// A clip that already fits and is plain H.264 + AAC is copied instead of encoded.
/// </summary>
public sealed class DiscordCompressService : IDisposable
{
    private readonly FFmpegJobRunner _runner;
    private readonly Func<AppSettings> _settings;
    private readonly GalleryMetaService _meta;

    /// <summary>One whole compress at a time: attempts of different clips must not interleave in the runner's queue.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _jobsGate = new();
    private readonly Dictionary<string, JobEntry> _jobs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set once hardware decoding failed: it is not tried again this session.</summary>
    private volatile bool _hwDecodeBroken;
    private volatile bool _disposed;

    public DiscordCompressService(FFmpegJobRunner runner, Func<AppSettings> settings, GalleryMetaService meta)
    {
        _runner = runner;
        _settings = settings;
        _meta = meta;
    }

    /// <summary>Names of the encoders the codec detection found (h264_amf, libx264, ...).</summary>
    public Func<IEnumerable<string>> AvailableEncoders { get; init; } = () => Array.Empty<string>();

    /// <summary>True while a game is in the foreground (only asked when the settings hold jobs back for games).</summary>
    public Func<bool> GameInForeground { get; init; } = () => false;

    internal Action<string> PlaceOnClipboard { get; init; } = DefaultPlaceOnClipboard;
    internal Action<ToastKind, string, string, string?> Notify { get; init; } =
        (kind, title, body, path) => ToastService.Show(kind, title, body, path);
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
    internal TimeSpan GamePollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Test seam: runs a job instead of the runner.</summary>
    internal Func<FFmpegJob, IProgress<FFmpegProgress>?, CancellationToken, Task<FFmpegJobResult>>? RunJob { get; init; }

    /// <summary>Test seam: reads the video's properties instead of starting ffmpeg.</summary>
    internal Func<string, CancellationToken, Task<MediaProbe?>>? Probe { get; init; }

    /// <summary>Test seam: free bytes on the drive of a folder, null if unknown.</summary>
    internal Func<string, long?> FreeSpace { get; init; } = FFmpegJobRunner.DefaultFreeSpace;

    /// <summary>
    /// Where the x264 pass-1 statistics live: a folder of our own, so they are never mixed with other programs'
    /// temp files and the sweep at startup knows exactly where to look.
    /// </summary>
    internal string StatsFolder { get; init; } = DefaultStatsFolder;

    internal static string DefaultStatsFolder => Path.Combine(Path.GetTempPath(), "WKI_Clipper", "pass-logs");

    private Task<FFmpegJobResult> RunOnRunnerAsync(FFmpegJob job, IProgress<FFmpegProgress>? progress, CancellationToken ct)
        => RunJob is { } custom ? custom(job, progress, ct) : _runner.RunAsync(job, progress, ct);

    /// <summary>Raised on every change of a job (from a background thread).</summary>
    public event Action<CompressJobSnapshot>? JobChanged;

    // ================================================================ jobs for the gallery

    /// <summary>
    /// Starts a compress in the background. False if this clip is already being compressed (or the service is
    /// shut down). A failed earlier job of the clip is replaced.
    /// </summary>
    public bool Start(string sourcePath, long targetBytes)
    {
        var key = Key(sourcePath);
        JobEntry entry;
        lock (_jobsGate)
        {
            if (_disposed) return false;
            if (_jobs.TryGetValue(key, out var existing) && existing.Outcome is null) return false;
            entry = new JobEntry(sourcePath, targetBytes);
            _jobs[key] = entry;
        }
        Raise(entry);
        _ = Task.Run(() => RunJobAsync(entry));
        return true;
    }

    /// <summary>Current state of the clip's job (running, or failed and not yet dismissed); null if there is none.</summary>
    public CompressJobSnapshot? Snapshot(string sourcePath)
    {
        lock (_jobsGate) return _jobs.TryGetValue(Key(sourcePath), out var e) ? e.Snapshot() : null;
    }

    /// <summary>Asks the clip's running job to stop. False if there is none.</summary>
    public bool Cancel(string sourcePath)
    {
        JobEntry? entry;
        lock (_jobsGate) _jobs.TryGetValue(Key(sourcePath), out entry);
        if (entry is null || entry.Outcome is not null) return false;
        try { entry.Cts.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }

    /// <summary>Forgets finished jobs (their error text leaves the gallery rows).</summary>
    public void ClearFinished()
    {
        lock (_jobsGate)
            foreach (var key in _jobs.Where(kv => kv.Value.Outcome is not null).Select(kv => kv.Key).ToList())
                _jobs.Remove(key);
    }

    /// <summary>Forgets the finished (failed) job of one clip; a running job is left alone.</summary>
    public void Dismiss(string sourcePath)
    {
        lock (_jobsGate)
            if (_jobs.TryGetValue(Key(sourcePath), out var entry) && entry.Outcome is not null)
                _jobs.Remove(Key(sourcePath));
    }

    private async Task RunJobAsync(JobEntry entry)
    {
        var progress = new InlineProgress<CompressProgress>(p =>
        {
            lock (_jobsGate) { entry.Stage = p.Stage; entry.Fraction = p.Fraction; entry.Attempt = p.Attempt; }
            Raise(entry);
        });

        CompressResult result;
        try { result = await CompressAsync(entry.SourcePath, entry.TargetBytes, progress, entry.Cts.Token).ConfigureAwait(false); }
        catch (Exception ex)
        {
            Logger.Error("Discord compress job crashed", ex);
            result = new CompressResult(CompressStatus.Failed, null, 0, L.T("Unerwarteter Fehler: ", "Unexpected error: ") + ex.Message);
        }

        lock (_jobsGate)
        {
            entry.Outcome = result.Status;
            entry.Error = result.Succeeded || result.Status == CompressStatus.Cancelled ? null : result.Message;
            // Only a failure stays visible (its error is shown in the row); done and cancelled leave no trace.
            if (entry.Error is null) _jobs.Remove(Key(entry.SourcePath));
        }
        Raise(entry);
        entry.Cts.Dispose();
    }

    private void Raise(JobEntry entry)
    {
        CompressJobSnapshot snapshot;
        lock (_jobsGate) snapshot = entry.Snapshot();
        try { JobChanged?.Invoke(snapshot); }
        catch (Exception ex) { Logger.Warn("Discord compress listener failed: " + ex.Message); }
    }

    private static string Key(string path)
    {
        try { return Path.GetFullPath(path); } catch { return path; }
    }

    private sealed class JobEntry
    {
        public JobEntry(string sourcePath, long targetBytes) { SourcePath = sourcePath; TargetBytes = targetBytes; }

        public string SourcePath { get; }
        public long TargetBytes { get; }
        public CancellationTokenSource Cts { get; } = new();
        public CompressStage Stage { get; set; } = CompressStage.Queued;
        public double Fraction { get; set; }
        public int Attempt { get; set; }
        public string? Error { get; set; }
        public CompressStatus? Outcome { get; set; }

        public CompressJobSnapshot Snapshot() => new(SourcePath, TargetBytes, Stage, Fraction, Attempt, Error, Outcome);
    }

    // ================================================================ the compress

    /// <summary>
    /// Compresses one clip. Never throws: the outcome is in the result. Waits for its turn (one compress at a
    /// time) and, when the settings ask for it, for the game to leave the foreground.
    /// </summary>
    public async Task<CompressResult> CompressAsync(
        string sourcePath, long targetBytes, IProgress<CompressProgress>? progress, CancellationToken ct)
    {
        if (_disposed) return Cancelled();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var token = linked.Token;
        bool gated = false;
        CompressResult result;
        try
        {
            progress?.Report(new CompressProgress(CompressStage.Queued, 0, 0));
            await _gate.WaitAsync(token).ConfigureAwait(false);
            gated = true;

            await WaitForNoGameAsync(progress, token).ConfigureAwait(false);
            result = await CompressCoreAsync(sourcePath, targetBytes, progress, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return Cancelled(); }
        catch (Exception ex)
        {
            Logger.Error("Discord compress failed unexpectedly", ex);
            result = Failed(L.T("Unerwarteter Fehler: ", "Unexpected error: ") + ex.Message);
        }
        finally { if (gated) _gate.Release(); }

        if (result.Status == CompressStatus.Failed)
            Toast(ToastKind.Warning, L.T("Komprimieren fehlgeschlagen", "Compressing failed"),
                  Path.GetFileName(sourcePath) + " — " + result.Message, null);
        return result;
    }

    private async Task WaitForNoGameAsync(IProgress<CompressProgress>? progress, CancellationToken ct)
    {
        if (!_settings().Discord.OnlyWhenNoGame || !GameInForeground()) return;

        Logger.Info("Discord compress waits: a game is in the foreground.");
        progress?.Report(new CompressProgress(CompressStage.WaitingForGame, 0, 0));
        while (GameInForeground())
            await Delay(GamePollInterval, ct).ConfigureAwait(false);
    }

    private async Task<CompressResult> CompressCoreAsync(
        string source, long requestedTarget, IProgress<CompressProgress>? progress, CancellationToken ct)
    {
        long target = DiscordCompressRules.EffectiveTarget(requestedTarget, 20_000_000);

        if (!File.Exists(source)) return Failed(L.T("Datei nicht gefunden.", "File not found."));
        var ext = Path.GetExtension(source).ToLowerInvariant();
        if (ext is not (".mp4" or ".mkv" or ".mov"))
            return Failed(L.T("Nur Videos können komprimiert werden.", "Only videos can be compressed."));

        progress?.Report(new CompressProgress(CompressStage.Analyzing, 0, 0));
        var probe = Probe is { } customProbe
            ? await customProbe(source, ct).ConfigureAwait(false)
            : await _runner.ProbeAsync(source, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (probe is null || probe.VideoCodec is null || probe.Width <= 0 || probe.Height <= 0)
            return Failed(L.T("Das Video konnte nicht gelesen werden.", "The video could not be read."));
        if (probe.Duration is not { } duration || duration <= TimeSpan.Zero)
            return Failed(L.T("Die Länge des Videos ist unbekannt.", "The length of the video is unknown."));

        var clipsBase = SettingsService.ExpandPath(_settings().Output.ClipsFolder);
        var desired = DiscordCompressRules.OutputPath(clipsBase, source, target);

        long sourceBytes = new FileInfo(source).Length;
        string output;
        long outputBytes;
        bool copied = false;
        int attempts = 0;

        if (ext == ".mp4" && SizeTargetPlanner.ShouldCopyInstead(probe, sourceBytes, target))
        {
            // Already small enough and already something Discord plays: a re-encode could only make it worse.
            var copy = await CopyAsync(source, desired, sourceBytes, progress, ct).ConfigureAwait(false);
            if (copy.Error is not null) return Failed(copy.Error);
            output = copy.Path!;
            outputBytes = sourceBytes;
            copied = true;
        }
        else
        {
            var plan = SizeTargetPlanner.Plan(duration.TotalSeconds, probe.Width, probe.Height, probe.Fps, target,
                hasAudio: probe.AudioStreamCount > 0,
                sourceBitsPerSecond: (long)(sourceBytes * 8.0 / duration.TotalSeconds));
            if (plan.TooLong)
                return new CompressResult(CompressStatus.Failed, null, 0, DiscordCompressRules.TooLongMessage(target), TooLong: true);

            var encoder = DiscordCompressRules.PickEncoder(AvailableEncoders());
            var context = new AttemptContext(source, desired, plan, duration, target, Math.Min(probe.AudioStreamCount, 2), progress);
            Logger.Info($"Discord compress: {Path.GetFileName(source)} -> {DiscordCompressRules.TargetLabel(target)} with {encoder}, " +
                        $"{plan.Start.Width}x{plan.Start.Height}@{plan.Start.Fps}, {plan.Start.VideoBps / 1000} kbit/s video");

            var loop = await CompressAttemptLoop.RunAsync(
                plan, encoder == CompressEncoder.X264,
                spec => RunAttemptAsync(context, encoder, spec, ct),
                DiscardFile).ConfigureAwait(false);

            attempts = loop.Attempts;
            if (loop.Status == CompressStatus.Cancelled) return Cancelled();
            if (loop.Status != CompressStatus.Succeeded)
                return new CompressResult(CompressStatus.Failed, null, 0, loop.Message, Attempts: attempts, TooLong: loop.TooLong);
            output = loop.OutputPath!;
            outputBytes = loop.OutputBytes;
        }

        Logger.Info($"Discord compress done: {output} ({outputBytes} bytes, {attempts} attempt(s), copied={copied})");
        CopyMeta(clipsBase, source, output, target);
        try { PlaceOnClipboard(output); }
        catch (Exception ex) { Logger.Warn("Could not put the compressed clip on the clipboard: " + ex.Message); }

        Toast(ToastKind.Clip, L.T("Für Discord bereit", "Ready for Discord"),
              L.T($"{Path.GetFileName(output)} · {FormatMegabytes(outputBytes)} — mit Strg+V in Discord einfügen. Klick öffnet den Ordner.",
                  $"{Path.GetFileName(output)} · {FormatMegabytes(outputBytes)} — paste it into Discord with Ctrl+V. Click opens the folder."),
              Path.GetDirectoryName(output));

        return new CompressResult(CompressStatus.Succeeded, output, outputBytes, "", copied, attempts);
    }

    // ---------------------------------------------------------------- encode attempts

    private sealed record AttemptContext(
        string Source, string DesiredPath, SizePlan Plan, TimeSpan Duration, long Target,
        int AudioTracks, IProgress<CompressProgress>? Progress);

    private async Task<AttemptRun> RunAttemptAsync(AttemptContext c, CompressEncoder hardware, AttemptSpec spec, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // "Only while no game is in the foreground" holds for every ffmpeg this job starts - each retry (the size was
        // missed, a lower rung, the software fallback) and, below, between the two passes - not just for the first start.
        await WaitForNoGameAsync(c.Progress, ct).ConfigureAwait(false);
        return spec.Software
            ? await RunSoftwareAsync(c, spec, ct).ConfigureAwait(false)
            : await RunHardwareAsync(c, hardware, spec, ct).ConfigureAwait(false);
    }

    /// <summary>What this attempt can write to the disk (see <see cref="DiscordCompressRules.EstimateOutputBytes"/>).</summary>
    private static long EstimateBytes(AttemptContext c, CompressRung rung, long requestBps)
        => DiscordCompressRules.EstimateOutputBytes(requestBps, c.Duration.TotalSeconds, rung.AudioBytes, rung.MuxBytes, c.Target);

    private async Task<AttemptRun> RunHardwareAsync(AttemptContext c, CompressEncoder encoder, AttemptSpec spec, CancellationToken ct)
    {
        var rung = c.Plan.Rungs[spec.RungIndex];
        var part = FFmpegJob.PartPathFor(c.DesiredPath);
        long estimate = EstimateBytes(c, rung, spec.RequestBps);

        var result = await RunWithHwDecodeFallbackAsync(
            hw => new FFmpegJob(
                FFmpegCommandBuilder.BuildCompress(new CompressCommand(
                    c.Source, part, encoder, rung.Width, rung.Fps, spec.RequestBps, rung.AudioKbps, c.AudioTracks, hw)),
                c.DesiredPath, c.Duration, estimate),
            EncodeProgress(c, spec.Number, 0, 1), ct,
            beforeRetry: () => WaitForNoGameAsync(c.Progress, ct)).ConfigureAwait(false);

        return ToRun(result);
    }

    /// <summary>libx264, two passes: the first measures (writes nothing), the second encodes with those statistics.</summary>
    private async Task<AttemptRun> RunSoftwareAsync(AttemptContext c, AttemptSpec spec, CancellationToken ct)
    {
        var rung = c.Plan.Rungs[spec.RungIndex];
        var part = FFmpegJob.PartPathFor(c.DesiredPath);
        // A long software encode must not take every core from the game.
        int threads = Math.Max(2, Environment.ProcessorCount / 2);

        // Pass 1 writes its statistics (the .log and the much bigger .mbtree: ~600 MB for ten minutes of 1080p60) and
        // runs as a discarding job, which the runner does not check for disk space. So it is checked here, on the
        // drive of our own statistics folder - not on whatever %TEMP% happens to be.
        long statsBytes = DiscordCompressRules.EstimatePassLogBytes(rung.Width, rung.Height, rung.Fps, c.Duration);
        if (FreeSpace(StatsFolder) is { } free && !FFmpegJobRules.HasEnoughDiskSpace(free, statsBytes))
        {
            var drive = Path.GetPathRoot(Path.GetFullPath(StatsFolder)) ?? StatsFolder;
            return new AttemptRun(AttemptOutcome.Failed, 0, null,
                L.T($"Zu wenig Speicherplatz für die 2-Pass-Statistik auf {drive}.",
                    $"Not enough free disk space for the 2-pass statistics on {drive}."));
        }
        try { Directory.CreateDirectory(StatsFolder); }
        catch (Exception ex)
        {
            return new AttemptRun(AttemptOutcome.Failed, 0, null,
                L.T($"Ordner für die 2-Pass-Statistik nicht verfügbar: {ex.Message}", $"Folder for the 2-pass statistics unavailable: {ex.Message}"));
        }
        string passLog = Path.Combine(StatsFolder, "wki_" + Guid.NewGuid().ToString("N"));   // unique per attempt

        CompressCommand Command(bool hw, int pass) => new(
            c.Source, part, CompressEncoder.X264, rung.Width, rung.Fps, spec.RequestBps, rung.AudioKbps, c.AudioTracks,
            hw, pass, passLog, threads);

        try
        {
            var first = await RunWithHwDecodeFallbackAsync(
                hw => new FFmpegJob(FFmpegCommandBuilder.BuildCompress(Command(hw, 1)), passLog, c.Duration, 0, DiscardOutput: true),
                EncodeProgress(c, spec.Number, 0, 0.5), ct,
                beforeRetry: () => WaitForNoGameAsync(c.Progress, ct)).ConfigureAwait(false);
            if (!first.Succeeded) return ToRun(first);

            // The statistics survive the wait; the second pass is a second ffmpeg and must not start in front of a game.
            await WaitForNoGameAsync(c.Progress, ct).ConfigureAwait(false);

            var second = await RunWithHwDecodeFallbackAsync(
                hw => new FFmpegJob(FFmpegCommandBuilder.BuildCompress(Command(hw, 2)), c.DesiredPath, c.Duration,
                                    EstimateBytes(c, rung, spec.RequestBps)),
                EncodeProgress(c, spec.Number, 0.5, 0.5), ct,
                beforeRetry: () => WaitForNoGameAsync(c.Progress, ct)).ConfigureAwait(false);
            return ToRun(second);
        }
        finally { DeletePassLogs(passLog); }
    }

    /// <summary>
    /// Runs the job with hardware decoding. Only when ffmpeg's own error names the hardware decoder (d3d11va, hwaccel
    /// setup) is it run once more without - an encoder that would not start, a full disk or a damaged input fail the
    /// same way on the CPU, and a second run would only double the time. Hardware decoding stays off for the rest of
    /// the session only if that second run WORKED: then it is proven to have been the cause.
    /// </summary>
    internal async Task<FFmpegJobResult> RunWithHwDecodeFallbackAsync(
        Func<bool, FFmpegJob> makeJob, IProgress<FFmpegProgress> progress, CancellationToken ct, Func<Task>? beforeRetry = null)
    {
        bool hw = !_hwDecodeBroken;
        var result = await RunOnRunnerAsync(makeJob(hw), progress, ct).ConfigureAwait(false);
        if (hw && result.Status == FFmpegJobStatus.Failed && result.ExitCode is not (0 or -1)
            && DiscordCompressRules.LooksLikeHwDecodeFailure(result.StderrTail))
        {
            Logger.Warn($"Hardware decoding failed (exit {result.ExitCode}) - trying again without it: {result.Message}");
            if (beforeRetry is not null) await beforeRetry().ConfigureAwait(false);
            var retry = await RunOnRunnerAsync(makeJob(false), progress, ct).ConfigureAwait(false);
            if (retry.Succeeded)
            {
                _hwDecodeBroken = true;
                Logger.Info("The job worked without hardware decoding - it stays off for this session.");
            }
            result = retry;
        }
        return result;
    }

    // ---------------------------------------------------------------- leftovers of crashed runs

    /// <summary>
    /// Deletes pass-1 statistics a crash or a killed clipper left behind: the files named like ours ("wki_&lt;32 hex&gt;-0.log"
    /// and its ".mbtree"/".temp" companions) in our statistics folder and - from earlier versions, which wrote them
    /// straight into %TEMP% - there, but only when older than 10 minutes. Meant for startup. Returns how many were removed.
    /// </summary>
    public int CleanupStaleStats(DateTime? nowUtc = null, TimeSpan? minAge = null)
        => SweepStaleStats(new[] { StatsFolder, Path.GetTempPath() }, nowUtc ?? DateTime.UtcNow, minAge ?? TimeSpan.FromMinutes(10));

    internal static int SweepStaleStats(IEnumerable<string> folders, DateTime nowUtc, TimeSpan minAge)
    {
        int removed = 0;
        foreach (var folder in folders)
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var file in Directory.EnumerateFiles(folder, "wki_*"))
                {
                    if (!DiscordCompressRules.IsPassStatsFile(Path.GetFileName(file))) continue;
                    try
                    {
                        if (nowUtc - File.GetLastWriteTimeUtc(file) <= minAge) continue;   // a job may be writing it right now
                        File.Delete(file);
                        removed++;
                        Logger.Info("Removed stale 2-pass statistics: " + file);
                    }
                    catch (Exception ex) { Logger.Warn($"Could not remove '{file}': {ex.Message}"); }
                }
            }
            catch (Exception ex) { Logger.Warn($"Could not sweep '{folder}': {ex.Message}"); }
        }
        return removed;
    }
    private static AttemptRun ToRun(FFmpegJobResult result)
    {
        switch (result.Status)
        {
            case FFmpegJobStatus.Succeeded when result.OutputPath is not null:
                return new AttemptRun(AttemptOutcome.Ok, new FileInfo(result.OutputPath).Length, result.OutputPath, "");
            case FFmpegJobStatus.Cancelled:
                return new AttemptRun(AttemptOutcome.Cancelled, 0, null, result.Message);
            default:
                return new AttemptRun(AttemptOutcome.Failed, 0, null, result.Message);
        }
    }

    private static IProgress<FFmpegProgress> EncodeProgress(AttemptContext c, int attempt, double offset, double span)
        => new InlineProgress<FFmpegProgress>(p =>
            c.Progress?.Report(new CompressProgress(CompressStage.Encoding, offset + Math.Clamp(p.Fraction, 0, 1) * span, attempt)));

    private static void DeletePassLogs(string passLog)
    {
        try
        {
            var dir = Path.GetDirectoryName(passLog)!;
            foreach (var file in Directory.GetFiles(dir, Path.GetFileName(passLog) + "-*"))
                DeleteFile(file, retries: 0);
        }
        catch (Exception ex) { Logger.Warn("Could not clean up the 2-pass statistics: " + ex.Message); }
    }

    // ---------------------------------------------------------------- copy

    private static async Task<(string? Path, string? Error)> CopyAsync(
        string source, string desired, long sourceBytes, IProgress<CompressProgress>? progress, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(desired)!;
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex)
        {
            return (null, L.T($"Zielordner nicht verfügbar: {ex.Message}", $"Output folder unavailable: {ex.Message}"));
        }
        if (FFmpegJobRunner.DefaultFreeSpace(dir) is { } free && !FFmpegJobRules.HasEnoughDiskSpace(free, sourceBytes))
            return (null, L.T("Zu wenig Speicherplatz zum Kopieren.", "Not enough free disk space to copy."));

        var part = FFmpegJob.PartPathFor(desired);
        try
        {
            DeleteFile(part, retries: 0);
            progress?.Report(new CompressProgress(CompressStage.Copying, 0, 0));
            await using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, useAsync: true))
            await using (var to = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                long done = 0;
                int read;
                while ((read = await from.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await to.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    progress?.Report(new CompressProgress(CompressStage.Copying, sourceBytes > 0 ? (double)done / sourceBytes : 0, 0));
                }
            }
        }
        catch (OperationCanceledException)
        {
            DeleteFile(part, retries: 0);
            throw;
        }
        catch (Exception ex)
        {
            DeleteFile(part, retries: 0);
            return (null, L.T($"Kopieren fehlgeschlagen: {ex.Message}", $"Copy failed: {ex.Message}"));
        }

        try { return (await FFmpegJobRunner.FinishFileAsync(part, desired).ConfigureAwait(false), null); }
        catch (Exception ex)
        {
            // The copy itself is complete - keep it and say where it is.
            return (null, L.T($"Fertige Datei konnte nicht umbenannt werden ({ex.Message}). Sie liegt unter: {part}",
                              $"The finished file could not be renamed ({ex.Message}). It is kept at: {part}"));
        }
    }

    // ---------------------------------------------------------------- after the compress

    /// <summary>The copy carries the original's star and note (the note marked as the Discord version).</summary>
    internal void CopyMeta(string clipsBase, string sourcePath, string outputPath, long targetBytes)
    {
        try
        {
            var from = GalleryMetaKey.For(GalleryBase.Clips, clipsBase, sourcePath);
            var to = GalleryMetaKey.For(GalleryBase.Clips, clipsBase, outputPath);

            bool saved = true;
            var note = DiscordCompressRules.NoteForCopy(_meta.GetNote(from), targetBytes);
            if (note.Length > 0) saved &= _meta.SetNote(to, note);
            if (_meta.IsFavorite(from)) saved &= _meta.SetFavorite(to, true);
            if (!saved) Logger.Warn("The note of the compressed clip is only in memory so far (the notes file is locked).");
        }
        catch (Exception ex) { Logger.Warn("Could not copy the note to the compressed clip: " + ex.Message); }
    }

    private void Toast(ToastKind kind, string title, string body, string? path)
    {
        try
        {
            if (_settings().Behavior.ShowToastNotifications) Notify(kind, title, body, path);
        }
        catch (Exception ex) { Logger.Warn("Toast failed: " + ex.Message); }
    }

    /// <summary>Puts the file on the clipboard as a file drop - Ctrl+V in Discord uploads it.</summary>
    private static void DefaultPlaceOnClipboard(string path)
    {
        var app = Application.Current;
        if (app is null) return;

        void Set()
        {
            var data = new System.Windows.Forms.DataObject();
            data.SetFileDropList(new StringCollection { path });
            // retries: another program (a clipboard manager) may hold the clipboard for a moment
            System.Windows.Forms.Clipboard.SetDataObject(data, copy: true, retryTimes: 5, retryDelay: 100);
        }

        if (app.Dispatcher.CheckAccess()) Set();
        else app.Dispatcher.Invoke(Set);
    }

    private static string FormatMegabytes(long bytes)
        => (bytes / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    private static void DiscardFile(string path) => DeleteFile(path, retries: 3);

    /// <summary>A virus scanner may still hold a fresh file for a moment; a stray oversize result is worse than a short wait.</summary>
    private static void DeleteFile(string path, int retries)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= retries)
                {
                    Logger.Warn($"Could not delete '{path}': {ex.Message}");
                    return;
                }
                Thread.Sleep(200);
            }
        }
    }

    private static CompressResult Failed(string message) => new(CompressStatus.Failed, null, 0, message);

    private static CompressResult Cancelled() => new(CompressStatus.Cancelled, null, 0, L.T("Abgebrochen.", "Cancelled."));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        lock (_jobsGate)
            foreach (var entry in _jobs.Values.Where(e => e.Outcome is null))
                try { entry.Cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Reports on the calling thread at once (unlike <see cref="Progress{T}"/>, which posts to a synchronization context).</summary>
    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;
        public InlineProgress(Action<T> report) => _report = report;

        public void Report(T value)
        {
            try { _report(value); }
            catch (Exception ex) { Logger.Warn("Progress callback failed: " + ex.Message); }
        }
    }
}
