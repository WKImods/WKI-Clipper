using System;
using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>
/// Limits and repair rules of the mouse patterns in <see cref="ClickerSettings"/> (mouse-patterns design 4.1, 5.2).
/// <see cref="Normalize"/> runs as part of <see cref="ClickerSettingsRules.Normalize"/>.
/// </summary>
public static class MousePatternRules
{
    public const int MaxPatterns = 50;
    public const double MinSpeed = 0.25;
    public const double MaxSpeed = 4.0;
    public const double DefaultSpeed = 1.0;

    /// <summary>A recording stops by itself after this long (60 s after the first movement).</summary>
    public const int MaxDurationMs = 60_000;

    /// <summary>Samples are summed into bins of this many milliseconds.</summary>
    public const int BinMs = 1;

    /// <summary>The helper process is wanted while the clicker or the patterns are armed.</summary>
    public static bool HostWanted(ClickerSettings s) => s.Armed || s.MousePatternsArmed;

    /// <summary>Strg+Alt+R (no clipper default uses it). A new instance every call: hotkeys are mutable.</summary>
    public static ClickerHotkey DefaultRecordHotkey() => new() { Modifiers = ClickerModifiers.Ctrl | ClickerModifiers.Alt, Keys = { 0x52 } };

    /// <summary>32 hex characters (a GUID in "N" form): the only ids that become file names.</summary>
    public static bool IsValidId(string? id) => id is { Length: 32 } && id.All(char.IsAsciiHexDigit);

    /// <summary>"Muster 1", "Muster 2" ...: the first number whose name is not taken (case-insensitive).</summary>
    public static string NextName(IEnumerable<MousePattern> patterns)
    {
        var taken = new HashSet<string>(patterns.Where(p => p is not null).Select(p => p.Name ?? ""), StringComparer.OrdinalIgnoreCase);
        var prefix = L.T("Muster", "Pattern");
        for (int n = 1; ; n++)
        {
            var name = $"{prefix} {n}";
            if (!taken.Contains(name)) return name;
        }
    }

    /// <summary>
    /// Repairs the record hotkey and the pattern list in place: null entries dropped, at most <see cref="MaxPatterns"/>, ids valid and
    /// unique (a file name, so compared case-insensitively), speed clamped (NaN/infinite/negative -> 1x), names cleaned with the next
    /// free default name as fallback, hotkeys repaired (an invalid one becomes "no hotkey"), the program reduced to an exe file name.
    /// </summary>
    public static void Normalize(ClickerSettings s)
    {
        s.PatternRecordHotkey = ClickerSettingsRules.NormalizeHotkey(s.PatternRecordHotkey, required: false) ?? DefaultRecordHotkey();

        var result = new List<MousePattern>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in s.MousePatterns ?? new())
        {
            if (p is null) continue;
            if (result.Count >= MaxPatterns) break;

            p.Id = IsValidId(p.Id) && ids.Add(p.Id) ? p.Id : NewUniqueId(ids);
            p.Name = ClickerSettingsRules.CleanName(p.Name, NextName(result));
            p.Hotkey = ClickerSettingsRules.NormalizeHotkey(p.Hotkey, required: false);
            p.Mode = Enum.IsDefined(p.Mode) ? p.Mode : ActivationMode.Hold;
            p.Replay = Enum.IsDefined(p.Replay) ? p.Replay : PatternReplay.Auto;
            p.Speed = double.IsFinite(p.Speed) && p.Speed >= 0 ? Math.Clamp(p.Speed, MinSpeed, MaxSpeed) : DefaultSpeed;
            p.Program = AutoClickerBlacklist.Canonical(p.Program);
            if (p.Program is null) p.OnlyInProgram = false;   // nothing to bind to
            p.DurationMs = double.IsFinite(p.DurationMs) ? Math.Clamp(p.DurationMs, 0, MaxDurationMs) : 0;
            p.Revision = Math.Max(0, p.Revision);
            result.Add(p);
        }
        s.MousePatterns = result;
    }

    private static string NewUniqueId(ISet<string> ids)
    {
        while (true)
        {
            var id = Guid.NewGuid().ToString("N");
            if (ids.Add(id)) return id;
        }
    }
}
