using System;
using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>The numeric crosshair values the widget's sliders edit (the position and the image id have their own members).</summary>
public enum CrosshairField { Scale, Opacity, Brightness, Contrast, Saturation, GainR, GainG, GainB }

/// <summary>
/// What the crosshair widget edits right now: the standard look ("Alle Spiele") or one game's profile.
/// Reads give the value that applies (profile value, else the standard); writes go to the selected target
/// only, and a profile stores nothing but the fields that were actually changed. Selecting a game creates
/// nothing; the profile appears with the first change. The caller saves the settings.
/// </summary>
public sealed class CrosshairProfileEditor
{
    private readonly AppSettings _settings;
    private string? _displayName;

    public CrosshairProfileEditor(AppSettings settings) => _settings = settings;

    /// <summary>The selected game's profile key; null = all games (the standard look).</summary>
    public string? TargetKey { get; private set; }

    public bool IsProfile => TargetKey is not null;

    /// <summary>The selected game's label (for hints); empty for all games.</summary>
    public string TargetName => TargetKey is null ? "" : (string.IsNullOrWhiteSpace(_displayName) ? TargetKey : _displayName!);

    /// <summary>
    /// The user chose the target in the selector. From then on the editor stays where they put it
    /// (see <see cref="FollowDetectedGame"/>) until <see cref="ReleaseUserPick"/>.
    /// </summary>
    public bool UserPicked { get; private set; }

    /// <summary>The USER selects what is edited. An exe that names no game (null, Desktop) selects all games.</summary>
    public void SetTarget(string? exe, string? displayName)
    {
        UserPicked = true;
        Assign(exe, displayName);
    }

    private void Assign(string? exe, string? displayName)
    {
        TargetKey = EffectiveSettings.ProfileKey(exe);
        _displayName = TargetKey is null ? null : displayName;
    }

    /// <summary>
    /// Until the user picks a target themselves, the editor follows what is on screen: the detected game when it
    /// has crosshair values of its own, else all games. That keeps the preview and a drag on the game's profile
    /// when the board opens over a game - instead of silently editing the standard. Never writes anything.
    /// True = the target changed (sliders and lists must be rebuilt).
    /// </summary>
    public bool FollowDetectedGame(string? gameExe, string? gameName)
    {
        if (UserPicked) return false;
        string? key = GameProfiles.HasOverrides(EffectiveSettings.ProfileFor(_settings, gameExe)?.Crosshair)
            ? EffectiveSettings.ProfileKey(gameExe) : null;
        if (string.Equals(key, TargetKey, StringComparison.Ordinal))
        {
            if (key is not null && !string.IsNullOrWhiteSpace(gameName)) _displayName = gameName;
            return false;
        }
        Assign(key, gameName);
        return true;
    }

    /// <summary>The widget left the screen: the next time it opens it follows the detected game again.</summary>
    public void ReleaseUserPick() => UserPicked = false;

    private CrosshairLook? ExistingLook => TargetKey is null ? null : EffectiveSettings.ProfileFor(_settings, TargetKey)?.Crosshair;

    /// <summary>The profile's own look for writing: created on demand. Only call with a game selected.</summary>
    private CrosshairLook LookForWriting()
        => GameProfiles.LookForEditing(_settings, TargetKey, _displayName)
           ?? throw new InvalidOperationException("No game selected.");

    // ---- numeric values ----

    public double Get(CrosshairField f)
    {
        var g = _settings.Crosshair;
        var look = ExistingLook;
        return f switch
        {
            CrosshairField.Scale => look?.Scale ?? g.Scale,
            CrosshairField.Opacity => look?.Opacity ?? g.Opacity,
            CrosshairField.Brightness => look?.Brightness ?? g.Brightness,
            CrosshairField.Contrast => look?.Contrast ?? g.Contrast,
            CrosshairField.Saturation => look?.Saturation ?? g.Saturation,
            CrosshairField.GainR => look?.GainR ?? g.RedGain,
            CrosshairField.GainG => look?.GainG ?? g.GreenGain,
            CrosshairField.GainB => look?.GainB ?? g.BlueGain,
            _ => throw new ArgumentOutOfRangeException(nameof(f)),
        };
    }

    public void Set(CrosshairField f, double value)
    {
        if (TargetKey is null)
        {
            var g = _settings.Crosshair;
            switch (f)
            {
                case CrosshairField.Scale: g.Scale = value; break;
                case CrosshairField.Opacity: g.Opacity = value; break;
                case CrosshairField.Brightness: g.Brightness = value; break;
                case CrosshairField.Contrast: g.Contrast = value; break;
                case CrosshairField.Saturation: g.Saturation = value; break;
                case CrosshairField.GainR: g.RedGain = value; break;
                case CrosshairField.GainG: g.GreenGain = value; break;
                case CrosshairField.GainB: g.BlueGain = value; break;
            }
            return;
        }

        var look = LookForWriting();
        switch (f)
        {
            case CrosshairField.Scale: look.Scale = value; break;
            case CrosshairField.Opacity: look.Opacity = value; break;
            case CrosshairField.Brightness: look.Brightness = value; break;
            case CrosshairField.Contrast: look.Contrast = value; break;
            case CrosshairField.Saturation: look.Saturation = value; break;
            case CrosshairField.GainR: look.GainR = value; break;
            case CrosshairField.GainG: look.GainG = value; break;
            case CrosshairField.GainB: look.GainB = value; break;
        }
    }

    /// <summary>True when the selected game's profile sets this value itself (always false for all games).</summary>
    public bool IsOverridden(CrosshairField f)
    {
        var look = ExistingLook;
        if (look is null) return false;
        return f switch
        {
            CrosshairField.Scale => look.Scale is not null,
            CrosshairField.Opacity => look.Opacity is not null,
            CrosshairField.Brightness => look.Brightness is not null,
            CrosshairField.Contrast => look.Contrast is not null,
            CrosshairField.Saturation => look.Saturation is not null,
            CrosshairField.GainR => look.GainR is not null,
            CrosshairField.GainG => look.GainG is not null,
            CrosshairField.GainB => look.GainB is not null,
            _ => false,
        };
    }

    // ---- image ----

    /// <summary>The library image that applies for the target.</summary>
    public string? ActiveId => string.IsNullOrEmpty(ExistingLook?.ActiveId) ? _settings.Crosshair.ActiveId : ExistingLook!.ActiveId;

    public void SetActiveId(string? id)
    {
        if (TargetKey is null) _settings.Crosshair.ActiveId = id;
        else LookForWriting().ActiveId = id;
    }

    // ---- position ----

    /// <summary>
    /// "Exakt zentrieren": the standard position goes back to the primary screen's centre; a game's crosshair is
    /// pinned to the centre of the monitor that game is on (offset 0/0).
    /// </summary>
    public void CenterExactly()
    {
        if (TargetKey is null)
        {
            _settings.Crosshair.CenterX = null;
            _settings.Crosshair.CenterY = null;
            return;
        }
        var look = LookForWriting();
        look.OffsetX = 0;
        look.OffsetY = 0;
    }

    // ---- back to standard ----

    /// <summary>True when the selected game's profile sets any crosshair value.</summary>
    public bool HasOverrides => GameProfiles.HasOverrides(ExistingLook);

    /// <summary>"Wie Standard": the selected game loses all its crosshair values (its F9 length stays). Nothing for all games.</summary>
    public void ResetToStandard()
    {
        if (TargetKey is not null) GameProfiles.ResetCrosshair(_settings, TargetKey);
    }
}

/// <summary>
/// What the open crosshair widget is editing, as seen by the host: while the board is up the overlay previews
/// exactly this look (what you edit is what you see) and a drag moves this target. Key null = all games.
/// </summary>
public sealed record CrosshairEditTarget(string? Key, string DisplayName);

/// <summary>One entry of the "Gilt für" selector.</summary>
/// <param name="Key">Profile key; null = "Alle Spiele (Standard)".</param>
/// <param name="DisplayName">The game's label (the exe name when none is stored).</param>
/// <param name="IsCurrent">This is the game that is in front right now.</param>
public sealed record CrosshairTarget(string? Key, string DisplayName, bool IsCurrent);

/// <summary>Builds the "Gilt für" selector: all games, the detected game, then every existing profile.</summary>
public static class CrosshairTargetList
{
    /// <param name="selectedKey">The game the widget is editing right now. It stays in the list even without a
    /// profile (nothing is created by merely selecting a game), so the selector never jumps away from it.</param>
    public static IReadOnlyList<CrosshairTarget> Build(AppSettings settings, string? currentExe, string? currentName,
        string? selectedKey = null, string? selectedName = null)
    {
        var list = new List<CrosshairTarget> { new(null, "", false) };

        var currentKey = EffectiveSettings.ProfileKey(currentExe);
        if (currentKey is not null)
        {
            // A stored name may be older than the detected one; the detected label is the one the user sees on the game folders.
            var name = !string.IsNullOrWhiteSpace(currentName) ? currentName!.Trim()
                : EffectiveSettings.ProfileFor(settings, currentKey) is { DisplayName: { Length: > 0 } stored } ? stored : currentKey;
            list.Add(new CrosshairTarget(currentKey, name, true));
        }

        var others = new Dictionary<string, string>();
        foreach (var (key, profile) in settings.GameProfiles ?? new())
            if (key != currentKey) others[key] = string.IsNullOrWhiteSpace(profile?.DisplayName) ? key : profile!.DisplayName;
        if (selectedKey is not null && selectedKey != currentKey && !others.ContainsKey(selectedKey))
            others[selectedKey] = string.IsNullOrWhiteSpace(selectedName) ? selectedKey : selectedName!;

        list.AddRange(others
            .Select(kv => new CrosshairTarget(kv.Key, kv.Value, false))
            .OrderBy(t => t.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(t => t.Key, StringComparer.Ordinal));
        return list;
    }
}
