using System;
using System.Collections.Generic;
using System.IO;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>
/// Why a run ended. The first block is what <see cref="StopRules.Evaluate"/> returns, in the order it checks; the rest come from the
/// hotkey/activation logic and the engine. <see cref="StopReasonText"/> turns them into text.
/// </summary>
public enum StopReason
{
    None,

    // StopRules, in evaluation order
    ClickLimit,
    StopZone,
    CornerTopLeft,
    CornerTopRight,
    CornerBottomLeft,
    CornerBottomRight,
    EdgeTop,
    EdgeBottom,
    EdgeLeft,
    EdgeRight,
    TaskSwitcher,
    NotOnWhitelist,
    Locked,
    TimeLimit,

    // everything else
    Hotkey,
    HoldReleased,
    MasterSwitch,
    AllPointsDone,
    LeftStartZone,
    PickerOpened,
    ClipperClosed,
    Manual
}

/// <summary>
/// Everything one stop check looks at. The engine builds it once from the settings (<see cref="From"/>) and then only updates the
/// moving fields (clicks, elapsed time, cursor, foreground window, lock, monitors) before each <see cref="StopRules.Evaluate"/>, so the
/// loop allocates nothing. The whitelist answer is cached per foreground program.
/// </summary>
public sealed class StopContext
{
    private IReadOnlyList<BlacklistEntry> _whitelist = Array.Empty<BlacklistEntry>();
    private string? _foregroundExe;
    private bool _cachedOnWhitelist;
    private bool _cacheValid;

    // ---- moving state ----
    public long Clicks { get; set; }
    public double ElapsedSeconds { get; set; }
    public PixelPoint Cursor { get; set; }
    public IReadOnlyList<PixelRect> Monitors { get; set; } = Array.Empty<PixelRect>();
    public string? ForegroundClass { get; set; }
    public bool Locked { get; set; }

    /// <summary>Exe name or path of the foreground window's process; null when it cannot be told (counts as "not on the whitelist").</summary>
    public string? ForegroundExe
    {
        get => _foregroundExe;
        set
        {
            if (string.Equals(_foregroundExe, value, StringComparison.Ordinal)) return;
            _foregroundExe = value;
            _cacheValid = false;
        }
    }

    // ---- configuration ----
    public bool ClickLimitEnabled { get; set; }
    public long ClickLimit { get; set; }
    public bool TimeLimitEnabled { get; set; }
    public double TimeLimitSeconds { get; set; }
    public bool CornerStopEnabled { get; set; }
    public int CornerSizePx { get; set; }
    public bool EdgeStopEnabled { get; set; }
    public int EdgeSizePx { get; set; }
    public bool TaskSwitcherStopEnabled { get; set; }
    public bool ZonesEnabled { get; set; }
    public IReadOnlyList<ClickerZone> Zones { get; set; } = Array.Empty<ClickerZone>();
    public bool WhitelistEnabled { get; set; }

    public IReadOnlyList<BlacklistEntry> Whitelist
    {
        get => _whitelist;
        set
        {
            _whitelist = value ?? Array.Empty<BlacklistEntry>();
            _cacheValid = false;
        }
    }

    /// <summary>The configuration half of the context from the (normalized) settings.</summary>
    public static StopContext From(ClickerSettings s) => new()
    {
        ClickLimitEnabled = s.ClickLimitEnabled,
        ClickLimit = s.ClickLimit,
        TimeLimitEnabled = s.TimeLimitEnabled,
        TimeLimitSeconds = StopRules.TimeLimitSeconds(s.TimeLimit, s.TimeLimitUnit),
        CornerStopEnabled = s.CornerStopEnabled,
        CornerSizePx = s.CornerSizePx,
        EdgeStopEnabled = s.EdgeStopEnabled,
        EdgeSizePx = s.EdgeSizePx,
        TaskSwitcherStopEnabled = s.TaskSwitcherStopEnabled,
        ZonesEnabled = s.ZonesEnabled,
        Zones = s.Zones,
        WhitelistEnabled = s.WhitelistEnabled,
        Whitelist = s.Whitelist
    };

    /// <summary>true when the foreground program is on the whitelist (same exe-name matching as the blacklist).</summary>
    internal bool ForegroundOnWhitelist()
    {
        if (!_cacheValid)
        {
            _cachedOnWhitelist = AutoClickerBlacklist.Contains(_whitelist, _foregroundExe);
            _cacheValid = true;
        }
        return _cachedOnWhitelist;
    }
}

/// <summary>
/// The failsafes checked on every loop iteration (design section 4), in this order: click limit, stop zone under the cursor, corner,
/// edge, Alt+Tab / Task View in front, whitelist, lock from the clipper, time limit. Corners and edges are checked per monitor against
/// that monitor's own rectangle, so monitors left of or above the primary one (negative coordinates) behave like any other; an edge
/// where two monitors touch counts for both of them.
/// </summary>
public static class StopRules
{
    public static StopReason Evaluate(StopContext c)
    {
        if (c.ClickLimitEnabled && c.Clicks >= c.ClickLimit) return StopReason.ClickLimit;
        if (c.ZonesEnabled && ZoneTracker.Hit(c.Zones, ZoneAction.Stop, c.Cursor.X, c.Cursor.Y)) return StopReason.StopZone;
        if (c.CornerStopEnabled && Corner(c) is var corner && corner != StopReason.None) return corner;
        if (c.EdgeStopEnabled && Edge(c) is var edge && edge != StopReason.None) return edge;
        if (c.TaskSwitcherStopEnabled && IsTaskSwitcher(c.ForegroundClass, c.ForegroundExe)) return StopReason.TaskSwitcher;
        if (c.WhitelistEnabled && !c.ForegroundOnWhitelist()) return StopReason.NotOnWhitelist;
        if (c.Locked) return StopReason.Locked;
        if (c.TimeLimitEnabled && c.ElapsedSeconds >= c.TimeLimitSeconds) return StopReason.TimeLimit;
        return StopReason.None;
    }

    public static double TimeLimitSeconds(double value, TimeLimitUnit unit) => value * unit switch
    {
        TimeLimitUnit.Minutes => 60.0,
        TimeLimitUnit.Hours => 3600.0,
        _ => 1.0
    };

    /// <summary>
    /// Alt+Tab and Task View in front. The classic classes always count; on Windows 11 the switcher lives in an explorer-hosted XAML
    /// island, so that class counts only for explorer.exe (other programs may use the same class name for their own windows).
    /// </summary>
    public static bool IsTaskSwitcher(string? windowClass, string? exe)
    {
        if (string.IsNullOrEmpty(windowClass)) return false;
        if (Is(windowClass, "TaskSwitcherWnd") || Is(windowClass, "TaskViewWindow")
            || Is(windowClass, "WindowsSwitchWindow") || Is(windowClass, "MultitaskingViewFrame")) return true;
        return Is(windowClass, "XamlExplorerHostIslandWindow") && exe is not null
            && Path.GetFileName(exe.AsSpan()).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static StopReason Corner(StopContext c)
    {
        int size = c.CornerSizePx;
        var monitors = c.Monitors;
        int x = c.Cursor.X, y = c.Cursor.Y;
        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            if (!m.Contains(x, y)) continue;
            bool left = x - m.X < size, right = m.Right - 1 - x < size;
            bool top = y - m.Y < size, bottom = m.Bottom - 1 - y < size;
            if (top && left) return StopReason.CornerTopLeft;
            if (top && right) return StopReason.CornerTopRight;
            if (bottom && left) return StopReason.CornerBottomLeft;
            if (bottom && right) return StopReason.CornerBottomRight;
        }
        return StopReason.None;
    }

    private static StopReason Edge(StopContext c)
    {
        int size = c.EdgeSizePx;
        var monitors = c.Monitors;
        int x = c.Cursor.X, y = c.Cursor.Y;
        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            if (!m.Contains(x, y)) continue;
            if (y - m.Y < size) return StopReason.EdgeTop;
            if (m.Bottom - 1 - y < size) return StopReason.EdgeBottom;
            if (x - m.X < size) return StopReason.EdgeLeft;
            if (m.Right - 1 - x < size) return StopReason.EdgeRight;
        }
        return StopReason.None;
    }
}
