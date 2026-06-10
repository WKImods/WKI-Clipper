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

        var (windowTitle, _) = GetActiveWindowInfo();
        var safeTitle = SanitizeFilename(windowTitle);
        var outPath = Path.Combine(outDir, $"Shot_{safeTitle}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");

        // Try PrintWindow first
        if (TryPrintWindow(outPath))
        {
            ScreenshotSaved?.Invoke(this, outPath);
            return outPath;
        }

        // Fallback: ffmpeg ddagrab single frame (captures the whole display)
        var ok = await TryFFmpegFallbackAsync(outPath);
        if (ok)
        {
            ScreenshotSaved?.Invoke(this, outPath);
            return outPath;
        }

        ScreenshotFailed?.Invoke(this, outPath);
        return null;
    }

    private static bool TryPrintWindow(string outPath)
    {
        try
        {
            var hwnd = User32.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            if (!User32.GetWindowRect(hwnd, out var rect)) return false;
            int w = rect.Width;
            int h = rect.Height;
            if (w <= 0 || h <= 0) return false;

            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                try
                {
                    bool ok = User32.PrintWindow(hwnd, hdc, User32.PW_RENDERFULLCONTENT);
                    if (!ok) return false;
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
            }
            bmp.Save(outPath, ImageFormat.Png);
            return new FileInfo(outPath).Length > 1024; // PrintWindow can return true and produce a blank image
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TryFFmpegFallbackAsync(string outPath)
    {
        try
        {
            var ffmpeg = new FFmpegService();
            if (!ffmpeg.IsAvailable()) return false;
            var args = FFmpegCommandBuilder.BuildScreenshot(outPath);
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
