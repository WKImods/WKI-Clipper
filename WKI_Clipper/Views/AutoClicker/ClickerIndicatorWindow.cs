using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using WKI_Clipper.Native;
using WKI_Clipper.Services;
using WKI_Clipper.Services.AutoClicker;
using WinForms = System.Windows.Forms;

namespace WKI_Clipper.Views.AutoClicker;

/// <summary>
/// The small "AutoClicker · 25 cps" label while the clicker runs, a mouse pattern plays or one is recorded ("Mausbewegung · Kreis",
/// "Aufnahme läuft · Strg+Alt+R beendet"): topmost, click-through, never focusable and never in Alt-Tab.
/// It is marked WDA_EXCLUDEFROMCAPTURE like the crosshair and the recording badge, so it stays out of clips, stream and screenshots.
/// <see cref="TryShow"/> refuses to show it when that exclusion cannot be set - a label that might end up in a clip is worse than none.
/// Colours come from the theme's palette keys, so a theme switch follows.
/// </summary>
public sealed class ClickerIndicatorWindow : Window
{
    private readonly TextBlock _text;
    private IntPtr _hwnd;

    public ClickerIndicatorWindow()
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
        IsHitTestVisible = false;
        Focusable = false;
        Title = "WKI AutoClicker Indicator";

        var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        dot.SetResourceReference(Shape.FillProperty, "AccentBrush");

        _text = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        row.Children.Add(dot);
        row.Children.Add(_text);

        var frame = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(10, 4, 12, 4), Child = row };
        frame.SetResourceReference(Border.BackgroundProperty, "PopupBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        Content = frame;
    }

    public string Text
    {
        get => _text.Text;
        set => _text.Text = value;
    }

    /// <summary>
    /// Shows the label top-centre on <paramref name="screen"/>. False (and nothing shown) when the window could not be hidden from
    /// capture. Safe to call again while it is up: only the text changes then.
    /// </summary>
    public bool TryShow(WinForms.Screen screen)
    {
        if (IsVisible) return true;
        if (_exclusionRefused) return false;   // one try (and one warning) per window, not one per state message
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (!ApplyStyles())
        {
            _exclusionRefused = true;
            return false;
        }

        Show();
        UpdateLayout();
        var b = DisplayGeometry.Bounds(screen);   // physical pixels
        double s = DisplayGeometry.ScaleOf(screen);
        DisplayGeometry.MoveTo(this, b.X + (b.W - ActualWidth * s) / 2, b.Y + 12 * s);
        return true;
    }

    private bool _exclusionRefused;

    /// <summary>Click-through, no-activate, tool window and capture exclusion. False when the exclusion is refused.</summary>
    private bool ApplyStyles()
    {
        try
        {
            if (!User32.SetWindowDisplayAffinity(_hwnd, User32.WDA_EXCLUDEFROMCAPTURE))
            {
                Logger.Warn("AutoClicker indicator: the window cannot be excluded from capture - it stays hidden.");
                return false;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("AutoClicker indicator: capture exclusion is not available - it stays hidden: " + ex.Message);
            return false;
        }

        int style = User32.GetWindowLong(_hwnd, User32.GWL_EXSTYLE);
        User32.SetWindowLong(_hwnd, User32.GWL_EXSTYLE,
            style | User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW | User32.WS_EX_TRANSPARENT);
        return true;
    }
}

/// <summary>
/// Keeps <see cref="ClickerIndicatorWindow"/> in step with the clicker and the mouse patterns: up while one of the label's parts applies
/// and the capture path can hide it (<see cref="ClickerIndicatorRules"/>), hidden otherwise. A settings save refreshes it too (pattern
/// names, the record hotkey). It only reads the capture settings; it never asks the buffer to restart. Created and used on the UI
/// thread; the controller's events are marshalled here.
/// </summary>
public sealed class ClickerIndicatorPresenter : IDisposable
{
    private readonly AppHost _host;
    private ClickerIndicatorWindow? _window;
    private bool _disposed;
    private readonly object _gate = new();
    private bool _refreshQueued;
    private bool _settingsChanged;

    public ClickerIndicatorPresenter(AppHost host)
    {
        _host = host;
        _host.Clicker.StateChanged += OnChanged;
        _host.Settings.SettingsChanged += OnSettingsChanged;
    }

    private void OnChanged(ClickerState _) => QueueRefresh();

    private void OnSettingsChanged(object? sender, Models.AppSettings _)
    {
        lock (_gate) _settingsChanged = true;
        QueueRefresh();
    }

    /// <summary>One refresh at a time on the UI thread, however many events arrive (10 state messages a second while running).</summary>
    private void QueueRefresh()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || _disposed) return;
        lock (_gate)
        {
            if (_refreshQueued) return;
            _refreshQueued = true;
        }
        dispatcher.BeginInvoke(new Action(() =>
        {
            lock (_gate) _refreshQueued = false;
            Refresh();
        }));
    }

    private void Refresh()
    {
        if (_disposed) return;
        try
        {
            bool settingsChanged;
            lock (_gate)
            {
                settingsChanged = _settingsChanged;
                _settingsChanged = false;
            }
            // A window whose capture exclusion was refused never tries again; after a settings change a fresh one gets one new try.
            if (settingsChanged && _window is { IsVisible: false })
            {
                _window.Close();
                _window = null;
            }

            var state = _host.Clicker.State;
            var settings = _host.Settings.Current;
            if (!ClickerIndicatorRules.ShouldShow(state, settings))
            {
                _window?.Hide();
                return;
            }

            _window ??= new ClickerIndicatorWindow();
            _window.Text = ClickerIndicatorRules.Text(state, settings);
            if (!_window.IsVisible)
            {
                var screen = WinForms.Screen.FromPoint(WinForms.Cursor.Position);
                _window.TryShow(screen);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("AutoClicker indicator refresh failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _host.Clicker.StateChanged -= OnChanged;
        _host.Settings.SettingsChanged -= OnSettingsChanged;
        try { _window?.Close(); } catch { }
        _window = null;
    }
}
