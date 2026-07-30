using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WKI_Clipper.Native;
using WinForms = System.Windows.Forms;

namespace WKI_Clipper.Views;

/// <summary>
/// A small blinking "REC" badge shown while a manual recording runs. Topmost so the
/// user always sees it, but marked WDA_EXCLUDEFROMCAPTURE so it is invisible to every
/// screen capture (WGC and monitor/ddagrab alike) — it therefore never appears in the
/// recording itself. Never takes focus from the game.
/// </summary>
public sealed class RecordingIndicatorWindow : Window
{
    private static readonly SolidColorBrush RecRed = new(Color.FromRgb(0xE0, 0x3E, 0x3E));

    private readonly Ellipse _dot;
    private readonly TextBlock _timeText;
    private readonly DispatcherTimer _timer;
    private DateTime _startedAt;
    private bool _blinkOn = true;
    private IntPtr _hwnd;
    private WinForms.Screen? _screen;

    public RecordingIndicatorWindow()
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

        _dot = new Ellipse
        {
            Width = 12, Height = 12, Fill = RecRed,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
        };
        var recLabel = new TextBlock
        {
            Text = "REC", Foreground = RecRed, FontWeight = FontWeights.Bold, FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
        };
        _timeText = new TextBlock
        {
            Text = "00:00", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };

        var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        panel.Children.Add(_dot);
        panel.Children.Add(recLabel);
        panel.Children.Add(_timeText);

        Content = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x14, 0x14, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 5, 12, 5),
            Child = panel
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += OnTick;
    }

    /// <summary>Show the badge on the given monitor (top-right) and start blinking + counting.</summary>
    public void StartOn(WinForms.Screen screen)
    {
        _screen = screen;
        _startedAt = DateTime.Now;
        _blinkOn = true;
        _dot.Opacity = 1;
        UpdateText();
        Show();
        Reposition();
        _timer.Start();
    }

    public void StopIndicator()
    {
        _timer.Stop();
        Hide();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _blinkOn = !_blinkOn;
        _dot.Opacity = _blinkOn ? 1.0 : 0.15;
        UpdateText();
    }

    private void UpdateText() => _timeText.Text = (DateTime.Now - _startedAt).ToString(@"mm\:ss");

    private void Reposition()
    {
        if (_screen is null) return;
        UpdateLayout();
        var b = _screen.Bounds;   // DIPs (app assumes 100% DPI, like the other overlays)
        Left = b.Right - ActualWidth - 24;
        Top = b.Top + 24;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        // Invisible to every screen capture → never in the recording.
        try { User32.SetWindowDisplayAffinity(_hwnd, User32.WDA_EXCLUDEFROMCAPTURE); } catch { }
        // No-activate so it can't steal focus from the game.
        try
        {
            int ex = User32.GetWindowLong(_hwnd, User32.GWL_EXSTYLE);
            User32.SetWindowLong(_hwnd, User32.GWL_EXSTYLE, ex | User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW);
        }
        catch { }
    }
}
