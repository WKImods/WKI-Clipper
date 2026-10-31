using System;
using System.Collections.Generic;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services.AutoClicker;

public enum PauseChange { None, Paused, Resumed }

public enum StartZoneAction { None, Start, Stop }

/// <summary>
/// The zone state machines of the engine (design section 4). Pause zones: clicking is suspended while the cursor is inside any enabled
/// pause zone (the engine polls every 10 ms). Start zones: the host's 100 ms monitor starts the clicker when the cursor ENTERS a start
/// zone while the clicker is idle (and may start) and stops it again when the cursor leaves, but only a run the zone started. Stop zones
/// are not tracked here: <see cref="StopRules"/> checks them every iteration through <see cref="Hit"/>.
/// All of it is switched off by <c>ZonesEnabled</c>. Not thread-safe: the pause part belongs to the engine thread, the start part to the
/// monitor thread, and each only touches its own fields.
/// </summary>
public sealed class ZoneTracker
{
    private IReadOnlyList<ClickerZone> _zones = Array.Empty<ClickerZone>();
    private bool _enabled;

    private bool _paused;
    private bool _insideStart;
    private bool _startBaselined;   // false: the next PollStart only records where the cursor is
    private bool _startedByZone;

    public bool IsPaused => _paused;

    /// <summary>The current run was started by a start zone, so leaving the zone stops it.</summary>
    public bool StartedByZone => _startedByZone;

    /// <summary>
    /// Applies the zone list and the master switch for zones. Position state is kept, so pushing the same configuration again (every
    /// settings edit does) never makes a stopped clicker restart because the cursor still sits in a start zone.
    /// </summary>
    public void Configure(bool zonesEnabled, IReadOnlyList<ClickerZone>? zones)
    {
        _enabled = zonesEnabled;
        _zones = zones ?? Array.Empty<ClickerZone>();
    }

    /// <summary>true when an enabled zone of this action covers the pixel.</summary>
    public static bool Hit(IReadOnlyList<ClickerZone> zones, ZoneAction action, int x, int y)
    {
        for (int i = 0; i < zones.Count; i++)
        {
            var z = zones[i];
            if (z.Enabled && z.Action == action && x >= z.X && x < z.X + z.W && y >= z.Y && y < z.Y + z.H) return true;
        }
        return false;
    }

    // ---- pause zones ----

    /// <summary>One poll of the cursor: reports the moment the cursor enters (Paused) or leaves (Resumed) the pause zones.</summary>
    public PauseChange PollPause(PixelPoint cursor)
    {
        bool inside = _enabled && Hit(_zones, ZoneAction.Pause, cursor.X, cursor.Y);
        if (inside == _paused) return PauseChange.None;
        _paused = inside;
        return inside ? PauseChange.Paused : PauseChange.Resumed;
    }

    /// <summary>A new run starts unpaused; the first poll inside a pause zone reports it again.</summary>
    public void ResetPause() => _paused = false;

    // ---- start zones ----

    /// <summary>
    /// One poll of the 100 ms monitor. <paramref name="running"/> is whether the engine runs now; <paramref name="canStart"/> whether a
    /// start would be accepted (armed, idle, unlocked ...). Returns Start on the entering edge when idle and allowed - the tracker then
    /// assumes the start is executed (call <see cref="OnStartRefused"/> if the activation machine says no) - and Stop on the leaving
    /// edge of a run the zone started. An entering edge that cannot start is used up: staying inside starts nothing later. The first
    /// poll after creation or after the zones were switched back on is only a baseline: a cursor already parked in a start zone starts
    /// nothing until it leaves and enters again.
    /// </summary>
    public StartZoneAction PollStart(PixelPoint cursor, bool running, bool canStart)
    {
        if (!_enabled)
        {
            _insideStart = false;
            _startedByZone = false;
            _startBaselined = false;          // switching zones back on takes a fresh baseline
            return StartZoneAction.None;
        }

        bool inside = Hit(_zones, ZoneAction.Start, cursor.X, cursor.Y);
        if (!_startBaselined)
        {
            // Arming the host, a host restart or switching zones back on is not an entry.
            _startBaselined = true;
            _insideStart = inside;
            return StartZoneAction.None;
        }
        bool entered = inside && !_insideStart;
        bool left = !inside && _insideStart;
        _insideStart = inside;

        if (entered)
        {
            if (running) return StartZoneAction.None;
            if (!canStart) return StartZoneAction.None;
            _startedByZone = true;
            return StartZoneAction.Start;
        }

        if (left && _startedByZone)
        {
            _startedByZone = false;
            return running ? StartZoneAction.Stop : StartZoneAction.None;
        }
        return StartZoneAction.None;
    }

    /// <summary>The next <see cref="PollStart"/> is only a baseline again, as after creation (the host's clicker was armed again in a helper that kept running).</summary>
    public void ResetStartBaseline()
    {
        _startBaselined = false;
        _insideStart = false;
        _startedByZone = false;
    }

    /// <summary>The start the tracker asked for was not executed.</summary>
    public void OnStartRefused() => _startedByZone = false;

    /// <summary>The run ended (any reason): leaving the zone no longer has anything to stop.</summary>
    public void OnRunEnded() => _startedByZone = false;
}
