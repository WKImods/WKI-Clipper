using System;
using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.Models;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Mouse-patterns design 4.8: per-pattern warnings, never blocking.</summary>
public sealed class MousePatternConflictsTests
{
    private static ClickerHotkey Hk(ClickerModifiers mods, params int[] keys) => new() { Modifiers = mods, Keys = new List<int>(keys) };

    private static MousePattern Pattern(string name, ClickerHotkey? hotkey, bool active = true, string? program = null, bool onlyIn = false)
        => new() { Id = Guid.NewGuid().ToString("N"), Name = name, Hotkey = hotkey, Active = active, Program = program, OnlyInProgram = onlyIn };

    private static AppSettings Settings(params MousePattern[] patterns)
    {
        var s = new AppSettings();
        s.AutoClicker.Clicker.MousePatterns.AddRange(patterns);
        return s;
    }

    private static IReadOnlyList<PatternWarning> Eval(AppSettings s) => MousePatternConflicts.Evaluate(s, _ => true);

    private static List<PatternWarning> For(IReadOnlyList<PatternWarning> all, MousePattern p) => all.Where(w => w.PatternId == p.Id).ToList();

    [Fact]
    public void Plain_patterns_have_no_warnings()
    {
        var a = Pattern("Kreis", Hk(ClickerModifiers.None, 0x75));   // F6
        var b = Pattern("Welle", Hk(ClickerModifiers.None, 0x76));   // F7
        var c = Pattern("Ohne Hotkey", null);
        Assert.Empty(Eval(Settings(a, b, c)));
        Assert.Empty(MousePatternConflicts.Evaluate(new AppSettings(), _ => false));
    }

    [Fact]
    public void Warnings_flag_overlapping_active_patterns_only()
    {
        var a = Pattern("Kreis", Hk(ClickerModifiers.None, 0x75));
        var b = Pattern("Welle", Hk(ClickerModifiers.None, 0x75));
        var c = Pattern("Acht", Hk(ClickerModifiers.Ctrl, 0x75));            // not strict: Ctrl+F6 also satisfies F6
        var off = Pattern("Alt", Hk(ClickerModifiers.None, 0x75), active: false);
        var all = Eval(Settings(a, b, c, off));

        var wa = Assert.Single(For(all, a));
        Assert.False(wa.Info);
        Assert.Contains("'Welle'", wa.Message);
        Assert.Contains("'Acht'", wa.Message);
        Assert.DoesNotContain("'Alt'", wa.Message);
        Assert.Contains("'Kreis'", Assert.Single(For(all, b)).Message);
        Assert.Empty(For(all, off));                                         // the inactive duplicate is not tracked: no warning either way

        var strict = Settings(a, b, c);
        strict.AutoClicker.Clicker.StrictModifiers = true;
        Assert.DoesNotContain("'Acht'", Assert.Single(For(Eval(strict), a)).Message);
    }

    [Fact]
    public void Patterns_bound_to_different_programs_never_start_together()
    {
        var a = Pattern("Kickflip", Hk(ClickerModifiers.None, ClickerVk.XButton1), program: "skate.exe", onlyIn: true);
        var b = Pattern("Angeln", Hk(ClickerModifiers.None, ClickerVk.XButton1), program: "fish.exe", onlyIn: true);
        var c = Pattern("Heelflip", Hk(ClickerModifiers.None, ClickerVk.XButton1), program: "SKATE.exe", onlyIn: true);
        var all = Eval(Settings(a, b, c));

        Assert.Contains("'Heelflip'", Assert.Single(For(all, a)).Message);
        Assert.Empty(For(all, b));
    }

    [Fact]
    public void Warning_for_bare_mouse_button_without_program_binding()
    {
        var loose = Pattern("Kickflip", Hk(ClickerModifiers.None, ClickerVk.LButton), program: "skate.exe");
        var w = Assert.Single(Eval(Settings(loose)));
        Assert.False(w.Info);
        Assert.Contains("linke Maustaste allein", w.Message);
        Assert.Contains("Wirkt überall - 'Nur in skate.exe' anhaken?", w.Message);

        var bound = Pattern("Kickflip", Hk(ClickerModifiers.None, ClickerVk.RButton), program: "skate.exe", onlyIn: true);
        var wb = Assert.Single(Eval(Settings(bound)));
        Assert.Contains("rechte Maustaste allein", wb.Message);
        Assert.DoesNotContain("Wirkt überall", wb.Message);

        var unknown = Pattern("Kickflip", Hk(ClickerModifiers.None, ClickerVk.LButton));
        var wu = Assert.Single(Eval(Settings(unknown)));
        Assert.Contains("Wirkt überall.", wu.Message);
        Assert.DoesNotContain("Nur in", wu.Message);
    }

    [Fact]
    public void The_other_risky_hotkey_hints_apply_to_patterns()
    {
        var mods = Pattern("a", Hk(ClickerModifiers.Ctrl | ClickerModifiers.Shift));
        var esc = Pattern("b", Hk(ClickerModifiers.Alt, ClickerVk.Escape));
        var wheel = Pattern("c", Hk(ClickerModifiers.Shift, ClickerVk.WheelUp));
        var all = Eval(Settings(mods, esc, wheel));

        Assert.Contains("nur aus Modifikatortasten (Strg+Umschalt)", Assert.Single(For(all, mods)).Message);
        Assert.Contains("enthält Esc", Assert.Single(For(all, esc)).Message);
        Assert.Contains("nutzt das Mausrad", Assert.Single(For(all, wheel)).Message);
        Assert.All(all, w => Assert.False(w.Info));
    }

    [Fact]
    public void A_pattern_on_the_record_hotkey_warns()
    {
        var p = Pattern("Kreis", MousePatternRules.DefaultRecordHotkey());
        var w = Assert.Single(Eval(Settings(p)));
        Assert.Contains("Aufnahme-Hotkey", w.Message);

        var off = Pattern("Kreis", MousePatternRules.DefaultRecordHotkey(), active: false);
        Assert.Empty(Eval(Settings(off)));
    }

    [Fact]
    public void A_pattern_on_a_clipper_hotkey_warns_and_names_the_action()
    {
        var p = Pattern("Kreis", Hk(ClickerModifiers.Ctrl | ClickerModifiers.Alt, 0x47));   // ToggleOverlay
        var w = Assert.Single(Eval(Settings(p)));
        Assert.Contains("ToggleOverlay", w.Message);
        Assert.Contains("Beide Aktionen würden gleichzeitig ausgelöst", w.Message);
    }

    [Fact]
    public void Sharing_the_clicker_hotkey_is_info_not_warning()
    {
        var p = Pattern("Kreis", ClickerHotkey.Default());
        var w = Assert.Single(Eval(Settings(p)));
        Assert.True(w.Info);
        Assert.Contains("Clicker", w.Message);
    }

    [Fact]
    public void Sharing_the_hotkey_with_a_clicker_that_uses_click_points_warns()
    {
        var p = Pattern("Kreis", ClickerHotkey.Default());
        var s = Settings(p);
        s.AutoClicker.Clicker.ClickPointsEnabled = true;
        s.AutoClicker.Clicker.ClickPoints.Add(new ClickPoint { X = 10, Y = 10 });

        var w = Assert.Single(Eval(s));
        Assert.False(w.Info);
        Assert.Contains("Klickpunkte", w.Message);

        s.AutoClicker.Clicker.ClickPoints.Clear();   // enabled but empty: the clicker does not move the pointer
        Assert.True(Assert.Single(Eval(s)).Info);
    }

    [Fact]
    public void Missing_data_warns_even_for_inactive_patterns_without_hotkey()
    {
        var p = Pattern("Kreis", null, active: false);
        var q = Pattern("Welle", null);
        var all = MousePatternConflicts.Evaluate(Settings(p, q), id => id == q.Id);

        var w = Assert.Single(all);
        Assert.Equal(p.Id, w.PatternId);
        Assert.False(w.Info);
        Assert.Contains("Aufnahme fehlt", w.Message);
    }

    [Fact]
    public void No_clicker_section_means_no_warnings()
    {
        var s = new AppSettings();
        s.AutoClicker.Clicker = null!;
        Assert.Empty(MousePatternConflicts.Evaluate(s, _ => false));
    }

    // ---- the clicker's own texts did not change when the risk texts were shared ----

    [Fact]
    public void Clicker_conflict_texts_are_unchanged()
    {
        static string Msg(Action<ClickerSettings> edit, ClickerConflictKind kind)
        {
            var s = new AppSettings();
            edit(s.AutoClicker.Clicker);
            return ClickerConflicts.Evaluate(s).Single(c => c.Kind == kind).Message;
        }

        Assert.Equal("Der Hotkey ist die linke Maustaste allein: jeder Linksklick löst ihn aus, auch in Menüs und im Spiel (nicht über dem Clipper).",
            Msg(c => c.Hotkey = Hk(ClickerModifiers.None, ClickerVk.LButton), ClickerConflictKind.HotkeyIsBareMouseButton));
        Assert.Equal("Der Master-Hotkey ist die rechte Maustaste allein: jeder Rechtsklick löst ihn aus, auch in Menüs und im Spiel (nicht über dem Clipper).",
            Msg(c => c.MasterHotkey = Hk(ClickerModifiers.None, ClickerVk.RButton), ClickerConflictKind.MasterIsBareMouseButton));
        Assert.Equal("Der Hotkey besteht nur aus Modifikatortasten (Strg+Umschalt): jede Tastenkombination damit, z. B. Strg+C, löst ihn mit aus.",
            Msg(c => c.Hotkey = Hk(ClickerModifiers.Ctrl | ClickerModifiers.Shift), ClickerConflictKind.HotkeyIsModifierOnly));
        Assert.Equal("Der Master-Hotkey enthält Esc: Esc schließt auch Menüs und Dialoge, und jeder solche Druck löst ihn mit aus.",
            Msg(c => c.MasterHotkey = Hk(ClickerModifiers.None, ClickerVk.Escape), ClickerConflictKind.MasterHasEscape));
        Assert.Equal("Der Hotkey nutzt das Mausrad: jedes Scrollen löst ihn aus. Eine Scroll-Bewegung zählt als ein Druck, gehalten solange du weiterscrollst.",
            Msg(c => c.Hotkey = Hk(ClickerModifiers.None, ClickerVk.WheelDown), ClickerConflictKind.HotkeyHasWheel));
        Assert.Equal("Der Hotkey ist schon belegt: Clipper-Aktion \"ToggleOverlay\". Beide Aktionen würden gleichzeitig ausgelöst.",
            Msg(c => c.Hotkey = Hk(ClickerModifiers.Ctrl | ClickerModifiers.Alt, 0x47), ClickerConflictKind.HotkeyIsClipperHotkey));
    }
}

[Collection("Language")]
public sealed class MousePatternConflictsEnglishTests
{
    [Fact]
    public void The_pattern_warnings_follow_the_language()
    {
        using var _ = TestLanguage.English();
        var s = new AppSettings();
        var p = new MousePattern { Id = Guid.NewGuid().ToString("N"), Name = "Kickflip", Hotkey = new ClickerHotkey { Keys = { ClickerVk.LButton } }, Program = "skate.exe" };
        s.AutoClicker.Clicker.MousePatterns.Add(p);

        var all = MousePatternConflicts.Evaluate(s, _ => false);

        Assert.Contains(all, w => w.Message.StartsWith("The hotkey is the left mouse button alone") && w.Message.Contains("Works everywhere - tick 'Only in skate.exe'?"));
        Assert.Contains(all, w => w.Message.StartsWith("Recording missing"));
        Assert.Equal("Pattern 1", MousePatternRules.NextName(Array.Empty<MousePattern>()));
    }
}
