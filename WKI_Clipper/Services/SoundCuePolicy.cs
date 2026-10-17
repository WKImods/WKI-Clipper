using System;
using System.Collections.Generic;
using System.IO;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>The short feedback sounds the app can play.</summary>
public enum SoundCue
{
    /// <summary>A clip or GIF save was accepted (played when it STARTS, not when the file is done).</summary>
    ClipSaved,
    Screenshot,
    RecordingStarted,
    RecordingStopped,
    /// <summary>A save was refused or failed, or a recording/screenshot failed.</summary>
    Error
}

/// <summary>
/// The decisions behind a cue, kept free of audio and clock so each boundary is unit-tested:
/// which switch governs which cue, the volume, which own file replaces the built-in sound and
/// which output device to use.
/// </summary>
public static class SoundCuePolicy
{
    /// <summary>At most one play per cue within this window - a held key must not machine-gun the sound.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(300);

    public const double DefaultVolume = 0.6;

    /// <summary>Master switch AND the cue's own switch (recording covers start and stop).</summary>
    public static bool IsCueEnabled(SoundSettings s, SoundCue cue)
    {
        if (!s.Enabled) return false;
        return cue switch
        {
            SoundCue.ClipSaved => s.ClipSaved,
            SoundCue.Screenshot => s.Screenshot,
            SoundCue.RecordingStarted or SoundCue.RecordingStopped => s.Recording,
            SoundCue.Error => s.Errors,
            _ => false
        };
    }

    /// <summary>Settings volume as a usable 0..1 gain; a broken value (NaN, infinity) becomes the default.</summary>
    public static float EffectiveVolume(double volume)
    {
        if (double.IsNaN(volume) || double.IsInfinity(volume)) return (float)DefaultVolume;
        return (float)Math.Clamp(volume, 0.0, 1.0);
    }

    /// <summary>
    /// The user's own file for a cue, or null for the built-in sound: no entry, not a .wav, or
    /// <paramref name="isPlayable"/> says the file cannot be used. The probe only runs for
    /// entries that could work at all, so a blank or non-wav entry never touches the disk.
    /// </summary>
    public static string? PickCustomPath(SoundSettings s, SoundCue cue, Func<string, bool> isPlayable)
    {
        if (s.CustomFiles is null) return null;
        if (!s.CustomFiles.TryGetValue(cue.ToString(), out var path) || string.IsNullOrWhiteSpace(path)) return null;
        if (!string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase)) return null;
        return isPlayable(path) ? path : null;
    }

    /// <summary>
    /// Index of the wanted output device (by id first, then friendly name), or -1 for the
    /// Windows default - also when the saved device is no longer plugged in.
    /// </summary>
    public static int PickDeviceIndex(IReadOnlyList<(string Id, string Name)> devices, string? wanted)
    {
        if (string.IsNullOrWhiteSpace(wanted)) return -1;
        for (int i = 0; i < devices.Count; i++)
            if (string.Equals(devices[i].Id, wanted, StringComparison.OrdinalIgnoreCase)) return i;
        for (int i = 0; i < devices.Count; i++)
            if (string.Equals(devices[i].Name, wanted, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}

/// <summary>
/// Decides whether a cue plays right now: switches first, then the per-cue rate limit. A cue that
/// is switched off or rate-limited does not move the window, so a held key still sounds every
/// <see cref="SoundCuePolicy.MinInterval"/> instead of going silent after the first press.
/// </summary>
public sealed class SoundCueGate
{
    private readonly object _lock = new();
    private readonly Dictionary<SoundCue, DateTime> _lastPlayed = new();

    public bool TryAcquire(SoundSettings settings, SoundCue cue, DateTime nowUtc)
    {
        if (!SoundCuePolicy.IsCueEnabled(settings, cue)) return false;
        lock (_lock)
        {
            // A clock that went backwards must not keep the cue muted until it catches up.
            if (_lastPlayed.TryGetValue(cue, out var last)
                && nowUtc >= last && nowUtc - last < SoundCuePolicy.MinInterval)
                return false;
            _lastPlayed[cue] = nowUtc;
            return true;
        }
    }
}
