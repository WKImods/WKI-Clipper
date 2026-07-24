using System.Windows;
using System.Windows.Controls;
using WKI_Clipper.Services;

namespace WKI_Clipper.Views;

/// <summary>
/// The "less-frequent config" widget: bundles Video / Hotkeys / Paths / About behind
/// a small tab strip. Reuses the existing settings views unchanged.
/// </summary>
public partial class SettingsWidgetView : UserControl
{
    private VideoSettingsView? _video;
    private HotkeysView? _hotkeys;
    private PathsView? _paths;
    private AboutView? _about;

    public SettingsWidgetView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TabVideo.Content   = "Video";
        TabHotkeys.Content = "Hotkeys";
        TabPaths.Content   = L.T("Pfade", "Paths");
        TabAbout.Content   = L.T("Über", "About");
        SaveBtn.Content    = L.T("Settings speichern", "Save settings");

        if (TabVideo.IsChecked != true) TabVideo.IsChecked = true; // triggers OnTab → default view
    }

    private void OnTab(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded && Host == null) return;
        if (TabVideo.IsChecked == true)        Host.Content = _video   ??= new VideoSettingsView();
        else if (TabHotkeys.IsChecked == true) Host.Content = _hotkeys ??= new HotkeysView();
        else if (TabPaths.IsChecked == true)   Host.Content = _paths   ??= new PathsView();
        else if (TabAbout.IsChecked == true)   Host.Content = _about   ??= new AboutView();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var host = App.Host;
        if (host is null) return;
        host.Settings.Save();
        host.Hotkeys.RegisterAll();
        ToastService.Show(ToastKind.Info, L.T("Gespeichert", "Saved"),
            L.T("Einstellungen übernommen.", "Settings applied."), durationSeconds: 2.0);
    }
}
