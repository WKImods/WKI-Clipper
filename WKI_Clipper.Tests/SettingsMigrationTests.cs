using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class SettingsMigrationTests
{
    [Fact]
    public void Legacy_v0_gameonly_migrates_capture_and_widgets()
    {
        var s = new AppSettings
        {
            SchemaVersion = 0,
            Audio = new AudioSettings { SystemCaptureMode = AudioCaptureMode.GameOnly, GameProcessName = "arma" }
        };

        bool changed = SettingsService.MigrateIfNeeded(s);

        Assert.True(changed);
        Assert.Equal(SettingsService.CurrentSchemaVersion, s.SchemaVersion);
        Assert.Equal(CaptureMode.Window, s.Capture.Mode);
        Assert.Equal("arma", s.Capture.TargetProcessName);
        Assert.True(s.Capture.CoupleAudio);
        Assert.Equal(7, s.Widgets.Widgets.Count); // default widget layout ensured
    }

    [Fact]
    public void V1_only_needs_widget_migration_capture_untouched()
    {
        var s = new AppSettings
        {
            SchemaVersion = 1,
            Capture = new CaptureProfile { Mode = CaptureMode.Monitor, CoupleAudio = false },
            Widgets = new WidgetSettings { Widgets = new() } // empty → must be filled
        };

        bool changed = SettingsService.MigrateIfNeeded(s);

        Assert.True(changed);
        Assert.Equal(SettingsService.CurrentSchemaVersion, s.SchemaVersion);
        Assert.Equal(CaptureMode.Monitor, s.Capture.Mode);   // unchanged
        Assert.False(s.Capture.CoupleAudio);                 // unchanged
        Assert.Equal(7, s.Widgets.Widgets.Count);            // default layout ensured
    }

    [Fact]
    public void V2_gets_the_crosshair_hotkey_merged_in()
    {
        // A v2 file has a saved Hotkeys dictionary without ToggleCrosshair — the
        // new default would otherwise never reach existing users.
        var s = new AppSettings { SchemaVersion = 2 };
        s.Hotkeys.Remove(HotkeyActions.ToggleCrosshair);

        bool changed = SettingsService.MigrateIfNeeded(s);

        Assert.True(changed);
        // v2 now migrates all the way up (crosshair v3 + gif v4); the crosshair
        // hotkey is still merged in on the way.
        Assert.Equal(SettingsService.CurrentSchemaVersion, s.SchemaVersion);
        Assert.True(s.Hotkeys.ContainsKey(HotkeyActions.ToggleCrosshair));
        Assert.Equal(0x43u, s.Hotkeys[HotkeyActions.ToggleCrosshair].Key);   // 'C'
        Assert.Equal(HotkeyModifier.Control | HotkeyModifier.Alt,
                     s.Hotkeys[HotkeyActions.ToggleCrosshair].Modifiers);
        Assert.Contains(s.Widgets.Widgets, w => w.Id == WidgetId.Crosshair);
    }

    [Fact]
    public void Existing_custom_hotkeys_survive_the_v3_merge()
    {
        var s = new AppSettings { SchemaVersion = 2 };
        s.Hotkeys[HotkeyActions.SaveReplay] = new HotkeyBinding { Modifiers = HotkeyModifier.Shift, Key = 0x70 };
        s.Hotkeys.Remove(HotkeyActions.ToggleCrosshair);

        SettingsService.MigrateIfNeeded(s);

        Assert.Equal(0x70u, s.Hotkeys[HotkeyActions.SaveReplay].Key);         // untouched
        Assert.Equal(HotkeyModifier.Shift, s.Hotkeys[HotkeyActions.SaveReplay].Modifiers);
    }

    [Fact]
    public void Current_version_is_a_noop()
    {
        var s = new AppSettings { SchemaVersion = SettingsService.CurrentSchemaVersion };
        Assert.False(SettingsService.MigrateIfNeeded(s));
    }

    [Fact]
    public void Default_widget_layout_has_the_builtins()
    {
        var layout = WidgetSettings.DefaultLayout();
        Assert.Equal(7, layout.Count);
        Assert.Contains(layout, w => w.Id == WidgetId.Crosshair);
        Assert.Contains(layout, w => w.Id == WidgetId.Capture);
        Assert.Contains(layout, w => w.Id == WidgetId.Audio);
        Assert.Contains(layout, w => w.Id == WidgetId.Gallery);
        Assert.Contains(layout, w => w.Id == WidgetId.Performance);
        Assert.Contains(layout, w => w.Id == WidgetId.Settings);
    }
}
