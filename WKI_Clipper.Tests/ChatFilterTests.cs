using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Mention matching. The boundary rules are the whole point: a false positive on every
/// message containing your name as a substring would highlight half the chat.
/// </summary>
public class ChatFilterTests
{
    [Theory]
    [InlineData("@oskar_blitz nice shot")]
    [InlineData("oskar_blitz how do you record that?")]
    [InlineData("hey @Oskar_Blitz what mod is that")]      // case-insensitive
    [InlineData("gg oskar_blitz")]                          // end of line
    [InlineData("was that @oskar_blitz?")]                  // punctuation boundary
    [InlineData("oskar_blitz")]                             // whole message
    public void Detects_a_real_mention(string text)
        => Assert.True(ChatFilter.IsMention(text, "oskar_blitz"));

    [Theory]
    [InlineData("oskar_blitzz is a different person")]      // longer name
    [InlineData("notoskar_blitz said so")]                  // prefixed
    [InlineData("xoskar_blitzy")]                           // both sides
    [InlineData("oskar blitz without the underscore")]
    [InlineData("nothing to do with anyone")]
    public void Ignores_near_misses(string text)
        => Assert.False(ChatFilter.IsMention(text, "oskar_blitz"));

    [Fact]
    public void Finds_a_later_mention_after_a_near_miss()
    {
        // The scan must keep going past a boundary failure instead of giving up.
        Assert.True(ChatFilter.IsMention("oskar_blitzz asked, but @oskar_blitz answered", "oskar_blitz"));
    }

    [Theory]
    [InlineData(null, "oskar_blitz")]
    [InlineData("", "oskar_blitz")]
    [InlineData("   ", "oskar_blitz")]
    [InlineData("hi", null)]
    [InlineData("hi", "")]
    [InlineData("hi", "  @  ")]
    [InlineData("me", "oskar_blitz")]                       // name longer than the text
    public void Handles_missing_or_impossible_input(string? text, string? name)
        => Assert.False(ChatFilter.IsMention(text, name));

    [Fact]
    public void A_configured_name_may_carry_the_at_sign()
    {
        Assert.True(ChatFilter.IsMention("yo oskar_blitz", "@oskar_blitz"));
    }
}
