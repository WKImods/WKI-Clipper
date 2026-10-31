using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.Views;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>The gallery's "Löschen" item and its messages: enabled state, tooltip reason, toast text per outcome.</summary>
public sealed class GalleryDeleteRulesTests
{
    // ---- 1: the menu item ----

    [Fact]
    public void A_file_on_a_drive_with_a_recycle_bin_can_be_deleted_and_the_tooltip_says_where_it_goes()
    {
        var state = GalleryDeleteRules.ItemState(fileExists: true, recyclable: true, compressing: false, deleting: false);

        Assert.True(state.Enabled);
        Assert.Equal("Verschiebt die Datei in den Papierkorb. Von dort lässt sie sich wiederherstellen.", state.ToolTip);
    }

    [Fact]
    public void A_drive_without_a_recycle_bin_disables_the_item_and_points_to_show_in_folder()
    {
        var state = GalleryDeleteRules.ItemState(fileExists: true, recyclable: false, compressing: false, deleting: false);

        Assert.False(state.Enabled);
        Assert.Equal("Auf diesem Laufwerk gibt es keinen Papierkorb. Über „Im Ordner zeigen“ selbst löschen.", state.ToolTip);
    }

    [Fact]
    public void A_running_discord_compress_of_the_file_disables_the_item()
    {
        var state = GalleryDeleteRules.ItemState(fileExists: true, recyclable: true, compressing: true, deleting: false);

        Assert.False(state.Enabled);
        Assert.Contains("komprimiert", state.ToolTip);
    }

    [Fact]
    public void A_delete_that_is_still_running_disables_the_item_so_it_cannot_start_twice()
    {
        var state = GalleryDeleteRules.ItemState(fileExists: true, recyclable: true, compressing: false, deleting: true);

        Assert.False(state.Enabled);
        Assert.Contains("in den Papierkorb verschoben", state.ToolTip);
    }

    [Fact]
    public void A_file_that_is_gone_is_not_blamed_on_the_drive()
    {
        // IsSupportedPath is false for a missing file too: "no Recycle Bin here" would be wrong then
        var state = GalleryDeleteRules.ItemState(fileExists: false, recyclable: false, compressing: false, deleting: false);

        Assert.False(state.Enabled);
        Assert.DoesNotContain("keinen Papierkorb", state.ToolTip);
        Assert.Contains("gibt es nicht mehr", state.ToolTip);
    }

    [Fact]
    public void A_drive_without_a_bin_is_the_reason_given_over_a_running_compress()
    {
        // the compress ends, the missing bin stays: name the reason that will still hold afterwards
        var state = GalleryDeleteRules.ItemState(fileExists: true, recyclable: false, compressing: true, deleting: false);

        Assert.False(state.Enabled);
        Assert.Contains("keinen Papierkorb", state.ToolTip);
    }

    [Fact]
    public void A_running_delete_is_the_reason_given_over_everything_else()
    {
        var state = GalleryDeleteRules.ItemState(fileExists: false, recyclable: false, compressing: true, deleting: true);

        Assert.False(state.Enabled);
        Assert.Contains("in den Papierkorb verschoben", state.ToolTip);
    }

    // ---- 2: what the toast says ----

    [Theory]
    [InlineData(RecycleOutcome.Recycled, "„Clip_1.mp4“ liegt im Papierkorb.")]
    [InlineData(RecycleOutcome.InUse, "Die Datei „Clip_1.mp4“ ist in Verwendung und wurde behalten.")]
    [InlineData(RecycleOutcome.NotSupported,
        "Die Datei „Clip_1.mp4“ ließ sich nicht in den Papierkorb verschieben (zu groß dafür oder der Papierkorb ist ausgeschaltet) und wurde behalten.")]
    [InlineData(RecycleOutcome.Declined, "Nicht endgültig gelöscht — die Datei „Clip_1.mp4“ wurde behalten.")]
    [InlineData(RecycleOutcome.DeletedPermanently, "Die Datei „Clip_1.mp4“ wurde gelöscht, liegt aber nicht im Papierkorb.")]
    [InlineData(RecycleOutcome.Failed, "Die Datei „Clip_1.mp4“ konnte nicht in den Papierkorb verschoben werden und wurde behalten.")]
    public void Each_outcome_has_its_own_sentence(RecycleOutcome outcome, string expected)
        => Assert.Equal(expected, GalleryDeleteRules.Message(outcome, "Clip_1.mp4"));

    [Fact]
    public void Only_a_verified_move_says_the_file_is_in_the_recycle_bin()
    {
        foreach (var outcome in Enum.GetValues<RecycleOutcome>())
            Assert.Equal(outcome == RecycleOutcome.Recycled,
                         GalleryDeleteRules.Message(outcome, "Clip_1.mp4").Contains("liegt im Papierkorb"));
    }

    [Fact]
    public void Every_message_names_the_file_and_none_talks_about_an_original()
    {
        foreach (var outcome in Enum.GetValues<RecycleOutcome>())
        {
            var text = GalleryDeleteRules.Message(outcome, "Screenshot_7.png");
            Assert.Contains("„Screenshot_7.png“", text);
            Assert.DoesNotContain("Original", text);
        }
    }

    [Fact]
    public void Only_a_recycled_file_is_good_news_everything_else_is_a_warning()
    {
        foreach (var outcome in Enum.GetValues<RecycleOutcome>())
            Assert.Equal(outcome == RecycleOutcome.Recycled ? ToastKind.Info : ToastKind.Warning, GalleryDeleteRules.ToastKindOf(outcome));
    }

    [Fact]
    public void A_warning_stays_on_screen_longer_than_the_good_news()
        => Assert.True(GalleryDeleteRules.ToastSeconds(RecycleOutcome.InUse) > GalleryDeleteRules.ToastSeconds(RecycleOutcome.Recycled));

    [Fact]
    public void The_toast_title_is_the_gallery()
        => Assert.Equal("Galerie", GalleryDeleteRules.ToastTitle);

    [Fact]
    public void Only_a_file_that_is_gone_takes_its_row_away()
    {
        foreach (var outcome in Enum.GetValues<RecycleOutcome>())
            Assert.Equal(outcome is RecycleOutcome.Recycled or RecycleOutcome.DeletedPermanently, GalleryDeleteRules.RemovesFile(outcome));
    }

    [Fact]
    public void Switched_off_toasts_only_silence_the_good_news()
    {
        // the row disappearing says "done"; a kept file or one gone past the bin has no other way to reach the user
        foreach (var outcome in Enum.GetValues<RecycleOutcome>())
        {
            Assert.True(GalleryDeleteRules.ShowsToast(outcome, toastsOn: true));
            Assert.Equal(outcome != RecycleOutcome.Recycled, GalleryDeleteRules.ShowsToast(outcome, toastsOn: false));
        }
    }
}

/// <summary>The English texts of the gallery delete.</summary>
[Collection("Language")]
public sealed class GalleryDeleteRulesEnglishTests
{
    [Fact]
    public void The_english_tooltips()
    {
        using var _ = TestLanguage.English();

        Assert.Equal("Moves the file to the Recycle Bin. You can restore it from there.",
                     GalleryDeleteRules.ItemState(true, true, false, false).ToolTip);
        Assert.Equal("This drive has no Recycle Bin. Use “Show in folder” to delete it yourself.",
                     GalleryDeleteRules.ItemState(true, false, false, false).ToolTip);
        Assert.Contains("being compressed", GalleryDeleteRules.ItemState(true, true, true, false).ToolTip);
        Assert.Contains("no longer exists", GalleryDeleteRules.ItemState(false, false, false, false).ToolTip);
    }

    [Theory]
    [InlineData(RecycleOutcome.Recycled, "“Clip_1.mp4” is in the Recycle Bin.")]
    [InlineData(RecycleOutcome.InUse, "The file “Clip_1.mp4” is in use and was kept.")]
    [InlineData(RecycleOutcome.NotSupported,
        "The file “Clip_1.mp4” could not go to the Recycle Bin (too big for it, or the bin is switched off) and was kept.")]
    [InlineData(RecycleOutcome.Declined, "Not deleted permanently — the file “Clip_1.mp4” was kept.")]
    [InlineData(RecycleOutcome.DeletedPermanently, "The file “Clip_1.mp4” was deleted but is not in the Recycle Bin.")]
    [InlineData(RecycleOutcome.Failed, "The file “Clip_1.mp4” could not be moved to the Recycle Bin and was kept.")]
    public void The_english_messages(RecycleOutcome outcome, string expected)
    {
        using var _ = TestLanguage.English();

        Assert.Equal(expected, GalleryDeleteRules.Message(outcome, "Clip_1.mp4"));
        Assert.Equal("Gallery", GalleryDeleteRules.ToastTitle);
    }
}

/// <summary>The delete itself: off the UI thread, retried while the file is in use, one at a time per file, then a toast.</summary>
public sealed class GalleryDeleteServiceTests
{
    private static readonly string Clip = Path.Combine(Path.GetTempPath(), "wki_delete", "Arma Reforger", "Clip_1.mp4");

    private readonly List<(ToastKind Kind, string Title, string Body, double Seconds)> _toasts = new();
    private readonly List<string> _removed = new();

    private GalleryDeleteService Build(Func<string, RecycleOutcome> recycle, Func<TimeSpan, Task>? delay = null, bool toasts = true,
        Func<Func<RecycleOutcome>, Task<RecycleOutcome>>? shell = null)
    {
        var settings = new AppSettings();
        settings.Behavior.ShowToastNotifications = toasts;
        var service = new GalleryDeleteService(() => settings)
        {
            Recycle = (path, _) => recycle(path),
            RunShell = shell ?? (work => Task.FromResult(work())),
            Delay = delay ?? (_ => Task.CompletedTask),
            Notify = (kind, title, body, seconds) => { lock (_toasts) _toasts.Add((kind, title, body, seconds)); }
        };
        service.Removed += p => { lock (_removed) _removed.Add(p); };
        return service;
    }

    // ---- 1: one try ----

    [Fact]
    public async Task A_recycled_file_is_announced_and_its_row_goes()
    {
        var recycled = new List<string>();
        var service = Build(p => { recycled.Add(p); return RecycleOutcome.Recycled; });

        var outcome = await service.DeleteAsync(Clip);

        Assert.Equal(RecycleOutcome.Recycled, outcome);
        Assert.Equal(new[] { Clip }, recycled);
        Assert.Equal(new[] { Clip }, _removed);
        var toast = Assert.Single(_toasts);
        Assert.Equal(ToastKind.Info, toast.Kind);
        Assert.Equal("Galerie", toast.Title);
        Assert.Equal("„Clip_1.mp4“ liegt im Papierkorb.", toast.Body);
    }

    [Theory]
    [InlineData(RecycleOutcome.Recycled, true)]
    [InlineData(RecycleOutcome.DeletedPermanently, true)]   // gone is gone, in the bin or not
    [InlineData(RecycleOutcome.InUse, false)]
    [InlineData(RecycleOutcome.Declined, false)]
    [InlineData(RecycleOutcome.NotSupported, false)]
    [InlineData(RecycleOutcome.Failed, false)]
    public async Task The_gallery_only_reloads_when_the_file_is_gone(RecycleOutcome outcome, bool reloads)
    {
        var service = Build(_ => outcome);

        Assert.Equal(outcome, await service.DeleteAsync(Clip));

        Assert.Equal(reloads, _removed.Count == 1);
        var toast = Assert.Single(_toasts);
        Assert.Equal(GalleryDeleteRules.Message(outcome, "Clip_1.mp4"), toast.Body);
        Assert.Equal(GalleryDeleteRules.ToastKindOf(outcome), toast.Kind);
        Assert.Equal(GalleryDeleteRules.ToastSeconds(outcome), toast.Seconds);
    }

    // ---- 2: the retry while the file is in use ----

    [Fact]
    public async Task A_file_in_use_is_tried_again_with_a_growing_wait_and_then_moved()
    {
        int calls = 0;
        var waits = new List<TimeSpan>();
        var service = Build(_ => ++calls < 3 ? RecycleOutcome.InUse : RecycleOutcome.Recycled,
                            delay: t => { waits.Add(t); return Task.CompletedTask; });

        var outcome = await service.DeleteAsync(Clip);

        Assert.Equal(RecycleOutcome.Recycled, outcome);
        Assert.Equal(3, calls);
        Assert.Equal(2, waits.Count);
        Assert.True(waits[1] > waits[0]);
        Assert.Single(_removed);
        Assert.Single(_toasts);                                       // one toast at the end, not one per try
    }

    [Fact]
    public async Task A_file_that_stays_in_use_is_kept_after_the_last_wait_with_a_warning()
    {
        int calls = 0;
        var waits = new List<TimeSpan>();
        var service = Build(_ => { calls++; return RecycleOutcome.InUse; }, delay: t => { waits.Add(t); return Task.CompletedTask; });

        var outcome = await service.DeleteAsync(Clip);

        Assert.Equal(RecycleOutcome.InUse, outcome);
        Assert.Equal(1 + RecycleBin.RetryDelays.Length, calls);
        Assert.Equal(RecycleBin.RetryDelays, waits);
        Assert.Empty(_removed);
        var toast = Assert.Single(_toasts);
        Assert.Equal(ToastKind.Warning, toast.Kind);
        Assert.Contains("in Verwendung", toast.Body);
    }

    [Theory]
    [InlineData(RecycleOutcome.Recycled)]
    [InlineData(RecycleOutcome.DeletedPermanently)]
    [InlineData(RecycleOutcome.Declined)]       // asking "delete permanently?" again would be nagging
    [InlineData(RecycleOutcome.Failed)]
    [InlineData(RecycleOutcome.NotSupported)]
    public async Task Only_a_file_in_use_is_tried_again(RecycleOutcome outcome)
    {
        int calls = 0;
        var waits = new List<TimeSpan>();
        var service = Build(_ => { calls++; return outcome; }, delay: t => { waits.Add(t); return Task.CompletedTask; });

        Assert.Equal(outcome, await service.DeleteAsync(Clip));
        Assert.Equal(1, calls);
        Assert.Empty(waits);
    }

    // ---- 3: one delete per file ----

    [Fact]
    public async Task A_second_delete_of_the_same_file_does_not_start_while_the_first_runs()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var service = Build(_ => Interlocked.Increment(ref calls) == 1 ? RecycleOutcome.InUse : RecycleOutcome.Recycled,
                            delay: _ => release.Task);

        var first = service.DeleteAsync(Clip);
        Assert.True(service.IsDeleting(Clip));
        Assert.True(service.IsDeleting(Clip.ToUpperInvariant()));     // Windows paths: case does not make another file

        Assert.Null(await service.DeleteAsync(Clip));                 // nothing started
        Assert.Equal(1, calls);

        release.SetResult();
        Assert.Equal(RecycleOutcome.Recycled, await first);
        Assert.Equal(2, calls);
        Assert.False(service.IsDeleting(Clip));
        Assert.Single(_removed);
        Assert.Single(_toasts);
    }

    [Fact]
    public async Task Another_file_can_be_deleted_while_one_waits()
    {
        var other = Path.Combine(Path.GetDirectoryName(Clip)!, "Clip_2.mp4");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Build(p => p == Clip && !release.Task.IsCompleted ? RecycleOutcome.InUse : RecycleOutcome.Recycled,
                            delay: _ => release.Task);

        var first = service.DeleteAsync(Clip);
        Assert.Equal(RecycleOutcome.Recycled, await service.DeleteAsync(other));

        release.SetResult();
        Assert.Equal(RecycleOutcome.Recycled, await first);
        Assert.Equal(new[] { other, Clip }, _removed);
    }

    [Fact]
    public async Task A_finished_delete_can_be_tried_again()
    {
        int calls = 0;
        var service = Build(_ => ++calls == 1 ? RecycleOutcome.Declined : RecycleOutcome.Recycled);

        Assert.Equal(RecycleOutcome.Declined, await service.DeleteAsync(Clip));
        Assert.Equal(RecycleOutcome.Recycled, await service.DeleteAsync(Clip));
    }

    // ---- 4: off the UI thread, never throwing ----

    [Fact]
    public async Task By_default_each_try_runs_on_its_own_sta_thread_and_not_on_the_callers()
    {
        int caller = Environment.CurrentManagedThreadId;
        int ranOn = caller;
        var apartment = ApartmentState.Unknown;
        var settings = new AppSettings();
        var service = new GalleryDeleteService(() => settings)
        {
            Recycle = (_, _) =>
            {
                ranOn = Environment.CurrentManagedThreadId;
                apartment = Thread.CurrentThread.GetApartmentState();
                return RecycleOutcome.Recycled;
            },
            Notify = (_, _, _, _) => { }
        };

        Assert.Equal(RecycleOutcome.Recycled, await service.DeleteAsync(Clip));
        Assert.NotEqual(caller, ranOn);
        Assert.Equal(ApartmentState.STA, apartment);    // the shell is apartment threaded, like the UI thread
    }

    [Fact]
    public async Task Every_try_gets_the_gallery_window_as_owner_of_windows_question()
    {
        // the board's widgets are topmost: a "delete permanently?" without an owner would hide behind them
        var owners = new List<IntPtr>();
        int calls = 0;
        var settings = new AppSettings();
        var service = new GalleryDeleteService(() => settings)
        {
            Recycle = (_, owner) => { owners.Add(owner); return ++calls == 1 ? RecycleOutcome.InUse : RecycleOutcome.Recycled; },
            RunShell = work => Task.FromResult(work()),
            Delay = _ => Task.CompletedTask,
            Notify = (_, _, _, _) => { }
        };

        Assert.Equal(RecycleOutcome.Recycled, await service.DeleteAsync(Clip, new IntPtr(0x1234)));
        Assert.Equal(new[] { new IntPtr(0x1234), new IntPtr(0x1234) }, owners);
    }

    [Fact]
    public async Task A_shell_call_that_throws_is_a_failure_with_a_warning_and_not_a_crash()
    {
        var service = Build(_ => RecycleOutcome.Recycled,
                            shell: _ => Task.FromException<RecycleOutcome>(new InvalidOperationException("boom")));

        Assert.Equal(RecycleOutcome.Failed, await service.DeleteAsync(Clip));
        Assert.False(service.IsDeleting(Clip));
        Assert.Empty(_removed);
        Assert.Equal(ToastKind.Warning, Assert.Single(_toasts).Kind);
    }

    // ---- 5: the toast switch ----

    [Fact]
    public async Task With_toasts_switched_off_a_recycled_file_needs_no_toast()
    {
        var service = Build(_ => RecycleOutcome.Recycled, toasts: false);

        Assert.Equal(RecycleOutcome.Recycled, await service.DeleteAsync(Clip));
        Assert.Empty(_toasts);
        Assert.Single(_removed);
    }

    [Fact]
    public async Task With_toasts_switched_off_a_kept_file_still_warns()
    {
        var service = Build(_ => RecycleOutcome.Failed, toasts: false);

        Assert.Equal(RecycleOutcome.Failed, await service.DeleteAsync(Clip));
        Assert.Equal(ToastKind.Warning, Assert.Single(_toasts).Kind);
    }
}
