using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Versioning;
using System.Windows.Media.Imaging;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>
/// Applies the crosshair's brightness / contrast / saturation / color-gain settings to
/// a PNG and hands back a WPF BitmapSource. Uses a GDI+ ColorMatrix (one pass, alpha
/// preserved — essential for a crosshair's transparent background).
/// </summary>
[SupportedOSPlatform("windows")]
public static class CrosshairImage
{
    /// <summary>
    /// Loads <paramref name="path"/> and returns it with the color adjustments applied.
    /// Returns null when the file is missing or not a readable image.
    /// </summary>
    public static BitmapSource? Render(string path, CrosshairSettings s)
    {
        try
        {
            if (!File.Exists(path)) return null;

            // Load fully into memory so the file isn't locked while the overlay runs.
            byte[] bytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(bytes);
            using var src = new Bitmap(ms);

            using var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            using (var attrs = new ImageAttributes())
            {
                g.Clear(System.Drawing.Color.Transparent);
                attrs.SetColorMatrix(BuildMatrix(s), ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                g.DrawImage(src,
                    new Rectangle(0, 0, src.Width, src.Height),
                    0, 0, src.Width, src.Height,
                    GraphicsUnit.Pixel, attrs);
            }

            return ToBitmapSource(dst);
        }
        catch (Exception ex)
        {
            Logger.Warn("Crosshair render failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>Unmodified load — used for library thumbnails.</summary>
    public static BitmapSource? LoadRaw(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;   // don't keep the file locked
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>
    /// Saturation → contrast → brightness, combined into a single 5x5 color matrix.
    /// Alpha is passed through untouched (window opacity handles transparency).
    /// </summary>
    private static ColorMatrix BuildMatrix(CrosshairSettings s)
    {
        float sat = (float)Math.Clamp(s.Saturation, 0, 2);
        float con = (float)Math.Clamp(s.Contrast, 0.1, 3);
        float bri = (float)Math.Clamp(s.Brightness, -1, 1);
        float rg = (float)Math.Clamp(s.RedGain, 0, 2);
        float gg = (float)Math.Clamp(s.GreenGain, 0, 2);
        float bg = (float)Math.Clamp(s.BlueGain, 0, 2);

        // Luminance weights (Rec. 601) for the saturation blend.
        const float lr = 0.3086f, lg = 0.6094f, lb = 0.0820f;
        float sr = (1 - sat) * lr, sgr = (1 - sat) * lg, sb = (1 - sat) * lb;

        // Contrast pivots around 0.5 so mid-gray stays put; brightness is an offset.
        float t = bri + (0.5f - con * 0.5f);

        return new ColorMatrix(new[]
        {
            new[] { (sr + sat) * con * rg, sr * con * gg,        sr * con * bg,        0f, 0f },
            new[] { sgr * con * rg,        (sgr + sat) * con * gg, sgr * con * bg,     0f, 0f },
            new[] { sb * con * rg,         sb * con * gg,        (sb + sat) * con * bg, 0f, 0f },
            new[] { 0f, 0f, 0f, 1f, 0f },   // alpha untouched
            new[] { t,  t,  t,  0f, 1f }
        });
    }

    private static BitmapSource ToBitmapSource(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var src = BitmapSource.Create(
                bmp.Width, bmp.Height, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null,
                data.Scan0, data.Stride * bmp.Height, data.Stride);
            src.Freeze();   // cross-thread safe + cheaper rendering
            return src;
        }
        finally { bmp.UnlockBits(data); }
    }
}
