using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media;
using WKI_Clipper.Models;
using WKI_Clipper.Services;

namespace WKI_Clipper.Views;

/// <summary>
/// Spotify widget with two views:
///  • Compact — cover, title, seekable progress, transport, Spotify's own volume. Drives
///    the user's Spotify APP through the Windows media session; small enough to pin over
///    a game.
///  • Full — the Spotify web interface (search, playlists, library) with the user's own
///    account. Normally a remote for the app (Spotify Connect); if the web player starts
///    playing by itself, the widget says so and explains how to switch back.
/// </summary>
public sealed class SpotifyView : UserControl, IWebWidget
{
    private static AppHost Host => App.Host;
    private static SpotifySettings Cfg => Host.Settings.Current.Spotify;

    // Icons via IconGlyph: the font must sit on the TextBlock itself (see there).
    private static readonly FontFamily IconFont = IconGlyph.Font;
    private static TextBlock Icon(string glyph) => IconGlyph.Make(glyph);
    private const string IconPrev = IconGlyph.Previous, IconNext = IconGlyph.Next, IconPlay = IconGlyph.Play,
                         IconPause = IconGlyph.Pause, IconShuffle = IconGlyph.Shuffle, IconRepeatAll = IconGlyph.RepeatAll,
                         IconRepeatOne = IconGlyph.RepeatOne, IconVolume = IconGlyph.Volume;

    // compact
    private readonly Grid _compact = new();
    private readonly System.Windows.Controls.Image _cover = new() { Width = 64, Height = 64, Stretch = System.Windows.Media.Stretch.UniformToFill };
    private readonly Border _coverFrame;
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _artist = new() { TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    private readonly Button _launch = new() { Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
    private readonly Slider _progress = new() { Minimum = 0, Maximum = 1, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
    private readonly TextBlock _posText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
    private readonly TextBlock _durText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
    private readonly Button _prev, _play, _next, _repeat;
    private readonly ToggleButton _shuffle;
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 1, Width = 80, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _volumeIcon = new() { Text = IconVolume, FontFamily = IconFont, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };
    private readonly Button _toFull = new() { Padding = new Thickness(10, 4, 10, 4) };

    // full
    private readonly DockPanel _full = new();
    private readonly Button _toCompact = new() { Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock _webHint = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _webHost = new();
    private WebAppHost? _web;

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private int _tickCount;
    private bool _seeking;
    private bool _settingVolume;
    private byte[]? _shownCover;
    private bool? _installed;
    private bool _appRunning;
    private bool _webPlaying;
    private (string Title, string Artist)? _webTrack;
    private float _pendingVolume = -1;
    private int _volumeWorker;

    /// <summary>Compact ↔ full switched; the host swaps the window size.</summary>
    public event Action<bool>? ViewModeChanged;
    /// <summary>The Spotify app was launched from here (it may grab focus; host returns it).</summary>
    public event Action? ExternalAppLaunched;

    public SpotifyView()
    {
        _prev = TransportButton(IconPrev, () => _ = Host.Spotify.PreviousAsync());
        _play = TransportButton(IconPlay, () => _ = Host.Spotify.TogglePlayPauseAsync());
        _next = TransportButton(IconNext, () => _ = Host.Spotify.NextAsync());
        _repeat = TransportButton(IconRepeatAll, () => _ = Host.Spotify.CycleRepeatAsync());
        _shuffle = new ToggleButton
        {
            Content = Icon(IconShuffle),
            Width = 34, Height = 30, Margin = new Thickness(0, 0, 4, 0), Cursor = Cursors.Hand,
            Foreground = (Brush)Application.Current.FindResource("TextBrush"),
            Template = (ControlTemplate)Application.Current.FindResource("LauncherToggleTemplate")
        };
        _shuffle.Click += (_, _) => _ = Host.Spotify.SetShuffleAsync(_shuffle.IsChecked == true);

        _launch.Click += async (_, _) =>
        {
            ExternalAppLaunched?.Invoke();
            await SpotifyMediaService.LaunchAppAsync();
        };
        _toFull.Click += (_, _) => SetFullView(true);
        _toCompact.Click += (_, _) => SetFullView(false);

        // Seek on release (click or drag) — never fight the user while they hold the thumb.
        _progress.PreviewMouseLeftButtonDown += (_, _) => _seeking = true;
        _progress.PreviewMouseLeftButtonUp += (_, _) =>
        {
            _seeking = false;
            _ = Host.Spotify.SeekAsync(TimeSpan.FromSeconds(_progress.Value));
        };
        _volume.ValueChanged += (_, e) => { if (!_settingVolume) QueueVolume((float)e.NewValue); };

        _coverFrame = new Border
        {
            Width = 64, Height = 64, CornerRadius = new CornerRadius(4), ClipToBounds = true,
            Background = (Brush)Application.Current.FindResource("PanelBrush"),
            Child = _cover, Margin = new Thickness(0, 0, 10, 0)
        };

        BuildCompact();
        BuildFull();

        var root = new Grid();
        root.Children.Add(_compact);
        root.Children.Add(_full);
        Content = root;

        ApplyTexts();
        ShowView(Cfg.FullView);

        _tick.Tick += (_, _) => OnTick();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { _tick.Start(); PollAppState(); Render(); }
            else _tick.Stop();
        };
        Loaded += async (_, _) =>
        {
            Host.Spotify.StateChanged -= OnServiceState;
            Host.Spotify.StateChanged += OnServiceState;
            await Host.Spotify.StartAsync();
            _installed ??= await SpotifyMediaService.IsInstalledAsync();
            Render();
        };
        Unloaded += (_, _) => Host.Spotify.StateChanged -= OnServiceState;
    }

    // ---- layout ----

    private void BuildCompact()
    {
        _compact.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _compact.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _compact.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _compact.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var statusStack = new StackPanel();
        statusStack.Children.Add(_status);
        statusStack.Children.Add(_launch);
        Grid.SetRow(statusStack, 0);
        _compact.Children.Add(statusStack);

        var info = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(_coverFrame, Dock.Left);
        info.Children.Add(_coverFrame);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(_title);
        _artist.Style = (Style)Application.Current.FindResource("MutedStyle");
        texts.Children.Add(_artist);
        info.Children.Add(texts);
        Grid.SetRow(info, 1);
        _compact.Children.Add(info);

        var progressRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        _posText.Style = (Style)Application.Current.FindResource("MutedStyle");
        _durText.Style = (Style)Application.Current.FindResource("MutedStyle");
        DockPanel.SetDock(_posText, Dock.Left);
        DockPanel.SetDock(_durText, Dock.Right);
        progressRow.Children.Add(_posText);
        progressRow.Children.Add(_durText);
        progressRow.Children.Add(_progress);
        Grid.SetRow(progressRow, 2);
        _compact.Children.Add(progressRow);

        var controls = new DockPanel { LastChildFill = false };
        var transport = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        transport.Children.Add(_prev);
        transport.Children.Add(_play);
        transport.Children.Add(_next);
        transport.Children.Add(_shuffle);
        transport.Children.Add(_repeat);
        transport.Children.Add(_volumeIcon);
        transport.Children.Add(_volume);
        controls.Children.Add(transport);
        DockPanel.SetDock(_toFull, Dock.Right);
        controls.Children.Add(_toFull);
        Grid.SetRow(controls, 3);
        _compact.Children.Add(controls);
    }

    private void BuildFull()
    {
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(_toCompact, Dock.Left);
        bar.Children.Add(_toCompact);
        _webHint.Foreground = (Brush)Application.Current.FindResource("AccentBrush");
        bar.Children.Add(_webHint);
        DockPanel.SetDock(bar, Dock.Top);
        _full.Children.Add(bar);
        _full.Children.Add(_webHost);
    }

    private Button TransportButton(string glyph, Action action)
    {
        var b = new Button { Content = Icon(glyph), Width = 34, Height = 30, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(0) };
        b.Click += (_, _) => action();
        return b;
    }

    private void ApplyTexts()
    {
        _toFull.Content = L.T("Vollansicht ▸", "Full view ▸");
        _toFull.ToolTip = L.T("Spotify-Oberfläche: Suche, Playlists, Bibliothek", "Spotify interface: search, playlists, library");
        _toCompact.Content = L.T("◂ Kompakt", "◂ Compact");
        _launch.Content = L.T("Spotify starten", "Start Spotify");
        _prev.ToolTip = L.T("Zurück", "Previous");
        _play.ToolTip = L.T("Abspielen/Pause", "Play/pause");
        _next.ToolTip = L.T("Weiter", "Next");
        _shuffle.ToolTip = L.T("Zufallswiedergabe", "Shuffle");
        _volume.ToolTip = L.T("Spotify-Lautstärke (Windows-Mixer, nur Spotify)", "Spotify volume (Windows mixer, Spotify only)");
        _web?.Relocalize();
        Render();
    }

    // ---- view switching ----

    private void SetFullView(bool full)
    {
        if (Cfg.FullView == full) return;
        Cfg.FullView = full;
        Host.Settings.Save();
        ShowView(full);
        if (full) StartWeb();
        ViewModeChanged?.Invoke(full);
    }

    private void ShowView(bool full)
    {
        _compact.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
        _full.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StartWeb()
    {
        if (_web == null)
        {
            _web = new WebAppHost(WebApp.Spotify) { Zoom = Cfg.Zoom };
            _web.ZoomChanged += z => { Cfg.Zoom = z; Host.Settings.Save(); };
            _web.PlayingAudioChanged += playing => Dispatcher.BeginInvoke(new Action(() =>
            {
                _webPlaying = playing;
                if (playing) Logger.Info("Spotify: the web player inside the widget is playing audio itself.");
                Render();
            }));
            _web.TitleChanged += t => Dispatcher.BeginInvoke(new Action(() =>
            {
                _webTrack = WebAppRules.ParseSpotifyTitle(t);
                Render();
            }));
            _webHost.Child = _web;
        }
        _web.EnsureStarted();
    }

    // ---- IWebWidget ----

    public void EnsureStarted()
    {
        if (Cfg.FullView) StartWeb();
    }

    public bool HasKeyboardFocusWithin => _web?.HasKeyboardFocusWithin == true;

    public void Relocalize() => ApplyTexts();

    // ---- state ----

    private void OnServiceState() => Dispatcher.BeginInvoke(new Action(Render));

    private void OnTick()
    {
        UpdateProgress();
        // App state + mixer volume every ~3 s; neither raises events.
        if (++_tickCount % 6 == 0) PollAppState();
    }

    private void PollAppState()
    {
        _ = Task.Run(() =>
        {
            bool running = SpotifyMediaService.IsAppRunning();
            float? vol = running ? SpotifyMediaService.GetAppVolume() : null;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _appRunning = running;
                if (!_volume.IsMouseCaptureWithin)
                {
                    _settingVolume = true;
                    _volume.IsEnabled = vol.HasValue;
                    if (vol is { } v) _volume.Value = v;
                    _settingVolume = false;
                }
                _volume.ToolTip = vol.HasValue
                    ? L.T("Spotify-Lautstärke (Windows-Mixer, nur Spotify)", "Spotify volume (Windows mixer, Spotify only)")
                    : L.T("Verfügbar, sobald Spotify etwas abspielt", "Available once Spotify plays something");
                Render();
            }));
        });
    }

    private void QueueVolume(float v)
    {
        // Slider drags fire per pixel; one worker applies only the latest value.
        System.Threading.Volatile.Write(ref _pendingVolume, v);
        if (System.Threading.Interlocked.Exchange(ref _volumeWorker, 1) == 1) return;
        _ = Task.Run(() =>
        {
            float applied;
            do
            {
                try
                {
                    do
                    {
                        applied = System.Threading.Volatile.Read(ref _pendingVolume);
                        SpotifyMediaService.SetAppVolume(applied);
                    } while (Math.Abs(applied - System.Threading.Volatile.Read(ref _pendingVolume)) > 0.0001f);
                }
                finally { System.Threading.Interlocked.Exchange(ref _volumeWorker, 0); }
                // A value set between the last check and releasing the flag found the flag
                // still taken and returned — pick it up here instead of dropping it.
            } while (Math.Abs(applied - System.Threading.Volatile.Read(ref _pendingVolume)) > 0.0001f
                     && System.Threading.Interlocked.Exchange(ref _volumeWorker, 1) == 0);
        });
    }

    private void Render()
    {
        var snap = Host.Spotify.Current;

        // Status line — exactly one explanation of what is going on.
        string? status = null;
        bool showLaunch = false;
        if (_webPlaying)
            status = L.T("Spielt im Widget (Web-Player) statt in deiner Spotify-App. Zum Wechseln in der Vollansicht unten rechts auf das Geräte-Symbol klicken und deinen PC wählen.",
                         "Playing in the widget (web player) instead of your Spotify app. To switch, click the devices icon at the bottom right of the full view and pick your PC.");
        else if (!snap.HasSession)
        {
            if (_appRunning)
                status = L.T("Spotify ist offen — starte einen Titel, dann erscheint er hier.",
                             "Spotify is open — start a track and it shows up here.");
            else if (_installed == false)
                status = L.T("Keine Spotify-App gefunden. Die Vollansicht funktioniert trotzdem.",
                             "No Spotify app found. The full view still works.");
            else
            {
                status = L.T("Spotify läuft nicht.", "Spotify is not running.");
                showLaunch = true;
            }
        }
        _status.Text = status ?? "";
        _status.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
        _status.Foreground = (Brush)Application.Current.FindResource(_webPlaying ? "AccentBrush" : "MutedBrush");
        _launch.Visibility = showLaunch ? Visibility.Visible : Visibility.Collapsed;
        _webHint.Text = _webPlaying ? status! : "";

        // Track info: the app's session first; the web player's title as fallback.
        if (snap.HasSession)
        {
            _title.Text = snap.Title.Length > 0 ? snap.Title : "—";
            _artist.Text = snap.Artist;
        }
        else if (_webPlaying && _webTrack is { } wt)
        {
            _title.Text = wt.Title;
            _artist.Text = wt.Artist;
        }
        else
        {
            _title.Text = "Spotify";
            _artist.Text = "";
        }

        if (!ReferenceEquals(snap.Cover, _shownCover))
        {
            _shownCover = snap.Cover;
            _cover.Source = LoadCover(snap.Cover);
        }

        bool controllable = snap.HasSession;
        _prev.IsEnabled = _play.IsEnabled = _next.IsEnabled = controllable;
        _shuffle.IsEnabled = controllable && snap.Shuffle.HasValue;
        _repeat.IsEnabled = controllable && snap.Repeat.HasValue;
        ((TextBlock)_play.Content).Text = snap.IsPlaying ? IconPause : IconPlay;
        _shuffle.IsChecked = snap.Shuffle == true;
        ((TextBlock)_repeat.Content).Text = snap.Repeat == MediaPlaybackAutoRepeatMode.Track ? IconRepeatOne : IconRepeatAll;
        _repeat.Opacity = snap.Repeat is MediaPlaybackAutoRepeatMode.List or MediaPlaybackAutoRepeatMode.Track ? 1.0 : 0.5;
        _repeat.ToolTip = snap.Repeat switch
        {
            MediaPlaybackAutoRepeatMode.List => L.T("Wiederholen: Liste", "Repeat: list"),
            MediaPlaybackAutoRepeatMode.Track => L.T("Wiederholen: Titel", "Repeat: track"),
            _ => L.T("Wiederholen: aus", "Repeat: off")
        };
        _progress.IsEnabled = controllable && snap.CanSeek;
        UpdateProgress();
    }

    private void UpdateProgress()
    {
        var snap = Host.Spotify.Current;
        var dur = snap.Duration;
        _durText.Text = Format(dur);
        if (_seeking) { _posText.Text = Format(TimeSpan.FromSeconds(_progress.Value)); return; }
        var pos = snap.PositionAt(DateTimeOffset.Now);
        _progress.Maximum = Math.Max(1, dur.TotalSeconds);
        _progress.Value = Math.Min(_progress.Maximum, pos.TotalSeconds);
        _posText.Text = Format(pos);
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private static BitmapImage? LoadCover(byte[]? data)
    {
        if (data is null) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 128;
            bmp.StreamSource = new MemoryStream(data);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _tick.Stop();
        Host.Spotify.StateChanged -= OnServiceState;
        _web?.Dispose();
    }
}
