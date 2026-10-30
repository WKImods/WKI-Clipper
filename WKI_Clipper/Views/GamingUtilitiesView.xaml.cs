using System.Windows;
using System.Windows.Controls;
using WKI_Clipper.Services;

namespace WKI_Clipper.Views;

/// <summary>
/// The "Gaming Utilities" widget (WidgetId.Crosshair keeps its id, so layouts, hotkeys and settings carry over): a tab strip
/// over the crosshair configuration and the AutoClicker tab. The last selected tab is saved.
/// </summary>
public partial class GamingUtilitiesView : UserControl
{
    public const string CrosshairTabKey = "Crosshair";
    public const string AutoClickerTabKey = "AutoClicker";

    private bool _restoring;

    public GamingUtilitiesView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        // The whole widget left the screen (also while the AutoClicker tab shows, when the crosshair view gets no event of its own).
        IsVisibleChanged += (_, _) => { if (!IsVisible) CrosshairHost.ReleaseUserPick(); };
    }

    /// <summary>The crosshair configuration (the widget host drives it: preview sync, game changes, profile refresh).</summary>
    public CrosshairView Crosshair => CrosshairHost;

    /// <summary>The AutoClicker tab.</summary>
    public AutoClickerView AutoClicker => AutoClickerHost;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TabCrosshair.Content   = L.T("Fadenkreuz", "Crosshair");
        TabAutoClicker.Content = "AutoClicker";

        var saved = App.Host?.Settings.Current.AutoClicker?.GamingUtilitiesTab;
        _restoring = true;
        try
        {
            var target = saved == AutoClickerTabKey ? TabAutoClicker : TabCrosshair;
            if (target.IsChecked != true) target.IsChecked = true;   // triggers OnTab → shows the tab
            else ShowTab(target == TabAutoClicker);
        }
        finally { _restoring = false; }
    }

    private void OnTab(object sender, RoutedEventArgs e)
    {
        if (CrosshairHost == null || AutoClickerHost == null) return;   // Checked fires while the XAML is still being built
        bool autoClicker = TabAutoClicker.IsChecked == true;
        ShowTab(autoClicker);
        if (_restoring) return;

        var host = App.Host;
        var section = host?.Settings.Current.AutoClicker;
        var key = autoClicker ? AutoClickerTabKey : CrosshairTabKey;
        if (host is null || section is null || section.GamingUtilitiesTab == key) return;
        section.GamingUtilitiesTab = key;
        host.Settings.Save();
    }

    private void ShowTab(bool autoClicker)
    {
        CrosshairHost.Visibility = autoClicker ? Visibility.Collapsed : Visibility.Visible;
        AutoClickerHost.Visibility = autoClicker ? Visibility.Visible : Visibility.Collapsed;
    }
}
