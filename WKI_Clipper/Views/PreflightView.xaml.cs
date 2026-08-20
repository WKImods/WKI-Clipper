using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
// WPF + WinForms both define CheckState; ours is the checklist state.
using CheckState = WKI_Clipper.Services.CheckState;

namespace WKI_Clipper.Views;

/// <summary>
/// Go-live checklist: a traffic light per precondition (OBS, mic, replay buffers, scene,
/// disk) plus a one-click start sequence — start scene → stream → replay buffer →
/// countdown → target scene. Going live is public and irreversible-ish, so it always
/// asks first and the countdown can be cancelled.
/// </summary>
public partial class PreflightView : UserControl
{
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _sequenceCts;
    private bool _subscribed;

    public PreflightView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _refresh.Tick += (_, _) => Refresh();
    }

    private static PreflightSettings Cfg => App.Host!.Settings.Current.Streaming.Preflight;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var host = App.Host;
        if (host is null) return;

        GoLiveBtn.Content = L.T("🔴  Live gehen", "🔴  Go live");
        CancelBtn.Content = L.T("Abbrechen", "Cancel");
        EndBtn.Content = L.T("⏹  Stream beenden", "⏹  End stream");
        ConfigExpander.Header = L.T("Ablauf einstellen", "Configure sequence");
        if (ConfigPanel.Children.Count == 0) BuildConfig(host);

        if (!_subscribed) { host.Obs.StatusChanged += OnStatus; _subscribed = true; }
        _refresh.Start();
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var host = App.Host;
        if (host != null && _subscribed) { host.Obs.StatusChanged -= OnStatus; _subscribed = false; }
        _refresh.Stop();
        // A running sequence belongs to the widget — closing it cancels the countdown.
        _sequenceCts?.Cancel();
    }

    private void OnStatus() => Dispatcher.BeginInvoke(new Action(() => { if (IsLoaded) Refresh(); }));

    // ---- checklist ----

    private void Refresh()
    {
        var host = App.Host;
        if (host is null || !IsVisible) return;

        var checks = PreflightChecks.Evaluate(BuildInput(host));
        ChecksPanel.Children.Clear();
        foreach (var c in checks) ChecksPanel.Children.Add(BuildCheckRow(c));

        bool live = host.Obs.Status.Streaming;
        bool blocked = !PreflightChecks.CanGoLive(checks);
        bool running = _sequenceCts != null;
        GoLiveBtn.IsEnabled = !blocked && !running && !live && host.Obs.IsConnected;
        GoLiveBtn.ToolTip = live
            ? L.T("Du bist bereits live.", "You are already live.")
            : blocked ? L.T("Mindestens eine Prüfung ist rot.", "At least one check is red.") : null;

        // The counterpart to Go Live — only offered while there is something to end.
        EndBtn.Visibility = live && !running ? Visibility.Visible : Visibility.Collapsed;
    }

    private static PreflightInput BuildInput(AppHost host)
    {
        var st = host.Obs.Status;
        string mic = Cfg.MicInputName;
        bool micKnown = st.InputMuted.ContainsKey(mic);

        // Two drives matter: the clipper's clips folder and OBS's recording folder.
        // They are usually different, and only one of them being full still loses footage.
        double freeGb = FreeGb(SettingsService.ExpandPath(host.Settings.Current.Output.ClipsFolder),
                               out string? clipperRoot);
        double obsGb = 0;
        bool obsSeparate = false;
        if (!string.IsNullOrWhiteSpace(st.RecordDirectory))
        {
            obsGb = FreeGb(st.RecordDirectory!, out string? obsRoot);
            obsSeparate = !string.IsNullOrEmpty(obsRoot)
                          && !string.Equals(obsRoot, clipperRoot, StringComparison.OrdinalIgnoreCase);
        }

        return new PreflightInput(
            ObsConnected: host.Obs.IsConnected,
            CurrentScene: st.CurrentScene,
            Streaming: st.Streaming,
            ReplayBufferActive: st.ReplayBufferActive,
            MicInputKnown: micKnown,
            MicMuted: micKnown && st.InputMuted[mic],
            MicInputName: mic,
            ClipperBufferRunning: host.ReplayBuffer.IsRunning,
            FreeDiskGb: freeGb,
            ObsFreeDiskGb: obsGb,
            ObsDiskSeparate: obsSeparate,
            Health: st.Health);
    }

    /// <summary>Free gigabytes on the volume holding <paramref name="dir"/>; 0 when unknown.</summary>
    private static double FreeGb(string dir, out string? root)
    {
        root = null;
        try
        {
            root = Path.GetPathRoot(dir);
            if (string.IsNullOrEmpty(root)) return 0;
            var d = new DriveInfo(root);
            return d.IsReady ? d.AvailableFreeSpace / (1024.0 * 1024 * 1024) : 0;
        }
        catch { return 0; }   // unreachable network path etc. → reported as "—"
    }

    private FrameworkElement BuildCheckRow(CheckResult c)
    {
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 10, Height = 10, Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(c.State switch
            {
                CheckState.Ok => Color.FromRgb(0x4A, 0xD8, 0x6A),
                CheckState.Warn => Color.FromRgb(0xE0, 0xA8, 0x40),
                CheckState.Fail => Color.FromRgb(0xE0, 0x4E, 0x4E),
                _ => Color.FromRgb(0x55, 0x55, 0x55)
            })
        };

        var texts = new StackPanel();
        texts.Children.Add(new TextBlock { Text = c.Title, FontWeight = FontWeights.SemiBold });
        texts.Children.Add(new TextBlock
        {
            Text = c.Detail, Style = (Style)FindResource("MutedStyle"), TextWrapping = TextWrapping.Wrap
        });

        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        row.Children.Add(dot);
        row.Children.Add(texts);

        return new Border
        {
            Background = (Brush)FindResource("PanelBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 5),
            Child = row
        };
    }

    // ---- config ----

    private void BuildConfig(AppHost host)
    {
        ConfigPanel.Children.Add(SceneRow(host, L.T("Startszene", "Start scene"),
            () => Cfg.StartScene, v => Cfg.StartScene = v));
        ConfigPanel.Children.Add(SceneRow(host, L.T("Zielszene", "Target scene"),
            () => Cfg.TargetScene, v => Cfg.TargetScene = v));
        ConfigPanel.Children.Add(SceneRow(host, L.T("Mikrofon-Eingang", "Microphone input"),
            () => Cfg.MicInputName, v => Cfg.MicInputName = v, inputs: true));

        var secBox = new TextBox { Text = Cfg.CountdownSeconds.ToString(), MinWidth = 60 };
        secBox.LostFocus += (_, _) =>
        {
            if (int.TryParse(secBox.Text.Trim(), out int s) && s is >= 0 and <= 600)
            { Cfg.CountdownSeconds = s; host.Settings.Save(); }
            secBox.Text = Cfg.CountdownSeconds.ToString();
        };
        ConfigPanel.Children.Add(Labeled(L.T("Countdown (Sekunden)", "Countdown (seconds)"), secBox));

        var buf = new CheckBox
        {
            Content = L.T("OBS-Replay-Buffer mitstarten", "Start OBS replay buffer too"),
            IsChecked = Cfg.StartReplayBuffer,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 6, 0, 0)
        };
        buf.Checked += (_, _) => { Cfg.StartReplayBuffer = true; host.Settings.Save(); };
        buf.Unchecked += (_, _) => { Cfg.StartReplayBuffer = false; host.Settings.Save(); };
        ConfigPanel.Children.Add(buf);
    }

    /// <summary>Editable combo filled from OBS (scenes or audio inputs) — typable while offline.</summary>
    private FrameworkElement SceneRow(AppHost host, string label, Func<string> get, Action<string> set, bool inputs = false)
    {
        var box = new ComboBox { IsEditable = true, MinWidth = 200, Text = get() };
        box.DropDownOpened += async (_, _) =>
        {
            var typed = box.Text;
            var items = await Task.Run(() => inputs ? host.Obs.ListAudioInputNames() : host.Obs.ListScenes());
            box.Items.Clear();
            foreach (var s in items) box.Items.Add(s);
            box.Text = typed;
        };
        box.LostFocus += (_, _) => { set(box.Text.Trim()); host.Settings.Save(); };
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is string s) { set(s); host.Settings.Save(); } };
        return Labeled(label, box);
    }

    private FrameworkElement Labeled(string label, FrameworkElement content)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        sp.Children.Add(new TextBlock
        {
            Text = label, Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 0, 0, 2)
        });
        sp.Children.Add(content);
        return sp;
    }

    // ---- the go-live sequence ----

    private async void OnGoLive(object sender, RoutedEventArgs e)
    {
        var host = App.Host;
        if (host is null || _sequenceCts != null) return;

        // Going live is public — always confirm, never on a stray click.
        var msg = L.T(
            $"Jetzt live gehen?\n\nSzene „{Cfg.StartScene}\" → Stream starten"
                + (Cfg.StartReplayBuffer ? " → Replay-Buffer an" : "")
                + $" → nach {Cfg.CountdownSeconds}s auf „{Cfg.TargetScene}\".",
            $"Go live now?\n\nScene \"{Cfg.StartScene}\" → start stream"
                + (Cfg.StartReplayBuffer ? " → replay buffer on" : "")
                + $" → switch to \"{Cfg.TargetScene}\" after {Cfg.CountdownSeconds}s.");
        if (MessageBox.Show(msg, L.T("Live gehen", "Go live"),
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        _sequenceCts = new CancellationTokenSource();
        var ct = _sequenceCts.Token;
        CancelBtn.Visibility = Visibility.Visible;
        GoLiveBtn.IsEnabled = false;
        CountdownText.Visibility = Visibility.Visible;

        try
        {
            CountdownText.Text = L.T("Szene wird gesetzt…", "Switching scene…");
            await host.Obs.ExecuteAsync(new StreamButtonConfig { Action = StreamAction.SetScene, Param = Cfg.StartScene });
            await Task.Delay(600, ct);

            CountdownText.Text = L.T("Stream startet…", "Starting stream…");
            await host.Obs.ExecuteAsync(new StreamButtonConfig { Action = StreamAction.StartStream });

            // ExecuteAsync never faults — a rejected StartStream (missing stream key, dead
            // uplink) only raises a toast. The sequence has to SEE the stream actually
            // running, or it would count down and announce "You're live" over nothing.
            if (!await WaitForStatusAsync(host, () => host.Obs.Status.Streaming,
                                          TimeSpan.FromSeconds(12), ct))
            {
                CountdownText.Text = L.T(
                    "Stream-Start fehlgeschlagen — OBS meldet nicht LIVE. Stream-Key/Verbindung prüfen.",
                    "Stream start failed — OBS does not report LIVE. Check stream key/connection.");
                return;
            }

            if (Cfg.StartReplayBuffer && !host.Obs.Status.ReplayBufferActive)
                await host.Obs.ExecuteAsync(new StreamButtonConfig { Action = StreamAction.ToggleReplayBuffer });

            for (int left = Cfg.CountdownSeconds; left > 0; left--)
            {
                CountdownText.Text = L.T($"Wechsel zu „{Cfg.TargetScene}\" in {left}s",
                                         $"Switching to \"{Cfg.TargetScene}\" in {left}s");
                await Task.Delay(1000, ct);
            }

            await host.Obs.ExecuteAsync(new StreamButtonConfig { Action = StreamAction.SetScene, Param = Cfg.TargetScene });
            CountdownText.Text = L.T("Läuft. Viel Spaß!", "You're live. Have fun!");
            ToastService.Show(ToastKind.Info, L.T("Live", "Live"),
                L.T($"Szene „{Cfg.TargetScene}\" ist aktiv.", $"Scene \"{Cfg.TargetScene}\" is active."));
        }
        catch (OperationCanceledException)
        {
            // Cancel only stops the countdown/scene switch — the stream keeps running,
            // stopping it is a deliberate separate action.
            CountdownText.Text = L.T("Ablauf abgebrochen (Stream läuft weiter).",
                                     "Sequence cancelled (stream keeps running).");
        }
        catch (Exception ex)
        {
            Logger.Warn("Go-live sequence failed: " + ex.Message);
            CountdownText.Text = L.T("Ablauf fehlgeschlagen — siehe Log.", "Sequence failed — see the log.");
        }
        finally
        {
            _sequenceCts?.Dispose();
            _sequenceCts = null;
            CancelBtn.Visibility = Visibility.Collapsed;
            Refresh();
        }
    }

    private void OnCancelSequence(object sender, RoutedEventArgs e) => _sequenceCts?.Cancel();

    /// <summary>Counterpart to Go Live. Ends a public broadcast, so it confirms first.</summary>
    private async void OnEndStream(object sender, RoutedEventArgs e)
    {
        var host = App.Host;
        if (host is null || !host.Obs.Status.Streaming) return;

        bool alsoBuffer = Cfg.StartReplayBuffer && host.Obs.Status.ReplayBufferActive;
        var msg = L.T("Stream jetzt beenden?" + (alsoBuffer ? "\n\nDer OBS-Replay-Buffer wird mit gestoppt." : ""),
                      "End the stream now?" + (alsoBuffer ? "\n\nThe OBS replay buffer will be stopped as well." : ""));
        if (MessageBox.Show(msg, L.T("Stream beenden", "End stream"),
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        await host.Obs.ExecuteAsync(new StreamButtonConfig { Action = StreamAction.StopStream });

        // Same honesty rule as Go Live: report success only after OBS confirms it.
        bool ended = await WaitForStatusAsync(host, () => !host.Obs.Status.Streaming,
                                              TimeSpan.FromSeconds(10), CancellationToken.None);
        if (ended && alsoBuffer)
            await host.Obs.ExecuteAsync(new StreamButtonConfig { Action = StreamAction.ToggleReplayBuffer });

        CountdownText.Visibility = Visibility.Visible;
        CountdownText.Text = ended
            ? L.T("Stream beendet.", "Stream ended.")
            : L.T("Stopp nicht bestätigt — bitte direkt in OBS prüfen.", "Stop not confirmed — check OBS directly.");
        Refresh();
    }

    /// <summary>
    /// Waits until the mirrored OBS state satisfies <paramref name="condition"/>, driven by
    /// StatusChanged events (no polling). Needed because ExecuteAsync deliberately never
    /// faults — request errors only surface as toasts, so sequences must watch the state.
    /// </summary>
    private static async Task<bool> WaitForStatusAsync(AppHost host, Func<bool> condition,
                                                       TimeSpan timeout, CancellationToken ct)
    {
        if (condition()) return true;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action onStatus = () => { if (condition()) tcs.TrySetResult(); };
        host.Obs.StatusChanged += onStatus;
        try
        {
            if (condition()) return true;   // may have flipped between check and subscribe
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct));
            if (done == tcs.Task) return true;
            ct.ThrowIfCancellationRequested();   // countdown cancel → the OCE handler above
            return condition();
        }
        finally { host.Obs.StatusChanged -= onStatus; }
    }
}
