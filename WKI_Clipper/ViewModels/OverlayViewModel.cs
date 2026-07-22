using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WKI_Clipper.Models;
using WKI_Clipper.Services;

namespace WKI_Clipper.ViewModels;

public partial class OverlayViewModel : ObservableObject
{
    private readonly AppHost _host;

    [ObservableProperty] private string _currentTab = "Capture";
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private bool _isBufferRunning;
    [ObservableProperty] private string _bufferStatusText = L.T("Buffer aus", "Buffer off");
    [ObservableProperty] private string _recordingStatusText = L.T("Bereit", "Ready");
    [ObservableProperty] private string _lastEvent = "";
    [ObservableProperty] private string _audioStatusText = "";

    public AppSettings Settings => _host.Settings.Current;

    public ObservableCollection<ClipMetadata> Clips { get; } = new();
    public ObservableCollection<string> Microphones { get; } = new();
    public ObservableCollection<string> RenderDevices { get; } = new();
    public ObservableCollection<ResolutionPreset> Resolutions { get; } =
        new(Enum.GetValues<ResolutionPreset>());
    public ObservableCollection<string> Codecs { get; } = new() { "h264_amf", "hevc_amf", "libx264" };

    public OverlayViewModel(AppHost host)
    {
        _host = host;

        _host.ManualRecording.RecordingStarted += (_, p) =>
        {
            IsRecording = true;
            RecordingStatusText = L.T("Aufnahme läuft → ", "Recording → ") + Path.GetFileName(p);
            LastEvent = L.T($"Aufnahme gestartet: {Path.GetFileName(p)}", $"Recording started: {Path.GetFileName(p)}");
        };
        _host.ManualRecording.RecordingStopped += (_, result) =>
        {
            IsRecording = false;
            if (result.Success)
            {
                RecordingStatusText = L.T("Bereit", "Ready");
                LastEvent = L.T($"Aufnahme gespeichert: {Path.GetFileName(result.Path)}", $"Recording saved: {Path.GetFileName(result.Path)}");
                ReloadClips();
            }
            else
            {
                RecordingStatusText = L.T("Fehler: ", "Error: ") + (result.Error ?? L.T("Aufnahme fehlgeschlagen", "Recording failed"));
                LastEvent = L.T("Aufnahme fehlgeschlagen", "Recording failed");
            }
        };

        _host.ReplayBuffer.BufferStateChanged += (_, running) =>
        {
            IsBufferRunning = running;
            UpdateAudioStatus();
            BufferStatusText = running
                ? L.T($"Buffer aktiv ({Settings.ReplayBuffer.DurationSeconds} s)", $"Buffer active ({Settings.ReplayBuffer.DurationSeconds} s)")
                : L.T("Buffer aus", "Buffer off");
        };
        _host.ReplayBuffer.ReplaySaved += (_, p) =>
        {
            LastEvent = L.T($"Clip gespeichert: {Path.GetFileName(p)}", $"Clip saved: {Path.GetFileName(p)}");
            ReloadClips();
        };
        _host.ReplayBuffer.BufferError += (_, msg) =>
        {
            BufferStatusText = L.T("Fehler: ", "Error: ") + msg;
        };

        _host.Screenshots.ScreenshotSaved += (_, p) =>
        {
            LastEvent = $"Screenshot: {Path.GetFileName(p)}";
            ReloadClips();
        };

        ReloadDevices();
        ReloadClips();
        UpdateAudioStatus();
    }

    public void UpdateAudioStatus()
    {
        var a = Settings.Audio;
        var parts = new System.Collections.Generic.List<string>();
        if (a.RecordMicrophone) parts.Add("Mic");
        if (a.RecordSystemSound) parts.Add("System");
        AudioStatusText = parts.Count == 0 ? L.T("Audio: aus", "Audio: off") : "Audio: " + string.Join(" + ", parts);
    }

    [RelayCommand]
    private void SelectTab(string tab) => CurrentTab = tab;

    [RelayCommand]
    private async Task ToggleRecording() => await _host.ManualRecording.ToggleAsync();

    [RelayCommand]
    private async Task ToggleBuffer() => await _host.ReplayBuffer.ToggleAsync();

    [RelayCommand]
    private async Task SaveReplay() => await _host.ReplayBuffer.SaveLastAsync();

    [RelayCommand]
    private async Task TakeScreenshot() => await _host.Screenshots.CaptureActiveWindowAsync();

    [RelayCommand]
    private void OpenClipsFolder()
    {
        var dir = SettingsService.ExpandPath(Settings.Output.ClipsFolder);
        try { Process.Start("explorer.exe", dir); } catch { }
    }

    [RelayCommand]
    private void OpenClip(ClipMetadata? clip)
    {
        if (clip is null || !File.Exists(clip.FilePath)) return;
        try { Process.Start(new ProcessStartInfo(clip.FilePath) { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private void SaveSettings()
    {
        _host.Settings.Save();
        _host.Hotkeys.RegisterAll();
        LastEvent = L.T("Settings gespeichert", "Settings saved");
    }

    [RelayCommand]
    private void ReloadDevices()
    {
        Microphones.Clear();
        RenderDevices.Clear();

        Microphones.Add("default");
        foreach (var d in _host.AudioDevices.ListMicrophones())
            Microphones.Add(d.Name);

        RenderDevices.Add("default");
        foreach (var d in _host.AudioDevices.ListRenderDevices())
            RenderDevices.Add(d.Name);
    }

    [RelayCommand]
    private void ReloadClips()
    {
        Clips.Clear();
        var dir = SettingsService.ExpandPath(Settings.Output.ClipsFolder);
        if (!Directory.Exists(dir)) return;

        var entries = new DirectoryInfo(dir).EnumerateFiles("*.mp4")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(50)
            .Select(f => new ClipMetadata
            {
                FilePath = f.FullName,
                FileName = f.Name,
                CreatedAt = f.LastWriteTime,
                FileSizeBytes = f.Length,
                Kind = f.Name.StartsWith("Rec_") ? ClipKind.ManualRecording : ClipKind.Replay
            });

        foreach (var e in entries) Clips.Add(e);
    }
}
