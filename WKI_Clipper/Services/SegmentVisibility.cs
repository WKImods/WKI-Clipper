using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WKI_Clipper.Services;

public sealed record SegmentFileState(string Name, DateTime LastWriteUtc, bool Hidden);

/// <summary>
/// Keeps the disk ring out of sight: the buffer folder and the segment files carry FILE_ATTRIBUTE_HIDDEN.
///
/// Why not simply every file: ffmpeg opens a segment with CREATE_ALWAYS semantics (O_CREAT|O_TRUNC), and Windows refuses
/// that for an existing HIDDEN file ("Permission denied" - measured with ffmpeg 8.1: the segment muxer stops at the first
/// wrapped name). The ring reuses its names every <c>wrap</c> segments, so the files ffmpeg will open next must be visible
/// again in time: the open one and the next <see cref="ProtectAhead"/> ring slots stay (or become) visible, everything else
/// of the running generation is hidden. Older generations are never written again in this process and are always hidden;
/// before a run starts, leftovers carrying its generation number are made visible.
/// Pure plan + a thin apply step.
/// </summary>
public static class SegmentVisibility
{
    /// <summary>Ring slots ahead of the open one that stay visible: at least 15 s of recording, never the whole ring.</summary>
    public static int ProtectAhead(int segmentSeconds, int wrap)
    {
        int ahead = Math.Max(3, (int)Math.Ceiling(15.0 / Math.Max(1, segmentSeconds)));
        return Math.Max(1, Math.Min(ahead, wrap - 2));
    }

    /// <summary>Which files to hide and which to make visible.</summary>
    public static (List<string> Hide, List<string> Show) Plan(IReadOnlyList<SegmentFileState> files, int runningGeneration,
        int wrap, int protectAhead)
    {
        var hide = new List<string>();
        var show = new List<string>();
        wrap = Math.Max(1, wrap);

        var current = files.Where(f => SegmentName.TryParse(f.Name, out int g, out _) && g == runningGeneration).ToList();
        int open = -1;
        if (current.Count > 0)
        {
            var newest = current.OrderBy(f => f.LastWriteUtc).Last();
            SegmentName.TryParse(newest.Name, out _, out open);
        }
        var protectedSlots = new HashSet<int>();
        int first = open < 0 ? 0 : open;
        for (int k = 0; k <= protectAhead; k++) protectedSlots.Add((first + k) % wrap);

        foreach (var f in files)
        {
            if (!SegmentName.TryParse(f.Name, out int g, out int index)) continue;
            bool keepVisible = g == runningGeneration && protectedSlots.Contains(index % wrap);
            if (keepVisible && f.Hidden) show.Add(f.Name);
            else if (!keepVisible && !f.Hidden) hide.Add(f.Name);
        }
        return (hide, show);
    }

    /// <summary>Applies <see cref="Plan"/> to the folder. Best effort, never throws.</summary>
    public static void Apply(string dir, int runningGeneration, int wrap, int segmentSeconds)
    {
        try
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            var files = new DirectoryInfo(dir).EnumerateFiles("seg_*.mp4")
                .Select(fi => new SegmentFileState(fi.Name, fi.LastWriteTimeUtc, (fi.Attributes & FileAttributes.Hidden) != 0))
                .ToList();
            var (hide, show) = Plan(files, runningGeneration, wrap, ProtectAhead(segmentSeconds, wrap));
            foreach (var name in show) SetHidden(Path.Combine(dir, name), false);
            foreach (var name in hide) SetHidden(Path.Combine(dir, name), true);
        }
        catch { }
    }

    /// <summary>Makes every file of <paramref name="generation"/> visible (ffmpeg is about to (re)create those names).</summary>
    public static void ShowGeneration(string dir, int generation)
    {
        try
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            foreach (var path in Directory.EnumerateFiles(dir, $"seg_{generation}_*.mp4"))
                if (SegmentName.TryParse(Path.GetFileName(path), out int g, out _) && g == generation)
                    SetHidden(path, false);
        }
        catch { }
    }

    private static void SetHidden(string path, bool hidden)
    {
        try
        {
            var a = File.GetAttributes(path);
            var b = hidden ? a | FileAttributes.Hidden : a & ~FileAttributes.Hidden;
            if (a != b) File.SetAttributes(path, b);
        }
        catch { /* deleted meanwhile, or held - the next pass gets it */ }
    }

    /// <summary>
    /// Keeps the hidden attribute of the buffer folder right. Only a folder that is ours may be hidden at all
    /// (<paramref name="ours"/>: the default buffer folder, or one this app created) - hiding a folder the user picked would
    /// hide whatever else they keep there. Ours and holding only buffer files: hidden. Ours with anything else in it
    /// (the user put something there): visible again. Not ours: left alone. Returns true when the folder is hidden afterwards.
    /// </summary>
    public static bool UpdateFolder(string dir, bool ours)
    {
        try
        {
            var di = new DirectoryInfo(dir);
            if (!di.Exists || di.Parent is null) return false;   // never a drive root
            bool hidden = (di.Attributes & FileAttributes.Hidden) != 0;
            if (!ours) return hidden;
            bool onlyOwn = di.EnumerateFileSystemInfos()
                .All(e => e is not DirectoryInfo && BufferFolderRules.IsOwnBufferFile(e.Name));
            if (onlyOwn && !hidden) di.Attributes |= FileAttributes.Hidden;
            else if (!onlyOwn && hidden) di.Attributes &= ~FileAttributes.Hidden;
            return onlyOwn;
        }
        catch { return false; }
    }

    /// <summary>Takes the hidden attribute back (a folder the buffer has moved away from). Best effort.</summary>
    public static void ShowFolder(string dir)
    {
        try
        {
            var di = new DirectoryInfo(dir);
            if (di.Exists && (di.Attributes & FileAttributes.Hidden) != 0) di.Attributes &= ~FileAttributes.Hidden;
        }
        catch { }
    }
}
