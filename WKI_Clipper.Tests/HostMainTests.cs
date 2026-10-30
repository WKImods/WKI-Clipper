using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.ClickerHost;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Host mode detection, the host log, the high-resolution waiter and the read-only Windows queries. No input is injected and no hook is installed.</summary>
public sealed class HostMainTests
{
    private const string Pipe = "WKI_Clipper.Clicker.4242.0123456789abcdef";

    // ---- command line ----

    [Fact]
    public void A_valid_command_line_is_recognised()
    {
        var args = HostMain.TryParseArgs(new[] { "--clicker-host", Pipe, "4242" });
        Assert.Equal(new HostArgs(Pipe, 4242), args);
        Assert.True(HostMain.IsHostInvocation(new[] { "--clicker-host", Pipe, "4242" }));
    }

    [Theory]
    [InlineData("--clicker-host")]
    [InlineData("--clicker-host", Pipe)]
    [InlineData("--clicker-host", Pipe, "0")]
    [InlineData("--clicker-host", Pipe, "-5")]
    [InlineData("--clicker-host", Pipe, "abc")]
    [InlineData("--clicker-host", Pipe, "+7")]
    [InlineData("--clicker-host", "not-a-pipe", "4242")]
    [InlineData("--clicker-host", @"\\.\pipe\x", "4242")]
    [InlineData("--clicker-host", Pipe, "4242", "extra")]
    [InlineData("--CLICKER-HOST", Pipe, "4242")]
    [InlineData("--other", Pipe, "4242")]
    public void Anything_else_is_not_a_host_command_line(params string[] args)
        => Assert.Null(HostMain.TryParseArgs(args));

    [Fact]
    public void No_arguments_are_not_a_host_command_line()
    {
        Assert.Null(HostMain.TryParseArgs(Array.Empty<string>()));
        Assert.Null(HostMain.TryParseArgs(null));
    }

    [Fact]
    public void A_broken_host_command_line_is_still_a_host_invocation_so_the_full_app_never_starts_by_accident()
    {
        Assert.True(HostMain.IsHostInvocation(new[] { "--clicker-host", "garbage" }));
        Assert.False(HostMain.IsHostInvocation(Array.Empty<string>()));
        Assert.False(HostMain.IsHostInvocation(new[] { "--minimized" }));
        Assert.False(HostMain.IsHostInvocation(null));
    }

    [Fact]
    public void A_host_started_with_bad_arguments_exits_with_the_bad_arguments_code()
    {
        var path = Path.Combine(Path.GetTempPath(), "wki-hostlog-" + Guid.NewGuid().ToString("N") + ".log");
        var previous = HostLog.Path;
        HostLog.SetPath(path);
        try
        {
            Assert.Equal(HostMain.ExitBadArguments, HostMain.Run(new[] { "--clicker-host", "garbage" }));
        }
        finally
        {
            HostLog.SetPath(previous);
            TryDelete(path);
        }
    }

    // ---- log ----

    private static void TryDelete(string path)
    {
        try { File.Delete(path); File.Delete(path + ".old"); } catch { }
    }

    [Fact]
    public void The_host_log_appends_lines_forwards_warnings_and_rotates_a_big_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "wki-hostlog-" + Guid.NewGuid().ToString("N") + ".log");
        var previous = HostLog.Path;
        var forwarded = new System.Collections.Concurrent.ConcurrentQueue<(string, string)>();
        HostLog.SetPath(path);
        HostLog.Forward = (level, text) => { if (text.StartsWith("unit-test")) forwarded.Enqueue((level, text)); };
        try
        {
            HostLog.Info("unit-test info");
            HostLog.Warn("unit-test warn");
            HostLog.Error("unit-test error");
            var text = File.ReadAllText(path);
            Assert.Contains("[INFO] unit-test info", text);
            Assert.Contains("[WARN] unit-test warn", text);
            Assert.Contains("[ERR ] unit-test error", text);
            Assert.Equal(new[] { ("warn", "unit-test warn"), ("error", "unit-test error") }, forwarded.ToArray());

            File.WriteAllBytes(path, new byte[HostLog.MaxBytes + 10]);
            HostLog.Rotate();
            Assert.True(File.Exists(path + ".old"));
            Assert.True(new FileInfo(path + ".old").Length > HostLog.MaxBytes);
            Assert.False(File.Exists(path) && new FileInfo(path).Length > HostLog.MaxBytes);
        }
        finally
        {
            HostLog.Forward = null;
            HostLog.SetPath(previous);
            TryDelete(path);
        }
    }

    // ---- waiter (a real timer, nothing else) ----

    [Fact]
    public void The_waiter_returns_true_only_after_the_deadline()
    {
        var clock = new StopwatchClock();
        using var waiter = new HighResWaiter(clock);

        double deadline = clock.NowMs + 30;
        Assert.True(waiter.WaitUntil(deadline));

        Assert.True(clock.NowMs >= deadline);
        Assert.True(waiter.WaitUntil(clock.NowMs - 5));   // a deadline in the past returns at once
    }

    [Fact]
    public async Task A_wake_cuts_a_long_wait_short_and_stays_set_until_cleared()
    {
        var clock = new StopwatchClock();
        using var waiter = new HighResWaiter(clock);

        var wait = Task.Run(() => waiter.WaitUntil(clock.NowMs + 30_000));
        await Task.Delay(50);
        waiter.Wake();

        Assert.False(await wait.WaitAsync(TimeSpan.FromSeconds(5)));   // woken, not timed out
        Assert.False(waiter.WaitUntil(clock.NowMs + 1000));   // still woken: returns at once

        waiter.ClearWake();
        Assert.True(waiter.WaitUntil(clock.NowMs + 5));
    }

    [Fact]
    public void Sessions_can_begin_and_end_repeatedly()
    {
        using var waiter = new HighResWaiter(new StopwatchClock());
        for (int i = 0; i < 3; i++)
        {
            waiter.BeginSession();
            waiter.EndSession();
        }
    }

    // ---- clock ----

    [Fact]
    public void The_stopwatch_clock_is_monotonic_and_in_milliseconds()
    {
        var clock = new StopwatchClock();
        double a = clock.NowMs;
        Thread.Sleep(20);
        double b = clock.NowMs;
        Assert.InRange(b - a, 15, 2000);
    }

    // ---- read-only Windows queries ----

    [Fact]
    public void The_windows_environment_answers_its_queries_with_plausible_values()
    {
        var env = new WindowsHostEnvironment();

        Assert.NotEmpty(env.Monitors);
        Assert.True(env.VirtualScreen.W > 0 && env.VirtualScreen.H > 0);
        Assert.InRange(env.KeyboardSpeed, 0, 31);
        Assert.InRange(env.KeyboardDelayIndex, 0, 3);
        Assert.True(env.DoubleClickTimeMs > 0);
        _ = env.CursorPosition;
        _ = env.CapsLockOn;
        _ = env.HostElevated;
        Assert.NotNull(env.Foreground);
        Assert.False(env.IsKeyDown(0xFF));
    }
}
