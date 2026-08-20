using System.Linq;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Pins the testable parts of the streaming-stack review fixes (v0.10.0): a folder rescan
/// must not lose the playing track, and the default layout carries the sources widget.
/// </summary>
public class StreamReviewFixTests
{
    private static Playlist Ordered(params string[] tracks)
    {
        var p = new Playlist(seed: 1);
        p.SetTracks(tracks, shuffle: false);
        return p;
    }

    [Fact]
    public void Rescan_keeps_the_playing_track_when_files_are_added()
    {
        var p = Ordered(@"D:\m\a.mp3", @"D:\m\b.mp3", @"D:\m\c.mp3");
        p.Next();                                   // now playing b (pos 1)
        Assert.Equal(@"D:\m\b.mp3", p.Current);

        // Two new files land BEFORE b alphabetically — indices shift.
        p.SetTracks(new[] { @"D:\m\a.mp3", @"D:\m\aa.mp3", @"D:\m\ab.mp3", @"D:\m\b.mp3", @"D:\m\c.mp3" },
                    shuffle: false);

        // The bug this guards: the position snapped to the first track while b kept
        // playing, and auto-advance continued from the top of the list.
        Assert.Equal(@"D:\m\b.mp3", p.Current);
        Assert.Equal(@"D:\m\c.mp3", p.Next());      // continues AFTER the playing track
    }

    [Fact]
    public void Rescan_matches_the_track_case_insensitively()
    {
        var p = Ordered(@"D:\m\a.mp3", @"D:\m\B.mp3");
        p.Next();
        p.SetTracks(new[] { @"D:\m\a.mp3", @"D:\m\b.MP3" }, shuffle: false);
        Assert.Equal(@"D:\m\b.MP3", p.Current);
    }

    [Fact]
    public void Rescan_falls_back_to_the_start_when_the_playing_track_vanished()
    {
        var p = Ordered(@"D:\m\a.mp3", @"D:\m\b.mp3", @"D:\m\c.mp3");
        p.Next();                                   // playing b
        p.SetTracks(new[] { @"D:\m\a.mp3", @"D:\m\c.mp3" }, shuffle: false);
        Assert.Equal(@"D:\m\a.mp3", p.Current);
    }

    [Fact]
    public void Rescan_keeps_the_track_in_shuffle_mode_too()
    {
        var p = new Playlist(seed: 7);
        p.SetTracks(new[] { @"D:\m\a.mp3", @"D:\m\b.mp3", @"D:\m\c.mp3", @"D:\m\d.mp3" }, shuffle: true);
        var playing = p.Current!;

        p.SetTracks(new[] { @"D:\m\a.mp3", @"D:\m\b.mp3", @"D:\m\c.mp3", @"D:\m\d.mp3", @"D:\m\e.mp3" },
                    shuffle: true);

        Assert.Equal(playing, p.Current);
    }

    [Fact]
    public void Rescan_to_an_empty_folder_is_harmless()
    {
        var p = Ordered(@"D:\m\a.mp3");
        p.SetTracks(System.Array.Empty<string>(), shuffle: false);
        Assert.Null(p.Current);
        Assert.Null(p.Next());
    }

    [Fact]
    public void Default_layout_contains_the_sources_widget_hidden_by_default()
    {
        var layout = WKI_Clipper.Models.WidgetSettings.DefaultLayout();
        var sources = layout.Single(w => w.Id == WKI_Clipper.Models.WidgetId.Sources);
        Assert.False(sources.Visible);
    }
}
