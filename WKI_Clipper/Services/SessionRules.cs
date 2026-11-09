namespace WKI_Clipper.Services;

/// <summary>How a buffer (re)start or retarget treats the footage and the pipeline of the run before it.</summary>
public static class SessionRules
{
    /// <summary>
    /// A warm restart starts a fresh save window (the older segments can never join a clip) when the buffer folder moved
    /// or another physical monitor is recorded than at the last START. <paramref name="prev"/> is that last started source,
    /// kept across the stop of the restart. A mode change of the same monitor keeps the session: preset output stays the
    /// same size, and Native sizes are already kept apart by the segment source key.
    /// </summary>
    public static bool ShouldResetSession(CaptureSource? prev, CaptureSource next, bool folderMoved)
        => folderMoved || prev is not null && !CaptureSourceRules.SameMonitor(prev, next);

    /// <summary>
    /// A retarget may keep the running video pipeline (and only swap the audio): the same source - monitor, DXGI output and
    /// size, so ffmpeg would get the same arguments - and, for WGC window capture, the same window.
    /// </summary>
    public static bool KeepsVideo(CaptureTargetResolver.CapturePlan prev, CaptureTargetResolver.CapturePlan next)
        => CaptureSourceRules.SameSource(prev.Source, next.Source)
           && prev.UseWgc == next.UseWgc
           && (!next.UseWgc || prev.Hwnd == next.Hwnd);
}
