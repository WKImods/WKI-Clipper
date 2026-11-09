using System;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace WKI_Clipper.Services;

/// <summary>
/// Follows display changes (decision 7): every display event (a display settings change, resume from sleep) stamps
/// <see cref="LastChangeUtc"/> and invalidates the topology cache; <see cref="Debounce"/> after the last event of a burst
/// the topology is read once. A changed topology is logged with its dump and announced (<see cref="TopologyChanged"/>, the
/// monitor lists rebuild); the buffer is retargeted only when the capture would now record another picture
/// (<see cref="NeedsRetarget"/>). Every settled burst is announced (<see cref="Settled"/>), changed or not: a buffer that
/// waits for its monitor starts again then. A game flipping the mode several times while it starts causes one read (R8).
/// </summary>
public sealed class DisplayTopologyWatcher : IDisposable
{
    /// <summary>How long the display gets to settle after the last event before it is read.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(1500);

    private readonly DisplayTopologyCache _cache;
    private readonly TimeProvider _time;
    private readonly Func<CaptureSource?> _currentSource;
    private readonly Func<CaptureSource?> _resolveSource;
    private readonly Action _requestRetarget;
    private readonly Func<bool>? _modeChangeNeedsRetarget;
    private readonly IDisplayEvents _events;
    private readonly ITimer _timer;
    private readonly object _settleGate = new();
    private DisplayTopology _known;
    private long _lastChangeTicks;   // 0 = no event yet
    private string _lastEvent = "";  // what the last event was, for the log
    private int _disposed;

    /// <param name="cache">The topology the capture resolves on (invalidated on every event).</param>
    /// <param name="currentSource">What the buffer records right now; null when nothing runs.</param>
    /// <param name="resolveSource">What the buffer's target resolves to on the topology now; null when nothing can be recorded.</param>
    /// <param name="requestRetarget">Moves the buffer to the re-resolved source (debounced by the buffer itself).</param>
    /// <param name="modeChangeNeedsRetarget">
    /// True while the running capture does not end by itself on a mode switch of its monitor (vsrc_amf keeps waiting for
    /// frames): a new size of the same monitor then needs a retarget too. Null = never (ddagrab ends on it).
    /// </param>
    public DisplayTopologyWatcher(DisplayTopologyCache cache, TimeProvider time, Func<CaptureSource?> currentSource,
                                  Func<CaptureSource?> resolveSource, Action requestRetarget,
                                  Func<bool>? modeChangeNeedsRetarget = null)
        : this(cache, time, currentSource, resolveSource, requestRetarget, new SystemDisplayEvents(), modeChangeNeedsRetarget)
    {
    }

    internal DisplayTopologyWatcher(DisplayTopologyCache cache, TimeProvider time, Func<CaptureSource?> currentSource,
                                    Func<CaptureSource?> resolveSource, Action requestRetarget, IDisplayEvents events,
                                    Func<bool>? modeChangeNeedsRetarget = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _currentSource = currentSource ?? throw new ArgumentNullException(nameof(currentSource));
        _resolveSource = resolveSource ?? throw new ArgumentNullException(nameof(resolveSource));
        _requestRetarget = requestRetarget ?? throw new ArgumentNullException(nameof(requestRetarget));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _modeChangeNeedsRetarget = modeChangeNeedsRetarget;
        // What the first change is compared with.
        _known = cache.Get();
        _timer = time.CreateTimer(_ => OnSettled(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _events.Raised += OnDisplayEvent;
    }

    /// <summary>
    /// When the last display event came (UTC); null before the first. An ffmpeg exit soon after it counts as caused by the
    /// display (<see cref="DisplayRestartPolicy"/>).
    /// </summary>
    public DateTime? LastChangeUtc
    {
        get
        {
            long ticks = Interlocked.Read(ref _lastChangeTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    /// <summary>The topology changed (fingerprint), raised on a pool thread once the display settled; UI consumers marshal themselves.</summary>
    public event Action? TopologyChanged;

    /// <summary>The display settled after a burst of events (raised on a pool thread after every debounced read, changed or not).</summary>
    public event Action? Settled;

    /// <summary>
    /// The capture would record another picture on the topology now: another monitor, or the same one through another
    /// adapter output (a new primary renumbers the outputs - ddagrab's old index would show another monitor). Not a mode
    /// change of the same monitor on the same output: ddagrab ends on it by itself and the buffer's automatic restart picks
    /// up the new size, one restart per switch (R2) - unless <paramref name="sizeCounts"/>: a capture that does not end on
    /// a mode switch (vsrc_amf) needs the retarget for the new size. Not a monitor whose device path Windows lost or reports
    /// again either (<see cref="CaptureSourceRules.SameMonitorOrPathless"/>).
    /// </summary>
    internal static bool NeedsRetarget(CaptureSource current, CaptureSource next, bool sizeCounts = false)
        => !(CaptureSourceRules.SameMonitorOrPathless(current, next)
             && current.AdapterLuid == next.AdapterLuid && current.OutputIndex == next.OutputIndex
             && (!sizeCounts || current.Width == next.Width && current.Height == next.Height));

    private void OnDisplayEvent(string what)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Interlocked.Exchange(ref _lastChangeTicks, _time.GetUtcNow().UtcDateTime.Ticks);
        Volatile.Write(ref _lastEvent, what);
        _cache.Invalidate();
        // Every further event of the burst pushes the read back.
        try { _timer.Change(Debounce, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private void OnSettled()
    {
        bool changed, retarget;
        lock (_settleGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try
            {
                var old = _known;
                var fresh = _cache.Get();
                _known = fresh;
                changed = !string.Equals(old.Fingerprint, fresh.Fingerprint, StringComparison.Ordinal);

                var current = _currentSource();
                var next = current is null ? null : _resolveSource();
                retarget = current is not null && next is not null
                           && NeedsRetarget(current, next, sizeCounts: _modeChangeNeedsRetarget?.Invoke() ?? false);
                string target = current is null ? "no capture running"
                    : next is null ? "unchanged (no monitor to move to)"
                    : retarget ? "changed -> restart" : "unchanged";

                Logger.Info(changed
                    ? $"Display topology changed ({Volatile.Read(ref _lastEvent)}): {Summary(old)} -> {Summary(fresh)}; target {target}{Environment.NewLine}{fresh.Dump()}"
                    : $"Display event ({Volatile.Read(ref _lastEvent)}): topology unchanged; target {target}");
            }
            catch (Exception ex)
            {
                Logger.Warn("Display topology watcher failed: " + ex.Message);
                return;
            }
        }

        if (changed)
        {
            try { TopologyChanged?.Invoke(); }
            catch (Exception ex) { Logger.Warn("TopologyChanged handler threw: " + ex.Message); }
        }
        if (retarget)
        {
            try { _requestRetarget(); }
            catch (Exception ex) { Logger.Warn("Display watcher retarget failed: " + ex.Message); }
        }
        try { Settled?.Invoke(); }
        catch (Exception ex) { Logger.Warn("Display watcher Settled handler threw: " + ex.Message); }
    }

    /// <summary>"LG ULTRAGEAR+ 3440x1440@(0,0)*, DELL S2721DGF 2560x1440@(3440,0)" (left to right, * = primary).</summary>
    private static string Summary(DisplayTopology t)
        => t.Displays.Count == 0 ? "no displays"
            : string.Join(", ", t.Displays.OrderBy(d => d.X).ThenBy(d => d.Y)
                .Select(d => $"{d.FriendlyName ?? d.GdiName} {d.Width}x{d.Height}@({d.X},{d.Y}){(d.IsPrimary ? "*" : "")}"));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _events.Raised -= OnDisplayEvent;
        _timer.Dispose();
    }
}

/// <summary>Where display events come from: Windows in the app, a fake in tests. Raised with what happened.</summary>
internal interface IDisplayEvents
{
    event Action<string>? Raised;
}

/// <summary>
/// Windows' display events: <see cref="SystemEvents.DisplaySettingsChanged"/> and the resume of
/// <see cref="SystemEvents.PowerModeChanged"/> (monitors come back after sleep). Subscribed to Windows only while someone
/// listens - SystemEvents holds its handlers statically.
/// </summary>
internal sealed class SystemDisplayEvents : IDisplayEvents
{
    private readonly object _gate = new();
    private Action<string>? _raised;

    public event Action<string>? Raised
    {
        add
        {
            lock (_gate)
            {
                if (_raised is null && value is not null)
                {
                    SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
                    SystemEvents.PowerModeChanged += OnPowerModeChanged;
                }
                _raised += value;
            }
        }
        remove
        {
            lock (_gate)
            {
                if (_raised is null) return;
                _raised -= value;
                if (_raised is null)
                {
                    SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                    SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                }
            }
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Volatile.Read(ref _raised)?.Invoke("display settings");

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Volatile.Read(ref _raised)?.Invoke("resume");
    }
}
