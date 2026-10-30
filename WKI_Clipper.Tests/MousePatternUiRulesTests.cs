using System.Collections.Generic;
using WKI_Clipper.Models;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>The words of the "Mausbewegung" sub-tab and the patterns' share of the status row (mouse-patterns design 4.5-4.8).</summary>
public sealed class MousePatternUiRulesTests
{
    private const string Circle = "0123456789abcdef0123456789abcdef";
    private const string Wave = "fedcba9876543210fedcba9876543210";
    private const string Line = "00112233445566778899aabbccddeeff";

    private static ClickerSettings Settings(bool patternsArmed = true, bool clickerArmed = false, int active = 2, int inactive = 1)
    {
        var c = new ClickerSettings { Armed = clickerArmed, MousePatternsArmed = patternsArmed, MousePatterns = new List<MousePattern>() };
        var ids = new[] { Circle, Wave, Line };
        var names = new[] { "Kreis", "Welle", "Linie" };
        for (int i = 0; i < active + inactive; i++)
            c.MousePatterns.Add(new MousePattern { Id = ids[i], Name = names[i], Active = i < active, Revision = 1 });
        return c;
    }

    private static ClickerState Ready => ClickerState.Off with { Phase = ClickerPhase.Ready, PatternsArmed = true };

    [Theory]
    [InlineData("off", "Mausbewegung ist aus - oben 'Mausbewegung bereit' einschalten")]
    [InlineData("starting", "Startet …")]
    [InlineData("helper-not-yet-up", "Startet …")]
    [InlineData("error", "Der AutoClicker-Helfer wurde unerwartet beendet")]
    [InlineData("locked", "Gesperrt: Arma auf der Blacklist")]
    [InlineData("ready", "Bereit · 2 Muster aktiv")]
    [InlineData("ready-one", "Bereit · 1 Muster aktiv")]
    [InlineData("ready-none-active", "Bereit · kein Muster aktiv")]
    [InlineData("ready-no-patterns", "Bereit · noch keine Muster")]
    [InlineData("recording", "Aufnahme läuft · Strg+Alt+R beendet")]
    [InlineData("playing", "Spielt: Kreis")]
    [InlineData("playing-two", "Spielt: Kreis, Welle")]
    [InlineData("playing-deleted", "Ein Muster spielt")]
    [InlineData("blocked", "Gestoppt: game.exe läuft als Administrator und nimmt keine Bewegung an")]
    [InlineData("blocked-unknown", "Gestoppt: ein Administrator-Fenster nimmt keine Bewegung an")]
    [InlineData("recording-while-playing", "Aufnahme läuft · Strg+Alt+R beendet")]
    [InlineData("clicker-running", "Bereit · 2 Muster aktiv")]
    public void Status_line_texts(string scenario, string expected)
    {
        var (state, settings) = scenario switch
        {
            "off" => (ClickerState.Off, Settings(patternsArmed: false)),
            "starting" => (ClickerState.Off with { Phase = ClickerPhase.Starting }, Settings()),
            "helper-not-yet-up" => (ClickerState.Off, Settings()),
            "error" => (ClickerState.Off with { Phase = ClickerPhase.Error, Fault = ClickerFault.Crashed }, Settings()),
            "locked" => (Ready with { Locked = true, LockedBy = "Arma" }, Settings()),
            "ready" => (Ready, Settings()),
            "ready-one" => (Ready, Settings(active: 1, inactive: 2)),
            "ready-none-active" => (Ready, Settings(active: 0, inactive: 2)),
            "ready-no-patterns" => (Ready, Settings(active: 0, inactive: 0)),
            "recording" => (Ready with { Recording = true }, Settings()),
            "playing" => (Ready with { Playing = Circle }, Settings()),
            "playing-two" => (Ready with { Playing = Circle + "," + Wave.ToUpperInvariant() }, Settings()),
            "playing-deleted" => (Ready with { Playing = "11111111111111111111111111111111" }, Settings()),
            "blocked" => (Ready with { PatternsBlockedBy = "game.exe" }, Settings()),
            "blocked-unknown" => (Ready with { PatternsBlockedBy = "?" }, Settings()),
            "recording-while-playing" => (Ready with { Recording = true, Playing = Circle }, Settings()),
            "clicker-running" => (Ready with { Phase = ClickerPhase.Running, Cps = 25 }, Settings(clickerArmed: true)),
            _ => throw new System.ArgumentException(scenario)
        };

        Assert.Equal(expected, MousePatternUiRules.StatusLine(state, settings));
    }

    [Fact]
    public void The_clicker_pill_and_Start_are_off_while_only_the_patterns_keep_the_helper_running()
    {
        var onlyPatterns = Settings(clickerArmed: false);
        var both = Settings(clickerArmed: true);

        var clickerSide = MousePatternUiRules.ClickerSide(Ready, onlyPatterns);
        Assert.Equal("Aus", ClickerStateText.Pill(clickerSide));
        Assert.Equal(ClickerPillTone.Muted, ClickerStatusRules.Tone(clickerSide));
        Assert.False(ClickerStatusRules.StartButton(clickerSide, canStart: true).Enabled);

        Assert.Equal(Ready, MousePatternUiRules.ClickerSide(Ready, both));
        Assert.True(ClickerStatusRules.StartButton(MousePatternUiRules.ClickerSide(Ready, both), canStart: true).Enabled);
    }

    [Fact]
    public void An_error_and_a_run_still_show_on_the_clicker_side_with_the_clicker_off()
    {
        var error = ClickerState.Off with { Phase = ClickerPhase.Error, Fault = ClickerFault.Crashed };
        var running = Ready with { Phase = ClickerPhase.Running };   // switched off a moment ago: it can still be stopped

        Assert.Same(error, MousePatternUiRules.ClickerSide(error, Settings(clickerArmed: false)));
        Assert.Same(running, MousePatternUiRules.ClickerSide(running, Settings(clickerArmed: false)));
    }

    [Fact]
    public void The_second_switch_names_the_record_hotkey()
    {
        var c = Settings();
        Assert.Equal("Mausbewegung bereit (Aufnahme: Strg+Alt+R)", MousePatternUiRules.ArmedLabel(c));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "1 Muster-Hinweis - siehe Mausbewegung")]
    [InlineData(3, "3 Muster-Hinweise - siehe Mausbewegung")]
    public void The_status_row_counts_the_pattern_warnings(int count, string? expected)
        => Assert.Equal(expected, MousePatternUiRules.WarningSummary(count));

    [Fact]
    public void Only_warnings_count_for_the_status_row_not_info_lines()
    {
        var lines = new[]
        {
            new PatternWarning(Circle, "a", Info: false),
            new PatternWarning(Circle, "b", Info: true),
            new PatternWarning(Wave, "c", Info: false),
        };

        Assert.Equal(2, MousePatternUiRules.WarningCount(lines));
    }

    [Fact]
    public void Empty_state_and_target_line_name_the_record_hotkey()
    {
        var c = Settings();

        Assert.Equal("Noch keine Muster. Strg+Alt+R drücken, Bewegung machen, nochmal drücken.", MousePatternUiRules.EmptyText(c));
        Assert.Equal("Nächste Aufnahme ersetzt 'Kreis' - Strg+Alt+R im Spiel drücken", MousePatternUiRules.TargetLine("Kreis", c));
    }

    [Fact]
    public void Card_texts()
    {
        Assert.Equal("2,4 s", MousePatternUiRules.Duration(2400));
        Assert.Equal("0 s", MousePatternUiRules.Duration(double.NaN));
        Assert.Equal("Nur in game.exe", MousePatternUiRules.OnlyIn("game.exe"));
        Assert.Equal("Automatisch", MousePatternUiRules.ReplayName(PatternReplay.Auto));
        Assert.Equal("Zeiger", MousePatternUiRules.ReplayName(PatternReplay.Pointer));
        Assert.Equal("Spiel (roh)", MousePatternUiRules.ReplayName(PatternReplay.Raw));
        Assert.Equal("Halten", MousePatternUiRules.ModeName(ActivationMode.Hold));
        Assert.Equal("Umschalten", MousePatternUiRules.ModeName(ActivationMode.Toggle));
    }
}
