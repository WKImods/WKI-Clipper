using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.ViewModels;

namespace WKI_Clipper.Views;

/// <summary>
/// The "Streaming" widget: a software stream deck. Top card = OBS connection
/// (host/port/password + status), middle = the tile grid (click = run action,
/// right-click = edit/remove, empty slot = create), bottom = live status line.
/// Tiles reflect OBS state live (active scene, LIVE, REC, mute) and grey out
/// while disconnected.
/// </summary>
public partial class StreamingView : UserControl
{
    private System.Windows.Shapes.Ellipse? _connDot;
    private TextBlock? _connText;
    private Button? _connectBtn;
    private PasswordBox? _pwBox;
    private bool _subscribed;

    public StreamingView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var host = App.Host;
        if (host is null) return;

        if (!_subscribed)
        {
            host.Obs.StatusChanged += OnObsStatusChanged;
            host.Obs.ActionError += OnObsActionError;
            _subscribed = true;
        }
        if (ConnectionPanel.Children.Count == 0) BuildConnectionCard(host);
        RebuildGrid();
        UpdateConnectionUi();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var host = App.Host;
        if (host != null && _subscribed)
        {
            host.Obs.StatusChanged -= OnObsStatusChanged;
            host.Obs.ActionError -= OnObsActionError;
            _subscribed = false;
        }
    }

    // Service events arrive on socket worker threads.
    private void OnObsStatusChanged()
        => Dispatcher.BeginInvoke(new Action(() => { if (IsLoaded) { RebuildGrid(); UpdateConnectionUi(); } }));

    private void OnObsActionError(string msg)
        => Dispatcher.BeginInvoke(new Action(() =>
            ToastService.Show(ToastKind.Warning, "OBS", msg, durationSeconds: 4.0)));

    // ---- connection card ----

    private void BuildConnectionCard(AppHost host)
    {
        var o = host.Settings.Current.Streaming.Obs;

        var header = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        _connDot = new System.Windows.Shapes.Ellipse
        {
            Width = 10, Height = 10, Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55))
        };
        _connText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        header.Children.Add(_connDot);
        header.Children.Add(_connText);
        ConnectionPanel.Children.Add(header);

        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };

        var hostBox = new TextBox { Text = o.Host, MinWidth = 110, Margin = new Thickness(0, 0, 6, 0) };
        var portBox = new TextBox { Text = o.Port.ToString(), MinWidth = 56, Margin = new Thickness(0, 0, 6, 0) };
        _pwBox = new PasswordBox
        {
            MinWidth = 110, Margin = new Thickness(0, 0, 6, 0), MinHeight = 32,
            Background = (Brush)FindResource("PanelBrush"),
            Foreground = (Brush)FindResource("TextBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush")
        };
        // Placeholder dots when a protected password exists (we never show plaintext).
        if (!string.IsNullOrEmpty(o.PasswordProtected)) _pwBox.Password = "********";
        bool pwTouched = false;
        _pwBox.PasswordChanged += (_, _) => pwTouched = true;

        _connectBtn = new Button { Content = "…", MinWidth = 96 };
        _connectBtn.Click += (_, _) =>
        {
            var s = host.Settings.Current.Streaming.Obs;
            s.Host = hostBox.Text.Trim();
            if (int.TryParse(portBox.Text.Trim(), out int p) && p is > 0 and < 65536) s.Port = p;
            if (pwTouched) s.PasswordProtected = SecretProtector.Protect(_pwBox!.Password);
            host.Settings.Save();

            if (host.Obs.IsConnected) host.Obs.Disable();
            else host.Obs.Enable();
            UpdateConnectionUi();
        };

        row.Children.Add(Labeled("Host", hostBox));
        row.Children.Add(Labeled("Port", portBox));
        row.Children.Add(Labeled(L.T("Passwort", "Password"), _pwBox));
        row.Children.Add(new Border { Width = 1 }); // spacer
        var btnHolder = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        btnHolder.Children.Add(_connectBtn);
        row.Children.Add(btnHolder);
        ConnectionPanel.Children.Add(row);

        var auto = new CheckBox
        {
            Content = L.T("Automatisch verbinden (auch wenn OBS später startet)",
                          "Connect automatically (even when OBS starts later)"),
            IsChecked = o.AutoConnect,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 8, 0, 0)
        };
        auto.Checked += (_, _) => { o.AutoConnect = true; host.Settings.Save(); host.Obs.Enable(); };
        auto.Unchecked += (_, _) => { o.AutoConnect = false; host.Settings.Save(); };
        ConnectionPanel.Children.Add(auto);
    }

    private static FrameworkElement Labeled(string label, FrameworkElement content)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
        sp.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = (Brush)Application.Current.FindResource("MutedBrush"),
            Margin = new Thickness(0, 0, 0, 2)
        });
        sp.Children.Add(content);
        return sp;
    }

    private void UpdateConnectionUi()
    {
        var host = App.Host;
        if (host is null || _connDot is null || _connText is null || _connectBtn is null) return;
        bool on = host.Obs.IsConnected;
        bool wants = host.Settings.Current.Streaming.Obs.AutoConnect;

        _connDot.Fill = new SolidColorBrush(on
            ? Color.FromRgb(0x4A, 0xD8, 0x6A)
            : Color.FromRgb(0x55, 0x55, 0x55));
        _connText.Text = on
            ? L.T("OBS verbunden", "OBS connected")
            : wants
                ? L.T("OBS nicht erreichbar — verbinde automatisch…", "OBS unreachable — auto-connecting…")
                : L.T("Nicht verbunden", "Not connected");
        _connectBtn.Content = on ? L.T("Trennen", "Disconnect") : L.T("Verbinden", "Connect");

        // Status line
        var st = host.Obs.Status;
        if (on)
        {
            string live = st.Streaming ? "  ·  LIVE" : "";
            string rec = st.Recording ? (st.RecordPaused ? L.T("  ·  REC (Pause)", "  ·  REC (paused)") : "  ·  REC") : "";
            string buf = st.ReplayBufferActive ? L.T("  ·  Replay-Buffer an", "  ·  replay buffer on") : "";
            StatusLine.Text = L.T($"Szene: {st.CurrentScene ?? "—"}", $"Scene: {st.CurrentScene ?? "—"}") + live + rec + buf;
        }
        else
        {
            StatusLine.Text = L.T("Buttons sind inaktiv, bis OBS verbunden ist (OBS → Werkzeuge → WebSocket-Servereinstellungen).",
                                  "Buttons stay inactive until OBS is connected (OBS → Tools → WebSocket Server Settings).");
        }
    }

    // ---- the deck grid ----

    private void RebuildGrid()
    {
        var host = App.Host;
        if (host is null) return;
        var s = host.Settings.Current.Streaming;

        DeckGrid.Columns = Math.Max(1, s.GridColumns);
        DeckGrid.Rows = Math.Max(1, s.GridRows);
        DeckGrid.Children.Clear();
        DeckGrid.IsEnabled = true; // empty slots stay clickable for configuration
        int slots = DeckGrid.Columns * DeckGrid.Rows;

        bool connected = host.Obs.IsConnected;
        var st = host.Obs.Status;

        for (int slot = 0; slot < slots; slot++)
        {
            var cfg = s.Buttons.FirstOrDefault(b => b.Slot == slot);
            DeckGrid.Children.Add(cfg is null ? BuildEmptyTile(host, slot) : BuildTile(host, cfg, connected, st));
        }
    }

    private FrameworkElement BuildEmptyTile(AppHost host, int slot)
    {
        var border = new Border
        {
            Margin = new Thickness(3),
            CornerRadius = new CornerRadius(6),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = "+",
                FontSize = 22,
                Foreground = (Brush)FindResource("MutedBrush"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        border.MouseLeftButtonUp += (_, _) => EditButton(host, slot, null);
        return border;
    }

    private FrameworkElement BuildTile(AppHost host, StreamButtonConfig cfg, bool connected, ObsStatus st)
    {
        bool isActiveScene = cfg.Action == StreamAction.SetScene
                             && connected && cfg.Param != null
                             && string.Equals(cfg.Param, st.CurrentScene, StringComparison.OrdinalIgnoreCase);

        var stack = new StackPanel
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(new TextBlock
        {
            Text = cfg.Label,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 110
        });

        string? badge = Badge(cfg, connected, st);
        if (badge != null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = badge,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x8A)),
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            });
        }
        if (cfg.Hotkey is { Key: not 0 } hk)
        {
            stack.Children.Add(new TextBlock
            {
                Text = HotkeyEntryViewModel.Describe(hk),
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0)
            });
        }

        var border = new Border
        {
            Margin = new Thickness(3),
            CornerRadius = new CornerRadius(6),
            Background = StreamButtonDialog.BrushFromHex(cfg.Color),
            BorderThickness = new Thickness(isActiveScene ? 3 : 1),
            BorderBrush = isActiveScene
                ? (Brush)FindResource("AccentBrush")
                : new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)),
            Cursor = Cursors.Hand,
            Opacity = connected ? 1.0 : 0.45,
            Child = stack
        };

        border.MouseLeftButtonUp += async (_, _) =>
        {
            if (!connected)
            {
                ToastService.Show(ToastKind.Info, "OBS",
                    L.T("OBS ist nicht verbunden.", "OBS is not connected."), durationSeconds: 2.5);
                return;
            }
            await host.Obs.ExecuteAsync(cfg);
        };

        var menu = new ContextMenu();
        var edit = new MenuItem { Header = L.T("Bearbeiten…", "Edit…") };
        edit.Click += (_, _) => EditButton(host, cfg.Slot, cfg);
        var remove = new MenuItem { Header = L.T("Entfernen", "Remove") };
        remove.Click += (_, _) =>
        {
            host.Settings.Current.Streaming.Buttons.RemoveAll(b => b.Slot == cfg.Slot);
            host.Settings.Save();
            host.Hotkeys.RegisterAll();   // frees the tile's global hotkey
            RebuildGrid();
        };
        menu.Items.Add(edit);
        menu.Items.Add(remove);
        border.ContextMenu = menu;

        return border;
    }

    /// <summary>Small state badge under the label, from live OBS status.</summary>
    private static string? Badge(StreamButtonConfig cfg, bool connected, ObsStatus st)
    {
        if (!connected) return null;
        return cfg.Action switch
        {
            StreamAction.ToggleStream or StreamAction.StartStream or StreamAction.StopStream
                => st.Streaming ? "LIVE" : null,
            StreamAction.ToggleRecord
                => st.Recording ? (st.RecordPaused ? L.T("REC ⏸", "REC ⏸") : "REC") : null,
            StreamAction.PauseResumeRecord
                => st.RecordPaused ? L.T("PAUSIERT", "PAUSED") : (st.Recording ? "REC" : null),
            StreamAction.ToggleReplayBuffer or StreamAction.SaveReplayBuffer
                => st.ReplayBufferActive ? L.T("BUFFER AN", "BUFFER ON") : null,
            StreamAction.ToggleInputMute when cfg.Param != null && st.InputMuted.TryGetValue(cfg.Param, out bool muted)
                => muted ? "MUTE" : null,
            StreamAction.ToggleVirtualCam
                => st.VirtualCamActive ? L.T("KAMERA AN", "CAM ON") : null,
            _ => null
        };
    }

    private void EditButton(AppHost host, int slot, StreamButtonConfig? existing)
    {
        var owner = Window.GetWindow(this);
        var dlg = new StreamButtonDialog(host, slot, existing, owner!);
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            var list = host.Settings.Current.Streaming.Buttons;
            list.RemoveAll(b => b.Slot == slot);
            list.Add(dlg.Result);
            host.Settings.Save();
            host.Hotkeys.RegisterAll();   // picks up the new/changed tile hotkey
            RebuildGrid();
        }
    }
}
