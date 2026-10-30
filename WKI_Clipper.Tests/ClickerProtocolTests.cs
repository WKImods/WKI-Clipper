using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.Models;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>The JSON-line codec of the clipper pipe (design section 6): round trips, versioning, unknown types, broken lines, the byte-stream channel.</summary>
public sealed class ClickerProtocolTests
{
    private static T RoundTrip<T>(T message) where T : ClickerMessage
    {
        var line = ClickerProtocol.Encode(message);
        Assert.DoesNotContain('\n', line);
        var decoded = ClickerProtocol.Decode(line);
        Assert.Equal(DecodeStatus.Ok, decoded.Status);
        return Assert.IsType<T>(decoded.Message);
    }

    // ---- messages ----

    [Fact]
    public void Every_message_survives_a_round_trip()
    {
        Assert.Equal(new HelloMessage(1, 4321), RoundTrip(new HelloMessage(1, 4321)));
        Assert.Equal(new LockMessage("ArmaReforgerSteam.exe"), RoundTrip(new LockMessage("ArmaReforgerSteam.exe")));
        Assert.Equal(new UnlockMessage(), RoundTrip(new UnlockMessage()));
        Assert.Equal(new StartMessage(), RoundTrip(new StartMessage()));
        Assert.Equal(new StopMessage(), RoundTrip(new StopMessage()));
        Assert.Equal(new PickerMessage(true), RoundTrip(new PickerMessage(true)));
        Assert.Equal(new ShutdownMessage(), RoundTrip(new ShutdownMessage()));
        var state = new StateMessage(true, true, false, null, 1234, 25.5, 987654, "admin.exe", MasterAllowed: false);
        Assert.Equal(state, RoundTrip(state));
        var ended = new RunEndedMessage(new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc), 12.5, 300, "ClickLimit", "300");
        Assert.Equal(ended, RoundTrip(ended));
        Assert.Equal(new LogMessage("warn", "Größe €"), RoundTrip(new LogMessage("warn", "Größe €")));
    }

    [Fact]
    public void A_state_without_the_master_flag_reads_as_allowed()
    {
        var line = ClickerProtocol.Encode(new StateMessage(false, true, false, null, 0, 0, 0, null)).Replace(",\"masterAllowed\":true", "");
        Assert.DoesNotContain("masterAllowed", line);

        var state = Assert.IsType<StateMessage>(ClickerProtocol.Decode(line).Message);

        Assert.True(state.MasterAllowed);
    }

    [Fact]
    public void The_type_is_the_first_property_of_the_line()
    {
        Assert.StartsWith("{\"t\":\"state\"", ClickerProtocol.Encode(new StateMessage(false, true, false, null, 0, 0, 0, null)));
        Assert.StartsWith("{\"t\":\"runEnded\"", ClickerProtocol.Encode(new RunEndedMessage(DateTime.UtcNow, 0, 0, "Hotkey")));
        Assert.Equal("{\"t\":\"unlock\"}", ClickerProtocol.Encode(new UnlockMessage()));
    }

    [Fact]
    public void A_config_carries_the_settings_but_not_the_presets_and_the_statistics()
    {
        var s = new ClickerSettings
        {
            ClickSpeed = 120.5,
            ClickUnit = RateUnit.Minute,
            InputType = ClickerInput.Keyboard,
            KeyboardVk = 0x41,
            KeyCase = KeyCase.Upper,
            DoubleClick = true,
            ActivationMode = ActivationMode.Hold,
            MasterHotkey = new ClickerHotkey { Modifiers = ClickerModifiers.LShift, Keys = { ClickerVk.XButton1 } },
            MasterMode = ActivationMode.Hold,
            StrictModifiers = true,
            ClickLimitEnabled = true,
            ClickLimit = 77,
            TimeLimitUnit = TimeLimitUnit.Hours,
            ZonesEnabled = true,
            Zones = { new ClickerZone { Id = "z1", Name = "Z", X = -100, Y = 5, W = 30, H = 40, Action = ZoneAction.Start, Enabled = false } },
            ClickPointsEnabled = true,
            ClickPoints = { new ClickPoint { X = -50, Y = 60, Clicks = 3, Radius = 4 } },
            WhitelistEnabled = true,
            Whitelist = { new BlacklistEntry { ExeName = "game.exe", DisplayName = "Game" } },
            Presets = { new ClickerPreset { Name = "Mine" } },
            Stats = { TotalClicks = 999 }
        };
        s.Stats.AddRun(new RunRecord { Clicks = 5, StopReason = "Hotkey" });

        var line = ClickerProtocol.Encode(new ConfigMessage(s));
        Assert.DoesNotContain("presets", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stats", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"inputType\":\"Keyboard\"", line);   // enums travel as text

        var back = RoundTrip(new ConfigMessage(s)).Settings;
        Assert.Equal(120.5, back.ClickSpeed);
        Assert.Equal(RateUnit.Minute, back.ClickUnit);
        Assert.Equal(ClickerInput.Keyboard, back.InputType);
        Assert.Equal(0x41, back.KeyboardVk);
        Assert.Equal(KeyCase.Upper, back.KeyCase);
        Assert.True(back.DoubleClick);
        Assert.Equal(ActivationMode.Hold, back.ActivationMode);
        Assert.True(ClickerHotkey.SameCombo(s.MasterHotkey, back.MasterHotkey));
        Assert.Equal(ClickerModifiers.LShift, back.MasterHotkey!.Modifiers);
        Assert.True(ClickerHotkey.SameCombo(s.Hotkey, back.Hotkey));
        Assert.Equal(77, back.ClickLimit);
        Assert.Equal(TimeLimitUnit.Hours, back.TimeLimitUnit);
        var zone = Assert.Single(back.Zones);
        Assert.Equal((-100, 5, 30, 40, ZoneAction.Start, false), (zone.X, zone.Y, zone.W, zone.H, zone.Action, zone.Enabled));
        var point = Assert.Single(back.ClickPoints);
        Assert.Equal((-50, 60, 3, 4), (point.X, point.Y, point.Clicks, point.Radius));
        Assert.Equal("game.exe", Assert.Single(back.Whitelist).ExeName);
        Assert.Empty(back.Presets);
        Assert.Equal(0, back.Stats.TotalClicks);
    }

    [Fact]
    public void A_config_without_a_master_hotkey_stays_without_one()
    {
        var back = RoundTrip(new ConfigMessage(new ClickerSettings { MasterHotkey = null })).Settings;
        Assert.Null(back.MasterHotkey);
    }

    // ---- unknown and broken lines ----

    [Fact]
    public void An_unknown_message_type_is_reported_as_such_and_not_as_an_error()
    {
        var d = ClickerProtocol.Decode("{\"t\":\"telemetry\",\"x\":1}");
        Assert.Equal(DecodeStatus.Unknown, d.Status);
        Assert.Null(d.Message);
        Assert.Equal("telemetry", d.Detail);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"x\":1}")]
    [InlineData("{\"t\":7}")]
    [InlineData("{\"t\":\"state\",\"running\":\"maybe\"}")]
    [InlineData("{\"t\":\"config\",\"settings\":42}")]
    [InlineData("{\"t\":\"hello\"")]
    public void Garbage_is_malformed(string line)
    {
        var d = ClickerProtocol.Decode(line);
        Assert.Equal(DecodeStatus.Malformed, d.Status);
        Assert.Null(d.Message);
    }

    [Fact]
    public void Unknown_fields_of_a_known_message_are_ignored()
    {
        var d = ClickerProtocol.Decode("{\"t\":\"hello\",\"v\":1,\"pid\":5,\"future\":true}");
        Assert.Equal(new HelloMessage(1, 5), d.Message);
    }

    [Fact]
    public void A_hello_from_another_version_decodes_with_its_version()
    {
        Assert.Equal(new HelloMessage(2), ClickerProtocol.Decode("{\"t\":\"hello\",\"v\":2}").Message);
    }

    // ---- pipe name ----

    [Fact]
    public void Pipe_names_have_the_documented_form_and_are_unique()
    {
        var a = ClickerProtocol.NewPipeName(4242);
        var b = ClickerProtocol.NewPipeName(4242);
        Assert.NotEqual(a, b);
        Assert.Matches(@"^WKI_Clipper\.Clicker\.4242\.[0-9a-f]{16}$", a);
        Assert.True(ClickerProtocol.IsValidPipeName(a));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("WKI_Clipper.Clicker.1.zzzz")]
    [InlineData("WKI_Clipper.Clicker.abc.0123456789abcdef")]
    [InlineData(@"\\.\pipe\WKI_Clipper.Clicker.1.0123456789abcdef")]
    [InlineData("WKI_Clipper.Clicker.1.0123456789abcdef\n")]
    [InlineData("other.1.0123456789abcdef")]
    public void Other_pipe_names_are_refused(string? name) => Assert.False(ClickerProtocol.IsValidPipeName(name));

    // ---- channel ----

    private sealed class DribbleStream : MemoryStream
    {
        public DribbleStream(byte[] data) : base(data) { }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => await base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }

    private static async Task<ClickerDecode?[]> ReadAll(ClickerChannel channel)
    {
        var list = new System.Collections.Generic.List<ClickerDecode?>();
        while (await channel.ReadAsync() is { } d) list.Add(d);
        return list.ToArray();
    }

    [Fact]
    public async Task The_channel_reads_lines_that_arrive_a_byte_at_a_time_even_inside_multi_byte_characters()
    {
        var bytes = Encoding.UTF8.GetBytes(
            ClickerProtocol.Encode(new LogMessage("info", "Größe € 日本")) + "\r\n\r\n" + ClickerProtocol.Encode(new StopMessage()) + "\n");
        using var channel = new ClickerChannel(new DribbleStream(bytes));

        var all = await ReadAll(channel);

        Assert.Equal(2, all.Length);   // the blank line is skipped, CRLF is accepted
        Assert.Equal(new LogMessage("info", "Größe € 日本"), all[0]!.Value.Message);
        Assert.Equal(new StopMessage(), all[1]!.Value.Message);
    }

    [Fact]
    public async Task The_channel_drops_a_half_line_at_the_end_of_the_stream_and_reports_unknown_and_malformed_lines()
    {
        var text = "{\"t\":\"nope\"}\ngarbage\n" + ClickerProtocol.Encode(new UnlockMessage()) + "\n{\"t\":\"stop\"";
        using var channel = new ClickerChannel(new MemoryStream(Encoding.UTF8.GetBytes(text)));

        var all = await ReadAll(channel);

        Assert.Equal(new[] { DecodeStatus.Unknown, DecodeStatus.Malformed, DecodeStatus.Ok }, all.Select(d => d!.Value.Status).ToArray());
    }

    [Fact]
    public async Task A_line_over_the_limit_is_an_error_not_a_message()
    {
        var text = new string('x', 500) + "\n";
        using var channel = new ClickerChannel(new MemoryStream(Encoding.UTF8.GetBytes(text)), maxLineBytes: 100);
        await Assert.ThrowsAsync<InvalidDataException>(() => channel.ReadAsync());
    }

    [Fact]
    public async Task Messages_written_to_one_end_of_a_pipe_arrive_at_the_other_in_order()
    {
        var (a, b) = InMemoryPipe.Create();
        using var left = new ClickerChannel(a);
        using var right = new ClickerChannel(b);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => left.WriteAsync(new PickerMessage(i % 2 == 0))));
        await left.WriteAsync(new ShutdownMessage());

        int pickers = 0;
        while (await right.ReadAsync() is { } d)
        {
            if (d.Message is PickerMessage) pickers++;
            if (d.Message is ShutdownMessage) break;
        }
        Assert.Equal(20, pickers);   // concurrent writers never interleave inside a line
    }
}
