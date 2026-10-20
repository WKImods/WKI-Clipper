using WKI_Clipper.Views;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class ReloadDeferralTests
{
    [Fact]
    public void Without_an_open_edit_a_reload_runs_at_once_and_nothing_waits()
    {
        var d = new ReloadDeferral();

        Assert.True(d.Request(editing: false));
        Assert.False(d.TakeIfDue(editing: false));
    }

    [Fact]
    public void During_an_edit_the_reload_waits_and_runs_once_when_the_edit_has_ended()
    {
        var d = new ReloadDeferral();

        Assert.False(d.Request(editing: true));
        Assert.False(d.TakeIfDue(editing: true));    // still typing
        Assert.True(d.TakeIfDue(editing: false));    // done
        Assert.False(d.TakeIfDue(editing: false));   // and only once
    }

    [Fact]
    public void Several_requests_during_one_edit_make_one_reload()
    {
        var d = new ReloadDeferral();

        d.Request(editing: true);
        d.Request(editing: true);
        d.Request(editing: true);

        Assert.True(d.TakeIfDue(editing: false));
        Assert.False(d.TakeIfDue(editing: false));
    }

    [Fact]
    public void A_reload_that_ran_anyway_satisfies_the_waiting_one()
    {
        var d = new ReloadDeferral();
        d.Request(editing: true);

        d.Done();

        Assert.False(d.TakeIfDue(editing: false));
    }

    [Fact]
    public void An_edit_that_ends_with_nothing_waiting_reloads_nothing()
    {
        var d = new ReloadDeferral();

        Assert.False(d.TakeIfDue(editing: false));
    }
}
