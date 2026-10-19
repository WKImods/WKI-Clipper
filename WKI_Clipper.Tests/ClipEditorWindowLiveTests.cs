using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.Views;
using Xunit;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using RadioButton = System.Windows.Controls.RadioButton;

namespace WKI_Clipper.Tests;

/// <summary>
/// The clip editor window itself, driven like a user: keys, a click on "Schneiden", the result on disk. Opens a real
/// window, so it only runs with WKI_LIVE=1 (and needs ffmpeg for the cut).
/// </summary>
[Trait("Category", "Live")]
[Collection("WpfApplication")]
public sealed class ClipEditorWindowLiveTests : ClipCutFixture
{
    private static bool Enabled => Environment.GetEnvironmentVariable("WKI_LIVE") == "1";

    private ClipEditorWindow.EditorServices Services(string ffmpeg, FFmpegJobRunner runner, AppSettings settings, GalleryMetaService meta)
    {
        var cut = new ClipCutService(runner, () => settings, meta)
        {
            AvailableEncoders = () => new[] { "libx264" },
            Notify = (k, t, b, p) => Toasts.Add((k, t, b, p))
        };
        var discord = new DiscordCompressService(runner, () => settings, meta) { AvailableEncoders = () => new[] { "libx264" } };
        return new ClipEditorWindow.EditorServices(ffmpeg, cut, discord, () => settings.Discord, () => new[] { "libx264" });
    }

    [Fact]
    public void A_file_that_is_no_readable_mp4_says_so_and_cannot_be_cut()
    {
        if (!Enabled) return;
        var junk = Path.Combine(Clips, "junk.mp4");
        File.WriteAllBytes(junk, new byte[4096]);
        using var runner = new FFmpegJobRunner(typeof(ClipEditorWindowLiveTests).Assembly.Location, null, PartsFile);
        var services = Services(typeof(ClipEditorWindowLiveTests).Assembly.Location, runner, Settings(), NewMeta());

        RunOnWpfThread(async () =>
        {
            var window = ClipEditorWindow.Open(services, junk);
            await WaitFor(() => window.StateName == "Unsupported");

            Assert.Equal("Schneiden — junk.mp4", window.Title);
            Assert.Contains(Texts(window), t => t.Contains("kein lesbares MP4"));
            Assert.False(Find<Button>(window, b => b.Content as string == "Schneiden")!.IsEnabled);
            Assert.Same(window, ClipEditorWindow.Open(services, junk));   // one window per file
            window.Close();
            var second = ClipEditorWindow.Open(services, junk);   // closed windows are forgotten
            Assert.NotSame(window, second);
            second.Close();
        });
    }

    [Fact]
    public void A_user_can_mark_a_range_cut_it_exactly_and_find_the_result_next_to_the_clip()
    {
        if (!Enabled) return;
        var ffmpeg = FFmpegTestSupport.Find();
        if (ffmpeg is null) return;

        var stamped = Path.Combine(Dir, "stamp.mp4");
        StampFootage.Make(ffmpeg, stamped);
        var source = Path.Combine(Clips, "Arma Reforger", "Clip_1.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.Copy(stamped, source);
        var meta = NewMeta();
        meta.SetNote(GalleryMetaKey.For(GalleryBase.Clips, Clips, source), "Beweis");
        using var runner = new FFmpegJobRunner(ffmpeg, null, PartsFile);
        var services = Services(ffmpeg, runner, Settings(), meta);

        RunOnWpfThread(async () =>
        {
            var window = ClipEditorWindow.Open(services, source);
            await WaitFor(() => window.StateName == "Editing", 15000);

            // the whole clip is selected: nothing to cut, and the button says no
            var cutButton = Find<Button>(window, b => b.Content as string == "Schneiden")!;
            Assert.False(cutButton.IsEnabled);
            Assert.Contains(Texts(window), t => t.Contains("ganzen Clip"));

            // exact mode, start at frame 10, end at frame 350
            Find<RadioButton>(window, r => r.Content as string == "Genau (neu kodieren)")!.IsChecked = true;
            for (int i = 0; i < 10; i++) Press(window, Key.Right);
            Press(window, Key.I);
            Press(window, Key.End);
            for (int i = 0; i < 9; i++) Press(window, Key.Left);
            Press(window, Key.O);
            Assert.Contains(Texts(window), t => t.StartsWith("Start 00:00.333 · Ende 00:11.700"));
            Assert.True(cutButton.IsEnabled);

            cutButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitFor(() => window.StateName == "Done", 90000);

            var output = Path.Combine(Path.GetDirectoryName(source)!, "Clip_1_cut.mp4");
            Assert.True(File.Exists(output));
            Assert.Contains(Texts(window), t => t.StartsWith("Gespeichert: Clip_1_cut.mp4"));
            Assert.Equal(341, Mp4Index.TryRead(output)!.FrameCount);
            Assert.Equal(10, StampFootage.FirstStamp(ffmpeg, output));
            Assert.Equal(350, StampFootage.LastStamp(ffmpeg, output));
            Assert.Equal("Beweis (Ausschnitt 00:00–00:12)", meta.GetNote(GalleryMetaKey.For(GalleryBase.Clips, Clips, output)));
            Assert.True(File.Exists(source));            // the original stays unless the box was ticked

            // and back to editing for the next cut
            Find<Button>(window, b => b.Content as string == "Weiter schneiden")!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitFor(() => window.StateName == "Editing");
            window.Close();
        });
    }

    // ---------------------------------------------------------------- helpers

    private static void Press(Window window, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        window.RaiseEvent(args);
    }

    private static async Task WaitFor(Func<bool> condition, int milliseconds = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the window");
            await Task.Delay(50);
        }
    }

    private static T? Find<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T hit && match(hit)) return hit;
            if (Find(child, match) is { } deeper) return deeper;
        }
        return null;
    }

    private static List<string> Texts(DependencyObject root)
    {
        var all = new List<string>();
        void Walk(DependencyObject o)
        {
            if (o is TextBlock { Text.Length: > 0 } tb) all.Add(tb.Text);
            foreach (var child in LogicalTreeHelper.GetChildren(o).OfType<DependencyObject>()) Walk(child);
        }
        Walk(root);
        return all;
    }

    private static void RunOnWpfThread(Func<Task> body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                WpfTestSupport.EnsureTheme();
                var dispatcher = Dispatcher.CurrentDispatcher;
                _ = dispatcher.InvokeAsync(async () =>
                {
                    try { await body(); }
                    catch (Exception e) { error = e; }
                    finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
                });
                Dispatcher.Run();
            }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(3))) throw new TimeoutException("the window test did not finish");
        if (error is not null) throw new Exception("window test failed: " + error.Message, error);
    }
}
