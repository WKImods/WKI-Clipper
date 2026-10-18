using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>The two Discord targets shared by the gallery menu and the clip editor.</summary>
public sealed class DiscordCompressTargetsTests
{
    [Fact]
    public void The_defaults_are_20_and_500_MB()
        => Assert.Equal((20_000_000L, 500_000_000L), DiscordCompressRules.Targets(new DiscordCompressSettings()));

    [Fact]
    public void Configured_values_are_used()
        => Assert.Equal((50_000_000L, 1_000_000_000L),
            DiscordCompressRules.Targets(new DiscordCompressSettings { SmallTargetBytes = 50_000_000, LargeTargetBytes = 1_000_000_000 }));

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    public void Zero_or_negative_falls_back_to_the_default(long stored)
        => Assert.Equal((20_000_000L, 500_000_000L),
            DiscordCompressRules.Targets(new DiscordCompressSettings { SmallTargetBytes = stored, LargeTargetBytes = stored }));

    [Fact]
    public void A_target_below_one_megabyte_is_raised_to_one_megabyte()
        => Assert.Equal((1_000_000L, 1_000_000L),
            DiscordCompressRules.Targets(new DiscordCompressSettings { SmallTargetBytes = 10, LargeTargetBytes = 999_999 }));
}
