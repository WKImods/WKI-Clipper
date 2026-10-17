using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.Native;

namespace WKI_Clipper.Services;

/// <summary>
/// Runs finite ffmpeg encodes (Discord compression, the clip editor) one at a time, in order,
/// with progress, cancellation and a guarantee that a half-finished file is never mistaken for
/// a result: ffmpeg writes to "&lt;name&gt;.part", which only becomes the real file after a clean
/// exit.
///
/// Deliberately NOT used by the F9 save path. F9 has its own lane in ReplayBufferService and must
/// never wait behind a long encode in this queue.
///
/// Every ffmpeg it starts is covered by a kill-on-close job object; a process that cannot be
/// covered is not left running, and nothing starts once the runner is disposed.
/// </summary>
public sealed class FFmpegJobRunner : IDisposable
{
    private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StaleAge = TimeSpan.FromMinutes(10);
    private const int StderrTailLines = 30;

    /// <summary>Waits between rename attempts while a virus scanner holds the fresh file (~10 s in total).</summary>
    private static readonly TimeSpan[] RenameBackoff =
    {
        TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800),
        TimeSpan.FromMilliseconds(1600), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)
    };

    private readonly string _ffmpegPath;
    private readonly Func<string, long?> _freeSpace;
    private readonly string _partsFile;

    private readonly object _queueGate = new();
    private Task _tail = Task.CompletedTask;
    /// <summary>Cancelled by <see cref="CancelAll"/> (and replaced), so jobs queued afterwards are unaffected.</summary>
    private CancellationTokenSource _generation = new();

    private readonly object _partsGate = new();
    private volatile string? _activePart;
    private volatile bool _disposed;

    // Created on the first job: an app that never encodes never makes a job object.
    private readonly Lazy<KillOnCloseJob?> _guard = new(KillOnCloseJob.TryCreate);

    public FFmpegJobRunner(FFmpegService ffmpeg) : this(ffmpeg.FFmpegPath) { }

    /// <param name="freeSpace">Free bytes on the drive of a folder, null if unknown (test seam).</param>
    /// <param name="partsFile">Where the exact ".part" paths of running jobs are recorded for the crash cleanup (test seam).</param>
    public FFmpegJobRunner(string ffmpegPath, Func<string, long?>? freeSpace = null, string? partsFile = null)
    {
        _ffmpegPath = ffmpegPath;
        _freeSpace = freeSpace ?? DefaultFreeSpace;
        _partsFile = partsFile ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WKI_Clipper", "ffmpeg-job-parts.txt");
    }

    /// <summary>
    /// Queues the job and completes when it is finished. Never throws for a failed or cancelled
    /// job: the outcome is in the result. A job cancelled while still waiting never starts.
    /// </summary>
    public async Task<FFmpegJobResult> RunAsync(FFmpegJob job, IProgress<FFmpegProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (_disposed) return CancelledResult();

        Task previous;
        CancellationToken generation;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_queueGate)
        {
            previous = _tail;
            _tail = done.Task;
            generation = _generation.Token;
        }

        // The caller's token, plus "CancelAll()/Dispose() was called after this job was queued".
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, generation);
        var token = linked.Token;

        bool myTurn = false;
        try
        {
            await previous.WaitAsync(token).ConfigureAwait(false);
            myTurn = true;
        }
        catch (OperationCanceledException) { }

        if (!myTurn)
        {
            // Our slot may only end once the predecessor's has, or the next job would start
            // alongside it.
            _ = previous.ContinueWith(_ => done.TrySetResult(), TaskScheduler.Default);
            return CancelledResult();
        }

        try { return await RunCoreAsync(job, progress, token).ConfigureAwait(false); }
        catch (Exception ex)
        {
            Logger.Error("FFmpeg job crashed", ex);
            return Failed(-1, L.T("Unerwarteter Fehler: ", "Unexpected error: ") + ex.Message, "");
        }
        finally { done.TrySetResult(); }
    }

    /// <summary>Cancels the running job and everything queued so far. Jobs queued afterwards run normally.</summary>
    public void CancelAll()
    {
        CancellationTokenSource old;
        lock (_queueGate)
        {
            old = _generation;
            _generation = new CancellationTokenSource();
        }
        old.Cancel();   // not disposed: queued jobs hold linked sources on its token
    }

    private async Task<FFmpegJobResult> RunCoreAsync(FFmpegJob job, IProgress<FFmpegProgress>? progress, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || _disposed) return CancelledResult();
        if (!File.Exists(_ffmpegPath))
            return Failed(-1, L.T($"ffmpeg.exe nicht gefunden ({_ffmpegPath}).", $"ffmpeg.exe not found ({_ffmpegPath})."), "");

        if (job.DiscardOutput)
        {
            // Nothing is written, so there is no folder to make, no space to reserve and no ".part" to track.
            return await ExecuteAsync(job, progress, ct).ConfigureAwait(false);
        }

        var part = job.PartPath;
        var dir = Path.GetDirectoryName(Path.GetFullPath(job.FinalPath)) ?? "";
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex)
        {
            return Failed(-1, L.T($"Zielordner nicht verfügbar: {ex.Message}", $"Output folder unavailable: {ex.Message}"), "");
        }

        if (_freeSpace(dir) is { } free && !FFmpegJobRules.HasEnoughDiskSpace(free, job.EstimatedOutputBytes))
        {
            var drive = Path.GetPathRoot(dir) ?? dir;
            var need = FFmpegJobRules.RequiredFreeBytes(job.EstimatedOutputBytes);
            return Failed(-1,
                L.T($"Zu wenig Speicherplatz auf {drive}: {FormatBytes(free)} frei, mindestens {FormatBytes(need)} nötig.",
                    $"Not enough free disk space on {drive}: {FormatBytes(free)} free, at least {FormatBytes(need)} needed."), "");
        }

        DeleteQuietly(part);   // leftover of an earlier crash; ffmpeg runs with -n and would refuse it
        RecordPart(part);      // from here until the job ended a crash can leave this exact file behind
        _activePart = part;
        try { return await ExecuteAsync(job, progress, ct).ConfigureAwait(false); }
        finally
        {
            _activePart = null;
            ForgetPart(part);   // ended (even if the finished file could not be renamed and was kept): never swept
        }
    }

    private async Task<FFmpegJobResult> ExecuteAsync(FFmpegJob job, IProgress<FFmpegProgress>? progress, CancellationToken ct)
    {
        // A discarding job has no part file: every delete below is then a no-op.
        var part = job.DiscardOutput ? null : job.PartPath;
        var tail = new TailBuffer(StderrTailLines);

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = BuildArguments(job),
            UseShellExecute = false,
            // stdin stays open on purpose (no -nostdin): cancel writes "q" to it.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult();

        // Progress both feeds the caller and tells the watchdog the job is alive.
        long lastAdvance = Stopwatch.GetTimestamp();
        var lastPosition = TimeSpan.MinValue;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            try
            {
                if (!FFmpegProgressParser.TryParseOutTime(e.Data, out var pos) || pos == lastPosition) return;
                lastPosition = pos;
                Interlocked.Exchange(ref lastAdvance, Stopwatch.GetTimestamp());
                progress?.Report(new FFmpegProgress(FFmpegProgressParser.Fraction(pos, job.ExpectedDuration), pos));
            }
            catch (Exception ex) { Logger.Warn("FFmpeg progress callback failed: " + ex.Message); }
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) tail.Add(e.Data); };

        if (_disposed) return CancelledResult();   // last look before a process comes into existence
        Logger.Info($"FFmpeg job: {psi.Arguments}");
        try { process.Start(); }
        catch (Exception ex)
        {
            Logger.Error("FFmpeg job could not start", ex);
            return Failed(-1, L.T("ffmpeg konnte nicht gestartet werden: ", "ffmpeg could not be started: ") + ex.Message, "");
        }

        if (!Guard(process) && !HasExited(process))
        {
            // Unprotected, it would outlive the clipper. Better no encode than an orphan.
            KillIfRunning(process);
            DeleteQuietly(part);
            return _disposed
                ? CancelledResult()
                : Failed(-1, L.T("ffmpeg konnte nicht abgesichert werden und wurde beendet.",
                                 "ffmpeg could not be put under the safety job object and was terminated."), "");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        int stalled = 0;
        var watchdog = Task.CompletedTask;
        var stallAfter = job.EffectiveStallTimeout;
        if (stallAfter > TimeSpan.Zero)
        {
            var poll = TimeSpan.FromMilliseconds(Math.Clamp(stallAfter.TotalMilliseconds / 4, 100, 1000));
            watchdog = Task.Run(async () =>
            {
                try
                {
                    while (!jobCts.IsCancellationRequested && !exited.Task.IsCompleted)
                    {
                        await Task.WhenAny(Task.Delay(poll, jobCts.Token), exited.Task).ConfigureAwait(false);
                        if (exited.Task.IsCompleted || jobCts.IsCancellationRequested) return;
                        if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastAdvance)) > stallAfter)
                        {
                            Volatile.Write(ref stalled, 1);
                            jobCts.Cancel();   // takes the same road as a cancel: "q", then the process tree
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) { }
            });
        }

        bool cancelled = false;
        try
        {
            try { await exited.Task.WaitAsync(jobCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                cancelled = true;
                await StopAsync(process, exited.Task).ConfigureAwait(false);
            }
            jobCts.Cancel();
            await watchdog.ConfigureAwait(false);

            // Exited can fire before the last output lines are pumped; only the parameterless
            // WaitForExit() returns once the redirected streams are fully read.
            bool drained = await DrainAsync(process).ConfigureAwait(false);
            int exitCode = SafeExitCode(process);
            var stderr = tail.ToString();

            if (cancelled)
            {
                DeleteQuietly(part);
                if (Volatile.Read(ref stalled) == 1)
                {
                    var seconds = ((int)stallAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                    Logger.Warn($"FFmpeg job terminated: no progress for {seconds} s ({job.FinalPath})");
                    return Failed(-1,
                        L.T($"ffmpeg hat seit {seconds} s keinen Fortschritt gemeldet und wurde beendet.",
                            $"ffmpeg made no progress for {seconds} s and was terminated."), stderr);
                }
                Logger.Info("FFmpeg job cancelled: " + job.FinalPath);
                return CancelledResult();
            }

            if (exitCode != 0)
            {
                DeleteQuietly(part);
                Logger.Warn($"FFmpeg job failed (exit {exitCode}): {tail.Last}");
                return Failed(exitCode, DescribeFailure(exitCode, tail.Last), stderr);
            }
            if (!drained)
            {
                DeleteQuietly(part);
                return Failed(exitCode, L.T("ffmpeg hat seine Ausgabe nicht sauber beendet.",
                                            "ffmpeg did not finish its output cleanly."), stderr);
            }
            if (part is null)
            {
                progress?.Report(new FFmpegProgress(1.0, job.ExpectedDuration));
                Logger.Info("FFmpeg job done (no output file): " + job.FinalPath);
                return new FFmpegJobResult(FFmpegJobStatus.Succeeded, null, 0, "", stderr);
            }
            if (!File.Exists(part) || new FileInfo(part).Length == 0)
            {
                DeleteQuietly(part);
                return Failed(exitCode, L.T("ffmpeg hat keine Ausgabedatei erzeugt.", "ffmpeg did not produce an output file."), stderr);
            }

            string finalPath;
            try { finalPath = await FinishFileAsync(part, job.FinalPath).ConfigureAwait(false); }
            catch (Exception ex)
            {
                // The encode itself worked — keep the .part so the result is not thrown away with the
                // error. It is no longer tracked as a crash leftover, so the startup sweep leaves it alone.
                Logger.Error("FFmpeg job: could not move the finished file into place", ex);
                return Failed(exitCode,
                    L.T($"Fertige Datei konnte nicht umbenannt werden ({ex.Message}). Sie liegt unter: {part}",
                        $"The finished file could not be renamed ({ex.Message}). It is kept at: {part}"), stderr);
            }

            progress?.Report(new FFmpegProgress(1.0, job.ExpectedDuration));
            Logger.Info("FFmpeg job done: " + finalPath);
            return new FFmpegJobResult(FFmpegJobStatus.Succeeded, finalPath, 0, "", stderr);
        }
        finally
        {
            jobCts.Cancel();
            KillIfRunning(process);
        }
    }

    /// <summary>
    /// The flags the runner owns, then the caller's arguments. The runner decides about overwriting
    /// (never — the stale .part was deleted beforehand), so a caller's own -y/-n is dropped: ffmpeg
    /// treats the pair as an error and every job would fail.
    /// </summary>
    internal static string BuildArguments(FFmpegJob job)
        => "-hide_banner -nostats -n -progress pipe:1 "
           + FFmpegJobRules.ApplyThreadCap(FFmpegJobRules.StripOverwriteFlags(job.Arguments), job.PartPath, job.ThreadCap);

    /// <summary>
    /// Asks ffmpeg to quit ("q" on stdin finalizes the file properly); after the grace period the
    /// whole process tree is killed.
    /// </summary>
    private static async Task StopAsync(Process process, Task exited)
    {
        try
        {
            process.StandardInput.Write("q");
            process.StandardInput.Flush();
        }
        catch { /* stdin already closed — the process is on its way out anyway */ }

        if (await Task.WhenAny(exited, Task.Delay(CancelGrace)).ConfigureAwait(false) != exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
        await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
    }

    private static async Task<bool> DrainAsync(Process process)
    {
        try
        {
            await Task.Run(() => process.WaitForExit()).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("FFmpeg output streams did not close: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Moves the finished ".part" to its final name. A virus scanner often holds a fresh file for a
    /// few seconds (IOException or UnauthorizedAccessException), so both are retried for ~10 s; if
    /// the move still fails, copying is tried before giving up. Throws only when the finished file
    /// could not be put in place (it is then left where it is).
    /// </summary>
    internal static async Task<string> FinishFileAsync(
        string part, string desiredFinal,
        Action<string, string>? move = null, Action<string, string>? copy = null, Func<TimeSpan, Task>? delay = null)
    {
        move ??= (source, target) => File.Move(source, target, overwrite: false);
        copy ??= (source, target) => File.Copy(source, target, overwrite: false);
        delay ??= span => Task.Delay(span);

        Exception? last = null;
        int nameRaces = 0, waits = 0;
        while (true)
        {
            var target = FFmpegJobRules.UniquePath(desiredFinal, File.Exists);
            try
            {
                move(part, target);
                return target;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                // Name taken between the check and the move → next number at once.
                if (ex is IOException && File.Exists(target) && ++nameRaces < 100) continue;
                if (waits >= RenameBackoff.Length) break;
                await delay(RenameBackoff[waits++]).ConfigureAwait(false);
            }
        }

        var copyTarget = FFmpegJobRules.UniquePath(desiredFinal, File.Exists);
        try
        {
            copy(part, copyTarget);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(copyTarget)) File.Delete(copyTarget); } catch { }   // a half-written copy is no result
            throw new IOException(last?.Message ?? ex.Message, ex);
        }

        DeleteQuietly(part);   // the copy is the result now; if this fails the part simply stays
        return copyTarget;
    }

    /// <summary>
    /// Kill-on-close job object (the clipper's death takes ffmpeg with it) and below-normal
    /// priority (a long encode must not cost the game FPS). False = the process is NOT covered.
    /// </summary>
    private bool Guard(Process process)
    {
        if (_disposed) return false;

        bool covered = false;
        try
        {
            var job = _guard.Value;
            covered = job is not null && job.TryAssign(process);
        }
        catch (Exception ex) { Logger.Warn("Job object assignment failed: " + ex.Message); }
        if (!covered) Logger.Warn("FFmpeg process is not covered by the kill-on-close job object.");

        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) { Logger.Warn("Could not lower the ffmpeg priority: " + ex.Message); }
        return covered;
    }

    // ---- probing ----

    /// <summary>
    /// Duration, first video stream and audio tracks of a file, via `ffmpeg -i` (ffprobe.exe is not
    /// shipped; exit code 1 is normal because no output is given). Null if the file is no media.
    /// </summary>
    public async Task<MediaProbe?> ProbeAsync(string path, CancellationToken ct = default)
    {
        if (_disposed || !File.Exists(_ffmpegPath) || !File.Exists(path)) return null;

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-i", path }) psi.ArgumentList.Add(a);

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return null;
            if (!Guard(process) && !HasExited(process))
            {
                KillIfRunning(process);
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(stderr, stdout).ConfigureAwait(false);
                return FFmpegProbeParser.Parse(stderr.Result);
            }
            catch (OperationCanceledException)
            {
                KillIfRunning(process);
                return null;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Probe failed: " + ex.Message);
            return null;
        }
    }

    // ---- leftovers of crashed runs ----

    /// <summary>
    /// Deletes the ".part" files of jobs that never ended (a crash or a killed clipper left them) —
    /// exactly the paths recorded when a job started, and only when older than 10 minutes. A
    /// foreign "*.part" in the same folder (a browser download) is never touched, nor is a
    /// finished file that could not be renamed (it is forgotten when its job ends). Returns how
    /// many were removed. Meant for startup.
    /// </summary>
    public int CleanupStaleParts(DateTime? nowUtc = null, TimeSpan? minAge = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var age = minAge ?? StaleAge;
        int removed = 0;

        lock (_partsGate)
        {
            var keep = new List<string>();
            foreach (var part in ReadParts())
            {
                if (string.Equals(part, _activePart, StringComparison.OrdinalIgnoreCase)) { keep.Add(part); continue; }

                try
                {
                    if (!File.Exists(part)) continue;   // gone already: drop the entry
                    if (!FFmpegJobRules.IsStalePart(Path.GetFileName(part), File.GetLastWriteTimeUtc(part), now, age))
                    {
                        keep.Add(part);   // touched recently — may still be written by another clipper instance
                        continue;
                    }
                    File.Delete(part);
                    removed++;
                    Logger.Info("Removed stale ffmpeg leftover: " + part);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Could not remove '{part}': {ex.Message}");
                    keep.Add(part);
                }
            }
            WriteParts(keep);
        }
        return removed;
    }

    private void RecordPart(string part)
    {
        lock (_partsGate)
        {
            var parts = ReadParts();
            if (!parts.Contains(part, StringComparer.OrdinalIgnoreCase)) parts.Add(part);
            WriteParts(parts);
        }
    }

    private void ForgetPart(string part)
    {
        lock (_partsGate)
        {
            var parts = ReadParts();
            if (parts.RemoveAll(p => string.Equals(p, part, StringComparison.OrdinalIgnoreCase)) > 0) WriteParts(parts);
        }
    }

    private List<string> ReadParts()
    {
        try
        {
            return File.Exists(_partsFile)
                ? File.ReadAllLines(_partsFile).Where(l => !string.IsNullOrWhiteSpace(l)).ToList()
                : new List<string>();
        }
        catch { return new List<string>(); }
    }

    private void WriteParts(List<string> parts)
    {
        try
        {
            var parent = Path.GetDirectoryName(_partsFile);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.WriteAllLines(_partsFile, parts);
        }
        catch (Exception ex) { Logger.Warn("Could not update the ffmpeg leftover list: " + ex.Message); }
    }

    // ---- small helpers ----

    private static FFmpegJobResult CancelledResult()
        => new(FFmpegJobStatus.Cancelled, null, -1, L.T("Abgebrochen.", "Cancelled."), "");

    private static FFmpegJobResult Failed(int exitCode, string message, string stderrTail)
        => new(FFmpegJobStatus.Failed, null, exitCode, message, stderrTail);

    private static string DescribeFailure(int exitCode, string lastLine)
    {
        var code = exitCode.ToString(CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(lastLine)
            ? L.T($"ffmpeg ist mit Fehlercode {code} beendet.", $"ffmpeg exited with code {code}.")
            : L.T($"ffmpeg ist mit Fehlercode {code} beendet: {lastLine}", $"ffmpeg exited with code {code}: {lastLine}");
    }

    private static string FormatBytes(long bytes)
        => bytes >= 1L << 30
            ? (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
            : (bytes / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB";

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; } catch { return -1; }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; } catch { return true; }
    }

    private static void KillIfRunning(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    private static void DeleteQuietly(string? path)
    {
        if (path is null) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Logger.Warn($"Could not delete '{path}': {ex.Message}"); }
    }

    /// <summary>Free bytes on the drive of <paramref name="dir"/>; null for network paths (check skipped).</summary>
    internal static long? DefaultFreeSpace(string dir)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return null; }
    }

    /// <summary>
    /// Ends the runner for good: running and queued jobs are cancelled, closing the job object kills
    /// any ffmpeg still alive at once, and no process is started afterwards.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelAll();
        if (_guard.IsValueCreated) _guard.Value?.Dispose();
    }

    /// <summary>Last N stderr lines, written from the pump thread and read after the process ended.</summary>
    private sealed class TailBuffer
    {
        private readonly int _capacity;
        private readonly Queue<string> _lines = new();
        private readonly object _gate = new();

        public TailBuffer(int capacity) => _capacity = capacity;

        public void Add(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (_gate)
            {
                _lines.Enqueue(line);
                while (_lines.Count > _capacity) _lines.Dequeue();
            }
        }

        public string Last { get { lock (_gate) return _lines.Count == 0 ? "" : _lines.Last(); } }

        public override string ToString() { lock (_gate) return string.Join(Environment.NewLine, _lines); }
    }
}
