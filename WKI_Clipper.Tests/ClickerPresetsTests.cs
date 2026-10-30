using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WKI_Clipper.Models;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class ClickerPresetsTests
{
    // ---- factory presets ----

    [Fact]
    public void There_are_three_factory_presets_with_the_numbers_of_the_design()
    {
        var f = ClickerPresets.Factory();
        Assert.Equal(3, f.Count);

        Assert.Equal("Standard", f[0].Name);
        Assert.Equal((25.0, 45.0, 35.0, false), (f[0].ClickSpeed, f[0].DutyCyclePercent, f[0].SpeedRandomizationPercent, f[0].ClickLimitEnabled));

        Assert.Equal("Rapid Fire", f[1].Name);
        Assert.Equal((100.0, 20.0, 15.0), (f[1].ClickSpeed, f[1].DutyCyclePercent, f[1].SpeedRandomizationPercent));
        Assert.True(f[1].ClickLimitEnabled);
        Assert.Equal(5000, f[1].ClickLimit);

        Assert.Equal("Präzision", f[2].Name);
        Assert.Equal((10.0, 60.0, 10.0, false), (f[2].ClickSpeed, f[2].DutyCyclePercent, f[2].SpeedRandomizationPercent, f[2].ClickLimitEnabled));
    }

    [Fact]
    public void Factory_presets_survive_normalization_unchanged()
    {
        foreach (var p in ClickerPresets.Factory())
        {
            var before = JsonSerializer.Serialize(p);
            ClickerSettingsRules.NormalizePreset(p);
            Assert.Equal(before, JsonSerializer.Serialize(p));
        }
    }

    [Fact]
    public void Factory_hands_out_fresh_instances()
    {
        ClickerPresets.Factory()[0].ClickSpeed = 999;
        Assert.Equal(25, ClickerPresets.Factory()[0].ClickSpeed);
    }

    // ---- apply ----

    [Fact]
    public void Apply_copies_the_preset_fields_and_leaves_the_rest_alone()
    {
        var s = new ClickerSettings
        {
            Armed = true,
            Hotkey = new ClickerHotkey { Modifiers = ClickerModifiers.LCtrl, Keys = { 0x46 } },
            ActivationMode = ActivationMode.Hold,
            StrictModifiers = true,
            MasterHotkey = new ClickerHotkey { Keys = { 0x7B } },
            ShowActiveIndicator = false,
            SubTab = "presets"
        };
        s.Presets.Add(new ClickerPreset { Name = "keep" });
        s.Stats.AddRun(new RunRecord { Clicks = 5, Seconds = 1 });

        ClickerPresets.Apply(ClickerPresets.Factory()[1], s);

        Assert.Equal(100, s.ClickSpeed);
        Assert.Equal(20, s.DutyCyclePercent);
        Assert.Equal(15, s.SpeedRandomizationPercent);
        Assert.True(s.ClickLimitEnabled);
        Assert.Equal(5000, s.ClickLimit);
        // untouched
        Assert.True(s.Armed);
        Assert.Equal(new[] { 0x46 }, s.Hotkey.Keys);
        Assert.Equal(ActivationMode.Hold, s.ActivationMode);
        Assert.True(s.StrictModifiers);
        Assert.NotNull(s.MasterHotkey);
        Assert.False(s.ShowActiveIndicator);
        Assert.Equal("presets", s.SubTab);
        Assert.Equal("keep", Assert.Single(s.Presets).Name);
        Assert.Equal(1, s.Stats.Sessions);
    }

    [Fact]
    public void ApplyFactory_keeps_zones_points_whitelist_and_output()
    {
        var s = new ClickerSettings
        {
            WhitelistEnabled = true,
            ZonesEnabled = true,
            ClickPointsEnabled = true,
            InputType = ClickerInput.Keyboard,
            KeyboardVk = 0x45,
            DoubleClick = false
        };
        s.Whitelist.Add(new BlacklistEntry { ExeName = "cs2.exe", DisplayName = "CS2" });
        s.Zones.Add(new ClickerZone { Id = "a", W = 10, H = 10 });
        s.Zones.Add(new ClickerZone { Id = "b", W = 20, H = 20 });
        s.ClickPoints.Add(new ClickPoint { X = 1, Y = 2, Clicks = 3, Radius = 4 });

        ClickerPresets.ApplyFactory(ClickerPresets.Factory()[1], s);

        Assert.Equal((100.0, 20.0, 15.0), (s.ClickSpeed, s.DutyCyclePercent, s.SpeedRandomizationPercent));
        Assert.True(s.DutyCycleEnabled);
        Assert.True(s.SpeedRandomizationEnabled);
        Assert.True(s.ClickLimitEnabled);
        Assert.Equal(5000, s.ClickLimit);

        Assert.True(s.WhitelistEnabled);
        Assert.Equal("cs2.exe", Assert.Single(s.Whitelist).ExeName);
        Assert.True(s.ZonesEnabled);
        Assert.Equal(2, s.Zones.Count);
        Assert.True(s.ClickPointsEnabled);
        Assert.Single(s.ClickPoints);
        Assert.Equal(ClickerInput.Keyboard, s.InputType);
        Assert.Equal(0x45, s.KeyboardVk);
    }

    [Fact]
    public void ApplyFactory_still_normalizes_against_the_current_setup()
    {
        var s = new ClickerSettings { DoubleClick = true };
        ClickerPresets.ApplyFactory(ClickerPresets.Factory()[1], s);
        Assert.Equal(10, s.ClickSpeed);   // double click caps at 10 per second
    }

    [Fact]
    public void Apply_deep_copies_the_lists()
    {
        var preset = new ClickerPreset
        {
            Name = "p",
            Zones = { new ClickerZone { Id = "z", W = 10, H = 10 } },
            ClickPoints = { new ClickPoint { X = 1, Y = 2, Clicks = 3, Radius = 4 } },
            Whitelist = { new BlacklistEntry { ExeName = "a.exe", DisplayName = "A" } }
        };
        var s = new ClickerSettings();

        ClickerPresets.Apply(preset, s);
        s.Zones[0].W = 99;
        s.ClickPoints[0].X = 99;
        s.Whitelist[0].DisplayName = "changed";

        Assert.Equal(10, preset.Zones[0].W);
        Assert.Equal(1, preset.ClickPoints[0].X);
        Assert.Equal("A", preset.Whitelist[0].DisplayName);
    }

    [Fact]
    public void Apply_normalizes_a_preset_that_is_wrong_for_the_current_setup()
    {
        var s = new ClickerSettings();
        ClickerPresets.Apply(new ClickerPreset { Name = "x", ClickSpeed = 400, DoubleClick = true }, s);
        Assert.Equal(10, s.ClickSpeed);   // double click caps at 10 per second
    }

    // ---- capture / save as ----

    [Fact]
    public void SaveAs_stores_the_current_fields_under_the_name()
    {
        var s = new ClickerSettings { ClickSpeed = 77, MouseButton = ClickerMouseButton.Right, ClickPointsEnabled = true };
        s.ClickPoints.Add(new ClickPoint { X = 5, Y = 6, Clicks = 2 });

        var saved = ClickerPresets.SaveAs(s, "  My preset ");

        Assert.NotNull(saved);
        Assert.Equal("My preset", saved!.Name);
        Assert.Same(saved, Assert.Single(s.Presets));
        Assert.Equal(77, saved.ClickSpeed);
        Assert.Equal(ClickerMouseButton.Right, saved.MouseButton);
        Assert.True(saved.ClickPointsEnabled);
        // a snapshot, not a view
        s.ClickPoints[0].X = 500;
        Assert.Equal(5, saved.ClickPoints[0].X);
    }

    [Fact]
    public void SaveAs_never_stores_hotkeys_or_stats()
    {
        var s = new ClickerSettings { Armed = true, MasterHotkey = new ClickerHotkey { Keys = { 0x7B } } };
        s.Stats.AddRun(new RunRecord { Clicks = 9 });

        var json = JsonSerializer.Serialize(ClickerPresets.SaveAs(s, "n"));

        foreach (var forbidden in new[] { "Hotkey", "Armed", "Stats", "SubTab", "Master", "Activation", "ShowActiveIndicator", "Presets", "Strict" })
            Assert.DoesNotContain(forbidden, json);
    }

    [Fact]
    public void SaveAs_with_an_existing_name_overwrites_it_case_insensitively()
    {
        var s = new ClickerSettings { ClickSpeed = 10 };
        ClickerPresets.SaveAs(s, "Fast");
        s.ClickSpeed = 20;

        var again = ClickerPresets.SaveAs(s, "FAST");

        var only = Assert.Single(s.Presets);
        Assert.Same(again, only);
        Assert.Equal("FAST", only.Name);
        Assert.Equal(20, only.ClickSpeed);
    }

    [Fact]
    public void SaveAs_refuses_a_blank_name_and_a_full_list()
    {
        var s = new ClickerSettings();
        Assert.Null(ClickerPresets.SaveAs(s, ""));
        Assert.Null(ClickerPresets.SaveAs(s, "   "));
        Assert.Null(ClickerPresets.SaveAs(s, null));

        for (int i = 0; i < 100; i++) Assert.NotNull(ClickerPresets.SaveAs(s, "p" + i));
        Assert.Null(ClickerPresets.SaveAs(s, "one too many"));
        Assert.Equal(100, s.Presets.Count);
        Assert.NotNull(ClickerPresets.SaveAs(s, "P5"));   // an overwrite still works on a full list
    }

    [Fact]
    public void SaveAs_cuts_a_long_name_to_40_characters()
    {
        var s = new ClickerSettings();
        Assert.Equal(40, ClickerPresets.SaveAs(s, new string('n', 90))!.Name.Length);
    }

    // ---- rename / delete ----

    [Fact]
    public void Rename_changes_the_name_unless_it_is_blank_or_taken()
    {
        var presets = new List<ClickerPreset> { new() { Name = "a" }, new() { Name = "b" } };

        Assert.True(ClickerPresets.Rename(presets, "a", " alpha "));
        Assert.Equal("alpha", presets[0].Name);
        Assert.False(ClickerPresets.Rename(presets, "alpha", "B"));      // taken by "b"
        Assert.False(ClickerPresets.Rename(presets, "alpha", "  "));
        Assert.False(ClickerPresets.Rename(presets, "missing", "x"));
        Assert.True(ClickerPresets.Rename(presets, "ALPHA", "Alpha"));   // case change of its own name
        Assert.Equal("Alpha", presets[0].Name);
        Assert.True(ClickerPresets.Rename(presets, "b", new string('q', 70)));
        Assert.Equal(40, presets[1].Name.Length);
    }

    [Fact]
    public void Delete_removes_the_named_preset()
    {
        var presets = new List<ClickerPreset> { new() { Name = "a" }, new() { Name = "b" } };
        Assert.True(ClickerPresets.Delete(presets, "A"));
        Assert.Equal("b", Assert.Single(presets).Name);
        Assert.False(ClickerPresets.Delete(presets, "a"));
    }

    // ---- export / import ----

    [Fact]
    public void Export_and_import_round_trip()
    {
        var s = new ClickerSettings
        {
            ClickSpeed = 33.5, ClickUnit = RateUnit.Minute, DutyMode = DutyMode.Hold, InputType = ClickerInput.Keyboard, KeyboardVk = 0x42,
            KeyCase = KeyCase.Upper, TimeLimitEnabled = true, TimeLimit = 2.5, TimeLimitUnit = TimeLimitUnit.Minutes,
            WhitelistEnabled = true, StopWhenComplete = true, ZonesEnabled = true
        };
        s.Zones.Add(new ClickerZone { Id = "z1", Name = "N", X = -100, Y = 20, W = 30, H = 40, Action = ZoneAction.Start, Enabled = false });
        s.ClickPoints.Add(new ClickPoint { X = -1, Y = -2, Clicks = 7, Radius = 8 });
        s.Whitelist.Add(new BlacklistEntry { ExeName = "tool.exe", DisplayName = "Tool" });
        var original = ClickerPresets.Capture(s, "Round");

        var json = ClickerPresets.Export(original);
        var target = new List<ClickerPreset>();
        var result = ClickerPresets.Import(target, json);

        Assert.True(result.Success);
        Assert.Equal(PresetImportError.None, result.Error);
        Assert.Same(result.Preset, Assert.Single(target));
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(result.Preset));
    }

    [Fact]
    public void The_export_file_names_its_version()
    {
        var json = ClickerPresets.Export(ClickerPresets.Factory()[0]);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal("Standard", doc.RootElement.GetProperty("Preset").GetProperty("Name").GetString());
    }

    [Fact]
    public void Import_gives_a_taken_name_a_suffix()
    {
        var target = new List<ClickerPreset> { new() { Name = "Standard" } };
        var json = ClickerPresets.Export(ClickerPresets.Factory()[0]);

        var r = ClickerPresets.Import(target, json);

        Assert.True(r.Success);
        Assert.Equal("Standard (2)", r.Preset!.Name);
        Assert.Equal(2, target.Count);
    }

    [Fact]
    public void Import_normalizes_wild_values()
    {
        const string json = """
            {"Version":1,"Preset":{"Name":"  wild  ","ClickSpeed":123456,"DutyCyclePercent":-9,"ClickLimit":0,
             "Zones":[{"Id":"","W":0,"H":5},{"Id":"ok","W":5,"H":5}],"ClickPoints":[{"Clicks":99999999,"Radius":-1}]}}
            """;
        var target = new List<ClickerPreset>();

        var r = ClickerPresets.Import(target, json);

        Assert.True(r.Success);
        var p = r.Preset!;
        Assert.Equal("wild", p.Name);
        Assert.Equal(500, p.ClickSpeed);
        Assert.Equal(45, p.DutyCyclePercent);
        Assert.Equal(1, p.ClickLimit);
        Assert.Equal("ok", Assert.Single(p.Zones).Id);
        Assert.Equal((999_999, 0), (p.ClickPoints[0].Clicks, p.ClickPoints[0].Radius));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"Version\":1,")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    [InlineData("{\"Version\":1,\"Preset\":{\"ClickSpeed\":\"fast\"}}")]
    [InlineData("{\"Version\":1,\"Preset\":{\"ClickUnit\":\"Fortnight\"}}")]
    public void Import_rejects_garbage(string json)
    {
        var target = new List<ClickerPreset>();
        var r = ClickerPresets.Import(target, json);
        Assert.False(r.Success);
        Assert.Equal(PresetImportError.InvalidJson, r.Error);
        Assert.Empty(target);
    }

    [Theory]
    [InlineData("{\"Version\":2,\"Preset\":{\"Name\":\"x\"}}")]
    [InlineData("{\"Version\":0,\"Preset\":{\"Name\":\"x\"}}")]
    [InlineData("{\"Preset\":{\"Name\":\"x\"}}")]
    public void Import_rejects_an_unknown_version(string json)
    {
        var target = new List<ClickerPreset>();
        var r = ClickerPresets.Import(target, json);
        Assert.Equal(PresetImportError.UnknownVersion, r.Error);
        Assert.Empty(target);
    }

    [Fact]
    public void Import_rejects_a_file_without_a_preset()
    {
        var target = new List<ClickerPreset>();
        Assert.Equal(PresetImportError.MissingPreset, ClickerPresets.Import(target, "{\"Version\":1}").Error);
        Assert.Equal(PresetImportError.MissingPreset, ClickerPresets.Import(target, "{\"Version\":1,\"Preset\":null}").Error);
        Assert.Empty(target);
    }

    [Fact]
    public void Import_rejects_a_huge_text_and_a_full_list()
    {
        var target = new List<ClickerPreset>();
        Assert.Equal(PresetImportError.TooLarge, ClickerPresets.Import(target, new string(' ', ClickerPresets.MaxImportLength) + "{}").Error);

        for (int i = 0; i < 100; i++) target.Add(new ClickerPreset { Name = "p" + i });
        var r = ClickerPresets.Import(target, ClickerPresets.Export(ClickerPresets.Factory()[0]));
        Assert.Equal(PresetImportError.ListFull, r.Error);
        Assert.Equal(100, target.Count);
    }

    [Fact]
    public void Import_survives_NaN_literals_by_falling_back_to_defaults()
    {
        var r = ClickerPresets.Import(new List<ClickerPreset>(), "{\"Version\":1,\"Preset\":{\"Name\":\"n\",\"ClickSpeed\":\"NaN\"}}");
        Assert.True(r.Success);
        Assert.Equal(25, r.Preset!.ClickSpeed);
    }

    [Fact]
    public void An_exported_factory_preset_imports_back_equal()
    {
        foreach (var f in ClickerPresets.Factory())
        {
            var r = ClickerPresets.Import(new List<ClickerPreset>(), ClickerPresets.Export(f));
            Assert.Equal(JsonSerializer.Serialize(f), JsonSerializer.Serialize(r.Preset));
        }
    }
}
