using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WKI_Clipper.Native;
using WKI_Clipper.Services;

namespace WKI_Clipper.Views;

/// <summary>
/// An embedded web app (WhatsApp Web, Spotify) inside a widget.
///
/// Two renderings, picked per app:
///  • Spotify: <see cref="WebView2CompositionControl"/>. The classic HWND-based WebView2
///    does not render inside the normal widget windows (AllowsTransparency = per-pixel
///    layered); the composition variant does — it fetches its picture via Windows' screen
///    capture and draws it in WPF.
///  • WhatsApp: the classic HWND <see cref="Microsoft.Web.WebView2.Wpf.WebView2"/> in a
///    direct-rendering widget window. WhatsApp is hidden from capture (display affinity),
///    and that also blocks the composition control's own capture — the user saw an empty
///    widget. A real child window is drawn by the compositor directly: visible on screen,
///    still absent from every recording.
/// All web apps share ONE browser environment (one process tree) with a separate profile
/// each, so logins stay apart. Data lives under %LOCALAPPDATA%\WKI_Clipper\WebView2 and
/// survives reinstalls.
///
/// Nothing starts on construction: <see cref="EnsureStarted"/> is called by the widget host
/// the first time the widget is really shown — never by the startup prewarm, which would
/// otherwise boot a browser nobody asked for.
/// </summary>
public sealed class WebAppHost : UserControl, IDisposable
{
    private static Task<CoreWebView2Environment>? s_environment;

    private static string DataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WKI_Clipper", "WebView2");

    // Edit commands only — no "inspect", "save page", "print", "back" etc.
    private static readonly HashSet<string> AllowedMenuItems = new(StringComparer.OrdinalIgnoreCase)
    {
        "undo", "redo", "cut", "copy", "paste", "pasteAndMatchStyle", "selectAll", "emoji",
        "copyImage", "copyImageLocation", "copyLinkLocation"
    };

    private readonly WebApp _app;
    private readonly Grid _root = new();
    private readonly Border _statusPanel;
    private readonly TextBlock _statusText;
    private readonly Button _retryButton;

    private IWebView2? _web;

    /// <summary>True when this app must render as a real child window (see class summary).</summary>
    public static bool UsesDirectRendering(WebApp app) => app == WebApp.WhatsApp;
    private bool _starting;
    private bool _disposed;
    private bool _muted;
    private double _zoom = 1.0;

    /// <summary>Page title changed (the only thing read from the page).</summary>
    public event Action<string>? TitleChanged;
    /// <summary>The page started or stopped playing sound.</summary>
    public event Action<bool>? PlayingAudioChanged;
    /// <summary>The user zoomed with Ctrl + mouse wheel.</summary>
    public event Action<double>? ZoomChanged;

    public WebAppHost(WebApp app)
    {
        _app = app;

        _statusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = (Brush)Application.Current.FindResource("MutedBrush")
        };
        _retryButton = new Button
        {
            Content = L.T("Neu starten", "Restart"),
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        _retryButton.Click += (_, _) => Restart();

        var statusStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            MaxWidth = 360
        };
        statusStack.Children.Add(_statusText);
        statusStack.Children.Add(_retryButton);
        _statusPanel = new Border { Child = statusStack, Background = Brushes.Transparent };

        _root.Children.Add(_statusPanel);
        Content = _root;
        ShowStatus(L.T("Wird beim ersten Öffnen geladen …", "Loads when first opened …"), retry: false);
    }

    public bool IsStarted => _web?.CoreWebView2 != null;

    /// <summary>True while the user is typing in the page (focus handback on board close).</summary>
    public bool HasKeyboardFocusWithin => (_web as UIElement)?.IsKeyboardFocusWithin == true;

    public bool IsMuted
    {
        get => _muted;
        set
        {
            _muted = value;
            if (_web?.CoreWebView2 is { } core) core.IsMuted = value;
        }
    }

    /// <summary>Initial/persisted zoom; applied on start.</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            _zoom = Math.Clamp(value, 0.25, 4.0);
            if (_web != null && Math.Abs(_web.ZoomFactor - _zoom) > 0.001) _web.ZoomFactor = _zoom;
        }
    }

    /// <summary>Boots the browser for this app once. Safe to call repeatedly.</summary>
    public void EnsureStarted()
    {
        if (_disposed || _starting || _web != null) return;
        _ = StartAsync();
    }

    public void Reload()
    {
        if (_starting) return;   // a start is in flight — tearing it down mid-init races it
        if (_web?.CoreWebView2 is { } core) core.Reload();
        else Restart();
    }

    /// <summary>Re-applies localized texts (language switch) without touching the session.</summary>
    public void Relocalize()
    {
        _retryButton.Content = L.T("Neu starten", "Restart");
        if (_statusPanel.Visibility == Visibility.Visible && _web == null && !_starting)
            ShowStatus(L.T("Wird beim ersten Öffnen geladen …", "Loads when first opened …"), retry: false);
    }

    private static Task<CoreWebView2Environment> GetEnvironmentAsync()
        => s_environment ??= CoreWebView2Environment.CreateAsync(null, DataFolder);

    private async Task StartAsync()
    {
        _starting = true;
        ShowStatus(L.T("Lädt …", "Loading …"), retry: false);
        var sw = Stopwatch.StartNew();
        try
        {
            CoreWebView2Environment env;
            try { env = await GetEnvironmentAsync(); }
            catch { s_environment = null; throw; }   // let a later attempt retry

            var options = env.CreateCoreWebView2ControllerOptions();
            options.ProfileName = WebAppRules.ProfileName(_app);

            IWebView2 web = UsesDirectRendering(_app)
                ? new Microsoft.Web.WebView2.Wpf.WebView2()
                : new WebView2CompositionControl();
            // Matches the widget background so loading never flashes white.
            web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 30, 30, 36);
            web.ZoomFactor = _zoom;
            _root.Children.Insert(0, (UIElement)web);
            _web = web;

            await web.EnsureCoreWebView2Async(env, options);
            // Disposed or replaced while initializing: this control is no longer ours.
            if (_disposed || !ReferenceEquals(_web, web)) return;

            Configure(web.CoreWebView2);
            web.ZoomFactorChanged += (_, _) =>
            {
                _zoom = web.ZoomFactor;
                ZoomChanged?.Invoke(_zoom);
            };
            web.CoreWebView2.Navigate(WebAppRules.HomeUrl(_app));
            _statusPanel.Visibility = Visibility.Collapsed;
            Logger.Info($"Web widget {_app} started in {sw.ElapsedMilliseconds} ms (profile '{options.ProfileName}').");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            TearDownControl();
            ShowStatus(L.T("Die Microsoft-Edge-WebView2-Runtime fehlt. Bitte über Windows Update oder von microsoft.com installieren.",
                           "The Microsoft Edge WebView2 Runtime is missing. Install it via Windows Update or from microsoft.com."), retry: true);
            Logger.Warn($"Web widget {_app}: WebView2 runtime not found.");
        }
        catch (Exception ex)
        {
            TearDownControl();
            ShowStatus(L.T("Konnte nicht geladen werden.", "Could not be loaded.") + "\n" + ex.Message, retry: true);
            Logger.Error($"Web widget {_app} failed to start", ex);
        }
        finally
        {
            _starting = false;
        }
    }

    private void Configure(CoreWebView2 core)
    {
        ApplyPagePolicy(core);
        core.Settings.AreDefaultContextMenusEnabled = true;   // filtered to edit commands below
        core.Settings.IsZoomControlEnabled = true;            // Ctrl + wheel, persisted per widget

        core.NavigationStarting += (_, e) => GuardNavigation(e.Uri, () => e.Cancel = true);
        core.NewWindowRequested += OnNewWindowRequested;
        core.ContextMenuRequested += OnContextMenuRequested;
        core.DocumentTitleChanged += (_, _) => TitleChanged?.Invoke(core.DocumentTitle ?? "");
        core.IsDocumentPlayingAudioChanged += (_, _) => PlayingAudioChanged?.Invoke(core.IsDocumentPlayingAudio);
        core.ProcessFailed += OnProcessFailed;
    }

    /// <summary>
    /// Rules every page of this app gets — the widget itself and its sign-in popups alike:
    /// no dev tools/browser shortcuts/autofill, no bridge into the app, the permission and
    /// download policy, and the mute state.
    /// </summary>
    private void ApplyPagePolicy(CoreWebView2 core)
    {
        var s = core.Settings;
        s.AreDevToolsEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;   // no F5/F12/Ctrl+P; edit keys still work
        s.AreDefaultContextMenusEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.AreHostObjectsAllowed = false;              // the page gets no bridge into the app
        s.IsWebMessageEnabled = false;
        s.IsSwipeNavigationEnabled = false;

        core.IsMuted = _muted;
        core.PermissionRequested += OnPermissionRequested;
        core.DownloadStarting += OnDownloadStarting;
    }

    /// <summary>Top-level navigation: own pages stay, web links go to the browser, the rest is dropped.</summary>
    private void GuardNavigation(string uri, Action cancel)
    {
        switch (WebAppRules.Decide(_app, uri))
        {
            case WebNavDecision.Allow:
                return;
            case WebNavDecision.OpenExternal:
                cancel();
                OpenExternal(uri);
                return;
            default:
                cancel();
                Logger.Info($"Web widget {_app}: blocked navigation to a non-web address.");
                return;
        }
    }

    private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var decision = WebAppRules.Decide(_app, e.Uri);
        if (decision != WebNavDecision.Allow)
        {
            e.Handled = true;
            if (decision == WebNavDecision.OpenExternal) OpenExternal(e.Uri);
            return;
        }

        // Sign-in popups (Google/Apple/Facebook) need window.opener, so they open as a real
        // popup — in our own topmost window, or they would hide behind the widget board.
        // Not owned by the widget: closing the board would otherwise hide it mid-login.
        var deferral = e.GetDeferral();
        WebPopupWindow? popup = null;
        try
        {
            var env = await GetEnvironmentAsync();
            var options = env.CreateCoreWebView2ControllerOptions();
            options.ProfileName = WebAppRules.ProfileName(_app);

            popup = new WebPopupWindow(excludeFromCapture: _app == WebApp.WhatsApp);
            popup.Show();
            await popup.Web.EnsureCoreWebView2Async(env, options);
            var pc = popup.Web.CoreWebView2;
            ApplyPagePolicy(pc);
            pc.NavigationStarting += (_, ne) => GuardNavigation(ne.Uri, () => ne.Cancel = true);
            pc.NewWindowRequested += (_, ne) =>
            {
                ne.Handled = true;
                if (WebAppRules.Decide(_app, ne.Uri) != WebNavDecision.Block) OpenExternal(ne.Uri);
            };
            var window = popup;
            pc.WindowCloseRequested += (_, _) => window.Close();
            pc.DocumentTitleChanged += (_, _) => window.Title = pc.DocumentTitle;
            e.NewWindow = pc;
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Web widget {_app}: popup failed ({ex.Message}).");
            try { popup?.Close(); } catch { }   // never leave an empty topmost window behind
            e.Handled = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = e.PermissionKind switch
        {
            // Without it WhatsApp keeps its session only in temporary storage — and asks
            // for the QR code again after every restart.
            CoreWebView2PermissionKind.PersistentStorage => CoreWebView2PermissionState.Allow,
            // No desktop notifications: they would pop names and texts over the game/stream.
            CoreWebView2PermissionKind.Notifications => CoreWebView2PermissionState.Deny,
            // Voice messages in WhatsApp: ask the user (WebView2 shows its own prompt).
            CoreWebView2PermissionKind.Microphone when _app == WebApp.WhatsApp => CoreWebView2PermissionState.Default,
            CoreWebView2PermissionKind.ClipboardRead => CoreWebView2PermissionState.Default,
            CoreWebView2PermissionKind.Autoplay => CoreWebView2PermissionState.Default,
            _ => CoreWebView2PermissionState.Deny
        };
        if (e.State != CoreWebView2PermissionState.Default) e.SavesInProfile = true;
        Logger.Info($"Web widget {_app}: permission {e.PermissionKind} → {e.State}.");
    }

    private static void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        // No browser download flyout over the widget; the file goes to the default
        // download folder (ResultFilePath is already set there).
        e.Handled = true;
        var op = e.DownloadOperation;
        // The toast opens the FOLDER, never the file: attachments come from strangers, and
        // a click on a toast next to the crosshair must not run a downloaded .exe/.lnk.
        var folder = System.IO.Path.GetDirectoryName(e.ResultFilePath);
        op.StateChanged += (_, _) =>
        {
            if (op.State == CoreWebView2DownloadState.Completed)
                // Title only — a file name from a private chat must not show up on stream.
                ToastService.Show(ToastKind.Info, L.T("Download gespeichert", "Download saved"),
                    L.T("Im Download-Ordner — klicken öffnet den Ordner.", "In the downloads folder — click to open the folder."), folder);
        };
    }

    private static void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        var items = e.MenuItems;
        for (int i = items.Count - 1; i >= 0; i--)
            if (!AllowedMenuItems.Contains(items[i].Name)) items.RemoveAt(i);
        if (items.Count == 0) e.Handled = true;
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Logger.Warn($"Web widget {_app}: browser process failed ({e.ProcessFailedKind}).");
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            s_environment = null;   // the shared environment died with it
        // "Unresponsive" is only logged: WhatsApp is routinely sluggish while it syncs the
        // chat history, and tearing the page down then would lose exactly that sync.
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited
            or CoreWebView2ProcessFailedKind.RenderProcessExited)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                TearDownControl();
                ShowStatus(L.T("Die Seite ist abgestürzt.", "The page crashed."), retry: true);
            }));
        }
    }

    private void Restart()
    {
        if (_starting) return;
        TearDownControl();
        EnsureStarted();
    }

    private void TearDownControl()
    {
        if (_web == null) return;
        try { _root.Children.Remove((UIElement)_web); ((IDisposable)_web).Dispose(); } catch { }
        _web = null;
    }

    private void ShowStatus(string text, bool retry)
    {
        _statusText.Text = text;
        _retryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        _statusPanel.Visibility = Visibility.Visible;
    }

    private void OpenExternal(string uri)
    {
        // Decide() guarantees http/https here; AbsoluteUri re-serializes it cleanly.
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)
            || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Warn($"Web widget {_app}: could not open link externally ({ex.Message})."); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        TearDownControl();
    }

    /// <summary>
    /// Topmost window for sign-in popups, sharing the widget's profile. A normal window, so
    /// the classic HWND WebView2 renders in it — also when it is hidden from capture.
    /// </summary>
    private sealed class WebPopupWindow : Window
    {
        public Microsoft.Web.WebView2.Wpf.WebView2 Web { get; } = new()
        {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 30, 30, 36)
        };

        public WebPopupWindow(bool excludeFromCapture)
        {
            Title = "WKI Clipper";
            Width = 520;
            Height = 720;
            Topmost = true;
            // In the taskbar: an unowned popup must stay findable if it ends up behind the game.
            ShowInTaskbar = true;
            Background = (Brush)Application.Current.FindResource("BgBrush");
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Content = Web;
            if (excludeFromCapture)
                SourceInitialized += (_, _) => User32.SetWindowDisplayAffinity(
                    new System.Windows.Interop.WindowInteropHelper(this).Handle, User32.WDA_EXCLUDEFROMCAPTURE);
            Closed += (_, _) => { try { Web.Dispose(); } catch { } };
        }
    }
}
