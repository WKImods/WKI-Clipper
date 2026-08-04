using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WKI_Clipper.Models;
using WKI_Clipper.Services;

namespace WKI_Clipper.Views;

/// <summary>
/// Read-only Twitch chat. Pinned over a game it is click-through, so it can be watched
/// mid-match without ever swallowing a click. Auto-scrolls to the newest line, but stops
/// doing so the moment you scroll up to read something.
/// </summary>
public partial class ChatView : UserControl
{
    /// <summary>Data older than this means the connection is suspect, not healthy.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2.5);

    private bool _subscribed;
    private bool _autoScroll = true;
    /// <summary>True while the UI is being filled from settings — writes back are then noise.</summary>
    private bool _syncingUi;
    /// <summary>The dot ages on its own — without a tick it would stay green after a stall.</summary>
    private readonly System.Windows.Threading.DispatcherTimer _statusTick =
        new() { Interval = TimeSpan.FromSeconds(10) };

    public ChatView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _statusTick.Tick += (_, _) => UpdateStatus();
    }

    private static ChatSettings Cfg => App.Host!.Settings.Current.Chat;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var host = App.Host;
        if (host is null) return;

        ChannelBox.Text = Cfg.Channel;
        ChannelBox.ToolTip = L.T("Twitch-Kanal (Enter zum Übernehmen)", "Twitch channel (press Enter to apply)");
        ReconnectBtn.Content = L.T("Neu verbinden", "Reconnect");
        ToastsBox.Content = L.T("Raid-Hinweis", "Raid alert");
        ToastsBox.ToolTip = L.T("Einblendung bei Raids und Sub-Geschenken — auch wenn dieses Fenster zu ist.",
                                "Popup on raids and gifted subs — even while this window is closed.");
        _syncingUi = true;
        ToastsBox.IsChecked = Cfg.EventToasts;
        _syncingUi = false;

        if (!_subscribed)
        {
            host.Chat.MessageReceived += OnMessage;
            host.Chat.StatusChanged += OnStatus;
            _subscribed = true;
        }

        // Show what arrived while the widget was closed.
        Messages.Children.Clear();
        foreach (var m in host.Chat.History()) Messages.Children.Add(BuildLine(m));
        ScrollToEnd();
        UpdateStatus();
        _statusTick.Start();

        if (!host.Chat.IsConnected && Cfg.AutoConnect) host.Chat.Restart();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var host = App.Host;
        if (host != null && _subscribed)
        {
            host.Chat.MessageReceived -= OnMessage;
            host.Chat.StatusChanged -= OnStatus;
            _subscribed = false;
        }
        _statusTick.Stop();
    }

    // Both events come off the socket worker thread.
    private void OnMessage(ChatMessage m) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (!IsLoaded) return;
        Messages.Children.Add(BuildLine(m));
        int max = Math.Clamp(Cfg.MaxMessages, 20, 1000);
        while (Messages.Children.Count > max) Messages.Children.RemoveAt(0);
        if (_autoScroll) ScrollToEnd();
    }));

    private void OnStatus() => Dispatcher.BeginInvoke(new Action(UpdateStatus));

    /// <summary>
    /// The dot reports what actually arrives, not just whether a socket object exists.
    /// A WebSocket can sit open and silent for hours; a permanently green dot in that
    /// state is a lie, so anything older than <see cref="StaleAfter"/> turns amber.
    /// </summary>
    private void UpdateStatus()
    {
        var host = App.Host;
        if (host is null) return;

        Color color;
        string tip;
        if (!host.Chat.IsConnected)
        {
            color = Color.FromRgb(0x55, 0x55, 0x55);
            tip = L.T("Nicht verbunden — versucht es weiter.", "Not connected — keeps retrying.");
        }
        else if (host.Chat.LastReceivedUtc is not { } last)
        {
            color = Color.FromRgb(0xE0, 0xA8, 0x40);
            tip = L.T("Verbunden — wartet auf die ersten Daten.", "Connected — waiting for first data.");
        }
        else
        {
            var age = DateTime.UtcNow - last;
            bool stale = age > StaleAfter;
            color = stale ? Color.FromRgb(0xE0, 0xA8, 0x40) : Color.FromRgb(0x4A, 0xD8, 0x6A);
            tip = stale
                ? L.T($"Seit {age.TotalMinutes:0} min keine Daten — Verbindung wird geprüft.",
                      $"No data for {age.TotalMinutes:0} min — checking the connection.")
                : L.T($"Verbunden mit #{host.Chat.Channel} · letzte Daten vor {age.TotalSeconds:0}s",
                      $"Connected to #{host.Chat.Channel} · last data {age.TotalSeconds:0}s ago");
        }

        ConnDot.Fill = new SolidColorBrush(color);
        ConnDot.ToolTip = tip;
    }

    private FrameworkElement BuildLine(ChatMessage m)
    {
        if (m.IsEvent) return BuildEventLine(m);

        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = Math.Clamp(Cfg.FontSize, 9, 28),
            Margin = new Thickness(0, 0, 0, 3)
        };

        string badges = "";
        if (m.IsBroadcaster) badges += "📷 ";
        else if (m.IsMod) badges += "🗡 ";
        if (m.IsVip) badges += "💎 ";
        if (m.IsSub) badges += "★ ";
        if (badges.Length > 0)
            tb.Inlines.Add(new Run(badges) { Foreground = (Brush)FindResource("MutedBrush") });

        tb.Inlines.Add(new Run(m.User + ": ")
        {
            FontWeight = FontWeights.Bold,
            Foreground = ParseColor(m.Color) ?? (Brush)FindResource("AccentBrush")
        });
        if (m.Bits > 0)
            tb.Inlines.Add(new Run($"[{m.Bits} Bits] ")
            {
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("AccentBrush")
            });
        tb.Inlines.Add(new Run(m.Text) { Foreground = (Brush)FindResource("TextBrush") });

        // Someone talking TO you is the one line you must not scroll past mid-match.
        // The target is simply the channel — on your own stream that is your name.
        string me = App.Host?.Chat.Channel is { Length: > 0 } c ? c : Cfg.Channel;
        if (!ChatFilter.IsMention(m.Text, me)) return tb;

        return new Border
        {
            // Blue, so a mention never reads as a raid (accent-tinted) at a glance.
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0x4A, 0x9E, 0xE0)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 4, 7, 4),
            Margin = new Thickness(0, 2, 0, 3),
            Child = tb
        };
    }

    /// <summary>
    /// Raids, subs and announcements get a tinted block so they are visible at a glance
    /// while a fast chat scrolls past — these are the lines worth reacting to mid-game.
    /// </summary>
    private FrameworkElement BuildEventLine(ChatMessage m)
    {
        double size = Math.Clamp(Cfg.FontSize, 9, 28);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = m.SystemText ?? m.Text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("AccentBrush")
        });

        // A resub/raid may carry the viewer's own message — announcements do not, their
        // text IS the system message and would otherwise be printed twice.
        if (!string.IsNullOrWhiteSpace(m.Text) && m.Kind != ChatKind.Announcement)
            panel.Children.Add(new TextBlock
            {
                Text = m.Text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = size,
                Foreground = (Brush)FindResource("TextBrush"),
                Margin = new Thickness(0, 2, 0, 0)
            });

        return new Border
        {
            Background = AccentTint(),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 5, 7, 5),
            Margin = new Thickness(0, 2, 0, 4),
            Child = panel
        };
    }

    private Brush AccentTint()
    {
        if (FindResource("AccentBrush") is SolidColorBrush a)
            return new SolidColorBrush(Color.FromArgb(0x2E, a.Color.R, a.Color.G, a.Color.B));
        return new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0x6A, 0x2C));
    }

    /// <summary>Twitch colors are #RRGGBB; a too-dark one is lifted so it stays readable.</summary>
    private static Brush? ParseColor(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        try
        {
            var c = (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
            if (lum < 0.35)
            {
                c = Color.FromRgb(
                    (byte)Math.Min(255, c.R + 90),
                    (byte)Math.Min(255, c.G + 90),
                    (byte)Math.Min(255, c.B + 90));
            }
            return new SolidColorBrush(c);
        }
        catch { return null; }
    }

    private void ScrollToEnd() => Scroller.ScrollToEnd();

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Reading old messages pauses auto-scroll; returning to the bottom resumes it.
        if (e.ExtentHeightChange != 0) return;
        _autoScroll = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 8;
    }

    private void OnChannelKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitChannel(); e.Handled = true; }
    }

    private void OnChannelCommit(object sender, RoutedEventArgs e) => CommitChannel();

    private void CommitChannel()
    {
        var host = App.Host;
        if (host is null) return;
        var ch = ChannelBox.Text.Trim().TrimStart('#');
        if (ch.Length == 0 || string.Equals(ch, Cfg.Channel, StringComparison.OrdinalIgnoreCase)) return;
        Cfg.Channel = ch;
        host.Settings.Save();
        Messages.Children.Clear();
        host.Chat.Restart();
    }

    private void OnReconnect(object sender, RoutedEventArgs e) => App.Host?.Chat.Restart();

    private void OnToastsChanged(object sender, RoutedEventArgs e)
    {
        var host = App.Host;
        if (host is null || _syncingUi) return;   // ignore the initial IsChecked assignment
        Cfg.EventToasts = ToastsBox.IsChecked == true;
        host.Settings.Save();
    }
}
