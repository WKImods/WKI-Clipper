using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WKI_Clipper.Native;
using WKI_Clipper.Services;
using WKI_Clipper.Services.AutoClicker;
using WinForms = System.Windows.Forms;

namespace WKI_Clipper.Views.AutoClicker;

/// <summary>
/// One click-point or zone picker in progress (design section 8): one overlay window per monitor, the clicker told about it
/// (<see cref="ClickerController.SetPicker"/> - hotkeys are ignored and a running clicker stops while it is open), and the edited list
/// written to the settings once when it closes. Only one picker can be open at a time. UI thread only.
///
/// Every way out ends in <see cref="Finish"/>: Esc, right-click, a window closed from outside (Alt+F4, app shutdown) and an error in
/// a handler - a picker that fails half-way must never leave the clicker blocked or a dimmed screen behind.
/// </summary>
public abstract class PickerSession
{
    private static PickerSession? _active;

    /// <summary>True while a click-point or zone picker is on screen.</summary>
    public static bool IsOpen => _active is not null;

    /// <summary>Raised on the UI thread when a picker opens or closes (the UI greys its picker buttons out meanwhile).</summary>
    public static event Action? OpenChanged;

    protected readonly AppHost Host;
    private readonly List<PickerOverlayWindow> _windows = new();
    private readonly Action? _closed;
    private bool _finished;

    protected PickerSession(AppHost host, Action? closed)
    {
        Host = host;
        _closed = closed;
    }

    protected IReadOnlyList<PickerOverlayWindow> Windows => _windows;

    /// <summary>Opens one window per monitor. False when another picker is already open (it is brought forward) or nothing could be shown.</summary>
    protected bool Open()
    {
        if (_active is not null)
        {
            _active.FocusWindowUnderCursor();
            return false;
        }

        var monitors = CurrentMonitors();
        if (monitors.Count == 0) return false;

        _active = this;
        Host.Clicker.SetPicker(true);
        try
        {
            foreach (var m in monitors)
            {
                var w = CreateWindow(m);
                _windows.Add(w);
                w.Closed += OnWindowClosed;
                w.ShowOn();
            }
            RedrawAll();
            FocusWindowUnderCursor();
        }
        catch (Exception ex)
        {
            Logger.Error("AutoClicker picker could not open", ex);
            Finish(commit: false);
            return false;
        }
        OpenChanged?.Invoke();
        return true;
    }

    protected abstract PickerOverlayWindow CreateWindow(PickerMonitor monitor);

    /// <summary>Writes the edited list to the settings (only called when something changed).</summary>
    protected abstract void Commit();

    /// <summary>True once the user changed something worth saving.</summary>
    protected bool Dirty { get; set; }

    public void Finish() => Finish(commit: true);

    private void Finish(bool commit)
    {
        if (_finished) return;
        _finished = true;

        if (commit && Dirty)
        {
            try { Commit(); }
            catch (Exception ex) { Logger.Error("AutoClicker picker could not save its result", ex); }
        }

        foreach (var w in _windows)
        {
            w.Closed -= OnWindowClosed;
            try { w.Close(); } catch { /* already closing */ }
        }
        _windows.Clear();

        try { Host.Clicker.SetPicker(false); }
        catch (Exception ex) { Logger.Warn("AutoClicker picker: releasing the clicker failed: " + ex.Message); }

        if (ReferenceEquals(_active, this)) _active = null;
        OpenChanged?.Invoke();
        try { _closed?.Invoke(); }
        catch (Exception ex) { Logger.Warn("AutoClicker picker close callback failed: " + ex.Message); }
    }

    private void OnWindowClosed(object? sender, EventArgs e) => Finish();

    /// <summary>Runs a window event handler; an exception closes the picker (keeping what was picked) instead of wedging it.</summary>
    internal void Guard(Action action)
    {
        if (_finished) return;
        try { action(); }
        catch (Exception ex)
        {
            Logger.Error("AutoClicker picker failed - closing it", ex);
            Finish();
        }
    }

    protected void RedrawAll()
    {
        foreach (var w in _windows) w.Redraw();
    }

    private void FocusWindowUnderCursor()
    {
        if (_windows.Count == 0) return;
        var monitors = new List<PickerMonitor>(_windows.Count);
        foreach (var w in _windows) monitors.Add(w.Monitor);
        int i = User32.GetCursorPos(out var pt) ? PickerGeometry.MonitorIndexAt(monitors, new PixelPoint(pt.X, pt.Y)) : -1;
        var target = _windows[i >= 0 ? i : 0];
        target.Activate();
        target.Focus();
    }

    private static List<PickerMonitor> CurrentMonitors()
    {
        var result = new List<PickerMonitor>();
        foreach (var s in WinForms.Screen.AllScreens)
            result.Add(new PickerMonitor(new PixelRect(s.Bounds.Left, s.Bounds.Top, s.Bounds.Width, s.Bounds.Height), DisplayGeometry.ScaleOf(s)));
        return result;
    }
}

/// <summary>
/// The shared look of a picker window: borderless, topmost, covering exactly one monitor (physical bounds, per-monitor DPI), dimmed so
/// it catches every click, a cross cursor, an instruction banner and a drawing layer the concrete picker fills in <see cref="Redraw"/>.
/// Positions are always taken from the physical cursor and converted with <see cref="PickerGeometry"/>, so 125/150 % monitors and
/// monitors left of or above the primary one store the same pixels the host later clicks.
/// </summary>
public abstract class PickerOverlayWindow : Window
{
    protected readonly Canvas Layer;
    private readonly TextBlock _hint;
    private readonly TextBlock _status;

    protected PickerOverlayWindow(PickerMonitor monitor, string hint)
    {
        Monitor = monitor;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = Cursors.Cross;
        Focusable = true;
        Title = "WKI AutoClicker Picker";

        // Not fully transparent: a transparent pixel of a layered window lets the click through to whatever is below.
        var dim = new System.Windows.Shapes.Rectangle { Opacity = 0.35 };
        dim.SetResourceReference(Shape.FillProperty, "BgBrush");

        Layer = new Canvas { ClipToBounds = true };

        _hint = new TextBlock { Text = hint, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        _status = new TextBlock { FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var text = new StackPanel();
        text.Children.Add(_hint);
        text.Children.Add(_status);

        var banner = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 24, 0, 0),
            MaxWidth = 720,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            IsHitTestVisible = false,
            Child = text
        };
        banner.SetResourceReference(Border.BackgroundProperty, "PopupBrush");
        banner.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        var root = new Grid();
        root.Children.Add(dim);
        root.Children.Add(Layer);
        root.Children.Add(banner);
        Content = root;

        DpiChanged += (_, e) =>
        {
            Monitor = Monitor with { Scale = e.NewDpi.DpiScaleX };
            Redraw();
        };
    }

    /// <summary>The monitor this window covers; the scale follows the window's real DPI once it is shown.</summary>
    public PickerMonitor Monitor { get; private set; }

    protected DipSize LocalSize => PickerGeometry.WindowSize(Monitor);

    public string StatusText
    {
        get => _status.Text;
        set => _status.Text = value;
    }

    /// <summary>Shows the window and puts it exactly on its monitor (physical pixels), then takes the DPI WPF really uses there.</summary>
    internal void ShowOn()
    {
        var size = PickerGeometry.WindowSize(Monitor);
        Width = size.W;
        Height = size.H;
        var b = Monitor.Bounds;
        DisplayGeometry.PlaceAt(this, b.X, b.Y, b.W, b.H);
        Show();
        DisplayGeometry.PlaceAt(this, b.X, b.Y, b.W, b.H);
        Monitor = Monitor with { Scale = VisualTreeHelper.GetDpi(this).DpiScaleX };
    }

    /// <summary>Rebuilds the drawing layer from the session's current state.</summary>
    public abstract void Redraw();

    /// <summary>The physical virtual-screen pixel under the mouse: the real cursor, or the event position converted when that fails.</summary>
    protected PixelPoint CursorPixel(System.Windows.Input.MouseEventArgs e)
    {
        if (User32.GetCursorPos(out var pt)) return new PixelPoint(pt.X, pt.Y);
        var p = e.GetPosition(this);
        return PickerGeometry.ToPhysical(Monitor, new DipPoint(p.X, p.Y));
    }

    /// <summary>Places <paramref name="element"/> with its top-left at a local DIP position.</summary>
    protected static void Put(UIElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
    }
}
