using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>The per-user ETW session name: unique per account, stable, ETW- and logman-safe.</summary>
public class EtwSessionNameTests
{
    [Fact]
    public void The_name_carries_the_users_sid()
    {
        Assert.Equal("WKI-Clipper-FPS-S-1-5-21-1727089496-3751423440-187885283-1001",
            EtwPresentSession.SessionNameForUser("S-1-5-21-1727089496-3751423440-187885283-1001", "cr4zy"));
    }

    [Fact]
    public void Accounts_sharing_a_relative_id_still_get_their_own_session()
    {
        // A local and a domain account can both end in -1001; stopping the other one's
        // "orphan" would break its overlay.
        var local = EtwPresentSession.SessionNameForUser("S-1-5-21-1-2-3-1001", "a");
        var domain = EtwPresentSession.SessionNameForUser("S-1-5-21-7-8-9-1001", "a");
        Assert.NotEqual(local, domain);
    }

    [Theory]
    [InlineData(null, "Max Mustermann", "WKI-Clipper-FPS-Max_Mustermann")]
    [InlineData("", "", "WKI-Clipper-FPS-user")]
    [InlineData(null, null, "WKI-Clipper-FPS-user")]
    [InlineData("  ", "jürgen", "WKI-Clipper-FPS-j_rgen")]
    public void Without_a_sid_the_user_name_is_used_in_a_safe_form(string? sid, string? user, string expected)
        => Assert.Equal(expected, EtwPresentSession.SessionNameForUser(sid, user));

    [Fact]
    public void The_name_is_stable_and_bounded()
    {
        Assert.Equal(EtwPresentSession.SessionNameForCurrentUser(), EtwPresentSession.SessionNameForCurrentUser());
        Assert.StartsWith(EtwPresentSession.SessionNamePrefix, EtwPresentSession.SessionNameForCurrentUser());
        Assert.True(EtwPresentSession.SessionNameForUser(new string('x', 5000), null).Length <= 200);
    }
}
