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
            Current = new AppSettings();
            Save();
            return Current;
        }

        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            Current = loaded ?? new AppSettings();
        }
        catch (Exception)
        {
            // Corrupt settings file → back it up, start fresh
            var backup = SettingsFilePath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try { File.Move(SettingsFilePath, backup); } catch { }
            Current = new AppSettings();
            Save();
        }

        EnsureOutputDirs();
        return Current;
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
