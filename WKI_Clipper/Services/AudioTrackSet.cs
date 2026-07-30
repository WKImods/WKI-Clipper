using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>
/// Wraps one or two <see cref="AudioPipeService"/> instances behind a single-track-
/// shaped API so the recording/buffer code barely changes:
///  - Normal: ONE mixed pipe (system + mic together) → one audio track.
///  - SeparateMicTrack (only when BOTH sources are present): TWO pipes — system-only
///    and mic-only — which ffmpeg maps as track 1 (system) and track 2 (mic).
///
/// The individual pipes are used exactly as before; both are paced against their own
/// wall clock, which is anchored to real time, so the two tracks stay in sync with
/// each other (and with the video) without ever drifting. The mixing/pacing writer
/// loop itself is untouched — the split happens purely via each pipe's source mask.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AudioTrackSet : IDisposable
{
    private readonly AudioPipeService? _mixed;
    private readonly AudioPipeService? _sys;   // separate mode: system track
    private readonly AudioPipeService? _mic;   // separate mode: mic track
    private readonly List<AudioPipeService> _pipes = new();
    private readonly List<AudioPipeService> _started = new();

    /// <summary>True when the mic is on its own second track.</summary>
    public bool Separate { get; }

    public AudioTrackSet(AppSettings settings, SystemAudioMode sysMode, int? gamePid = null)
    {
        bool wantMic = settings.Audio.RecordMicrophone && !string.IsNullOrWhiteSpace(settings.Audio.MicDeviceId);
        bool wantSys = sysMode != SystemAudioMode.None;
        // Separate tracks only make sense when there are two sources to separate.
        Separate = settings.Audio.SeparateMicTrack && wantMic && wantSys;

        if (Separate)
        {
            _sys = new AudioPipeService(settings, sysMode, gamePid, AudioSourceMask.SystemOnly);
            _mic = new AudioPipeService(settings, SystemAudioMode.None, null, AudioSourceMask.MicOnly);
            _pipes.Add(_sys);   // order = ffmpeg map order: track 1 then track 2
            _pipes.Add(_mic);
        }
        else
        {
            _mixed = new AudioPipeService(settings, sysMode, gamePid, AudioSourceMask.Both);
            _pipes.Add(_mixed);
        }
    }

    public bool HasAnyAudio() => _pipes.Any(p => p.HasAnyAudio());

    public string? LastError => _pipes.Select(p => p.LastError).FirstOrDefault(e => !string.IsNullOrEmpty(e));

    /// <summary>
    /// Starts every non-empty pipe. Returns true if at least one is running. If one of
    /// two separate pipes fails to start, the recording gracefully degrades to the
    /// track(s) that did start.
    /// </summary>
    public bool Start()
    {
        _started.Clear();
        foreach (var p in _pipes)
        {
            if (!p.HasAnyAudio()) continue;
            if (p.Start()) _started.Add(p);
            else Logger.Warn("AudioTrackSet: a pipe failed to start — " + (p.LastError ?? "unknown"));
        }
        return _started.Count > 0;
    }

    /// <summary>The ffmpeg -i input fragments for each started track, in map order.</summary>
    public IReadOnlyList<string> PipeArgs => _started.Select(p => p.FFmpegInputArgs).ToList();

    /// <summary>Swap the SYSTEM source in place; the mic track (if any) is never retargeted.</summary>
    public bool SwapSystemSource(SystemAudioMode mode, int? pid)
        => (_sys ?? _mixed)?.SwapSystemSource(mode, pid) == true;

    public void Dispose()
    {
        foreach (var p in _pipes) { try { p.Dispose(); } catch { } }
    }
}
