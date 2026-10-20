using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Media;
using WKI_Clipper.Models;
using WKI_Clipper.Services;

namespace WKI_Clipper.Views;

public partial class VideoSettingsView : UserControl
{
    private TextBlock? _resolutionHint;
    private TextBlock? _bitrateHint;
    private System.Windows.Controls.ComboBox? _codecBox;
    private System.Windows.Controls.ComboBox? _resolutionBox;
    private System.Windows.Controls.ComboBox? _qualityBox;
    private TextBox? _customBitrateBox;
    private TextBlock? _bufferHint;
    private TextBlock? _bufferDiskWarn;
    private TextBlock? _bufferLongNote;
    // Re-evaluates the buffer-length hint when the bitrate changes (the byte estimate follows it).
    private Action? _refreshBufferHint;
    // True while RefreshCodecBox rebuilds the list programmatically, so the
    // SelectionChanged handler ignores the induced selection changes (which
    // otherwise fire Save + buffer restart on every codec-detection tick).
    private bool _suppressCodecChange;

    // "Eigene Länge pro Spiel": the muted line that says a profile sizes the buffer, the list's add button state, and the
    // host whose GameChanged keeps that button current while the settings are open.
    private TextBlock? _ringNote;
    private Action? _refreshAddCurrent;
    private AppHost? _watchedHost;

    public VideoSettingsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => UnwatchGame();
    }

    private void WatchGame(AppHost host)
    {
        if (_watchedHost is not null) return;
        _watchedHost = host;
        host.GameChanged += OnGameChanged;
    }

    private void UnwatchGame()
    {
        if (_watchedHost is null) return;
        _watchedHost.GameChanged -= OnGameChanged;
        _watchedHost = null;
    }

    // Raised on a pool thread. The detector keeps the last game while the clipper's own window is in front, so
    // "current game" stays available while these settings are open.
    private void OnGameChanged(GameInfo? _)
        => Dispatcher.BeginInvoke(new Action(() => { if (IsLoaded) _refreshAddCurrent?.Invoke(); }));

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var host0 = App.Host;
        if (host0 is not null)
        {
            WatchGame(host0);
            _refreshAddCurrent?.Invoke();
        }
        if (VideoRows.Children.Count > 0) return;
        var host = App.Host;
        if (host is null) return;

        BufferHeading.Text = L.T("Replay-Buffer", "Replay buffer");

        // Capture target (which monitor/window + audio) now lives in the Aufnahme
        // tab; this tab only covers how it is encoded.
        VideoRows.Children.Add(new TextBlock
        {
            Text = L.T("Was aufgenommen wird (Monitor/Fenster) legst du im Tab „Aufnahme“ fest. Hier geht es nur um die Encoding-Qualität.",
                       "What gets captured (monitor/window) is set in the Capture tab. This tab only covers encoding quality."),
            Style = (Style)FindResource("MutedStyle"),
            Margin = new Thickness(0, 0, 0, 14),
            TextWrapping = TextWrapping.Wrap
        });

        // Resolution
        _resolutionBox = new System.Windows.Controls.ComboBox { MinWidth = 220 };
        foreach (var preset in new[] { ResolutionPreset.Native, ResolutionPreset.FullHD, ResolutionPreset.WQHD, ResolutionPreset.UHD })
            _resolutionBox.Items.Add(LabelForResolution(preset));
        _resolutionBox.SelectedIndex = IndexFromResolution(host.Settings.Current.Video.Resolution);
        _resolutionBox.SelectionChanged += (_, _) =>
        {
            var newRes = ResolutionFromIndex(_resolutionBox.SelectedIndex);
            if (newRes == host.Settings.Current.Video.Resolution) return;
            host.Settings.Current.Video.Resolution = newRes;
            host.Settings.Save();
            UpdateBitrateHint(host);
            host.ReplayBuffer.RequestRestart();
        };
        _resolutionHint = new TextBlock { Style = (Style)FindResource("MutedStyle"), Margin = new Thickness(0, 4, 0, 0) };
        UpdateResolutionHint();
        VideoRows.Children.Add(LabeledRow(L.T("Auflösung", "Resolution"), _resolutionBox, _resolutionHint));

        // Framerate
        var fpsBox = new System.Windows.Controls.ComboBox { MinWidth = 220 };
        foreach (var fps in new[] { 30, 60, 120 }) fpsBox.Items.Add($"{fps} fps");
        fpsBox.SelectedIndex = host.Settings.Current.Video.Framerate switch
        {
            30 => 0,
            60 => 1,
            120 => 2,
            _ => 1
        };
        fpsBox.SelectionChanged += (_, _) =>
        {
            var newFps = fpsBox.SelectedIndex switch { 0 => 30, 2 => 120, _ => 60 };
            if (newFps == host.Settings.Current.Video.Framerate) return;
            host.Settings.Current.Video.Framerate = newFps;
            host.Settings.Save();
            host.ReplayBuffer.RequestRestart();
        };
        VideoRows.Children.Add(LabeledRow(L.T("Framerate", "Frame rate"), fpsBox));

        // Codec
        _codecBox = new System.Windows.Controls.ComboBox { MinWidth = 280 };
        RefreshCodecBox(host);
        _codecBox.SelectionChanged += (_, _) =>
        {
            if (_suppressCodecChange) return;
            if (_codecBox.SelectedItem is CodecInfo c)
            {
                // Only persist + restart on a real change, not a programmatic
                // re-selection of the same codec during list refresh.
                if (c.FFmpegName == host.Settings.Current.Video.Codec) return;
                host.Settings.Current.Video.Codec = c.FFmpegName;
                host.Settings.Save();
                host.ReplayBuffer.RequestRestart();
                _refreshBufferHint?.Invoke();   // AV1 cannot be buffered in RAM
            }
        };
        VideoRows.Children.Add(LabeledRow("Codec", _codecBox,
            new TextBlock
            {
                Text = L.T("Wenn keine AMD-GPU-Encoder gelistet sind, läuft Codec-Detection noch — kurz warten und Settings-Tab wechseln.",
                           "If no AMD GPU encoders are listed, codec detection is still running — wait a moment and switch tabs."),
                Style = (Style)FindResource("MutedStyle"),
                Margin = new Thickness(0, 4, 0, 0)
            }));

        // Quality preset
        _qualityBox = new System.Windows.Controls.ComboBox { MinWidth = 220 };
        _qualityBox.Items.Add(L.T("Niedrig", "Low"));
        _qualityBox.Items.Add(L.T("Mittel  (empfohlen)", "Medium  (recommended)"));
        _qualityBox.Items.Add(L.T("Hoch", "High"));
        _qualityBox.Items.Add(L.T("Sehr hoch", "Very high"));
        _qualityBox.Items.Add(L.T("Manuell (Bitrate-Eingabe)", "Manual (bitrate input)"));
        _qualityBox.SelectedIndex = (int)host.Settings.Current.Video.Quality;
        _qualityBox.SelectionChanged += (_, _) =>
        {
            var newQuality = (QualityPreset)_qualityBox.SelectedIndex;
            if (newQuality == host.Settings.Current.Video.Quality) return;
            host.Settings.Current.Video.Quality = newQuality;
            host.Settings.Save();
            UpdateBitrateHint(host);
            if (_customBitrateBox != null)
                _customBitrateBox.IsEnabled = host.Settings.Current.Video.Quality == QualityPreset.Custom;
            host.ReplayBuffer.RequestRestart();
        };
        _bitrateHint = new TextBlock { Style = (Style)FindResource("MutedStyle"), Margin = new Thickness(0, 4, 0, 0) };
        VideoRows.Children.Add(LabeledRow(L.T("Qualität", "Quality"), _qualityBox, _bitrateHint));

        // Custom bitrate (only enabled when Quality == Custom)
        _customBitrateBox = new TextBox
        {
            Text = host.Settings.Current.Video.Bitrate.ToString(),
            MinWidth = 220,
            IsEnabled = host.Settings.Current.Video.Quality == QualityPreset.Custom
        };
        _customBitrateBox.LostFocus += (_, _) =>
        {
            if (int.TryParse(_customBitrateBox.Text.Replace("_", "").Replace(" ", ""), out var bps))
            {
                host.Settings.Current.Video.Bitrate = bps;
                host.Settings.Save();
                host.ReplayBuffer.RequestRestart();
            }
            UpdateBitrateHint(host);
        };
        VideoRows.Children.Add(LabeledRow(L.T("Bitrate (bps)", "Bitrate (bps)"), _customBitrateBox));

        UpdateBitrateHint(host);

        // Periodically refresh codec list while detection runs in background.
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(800)
        };
        int attempts = 0;
        timer.Tick += (_, _) =>
        {
            attempts++;
            RefreshCodecBox(host);
            if (host.AvailableCodecs.Count > 1 || attempts > 10) timer.Stop();
        };
        timer.Start();

        // Buffer rows
        var bufferEnabled = new System.Windows.Controls.CheckBox
        {
            Content = L.T("Buffer aktiv (auto-Start beim App-Start)", "Buffer active (auto-start with the app)"),
            IsChecked = host.Settings.Current.ReplayBuffer.Enabled,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 8)
        };
        bufferEnabled.Checked += (_, _) =>
        {
            host.Settings.Current.ReplayBuffer.Enabled = true;
            host.Settings.Save();
            if (!host.ReplayBuffer.IsRunning) host.ReplayBuffer.Start();
        };
        bufferEnabled.Unchecked += (_, _) =>
        {
            host.Settings.Current.ReplayBuffer.Enabled = false;
            host.Settings.Save();
            _ = host.ReplayBuffer.StopAsync();
        };
        BufferRows.Children.Add(bufferEnabled);

        var bufferValues = DurationText.Choices;
        var bufferDur = new System.Windows.Controls.ComboBox { MinWidth = 220 };
        foreach (var s in bufferValues) bufferDur.Items.Add(DurationText.Format(s));
        bufferDur.SelectedIndex = InitialBufferIndex(bufferValues, host.Settings.Current.ReplayBuffer.DurationSeconds);

        // What the chosen length costs on the buffer drive; a 30-minute lookback is 6-7 GB (up to ~25 GB at "very high").
        _bufferHint = new TextBlock { Style = (Style)FindResource("MutedStyle"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        _bufferDiskWarn = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xA8, 0x40)),
            Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed
        };
        _bufferLongNote = new TextBlock
        {
            Style = (Style)FindResource("MutedStyle"), Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Text = L.T("Lange Rückblicke: Für lückenlose Beweise das Aufnahme-Ziel „Ganzer Monitor“ oder „Bestimmtes Fenster“ wählen. In „Automatik“ fehlt die Zeit in anderen Fenstern (Alt-Tab) — sie wird im Clip als Kapitel und Notiz markiert. Strg+F9-Aufnahmen pausieren den Buffer. Fenstergröße/Vollbild, Codec oder FPS ändern beendet den bisherigen Rückblick. Ein 30-min-Clip braucht je nach Platte 20–90 s zum Speichern; ",
                       "Long lookbacks: for gap-free evidence choose the capture target “Whole monitor” or “Specific window”. In “Automatic” the time spent in other windows (Alt-Tab) is missing — it is marked in the clip as a chapter and a note. Ctrl+F9 recordings pause the buffer. Changing the window size/fullscreen, codec or FPS ends the previous lookback. A 30 min clip takes 20–90 s to save depending on the disk; ")
                   + SaveKeyHints.BlockedMeanwhile(host.Settings.Current)   // the user's own keys, not the defaults
        };
        // "Automatisch" names what Auto really keeps in RAM on this machine with these settings (not a fixed "5 min").
        System.Windows.Controls.ComboBoxItem? autoItem = null;
        string AutoLabel()
        {
            int max = 0;
            try { max = host.ReplayBuffer.LongestMemoryLookback(); }
            catch (Exception ex) { Logger.Warn("RAM lookback estimate failed: " + ex.Message); }
            return max > 0
                ? L.T($"Automatisch (RAM bis {DurationText.Format(max)}, länger auf Festplatte)", $"Automatic (RAM up to {DurationText.Format(max)}, longer on disk)")
                : L.T("Automatisch (hier: Festplatte — zu wenig RAM für den Rückblick)", "Automatic (here: disk — not enough RAM for the lookback)");
        }

        void RefreshBufferHint()
        {
            if (autoItem is not null) autoItem.Content = AutoLabel();
            if (_bufferHint is null || _bufferDiskWarn is null || _bufferLongNote is null) return;
            int idx = bufferDur.SelectedIndex;
            if (idx < 0 || idx >= bufferValues.Length) return;
            int global = bufferValues[idx];
            // The ring (what the estimate, the storage decision and the disk check are about) is the LONGEST of the chosen
            // standard length and every game's own length - not just the picker's value.
            int chosen = ProfileLengths.RingSeconds(host.Settings.Current, global);
            DiskForecast f;
            try { f = host.ReplayBuffer.Forecast(chosen); }
            catch (Exception ex)
            {
                Logger.Warn("Buffer forecast failed: " + ex.Message);
                return;
            }

            StorageDecision storage;
            try { storage = host.ReplayBuffer.PlanStorage(chosen); }
            catch (Exception ex)
            {
                Logger.Warn("Buffer storage plan failed: " + ex.Message);
                return;
            }
            _bufferLongNote.Visibility = chosen >= DurationText.LongClipSeconds ? Visibility.Visible : Visibility.Collapsed;
            UpdateRingNote(host, global, storage, f);

            if (storage.Kind == BufferStorageKind.Memory)
            {
                _bufferHint.Text = L.T(
                    $"Bestimmt, wie viel F9 speichert. Zwischenspeicher: Arbeitsspeicher, ca. {DiskPlan.FormatBytes(storage.EstimatedBytes)} (Grenze {DiskPlan.FormatBytes(storage.BudgetBytes)}) — keine Dateien auf der Platte, kein SSD-Verschleiß.",
                    $"Determines how much F9 saves. Buffer storage: RAM, about {DiskPlan.FormatBytes(storage.EstimatedBytes)} (limit {DiskPlan.FormatBytes(storage.BudgetBytes)}) — no files on the disk, no SSD wear.");
                if (storage.Shortened)
                {
                    _bufferDiskWarn.Text = L.T($"Zu wenig Arbeitsspeicher — der Buffer läuft nur mit {DurationText.Format(storage.LookbackSeconds)}. Für längere Rückblicke „Automatisch“ oder „Festplatte“ wählen.",
                                               $"Not enough RAM — the buffer only runs with {DurationText.Format(storage.LookbackSeconds)}. For longer lookbacks choose “Automatic” or “Disk”.");
                    _bufferDiskWarn.Visibility = Visibility.Visible;
                }
                else _bufferDiskWarn.Visibility = Visibility.Collapsed;
                return;
            }

            long ramBudget = (long)BufferStoragePlanner.EffectiveMaxMemoryMB(host.Settings.Current.ReplayBuffer.MaxMemoryMB,
                PhysicalMemory.Read()?.TotalBytes ?? 0) << 20;
            string why = storage.Reason switch
            {
                StorageReason.OverBudget => L.T($" Zu lang für den RAM-Zwischenspeicher (braucht ca. {DiskPlan.FormatBytes(storage.EstimatedBytes)}, max. {DiskPlan.FormatBytes(ramBudget)}).",
                                                $" Too long for the RAM buffer (needs about {DiskPlan.FormatBytes(storage.EstimatedBytes)}, max. {DiskPlan.FormatBytes(ramBudget)})."),
                StorageReason.LowMemory => L.T(" Zu wenig Arbeitsspeicher für den RAM-Zwischenspeicher neben einem Spiel.", " Not enough RAM for the RAM buffer next to a game."),
                StorageReason.RecentLowMemory => L.T(" Der RAM war zuletzt knapp — „Arbeitsspeicher“ wählen, um es trotzdem zu versuchen.", " RAM ran low recently — choose “Memory” to try anyway."),
                StorageReason.CodecUnsupported => L.T(" AV1 lässt sich nicht im Arbeitsspeicher puffern.", " AV1 cannot be buffered in RAM."),
                _ => ""
            };
            _bufferHint.Text = f.Known
                ? L.T($"Bestimmt, wie viel F9 speichert. Zwischenspeicher: Festplatte (versteckter Buffer-Ordner), ca. {DiskPlan.FormatBytes(f.RequiredBytes)} fest belegt, wird laufend überschrieben ({f.Drive} frei: {DiskPlan.FormatBytes(f.FreeBytes)}).{why}",
                      $"Determines how much F9 saves. Buffer storage: disk (hidden buffer folder), about {DiskPlan.FormatBytes(f.RequiredBytes)} reserved, overwritten continuously ({f.Drive} free: {DiskPlan.FormatBytes(f.FreeBytes)}).{why}")
                : L.T($"Bestimmt, wie viel F9 speichert. Zwischenspeicher: Festplatte (versteckter Buffer-Ordner), ca. {DiskPlan.FormatBytes(f.RequiredBytes)} fest belegt, wird laufend überschrieben.{why}",
                      $"Determines how much F9 saves. Buffer storage: disk (hidden buffer folder), about {DiskPlan.FormatBytes(f.RequiredBytes)} reserved, overwritten continuously.{why}");

            var d = f.Decision;
            if (f.Known && d.CannotStart)
            {
                _bufferDiskWarn.Text = L.T($"Zu wenig Platz auf {f.Drive} — der Buffer kann so nicht starten. Buffer-Ordner unter Einstellungen → Pfade auf ein größeres Laufwerk legen.",
                                           $"Not enough space on {f.Drive} — the buffer cannot start like this. Move the buffer folder to a bigger drive under Settings → Paths.");
                _bufferDiskWarn.Visibility = Visibility.Visible;
            }
            else if (f.Known && d.Shortened)
            {
                _bufferDiskWarn.Text = L.T($"Zu wenig Platz — der Buffer läuft nur mit {DurationText.Format(d.EffectiveSeconds)}. Buffer-Ordner unter Einstellungen → Pfade auf ein größeres Laufwerk legen.",
                                           $"Not enough space — the buffer only runs with {DurationText.Format(d.EffectiveSeconds)}. Move the buffer folder to a bigger drive under Settings → Paths.");
                _bufferDiskWarn.Visibility = Visibility.Visible;
            }
            else _bufferDiskWarn.Visibility = Visibility.Collapsed;

            _bufferLongNote.Visibility = chosen >= DurationText.LongClipSeconds ? Visibility.Visible : Visibility.Collapsed;
        }
        _refreshBufferHint = RefreshBufferHint;

        bufferDur.SelectionChanged += (_, _) =>
        {
            if (bufferDur.SelectedIndex >= 0 && bufferDur.SelectedIndex < bufferValues.Length)
            {
                int chosen = bufferValues[bufferDur.SelectedIndex];
                // The initial selection fires this too: nothing to save or restart then.
                if (chosen != host.Settings.Current.ReplayBuffer.DurationSeconds)
                {
                    int ringBefore = EffectiveSettings.LongestClipSeconds(host.Settings.Current);
                    host.Settings.Current.ReplayBuffer.DurationSeconds = chosen;
                    host.Settings.Save();
                    // The ring holds the longest of the standard and every game's own length: a standard length below a
                    // longer profile length changes what F9 saves by default, not the ring - no restart then.
                    if (EffectiveSettings.LongestClipSeconds(host.Settings.Current) != ringBefore)
                        host.ReplayBuffer.RequestRestart();
                }
                RefreshBufferHint();
            }
        };

        _ringNote = new TextBlock
        {
            Style = (Style)FindResource("MutedStyle"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        var hintStack = new StackPanel();
        hintStack.Children.Add(_bufferHint);
        hintStack.Children.Add(_ringNote);
        hintStack.Children.Add(_bufferDiskWarn);
        hintStack.Children.Add(_bufferLongNote);
        BufferRows.Children.Add(LabeledRow(L.T("Buffer-Länge", "Buffer length"), bufferDur, hintStack));

        // F9 length per game: own rows below the standard length. The ring always holds the longest of them.
        BufferRows.Children.Add(BuildProfileLengthSection(host, bufferValues, RefreshBufferHint));

        // Where the ring lives. Auto keeps short lookbacks in RAM (no files, no SSD wear) and puts long ones on the disk.
        var storageBox = new System.Windows.Controls.ComboBox { MinWidth = 220 };
        autoItem = new System.Windows.Controls.ComboBoxItem { Content = AutoLabel() };
        storageBox.Items.Add(autoItem);
        storageBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = L.T("Arbeitsspeicher (RAM)", "Memory (RAM)") });
        storageBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = L.T("Festplatte (versteckter Buffer-Ordner)", "Disk (hidden buffer folder)") });
        storageBox.SelectedIndex = host.Settings.Current.ReplayBuffer.StorageMode switch
        {
            BufferStorageMode.Memory => 1,
            BufferStorageMode.Disk => 2,
            _ => 0
        };
        storageBox.SelectionChanged += (_, _) =>
        {
            var mode = storageBox.SelectedIndex switch { 1 => BufferStorageMode.Memory, 2 => BufferStorageMode.Disk, _ => BufferStorageMode.Auto };
            if (mode == host.Settings.Current.ReplayBuffer.StorageMode) return;
            host.Settings.Current.ReplayBuffer.StorageMode = mode;
            host.Settings.Save();
            host.ReplayBuffer.ClearLowMemoryHistory();   // an explicit choice overrides "RAM was short last time"
            host.ReplayBuffer.RequestRestart();
            RefreshBufferHint();
        };
        BufferRows.Children.Add(LabeledRow(L.T("Zwischenspeicher", "Buffer storage"), storageBox, new TextBlock
        {
            Text = L.T("RAM: der Rückblick liegt nur im Arbeitsspeicher — keine Dateien, nichts im Explorer. Festplatte: für lange Rückblicke (bis 30 min); die Dateien liegen versteckt im Buffer-Ordner und werden laufend überschrieben. Wechseln startet den Rückblick neu.",
                       "RAM: the lookback lives only in memory — no files, nothing in Explorer. Disk: for long lookbacks (up to 30 min); the files sit hidden in the buffer folder and are overwritten continuously. Switching starts the lookback over."),
            Style = (Style)FindResource("MutedStyle"),
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        }));
        RefreshBufferHint();
    }

    /// <summary>
    /// "A profile sets the buffer size": shown when the longest length belongs to a game, not to the standard picker. Says
    /// when that is what flips the buffer from RAM to the disk, otherwise what the ring costs.
    /// </summary>
    private void UpdateRingNote(AppHost host, int globalSeconds, StorageDecision ring, DiskForecast ringForecast)
    {
        if (_ringNote is null) return;
        var driver = ProfileLengths.DriverOf(host.Settings.Current, globalSeconds);
        if (driver is null)
        {
            _ringNote.Visibility = Visibility.Collapsed;
            return;
        }

        string who = $"{driver.DisplayName}, {DurationText.Format(driver.Seconds)}";
        bool flipsToDisk = false;
        try { flipsToDisk = ring.Kind == BufferStorageKind.Disk && host.ReplayBuffer.PlanStorage(globalSeconds).Kind == BufferStorageKind.Memory; }
        catch (Exception ex) { Logger.Warn("Buffer storage plan failed: " + ex.Message); }
        string size = DiskPlan.FormatBytes(ring.Kind == BufferStorageKind.Memory ? ring.EstimatedBytes : ringForecast.RequiredBytes);

        _ringNote.Text = flipsToDisk
            ? L.T($"Ein Profil ({who}) bestimmt die Puffergröße — dadurch Festplatte statt Arbeitsspeicher.",
                  $"A profile ({who}) sets the buffer size — so it uses the disk instead of RAM.")
            : L.T($"Ein Profil ({who}) bestimmt die Puffergröße (~{size}).",
                  $"A profile ({who}) sets the buffer size (~{size}).");
        _ringNote.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// The "Eigene Länge pro Spiel" list below the standard F9 length: one row per game that has its own length (picker with
    /// the same choices, remove) and a button that adds the game the detector sees right now. The ring always holds the
    /// longest length; a change that alters it restarts the buffer like changing the standard length does.
    /// </summary>
    private FrameworkElement BuildProfileLengthSection(AppHost host, int[] choices, Action refreshHint)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        panel.Children.Add(new TextBlock
        {
            Text = L.T("Eigene Länge pro Spiel", "Own length per game"),
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 2)
        });
        panel.Children.Add(new TextBlock
        {
            Text = L.T("Gilt, wenn das Spiel erkannt wird — sonst die Länge oben.", "Applies when the game is detected — otherwise the length above."),
            Style = (Style)FindResource("MutedStyle"),
            TextWrapping = TextWrapping.Wrap
        });

        var rows = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(rows);
        var addBtn = new System.Windows.Controls.Button { HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        var addHint = new TextBlock { Style = (Style)FindResource("MutedStyle"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(addBtn);
        panel.Children.Add(addHint);

        void Apply(string key, string? name, int? seconds)
        {
            var s = host.Settings.Current;
            int before = EffectiveSettings.LongestClipSeconds(s);
            GameProfiles.SetClipSeconds(s, key, name, seconds);
            host.Settings.Save();
            if (EffectiveSettings.LongestClipSeconds(s) != before) host.ReplayBuffer.RequestRestart();
            refreshHint();
        }

        void RefreshAdd()
        {
            var game = host.CurrentGame;
            var st = ProfileLengths.AddCurrent(host.Settings.Current, game?.ExeName, game?.DisplayName);
            addBtn.Content = st.Name.Length > 0
                ? L.T($"Aktuelles Spiel hinzufügen: {st.Name}", $"Add current game: {st.Name}")
                : L.T("Aktuelles Spiel hinzufügen", "Add current game");
            addBtn.IsEnabled = st.CanAdd;
            addHint.Text = st.Reason switch
            {
                AddCurrentReason.NoGame => L.T("Kein Spiel erkannt — starte das Spiel; es muss einmal im Vordergrund gewesen sein.",
                                               "No game detected — start the game; it has to have been in front once."),
                AddCurrentReason.AlreadyHasLength => L.T($"{st.Name} hat schon eine eigene Länge.", $"{st.Name} already has its own length."),
                _ => ""
            };
            addHint.Visibility = addHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void RebuildRows()
        {
            rows.Children.Clear();
            var list = ProfileLengths.Rows(host.Settings.Current);
            if (list.Count == 0)
                rows.Children.Add(new TextBlock
                {
                    Text = L.T("Noch keine eigenen Längen — jedes Spiel nutzt die Länge oben.", "No own lengths yet — every game uses the length above."),
                    Style = (Style)FindResource("MutedStyle"), TextWrapping = TextWrapping.Wrap
                });
            foreach (var row in list)
            {
                var key = row.Key;
                var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var name = new TextBlock
                {
                    Text = row.DisplayName, ToolTip = row.Key,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("TextBrush"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(name, 0);
                grid.Children.Add(name);

                var picker = new System.Windows.Controls.ComboBox { MinWidth = 130, Margin = new Thickness(8, 0, 8, 0) };
                foreach (var c in choices) picker.Items.Add(DurationText.Format(c));
                picker.SelectedIndex = InitialBufferIndex(choices, row.Seconds);
                // Attached after the initial selection: opening the settings must not rewrite a hand-edited length.
                picker.SelectionChanged += (_, _) =>
                {
                    if (picker.SelectedIndex < 0 || picker.SelectedIndex >= choices.Length) return;
                    Apply(key, null, choices[picker.SelectedIndex]);
                };
                Grid.SetColumn(picker, 1);
                grid.Children.Add(picker);

                var remove = new System.Windows.Controls.Button { Content = L.T("Entfernen", "Remove") };
                remove.Click += (_, _) =>
                {
                    Apply(key, null, null);
                    RebuildRows();
                    RefreshAdd();
                };
                Grid.SetColumn(remove, 2);
                grid.Children.Add(remove);
                rows.Children.Add(grid);
            }
        }

        addBtn.Click += (_, _) =>
        {
            var game = host.CurrentGame;
            var st = ProfileLengths.AddCurrent(host.Settings.Current, game?.ExeName, game?.DisplayName);
            if (!st.CanAdd || st.Key is null) return;
            // Starts at the standard length: the ring does not change, the user picks the real length in the new row.
            Apply(st.Key, st.Name, DurationText.Clamp(host.Settings.Current.ReplayBuffer.DurationSeconds));
            RebuildRows();
            RefreshAdd();
        };

        _refreshAddCurrent = RefreshAdd;
        RebuildRows();
        RefreshAdd();
        return panel;
    }

    /// <summary>
    /// The list entry for the current setting: the exact length, otherwise the largest offered length below it
    /// (a hand-edited 100 s shows 90 s), otherwise 60 s.
    /// </summary>
    internal static int InitialBufferIndex(int[] choices, int current)
    {
        int clamped = DurationText.Clamp(current);
        int exact = Array.IndexOf(choices, clamped);
        if (exact >= 0) return exact;
        for (int i = choices.Length - 1; i >= 0; i--)
            if (choices[i] <= clamped) return i;
        return Math.Max(0, Array.IndexOf(choices, 60));
    }

    private void RefreshCodecBox(AppHost host)
    {
        if (_codecBox is null) return;
        var current = host.Settings.Current.Video.Codec;
        _suppressCodecChange = true;
        try
        {
            _codecBox.Items.Clear();
            var codecs = host.AvailableCodecs.Count > 0
                ? host.AvailableCodecs
                : new[] { new CodecInfo(current, L.T("(läuft Detection…) ", "(detecting…) ") + current) };
            int select = 0;
            for (int i = 0; i < codecs.Count; i++)
            {
                _codecBox.Items.Add(codecs[i]);
                if (codecs[i].FFmpegName == current) select = i;
            }
            _codecBox.DisplayMemberPath = nameof(CodecInfo.Label);
            _codecBox.SelectedIndex = select;
        }
        finally { _suppressCodecChange = false; }
    }

    private void UpdateResolutionHint()
    {
        if (_resolutionHint is null) return;
        try
        {
            var s = Screen.PrimaryScreen;
            if (s != null)
                _resolutionHint.Text = L.T(
                    $"Display: {s.Bounds.Width} × {s.Bounds.Height}  ({Screen.AllScreens.Length} Monitor(e) erkannt)",
                    $"Display: {s.Bounds.Width} × {s.Bounds.Height}  ({Screen.AllScreens.Length} monitor(s) detected)");
        }
        catch { _resolutionHint.Text = ""; }
    }

    private void UpdateBitrateHint(AppHost host)
    {
        if (_bitrateHint is null) return;
        var v = host.Settings.Current.Video;
        if (v.Quality == QualityPreset.Custom)
        {
            _bitrateHint.Text = L.T($"Manuell: {v.Bitrate / 1_000_000} Mbps", $"Manual: {v.Bitrate / 1_000_000} Mbps");
        }
        else
        {
            var bps = QualityPresets.ComputeBitrate(v.Quality, v.Resolution);
            _bitrateHint.Text = L.T($"Effektive Bitrate: {bps / 1_000_000} Mbps (errechnet aus Qualität × Auflösung)",
                                    $"Effective bitrate: {bps / 1_000_000} Mbps (derived from quality × resolution)");
        }
        _refreshBufferHint?.Invoke();
    }

    private FrameworkElement LabeledRow(string label, FrameworkElement control, FrameworkElement? hint = null)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var labelBlock = new TextBlock
        {
            Text = label,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 6)
        };
        stack.Children.Add(labelBlock);
        stack.Children.Add(control);
        if (hint != null) stack.Children.Add(hint);
        return stack;
    }

    private static string LabelForResolution(ResolutionPreset r) => r switch
    {
        ResolutionPreset.Native => L.T("Native (automatisch)", "Native (automatic)"),
        ResolutionPreset.FullHD => "Full HD  (1920 × 1080)",
        ResolutionPreset.WQHD   => "WQHD  (2560 × 1440)",
        ResolutionPreset.UHD    => "4K UHD  (3840 × 2160)",
        _                       => r.ToString()
    };

    private static int IndexFromResolution(ResolutionPreset r) => r switch
    {
        ResolutionPreset.Native => 0,
        ResolutionPreset.FullHD => 1,
        ResolutionPreset.WQHD   => 2,
        ResolutionPreset.UHD    => 3,
        _                       => 0
    };

    private static ResolutionPreset ResolutionFromIndex(int i) => i switch
    {
        0 => ResolutionPreset.Native,
        1 => ResolutionPreset.FullHD,
        2 => ResolutionPreset.WQHD,
        3 => ResolutionPreset.UHD,
        _ => ResolutionPreset.Native
    };
}
