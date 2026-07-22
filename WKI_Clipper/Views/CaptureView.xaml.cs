using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Screen = System.Windows.Forms.Screen;
// WinForms is also referenced (UseWindowsForms=true); Button/CheckBox/ComboBox are
// aliased globally (GlobalUsings), these are the remaining ambiguous WPF controls.
using RadioButton = System.Windows.Controls.RadioButton;
using Orientation = System.Windows.Controls.Orientation;
using UniformGrid = System.Windows.Controls.Primitives.UniformGrid;

namespace WKI_Clipper.Views;

/// <summary>
/// The primary tab: one place that shows AND controls what gets clipped — the
/// capture target (Auto / specific window / whole monitor), audio coupling, and
/// a live "what F9/Ctrl+F9 will capture" readout, plus the main action buttons.
/// </summary>
public partial class CaptureView : UserControl
{
    private static readonly Color Green = Color.FromRgb(0x4A, 0xD8, 0x6A);
    private static readonly Color Grey = Color.FromRgb(0x55, 0x55, 0x55);

    private DispatcherTimer? _timer;
    private TextBlock? _targetText;
    private TextBlock? _audioText;
    private TextBlock? _noteText;
    private System.Windows.Shapes.Ellipse? _bufferDot;
    private TextBlock? _bufferText;
    private System.Windows.Shapes.Ellipse? _recDot;
    private TextBlock? _recText;

    private RadioButton? _autoRadio, _windowRadio, _monitorRadio;
    private StackPanel? _windowPanel, _monitorPanel, _autoPanel;
    private ComboBox? _windowBox, _monitorBox;
    private TextBlock? _autoLiveText;

    public CaptureView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (RowsContainer.Children.Count == 0)
            BuildUi();
        UpdatePickerVisibility();
        Refresh();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer?.Stop();
        _timer = null;
    }

    private void BuildUi()
    {
        var host = App.Host;
        if (host is null) return;

        // ---- Live "what gets captured" head ----
        _targetText = new TextBlock
        {
            Foreground = (Brush)FindResource("TextBrush"),
            FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap
        };
        _audioText = new TextBlock { Style = (Style)FindResource("MutedStyle"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        _noteText = new TextBlock { Foreground = (Brush)FindResource("AccentBrush"), Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        _bufferDot = MakeDot();
        _bufferText = MakeRowText();
        _recDot = MakeDot();
        _recText = MakeRowText();

        var headStack = new StackPanel();
        headStack.Children.Add(_targetText);
        headStack.Children.Add(_audioText);
        headStack.Children.Add(_noteText);
        headStack.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("BorderBrush"), Margin = new Thickness(0, 12, 0, 12) });
        headStack.Children.Add(MakeIconRow(_bufferDot, _bufferText));
        headStack.Children.Add(MakeIconRow(_recDot, _recText));
        RowsContainer.Children.Add(MakeCard("Was wird aufgenommen?",
            "Gilt für F9-Clip und Strg+F9-Aufnahme.", headStack));

        // ---- Target mode ----
        var modeStack = new StackPanel();
        _autoRadio = MakeRadio("Automatik (Spiel im Vordergrund)");
        _windowRadio = MakeRadio("Bestimmtes Fenster");
        _monitorRadio = MakeRadio("Ganzer Monitor");
        var mode = host.Settings.Current.Capture.Mode;
        _autoRadio.IsChecked = mode == CaptureMode.Auto;
        _windowRadio.IsChecked = mode == CaptureMode.Window;
        _monitorRadio.IsChecked = mode == CaptureMode.Monitor;
        _autoRadio.Checked += (_, _) => SetMode(CaptureMode.Auto);
        _windowRadio.Checked += (_, _) => SetMode(CaptureMode.Window);
        _monitorRadio.Checked += (_, _) => SetMode(CaptureMode.Monitor);
        modeStack.Children.Add(_autoRadio);
        modeStack.Children.Add(_windowRadio);
        modeStack.Children.Add(_monitorRadio);

        // Auto panel: live "what would be captured right now" + explanatory note.
        _autoPanel = new StackPanel { Margin = new Thickness(24, 6, 0, 0) };
        _autoLiveText = new TextBlock
        {
            Foreground = (Brush)FindResource("TextBrush"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
            TextWrapping = TextWrapping.Wrap
        };
        _autoPanel.Children.Add(_autoLiveText);
        _autoPanel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("MutedStyle"),
            TextWrapping = TextWrapping.Wrap,
            Text = "Freecam: Das Ziel folgt automatisch dem Fenster, das du anklickst (nach ~1,5 s Verweilzeit). Klickst du auf Arma, steht hier Arma — ohne manuelles Umstellen.\n\nLaufende Aufnahmen sind davon unberührt: Eine gestartete Strg+F9-Aufnahme bleibt bei ihrem Fenster, und ein F9-Clip enthält immer nur das aktuell gepinnte Fenster (nie zwei gemischt).\n\nFür ein festes Stammspiel, das NIE wechseln soll (auch nicht beim Fokuswechsel), nutze den Fenster-Modus."
        });

        // Window panel: shared window picker.
        _windowPanel = new StackPanel { Margin = new Thickness(24, 6, 0, 0) };
        _windowBox = new ComboBox { MinWidth = 320, Margin = new Thickness(0, 0, 0, 6) };
        RefreshWindowList();
        _windowBox.SelectionChanged += (_, _) =>
        {
            if (_windowBox.SelectedItem is WindowChoice wc)
            {
                if (wc.ProcessName == host.Settings.Current.Capture.TargetProcessName) return;
                host.Settings.Current.Capture.TargetProcessName = wc.ProcessName;
                host.Settings.Save();
                host.StartGameWatcherIfNeeded();
                host.ReplayBuffer.RequestRestart();
                Refresh();
            }
        };
        var refreshBtn = new Button { Content = "Fensterliste aktualisieren", Margin = new Thickness(0, 0, 0, 0) };
        refreshBtn.Click += (_, _) => RefreshWindowList();
        _windowPanel.Children.Add(_windowBox);
        _windowPanel.Children.Add(refreshBtn);
        _windowPanel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("MutedStyle"), Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap,
            Text = "Nimmt den Monitor des gewählten Programms auf und bleibt darauf, sobald es läuft — ideal für dein Hauptspiel."
        });

        // Monitor panel: monitor dropdown.
        _monitorPanel = new StackPanel { Margin = new Thickness(24, 6, 0, 0) };
        _monitorBox = new ComboBox { MinWidth = 320 };
        RefreshMonitorList();
        _monitorBox.SelectionChanged += (_, _) =>
        {
            if (_monitorBox.SelectedItem is MonitorChoice mc)
            {
                if (mc.DeviceName == host.Settings.Current.Capture.MonitorDeviceName) return;
                host.Settings.Current.Capture.MonitorDeviceName = mc.DeviceName;
                host.Settings.Save();
                host.ReplayBuffer.RequestRestart();
                Refresh();
            }
        };
        _monitorPanel.Children.Add(_monitorBox);
        _monitorPanel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("MutedStyle"), Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap,
            Text = "Nimmt einen ganzen Bildschirm auf — für Tutorials o.ä., bei denen alles auf dem Monitor drauf sein soll."
        });

        modeStack.Children.Add(_autoPanel);
        modeStack.Children.Add(_windowPanel);
        modeStack.Children.Add(_monitorPanel);
        RowsContainer.Children.Add(MakeCard("Ziel", "Was aufgenommen wird.", modeStack));

        // ---- Audio coupling ----
        var coupleBox = new CheckBox
        {
            Content = "Ton vom selben Ziel aufnehmen (empfohlen)",
            IsChecked = host.Settings.Current.Capture.CoupleAudio,
            Foreground = (Brush)FindResource("TextBrush")
        };
        coupleBox.Checked += (_, _) => SetCouple(true);
        coupleBox.Unchecked += (_, _) => SetCouple(false);
        var audioStack = new StackPanel();
        audioStack.Children.Add(coupleBox);
        audioStack.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("MutedStyle"), Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap,
            Text = "An: es wird nur der Ton des aufgenommenen Programms mitgeschnitten (Discord & Co. bleiben draußen). Aus: es gelten die Einstellungen im Audio-Tab. Mikro wird separat im Audio-Tab geregelt."
        });
        RowsContainer.Children.Add(MakeCard("Ton", "Woher der Clip-Ton kommt.", audioStack));

        // ---- Actions ----
        var actions = new UniformGrid { Columns = 2, Rows = 2, Margin = new Thickness(0, 2, 0, 0) };
        actions.Children.Add(MakeActionButton("Clip speichern  (F9)", accent: true, hideFirst: false, async h => await h.ReplayBuffer.SaveLastAsync()));
        actions.Children.Add(MakeActionButton("Aufnahme  (Strg+F9)", accent: false, hideFirst: true, async h =>
        {
            if (!h.ManualRecording.IsRecording) await h.ManualRecording.ToggleAsync();
            else await h.ManualRecording.ToggleAsync();
        }));
        actions.Children.Add(MakeActionButton("Screenshot  (F10)", accent: false, hideFirst: true, async h => await h.Screenshots.CaptureActiveWindowAsync()));
        actions.Children.Add(MakeActionButton("Buffer ein/aus  (Strg+F10)", accent: false, hideFirst: false, async h => await h.ReplayBuffer.ToggleAsync()));
        RowsContainer.Children.Add(MakeCard("Aktionen", "Per Klick oder Hotkey.", actions));
    }

    private void SetMode(CaptureMode mode)
    {
        var host = App.Host; if (host is null) return;
        if (host.Settings.Current.Capture.Mode == mode) return;
        host.Settings.Current.Capture.Mode = mode;
        host.Settings.Save();
        UpdatePickerVisibility();
        host.StartGameWatcherIfNeeded();
        host.ReplayBuffer.RequestRestart();
        Refresh();
    }

    private void SetCouple(bool on)
    {
        var host = App.Host; if (host is null) return;
        if (host.Settings.Current.Capture.CoupleAudio == on) return;
        host.Settings.Current.Capture.CoupleAudio = on;
        host.Settings.Save();
        host.ReplayBuffer.RequestRestart();
        Refresh();
    }

    private void UpdatePickerVisibility()
    {
        var host = App.Host; if (host is null) return;
        var mode = host.Settings.Current.Capture.Mode;
        if (_autoPanel != null) _autoPanel.Visibility = mode == CaptureMode.Auto ? Visibility.Visible : Visibility.Collapsed;
        if (_windowPanel != null) _windowPanel.Visibility = mode == CaptureMode.Window ? Visibility.Visible : Visibility.Collapsed;
        if (_monitorPanel != null) _monitorPanel.Visibility = mode == CaptureMode.Monitor ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshWindowList()
    {
        var host = App.Host; if (host is null || _windowBox is null) return;
        var saved = host.Settings.Current.Capture.TargetProcessName;
        _windowBox.Items.Clear();
        WindowChoice? toSelect = null;
        foreach (var w in CaptureTargetResolver.ListWindowedProcesses())
        {
            var choice = new WindowChoice(w.ProcessName, $"{w.Title}  ({w.ProcessName})");
            _windowBox.Items.Add(choice);
            if (w.ProcessName == saved) toSelect = choice;
        }
        if (toSelect == null && !string.IsNullOrEmpty(saved))
        {
            var placeholder = new WindowChoice(saved, $"{saved}  (nicht aktiv)");
            _windowBox.Items.Add(placeholder);
            toSelect = placeholder;
        }
        _windowBox.DisplayMemberPath = nameof(WindowChoice.Display);
        if (toSelect != null) _windowBox.SelectedItem = toSelect;
        else if (_windowBox.Items.Count > 0) _windowBox.SelectedIndex = 0;
    }

    private void RefreshMonitorList()
    {
        var host = App.Host; if (host is null || _monitorBox is null) return;
        var saved = host.Settings.Current.Capture.MonitorDeviceName;
        _monitorBox.Items.Clear();
        var screens = Screen.AllScreens;
        MonitorChoice? toSelect = null;
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            string label = $"Monitor {i + 1}  ({s.Bounds.Width}×{s.Bounds.Height}){(s.Primary ? "  · primär" : "")}";
            var choice = new MonitorChoice(s.DeviceName, label);
            _monitorBox.Items.Add(choice);
            if (s.DeviceName == saved) toSelect = choice;
        }
        _monitorBox.DisplayMemberPath = nameof(MonitorChoice.Display);
        if (toSelect != null) _monitorBox.SelectedItem = toSelect;
        else if (_monitorBox.Items.Count > 0) _monitorBox.SelectedIndex = 0;
    }

    private void Refresh()
    {
        // Skip the (process-enumerating) work while the overlay is hidden.
        if (!IsVisible) return;
        var host = App.Host;
        if (host is null || _targetText is null) return;

        // What Ctrl+F9 would capture right now (fresh resolve of the current profile).
        var preview = CaptureTargetResolver.Resolve(host.Settings.Current.Capture, host.Settings.Current);
        // What F9 captures (the pinned buffer plan, if running).
        var bufferPlan = host.ReplayBuffer.CurrentPlan;

        _targetText.Text = (bufferPlan?.VideoLabel) ?? preview.VideoLabel;
        _audioText!.Text = "Ton: " + ((bufferPlan?.AudioLabel) ?? preview.AudioLabel);

        // Live line in the Automatik panel: what a capture started RIGHT NOW
        // would grab (the resolver already excludes this overlay itself, so this
        // shows the window/monitor behind it).
        if (_autoLiveText != null)
        {
            string mon = $"Monitor {preview.MonitorIndex + 1} ({preview.MonitorWidth}×{preview.MonitorHeight})";
            _autoLiveText.Text = preview.TargetProcess != null
                ? $"Jetzt im Vordergrund: {preview.TargetProcess} → {mon}"
                : $"Jetzt im Vordergrund: {mon}";
        }

        // Auto mode: Strg+F9 is a freecam screen recording (follows windows on
        // the monitor) while F9 stays pinned — show that, it's the key
        // behavioural difference. Other modes: warn only if the fresh resolve
        // diverges from the pinned buffer plan.
        if (host.Settings.Current.Capture.Mode == CaptureMode.Auto)
        {
            var rec = CaptureTargetResolver.ResolveForManualRecording(host.Settings.Current.Capture, host.Settings.Current);
            _noteText!.Text = $"Strg+F9 (Aufnahme): {rec.VideoLabel} · {rec.AudioLabel}";
            _noteText.Visibility = Visibility.Visible;
        }
        else if (bufferPlan is { } bp && bp.VideoLabel != preview.VideoLabel)
        {
            _noteText!.Text = $"Hinweis: Strg+F9 würde gerade stattdessen aufnehmen: {preview.VideoLabel}. F9 bleibt beim oben gepinnten Ziel.";
            _noteText.Visibility = Visibility.Visible;
        }
        else
        {
            _noteText!.Visibility = Visibility.Collapsed;
        }

        if (host.ReplayBuffer.IsRunning)
        {
            _bufferDot!.Fill = new SolidColorBrush(Green);
            int avail = host.ReplayBuffer.AvailableSeconds();
            _bufferText!.Text = $"Buffer aktiv · {avail} s bereit für F9";
        }
        else
        {
            _bufferDot!.Fill = new SolidColorBrush(Grey);
            _bufferText!.Text = "Buffer aus · Strg+F10 startet ihn";
        }

        if (host.ManualRecording.IsRecording)
        {
            _recDot!.Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0x4E, 0x4E));
            var dur = host.ManualRecording.StartedAt is { } s ? (DateTime.Now - s).ToString(@"mm\:ss") : "00:00";
            _recText!.Text = $"Aufnahme läuft · {dur}";
            _recDot.Visibility = Visibility.Visible;
            _recText.Visibility = Visibility.Visible;
        }
        else
        {
            _recDot!.Visibility = Visibility.Collapsed;
            _recText!.Visibility = Visibility.Collapsed;
        }
    }

    // ---- UI helpers ----

    private Button MakeActionButton(string text, bool accent, bool hideFirst, Func<AppHost, System.Threading.Tasks.Task> action)
    {
        var b = new Button { Content = text, Margin = new Thickness(4) };
        if (accent) b.Style = (Style)FindResource("AccentButton");
        b.Click += async (_, _) =>
        {
            var host = App.Host; if (host is null) return;
            if (hideFirst)
            {
                // So the capture reflects the game, not this overlay.
                Window.GetWindow(this)?.Hide();
                await System.Threading.Tasks.Task.Delay(180);
            }
            try { await action(host); } catch (Exception ex) { Logger.Error("Capture action failed", ex); }
        };
        return b;
    }

    private RadioButton MakeRadio(string text) => new()
    {
        Content = text,
        GroupName = "CaptureMode",
        Foreground = (Brush)FindResource("TextBrush"),
        Margin = new Thickness(0, 0, 0, 4)
    };

    private FrameworkElement MakeCard(string title, string description, FrameworkElement content)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14, Foreground = (Brush)FindResource("TextBrush") });
        stack.Children.Add(new TextBlock { Text = description, Style = (Style)FindResource("MutedStyle"), Margin = new Thickness(0, 2, 0, 10) });
        stack.Children.Add(content);
        return new Border
        {
            Background = (Brush)FindResource("PanelBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 12, 16, 14),
            Margin = new Thickness(0, 0, 0, 10),
            Child = stack
        };
    }

    private FrameworkElement MakeIconRow(System.Windows.Shapes.Ellipse dot, TextBlock text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        sp.Children.Add(dot);
        sp.Children.Add(text);
        return sp;
    }

    private System.Windows.Shapes.Ellipse MakeDot() => new()
    {
        Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 10, 0), Fill = new SolidColorBrush(Grey)
    };

    private TextBlock MakeRowText() => new()
    {
        Foreground = (Brush)FindResource("TextBrush"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap
    };

    private sealed record WindowChoice(string ProcessName, string Display);
    private sealed record MonitorChoice(string DeviceName, string Display);
}
