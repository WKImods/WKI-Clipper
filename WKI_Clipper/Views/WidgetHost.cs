using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WinForms = System.Windows.Forms;

namespace WKI_Clipper.Views;

/// <summary>
/// Owns the whole widget overlay: the floating widget windows, the dim backdrop and
/// the launcher pill. Replaces the old single tabbed OverlayWindow. State machine is
/// simple — the board is either open (backdrop + launcher + all visible widgets,
/// interactive) or closed (only pinned widgets remain, as no-activate floats).
/// </summary>
public sealed class WidgetHost : IDisposable
{
    private readonly AppHost _host;
    private readonly Dictionary<WidgetId, WidgetWindow> _windows = new();
    private readonly Dictionary<WidgetId, ToggleButton> _toggles = new();

    private WidgetBackdropWindow? _backdrop;
    private WidgetLauncherWindow? _launcher;
    private bool _boardOpen;
    private bool _suppressToggle;
    private System.Windows.Threading.DispatcherTimer? _opacitySaveTimer;

    private System.Windows.Threading.DispatcherTimer CreateOpacitySaveTimer()
    {
        var t = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        t.Tick += (_, _) => { t.Stop(); _host.Settings.Save(); };
        return t;
    }

    private CrosshairOverlayWindow? _crosshair;

    public WidgetHost(AppHost host)
    {
        _host = host;
        L.LanguageChanged += OnLanguageChanged;
        _host.CrosshairRefresh = ApplyCrosshair;
    }

    // ---- crosshair overlay ----

    /// <summary>
    /// Renders the active crosshair PNG with the current image settings and shows or
    /// hides the overlay. Called on every settings change and on the Ctrl+Alt+C toggle.
    /// The overlay is NOT part of <see cref="HideDuringCapture"/> — a crosshair is part
    /// of what the user aims with, so it stays in their own clips/screenshots.
    /// </summary>
    public void ApplyCrosshair()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(ApplyCrosshair));
            return;
        }

        var s = _host.Settings.Current.Crosshair;
        var entry = _host.Crosshairs.GetById(s.ActiveId);

        // Keep the config widget's checkbox in sync (hotkey toggles bypass the UI).
        if (_windows.TryGetValue(WidgetId.Crosshair, out var cw) && cw.WidgetContent is CrosshairView cv)
            cv.SyncEnabled(s.Enabled);

        if (!s.Enabled || entry is null)
        {
            _crosshair?.Hide();
            return;
        }

        var image = CrosshairImage.Render(_host.Crosshairs.FullPath(entry), s);
        if (image is null)
        {
            _crosshair?.Hide();
            Logger.Warn("Crosshair image could not be rendered — overlay hidden.");
            return;
        }

        if (_crosshair is null)
        {
            _crosshair = new CrosshairOverlayWindow();
            _crosshair.Moved += (cx, cy) =>
            {
                var cs = _host.Settings.Current.Crosshair;
                cs.CenterX = cx; cs.CenterY = cy;
                _host.Settings.Save();
            };
        }

        _crosshair.Apply(image, s);
        _crosshair.SetInteractive(_boardOpen);   // draggable only while the board is up
        if (!_crosshair.IsVisible) _crosshair.Show();
        // Re-center once the window has actually measured itself (SizeToContent).
        _crosshair.CenterOn(s);
        BumpTopmost(_crosshair);
    }

    /// <summary>Ctrl+Alt+C: flip the crosshair on/off and persist.</summary>
    public bool ToggleCrosshair()
    {
        var s = _host.Settings.Current.Crosshair;
        s.Enabled = !s.Enabled;
        _host.Settings.Save();
        ApplyCrosshair();
        return s.Enabled;
    }

    private void OnLanguageChanged()
        => Application.Current.Dispatcher.BeginInvoke(new Action(RebuildForLanguageChange));

    /// <summary>
    /// Widget windows and the launcher build their (localized) content once. On a
    /// language flip, tear them all down and rebuild so the new language shows
    /// everywhere immediately — no app restart. Geometry/visibility survive via the
    /// persisted <see cref="WidgetState"/>s.
    /// </summary>
    private void RebuildForLanguageChange()
    {
        bool wasOpen = _boardOpen;
        foreach (var (id, w) in _windows) CaptureGeometry(id, w);

        foreach (var w in _windows.Values) { try { w.Close(); } catch { } }
        _windows.Clear();
        if (_launcher != null) { try { _launcher.Close(); } catch { } _launcher = null; }
        _toggles.Clear();
        if (_backdrop != null) { try { _backdrop.Close(); } catch { } _backdrop = null; }
        _boardOpen = false;

        if (wasOpen) OpenBoard();
        else RestorePinned();
        Logger.Info("Widgets rebuilt for language change.");
    }

    /// <summary>Order of widgets in the launcher; also the stagger order for default placement.</summary>
    private static readonly WidgetId[] Order =
        { WidgetId.Capture, WidgetId.Audio, WidgetId.Gallery, WidgetId.Performance, WidgetId.Crosshair,
          WidgetId.Streaming, WidgetId.Mixer, WidgetId.Preflight, WidgetId.Chat, WidgetId.Music,
          WidgetId.Settings };

    private WidgetSettings Settings => _host.Settings.Current.Widgets;

    private static string Label(WidgetId id) => id switch
    {
        WidgetId.Capture     => L.T("Aufnahme", "Capture"),
        WidgetId.Audio       => "Audio",
        WidgetId.Gallery     => L.T("Galerie", "Gallery"),
        WidgetId.Performance => L.T("Leistung", "Performance"),
        WidgetId.Crosshair   => L.T("Crosshair", "Crosshair"),
        WidgetId.Streaming   => "Streaming",
        WidgetId.Mixer       => L.T("Mixer", "Mixer"),
        WidgetId.Preflight   => L.T("Go Live", "Go Live"),
        WidgetId.Chat        => "Chat",
        WidgetId.Music       => L.T("Musik", "Music"),
        WidgetId.Settings    => L.T("Einstellungen", "Settings"),
        _                    => id.ToString()
    };

    private static FrameworkElement CreateContent(WidgetId id) => id switch
    {
        WidgetId.Capture     => new CaptureView(),
        WidgetId.Audio       => new AudioSettingsView(),
        WidgetId.Gallery     => new ClipsView(),
        WidgetId.Performance => new PerformanceView(),
        WidgetId.Crosshair   => new CrosshairView(),
        WidgetId.Streaming   => new StreamingView(),
        WidgetId.Mixer       => new MixerView(),
        WidgetId.Preflight   => new PreflightView(),
        WidgetId.Chat        => new ChatView(),
        WidgetId.Music       => new MusicView(),
        WidgetId.Settings    => new SettingsWidgetView(),
        _                    => new System.Windows.Controls.Control()
    };

    // ---- Public entry points ----

    public void ToggleBoard()
    {
        if (_boardOpen) CloseBoard();
        else OpenBoard();
    }

    public void OpenBoard()
    {
        var screen = WinForms.Screen.FromPoint(WinForms.Cursor.Position);
        _boardOpen = true;

        _backdrop ??= CreateBackdrop();
        _backdrop.ShowOn(screen);   // activates → briefly on top; widgets re-assert below

        int i = 0;
        foreach (var id in Order)
        {
            var st = Settings.GetOrAdd(id);
            if (st.Visible) ShowWidget(id, st, screen, i);
            SyncToggle(id, st.Visible);
            i++;
        }

        // Launcher last so the pill sits above the backdrop and widgets.
        EnsureLauncher();
        _launcher!.ShowAt(screen);
        BumpTopmost(_launcher);

        // Crosshair becomes draggable while the board is open.
        if (_crosshair is { IsVisible: true }) { _crosshair.SetInteractive(true); BumpTopmost(_crosshair); }
        Logger.Info("Widget board opened.");
    }

    public void CloseBoard()
    {
        // Persist live geometry before hiding.
        foreach (var (id, w) in _windows) CaptureGeometry(id, w);

        foreach (var (id, w) in _windows)
        {
            var st = Settings.GetOrAdd(id);
            if (st.Pinned && st.Visible)
            {
                w.SetBoardOpen(false);   // becomes a no-activate float over the game
            }
            else
            {
                w.Hide();
            }
        }

        _backdrop?.Hide();
        _launcher?.Hide();
        // Crosshair goes click-through so it never intercepts a shot in-game.
        _crosshair?.SetInteractive(false);
        _host.Settings.Save();
        _boardOpen = false;
        Logger.Info("Widget board closed.");
    }

    /// <summary>Show pinned widgets immediately at launch (board stays closed).</summary>
    public void RestorePinned()
    {
        foreach (var id in Order)
        {
            var st = Settings.GetOrAdd(id);
            if (st.Pinned && st.Visible)
            {
                ShowWidget(id, st, PrimaryScreen(), Array.IndexOf(Order, id));
                _windows[id].SetBoardOpen(false);
            }
        }
        // The crosshair is its own kind of "pinned": restore it if it was left on.
        ApplyCrosshair();
    }

    // ---- Widget window lifecycle ----

    private void ShowWidget(WidgetId id, WidgetState st, WinForms.Screen defaultScreen, int index)
    {
        var w = EnsureWindow(id);
        w.Width = st.Width > 0 ? st.Width : 360;
        w.Height = st.Height > 0 ? st.Height : 400;
        PositionWindow(w, st, defaultScreen, index);
        w.IsPinned = st.Pinned;
        w.ClickThroughWhenPinned = st.ClickThrough;
        w.SetConfiguredOpacity(st.Opacity);
        if (_boardOpen) w.SetBoardOpen(true);
        w.Show();
        if (_boardOpen) BumpTopmost(w); // re-assert above the just-activated backdrop
    }

    /// <summary>Crosshair glyph for the launcher pill: ring + four ticks + center dot.</summary>
    private static FrameworkElement MakeCrosshairGlyph()
    {
        var brush = (System.Windows.Media.Brush)Application.Current.FindResource("TextBrush");
        var root = new System.Windows.Controls.Grid { Width = 18, Height = 18 };

        root.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 12, Height = 12,
            Stroke = brush, StrokeThickness = 1.4,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        root.Children.Add(new System.Windows.Shapes.Path
        {
            Stroke = brush, StrokeThickness = 1.4,
            Data = System.Windows.Media.Geometry.Parse("M9,0 L9,4 M9,14 L9,18 M0,9 L4,9 M14,9 L18,9")
        });
        root.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 2.6, Height = 2.6, Fill = brush,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        return root;
    }

    /// <summary>Lift a topmost window above other topmost windows without stealing focus.</summary>
    private static void BumpTopmost(Window w)
    {
        w.Topmost = false;
        w.Topmost = true;
    }

    private WidgetWindow EnsureWindow(WidgetId id)
    {
        if (_windows.TryGetValue(id, out var existing)) return existing;

        var w = new WidgetWindow(id, Label(id), CreateContent(id));
        w.PinToggled += OnPinToggled;
        w.CloseRequested += OnWidgetClosed;
        w.GeometryChanged += ww => CaptureGeometry(id, ww);
        w.OpacityChanged += ww =>
        {
            // Slider drags fire per pixel — update in memory immediately, write the
            // settings file debounced.
            Settings.GetOrAdd(id).Opacity = ww.ConfiguredOpacity;
            _opacitySaveTimer ??= CreateOpacitySaveTimer();
            _opacitySaveTimer.Stop();
            _opacitySaveTimer.Start();
        };
        _windows[id] = w;
        return w;
    }

    private void PositionWindow(WidgetWindow w, WidgetState st, WinForms.Screen defaultScreen, int index)
    {
        if (WidgetLayout.HasValidGeometry(st.Width, st.Height) && (st.X != 0 || st.Y != 0))
        {
            var screen = ScreenByDeviceName(st.MonitorDeviceName) ?? ScreenContaining(st.X, st.Y) ?? defaultScreen;
            var wa = screen.WorkingArea;
            var (cx, cy) = WidgetLayout.Clamp(st.X, st.Y, w.Width, w.Height,
                wa.Left, wa.Top, wa.Right, wa.Bottom);
            w.Left = cx; w.Top = cy;
        }
        else
        {
            // Staggered default within the target monitor's work area.
            var wa = defaultScreen.WorkingArea;
            w.Left = wa.Left + 40 + index * 44;
            w.Top  = wa.Top + 40 + index * 44;
        }
    }

    private void CaptureGeometry(WidgetId id, WidgetWindow w)
    {
        if (!w.IsVisible) return;
        var st = Settings.GetOrAdd(id);
        st.X = w.Left; st.Y = w.Top; st.Width = w.Width; st.Height = w.Height;
        var center = new System.Drawing.Point((int)(w.Left + w.Width / 2), (int)(w.Top + w.Height / 2));
        try { st.MonitorDeviceName = WinForms.Screen.FromPoint(center).DeviceName; } catch { }
    }

    private void OnPinToggled(WidgetWindow w)
    {
        var st = Settings.GetOrAdd(w.Id);
        st.Pinned = w.IsPinned;
        _host.Settings.Save();
    }

    private void OnWidgetClosed(WidgetWindow w)
    {
        var st = Settings.GetOrAdd(w.Id);
        st.Visible = false;
        w.Hide();
        SyncToggle(w.Id, false);
        _host.Settings.Save();
    }

    // ---- Launcher ----

    private void EnsureLauncher()
    {
        if (_launcher != null) return;
        _launcher = new WidgetLauncherWindow();
        foreach (var id in Order)
        {
            var toggle = new ToggleButton
            {
                // The crosshair gets a glyph instead of a word — it's the one widget
                // whose job is obvious from its symbol.
                Content = id == WidgetId.Crosshair ? MakeCrosshairGlyph() : Label(id),
                ToolTip = Label(id),
                Margin = new Thickness(3, 0, 3, 0),
                Padding = new Thickness(10, 6, 10, 6),
                Cursor = System.Windows.Input.Cursors.Hand,
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextBrush"),
                Template = (System.Windows.Controls.ControlTemplate)Application.Current.FindResource("LauncherToggleTemplate")
            };
            var captured = id;
            toggle.Checked += (_, _) => { if (!_suppressToggle) SetWidgetVisible(captured, true); };
            toggle.Unchecked += (_, _) => { if (!_suppressToggle) SetWidgetVisible(captured, false); };
            _toggles[id] = toggle;
            _launcher.WidgetButtons.Children.Add(toggle);
        }
    }

    private void SetWidgetVisible(WidgetId id, bool visible)
    {
        var st = Settings.GetOrAdd(id);
        st.Visible = visible;

        if (visible)
        {
            var screen = _backdrop is { IsVisible: true }
                ? WinForms.Screen.FromPoint(new System.Drawing.Point((int)_backdrop.Left + 10, (int)_backdrop.Top + 10))
                : PrimaryScreen();
            ShowWidget(id, st, screen, Array.IndexOf(Order, id));
        }
        else if (_windows.TryGetValue(id, out var w))
        {
            w.Hide();
        }
        _host.Settings.Save();
    }

    /// <summary>Reflect visibility on the launcher toggle without re-triggering it.</summary>
    private void SyncToggle(WidgetId id, bool on)
    {
        if (!_toggles.TryGetValue(id, out var t) || t.IsChecked == on) return;
        _suppressToggle = true;
        t.IsChecked = on;
        _suppressToggle = false;
    }

    private WidgetBackdropWindow CreateBackdrop()
    {
        var b = new WidgetBackdropWindow();
        b.Dismissed += CloseBoard;
        return b;
    }

    // ---- Screen helpers (bounds treated as DIPs; app assumes 100% DPI like OverlayWindow) ----

    private static WinForms.Screen PrimaryScreen() => WinForms.Screen.PrimaryScreen ?? WinForms.Screen.AllScreens[0];

    private static WinForms.Screen? ScreenByDeviceName(string? name)
        => string.IsNullOrEmpty(name) ? null : WinForms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == name);

    private static WinForms.Screen? ScreenContaining(double x, double y)
    {
        try { return WinForms.Screen.FromPoint(new System.Drawing.Point((int)x, (int)y)); }
        catch { return null; }
    }

    /// <summary>
    /// Temporarily hides all visible overlay windows (widgets + board) so an OWN
    /// screenshot doesn't include them. Returns an IDisposable that restores them, or
    /// null if nothing was visible. Widgets stay capture-visible to external tools
    /// (Snipping Tool, OBS) — WDA_EXCLUDEFROMCAPTURE is intentionally not used.
    /// </summary>
    public IDisposable? HideDuringCapture()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
            return Application.Current.Dispatcher.Invoke(HideDuringCapture);

        var hidden = new List<Window>();
        void HideIfVisible(Window? w)
        {
            if (w is { IsVisible: true }) { w.Visibility = Visibility.Hidden; hidden.Add(w); }
        }
        foreach (var w in _windows.Values) HideIfVisible(w);
        HideIfVisible(_backdrop);
        HideIfVisible(_launcher);
        return hidden.Count == 0 ? null : new CaptureRestore(hidden);
    }

    private sealed class CaptureRestore : IDisposable
    {
        private readonly List<Window> _windows;
        private bool _done;
        public CaptureRestore(List<Window> windows) => _windows = windows;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            void Restore() { foreach (var w in _windows) w.Visibility = Visibility.Visible; }
            if (Application.Current.Dispatcher.CheckAccess()) Restore();
            else Application.Current.Dispatcher.Invoke(Restore);
        }
    }

    public void Dispose()
    {
        L.LanguageChanged -= OnLanguageChanged;
        _host.CrosshairRefresh = null;
        foreach (var w in _windows.Values) { try { w.Close(); } catch { } }
        try { _launcher?.Close(); } catch { }
        try { _backdrop?.Close(); } catch { }
        try { _crosshair?.Close(); } catch { }
    }
}
