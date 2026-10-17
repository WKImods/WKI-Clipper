using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WKI_Clipper.Services;

namespace WKI_Clipper.Views;

[SupportedOSPlatform("windows")]
public partial class AudioSettingsView : UserControl
{
    private DispatcherTimer? _meterTimer;
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _micDevice;
    private MMDevice? _sysDevice;
    private System.Windows.Shapes.Rectangle? _micMeter;
    private System.Windows.Shapes.Rectangle? _sysMeter;
    private TextBlock? _micMeterText;
    private TextBlock? _sysMeterText;
    private System.Windows.Shapes.Ellipse? _micStatusDot;
    private System.Windows.Shapes.Ellipse? _sysStatusDot;
    private System.Windows.Controls.ComboBox? _micBox;
    private System.Windows.Controls.ComboBox? _sysBox;
    private System.Windows.Controls.CheckBox? _micEnable;
    private System.Windows.Controls.CheckBox? _sysEnable;

    public AudioSettingsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (RowsContainer.Children.Count > 0)
        {
            StartMeter();
            return;
        }
        var host = App.Host;
        if (host is null) return;

        SubheadingText.Text = L.T(
            "Mikrofon + System-Sound werden direkt via WASAPI in der App gecapturet — kein Stereo-Mix oder VB-CABLE nötig.",
            "Microphone + system sound are captured in-app via WASAPI — no Stereo Mix or VB-CABLE needed.");

        _enumerator = new MMDeviceEnumerator();

        RowsContainer.Children.Add(BuildMicCard(host));
        RowsContainer.Children.Add(BuildSysCard(host));
        RowsContainer.Children.Add(BuildSeparateTrackCard(host));
        RowsContainer.Children.Add(BuildSyncCard(host));
        RowsContainer.Children.Add(BuildGameAudioCard(host));
        RowsContainer.Children.Add(BuildSoundCuesCard(host));

        StartMeter();
    }

    // ---- sound cues ----

    private DispatcherTimer? _soundSaveTimer;

    private sealed record SoundDeviceItem(string? Id, string Name)
    {
        public override string ToString() => Name;
    }

    private FrameworkElement BuildSoundCuesCard(AppHost host)
    {
        // Always read through the host: the settings object is the one the service reads at every cue.
        Models.SoundSettings Cfg() => host.Settings.Current.Sounds;

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = L.T("Hinweis-Töne", "Sound cues"),
            FontWeight = System.Windows.FontWeights.SemiBold,
            FontSize = 14,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 6)
        });
        stack.Children.Add(new TextBlock
        {
            Text = L.T("Kurze Töne, wenn ein Clip gespeichert, ein Screenshot gemacht oder eine Aufnahme gestartet bzw. beendet wird — und wenn etwas schiefgeht. Der Clip-Ton kommt direkt beim Tastendruck, nicht erst wenn die Datei fertig ist.",
                       "Short sounds when a clip is saved, a screenshot is taken, a recording starts or stops — and when something goes wrong. The clip sound plays right at the key press, not when the file is finished."),
            Style = (Style)FindResource("MutedStyle"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });

        var master = new System.Windows.Controls.CheckBox
        {
            Content = L.T("Hinweis-Töne aktivieren", "Enable sound cues"),
            IsChecked = Cfg().Enabled,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 8)
        };
        stack.Children.Add(master);

        // Everything below the master switch greys out with it.
        var body = new StackPanel { IsEnabled = Cfg().Enabled };
        stack.Children.Add(body);
        master.Checked += (_, _) => { Cfg().Enabled = true; body.IsEnabled = true; host.Settings.Save(); };
        master.Unchecked += (_, _) => { Cfg().Enabled = false; body.IsEnabled = false; host.Settings.Save(); };

        // Volume: applies live (the service reads it per cue), the file is written once the slider settles.
        var volume = new System.Windows.Controls.Slider
        {
            Minimum = 0, Maximum = 100,
            SmallChange = 1, LargeChange = 10,
            // A hand-edited NaN would make Slider.Value throw and take the whole page down.
            Value = double.IsFinite(Cfg().Volume) ? Math.Clamp(Cfg().Volume * 100.0, 0, 100) : SoundCuePolicy.DefaultVolume * 100.0,
            VerticalAlignment = VerticalAlignment.Center
        };
        var volumeLabel = new TextBlock
        {
            Text = $"{volume.Value:0} %",
            Foreground = (Brush)FindResource("TextBrush"),
            FontWeight = System.Windows.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 48,
            TextAlignment = TextAlignment.Right
        };
        volume.ValueChanged += (_, _) =>
        {
            volumeLabel.Text = $"{volume.Value:0} %";
            Cfg().Volume = Math.Round(volume.Value) / 100.0;
            SaveSoundSettingsDebounced();
        };
        var volumeRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(volumeLabel, Dock.Right);
        volumeRow.Children.Add(volumeLabel);
        volumeRow.Children.Add(volume);
        body.Children.Add(BuildLabeledRow(L.T("Lautstärke", "Volume"), volumeRow));

        body.Children.Add(BuildLabeledRow(L.T("Ausgabe", "Output"), BuildSoundDeviceBox(host)));

        void AddGroup(string title, Func<bool> get, Action<bool> set, params (SoundCue Cue, string? Label)[] cues)
        {
            var box = new System.Windows.Controls.CheckBox
            {
                Content = title,
                IsChecked = get(),
                Foreground = (Brush)FindResource("TextBrush"),
                Margin = new Thickness(0, 10, 0, 0)
            };
            box.Checked += (_, _) => { set(true); host.Settings.Save(); };
            box.Unchecked += (_, _) => { set(false); host.Settings.Save(); };
            body.Children.Add(box);
            foreach (var (cue, label) in cues)
                body.Children.Add(BuildCueRow(host, cue, label));
        }

        AddGroup(L.T("Clip / GIF gespeichert", "Clip / GIF saved"),
            () => Cfg().ClipSaved, v => Cfg().ClipSaved = v, (SoundCue.ClipSaved, null));
        AddGroup(L.T("Screenshot", "Screenshot"),
            () => Cfg().Screenshot, v => Cfg().Screenshot = v, (SoundCue.Screenshot, null));
        AddGroup(L.T("Aufnahme Start / Stopp", "Recording start / stop"),
            () => Cfg().Recording, v => Cfg().Recording = v,
            (SoundCue.RecordingStarted, L.T("Start", "Start")), (SoundCue.RecordingStopped, L.T("Stopp", "Stop")));
        AddGroup(L.T("Fehler (Speichern abgelehnt oder fehlgeschlagen)", "Errors (save refused or failed)"),
            () => Cfg().Errors, v => Cfg().Errors = v, (SoundCue.Error, null));

        stack.Children.Add(new TextBlock
        {
            Text = L.T("Im Modus \"Alle Sounds aufnehmen\" kann ein Ton im NÄCHSTEN Clip landen, und über Lautsprecher abgespielte Töne nimmt das Mikrofon mit auf — Kopfhörer empfohlen.",
                       "In \"capture all sounds\" mode a cue can end up in the NEXT clip, and cues played over speakers can be picked up by the microphone — headphones recommended."),
            Style = (Style)FindResource("MutedStyle"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0)
        });

        return Card(stack);
    }

    private System.Windows.Controls.ComboBox BuildSoundDeviceBox(AppHost host)
    {
        var items = new List<SoundDeviceItem> { new(null, L.T("Windows-Standard", "Windows default")) };
        try
        {
            items.AddRange(host.AudioDevices.ListRenderDevices().Select(d => new SoundDeviceItem(d.Id, d.Name)));
        }
        catch (Exception ex) { Logger.Warn("SoundCues: could not list output devices: " + ex.Message); }

        int selected = 0;
        var saved = host.Settings.Current.Sounds.DeviceId;
        if (!string.IsNullOrWhiteSpace(saved))
        {
            int found = SoundCuePolicy.PickDeviceIndex(
                items.Skip(1).Select(d => (d.Id!, d.Name)).ToList(), saved);
            if (found >= 0) selected = found + 1;
            else
            {
                // Unplugged: keep the saved choice visible (and unchanged) instead of silently showing the default.
                items.Add(new SoundDeviceItem(saved, L.T("Gespeichertes Gerät (nicht verfügbar)", "Saved device (unavailable)")));
                selected = items.Count - 1;
            }
        }

        var box = new System.Windows.Controls.ComboBox { MinWidth = 200, DisplayMemberPath = nameof(SoundDeviceItem.Name) };
        foreach (var item in items) box.Items.Add(item);
        box.SelectedIndex = selected;   // before the handler: loading must not rewrite the setting
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is not SoundDeviceItem d) return;
            host.Settings.Current.Sounds.DeviceId = d.Id;
            host.Settings.Save();
        };
        return box;
    }

    /// <summary>One cue: which sound it uses, a test button, and an own-WAV picker with a reset.</summary>
    private FrameworkElement BuildCueRow(AppHost host, SoundCue cue, string? label)
    {
        string key = cue.ToString();
        var status = new TextBlock
        {
            Style = (Style)FindResource("MutedStyle"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 2)
        };
        System.Windows.Controls.Button MakeButton(string text) => new()
        {
            Content = text,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 2, 6, 2),
            Cursor = Cursors.Hand
        };
        var test = MakeButton(L.T("▶ Test", "▶ Test"));
        var pick = MakeButton(L.T("Eigene WAV…", "Custom WAV…"));
        var reset = MakeButton(L.T("Standard", "Default"));

        void Refresh()
        {
            host.Settings.Current.Sounds.CustomFiles.TryGetValue(key, out var path);
            bool has = !string.IsNullOrWhiteSpace(path);
            string state;
            if (!has) state = L.T("Standard-Ton", "Built-in sound");
            else
            {
                var name = System.IO.Path.GetFileName(path!);
                state = File.Exists(path)
                    ? name
                    : name + L.T(" (Datei fehlt — Standard-Ton)", " (file missing — built-in sound)");
            }
            status.Text = label is null ? state : $"{label}: {state}";
            status.ToolTip = has ? path : null;
            reset.IsEnabled = has;
        }

        test.Click += (_, _) => host.Sounds.Preview(cue);
        pick.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = L.T("WAV-Dateien (*.wav)|*.wav", "WAV files (*.wav)|*.wav"),
                CheckFileExists = true
            };
            if (dlg.ShowDialog() != true) return;
            if (SoundWav.TryReadFile(dlg.FileName, out var problem) is null)
            {
                Logger.Warn($"SoundCues: refused custom file '{dlg.FileName}' ({problem})");
                MessageBox.Show(
                    L.T($"Diese Datei lässt sich nicht als Hinweis-Ton verwenden (WAV mit PCM oder Float, höchstens {SoundWav.MaxSeconds} s).",
                        $"This file cannot be used as a sound cue (WAV with PCM or float samples, at most {SoundWav.MaxSeconds} s)."),
                    "WKI Clipper", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            SetCustomFile(host, key, dlg.FileName);
            Refresh();
            host.Sounds.Preview(cue);
        };
        reset.Click += (_, _) =>
        {
            SetCustomFile(host, key, null);
            Refresh();
        };
        Refresh();

        var buttons = new WrapPanel();
        buttons.Children.Add(test);
        buttons.Children.Add(pick);
        buttons.Children.Add(reset);

        var panel = new StackPanel { Margin = new Thickness(22, 2, 0, 4) };
        panel.Children.Add(status);
        panel.Children.Add(buttons);
        return panel;
    }

    // The map is replaced, never edited: the service reads it from a pool thread while the UI writes.
    private static void SetCustomFile(AppHost host, string key, string? path)
    {
        var cfg = host.Settings.Current.Sounds;
        var map = new Dictionary<string, string>(cfg.CustomFiles);
        if (string.IsNullOrWhiteSpace(path)) map.Remove(key);
        else map[key] = path;
        cfg.CustomFiles = map;
        host.Settings.Save();
    }

    private void SaveSoundSettingsDebounced()
    {
        if (_soundSaveTimer is null)
        {
            _soundSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _soundSaveTimer.Tick += (_, _) => { _soundSaveTimer.Stop(); App.Host?.Settings.Save(); };
        }
        _soundSaveTimer.Stop();
        _soundSaveTimer.Start();
    }

    // The widget can close within the debounce window; the last slider move must not get lost.
    private void FlushSoundSettings()
    {
        if (_soundSaveTimer is not { IsEnabled: true }) return;
        _soundSaveTimer.Stop();
        App.Host?.Settings.Save();
    }

    private FrameworkElement BuildGameAudioCard(AppHost host)
    {
        var stack = new StackPanel();

        // Header
        stack.Children.Add(new TextBlock
        {
            Text = L.T("Spiel-Audio", "Game audio"),
            FontWeight = System.Windows.FontWeights.SemiBold,
            FontSize = 14,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 6)
        });

        // Radio: AllAudio vs GameOnly
        var radioAll = new System.Windows.Controls.RadioButton
        {
            Content = L.T("Alle Sounds aufnehmen", "Capture all sounds"),
            IsChecked = host.Settings.Current.Audio.SystemCaptureMode == Models.AudioCaptureMode.AllAudio,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 4)
        };
        var radioGame = new System.Windows.Controls.RadioButton
        {
            Content = L.T("Nur Spiel-Audio aufnehmen", "Capture game audio only"),
            IsChecked = host.Settings.Current.Audio.SystemCaptureMode == Models.AudioCaptureMode.GameOnly,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 8)
        };
        stack.Children.Add(radioAll);
        stack.Children.Add(radioGame);

        // Process picker panel — only visible when GameOnly
        var pickerPanel = new StackPanel
        {
            Visibility = radioGame.IsChecked == true ? Visibility.Visible : Visibility.Collapsed,
            Margin = new Thickness(20, 0, 0, 0)
        };

        // Process dropdown
        var processBox = new System.Windows.Controls.ComboBox
        {
            MinWidth = 320,
            Margin = new Thickness(0, 0, 0, 8)
        };
        RefreshProcessList(processBox, host.Settings.Current.Audio.GameProcessName);

        var refreshBtn = new System.Windows.Controls.Button
        {
            Content = L.T("Aktualisieren", "Refresh"),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(8, 0, 0, 0)
        };
        refreshBtn.Click += (_, _) =>
            RefreshProcessList(processBox, host.Settings.Current.Audio.GameProcessName);

        var processRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(refreshBtn, Dock.Right);
        processRow.Children.Add(refreshBtn);
        processRow.Children.Add(processBox);
        pickerPanel.Children.Add(BuildLabeledRow(L.T("Prozess", "Process"), processRow));

        // Status text
        var statusText = new TextBlock
        {
            Style = (Style)FindResource("MutedStyle"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };
        UpdateGameStatus(statusText, host);
        pickerPanel.Children.Add(statusText);

        stack.Children.Add(pickerPanel);

        // Explanation
        var hint = new TextBlock
        {
            Text = L.T("Im Modus \"Nur Spiel-Audio\" wird nur der Sound des ausgewaehlten Prozesses aufgenommen. Discord, Browser und andere Apps sind automatisch stumm im Clip. Gilt als Fallback, wenn im Aufnahme-Tab die Ton-Kopplung aus ist oder kein Ziel auflösbar ist.",
                       "In \"game audio only\" mode, only the selected process's sound is captured. Discord, browsers and other apps are automatically silent in the clip. Acts as the fallback when audio coupling in the Capture tab is off or no target can be resolved."),
            Style = (Style)FindResource("MutedStyle"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        stack.Children.Add(hint);

        // Event handlers
        radioAll.Checked += (_, _) =>
        {
            Logger.Info("AudioSettings: user selected AllAudio");
            host.Settings.Current.Audio.SystemCaptureMode = Models.AudioCaptureMode.AllAudio;
            host.Settings.Save();
            pickerPanel.Visibility = Visibility.Collapsed;
            host.StartGameWatcherIfNeeded();
            host.ReplayBuffer.RequestRestart();
            UpdateGameStatus(statusText, host);
        };
        radioGame.Checked += (_, _) =>
        {
            Logger.Info("AudioSettings: user selected GameOnly");
            host.Settings.Current.Audio.SystemCaptureMode = Models.AudioCaptureMode.GameOnly;
            host.Settings.Save();
            pickerPanel.Visibility = Visibility.Visible;
            host.StartGameWatcherIfNeeded();
            host.ReplayBuffer.RequestRestart();
            UpdateGameStatus(statusText, host);
        };
        processBox.SelectionChanged += (_, _) =>
        {
            if (processBox.SelectedItem is ProcessListEntry entry)
            {
                var procName = entry.IsAutoDetect ? null : entry.ProcessName;
                Logger.Info($"AudioSettings: process selected = '{procName ?? "(auto-detect)"}' (mode={host.Settings.Current.Audio.SystemCaptureMode})");
                host.Settings.Current.Audio.GameProcessName = procName;
                host.Settings.Save();
                host.StartGameWatcherIfNeeded();
                host.ReplayBuffer.RequestRestart();
                UpdateGameStatus(statusText, host);
            }
        };

        return Card(stack);
    }

    private static void RefreshProcessList(System.Windows.Controls.ComboBox box, string? currentName)
    {
        box.Items.Clear();

        // First entry: auto-detect
        var autoEntry = new ProcessListEntry(L.T("Automatisch (Vordergrundfenster)", "Automatic (foreground window)"), null, true);
        box.Items.Add(autoEntry);

        // All processes with a main window
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proc in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (proc.MainWindowHandle == IntPtr.Zero) continue;
                string name = proc.ProcessName;
                if (!seen.Add(name)) continue;

                string title = proc.MainWindowTitle;
                string display = string.IsNullOrEmpty(title) ? name : $"{title} ({name})";
                var entry = new ProcessListEntry(display, name, false);
                box.Items.Add(entry);
            }
            catch { }
            finally { proc.Dispose(); }
        }

        // Select current
        if (string.IsNullOrEmpty(currentName))
        {
            box.SelectedIndex = 0; // auto-detect
        }
        else
        {
            bool found = false;
            for (int i = 1; i < box.Items.Count; i++)
            {
                if (box.Items[i] is ProcessListEntry e
                    && string.Equals(e.ProcessName, currentName, StringComparison.OrdinalIgnoreCase))
                {
                    box.SelectedIndex = i;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                // Process not running — add a placeholder
                var placeholder = new ProcessListEntry($"{currentName} ({L.T("nicht aktiv", "not running")})", currentName, false);
                box.Items.Add(placeholder);
                box.SelectedIndex = box.Items.Count - 1;
            }
        }
    }

    private static void UpdateGameStatus(TextBlock statusText, AppHost host)
    {
        if (host.Settings.Current.Audio.SystemCaptureMode != Models.AudioCaptureMode.GameOnly)
        {
            statusText.Text = "";
            return;
        }
        var name = host.Settings.Current.Audio.GameProcessName ?? L.T("Vordergrundfenster", "foreground window");
        var pid = host.GameWatcher?.CurrentPid;
        if (pid.HasValue)
        {
            statusText.Text = L.T($"Aktiv: {name} (PID {pid}) — nur Game-Audio wird aufgenommen",
                                  $"Active: {name} (PID {pid}) — only game audio is captured");
            statusText.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0xD8, 0x6A));
        }
        else
        {
            statusText.Text = L.T($"{name} nicht gestartet — aktuell werden alle Sounds aufgenommen",
                                  $"{name} not running — currently all sounds are captured");
            statusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xA8, 0x40));
        }
    }

    private sealed record ProcessListEntry(string DisplayName, string? ProcessName, bool IsAutoDetect)
    {
        public override string ToString() => DisplayName;
    }

    private FrameworkElement BuildSeparateTrackCard(AppHost host)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = L.T("Separate Mikrofonspur", "Separate microphone track"),
            FontWeight = System.Windows.FontWeights.SemiBold, FontSize = 14,
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush")
        });
        var box = new System.Windows.Controls.CheckBox
        {
            Content = L.T("Mikrofon auf eigener Tonspur (statt gemischt)",
                          "Microphone on its own audio track (instead of mixed)"),
            IsChecked = host.Settings.Current.Audio.SeparateMicTrack,
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 8, 0, 0)
        };
        void Apply(bool on)
        {
            host.Settings.Current.Audio.SeparateMicTrack = on;
            host.Settings.Save();
            host.ReplayBuffer.RequestRestart();   // running buffer picks it up
        }
        box.Checked += (_, _) => Apply(true);
        box.Unchecked += (_, _) => Apply(false);
        stack.Children.Add(box);
        stack.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("MutedStyle"), Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Text = L.T("Spur 1 = Spiel/System, Spur 2 = Mikro — ideal zum getrennten Schneiden. Greift nur, wenn Mikro UND System aktiv sind.",
                       "Track 1 = game/system, track 2 = mic — ideal for editing them separately. Only applies when both mic AND system are on.")
        });
        return Card(stack);
    }

    private FrameworkElement BuildSyncCard(AppHost host)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = L.T("Audio-Synchronisation", "Audio synchronization"),
            FontWeight = System.Windows.FontWeights.SemiBold,
            FontSize = 14,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 6)
        });
        stack.Children.Add(new TextBlock
        {
            Text = L.T("Negativer Wert = Audio kommt früher. WASAPI-Capture hat typisch ~150 ms Lag gegenüber dem Video. Wenn Audio im Clip zu spät kommt: weiter ins Negative. Wenn zu früh: Richtung 0 oder positiv.",
                       "Negative value = audio plays earlier. WASAPI capture typically lags the video by ~150 ms. If clip audio arrives late: go more negative. If too early: towards 0 or positive."),
            Style = (Style)FindResource("MutedStyle"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });

        var slider = new System.Windows.Controls.Slider
        {
            Minimum = -500, Maximum = 500,
            TickFrequency = 50, IsSnapToTickEnabled = true,
            Value = host.Settings.Current.Audio.OffsetMilliseconds,
            Margin = new Thickness(0, 0, 0, 4)
        };
        var valueLabel = new TextBlock
        {
            Text = host.Settings.Current.Audio.OffsetMilliseconds + " ms",
            Foreground = (Brush)FindResource("TextBrush"),
            FontWeight = System.Windows.FontWeights.SemiBold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        slider.ValueChanged += (_, _) =>
        {
            int v = (int)Math.Round(slider.Value / 25.0) * 25; // snap to 25ms steps
            valueLabel.Text = v + " ms";
            host.Settings.Current.Audio.OffsetMilliseconds = v;
        };
        slider.LostMouseCapture += (_, _) =>
        {
            host.Settings.Save();
            host.ReplayBuffer.RequestRestart();
        };

        stack.Children.Add(valueLabel);
        stack.Children.Add(slider);

        var resetBtn = new System.Windows.Controls.Button
        {
            Content = L.T("Auf Standard (-150 ms) zurücksetzen", "Reset to default (-150 ms)"),
            Padding = new Thickness(10, 6, 10, 6),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(0, 12, 0, 0)
        };
        resetBtn.Click += (_, _) =>
        {
            slider.Value = -150;
            host.Settings.Current.Audio.OffsetMilliseconds = -150;
            host.Settings.Save();
            host.ReplayBuffer.RequestRestart();
        };
        stack.Children.Add(resetBtn);

        return Card(stack);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopMeter();
        FlushSoundSettings();
    }

    private void StartMeter()
    {
        if (_meterTimer != null) return;
        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _meterTimer.Tick += (_, _) => UpdateMeters();
        _meterTimer.Start();
    }

    private void StopMeter()
    {
        _meterTimer?.Stop();
        _meterTimer = null;
        _micDevice = null;
        _sysDevice = null;
        _enumerator?.Dispose();
        _enumerator = null;
    }

    private void UpdateMeters()
    {
        var host = App.Host;
        if (host is null || _enumerator is null) return;

        // Resolve devices from current selection.
        if (_micBox?.SelectedItem is AudioDeviceInfo m && (_micDevice == null || _micDevice.ID != m.Id))
            _micDevice = ResolveDevice(_enumerator, DataFlow.Capture, m);
        if (_sysBox?.SelectedItem is AudioDeviceInfo s && (_sysDevice == null || _sysDevice.ID != s.Id))
            _sysDevice = ResolveDevice(_enumerator, DataFlow.Render, s);

        TryUpdate(_micDevice, _micMeter, _micMeterText, _micEnable?.IsChecked ?? false);
        TryUpdate(_sysDevice, _sysMeter, _sysMeterText, _sysEnable?.IsChecked ?? false);
    }

    private static void TryUpdate(MMDevice? dev, System.Windows.Shapes.Rectangle? meter,
                                  TextBlock? text, bool enabled)
    {
        if (meter is null) return;
        if (dev is null || !enabled)
        {
            meter.Width = 0;
            if (text != null) text.Text = enabled ? L.T("kein Gerät", "no device") : L.T("aus", "off");
            return;
        }
        try
        {
            float peak = dev.AudioMeterInformation.MasterPeakValue; // 0..1
            // Map to visible width (parent has width ~280)
            meter.Width = Math.Max(2, peak * 280);

            float db = peak <= 0 ? -120 : (float)(20 * Math.Log10(peak));
            if (text != null) text.Text = db < -90 ? "—" : $"{db:F0} dB";
        }
        catch
        {
            meter.Width = 0;
            if (text != null) text.Text = "—";
        }
    }

    private static MMDevice? ResolveDevice(MMDeviceEnumerator e, DataFlow flow, AudioDeviceInfo info)
    {
        try
        {
            foreach (var d in e.EnumerateAudioEndPoints(flow, DeviceState.Active))
                if (d.ID == info.Id) return d;
        }
        catch { }
        return null;
    }

    private FrameworkElement BuildMicCard(AppHost host)
    {
        var stack = new StackPanel();

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
        _micStatusDot = MakeStatusDot();
        DockPanel.SetDock(_micStatusDot, Dock.Left);
        header.Children.Add(_micStatusDot);

        var title = new TextBlock
        {
            Text = L.T("Mikrofon", "Microphone"),
            FontWeight = System.Windows.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = (Brush)FindResource("TextBrush"),
            FontSize = 14
        };
        DockPanel.SetDock(title, Dock.Left);
        header.Children.Add(title);

        _micEnable = new System.Windows.Controls.CheckBox
        {
            Content = L.T("aufnehmen", "record"),
            IsChecked = host.Settings.Current.Audio.RecordMicrophone,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Foreground = (Brush)FindResource("TextBrush")
        };
        _micEnable.Checked += (_, _) =>
        {
            host.Settings.Current.Audio.RecordMicrophone = true;
            host.Settings.Save();
            UpdateStatusDot(_micStatusDot, true, _micDevice != null);
            host.ReplayBuffer.RequestRestart();
        };
        _micEnable.Unchecked += (_, _) =>
        {
            host.Settings.Current.Audio.RecordMicrophone = false;
            host.Settings.Save();
            UpdateStatusDot(_micStatusDot, false, _micDevice != null);
            host.ReplayBuffer.RequestRestart();
        };
        header.Children.Add(_micEnable);

        stack.Children.Add(header);

        _micBox = BuildDeviceComboBox(host.AudioDevices.ListMicrophones().ToList(),
            host.Settings.Current.Audio.MicDeviceId);
        _micBox.SelectionChanged += (_, _) =>
        {
            if (_micBox.SelectedItem is AudioDeviceInfo d)
            {
                host.Settings.Current.Audio.MicDeviceId = d.Id;
                host.Settings.Save();
                _micDevice = null; // force refresh
                host.ReplayBuffer.RequestRestart();
            }
        };
        stack.Children.Add(BuildLabeledRow(L.T("Gerät", "Device"), _micBox));

        var (meter, label) = BuildMeterRow();
        _micMeter = meter;
        _micMeterText = label;
        stack.Children.Add(BuildLabeledRow(L.T("Pegel", "Level"), BuildMeterPanel(meter, label, () => RunTest(host, isMic: true))));

        // Mic gain slider (0.0 .. 4.0 = -∞ dB .. +12 dB)
        var (gainSlider, gainText) = BuildGainRow(host.Settings.Current.Audio.MicVolume, v =>
        {
            host.Settings.Current.Audio.MicVolume = v;
            host.Settings.Save();
            host.ReplayBuffer.RequestRestart();
        });
        stack.Children.Add(BuildLabeledRow(L.T("Verstärkung", "Gain"), BuildGainPanel(gainSlider, gainText)));

        UpdateStatusDot(_micStatusDot, host.Settings.Current.Audio.RecordMicrophone, true);
        return Card(stack);
    }

    private FrameworkElement BuildSysCard(AppHost host)
    {
        var stack = new StackPanel();

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
        _sysStatusDot = MakeStatusDot();
        DockPanel.SetDock(_sysStatusDot, Dock.Left);
        header.Children.Add(_sysStatusDot);

        var title = new TextBlock
        {
            Text = L.T("System-Sound", "System sound"),
            FontWeight = System.Windows.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = (Brush)FindResource("TextBrush"),
            FontSize = 14
        };
        DockPanel.SetDock(title, Dock.Left);
        header.Children.Add(title);

        _sysEnable = new System.Windows.Controls.CheckBox
        {
            Content = L.T("aufnehmen", "record"),
            IsChecked = host.Settings.Current.Audio.RecordSystemSound,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Foreground = (Brush)FindResource("TextBrush")
        };
        _sysEnable.Checked += (_, _) =>
        {
            host.Settings.Current.Audio.RecordSystemSound = true;
            host.Settings.Save();
            UpdateStatusDot(_sysStatusDot, true, _sysDevice != null);
            host.ReplayBuffer.RequestRestart();
        };
        _sysEnable.Unchecked += (_, _) =>
        {
            host.Settings.Current.Audio.RecordSystemSound = false;
            host.Settings.Save();
            UpdateStatusDot(_sysStatusDot, false, _sysDevice != null);
            host.ReplayBuffer.RequestRestart();
        };
        header.Children.Add(_sysEnable);

        stack.Children.Add(header);

        _sysBox = BuildDeviceComboBox(host.AudioDevices.ListRenderDevices().ToList(),
            host.Settings.Current.Audio.SystemDeviceId);
        _sysBox.SelectionChanged += (_, _) =>
        {
            if (_sysBox.SelectedItem is AudioDeviceInfo d)
            {
                host.Settings.Current.Audio.SystemDeviceId = d.Id;
                host.Settings.Save();
                _sysDevice = null;
                host.ReplayBuffer.RequestRestart();
            }
        };
        stack.Children.Add(BuildLabeledRow(L.T("Gerät", "Device"), _sysBox));

        var (meter, label) = BuildMeterRow();
        _sysMeter = meter;
        _sysMeterText = label;
        stack.Children.Add(BuildLabeledRow(L.T("Pegel", "Level"), BuildMeterPanel(meter, label, () => RunTest(host, isMic: false))));

        var (gainSlider, gainText) = BuildGainRow(host.Settings.Current.Audio.SystemVolume, v =>
        {
            host.Settings.Current.Audio.SystemVolume = v;
            host.Settings.Save();
            host.ReplayBuffer.RequestRestart();
        });
        stack.Children.Add(BuildLabeledRow(L.T("Verstärkung", "Gain"), BuildGainPanel(gainSlider, gainText)));

        UpdateStatusDot(_sysStatusDot, host.Settings.Current.Audio.RecordSystemSound, true);
        return Card(stack);
    }

    private (System.Windows.Controls.Slider slider, TextBlock text) BuildGainRow(double initial, Action<double> onSettle)
    {
        var slider = new System.Windows.Controls.Slider
        {
            Minimum = 0.0, Maximum = 4.0,
            SmallChange = 0.1, LargeChange = 0.5,
            TickFrequency = 0.5, IsSnapToTickEnabled = false,
            Value = Math.Clamp(initial, 0.0, 4.0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var label = new TextBlock
        {
            Text = FormatGain(slider.Value),
            Foreground = (Brush)FindResource("TextBrush"),
            FontWeight = System.Windows.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 70,
            TextAlignment = TextAlignment.Right
        };
        slider.ValueChanged += (_, _) => label.Text = FormatGain(slider.Value);
        slider.LostMouseCapture += (_, _) => onSettle(slider.Value);
        slider.PreviewKeyUp += (_, _) => onSettle(slider.Value);
        return (slider, label);
    }

    private FrameworkElement BuildGainPanel(System.Windows.Controls.Slider slider, TextBlock label)
    {
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(label, Dock.Right);
        dock.Children.Add(label);
        dock.Children.Add(slider);
        return dock;
    }

    private static string FormatGain(double v)
    {
        if (v <= 0.001) return L.T("stumm", "muted");
        var db = 20.0 * Math.Log10(v);
        return $"{v:F2}×  ·  {(db >= 0 ? "+" : "")}{db:F1} dB";
    }

    private static System.Windows.Controls.ComboBox BuildDeviceComboBox(
        IReadOnlyList<AudioDeviceInfo> devices, string? selectedIdOrName)
    {
        var box = new System.Windows.Controls.ComboBox
        {
            MinWidth = 320,
            DisplayMemberPath = nameof(AudioDeviceInfo.Name)
        };
        int select = 0;
        for (int i = 0; i < devices.Count; i++)
        {
            box.Items.Add(devices[i]);
            if (devices[i].Id == selectedIdOrName || devices[i].Name == selectedIdOrName) select = i;
        }
        if (devices.Count > 0) box.SelectedIndex = select;
        return box;
    }

    private (System.Windows.Shapes.Rectangle meter, TextBlock text) BuildMeterRow()
    {
        var meter = new System.Windows.Shapes.Rectangle
        {
            Width = 0, Height = 16,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Fill = (Brush)FindResource("AccentBrush"),
            RadiusX = 2, RadiusY = 2
        };
        var text = new TextBlock
        {
            Style = (Style)FindResource("MutedStyle"),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Text = "—"
        };
        return (meter, text);
    }

    private FrameworkElement BuildMeterPanel(System.Windows.Shapes.Rectangle meter, TextBlock text, Action onTest)
    {
        var dock = new DockPanel { LastChildFill = true };

        var testBtn = new System.Windows.Controls.Button
        {
            Content = L.T("Test (3 s)", "Test (3 s)"),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(12, 0, 0, 0),
            Cursor = Cursors.Hand
        };
        testBtn.Click += (_, _) => onTest();
        DockPanel.SetDock(testBtn, Dock.Right);
        dock.Children.Add(testBtn);

        DockPanel.SetDock(text, Dock.Right);
        dock.Children.Add(text);

        var meterBg = new Border
        {
            Height = 16,
            Background = (Brush)FindResource("BgBrush"),
            CornerRadius = new CornerRadius(2),
            Child = new Grid
            {
                Children = { meter }
            }
        };
        dock.Children.Add(meterBg);
        return dock;
    }

    private FrameworkElement BuildLabeledRow(string label, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var t = new TextBlock
        {
            Text = label,
            Foreground = (Brush)FindResource("TextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(t, 0);
        grid.Children.Add(t);

        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private System.Windows.Shapes.Ellipse MakeStatusDot() => new System.Windows.Shapes.Ellipse
    {
        Width = 12, Height = 12,
        VerticalAlignment = VerticalAlignment.Center,
        Fill = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55))
    };

    private void UpdateStatusDot(System.Windows.Shapes.Ellipse? dot, bool enabled, bool deviceFound)
    {
        if (dot is null) return;
        if (!enabled) dot.Fill = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        else if (!deviceFound) dot.Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0x4E, 0x4E));
        else dot.Fill = new SolidColorBrush(Color.FromRgb(0x4A, 0xD8, 0x6A));
    }

    private Border Card(FrameworkElement content) => new Border
    {
        Background = (Brush)FindResource("PanelBrush"),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(14, 12, 14, 14),
        Margin = new Thickness(0, 4, 0, 8),
        Child = content
    };

    /// <summary>
    /// Quick capture-to-WAV-and-play-back so the user can confirm the right
    /// device is selected. 3-second recording.
    /// </summary>
    private async void RunTest(AppHost host, bool isMic)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice device;
            WasapiCapture capture;
            if (isMic)
            {
                var info = host.AudioDevices.ListMicrophones()
                    .FirstOrDefault(d => d.Id == host.Settings.Current.Audio.MicDeviceId)
                    ?? host.AudioDevices.GetDefaultMicrophone();
                if (info is null) { MessageBox.Show(L.T("Kein Mikrofon gefunden.", "No microphone found.")); return; }
                device = enumerator.GetDevice(info.Id);
                capture = new WasapiCapture(device);
            }
            else
            {
                var info = host.AudioDevices.ListRenderDevices()
                    .FirstOrDefault(d => d.Id == host.Settings.Current.Audio.SystemDeviceId)
                    ?? host.AudioDevices.GetDefaultRenderDevice();
                if (info is null) { MessageBox.Show(L.T("Kein Wiedergabegerät gefunden.", "No playback device found.")); return; }
                device = enumerator.GetDevice(info.Id);
                capture = new WasapiLoopbackCapture(device);
            }

            var tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WKI_Clipper");
            Directory.CreateDirectory(tmpDir);
            var wavPath = System.IO.Path.Combine(tmpDir, (isMic ? "mic" : "sys") + "_test.wav");

            using var writer = new WaveFileWriter(wavPath, capture.WaveFormat);
            capture.DataAvailable += (_, e) => writer.Write(e.Buffer, 0, e.BytesRecorded);

            capture.StartRecording();
            await System.Threading.Tasks.Task.Delay(3000);
            capture.StopRecording();
            capture.Dispose();
            writer.Flush();
            writer.Dispose();

            // Open the wav — default app plays it
            Process.Start(new ProcessStartInfo(wavPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error("Audio test failed", ex);
            MessageBox.Show(L.T("Test fehlgeschlagen: ", "Test failed: ") + ex.Message, "WKI Clipper",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
