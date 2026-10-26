using System;
using System.Windows;
using System.Windows.Media;

namespace WKI_Clipper.Services;

/// <summary>
/// Puts a <see cref="ThemePalette"/> on screen. The colours and brushes of the palette (see <see cref="ThemePaletteKeys"/>) are
/// resources of Views/Theme.xaml; a switch replaces them with NEW instances. Nothing is ever changed in place: a brush that sits
/// inside a style is frozen, and the old instances may still be held by windows that have not rebuilt themselves yet.
///
/// Everything that references a palette key with {DynamicResource} (all of the XAML, and code that uses SetResourceReference)
/// follows at once. Code-built UI that took a brush with FindResource keeps the one it got: it is rebuilt after the switch
/// (the widget host does that on <see cref="Changed"/>).
/// </summary>
public sealed class ThemeService
{
    /// <summary>Marks the dictionary a theme is applied as, so any service finds and replaces it - there is only ever one overlay.</summary>
    private const string OverlayMarker = "WKI.ThemeOverlay";

    /// <summary>Raised on the calling (UI) thread after a palette has been applied.</summary>
    public event Action? Changed;

    /// <summary>The palette applied last; null while the theme is the one of Theme.xaml.</summary>
    public ThemePalette? Current { get; private set; }

    /// <summary>
    /// Makes the palette the application's theme. It goes in as one dictionary merged after Theme.xaml (merged dictionaries are
    /// searched last to first), swapped in a single step: one notification to every dynamic reference instead of one per key.
    /// </summary>
    public void Apply(ThemePalette palette)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        var next = new ResourceDictionary { [OverlayMarker] = true };
        ApplyTo(next, palette);

        int at = -1;
        for (int i = 0; i < merged.Count; i++)
            if (merged[i].Contains(OverlayMarker)) { at = i; break; }
        if (at >= 0) merged[at] = next;
        else merged.Add(next);

        Current = palette;
        Changed?.Invoke();
    }

    /// <summary>Sets every palette entry of <paramref name="target"/> to a new instance made from the palette.</summary>
    public static void ApplyTo(ResourceDictionary target, ThemePalette palette)
    {
        foreach (var key in ThemePaletteKeys.ColorKeys)
            target[key] = palette.Colors[key];

        foreach (var (brush, colorKey) in ThemePaletteKeys.SolidBrushes)
        {
            var b = new SolidColorBrush(palette.Colors[colorKey]);
            b.Freeze();
            target[brush] = b;
        }

        foreach (var spec in ThemePaletteKeys.Gradients)
        {
            var stops = palette.GradientStops[spec.Key];
            var b = new LinearGradientBrush { StartPoint = spec.Start, EndPoint = spec.End };
            for (int i = 0; i < stops.Length; i++)
                b.GradientStops.Add(new GradientStop(stops[i], spec.Offsets[i]));
            b.Freeze();
            target[spec.Key] = b;
        }
    }
}
