using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NAudio.Wave;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Sound cues: the pure decisions (does a cue play, which device, own file or built-in), the
/// settings section and the embedded assets. Actual playback needs a sound card and is not tested.
/// </summary>
public sealed class SoundCueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_sounds_" + Guid.NewGuid().ToString("N"));

    public SoundCueTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static readonly DateTime T0 = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    public static IEnumerable<object[]> AllCues() => Enum.GetValues<SoundCue>().Select(c => new object[] { c });

    // ---- gate: switches ----

    [Theory, MemberData(nameof(AllCues))]
    public void Master_switch_off_blocks_every_cue(SoundCue cue)
    {
        var s = new SoundSettings { Enabled = false };
        Assert.False(new SoundCueGate().TryAcquire(s, cue, T0));
    }

    [Theory, MemberData(nameof(AllCues))]
    public void Defaults_let_every_cue_play(SoundCue cue)
    {
        Assert.True(new SoundCueGate().TryAcquire(new SoundSettings(), cue, T0));
    }

    [Fact]
    public void Each_cue_follows_its_own_switch()
    {
        var s = new SoundSettings { ClipSaved = false };
        Assert.False(SoundCuePolicy.IsCueEnabled(s, SoundCue.ClipSaved));
        Assert.True(SoundCuePolicy.IsCueEnabled(s, SoundCue.Screenshot));
        Assert.True(SoundCuePolicy.IsCueEnabled(s, SoundCue.Error));

        s = new SoundSettings { Screenshot = false };
        Assert.False(SoundCuePolicy.IsCueEnabled(s, SoundCue.Screenshot));
        Assert.True(SoundCuePolicy.IsCueEnabled(s, SoundCue.ClipSaved));

        s = new SoundSettings { Errors = false };
        Assert.False(SoundCuePolicy.IsCueEnabled(s, SoundCue.Error));
        Assert.True(SoundCuePolicy.IsCueEnabled(s, SoundCue.RecordingStarted));
    }

    [Fact]
    public void The_recording_switch_covers_start_and_stop()
    {
        var s = new SoundSettings { Recording = false };
        Assert.False(SoundCuePolicy.IsCueEnabled(s, SoundCue.RecordingStarted));
        Assert.False(SoundCuePolicy.IsCueEnabled(s, SoundCue.RecordingStopped));
        Assert.True(SoundCuePolicy.IsCueEnabled(s, SoundCue.ClipSaved));
    }

    // ---- gate: rate limit ----

    [Fact]
    public void A_cue_plays_at_most_once_per_300_ms()
    {
        var gate = new SoundCueGate();
        var s = new SoundSettings();
        Assert.True(gate.TryAcquire(s, SoundCue.ClipSaved, T0));
        Assert.False(gate.TryAcquire(s, SoundCue.ClipSaved, T0.AddMilliseconds(299)));
        Assert.True(gate.TryAcquire(s, SoundCue.ClipSaved, T0.AddMilliseconds(300)));
    }

    [Fact]
    public void The_rate_limit_is_per_cue()
    {
        var gate = new SoundCueGate();
        var s = new SoundSettings();
        Assert.True(gate.TryAcquire(s, SoundCue.ClipSaved, T0));
        Assert.True(gate.TryAcquire(s, SoundCue.Error, T0.AddMilliseconds(10)));
        Assert.True(gate.TryAcquire(s, SoundCue.Screenshot, T0.AddMilliseconds(20)));
        Assert.False(gate.TryAcquire(s, SoundCue.Error, T0.AddMilliseconds(30)));
    }

    [Fact]
    public void A_held_key_plays_every_300_ms_not_never()
    {
        // Refused attempts must not push the window forward, or a held key would go silent for good.
        var gate = new SoundCueGate();
        var s = new SoundSettings();
        int played = 0;
        for (int ms = 0; ms <= 1000; ms += 30)
            if (gate.TryAcquire(s, SoundCue.Error, T0.AddMilliseconds(ms))) played++;
        Assert.Equal(4, played);   // ~0, ~300, ~600, ~900
    }

    [Fact]
    public void A_switched_off_cue_does_not_use_up_the_slot()
    {
        var gate = new SoundCueGate();
        var s = new SoundSettings { Enabled = false };
        Assert.False(gate.TryAcquire(s, SoundCue.ClipSaved, T0));
        s.Enabled = true;
        Assert.True(gate.TryAcquire(s, SoundCue.ClipSaved, T0.AddMilliseconds(1)));
    }

    [Fact]
    public void A_clock_that_jumps_back_does_not_silence_the_cue()
    {
        var gate = new SoundCueGate();
        var s = new SoundSettings();
        Assert.True(gate.TryAcquire(s, SoundCue.ClipSaved, T0));
        Assert.True(gate.TryAcquire(s, SoundCue.ClipSaved, T0.AddMinutes(-5)));
    }

    // ---- volume ----

    [Theory]
    [InlineData(0.0, 0f)]
    [InlineData(0.5, 0.5f)]
    [InlineData(1.0, 1f)]
    [InlineData(-3.0, 0f)]
    [InlineData(7.0, 1f)]
    public void Volume_is_clamped_to_0_1(double set, float expected)
    {
        Assert.Equal(expected, SoundCuePolicy.EffectiveVolume(set));
    }

    [Fact]
    public void A_broken_volume_falls_back_to_the_default()
    {
        Assert.Equal(0.6f, SoundCuePolicy.EffectiveVolume(double.NaN));
        Assert.Equal(0.6f, SoundCuePolicy.EffectiveVolume(double.PositiveInfinity));
    }

    // ---- custom file decision ----

    [Fact]
    public void No_custom_entry_means_built_in()
    {
        Assert.Null(SoundCuePolicy.PickCustomPath(new SoundSettings(), SoundCue.ClipSaved, _ => true));
    }

    [Fact]
    public void A_playable_custom_wav_is_picked_by_cue_name()
    {
        var s = new SoundSettings();
        s.CustomFiles["ClipSaved"] = @"C:\x\mine.wav";
        Assert.Equal(@"C:\x\mine.wav", SoundCuePolicy.PickCustomPath(s, SoundCue.ClipSaved, _ => true));
        Assert.Null(SoundCuePolicy.PickCustomPath(s, SoundCue.Error, _ => true));
    }

    [Fact]
    public void An_unplayable_custom_file_falls_back_to_built_in()
    {
        var s = new SoundSettings();
        s.CustomFiles["Error"] = @"C:\x\broken.wav";
        Assert.Null(SoundCuePolicy.PickCustomPath(s, SoundCue.Error, _ => false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\x\song.mp3")]
    [InlineData(@"C:\x\noextension")]
    public void Blank_or_non_wav_entries_never_reach_the_probe(string path)
    {
        var s = new SoundSettings();
        s.CustomFiles["Screenshot"] = path;
        bool probed = false;
        Assert.Null(SoundCuePolicy.PickCustomPath(s, SoundCue.Screenshot, _ => { probed = true; return true; }));
        Assert.False(probed);
    }

    [Fact]
    public void The_wav_extension_is_case_insensitive()
    {
        var s = new SoundSettings();
        s.CustomFiles["RecordingStarted"] = @"C:\x\LOUD.WAV";
        Assert.Equal(@"C:\x\LOUD.WAV", SoundCuePolicy.PickCustomPath(s, SoundCue.RecordingStarted, _ => true));
    }

    [Fact]
    public void A_null_custom_file_map_means_built_in()
    {
        var s = new SoundSettings { CustomFiles = null! };
        Assert.Null(SoundCuePolicy.PickCustomPath(s, SoundCue.ClipSaved, _ => true));
    }

    // ---- device decision ----

    private static readonly IReadOnlyList<(string Id, string Name)> Devices = new List<(string, string)>
    {
        ("{0.0.0.00000000}.{aaa}", "Speakers (Realtek)"),
        ("{0.0.0.00000000}.{bbb}", "Headset (Arctis 7)"),
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_device_selected_means_the_windows_default(string? wanted)
    {
        Assert.Equal(-1, SoundCuePolicy.PickDeviceIndex(Devices, wanted));
    }

    [Fact]
    public void A_device_is_found_by_id_or_by_friendly_name()
    {
        Assert.Equal(1, SoundCuePolicy.PickDeviceIndex(Devices, "{0.0.0.00000000}.{bbb}"));
        Assert.Equal(0, SoundCuePolicy.PickDeviceIndex(Devices, "speakers (realtek)"));
    }

    [Fact]
    public void An_unplugged_device_falls_back_to_the_default()
    {
        Assert.Equal(-1, SoundCuePolicy.PickDeviceIndex(Devices, "{0.0.0.00000000}.{gone}"));
        Assert.Equal(-1, SoundCuePolicy.PickDeviceIndex(Array.Empty<(string, string)>(), "{x}"));
    }

    [Fact]
    public void An_id_match_wins_over_a_name_match()
    {
        var devices = new List<(string Id, string Name)> { ("A", "B"), ("B", "A") };
        Assert.Equal(1, SoundCuePolicy.PickDeviceIndex(devices, "B"));
    }

    // ---- settings ----

    [Fact]
    public void Sound_defaults()
    {
        var s = new AppSettings().Sounds;
        Assert.True(s.Enabled);
        Assert.Equal(0.6, s.Volume);
        Assert.Null(s.DeviceId);
        Assert.True(s.ClipSaved);
        Assert.True(s.Screenshot);
        Assert.True(s.Recording);
        Assert.True(s.Errors);
        Assert.NotNull(s.CustomFiles);
        Assert.Empty(s.CustomFiles);
    }

    // Output folders point into the temp dir so Load()'s EnsureOutputDirs never touches the real Videos folder.
    private string WriteSettingsFile(string soundsFragment)
    {
        string Esc(string p) => JsonSerializer.Serialize(p);
        var json = "{ \"SchemaVersion\": " + SettingsService.CurrentSchemaVersion + ", " +
                   "\"Output\": { \"ClipsFolder\": " + Esc(Path.Combine(_dir, "c")) +
                   ", \"ScreenshotsFolder\": " + Esc(Path.Combine(_dir, "s")) +
                   ", \"BufferFolder\": " + Esc(Path.Combine(_dir, "b")) + " }" +
                   soundsFragment + " }";
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void A_settings_file_without_the_section_loads_with_defaults_and_keeps_its_version()
    {
        WriteSettingsFile("");
        var svc = new SettingsService(_dir);
        var loaded = svc.Load();

        Assert.Equal(SettingsService.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.NotNull(loaded.Sounds);
        Assert.True(loaded.Sounds.Enabled);
        Assert.Equal(0.6, loaded.Sounds.Volume);
        Assert.NotNull(loaded.Sounds.CustomFiles);
    }

    [Fact]
    public void An_explicit_null_section_loads_with_defaults()
    {
        WriteSettingsFile(", \"Sounds\": null");
        var loaded = new SettingsService(_dir).Load();
        Assert.NotNull(loaded.Sounds);
        Assert.True(loaded.Sounds.Enabled);
    }

    [Fact]
    public void An_explicit_null_custom_file_map_loads_as_an_empty_map()
    {
        WriteSettingsFile(", \"Sounds\": { \"Volume\": 0.25, \"CustomFiles\": null }");
        var loaded = new SettingsService(_dir).Load();
        Assert.Equal(0.25, loaded.Sounds.Volume);
        Assert.NotNull(loaded.Sounds.CustomFiles);
        Assert.Empty(loaded.Sounds.CustomFiles);
    }

    [Fact]
    public void A_partial_section_keeps_the_other_defaults()
    {
        WriteSettingsFile(", \"Sounds\": { \"Errors\": false }");
        var loaded = new SettingsService(_dir).Load();
        Assert.False(loaded.Sounds.Errors);
        Assert.True(loaded.Sounds.Enabled);
        Assert.True(loaded.Sounds.ClipSaved);
        Assert.Equal(0.6, loaded.Sounds.Volume);
    }

    [Fact]
    public void The_section_survives_a_save_and_load()
    {
        WriteSettingsFile("");
        var svc = new SettingsService(_dir);
        svc.Load();
        svc.Current.Sounds.Enabled = false;
        svc.Current.Sounds.Volume = 0.3;
        svc.Current.Sounds.DeviceId = "{dev}";
        svc.Current.Sounds.CustomFiles["Error"] = @"C:\x\e.wav";
        svc.Save();

        var again = new SettingsService(_dir).Load();
        Assert.False(again.Sounds.Enabled);
        Assert.Equal(0.3, again.Sounds.Volume);
        Assert.Equal("{dev}", again.Sounds.DeviceId);
        Assert.Equal(@"C:\x\e.wav", again.Sounds.CustomFiles["Error"]);
    }

    // ---- embedded assets ----

    [Fact]
    public void Every_cue_has_its_own_embedded_resource()
    {
        var names = typeof(SoundCueService).Assembly.GetManifestResourceNames();
        var wanted = Enum.GetValues<SoundCue>().Select(SoundCueResources.ResourceName).ToList();

        Assert.Equal(wanted.Count, wanted.Distinct().Count());
        foreach (var n in wanted) Assert.Contains(n, names);
    }

    [Fact]
    public void There_are_no_unused_embedded_sounds()
    {
        var wanted = Enum.GetValues<SoundCue>().Select(SoundCueResources.ResourceName).ToHashSet();
        var sounds = typeof(SoundCueService).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(SoundCueResources.Prefix, StringComparison.Ordinal));
        foreach (var n in sounds) Assert.Contains(n, wanted);
    }

    [Theory, MemberData(nameof(AllCues))]
    public void Embedded_sounds_are_valid_short_mono_pcm_wavs(SoundCue cue)
    {
        var bytes = SoundCueResources.Load(cue);
        Assert.NotNull(bytes);

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes!, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes!, 8, 4));
        Assert.Equal("fmt ", System.Text.Encoding.ASCII.GetString(bytes!, 12, 4));
        Assert.Equal(1, BitConverter.ToUInt16(bytes!, 20));        // PCM
        Assert.Equal(1, BitConverter.ToUInt16(bytes!, 22));        // mono
        Assert.Equal(44100, BitConverter.ToInt32(bytes!, 24));
        Assert.Equal(16, BitConverter.ToUInt16(bytes!, 34));

        using var reader = new WaveFileReader(new MemoryStream(bytes!));
        Assert.InRange(reader.TotalTime.TotalSeconds, 0.05, 1.0);
        Assert.True(SoundWav.IsPlayable(bytes!));
    }

    // ---- WAV validation ----

    private static byte[] MakeWav(int seconds, int sampleRate = 8000)
    {
        using var ms = new MemoryStream();
        using (var w = new WaveFileWriter(ms, new WaveFormat(sampleRate, 16, 1)))
            w.Write(new byte[sampleRate * 2 * seconds], 0, sampleRate * 2 * seconds);
        return ms.ToArray();
    }

    [Fact]
    public void A_real_wav_is_playable()
    {
        Assert.True(SoundWav.IsPlayable(MakeWav(1)));
    }

    [Fact]
    public void Garbage_empty_and_truncated_data_are_not_playable()
    {
        Assert.False(SoundWav.IsPlayable(Array.Empty<byte>()));
        Assert.False(SoundWav.IsPlayable(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }));
        Assert.False(SoundWav.IsPlayable(System.Text.Encoding.ASCII.GetBytes("RIFF\0\0\0\0WAVEfmt ")));
        var wav = MakeWav(1);
        Assert.False(SoundWav.IsPlayable(wav.Take(30).ToArray()));
    }

    [Fact]
    public void A_wav_without_samples_is_not_playable()
    {
        Assert.False(SoundWav.IsPlayable(MakeWav(0)));
    }

    [Fact]
    public void A_wav_longer_than_the_limit_is_not_playable()
    {
        Assert.False(SoundWav.IsPlayable(MakeWav(SoundWav.MaxSeconds + 1)));
        Assert.True(SoundWav.IsPlayable(MakeWav(SoundWav.MaxSeconds)));
    }

    [Fact]
    public void Reading_a_file_reports_why_it_cannot_be_used()
    {
        var good = Path.Combine(_dir, "good.wav");
        var garbage = Path.Combine(_dir, "garbage.wav");
        File.WriteAllBytes(good, MakeWav(1));
        File.WriteAllText(garbage, "RIFF but really not a wave file at all, just some text to pad the size out");

        Assert.NotNull(SoundWav.TryReadFile(good, out var okProblem));
        Assert.Null(okProblem);

        Assert.Null(SoundWav.TryReadFile(garbage, out var badProblem));
        Assert.False(string.IsNullOrEmpty(badProblem));

        Assert.Null(SoundWav.TryReadFile(Path.Combine(_dir, "gone.wav"), out var missingProblem));
        Assert.Equal("file not found", missingProblem);
    }

    // ---- service: which bytes would play ----

    private sealed class FakeOutput : ISoundOutput
    {
        public readonly List<(byte[] Wav, float Volume, string? Device)> Calls = new();
        public Exception? Throws;
        public bool Disposed;

        public Task PlayAsync(byte[] wav, float volume, string? deviceId)
        {
            Calls.Add((wav, volume, deviceId));
            if (Throws != null) throw Throws;
            return Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;
    }

    // Runs the "background" work inline, so every assertion about what did or did not play is deterministic.
    private SoundCueService NewService(Action<SoundSettings>? configure = null, FakeOutput? output = null)
    {
        var settings = new SettingsService(_dir);
        configure?.Invoke(settings.Current.Sounds);
        return new SoundCueService(settings, output ?? new FakeOutput(), schedule: a => a());
    }

    [Fact]
    public void Without_a_custom_file_the_built_in_sound_plays()
    {
        using var svc = NewService();
        Assert.Equal(SoundCueResources.Load(SoundCue.ClipSaved), svc.ResolveBytes(SoundCue.ClipSaved));
    }

    [Fact]
    public void A_valid_custom_wav_replaces_the_built_in_sound()
    {
        var path = Path.Combine(_dir, "mine.wav");
        var mine = MakeWav(1);
        File.WriteAllBytes(path, mine);

        using var svc = NewService(s => s.CustomFiles["Error"] = path);
        Assert.Equal(mine, svc.ResolveBytes(SoundCue.Error));
        Assert.Equal(SoundCueResources.Load(SoundCue.ClipSaved), svc.ResolveBytes(SoundCue.ClipSaved));
    }

    [Fact]
    public void A_missing_or_broken_custom_file_falls_back_to_the_built_in_sound()
    {
        var broken = Path.Combine(_dir, "broken.wav");
        File.WriteAllText(broken, "this is not a wave file");

        using var svc = NewService(s =>
        {
            s.CustomFiles["Error"] = Path.Combine(_dir, "gone.wav");
            s.CustomFiles["Screenshot"] = broken;
        });
        Assert.Equal(SoundCueResources.Load(SoundCue.Error), svc.ResolveBytes(SoundCue.Error));
        Assert.Equal(SoundCueResources.Load(SoundCue.Screenshot), svc.ResolveBytes(SoundCue.Screenshot));
    }

    [Fact]
    public void An_edited_custom_file_is_picked_up_without_a_restart()
    {
        var path = Path.Combine(_dir, "mine.wav");
        File.WriteAllBytes(path, MakeWav(1));
        using var svc = NewService(s => s.CustomFiles["ClipSaved"] = path);
        Assert.Equal(MakeWav(1).Length, svc.ResolveBytes(SoundCue.ClipSaved)!.Length);

        var longer = MakeWav(2);
        File.WriteAllBytes(path, longer);
        Assert.Equal(longer.Length, svc.ResolveBytes(SoundCue.ClipSaved)!.Length);

        File.Delete(path);
        Assert.Equal(SoundCueResources.Load(SoundCue.ClipSaved), svc.ResolveBytes(SoundCue.ClipSaved));
    }

    [Fact]
    public void An_unknown_cue_has_no_sound()
    {
        using var svc = NewService();
        Assert.Null(svc.ResolveBytes((SoundCue)999));
        Assert.Null(SoundCueResources.Load((SoundCue)999));
    }

    // ---- service: play flow ----

    [Fact]
    public void Play_hands_the_sound_volume_and_device_to_the_output()
    {
        var output = new FakeOutput();
        using var svc = NewService(s => { s.Volume = 0.25; s.DeviceId = "{dev}"; }, output);

        svc.Play(SoundCue.Screenshot);

        var call = Assert.Single(output.Calls);
        Assert.Equal(SoundCueResources.Load(SoundCue.Screenshot), call.Wav);
        Assert.Equal(0.25f, call.Volume);
        Assert.Equal("{dev}", call.Device);
    }

    [Fact]
    public void Play_respects_the_master_and_the_per_cue_switch()
    {
        var output = new FakeOutput();
        using var svc = NewService(s => s.Errors = false, output);
        svc.Play(SoundCue.Error);
        Assert.Empty(output.Calls);

        var off = new FakeOutput();
        using var svc2 = NewService(s => s.Enabled = false, off);
        svc2.Play(SoundCue.ClipSaved);
        Assert.Empty(off.Calls);
    }

    [Fact]
    public void A_burst_of_the_same_cue_plays_once()
    {
        var output = new FakeOutput();
        using var svc = NewService(output: output);
        for (int i = 0; i < 10; i++) svc.Play(SoundCue.ClipSaved);
        Assert.Single(output.Calls);
    }

    [Fact]
    public void Different_cues_can_overlap()
    {
        var output = new FakeOutput();
        using var svc = NewService(output: output);
        svc.Play(SoundCue.ClipSaved);
        svc.Play(SoundCue.Error);
        svc.Play(SoundCue.Screenshot);
        Assert.Equal(3, output.Calls.Count);
    }

    [Fact]
    public void Preview_plays_even_when_the_cue_is_switched_off_and_is_not_rate_limited()
    {
        var output = new FakeOutput();
        using var svc = NewService(s => { s.Enabled = false; s.Errors = false; }, output);
        svc.Preview(SoundCue.Error);
        svc.Preview(SoundCue.Error);
        Assert.Equal(2, output.Calls.Count);
    }

    [Fact]
    public void A_failing_output_never_reaches_the_caller()
    {
        var output = new FakeOutput { Throws = new InvalidOperationException("no sound card") };
        using var svc = NewService(output: output);
        var ex = Record.Exception(() => { svc.Play(SoundCue.ClipSaved); svc.Preview(SoundCue.Error); });
        Assert.Null(ex);
        Assert.NotEmpty(output.Calls);
    }

    [Fact]
    public void A_disposed_service_plays_nothing_and_releases_the_output()
    {
        var output = new FakeOutput();
        var svc = NewService(output: output);
        svc.Dispose();

        var ex = Record.Exception(() => { svc.Play(SoundCue.ClipSaved); svc.Preview(SoundCue.Error); svc.Dispose(); });

        Assert.Null(ex);
        Assert.Empty(output.Calls);
        Assert.True(output.Disposed);
    }

    [Fact]
    public void Play_survives_broken_settings()
    {
        var settings = new SettingsService(_dir);
        settings.Current.Sounds = null!;
        using var svc = new SoundCueService(settings, new FakeOutput(), schedule: a => a());
        Assert.Null(Record.Exception(() => svc.Play(SoundCue.ClipSaved)));
    }
}
