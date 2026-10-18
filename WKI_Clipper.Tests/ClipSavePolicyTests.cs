using System;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class ClipSavePolicyTests
{
    private const long GiB = 1L << 30;

    [Fact]
    public void Faststart_is_kept_up_to_exactly_one_gibibyte()
    {
        Assert.True(ClipSavePolicy.UseFaststart(GiB));
        Assert.False(ClipSavePolicy.UseFaststart(GiB + 1));
        Assert.True(ClipSavePolicy.UseFaststart(20L << 20));
        Assert.False(ClipSavePolicy.UseFaststart(6_500_000_000));
    }

    [Fact]
    public void Fraction_is_written_over_expected_and_never_reaches_one_hundred_percent()
    {
        Assert.Equal(0.5, ClipSavePolicy.Fraction(500, 1000), 6);
        Assert.Equal(0.99, ClipSavePolicy.Fraction(1000, 1000), 6);
        Assert.Equal(0.99, ClipSavePolicy.Fraction(5000, 1000), 6);
    }

    [Theory]
    [InlineData(0L, 1000L)]
    [InlineData(-5L, 1000L)]
    [InlineData(500L, 0L)]
    [InlineData(500L, -1L)]
    public void Fraction_without_data_is_zero(long written, long expected)
        => Assert.Equal(0, ClipSavePolicy.Fraction(written, expected));

    [Fact]
    public void Phase_is_finalizing_only_with_faststart_and_after_the_copy()
    {
        Assert.Equal(SavePhase.Copying, ClipSavePolicy.Phase(900, 1000, faststart: true));
        Assert.Equal(SavePhase.Finalizing, ClipSavePolicy.Phase(950, 1000, faststart: true));
        Assert.Equal(SavePhase.Copying, ClipSavePolicy.Phase(1000, 1000, faststart: false));
        Assert.Equal(SavePhase.Copying, ClipSavePolicy.Phase(100, 0, faststart: true));
    }

    [Theory]
    [InlineData(SaveKind.Clip, SaveFailureReason.NotRunning, true)]
    [InlineData(SaveKind.Clip, SaveFailureReason.Stale, true)]
    [InlineData(SaveKind.Clip, SaveFailureReason.NotEnoughMaterial, true)]
    [InlineData(SaveKind.Clip, SaveFailureReason.Aborted, true)]
    [InlineData(SaveKind.Clip, SaveFailureReason.Failed, true)]
    [InlineData(SaveKind.Clip, SaveFailureReason.RenameFailed, true)]
    [InlineData(SaveKind.Clip, SaveFailureReason.NoDiskSpace, true)]
    [InlineData(SaveKind.Clip, SaveFailureReason.Busy, false)]   // a refused SECOND press: the first save is still running
    [InlineData(SaveKind.Gif, SaveFailureReason.Failed, false)]
    [InlineData(SaveKind.Gif, SaveFailureReason.Busy, false)]
    public void The_progress_toast_closes_only_when_the_clip_save_itself_failed(SaveKind kind, SaveFailureReason reason, bool closes)
        => Assert.Equal(closes, ClipSavePolicy.ClosesProgressToast(new SaveFailure(kind, reason)));

    private static SaveProgressInfo Progress(SaveKind kind, int clipSeconds, double fraction)
        => new(kind, clipSeconds, fraction, SavePhase.Copying, TimeSpan.FromSeconds(10));

    [Fact]
    public void Busy_during_a_long_clip_names_the_clip_and_the_progress()
    {
        var text = ClipSavePolicy.BusyMessage(Progress(SaveKind.Clip, 1800, 0.427), SaveKind.Clip);
        Assert.Contains("30 min", text);
        Assert.Contains("42 %", text);
        Assert.Contains("Clip danach nochmal", text);
    }

    [Fact]
    public void Busy_for_a_gif_during_a_long_clip_uses_the_gif_wording()
    {
        var text = ClipSavePolicy.BusyMessage(Progress(SaveKind.Clip, 600, 0.1), SaveKind.Gif);
        Assert.Contains("GIF danach nochmal", text);
        Assert.Contains("10 min", text);
    }

    [Fact]
    public void Busy_during_a_short_clip_keeps_the_old_text()
    {
        var text = ClipSavePolicy.BusyMessage(Progress(SaveKind.Clip, 60, 0.5), SaveKind.Clip);
        Assert.Equal("Speichern läuft bereits — kurz warten.", text);
    }

    [Fact]
    public void Busy_exactly_at_the_long_clip_threshold_is_already_long()
    {
        Assert.Contains("5 min", ClipSavePolicy.BusyMessage(Progress(SaveKind.Clip, DurationText.LongClipSeconds, 0.2), SaveKind.Clip));
        Assert.DoesNotContain("%", ClipSavePolicy.BusyMessage(Progress(SaveKind.Clip, DurationText.LongClipSeconds - 1, 0.2), SaveKind.Clip));
    }

    [Fact]
    public void Busy_without_progress_info_or_during_a_gif_keeps_the_old_text()
    {
        const string old = "Speichern läuft bereits — kurz warten.";
        Assert.Equal(old, ClipSavePolicy.BusyMessage(null, SaveKind.Clip));
        Assert.Equal(old, ClipSavePolicy.BusyMessage(Progress(SaveKind.Gif, 1800, 0.5), SaveKind.Clip));
    }
}
