using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using WKI_Clipper.Native;
using Timer = System.Threading.Timer;

namespace WKI_Clipper.Services;

/// <summary>
/// The game in front. Identity is the process id PLUS the exe file name (a pid can be reused by another
/// program), never the display name — so a re-resolved label does not look like a new game.
/// </summary>
public sealed record GameInfo(int Pid, string ExeName, string DisplayName)
{
    /// <summary>
    /// The monitor (physical pixels) the game's window was on when it came to the front; null when unknown.
    /// Only a snapshot: it goes stale when the resolution changes or the game is moved, so consumers that need
    /// the CURRENT monitor look it up from <see cref="Window"/> first. Not part of the identity.
    /// </summary>
    public LayoutRect? Monitor { get; init; }

    /// <summary>
    /// The window handle the game was first seen with (<see cref="IntPtr.Zero"/> = unknown), for resolving the
    /// monitor it is on right now. Can go stale (window recreated); callers check it is still a window.
    /// Not part of the identity.
    /// </summary>
    public IntPtr Window { get; init; }

    public bool Equals(GameInfo? other)
        => other is not null && Pid == other.Pid && string.Equals(ExeName, other.ExeName, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(Pid, StringComparer.OrdinalIgnoreCase.GetHashCode(ExeName));
}

/// <summary>
/// What the rules need to know about the foreground window (all sizes in physical pixels). Gathered by
/// <see cref="GameDetector"/>'s Win32 probe; tests build it by hand.
/// </summary>
public readonly record struct ForegroundFacts(
    int Pid,
    string ExePath,               // "" when the image path could not be read
    string ExeName,               // file name with extension, e.g. "ArmaReforgerSteam.exe"
    LayoutRect Window,
    LayoutRect Monitor,           // the monitor the window is on
    bool IsMinimized,
    bool IsMaximized,
    bool HasCaption,              // has a full title bar (WS_CAPTION)
    bool IsWindowsImage)          // the exe lives under the Windows directory (Settings, input hosts, ...)
{
    /// <summary>Exe name without extension — what the capture settings call the process name.</summary>
    public string ProcessName => Path.GetFileNameWithoutExtension(ExeName);
}

/// <summary>
/// Pure debounce of the "game in front" state: a change counts only after the new state stayed in front for
/// <see cref="DelayMs"/>. Times are caller-supplied milliseconds, so it is fully testable.
///
/// A candidate that equals the current state cancels a pending change (quick alt-tab and back = nothing
/// happened); seeing the SAME pending candidate again does not restart its wait (so a re-check can never
/// postpone an announcement forever); a DIFFERENT candidate replaces the pending one and restarts the wait.
/// Not thread-safe; <see cref="GameDetector"/> serializes access.
/// </summary>
public sealed class GameDebounce
{
    public const long DelayMs = 1500;

    private GameInfo? _pending;
    private bool _hasPending;
    private long _pendingSince;
    private bool _pendingShellOnly;

    /// <summary>The settled state: the game in front, or null.</summary>
    public GameInfo? Current { get; private set; }

    public bool HasPending => _hasPending;

    /// <summary>When the pending candidate matures; null = nothing pending.</summary>
    public long? DueAtMs => _hasPending ? _pendingSince + DelayMs : null;

    /// <summary>
    /// The pending change is "no game", and every observation behind it was a shell window (taskbar, start menu,
    /// search) - a transient that usually just precedes the clipper's own window (tray click). See
    /// <see cref="GameDetector"/>: such a pending is dropped instead of committed while our window is in front.
    /// </summary>
    public bool PendingIsShellTransient => _hasPending && _pending is null && _pendingShellOnly;

    /// <summary>
    /// The newest observation of what is in front (null = no game).
    /// <paramref name="shellWindow"/>: the observed window is a shell window (only meaningful for a null candidate).
    /// </summary>
    public void Observe(GameInfo? candidate, long nowMs, bool shellWindow = false)
    {
        if (Equals(candidate, Current))
        {
            // Same game: take over its newest window/monitor so consumers do not keep a stale handle.
            if (candidate is not null) Current = candidate;
            CancelPending();
            return;
        }
        if (_hasPending && Equals(candidate, _pending))   // same candidate: keep its original start
        {
            _pendingShellOnly &= shellWindow;             // ... but one non-shell sighting makes it a real "left the game"
            return;
        }
        _hasPending = true;
        _pending = candidate;
        _pendingSince = nowMs;
        _pendingShellOnly = shellWindow;
    }

    /// <summary>Forgets the pending change; <see cref="Current"/> stays.</summary>
    public void CancelPending()
    {
        _hasPending = false;
        _pending = null;
        _pendingShellOnly = false;
    }

    /// <summary>Commits the pending candidate once it matured. True = <see cref="Current"/> changed.</summary>
    public bool Poll(long nowMs)
    {
        if (!_hasPending || nowMs - _pendingSince < DelayMs) return false;
        Current = _pending;
        CancelPending();
        return true;
    }
}

/// <summary>
/// One source of truth for "is a game in front, and which one", in EVERY capture mode.
///
/// Input: foreground-window changes (<see cref="ForegroundTracker.ForegroundWindowChanged"/> — the tracker's
/// existing WinEvent hook, no second hook) plus <see cref="Reevaluate"/> after a settings change. Each
/// foreground window is turned into <see cref="ForegroundFacts"/> and run through the pure rules in
/// <see cref="IsGame"/>: not us, not minimized, not on the blocklist (browsers, chat, launchers, editors,
/// shell), not a Windows system image, not a maximized document window — and then either its window covers
/// its whole monitor (fullscreen / borderless) or it is the Window-mode capture target.
/// The result is debounced (<see cref="GameDebounce"/>, 1.5 s) and announced through <see cref="GameChanged"/>.
///
/// Only PROCESS_QUERY_LIMITED_INFORMATION handles are ever opened (anti-cheat): image path and exit code,
/// never memory rights. Our own windows (clicking in the widget board) never change the state.
/// </summary>
public sealed class GameDetector : IDisposable
{
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private readonly Func<string?> _windowModeTarget;
    private readonly Func<IntPtr, ForegroundFacts?> _probe;
    private readonly Func<IntPtr> _foreground;
    private readonly Func<ForegroundFacts, string> _displayName;
    private readonly Func<GameInfo, bool> _isAlive;
    private readonly Func<long> _clock;
    private readonly int _selfPid;

    private readonly object _gate = new();
    private readonly GameDebounce _debounce = new();
    private readonly Timer? _timer;
    private volatile GameInfo? _current;
    private bool _disposed;

    /// <summary>
    /// The game in front changed (null = none any more). Raised on a pool thread, outside every lock,
    /// at most once per settled change.
    /// </summary>
    public event Action<GameInfo?>? GameChanged;

    /// <summary>The settled game in front; null = none.</summary>
    public GameInfo? Current => _current;

    /// <param name="windowModeTarget">Process name of the Window-mode capture target, or null (any other mode / none set).</param>
    /// <param name="displayName">Label for a game (the quiet game-folder naming). Called only for games, once per change.</param>
    public GameDetector(Func<string?> windowModeTarget, Func<ForegroundFacts, string> displayName)
        : this(windowModeTarget, ProbeWin32, User32.GetForegroundWindow, displayName, IsAliveWin32,
               () => Environment.TickCount64, Environment.ProcessId, autoTimer: true)
    {
    }

    /// <param name="autoTimer">False = no timer, the caller drives <see cref="Pump"/> (tests).</param>
    internal GameDetector(Func<string?> windowModeTarget, Func<IntPtr, ForegroundFacts?> probe, Func<IntPtr> foreground,
        Func<ForegroundFacts, string> displayName, Func<GameInfo, bool> isAlive, Func<long> clock, int selfPid, bool autoTimer)
    {
        _windowModeTarget = windowModeTarget;
        _probe = probe;
        _foreground = foreground;
        _displayName = displayName;
        _isAlive = isAlive;
        _clock = clock;
        _selfPid = selfPid;
        if (autoTimer) _timer = new Timer(_ => Pump(), null, Timeout.Infinite, Timeout.Infinite);
    }

    // ---------------- pure rules ----------------

    /// <summary>
    /// Exe names (no extension, any case) that are never a game: browsers, chat, launchers, editors, media
    /// players, the shell. Compared exactly, except the "notepad" family (Notepad, Notepad++, ...).
    /// Everything <see cref="CaptureTargetResolver.IsCouplableApp"/> refuses (shell, us) is blocked too.
    /// </summary>
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "comet", "vivaldi", "chromium", "iexplore",
        "discord", "discordptb", "discordcanary", "slack", "teams", "ms-teams", "msteams", "whatsapp", "spotify",
        "steam", "steamwebhelper", "steamservice", "epicgameslauncher", "epicwebhelper", "battle.net", "eadesktop",
        "ubisoftconnect", "upc", "galaxyclient",
        "obs", "obs32", "obs64",
        "code", "devenv", "rider64", "idea64", "pycharm64", "cursor", "claude",
        "windowsterminal", "cmd", "powershell", "pwsh", "conhost", "taskmgr", "mmc",
        "vlc", "mpv", "mpc-hc", "mpc-hc64", "mpc-be", "mpc-be64", "potplayer", "potplayermini64", "wmplayer", "powerpnt",
    };

    private static string Normalize(string? exeName)
    {
        var n = (exeName ?? "").Trim();
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
    }

    /// <summary>True when the process can never be "the game" (see <see cref="Blocked"/>). Empty = blocked.</summary>
    public static bool IsBlocked(string? processName)
    {
        var n = Normalize(processName);
        if (n.Length == 0) return true;
        return Blocked.Contains(n)
            || n.StartsWith("notepad", StringComparison.OrdinalIgnoreCase)
            || !CaptureTargetResolver.IsCouplableApp(n);
    }

    /// <summary>
    /// The window spans its whole monitor (fullscreen or borderless; a window that overhangs it — invisible
    /// resize borders, windowed at native resolution — counts too). <paramref name="tolerance"/> pixels short
    /// on any side still count.
    /// </summary>
    public static bool CoversMonitor(LayoutRect window, LayoutRect monitor, double tolerance = 2)
    {
        if (monitor.W <= 0 || monitor.H <= 0) return false;
        return window.X <= monitor.X + tolerance
            && window.Y <= monitor.Y + tolerance
            && window.Right >= monitor.Right - tolerance
            && window.Bottom >= monitor.Bottom - tolerance;
    }

    /// <summary>
    /// Is the foreground window a game? The Window-mode target is the user's explicit choice and wins over
    /// size and blocklist; nothing of ours (<paramref name="selfPid"/>) and nothing minimized ever counts.
    /// </summary>
    public static bool IsGame(ForegroundFacts f, string? windowModeTarget, int selfPid)
    {
        if (f.Pid <= 0 || f.Pid == selfPid || f.IsMinimized) return false;

        var name = f.ProcessName;
        var target = Normalize(windowModeTarget);
        if (target.Length > 0 && string.Equals(name, target, StringComparison.OrdinalIgnoreCase)) return true;

        if (IsBlocked(name) || f.IsWindowsImage) return false;
        // A maximized window with a title bar is a document/app window that happens to fill an
        // auto-hide-taskbar screen, not a game (games are popup/borderless windows).
        if (f.IsMaximized && f.HasCaption) return false;
        return CoversMonitor(f.Window, f.Monitor);
    }

    // ---------------- runtime ----------------

    /// <summary>A foreground-change event for <paramref name="hwnd"/> (the hook's callback, or a re-check).</summary>
    public void OnForegroundChanged(IntPtr hwnd)
    {
        if (_disposed) return;
        try
        {
            var facts = _probe(hwnd);
            if (facts is not { } f) return;              // gone / unreadable: keep what we know
            if (f.Pid == _selfPid) return;               // our own windows never change the game

            // Shell windows (taskbar, start menu, search) are transients: a tray click puts the taskbar in front
            // for a moment, then our own window follows. The same rule the foreground tracker uses.
            bool shell = !CaptureTargetResolver.IsCouplableApp(f.ProcessName);

            // Only games cost a name lookup (disk read on the first sight of an exe).
            GameInfo? candidate = IsGame(f, _windowModeTarget(), _selfPid)
                ? new GameInfo(f.Pid, f.ExeName, _displayName(f)) { Monitor = f.Monitor, Window = hwnd }
                : null;
            lock (_gate)
            {
                if (!shell) _lastExternalWindow = hwnd.ToInt64();
                _debounce.Observe(candidate, _clock(), shellWindow: candidate is null && shell);
                if (candidate is not null && Equals(candidate, _current)) _current = _debounce.Current;   // refreshed window/monitor
                ArmLocked();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("GameDetector: foreground check failed: " + ex.Message);
        }
    }

    /// <summary>The last foreground window that was neither ours nor a shell window (an IntPtr kept as long); guarded by the gate.</summary>
    private long _lastExternalWindow;

    /// <summary>
    /// The answer for a caller that needs it NOW (the compress wait asks every few seconds), not after the debounce.
    /// The window in front is probed again with the same rules (<see cref="IsGame"/>) - so a game that became
    /// borderless after it got the focus counts, and one that went back to a window stops counting. While one of
    /// our windows is in front, the last non-shell external window is judged instead (opening the gallery over a
    /// game must not look like "no game"). The re-probed window is also fed to the debounce so
    /// <see cref="Current"/> catches up. A window that cannot be read keeps the settled state.
    /// </summary>
    public bool IsGameInFrontNow()
    {
        if (_disposed) return false;
        try
        {
            Verify();
            var (facts, hwnd) = LiveWindow();
            if (facts is not { } f) return _current is not null;

            bool live = IsGame(f, _windowModeTarget(), _selfPid);
            OnForegroundChanged(hwnd);
            return live;
        }
        catch (Exception ex)
        {
            Logger.Warn("GameDetector: live check failed: " + ex.Message);
            return _current is not null;
        }
    }

    /// <summary>
    /// The window the question "which game is in front" is about right now: the foreground window, or - while one of our
    /// windows is in front - the last non-shell external one.
    /// </summary>
    private (ForegroundFacts? Facts, IntPtr Hwnd) LiveWindow()
    {
        var hwnd = _foreground();
        var facts = _probe(hwnd);
        if (facts is { } own && own.Pid == _selfPid)
        {
            long last;
            lock (_gate) last = _lastExternalWindow;
            hwnd = new IntPtr(last);
            facts = last == 0 ? null : _probe(hwnd);
        }
        return (facts, hwnd);
    }

    /// <summary>
    /// The exe file name of the game in front right now (same live window and same <see cref="IsGame"/> rules as
    /// <see cref="IsGameInFrontNow"/>), or null when none is. A plain question: nothing is fed to the debounce, no state
    /// changes. For the F9 press: a game that came to the front within the debounce (Alt-Tab, then F9 at once) still
    /// names its length, while <see cref="Current"/> has not caught up yet.
    /// </summary>
    public string? GameExeInFrontNow()
    {
        if (_disposed) return null;
        try
        {
            var (facts, _) = LiveWindow();
            return facts is { } f && IsGame(f, _windowModeTarget(), _selfPid) ? f.ExeName : null;
        }
        catch (Exception ex)
        {
            Logger.Warn("GameDetector: live game lookup failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>Re-checks the window that is in front now — after a setting (the Window-mode target) changed.</summary>
    public void Reevaluate() => OnForegroundChanged(_foreground());

    /// <summary>
    /// Drops a game that exited without a foreground change (e.g. while one of our windows was in front).
    /// Cheap; call it from a periodic tick. Never overrides a pending change to another game.
    /// </summary>
    public void Verify()
    {
        if (_disposed) return;
        var current = _current;
        if (current is null) return;
        bool alive;
        try { alive = _isAlive(current); }
        catch { alive = true; }
        if (alive) return;

        lock (_gate)
        {
            if (_debounce.HasPending) return;   // something newer is already on its way
            // The liveness check ran outside the lock: if another game was committed meanwhile, the one we
            // checked is no longer the settled state and must not take it down with it.
            if (!Equals(_debounce.Current, current)) return;
            _debounce.Observe(null, _clock());
            ArmLocked();
        }
    }

    /// <summary>Commits a matured change and raises <see cref="GameChanged"/>. Driven by the timer (or by tests).</summary>
    internal void Pump()
    {
        GameInfo? now;
        bool changed;
        lock (_gate)
        {
            if (_disposed) return;
            // A "no game" that only came from shell windows (tray click -> taskbar, then our own window) is not a
            // game that was left: with our window in front the game is still the one underneath.
            if (_debounce.PendingIsShellTransient && _debounce.DueAtMs <= _clock() && OwnWindowInFront())
                _debounce.CancelPending();
            changed = _debounce.Poll(_clock());
            now = _debounce.Current;
            if (changed) _current = now;
            else ArmLocked();   // fired early, or a newer candidate is waiting
        }
        if (!changed) return;

        Logger.Info(now is null
            ? "GameDetector: no game in front."
            : $"GameDetector: game in front: '{now.DisplayName}' ({now.ExeName}, PID {now.Pid}).");

        var handlers = GameChanged;
        if (handlers is null) return;
        foreach (Action<GameInfo?> h in handlers.GetInvocationList())
        {
            try { h(now); }
            catch (Exception ex) { Logger.Warn("GameDetector: GameChanged handler threw: " + ex.Message); }
        }
    }

    private bool OwnWindowInFront()
    {
        try { return _probe(_foreground()) is { } f && f.Pid == _selfPid; }
        catch { return false; }
    }

    private void ArmLocked()
    {
        if (_timer is null || _debounce.DueAtMs is not long due) return;
        long wait = Math.Max(0, due - _clock()) + 20;   // a hair after the due time so Poll finds it matured
        try { _timer.Change(wait, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _timer?.Dispose();
    }

    // ---------------- Win32 ----------------

    private static ForegroundFacts? ProbeWin32(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero || !User32.IsWindow(hwnd)) return null;
            User32.GetWindowThreadProcessId(hwnd, out uint upid);
            int pid = (int)upid;
            if (pid <= 0) return null;

            string? path = GameContext.ImagePath(pid);
            string exeName;
            if (path != null) exeName = Path.GetFileName(path);
            else
            {
                using var p = Process.GetProcessById(pid);   // throws when it is already gone
                exeName = p.ProcessName + ".exe";
            }

            if (!User32.GetWindowRect(hwnd, out var r)) return null;
            var b = Screen.FromHandle(hwnd).Bounds;
            int style = User32.GetWindowLong(hwnd, User32.GWL_STYLE);
            return new ForegroundFacts(pid, path ?? "", exeName,
                new LayoutRect(r.Left, r.Top, r.Width, r.Height),
                new LayoutRect(b.X, b.Y, b.Width, b.Height),
                User32.IsIconic(hwnd), User32.IsZoomed(hwnd),
                (style & User32.WS_CAPTION) == User32.WS_CAPTION,
                GameFolderNaming.IsWindowsSystemImage(path, WindowsDir));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Still running and still the same program (a pid can be reused). Access denied = cannot tell = alive.</summary>
    private static bool IsAliveWin32(GameInfo game)
    {
        var h = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, game.Pid);
        if (h == IntPtr.Zero)
            return System.Runtime.InteropServices.Marshal.GetLastWin32Error() == Kernel32.ERROR_ACCESS_DENIED;
        try
        {
            if (Kernel32.GetExitCodeProcess(h, out uint code) && code != Kernel32.STILL_ACTIVE) return false;
            var sb = new System.Text.StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            if (!Kernel32.QueryFullProcessImageName(h, 0, sb, ref size)) return true;
            return string.Equals(Path.GetFileName(sb.ToString(0, (int)size)), game.ExeName, StringComparison.OrdinalIgnoreCase);
        }
        finally { Kernel32.CloseHandle(h); }
    }
}
