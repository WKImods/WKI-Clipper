using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WKI_Clipper.Services;

/// <summary>
/// Captures a screenshot of the WHOLE active monitor. Deliberately decoupled from the
/// capture profile (Auto/Window/Monitor) — a screenshot is always a full-monitor grab
/// of the display the user is currently looking at, never a single window. The old
/// per-window PrintWindow path was removed because it produced black images for
/// GPU-rendered games and ignored the "whole monitor" intent.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScreenshotService
{
    private readonly SettingsService _settings;

    public event EventHandler<string>? ScreenshotSaved;
    public event EventHandler<string>? ScreenshotFailed;

    /// <summary>
    /// Optional hook that hides our own overlay windows for the duration of the grab
    /// (returns an IDisposable that restores them, or null if nothing was visible).
    /// Set by the app once the widget host exists. Keeps own screenshots free of the
    /// overlay while widgets stay visible to external tools (Snipping Tool, OBS).
    /// </summary>
    public Func<IDisposable?>? OverlayHider { get; set; }

    public ScreenshotService(SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Names the game folder (set by the composition root). A screenshot is always the
    /// whole active monitor, so there is no plan — the foreground app names it.
    /// </summary>
    public Func<CaptureTargetResolver.CapturePlan?, string>? GameFolderFor { get; set; }

    public async Task<string?> CaptureAsync()
    {
        // Resolve the folder BEFORE hiding the overlay: that is still the moment the user
        // pressed the key, with the game in front.
        var o = _settings.Current.Output;
        var outDir = GameFolderNaming.PrepareDirectory(o.ScreenshotsFolder, o.SortByGame,
            GameFolderFor is { } f ? () => f(null) : null);
        var ts = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

        CaptureSource source;
        try
        {
            source = CaptureTargetResolver.ResolveActiveMonitor();
        }
        catch (InvalidOperationException ex)
        {
            Logger.Warn("Screenshot: " + ex.Message);
            ScreenshotFailed?.Invoke(this, Path.Combine(outDir, FileName(null, ts)));
            return null;
        }
        if (source.Resolution.IsFallback)
            Logger.Warn($"Screenshot: {source.Resolution.Reason} - taking the primary monitor '{source.Label}'");
        var path = Path.Combine(outDir, FileName(source.Resolution.Display?.FriendlyName, ts));

        // Hide our own overlay so it isn't in the shot; let it actually disappear.
        var hider = OverlayHider?.Invoke();
        try
        {
            if (hider != null) await Task.Delay(150);

            // Primary: DXGI Desktop Duplication (ddagrab) — captures the whole monitor
            // including exclusive-fullscreen games, where GDI would be black.
            if (await TryFFmpegMonitorAsync(path, source.OutputIndex))
            {
                ScreenshotSaved?.Invoke(this, path);
                return path;
            }

            // Fallback: GDI full-monitor copy (still whole monitor, never a window).
            if (source.Resolution.Display is { } display
                && TryGdiMonitorGrab(new Rectangle(display.X, display.Y, display.Width, display.Height), path))
            {
                ScreenshotSaved?.Invoke(this, path);
                return path;
            }
        }
        finally
        {
            hider?.Dispose();
        }

        ScreenshotFailed?.Invoke(this, path);
        return null;
    }

    /// <summary>"Shot_LG-ULTRAGEAR+_2026-11-09_12-00-00.png": the monitor's Windows name, file-safe; "Display" without one.</summary>
    internal static string FileName(string? friendlyName, string timestamp)
    {
        string name = Regex.Replace(GameFolderNaming.Sanitize(friendlyName), @"\s+", "-");
        return $"Shot_{(name.Length > 0 ? name : "Display")}_{timestamp}.png";
    }

    private async Task<bool> TryFFmpegMonitorAsync(string outPath, int outputIndex)
    {
        try
        {
            var ffmpeg = new FFmpegService();
            if (!ffmpeg.IsAvailable()) return false;
            var args = FFmpegCommandBuilder.BuildScreenshot(outPath, outputIndex);
            var tcs = new TaskCompletionSource<int>();
            ffmpeg.Exited += (_, code) => tcs.TrySetResult(code);
            ffmpeg.Start(args);
            var code = await tcs.Task;
            return code == 0 && File.Exists(outPath) && new FileInfo(outPath).Length > 1024;
        }
        catch
        {
            return false;
        }
    }

    /// <param name="b">The monitor rect in physical pixels (the app is per-monitor DPI aware, like the topology).</param>
    private static bool TryGdiMonitorGrab(Rectangle b, string outPath)
    {
        try
        {
            if (b.Width <= 0 || b.Height <= 0) return false;
            using var bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(b.Location, System.Drawing.Point.Empty, b.Size);
            bmp.Save(outPath, ImageFormat.Png);
            return new FileInfo(outPath).Length > 1024;
        }
        catch
        {
            return false;
        }
    }
}
