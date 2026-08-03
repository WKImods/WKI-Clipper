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
    private bool _subscribed;
    private bool _autoScroll = true;

    public ChatView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static ChatSettings Cfg => App.Host!.Settings.Current.Chat;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var host = App.Host;
        if (host is null) return;

        ChannelBox.Text = Cfg.Channel;
        ChannelBox.ToolTip = L.T("Twitch-Kanal (Enter zum Übernehmen)", "Twitch channel (press Enter to apply)");
        ReconnectBtn.Content = L.T("Neu verbinden", "Reconnect");

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

    private void UpdateStatus()
    {
        var host = App.Host;
        if (host is null) return;
        bool on = host.Chat.IsConnected;
        ConnDot.Fill = new SolidColorBrush(on
            ? Color.FromRgb(0x4A, 0xD8, 0x6A)
            : Color.FromRgb(0x55, 0x55, 0x55));
        ConnDot.ToolTip = on
            ? L.T($"Verbunden mit #{host.Chat.Channel}", $"Connected to #{host.Chat.Channel}")
            : L.T("Nicht verbunden — versucht es weiter.", "Not connected — keeps retrying.");
    }

    private FrameworkElement BuildLine(ChatMessage m)
    {
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
        tb.Inlines.Add(new Run(m.Text) { Foreground = (Brush)FindResource("TextBrush") });
        return tb;
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
}
