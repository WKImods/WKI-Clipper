using System.Linq;
using System.Text.RegularExpressions;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class RecordingFeaturesTests
{
    // ---- FFmpegCommandBuilder: multi-track audio mapping ----

    [Fact]
    public void Build_single_audio_pipe_maps_one_track()
    {
        var args = FFmpegCommandBuilder.Build(new AppSettings(), "out.mp4", segmentOutput: false,
            audioPipeArgs: new[] { @"-f s16le -ar 48000 -ac 2 -i \\.\pipe\x" });
        Assert.Contains("-map 0:v", args);
        Assert.Contains("-map 1:a", args);
        Assert.DoesNotContain("-map 2:a", args);
    }

    [Fact]
    public void Build_two_audio_pipes_map_two_tracks()
    {
        var args = FFmpegCommandBuilder.Build(new AppSettings(), "out.mp4", segmentOutput: false,
            audioPipeArgs: new[] { "-i a", "-i b" });
        Assert.Contains("-map 0:v", args);
        Assert.Contains("-map 1:a", args);
        Assert.Contains("-map 2:a", args);
        Assert.DoesNotContain("-map 3:a", args);
    }

    [Fact]
    public void Build_no_audio_maps_video_only()
    {
        var args = FFmpegCommandBuilder.Build(new AppSettings(), "out.mp4", segmentOutput: false,
            audioPipeArgs: null);
        Assert.Contains("-map 0:v", args);
        Assert.DoesNotContain("-map 1:a", args);
    }

    [Fact]
    public void Build_applies_the_sync_offset_to_every_audio_pipe()
    {
        var s = new AppSettings();
        s.Audio.OffsetMilliseconds = -150;
        var args = FFmpegCommandBuilder.Build(s, "out.mp4", segmentOutput: false,
            audioPipeArgs: new[] { "-i a", "-i b" });
        Assert.Equal(2, Regex.Matches(args, "-itsoffset").Count);
    }

    [Fact]
    public void BuildConcat_maps_all_streams_to_keep_every_audio_track()
    {
        // Verified empirically: without -map 0 the concat demuxer drops the 2nd audio
        // track, so a separate-mic-track F9 clip would lose the mic.
        var args = FFmpegCommandBuilder.BuildConcat("list.txt", "out.mp4");
        Assert.Contains("-map 0", args);
        Assert.Contains("-c copy", args);
    }

    // ---- FFmpegCommandBuilder: instant GIF ----

    [Fact]
    public void BuildGif_uses_palette_and_seeks_from_the_end()
    {
        var args = FFmpegCommandBuilder.BuildGif("src.mp4", "out.gif", 5, 15, 640);
        Assert.Contains("-sseof -5", args);
        Assert.Contains("-t 5", args);
        Assert.Contains("palettegen", args);
        Assert.Contains("paletteuse", args);
        Assert.Contains("fps=15", args);
        Assert.Contains("scale=640", args);
        Assert.Contains("-loop 0", args);
    }

    [Fact]
    public void BuildGif_clamps_and_drops_scale_when_width_zero()
    {
        var args = FFmpegCommandBuilder.BuildGif("s.mp4", "o.gif", durationSec: 999, fps: 999, width: 0);
        Assert.Contains("-sseof -30", args);   // duration clamped to 30
        Assert.Contains("fps=30", args);       // fps clamped to 30
        Assert.DoesNotContain("lanczos", args);// width 0 → source width, no scale filter
    }

    // ---- AudioTrackSet: separate-track decision (no hardware touched in the ctor) ----

    private static AppSettings SepSettings(bool sep, bool mic, SystemAudioMode _)
    {
        var s = new AppSettings();
        s.Audio.SeparateMicTrack = sep;
        s.Audio.RecordMicrophone = mic;
        s.Audio.MicDeviceId = mic ? "mic-device" : "";
        return s;
    }

    [Fact]
    public void TrackSet_is_separate_only_with_toggle_and_both_sources()
    {
        using var ts = new AudioTrackSet(SepSettings(true, true, SystemAudioMode.AllAudio), SystemAudioMode.AllAudio);
        Assert.True(ts.Separate);
    }

    [Fact]
    public void TrackSet_not_separate_without_mic()
    {
        using var ts = new AudioTrackSet(SepSettings(true, false, SystemAudioMode.AllAudio), SystemAudioMode.AllAudio);
        Assert.False(ts.Separate);
    }

    [Fact]
    public void TrackSet_not_separate_without_system()
    {
        using var ts = new AudioTrackSet(SepSettings(true, true, SystemAudioMode.None), SystemAudioMode.None);
        Assert.False(ts.Separate);
    }

    [Fact]
    public void TrackSet_not_separate_when_toggle_off()
    {
        using var ts = new AudioTrackSet(SepSettings(false, true, SystemAudioMode.AllAudio), SystemAudioMode.AllAudio);
        Assert.False(ts.Separate);
    }

    // ---- Migration v4: instant-GIF hotkey ----

    [Fact]
    public void V3_gets_the_gif_hotkey_merged_in()
    {
        var s = new AppSettings { SchemaVersion = 3 };
        s.Hotkeys.Remove(HotkeyActions.SaveGif);

        bool changed = SettingsService.MigrateIfNeeded(s);

        Assert.True(changed);
        Assert.Equal(4, s.SchemaVersion);
        Assert.True(s.Hotkeys.ContainsKey(HotkeyActions.SaveGif));
        Assert.Equal(0x77u, s.Hotkeys[HotkeyActions.SaveGif].Key);   // F8
    }

    [Fact]
    public void Current_version_still_a_noop_at_v4()
    {
        var s = new AppSettings { SchemaVersion = SettingsService.CurrentSchemaVersion };
        Assert.False(SettingsService.MigrateIfNeeded(s));
    }
}
