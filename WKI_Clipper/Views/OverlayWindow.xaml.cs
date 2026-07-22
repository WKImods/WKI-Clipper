using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Forms;
using System.Windows.Input;
using WKI_Clipper.Services;
using WKI_Clipper.ViewModels;

namespace WKI_Clipper.Views;

public partial class OverlayWindow : Window
{
    public OverlayViewModel ViewModel { get; }

    public OverlayWindow(AppHost host)
    {
        ViewModel = new OverlayViewModel(host);
        DataContext = ViewModel;
        InitializeComponent();

        // Localized sidebar (XAML holds the German defaults).
        TabCapture.Content = L.T("Aufnahme", "Capture");
        TabStatus.Content = L.T("Status", "Status");
        TabClips.Content = L.T("Clips", "Clips");
        TabAudio.Content = L.T("Audio", "Audio");
        TabVideo.Content = L.T("Video", "Video");
        TabHotkeys.Content = L.T("Hotkeys", "Hotkeys");
        TabPaths.Content = L.T("Pfade", "Paths");
        TabAbout.Content = L.T("Über", "About");
        SaveSettingsBtn.Content = L.T("Settings speichern", "Save settings");
        ClipsFolderBtn.Content = L.T("Clips-Ordner", "Clips folder");
    }

    public void ShowOnActiveMonitor()
    {
        // Find the monitor that currently has the mouse cursor.
        var pos = System.Windows.Forms.Cursor.Position;
        var screen = Screen.FromPoint(pos);
        var bounds = screen.WorkingArea;

        Left = bounds.Left + (bounds.Width - Width) / 2;
        Top  = bounds.Top  + (bounds.Height - Height) / 2;
        Show();
        Activate();
        Focus();
    }

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Hide();
        base.OnKeyDown(e);
    }
}

/// <summary>
/// Converts the current-tab string to a bool for RadioButton.IsChecked.
/// </summary>
public sealed class TabConverter : IValueConverter
{
    public static readonly TabConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && parameter is string p && string.Equals(s, p, StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true && parameter is string p ? p : Binding.DoNothing;
}
