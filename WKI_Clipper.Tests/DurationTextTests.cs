using System.Linq;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Phase 4: buffer lengths up to 30 minutes need one formatter everywhere ("45 s", "30 min").</summary>
public sealed class DurationTextTests
{
    [Theory]
    [InlineData(15, "15 s")]
    [InlineData(45, "45 s")]
    [InlineData(59, "59 s")]
    [InlineData(60, "1 min")]
    [InlineData(90, "1:30 min")]
    [InlineData(180, "3 min")]
    [InlineData(1200, "20 min")]
    [InlineData(1800, "30 min")]
    public void Format_names_configured_lengths(int seconds, string expected)
        => Assert.Equal(expected, DurationText.Format(seconds));

    [Theory]
    [InlineData(1799.6, "30:00 min")]
    [InlineData(1745, "29:05 min")]
    [InlineData(42.4, "42 s")]
    [InlineData(59.4, "59 s")]
    [InlineData(59.6, "1:00 min")]
    [InlineData(60, "1:00 min")]
    [InlineData(0, "0 s")]
    public void FormatActual_rounds_to_whole_seconds(double seconds, string expected)
        => Assert.Equal(expected, DurationText.FormatActual(seconds));

    [Fact]
    public void FormatActual_never_goes_negative()
        => Assert.Equal("0 s", DurationText.FormatActual(-3));

    [Theory]
    [InlineData(7200, 1800)]
    [InlineData(0, 15)]
    [InlineData(-5, 15)]
    [InlineData(60, 60)]
    [InlineData(1801, 1800)]
    public void Clamp_keeps_the_length_inside_the_supported_range(int input, int expected)
        => Assert.Equal(expected, DurationText.Clamp(input));

    [Fact]
    public void Choices_are_ascending_and_end_at_the_maximum()
    {
        Assert.Equal(DurationText.Choices.OrderBy(c => c), DurationText.Choices);
        Assert.Equal(DurationText.MaxSeconds, DurationText.Choices[^1]);
        Assert.Equal(DurationText.MinSeconds, DurationText.Choices[0]);
        foreach (var c in new[] { 300, 600, 900, 1200 })
            Assert.Contains(c, DurationText.Choices);
    }

    [Theory]
    [InlineData(60, 60)]
    [InlineData(1800, 1800)]
    [InlineData(100, 90)]     // a hand-edited value shows the largest offered length below it
    [InlineData(7200, 1800)]  // and cannot exceed the maximum
    [InlineData(0, 15)]
    [InlineData(200, 180)]
    public void The_settings_list_selects_the_current_length_or_the_one_below(int configured, int expectedChoice)
    {
        int idx = WKI_Clipper.Views.VideoSettingsView.InitialBufferIndex(DurationText.Choices, configured);
        Assert.Equal(expectedChoice, DurationText.Choices[idx]);
    }

    [Fact]
    public void Every_choice_survives_the_clamp()
    {
        foreach (var c in DurationText.Choices)
            Assert.Equal(c, DurationText.Clamp(c));
    }
}
