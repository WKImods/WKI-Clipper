using System;
using System.Collections.Generic;
using System.Linq;
using WKI_Clipper.ClickerHost;
using WKI_Clipper.Models;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Mouse-patterns design 4.2/4.4/4.6/5.1: the helper's pattern coordinator between the hotkey tracker, the player and the recorder -
/// Hold/Toggle, repeat, lock, suppression, the clipper's own windows, the program binding, the task switcher, recording and a refused
/// SendInput. Real player (on virtual time, no thread) and real recorder (drain timer on <see cref="ManualTime"/>); edges come through a
/// real <see cref="HotkeyTracker"/> fed with the bindings the coordinator returns.
/// </summary>
public sealed class PatternCoordinatorTests
{
    private const int F8 = 0x77, F9 = 0x78, R = 0x52;
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", C = "cccccccccccccccccccccccccccccccc";

    private static readonly ForegroundInfo Game = new(new IntPtr(1), 100, "UnrealWindow", @"C:\Games\game.exe", false);
    private static readonly ForegroundInfo Other = new(new IntPtr(2), 200, "Chrome_WidgetWin_1", @"C:\Apps\other.exe", false);
    private static readonly ForegroundInfo TaskView = new(new IntPtr(3), 300, "XamlExplorerHostIslandWindow", @"C:\Windows\explorer.exe", false);

    private sealed class Rig : IDisposable
    {
        public readonly FakeClock Clock = new() { Now = 1000 };
        public readonly ManualTime Time = new();
        public readonly FakeEnvironment Host = new();
        public readonly FakeInput Input;
        public readonly PatternPlayer Player;
        public readonly PatternRecorder Recorder;
        public readonly HotkeyTracker Tracker = new();
        public readonly List<ClickerMessage> Sent = new();
        public readonly PatternCoordinator Patterns;
        public readonly ClickerSettings Settings = new() { MousePatternsArmed = true };
        public IReadOnlyList<TrackedBinding> Bindings = Array.Empty<TrackedBinding>();
        public ForegroundInfo Foreground = Game;
        public int ForegroundReads;
        public bool OverClipper;
        public int StateChanges;

        public Rig()
        {
            Input = new FakeInput(Clock, Host);
            Player = new PatternPlayer(Input, new FakeWaiter(Clock), Clock, new FakePatternEnvironment(Host), ownThread: false);
            Recorder = new PatternRecorder(Clock, Time);
            Patterns = new PatternCoordinator(Player, Recorder, () => { ForegroundReads++; return Foreground; }, () => OverClipper, Sent.Add);
            Patterns.StateChanged += () => StateChanges++;
            Tracker.BindingChanged = Patterns.OnBinding;
            Push();
        }

        /// <summary>A pattern (Hold, no repeat, recorded in game.exe) with its data, pushed like a config.</summary>
        public MousePattern Add(string id, ClickerHotkey? hotkey, Action<MousePattern>? edit = null, bool withData = true)
        {
            var p = new MousePattern { Id = id, Name = id[..4], Hotkey = hotkey, Program = "game.exe" };
            edit?.Invoke(p);
            Settings.MousePatterns.Add(p);
            Push();
            if (withData) Patterns.SetData(id, p.Revision, Line());
            return p;
        }

        public void Push()
        {
            Bindings = Patterns.Configure(Settings);
            Tracker.Configure(null, null, Settings.StrictModifiers, Bindings);
        }

        public void Press(params int[] vks) { foreach (var v in vks) Tracker.OnKey(v, true); }
        public void Release(params int[] vks) { foreach (var v in vks) Tracker.OnKey(v, false); }
        public void Tap(int vk) { Press(vk); Release(vk); }

        /// <summary>Strg+Alt+R (the default record hotkey) pressed and released.</summary>
        public void RecordPress()
        {
            Press(ClickerVk.LControl, ClickerVk.LMenu, R);
            Release(R, ClickerVk.LMenu, ClickerVk.LControl);
        }

        public bool Playing(string id) => Player.IsPlaying(id);

        /// <summary>Lets the player tick for <paramref name="ms"/> of virtual time (or until nothing plays).</summary>
        public void Run(double ms) => Player.RunSync(Clock.Now + ms);

        /// <summary>Lets the player tick until nothing plays any more.</summary>
        public void RunOut() => Player.RunSync();

        public (long X, long Y) Moved => (Input.Of("moveby").Sum(e => (long)e.A), Input.Of("moveby").Sum(e => (long)e.B));

        public List<PatternRecordedMessage> Recorded => Sent.OfType<PatternRecordedMessage>().ToList();

        public void Dispose()
        {
            Recorder.Dispose();
            Player.Dispose();
        }
    }

    private static ClickerHotkey Key(int vk) => new() { Keys = { vk } };

    private static PatternTrack Bins(int n, Func<int, int> dx)
    {
        var at = new int[n]; var x = new int[n]; var y = new int[n];
        for (int i = 0; i < n; i++) (at[i], x[i]) = (i, dx(i));
        return new PatternTrack(at, x, y);
    }

    /// <summary>100 ms, 1 px to the right per ms on both tracks (Auto with linear ballistics replays the raw track as relative moves).</summary>
    private static PatternData Line()
    {
        var track = Bins(100, _ => 1);
        return new PatternData(track, track, 99);
    }

    // ---- bindings ----

    [Fact]
    public void Configure_returns_the_record_hotkey_and_the_active_patterns_with_a_hotkey()
    {
        using var r = new Rig();
        r.Add(A, Key(F8));
        r.Add(B, Key(F8), p => p.Active = false);
        r.Add(C, null);

        Assert.Equal(new[] { PatternCoordinator.RecordBindingId, A }, r.Bindings.Select(b => b.Id).ToArray());
        Assert.True(ClickerHotkey.SameCombo(r.Settings.PatternRecordHotkey, r.Bindings[0].Hotkey));

        r.Press(F8);
        Assert.True(r.Playing(A));
        Assert.False(r.Playing(B));   // an inactive pattern on the same key never starts
    }

    // ---- Hold / Toggle / repeat ----

    [Fact]
    public void Hold_plays_while_held_and_stops_on_release()
    {
        using var r = new Rig();
        r.Add(A, Key(F8), p => p.Repeat = true);

        r.Press(F8);
        Assert.True(r.Playing(A));
        Assert.Equal(A, r.Patterns.Status.Playing);
        r.Run(250);                       // repeat: still going after two and a half passes
        Assert.True(r.Playing(A));
        Assert.True(r.Moved.X > 200);

        r.Release(F8);
        Assert.False(r.Playing(A));
        Assert.Null(r.Patterns.Status.Playing);
        int sent = r.Input.Events.Count;
        r.Run(100);
        Assert.Equal(sent, r.Input.Events.Count);
    }

    [Fact]
    public void Hold_without_repeat_plays_one_pass()
    {
        using var r = new Rig();
        r.Add(A, Key(F8));

        r.Press(F8);
        r.RunOut();
        Assert.False(r.Playing(A));
        Assert.Equal((100L, 0L), r.Moved);
        Assert.Null(r.Patterns.Status.Playing);

        r.Run(500);                       // still held: no second pass
        Assert.Equal((100L, 0L), r.Moved);

        r.Release(F8);
        r.Press(F8);                      // a new press is a new pass
        Assert.True(r.Playing(A));
        r.RunOut();
        Assert.Equal((200L, 0L), r.Moved);
    }

    [Fact]
    public void Toggle_second_press_stops()
    {
        using var r = new Rig();
        r.Add(A, Key(F8), p => { p.Mode = ActivationMode.Toggle; p.Repeat = true; });

        r.Tap(F8);
        Assert.True(r.Playing(A));        // the release does not stop a toggle
        r.Run(150);
        Assert.True(r.Playing(A));

        r.Press(F8);
        Assert.False(r.Playing(A));
        r.Release(F8);
        r.Tap(F8);
        Assert.True(r.Playing(A));
    }

    [Fact]
    public void Toggle_without_repeat_stops_after_one_pass_and_the_next_press_starts_again()
    {
        using var r = new Rig();
        r.Add(A, Key(F8), p => p.Mode = ActivationMode.Toggle);

        r.Tap(F8);
        r.RunOut();
        Assert.False(r.Playing(A));

        r.Tap(F8);                        // not a stop of the finished pass: a new start
        Assert.True(r.Playing(A));
    }

    [Fact]
    public void Configure_while_held_keeps_playing_and_release_stops()
    {
        using var r = new Rig();
        // 50 ms at 1 px/ms, then 50 ms at 3 px/ms: a restart would show in the sum.
        var track = Bins(100, i => i < 50 ? 1 : 3);
        var p = r.Add(A, Key(F8), q => q.Repeat = true, withData: false);
        r.Patterns.SetData(A, 0, new PatternData(track, track, 99));

        r.Press(F8);
        r.Run(40);
        p.Speed = 4;                                       // edited while it plays: applies to the next start
        r.Settings.MousePatterns.Add(new MousePattern { Id = B, Hotkey = Key(F9) });
        r.Push();
        r.Run(50);

        Assert.True(r.Playing(A));
        var expected = new PatternCurve(track, 99).At(90).X;   // one uninterrupted playback at 1x, 90 ms in
        Assert.Equal((long)Math.Round(expected), r.Moved.X);

        r.Release(F8);
        Assert.False(r.Playing(A));
    }

    [Fact]
    public void Deactivating_or_deleting_a_playing_pattern_stops_it()
    {
        using var r = new Rig();
        r.Add(A, Key(F8), p => { p.Mode = ActivationMode.Toggle; p.Repeat = true; });
        r.Add(B, Key(F9), p => { p.Mode = ActivationMode.Toggle; p.Repeat = true; });
        r.Tap(F8);
        r.Tap(F9);

        r.Settings.MousePatterns[0].Active = false;
        r.Settings.MousePatterns.RemoveAt(1);
        r.Push();

        Assert.Empty(r.Player.Playing);
        Assert.Null(r.Patterns.Status.Playing);
    }

    // ---- data ----

    [Fact]
    public void Missing_data_never_starts()
    {
        using var r = new Rig();
        r.Add(A, Key(F8), p => p.Revision = 2, withData: false);

        r.Press(F8);                      // right after the handshake: the samples are not there yet
        Assert.False(r.Playing(A));
        r.RunOut();
        Assert.Empty(r.Input.Events);
        r.Release(F8);

        r.Patterns.SetData(A, 1, Line()); // the samples of an older recording
        r.Press(F8);
        Assert.False(r.Playing(A));
        r.Release(F8);

        r.Patterns.SetData(A, 2, Line());
        r.Press(F8);
        Assert.True(r.Playing(A));
    }

    // ---- lock, suppression, the clipper's windows ----

    [Fact]
    public void Lock_stops_playback_and_cancels_recording_synchronously()
    {
        using var r = new Rig();
        r.Add(A, Key(F8), p => p.Repeat = true);
        r.Press(F8);
        r.Run(10);

        r.Patterns.Lock();

        Assert.Empty(r.Player.Playing);   // already stopped when Lock returns
        int sent = r.Input.Events.Count;
        r.Run(50);
        Assert.Equal(sent, r.Input.Events.Count);
        r.Release(F8);
        r.Press(F8);
        Assert.False(r.Playing(A));       // refused while locked
        r.Release(F8);

        r.Patterns.Unlock();
        r.RecordPress();
        Assert.True(r.Recorder.IsRecording);
        r.Patterns.Lock();
        Assert.False(r.Recorder.IsRecording);
        Assert.False(r.Patterns.Status.Recording);
        var cancelled = Assert.Single(r.Recorded);
        Assert.Equal(nameof(PatternRecordingOutcome.Cancelled), cancelled.Outcome);
        Assert.Null(cancelled.Data);
        r.RecordPress();
        Assert.False(r.Recorder.IsRecording);

        r.Patterns.Unlock();
        r.Press(F8);
        Assert.True(r.Playing(A));
    }

    [Fact]
    public void Suppression_ignores_edges()
    {
        using var r = new Rig();
        r.Add(A, Key(F8));

        r.Patterns.OnSuppression(true);
        r.Press(F8);
        r.RecordPress();
        Assert.False(r.Playing(A));
        Assert.False(r.Recorder.IsRecording);
        r.Release(F8);

        r.Patterns.OnSuppression(false);
        r.Press(F8);
        Assert.True(r.Playing(A));
    }

    [Fact]
    public void Suppression_cancels_recording_and_stops_playback()
    {
        using var r = new Rig();
        r.Add(A, Key(F8), p => p.Repeat = true);
        r.Press(F8);

        r.Patterns.OnSuppression(true);
        Assert.False(r.Playing(A));
        r.Release(F8);

        r.Patterns.OnSuppression(false);
        r.RecordPress();
        Assert.True(r.Recorder.IsRecording);
        r.Patterns.OnSuppression(true);
        Assert.False(r.Recorder.IsRecording);
        Assert.Equal(nameof(PatternRecordingOutcome.Cancelled), Assert.Single(r.Recorded).Outcome);
    }

    [Fact]
    public void Mouse_hotkey_over_clipper_is_ignored()
    {
        using var r = new Rig();
        r.Add(A, Key(ClickerVk.XButton1), p => p.Repeat = true);
        r.Add(B, Key(F9), p => p.Repeat = true);
        r.OverClipper = true;

        r.Press(ClickerVk.XButton1);
        r.Press(F9);
        Assert.False(r.Playing(A));
        Assert.True(r.Playing(B));        // a keyboard hotkey works over the clipper too
        r.Release(ClickerVk.XButton1);

        r.OverClipper = false;
        r.Press(ClickerVk.XButton1);
        Assert.True(r.Playing(A));
        r.OverClipper = true;
        r.Release(ClickerVk.XButton1);    // a release always gets through
        Assert.False(r.Playing(A));
    }

    [Fact]
    public void A_toggled_mouse_hotkey_stops_over_the_clipper_too()
    {
        using var r = new Rig();
        r.Add(A, Key(ClickerVk.XButton1), p => { p.Mode = ActivationMode.Toggle; p.Repeat = true; });

        r.Tap(ClickerVk.XButton1);
        Assert.True(r.Playing(A));

        r.OverClipper = true;             // the path ran over the clipper: the stop press must not be eaten
        r.Press(ClickerVk.XButton1);
        Assert.False(r.Playing(A));
        r.Release(ClickerVk.XButton1);

        r.Tap(ClickerVk.XButton1);        // a start over the clipper is still refused
        Assert.False(r.Playing(A));
    }

    [Fact]
    public void A_mouse_record_hotkey_stops_the_recording_over_the_clipper_too()
    {
        using var r = new Rig();
        r.Settings.PatternRecordHotkey = Key(ClickerVk.XButton1);
        r.Push();

        r.Tap(ClickerVk.XButton1);
        Assert.True(r.Recorder.IsRecording);
        r.Recorder.OnRawMouse(1005, 3, 0);
        r.Recorder.OnPointerMove(1005, 3, 0);
        r.Clock.Now = 1100;

        r.OverClipper = true;
        r.Tap(ClickerVk.XButton1);
        Assert.False(r.Recorder.IsRecording);
        Assert.False(r.Patterns.Status.Recording);
        Assert.Equal(nameof(PatternRecordingOutcome.Ok), Assert.Single(r.Recorded).Outcome);

        r.Tap(ClickerVk.XButton1);        // a start over the clipper is still refused
        Assert.False(r.Recorder.IsRecording);
    }

    // ---- program binding and task switcher ----

    [Fact]
    public void Program_bound_pattern_needs_its_program_in_front()
    {
        using var r = new Rig();
        r.Add(A, Key(F8), p => { p.OnlyInProgram = true; p.Repeat = true; });
        r.Add(B, Key(F9), p => p.Repeat = true);   // recorded in game.exe too, but not bound
        r.Foreground = Other;

        r.Press(F8);
        r.Press(F9);
        Assert.False(r.Playing(A));
        Assert.True(r.Playing(B));
        r.Release(F8);

        r.Foreground = new ForegroundInfo(new IntPtr(4), 400, "UnrealWindow", @"D:\Steam\GAME.EXE", false);   // file name, any case
        r.Press(F8);
        Assert.True(r.Playing(A));

        r.Foreground = Other;
        r.Patterns.Poll();
        Assert.False(r.Playing(A));
        Assert.True(r.Playing(B));        // only the bound one stops on a foreground change
    }

    [Fact]
    public void Poll_stops_on_task_switcher()
    {
        using var r = new Rig();
        r.Add(A, Key(F8), p => p.Repeat = true);
        r.Add(B, Key(F9), p => p.Repeat = true);
        int reads = r.ForegroundReads;
        r.Patterns.Poll();
        Assert.Equal(reads, r.ForegroundReads);   // nothing plays: the poll looks at nothing

        r.Press(F8);
        r.Press(F9);
        r.Foreground = TaskView;
        r.Patterns.Poll();

        Assert.Empty(r.Player.Playing);
        r.Release(F8);
        r.Foreground = Game;
        r.Press(F8);
        Assert.True(r.Playing(A));
    }

    // ---- recording ----

    [Fact]
    public void Record_toggle_sends_result()
    {
        using var r = new Rig();
        int changes = r.StateChanges;

        r.RecordPress();
        Assert.True(r.Recorder.IsRecording);
        Assert.True(r.Patterns.Status.Recording);
        Assert.True(r.StateChanges > changes);
        Assert.Equal("game.exe", r.Recorder.Program);
        r.Recorder.OnRawMouse(1005, 3, -1);
        r.Recorder.OnPointerMove(1005, 3, -1);
        r.Recorder.OnRawMouse(1015, 4, 0);
        r.Recorder.OnPointerMove(1015, 4, 0);
        r.Clock.Now = 1100;
        r.RecordPress();

        Assert.False(r.Recorder.IsRecording);
        Assert.False(r.Patterns.Status.Recording);
        var m = Assert.Single(r.Recorded);
        Assert.Equal(nameof(PatternRecordingOutcome.Ok), m.Outcome);
        Assert.Equal("game.exe", m.Program);
        Assert.True(PatternCodec.TryDecode(m.Data, out var d));
        Assert.Equal((7L, -1L), d.Raw.Total);
        Assert.Equal((7L, -1L), d.Pointer.Total);
        Assert.Equal(d.DurationMs, m.DurationMs);

        r.RecordPress();
        r.RecordPress();                  // no motion in between
        var empty = r.Recorded.Last();
        Assert.Equal(nameof(PatternRecordingOutcome.Empty), empty.Outcome);
        Assert.Null(empty.Data);
    }

    [Fact]
    public void Recording_limit_stops_and_sends_Cut()
    {
        using var r = new Rig();
        r.RecordPress();
        r.Recorder.OnRawMouse(r.Clock.Now, 1, 0);

        r.Clock.Now += MousePatternRules.MaxDurationMs + PatternRecorder.DrainMs;
        r.Time.Advance(TimeSpan.FromMilliseconds(MousePatternRules.MaxDurationMs + PatternRecorder.DrainMs));

        Assert.False(r.Recorder.IsRecording);
        Assert.False(r.Patterns.Status.Recording);
        var cut = Assert.Single(r.Recorded);
        Assert.Equal(nameof(PatternRecordingOutcome.Cut), cut.Outcome);
        Assert.NotNull(cut.Data);

        r.RecordPress();                  // the next press starts a new recording, it does not stop the old one again
        Assert.True(r.Recorder.IsRecording);
        Assert.Single(r.Recorded);
    }

    [Fact]
    public void Pattern_edges_ignored_while_recording()
    {
        using var r = new Rig();
        r.Add(A, Key(F8), p => { p.Mode = ActivationMode.Toggle; p.Repeat = true; });
        r.Tap(F8);
        Assert.True(r.Playing(A));

        r.RecordPress();                  // a pattern must not move the mouse into a recording
        Assert.False(r.Playing(A));
        r.Tap(F8);
        Assert.False(r.Playing(A));

        r.RecordPress();
        r.Tap(F8);
        Assert.True(r.Playing(A));
    }

    [Fact]
    public void A_pattern_on_the_record_hotkey_never_starts_with_it()
    {
        using var r = new Rig();
        r.Add(A, Key(R), p => p.Repeat = true);   // overlaps Strg+Alt+R

        r.RecordPress();
        Assert.True(r.Recorder.IsRecording);
        r.RecordPress();                          // stops the recording; the same press does not start the pattern
        Assert.False(r.Recorder.IsRecording);
        Assert.False(r.Playing(A));

        r.Press(R);                               // R alone is the pattern's
        Assert.True(r.Playing(A));
    }

    // ---- disarm and refused input ----

    [Fact]
    public void Disarming_patterns_stops_everything()
    {
        using var r = new Rig();
        r.Add(A, Key(F8), p => p.Repeat = true);
        r.Press(F8);

        r.Settings.MousePatternsArmed = false;
        r.Push();
        Assert.False(r.Playing(A));
        Assert.Empty(r.Bindings);
        r.Release(F8);
        r.Press(F8);
        r.RecordPress();
        Assert.False(r.Playing(A));
        Assert.False(r.Recorder.IsRecording);
        r.Release(F8);

        r.Settings.MousePatternsArmed = true;
        r.Push();
        r.RecordPress();
        Assert.True(r.Recorder.IsRecording);
        r.Settings.MousePatternsArmed = false;
        r.Push();
        Assert.False(r.Recorder.IsRecording);
        Assert.Equal(nameof(PatternRecordingOutcome.Cancelled), Assert.Single(r.Recorded).Outcome);
    }

    [Fact]
    public void Blocked_send_reports_blocking_program()
    {
        using var r = new Rig();
        r.Add(A, Key(F8), p => p.Repeat = true);
        r.Foreground = new ForegroundInfo(new IntPtr(9), 900, "AdminWindow", @"C:\Tools\admin.exe", true);
        r.Input.FailEverything = true;

        r.Press(F8);
        r.Run(5);

        Assert.False(r.Playing(A));
        Assert.Equal("admin.exe", r.Patterns.Status.BlockedBy);
        Assert.Null(r.Patterns.Status.Playing);

        r.Release(F8);
        r.Input.FailEverything = false;
        r.Press(F8);                      // the next start clears it
        Assert.True(r.Playing(A));
        Assert.Null(r.Patterns.Status.BlockedBy);
    }
}
