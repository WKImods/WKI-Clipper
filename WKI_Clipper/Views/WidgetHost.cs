using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using WKI_Clipper.Models;
using WKI_Clipper.Native;
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

    private WidgetBackdropWindow? _backdrop;
    private WidgetSidebarWindow? _sidebar;
    private readonly Dictionary<WidgetId, SidebarEntry> _entries = new();
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
    /// <summary>Last known "an overlay must stay out of capture", to detect the flip that needs a capture-path switch.</summary>
    private bool? _lastExclusionRequired;

    /// <summary>Foreground window before the board opened — gets the focus back on close.</summary>
    private IntPtr _returnFocusTo;
    /// <summary>An external app (Spotify) was launched from the board and may hold the focus.</summary>
    private bool _externalLaunchDuringBoard;
    private int _whatsAppUnread;
    /// <summary>Monitor the board is open on (where newly toggled widgets appear).</summary>
    private WinForms.Screen? _boardScreen;
    private System.Windows.Controls.Primitives.ToggleButton? _snapToggle;
    private System.Windows.Controls.Primitives.ToggleButton? _overlapToggle;

    public WidgetHost(AppHost host)
    {
        _host = host;
        L.LanguageChanged += OnLanguageChanged;
        _host.CrosshairRefresh = ApplyCrosshair;
        _host.PerfOverlayRefresh = ApplyPerfOverlay;
        _displaySignature = DisplaySignature();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    // ---- crosshair overlay ----

    /// <summary>
    /// Renders the active crosshair PNG with the current image settings and shows or
    /// hides the overlay. Called on every settings change and on the Ctrl+Alt+C toggle.
    /// The overlay is NOT part of <see cref="HideDuringCapture"/>: it is excluded from
    /// capture permanently via WDA_EXCLUDEFROMCAPTURE instead, because F9 saves seconds
    /// that were recorded before the key was pressed.
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

        ReconcileCapturePath();

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

    /// <summary>
    /// AMD's own capture (vsrc_amf) ignores WDA_EXCLUDEFROMCAPTURE, so while the crosshair
    /// is on or a widget is marked capture-excluded (WhatsApp) the buffer must run on
    /// ddagrab. Re-arms the buffer only on an actual flip, and only when the AMF path is
    /// configured at all — otherwise the pipeline would not change and the restart is pure loss.
    /// </summary>
    private void ReconcileCapturePath()
    {
        var settings = _host.Settings.Current;
        bool required = FFmpegCommandBuilder.MustHonorCaptureExclusion(settings);
        if (_lastExclusionRequired is { } was && was != required && FFmpegCommandBuilder.AmfCaptureConfigured(settings))
        {
            Logger.Info($"Capture exclusion {(required ? "needed" : "no longer needed")} — restarting the buffer to switch capture path.");
            _host.ReplayBuffer.RequestRestart();
        }
        _lastExclusionRequired = required;
    }

    // ---- on-screen performance overlay ----

    private PerfOverlayWindow? _perfOverlay;
    private bool _perfOverlayViewing;

    /// <summary>
    /// Shows, updates or hides the corner performance readout from current settings. Like
    /// the crosshair it lives outside the board and stays up while playing.
    /// </summary>
    public void ApplyPerfOverlay()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(ApplyPerfOverlay));
            return;
        }

        var s = _host.Settings.Current.PerfOverlay;
        ReconcileCapturePath();   // a capture-hidden overlay rules AMF's own capture out
        if (!s.Enabled)
        {
            _perfOverlay?.Hide();
            if (_perfOverlayViewing)
            {
                _host.Performance.Sampled -= OnPerfSampleForOverlay;
                _host.Performance.RemoveViewer();
                _perfOverlayViewing = false;
            }
            return;
        }

        _perfOverlay ??= new PerfOverlayWindow();
        _perfOverlay.SetExcludedFromCapture(s.HideFromCapture);
        if (!_perfOverlayViewing)
        {
            _host.Performance.Sampled += OnPerfSampleForOverlay;
            _host.Performance.AddViewer();
            _perfOverlayViewing = true;
        }
        RenderPerfOverlay();
    }

    private void OnPerfSampleForOverlay(PerfSample _)
        => Application.Current?.Dispatcher.BeginInvoke(new Action(RenderPerfOverlay));

    private void RenderPerfOverlay()
    {
        if (_perfOverlay is null || !_perfOverlayViewing) return;
        var s = _host.Settings.Current.PerfOverlay;
        var lines = PerfOverlayLayout.Lines(s, _host.Performance.Last, _host.Performance.History(), DateTime.Now);
        double scale = PerfOverlayLayout.ClampScale(s.Scale);
        _perfOverlay.Render(lines, scale);
        bool justShown = !_perfOverlay.IsVisible;
        if (justShown) _perfOverlay.Show();
        _perfOverlay.UpdateLayout();

        // Corner of the primary monitor's full area — in a fullscreen game that IS the corner.
        var screen = PrimaryScreen();
        var bounds = DisplayGeometry.Bounds(screen);
        double dpi = DisplayGeometry.ScaleOf(screen);
        var (x, y) = PerfOverlayLayout.Place(s.Corner, _perfOverlay.ActualWidth * dpi, _perfOverlay.ActualHeight * dpi,
                                             bounds, 10 * dpi);
        if (DisplayGeometry.BoundsOf(_perfOverlay) is not { } cur || Math.Abs(cur.X - x) > 0.5 || Math.Abs(cur.Y - y) > 0.5)
            DisplayGeometry.MoveTo(_perfOverlay, x, y);
        // Only when it appears — re-asserting z-order every second would churn the desktop.
        if (justShown) BumpTopmost(_perfOverlay);
    }

    /// <summary>Hotkey: overlay on/off and persist.</summary>
    public bool TogglePerfOverlay()
    {
        var s = _host.Settings.Current.PerfOverlay;
        s.Enabled = !s.Enabled;
        _host.Settings.Save();
        ApplyPerfOverlay();
        return s.Enabled;
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
    ///
    /// Web widgets are the exception: rebuilding would throw away the page (and with it
    /// an open WhatsApp chat or a half-typed message). They only get new labels.
    /// </summary>
    private void RebuildForLanguageChange()
    {
        bool wasOpen = _boardOpen;
        foreach (var (id, w) in _windows) CaptureGeometry(id, w);

        foreach (var (id, w) in _windows.ToList())
        {
            if (w.WidgetContent is IWebWidget web)
            {
                w.SetTitle(Label(id));
                web.Relocalize();
                continue;
            }
            try { w.Close(); } catch { }
            _windows.Remove(id);
        }
        if (_sidebar != null) { try { _sidebar.Close(); } catch { } _sidebar = null; }
        _entries.Clear();
        if (_backdrop != null) { try { _backdrop.Close(); } catch { } _backdrop = null; }
        _boardOpen = false;

        // OpenBoard records "where the keyboard was" — during a rebuild that would be one
        // of our own windows, so keep the game the user actually came from.
        var returnFocusTo = _returnFocusTo;
        bool externalLaunch = _externalLaunchDuringBoard;
        if (wasOpen) OpenBoard();
        else RestorePinned();
        _returnFocusTo = returnFocusTo;
        _externalLaunchDuringBoard = externalLaunch;
        Logger.Info("Widgets rebuilt for language change.");
    }

    /// <summary>Sidebar order; also the stagger order for default placement.</summary>
    private static readonly IReadOnlyList<WidgetId> Order = WidgetCatalog.Order;

    private WidgetSettings Settings => _host.Settings.Current.Widgets;

    private static string Label(WidgetId id) => WidgetCatalog.Label(id);

    private static int IndexOf(WidgetId id)
    {
        for (int i = 0; i < Order.Count; i++) if (Order[i] == id) return i;
        return 0;
    }

    private static FrameworkElement CreateContent(WidgetId id) => id switch
    {
        WidgetId.Capture     => new CaptureView(),
        WidgetId.Audio       => new AudioSettingsView(),
        WidgetId.Gallery     => new ClipsView(),
        WidgetId.Performance => new PerformanceView(),
        WidgetId.Crosshair   => new CrosshairView(),
        WidgetId.Streaming   => new StreamingView(),
        WidgetId.Mixer       => new MixerView(),
        WidgetId.Sources     => new SourcesView(),
        WidgetId.Preflight   => new PreflightView(),
        WidgetId.Chat        => new ChatView(),
        WidgetId.Music       => new MusicView(),
        WidgetId.Spotify     => new SpotifyView(),
        WidgetId.WhatsApp    => new WhatsAppView(),
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
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var screen = WinForms.Screen.FromPoint(WinForms.Cursor.Position);
        // Remember where the keyboard was (normally the game) before the backdrop takes it.
        if (!_boardOpen)
        {
            var fg = User32.GetForegroundWindow();
            _returnFocusTo = IsOwnWindow(fg) ? IntPtr.Zero : fg;
            _externalLaunchDuringBoard = false;
        }
        _boardOpen = true;
        _boardScreen = screen;

        _backdrop ??= CreateBackdrop();
        _backdrop.ShowOn(screen);   // activates → briefly on top; widgets re-assert below

        // Sidebar before the widgets: it is an obstacle the grid has to know about.
        EnsureSidebar();
        _sidebar!.ShowOn(screen);

        int i = 0;
        foreach (var id in Order)
        {
            var st = Settings.GetOrAdd(id);
            if (st.Visible) ShowWidget(id, st, screen, i);
            SyncToggle(id, st.Visible);
            i++;
        }
        KeepWidgetsClearOfSidebar();
        BumpTopmost(_sidebar);

        // Crosshair becomes draggable while the board is open.
        if (_crosshair is { IsVisible: true }) { _crosshair.SetInteractive(true); BumpTopmost(_crosshair); }
        // The duration is the whole point of the prewarm work — keep it measurable.
        Logger.Info($"Widget board opened in {sw.ElapsedMilliseconds} ms.");
    }

    /// <summary>
    /// Builds every widget window once, far off-screen, so the FIRST real board open only
    /// has to make already-built windows visible. The cost being moved here is real: XAML
    /// inflation and first-use JIT per view, audio device enumeration, performance-counter
    /// setup, the gallery scan. After a close the windows are only ever hidden — which is
    /// exactly why the SECOND open was always fast. Prewarming reaches that state before
    /// the user asks for it.
    ///
    /// Safe by construction: windows show at -20000/-20000 with ShowActivated=false, so
    /// nothing is visible and nothing takes focus; GeometryChanged fires only on user
    /// drags/deactivation, so stored positions are untouched, and the real open re-places
    /// every window via PositionWindow anyway.
    /// </summary>
    public async Task PrewarmAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int built = 0;
        foreach (var id in Order)
        {
            // The user beat us to it — everything left will be built visibly right now.
            if (_boardOpen) { Logger.Info("Widget prewarm stopped — board opened by the user."); return; }
            if (_windows.ContainsKey(id)) continue;   // pinned widgets already exist

            try
            {
                var w = EnsureWindow(id);
                w.Left = -20000;
                w.Top = -20000;
                w.Show();     // raises Loaded → the view runs its init work off-screen
                // Let render + the view's Loaded handlers settle before hiding.
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.ContextIdle);
                w.Hide();
                built++;
            }
            catch (Exception ex) { Logger.Warn($"Prewarm of {id} failed: {ex.Message}"); }

            // One window at a time with breathing room — startup must stay responsive.
            await Task.Delay(120);
        }

        // Backdrop + sidebar are part of the first-open cost too; construct them now.
        try { _backdrop ??= CreateBackdrop(); EnsureSidebar(); } catch { }

        Logger.Info($"Widget prewarm done: {built} windows built in {sw.ElapsedMilliseconds} ms.");
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
        _sidebar?.Hide();
        // Crosshair goes click-through so it never intercepts a shot in-game.
        _crosshair?.SetInteractive(false);
        _host.Settings.Save();
        _boardOpen = false;
        _boardScreen = null;
        ReturnFocus();
        Logger.Info("Widget board closed.");
    }

    /// <summary>
    /// Gives the keyboard back to the window the user came from. Without this a pinned
    /// widget that was typed into (WhatsApp, a settings field) stays the foreground
    /// window after the board closes — and WASD lands in the chat instead of the game.
    /// Only acts while the focus is still on the board (or on Spotify we just launched), so
    /// a window the user deliberately switched to — including our own sign-in popup — is
    /// left alone.
    /// </summary>
    private void ReturnFocus()
    {
        System.Windows.Input.Keyboard.ClearFocus();
        var target = _returnFocusTo;
        _returnFocusTo = IntPtr.Zero;
        if (target == IntPtr.Zero || !User32.IsWindow(target)) return;

        var fg = User32.GetForegroundWindow();
        if (fg == target) return;
        bool onBoard = fg == IntPtr.Zero || IsBoardWindow(fg);
        if (!onBoard && !(_externalLaunchDuringBoard && !IsOwnWindow(fg))) return;
        if (!User32.SetForegroundWindow(target))
            Logger.Info("Board closed: focus could not be returned to the previous window.");
    }

    /// <summary>A widget window, the dim backdrop or the launcher pill.</summary>
    private bool IsBoardWindow(IntPtr hwnd)
    {
        bool Is(Window? w) => w != null && new System.Windows.Interop.WindowInteropHelper(w).Handle == hwnd;
        if (Is(_backdrop) || Is(_sidebar)) return true;
        foreach (var w in _windows.Values) if (Is(w)) return true;
        return false;
    }

    private static bool IsOwnWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        User32.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// <summary>Show pinned widgets immediately at launch (board stays closed).</summary>
    public void RestorePinned()
    {
        foreach (var id in Order)
        {
            var st = Settings.GetOrAdd(id);
            if (st.Pinned && st.Visible)
            {
                // A pinned web widget's browser boots a few seconds later, so it does not
                // compete with the replay buffer's ffmpeg spin-up at app start.
                ShowWidget(id, st, PrimaryScreen(), IndexOf(id), startWebDelayed: true);
                _windows[id].SetBoardOpen(false);
            }
        }
        // The crosshair and the performance overlay are their own kind of "pinned":
        // restore them if they were left on.
        ApplyCrosshair();
        ApplyPerfOverlay();
    }

    // ---- Widget window lifecycle ----

    private void ShowWidget(WidgetId id, WidgetState st, WinForms.Screen defaultScreen, int index,
                            bool startWebDelayed = false)
    {
        var w = EnsureWindow(id);
        // Sizes are DIPs: the content scales with Windows' display scaling.
        w.Width = st.Width > 0 ? st.Width : 360;
        w.Height = st.Height > 0 ? st.Height : 400;
        w.IsPinned = st.Pinned;
        w.ClickThroughWhenPinned = st.ClickThrough;
        w.ExcludeFromCapture = st.ExcludeFromCapture;
        w.SetConfiguredOpacity(st.Opacity);
        PositionWindow(w, st, defaultScreen, index);   // before Show: no flash at the old spot
        if (_boardOpen) w.SetBoardOpen(true);
        w.Show();
        if (_boardOpen) BumpTopmost(w); // re-assert above the just-activated backdrop
        if (KeepApart) MoveToFreeSpot(w, userMove: false);

        // Web widgets boot their browser on the first REAL show — the prewarm path shows
        // windows off-screen without coming through here.
        if (w.WidgetContent is IWebWidget web)
        {
            if (!startWebDelayed) web.EnsureStarted();
            else
            {
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                timer.Tick += (_, _) => { timer.Stop(); web.EnsureStarted(); };
                timer.Start();
            }
        }
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

        // WhatsApp renders as a real child window (see WebAppHost) — its widget window must
        // not be per-pixel transparent, or that child would not show.
        bool direct = id == WidgetId.WhatsApp && WebAppHost.UsesDirectRendering(WebApp.WhatsApp);
        var w = new WidgetWindow(id, Label(id), CreateContent(id), directRendering: direct);
        w.SetIcon(IconGlyph.ForWidget(id, 15, (Brush)Application.Current.FindResource("AccentBrush")));
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

        ApplySnapHooks(w);
        w.DragFinished += ww => { if (KeepApart) MoveToFreeSpot(ww, userMove: true); };
        w.ResizeFinished += ww => { if (KeepApart) MakeRoomAround(ww); CaptureGeometry(id, ww); };
        // Each window gets a DPI message when the scaling changes; the debounced
        // signature check turns that burst into one re-layout.
        w.DpiChanged += (_, _) => ScheduleDisplayCheck();

        if (w.WidgetContent is IWebWidget)
            w.ContentPadding = new Thickness(6);   // a web page wants every pixel
        if (w.WidgetContent is WhatsAppView wa)
        {
            wa.UnreadChanged += n => Application.Current.Dispatcher.BeginInvoke(new Action(() => SetWhatsAppUnread(n)));
            wa.CaptureExclusionChanged += on => SetCaptureExclusion(WidgetId.WhatsApp, on);
        }
        if (w.WidgetContent is SpotifyView sv)
        {
            sv.ViewModeChanged += full => ApplySpotifyViewSize(w, full);
            sv.ExternalAppLaunched += () => _externalLaunchDuringBoard = true;
        }

        _windows[id] = w;
        return w;
    }

    /// <summary>Flips "hidden from stream/clips" for a widget — live, no rebuild.</summary>
    private void SetCaptureExclusion(WidgetId id, bool excluded)
    {
        var st = Settings.GetOrAdd(id);
        st.ExcludeFromCapture = excluded;
        if (_windows.TryGetValue(id, out var w)) w.ExcludeFromCapture = excluded;
        _host.Settings.Save();
        Logger.Info($"Widget {id}: {(excluded ? "hidden from" : "visible in")} screen capture.");
        ReconcileCapturePath();
    }

    /// <summary>
    /// Compact and full Spotify view each keep their own window size: the size of the
    /// view being left is stored, the other one applied (clamped onto the screen).
    /// </summary>
    private void ApplySpotifyViewSize(WidgetWindow w, bool full)
    {
        var sp = _host.Settings.Current.Spotify;
        double curW = w.ActualWidth > 0 ? w.ActualWidth : w.Width, curH = w.ActualHeight > 0 ? w.ActualHeight : w.Height;
        if (full) { sp.CompactWidth = curW; sp.CompactHeight = curH; }
        else      { sp.FullWidth = curW;    sp.FullHeight = curH; }

        double width = full ? sp.FullWidth : sp.CompactWidth;
        double height = full ? sp.FullHeight : sp.CompactHeight;
        w.Width = Math.Max(w.MinWidth, width);
        w.Height = Math.Max(w.MinHeight, height);

        // Keep the grown window on its screen.
        if (DisplayGeometry.BoundsOf(w) is { } b)
        {
            var screen = DisplayGeometry.ScreenOf(b);
            var area = DisplayGeometry.WorkArea(screen);
            double margin = EdgeMarginDip * DisplayGeometry.ScaleOf(screen);
            var (x, y) = WidgetLayout.Clamp(b.X, b.Y, b.W, b.H, area.X, area.Y, area.Right, area.Bottom, margin);
            if (Math.Abs(x - b.X) > 0.5 || Math.Abs(y - b.Y) > 0.5) DisplayGeometry.MoveTo(w, x, y);
        }
        if (KeepApart) MakeRoomAround(w);
        CaptureGeometry(WidgetId.Spotify, w);
        _host.Settings.Save();
    }

    // ---- WhatsApp unread badge ----

    private void SetWhatsAppUnread(int count)
    {
        _whatsAppUnread = Math.Max(0, count);
        ApplyBadges();
    }

    /// <summary>
    /// Count on the WhatsApp row of the sidebar (a pill; a dot when collapsed). Deliberately
    /// the only signal: the sidebar is visible only with the board open, so nothing ever
    /// pops over the game or the stream. Re-applied after every sidebar rebuild.
    /// </summary>
    private void ApplyBadges()
    {
        if (_entries.TryGetValue(WidgetId.WhatsApp, out var entry)) entry.SetBadge(_whatsAppUnread);
    }

    /// <summary>
    /// Puts a widget where the user left it, in physical pixels of its monitor. The place
    /// is stored relative to the work area (<see cref="WidgetState.RelX"/>), so it survives
    /// a change of display scaling or resolution: top-right stays top-right, a window that
    /// grew with the scaling is clamped back onto the screen.
    /// </summary>
    private void PositionWindow(WidgetWindow w, WidgetState st, WinForms.Screen defaultScreen, int index)
    {
        bool legacy = st.RelX is null && WidgetLayout.HasValidGeometry(st.Width, st.Height) && (st.X != 0 || st.Y != 0);
        var screen = ScreenByDeviceName(st.MonitorDeviceName)
                     ?? (legacy ? DisplayGeometry.ScreenAt(st.X, st.Y) : defaultScreen);
        double s = DisplayGeometry.ScaleOf(screen);
        var area = DisplayGeometry.WorkArea(screen);
        double pw = w.Width * s, ph = w.Height * s, margin = EdgeMarginDip * s;

        double x, y;
        if (st.RelX is double rx && st.RelY is double ry)
            (x, y) = WidgetLayout.FromRelative(rx, ry, pw, ph, area, margin);
        else if (legacy)
            // Saved before v0.13: X/Y came from WPF at the old 100 % assumption, i.e. pixels.
            (x, y) = WidgetLayout.Clamp(st.X, st.Y, pw, ph, area.X, area.Y, area.Right, area.Bottom, margin);
        else
            // Never placed: staggered default within the monitor's work area.
            // Right of the (expanded) sidebar, staggered.
            (x, y) = WidgetLayout.Clamp(area.X + (WidgetSidebarWindow.ExpandedWidth + 40 + index * 44) * s, area.Y + (40 + index * 44) * s,
                                        pw, ph, area.X, area.Y, area.Right, area.Bottom, margin);
        DisplayGeometry.MoveTo(w, x, y);
        RememberSystemPlacement(w);
    }

    /// <summary>
    /// Where the APP last put each window (restore, clamp, re-layout after a scaling
    /// change). Saving such a spot back as the user's choice would be lossy: a window
    /// clamped inward at 150 % would come back 100+ px off the edge at 100 %. Only a
    /// window that has moved since — i.e. the user moved or resized it — is saved.
    /// </summary>
    private readonly Dictionary<WidgetId, LayoutRect> _systemPlaced = new();

    private void RememberSystemPlacement(WidgetWindow w)
    {
        if (DisplayGeometry.BoundsOf(w) is { } b) _systemPlaced[w.Id] = b;
    }

    private void CaptureGeometry(WidgetId id, WidgetWindow w)
    {
        if (!w.IsVisible || DisplayGeometry.BoundsOf(w) is not { } b) return;
        if (b.X < -10000 || b.Y < -10000) return;   // prewarm parking spot, never a user choice
        if (_systemPlaced.TryGetValue(id, out var placed) && placed == b) return;   // untouched since we placed it
        _systemPlaced.Remove(id);
        var st = Settings.GetOrAdd(id);
        var screen = DisplayGeometry.ScreenOf(b);
        var (rx, ry) = WidgetLayout.ToRelative(b, DisplayGeometry.WorkArea(screen));
        st.RelX = rx;
        st.RelY = ry;
        st.X = b.X;
        st.Y = b.Y;
        st.Width = w.ActualWidth > 0 ? w.ActualWidth : w.Width;
        st.Height = w.ActualHeight > 0 ? w.ActualHeight : w.Height;
        st.MonitorDeviceName = screen.DeviceName;
    }

    // ---- Grid & snapping ("Raster" switch in the launcher) ----
    //
    // All geometry here is in physical pixels of one monitor; the DIP constants are scaled
    // with that monitor's display scaling so the grid feels the same at 100 and 150 %.

    private const double GridDip = 16, GapDip = 8, SnapDistanceDip = 14, EdgeMarginDip = 8;

    private bool SnapOn => Settings.Snap;

    /// <summary>Grid on and stacking not allowed: widgets step aside instead of overlapping.</summary>
    private bool KeepApart => Settings.Snap && !Settings.AllowOverlap;

    private void ApplySnapHooks(WidgetWindow w)
    {
        w.MoveSnapper = SnapOn ? SnapWhileMoving : null;
        w.ResizeSnapper = SnapOn ? SnapWhileResizing : null;
    }

    private void SetSnap(bool on)
    {
        if (Settings.Snap == on) return;
        Settings.Snap = on;
        foreach (var w in _windows.Values) ApplySnapHooks(w);
        if (on) ArrangeVisible(snapToGrid: true, persist: true);
        _host.Settings.Save();
        Logger.Info($"Widget grid {(on ? "on — widgets arranged" : "off")}.");
    }

    /// <summary>
    /// "Überlappen" switch. Allowing it leaves every window where it is; forbidding it
    /// again (grid on) tidies existing overlaps away right away.
    /// </summary>
    private void SetAllowOverlap(bool allow)
    {
        if (Settings.AllowOverlap == allow) return;
        Settings.AllowOverlap = allow;
        if (!allow && SnapOn) ArrangeVisible(snapToGrid: false, persist: true);
        _host.Settings.Save();
        Logger.Info($"Widget overlap {(allow ? "allowed (stacking)" : "prevented")}.");
    }

    /// <summary>Visible widgets (and the sidebar) on the monitor <paramref name="area"/> belongs to.</summary>
    private List<LayoutRect> ObstaclesOn(LayoutRect area, WidgetWindow? except)
    {
        var list = new List<LayoutRect>();
        foreach (var w in _windows.Values)
        {
            if (w == except || !w.IsVisible || DisplayGeometry.BoundsOf(w) is not { } b) continue;
            if (b.Overlaps(area)) list.Add(b);
        }
        if (SidebarRect() is { } sb && sb.Overlaps(area)) list.Add(sb);
        return list;
    }

    /// <summary>The sidebar's on-screen rectangle while the board is open.</summary>
    private LayoutRect? SidebarRect()
        => _sidebar is { IsVisible: true } ? DisplayGeometry.BoundsOf(_sidebar) : null;

    /// <summary>
    /// Widgets left under where the sidebar now sits (layouts from before v0.15, or a
    /// sidebar that was just expanded) move to its right edge. Saved, so it happens once.
    /// </summary>
    private void KeepWidgetsClearOfSidebar()
    {
        if (SidebarRect() is not { } sb) return;
        var screen = DisplayGeometry.ScreenOf(sb);
        double s = DisplayGeometry.ScaleOf(screen);
        var area = DisplayGeometry.WorkArea(screen);
        foreach (var w in _windows.Values)
        {
            if (!w.IsVisible || DisplayGeometry.BoundsOf(w) is not { } b || !b.Overlaps(sb)) continue;
            var (x, y) = WidgetLayout.Clamp(sb.Right + GapDip * s, b.Y, b.W, b.H, area.X, area.Y, area.Right, area.Bottom, EdgeMarginDip * s);
            DisplayGeometry.MoveTo(w, x, y);
            CaptureGeometry(w.Id, w);
        }
        if (KeepApart) ArrangeVisible(snapToGrid: false, persist: true);
    }

    private LayoutRect SnapWhileMoving(WidgetWindow w, LayoutRect proposed)
    {
        // Shift held = place freely, like in most editors.
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0) return proposed;
        var screen = DisplayGeometry.ScreenOf(proposed);
        double s = DisplayGeometry.ScaleOf(screen);
        var area = DisplayGeometry.WorkArea(screen);
        var (x, y) = WidgetLayout.SnapMove(proposed, ObstaclesOn(area, w), area, GridDip * s, SnapDistanceDip * s, GapDip * s);
        return proposed.At(x, y);
    }

    private (double W, double H) SnapWhileResizing(WidgetWindow w, LayoutRect proposed)
    {
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0) return (proposed.W, proposed.H);
        var screen = DisplayGeometry.ScreenOf(proposed);
        double s = DisplayGeometry.ScaleOf(screen);
        var area = DisplayGeometry.WorkArea(screen);
        return WidgetLayout.SnapResize(proposed, ObstaclesOn(area, w), area, GridDip * s, SnapDistanceDip * s, GapDip * s,
                                       w.MinWidth * s, w.MinHeight * s);
    }

    /// <summary>
    /// A dropped or newly shown widget that overlaps another moves to the nearest free spot.
    /// <paramref name="userMove"/>: the user dropped it there (save the result) — otherwise
    /// the app is just showing it, and the stored place stays the user's.
    /// </summary>
    private void MoveToFreeSpot(WidgetWindow w, bool userMove)
    {
        if (DisplayGeometry.BoundsOf(w) is not { } b) return;
        var screen = DisplayGeometry.ScreenOf(b);
        double gap = GapDip * DisplayGeometry.ScaleOf(screen);
        var area = DisplayGeometry.WorkArea(screen);
        var obstacles = ObstaclesOn(area, w);
        if (obstacles.Exists(o => b.Overlaps(o, gap))
            && WidgetLayout.FindFreeSpot(b, obstacles, area, gap) is { } spot)   // null = screen full: leave it
            DisplayGeometry.MoveTo(w, spot.X, spot.Y);
        if (userMove) CaptureGeometry(w.Id, w);
        else RememberSystemPlacement(w);
    }

    /// <summary>A widget grew (resize grip, Spotify view switch): neighbours in the way step aside.</summary>
    private void MakeRoomAround(WidgetWindow grown)
    {
        if (DisplayGeometry.BoundsOf(grown) is not { } b) return;
        var fixedObstacles = new List<LayoutRect> { b };
        if (SidebarRect() is { } sb) fixedObstacles.Add(sb);
        RearrangeOn(DisplayGeometry.ScreenOf(b), except: grown, snapToGrid: false, fixedObstacles, persist: true);
    }

    /// <summary>
    /// Tidies every visible widget, per monitor. <paramref name="persist"/>: the user asked
    /// for it (switch turned on) — false after a display change, where the tidy layout is
    /// only a consequence of the new scaling and must not replace the user's places.
    /// </summary>
    private void ArrangeVisible(bool snapToGrid, bool persist)
    {
        var screens = new Dictionary<string, WinForms.Screen>();
        foreach (var w in _windows.Values)
            if (w.IsVisible && DisplayGeometry.BoundsOf(w) is { } b)
            {
                var sc = DisplayGeometry.ScreenOf(b);
                screens[sc.DeviceName] = sc;
            }
        foreach (var screen in screens.Values)
        {
            var fixedObstacles = new List<LayoutRect>();
            if (SidebarRect() is { } sb) fixedObstacles.Add(sb);
            RearrangeOn(screen, except: null, snapToGrid, fixedObstacles, persist);
        }
    }

    private void RearrangeOn(WinForms.Screen screen, WidgetWindow? except, bool snapToGrid,
                             List<LayoutRect> fixedObstacles, bool persist)
    {
        double s = DisplayGeometry.ScaleOf(screen);
        var area = DisplayGeometry.WorkArea(screen);
        var windows = new List<WidgetWindow>();
        var rects = new List<LayoutRect>();
        foreach (var w in _windows.Values)
        {
            if (w == except || !w.IsVisible || DisplayGeometry.BoundsOf(w) is not { } b) continue;
            if (DisplayGeometry.ScreenOf(b).DeviceName != screen.DeviceName) continue;
            windows.Add(w);
            rects.Add(b);
        }
        if (windows.Count == 0) return;

        double minW = windows.Min(w => w.MinWidth) * s, minH = windows.Min(w => w.MinHeight) * s;
        var arranged = WidgetLayout.Arrange(rects, area, GridDip * s, GapDip * s, minW, minH, snapToGrid, fixedObstacles,
                                            resolveOverlaps: !Settings.AllowOverlap);
        for (int i = 0; i < windows.Count; i++)
        {
            var w = windows[i];
            var r = arranged[i];
            if (Math.Abs(r.W - rects[i].W) > 0.5 || Math.Abs(r.H - rects[i].H) > 0.5)
            {
                w.Width = Math.Max(w.MinWidth, r.W / s);
                w.Height = Math.Max(w.MinHeight, r.H / s);
            }
            if (Math.Abs(r.X - rects[i].X) > 0.5 || Math.Abs(r.Y - rects[i].Y) > 0.5)
                DisplayGeometry.MoveTo(w, r.X, r.Y);
            if (persist) CaptureGeometry(w.Id, w);
            else RememberSystemPlacement(w);
        }
    }

    // ---- Display changes (scaling 100 → 150 %, resolution, monitors, taskbar) ----

    private string _displaySignature = "";
    private System.Windows.Threading.DispatcherTimer? _displayTimer;

    /// <summary>Monitor bounds, work areas and scale factors — what the overlay layout depends on.</summary>
    private static string DisplaySignature()
        => string.Join("|", WinForms.Screen.AllScreens.Select(s =>
            $"{s.DeviceName}:{s.Bounds}:{s.WorkingArea}:{DisplayGeometry.ScaleOf(s):0.###}"));

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        => Application.Current?.Dispatcher.BeginInvoke(new Action(ScheduleDisplayCheck));

    /// <summary>
    /// Debounced: scaling changes arrive as a burst (one DPI message per window, plus the
    /// system event). The signature comparison ignores a window merely dragged onto
    /// another monitor — only a real change of the displays re-lays the overlay out.
    /// </summary>
    private void ScheduleDisplayCheck()
    {
        if (_displayTimer is null)
        {
            _displayTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _displayTimer.Tick += (_, _) =>
            {
                _displayTimer.Stop();
                var sig = DisplaySignature();
                if (sig == _displaySignature) return;
                _displaySignature = sig;
                RelayoutForDisplayChange();
            };
        }
        _displayTimer.Stop();
        _displayTimer.Start();
    }

    private void RelayoutForDisplayChange()
    {
        Logger.Info("Display layout changed (scaling/resolution/monitors) — re-placing the overlay.");
        // Screen objects are snapshots: re-resolve the board's monitor from the fresh list
        // (it may have changed resolution or been unplugged).
        if (_boardScreen != null) _boardScreen = ScreenByDeviceName(_boardScreen.DeviceName) ?? PrimaryScreen();
        int index = 0;
        foreach (var id in Order)
        {
            if (_windows.TryGetValue(id, out var w) && w.IsVisible)
            {
                var st = Settings.GetOrAdd(id);
                w.Width = st.Width > 0 ? st.Width : w.Width;
                w.Height = st.Height > 0 ? st.Height : w.Height;
                PositionWindow(w, st, _boardScreen ?? PrimaryScreen(), index);
            }
            index++;
        }
        if (_boardOpen)
        {
            var screen = _boardScreen ?? PrimaryScreen();
            _backdrop?.Cover(screen);
            _sidebar?.Reposition(screen);
        }
        if (KeepApart) ArrangeVisible(snapToGrid: false, persist: false);
        ApplyCrosshair();
        ApplyPerfOverlay();
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

    // ---- Sidebar ----

    /// <summary>
    /// Builds the board's sidebar once: sections from <see cref="WidgetCatalog"/>, a toggle
    /// row per widget, the layout switches and Settings in the footer.
    /// </summary>
    private void EnsureSidebar()
    {
        if (_sidebar != null) return;
        _sidebar = new WidgetSidebarWindow(Settings.SidebarCollapsed) { StatusProvider = BoardStatus };
        _sidebar.CollapseToggled += collapsed =>
        {
            Settings.SidebarCollapsed = collapsed;
            _sidebar!.SetCollapsed(collapsed);
            _host.Settings.Save();
            // Expanding can put the sidebar on top of widgets next to it.
            if (_boardOpen) KeepWidgetsClearOfSidebar();
        };

        var textBrush = (Brush)Application.Current.FindResource("TextBrush");
        foreach (var (section, widgets) in WidgetCatalog.Sections)
        {
            _sidebar.AddSectionHeader(WidgetCatalog.SectionTitle(section));
            foreach (var id in widgets) WireEntry(id, _sidebar.AddEntry(IconGlyph.ForWidget(id, 15, textBrush), Label(id)));
        }

        // Footer: the layout switches, then Settings (a widget like the others).
        _snapToggle = _sidebar.AddFooterChip(IconGlyph.Make(IconGlyph.Grid, 13), L.T("Raster", "Grid"));
        _snapToggle.IsChecked = SnapOn;
        _snapToggle.ToolTip = L.T("Raster an: Fenster rasten am Raster und aneinander ein. Beim Einschalten wird alles "
                                  + "ordentlich angeordnet. Shift halten = frei verschieben.",
                                  "Grid on: windows snap to the grid and to each other. Turning it on tidies "
                                  + "everything up. Hold Shift to move freely.");
        _snapToggle.Checked += (_, _) => { SetSnap(true); UpdateOverlapToggle(); };
        _snapToggle.Unchecked += (_, _) => { SetSnap(false); UpdateOverlapToggle(); };

        _overlapToggle = _sidebar.AddFooterChip(IconGlyph.Make(IconGlyph.Stack, 13), L.T("Überlappen", "Overlap"));
        _overlapToggle.IsChecked = Settings.AllowOverlap;
        _overlapToggle.ToolTip = L.T("Aus: Fenster weichen einander aus und überlappen sich nie. "
                                     + "An: Fenster dürfen übereinander liegen und gestapelt werden — Raster und Einrasten bleiben aktiv. "
                                     + "Nur mit eingeschaltetem Raster wirksam.",
                                     "Off: windows step aside and never overlap. "
                                     + "On: windows may lie on top of each other and be stacked — grid and snapping stay active. "
                                     + "Only takes effect with the grid on.");
        _overlapToggle.Checked += (_, _) => SetAllowOverlap(true);
        _overlapToggle.Unchecked += (_, _) => SetAllowOverlap(false);
        UpdateOverlapToggle();

        WireEntry(WidgetId.Settings,
            _sidebar.AddFooterEntry(IconGlyph.ForWidget(WidgetId.Settings, 15, textBrush), Label(WidgetId.Settings)));
        ApplyBadges();
    }

    private void WireEntry(WidgetId id, SidebarEntry entry)
    {
        entry.Button.IsChecked = Settings.GetOrAdd(id).Visible;
        entry.Button.Checked += (_, _) => { if (!_suppressToggle) SetWidgetVisible(id, true); };
        entry.Button.Unchecked += (_, _) => { if (!_suppressToggle) SetWidgetVisible(id, false); };
        _entries[id] = entry;
    }

    /// <summary>The sidebar header's live line: recording, buffer running or paused.</summary>
    private (string Text, BoardStatusKind Kind) BoardStatus()
    {
        var rec = _host.ManualRecording;
        bool recording = rec.IsRecording;
        var recFor = recording && rec.StartedAt is { } t ? DateTime.Now - t : TimeSpan.Zero;
        return WidgetCatalog.Status(_host.ReplayBuffer.IsRunning, _host.Settings.Current.ReplayBuffer.DurationSeconds,
                                    recording, recFor);
    }

    private void UpdateOverlapToggle()
    {
        if (_overlapToggle is null) return;
        _overlapToggle.IsEnabled = SnapOn;
        _overlapToggle.Opacity = SnapOn ? 1.0 : 0.4;
    }

    private void SetWidgetVisible(WidgetId id, bool visible)
    {
        var st = Settings.GetOrAdd(id);
        st.Visible = visible;

        if (visible)
        {
            var screen = _boardOpen && _boardScreen != null ? _boardScreen : PrimaryScreen();
            ShowWidget(id, st, screen, IndexOf(id));
        }
        else if (_windows.TryGetValue(id, out var w))
        {
            w.Hide();
        }
        _host.Settings.Save();
    }

    /// <summary>Reflect visibility on the sidebar row without re-triggering it.</summary>
    private void SyncToggle(WidgetId id, bool on)
    {
        if (!_entries.TryGetValue(id, out var e) || e.Button.IsChecked == on) return;
        var t = e.Button;
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

    // ---- Screen helpers (physical pixels; see DisplayGeometry) ----

    private static WinForms.Screen PrimaryScreen() => WinForms.Screen.PrimaryScreen ?? WinForms.Screen.AllScreens[0];

    private static WinForms.Screen? ScreenByDeviceName(string? name)
        => string.IsNullOrEmpty(name) ? null : WinForms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == name);

    /// <summary>
    /// Temporarily hides all visible overlay windows (widgets + board) so an OWN
    /// screenshot doesn't include them. Returns an IDisposable that restores them, or
    /// null if nothing was visible. Widgets stay capture-visible to external tools
    /// (Snipping Tool, OBS) unless the user excluded them (WhatsApp): those are invisible
    /// to every capture already and are skipped here — hiding them would only flicker.
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
        foreach (var w in _windows.Values)
            if (!w.ExcludeFromCapture) HideIfVisible(w);
        HideIfVisible(_backdrop);
        HideIfVisible(_sidebar);
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

    /// <summary>
    /// Closes everything. Web widgets dispose their browser explicitly so WebView2 can
    /// flush cookies/storage (otherwise a fresh WhatsApp login may not survive the exit).
    /// </summary>
    public void Dispose()
    {
        L.LanguageChanged -= OnLanguageChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _displayTimer?.Stop();
        _host.PerfOverlayRefresh = null;
        if (_perfOverlayViewing)
        {
            _host.Performance.Sampled -= OnPerfSampleForOverlay;
            _perfOverlayViewing = false;
        }
        try { _perfOverlay?.Close(); } catch { }
        _host.CrosshairRefresh = null;
        foreach (var w in _windows.Values)
        {
            if (w.WidgetContent is IDisposable d) { try { d.Dispose(); } catch { } }
            try { w.Close(); } catch { }
        }
        _windows.Clear();
        try { _sidebar?.Close(); } catch { }
        try { _backdrop?.Close(); } catch { }
        try { _crosshair?.Close(); } catch { }
    }
}
