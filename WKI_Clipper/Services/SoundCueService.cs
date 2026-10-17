using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>Where a cue's sound is actually rendered. Seam for tests - nothing here may block or throw.</summary>
internal interface ISoundOutput : IDisposable
{
    /// <summary>Plays one WAV to completion on its own player. The returned task never faults for playback problems.</summary>
    Task PlayAsync(byte[] wav, float volume, string? deviceId);
}

/// <summary>
/// Feedback sounds for the actions that otherwise leave no trace while a game is in full screen
/// (clip saved, screenshot, recording start/stop, error). Fire-and-forget: <see cref="Play"/>
/// returns at once, never throws and lets cues overlap.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SoundCueService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly ISoundOutput _output;
    private readonly Action<Action> _schedule;
    private readonly SoundCueGate _gate = new();
    private readonly ConcurrentDictionary<string, CustomEntry> _custom = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _failuresLogged = new();
    private volatile bool _disposed;

    private sealed record CustomEntry(DateTime StampUtc, long Length, byte[]? Bytes);

    public SoundCueService(SettingsService settings) : this(settings, new WasapiSoundOutput(), null) { }

    // Test seam: a fake output, and a scheduler that runs the work inline instead of on the thread pool.
    internal SoundCueService(SettingsService settings, ISoundOutput output, Action<Action>? schedule)
    {
        _settings = settings;
        _output = output;
        _schedule = schedule ?? (work => _ = Task.Run(work));
    }

    /// <summary>Plays a cue if the switches and the rate limit allow it.</summary>
    public void Play(SoundCue cue) => Dispatch(cue, preview: false);

    /// <summary>The settings page's "Test" button: plays regardless of the switches and the rate limit.</summary>
    public void Preview(SoundCue cue) => Dispatch(cue, preview: true);

    private void Dispatch(SoundCue cue, bool preview)
    {
        try
        {
            if (_disposed) return;
            var s = _settings.Current.Sounds;
            if (s is null) return;
            if (!preview && !_gate.TryAcquire(s, cue, DateTime.UtcNow)) return;

            float volume = SoundCuePolicy.EffectiveVolume(s.Volume);
            string? device = s.DeviceId;
            _schedule(() => Run(cue, volume, device));
        }
        catch (Exception ex)
        {
            LogOnce("dispatch " + ex.GetType().Name, "SoundCues: could not start " + cue + ": " + ex.Message);
        }
    }

    // Off the caller's thread: resolving an own file touches the disk, opening the device takes tens of ms.
    private void Run(SoundCue cue, float volume, string? device)
    {
        try
        {
            if (_disposed) return;
            var bytes = ResolveBytes(cue);
            if (bytes is null) return;
            _output.PlayAsync(bytes, volume, device).ContinueWith(
                t => LogOnce("play " + t.Exception?.GetBaseException().GetType().Name,
                             "SoundCues: playback failed: " + t.Exception?.GetBaseException().Message),
                TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            LogOnce("run " + ex.GetType().Name, "SoundCues: playback failed: " + ex.Message);
        }
    }

    /// <summary>The WAV a cue plays right now: the user's own file when usable, otherwise the built-in sound.</summary>
    internal byte[]? ResolveBytes(SoundCue cue)
    {
        var s = _settings.Current.Sounds;
        if (s is not null)
        {
            var path = SoundCuePolicy.PickCustomPath(s, cue, p => LoadCustom(p) is not null);
            if (path is not null && LoadCustom(path) is { } custom) return custom;
        }
        return SoundCueResources.Load(cue);
    }

    /// <summary>
    /// Bytes of a user file, cached until the file changes. An unusable file is remembered the same
    /// way, so it is logged once instead of on every press.
    /// </summary>
    private byte[]? LoadCustom(string path)
    {
        long length = -1;
        DateTime stamp = DateTime.MinValue;
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists) { length = fi.Length; stamp = fi.LastWriteTimeUtc; }
        }
        catch { /* treated as missing */ }

        if (_custom.TryGetValue(path, out var cached) && cached.Length == length && cached.StampUtc == stamp)
            return cached.Bytes;

        var bytes = SoundWav.TryReadFile(path, out var problem);
        _custom[path] = new CustomEntry(stamp, length, bytes);
        if (problem is not null)
            Logger.Warn($"SoundCues: custom file '{path}' ignored ({problem}) - using the built-in sound");
        return bytes;
    }

    // A dead sound card would otherwise write a log line per F9 press.
    private void LogOnce(string key, string message)
    {
        if (_failuresLogged.TryAdd(key, 0)) Logger.Warn(message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _output.Dispose(); } catch { }
    }
}

/// <summary>
/// WASAPI shared-mode playback, one short-lived player per cue so cues can overlap. Each player is
/// released as soon as its sound is done.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WasapiSoundOutput : ISoundOutput
{
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, byte> _failuresLogged = new();

    public async Task PlayAsync(byte[] wav, float volume, string? deviceId)
    {
        MMDevice? device = null;
        WaveFileReader? reader = null;
        WasapiOut? player = null;
        try
        {
            if (_cts.IsCancellationRequested) return;
            device = OpenDevice(deviceId);
            reader = new WaveFileReader(new MemoryStream(wav, writable: false));

            ISampleProvider chain = reader.ToSampleProvider();
            // Same shape as the music player's proven path: stereo float; the output resamples to the mix format.
            if (chain.WaveFormat.Channels == 1) chain = new MonoToStereoSampleProvider(chain);
            chain = new VolumeSampleProvider(chain) { Volume = volume };

            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            player = new WasapiOut(device, AudioClientShareMode.Shared, true, 60);
            player.PlaybackStopped += (_, _) => stopped.TrySetResult();
            player.Init(chain);
            player.Play();

            // Normally PlaybackStopped ends the wait; the timeout covers a device that vanished mid-sound.
            var limit = reader.TotalTime + TimeSpan.FromSeconds(3);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            await Task.WhenAny(stopped.Task, Task.Delay(limit, wait.Token)).ConfigureAwait(false);
            wait.Cancel();   // releases the timer of the losing Delay
        }
        catch (Exception ex)
        {
            if (_failuresLogged.TryAdd(ex.GetType().Name + ex.Message, 0))
                Logger.Warn("SoundCues: playback failed: " + ex.Message);
        }
        finally
        {
            // Stop/Dispose happen here on the pool thread, never inside PlaybackStopped (that runs on
            // the player's own thread, which Stop would then wait for - a self-join).
            try { player?.Stop(); } catch { }
            try { player?.Dispose(); } catch { }
            try { reader?.Dispose(); } catch { }
            try { device?.Dispose(); } catch { }
        }
    }

    /// <summary>The chosen render device, or the Windows default (multimedia role) when none is set or it is gone.</summary>
    private static MMDevice OpenDevice(string? wanted)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            var all = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
            int index = SoundCuePolicy.PickDeviceIndex(all.Select(d => (d.ID, d.FriendlyName)).ToList(), wanted);
            if (index >= 0)
            {
                var chosen = all[index];
                foreach (var other in all) if (!ReferenceEquals(other, chosen)) other.Dispose();
                return chosen;
            }
            foreach (var d in all) d.Dispose();
        }
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
    }
}
