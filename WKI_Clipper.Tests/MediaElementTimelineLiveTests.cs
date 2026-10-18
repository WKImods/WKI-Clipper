using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WKI_Clipper.Services;
using Xunit;
using Xunit.Abstractions;
using Canvas = System.Windows.Controls.Canvas;
using Brushes = System.Windows.Media.Brushes;
using HAlign = System.Windows.HorizontalAlignment;

namespace WKI_Clipper.Tests;

/// <summary>
/// Guards the one assumption the editor's preview rests on: WPF's MediaElement (Media Foundation) shows the frame
/// <see cref="ClipTimeline.FrameAtPreview"/> predicts — IGNORING the edit list of the mp4 — and a seek to
/// <see cref="ClipTimeline.PreviewSeekTarget"/> shows exactly that frame. Windows updates could change it.
///
/// Needs an interactive desktop and a real ffmpeg, so it only runs with WKI_LIVE=1 (otherwise it returns at once).
/// </summary>
[Trait("Category", "Live")]
public sealed class MediaElementTimelineLiveTests
{
    private readonly ITestOutputHelper _out;
    public MediaElementTimelineLiveTests(ITestOutputHelper output) => _out = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("WKI_LIVE") == "1";

    [Fact]
    public void The_preview_shows_the_frame_the_index_predicts_for_a_video_offset_and_for_B_frames()
    {
        if (!Enabled) return;
        var ffmpeg = FFmpegTestSupport.Find();
        if (ffmpeg is null) return;

        var dir = Path.Combine(Path.GetTempPath(), "wki_melive_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var plain = Path.Combine(dir, "plain.mp4");
            var voff = Path.Combine(dir, "voff.mp4");
            var bf = Path.Combine(dir, "bf.mp4");
            StampFootage.Make(ffmpeg, plain);
            StampFootage.Make(ffmpeg, bf, bframes: true);
            StampFootage.MakeVideoOffset(ffmpeg, plain, voff);

            RunOnWpfThread(async host =>
            {
                foreach (var file in new[] { voff, bf })
                {
                    var index = Mp4Index.TryRead(file)!;
                    var timeline = new ClipTimeline(index);
                    await host.Open(file);
                    _out.WriteLine($"{Path.GetFileName(file)}: preview[0]={index.PreviewSeconds[0]:0.0000} file[0]={index.FileSeconds[0]:0.0000}");

                    // 1. arbitrary positions: the picture is the frame the index names for that time
                    int previous = host.Sig();
                    foreach (var frame in new[] { 12, 99, 181, 250, 358 })
                    {
                        double p = index.PreviewSeconds[frame] + 0.4 * timeline.FrameDuration(frame);
                        int shown = await host.SeekAndWait(p, previous);
                        _out.WriteLine($"  P={p:0.000} expects {timeline.FrameAtPreview(p)} shows {shown}");
                        Assert.Equal(timeline.FrameAtPreview(p), shown);
                        previous = shown;
                    }

                    // 2. stepping with the frame-middle target: 8 consecutive frames forward, then back
                    int start = 100;
                    int current = await host.SeekAndWait(timeline.PreviewSeekTarget(start), previous);
                    Assert.Equal(start, current);
                    for (int i = 1; i <= 8; i++)
                    {
                        current = await host.SeekAndWait(timeline.PreviewSeekTarget(start + i), current);
                        Assert.Equal(start + i, current);
                    }
                    for (int i = 7; i >= 0; i--)
                    {
                        current = await host.SeekAndWait(timeline.PreviewSeekTarget(start + i), current);
                        Assert.Equal(start + i, current);
                    }
                    host.Close();
                }
            });
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // ---------------------------------------------------------------- the WPF side

    private sealed class MediaHost
    {
        private readonly MediaElement _me = new()
        {
            LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, ScrubbingEnabled = true,
            Volume = 0, Stretch = Stretch.None, HorizontalAlignment = HAlign.Left, VerticalAlignment = VerticalAlignment.Top
        };

        public UIElement Element => _me;

        public async Task Open(string file)
        {
            var opened = new TaskCompletionSource<string>();
            RoutedEventHandler onOpen = (_, _) => opened.TrySetResult("opened");
            EventHandler<ExceptionRoutedEventArgs> onFail = (_, e) => opened.TrySetResult("FAILED " + e.ErrorException?.Message);
            _me.MediaOpened += onOpen;
            _me.MediaFailed += onFail;
            _me.Source = new Uri(file);
            _me.Play();
            _me.Pause();
            var done = await Task.WhenAny(opened.Task, Task.Delay(15000));
            _me.MediaOpened -= onOpen;
            _me.MediaFailed -= onFail;
            Assert.True(done == opened.Task && opened.Task.Result == "opened", "the clip did not open: " + (done == opened.Task ? opened.Task.Result : "timeout"));
            await Task.Delay(300);   // the first picture needs a moment after opening
        }

        public void Close()
        {
            _me.Stop();
            _me.Close();
            _me.Source = null;
        }

        /// <summary>Frame number read from the stamp in the rendered picture.</summary>
        public int Sig()
        {
            const int w = 192, h = 64;
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(_me);
            var px = new byte[w * h * 4];
            rtb.CopyPixels(px, w * 4, 0);
            int Decode(int x)
            {
                int r = px[(32 * w + x) * 4 + 2];
                double y = r * 219.0 / 255 + 16;
                return (int)Math.Round((y - 20) / 3.0);
            }
            return Decode(32) + 64 * Decode(96) + 4096 * Decode(160);
        }

        /// <summary>Seeks and waits (up to 3 s) until the picture shows something else than before, then lets it settle.</summary>
        public async Task<int> SeekAndWait(double previewSeconds, int previous)
        {
            _me.Position = TimeSpan.FromSeconds(previewSeconds);
            var sw = Stopwatch.StartNew();
            int sig = previous;
            while (sw.ElapsedMilliseconds < 3000)
            {
                await NextRender();
                sig = Sig();
                if (sig != previous) break;
            }
            // the pipeline may show an intermediate picture first: the answer is the one that stays for 300 ms
            var stable = Stopwatch.StartNew();
            int last = sig;
            while (stable.ElapsedMilliseconds < 300 && sw.ElapsedMilliseconds < 6000)
            {
                await NextRender();
                int now = Sig();
                if (now != last) { last = now; stable.Restart(); }
            }
            return last;
        }

        private static Task NextRender()
        {
            var tcs = new TaskCompletionSource();
            EventHandler? handler = null;
            handler = (_, _) => { CompositionTarget.Rendering -= handler; tcs.TrySetResult(); };
            CompositionTarget.Rendering += handler;
            return tcs.Task;
        }
    }

    private static void RunOnWpfThread(Func<MediaHost, Task> body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var host = new MediaHost();
                var canvas = new Canvas();
                canvas.Children.Add(host.Element);
                var window = new Window
                {
                    Width = 400, Height = 300, Left = 40, Top = 40, ShowInTaskbar = false,
                    Background = Brushes.Black, Content = canvas, WindowStartupLocation = WindowStartupLocation.Manual
                };
                window.Loaded += async (_, _) =>
                {
                    try { await body(host); }
                    catch (Exception e) { error = e; }
                    finally
                    {
                        window.Close();
                        Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    }
                };
                window.Show();
                Dispatcher.Run();
            }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(3))) throw new TimeoutException("the live test did not finish");
        if (error is not null) throw new Exception("live test failed: " + error.Message, error);
    }
}
