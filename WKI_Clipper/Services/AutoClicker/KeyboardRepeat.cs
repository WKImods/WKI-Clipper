using System;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>Delay before the first repeat and the time between repeats, both from the Windows keyboard settings.</summary>
public readonly record struct RepeatPlan(double DelayMs, double IntervalMs);

/// <summary>
/// The key-hold ("repeat") mode of the keyboard clicker (design section 4): key down, wait the Windows repeat delay, then key-down
/// repeats at the Windows repeat rate. The engine reads SPI_GETKEYBOARDDELAY / SPI_GETKEYBOARDSPEED and feeds the raw values here.
/// </summary>
public static class KeyboardRepeat
{
    public const int MaxDelayIndex = 3;
    public const int MaxSpeed = 31;
    public const double MinRateHz = 2.5;
    public const double MaxRateHz = 30;

    /// <summary>SPI keyboard delay index 0..3 to 250, 500, 750 or 1000 ms; out-of-range values are clamped.</summary>
    public static double DelayMs(int spiDelayIndex) => (Math.Clamp(spiDelayIndex, 0, MaxDelayIndex) + 1) * 250.0;

    /// <summary>SPI keyboard speed 0..31 to 2.5..30 repeats per second, linear.</summary>
    public static double RateHz(int spiSpeed) => MinRateHz + Math.Clamp(spiSpeed, 0, MaxSpeed) * ((MaxRateHz - MinRateHz) / MaxSpeed);

    public static double IntervalMs(int spiSpeed) => 1000.0 / RateHz(spiSpeed);

    public static RepeatPlan Plan(int spiDelayIndex, int spiSpeed) => new(DelayMs(spiDelayIndex), IntervalMs(spiSpeed));

    /// <summary>How many repeat key-downs (after the first press) are due this long after the first press.</summary>
    public static int RepeatsDueBy(double msSinceFirstPress, RepeatPlan plan)
    {
        if (msSinceFirstPress < plan.DelayMs) return 0;
        return 1 + (int)Math.Floor((msSinceFirstPress - plan.DelayMs) / plan.IntervalMs);
    }

    /// <summary>When repeat number <paramref name="repeatIndex"/> (0 = the first repeat) is due, measured from the first press.</summary>
    public static double RepeatTimeMs(int repeatIndex, RepeatPlan plan) => plan.DelayMs + repeatIndex * plan.IntervalMs;

    /// <summary>
    /// Keyboard output holds the key instead of tapping it: Duty Mode "Hold", or a duty cycle switched on at 100 %. The raw percent
    /// decides (not the rate-capped one): 100 % is the user's explicit "keep it down". The mouse never uses key repeats.
    /// </summary>
    public static bool IsHoldMode(ClickerInput input, bool dutyEnabled, double dutyPercent, DutyMode dutyMode)
        => input == ClickerInput.Keyboard && (dutyMode == DutyMode.Hold || (dutyEnabled && dutyPercent >= 100));

    public static bool IsHoldMode(ClickerSettings s) => IsHoldMode(s.InputType, s.DutyCycleEnabled, s.DutyCyclePercent, s.DutyMode);
}
