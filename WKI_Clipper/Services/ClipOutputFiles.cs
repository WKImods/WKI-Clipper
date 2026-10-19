using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WKI_Clipper.Services;

/// <summary>What happened to a finished ffmpeg output.</summary>
public enum FinishOutcome
{
    /// <summary>The clip now exists under its real name.</summary>
    Published,
    /// <summary>ffmpeg failed or produced nothing; the partial output was removed.</summary>
    Failed,
    /// <summary>
    /// ffmpeg succeeded but the file could not be given its real name (not even by copying).
    /// The finished clip is still on disk as the .part file - it must NOT be deleted.
    /// </summary>
    RenameFailed,
}

/// <summary>File operations of <see cref="ClipOutputFiles.FinishAsync(string,string,int)"/> - a seam for tests.</summary>
internal sealed class OutputFileOps
{
    public Action<string, string> Move { get; init; } = (from, to) => File.Move(from, to, overwrite: true);
    public Action<string, string> Copy { get; init; } = (from, to) => File.Copy(from, to, overwrite: true);
    public Func<TimeSpan, Task> Delay { get; init; } = Task.Delay;
}

/// <summary>
/// Clips and GIFs are written to "&lt;name&gt;.mp4.part" / "&lt;name&gt;.gif.part" and only get
/// their real name once ffmpeg finished successfully. Writing straight to Clip_*.mp4 let the
/// gallery list a half-written file that was still growing, and a timeout could leave it behind.
/// The gallery filters by extension, so a .part file is invisible to it by construction.
/// </summary>
public static class ClipOutputFiles
{
    public const string PartSuffix = ".part";

    /// <summary>
    /// A .part file this old is a leftover. Deliberately a day, not minutes: when the rename
    /// fails the finished clip is kept as .part, and the user needs time to find it.
    /// </summary>
    public static readonly TimeSpan StalePartAge = TimeSpan.FromHours(24);

    /// <summary>
    /// Waits between rename attempts (~10 s in total): Defender, the search indexer and OneDrive
    /// like to hold a just-closed file for a moment, as IOException or UnauthorizedAccessException.
    /// </summary>
    public static readonly TimeSpan[] RenameBackoff =
    {
        TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(2000),
        TimeSpan.FromMilliseconds(2000), TimeSpan.FromMilliseconds(3000),
    };

    // Our own leftovers only: Clip_<stamp>.mp4.part / Gif_<stamp>.gif.part (+ optional " (n)"
    // collision suffix). A bare "*.mp4.part" also matches browser downloads (Firefox keeps them as
    // name.mp4.part), and the clips folder may well be a drive root.
    private static readonly Regex OwnPartName = new(
        @"^(?:Clip_[0-9]{4}-[0-9]{2}-[0-9]{2}_[0-9]{2}-[0-9]{2}-[0-9]{2}(?: \([0-9]+\))?\.mp4" +
        @"|Gif_[0-9]{4}-[0-9]{2}-[0-9]{2}_[0-9]{2}-[0-9]{2}-[0-9]{2}(?: \([0-9]+\))?\.gif)\.part$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string PartPathFor(string finalPath) => finalPath + PartSuffix;

    /// <summary>
    /// Helper files of a RAM-buffer save live next to the output: "Clip_….mp4.wkitmp.0.ts", "….wkitmp.list.txt". Hidden,
    /// deleted when the save ends, and never one of the gallery's extensions. A crash leaves them behind for the sweep.
    /// </summary>
    public const string TempMarker = ".wkitmp.";

    /// <summary>A temp file this old is a leftover (no save runs for hours).</summary>
    public static readonly TimeSpan StaleTempAge = TimeSpan.FromHours(6);

    private static readonly Regex OwnTempName = new(
        @"^(?:Clip_[0-9]{4}-[0-9]{2}-[0-9]{2}_[0-9]{2}-[0-9]{2}-[0-9]{2}(?: \([0-9]+\))?\.mp4" +
        @"|Gif_[0-9]{4}-[0-9]{2}-[0-9]{2}_[0-9]{2}-[0-9]{2}-[0-9]{2}(?: \([0-9]+\))?\.gif)\.wkitmp\.[A-Za-z0-9_-]+(?:\.[a-z0-9]{1,4})?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <param name="tag">"0.ts", "list.txt", "chapters.txt", "src" - letters, digits, '-', '_' and one short extension.</param>
    public static string TempPathFor(string finalPath, string tag) => finalPath + TempMarker + tag;

    public static bool IsStaleTemp(string fileName, DateTime lastWriteUtc, DateTime nowUtc)
        => IsOwnTemp(fileName) && nowUtc - lastWriteUtc > StaleTempAge;

    /// <summary>One of our RAM-save helper files, whatever its age.</summary>
    public static bool IsOwnTemp(string fileName) => OwnTempName.IsMatch(fileName);

    /// <summary>
    /// A leftover of an interrupted save: one of our own Clip_/Gif_ names with the .part suffix,
    /// older than <see cref="StalePartAge"/>. Anything else in the clips folder belongs to the user.
    /// </summary>
    public static bool IsStalePart(string fileName, DateTime lastWriteUtc, DateTime nowUtc)
        => OwnPartName.IsMatch(fileName) && nowUtc - lastWriteUtc > StalePartAge;

    /// <summary>
    /// Removes stale leftovers from the clips folder and one level of game subfolders (the same
    /// scan depth as the gallery). Returns how many files were deleted.
    /// </summary>
    /// <param name="tempsOfAnyAge">RAM-save helper files go regardless of age (first start of the process: all orphans).</param>
    public static int DeleteStaleParts(string clipsDir, DateTime nowUtc, bool tempsOfAnyAge = false)
    {
        int deleted = 0;
        foreach (var (file, _) in GameFolderNaming.EnumerateMedia(clipsDir,
                     f => IsStalePart(f.Name, f.LastWriteTimeUtc, nowUtc)
                          || (tempsOfAnyAge ? IsOwnTemp(f.Name) : IsStaleTemp(f.Name, f.LastWriteTimeUtc, nowUtc))))
        {
            try { file.Delete(); deleted++; }
            catch (Exception ex) { Logger.Warn($"Could not delete stale partial output '{file.FullName}': {ex.Message}"); }
        }
        return deleted;
    }

    public static Task<FinishOutcome> FinishAsync(string partPath, string finalPath, int exitCode)
        => FinishAsync(partPath, finalPath, exitCode, new OutputFileOps());

    /// <summary>
    /// Publishes the .part file under its real name when ffmpeg exited 0 and produced it.
    /// The rename is retried (IOException and UnauthorizedAccessException alike) for ~10 s, then a
    /// copy is tried. A finished clip is never deleted: if nothing works the .part stays and the
    /// result is <see cref="FinishOutcome.RenameFailed"/>. Only ffmpeg failure discards the output.
    /// </summary>
    internal static async Task<FinishOutcome> FinishAsync(string partPath, string finalPath, int exitCode,
        OutputFileOps ops)
    {
        if (exitCode != 0 || !File.Exists(partPath))
        {
            Discard(partPath);
            return FinishOutcome.Failed;
        }

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                ops.Move(partPath, finalPath);
                return FinishOutcome.Published;
            }
            catch (Exception ex)
            {
                bool transient = ex is IOException or UnauthorizedAccessException;
                if (!transient || attempt >= RenameBackoff.Length)
                {
                    Logger.Warn($"Could not rename '{partPath}' to '{finalPath}' ({ex.GetType().Name}: {ex.Message}) — trying a copy");
                    break;
                }
            }
            await ops.Delay(RenameBackoff[attempt]).ConfigureAwait(false);
        }

        bool finalExisted = File.Exists(finalPath);
        try
        {
            ops.Copy(partPath, finalPath);
        }
        catch (Exception ex)
        {
            // A copy that died midway leaves a half file under the real name - the one thing
            // this class exists to prevent. An older clip that was already there stays untouched.
            if (!finalExisted) { try { File.Delete(finalPath); } catch { } }
            Logger.Error($"Clip finished but could not be published as '{finalPath}' ({ex.GetType().Name}: {ex.Message}) — kept at '{partPath}'");
            return FinishOutcome.RenameFailed;
        }

        // Published. If the leftover cannot be removed now, the 24 h sweep does it later.
        try { File.Delete(partPath); } catch { }
        return FinishOutcome.Published;
    }

    /// <summary>Deletes a partial output, retrying briefly while a killed ffmpeg still holds it.</summary>
    public static void Discard(string partPath)
    {
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                if (File.Exists(partPath)) File.Delete(partPath);
                return;
            }
            catch { if (attempt < 5) Thread.Sleep(100); }
        }
    }
}
