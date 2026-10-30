using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WKI_Clipper.Models;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Mouse-patterns design 4.1/5.2: the pattern list in <see cref="ClickerSettings"/> and <see cref="MousePatternRules"/>.</summary>
public sealed class MousePatternRulesTests
{
    private static string NewId() => Guid.NewGuid().ToString("N");

    private static ClickerSettings Norm(Action<ClickerSettings> edit)
    {
        var c = new ClickerSettings();
        edit(c);
        return ClickerSettingsRules.Normalize(c);
    }

    [Fact]
    public void A_new_pattern_has_the_design_defaults()
    {
        var p = new MousePattern();
        Assert.True(p.Active);
        Assert.Null(p.Hotkey);
        Assert.Equal(ActivationMode.Hold, p.Mode);
        Assert.False(p.Repeat);
        Assert.Equal(1.0, p.Speed);
        Assert.Equal(PatternReplay.Auto, p.Replay);
        Assert.Null(p.Program);
        Assert.False(p.OnlyInProgram);
        Assert.Equal(0, p.Revision);
    }

    [Fact]
    public void The_settings_defaults_have_patterns_off_and_the_record_hotkey_set()
    {
        var c = new ClickerSettings();
        Assert.False(c.MousePatternsArmed);
        Assert.Empty(c.MousePatterns);
        Assert.True(ClickerHotkey.SameCombo(MousePatternRules.DefaultRecordHotkey(), c.PatternRecordHotkey));
        Assert.Equal(ClickerModifiers.Ctrl | ClickerModifiers.Alt, c.PatternRecordHotkey.Modifiers);
        Assert.Equal(new[] { 0x52 }, c.PatternRecordHotkey.Keys);   // Strg+Alt+R
    }

    [Fact]
    public void The_default_record_hotkey_clashes_with_no_clipper_default_and_not_with_the_clicker()
    {
        var s = new AppSettings();
        Assert.Null(ClickerConflicts.ClipperOwner(s, MousePatternRules.DefaultRecordHotkey(), strict: false));
        Assert.False(ClickerHotkey.Overlaps(MousePatternRules.DefaultRecordHotkey(), ClickerHotkey.Default(), strict: false));
        Assert.NotSame(MousePatternRules.DefaultRecordHotkey(), MousePatternRules.DefaultRecordHotkey());   // hotkeys are mutable
    }

    [Fact]
    public void Normalize_gives_patterns_ids_caps_and_clamps()
    {
        string keep = NewId();
        var c = Norm(x =>
        {
            for (int i = 0; i < 51; i++) x.MousePatterns.Add(new MousePattern { Id = NewId(), Name = "p" + i });
        });
        Assert.Equal(MousePatternRules.MaxPatterns, c.MousePatterns.Count);
        Assert.Equal("p0", c.MousePatterns[0].Name);
        Assert.Equal("p49", c.MousePatterns[49].Name);

        var d = Norm(x =>
        {
            x.MousePatterns.Add(new MousePattern { Id = keep, Name = "  ", Speed = 9 });
            x.MousePatterns.Add(new MousePattern { Id = keep, Name = "Kreis", Speed = 0 });          // duplicate id
            x.MousePatterns.Add(new MousePattern { Id = "", Name = "Welle", Speed = double.NaN });   // empty id
            x.MousePatterns.Add(new MousePattern { Id = "../evil", Speed = double.PositiveInfinity });   // no valid id: never a file name
            x.MousePatterns.Add(null!);
            x.MousePatterns.Add(new MousePattern { Id = NewId(), Speed = -2 });
            x.MousePatterns.Add(new MousePattern { Id = NewId(), Speed = 1.5 });
        });

        var p = d.MousePatterns;
        Assert.Equal(6, p.Count);
        Assert.Equal(keep, p[0].Id);
        Assert.All(p, x => Assert.True(MousePatternRules.IsValidId(x.Id)));
        Assert.Equal(6, p.Select(x => x.Id.ToLowerInvariant()).Distinct().Count());
        Assert.Equal(new[] { 4.0, 0.25, 1.0, 1.0, 1.0, 1.5 }, p.Select(x => x.Speed));
        Assert.Equal("Muster 1", p[0].Name);   // blank name -> the first free default name
        Assert.Equal("Kreis", p[1].Name);
        Assert.Equal("Welle", p[2].Name);
        Assert.Equal("Muster 2", p[3].Name);
    }

    [Fact]
    public void Normalize_repairs_the_other_pattern_fields()
    {
        var c = Norm(x =>
        {
            x.MousePatterns.Add(new MousePattern
            {
                Id = NewId(), Name = "  " + new string('n', 60) + "\t", Mode = (ActivationMode)9, Replay = (PatternReplay)7,
                Hotkey = new ClickerHotkey { Modifiers = ClickerModifiers.Ctrl | ClickerModifiers.LCtrl, Keys = { 0x41, 0x41, 0xA2 } },
                Program = @"C:\Games\Skate\SkaterXL", OnlyInProgram = true, DurationMs = 999_999, Revision = -4
            });
            x.MousePatterns.Add(new MousePattern
            {
                Id = NewId(), Hotkey = new ClickerHotkey(), Program = "   ", OnlyInProgram = true, DurationMs = double.NaN
            });
        });

        var a = c.MousePatterns[0];
        Assert.Equal(40, a.Name.Length);
        Assert.Equal(ActivationMode.Hold, a.Mode);
        Assert.Equal(PatternReplay.Auto, a.Replay);
        Assert.Equal(ClickerModifiers.LCtrl, a.Hotkey!.Modifiers);
        Assert.Equal(new[] { 0x41 }, a.Hotkey.Keys);
        Assert.Equal("SkaterXL.exe", a.Program);   // exe file name, like the blacklist
        Assert.True(a.OnlyInProgram);
        Assert.Equal(MousePatternRules.MaxDurationMs, a.DurationMs);
        Assert.Equal(0, a.Revision);

        var b = c.MousePatterns[1];
        Assert.Null(b.Hotkey);              // an empty hotkey is "no hotkey", never the clicker default
        Assert.Null(b.Program);
        Assert.False(b.OnlyInProgram);      // nothing to bind to
        Assert.Equal(0, b.DurationMs);
    }

    [Fact]
    public void The_record_hotkey_is_required_and_falls_back_to_its_own_default()
    {
        Assert.True(ClickerHotkey.SameCombo(MousePatternRules.DefaultRecordHotkey(), Norm(x => x.PatternRecordHotkey = null!).PatternRecordHotkey));
        Assert.True(ClickerHotkey.SameCombo(MousePatternRules.DefaultRecordHotkey(), Norm(x => x.PatternRecordHotkey = new ClickerHotkey()).PatternRecordHotkey));

        var custom = Norm(x => x.PatternRecordHotkey = new ClickerHotkey { Keys = { ClickerVk.XButton2 } });
        Assert.Equal(new[] { ClickerVk.XButton2 }, custom.PatternRecordHotkey.Keys);
    }

    [Fact]
    public void A_null_pattern_list_is_replaced()
    {
        var c = Norm(x => x.MousePatterns = null!);
        Assert.NotNull(c.MousePatterns);
        Assert.Empty(c.MousePatterns);
    }

    [Fact]
    public void Normalize_is_idempotent()
    {
        var c = Norm(x =>
        {
            x.MousePatternsArmed = true;
            x.PatternRecordHotkey = new ClickerHotkey { Modifiers = ClickerModifiers.Alt | ClickerModifiers.RAlt, Keys = { 0x52, 0x52 } };
            x.MousePatterns.Add(new MousePattern { Name = "", Speed = 12, Program = "game", DurationMs = -1 });
            x.MousePatterns.Add(new MousePattern { Name = "", Speed = double.NaN, Hotkey = new ClickerHotkey { Keys = { ClickerVk.LButton } } });
            x.MousePatterns.Add(new MousePattern { Id = "dup", Name = "x" });
            x.MousePatterns.Add(new MousePattern { Id = "dup", Name = "x" });
        });
        var once = JsonSerializer.Serialize(c);
        ClickerSettingsRules.Normalize(c);
        Assert.Equal(once, JsonSerializer.Serialize(c));
    }

    [Theory]
    [InlineData("patterns", "patterns")]
    [InlineData(" Patterns ", "patterns")]
    public void SubTab_patterns_survives_normalize(string input, string expected)
        => Assert.Equal(expected, Norm(c => c.SubTab = input).SubTab);

    [Fact]
    public void The_patterns_sub_tab_sits_between_click_points_and_presets()
        => Assert.Equal(new[] { "simple", "advanced", "zones", "points", "patterns", "presets" }, ClickerSettingsRules.SubTabs);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void HostWanted_is_true_when_either_switch_is_on(bool clicker, bool patterns, bool expected)
        => Assert.Equal(expected, MousePatternRules.HostWanted(new ClickerSettings { Armed = clicker, MousePatternsArmed = patterns }));

    [Fact]
    public void NextName_takes_the_first_free_number()
    {
        Assert.Equal("Muster 1", MousePatternRules.NextName(Array.Empty<MousePattern>()));
        Assert.Equal("Muster 2", MousePatternRules.NextName(new[] { new MousePattern { Name = "muster 1" } }));
        Assert.Equal("Muster 1", MousePatternRules.NextName(new[] { new MousePattern { Name = "Muster 2" }, new MousePattern { Name = "Kreis" } }));
        Assert.Equal("Muster 3", MousePatternRules.NextName(new[] { new MousePattern { Name = "Muster 1" }, null!, new MousePattern { Name = "Muster 2" } }));
    }

    [Fact]
    public void CleanName_uses_the_given_fallback()
    {
        Assert.Equal("Preset", ClickerSettingsRules.CleanName("  "));
        Assert.Equal("Muster 7", ClickerSettingsRules.CleanName(null, "Muster 7"));
        Assert.Equal("Kreis", ClickerSettingsRules.CleanName(" Kreis ", "Muster 7"));
    }

    [Fact]
    public void Ids_are_32_hex_characters()
    {
        Assert.True(MousePatternRules.IsValidId(NewId()));
        Assert.True(MousePatternRules.IsValidId(NewId().ToUpperInvariant()));
        Assert.False(MousePatternRules.IsValidId(null));
        Assert.False(MousePatternRules.IsValidId(""));
        Assert.False(MousePatternRules.IsValidId(NewId() + "0"));
        Assert.False(MousePatternRules.IsValidId(new string('g', 32)));
        Assert.False(MousePatternRules.IsValidId(@"..\" + new string('a', 29)));
    }

    [Fact]
    public void Presets_never_carry_or_touch_patterns()
    {
        var c = new ClickerSettings { MousePatternsArmed = true };
        c.MousePatterns.Add(new MousePattern { Id = NewId(), Name = "Kreis" });
        var preset = ClickerPresets.Capture(c, "mine");

        ClickerPresets.Apply(preset, c);

        Assert.True(c.MousePatternsArmed);
        Assert.Equal("Kreis", Assert.Single(c.MousePatterns).Name);
    }

    [Fact]
    public void Sided_recording_also_follows_the_pattern_and_record_hotkeys()
    {
        var c = new ClickerSettings();
        Assert.False(HotkeyRecorder.RecordsSided(c));

        c.PatternRecordHotkey = new ClickerHotkey { Modifiers = ClickerModifiers.RCtrl, Keys = { 0x52 } };
        Assert.True(HotkeyRecorder.RecordsSided(c));

        c.PatternRecordHotkey = MousePatternRules.DefaultRecordHotkey();
        c.MousePatterns.Add(new MousePattern { Id = NewId() });   // no hotkey
        c.MousePatterns.Add(new MousePattern { Id = NewId(), Hotkey = new ClickerHotkey { Modifiers = ClickerModifiers.LShift, Keys = { 0x46 } } });
        Assert.True(HotkeyRecorder.RecordsSided(c));

        c.SidedModifiers = false;
        Assert.False(HotkeyRecorder.RecordsSided(c));
    }
}
