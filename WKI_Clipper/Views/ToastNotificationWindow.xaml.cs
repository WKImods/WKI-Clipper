using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace WKI_Clipper.Views;

public partial class ToastNotificationWindow : Window
{
    public string? FilePath { get; private set; }
    public ToastKind Kind { get; private set; }

    private readonly DispatcherTimer _autoClose;
    public event EventHandler? Closing2;

    public ToastNotificationWindow()
    {
        InitializeComponent();
        _autoClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _autoClose.Tick += (_, _) => BeginFadeOut();
    }

    public void Configure(ToastKind kind, string title, string body, string? filePath)
    {
        Kind = kind;
        TitleText.Text = title;
        BodyText.Text = body;
        FilePath = filePath;
        HintText.Text = Services.L.T("Klicken zum Öffnen", "Click to open");
        HintText.Visibility = string.IsNullOrEmpty(filePath) ? Visibility.Collapsed : Visibility.Visible;

        // Accent stripe color per kind
        var color = kind switch
        {
            ToastKind.Recording  => System.Windows.Media.Color.FromRgb(0xE0, 0x4E, 0x4E),
            ToastKind.Clip       => System.Windows.Media.Color.FromRgb(0xFF, 0x6A, 0x2C),
            ToastKind.Screenshot => System.Windows.Media.Color.FromRgb(0x4A, 0xD8, 0x6A),
            ToastKind.Warning    => System.Windows.Media.Color.FromRgb(0xE0, 0xA8, 0x40),
            _                    => System.Windows.Media.Color.FromRgb(0xFF, 0x6A, 0x2C)
        };
        AccentStripe.Fill = new SolidColorBrush(color);
    }

    public void ShowToast(double durationSeconds = 4.0)
    {
        Show();
        var fadeIn = new DoubleAnimation
        {
            From = 0, To = 1,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, fadeIn);

        _autoClose.Interval = TimeSpan.FromSeconds(durationSeconds);
        _autoClose.Start();
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (!string.IsNullOrEmpty(FilePath) && File.Exists(FilePath))
        {
            try { Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true }); } catch { }
        }
        BeginFadeOut();
    }

    private void OnClose(object sender, RoutedEventArgs e) => BeginFadeOut();

    public void BeginFadeOut()
    {
        _autoClose.Stop();
        var fade = new DoubleAnimation
        {
            From = Opacity, To = 0,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) =>
        {
            Closing2?.Invoke(this, EventArgs.Empty);
            Close();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    // Prevent activation/focus stealing.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        const int GWL_EXSTYLE = -20;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        var current = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, current | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int nIndex);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int nIndex, int dwNewLong);
}

public enum ToastKind { Clip, Recording, Screenshot, Warning, Info }
