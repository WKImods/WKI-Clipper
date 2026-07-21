using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;
using WKI_Clipper.Models;
using WKI_Clipper.Native;

namespace WKI_Clipper.Services;

[SupportedOSPlatform("windows")]
public sealed class ScreenshotService
{
    private readonly SettingsService _settings;

    public event EventHandler<string>? ScreenshotSaved;
    public event EventHandler<string>? ScreenshotFailed;

    public ScreenshotService(SettingsService settings)
    {
        _settings = settings;
    }

    public async Task<string?> CaptureActiveWindowAsync()
    {
        var outDir = SettingsService.ExpandPath(_settings.Current.Output.ScreenshotsFolder);
        Directory.CreateDirectory(outDir);
        var ts = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

        var (windowTitle, hwnd) = GetActiveWindowInfo();

        // Try a clean per-window shot first.
        var winPath = Path.Combine(outDir, $"Shot_{SanitizeFilename(windowTitle)}_{ts}.png");
        if (TryPrintWindow(hwnd, winPath))
        {
            ScreenshotSaved?.Invoke(this, winPath);
            return winPath;
        }

        // Fallback: whole-monitor grab. Name + notify it HONESTLY as a display
        // shot (not the window title) and capture the resolved target monitor.
        var plan = CaptureTargetResolver.Resolve(_settings.Current.Capture, _settings.Current);
        var dispPath = Path.Combine(outDir, $"Shot_Display{plan.MonitorIndex + 1}_{ts}.png");
        if (await TryFFmpegFallbackAsync(dispPath, plan.MonitorIndex))
        {
            ScreenshotSaved?.Invoke(this, dispPath);
            return dispPath;
        }

        ScreenshotFailed?.Invoke(this, dispPath);
        return null;
    }

    private static bool TryPrintWindow(IntPtr hwnd, string outPath)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return false;
            if (!User32.GetWindowRect(hwnd, out var wr)) return false;
            int w = wr.Width, h = wr.Height;
            if (w <= 0 || h <= 0) return false;

            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                try
                {
                    if (!User32.PrintWindow(hwnd, hdc, User32.PW_RENDERFULLCONTENT))
                        return false;
                }
                finally { g.ReleaseHdc(hdc); }
            }

            // Crop away the invisible resize border (GetWindowRect includes it,
            // DWM's extended frame bounds don't) so the PNG has no blank edges.
            if (User32.DwmGetWindowAttribute(hwnd, User32.DWMWA_EXTENDED_FRAME_BOUNDS,
                    out var fb, System.Runtime.InteropServices.Marshal.SizeOf<User32.RECT>()) == 0
                && fb.Width > 0 && fb.Height > 0)
            {
                int offX = fb.Left - wr.Left;
                int offY = fb.Top - wr.Top;
                var crop = new Rectangle(
                    Math.Max(0, offX), Math.Max(0, offY),
                    Math.Min(fb.Width, w - Math.Max(0, offX)),
                    Math.Min(fb.Height, h - Math.Max(0, offY)));
                if (crop.Width > 0 && crop.Height > 0)
                {
                    using var cropped = bmp.Clone(crop, bmp.PixelFormat);
                    cropped.Save(outPath, ImageFormat.Png);
                    return new FileInfo(outPath).Length > 1024;
                }
            }

            bmp.Save(outPath, ImageFormat.Png);
            return new FileInfo(outPath).Length > 1024; // PrintWindow can return true yet produce a blank image
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TryFFmpegFallbackAsync(string outPath, int monitorIndex)
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
            return code == 0 && File.Exists(outPath);
        }
        catch
        {
            return false;
        }
    }

    private static (string title, IntPtr hwnd) GetActiveWindowInfo()
    {
        var hwnd = User32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return ("unknown", IntPtr.Zero);
        int len = User32.GetWindowTextLength(hwnd);
        if (len <= 0) return ("window", hwnd);
        var sb = new StringBuilder(len + 1);
        User32.GetWindowText(hwnd, sb, sb.Capacity);
        return (sb.ToString(), hwnd);
    }

    private static string SanitizeFilename(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "window";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        s = s.Replace(' ', '_');
        return s.Length > 40 ? s[..40] : s;
    }
}
