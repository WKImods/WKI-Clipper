using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>
/// Range rules of <see cref="ClickerSettings"/> (design section 3). <see cref="Normalize"/> runs after a load (EnsureSections), an
/// imported preset and every UI edit, so the host and the engine never see an out-of-range value.
/// A number that is NaN, infinite or negative falls back to its default; a number that is merely too small or too large is clamped.
/// </summary>
public static class ClickerSettingsRules
{
    public const double MaxClickSpeed = 500;
    public const double MaxClickSpeedExtended = 1000;

    /// <summary>With double click on, the rate is capped at this many cycles per second.</summary>
    public const double MaxDoubleClickPerSecond = 10;

    /// <summary>999 h 59 min 59 s 999 ms.</summary>
    public const long MaxDurationMs = 3_599_999_999L;

    public const int MaxClickLimit = 100_000_000;
    public const int MaxZones = 64;
    public const int MaxClickPoints = 500;
    public const int MaxPresets = 100;
    public const int MaxNameLength = 40;

    /// <summary>Virtual-screen coordinates are clamped to this magnitude (a corrupt file cannot carry int.MaxValue into the math).</summary>
    public const int CoordinateLimit = 100_000;

    private const int MaxStopReasonLength = 200;

    /// <summary>
    /// The keys of the sub-tab strip of the AutoClicker tab (design section 9). A key that is not here (such as a sub-tab removed
    /// before release) falls back to the first one when the settings are normalized.
    /// </summary>
    public static readonly IReadOnlyList<string> SubTabs = new[] { "simple", "advanced", "zones", "points", "presets" };

    /// <summary>Highest "n per unit" the UI may offer: 500, or 1000 with the extended limit; with double click never more than 10 per second.</summary>
    public static double MaxSpeed(bool extendedLimit, bool doubleClick, RateUnit unit)
    {
        double max = extendedLimit ? MaxClickSpeedExtended : MaxClickSpeed;
        return doubleClick ? Math.Min(max, MaxDoubleClickPerSecond * SecondsPerUnit(unit)) : max;
    }

    /// <summary>Shortest cycle in duration mode: 1 ms, or 100 ms (10 per second) with double click.</summary>
    public static long MinDurationMs(bool doubleClick) => doubleClick ? (long)(1000 / MaxDoubleClickPerSecond) : 1;

    public static double SecondsPerUnit(RateUnit unit) => unit switch
    {
        RateUnit.Minute => 60,
        RateUnit.Hour => 3600,
        RateUnit.Day => 86400,
        _ => 1
    };

    /// <summary>Clamps every field of the settings in place and returns the same instance. Null lists and sub-objects are replaced.</summary>
    public static ClickerSettings Normalize(ClickerSettings s)
    {
        s.RateInputMode = Valid(s.RateInputMode, RateInputMode.Rate);
        s.ClickUnit = Valid(s.ClickUnit, RateUnit.Second);
        s.DutyMode = Valid(s.DutyMode, DutyMode.Click);
        s.InputType = Valid(s.InputType, ClickerInput.Mouse);
        s.MouseButton = Valid(s.MouseButton, ClickerMouseButton.Left);
        s.KeyCase = Valid(s.KeyCase, KeyCase.Lower);
        s.ActivationMode = Valid(s.ActivationMode, ActivationMode.Toggle);
        s.MasterMode = Valid(s.MasterMode, ActivationMode.Toggle);
        s.TimeLimitUnit = Valid(s.TimeLimitUnit, TimeLimitUnit.Seconds);

        double maxSpeed = MaxSpeed(s.ExtendedSpeedLimit, s.DoubleClick, s.ClickUnit);
        s.ClickSpeed = Num(s.ClickSpeed, 1, maxSpeed, Math.Min(ClickerSettings.DefaultClickSpeed, maxSpeed));
        s.DurationMs = Num(s.DurationMs, MinDurationMs(s.DoubleClick), MaxDurationMs, ClickerSettings.DefaultDurationMs);
        s.SpeedRandomizationPercent = Num(s.SpeedRandomizationPercent, 0, 200, 35);
        s.DutyCyclePercent = Num(s.DutyCyclePercent, 0, 100, 45);

        s.KeyboardVk = s.KeyboardVk is < 1 or > 255 || ClickerVk.IsMouse(s.KeyboardVk) ? 0 : s.KeyboardVk;

        s.Hotkey = NormalizeHotkey(s.Hotkey, required: true)!;
        s.MasterHotkey = NormalizeHotkey(s.MasterHotkey, required: false);

        s.ClickLimit = Num(s.ClickLimit, 1, MaxClickLimit, 1000);
        s.TimeLimit = Num(s.TimeLimit, 0.1, 100_000, 60);

        s.CornerSizePx = Num(s.CornerSizePx, 1, 500, 50);
        s.EdgeSizePx = Num(s.EdgeSizePx, 1, 500, 40);

        s.Zones = NormalizeZones(s.Zones);
        s.ClickPoints = NormalizePoints(s.ClickPoints);
        s.Whitelist = AutoClickerBlacklist.Normalize(s.Whitelist);
        s.Presets = NormalizePresets(s.Presets);
        s.Stats = NormalizeStats(s.Stats);

        s.SubTab = SubTabs.FirstOrDefault(t => string.Equals(t, s.SubTab?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? SubTabs[0];
        return s;
    }

    /// <summary>Clamps a preset with the same rules as the settings (the hotkey fields a preset does not carry play no role).</summary>
    public static ClickerPreset NormalizePreset(ClickerPreset p)
    {
        var tmp = new ClickerSettings();
        ClickerPresets.CopyFields(p, tmp);
        Normalize(tmp);
        ClickerPresets.CopyFields(tmp, p);
        p.Name = CleanName(p.Name);
        return p;
    }

    /// <summary>
    /// A hotkey in its repaired form: undefined modifier bits dropped, a generic flag dropped when a sided one of its family is set,
    /// modifier keys, unknown codes and duplicates removed from the keys, at most 5 keys. Modifier-only hotkeys, a bare left mouse
    /// button and wheel keys stay as they are. Anything still invalid (neither modifier nor key, null) becomes the default Ctrl+Alt+Y
    /// when <paramref name="required"/>, else null ("no hotkey").
    /// </summary>
    public static ClickerHotkey? NormalizeHotkey(ClickerHotkey? hotkey, bool required)
    {
        if (hotkey is null) return required ? ClickerHotkey.Default() : null;

        var mods = hotkey.Modifiers & ModifierFamily.Mask;
        foreach (var f in ModifierFamily.Families)
            if ((mods & (f.Left | f.Right)) != 0) mods &= ~f.Generic;

        var keys = (hotkey.Keys ?? new List<int>())
            .Where(k => ClickerVk.IsKeyCode(k) && !ClickerVk.IsModifier(k))
            .Distinct()
            .Take(ClickerHotkey.MaxKeys)
            .ToList();

        var repaired = new ClickerHotkey { Modifiers = mods, Keys = keys };
        if (repaired.IsValid()) return repaired;
        return required ? ClickerHotkey.Default() : null;
    }

    /// <summary>Trimmed, control characters removed, at most 40 characters; "Preset" when nothing is left.</summary>
    public static string CleanName(string? name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name ?? "")
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        var clean = Truncate(sb.ToString().Trim(), MaxNameLength);
        return clean.Length == 0 ? "Preset" : clean;
    }

    /// <summary>The name, or "name (2)", "name (3)" ... when taken (case-insensitive); the result stays within 40 characters and is added to <paramref name="taken"/>.</summary>
    internal static string UniqueName(string name, ISet<string> taken)
    {
        if (taken.Add(name)) return name;
        for (int n = 2; ; n++)
        {
            var suffix = $" ({n})";
            var candidate = Truncate(name, MaxNameLength - suffix.Length) + suffix;
            if (taken.Add(candidate)) return candidate;
        }
    }

    private static List<ClickerPreset> NormalizePresets(List<ClickerPreset>? raw)
    {
        var result = new List<ClickerPreset>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in raw ?? new())
        {
            if (p is null) continue;
            if (result.Count >= MaxPresets) break;
            NormalizePreset(p);
            p.Name = UniqueName(p.Name, taken);
            result.Add(p);
        }
        return result;
    }

    private static List<ClickerZone> NormalizeZones(List<ClickerZone>? raw)
    {
        var result = new List<ClickerZone>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var z in raw ?? new())
        {
            if (z is null) continue;
            // A zone without area can never contain the cursor; old files carry them.
            if (z.W < 1 || z.H < 1) continue;
            if (result.Count >= MaxZones) break;

            z.X = Math.Clamp(z.X, -CoordinateLimit, CoordinateLimit);
            z.Y = Math.Clamp(z.Y, -CoordinateLimit, CoordinateLimit);
            z.W = Math.Clamp(z.W, 1, 2 * CoordinateLimit);
            z.H = Math.Clamp(z.H, 1, 2 * CoordinateLimit);
            z.Action = Valid(z.Action, ZoneAction.Stop);
            z.Name = Truncate((z.Name ?? "").Trim(), MaxNameLength);
            z.Id = string.IsNullOrWhiteSpace(z.Id) || !ids.Add(z.Id) ? NewUniqueId(ids) : z.Id;
            result.Add(z);
        }
        return result;
    }

    private static string NewUniqueId(ISet<string> ids)
    {
        while (true)
        {
            var id = Guid.NewGuid().ToString("N");
            if (ids.Add(id)) return id;
        }
    }

    private static List<ClickPoint> NormalizePoints(List<ClickPoint>? raw)
    {
        var result = new List<ClickPoint>();
        foreach (var p in raw ?? new())
        {
            if (p is null) continue;
            if (result.Count >= MaxClickPoints) break;
            p.X = Math.Clamp(p.X, -CoordinateLimit, CoordinateLimit);
            p.Y = Math.Clamp(p.Y, -CoordinateLimit, CoordinateLimit);
            p.Clicks = Num(p.Clicks, 1, 999_999, 1);
            p.Radius = Num(p.Radius, 0, 9999, 0);
            result.Add(p);
        }
        return result;
    }

    private static ClickerStats NormalizeStats(ClickerStats? stats)
    {
        stats ??= new ClickerStats();
        stats.TotalClicks = Math.Max(0, stats.TotalClicks);
        stats.TotalSeconds = Num(stats.TotalSeconds, 0, double.MaxValue, 0);
        stats.Sessions = Math.Max(0, stats.Sessions);

        var runs = new List<RunRecord>();
        foreach (var r in stats.LastRuns ?? new())
        {
            if (r is null) continue;
            if (runs.Count >= ClickerStats.MaxRuns) break;
            r.Seconds = Num(r.Seconds, 0, double.MaxValue, 0);
            r.Clicks = Math.Max(0, r.Clicks);
            r.StopReason = Truncate(r.StopReason ?? "", MaxStopReasonLength);
            runs.Add(r);
        }
        stats.LastRuns = runs;
        return stats;
    }

    private static T Valid<T>(T value, T fallback) where T : struct, Enum => Enum.IsDefined(value) ? value : fallback;

    private static double Num(double v, double min, double max, double def)
    {
        if (double.IsNaN(v) || double.IsInfinity(v) || v < 0) v = def;
        return Math.Clamp(v, min, max);
    }

    private static long Num(long v, long min, long max, long def) => Math.Clamp(v < 0 ? def : v, min, max);

    private static int Num(int v, int min, int max, int def) => Math.Clamp(v < 0 ? def : v, min, max);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd();
}
