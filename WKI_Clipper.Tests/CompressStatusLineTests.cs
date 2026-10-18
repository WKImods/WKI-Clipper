using System.Windows;
using System.Windows.Controls;
using WKI_Clipper.Services;
using WKI_Clipper.Views;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>The progress / error line a gallery row shows for a Discord compress, driven without a window.</summary>
[Collection("WpfApplication")]
public sealed class CompressStatusLineTests
{
    private static CompressJobSnapshot Active(CompressStage stage, double fraction = 0, int attempt = 1)
        => new(@"C:\Clips\a.mp4", 20_000_000, stage, fraction, attempt, null, null);

    private static CompressJobSnapshot Finished(CompressStatus outcome, string? error = null)
        => new(@"C:\Clips\a.mp4", 20_000_000, CompressStage.Encoding, 0.5, 2, error, outcome);

    private static T Run<T>(System.Func<CompressStatusLine, T> body, System.Action? cancel = null, System.Action? dismiss = null)
        => WpfTestSupport.OnSta(() =>
        {
            WpfTestSupport.EnsureTheme();
            var line = new CompressStatusLine(new Border(), cancel ?? (() => { }), dismiss ?? (() => { }));
            return body(line);
        });

    [Fact]
    public void A_row_without_a_job_shows_nothing()
        => Assert.False(Run(l => { l.Apply(null); return l.Root.Visibility == Visibility.Visible; }));

    [Fact]
    public void A_line_is_hidden_until_it_is_given_a_job()
        => Assert.Equal(Visibility.Collapsed, Run(l => l.Root.Visibility));

    [Fact]
    public void While_queued_the_bar_is_indeterminate_and_cancel_is_offered()
    {
        var (visible, indeterminate, cancel, dismiss, text) = Run(l =>
        {
            l.Apply(Active(CompressStage.Queued));
            return (l.Root.Visibility == Visibility.Visible, l.IsBarIndeterminate, l.CancelVisible, l.DismissVisible, l.Text);
        });

        Assert.True(visible);
        Assert.True(indeterminate);
        Assert.True(cancel);
        Assert.False(dismiss);
        Assert.Contains("Discord 20 MB", text);
        Assert.Contains("Warteschlange", text);
    }

    [Fact]
    public void While_a_game_runs_the_line_says_what_it_waits_for()
        => Assert.Contains("Spiel", Run(l => { l.Apply(Active(CompressStage.WaitingForGame)); return l.Text; }));

    [Fact]
    public void While_encoding_the_bar_follows_the_fraction_and_the_text_shows_the_percent()
    {
        var (indeterminate, value, text) = Run(l =>
        {
            l.Apply(Active(CompressStage.Encoding, 0.424));
            return (l.IsBarIndeterminate, l.BarValue, l.Text);
        });

        Assert.False(indeterminate);
        Assert.Equal(0.424, value, 3);
        Assert.Contains("42 %", text);
        Assert.DoesNotContain("Versuch", text);   // the first attempt is the normal case
    }

    [Fact]
    public void A_later_attempt_is_mentioned()
        => Assert.Contains("Versuch 3", Run(l => { l.Apply(Active(CompressStage.Encoding, 0.1, attempt: 3)); return l.Text; }));

    [Fact]
    public void A_fraction_outside_zero_to_one_is_held_inside_the_bar()
    {
        var (low, high) = Run(l =>
        {
            l.Apply(Active(CompressStage.Encoding, -0.5));
            var a = l.BarValue;
            l.Apply(Active(CompressStage.Encoding, 7));
            return (a, l.BarValue);
        });
        Assert.Equal(0, low);
        Assert.Equal(1, high);
    }

    [Fact]
    public void A_failure_shows_the_reason_in_red_with_a_dismiss_button_and_no_cancel()
    {
        var (visible, danger, bar, cancel, dismiss, text) = Run(l =>
        {
            l.Apply(Finished(CompressStatus.Failed, "Clip zu lang für 20 MB — erst schneiden oder größeres Ziel wählen"));
            return (l.Root.Visibility == Visibility.Visible, l.TextIsDanger, l.BarVisible, l.CancelVisible, l.DismissVisible, l.Text);
        });

        Assert.True(visible);
        Assert.True(danger);
        Assert.False(bar);
        Assert.False(cancel);
        Assert.True(dismiss);
        Assert.Contains("erst schneiden", text);
        Assert.StartsWith("Discord 20 MB: ", text);
    }

    [Fact]
    public void A_text_that_was_red_is_muted_again_for_the_next_job()
    {
        var danger = Run(l =>
        {
            l.Apply(Finished(CompressStatus.Failed, "kaputt"));
            l.Apply(Active(CompressStage.Encoding, 0.2));
            return l.TextIsDanger;
        });
        Assert.False(danger);
    }

    [Theory]
    [InlineData(CompressStatus.Succeeded)]
    [InlineData(CompressStatus.Cancelled)]
    public void A_finished_or_cancelled_job_leaves_the_row_as_it_was(CompressStatus outcome)
        => Assert.Equal(Visibility.Collapsed, Run(l =>
        {
            l.Apply(Active(CompressStage.Encoding, 0.5));
            l.Apply(Finished(outcome));
            return l.Root.Visibility;
        }));

    [Fact]
    public void A_failure_without_a_message_is_not_shown_as_an_empty_red_line()
        => Assert.Equal(Visibility.Collapsed, Run(l => { l.Apply(Finished(CompressStatus.Failed, "")); return l.Root.Visibility; }));

    [Fact]
    public void The_buttons_call_back_and_dismiss_hides_the_line()
    {
        int cancelled = 0, dismissed = 0;
        var visible = Run(l =>
        {
            l.Apply(Active(CompressStage.Encoding, 0.3));
            l.ClickCancel();
            l.Apply(Finished(CompressStatus.Failed, "kaputt"));
            l.ClickDismiss();
            return l.Root.Visibility == Visibility.Visible;
        }, cancel: () => cancelled++, dismiss: () => dismissed++);

        Assert.Equal(1, cancelled);
        Assert.Equal(1, dismissed);
        Assert.False(visible);
    }
}
