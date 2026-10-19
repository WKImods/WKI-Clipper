using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.Models;
using WKI_Clipper.Views;

namespace WKI_Clipper.Services;

public enum CutStatus { Succeeded, Failed, Cancelled }

public enum CutStage { Queued, Cutting, Verifying, Recycling }

/// <param name="Fraction">0..1 of the current stage (only <see cref="CutStage.Cutting"/> has a real one).</param>
public readonly record struct CutProgress(CutStage Stage, double Fraction);

/// <param name="Index">The index the range was resolved against (frame numbers mean nothing without it).</param>
/// <param name="Range">Already resolved by <see cref="ClipTimeline.Resolve"/> for <paramref name="Mode"/>.</param>
/// <param name="RecycleOriginal">Service moves the original to the Recycle Bin itself once the result is verified
/// (headless use and tests; the editor window closes its preview first and calls <see cref="ClipCutService.RecycleOriginal"/>).</param>
public sealed record CutRequest(string SourcePath, Mp4VideoIndex Index, CutRange Range, CutMode Mode, bool RecycleOriginal);

/// <param name="FrameCount">Frames the result has according to its own index; -1 when it could not be read.</param>
/// <param name="Warning">A check or Recycle-Bin note (bilingual), otherwise null. The cut itself still succeeded.</param>
public sealed record CutResult(
    CutStatus Status, string? OutputPath, long OutputBytes, string Message,
    int FrameCount = -1, int ExpectedFrames = 0, RecycleOutcome? Recycle = null, string? Warning = null)
{
    public bool Succeeded => Status == CutStatus.Succeeded;
}

/// <summary>
/// The clip editor's cut: one <see cref="FFmpegJob"/> through the shared <see cref="FFmpegJobRunner"/> (below-normal
/// priority, kill-on-close, cancellable, ".part" until finished), then a check of the result against the frames that
/// were asked for, the note and star copied over, and only then — if asked, and only if the check passed — the
/// original into the Recycle Bin. A result that fails the check is still kept and reported; nothing is ever lost.
///
/// A cut is one runner job, so it queues behind a running Discord compress (stage "Queued") without a gate of its own.
/// </summary>
public sealed class ClipCutService : IDisposable
{
    /// <summary>A fast cut of a source with B-frames may end a few frames later than asked (measured: about two).</summary>
    private const int ApproximateEndTolerance = 4;

    private readonly FFmpegJobRunner _runner;
    private readonly Func<AppSettings> _settings;
    private readonly GalleryMetaService _meta;
    private readonly CancellationTokenSource _lifetime = new();
    private volatile bool _disposed;

    public ClipCutService(FFmpegJobRunner runner, Func<AppSettings> settings, GalleryMetaService meta)
    {
        _runner = runner;
        _settings = settings;
        _meta = meta;
    }

    /// <summary>Names of the encoders the codec detection found (h264_amf, libx264, ...).</summary>
    public Func<IEnumerable<string>> AvailableEncoders { get; init; } = () => Array.Empty<string>();

    internal Func<string, Mp4VideoIndex?> ReadIndex { get; init; } = Mp4Index.TryRead;
    internal Func<string, RecycleOutcome> Recycle { get; init; } = RecycleBin.Send;
    internal Action<ToastKind, string, string, string?> Notify { get; init; } =
        (kind, title, body, path) => ToastService.Show(kind, title, body, path);

    /// <summary>Test seam: runs the job instead of the runner.</summary>
    internal Func<FFmpegJob, IProgress<FFmpegProgress>?, CancellationToken, Task<FFmpegJobResult>>? RunJob { get; init; }

    /// <summary>
    /// After every successful cut (from a background thread): the path of the result. The gallery reloads on it. Also
    /// raised with the original's path after <see cref="RecycleOriginal"/> moved it away.
    /// </summary>
    public event Action<string>? Finished;

    // ================================================================ the cut

    /// <summary>Never throws: the outcome is in the result.</summary>
    public async Task<CutResult> CutAsync(CutRequest request, IProgress<CutProgress>? progress, CancellationToken ct)
    {
        if (_disposed) return Cancelled();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        CutResult result;
        try { result = await CutCoreAsync(request, progress, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Cancelled(); }
        catch (Exception ex)
        {
            Logger.Error("Clip cut failed unexpectedly", ex);
            result = Failed(L.T("Unerwarteter Fehler: ", "Unexpected error: ") + ex.Message);
        }

        if (result.Status == CutStatus.Failed)
            Toast(ToastKind.Warning, L.T("Schneiden fehlgeschlagen", "Trimming failed"),
                  Path.GetFileName(request.SourcePath) + " — " + result.Message, null);
        return result;
    }

    private async Task<CutResult> CutCoreAsync(CutRequest request, IProgress<CutProgress>? progress, CancellationToken ct)
    {
        var source = request.SourcePath;

        // ---- 1. check the request ----
        if (!File.Exists(source)) return Failed(L.T("Datei nicht gefunden.", "File not found."));
        if (Path.GetExtension(source).ToLowerInvariant() is not (".mp4" or ".mov"))
            return Failed(L.T("Nur MP4-Videos lassen sich schneiden.", "Only MP4 videos can be trimmed."));
        if (request.Index.FrameCount <= 0) return Failed(L.T("Das Video enthält keine Bilder.", "The video has no frames."));

        var timeline = new ClipTimeline(request.Index);
        var range = request.Range;
        if (range.StartFrame < 0 || range.EndFrame > timeline.LastFrame)
            return Failed(L.T("Die Auswahl liegt außerhalb des Clips.", "The selection is outside the clip."));
        if (ClipCutRules.ValidationMessage(timeline.Validate(range, request.Mode)) is { } invalid)
            return Failed(invalid);

        // ---- 2. plan ----
        double startFile = timeline.FileStartOf(range.StartFrame);
        double endFile = timeline.FileEndExclusive(range.EndFrame);
        double? endExclusive = range.EndFrame == timeline.LastFrame ? null : endFile;
        double rangeSeconds = Math.Max(0, endFile - startFile);
        double totalSeconds = Math.Max(0, timeline.PreviewEnd - request.Index.PreviewSeconds[0]);

        var output = ClipCutRules.OutputPath(source);
        var part = FFmpegJob.PartPathFor(output);
        string args;
        long estimated;
        int? threadCap = null;
        if (request.Mode == CutMode.Fast)
        {
            estimated = ClipCutRules.EstimateFastBytes(new FileInfo(source).Length, rangeSeconds, totalSeconds);
            args = ClipCutRules.BuildFastArgs(source, part, startFile, endExclusive, ClipSavePolicy.UseFaststart(estimated));
        }
        else
        {
            var encoder = ClipCutRules.PickExactEncoder(AvailableEncoders(), request.Index.Width, request.Index.Height);
            long bps = ClipCutRules.ExactVideoBitrate(request.Index.VideoBytes, totalSeconds, request.Index.Codec);
            estimated = ClipCutRules.EstimateExactBytes(bps, request.Index.AudioTrackCount, rangeSeconds);
            args = ClipCutRules.BuildExactArgs(source, part, startFile, endExclusive, encoder, bps,
                request.Index.AudioTrackCount, ClipSavePolicy.UseFaststart(estimated));
            // A long software encode must not take every core from the game.
            if (encoder == CompressEncoder.X264) threadCap = Math.Max(2, Environment.ProcessorCount / 2);
        }
        Logger.Info($"Clip cut ({request.Mode}): {Path.GetFileName(source)} frames {range.StartFrame}..{range.EndFrame} " +
                    $"({rangeSeconds:0.000} s) -> {Path.GetFileName(output)}");

        // ---- 3. cut ----
        progress?.Report(new CutProgress(CutStage.Queued, 0));
        var job = new FFmpegJob(args, output, TimeSpan.FromSeconds(rangeSeconds), estimated, ThreadCap: threadCap);
        var runnerProgress = new SyncProgress<FFmpegProgress>(p =>
            progress?.Report(new CutProgress(CutStage.Cutting, Math.Clamp(p.Fraction, 0, 1))));
        var run = RunJob is { } custom
            ? await custom(job, runnerProgress, ct).ConfigureAwait(false)
            : await _runner.RunAsync(job, runnerProgress, ct).ConfigureAwait(false);

        if (run.Status == FFmpegJobStatus.Cancelled) return Cancelled();
        if (run.Status != FFmpegJobStatus.Succeeded || run.OutputPath is null) return Failed(run.Message);
        var result = run.OutputPath;
        long bytes = SafeLength(result);
        Logger.Info($"Clip cut done: {result} ({bytes} bytes)");

        // ---- 4. verify ----
        progress?.Report(new CutProgress(CutStage.Verifying, 1));
        int expected = range.EndFrame - range.StartFrame + 1;
        int frames = ReadIndex(result)?.FrameCount ?? -1;
        bool tolerated = request.Mode == CutMode.Fast && range.EndApproximate
                         && frames >= expected && frames <= expected + ApproximateEndTolerance;
        bool verified = frames == expected || tolerated;
        string? warning = verified ? null : ClipCutRules.VerifyMessage(frames, expected);
        if (!verified) Logger.Warn($"Clip cut check: {frames} frames, expected {expected} ({result})");

        // ---- 5. note and star ----
        CopyMeta(source, result, timeline, range);

        // ---- 6. the original ----
        RecycleOutcome? recycled = null;
        if (request.RecycleOriginal && verified)
        {
            progress?.Report(new CutProgress(CutStage.Recycling, 1));
            recycled = RecycleBin.IsSupportedPath(source) ? Recycle(source) : RecycleOutcome.NotSupported;
            if (recycled != RecycleOutcome.Recycled) warning = ClipCutRules.RecycleMessage(recycled.Value);
        }

        // ---- 7. tell the world ----
        RaiseFinished(result);
        Toast(ToastKind.Clip, L.T("Ausschnitt gespeichert", "Excerpt saved"),
              L.T($"{Path.GetFileName(result)} · {ClipCutRules.FormatBytes(bytes)} — Klick öffnet den Ordner.",
                  $"{Path.GetFileName(result)} · {ClipCutRules.FormatBytes(bytes)} — click opens the folder."),
              Path.GetDirectoryName(result));

        return new CutResult(CutStatus.Succeeded, result, bytes, "", frames, expected, recycled, warning);
    }

    // ================================================================ the original

    /// <summary>
    /// Moves the original into the Recycle Bin (only a local file; never File.Delete) and, if the file is gone
    /// afterwards, raises <see cref="Finished"/> with its path so the gallery reloads. The caller closes the preview first.
    /// </summary>
    public RecycleOutcome RecycleOriginal(string sourcePath)
    {
        if (!RecycleBin.IsSupportedPath(sourcePath)) return RecycleOutcome.NotSupported;
        var outcome = Recycle(sourcePath);
        if (outcome is RecycleOutcome.Recycled or RecycleOutcome.DeletedPermanently) RaiseFinished(sourcePath);
        return outcome;
    }

    /// <summary>
    /// Waits between the tries of <see cref="RecycleOriginalAsync"/>. A preview (MediaElement) or a still-frame grab
    /// (ffmpeg) lets go of the file a moment after it was told to, so the first try can find it in use.
    /// </summary>
    internal static readonly TimeSpan[] RecycleRetryDelays =
    {
        TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(1500)
    };

    /// <summary>Test seam: the wait between two tries.</summary>
    internal Func<TimeSpan, Task> Delay { get; init; } = Task.Delay;

    /// <summary>
    /// <see cref="RecycleOriginal"/>, tried again with a short back-off while the file is still in use (at most about
    /// 3 s in all, without blocking the caller). Any other outcome is final: asking "delete permanently?" a second
    /// time, say, would be nagging. Continues on the caller's context — the shell wants the UI thread.
    /// </summary>
    public async Task<RecycleOutcome> RecycleOriginalAsync(string sourcePath)
    {
        var outcome = RecycleOriginal(sourcePath);
        foreach (var wait in RecycleRetryDelays)
        {
            if (outcome != RecycleOutcome.InUse) break;
            await Delay(wait);
            outcome = RecycleOriginal(sourcePath);
        }
        return outcome;
    }

    // ================================================================ after the cut

    /// <summary>The result carries the original's star and note, the note extended by the range. The original keeps its own.</summary>
    internal void CopyMeta(string sourcePath, string outputPath, ClipTimeline timeline, CutRange range)
    {
        try
        {
            var clipsBase = SettingsService.ExpandPath(_settings().Output.ClipsFolder);
            var from = GalleryMetaKey.For(GalleryBase.Clips, clipsBase, sourcePath);
            var to = GalleryMetaKey.For(GalleryBase.Clips, clipsBase, outputPath);

            var (start, end) = timeline.NoteRange(range);
            bool saved = _meta.SetNote(to, ClipCutRules.NoteForCut(_meta.GetNote(from), start, end));
            if (_meta.IsFavorite(from)) saved &= _meta.SetFavorite(to, true);
            if (!saved) Logger.Warn("The note of the trimmed clip is only in memory so far (the notes file is locked).");
        }
        catch (Exception ex) { Logger.Warn("Could not copy the note to the trimmed clip: " + ex.Message); }
    }

    private void RaiseFinished(string path)
    {
        try { Finished?.Invoke(path); }
        catch (Exception ex) { Logger.Warn("Clip cut listener failed: " + ex.Message); }
    }

    private void Toast(ToastKind kind, string title, string body, string? path)
    {
        try
        {
            if (_settings().Behavior.ShowToastNotifications) Notify(kind, title, body, path);
        }
        catch (Exception ex) { Logger.Warn("Toast failed: " + ex.Message); }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static CutResult Failed(string message) => new(CutStatus.Failed, null, 0, message);

    private static CutResult Cancelled() => new(CutStatus.Cancelled, null, 0, L.T("Abgebrochen.", "Cancelled."));

    /// <summary>Cancels a cut that is still running; nothing new starts afterwards.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Reports on the calling thread at once (unlike <see cref="Progress{T}"/>, which posts to a synchronization context).</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;
        public SyncProgress(Action<T> report) => _report = report;

        public void Report(T value)
        {
            try { _report(value); }
            catch (Exception ex) { Logger.Warn("Progress callback failed: " + ex.Message); }
        }
    }
}
