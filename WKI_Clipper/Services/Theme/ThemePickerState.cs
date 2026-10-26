using System;
using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>The two halves of a theme, picked independently.</summary>
public enum ThemeTab { Accent, Glass }

/// <summary>
/// What the colour picker does, without any window: a click on a swatch, a typed hex, the sliders and "Standard" change
/// <see cref="ThemeSettings"/>, and every change is applied to the running app and saved at once. Built from the settings it
/// edits and two callbacks, so it can be tested without the app.
/// </summary>
public sealed class ThemePickerState
{
    /// <summary>The sliders only pick hue and saturation; the lightness is that of a typical accent (the orange sits at .59) ...</summary>
    private const double AccentLightness = 0.58;
    /// <summary>... or of the glass tint swatches (the violet one is .19) - a glass is dark whatever its hue.</summary>
    private const double GlassLightness = 0.194;

    private readonly ThemeSettings _settings;
    private readonly Action<ThemePalette> _apply;
    private readonly Action _save;

    public ThemePickerState(ThemeSettings settings, Action<ThemePalette> apply, Action save)
    {
        _settings = settings;
        _apply = apply;
        _save = save;
    }

    public IReadOnlyList<ThemePreset> Presets(ThemeTab tab) => tab == ThemeTab.Accent ? ThemePresets.Accent : ThemePresets.Glass;

    /// <summary>The preset in force on this tab; null while a custom colour is.</summary>
    public string? SelectedPresetId(ThemeTab tab)
    {
        if (CustomHex(tab) is not null) return null;
        string? id = tab == ThemeTab.Accent ? _settings.AccentPreset : _settings.GlassPreset;
        return Presets(tab).FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))?.Id ?? DefaultId(tab);
    }

    /// <summary>The custom colour in force on this tab ("#RRGGBB"), or null.</summary>
    public string? CustomHex(ThemeTab tab) => ThemePaletteBuilder.NormalizeHex(tab == ThemeTab.Accent ? _settings.AccentCustom : _settings.GlassCustom);

    /// <summary>The colour the tab shows: the custom one, else the swatch colour of the chosen preset.</summary>
    public string CurrentHex(ThemeTab tab)
        => CustomHex(tab) ?? Presets(tab).First(p => p.Id == SelectedPresetId(tab)).Hex;

    /// <summary>A click on a swatch: that preset, no custom colour.</summary>
    public void ChoosePreset(ThemeTab tab, string? id)
    {
        var preset = Presets(tab).FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (preset is null) return;
        Set(tab, preset.Id, null);
        Commit();
    }

    /// <summary>
    /// A typed (or slider-made) colour. A valid hex becomes the tab's custom colour and goes on at once; anything else changes
    /// nothing. <paramref name="shown"/> is what the field must show afterwards: the normalised hex, or - on a rejection - the
    /// colour that is in force.
    /// </summary>
    public bool ApplyHex(ThemeTab tab, string? text, out string shown)
    {
        string? hex = ThemePaletteBuilder.NormalizeHex(text);
        if (hex is null)
        {
            shown = CurrentHex(tab);
            return false;
        }

        shown = hex;
        if (CurrentHex(tab) == hex) return true;   // already in force: not a new theme
        Set(tab, tab == ThemeTab.Accent ? _settings.AccentPreset : _settings.GlassPreset, hex);
        Commit();
        return true;
    }

    /// <summary>"Standard": the tab goes back to the default choice (the other tab keeps its own).</summary>
    public void Reset(ThemeTab tab)
    {
        Set(tab, DefaultId(tab), null);
        Commit();
    }

    /// <summary>The colour the two sliders stand for, as hex - nothing is applied.</summary>
    public string HexFromHueSaturation(ThemeTab tab, double hue, double saturation)
        => ThemeColor.ToHex(ThemeColor.FromHsl(hue, saturation, tab == ThemeTab.Accent ? AccentLightness : GlassLightness));

    /// <summary>Where the sliders stand for a colour: hue 0-360, saturation 0-1 (a grey, or text that is no colour, is 0 and 0).</summary>
    public (double Hue, double Saturation) HueSaturationOf(string? hex)
    {
        if (ThemePaletteBuilder.ParseColor(hex) is not { } c) return (0, 0);
        var (h, s, _) = ThemeColor.ToHsl(c);
        return (h, s);
    }

    private static string DefaultId(ThemeTab tab) => tab == ThemeTab.Accent ? ThemePresets.DefaultAccentId : ThemePresets.DefaultGlassId;

    private void Set(ThemeTab tab, string? presetId, string? custom)
    {
        if (tab == ThemeTab.Accent)
        {
            _settings.AccentPreset = presetId ?? ThemePresets.DefaultAccentId;
            _settings.AccentCustom = custom;
        }
        else
        {
            _settings.GlassPreset = presetId ?? ThemePresets.DefaultGlassId;
            _settings.GlassCustom = custom;
        }
    }

    private void Commit()
    {
        _apply(ThemePaletteBuilder.Build(_settings));
        _save();
    }
}
