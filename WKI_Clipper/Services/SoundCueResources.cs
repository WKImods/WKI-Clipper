using System;
using System.IO;
using NAudio.Wave;

namespace WKI_Clipper.Services;

/// <summary>
/// The built-in cue sounds, embedded in the executable (single-file publish: no loose files,
/// nothing for the installer to place). See Assets\Sounds\LICENSE.md for their origin (CC0).
/// </summary>
public static class SoundCueResources
{
    /// <summary>Manifest name prefix, set by LogicalName in the csproj.</summary>
    public const string Prefix = "WKI_Clipper.Sounds.";

    private static string? FileName(SoundCue cue) => cue switch
    {
        SoundCue.ClipSaved => "clip_saved.wav",
        SoundCue.Screenshot => "screenshot.wav",
        SoundCue.RecordingStarted => "recording_started.wav",
        SoundCue.RecordingStopped => "recording_stopped.wav",
        SoundCue.Error => "error.wav",
        _ => null
    };

    public static string ResourceName(SoundCue cue) => Prefix + (FileName(cue) ?? cue.ToString());

    /// <summary>The WAV bytes of a built-in cue, or null if the resource is missing.</summary>
    public static byte[]? Load(SoundCue cue)
    {
        if (FileName(cue) is null) return null;
        using var stream = typeof(SoundCueResources).Assembly.GetManifestResourceStream(ResourceName(cue));
        if (stream is null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}

/// <summary>Validation for WAV data that is about to be played (user-chosen files in particular).</summary>
public static class SoundWav
{
    /// <summary>A feedback sound is a blip, not a song - longer files are refused.</summary>
    public const int MaxSeconds = 30;

    // A cue is a blip; anything bigger is a mistake and would sit in memory for nothing.
    public const long MaxFileBytes = 32L * 1024 * 1024;

    /// <summary>
    /// Reads a user-chosen file and checks it is playable. Null with <paramref name="problem"/> (English, for
    /// the log) when it is missing, too large, unreadable or not a usable WAV.
    /// </summary>
    public static byte[]? TryReadFile(string path, out string? problem)
    {
        problem = null;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) { problem = "file not found"; return null; }
            if (fi.Length > MaxFileBytes) { problem = "file is too large"; return null; }
            var bytes = File.ReadAllBytes(path);
            if (!IsPlayable(bytes)) { problem = $"not a PCM/float WAV of up to {MaxSeconds} s"; return null; }
            return bytes;
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            return null;
        }
    }

    /// <summary>True when the bytes are a WAV the player can decode: PCM or float, with samples, within the limit.</summary>
    public static bool IsPlayable(byte[] bytes)
    {
        if (bytes is null || bytes.Length < 44) return false;
        try
        {
            using var reader = new WaveFileReader(new MemoryStream(bytes, writable: false));
            var length = reader.TotalTime;
            if (length <= TimeSpan.Zero || length > TimeSpan.FromSeconds(MaxSeconds)) return false;
            // Throws for encodings the sample pipeline cannot convert (ADPCM, MP3-in-WAV, ...).
            _ = reader.ToSampleProvider();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
