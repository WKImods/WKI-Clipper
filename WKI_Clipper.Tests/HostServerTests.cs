using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.ClickerHost;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>The host's end of the pipe over an in-memory stream pair: hello first, dispatch, unknown/malformed lines, versions, shutdown, the outbox.</summary>
public sealed class HostServerTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly ClickerChannel _clipper;
    private readonly HostServer _server;
    private readonly ConcurrentQueue<ClickerMessage> _received = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Stream _clipperStream;

    public HostServerTests()
    {
        var (hostEnd, clipperEnd) = InMemoryPipe.Create();
        _clipperStream = clipperEnd;
        _clipper = new ClickerChannel(clipperEnd);
        _server = new HostServer(new ClickerChannel(hostEnd), pid: 777, _received.Enqueue);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _server.Dispose();
        _clipper.Dispose();
        _cts.Dispose();
    }

    private async Task<ClickerMessage> NextFromHost()
    {
        var decoded = await _clipper.ReadAsync(_cts.Token).WaitAsync(Wait);
        Assert.NotNull(decoded);
        return decoded!.Value.Message!;
    }

    private async Task WriteRaw(string text)
    {
        await _clipperStream.WriteAsync(Encoding.UTF8.GetBytes(text));
    }

    [Fact]
    public async Task The_host_speaks_first_with_its_version_and_process_id()
    {
        var run = _server.RunAsync(_cts.Token);

        var hello = Assert.IsType<HelloMessage>(await NextFromHost());

        Assert.Equal(new HelloMessage(ClickerProtocol.Version, 777), hello);
        await _clipper.WriteAsync(new ShutdownMessage());
        Assert.Equal(HostEnd.Shutdown, await run.WaitAsync(Wait));
    }

    [Fact]
    public async Task Hello_stays_first_even_when_a_log_line_is_queued_before_the_conversation_runs()
    {
        // HostMain forwards log lines from the moment the hooks start - before RunAsync - e.g. "the hotkey hooks are not running".
        _server.Send(new LogMessage("error", "hooks failed"));
        var run = _server.RunAsync(_cts.Token);

        Assert.IsType<HelloMessage>(await NextFromHost());
        Assert.IsType<LogMessage>(await NextFromHost());
        await _clipper.WriteAsync(new ShutdownMessage());
        Assert.Equal(HostEnd.Shutdown, await run.WaitAsync(Wait));
    }

    [Fact]
    public async Task Messages_from_the_clipper_reach_the_handler_in_order_and_shutdown_ends_the_conversation()
    {
        var run = _server.RunAsync(_cts.Token);
        await _clipper.WriteAsync(new HelloMessage(ClickerProtocol.Version));
        await _clipper.WriteAsync(new LockMessage("x"));
        await _clipper.WriteAsync(new PickerMessage(true));
        await _clipper.WriteAsync(new ShutdownMessage());

        Assert.Equal(HostEnd.Shutdown, await run.WaitAsync(Wait));

        Assert.Equal(new ClickerMessage[] { new HelloMessage(ClickerProtocol.Version), new LockMessage("x"), new PickerMessage(true) }, _received.ToArray());
    }

    [Fact]
    public async Task Unknown_and_malformed_lines_are_skipped_and_the_next_message_still_arrives()
    {
        var run = _server.RunAsync(_cts.Token);
        await WriteRaw("{\"t\":\"from-the-future\",\"x\":1}\nthis is not json\n{\"t\":\"state\",\"running\":\"maybe\"}\n");
        await _clipper.WriteAsync(new UnlockMessage());
        await _clipper.WriteAsync(new ShutdownMessage());

        Assert.Equal(HostEnd.Shutdown, await run.WaitAsync(Wait));

        Assert.Equal(new ClickerMessage[] { new UnlockMessage() }, _received.ToArray());
    }

    [Fact]
    public async Task A_clipper_with_another_protocol_version_ends_the_host()
    {
        var run = _server.RunAsync(_cts.Token);
        await _clipper.WriteAsync(new HelloMessage(ClickerProtocol.Version + 1));

        Assert.Equal(HostEnd.VersionMismatch, await run.WaitAsync(Wait));
        Assert.Empty(_received);
    }

    [Fact]
    public async Task A_closed_pipe_ends_the_conversation()
    {
        var run = _server.RunAsync(_cts.Token);
        _clipper.Dispose();

        Assert.Equal(HostEnd.PipeClosed, await run.WaitAsync(Wait));
    }

    [Fact]
    public async Task A_cancelled_token_ends_the_conversation()
    {
        var run = _server.RunAsync(_cts.Token);
        _cts.Cancel();

        Assert.Equal(HostEnd.Cancelled, await run.WaitAsync(Wait));
    }

    [Fact]
    public async Task A_handler_that_throws_does_not_end_the_conversation()
    {
        var calls = 0;
        var (hostEnd, clipperEnd) = InMemoryPipe.Create();
        using var clipper = new ClickerChannel(clipperEnd);
        using var server = new HostServer(new ClickerChannel(hostEnd), 1, m =>
        {
            calls++;
            if (m is LockMessage) throw new InvalidOperationException("boom");
        });
        var run = server.RunAsync(CancellationToken.None);
        await clipper.WriteAsync(new LockMessage("a"));
        await clipper.WriteAsync(new UnlockMessage());
        await clipper.WriteAsync(new ShutdownMessage());

        Assert.Equal(HostEnd.Shutdown, await run.WaitAsync(Wait));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Messages_sent_by_the_host_arrive_in_order_and_a_flush_delivers_what_is_queued()
    {
        var run = _server.RunAsync(_cts.Token);
        Assert.IsType<HelloMessage>(await NextFromHost());

        _server.Send(new StateMessage(true, true, false, null, 10, 25, 400, null));
        _server.Send(new RunEndedMessage(DateTime.UtcNow, 1, 10, "Hotkey"));
        _server.Send(new LogMessage("warn", "careful"));

        Assert.IsType<StateMessage>(await NextFromHost());
        Assert.IsType<RunEndedMessage>(await NextFromHost());
        Assert.IsType<LogMessage>(await NextFromHost());

        // The end: queue a last message after shutdown arrived, flush, and the clipper still gets it.
        await _clipper.WriteAsync(new ShutdownMessage());
        Assert.Equal(HostEnd.Shutdown, await run.WaitAsync(Wait));
        _server.Send(new RunEndedMessage(DateTime.UtcNow, 2, 20, "ClipperClosed"));
        await _server.FlushAsync(Wait);
        var last = Assert.IsType<RunEndedMessage>(await NextFromHost());
        Assert.Equal("ClipperClosed", last.Reason);
    }

    [Fact]
    public async Task An_oversized_line_ends_the_conversation_as_faulted()
    {
        var (hostEnd, clipperEnd) = InMemoryPipe.Create();
        using var server = new HostServer(new ClickerChannel(hostEnd, maxLineBytes: 64), 1, _ => { });
        var run = server.RunAsync(CancellationToken.None);
        await clipperEnd.WriteAsync(Encoding.UTF8.GetBytes(new string('x', 200) + "\n"));

        Assert.Equal(HostEnd.Faulted, await run.WaitAsync(Wait));
        clipperEnd.Dispose();
    }

    [Fact]
    public async Task Sending_after_the_outbox_is_closed_is_ignored_and_never_throws()
    {
        await _server.FlushAsync(TimeSpan.FromMilliseconds(10));
        _server.Send(new StopMessage());
    }
}
