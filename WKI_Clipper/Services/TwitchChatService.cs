using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WKI_Clipper.Services;

/// <summary>
/// Read-only Twitch chat over IRC-via-WebSocket. Uses the anonymous "justinfan" login,
/// so there is no OAuth, no API key and no account involved — the clipper simply reads
/// a public chat. Reconnects on its own (Twitch drops idle connections, and the PC
/// sleeps), which is why the read loop lives in one long-running task.
/// </summary>
public sealed class TwitchChatService : IDisposable
{
    private const string Url = "wss://irc-ws.chat.twitch.tv:443";

    private readonly SettingsService _settings;
    private readonly object _gate = new();
    private readonly LinkedList<ChatMessage> _history = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _connected;
    private string _joinedChannel = "";

    /// <summary>New chat line (worker thread!).</summary>
    public event Action<ChatMessage>? MessageReceived;
    /// <summary>Connection state or channel changed (worker thread!).</summary>
    public event Action? StatusChanged;

    public bool IsConnected => _connected;
    public string Channel => _joinedChannel;

    public TwitchChatService(SettingsService settings) => _settings = settings;

    /// <summary>Last messages, oldest first — lets a freshly opened widget show context.</summary>
    public IReadOnlyList<ChatMessage> History()
    {
        lock (_gate) return _history.ToArray();
    }

    /// <summary>Starts (or restarts, e.g. after a channel change) the reader.</summary>
    public void Restart()
    {
        Stop();
        var channel = (_settings.Current.Chat.Channel ?? "").Trim().TrimStart('#').ToLowerInvariant();
        if (channel.Length == 0) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => RunAsync(channel, ct), ct);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
        if (_connected) { _connected = false; StatusChanged?.Invoke(); }
    }

    private async Task RunAsync(string channel, CancellationToken ct)
    {
        int backoffSec = 2;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(Url), ct).ConfigureAwait(false);

                // Tags carry display name, color and badges — without this capability
                // the chat would render as plain grey nicknames.
                await SendAsync(ws, "CAP REQ :twitch.tv/tags twitch.tv/commands", ct).ConfigureAwait(false);
                await SendAsync(ws, $"NICK justinfan{Random.Shared.Next(10000, 99999)}", ct).ConfigureAwait(false);
                await SendAsync(ws, $"JOIN #{channel}", ct).ConfigureAwait(false);

                _joinedChannel = channel;
                _connected = true;
                backoffSec = 2;
                StatusChanged?.Invoke();
                Logger.Info($"Twitch chat connected to #{channel}");

                await ReadLoopAsync(ws, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Logger.Warn("Twitch chat connection failed: " + ex.Message);
            }

            if (_connected) { _connected = false; StatusChanged?.Invoke(); }
            if (ct.IsCancellationRequested) break;

            try { await Task.Delay(TimeSpan.FromSeconds(backoffSec), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            backoffSec = Math.Min(backoffSec * 2, 60);   // don't hammer Twitch when offline
        }
        _connected = false;
        StatusChanged?.Invoke();
    }

    private async Task ReadLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var pending = new StringBuilder();

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
            if (res.MessageType == WebSocketMessageType.Close) return;

            pending.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));

            // A frame can carry several lines, or half of one — only process complete lines.
            string all = pending.ToString();
            int lastNl = all.LastIndexOf('\n');
            if (lastNl < 0) continue;
            pending.Clear();
            pending.Append(all[(lastNl + 1)..]);

            foreach (var line in all[..lastNl].Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var l = line.TrimEnd('\r');
                if (IrcParser.IsPing(l))
                {
                    await SendAsync(ws, IrcParser.PongFor(l), ct).ConfigureAwait(false);
                    continue;
                }
                var msg = IrcParser.ParsePrivmsg(l);
                if (msg is null) continue;

                lock (_gate)
                {
                    _history.AddLast(msg);
                    int max = Math.Clamp(_settings.Current.Chat.MaxMessages, 20, 1000);
                    while (_history.Count > max) _history.RemoveFirst();
                }
                MessageReceived?.Invoke(msg);
            }
        }
    }

    private static Task SendAsync(ClientWebSocket ws, string line, CancellationToken ct)
        => ws.SendAsync(Encoding.UTF8.GetBytes(line + "\r\n"), WebSocketMessageType.Text, true, ct);

    public void Dispose() => Stop();
}
