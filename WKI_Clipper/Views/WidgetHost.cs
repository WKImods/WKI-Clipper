using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using WKI_Clipper.Models;
using WKI_Clipper.Native;
using WKI_Clipper.Services;
using WKI_Clipper.Views.AutoClicker;
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
    private ShortcutBarWindow? _bar;
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
    /// <summary>The "AutoClicker · 25 cps" label; follows the clicker's state by itself and never touches the capture path.</summary>
    private readonly ClickerIndicatorPresenter _clickerIndicator;
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
        _host.Theme.Changed += OnThemeChanged;
        _host.CrosshairRefresh = ApplyCrosshair;
        _host.PerfOverlayRefresh = ApplyPerfOverlay;
        _host.GameChanged += OnGameChangedForOverlay;
        _clickerIndicator = new ClickerIndicatorPresenter(host);
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

        var settings = _host.Settings.Current;
        var s = settings.Crosshair;   // the standard: Enabled lives here and nowhere else

        // Keep the config widget's checkbox in sync (hotkey toggles bypass the UI).
        CrosshairContent()?.SyncEnabled(s.Enabled);

        ReconcileCapturePath();

        // Which look: the game in front (its profile over the standard) - or, while the board is open and the
        // crosshair widget is being edited, exactly the look being edited (what you edit is what you see).
        var game = _host.CurrentGame;
        var editing = _boardOpen ? _host.CrosshairEditing : null;
        var look = EffectiveSettings.CrosshairLookFor(settings, CrosshairPlacement.LookExe(game?.ExeName, editing));
        var entry = _host.Crosshairs.GetById(look.ActiveId) ?? _host.Crosshairs.GetById(s.ActiveId);

        if (!s.Enabled || entry is null)
        {
            _crosshair?.Hide();
            return;
        }

        var image = CrosshairImage.Render(_host.Crosshairs.FullPath(entry), look);
        if (image is null)
        {
            _crosshair?.Hide();
            Logger.Warn("Crosshair image could not be rendered — overlay hidden.");
            return;
        }

        // A profile places the crosshair relative to the centre of the monitor the game is on.
        var lookExe = CrosshairPlacement.LookExe(game?.ExeName, editing);
        if (EffectiveSettings.CrosshairOffsetFor(settings, lookExe) is { } offset)
        {
            var (monitor, scale) = ReferenceMonitor(game, EffectiveSettings.ProfileKey(lookExe));
            (look.CenterX, look.CenterY) = CrosshairPlacement.Center(monitor, scale, offset.X, offset.Y);
        }

        if (_crosshair is null)
        {
            _crosshair = new CrosshairOverlayWindow();
            _crosshair.Moved += OnCrosshairMoved;
        }

        _crosshair.Apply(image, look);
        _crosshair.SetInteractive(_boardOpen);   // draggable only while the board is up
        if (!_crosshair.IsVisible) _crosshair.Show();
        // Re-center once the window has actually measured itself (SizeToContent).
        _crosshair.CenterOn(look);
        BumpTopmost(_crosshair);
    }

    /// <summary>
    /// The monitor (physical pixels) and its Windows scale the game is on RIGHT NOW: looked up from its window, so a
    /// resolution change, an unplugged monitor or a moved window is followed. Falls back to the snapshot taken when
    /// the game was detected, then to the primary monitor.
    /// </summary>
    private static (LayoutRect Bounds, double Scale) GameMonitor(GameInfo? game)
    {
        LayoutRect? live = null;
        if (game is { Window: var hwnd } && hwnd != IntPtr.Zero && User32.IsWindow(hwnd))
        {
            try
            {
                var b = WinForms.Screen.FromHandle(hwnd).Bounds;
                live = new LayoutRect(b.X, b.Y, b.Width, b.Height);
            }
            catch { /* window vanished meanwhile: the snapshot takes over */ }
        }
        var r = CrosshairPlacement.ResolveMonitor(live, game?.Monitor, DisplayGeometry.Bounds(PrimaryScreen()));
        return (r, DisplayGeometry.ScaleAt(r.X + r.W / 2, r.Y + r.H / 2));
    }

    /// <summary>
    /// The monitor a profile's offset is measured from: the game's own when the profile belongs to the game in front,
    /// otherwise (another game's profile edited from the board, or no game) the monitor the crosshair is on - at the
    /// drop point for a drag, else where the overlay currently sits. Preview and drag use this same reference.
    /// </summary>
    private (LayoutRect Bounds, double Scale) ReferenceMonitor(GameInfo? game, string? profileKey, double? atX = null, double? atY = null)
    {
        if (CrosshairPlacement.OffsetUsesGameMonitor(game?.ExeName, profileKey)) return GameMonitor(game);

        WinForms.Screen screen = atX is double x && atY is double y ? DisplayGeometry.ScreenAt(x, y)
            : _crosshair is { IsVisible: true } && DisplayGeometry.BoundsOf(_crosshair) is { } b ? DisplayGeometry.ScreenOf(b)
            : PrimaryScreen();
        return (DisplayGeometry.Bounds(screen), DisplayGeometry.ScaleOf(screen));
    }

    /// <summary>
    /// The user dragged the crosshair (physical centre). It belongs to a game profile - stored as an offset from the
    /// game monitor's centre - or, as before, to the standard position (see <see cref="CrosshairPlacement.DragProfileKey"/>).
    /// </summary>
    private void OnCrosshairMoved(double cx, double cy)
    {
        var settings = _host.Settings.Current;
        var game = _host.CurrentGame;
        var editing = _boardOpen ? _host.CrosshairEditing : null;

        if (CrosshairPlacement.DragProfileKey(settings, game?.ExeName, editing) is { } key)
        {
            var (monitor, scale) = ReferenceMonitor(game, key, cx, cy);
            var (ox, oy) = CrosshairPlacement.OffsetFrom(monitor, scale, cx, cy);
            string? name = editing?.Key is not null && EffectiveSettings.ProfileKey(editing.Key) == key ? editing.DisplayName
                : EffectiveSettings.ProfileKey(game?.ExeName) == key ? game!.DisplayName : null;
            GameProfiles.SetCrosshairOffset(settings, key, name, ox, oy);
            Logger.Info($"Crosshair of profile '{key}' moved to offset {ox:0.##}/{oy:0.##}.");
        }
        else
        {
            settings.Crosshair.CenterX = cx;
            settings.Crosshair.CenterY = cy;
        }
        _host.Settings.Save();

        // A first drag may have created the profile: the selector lists it from now on.
        CrosshairContent()?.RefreshTargets();
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
    /// <summary>Where the overlay was last seated (physical pixels). "Free" chosen from the list starts from here.</summary>
    private LayoutRect? _perfPlaced;
    /// <summary>The FPS measurement (a passive ETW session) — held only while an FPS value is ticked and the overlay is up.</summary>
    private IDisposable? _perfFpsLease;

    /// <summary>
    /// Shows, updates or hides the corner performance readout from current settings. Like
    /// the crosshair it lives outside the board and stays up while playing. With "only when a
    /// game is detected" it appears and disappears with the game in front — and while it is
    /// hidden, neither the counters nor the FPS measurement run.
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
        if (!PerfOverlayLayout.ShouldShow(s, _host.CurrentGame is not null))
        {
            _perfOverlay?.Hide();
            StopPerfOverlayFeeds();
            return;
        }

        if (_perfOverlay is null)
        {
            _perfOverlay = new PerfOverlayWindow();
            _perfOverlay.Dropped += OnPerfOverlayDropped;
        }
        _perfOverlay.SetExcludedFromCapture(s.HideFromCapture);
        if (!_perfOverlayViewing)
        {
            _host.Performance.Sampled += OnPerfSampleForOverlay;
            _host.Performance.AddViewer();
            _perfOverlayViewing = true;
        }
        UpdatePerfFpsLease(s);
        RenderPerfOverlay();
    }

    /// <summary>Takes or gives back the FPS measurement: only for a ticked FPS value on a visible overlay.</summary>
    private void UpdatePerfFpsLease(PerfOverlaySettings s)
    {
        bool want = PerfOverlayLayout.ShouldMeasureFps(s, overlayShown: true, widgetShown: false);
        if (want && _perfFpsLease is null)
        {
            try { _perfFpsLease = _host.FrameTimes.Subscribe(); }
            catch (ObjectDisposedException) { /* shutting down */ }
        }
        else if (!want && _perfFpsLease is not null)
        {
            _perfFpsLease.Dispose();
            _perfFpsLease = null;
        }
    }

    /// <summary>The overlay is not on screen: release the 1 Hz counters and the FPS session.</summary>
    private void StopPerfOverlayFeeds()
    {
        if (_perfOverlayViewing)
        {
            _host.Performance.Sampled -= OnPerfSampleForOverlay;
            _host.Performance.RemoveViewer();
            _perfOverlayViewing = false;
        }
        _perfFpsLease?.Dispose();
        _perfFpsLease = null;
    }

    private void OnGameChangedForOverlay(GameInfo? _)
    {
        // The overlay may appear or vanish with the game ("only when a game is detected"), and the
        // FPS lease depends on the overlay being up. ApplyPerfOverlay marshals to the UI thread itself.
        try { ApplyPerfOverlay(); }
        catch (Exception ex) { Logger.Warn("Overlay refresh after a game change failed: " + ex.Message); }

        // The crosshair follows the game's profile (image, size, colours, position); the widget's selector
        // learns about the new game. Marshalled: the detector raises this on a pool thread.
        try { Application.Current?.Dispatcher.BeginInvoke(new Action(RefreshCrosshairForGame)); }
        catch (Exception ex) { Logger.Warn("Crosshair refresh after a game change failed: " + ex.Message); }
    }

    private void RefreshCrosshairForGame()
    {
        ApplyCrosshair();
        CrosshairContent()?.OnGameChanged();
    }

    /// <summary>
    /// The crosshair configuration inside the "Gaming Utilities" widget (the widget keeps the id <see cref="WidgetId.Crosshair"/>), or null
    /// while its window is not built. Every host-to-view call about the crosshair goes through here.
    /// </summary>
    private CrosshairView? CrosshairContent()
        => _windows.TryGetValue(WidgetId.Crosshair, out var cw) && cw.WidgetContent is GamingUtilitiesView gu ? gu.Crosshair : null;

    private void OnPerfSampleForOverlay(PerfSample _)
    {
        // Cheap 1 Hz housekeeping: drop a game that exited while one of our windows had the focus.
        _host.GameDetector?.Verify();
        Application.Current?.Dispatcher.BeginInvoke(new Action(RenderPerfOverlay));
    }

    private void RenderPerfOverlay()
    {
        if (_perfOverlay is null || !_perfOverlayViewing) return;
        var s = _host.Settings.Current.PerfOverlay;
        var lines = PerfOverlayLayout.Lines(s, _host.Performance.Last, _host.Performance.History(), DateTime.Now,
            PerfOverlayLayout.AnyFpsValue(s) ? _host.FrameTimes.Current : null);
        if (lines.Count == 0)
        {
            _perfOverlay.Hide();   // nothing ticked: no empty panel on the screen
            return;
        }
        _perfOverlay.Render(lines, s);
        bool justShown = !_perfOverlay.IsVisible;
        // Grabbable exactly while the board is open - also for an overlay that only appears now (a game started).
        SyncPerfOverlayDrag();
        if (justShown) _perfOverlay.Show();
        _perfOverlay.UpdateLayout();

        // Never move it under the user's hand: the 1 Hz refresh would pull it back to the stored place mid-drag.
        if (!_perfOverlay.IsDragging) PlacePerfOverlay(s);
        // Only when it appears - re-asserting z-order every second would churn the desktop.
        if (justShown)
        {
            BumpTopmost(_perfOverlay);
            if (_boardOpen) RaiseBoardAboveOverlay();
        }
    }

    /// <summary>
    /// While the board is open the sidebar, the shortcut bar and the colour picker win over the performance overlay: the overlay is
    /// grabbable then, and an anchor like "top centre" lies exactly where the bar sits, one at the bottom left where the sidebar's footer
    /// chips are - on top it would take every click meant for them and drag instead. The overlay stays grabbable wherever it sticks out
    /// from under them. The order is that of <see cref="BoardZOrder"/>.
    /// </summary>
    private void RaiseBoardAboveOverlay()
    {
        foreach (var layer in BoardZOrder.RaisedAboveOverlay(_sidebar is { IsVisible: true }, _bar is { IsVisible: true }, _picker is { IsVisible: true }))
            BumpTopmost(layer switch { BoardLayer.Sidebar => _sidebar!, BoardLayer.Bar => _bar!, _ => _picker! });
    }

    /// <summary>
    /// Seats the overlay: one of eight places on the primary monitor's full area (in a fullscreen game that IS the screen
    /// edge) or a dragged-to spot on the monitor it was dropped on; a monitor that is gone means the primary one.
    /// </summary>
    private void PlacePerfOverlay(PerfOverlaySettings s)
    {
        var overlay = _perfOverlay!;
        var monitors = ScreenBoxes();
        if (PerfOverlayPlacement.ResolveMonitor(s.Anchor, s.Free, monitors, null) is not { } first) return;

        // "Free" picked from the list but nothing dragged yet: stay exactly where the overlay is, and drag on from there.
        if (s.Anchor == OverlayAnchor.Free && s.Free is null)
        {
            double scale = ScaleOfBox(first);
            var basis = _perfPlaced ?? PerfOverlayPlacement.Place(OverlayAnchor.TopRight, null,
                overlay.ActualWidth * scale, overlay.ActualHeight * scale, monitors, null, PerfOverlayPlacement.MarginDip * scale);
            if (PerfOverlayPlacement.MonitorAt(basis, monitors) is { } on)
                s.Free = PerfOverlayPlacement.FreeFrom(basis, on);
        }

        var monitor = PerfOverlayPlacement.ResolveMonitor(s.Anchor, s.Free, monitors, null) ?? first;
        double dpi = ScaleOfBox(monitor);
        var at = PerfOverlayPlacement.Place(s.Anchor, s.Free, overlay.ActualWidth * dpi, overlay.ActualHeight * dpi,
                                            monitors, null, PerfOverlayPlacement.MarginDip * dpi);
        if (DisplayGeometry.BoundsOf(overlay) is not { } cur || Math.Abs(cur.X - at.X) > 0.5 || Math.Abs(cur.Y - at.Y) > 0.5)
            DisplayGeometry.MoveTo(overlay, at.X, at.Y);
        _perfPlaced = at;
    }

    private static double ScaleOfBox(MonitorBox m) => DisplayGeometry.ScaleAt(m.WorkArea.X + m.WorkArea.W / 2, m.WorkArea.Y + m.WorkArea.H / 2);

    /// <summary>The overlay can be grabbed exactly while the board is open (see <see cref="PerfOverlayPlacement.DragAllowed"/>).</summary>
    private void SyncPerfOverlayDrag() => _perfOverlay?.SetDragMode(PerfOverlayPlacement.DragAllowed(_boardOpen));

    /// <summary>
    /// The user let go of the dragged overlay: within reach of one of the eight places it snaps to that, otherwise it stays
    /// where it is as a free place. Saved at once; the overlay is seated exactly, the settings list shows the result.
    /// </summary>
    private void OnPerfOverlayDropped(LayoutRect dropped)
    {
        var s = _host.Settings.Current.PerfOverlay;
        var monitors = ScreenBoxes();
        if (PerfOverlayPlacement.MonitorAt(dropped, monitors) is not { } monitor) return;

        double scale = ScaleOfBox(monitor);
        PerfOverlayPlacement.ApplyDrop(s, dropped, monitors, PerfOverlayPlacement.SnapDip * scale, PerfOverlayPlacement.MarginDip * scale);
        _host.Settings.Save();
        Logger.Info(s.Free is { } f
            ? $"Performance overlay dropped as a free place on {f.Monitor} (edges {f.HEdge}/{f.VEdge}, offset {f.RelDX:0.###}/{f.RelDY:0.###})."
            : $"Performance overlay dropped and snapped to {s.Anchor}.");

        RenderPerfOverlay();
        _host.NotifyPerfOverlayPlaced();
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
        => Application.Current.Dispatcher.BeginInvoke(new Action(() => RebuildUi("language")));

    /// <summary>
    /// A new colour theme is on: everything XAML draws follows by itself (dynamic resources), but the widget windows, the sidebar
    /// and the shortcut bar are built in code with the brushes of the moment - they are built anew, like on a language change.
    /// </summary>
    private void OnThemeChanged()
        => Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            // With the board closed (the picker's last choice went on while the board was being closed) the rebuild waits for the
            // next opening: no tearing down of pinned widgets over a running game.
            if (_themeGate.OnThemeChanged(_boardOpen)) RebuildUi("theme");
        }));

    private readonly ThemeRebuildGate _themeGate = new();

    /// <summary>
    /// Widget windows and the launcher build their (localized, coloured) content once. When that content has to change - the
    /// language flipped, a new colour theme is on - tear them all down and rebuild, so the change shows everywhere immediately,
    /// no app restart. Geometry/visibility/pins survive via the persisted <see cref="WidgetState"/>s: every window saves its
    /// place first, and the board (or just the pinned widgets) comes back from those.
    ///
    /// Web widgets are the exception (see <see cref="WidgetRebuildPlan"/>): rebuilding would throw away the page (and with it
    /// an open WhatsApp chat or a half-typed message). They only get new labels and a new icon colour; their chrome follows a
    /// theme through dynamic resources.
    /// </summary>
    private void RebuildUi(string reason)
    {
        var plan = TearDownForRebuild(reason);

        // OpenBoard records "where the keyboard was" - during a rebuild that would be one
        // of our own windows, so keep the game the user actually came from.
        var returnFocusTo = _returnFocusTo;
        bool externalLaunch = _externalLaunchDuringBoard;
        // The board comes back on the monitor it was on, not on the one the mouse is on (a colour is often committed with the mouse elsewhere).
        var keepScreen = plan.ReopenOnDevice is { } device ? ScreenByDeviceName(device) ?? PrimaryScreen() : null;
        if (plan.ReopenBoard) OpenBoard(keepScreen);
        else RestorePinned();
        _returnFocusTo = returnFocusTo;
        _externalLaunchDuringBoard = externalLaunch;
        Logger.Info($"Widgets rebuilt ({reason}).");
    }

    /// <summary>
    /// The first half of a rebuild: every window saves its place, the web widgets get their new texts and icon colour, everything else
    /// - widgets, sidebar, shortcut bar, backdrop - is closed. The board is left closed; what comes back is up to the caller.
    /// </summary>
    private WidgetRebuildPlan TearDownForRebuild(string reason)
    {
        CloseShortcutEditor();   // its owner, the bar, is built anew
        if (reason == "language") CloseThemePicker();   // its texts are built once, in the old language
        bool wasOpen = _boardOpen;
        var plan = WidgetRebuildPlan.For(_windows.Select(kv => new WidgetWindowInfo(kv.Key, kv.Value.WidgetContent is IWebWidget)), wasOpen,
                                         _boardScreen?.DeviceName, WinForms.Screen.AllScreens.Select(s => s.DeviceName).ToList(), PrimaryScreen().DeviceName);

        foreach (var id in plan.CaptureGeometry) CaptureGeometry(id, _windows[id], reason + "-rebuild");

        foreach (var id in plan.Keep)
        {
            var w = _windows[id];
            w.SetTitle(Label(id));
            w.SetIcon(IconGlyph.ForWidget(id, 15, (Brush)Application.Current.FindResource("AccentBrush")));
            ((IWebWidget)w.WidgetContent).Relocalize();
        }
        foreach (var id in plan.Rebuild)
        {
            try { _windows[id].Close(); } catch { }
            _windows.Remove(id);
        }
        if (_sidebar != null) { try { _sidebar.Close(); } catch { } _sidebar = null; }
        if (_bar != null) { try { _bar.Close(); } catch { } _bar = null; }
        _entries.Clear();
        if (_backdrop != null) { try { _backdrop.Close(); } catch { } _backdrop = null; }
        _boardOpen = false;
        return plan;
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
        WidgetId.Crosshair   => new GamingUtilitiesView(),
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

    /// <summary>The board is on screen. The shortcut bar closes it before starting a program, but only if it is open.</summary>
    public bool IsBoardOpen => _boardOpen;

    public void ToggleBoard()
    {
        if (_boardOpen) CloseBoard();
        else OpenBoard();
    }

    public void OpenBoard() => OpenBoard(null);

    /// <param name="keepScreen">The monitor to open on; null = the one the mouse is on (the normal case).</param>
    private void OpenBoard(WinForms.Screen? keepScreen)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // A colour that went on while the board was closed rebuilds the windows now, before anything is shown.
        if (!_boardOpen && _themeGate.TakePending()) TearDownForRebuild("theme");
        var screen = keepScreen ?? WinForms.Screen.FromPoint(WinForms.Cursor.Position);
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

        // The shortcut bar next, for the same reason: it is an obstacle the widgets have to know about. Opening the board
        // is also when the PC is looked at again - at most once a minute, in the background; the bar follows its changes.
        EnsureBar();
        _bar!.ShowOn(screen);
        _ = _host.Shortcuts.RescanIfStale();

        int i = 0;
        foreach (var id in Order)
        {
            var st = Settings.GetOrAdd(id);
            if (st.Visible) ShowWidget(id, st, screen, i);
            SyncToggle(id, st.Visible);
            i++;
        }
        // Bar first: its push is saved as the user's place, so it must not carry the sidebar's sideways (system) move along.
        KeepWidgetsClearOfBar();
        KeepWidgetsClearOfSidebar();
        BumpTopmost(_sidebar);
        BumpTopmost(_bar);

        // Crosshair becomes draggable while the board is open.
        if (_crosshair is { IsVisible: true }) { _crosshair.SetInteractive(true); BumpTopmost(_crosshair); }
        // So does the performance overlay: above the backdrop, grabbable, dashed outline.
        SyncPerfOverlayDrag();
        if (_perfOverlay is { IsVisible: true }) { BumpTopmost(_perfOverlay); RaiseBoardAboveOverlay(); }
        // The colour picker stays on top of the board that a theme switch just rebuilt around it.
        if (_picker is { IsVisible: true }) BumpTopmost(_picker);
        // An open crosshair widget previews the look it edits, not the game's (see ApplyCrosshair).
        if (_host.CrosshairEditing is not null) ApplyCrosshair();
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
    /// nothing is visible and nothing takes focus; only user drags/resizes ever save a
    /// geometry (see <see cref="PlacementLedger"/>), so stored positions are untouched, and
    /// the real open re-places every window via PositionWindow anyway.
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
        try { _backdrop ??= CreateBackdrop(); EnsureSidebar(); EnsureBar(); } catch { }

        Logger.Info($"Widget prewarm done: {built} windows built in {sw.ElapsedMilliseconds} ms.");
    }

    public void CloseBoard()
    {
        // A modal edit dialog over the board goes with it (before the focus goes back, so the game gets it and not the dialog).
        CloseShortcutEditor();

        // Persist what the user changed and has not been saved yet — never a spot the app chose.
        foreach (var (id, w) in _windows) CaptureGeometry(id, w, "board-close");

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

        CloseThemePicker();
        _backdrop?.Hide();
        _sidebar?.Hide();
        _bar?.Hide();
        // Crosshair goes click-through so it never intercepts a shot in-game.
        _crosshair?.SetInteractive(false);
        _host.Settings.Save();
        _boardOpen = false;
        _boardScreen = null;
        // The overlay is click-through again, whatever happens next.
        SyncPerfOverlayDrag();
        // Back from the previewed look to the game's own.
        if (_host.CrosshairEditing is not null) ApplyCrosshair();
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
        if (Is(_backdrop) || Is(_sidebar) || Is(_bar) || Is(_shortcutEditor)) return true;
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
        // Only now has the window its final size and monitor scale; before Show the rect is stale.
        RecordSystemPlacement(w);
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
        w.DragFinished += OnWidgetDragged;
        w.ResizeFinished += OnWidgetResized;
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
    /// The switch is a user resize: the window grows from its top-left corner, so the stored
    /// centre is recomputed for the new size from that corner (otherwise the old centre would
    /// reopen it shifted by half the growth). The clamp onto the screen stays a system move —
    /// what is stored is the intended rectangle, and restoring it clamps to the same spot.
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

        var st = Settings.GetOrAdd(WidgetId.Spotify);
        st.Width = w.Width;
        st.Height = w.Height;

        // Keep the grown window on its screen.
        if (DisplayGeometry.BoundsOf(w) is { } b)
        {
            var screen = DisplayGeometry.ScreenOf(b);
            var area = DisplayGeometry.WorkArea(screen);
            double s = DisplayGeometry.ScaleOf(screen);
            // The new size from the DIPs just set — the window's own rectangle may still show the old one.
            var intended = WidgetPlacementPlanner.IntendedAfterResize(b, w.Width, w.Height, s);
            var (rx, ry) = WidgetLayout.ToRelative(intended, area);
            st.RelX = rx;
            st.RelY = ry;
            st.X = intended.X;
            st.Y = intended.Y;
            st.MonitorDeviceName = screen.DeviceName;

            var (x, y) = WidgetLayout.Clamp(intended.X, intended.Y, intended.W, intended.H, area.X, area.Y, area.Right, area.Bottom, EdgeMarginDip * s);
            if (Math.Abs(x - b.X) > 0.5 || Math.Abs(y - b.Y) > 0.5) DisplayGeometry.MoveTo(w, x, y);
        }
        RecordSystemPlacement(w);
        if (KeepApart) MakeRoomAround(w);   // neighbours step aside as a consequence of the user's switch
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
        var r = _planner.Restore(st, w.Width, w.Height, index, legacy, DisplayGeometry.WorkArea(screen), DisplayGeometry.ScaleOf(screen));
        DisplayGeometry.MoveTo(w, r.X, r.Y);
        // No system placement is recorded here: callers do it once the window is shown and sized.
    }

    /// <summary>
    /// Persistence is opt-in. Where the APP puts a window (restore, clamp, sidebar clearance,
    /// re-layout after a scaling change) is a system placement and is never saved: the stored
    /// place is relative to the work area, so at another display scaling it only round-trips
    /// while it stays the user's own — a 175 % emergency spot written back displaced the
    /// widgets after switching back to 100 %. Only a drag, a resize or a deliberate
    /// re-arrangement marks a window as user-changed. The rules live in the planner (pure,
    /// unit-tested); this class only reads and moves the real windows.
    /// </summary>
    private readonly WidgetPlacementPlanner _planner = new();

    /// <summary>Record where the app has just put the window (call after it is shown and sized).</summary>
    private void RecordSystemPlacement(WidgetWindow w)
    {
        if (DisplayGeometry.BoundsOf(w) is { } b) _planner.Placed(w.Id, b);
    }

    /// <summary>
    /// Writes the window's current place into its <see cref="WidgetState"/> — but only when the
    /// planner says the user made it. <paramref name="reason"/> goes into the log line, so a
    /// displaced layout can be traced to the action that saved it.
    /// <paramref name="applied"/>: the geometry the code has JUST set on the window (physical
    /// rectangle and DIP size). A window that was resized a moment ago does not report that yet
    /// (ActualWidth follows with the next layout pass), so reading it back would save the old size.
    /// </summary>
    private void CaptureGeometry(WidgetId id, WidgetWindow w, string reason,
                                 (LayoutRect Rect, double WidthDip, double HeightDip)? applied = null)
    {
        if (!w.IsVisible) return;
        LayoutRect b;
        double width, height;
        if (applied is { } a) { b = a.Rect; width = a.WidthDip; height = a.HeightDip; }
        else if (DisplayGeometry.BoundsOf(w) is { } live)
        {
            b = live;
            width = w.ActualWidth > 0 ? w.ActualWidth : w.Width;
            height = w.ActualHeight > 0 ? w.ActualHeight : w.Height;
        }
        else return;
        if (b.X < -10000 || b.Y < -10000) return;   // prewarm parking spot, never a user choice

        var screen = DisplayGeometry.ScreenOf(b);
        if (!_planner.Commit(Settings.GetOrAdd(id), b, width, height, DisplayGeometry.WorkArea(screen), screen.DeviceName))
            return;   // a system placement, or nothing changed
        Logger.Info(FormattableString.Invariant(
            $"Widget {id}: place saved ({reason}) — {b.W:0}x{b.H:0} px at {b.X:0},{b.Y:0} on {screen.DeviceName}, scale {DisplayGeometry.ScaleOf(screen):0.##}."));
    }

    /// <summary>A title-bar drag moved the window: the user's own change, saved (after resolving overlaps).</summary>
    private void OnWidgetDragged(WidgetWindow w)
    {
        _planner.UserChanged(w.Id);
        if (KeepApart) MoveToFreeSpot(w, userMove: true);
        else CaptureGeometry(w.Id, w, "drag");
    }

    /// <summary>A resize grip changed the size: saved, and neighbours in the way step aside (and are saved too).</summary>
    private void OnWidgetResized(WidgetWindow w)
    {
        _planner.UserChanged(w.Id);
        if (KeepApart) MakeRoomAround(w);
        CaptureGeometry(w.Id, w, "resize");
    }

    // ---- Grid & snapping ("Raster" switch in the launcher) ----
    //
    // All geometry here is in physical pixels of one monitor; the DIP constants are scaled
    // with that monitor's display scaling so the grid feels the same at 100 and 150 %.

    private const double GridDip = 16, SnapDistanceDip = 14,
                         GapDip = WidgetPlacementPlanner.GapDip, EdgeMarginDip = WidgetPlacementPlanner.EdgeMarginDip;

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
        if (on) ArrangeVisible(snapToGrid: true, persist: true, reason: "grid");
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
        if (!allow && SnapOn) ArrangeVisible(snapToGrid: false, persist: true, reason: "overlap");
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
        if (BarRect() is { } bar && bar.Overlaps(area)) list.Add(bar);
        return list;
    }

    /// <summary>
    /// The sidebar's on-screen rectangle while the board is open — computed from the board
    /// monitor's work area and scaling (see <see cref="WidgetLayout.SidebarRect"/>), so it is
    /// right even in the moment a display change has not yet reached the sidebar's own HWND.
    /// </summary>
    private LayoutRect? SidebarRect()
    {
        if (_sidebar is not { IsVisible: true }) return null;
        if (_boardScreen is { } screen)
            return WidgetLayout.SidebarRect(DisplayGeometry.WorkArea(screen), DisplayGeometry.ScaleOf(screen), _sidebar.Collapsed);
        return DisplayGeometry.BoundsOf(_sidebar);
    }

    /// <summary>
    /// Widgets left under where the sidebar now sits (a layout from a smaller scaling, or a
    /// sidebar that was just expanded) move to its right edge. A system move: never saved,
    /// the stored place stays the user's and this re-runs, idempotently, on every open. The
    /// sidebar is 280 DIP wide, so at 175 % it reaches 518 px instead of 296 px — saving that
    /// emergency spot is what displaced the widgets after switching back to 100 %.
    /// </summary>
    private void KeepWidgetsClearOfSidebar()
    {
        if (SidebarRect() is not { } sb) return;
        var screen = DisplayGeometry.ScreenOf(sb);
        double s = DisplayGeometry.ScaleOf(screen);
        var area = DisplayGeometry.WorkArea(screen);
        var visible = new List<(WidgetId Id, LayoutRect Rect)>();
        foreach (var w in _windows.Values)
            if (w.IsVisible && DisplayGeometry.BoundsOf(w) is { } b) visible.Add((w.Id, b));
        foreach (var (id, moved) in _planner.ClearOfSidebar(visible, sb, area, s))
        {
            var w = _windows[id];
            DisplayGeometry.MoveTo(w, moved.X, moved.Y);
            RecordSystemPlacement(w);
        }
        if (KeepApart) ArrangeVisible(snapToGrid: false, persist: false, reason: "sidebar");
    }

    /// <summary>
    /// The shortcut bar's rectangle while it is on screen - computed from the monitor and the bar's content width, so it is right
    /// even in the moment a display change has not yet reached the bar's own HWND.
    /// </summary>
    private LayoutRect? BarRect() => _bar is { IsVisible: true, Rect: { } rect } ? rect : null;

    /// <summary>
    /// Widgets that really intersect the shortcut bar are pushed straight down below it. Unlike the sidebar clearance this is saved:
    /// the pushed place is the widget's place from now on, so a second open moves nothing (the planner marks it as the user's, the
    /// geometry is committed here and written with the board's save when it closes). Widgets that do not intersect are never touched.
    /// </summary>
    private void KeepWidgetsClearOfBar()
    {
        if (BarRect() is not { } bar) return;
        var screen = DisplayGeometry.ScreenOf(bar);
        double s = DisplayGeometry.ScaleOf(screen);
        var area = DisplayGeometry.WorkArea(screen);
        var visible = new List<(WidgetId Id, LayoutRect Rect)>();
        foreach (var w in _windows.Values)
            if (w.IsVisible && DisplayGeometry.BoundsOf(w) is { } b) visible.Add((w.Id, b));
        foreach (var (id, moved) in _planner.ClearOfBar(visible, bar, area, s))
        {
            var w = _windows[id];
            DisplayGeometry.MoveTo(w, moved.X, moved.Y);
            double dipW = w.ActualWidth > 0 ? w.ActualWidth : w.Width, dipH = w.ActualHeight > 0 ? w.ActualHeight : w.Height;
            CaptureGeometry(id, w, "shortcut-bar", (moved, dipW, dipH));
        }
    }

    // ---- Shortcut bar ----

    private void EnsureBar()
    {
        if (_bar != null) return;
        _bar = new ShortcutBarWindow(_host.Shortcuts, () => _host.CurrentGame, SidebarRect);   // a wide bar keeps to the right of the sidebar
        _bar.EditRequested += OpenShortcutEditor;
        // Folding or unfolding the bar (or new items) changes its width: widgets under it move down, like at the open.
        _bar.RectChanged += () => { if (_boardOpen) KeepWidgetsClearOfBar(); };
    }

    /// <summary>
    /// The bar's gear: the edit dialog, modal above the board. It changes the lists live; a changed hotkey re-registers the global
    /// hotkeys through the host. The bar and the widgets under it follow by themselves (ItemsChanged, the bar's rectangle).
    /// </summary>
    private void OpenShortcutEditor()
    {
        if (_shortcutEditor != null) return;
        try
        {
            var model = ShortcutEditorModel.ForThisPc(_host.Shortcuts, _host.ShortcutShell);
            _shortcutEditor = new ShortcutEditorWindow(_host.Shortcuts, model, _host.RefreshShortcutHotkeys, _bar);
            _shortcutEditor.ShowDialog();
        }
        catch (Exception ex) { Logger.Warn("The shortcut editor failed: " + ex.Message); }
        finally { _shortcutEditor = null; }   // ShowDialog returns only after whatever closed the editor has finished
    }

    /// <summary>
    /// The edit dialog while it is open (it is modal, but the global hotkeys keep working under it: the board's own, a shortcut's, a freshly
    /// bound one). The board closing - or being rebuilt - closes it, too: hiding its owner (the bar) does not, and a topmost dialog left alone
    /// over the game or over the program that was just started would also keep the keyboard from going back to the game.
    /// </summary>
    private ShortcutEditorWindow? _shortcutEditor;

    private void CloseShortcutEditor()
    {
        var editor = _shortcutEditor;   // stays set until ShowDialog returns, so a second call finds it closing and does nothing
        if (editor == null) return;
        try { editor.Close(); }
        catch (Exception ex) { Logger.Warn("Closing the shortcut editor failed: " + ex.Message); }
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
        bool moved = false;
        if (obstacles.Exists(o => b.Overlaps(o, gap))
            && WidgetLayout.FindFreeSpot(b, obstacles, area, gap) is { } spot)   // null = screen full: leave it
        {
            DisplayGeometry.MoveTo(w, spot.X, spot.Y);
            moved = true;
        }
        if (userMove)
        {
            _planner.UserChanged(w.Id);
            CaptureGeometry(w.Id, w, moved ? "free-spot" : "drag");
        }
        else RecordSystemPlacement(w);
    }

    /// <summary>
    /// A widget grew (resize grip, Spotify view switch): neighbours in the way step aside. They
    /// move as a direct consequence of the user's action at the current scaling, so — unlike a
    /// system move — their new places are saved.
    /// </summary>
    private void MakeRoomAround(WidgetWindow grown)
    {
        if (DisplayGeometry.BoundsOf(grown) is not { } b) return;
        var fixedObstacles = new List<LayoutRect> { b };
        if (SidebarRect() is { } sb) fixedObstacles.Add(sb);
        if (BarRect() is { } bar) fixedObstacles.Add(bar);
        RearrangeOn(DisplayGeometry.ScreenOf(b), except: grown, snapToGrid: false, fixedObstacles, persist: true, reason: "make-room");
    }

    /// <summary>
    /// Tidies every visible widget, per monitor. <paramref name="persist"/>: the user asked
    /// for it (switch turned on) — false after a display change or for the sidebar clearance,
    /// where the tidy layout is only a consequence of the new scaling and must not replace the
    /// user's places. <paramref name="reason"/> names the action in the log when it is saved.
    /// </summary>
    private void ArrangeVisible(bool snapToGrid, bool persist, string reason)
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
            if (BarRect() is { } bar) fixedObstacles.Add(bar);
            RearrangeOn(screen, except: null, snapToGrid, fixedObstacles, persist, reason);
        }
    }

    private void RearrangeOn(WinForms.Screen screen, WidgetWindow? except, bool snapToGrid,
                             List<LayoutRect> fixedObstacles, bool persist, string reason)
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
            // The size the window will have, in DIPs: from what is set below, not read back
            // afterwards (ActualWidth only follows with the next layout pass).
            double dipW = w.ActualWidth > 0 ? w.ActualWidth : w.Width, dipH = w.ActualHeight > 0 ? w.ActualHeight : w.Height;
            bool sized = Math.Abs(r.W - rects[i].W) > 0.5 || Math.Abs(r.H - rects[i].H) > 0.5;
            if (sized)
            {
                dipW = Math.Max(w.MinWidth, r.W / s);
                dipH = Math.Max(w.MinHeight, r.H / s);
                w.Width = dipW;
                w.Height = dipH;
            }
            bool moved = Math.Abs(r.X - rects[i].X) > 0.5 || Math.Abs(r.Y - rects[i].Y) > 0.5;
            if (moved) DisplayGeometry.MoveTo(w, r.X, r.Y);
            if (!persist) RecordSystemPlacement(w);
            else if (moved || sized)
            {
                // Only a window the arrangement actually changed is the user's new place; one it
                // left alone keeps what is stored (it may sit at a system spot of this scaling).
                // The arranged geometry is passed explicitly: the window cannot be asked yet.
                _planner.UserChanged(w.Id);
                var applied = new LayoutRect(Math.Round(r.X), Math.Round(r.Y), dipW * s, dipH * s);
                CaptureGeometry(w.Id, w, reason, (applied, dipW, dipH));
            }
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
                RecordSystemPlacement(w);   // the app's re-layout, not a user choice
            }
            index++;
        }
        if (_boardOpen)
        {
            var screen = _boardScreen ?? PrimaryScreen();
            _backdrop?.Cover(screen);
            _sidebar?.Reposition(screen);
            _bar?.Reposition(screen);
        }
        if (KeepApart) ArrangeVisible(snapToGrid: false, persist: false, reason: "display-change");
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

    // ---- Colour picker ----

    private ThemePickerWindow? _picker;
    private System.Windows.Controls.Primitives.ToggleButton? _themeChip;
    private bool _syncingThemeChip;

    /// <summary>Opens the colour picker next to the sidebar. Choices go on at once and are saved (see <see cref="ThemePickerState"/>).</summary>
    private void OpenThemePicker()
    {
        if (_picker is { IsVisible: true } || _sidebar is null) return;
        var state = new ThemePickerState(_host.Settings.Current.Theme, p => _host.Theme.Apply(p), () => _host.Settings.Save());
        _picker = new ThemePickerWindow(state);
        _picker.CloseRequested += CloseThemePicker;
        _picker.ShowNextTo(_sidebar, _boardScreen ?? PrimaryScreen());
        BumpTopmost(_picker);
        System.Windows.Input.InputManager.Current.PreProcessInput += OnPickerPreProcessInput;
        SyncThemeChip(true);
    }

    private void CloseThemePicker()
    {
        System.Windows.Input.InputManager.Current.PreProcessInput -= OnPickerPreProcessInput;
        if (_picker is { } p)
        {
            _picker = null;
            p.CloseRequested -= CloseThemePicker;
            try { p.Close(); } catch { }
        }
        SyncThemeChip(false);
    }

    private void SyncThemeChip(bool open)
    {
        if (_themeChip is null) return;
        _syncingThemeChip = true;
        try { _themeChip.IsChecked = open; }
        finally { _syncingThemeChip = false; }
    }

    /// <summary>
    /// A click anywhere else on the board closes the picker - except on its own chip (which toggles it) and inside pop-ups that
    /// belong to it (the text box's own menu). A click on the backdrop closes the board, and the picker with it.
    /// </summary>
    private void OnPickerPreProcessInput(object sender, System.Windows.Input.PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is not System.Windows.Input.MouseButtonEventArgs { ButtonState: System.Windows.Input.MouseButtonState.Pressed } click
            || click.RoutedEvent != System.Windows.Input.Mouse.PreviewMouseDownEvent) return;
        if (_picker is null || click.OriginalSource is not DependencyObject hit) return;

        var window = Window.GetWindow(hit);
        if (window is null || window == _picker) return;                  // a pop-up (no window of its own) or the picker itself
        if (_themeChip is not null && IsInside(hit, _themeChip)) return;  // the chip decides by itself

        Application.Current.Dispatcher.BeginInvoke(new Action(CloseThemePicker));
    }

    private static bool IsInside(DependencyObject element, DependencyObject ancestor)
    {
        for (var d = element; d is not null; d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                                                   ? System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d)
                                                   : LogicalTreeHelper.GetParent(d))
            if (ReferenceEquals(d, ancestor)) return true;
        return false;
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
            // The bar keeps to the right of the sidebar, so its place follows the sidebar's width. First, so that the widgets it pushes
            // are saved without the sidebar's sideways (system) move (see OpenBoard).
            if (_boardOpen && _boardScreen != null) _bar?.Reposition(_boardScreen);
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

        // The colour picker: checked while it is open. A rebuilt sidebar (a theme switch rebuilds it) shows the state it finds.
        _themeChip = _sidebar.AddFooterChip(IconGlyph.Make(IconGlyph.Palette, 13), L.T("Farben", "Colours"));
        _themeChip.ToolTip = L.T("Farben", "Colours");
        _themeChip.IsChecked = _picker is { IsVisible: true };
        _themeChip.Checked += (_, _) => { if (!_syncingThemeChip) OpenThemePicker(); };
        _themeChip.Unchecked += (_, _) => { if (!_syncingThemeChip) CloseThemePicker(); };

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
        return WidgetCatalog.Status(_host.ReplayBuffer.IsRunning, _host.ReplayBuffer.EffectiveDurationSeconds,
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

    /// <summary>Esc on the board: the colour picker closes first - and only it, the board stays - the board the next time.</summary>
    private void OnBoardEscape()
    {
        if (BoardEscape.Target(_picker is { IsVisible: true }) == EscapeTarget.ColourPicker) CloseThemePicker();
        else CloseBoard();
    }

    private WidgetBackdropWindow CreateBackdrop()
    {
        var b = new WidgetBackdropWindow();
        b.Dismissed += CloseBoard;
        b.EscapePressed += OnBoardEscape;
        return b;
    }

    // ---- Screen helpers (physical pixels; see DisplayGeometry) ----

    private static WinForms.Screen PrimaryScreen() => WinForms.Screen.PrimaryScreen ?? WinForms.Screen.AllScreens[0];

    /// <summary>Every monitor as the overlay placement sees it: device name, full bounds (physical pixels), primary flag.</summary>
    private static List<MonitorBox> ScreenBoxes()
        => WinForms.Screen.AllScreens.Select(sc => new MonitorBox(sc.DeviceName, DisplayGeometry.Bounds(sc), sc.Primary)).ToList();

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
        HideIfVisible(_bar);
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
        _host.Theme.Changed -= OnThemeChanged;
        CloseThemePicker();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _displayTimer?.Stop();
        _host.PerfOverlayRefresh = null;
        _host.GameChanged -= OnGameChangedForOverlay;
        if (_perfOverlayViewing)
        {
            _host.Performance.Sampled -= OnPerfSampleForOverlay;
            _perfOverlayViewing = false;
        }
        try { _perfFpsLease?.Dispose(); } catch { }
        _perfFpsLease = null;
        try { _perfOverlay?.Close(); } catch { }
        _host.CrosshairRefresh = null;
        try { _clickerIndicator.Dispose(); } catch { }
        foreach (var w in _windows.Values)
        {
            if (w.WidgetContent is IDisposable d) { try { d.Dispose(); } catch { } }
            try { w.Close(); } catch { }
        }
        _windows.Clear();
        try { _sidebar?.Close(); } catch { }
        try { _bar?.Close(); } catch { }
        try { _backdrop?.Close(); } catch { }
        try { _crosshair?.Close(); } catch { }
    }
}
