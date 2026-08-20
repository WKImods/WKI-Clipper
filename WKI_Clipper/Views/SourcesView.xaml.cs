using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WKI_Clipper.Models;
using WKI_Clipper.Services;

namespace WKI_Clipper.Views;

/// <summary>
/// Scene switcher + source visibility for OBS: one click to change the program scene, one
/// checkbox per source of the current scene to show/hide it in the stream. Everything is
/// event-driven off the OBS mirror — no polling; OBS pushes every relevant change
/// (scene switch, item added/removed, visibility toggled, also when done in OBS itself).
/// </summary>
public partial class SourcesView : UserControl
{
    private bool _subscribed;
    private bool _lastConnected;
    private string? _lastScene;
    /// <summary>True while checkboxes are being filled from OBS state — their
    /// Checked/Unchecked handlers must not echo those values back.</summary>
    private bool _syncingUi;
    /// <summary>Generation stamp so a slow item query cannot overwrite a newer one.</summary>
    private int _refreshGen;

    public SourcesView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var host = App.Host;
        if (host is null) return;

        ScenesHeader.Text = L.T("Szenen", "Scenes");
        if (!_subscribed)
        {
            host.Obs.StatusChanged += OnStatus;
            host.Obs.SceneItemsChanged += OnItemsChanged;
            _subscribed = true;
        }
        RefreshAll();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var host = App.Host;
        if (host != null && _subscribed)
        {
            host.Obs.StatusChanged -= OnStatus;
            host.Obs.SceneItemsChanged -= OnItemsChanged;
            _subscribed = false;
        }
    }

    // Both events arrive on socket worker threads.
    private void OnStatus() => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (!IsLoaded) return;
        var host = App.Host;
        if (host is null) return;
        // StatusChanged also fires for every volume tick — only connection or scene
        // transitions warrant rebuilding this widget.
        bool conn = host.Obs.IsConnected;
        string? scene = host.Obs.Status.CurrentScene;
        if (conn != _lastConnected || !string.Equals(scene, _lastScene, StringComparison.Ordinal))
            RefreshAll();
    }));

    private void OnItemsChanged() => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (IsLoaded) RefreshItems();
    }));

    /// <summary>Scenes + items, both fetched off the UI thread (blocking WS requests).</summary>
    private async void RefreshAll()
    {
        var host = App.Host;
        if (host is null) return;

        _lastConnected = host.Obs.IsConnected;
        _lastScene = host.Obs.Status.CurrentScene;

        if (!_lastConnected)
        {
            ScenePanel.Children.Clear();
            ItemsPanel.Children.Clear();
            SourcesHeader.Text = "";
            StatusLine.Text = L.T("OBS ist nicht verbunden.", "OBS is not connected.");
            return;
        }
        StatusLine.Text = "";

        var scenes = await Task.Run(host.Obs.ListScenes);
        if (!IsLoaded) return;
        BuildSceneButtons(scenes, _lastScene);
        RefreshItems();
    }

    private void BuildSceneButtons(List<string> scenes, string? current)
    {
        ScenePanel.Children.Clear();
        foreach (var scene in scenes)
        {
            bool isCurrent = string.Equals(scene, current, StringComparison.OrdinalIgnoreCase);
            var btn = new Button
            {
                Content = scene,
                Padding = new Thickness(9, 4, 9, 4),
                Margin = new Thickness(0, 0, 6, 6),
                FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
                BorderBrush = isCurrent ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("BorderBrush"),
                Tag = scene
            };
            btn.Click += OnSceneClick;
            ScenePanel.Children.Add(btn);
        }
    }

    private async void OnSceneClick(object sender, RoutedEventArgs e)
    {
        var host = App.Host;
        if (host is null || sender is not Button { Tag: string scene }) return;
        // Same path as a deck tile — the CurrentProgramSceneChanged event (which arrives
        // after the transition, stinger included) drives the highlight update.
        await host.Obs.ExecuteAsync(new StreamButtonConfig { Action = StreamAction.SetScene, Param = scene });
    }

    /// <summary>Sources of the current program scene, stale-response-proof.</summary>
    private async void RefreshItems()
    {
        var host = App.Host;
        if (host is null || !host.Obs.IsConnected) return;
        string? scene = host.Obs.Status.CurrentScene;
        if (string.IsNullOrEmpty(scene))
        {
            ItemsPanel.Children.Clear();
            SourcesHeader.Text = "";
            return;
        }

        int gen = ++_refreshGen;
        var items = await Task.Run(() => host.Obs.ListSceneItemsDetailed(scene));
        // A newer refresh (scene switched again mid-query) wins.
        if (!IsLoaded || gen != _refreshGen) return;

        SourcesHeader.Text = L.T($"Quellen in „{scene}\"", $"Sources in \"{scene}\"");
        _syncingUi = true;
        try
        {
            ItemsPanel.Children.Clear();
            foreach (var it in items)
            {
                var cb = new CheckBox
                {
                    Content = it.Name,
                    IsChecked = it.Enabled,
                    Foreground = (Brush)FindResource("TextBrush"),
                    Margin = new Thickness(2, 3, 2, 3),
                    Tag = (scene, it.ItemId)
                };
                cb.Checked += OnItemToggled;
                cb.Unchecked += OnItemToggled;
                ItemsPanel.Children.Add(cb);
            }
            if (items.Count == 0)
                StatusLine.Text = L.T("Diese Szene hat keine Quellen.", "This scene has no sources.");
            else if (StatusLine.Text.Length > 0 && _lastConnected)
                StatusLine.Text = "";
        }
        finally { _syncingUi = false; }
    }

    private async void OnItemToggled(object sender, RoutedEventArgs e)
    {
        if (_syncingUi) return;
        var host = App.Host;
        if (host is null || sender is not CheckBox { Tag: (string scene, int itemId) } cb) return;
        // OBS echoes SceneItemEnableStateChanged, which re-syncs the list — including the
        // revert if the request failed (e.g. item deleted a moment earlier).
        await host.Obs.SetSceneItemEnabledAsync(scene, itemId, cb.IsChecked == true);
    }
}
