using System.Text.Json;
using WKI_Clipper.Models;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>The hold time in percent of the cycle or in seconds (hold-seconds design section 2).</summary>
public sealed class DutyCycleTests
{
    [Fact]
    public void Seconds_hold_is_the_seconds_value_when_the_cycle_allows_it()
        => Assert.Equal(250, DutyCycle.HoldMs(1000, 1, true, DutyUnit.Seconds, 45, 0.25), 6);

    [Fact]
    public void Seconds_hold_never_exceeds_the_interval()
        => Assert.Equal(100, DutyCycle.HoldMs(100, 10, true, DutyUnit.Seconds, 45, 0.5), 6);

    [Fact]
    public void Seconds_hold_obeys_the_rate_cap()   // 100 per second: cap 70 %
        => Assert.Equal(7, DutyCycle.HoldMs(10, 100, true, DutyUnit.Seconds, 45, 0.05), 6);

    [Fact]
    public void Percent_unit_is_unchanged()
        => Assert.Equal(18, DutyCycle.HoldMs(40, 25, true, DutyUnit.Percent, 45, 9), 6);

    [Fact]
    public void Disabled_holds_nothing_in_either_unit()
    {
        Assert.Equal(0, DutyCycle.HoldMs(1000, 1, false, DutyUnit.Seconds, 45, 0.25));
        Assert.Equal(0, DutyCycle.HoldMs(1000, 1, false, DutyUnit.Percent, 45, 0.25));
    }

    [Fact]
    public void An_unusable_seconds_value_holds_nothing()
    {
        Assert.Equal(0, DutyCycle.HoldMs(1000, 1, true, DutyUnit.Seconds, 45, 0));
        Assert.Equal(0, DutyCycle.HoldMs(1000, 1, true, DutyUnit.Seconds, 45, -1));
        Assert.Equal(0, DutyCycle.HoldMs(1000, 1, true, DutyUnit.Seconds, 45, double.NaN));
    }

    [Fact]
    public void A_cut_seconds_value_reports_what_it_really_is()
    {
        Assert.True(DutyCycle.SecondsCut(40, 25, 0.05, out var cut));
        Assert.Equal(0.04, cut, 9);
        Assert.False(DutyCycle.SecondsCut(1000, 1, 0.25, out var kept));
        Assert.Equal(0.25, kept, 9);
    }

    [Fact]
    public void A_seconds_value_cut_by_the_rate_cap_reports_the_capped_hold()   // 100 per second: 70 % of 10 ms
    {
        Assert.True(DutyCycle.SecondsCut(10, 100, 0.009, out var cut));
        Assert.Equal(0.007, cut, 9);
    }

    /// <summary>
    /// Double click: the engine shortens both presses of a pair to fit the cycle and the double-click window, but a single press (the
    /// last click of an odd limit, a click point with one click left) keeps the hold - so the two are told apart.
    /// </summary>
    [Fact]
    public void A_double_click_pair_shortens_its_presses_and_a_single_press_keeps_the_hold()
    {
        double pairHold = DoubleClickTiming.Plan(500, 300, 500).Hold1Ms / 1000.0;

        var actual = DutyCycle.ActualHolds(500, 2, 0.3, doubleClick: true, doubleClickTimeMs: 500);

        Assert.Null(actual.Single);                       // a single press: the full 0.3 s
        Assert.Equal(pairHold, actual.Pair!.Value, 9);    // inside a pair: shorter
        Assert.True(actual.Pair < 0.3);
    }

    [Fact]
    public void Without_double_click_there_is_no_pair_value()
    {
        var actual = DutyCycle.ActualHolds(40, 25, 0.05, doubleClick: false, doubleClickTimeMs: 500);

        Assert.Equal(0.04, actual.Single!.Value, 9);      // cut by the cycle
        Assert.Null(actual.Pair);
    }

    [Fact]
    public void A_cut_single_hold_and_a_shorter_pair_are_both_reported()
    {
        var actual = DutyCycle.ActualHolds(40, 25, 0.05, doubleClick: true, doubleClickTimeMs: 500);

        Assert.Equal(0.04, actual.Single!.Value, 9);
        Assert.Equal(DoubleClickTiming.Plan(40, 40, 500).Hold1Ms / 1000.0, actual.Pair!.Value, 9);
    }

    [Fact]
    public void A_double_click_pair_that_fits_reports_nothing()
    {
        var actual = DutyCycle.ActualHolds(1000, 1, 0.05, doubleClick: true, doubleClickTimeMs: 500);

        Assert.Null(actual.Single);
        Assert.Null(actual.Pair);
    }

    /// <summary>The note never shows the typed value for a hold that was cut: floored to 0.1 ms, so a tiny cut still shows as smaller.</summary>
    [Theory]
    [InlineData(0.04, 0.04)]
    [InlineData(0.0196, 0.0196)]
    [InlineData(0.049996, 0.0499)]
    [InlineData(0.00199999, 0.0019)]
    public void The_actual_hold_is_shown_floored_to_a_tenth_of_a_millisecond(double actual, double shown)
        => Assert.Equal(shown, DutyCycle.ShownSeconds(actual), 9);

    [Fact]
    public void A_seconds_value_exactly_at_the_interval_is_not_cut()
    {
        Assert.False(DutyCycle.SecondsCut(40, 25, 0.04, out var kept));
        Assert.Equal(0.04, kept, 9);
    }

    [Fact]
    public void Describe_names_value_and_unit()
    {
        Assert.Equal(ClickerNumber.Format(45) + " %", DutyCycle.Describe(DutyUnit.Percent, 45, 0.05));
        Assert.Equal(ClickerNumber.Format(0.05) + " s", DutyCycle.Describe(DutyUnit.Seconds, 45, 0.05));
    }

    [Fact]
    public void Describe_shows_seconds_with_three_decimals()
        => Assert.EndsWith("005 s", DutyCycle.Describe(DutyUnit.Seconds, 45, 0.005));

    [Theory]
    [InlineData(0.005, false, "0,005")]
    [InlineData(0.005, true, "0.005")]
    [InlineData(1.25, false, "1,25")]
    [InlineData(3600, true, "3600")]
    public void Numbers_can_be_formatted_with_three_decimals(double value, bool english, string expected)
        => Assert.Equal(expected, ClickerNumber.Format(value, english, 3));

    [Fact]
    public void The_cadence_plan_uses_the_seconds_hold()
    {
        var s = new ClickerSettings { ClickSpeed = 2, DutyCycleEnabled = true, DutyCycleUnit = DutyUnit.Seconds, DutyCycleSeconds = 0.3 };
        Assert.Equal(300, CadencePlan.From(s).HoldMs, 6);
        s.DutyCycleUnit = DutyUnit.Percent;   // 45 % of 500 ms
        Assert.Equal(225, CadencePlan.From(s).HoldMs, 6);
    }
}

public sealed class KeyboardRepeatHoldUnitTests
{
    [Fact]
    public void A_seconds_hold_never_puts_the_keyboard_into_hold_mode()
    {
        Assert.False(KeyboardRepeat.IsHoldMode(ClickerInput.Keyboard, true, DutyUnit.Seconds, 100, DutyMode.Click));
        Assert.True(KeyboardRepeat.IsHoldMode(ClickerInput.Keyboard, true, DutyUnit.Percent, 100, DutyMode.Click));
        Assert.True(KeyboardRepeat.IsHoldMode(ClickerInput.Keyboard, true, DutyUnit.Seconds, 100, DutyMode.Hold));   // Duty Mode "Hold" still holds
    }

    [Fact]
    public void The_settings_overload_follows_the_unit()
    {
        var s = new ClickerSettings { InputType = ClickerInput.Keyboard, KeyboardVk = 0x41, DutyCycleEnabled = true, DutyCyclePercent = 100 };
        Assert.True(KeyboardRepeat.IsHoldMode(s));
        s.DutyCycleUnit = DutyUnit.Seconds;
        Assert.False(KeyboardRepeat.IsHoldMode(s));
    }
}

public sealed class ClickerSettingsRulesDutyUnitTests
{
    [Fact]
    public void Normalize_repairs_unit_and_seconds()
    {
        Assert.Equal(DutyUnit.Percent, ClickerSettingsRules.Normalize(new ClickerSettings { DutyCycleUnit = (DutyUnit)7 }).DutyCycleUnit);
        Assert.Equal(DutyUnit.Seconds, ClickerSettingsRules.Normalize(new ClickerSettings { DutyCycleUnit = DutyUnit.Seconds }).DutyCycleUnit);
        Assert.Equal(0.05, ClickerSettingsRules.Normalize(new ClickerSettings { DutyCycleSeconds = double.NaN }).DutyCycleSeconds);
        Assert.Equal(0.05, ClickerSettingsRules.Normalize(new ClickerSettings { DutyCycleSeconds = double.PositiveInfinity }).DutyCycleSeconds);
        Assert.Equal(0.05, ClickerSettingsRules.Normalize(new ClickerSettings { DutyCycleSeconds = -2 }).DutyCycleSeconds);
        Assert.Equal(0.001, ClickerSettingsRules.Normalize(new ClickerSettings { DutyCycleSeconds = 0 }).DutyCycleSeconds);
        Assert.Equal(3600, ClickerSettingsRules.Normalize(new ClickerSettings { DutyCycleSeconds = 5000 }).DutyCycleSeconds);
        Assert.Equal(1.5, ClickerSettingsRules.Normalize(new ClickerSettings { DutyCycleSeconds = 1.5 }).DutyCycleSeconds);
    }

    [Fact]
    public void Normalize_keeps_three_decimals_of_the_seconds()
    {
        Assert.Equal(0.123, ClickerSettingsRules.Normalize(new ClickerSettings { DutyCycleSeconds = 0.12345 }).DutyCycleSeconds);
        Assert.Equal(0.001, ClickerSettingsRules.Normalize(new ClickerSettings { DutyCycleSeconds = 0.0004 }).DutyCycleSeconds);
    }

    [Fact]
    public void Settings_without_the_new_fields_load_in_percent()
    {
        var c = ClickerSettingsRules.Normalize(JsonSerializer.Deserialize<ClickerSettings>("{}")!);
        Assert.Equal(DutyUnit.Percent, c.DutyCycleUnit);
        Assert.Equal(0.05, c.DutyCycleSeconds);
        Assert.Equal(DutyCycle.DefaultSeconds, new ClickerSettings().DutyCycleSeconds);

        var app = JsonSerializer.Deserialize<AppSettings>("{\"SchemaVersion\":12,\"AutoClicker\":{\"Clicker\":{\"DutyCyclePercent\":60}}}")!;
        var loaded = ClickerSettingsRules.Normalize(app.AutoClicker.Clicker);
        Assert.Equal((DutyUnit.Percent, 0.05, 60.0), (loaded.DutyCycleUnit, loaded.DutyCycleSeconds, loaded.DutyCyclePercent));
    }

    [Fact]
    public void Unit_and_seconds_round_trip_through_json()
    {
        var json = JsonSerializer.Serialize(new ClickerSettings { DutyCycleUnit = DutyUnit.Seconds, DutyCycleSeconds = 0.25 });
        Assert.Contains("\"DutyCycleUnit\":\"Seconds\"", json);
        var loaded = ClickerSettingsRules.Normalize(JsonSerializer.Deserialize<ClickerSettings>(json)!);
        Assert.Equal((DutyUnit.Seconds, 0.25), (loaded.DutyCycleUnit, loaded.DutyCycleSeconds));
    }
}
