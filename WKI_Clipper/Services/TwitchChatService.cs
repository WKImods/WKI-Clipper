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
    /// <summary>No traffic for this long → send our own PING to prove the link is alive.</summary>
    private static readonly TimeSpan SilenceBeforePing = TimeSpan.FromSeconds(60);
    /// <summary>That many unanswered rounds → the socket is dead, reconnect.</summary>
    private const int SilentRoundsBeforeReconnect = 3;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _connected;
    private string _joinedChannel = "";
    private long _lastRxTicks;
    private readonly ChannelJoinWatch _joinWatch = new();
    private int _joinGeneration;

    /// <summary>New chat line (worker thread!).</summary>
    public event Action<ChatMessage>? MessageReceived;
    /// <summary>Connection state or channel changed (worker thread!).</summary>
    public event Action? StatusChanged;
    /// <summary>Twitch never confirmed the joined channel — typo or renamed channel (worker thread!).</summary>
    public event Action<string>? ChannelNotFound;

    public bool IsConnected => _connected;
    public string Channel => _joinedChannel;

    /// <summary>True when Twitch did not answer the JOIN: the channel does not exist (anymore).</summary>
    public bool ChannelMissing { get { lock (_gate) return _joinWatch.Missing; } }

    /// <summary>
    /// When the last byte arrived from Twitch, or null before the first one. A socket can
    /// go silent without ever reporting an error, so "connected" alone is not proof that
    /// the chat is still live — the UI shows this age instead of a permanently green dot.
    /// </summary>
    public DateTime? LastReceivedUtc
    {
        get
        {
            long t = Interlocked.Read(ref _lastRxTicks);
            return t == 0 ? null : new DateTime(t, DateTimeKind.Utc);
        }
    }

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

        Interlocked.Exchange(ref _lastRxTicks, 0);   // the previous channel's age means nothing here

        // Dropping the backlog on a CHANNEL CHANGE only — a plain reconnect keeps it, which is
        // the point of the history. Kept lines would otherwise be replayed into every freshly
        // built ChatView (e.g. after a language switch) as if they belonged to the new channel,
        // and the mention highlight would re-check them against the new channel's name.
        if (!string.Equals(channel, _joinedChannel, StringComparison.OrdinalIgnoreCase))
            lock (_gate) _history.Clear();
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
        Interlocked.Increment(ref _joinGeneration);   // a pending join check belongs to the old run
        lock (_gate) _joinWatch.Start("", DateTime.UtcNow);
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
                lock (_gate) _joinWatch.Start(channel, DateTime.UtcNow);
                _ = WatchJoinAsync(channel, Interlocked.Increment(ref _joinGeneration), ct);
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
        var assembler = new Utf8LineAssembler();
        Task<WebSocketReceiveResult>? receive = null;
        int silentRounds = 0;

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            // Never cancel the receive on a timeout — that would abort the socket. Race it
            // against a timer instead, keep the same pending receive, and prod the server
            // with a PING. A silent connection is the failure mode that used to leave the
            // widget showing "connected" for hours while no message ever arrived again.
            receive ??= ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var winner = await Task.WhenAny(receive, Task.Delay(SilenceBeforePing, delayCts.Token))
                                       .ConfigureAwait(false);
                delayCts.Cancel();   // stop the timer when data won the race

                if (winner != receive)
                {
                    if (++silentRounds >= SilentRoundsBeforeReconnect)
                    {
                        Logger.Warn($"Twitch chat silent for {SilenceBeforePing.TotalSeconds * silentRounds:0} s — reconnecting.");
                        return;   // outer loop rebuilds the connection
                    }
                    await SendAsync(ws, "PING :tmi.twitch.tv", ct).ConfigureAwait(false);
                    continue;
                }
            }

            var res = await receive.ConfigureAwait(false);
            receive = null;
            silentRounds = 0;
            Interlocked.Exchange(ref _lastRxTicks, DateTime.UtcNow.Ticks);
            if (res.MessageType == WebSocketMessageType.Close) return;

            // A chunk can carry several lines, half a line, or half a character — the
            // assembler owns all three cases so this loop only sees finished lines.
            foreach (var l in assembler.Append(buffer, res.Count))
            {
                if (IrcParser.IsPing(l))
                {
                    await SendAsync(ws, IrcParser.PongFor(l), ct).ConfigureAwait(false);
                    continue;
                }
                bool confirmed;
                string? roomId;
                lock (_gate) { confirmed = _joinWatch.OnLine(l); roomId = _joinWatch.RoomId; }
                if (confirmed)
                {
                    Logger.Info($"Twitch chat: #{_joinedChannel} confirmed (room-id {roomId ?? "?"})");
                    StatusChanged?.Invoke();
                    continue;
                }
                var msg = IrcParser.ParseLine(l);
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

    /// <summary>
    /// Twitch answers a JOIN to an unknown login with silence, so the only way to notice a typo or
    /// a renamed channel is the missing ROOMSTATE. A newer join (reconnect, channel change) makes
    /// this check obsolete via the generation number.
    /// </summary>
    private async Task WatchJoinAsync(string channel, int generation, CancellationToken ct)
    {
        try { await Task.Delay(ChannelJoinWatch.Timeout + TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        if (generation != Volatile.Read(ref _joinGeneration)) return;

        bool missing;
        lock (_gate) missing = _joinWatch.CheckMissing(DateTime.UtcNow);
        if (!missing) return;

        Logger.Warn($"Twitch chat: #{channel} did not answer the JOIN within {ChannelJoinWatch.Timeout.TotalSeconds:0} s — the channel does not exist or was renamed");
        StatusChanged?.Invoke();
        ChannelNotFound?.Invoke(channel);
    }

    private static Task SendAsync(ClientWebSocket ws, string line, CancellationToken ct)
        => ws.SendAsync(Encoding.UTF8.GetBytes(line + "\r\n"), WebSocketMessageType.Text, true, ct);

    public void Dispose() => Stop();
}
