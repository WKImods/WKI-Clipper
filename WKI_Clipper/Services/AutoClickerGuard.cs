using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using WKI_Clipper.Models;
using WKI_Clipper.Services.AutoClicker;

namespace WKI_Clipper.Services;

public enum GuardAction { LockClicker, UnlockClicker }

/// <param name="Match">The blacklist entry that caused <see cref="GuardAction.LockClicker"/>; null otherwise.</param>
public readonly record struct GuardDecision(GuardAction Action, BlacklistEntry? Match);

/// <summary>
/// Locks the native clicker and its mouse patterns (<see cref="IClickerLock"/>) for as long as a blacklisted game runs or is in front.
/// The decision is a pure function (<see cref="DecideNative"/>); the instance only feeds it: the game in front (<see cref="OnGameChanged"/>),
/// every window switch (<see cref="OnForegroundChanged"/>) and a poll of the running process names.
///
/// The poll is cheap by design. Only while the helper is wanted (<see cref="IClickerLock.Armed"/>: "Clicker bereit" or "Mausbewegung
/// bereit" is on) and the blacklist is in force does a 3 s tick read the process list, so a game that starts in the background locks it,
/// too. While both are off nothing is polled and no process list is read: the game in front alone decides, so switching on in a
/// blacklisted game starts locked. "Blacklist ignorieren" on or an empty blacklist stops the poll and lifts the lock.
/// </summary>
public sealed class AutoClickerGuard : IDisposable
{
    /// <summary>Time between two checks while the helper is wanted.</summary>
    public const int PollIntervalMs = 3000;

    private readonly Func<AutoClickerSettings> _settings;
    private readonly IProcessProbe _processes;
    private readonly IClickerLock _clicker;
    private readonly int _pollMs;
    private readonly Action<Action> _background;
    private readonly System.Threading.Timer _timer;
    private int _busy;
    private int _recheck;
    private volatile bool _disposed;
    private volatile bool _polling;
    private volatile string? _foregroundExe;
    private volatile bool _ignore;

    /// <param name="settings">The live section (read on every check, so edits apply at once).</param>
    /// <param name="clicker">The native clicker that is locked and unlocked.</param>
    /// <param name="pollMs">Test seam: the real interval is <see cref="PollIntervalMs"/>.</param>
    /// <param name="background">Test seam: runs a check that was triggered from an event or the UI; the default is the thread pool,
    /// because reading the process list must never hold the game detector's or the UI thread.</param>
    public AutoClickerGuard(Func<AutoClickerSettings> settings, IProcessProbe processes, IClickerLock clicker,
        int pollMs = PollIntervalMs, Action<Action>? background = null)
    {
        _background = background ?? (work => ThreadPool.QueueUserWorkItem(_ => work()));
        _settings = settings;
        _processes = processes;
        _clicker = clicker;
        _pollMs = pollMs;
        _timer = new System.Threading.Timer(_ => Evaluate(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>"Blacklist ignorieren": session only, never saved; false again after a restart of the clipper.</summary>
    public bool IgnoreBlacklist
    {
        get => _ignore;
        set
        {
            if (_ignore == value) return;
            _ignore = value;
            Logger.Info(value ? "AutoClicker blacklist is ignored until the clipper restarts." : "AutoClicker blacklist is active again.");
            IgnoreChanged?.Invoke();
            if (value) Disarm();                 // ignoring: nothing is polled any more
            _background(Evaluate);               // either way the lock follows at once: lifted when ignoring, set when a blacklisted game runs
        }
    }

    /// <summary>Raised when <see cref="IgnoreBlacklist"/> flipped (any thread).</summary>
    public event Action? IgnoreChanged;

    /// <summary>True while the poll is armed (the helper is wanted and the blacklist is in force).</summary>
    internal bool IsPolling => _polling;

    // ---------------- pure rules ----------------

    /// <summary>
    /// The first blacklist entry that matches the game in front or any running process (compared by exe file name, case-insensitive,
    /// ".exe" optional). Independent of the ignore flag.
    /// </summary>
    public static BlacklistEntry? FindBlocker(IEnumerable<BlacklistEntry>? blacklist, IEnumerable<string>? runningProcessNames, string? foregroundExe)
    {
        if (blacklist is null) return null;
        var entries = blacklist.Where(e => e is not null && AutoClickerBlacklist.Key(e.ExeName) is not null).ToList();
        if (entries.Count == 0) return null;

        var live = new HashSet<string>();
        if (AutoClickerBlacklist.Key(foregroundExe) is { } fg) live.Add(fg);
        if (runningProcessNames is not null)
            foreach (var name in runningProcessNames)
                if (AutoClickerBlacklist.Key(name) is { } k) live.Add(k);

        return entries.FirstOrDefault(e => live.Contains(AutoClickerBlacklist.Key(e.ExeName)!));
    }

    /// <summary>
    /// What the native clicker should do: locked when the blacklist is not ignored and a blacklisted program runs or is in front;
    /// unlocked otherwise. Also decided while the clicker is switched off (then only the program in front is known), so a helper that
    /// is switched on in a blacklisted game starts locked instead of waiting for the next check.
    /// </summary>
    public static GuardDecision DecideNative(IEnumerable<BlacklistEntry>? blacklist, IEnumerable<string>? runningProcessNames,
        string? foregroundExe, bool ignore)
    {
        if (ignore) return new GuardDecision(GuardAction.UnlockClicker, null);
        return FindBlocker(blacklist, runningProcessNames, foregroundExe) is { } hit
            ? new GuardDecision(GuardAction.LockClicker, hit)
            : new GuardDecision(GuardAction.UnlockClicker, null);
    }

    // ---------------- feeding it ----------------

    /// <summary>The game in front changed (null = none; the detector raises this on a pool thread). Re-checks at once, in the background.</summary>
    public void OnGameChanged(GameInfo? game)
    {
        _foregroundExe = game?.ExeName;
        _background(Evaluate);
    }

    /// <summary>Any window came to the front, or the helper became wanted or unwanted: a re-check, in the background.</summary>
    public void OnForegroundChanged() => _background(Evaluate);

    /// <summary>
    /// One check: locks or unlocks the clicker, and keeps the poll armed exactly while the helper is wanted and the blacklist is in
    /// force. Safe from any thread; a call that arrives while a check runs is not lost: the running check goes once more.
    /// </summary>
    public void Evaluate()
    {
        do
        {
            if (_disposed) return;
            Volatile.Write(ref _recheck, 1);
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;   // the running check picks the flag up
            try
            {
                while (!_disposed && Interlocked.Exchange(ref _recheck, 0) == 1)
                {
                    try { EvaluateOnce(); }
                    catch (Exception ex) { Logger.Warn("AutoClicker guard check failed: " + ex.Message); }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        } while (Volatile.Read(ref _recheck) == 1);   // a call that came in between the last flag read and the release
    }

    private void EvaluateOnce()
    {
        var blacklist = _settings().Blacklist;
        bool nothingToGuard = _ignore || blacklist is not { Count: > 0 };
        bool watch = !nothingToGuard && _clicker.Armed;

        // Switched off, only the program in front counts (no process list read while no helper is wanted).
        var decision = DecideNative(blacklist, watch ? _processes.RunningNames() : null, _foregroundExe, _ignore);
        try
        {
            if (decision.Action == GuardAction.LockClicker && decision.Match is { } match) _clicker.Lock(match);
            else _clicker.Unlock();
        }
        catch (Exception ex) { Logger.Warn("AutoClicker guard could not lock the clicker: " + ex.Message); }

        if (watch) Arm();   // a blacklisted game may start in the background while the clicker is switched on
        else Disarm();
    }

    /// <summary>One-shot, re-armed by the next check: ticks can never pile up.</summary>
    private void Arm()
    {
        if (_disposed) return;
        _polling = true;
        try { _timer.Change(Math.Max(_pollMs, 1), Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    private void Disarm()
    {
        _polling = false;
        try { _timer.Change(Timeout.Infinite, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _disposed = true;
        _polling = false;
        _timer.Dispose();
    }
}
