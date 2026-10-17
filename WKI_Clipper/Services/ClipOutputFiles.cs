using System;
using System.IO;
using System.Threading;

namespace WKI_Clipper.Services;

/// <summary>
/// Clips and GIFs are written to "&lt;name&gt;.mp4.part" / "&lt;name&gt;.gif.part" and only get
/// their real name once ffmpeg finished successfully. Writing straight to Clip_*.mp4 let the
/// gallery list a half-written file that was still growing, and a timeout could leave it behind.
/// The gallery filters by extension, so a .part file is invisible to it by construction.
/// </summary>
public static class ClipOutputFiles
{
    public const string PartSuffix = ".part";

    /// <summary>Leftovers younger than this may belong to a save that is still running.</summary>
    public static readonly TimeSpan StalePartAge = TimeSpan.FromMinutes(10);

    public static string PartPathFor(string finalPath) => finalPath + PartSuffix;

    /// <summary>
    /// A leftover of an interrupted save: ends exactly in ".mp4.part" or ".gif.part" and is older
    /// than <see cref="StalePartAge"/>. Anything else in the clips folder belongs to the user.
    /// </summary>
    public static bool IsStalePart(string fileName, DateTime lastWriteUtc, DateTime nowUtc)
        => (fileName.EndsWith(".mp4" + PartSuffix, StringComparison.Ordinal)
            || fileName.EndsWith(".gif" + PartSuffix, StringComparison.Ordinal))
           && nowUtc - lastWriteUtc > StalePartAge;

    /// <summary>
    /// Removes stale leftovers from the clips folder and one level of game subfolders (the same
    /// scan depth as the gallery). Returns how many files were deleted.
    /// </summary>
    public static int DeleteStaleParts(string clipsDir, DateTime nowUtc)
    {
        int deleted = 0;
        foreach (var (file, _) in GameFolderNaming.EnumerateMedia(clipsDir,
                     f => IsStalePart(f.Name, f.LastWriteTimeUtc, nowUtc)))
        {
            try { file.Delete(); deleted++; }
            catch (Exception ex) { Logger.Warn($"Could not delete stale partial output '{file.FullName}': {ex.Message}"); }
        }
        return deleted;
    }

    /// <summary>
    /// Publishes the .part file under its real name when ffmpeg exited 0 and produced it;
    /// otherwise deletes it. True only when the final file now exists.
    /// </summary>
    public static bool Finish(string partPath, string finalPath, int exitCode)
    {
        if (exitCode != 0 || !File.Exists(partPath))
        {
            Discard(partPath);
            return false;
        }

        // The just-exited ffmpeg (or a scanner looking at the new file) can hold it for a moment.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(partPath, finalPath, overwrite: true);
                return true;
            }
            catch (IOException) when (attempt < 5) { Thread.Sleep(100); }
            catch (Exception ex)
            {
                Logger.Warn($"Could not publish '{partPath}' as '{finalPath}': {ex.Message}");
                Discard(partPath);
                return false;
            }
        }
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
