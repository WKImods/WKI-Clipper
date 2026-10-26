using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WKI_Clipper.Services;
using HAlign = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Orient = System.Windows.Controls.Orientation;
using ProgressBar = System.Windows.Controls.ProgressBar;
using RadioButton = System.Windows.Controls.RadioButton;

namespace WKI_Clipper.Views;

/// <summary>
/// "Schneiden…": the clip editor. A normal (never layered) window, because a layered window renders video in
/// software and stutters. Preview with WPF's MediaElement; the frame is window state, not MediaElement.Position,
/// and every jump goes through <see cref="ClipTimeline"/> (preview time and ffmpeg time are two different axes —
/// see <see cref="Mp4Index"/>). When Media Foundation cannot play the clip the window falls back to still frames.
/// </summary>
public sealed class ClipEditorWindow : Window
{
    private enum EditorState { Loading, Editing, Running, Done, Unsupported }

    private static readonly Dictionary<string, ClipEditorWindow> OpenWindows = new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan ScrubInterval = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan GrabQuietTime = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);

    private readonly EditorServices _svc;
    private readonly string _path;

    // clip
    private Mp4VideoIndex? _index;
    private ClipTimeline? _tl;
    private double _origin;                 // preview time of the first frame (the timeline starts there)
    private int _frame, _in, _out, _appliedFrame = -1;
    private CutMode _mode = CutMode.Fast;

    // state
    private EditorState _state = EditorState.Loading;
    private bool _fallback, _previewOpened, _previewClosed, _playing, _originalGone, _closeAfterCut, _closed, _recycling;
    private long _lastSeek;
    private string? _resultPath;
    private CancellationTokenSource? _cutCts;
    private CancellationTokenSource? _grabCts;
    private FramePreviewService? _grabs;
    private CutResult? _lastResult;
    private string? _lastRecycleText;
    private bool _lastRecycleOk;
    private string? _errorText;

    private readonly DispatcherTimer _playTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly DispatcherTimer _scrubTimer = new() { Interval = ScrubInterval };
    private readonly DispatcherTimer _grabTimer = new() { Interval = GrabQuietTime };
    private readonly DispatcherTimer _openTimer = new() { Interval = OpenTimeout };

    // preview
    private readonly MediaElement _media = new()
    {
        LoadedBehavior = MediaState.Manual,
        UnloadedBehavior = MediaState.Manual,
        ScrubbingEnabled = true,
        Stretch = Stretch.Uniform,
        Volume = 0.8,
        Focusable = false
    };
    private readonly Image _still = new() { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
    private readonly TextBlock _overlay = new()
    {
        Foreground = Brushes.White, FontSize = 15, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HAlign.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(30)
    };
    private readonly TextBlock _positionText = new() { FontFamily = new FontFamily("Consolas"), FontSize = 14 };
    private readonly TextBlock _previewHint = new() { TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HAlign.Right, TextAlignment = TextAlignment.Right };

    // timeline + transport
    private readonly EditorTimeline _timeline = new();
    private readonly Button _btnStart = new(), _btnBack1S = new(), _btnBackFrame = new(), _btnPlay = new(),
                            _btnFwdFrame = new(), _btnFwd1S = new(), _btnEnd = new(), _btnMute = new();

    // marks, mode, output
    private readonly Button _setIn = new(), _setOut = new();
    private readonly TextBlock _marksText = new() { FontFamily = new FontFamily("Consolas") };
    private readonly TextBlock _snapText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly RadioButton _fast = new(), _exact = new();
    private readonly TextBlock _modeText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _sizeText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _recycleBox = new();
    private readonly TextBlock _validationText = new() { TextWrapping = TextWrapping.Wrap };

    // running / done
    private readonly StackPanel _runPanel = new() { Visibility = Visibility.Collapsed };
    private readonly ProgressBar _progress = new() { Height = 8, Minimum = 0, Maximum = 1 };
    private readonly TextBlock _progressText = new();
    private readonly Button _cancelBtn = new();
    private readonly StackPanel _donePanel = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _doneText = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _doneNote = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _openBtn = new(), _folderBtn = new(), _discordSmall = new(), _discordLarge = new(), _again = new();
    private readonly Button _cutBtn = new(), _closeBtn = new();

    private bool _discordSmallStarted, _discordLargeStarted;

    // ================================================================ entry

    /// <summary>Opens the editor for a clip; a second call for the same file brings the open window forward.</summary>
    public static void Open(AppHost host, string path)
        => Open(new EditorServices(host.FFmpeg.FFmpegPath, host.ClipCut, host.Discord,
                () => host.Settings.Current.Discord, () => host.AvailableCodecs.Select(c => c.FFmpegName)), path);

    internal static ClipEditorWindow Open(EditorServices services, string path)
    {
        string key;
        try { key = Path.GetFullPath(path); } catch { key = path; }
        if (OpenWindows.TryGetValue(key, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }
        var window = new ClipEditorWindow(services, path);
        OpenWindows[key] = window;
        window._key = key;
        window.Show();
        return window;
    }

    /// <summary>What the window needs from the app (and what a test can supply without starting the app).</summary>
    internal sealed record EditorServices(
        string FFmpegPath, ClipCutService ClipCut, DiscordCompressService Discord,
        Func<Models.DiscordCompressSettings> DiscordSettings, Func<IEnumerable<string>> Encoders);

    /// <summary>For tests: the state machine's current state ("Loading", "Editing", "Running", "Done", "Unsupported").</summary>
    internal string StateName => _state.ToString();
    private string _key = "";

    private ClipEditorWindow(EditorServices services, string path)
    {
        _svc = services;
        _path = path;

        Width = 1100; Height = 760; MinWidth = 820; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        SetResourceReference(BackgroundProperty, "BgBrush");   // palette brushes: a reference, so a theme switch recolours the open editor
        Foreground = Res("TextBrush");

        BuildUi();
        WireEvents();
        ApplyTexts();
        SetState(EditorState.Loading);

        L.LanguageChanged += OnLanguageChanged;
        Loaded += (_, _) => _ = LoadAsync();
    }

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    // ================================================================ UI

    private void BuildUi()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 1. preview
        var preview = new Grid { Background = Brushes.Black };
        preview.Children.Add(_media);
        preview.Children.Add(_still);
        preview.Children.Add(_overlay);
        Grid.SetRow(preview, 0);
        root.Children.Add(preview);

        var panel = new StackPanel { Margin = new Thickness(16, 10, 16, 14) };
        Grid.SetRow(panel, 1);
        root.Children.Add(panel);

        // 2. position
        var posRow = new Grid();
        posRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        posRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        posRow.Children.Add(_positionText);
        _previewHint.Style = (Style)FindResourceStyle("MutedStyle");
        Grid.SetColumn(_previewHint, 1);
        posRow.Children.Add(_previewHint);
        panel.Children.Add(posRow);

        // 3. timeline
        _timeline.Margin = new Thickness(0, 6, 0, 6);
        panel.Children.Add(_timeline);

        // 4. transport
        var transport = new StackPanel { Orientation = Orient.Horizontal, HorizontalAlignment = HAlign.Center };
        foreach (var b in new[] { _btnStart, _btnBack1S, _btnBackFrame, _btnPlay, _btnFwdFrame, _btnFwd1S, _btnEnd })
        {
            b.MinWidth = 44; b.Padding = new Thickness(8, 5, 8, 5); b.Margin = new Thickness(2, 0, 2, 0); b.Focusable = false;
            transport.Children.Add(b);
        }
        _btnMute.MinWidth = 44; _btnMute.Padding = new Thickness(8, 5, 8, 5); _btnMute.Margin = new Thickness(16, 0, 2, 0); _btnMute.Focusable = false;
        transport.Children.Add(_btnMute);
        SetGlyph(_btnStart, IconGlyph.Previous);
        SetGlyph(_btnEnd, IconGlyph.Next);
        SetGlyph(_btnPlay, IconGlyph.Play);
        SetGlyph(_btnMute, IconGlyph.Volume);
        _btnBack1S.Content = "−1 s";
        _btnFwd1S.Content = "+1 s";
        _btnBackFrame.Content = SymbolText("◂");
        _btnFwdFrame.Content = SymbolText("▸");
        panel.Children.Add(transport);

        // 5. marks
        var marks = new StackPanel { Orientation = Orient.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        _setIn.Padding = _setOut.Padding = new Thickness(10, 5, 10, 5);
        _setIn.Margin = new Thickness(0, 0, 6, 0);
        _setOut.Margin = new Thickness(0, 0, 14, 0);
        _setIn.Focusable = _setOut.Focusable = false;
        _marksText.VerticalAlignment = VerticalAlignment.Center;
        marks.Children.Add(_setIn);
        marks.Children.Add(_setOut);
        marks.Children.Add(_marksText);
        panel.Children.Add(marks);
        _snapText.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        _snapText.Margin = new Thickness(0, 4, 0, 0);
        panel.Children.Add(_snapText);

        // 6. mode
        var modeRow = new StackPanel { Orientation = Orient.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        _fast.Margin = new Thickness(0, 0, 18, 0);
        _fast.GroupName = _exact.GroupName = "mode";
        _fast.Foreground = _exact.Foreground = Res("TextBrush");
        _fast.IsChecked = true;
        _fast.Focusable = _exact.Focusable = false;
        modeRow.Children.Add(_fast);
        modeRow.Children.Add(_exact);
        panel.Children.Add(modeRow);
        _modeText.Style = (Style)FindResourceStyle("MutedStyle");
        _modeText.Margin = new Thickness(0, 3, 0, 0);
        panel.Children.Add(_modeText);

        // 7. output
        _sizeText.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(_sizeText);
        _recycleBox.Margin = new Thickness(0, 6, 0, 0);
        _recycleBox.Foreground = Res("TextBrush");
        _recycleBox.Focusable = false;
        panel.Children.Add(_recycleBox);

        // running / done
        _runPanel.Margin = new Thickness(0, 10, 0, 0);
        _progress.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "AccentBrush");
        _progress.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "PanelBrush");
        _progress.BorderThickness = new Thickness(0);
        _runPanel.Children.Add(_progress);
        var runRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        runRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        runRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        runRow.Children.Add(_progressText);
        _cancelBtn.Padding = new Thickness(14, 5, 14, 5);
        Grid.SetColumn(_cancelBtn, 1);
        runRow.Children.Add(_cancelBtn);
        _runPanel.Children.Add(runRow);
        panel.Children.Add(_runPanel);

        _donePanel.Margin = new Thickness(0, 10, 0, 0);
        _donePanel.Children.Add(_doneText);
        _doneNote.Margin = new Thickness(0, 2, 0, 0);
        _donePanel.Children.Add(_doneNote);
        var doneButtons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var b in new[] { _openBtn, _folderBtn, _discordSmall, _discordLarge, _again })
        {
            b.Padding = new Thickness(12, 5, 12, 5);
            b.Margin = new Thickness(0, 0, 8, 4);
            doneButtons.Children.Add(b);
        }
        _donePanel.Children.Add(doneButtons);
        panel.Children.Add(_donePanel);

        // 8. actions
        _validationText.Foreground = Res("DangerBrush");
        _validationText.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(_validationText);
        var actions = new StackPanel { Orientation = Orient.Horizontal, HorizontalAlignment = HAlign.Right, Margin = new Thickness(0, 10, 0, 0) };
        _cutBtn.MinWidth = 110;
        _cutBtn.Style = (Style)Application.Current.FindResource("AccentButton");
        _closeBtn.MinWidth = 90;
        _closeBtn.Margin = new Thickness(8, 0, 0, 0);
        actions.Children.Add(_cutBtn);
        actions.Children.Add(_closeBtn);
        panel.Children.Add(actions);

        Content = root;
    }

    private static object FindResourceStyle(string key) => Application.Current.FindResource(key);

    private static void SetGlyph(Button button, string glyph)
    {
        var icon = IconGlyph.Make(glyph, 16);
        icon.Foreground = Res("TextBrush");
        button.Content = icon;
    }

    private static TextBlock SymbolText(string symbol) => new()
    {
        Text = symbol, FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 16,
        HorizontalAlignment = HAlign.Center, VerticalAlignment = VerticalAlignment.Center
    };

    private void ApplyTexts()
    {
        Title = L.T("Schneiden — ", "Trim — ") + Path.GetFileName(_path);

        _btnStart.ToolTip = L.T("Anfang (Pos1)", "Start (Home)");
        _btnBack1S.ToolTip = L.T("1 s zurück (Umschalt+←)", "Back 1 s (Shift+←)");
        _btnBackFrame.ToolTip = L.T("1 Frame zurück (←)", "Back 1 frame (←)");
        _btnPlay.ToolTip = L.T("Abspielen/Pause (Leertaste)", "Play/pause (Space)");
        _btnFwdFrame.ToolTip = L.T("1 Frame vor (→)", "Forward 1 frame (→)");
        _btnFwd1S.ToolTip = L.T("1 s vor (Umschalt+→)", "Forward 1 s (Shift+→)");
        _btnEnd.ToolTip = L.T("Ende (Ende)", "End (End)");
        _btnMute.ToolTip = L.T("Ton an/aus (M)", "Sound on/off (M)");

        _setIn.Content = L.T("Start setzen (I)", "Set start (I)");
        _setOut.Content = L.T("Ende setzen (O)", "Set end (O)");
        _fast.Content = L.T("Schnell (verlustfrei)", "Fast (lossless)");
        _exact.Content = L.T("Genau (neu kodieren)", "Exact (re-encode)");
        _recycleBox.Content = L.T("Original danach in den Papierkorb verschieben", "Move the original to the Recycle Bin afterwards");
        _cutBtn.Content = L.T("Schneiden", "Trim");
        _closeBtn.Content = L.T("Schließen", "Close");
        _cancelBtn.Content = L.T("Abbrechen", "Cancel");
        _openBtn.Content = L.T("Öffnen", "Open");
        _folderBtn.Content = L.T("Im Ordner zeigen", "Show in folder");
        _again.Content = L.T("Weiter schneiden", "Keep trimming");

        _recycleBox.ToolTip = RecycleBin.IsSupportedPath(_path) ? null
            : L.T("Nicht verfügbar für Netzlaufwerke und Laufwerke ohne Papierkorb (z. B. USB-Sticks).",
                  "Not available for network drives and drives without a Recycle Bin (such as USB sticks).");
        _recycleBox.IsEnabled = RecycleBin.IsSupportedPath(_path) && !_originalGone;

        if (_state == EditorState.Loading) _overlay.Text = L.T("Clip wird geladen…", "Loading clip…");
        if (_state == EditorState.Unsupported)
            _overlay.Text = L.T("Dieser Clip kann nicht geschnitten werden (kein lesbares MP4).", "This clip cannot be trimmed (not a readable MP4).");
        UpdateDiscordButtons();
        UpdateFallbackHint();
        RefreshAll();
        RefreshDone();
    }

    private void OnLanguageChanged() => Dispatcher.BeginInvoke(new Action(ApplyTexts));

    // ================================================================ events

    private void WireEvents()
    {
        PreviewKeyDown += OnKey;
        Closing += OnClosing;
        Closed += OnClosed;

        _media.MediaOpened += (_, _) => OnMediaOpened();
        _media.MediaFailed += (_, _) => { if (!_closed && !_fallback) EnterFallback(); };
        _media.MediaEnded += (_, _) => { PausePlayback(); if (_tl is not null) GoTo(_tl.LastFrame, immediate: true); };

        _playTimer.Tick += (_, _) => OnPlayTick();
        _scrubTimer.Tick += (_, _) => { _scrubTimer.Stop(); if (_appliedFrame != _frame) ApplySeek(_frame); };
        _grabTimer.Tick += (_, _) => { _grabTimer.Stop(); _ = ShowStillAsync(_frame); };
        _openTimer.Tick += (_, _) => { _openTimer.Stop(); if (!_previewOpened && !_fallback) EnterFallback(); };

        _btnStart.Click += (_, _) => Do(() => GoTo(0, true));
        _btnEnd.Click += (_, _) => Do(() => GoTo(_tl!.LastFrame, true));
        _btnBack1S.Click += (_, _) => Do(() => GoTo(_tl!.FrameAtPreviewOffset(_frame, -1), true));
        _btnFwd1S.Click += (_, _) => Do(() => GoTo(_tl!.FrameAtPreviewOffset(_frame, 1), true));
        _btnBackFrame.Click += (_, _) => Do(() => GoTo(_frame - 1, true));
        _btnFwdFrame.Click += (_, _) => Do(() => GoTo(_frame + 1, true));
        _btnPlay.Click += (_, _) => TogglePlay();
        _btnMute.Click += (_, _) => ToggleMute();

        _setIn.Click += (_, _) => SetIn(_frame);
        _setOut.Click += (_, _) => SetOut(_frame);
        _fast.Checked += (_, _) => { _mode = CutMode.Fast; RefreshAll(); };
        _exact.Checked += (_, _) => { _mode = CutMode.Exact; RefreshAll(); };

        _timeline.Scrub += seconds => Do(() => GoTo(_tl!.FrameAtPreview(seconds + _origin), immediate: false));
        _timeline.InDragged += seconds => { if (_tl is null) return; int f = _tl.FrameAtPreview(seconds + _origin); SetIn(f); GoTo(f, false); };
        _timeline.OutDragged += seconds => { if (_tl is null) return; int f = _tl.FrameAtPreview(seconds + _origin); SetOut(f); GoTo(f, false); };

        _cutBtn.Click += (_, _) => _ = StartCutAsync();
        _closeBtn.Click += (_, _) => Close();
        _cancelBtn.Click += (_, _) => _cutCts?.Cancel();

        _openBtn.Click += (_, _) => OpenFile(_resultPath);
        _folderBtn.Click += (_, _) => ShowInFolder(_resultPath);
        _discordSmall.Click += (_, _) => StartDiscord(small: true);
        _discordLarge.Click += (_, _) => StartDiscord(small: false);
        _again.Click += (_, _) => ContinueTrimming();
    }

    /// <summary>Runs a navigation action when there is a clip to navigate; playback is paused first.</summary>
    private void Do(Action action)
    {
        if (_tl is null || _state is EditorState.Loading or EditorState.Unsupported) return;
        PausePlayback();
        action();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { Close(); e.Handled = true; return; }
        if (_tl is null || _state is EditorState.Loading or EditorState.Unsupported) return;
        var tl = _tl;

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool handled = true;
        switch (key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Left:
                Do(() =>
                {
                    if (ctrl) GoTo(tl.PreviousKeyframe(_frame) ?? 0, true);
                    else if (shift) GoTo(tl.FrameAtPreviewOffset(_frame, -1), true);
                    else GoTo(_frame - 1, true);
                });
                break;
            case Key.Right:
                Do(() =>
                {
                    if (ctrl) GoTo(tl.NextKeyframe(_frame) ?? tl.LastFrame, true);
                    else if (shift) GoTo(tl.FrameAtPreviewOffset(_frame, 1), true);
                    else GoTo(_frame + 1, true);
                });
                break;
            case Key.Home: Do(() => GoTo(0, true)); break;
            case Key.End: Do(() => GoTo(tl.LastFrame, true)); break;
            case Key.I: Do(() => { if (shift) GoTo(_in, true); else SetIn(_frame); }); break;
            case Key.O: Do(() => { if (shift) GoTo(_out, true); else SetOut(_frame); }); break;
            case Key.M: ToggleMute(); break;
            default: handled = false; break;
        }
        if (handled) e.Handled = true;
    }

    // ================================================================ loading

    private async Task LoadAsync()
    {
        var index = await Task.Run(() => Mp4Index.TryRead(_path)).ConfigureAwait(true);
        if (_closed) return;
        if (index is null)
        {
            SetState(EditorState.Unsupported);
            ApplyTexts();
            return;
        }

        _index = index;
        _tl = new ClipTimeline(index);
        _origin = index.PreviewSeconds[0];
        _in = 0;
        _out = _tl.LastFrame;
        _frame = 0;
        OpenPreview();
    }

    private void OpenPreview()
    {
        _previewClosed = false;
        _previewOpened = false;
        if (_fallback) { EnterEditingIfLoading(); GoTo(_frame, true); return; }

        _media.Visibility = Visibility.Visible;
        _overlay.Visibility = Visibility.Visible;
        _overlay.Text = L.T("Clip wird geladen…", "Loading clip…");
        try
        {
            _media.Source = new Uri(_path);
            _media.Play();     // MediaElement shows its first frame only after Play(); Pause() right away keeps it still
            _media.Pause();
            _openTimer.Stop();
            _openTimer.Start();
        }
        catch (Exception ex)
        {
            Logger.Warn("Editor preview could not be opened: " + ex.Message);
            EnterFallback();
        }
    }

    private void OnMediaOpened()
    {
        if (_closed || _fallback || _tl is null) return;
        if (!_media.HasVideo || _media.NaturalVideoWidth <= 0) { EnterFallback(); return; }

        _openTimer.Stop();
        _previewOpened = true;
        _overlay.Visibility = Visibility.Collapsed;
        EnterEditingIfLoading();
        _media.Volume = 0.8;
        ApplySeek(_frame);
    }

    /// <summary>The clip cannot be played here: still frames from ffmpeg instead, ▶ opens the default player.</summary>
    private void EnterFallback()
    {
        _fallback = true;
        _openTimer.Stop();
        PausePlayback();
        try { _media.Close(); _media.Source = null; } catch { }
        _media.Visibility = Visibility.Collapsed;
        _still.Visibility = Visibility.Visible;
        _overlay.Visibility = Visibility.Collapsed;
        _grabs ??= new FramePreviewService(_svc.FFmpegPath);
        UpdateFallbackHint();
        EnterEditingIfLoading();
        GoTo(_frame, true);
        Logger.Info($"Editor preview fell back to still frames ({_index?.Codec}).");
    }

    private void UpdateFallbackHint()
    {
        if (!_fallback) { _previewHint.Text = ""; return; }
        var text = L.T("Vorschau nicht verfügbar — Standbilder per FFmpeg. ▶ öffnet den Standardplayer.",
                       "Preview unavailable — still frames via FFmpeg. ▶ opens the default player.");
        if (_index?.Codec is "hvc1" or "hev1")
            text += " " + L.T("Für HEVC-Clips fehlt die „HEVC-Videoerweiterung“ aus dem Microsoft Store.",
                              "HEVC clips need the \"HEVC Video Extensions\" from the Microsoft Store.");
        else if (_index?.Codec is "av01")
            text += " " + L.T("Für AV1-Clips fehlt die „AV1 Video Extension“ aus dem Microsoft Store.",
                              "AV1 clips need the \"AV1 Video Extension\" from the Microsoft Store.");
        _previewHint.Text = text;
    }

    private void EnterEditingIfLoading()
    {
        if (_state == EditorState.Loading) SetState(EditorState.Editing);
        RefreshAll();
    }

    // ================================================================ moving around

    private void GoTo(int frame, bool immediate)
    {
        if (_tl is null) return;
        _frame = Math.Clamp(frame, 0, _tl.LastFrame);
        UpdatePlayhead();

        if (_fallback) { _grabTimer.Stop(); if (immediate) _ = ShowStillAsync(_frame); else _grabTimer.Start(); return; }
        if (!_previewOpened) return;

        if (immediate) { _scrubTimer.Stop(); ApplySeek(_frame); return; }
        // scrubbing: a seek costs 50-400 ms, so only the last wanted frame counts, at most every 80 ms
        if (Stopwatch.GetElapsedTime(_lastSeek) >= ScrubInterval) { _scrubTimer.Stop(); ApplySeek(_frame); }
        else if (!_scrubTimer.IsEnabled) _scrubTimer.Start();
    }

    private void ApplySeek(int frame)
    {
        if (_tl is null || !_previewOpened || _fallback) return;
        _appliedFrame = frame;
        _lastSeek = Stopwatch.GetTimestamp();
        _media.Position = TimeSpan.FromSeconds(_tl.PreviewSeekTarget(frame));
    }

    private async Task ShowStillAsync(int frame)
    {
        if (_tl is null || _grabs is null || _previewClosed) return;   // a closed preview must not reopen the file
        _grabCts?.Cancel();
        var cts = _grabCts = new CancellationTokenSource();
        var bytes = await _grabs.GrabAsync(_path, _tl.FileStartOf(frame), 960, cts.Token).ConfigureAwait(true);
        if (bytes is null || cts.IsCancellationRequested || _closed || _frame != frame) return;
        try
        {
            var image = new BitmapImage();
            using (var ms = new MemoryStream(bytes))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = ms;
                image.EndInit();
            }
            image.Freeze();
            _still.Source = image;
        }
        catch (Exception ex) { Logger.Warn("Editor still frame could not be shown: " + ex.Message); }
    }

    private void TogglePlay()
    {
        if (_tl is null || _state is EditorState.Loading or EditorState.Unsupported) return;
        if (_fallback) { OpenFile(_path); return; }
        if (!_previewOpened) return;
        if (_playing) { PausePlayback(); return; }

        if (_frame >= _tl.LastFrame) { _frame = 0; ApplySeek(0); }
        _media.Play();
        _playing = true;
        _playTimer.Start();
        SetGlyph(_btnPlay, IconGlyph.Pause);
    }

    private void PausePlayback()
    {
        if (!_playing) return;
        _playing = false;
        _playTimer.Stop();
        _media.Pause();
        SetGlyph(_btnPlay, IconGlyph.Play);
        if (_tl is not null)
        {
            _frame = _tl.FrameAtPreview(_media.Position.TotalSeconds);
            ApplySeek(_frame);   // park on the middle of the frame so stepping starts from a known place
            UpdatePlayhead();
        }
    }

    private void OnPlayTick()
    {
        if (_tl is null || !_playing) return;
        _frame = _tl.FrameAtPreview(_media.Position.TotalSeconds);
        UpdatePlayhead();
    }

    private void ToggleMute()
    {
        _media.IsMuted = !_media.IsMuted;
        _btnMute.Opacity = _media.IsMuted ? 0.4 : 1.0;
    }

    private void UpdatePlayhead()
    {
        if (_tl is null || _index is null) return;
        _timeline.SetPlayhead(_index.PreviewSeconds[_frame] - _origin);
        _positionText.Text = ClipCutRules.FormatPosition(_index.PreviewSeconds[_frame] - _origin)
                             + " / " + ClipCutRules.FormatPosition(_tl.PreviewEnd - _origin)
                             + "   #" + (_frame + 1).ToString(CultureInfo.InvariantCulture);
    }

    // ================================================================ marks and numbers

    private bool CanEdit => _state == EditorState.Editing && _tl is not null;

    private void SetIn(int frame)
    {
        if (!CanEdit) return;
        _in = Math.Clamp(frame, 0, _tl!.LastFrame);
        RefreshAll();
    }

    private void SetOut(int frame)
    {
        if (!CanEdit) return;
        _out = Math.Clamp(frame, 0, _tl!.LastFrame);
        RefreshAll();
    }

    private static string Dec(double value, string format = "0.0")
        => value.ToString(format, CultureInfo.InvariantCulture).Replace(".", L.English ? "." : ",");

    private static string ApproxDuration(double seconds)
        => seconds < 90
            ? Math.Max(1, (int)Math.Round(seconds)).ToString(CultureInfo.InvariantCulture) + " s"
            : ((int)Math.Ceiling(seconds / 60)).ToString(CultureInfo.InvariantCulture) + " min";

    private static string EncoderName(CompressEncoder encoder) => encoder switch
    {
        CompressEncoder.Amf => L.T("AMD-GPU", "AMD GPU"),
        CompressEncoder.Nvenc => L.T("NVIDIA-GPU", "NVIDIA GPU"),
        CompressEncoder.Qsv => "Intel Quick Sync",
        _ => L.T("CPU (langsam)", "CPU (slow)")
    };

    private double SecondsBetween(CutRange range)
        => _tl is null || range.EndFrame < range.StartFrame ? 0
           : Math.Max(0, _tl.FileEndExclusive(range.EndFrame) - _tl.FileStartOf(range.StartFrame));

    private long EstimateBytes(CutRange range)
    {
        if (_tl is null || _index is null) return 0;
        double seconds = SecondsBetween(range);
        double total = Math.Max(0, _tl.PreviewEnd - _origin);
        if (_mode == CutMode.Fast)
        {
            long len = 0;
            try { len = new FileInfo(_path).Length; } catch { }
            return ClipCutRules.EstimateFastBytes(len, seconds, total);
        }
        long bps = ClipCutRules.ExactVideoBitrate(_index.VideoBytes, total, _index.Codec);
        return ClipCutRules.EstimateExactBytes(bps, _index.AudioTrackCount, seconds);
    }

    private void RefreshAll()
    {
        if (_tl is null || _index is null) { _cutBtn.IsEnabled = false; return; }

        var range = _tl.Resolve(_in, _out, _mode);
        var validation = _tl.Validate(range, _mode);

        var keys = _index.Keyframes.Select(k => _index.PreviewSeconds[k] - _origin).ToArray();
        double total = _tl.PreviewEnd - _origin;
        double startPos = _index.PreviewSeconds[Math.Min(range.StartFrame, _tl.LastFrame)] - _origin;
        double endPos = _index.PreviewSeconds[Math.Min(range.EndFrame, _tl.LastFrame)] + _tl.FrameDuration(Math.Min(range.EndFrame, _tl.LastFrame)) - _origin;
        double? snapFrom = range.StartSnapped ? _index.PreviewSeconds[_in] - _origin : null;
        _timeline.MarksEnabled = _state == EditorState.Editing;
        _timeline.SetState(total, startPos, endPos, _index.PreviewSeconds[_frame] - _origin, snapFrom, keys, _mode == CutMode.Exact);

        _marksText.Text = L.T("Start {0} · Ende {1} · Länge {2}", "Start {0} · End {1} · Length {2}")
            .Replace("{0}", ClipCutRules.FormatPosition(startPos))
            .Replace("{1}", ClipCutRules.FormatPosition(endPos))
            .Replace("{2}", ClipCutRules.FormatPosition(Math.Max(0, endPos - startPos)));

        string snap = "";
        if (range.StartSnapped)
            snap = L.T("Start auf Keyframe verschoben: {0} (−{1} s)", "Start moved to keyframe: {0} (−{1} s)")
                .Replace("{0}", ClipCutRules.FormatPosition(startPos))
                .Replace("{1}", Dec(_index.PreviewSeconds[_in] - _index.PreviewSeconds[range.StartFrame]));
        if (_mode == CutMode.Fast && range.EndApproximate)
            snap += (snap.Length > 0 ? "  " : "") + L.T("Das Ende kann ein paar Frames später liegen (Quelle mit B-Frames).",
                                                         "The end may be a few frames later (source uses B-frames).");
        _snapText.Text = snap;
        _snapText.Visibility = snap.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // mode explanation
        if (_mode == CutMode.Fast)
        {
            double spacing = _index.Keyframes.Length > 1
                ? (_index.PreviewSeconds[_index.Keyframes[^1]] - _index.PreviewSeconds[_index.Keyframes[0]]) / (_index.Keyframes.Length - 1)
                : total;
            _modeText.Text = L.T("Ohne Qualitätsverlust, in Sekunden fertig. Der Start springt auf den Keyframe davor (Abstand hier {0} s).",
                                 "No quality loss, done in seconds. The start moves to the keyframe before it (spacing here {0} s).")
                .Replace("{0}", Dec(spacing));
        }
        else
        {
            // the same choice the cut makes (a picture wider than 4096 px cannot use a hardware H.264 encoder)
            var encoder = ClipCutRules.PickExactEncoder(_svc.Encoders(), _index.Width, _index.Height);
            double fps = _tl.MedianFrameDuration > 0 ? 1.0 / _tl.MedianFrameDuration : 0;
            double estimate = ClipCutRules.EstimateExactSeconds(SecondsBetween(range), _index.Width, _index.Height, fps,
                                                                software: encoder == CompressEncoder.X264);
            _modeText.Text = L.T("Auf den Frame genau. Kodiert neu mit {0} — dauert ca. {1}.",
                                 "Frame-accurate. Re-encodes with {0} — takes about {1}.")
                .Replace("{0}", EncoderName(encoder)).Replace("{1}", ApproxDuration(estimate));
        }

        // size and disk space
        long bytes = EstimateBytes(range);
        string? dir = null;
        try { dir = Path.GetDirectoryName(Path.GetFullPath(_path)); } catch { }
        long? free = dir is null ? null : FFmpegJobRunner.DefaultFreeSpace(dir);
        bool enoughSpace = free is not { } f || FFmpegJobRules.HasEnoughDiskSpace(f, bytes);
        if (!enoughSpace)
        {
            _sizeText.Text = L.T("Zu wenig Speicherplatz: mindestens {0} nötig.", "Not enough disk space: at least {0} needed.")
                .Replace("{0}", ClipCutRules.FormatBytes(FFmpegJobRules.RequiredFreeBytes(bytes)));
            _sizeText.Foreground = Res("DangerBrush");
        }
        else
        {
            var drive = dir is null ? "" : Path.GetPathRoot(dir)?.TrimEnd('\\') ?? "";
            _sizeText.Text = free is { } fr
                ? L.T("Ergebnis ≈ {0} · frei auf {1} {2}", "Result ≈ {0} · free on {1} {2}")
                    .Replace("{0}", ClipCutRules.FormatBytes(bytes)).Replace("{1}", drive).Replace("{2}", ClipCutRules.FormatBytes(fr))
                : L.T("Ergebnis ≈ {0}", "Result ≈ {0}").Replace("{0}", ClipCutRules.FormatBytes(bytes));
            _sizeText.Foreground = Res("TextBrush");
        }

        // the cut button
        var invalid = ClipCutRules.ValidationMessage(validation);
        _validationText.Text = _state == EditorState.Editing ? invalid ?? _errorText ?? "" : "";
        _validationText.Visibility = _validationText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        bool canCut = _state == EditorState.Editing && invalid is null && enoughSpace;
        _cutBtn.IsEnabled = canCut;
        _cutBtn.ToolTip = invalid;
    }

    // ================================================================ state

    private void SetState(EditorState state)
    {
        _state = state;
        bool editing = state == EditorState.Editing;
        bool running = state == EditorState.Running;
        bool done = state == EditorState.Done;

        _runPanel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        _donePanel.Visibility = done ? Visibility.Visible : Visibility.Collapsed;

        foreach (var b in new[] { _setIn, _setOut }) b.IsEnabled = editing;
        _fast.IsEnabled = _exact.IsEnabled = editing;
        _recycleBox.IsEnabled = editing && RecycleBin.IsSupportedPath(_path);
        _timeline.MarksEnabled = editing;
        bool navigate = (editing || running || (done && !_originalGone && !_previewClosed)) && state != EditorState.Unsupported;
        foreach (var b in new[] { _btnStart, _btnBack1S, _btnBackFrame, _btnPlay, _btnFwdFrame, _btnFwd1S, _btnEnd, _btnMute })
            b.IsEnabled = navigate;
        _cutBtn.Visibility = editing || running ? Visibility.Visible : Visibility.Collapsed;
        _cutBtn.IsEnabled = editing && _cutBtn.IsEnabled;
        if (state == EditorState.Unsupported)
        {
            _overlay.Visibility = Visibility.Visible;
            foreach (var b in new[] { _setIn, _setOut }) b.IsEnabled = false;
            _cutBtn.IsEnabled = false;
        }
        RefreshAll();
    }

    // ================================================================ cutting

    private async Task StartCutAsync()
    {
        if (_state != EditorState.Editing || _tl is null || _index is null) return;
        var range = _tl.Resolve(_in, _out, _mode);
        if (_tl.Validate(range, _mode) != CutValidation.Ok) return;

        bool recycleAfter = _recycleBox.IsChecked == true && RecycleBin.IsSupportedPath(_path);
        PausePlayback();
        _errorText = null;
        _progress.Value = 0;
        _progress.IsIndeterminate = true;
        _progressText.Text = L.T("Wartet auf eine andere FFmpeg-Aufgabe…", "Waiting for another FFmpeg task…");
        SetState(EditorState.Running);

        var cts = _cutCts = new CancellationTokenSource();
        var progress = new Progress<CutProgress>(OnCutProgress);   // posts back to this (UI) thread
        var request = new CutRequest(_path, _index, range, _mode, RecycleOriginal: false);

        CutResult result;
        try { result = await _svc.ClipCut.CutAsync(request, progress, cts.Token); }
        catch (Exception ex)
        {
            Logger.Error("Editor cut crashed", ex);
            result = new CutResult(CutStatus.Failed, null, 0, ex.Message);
        }
        _cutCts = null;
        cts.Dispose();

        if (_closeAfterCut || _closed) { _closeAfterCut = true; SetState(EditorState.Editing); Close(); return; }

        switch (result.Status)
        {
            case CutStatus.Cancelled:
                SetState(EditorState.Editing);
                break;
            case CutStatus.Failed:
                _errorText = result.Message;
                SetState(EditorState.Editing);
                break;
            default:
                await ShowDoneAsync(result, recycleAfter);
                break;
        }
    }

    private void OnCutProgress(CutProgress p)
    {
        if (_state != EditorState.Running) return;
        switch (p.Stage)
        {
            case CutStage.Queued:
                _progress.IsIndeterminate = true;
                _progressText.Text = L.T("Wartet auf eine andere FFmpeg-Aufgabe…", "Waiting for another FFmpeg task…");
                break;
            case CutStage.Cutting:
                _progress.IsIndeterminate = false;
                _progress.Value = p.Fraction;
                _progressText.Text = L.T("Schneide… {0} %", "Trimming… {0} %")
                    .Replace("{0}", ((int)Math.Floor(p.Fraction * 100)).ToString(CultureInfo.InvariantCulture));
                break;
            case CutStage.Verifying:
                _progress.IsIndeterminate = true;
                _progressText.Text = L.T("Prüfe Ergebnis…", "Checking result…");
                break;
            case CutStage.Recycling:
                _progressText.Text = L.T("Verschiebe Original in den Papierkorb…", "Moving the original to the Recycle Bin…");
                break;
        }
    }

    private async Task ShowDoneAsync(CutResult result, bool recycleAfter)
    {
        _lastResult = result;
        _resultPath = result.OutputPath;
        _lastRecycleText = null;
        _discordSmallStarted = _discordLargeStarted = false;

        // the original only goes after a clean check, and only with the preview closed (it holds the file open)
        if (recycleAfter && result.Warning is null)
        {
            _progressText.Text = L.T("Verschiebe Original in den Papierkorb…", "Moving the original to the Recycle Bin…");
            ClosePreview();

            // The media pipeline and a still-frame grab release the file a moment after Close() returns, so the first try
            // can find it in use: RecycleOriginalAsync tries again for ~3 s without blocking this thread.
            RecycleOutcome outcome;
            _recycling = true;
            try { outcome = await _svc.ClipCut.RecycleOriginalAsync(_path); }
            finally { _recycling = false; }
            if (_closed) return;   // closed meanwhile: the move was still done, there is nothing left to show

            _lastRecycleText = ClipCutRules.RecycleMessage(outcome);
            _lastRecycleOk = outcome == RecycleOutcome.Recycled;
            // gone is gone, in the bin or not: no more trimming of a file that does not exist
            if (outcome is RecycleOutcome.Recycled or RecycleOutcome.DeletedPermanently) _originalGone = true;
        }

        SetState(EditorState.Done);
        UpdateDiscordButtons();
        RefreshDone();
    }

    private void RefreshDone()
    {
        if (_lastResult is null || _resultPath is null) return;
        _doneText.Text = L.T("Gespeichert: {0} · {1}", "Saved: {0} · {1}")
            .Replace("{0}", Path.GetFileName(_resultPath)).Replace("{1}", ClipCutRules.FormatBytes(_lastResult.OutputBytes));

        var notes = new List<string>();
        if (_lastResult.Warning is { } warning) notes.Add(warning);
        if (_lastRecycleText is { } recycle) notes.Add(recycle);
        _doneNote.Text = string.Join("  ", notes);
        _doneNote.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _doneNote.Foreground = Res(_lastResult.Warning is not null || (_lastRecycleText is not null && !_lastRecycleOk) ? "DangerBrush" : "SuccessBrush");
        _again.Visibility = _originalGone ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ContinueTrimming()
    {
        if (_originalGone) return;
        SetState(EditorState.Editing);
        if (_previewClosed) OpenPreview();
        RefreshAll();
    }

    // ================================================================ after the cut

    private void UpdateDiscordButtons()
    {
        var (small, large) = DiscordCompressRules.Targets(_svc.DiscordSettings());
        _discordSmall.Content = _discordSmallStarted ? L.T("Läuft in der Galerie…", "Running in the gallery…") : "Discord " + DiscordCompressRules.TargetLabel(small);
        _discordLarge.Content = _discordLargeStarted ? L.T("Läuft in der Galerie…", "Running in the gallery…") : "Discord " + DiscordCompressRules.TargetLabel(large);
        _discordSmall.IsEnabled = !_discordSmallStarted;
        _discordLarge.IsEnabled = !_discordLargeStarted;
    }

    private void StartDiscord(bool small)
    {
        if (_resultPath is null) return;
        var (s, l) = DiscordCompressRules.Targets(_svc.DiscordSettings());
        if (_svc.Discord.Start(_resultPath, small ? s : l))
        {
            if (small) _discordSmallStarted = true; else _discordLargeStarted = true;
        }
        UpdateDiscordButtons();
    }

    private static void OpenFile(string? path)
    {
        if (path is null || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Error("Editor: open failed: " + path, ex); }
    }

    private static void ShowInFolder(string? path)
    {
        if (path is null) return;
        try { Process.Start("explorer.exe", "/select,\"" + path + "\""); }
        catch (Exception ex) { Logger.Error("Editor: show in folder failed: " + path, ex); }
    }

    // ================================================================ closing

    /// <summary>
    /// Stops and releases the preview so the file is free: the media element and, in the still-frame fallback, a pending
    /// or running ffmpeg grab (ffmpeg opens the file without delete sharing). Both let go a moment after this returns;
    /// <see cref="ClipCutService.RecycleOriginalAsync"/> allows for that.
    /// </summary>
    private void ClosePreview()
    {
        PausePlayback();
        _scrubTimer.Stop();
        _openTimer.Stop();
        _grabTimer.Stop();
        _grabCts?.Cancel();   // FramePreviewService kills the running ffmpeg on the token
        try { _media.Stop(); _media.Close(); _media.Source = null; } catch { }
        _previewOpened = false;
        _previewClosed = true;
        _still.Source = null;
        if (!_originalGone) _overlay.Visibility = Visibility.Collapsed;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // the cut is over while the original is being moved: nothing to cancel, so no question
        if (_state != EditorState.Running || _closeAfterCut || _recycling) return;

        e.Cancel = true;
        var answer = MessageBox.Show(this, L.T("Schneiden läuft — abbrechen und schließen?", "Trimming is running — cancel and close?"),
            Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        _closeAfterCut = true;
        _cutCts?.Cancel();   // StartCutAsync closes the window once the job has ended
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        L.LanguageChanged -= OnLanguageChanged;
        _playTimer.Stop(); _scrubTimer.Stop(); _grabTimer.Stop(); _openTimer.Stop();
        _grabCts?.Cancel();
        try { _media.Stop(); _media.Close(); _media.Source = null; } catch { }
        _grabs?.Dispose();
        OpenWindows.Remove(_key);
    }
}
