using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // Safety net: a stray NaN/Infinity in ANY double (window geometry, sliders)
        // would otherwise make the whole settings file unwritable and brick startup.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Current settings schema version — bump when migrating.</summary>
    public const int CurrentSchemaVersion = 6;

    public string SettingsFilePath { get; }
    public string AppDataDir { get; }

    public AppSettings Current { get; private set; } = new();

    public event EventHandler<AppSettings>? SettingsChanged;

    public SettingsService()
    {
        AppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WKI_Clipper");
        Directory.CreateDirectory(AppDataDir);
        SettingsFilePath = Path.Combine(AppDataDir, "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(SettingsFilePath))
        {
            // Fresh install: use the modern defaults (Auto capture + coupled audio),
            // no legacy migration needed.
            Current = new AppSettings { SchemaVersion = CurrentSchemaVersion };
            Save();
            return Current;
        }

        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            Current = loaded ?? new AppSettings { SchemaVersion = CurrentSchemaVersion };
        }
        catch (Exception)
        {
            // Corrupt settings file → back it up, start fresh
            var backup = SettingsFilePath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try { File.Move(SettingsFilePath, backup); } catch { }
            Current = new AppSettings { SchemaVersion = CurrentSchemaVersion };
            Save();
        }

        if (MigrateIfNeeded(Current))
            Save();

        EnsureOutputDirs();
        return Current;
    }

    /// <summary>
    /// Upgrades an older settings file in place. Returns true if anything changed
    /// (so the caller re-saves). Migration is best-effort — it derives the new
    /// <see cref="CaptureProfile"/> from the legacy video CaptureSource + GameOnly
    /// audio fields so existing users keep a sensible behaviour.
    /// </summary>
    internal static bool MigrateIfNeeded(AppSettings s)
    {
        if (s.SchemaVersion >= CurrentSchemaVersion) return false;
        bool changed = false;

        // v0 → v1: legacy Video.CaptureSource + Audio.SystemCaptureMode/GameProcessName
        // become a unified CaptureProfile.
        if (s.SchemaVersion < 1)
        {
            if (s.Audio.SystemCaptureMode == AudioCaptureMode.GameOnly)
            {
                s.Capture.CoupleAudio = true;
                if (!string.IsNullOrEmpty(s.Audio.GameProcessName))
                {
                    s.Capture.Mode = CaptureMode.Window;
                    s.Capture.TargetProcessName = s.Audio.GameProcessName;
                }
                else
                {
                    s.Capture.Mode = CaptureMode.Auto;
                }
            }
            else
            {
                s.Capture.CoupleAudio = false;
                s.Capture.Mode = s.Video.CaptureSource == CaptureSource.ActiveWindow
                    ? CaptureMode.Auto
                    : CaptureMode.Monitor;
            }
            s.SchemaVersion = 1;
            changed = true;
            Logger.Info($"Settings migrated to schema v1: Capture.Mode={s.Capture.Mode}, CoupleAudio={s.Capture.CoupleAudio}, TargetProcess='{s.Capture.TargetProcessName ?? "(null)"}'");
        }

        // v1 → v2: widget-overlay layout. Ensure the five built-in widgets exist so
        // the host has a layout to place; just re-stamp the version otherwise.
        if (s.SchemaVersion < 2)
        {
            if (s.Widgets == null || s.Widgets.Widgets == null || s.Widgets.Widgets.Count == 0)
                s.Widgets = new WidgetSettings();
            s.SchemaVersion = 2;
            changed = true;
            Logger.Info($"Settings migrated to schema v2: {s.Widgets.Widgets.Count} widgets in layout");
        }

        // v2 → v3: crosshair overlay. Existing files carry a saved Hotkeys dictionary,
        // so newly added default bindings (Ctrl+Alt+C) would never reach them — merge
        // in any missing defaults. Same for the new crosshair widget in the layout.
        if (s.SchemaVersion < 3)
        {
            foreach (var kv in new AppSettings().Hotkeys)
            {
                if (!s.Hotkeys.ContainsKey(kv.Key))
                {
                    s.Hotkeys[kv.Key] = kv.Value;
                    Logger.Info($"Migration v3: added missing default hotkey '{kv.Key}'");
                }
            }
            s.Widgets.GetOrAdd(WidgetId.Crosshair);
            s.SchemaVersion = 3;
            changed = true;
            Logger.Info("Settings migrated to schema v3: crosshair hotkey + widget ensured");
        }

        // v3 → v4: instant-GIF hotkey (F8). Merge any still-missing default bindings
        // into an existing file so the new SaveGif hotkey reaches current users.
        if (s.SchemaVersion < 4)
        {
            foreach (var kv in new AppSettings().Hotkeys)
            {
                if (!s.Hotkeys.ContainsKey(kv.Key))
                {
                    s.Hotkeys[kv.Key] = kv.Value;
                    Logger.Info($"Migration v4: added missing default hotkey '{kv.Key}'");
                }
            }
            s.SchemaVersion = 4;
            changed = true;
            Logger.Info("Settings migrated to schema v4: GIF hotkey ensured");
        }

        // v4 → v5: Streaming widget (software stream deck for OBS). Ensure the
        // section exists with an empty grid and the widget appears in the layout.
        if (s.SchemaVersion < 5)
        {
            s.Streaming ??= new StreamingSettings();
            s.Widgets.GetOrAdd(WidgetId.Streaming);
            s.SchemaVersion = 5;
            changed = true;
            Logger.Info("Settings migrated to schema v5: streaming section + widget ensured");
        }

        // v5 → v6: OBS mixer + go-live preflight widgets.
        if (s.SchemaVersion < 6)
        {
            s.Streaming.Preflight ??= new PreflightSettings();
            s.Widgets.GetOrAdd(WidgetId.Mixer);
            s.Widgets.GetOrAdd(WidgetId.Preflight);
            s.SchemaVersion = 6;
            changed = true;
            Logger.Info("Settings migrated to schema v6: mixer + preflight widgets ensured");
        }

        return changed;
    }

    public void Save()
    {
        // A failed save must never take the app down (it is called from startup,
        // hotkeys and every settings widget). Log it and keep running with the
        // in-memory settings instead.
        try
        {
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch (Exception ex)
        {
            Logger.Error("Settings save failed — continuing with in-memory settings", ex);
        }
        SettingsChanged?.Invoke(this, Current);
    }

    public void Update(Action<AppSettings> mutator)
    {
        mutator(Current);
        Save();
    }

    private void EnsureOutputDirs()
    {
        TryCreate(ExpandPath(Current.Output.ClipsFolder));
        TryCreate(ExpandPath(Current.Output.ScreenshotsFolder));
        TryCreate(ExpandPath(Current.Output.BufferFolder));
    }

    public static string ExpandPath(string path)
        => Environment.ExpandEnvironmentVariables(path);

    private static void TryCreate(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch { /* ignore */ }
    }
}
