using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services.AutoClicker;

public enum PresetImportError { None, InvalidJson, UnknownVersion, MissingPreset, TooLarge, ListFull }

/// <summary>Outcome of <see cref="ClickerPresets.Import"/>: the added preset, or why nothing was added.</summary>
public sealed record PresetImportResult(ClickerPreset? Preset, PresetImportError Error)
{
    public bool Success => Preset is not null;
}

/// <summary>
/// Presets as pure list rules (design section 9): three factory presets that live in code, user presets in
/// <see cref="ClickerSettings.Presets"/>, apply / save as / rename / delete and a JSON export/import. A preset carries the click,
/// failsafe, limit, zone, click-point and whitelist fields - never hotkeys, the armed state, UI fields or statistics.
/// Callers save the settings afterwards.
/// </summary>
public static class ClickerPresets
{
    /// <summary>Format version of an exported preset file; <see cref="Import"/> rejects any other.</summary>
    public const int ExportVersion = 1;

    /// <summary>An import file larger than this (characters) is refused before parsing.</summary>
    public const int MaxImportLength = 1_000_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>The three built-in presets: Standard (25 per s, duty 45, randomization 35), Rapid Fire (100, 20, 15, limit 5000), Precision (10, 60, 10). Fresh instances every call.</summary>
    public static IReadOnlyList<ClickerPreset> Factory() => new[]
    {
        new ClickerPreset
        {
            Name = L.T("Standard", "Standard"),
            ClickSpeed = 25, DutyCyclePercent = 45, SpeedRandomizationPercent = 35
        },
        new ClickerPreset
        {
            Name = "Rapid Fire",
            ClickSpeed = 100, DutyCyclePercent = 20, SpeedRandomizationPercent = 15,
            ClickLimitEnabled = true, ClickLimit = 5000
        },
        new ClickerPreset
        {
            Name = L.T("Präzision", "Precision"),
            ClickSpeed = 10, DutyCyclePercent = 60, SpeedRandomizationPercent = 10
        }
    };

    /// <summary>Copies the preset's fields into the settings (deep copy, then normalized). Hotkeys, Armed, presets and stats stay as they are.</summary>
    public static void Apply(ClickerPreset preset, ClickerSettings settings)
    {
        CopyFields(preset, settings);
        ClickerSettingsRules.Normalize(settings);
    }

    /// <summary>
    /// Applies a factory preset: only rate, duty, randomization and click limit (design section 9). Zones, click points, whitelist,
    /// failsafes, the output key and the other fields stay as they are. Use <see cref="Apply"/> for user presets.
    /// </summary>
    public static void ApplyFactory(ClickerPreset preset, ClickerSettings settings)
    {
        settings.RateInputMode = RateInputMode.Rate;
        settings.ClickUnit = RateUnit.Second;
        settings.ClickSpeed = preset.ClickSpeed;
        settings.DutyCycleEnabled = true;
        settings.DutyCyclePercent = preset.DutyCyclePercent;
        settings.SpeedRandomizationEnabled = true;
        settings.SpeedRandomizationPercent = preset.SpeedRandomizationPercent;
        settings.ClickLimitEnabled = preset.ClickLimitEnabled;
        settings.ClickLimit = preset.ClickLimit;
        ClickerSettingsRules.Normalize(settings);
    }

    /// <summary>A preset made from the settings' current fields (deep copy). The name is cleaned but not made unique.</summary>
    public static ClickerPreset Capture(ClickerSettings settings, string? name)
    {
        var preset = new ClickerPreset { Name = ClickerSettingsRules.CleanName(name) };
        CopyFields(settings, preset);
        return ClickerSettingsRules.NormalizePreset(preset);
    }

    /// <summary>
    /// "Als Preset speichern": stores the current fields under the name. A user preset of the same name (case-insensitive) is
    /// overwritten; a new one is appended. null when the name is blank or the list holds 100 presets already.
    /// </summary>
    public static ClickerPreset? SaveAs(ClickerSettings settings, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var preset = Capture(settings, name);

        int existing = settings.Presets.FindIndex(p => SameName(p.Name, preset.Name));
        if (existing >= 0)
        {
            settings.Presets[existing] = preset;
            return preset;
        }
        if (settings.Presets.Count >= ClickerSettingsRules.MaxPresets) return null;
        settings.Presets.Add(preset);
        return preset;
    }

    /// <summary>False when the preset does not exist, the new name is blank or another preset already has it. A change of case only is fine.</summary>
    public static bool Rename(List<ClickerPreset> presets, string oldName, string? newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return false;
        var target = presets.Find(p => SameName(p.Name, oldName));
        if (target is null) return false;
        var clean = ClickerSettingsRules.CleanName(newName);
        if (presets.Any(p => !ReferenceEquals(p, target) && SameName(p.Name, clean))) return false;
        target.Name = clean;
        return true;
    }

    public static bool Delete(List<ClickerPreset> presets, string name)
        => presets.RemoveAll(p => SameName(p.Name, name)) > 0;

    /// <summary>One preset as a file: <c>{ "Version": 1, "Preset": { ... } }</c>.</summary>
    public static string Export(ClickerPreset preset)
        => JsonSerializer.Serialize(new ExportFile { Version = ExportVersion, Preset = preset }, JsonOptions);

    /// <summary>
    /// Reads an exported preset and appends it to <paramref name="presets"/>. The preset is normalized (every range clamped) and
    /// gets a free name ("name (2)" when taken). Refused: not JSON / wrong shape, a version other than 1, no preset inside, a
    /// huge text, a full list. Nothing is added on a refusal.
    /// </summary>
    public static PresetImportResult Import(List<ClickerPreset> presets, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(null, PresetImportError.InvalidJson);
        if (json.Length > MaxImportLength) return new(null, PresetImportError.TooLarge);

        ExportFile? file;
        try { file = JsonSerializer.Deserialize<ExportFile>(json, JsonOptions); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { return new(null, PresetImportError.InvalidJson); }

        if (file is null) return new(null, PresetImportError.InvalidJson);
        if (file.Version != ExportVersion) return new(null, PresetImportError.UnknownVersion);
        if (file.Preset is null) return new(null, PresetImportError.MissingPreset);
        if (presets.Count >= ClickerSettingsRules.MaxPresets) return new(null, PresetImportError.ListFull);

        var preset = ClickerSettingsRules.NormalizePreset(file.Preset);
        preset.Name = ClickerSettingsRules.UniqueName(preset.Name, new HashSet<string>(presets.Select(p => p.Name), StringComparer.OrdinalIgnoreCase));
        presets.Add(preset);
        return new(preset, PresetImportError.None);
    }

    private static bool SameName(string? a, string? b)
        => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private sealed class ExportFile
    {
        public int Version { get; set; }
        public ClickerPreset? Preset { get; set; }
    }

    /// <summary>Preset -> settings: the shared fields, lists deep-copied. The one place that knows which fields a preset carries.</summary>
    internal static void CopyFields(ClickerPreset p, ClickerSettings s)
    {
        s.RateInputMode = p.RateInputMode; s.ClickSpeed = p.ClickSpeed; s.ClickUnit = p.ClickUnit; s.DurationMs = p.DurationMs;
        s.ExtendedSpeedLimit = p.ExtendedSpeedLimit;
        s.SpeedRandomizationEnabled = p.SpeedRandomizationEnabled; s.SpeedRandomizationPercent = p.SpeedRandomizationPercent;
        s.DutyCycleEnabled = p.DutyCycleEnabled; s.DutyCyclePercent = p.DutyCyclePercent; s.DutyMode = p.DutyMode;
        s.InputType = p.InputType; s.MouseButton = p.MouseButton; s.KeyboardVk = p.KeyboardVk; s.KeyCase = p.KeyCase;
        s.DoubleClick = p.DoubleClick;
        s.ClickLimitEnabled = p.ClickLimitEnabled; s.ClickLimit = p.ClickLimit;
        s.TimeLimitEnabled = p.TimeLimitEnabled; s.TimeLimit = p.TimeLimit; s.TimeLimitUnit = p.TimeLimitUnit;
        s.CornerStopEnabled = p.CornerStopEnabled; s.CornerSizePx = p.CornerSizePx;
        s.EdgeStopEnabled = p.EdgeStopEnabled; s.EdgeSizePx = p.EdgeSizePx;
        s.TaskSwitcherStopEnabled = p.TaskSwitcherStopEnabled;
        s.ZonesEnabled = p.ZonesEnabled; s.Zones = CopyZones(p.Zones);
        s.ClickPointsEnabled = p.ClickPointsEnabled; s.ClickPoints = CopyPoints(p.ClickPoints); s.StopWhenComplete = p.StopWhenComplete;
        s.WhitelistEnabled = p.WhitelistEnabled; s.Whitelist = CopyWhitelist(p.Whitelist);
    }

    /// <summary>Settings -> preset (the inverse of the overload above).</summary>
    internal static void CopyFields(ClickerSettings s, ClickerPreset p)
    {
        p.RateInputMode = s.RateInputMode; p.ClickSpeed = s.ClickSpeed; p.ClickUnit = s.ClickUnit; p.DurationMs = s.DurationMs;
        p.ExtendedSpeedLimit = s.ExtendedSpeedLimit;
        p.SpeedRandomizationEnabled = s.SpeedRandomizationEnabled; p.SpeedRandomizationPercent = s.SpeedRandomizationPercent;
        p.DutyCycleEnabled = s.DutyCycleEnabled; p.DutyCyclePercent = s.DutyCyclePercent; p.DutyMode = s.DutyMode;
        p.InputType = s.InputType; p.MouseButton = s.MouseButton; p.KeyboardVk = s.KeyboardVk; p.KeyCase = s.KeyCase;
        p.DoubleClick = s.DoubleClick;
        p.ClickLimitEnabled = s.ClickLimitEnabled; p.ClickLimit = s.ClickLimit;
        p.TimeLimitEnabled = s.TimeLimitEnabled; p.TimeLimit = s.TimeLimit; p.TimeLimitUnit = s.TimeLimitUnit;
        p.CornerStopEnabled = s.CornerStopEnabled; p.CornerSizePx = s.CornerSizePx;
        p.EdgeStopEnabled = s.EdgeStopEnabled; p.EdgeSizePx = s.EdgeSizePx;
        p.TaskSwitcherStopEnabled = s.TaskSwitcherStopEnabled;
        p.ZonesEnabled = s.ZonesEnabled; p.Zones = CopyZones(s.Zones);
        p.ClickPointsEnabled = s.ClickPointsEnabled; p.ClickPoints = CopyPoints(s.ClickPoints); p.StopWhenComplete = s.StopWhenComplete;
        p.WhitelistEnabled = s.WhitelistEnabled; p.Whitelist = CopyWhitelist(s.Whitelist);
    }

    private static List<ClickerZone> CopyZones(List<ClickerZone>? src)
        => (src ?? new()).Where(z => z is not null)
            .Select(z => new ClickerZone { Id = z.Id, Name = z.Name, X = z.X, Y = z.Y, W = z.W, H = z.H, Action = z.Action, Enabled = z.Enabled })
            .ToList();

    private static List<ClickPoint> CopyPoints(List<ClickPoint>? src)
        => (src ?? new()).Where(p => p is not null)
            .Select(p => new ClickPoint { X = p.X, Y = p.Y, Clicks = p.Clicks, Radius = p.Radius })
            .ToList();

    private static List<BlacklistEntry> CopyWhitelist(List<BlacklistEntry>? src)
        => (src ?? new()).Where(e => e is not null)
            .Select(e => new BlacklistEntry { ExeName = e.ExeName, DisplayName = e.DisplayName })
            .ToList();
}
