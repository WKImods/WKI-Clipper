using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WKI_Clipper.Services;

/// <summary>
/// Polls for a named process every 5 seconds and raises events when the
/// process starts or stops. Used to auto-restart the replay buffer in
/// GameOnly audio capture mode.
/// </summary>
public sealed class GameProcessWatcher : IDisposable
{
    private readonly string _processName;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;

    /// <summary>PID of the currently detected process, or null.</summary>
    public int? CurrentPid { get; private set; }

    /// <summary>Raised on a thread-pool thread when the target process is found.</summary>
    public event Action<int>? ProcessFound;

    /// <summary>Raised on a thread-pool thread when the target process exits.</summary>
    public event Action? ProcessLost;

    /// <param name="processName">Process name WITHOUT .exe extension (e.g. "ArmaReforger").</param>
    public GameProcessWatcher(string processName)
    {
        _processName = processName;
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoop(_cts.Token));
        Logger.Info($"GameProcessWatcher started, watching for: {_processName}");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _pollTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts?.Dispose();
        _cts = null;
        _pollTask = null;
        CurrentPid = null;
        Logger.Info("GameProcessWatcher stopped.");
    }

    private async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(5000, ct).ConfigureAwait(false);
                CheckProcess();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Logger.Error("GameProcessWatcher poll error", ex);
            }
        }
    }

    private void CheckProcess()
    {
        Process? found = null;
        try
        {
            // GetProcessesByName expects name without .exe
            var procs = Process.GetProcessesByName(_processName);
            if (procs.Length > 0)
            {
                // Take the oldest (lowest start time) to be stable across multi-instance
                found = procs.OrderBy(p =>
                {
                    try { return p.StartTime; }
                    catch { return DateTime.MaxValue; }
                }).First();
            }
            // Dispose the others
            foreach (var p in procs)
            {
                if (p != found) p.Dispose();
            }
        }
        catch { }

        if (found != null)
        {
            int pid = found.Id;
            found.Dispose();

            if (CurrentPid == null)
            {
                CurrentPid = pid;
                Logger.Info($"GameProcessWatcher: {_processName} found (PID {pid})");
                ProcessFound?.Invoke(pid);
            }
            else if (CurrentPid != pid)
            {
                // PID changed (process restarted)
                CurrentPid = pid;
                Logger.Info($"GameProcessWatcher: {_processName} restarted (new PID {pid})");
                ProcessFound?.Invoke(pid);
            }
        }
        else if (CurrentPid != null)
        {
            CurrentPid = null;
            Logger.Info($"GameProcessWatcher: {_processName} exited");
            ProcessLost?.Invoke();
        }
    }

    public void Dispose() => Stop();
}
