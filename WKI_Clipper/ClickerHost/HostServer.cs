using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WKI_Clipper.Services.AutoClicker;

namespace WKI_Clipper.ClickerHost;

/// <summary>How the host's conversation with the clipper ended.</summary>
internal enum HostEnd { Shutdown, PipeClosed, VersionMismatch, Cancelled, Faulted }

/// <summary>
/// The host's end of the clipper pipe (protocol v1, design section 6). The clipper creates the pipe server, the host connects, so it
/// is the host that speaks first (<c>hello</c> with its version and process id). This class reads the clipper's messages and hands them
/// to a handler, and writes the host's messages from a queue so the engine and hook threads never wait for the pipe. Unknown
/// message types and malformed lines are skipped; a different protocol version ends the conversation.
/// </summary>
internal sealed class HostServer : IDisposable
{
    private readonly ClickerChannel _channel;
    private readonly Action<ClickerMessage> _onMessage;
    private readonly Channel<ClickerMessage> _outbox = Channel.CreateUnbounded<ClickerMessage>(new UnboundedChannelOptions { SingleReader = true });
    private Task? _writer;

    public HostServer(ClickerChannel channel, int pid, Action<ClickerMessage> onMessage)
    {
        _channel = channel;
        _onMessage = onMessage;
        // hello must be the first thing on the pipe: a log line forwarded while the hooks start must never get ahead of it.
        Send(new HelloMessage(ClickerProtocol.Version, pid));
    }

    /// <summary>Queues a message for the clipper; never blocks. Ignored once the conversation is over.</summary>
    public void Send(ClickerMessage message) => _outbox.Writer.TryWrite(message);

    /// <summary>
    /// Serves the pipe (the hello was queued at construction, so it goes out first) until the clipper says <c>shutdown</c>, the pipe closes, the versions differ or
    /// <paramref name="ct"/> is cancelled. The outbox stays open so the caller can still queue the last messages; <see cref="FlushAsync"/> ends it.
    /// </summary>
    public async Task<HostEnd> RunAsync(CancellationToken ct)
    {
        _writer = Task.Run(() => WriteLoopAsync(ct));

        HostEnd end;
        try
        {
            end = await ReadLoopAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { end = HostEnd.Cancelled; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
        {
            HostLog.Warn("Pipe read ended: " + ex.Message);
            end = ex is InvalidDataException ? HostEnd.Faulted : HostEnd.PipeClosed;
        }

        return end;
    }

    /// <summary>Writes out what is queued (up to <paramref name="timeout"/>) and closes the outbox. Call after <see cref="RunAsync"/> for a clean end.</summary>
    public async Task FlushAsync(TimeSpan timeout)
    {
        _outbox.Writer.TryComplete();
        if (_writer is null) return;
        try { await _writer.WaitAsync(timeout).ConfigureAwait(false); }
        catch { /* the pipe is gone or the flush timed out; nothing left to do */ }
    }

    private async Task<HostEnd> ReadLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            var decoded = await _channel.ReadAsync(ct).ConfigureAwait(false);
            if (decoded is null) return HostEnd.PipeClosed;

            switch (decoded.Value.Status)
            {
                case DecodeStatus.Unknown:
                    HostLog.Info("Ignoring a message of unknown type: " + decoded.Value.Detail);
                    continue;
                case DecodeStatus.Malformed:
                    HostLog.Warn("Ignoring a malformed message: " + decoded.Value.Detail);
                    continue;
            }

            var message = decoded.Value.Message!;
            if (message is HelloMessage hello && hello.V != ClickerProtocol.Version)
            {
                HostLog.Error($"Protocol version mismatch: the clipper speaks v{hello.V}, this host v{ClickerProtocol.Version}.");
                return HostEnd.VersionMismatch;
            }
            if (message is ShutdownMessage) return HostEnd.Shutdown;

            try { _onMessage(message); }
            catch (Exception ex) { HostLog.Error("Handling " + message.GetType().Name + " failed", ex); }
        }
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in _outbox.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                if (ct.IsCancellationRequested) break;
                await _channel.WriteAsync(message, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The clipper went away; the read side notices the same and ends the run.
            _outbox.Writer.TryComplete();
        }
    }

    public void Dispose()
    {
        _outbox.Writer.TryComplete();
        _channel.Dispose();
    }
}
