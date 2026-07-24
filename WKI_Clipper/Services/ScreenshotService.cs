using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Versioning;
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

    public async Task<string?> CaptureAsync()
    {
        var outDir = SettingsService.ExpandPath(_settings.Current.Output.ScreenshotsFolder);
        Directory.CreateDirectory(outDir);
        var ts = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

        var (idx, screen) = CaptureTargetResolver.ResolveActiveMonitor();
        var path = Path.Combine(outDir, $"Shot_Display{idx + 1}_{ts}.png");

        // Hide our own overlay so it isn't in the shot; let it actually disappear.
        var hider = OverlayHider?.Invoke();
        try
        {
            if (hider != null) await Task.Delay(150);

            // Primary: DXGI Desktop Duplication (ddagrab) — captures the whole monitor
            // including exclusive-fullscreen games, where GDI would be black.
            if (await TryFFmpegMonitorAsync(path, idx))
            {
                ScreenshotSaved?.Invoke(this, path);
                return path;
            }

            // Fallback: GDI full-monitor copy (still whole monitor, never a window).
            if (TryGdiMonitorGrab(screen, path))
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

    private async Task<bool> TryFFmpegMonitorAsync(string outPath, int monitorIndex)
    {
        try
        {
            var ffmpeg = new FFmpegService();
            if (!ffmpeg.IsAvailable()) return false;
            var args = FFmpegCommandBuilder.BuildScreenshot(outPath, monitorIndex);
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

    private static bool TryGdiMonitorGrab(System.Windows.Forms.Screen screen, string outPath)
    {
        try
        {
            var b = screen.Bounds;
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
