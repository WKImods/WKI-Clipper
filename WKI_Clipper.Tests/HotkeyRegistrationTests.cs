using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Held or stuck hotkeys must not auto-repeat: without MOD_NOREPEAT Windows keeps
/// firing WM_HOTKEY, and F9 would save a clip after clip.
/// </summary>
public class HotkeyRegistrationTests
{
    private static HotkeyBinding Bind(HotkeyModifier mods, uint key = 0x78)
        => new() { Modifiers = mods, Key = key };

    [Fact]
    public void Unmodified_hotkey_is_registered_with_NoRepeat_only()
        => Assert.Equal(0x4000u, HotkeyService.RegistrationModifiers(Bind(HotkeyModifier.None)));

    [Fact]
    public void Control_Alt_keeps_both_modifiers_and_adds_NoRepeat()
        => Assert.Equal(0x4003u,
            HotkeyService.RegistrationModifiers(Bind(HotkeyModifier.Control | HotkeyModifier.Alt)));

    [Theory]
    [InlineData(HotkeyModifier.Alt, 0x4001u)]
    [InlineData(HotkeyModifier.Control, 0x4002u)]
    [InlineData(HotkeyModifier.Shift, 0x4004u)]
    [InlineData(HotkeyModifier.Win, 0x4008u)]
    [InlineData(HotkeyModifier.Alt | HotkeyModifier.Control | HotkeyModifier.Shift | HotkeyModifier.Win, 0x400Fu)]
    public void Every_modifier_combination_gets_NoRepeat(HotkeyModifier mods, uint expected)
    {
        var result = HotkeyService.RegistrationModifiers(Bind(mods));
        Assert.Equal(expected, result);
        Assert.NotEqual(0u, result & (uint)HotkeyModifier.NoRepeat);
    }

    [Fact]
    public void Already_set_NoRepeat_is_not_toggled_off()
        => Assert.Equal(0x4002u,
            HotkeyService.RegistrationModifiers(Bind(HotkeyModifier.Control | HotkeyModifier.NoRepeat)));

    [Fact]
    public void Stored_binding_is_left_untouched()
    {
        // FindCollision compares stored Modifiers for equality and the UI displays them:
        // NoRepeat is a registration detail and must never leak into settings.
        var binding = Bind(HotkeyModifier.Control | HotkeyModifier.Alt);
        HotkeyService.RegistrationModifiers(binding);
        Assert.Equal(HotkeyModifier.Control | HotkeyModifier.Alt, binding.Modifiers);
    }
}
