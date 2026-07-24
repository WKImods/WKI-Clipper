using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WKI_Clipper.Native;
using WinForms = System.Windows.Forms;

namespace WKI_Clipper.Views;

/// <summary>
/// The Xbox-style "pill" shown while the board is open: a toggle per widget plus a
/// live clock. The host fills <see cref="WidgetButtons"/> with the toggle controls.
/// </summary>
public sealed class WidgetLauncherWindow : Window
{
    /// <summary>Host-owned toggle buttons live here.</summary>
    public StackPanel WidgetButtons { get; }

    private readonly TextBlock _clock;
    private readonly DispatcherTimer _clockTimer;
    private WinForms.Screen? _screen;

    public WidgetLauncherWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        WidgetButtons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };

        _clock = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 6, 0),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("TextBrush")
        };

        var sep = new Border
        {
            Width = 1, Margin = new Thickness(6, 6, 6, 6),
            Background = (Brush)Application.Current.FindResource("BorderBrush")
        };

        var bar = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        bar.Children.Add(WidgetButtons);
        bar.Children.Add(sep);
        bar.Children.Add(_clock);

        Content = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1E, 0x1E, 0x24)),
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 5, 8, 5),
            Child = bar
        };

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
    }

    public void ShowAt(WinForms.Screen screen)
    {
        _screen = screen;
        UpdateClock();
        _clockTimer.Start();
        Show();
        Recenter();
    }

    /// <summary>Re-center after the button set changes width.</summary>
    public void Recenter()
    {
        if (_screen is null) return;
        UpdateLayout();
        var b = _screen.Bounds;
        Left = b.Left + (b.Width - ActualWidth) / 2;
        Top  = b.Top + 12;
    }

    public new void Hide()
    {
        _clockTimer.Stop();
        base.Hide();
    }

    private void UpdateClock() => _clock.Text = DateTime.Now.ToString("HH:mm");
}
