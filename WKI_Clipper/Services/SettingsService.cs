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
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Current settings schema version — bump when migrating.</summary>
    public const int CurrentSchemaVersion = 1;

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
    private static bool MigrateIfNeeded(AppSettings s)
    {
        if (s.SchemaVersion >= CurrentSchemaVersion) return false;

        // v0 → v1: legacy Video.CaptureSource + Audio.SystemCaptureMode/GameProcessName
        // become a unified CaptureProfile.
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

        s.SchemaVersion = CurrentSchemaVersion;
        Logger.Info($"Settings migrated to schema v{CurrentSchemaVersion}: Capture.Mode={s.Capture.Mode}, CoupleAudio={s.Capture.CoupleAudio}, TargetProcess='{s.Capture.TargetProcessName ?? "(null)"}'");
        return true;
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(Current, JsonOptions);
        File.WriteAllText(SettingsFilePath, json);
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
