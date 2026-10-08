using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WKI_Clipper.Models;
using WKI_Clipper.Services;

namespace WKI_Clipper.Views;

/// <summary>
/// WhatsApp Web in a widget. Private by default: hidden from stream/clips (window display
/// affinity, applied by the host) and muted, both switchable in the toolbar. The only
/// thing read from the page is the unread count in its title, for the launcher badge.
/// </summary>
public sealed class WhatsAppView : UserControl, IWebWidget
{
    private readonly WebAppHost _web = new(WebApp.WhatsApp);
    private readonly ToggleButton _privacyChip;
    private readonly ToggleButton _soundChip;
    private readonly Button _reloadButton;
    private int _unread;
    private bool _titleLogged;

    /// <summary>Unread chats changed (from the page title).</summary>
    public event Action<int>? UnreadChanged;
    /// <summary>User flipped "hidden from stream" — the host applies it to the window.</summary>
    public event Action<bool>? CaptureExclusionChanged;

    private static AppHost Host => App.Host;
    private static WhatsAppSettings Cfg => Host.Settings.Current.WhatsApp;

    public WhatsAppView()
    {
        _web.IsMuted = Cfg.Muted;
        _web.Zoom = Cfg.Zoom;
        _web.ZoomChanged += z => { Cfg.Zoom = z; Host.Settings.Save(); };
        _web.TitleChanged += OnTitle;

        bool excluded = Host.Settings.Current.Widgets.GetOrAdd(WidgetId.WhatsApp).ExcludeFromCapture;
        _privacyChip = Chip(excluded, on => CaptureExclusionChanged?.Invoke(on));
        _soundChip = Chip(!Cfg.Muted, on =>
        {
            Cfg.Muted = !on;
            _web.IsMuted = !on;
            Host.Settings.Save();
            Relabel();
        });
        _reloadButton = new Button { Content = "⟳", Width = 30, Height = 26, Padding = new Thickness(0) };
        _reloadButton.Click += (_, _) => _web.Reload();

        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = false };
        DockPanel.SetDock(_reloadButton, Dock.Right);
        bar.Children.Add(_privacyChip);
        bar.Children.Add(_soundChip);
        bar.Children.Add(_reloadButton);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_web);
        Content = root;
        Relabel();
    }

    public void EnsureStarted() => _web.EnsureStarted();

    public bool HasKeyboardFocusWithin => _web.HasKeyboardFocusWithin;

    public void Relocalize()
    {
        _web.Relocalize();
        Relabel();
    }

    /// <summary>Host → view: keep the chip in sync when the flag changes elsewhere.</summary>
    public void SyncCaptureExclusion(bool excluded)
    {
        if (_privacyChip.IsChecked != excluded) _privacyChip.IsChecked = excluded;
        Relabel();
    }

    private void Relabel()
    {
        bool hidden = _privacyChip.IsChecked == true;
        _privacyChip.Content = hidden
            ? L.T("Im Stream: unsichtbar", "On stream: hidden")
            : L.T("Im Stream: SICHTBAR", "On stream: VISIBLE");
        _privacyChip.Foreground = (Brush)Application.Current.FindResource(hidden ? "TextBrush" : "DangerBrush");
        _privacyChip.ToolTip = L.T(
            "Unsichtbar = nicht im Stream, nicht in Clips, Aufnahmen und Screenshots. Du selbst siehst das Fenster trotzdem.",
            "Hidden = not on stream, not in clips, recordings or screenshots. You still see the window yourself.");
        bool soundOn = _soundChip.IsChecked == true;
        _soundChip.Content = soundOn ? L.T("Ton: an", "Sound: on") : L.T("Ton: aus", "Sound: off");
        _soundChip.ToolTip = L.T("Nachrichtentöne und Sprachnachrichten. Aus, damit nichts davon im Stream oder in Clips landet.",
                                 "Message tones and voice notes. Off so none of it reaches the stream or clips.");
        _reloadButton.ToolTip = L.T("Neu laden", "Reload");
    }

    private void OnTitle(string title)
    {
        var count = WebAppRules.ParseWhatsAppUnread(title);
        // Log the title SHAPE once (digits masked) — the parser was written blind, this
        // is how a wrong format shows up in the log without leaking anything private.
        if (!_titleLogged && count is > 0)
        {
            _titleLogged = true;
            Logger.Info($"WhatsApp title format: '{System.Text.RegularExpressions.Regex.Replace(title, @"\d", "#")}'");
        }
        if (count is not { } n || n == _unread) return;
        _unread = n;
        UnreadChanged?.Invoke(n);
    }

    private ToggleButton Chip(bool initial, Action<bool> onChange)
    {
        var t = new ToggleButton
        {
            IsChecked = initial,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = Cursors.Hand,
            Foreground = (Brush)Application.Current.FindResource("TextBrush"),
            Template = (ControlTemplate)Application.Current.FindResource("LauncherToggleTemplate")
        };
        t.Checked += (_, _) => onChange(true);
        t.Unchecked += (_, _) => onChange(false);
        return t;
    }

    public void Dispose() => _web.Dispose();
}

/// <summary>A widget that embeds a web app — started lazily, never rebuilt on language switch.</summary>
public interface IWebWidget : IDisposable
{
    /// <summary>Start the embedded browser (first real show; never during prewarm).</summary>
    void EnsureStarted();
    /// <summary>Re-apply localized texts without recreating the browser session.</summary>
    void Relocalize();
    /// <summary>True while the user is typing into the page.</summary>
    bool HasKeyboardFocusWithin { get; }
}
