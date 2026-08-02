using System.Text.Json;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class StreamingSettingsTests
{
    // ---- migration v5 ----

    [Fact]
    public void V4_gets_streaming_section_and_widget()
    {
        var s = new AppSettings { SchemaVersion = 4 };
        s.Widgets.Widgets.RemoveAll(w => w.Id == WidgetId.Streaming);

        bool changed = SettingsService.MigrateIfNeeded(s);

        Assert.True(changed);
        Assert.Equal(5, s.SchemaVersion);
        Assert.NotNull(s.Streaming);
        Assert.Empty(s.Streaming.Buttons);                    // default = empty grid
        Assert.Equal(4455, s.Streaming.Obs.Port);
        Assert.Contains(s.Widgets.Widgets, w => w.Id == WidgetId.Streaming);
    }

    [Fact]
    public void Default_layout_contains_streaming_widget()
        => Assert.Contains(WidgetSettings.DefaultLayout(), w => w.Id == WidgetId.Streaming);

    // ---- DPAPI secret protection ----

    [Fact]
    public void Protect_roundtrip_recovers_the_plaintext()
    {
        var protectedB64 = SecretProtector.Protect("Treeflip321.");
        Assert.NotNull(protectedB64);
        Assert.DoesNotContain("Treeflip", protectedB64);      // never plaintext
        Assert.Equal("Treeflip321.", SecretProtector.Unprotect(protectedB64));
    }

    [Fact]
    public void Protect_null_or_empty_yields_null()
    {
        Assert.Null(SecretProtector.Protect(null));
        Assert.Null(SecretProtector.Protect(""));
        Assert.Null(SecretProtector.Unprotect(null));
        Assert.Null(SecretProtector.Unprotect(""));
    }

    [Fact]
    public void Unprotect_garbage_returns_null_instead_of_throwing()
    {
        Assert.Null(SecretProtector.Unprotect("not-base64!!"));
        Assert.Null(SecretProtector.Unprotect("QUJDREVGRw=="));  // valid base64, invalid DPAPI blob
    }

    // ---- button config serialization ----

    [Fact]
    public void Button_config_survives_a_json_roundtrip()
    {
        var s = new AppSettings();
        s.Streaming.Buttons.Add(new StreamButtonConfig
        {
            Slot = 3,
            Label = "Arma",
            Color = "#2E7D32",
            Action = StreamAction.SetScene,
            Param = "Arma Reforger",
            Hotkey = new HotkeyBinding { Modifiers = HotkeyModifier.Control | HotkeyModifier.Alt, Key = 0x31 }
        });

        var json = JsonSerializer.Serialize(s);
        var back = JsonSerializer.Deserialize<AppSettings>(json)!;

        var b = Assert.Single(back.Streaming.Buttons);
        Assert.Equal(3, b.Slot);
        Assert.Equal(StreamAction.SetScene, b.Action);
        Assert.Equal("Arma Reforger", b.Param);
        Assert.Equal(0x31u, b.Hotkey!.Key);
        Assert.Contains("SetScene", json);                    // enum stored as string, not int
    }

    // ---- central hotkey collision check (both directions) ----

    private static AppSettings WithTile(int slot, string label, HotkeyModifier mods, uint key)
    {
        var s = new AppSettings();
        s.Streaming.Buttons.Add(new StreamButtonConfig
        {
            Slot = slot, Label = label,
            Hotkey = new HotkeyBinding { Modifiers = mods, Key = key }
        });
        return s;
    }

    [Fact]
    public void Collision_tile_vs_clipper_action_is_detected()
    {
        var s = new AppSettings();   // defaults include F9 = SaveReplay
        var f9 = new HotkeyBinding { Modifiers = HotkeyModifier.None, Key = 0x78 };
        Assert.NotNull(HotkeyService.FindCollision(s, f9));
    }

    [Fact]
    public void Collision_clipper_vs_tile_is_detected_the_other_way_round()
    {
        var s = WithTile(2, "Arma", HotkeyModifier.Control | HotkeyModifier.Shift, 0x53); // Ctrl+Shift+S
        var candidate = new HotkeyBinding { Modifiers = HotkeyModifier.Control | HotkeyModifier.Shift, Key = 0x53 };
        var clash = HotkeyService.FindCollision(s, candidate, excludeAction: HotkeyActions.SaveReplay);
        Assert.NotNull(clash);
        Assert.Contains("Arma", clash);
    }

    [Fact]
    public void Collision_excludes_the_entry_being_edited()
    {
        var s = WithTile(2, "Arma", HotkeyModifier.Control, 0x31);
        var same = new HotkeyBinding { Modifiers = HotkeyModifier.Control, Key = 0x31 };
        Assert.Null(HotkeyService.FindCollision(s, same, excludeSlot: 2));       // editing itself
        Assert.NotNull(HotkeyService.FindCollision(s, same, excludeSlot: 5));    // another tile
    }

    [Fact]
    public void Free_combo_has_no_collision()
    {
        var s = new AppSettings();
        var free = new HotkeyBinding { Modifiers = HotkeyModifier.Control | HotkeyModifier.Alt, Key = 0x39 }; // Ctrl+Alt+9
        Assert.Null(HotkeyService.FindCollision(s, free));
    }

    [Fact]
    public void DescribeAction_translates_stream_button_ids_to_labels()
    {
        var s = WithTile(7, "OBS Replay", HotkeyModifier.Control, 0x52);
        Assert.Contains("OBS Replay", HotkeyService.DescribeAction(s, "StreamButton:7"));
        Assert.Equal("SaveReplay", HotkeyService.DescribeAction(s, "SaveReplay"));  // unknown → passthrough
    }

    [Fact]
    public void Stream_button_hotkey_prefix_roundtrips_the_slot()
    {
        const string action = HotkeyService.StreamButtonPrefix + "7";
        Assert.StartsWith(HotkeyService.StreamButtonPrefix, action);
        Assert.Equal(7, int.Parse(action.Substring(HotkeyService.StreamButtonPrefix.Length)));
    }
}
