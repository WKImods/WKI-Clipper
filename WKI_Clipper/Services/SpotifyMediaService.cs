using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using Windows.Media;
using Windows.Media.Control;

namespace WKI_Clipper.Services;

/// <summary>What the Spotify widget shows. Immutable — replaced as a whole on every change.</summary>
public sealed record SpotifySnapshot(
    bool HasSession,
    string Title,
    string Artist,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset PositionUpdatedAt,
    bool CanSeek,
    bool? Shuffle,
    MediaPlaybackAutoRepeatMode? Repeat,
    byte[]? Cover)
{
    public static readonly SpotifySnapshot Empty = new(false, "", "", false, TimeSpan.Zero, TimeSpan.Zero,
        DateTimeOffset.MinValue, false, null, null, null);

    /// <summary>Position now, interpolated — Windows only reports a snapshot on play/pause/seek.</summary>
    public TimeSpan PositionAt(DateTimeOffset now)
    {
        var p = Position;
        if (IsPlaying && PositionUpdatedAt > DateTimeOffset.MinValue)
        {
            var elapsed = now - PositionUpdatedAt;
            if (elapsed > TimeSpan.Zero) p += elapsed;
        }
        if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
        return p < TimeSpan.Zero ? TimeSpan.Zero : p;
    }
}

/// <summary>
/// Controls the user's own Spotify app through the Windows media session (the same
/// channel the media keys and the volume flyout use) — no Spotify account access, no
/// developer registration. Per-app volume goes through the Windows mixer sessions.
///
/// Only sessions whose app id names Spotify are used, so the Spotify web player inside
/// our own widget (which also registers a media session) is never mistaken for the app.
/// </summary>
public sealed class SpotifyMediaService : IDisposable
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private Task? _startTask;
    private string _coverKey = "";
    private byte[]? _cover;
    private bool _disposed;

    public SpotifySnapshot Current { get; private set; } = SpotifySnapshot.Empty;

    /// <summary>Raised (on a worker thread) whenever <see cref="Current"/> was replaced.</summary>
    public event Action? StateChanged;

    public Task StartAsync() => _startTask ??= StartCoreAsync();

    private async Task StartCoreAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (_, _) => _ = AttachAsync();
            await AttachAsync();
        }
        catch (Exception ex)
        {
            Logger.Warn("Spotify: media session manager unavailable: " + ex.Message);
        }
    }

    internal static bool IsSpotifyAppId(string? aumid)
        => !string.IsNullOrEmpty(aumid) && aumid.Contains("spotify", StringComparison.OrdinalIgnoreCase);

    private async Task AttachAsync()
    {
        if (_manager is null || _disposed) return;
        GlobalSystemMediaTransportControlsSession? found = null;
        try { found = _manager.GetSessions().FirstOrDefault(s => IsSpotifyAppId(s.SourceAppUserModelId)); }
        catch (Exception ex) { Logger.Warn("Spotify: session list failed: " + ex.Message); }

        if (!ReferenceEquals(found, _session))
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnSessionChanged;
                _session.PlaybackInfoChanged -= OnSessionChanged;
                _session.TimelinePropertiesChanged -= OnSessionChanged;
            }
            _session = found;
            if (_session != null)
            {
                _session.MediaPropertiesChanged += OnSessionChanged;
                _session.PlaybackInfoChanged += OnSessionChanged;
                _session.TimelinePropertiesChanged += OnSessionChanged;
                Logger.Info($"Spotify: media session attached ({_session.SourceAppUserModelId}).");
            }
        }
        await RefreshAsync();
    }

    private void OnSessionChanged<T>(GlobalSystemMediaTransportControlsSession s, T args) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_disposed) return;
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var session = _session;
            if (session is null)
            {
                Current = SpotifySnapshot.Empty;
                _coverKey = "";
                _cover = null;
            }
            else
            {
                var props = await session.TryGetMediaPropertiesAsync();
                var playback = session.GetPlaybackInfo();
                var timeline = session.GetTimelineProperties();

                string title = props?.Title ?? "";
                string artist = props?.Artist ?? "";
                // The thumbnail is only re-read when the track changes.
                string key = title + "\u0001" + artist + "\u0001" + (props?.AlbumTitle ?? "");
                if (key != _coverKey)
                {
                    _coverKey = key;
                    _cover = await ReadCoverAsync(props).ConfigureAwait(false);
                }

                Current = new SpotifySnapshot(
                    HasSession: true,
                    Title: title,
                    Artist: artist,
                    IsPlaying: playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    Position: timeline?.Position ?? TimeSpan.Zero,
                    Duration: timeline is null ? TimeSpan.Zero : timeline.EndTime - timeline.StartTime,
                    PositionUpdatedAt: timeline?.LastUpdatedTime ?? DateTimeOffset.MinValue,
                    CanSeek: playback?.Controls.IsPlaybackPositionEnabled == true,
                    Shuffle: playback?.IsShuffleActive,
                    Repeat: playback?.AutoRepeatMode,
                    Cover: _cover);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Spotify: refresh failed: " + ex.Message);
        }
        finally
        {
            _refreshGate.Release();
        }
        StateChanged?.Invoke();
    }

    private static async Task<byte[]?> ReadCoverAsync(GlobalSystemMediaTransportControlsSessionMediaProperties? props)
    {
        if (props?.Thumbnail is null) return null;
        try
        {
            using var ras = await props.Thumbnail.OpenReadAsync();
            using var stream = ras.AsStreamForRead();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms).ConfigureAwait(false);
            return ms.Length > 0 ? ms.ToArray() : null;
        }
        catch { return null; }
    }

    // ---- transport ----

    public Task TogglePlayPauseAsync() => RunAsync(s => s.TryTogglePlayPauseAsync().AsTask());
    public Task NextAsync() => RunAsync(s => s.TrySkipNextAsync().AsTask());
    public Task PreviousAsync() => RunAsync(s => s.TrySkipPreviousAsync().AsTask());
    public Task SeekAsync(TimeSpan position) => RunAsync(s => s.TryChangePlaybackPositionAsync(position.Ticks).AsTask());
    public Task SetShuffleAsync(bool on) => RunAsync(s => s.TryChangeShuffleActiveAsync(on).AsTask());

    /// <summary>Off → whole list → single track → off, like the button in Spotify.</summary>
    public Task CycleRepeatAsync()
    {
        var next = Current.Repeat switch
        {
            MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.Track,
            MediaPlaybackAutoRepeatMode.Track => MediaPlaybackAutoRepeatMode.None,
            _ => MediaPlaybackAutoRepeatMode.List
        };
        return RunAsync(s => s.TryChangeAutoRepeatModeAsync(next).AsTask());
    }

    private async Task RunAsync(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> command)
    {
        await StartAsync().ConfigureAwait(false);
        var session = _session;
        if (session is null) return;
        try
        {
            if (!await command(session).ConfigureAwait(false))
                Logger.Info("Spotify: command was not accepted by the app.");
        }
        catch (Exception ex) { Logger.Warn("Spotify: command failed: " + ex.Message); }
    }

    // ---- app state ----

    public static bool IsAppRunning()
    {
        var procs = Process.GetProcessesByName("Spotify");
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }

    /// <summary>True when something handles spotify: links — i.e. the app is installed.</summary>
    public static async Task<bool> IsInstalledAsync()
    {
        try
        {
            var status = await Windows.System.Launcher.QueryUriSupportAsync(
                new Uri("spotify:"), Windows.System.LaunchQuerySupportType.Uri);
            return status == Windows.System.LaunchQuerySupportStatus.Available;
        }
        catch { return false; }
    }

    public static async Task<bool> LaunchAppAsync()
    {
        try { return await Windows.System.Launcher.LaunchUriAsync(new Uri("spotify:")); }
        catch (Exception ex)
        {
            Logger.Warn("Spotify: launch failed: " + ex.Message);
            return false;
        }
    }

    // ---- per-app volume (Windows mixer) ----

    /// <summary>
    /// Spotify's mixer volume (0…1), or null when it has no audio session (not playing
    /// since start). Spotify may output to any device, so every active output is checked.
    /// </summary>
    public static float? GetAppVolume()
    {
        float? result = null;
        ForEachSpotifySession(v => { result ??= v.Volume; });
        return result;
    }

    public static void SetAppVolume(float volume)
    {
        volume = Math.Clamp(volume, 0f, 1f);
        ForEachSpotifySession(v => v.Volume = volume);
    }

    private static void ForEachSpotifySession(Action<SimpleAudioVolume> action)
    {
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName("Spotify"))
        {
            pids.Add((uint)p.Id);
            p.Dispose();
        }
        if (pids.Count == 0) return;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        if (pids.Contains(session.GetProcessID)) action(session.SimpleAudioVolume);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Spotify: mixer access failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnSessionChanged;
            _session.PlaybackInfoChanged -= OnSessionChanged;
            _session.TimelinePropertiesChanged -= OnSessionChanged;
            _session = null;
        }
        _manager = null;
    }
}
