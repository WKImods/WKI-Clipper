using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WKI_Clipper.Services;

public enum FrameMetricsStatus
{
    /// <summary>Values are current.</summary>
    Ok,
    /// <summary>No right to start an ETW session — needs "Performance Log Users" (S-1-5-32-559) or admin.</summary>
    NoPermission,
    /// <summary>No game process is targeted (none set, or it would be the clipper itself).</summary>
    NoGame,
    /// <summary>Targeted, but it doesn't present (minimized, loading, just started).</summary>
    NoData,
    /// <summary>ETW dropped events in the current window — an FPS value would be too low ("k. A.").</summary>
    EventsLost,
    /// <summary>Not running (nobody subscribed) or the session could not be started.</summary>
    Unavailable,
}

/// <summary>One ~1 Hz reading of the target game's frame metrics. Values are null unless <see cref="Status"/> is Ok
/// (the lows/stutter additionally need ≥ 100 frames).</summary>
public sealed record FrameMetricsSnapshot(
    FrameMetricsStatus Status,
    int ProcessId = 0,
    PresentApi Api = PresentApi.Unknown,
    double? Fps = null,
    double? AvgFrameTimeMs = null,
    double? Low1 = null,
    double? Low01 = null,
    double? StutterPercent = null)
{
    public static readonly FrameMetricsSnapshot Unavailable = new(FrameMetricsStatus.Unavailable);
    public static readonly FrameMetricsSnapshot NoPermission = new(FrameMetricsStatus.NoPermission);
    public static readonly FrameMetricsSnapshot NoGame = new(FrameMetricsStatus.NoGame);
}

/// <summary>
/// Decides which presents of the target process are "the game's frames" and turns them into a
/// <see cref="FrameMetricsSnapshot"/>. Pure (timestamps come in, no clock, no ETW); not
/// thread-safe — <see cref="FrameTimeMonitor"/> serializes access.
///
/// Stream selection, re-evaluated once per 1 s epoch of event time:
/// <list type="bullet">
/// <item>Each swap chain (per runtime) and each kernel marker is its own stream; only the
///   <b>dominant</b> one is counted. The first stream seen is taken provisionally (no warm-up
///   delay), the first epoch picks the busiest, after that a stream must present more than twice
///   as often as the current one (or the current one must have gone silent) to take over — so two
///   equally busy windows don't flap.</item>
/// <item>Runtime streams (DXGI, D3D9) always win over the kernel fallback: D3D11/12 games emit
///   both, and counting both would double the FPS. Kernel events count only for a process that
///   produced no runtime present in the epoch (Vulkan / OpenGL).</item>
/// <item>Changing the target, the dominant stream, or losing events starts the statistics over.</item>
/// </list>
/// </summary>
internal sealed class PresentTracker
{
    /// <summary>No present for this long (event time vs. now) = the game isn't rendering. Covers ETW's ≤ 1 s flush latency.</summary>
    public const double StaleSeconds = 3.0;

    private readonly long _freq;
    private readonly long _staleTicks;
    private readonly FrameStats _stats;

    private int _pid;

    private bool _hasDominant, _confirmed;
    private ulong _dominant;

    private bool _epochOpen;
    private long _epochStart;
    private readonly Dictionary<ulong, int> _epochCounts = new();

    private bool _hasPresent;
    private long _lastPresent;

    private bool _lossPending;
    private long _ignoreBefore = long.MinValue;

    public PresentTracker(long ticksPerSecond)
    {
        _freq = ticksPerSecond;
        _staleTicks = (long)(StaleSeconds * ticksPerSecond);
        _stats = new FrameStats(ticksPerSecond);
    }

    public int TargetPid => _pid;

    /// <summary>Frames in the statistics window (diagnostics / tests).</summary>
    public int FrameCount => _stats.FrameCount;

    public PresentApi Api => _hasDominant ? SourceOf(_dominant) switch
    {
        PresentSource.Dxgi => PresentApi.Dxgi,
        PresentSource.D3D9 => PresentApi.D3D9,
        _ => PresentApi.Other,
    } : PresentApi.Unknown;

    /// <summary>New target process (0 = none). Always starts over.</summary>
    public void SetTarget(int pid)
    {
        _pid = Math.Max(0, pid);
        _stats.Reset();
        _hasDominant = _confirmed = false;
        _dominant = 0;
        _epochOpen = false;
        _epochCounts.Clear();
        _hasPresent = false;
        _lastPresent = 0;
        _lossPending = false;
        _ignoreBefore = long.MinValue;
    }

    public void OnPresent(in PresentEvent e)
    {
        if (_pid == 0 || e.ProcessId != _pid) return;
        if (e.Timestamp < _ignoreBefore) return;   // from before a loss, delivered late

        if (!_epochOpen)
        {
            _epochOpen = true;
            _epochStart = e.Timestamp;
        }
        else if (e.Timestamp - _epochStart >= _freq)
        {
            CloseEpoch();
            _epochStart = e.Timestamp;
        }

        ulong key = e.StreamKey;
        CollectionsMarshal.GetValueRefOrAddDefault(_epochCounts, key, out _)++;

        if (!_hasDominant)
        {
            _hasDominant = true;
            _dominant = key;
        }
        else if (!_confirmed && e.IsRuntime && !IsRuntime(_dominant))
        {
            // A provisional kernel stream must not count for even one epoch once the
            // runtime shows up: that is a D3D11/12 game, not a Vulkan one.
            _dominant = key;
            _stats.Reset();
        }

        if (key != _dominant) return;
        _stats.Add(e.Timestamp);
        _hasPresent = true;
        _lastPresent = e.Timestamp;
    }

    /// <summary>ETW lost events or buffers at <paramref name="timestamp"/>: the window has a hole.</summary>
    public void OnEventsLost(long timestamp)
    {
        _stats.Reset();
        if (timestamp > _ignoreBefore) _ignoreBefore = timestamp;
        _lossPending = true;
    }

    public FrameMetricsSnapshot Snapshot(long now)
    {
        if (_pid == 0) return FrameMetricsSnapshot.NoGame;

        if (!_hasPresent || now - _lastPresent > _staleTicks)
        {
            _lossPending = false;   // nothing is rendering; "no data" is the honest answer
            return new FrameMetricsSnapshot(FrameMetricsStatus.NoData, _pid, Api);
        }

        var m = _stats.Compute();
        if (_lossPending)
        {
            if (m.Fps is null) return new FrameMetricsSnapshot(FrameMetricsStatus.EventsLost, _pid, Api);
            _lossPending = false;
        }
        if (m.Fps is null) return new FrameMetricsSnapshot(FrameMetricsStatus.NoData, _pid, Api);

        return new FrameMetricsSnapshot(FrameMetricsStatus.Ok, _pid, Api,
            m.Fps, m.AvgFrameTimeMs, m.Low1, m.Low01, m.StutterPercent);
    }

    private void CloseEpoch()
    {
        bool anyRuntime = false;
        foreach (var (k, c) in _epochCounts)
            if (c > 0 && IsRuntime(k)) { anyRuntime = true; break; }

        ulong best = 0;
        int bestCount = 0;
        foreach (var (k, c) in _epochCounts)
        {
            if (IsRuntime(k) != anyRuntime) continue;
            if (c > bestCount || (c == bestCount && k == _dominant)) { best = k; bestCount = c; }
        }

        if (bestCount > 0 && best != _dominant)
        {
            _epochCounts.TryGetValue(_dominant, out int current);
            bool eligible = IsRuntime(_dominant) == anyRuntime;
            bool takeOver = !eligible
                || current == 0
                || (_confirmed ? bestCount > 2 * current : bestCount > current);
            if (takeOver)
            {
                _dominant = best;
                _stats.Reset();
            }
        }

        _confirmed = true;
        _epochCounts.Clear();
    }

    private static PresentSource SourceOf(ulong key) => (PresentSource)(key >> 56);

    private static bool IsRuntime(ulong key) => SourceOf(key) is PresentSource.Dxgi or PresentSource.D3D9;
}
