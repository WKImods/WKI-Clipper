using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WKI_Clipper.Services;

/// <summary>
/// Track order logic for the music widget — pure so next/prev/shuffle/repeat behaviour
/// is unit-tested instead of being poked at through the UI.
/// </summary>
public sealed class Playlist
{
    private readonly List<int> _order = new();   // indices into Tracks, in play order
    private int _pos = -1;
    private readonly Random _rng;

    public IReadOnlyList<string> Tracks { get; private set; } = Array.Empty<string>();
    public bool Shuffle { get; private set; }
    public bool Repeat { get; set; } = true;

    public Playlist(int? seed = null) => _rng = seed is { } s ? new Random(s) : new Random();

    /// <summary>Current track path, or null when the playlist is empty / not started.</summary>
    public string? Current => _pos >= 0 && _pos < _order.Count ? Tracks[_order[_pos]] : null;
    public int CurrentIndex => _pos >= 0 && _pos < _order.Count ? _order[_pos] : -1;

    public void SetTracks(IEnumerable<string> tracks, bool shuffle)
    {
        Tracks = tracks.ToList();
        Shuffle = shuffle;
        Rebuild(keepCurrent: false);
    }

    public void SetShuffle(bool shuffle)
    {
        if (Shuffle == shuffle) return;
        Shuffle = shuffle;
        Rebuild(keepCurrent: true);
    }

    /// <summary>Jump to a specific track (list click).</summary>
    public void JumpTo(int trackIndex)
    {
        int p = _order.IndexOf(trackIndex);
        if (p >= 0) _pos = p;
    }

    /// <summary>Advance; returns null at the end when Repeat is off.</summary>
    public string? Next()
    {
        if (_order.Count == 0) return null;
        if (_pos + 1 < _order.Count) { _pos++; return Current; }
        if (!Repeat) return null;
        // Reshuffle on wrap so a looping shuffle doesn't repeat the same order forever.
        if (Shuffle) Rebuild(keepCurrent: false);
        _pos = 0;
        return Current;
    }

    public string? Prev()
    {
        if (_order.Count == 0) return null;
        if (_pos - 1 >= 0) { _pos--; return Current; }
        _pos = Repeat ? _order.Count - 1 : 0;
        return Current;
    }

    private void Rebuild(bool keepCurrent)
    {
        int currentTrack = keepCurrent ? CurrentIndex : -1;
        _order.Clear();
        _order.AddRange(Enumerable.Range(0, Tracks.Count));
        if (Shuffle)
        {
            for (int i = _order.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (_order[i], _order[j]) = (_order[j], _order[i]);
            }
        }
        _pos = currentTrack >= 0 ? _order.IndexOf(currentTrack) : (_order.Count > 0 ? 0 : -1);
    }

    /// <summary>Audio files in a folder, sorted by name. Missing folder = empty list.</summary>
    public static List<string> ScanFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return new();
            var exts = new[] { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".wma", ".ogg" };
            return Directory.EnumerateFiles(folder)
                .Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => Path.GetFileName(f), StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex) { Logger.Warn("Playlist scan failed: " + ex.Message); return new(); }
    }
}

/// <summary>Turns a file path into artist/title for the now-playing text (no tag library needed).</summary>
public static class TrackName
{
    /// <summary>
    /// Splits "Artist - Title.mp3" on the FIRST " - ". Without that separator the whole
    /// file name becomes the title and the artist stays empty — which is exactly how the
    /// NCS downloads are named.
    /// </summary>
    public static (string Artist, string Title) Parse(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path ?? "") ?? "";
        int sep = name.IndexOf(" - ", StringComparison.Ordinal);
        if (sep <= 0) return ("", name.Trim());
        return (name[..sep].Trim(), name[(sep + 3)..].Trim());
    }

    /// <summary>Fills {artist} / {title} in the user's template; collapses a leading " - " when artist is empty.</summary>
    public static string Format(string template, string path)
    {
        var (artist, title) = Parse(path);
        var s = (template ?? "{artist} - {title}")
            .Replace("{artist}", artist)
            .Replace("{title}", title);
        return s.Trim().TrimStart('-').Trim();
    }
}
