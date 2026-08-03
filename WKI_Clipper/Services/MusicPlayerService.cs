using System;
using System.IO;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Collections.Generic;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>
/// Copies every sample the main output pulls into a buffer for the monitor output, so
/// both ears hear the same file from ONE decoder — two independent readers would drift
/// apart. The main output is the clock; the monitor just follows.
/// </summary>
internal sealed class TeeSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly BufferedWaveProvider _tap;
    private byte[] _bytes = Array.Empty<byte>();

    public TeeSampleProvider(ISampleProvider source, BufferedWaveProvider tap)
    {
        _source = source;
        _tap = tap;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read > 0)
        {
            int bytes = read * 4;
            if (_bytes.Length < bytes) _bytes = new byte[bytes];
            Buffer.BlockCopy(buffer, offset * 4, _bytes, 0, bytes);
            try { _tap.AddSamples(_bytes, 0, bytes); } catch { /* monitor stopped */ }
        }
        return read;
    }
}

/// <summary>
/// Stream music player: plays a folder of tracks to the stream device (VB-Cable) and,
/// optionally, to a second device so the streamer hears it too — each with its own
/// volume. Also writes the current track to a text file for an OBS text source.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MusicPlayerService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly Playlist _playlist = new();

    private AudioFileReader? _reader;
    private WasapiOut? _mainOut;
    private WasapiOut? _monitorOut;
    private BufferedWaveProvider? _monitorBuffer;
    private VolumeSampleProvider? _mainVolume;
    private VolumeSampleProvider? _monitorVolume;
    private bool _stopping;

    public event Action? StateChanged;

    public bool IsPlaying => _mainOut?.PlaybackState == PlaybackState.Playing;
    public string? CurrentTrack => _playlist.Current;
    public IReadOnlyList<string> Tracks => _playlist.Tracks;
    public int CurrentIndex => _playlist.CurrentIndex;
    public bool Shuffle => _playlist.Shuffle;
    public bool Repeat => _playlist.Repeat;

    public MusicPlayerService(SettingsService settings)
    {
        _settings = settings;
        ReloadPlaylist();
    }

    private MusicSettings Cfg => _settings.Current.Music;

    /// <summary>Re-scans the music folder (folder changed, or files added).</summary>
    public void ReloadPlaylist()
    {
        var files = Playlist.ScanFolder(SettingsService.ExpandPath(Cfg.Folder));
        _playlist.SetTracks(files, Cfg.Shuffle);
        _playlist.Repeat = Cfg.Repeat;
        StateChanged?.Invoke();
    }

    public void SetShuffle(bool on) { Cfg.Shuffle = on; _playlist.SetShuffle(on); _settings.Save(); StateChanged?.Invoke(); }
    public void SetRepeat(bool on) { Cfg.Repeat = on; _playlist.Repeat = on; _settings.Save(); StateChanged?.Invoke(); }

    public void PlayIndex(int trackIndex)
    {
        _playlist.JumpTo(trackIndex);
        StartCurrent();
    }

    public void TogglePlayPause()
    {
        if (_mainOut is null) { StartCurrent(); return; }
        if (_mainOut.PlaybackState == PlaybackState.Playing)
        {
            _mainOut.Pause();
            _monitorOut?.Pause();
        }
        else
        {
            _mainOut.Play();
            _monitorOut?.Play();
        }
        StateChanged?.Invoke();
    }

    public void Next() { _playlist.Next(); StartCurrent(); }
    public void Prev() { _playlist.Prev(); StartCurrent(); }

    public void Stop()
    {
        DisposePlayback();
        WriteNowPlaying(null);
        StateChanged?.Invoke();
    }

    /// <summary>Stream-side volume (what listeners hear).</summary>
    public void SetMainVolume(float v)
    {
        Cfg.Volume = Math.Clamp(v, 0f, 1f);
        if (_mainVolume != null) _mainVolume.Volume = Cfg.Volume;
        _settings.Save();
    }

    /// <summary>Monitor volume (what the streamer hears) — independent of the stream level.</summary>
    public void SetMonitorVolume(float v)
    {
        Cfg.MonitorVolume = Math.Clamp(v, 0f, 1f);
        if (_monitorVolume != null) _monitorVolume.Volume = Cfg.MonitorVolume;
        _settings.Save();
    }

    /// <summary>Turns the second (headphone) output on/off; restarts the current track to re-wire.</summary>
    public void SetMonitorEnabled(bool on)
    {
        Cfg.MonitorEnabled = on;
        _settings.Save();
        if (_reader != null) StartCurrent();   // rebuild the graph
        StateChanged?.Invoke();
    }

    private void StartCurrent()
    {
        var track = _playlist.Current;
        DisposePlayback();
        if (track is null || !File.Exists(track)) { WriteNowPlaying(null); StateChanged?.Invoke(); return; }

        try
        {
            _reader = new AudioFileReader(track);
            ISampleProvider chain = _reader;

            if (Cfg.MonitorEnabled)
            {
                // Monitor path: fed by the main pull via the tee. ReadFully = true is
                // CORRECT here — this is a pure playback path, where a short underrun
                // is inaudible. (The capture pipeline deliberately does the opposite.)
                _monitorBuffer = new BufferedWaveProvider(_reader.WaveFormat)
                {
                    BufferDuration = TimeSpan.FromSeconds(2),
                    DiscardOnBufferOverflow = true,
                    ReadFully = true
                };
                chain = new TeeSampleProvider(_reader, _monitorBuffer);
            }

            _mainVolume = new VolumeSampleProvider(chain) { Volume = Cfg.Volume };
            _mainOut = new WasapiOut(FindDevice(Cfg.OutputDeviceId), AudioClientShareMode.Shared, true, 100);
            _mainOut.Init(_mainVolume);
            _mainOut.PlaybackStopped += OnMainStopped;

            if (Cfg.MonitorEnabled && _monitorBuffer != null)
            {
                _monitorVolume = new VolumeSampleProvider(_monitorBuffer.ToSampleProvider()) { Volume = Cfg.MonitorVolume };
                _monitorOut = new WasapiOut(FindDevice(Cfg.MonitorDeviceId), AudioClientShareMode.Shared, true, 100);
                _monitorOut.Init(_monitorVolume);
                _monitorOut.Play();
            }

            _mainOut.Play();
            WriteNowPlaying(track);
            Logger.Info($"Music: playing {Path.GetFileName(track)}"
                        + (Cfg.MonitorEnabled ? " (+ monitor)" : ""));
        }
        catch (Exception ex)
        {
            Logger.Warn("Music playback failed: " + ex.Message);
            DisposePlayback();
        }
        StateChanged?.Invoke();
    }

    private void OnMainStopped(object? sender, StoppedEventArgs e)
    {
        // Only auto-advance on a natural end, not when we tore the graph down ourselves.
        if (_stopping) return;
        if (e.Exception != null) { Logger.Warn("Music output stopped: " + e.Exception.Message); return; }
        if (_playlist.Next() is null) { Stop(); return; }
        StartCurrent();
    }

    /// <summary>Device by id, or the system default when the saved device is gone.</summary>
    private static MMDevice? FindDevice(string? id)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            if (!string.IsNullOrEmpty(id))
            {
                foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    if (d.ID == id) return d;
            }
            return en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch { return null; }
    }

    private void WriteNowPlaying(string? track)
    {
        try
        {
            var path = SettingsService.ExpandPath(Cfg.NowPlayingFile);
            if (string.IsNullOrWhiteSpace(path)) return;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Empty file when stopped so the OBS text source goes blank.
            File.WriteAllText(path, track is null ? "" : TrackName.Format(Cfg.NowPlayingTemplate, track));
        }
        catch (Exception ex) { Logger.Warn("Now-playing write failed: " + ex.Message); }
    }

    private void DisposePlayback()
    {
        _stopping = true;
        try { if (_mainOut != null) { _mainOut.PlaybackStopped -= OnMainStopped; _mainOut.Stop(); _mainOut.Dispose(); } } catch { }
        try { _monitorOut?.Stop(); _monitorOut?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        _mainOut = null; _monitorOut = null; _reader = null;
        _monitorBuffer = null; _mainVolume = null; _monitorVolume = null;
        _stopping = false;
    }

    public void Dispose()
    {
        DisposePlayback();
        WriteNowPlaying(null);
    }
}
