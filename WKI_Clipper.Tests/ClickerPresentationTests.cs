using System;
using System.Linq;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>The words and the on-screen rules around the clicker: toasts, status pill, the "AutoClicker · 25 cps" label and the helper's command line.</summary>
public sealed class ClickerPresentationTests
{
    private static ClickerRunInfo Run(StopReason reason, string? detail = null, string? lockedBy = null)
        => new(new RunRecord { Clicks = 10, Seconds = 2, StopReason = reason.ToString() }, reason, detail, lockedBy);

    private static ClickerState State(ClickerPhase phase, Func<ClickerState, ClickerState>? edit = null)
    {
        var s = ClickerState.Off with { Phase = phase };
        return edit is null ? s : edit(s);
    }

    // ---- release process command line ----

    [Fact]
    public void The_release_command_line_round_trips_and_follows_the_output()
    {
        var mouse = ClickerRelease.For(new ClickerSettings { MouseButton = ClickerMouseButton.Middle });
        Assert.Equal(new ClickerRelease(ClickerMouseButton.Middle, 0, false), mouse);
        Assert.Equal(new[] { "--clicker-release", "Middle", "0", "0" }, mouse.ToArguments());

        var keys = ClickerRelease.For(new ClickerSettings { InputType = ClickerInput.Keyboard, KeyboardVk = 0x41 });
        Assert.Equal(new ClickerRelease(null, 0x41, true), keys);
        Assert.Equal(keys, ClickerRelease.TryParse(keys.ToArguments().ToArray()));
        Assert.Equal(mouse, ClickerRelease.TryParse(mouse.ToArguments().ToArray()));
    }

    [Theory]
    [InlineData("--clicker-release Left 0")]
    [InlineData("--clicker-release Up 0 0")]
    [InlineData("--clicker-release 7 0 0")]
    [InlineData("--clicker-release - -1 0")]
    [InlineData("--clicker-release - 300 0")]
    [InlineData("--clicker-release - 0 2")]
    [InlineData("--clicker-host - 0 0")]
    public void A_broken_release_command_line_is_refused(string line)
    {
        var args = line.Split(' ');
        Assert.Null(ClickerRelease.TryParse(args));
        Assert.Equal(args[0] == ClickerRelease.Switch, ClickerRelease.IsInvocation(args));
    }

    // ---- toasts ----

    [Fact]
    public void A_lock_that_stopped_a_run_names_the_game_and_warns()
    {
        var n = ClickerNotices.ForRunEnd(Run(StopReason.Locked, lockedBy: "Arma Reforger"))!;

        Assert.Equal("AutoClicker gestoppt - Arma Reforger steht auf der Blacklist", n.Body);
        Assert.True(n.IsWarning);
    }

    [Fact]
    public void A_lock_without_a_known_game_still_makes_a_sentence()
        => Assert.Equal("AutoClicker gestoppt - einem Spiel steht auf der Blacklist", ClickerNotices.ForRunEnd(Run(StopReason.Locked))!.Body);

    [Theory]
    [InlineData(StopReason.ClickLimit, "1000", "AutoClicker gestoppt - Klick-Limit erreicht (1000)")]
    [InlineData(StopReason.TimeLimit, "60 s", "AutoClicker gestoppt - Zeitlimit erreicht (60 s)")]
    public void A_limit_end_is_an_info_toast_with_the_limit(StopReason reason, string detail, string body)
    {
        var n = ClickerNotices.ForRunEnd(Run(reason, detail))!;

        Assert.Equal(body, n.Body);
        Assert.False(n.IsWarning);
    }

    [Theory]
    [InlineData(StopReason.Hotkey)]
    [InlineData(StopReason.HoldReleased)]
    [InlineData(StopReason.MasterSwitch)]
    [InlineData(StopReason.StopZone)]
    [InlineData(StopReason.CornerTopLeft)]
    [InlineData(StopReason.EdgeLeft)]
    [InlineData(StopReason.TaskSwitcher)]
    [InlineData(StopReason.NotOnWhitelist)]
    [InlineData(StopReason.AllPointsDone)]
    [InlineData(StopReason.LeftStartZone)]
    [InlineData(StopReason.PickerOpened)]
    [InlineData(StopReason.ClipperClosed)]
    [InlineData(StopReason.Manual)]
    [InlineData(StopReason.None)]
    public void Every_other_end_shows_only_in_the_state_pill(StopReason reason)
        => Assert.Null(ClickerNotices.ForRunEnd(Run(reason)));

    // ---- status pill ----

    [Fact]
    public void The_pill_says_what_the_clicker_is_doing()
    {
        Assert.Equal("Aus", ClickerStateText.Pill(ClickerState.Off));
        Assert.Equal("Startet …", ClickerStateText.Pill(State(ClickerPhase.Starting)));
        Assert.Equal("Bereit", ClickerStateText.Pill(State(ClickerPhase.Ready)));
        Assert.Equal("Läuft · 25 cps · 1.234 Klicks",
            ClickerStateText.Pill(State(ClickerPhase.Running, s => s with { Cps = 25, Clicks = 1234 })));
    }

    [Fact]
    public void A_ready_clicker_whose_master_switch_is_off_says_so()
        => Assert.Equal("Bereit · Master-Schalter aus",
            ClickerStateText.Pill(State(ClickerPhase.Ready, s => s with { MasterAllowed = false, LastStop = StopReason.MasterSwitch })));

    [Fact]
    public void A_stopped_clicker_names_the_reason_with_its_detail()
    {
        var stopped = State(ClickerPhase.Ready, s => s with { LastStop = StopReason.ClickLimit, LastStopDetail = "500" });

        Assert.Equal("Gestoppt: Klick-Limit erreicht (500)", ClickerStateText.Pill(stopped));
    }

    [Fact]
    public void A_lock_wins_over_ready_and_names_the_game()
    {
        var locked = State(ClickerPhase.Ready, s => s with { Locked = true, LockedBy = "Arma Reforger", LastStop = StopReason.Locked });

        Assert.Equal("Gesperrt: Arma Reforger auf der Blacklist", ClickerStateText.Pill(locked));
        Assert.Equal("Gesperrt (Blacklist)", ClickerStateText.Pill(locked with { LockedBy = null }));
    }

    [Fact]
    public void An_elevated_window_in_front_is_named_instead_of_the_click_count()
    {
        var blocked = State(ClickerPhase.Running, s => s with { Cps = 25, Clicks = 5, BlockedByElevated = "taskmgr.exe" });

        Assert.Equal("Ein Admin-Fenster (taskmgr.exe) blockiert die Klicks", ClickerStateText.Pill(blocked));
    }

    [Theory]
    [InlineData(ClickerFault.StartFailed)]
    [InlineData(ClickerFault.HandshakeFailed)]
    [InlineData(ClickerFault.Crashed)]
    [InlineData(ClickerFault.LockNotConfirmed)]
    public void Every_fault_has_a_text(ClickerFault fault)
    {
        var text = ClickerStateText.Pill(State(ClickerPhase.Error, s => s with { Fault = fault }));

        Assert.StartsWith("Der AutoClicker-Helfer", text);
    }

    // ---- the label ----

    [Theory]
    [InlineData(25, "AutoClicker · 25 cps")]
    [InlineData(1, "AutoClicker · 1 cps")]
    [InlineData(2.5, "AutoClicker · 2,5 cps")]
    [InlineData(10.004, "AutoClicker · 10 cps")]
    [InlineData(500, "AutoClicker · 500 cps")]
    [InlineData(1000, "AutoClicker · 1000 cps")]
    [InlineData(0.5, "AutoClicker · 30/min")]
    [InlineData(1.0 / 60, "AutoClicker · 1/min")]
    [InlineData(1.0 / 3600, "AutoClicker · 1/h")]
    [InlineData(1.0 / 86400, "AutoClicker · 0,04/h")]
    [InlineData(0, "AutoClicker · 0 cps")]
    [InlineData(double.NaN, "AutoClicker · 0 cps")]
    [InlineData(-5, "AutoClicker · 0 cps")]
    public void The_label_text_reads_naturally_at_any_rate(double cps, string expected)
        => Assert.Equal(expected, ClickerIndicatorRules.Text(cps));

    private static AppSettings WithAmf(Action<AppSettings>? edit = null)
    {
        var s = new AppSettings();
        s.Video.UseAmfCapture = true;
        s.Video.Codec = "h264_amf";
        s.Video.Resolution = ResolutionPreset.Native;
        // The default WhatsApp widget is capture-excluded, which alone keeps the buffer on ddagrab; AMD's capture needs it off.
        foreach (var w in s.Widgets.Widgets) w.ExcludeFromCapture = false;
        edit?.Invoke(s);
        return s;
    }

    [Fact]
    public void The_label_is_hidden_while_amds_own_capture_would_record_it()
    {
        Assert.False(ClickerIndicatorRules.CanShow(WithAmf()));
        Assert.True(ClickerIndicatorRules.CanShow(new AppSettings()));   // ddagrab honours the exclusion
    }

    [Fact]
    public void The_label_may_show_when_something_else_already_keeps_the_buffer_on_ddagrab()
    {
        Assert.True(ClickerIndicatorRules.CanShow(WithAmf(s => s.Crosshair.Enabled = true)));
        Assert.True(ClickerIndicatorRules.CanShow(WithAmf(s => { s.PerfOverlay.Enabled = true; s.PerfOverlay.HideFromCapture = true; })));
        Assert.True(ClickerIndicatorRules.CanShow(WithAmf(s => s.Widgets.GetOrAdd(WidgetId.WhatsApp).ExcludeFromCapture = true)));
    }

    [Fact]
    public void The_label_may_show_when_amf_is_not_configured_at_all()
    {
        Assert.True(ClickerIndicatorRules.CanShow(WithAmf(s => s.Video.UseAmfCapture = false)));
        Assert.True(ClickerIndicatorRules.CanShow(WithAmf(s => s.Video.Codec = "libx264")));

        // ... and with untouched settings: the default WhatsApp widget is capture-excluded, which keeps the buffer on ddagrab already
        var untouched = new AppSettings();
        untouched.Video.UseAmfCapture = true;
        Assert.True(ClickerIndicatorRules.CanShow(untouched));
    }

    [Fact]
    public void The_label_shows_only_while_running_wanted_and_hideable()
    {
        var running = State(ClickerPhase.Running);
        var settings = new AppSettings();

        Assert.True(ClickerIndicatorRules.ShouldShow(running, settings));
        Assert.False(ClickerIndicatorRules.ShouldShow(State(ClickerPhase.Ready), settings));
        Assert.False(ClickerIndicatorRules.ShouldShow(ClickerState.Off, settings));

        settings.AutoClicker.Clicker.ShowActiveIndicator = false;
        Assert.False(ClickerIndicatorRules.ShouldShow(running, settings));

        Assert.False(ClickerIndicatorRules.ShouldShow(running, WithAmf()));
    }

    // ---- mouse patterns on the label ----

    private static readonly string IdA = new('a', 32);
    private static readonly string IdB = new('b', 32);
    private static readonly string IdC = new('c', 32);

    private static AppSettings WithPatterns(AppSettings? settings = null)
    {
        var s = settings ?? new AppSettings();
        s.AutoClicker.Clicker.MousePatterns.Add(new MousePattern { Id = IdA, Name = "Kreis" });
        s.AutoClicker.Clicker.MousePatterns.Add(new MousePattern { Id = IdB, Name = "Welle" });
        return s;
    }

    [Fact]
    public void Indicator_shows_recording_even_with_indicator_option_off()
    {
        var settings = WithPatterns();
        settings.AutoClicker.Clicker.ShowActiveIndicator = false;
        var recording = State(ClickerPhase.Ready, s => s with { Recording = true });

        Assert.True(ClickerIndicatorRules.ShouldShow(recording, settings));
        Assert.Equal("Aufnahme läuft · Strg+Alt+R beendet", ClickerIndicatorRules.Text(recording, settings));

        // A playing pattern and a running clicker follow the option.
        Assert.False(ClickerIndicatorRules.ShouldShow(State(ClickerPhase.Running, s => s with { Playing = IdA }), settings));
        settings.AutoClicker.Clicker.ShowActiveIndicator = true;
        Assert.True(ClickerIndicatorRules.ShouldShow(State(ClickerPhase.Ready, s => s with { Playing = IdA }), settings));
        Assert.False(ClickerIndicatorRules.ShouldShow(State(ClickerPhase.Ready), settings));
    }

    [Fact]
    public void Indicator_text_joins_clicker_and_pattern()
    {
        var settings = WithPatterns();

        Assert.Equal("AutoClicker · 25 cps  ·  Mausbewegung · Kreis +1",
            ClickerIndicatorRules.Text(State(ClickerPhase.Running, s => s with { Cps = 25, Playing = IdA + "," + IdB }), settings));
        Assert.Equal("Mausbewegung · Welle", ClickerIndicatorRules.Text(State(ClickerPhase.Ready, s => s with { Playing = IdB.ToUpperInvariant() }), settings));
        Assert.Equal("AutoClicker · 25 cps", ClickerIndicatorRules.Text(State(ClickerPhase.Running, s => s with { Cps = 25 }), settings));

        // A pattern deleted meanwhile has no name: the next one names the label, or none at all.
        Assert.Equal("Mausbewegung · Welle +1", ClickerIndicatorRules.Text(State(ClickerPhase.Ready, s => s with { Playing = IdC + "," + IdB }), settings));
        Assert.Equal("Mausbewegung", ClickerIndicatorRules.Text(State(ClickerPhase.Ready, s => s with { Playing = IdC }), settings));

        settings.AutoClicker.Clicker.PatternRecordHotkey = new ClickerHotkey { Keys = { ClickerVk.XButton1 } };
        Assert.Equal("AutoClicker · 3 cps  ·  Aufnahme läuft · Maus 4 beendet",
            ClickerIndicatorRules.Text(State(ClickerPhase.Running, s => s with { Cps = 3, Recording = true }), settings));
    }

    [Fact]
    public void Indicator_hidden_under_amf_capture()
    {
        var amf = WithPatterns(WithAmf());
        amf.AutoClicker.Clicker.ShowActiveIndicator = true;

        Assert.False(ClickerIndicatorRules.ShouldShow(State(ClickerPhase.Ready, s => s with { Recording = true }), amf));
        Assert.False(ClickerIndicatorRules.ShouldShow(State(ClickerPhase.Ready, s => s with { Playing = IdA }), amf));
        Assert.True(ClickerIndicatorRules.ShouldShow(State(ClickerPhase.Ready, s => s with { Recording = true }), WithPatterns()));
    }

    // ---- recording toasts ----

    [Theory]
    [InlineData("Ok", "Muster 3", 2.4, "'Muster 3' aufgenommen (2,4 s)", false)]
    [InlineData("Ok", "Kreis", 0.04, "'Kreis' aufgenommen (0 s)", false)]
    [InlineData("Cut", "Kreis", 60, "'Kreis' aufgenommen (60 s) - nach 60 s abgeschnitten", false)]
    [InlineData("Empty", null, 0, "Keine Bewegung aufgenommen", true)]
    [InlineData("Cancelled", null, 0, "Aufnahme verworfen", true)]
    [InlineData(PatternRecordedInfo.Full, null, 0, "Aufnahme verworfen - es gibt schon 50 Muster", true)]
    [InlineData(PatternRecordedInfo.Failed, null, 0, "Aufnahme konnte nicht gespeichert werden", true)]
    public void A_finished_recording_gets_a_toast(string outcome, string? name, double seconds, string body, bool warning)
    {
        var n = ClickerNotices.ForPatternRecorded(new PatternRecordedInfo(outcome, name, seconds))!;

        Assert.Equal("Mausbewegung", n.Title);
        Assert.Equal(body, n.Body);
        Assert.Equal(warning, n.IsWarning);
    }

    [Fact]
    public void An_unknown_recording_outcome_gets_no_toast()
        => Assert.Null(ClickerNotices.ForPatternRecorded(new PatternRecordedInfo("FromTheFuture", null, 0)));

    [Fact]
    public void The_label_never_changes_what_the_capture_path_must_honour()
    {
        // It asks the capture path, it does not steer it: a label that flipped this would restart the replay buffer when it appears.
        var plain = WithAmf();
        var before = FFmpegCommandBuilder.MustHonorCaptureExclusion(plain);

        plain.AutoClicker.Clicker.Armed = true;
        plain.AutoClicker.Clicker.ShowActiveIndicator = true;

        Assert.Equal(before, FFmpegCommandBuilder.MustHonorCaptureExclusion(plain));
        Assert.False(FFmpegCommandBuilder.MustHonorCaptureExclusion(plain));
    }

    // ---- the helper's command line ----

    [Fact]
    public void The_helper_is_the_same_exe_in_host_mode()
    {
        var cmd = HostCommand.Build(@"C:\Apps\WKI_Clipper.exe", @"C:\Apps\WKI_Clipper.dll", "WKI_Clipper.Clicker.12.abcdef0123456789", 12);

        Assert.Equal(@"C:\Apps\WKI_Clipper.exe", cmd.FileName);
        Assert.Equal(new[] { "--clicker-host", "WKI_Clipper.Clicker.12.abcdef0123456789", "12" }, cmd.Arguments.ToArray());
    }

    [Fact]
    public void Under_the_dotnet_host_the_assembly_comes_first()
    {
        var cmd = HostCommand.Build(@"C:\Program Files\dotnet\dotnet.exe", @"C:\src\bin\WKI_Clipper.dll", "WKI_Clipper.Clicker.12.abcdef0123456789", 12);

        Assert.Equal(@"C:\Program Files\dotnet\dotnet.exe", cmd.FileName);
        Assert.Equal(new[] { @"C:\src\bin\WKI_Clipper.dll", "--clicker-host", "WKI_Clipper.Clicker.12.abcdef0123456789", "12" }, cmd.Arguments.ToArray());
    }

    [Fact]
    public void The_command_line_is_what_the_helper_accepts()
    {
        string pipe = ClickerProtocol.NewPipeName(4321);
        var cmd = HostCommand.Build(@"C:\Apps\WKI_Clipper.exe", null, pipe, 4321);

        var parsed = WKI_Clipper.ClickerHost.HostMain.TryParseArgs(cmd.Arguments.ToArray());

        Assert.NotNull(parsed);
        Assert.Equal(pipe, parsed!.PipeName);
        Assert.Equal(4321, parsed.ParentPid);
    }
}

/// <summary>The English wording; runs alone because the language is process-wide.</summary>
[Collection("Language")]
public sealed class ClickerPresentationEnglishTests
{
    [Fact]
    public void The_toasts_and_the_pill_speak_english()
    {
        using (TestLanguage.English())
        {
            var run = new ClickerRunInfo(new RunRecord(), StopReason.Locked, null, "Arma");
            Assert.Equal("AutoClicker stopped - Arma is on the blacklist", ClickerNotices.ForRunEnd(run)!.Body);

            var running = ClickerState.Off with { Phase = ClickerPhase.Running, Cps = 2.5, Clicks = 1234567 };
            Assert.Equal("Running · 2.5 cps · 1,234,567 clicks", ClickerStateText.Pill(running));
            Assert.Equal("An administrator window (x.exe) blocks the clicks", ClickerStateText.ElevatedNotice("x.exe"));
            Assert.Equal("AutoClicker · 2.5 cps", ClickerIndicatorRules.Text(2.5));
            Assert.Equal("Ready · master switch off", ClickerStateText.Pill(ClickerState.Off with { Phase = ClickerPhase.Ready, MasterAllowed = false }));
        }
    }

    [Fact]
    public void The_pattern_texts_speak_english()
    {
        using (TestLanguage.English())
        {
            var settings = new AppSettings();
            settings.AutoClicker.Clicker.MousePatterns.Add(new MousePattern { Id = new string('a', 32), Name = "Kreis" });
            var playing = ClickerState.Off with { Phase = ClickerPhase.Ready, Playing = new string('a', 32) };
            var recording = ClickerState.Off with { Phase = ClickerPhase.Ready, Recording = true };

            Assert.Equal("Mouse pattern · Kreis", ClickerIndicatorRules.Text(playing, settings));
            Assert.Equal("Recording · Ctrl+Alt+R stops it", ClickerIndicatorRules.Text(recording, settings));
            var ok = ClickerNotices.ForPatternRecorded(new PatternRecordedInfo("Cut", "Pattern 3", 2.4))!;
            Assert.Equal("Mouse patterns", ok.Title);
            Assert.Equal("'Pattern 3' recorded (2.4 s) - cut off after 60 s", ok.Body);
            Assert.Equal("No movement recorded", ClickerNotices.ForPatternRecorded(new PatternRecordedInfo("Empty", null, 0))!.Body);
        }
    }
}
