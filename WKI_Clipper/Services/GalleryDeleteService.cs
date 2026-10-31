using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.Models;
using WKI_Clipper.Views;

namespace WKI_Clipper.Services;

/// <summary>
/// The gallery's "Löschen": one file into the Recycle Bin (<see cref="RecycleBin.Send(string)"/>, never File.Delete),
/// tried again for a moment while it is in use, then a toast with the outcome. The bin is the undo, so there is no
/// question first. One delete per file at a time. Notes and stars are not touched: a file restored from the bin finds
/// them under its old path again.
/// </summary>
public sealed class GalleryDeleteService
{
    private readonly Func<AppSettings> _settings;
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public GalleryDeleteService(Func<AppSettings> settings) => _settings = settings;

    /// <summary>One try: the file and the window Windows' question belongs to.</summary>
    internal Func<string, IntPtr, RecycleOutcome> Recycle { get; init; } = RecycleBin.Send;

    /// <summary>Where one try runs: its own STA thread (see <see cref="RunOnStaThread{T}"/>). Test seam.</summary>
    internal Func<Func<RecycleOutcome>, Task<RecycleOutcome>> RunShell { get; init; } = RunOnStaThread;

    /// <summary>Test seam: the wait between two tries.</summary>
    internal Func<TimeSpan, Task> Delay { get; init; } = Task.Delay;

    internal Action<ToastKind, string, string, double> Notify { get; init; } =
        (kind, title, body, seconds) => ToastService.Show(kind, title, body, durationSeconds: seconds);

    /// <summary>After a delete that took the file away (recycled or deleted for good): its path. The gallery reloads on it.</summary>
    public event Action<string>? Removed;

    /// <summary>A delete of this file is still running (the menu item stays disabled meanwhile).</summary>
    public bool IsDeleting(string path)
    {
        lock (_gate) return _running.Contains(path);
    }

    /// <summary>
    /// Moves the file into the Recycle Bin and says how that went. Never throws. Null when a delete of this file is
    /// already running: nothing is started then.
    /// </summary>
    /// <param name="owner">The gallery's window: Windows' "delete permanently?" shows above it, not hidden behind the
    /// board's topmost windows.</param>
    public async Task<RecycleOutcome?> DeleteAsync(string path, IntPtr owner = default)
    {
        lock (_gate)
        {
            if (!_running.Add(path)) return null;
        }

        RecycleOutcome outcome;
        try { outcome = await RecycleBin.RetryWhileInUseAsync(() => RunShell(() => Recycle(path, owner)), Delay); }
        catch (Exception ex)
        {
            Logger.Warn("Gallery delete failed: " + ex.Message);
            outcome = RecycleOutcome.Failed;
        }
        finally
        {
            lock (_gate) _running.Remove(path);
        }

        Toast(outcome, Path.GetFileName(path));
        if (GalleryDeleteRules.RemovesFile(outcome)) RaiseRemoved(path);
        return outcome;
    }

    /// <summary>
    /// Runs one shell call on a fresh STA thread: the shell is apartment threaded, like the UI thread, but the gallery
    /// must not freeze while Windows counts the bin or asks "delete permanently?". A background thread, so a pending
    /// question never keeps the app alive.
    /// </summary>
    internal static Task<T> RunOnStaThread<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex) { done.SetException(ex); }
        })
        { IsBackground = true, Name = "WKI gallery delete (STA)" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private void Toast(RecycleOutcome outcome, string fileName)
    {
        try
        {
            if (!GalleryDeleteRules.ShowsToast(outcome, _settings().Behavior.ShowToastNotifications)) return;
            Notify(GalleryDeleteRules.ToastKindOf(outcome), GalleryDeleteRules.ToastTitle,
                   GalleryDeleteRules.Message(outcome, fileName), GalleryDeleteRules.ToastSeconds(outcome));
        }
        catch (Exception ex) { Logger.Warn("Toast failed: " + ex.Message); }
    }

    private void RaiseRemoved(string path)
    {
        try { Removed?.Invoke(path); }
        catch (Exception ex) { Logger.Warn("Gallery delete listener failed: " + ex.Message); }
    }
}
