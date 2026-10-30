using System;
using System.Linq;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.Services.AutoClicker;
using WKI_Clipper.Views.AutoClicker;
using RadioButton = System.Windows.Controls.RadioButton;

namespace WKI_Clipper.Views;

/// <summary>
/// The AutoClicker tab of Gaming Utilities (design section 9). On top: the anti-cheat warning (until acknowledged) and the status row of
/// the native clicker ("Clicker bereit", state pill, Start/Stop, conflicts). Below: the sub-tabs Einfach | Erweitert | Zonen &amp; Schutz |
/// Klickpunkte | Presets. The selected sub-tab is saved, so it survives the widget rebuild of a language or theme switch.
/// Controller and settings events arrive on any thread and are marshalled here.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class AutoClickerView : UserControl
{
    private bool _built;
    private bool _subscribed;
    private bool _restoring;
    private int _refreshQueued;
    private volatile bool _panelsDirty;

    public AutoClickerView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static AppHost? Host => App.Host;

    private ClickerPanel[] Panels => new ClickerPanel[] { SimplePanel, AdvancedPanel, ZonesPanel, PointsPanel, PresetsPanel };

    // ---------------- lifecycle ----------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var host = Host;
        if (host is null) return;

        if (!_built)
        {
            _built = true;
            BuildTexts();
            AckBtn.Click += OnAcknowledge;
            ArmedBox.Checked += (_, _) => SetArmed(true);
            ArmedBox.Unchecked += (_, _) => SetArmed(false);
            StartStopBtn.Click += OnStartStop;
            RetryBtn.Click += (_, _) => Host?.Clicker.Retry();
            RestoreSubTab();
        }

        if (!_subscribed)
        {
            host.Clicker.StateChanged += OnClickerState;
            host.Settings.SettingsChanged += OnSettingsChanged;
            _subscribed = true;
        }

        UpdateWarning();
        RefreshAll();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed && Host is { } host)
        {
            host.Clicker.StateChanged -= OnClickerState;
            host.Settings.SettingsChanged -= OnSettingsChanged;
            _subscribed = false;
        }
    }

    internal void BuildTexts()
    {
        WarningText.Text = L.T(
            "AutoClicker können in Online-Spielen mit Anti-Cheat (BattlEye, Easy Anti-Cheat, Vanguard ...) gegen die Regeln verstoßen und zu Bans führen. Nutzung auf eigene Gefahr.",
            "Auto clickers can break the rules of online games with anti-cheat (BattlEye, Easy Anti-Cheat, Vanguard ...) and lead to bans. Use at your own risk.");
        AckBtn.Content = L.T("Verstanden", "Understood");

        ArmedBox.ToolTip = L.T(
            "Startet den AutoClicker-Helfer im Hintergrund, der auf den Hotkey wartet. Aus = kein Hotkey, keine Klicks.",
            "Starts the AutoClicker helper in the background that waits for the hotkey. Off = no hotkey, no clicks.");
        RetryBtn.Content = L.T("Erneut versuchen", "Try again");

        TabSimple.Content = L.T("Einfach", "Simple");
        TabAdvanced.Content = L.T("Erweitert", "Advanced");
        TabZones.Content = L.T("Zonen & Schutz", "Zones & safety");
        TabPoints.Content = L.T("Klickpunkte", "Click points");
        TabPresets.Content = "Presets";
    }

    // ---------------- events (any thread) ----------------

    private void OnClickerState(ClickerState state) => QueueRefresh(panels: false);

    private void OnSettingsChanged(object? sender, AppSettings settings) => QueueRefresh(panels: true);

    /// <summary>A burst of state messages (10/s while running) and saves collapses into one refresh on the UI thread.</summary>
    private void QueueRefresh(bool panels)
    {
        if (panels) _panelsDirty = true;
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Volatile.Write(ref _refreshQueued, 0);
            if (!IsLoaded) return;
            UpdateStatus();
            if (!_panelsDirty) return;
            _panelsDirty = false;
            foreach (var panel in Panels) panel.Refresh();
        }));
    }

    private void RefreshAll()
    {
        UpdateStatus();
        foreach (var panel in Panels) panel.Refresh();
    }

    // ---------------- status row ----------------

    private void UpdateStatus()
    {
        var host = Host;
        if (host?.Settings.Current.AutoClicker?.Clicker is not { } clicker) return;
        var state = host.Clicker.State;
        var conflicts = ClickerConflicts.Evaluate(host.Settings.Current);

        _restoring = true;
        try
        {
            ArmedBox.IsChecked = clicker.Armed;
            ArmedBox.Content = L.T($"Clicker bereit (Hotkey: {ClickerHotkey.Describe(clicker.Hotkey)})",
                                   $"Clicker ready (hotkey: {ClickerHotkey.Describe(clicker.Hotkey)})");
        }
        finally { _restoring = false; }

        PillText.Text = ClickerStateText.Pill(state);
        PillText.SetResourceReference(TextBlock.ForegroundProperty, ClickerStatusRules.Tone(state) switch
        {
            ClickerPillTone.Active => "SuccessBrush",
            ClickerPillTone.Danger => "DangerBrush",
            ClickerPillTone.Normal => "TextBrush",
            _ => "MutedBrush"
        });

        var button = ClickerStatusRules.StartButton(state, ClickerConflicts.CanStart(conflicts));
        StartStopBtn.Content = button.IsStop ? L.T("Stopp", "Stop") : L.T("Start", "Start");
        StartStopBtn.IsEnabled = button.Enabled;
        StartStopBtn.ToolTip = clicker.Armed ? null : L.T("Zuerst \"Clicker bereit\" einschalten.", "Switch \"Clicker ready\" on first.");
        RetryBtn.Visibility = state.Phase == ClickerPhase.Error ? Visibility.Visible : Visibility.Collapsed;

        ConflictRows.Children.Clear();
        foreach (var conflict in conflicts)
        {
            var line = new TextBlock { Text = conflict.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            line.SetResourceReference(TextBlock.ForegroundProperty, conflict.BlocksStart ? "DangerBrush" : "MutedBrush");
            ConflictRows.Children.Add(line);
        }
    }

    private void SetArmed(bool armed)
    {
        var host = Host;
        if (_restoring || host?.Settings.Current.AutoClicker?.Clicker is not { } clicker || clicker.Armed == armed) return;
        clicker.Armed = armed;
        host.Settings.Save();   // the controller follows the switch from SettingsChanged
        UpdateStatus();
    }

    private void OnStartStop(object sender, RoutedEventArgs e)
    {
        var host = Host;
        if (host is null) return;
        if (host.Clicker.State.IsRunning) host.Clicker.Stop();
        else
        {
            // The clicker would start clicking at once under the mouse - the board's topmost windows must not be the target.
            host.CloseBoard?.Invoke();
            host.Clicker.Start();
        }
    }

    // ---------------- sub-tabs ----------------

    private RadioButton[] Tabs => new[] { TabSimple, TabAdvanced, TabZones, TabPoints, TabPresets };

    private UIElement[] Pages => new UIElement[] { SimplePanel, AdvancedPanel, ZonesPanel, PointsPanel, PresetsPanel };

    private void RestoreSubTab()
    {
        var saved = Host?.Settings.Current.AutoClicker?.Clicker?.SubTab;
        var target = Tabs.FirstOrDefault(t => (string)t.Tag == saved) ?? TabSimple;
        _restoring = true;
        try
        {
            if (target.IsChecked != true) target.IsChecked = true;   // triggers OnSubTab → shows the page
            else ShowSubTab(target);
        }
        finally { _restoring = false; }
    }

    private void OnSubTab(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton tab || PresetsPanel == null) return;   // Checked fires while the XAML is still being built (the last page)
        ShowSubTab(tab);
        if (_restoring) return;

        var host = Host;
        var clicker = host?.Settings.Current.AutoClicker?.Clicker;
        var key = (string)tab.Tag;
        if (host is null || clicker is null || clicker.SubTab == key) return;
        clicker.SubTab = key;
        host.Settings.Save();
    }

    private void ShowSubTab(RadioButton tab)
    {
        var tabs = Tabs;
        var pages = Pages;
        for (int i = 0; i < tabs.Length; i++)
            pages[i].Visibility = ReferenceEquals(tabs[i], tab) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- warning ----------------

    private void UpdateWarning()
    {
        var acknowledged = Host?.Settings.Current.AutoClicker?.WarningAcknowledged == true;
        WarningBanner.Visibility = acknowledged ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnAcknowledge(object sender, RoutedEventArgs e)
    {
        var host = Host;
        if (host is null) return;
        host.Settings.Current.AutoClicker.WarningAcknowledged = true;
        host.Settings.Save();
        UpdateWarning();
    }
}
